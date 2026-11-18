using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Security;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Authorization;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.API.Subscriptions;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Back-office user management. Admin-only: assigning roles and resetting credentials is
/// the most sensitive surface in the system, so it gets a tighter policy than the rest of
/// the catalog. Only <see cref="UserRole.Cashier"/> rows are ever replicated to terminals
/// (see SyncService) — creating or promoting a Manager/Admin here never exposes their
/// password hash to a till.
/// </summary>
public static class UsersEndpoints
{
    public static void MapUsersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users")
            .AddEndpointFilter<SubscriptionWriteFilter>();

        group.MapGet("/", async (PosDbContext db, CancellationToken ct) =>
        {
            var users = await db.Users.AsNoTracking().Where(u => !u.IsDeleted).OrderBy(u => u.Username).ToListAsync(ct);
            var effective = await UserRoleWriter.EffectiveAsync(db, users, ct);
            var tills = await OwnTillsAsync(db, users.Select(u => u.Id).ToList(), ct);
            var numbers = await EmployeeNumbersAsync(db, users, ct);
            var untaxedRoles = await UntaxedSalesGrant.RolesGrantingAsync(db, ct);
            var roles = await RolesAsync(db, ct);
            var own = await UserGrantEditor.OwnAsync(db, users.Select(u => u.Id).ToList(), ct);
            return Results.Ok(users.Select(u => Map(u, effective[u.Id], tills.GetValueOrDefault(u.Id), numbers, untaxedRoles, roles,
                own.GetValueOrDefault(u.Id, []))).ToList());
        });
        PeoplePageRoutes.MapUsersPage(group);

        // An account is given to an employee, and its Login ID is drawn here, at save time (owner,
        // 2026-10-05). A username or a name in the body is ignored: the account takes the employee's
        // name, and the server alone decides the number it signs in with.
        group.MapPost("/", async (CreateUserRequest r, ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            if (await UserRoleWriter.ChooseAsync(db, r.RoleId, r.Role, null, ct) is not { } choice)
                return UnknownRole(r.RoleId);
            if (r.EmployeeId is not Guid employeeId)
                return Coded(StatusCodes.Status400BadRequest, EmployeeAccounts.EmployeeRequiredCode, EmployeeAccounts.EmployeeRequiredMessage);
            if (!PasswordRule.IsAcceptable(r.Password))
                return Results.BadRequest(PasswordRule.TooShortMessage);
            if (r.Pin is not null && !IsValidPin(r.Pin))
                return Results.BadRequest("PIN must be 4 to 6 digits.");

            var (employee, refusal, refusalMessage) = await EmployeeAccounts.EligibleAsync(db, employeeId, ct);
            if (refusal is not null) return Coded(StatusCodes.Status400BadRequest, refusal, refusalMessage!);
            if (await EmployeeAccounts.ActiveAccountAsync(db, employeeId, null, ct) is User existing)
                return EmployeeHasAccount(existing);

            // Every user works at a branch, whatever the role (BranchLock); the employee's own branch
            // unless the screen chose another.
            var branch = r.BranchId ?? employee!.BranchId;
            if (!await BranchExistsAsync(db, branch, ct))
                return Results.BadRequest("Unknown branch.");

            var loginId = await LoginIds.NewAsync(db, null, ct);
            var entity = new User
            {
                Username = loginId,
                LoginId = loginId,
                EmployeeId = employeeId,
                FullName = employee!.FullName,
                IsActive = true,
                // Copied explicitly, never mass-assigned. It binds a cashier's terminals (BranchScope)
                // and, for every role but the Admin, is the only branch their screens offer (BranchLock).
                BranchId = branch,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(r.Password),
                // A user with no PIN simply cannot unlock offline; that is fine for
                // Manager/Admin, who are expected to always be online.
                PinHash = string.IsNullOrWhiteSpace(r.Pin) ? string.Empty : BCrypt.Net.BCrypt.HashPassword(r.Pin)
            };
            if (Beyond(caller, choice) is { } wider) return wider;
            if (r.AdditionalPermissions is { } additional)
            {
                // The users screen's additional permissions (AUTH-1 §17.10): any key of the catalogue, the whole set.
                if (await UserRoleWriter.RoleBeyondCallerAsync(db, entity, true, choice, Decided(caller), ct) is { } roleBeyond)
                    return GrantBeyondOwn(roleBeyond);
                var plan = await UserGrantEditor.PlanAsync(db, entity, true, choice.RoleId, additional, Decided(caller), ct);
                if (Refused(plan) is { } refused) return refused;
                db.Users.Add(entity);
                UserRoleWriter.PlaceOnRole(entity, choice);
                UserGrantEditor.Apply(db, entity, plan, CallerName(caller));
            }
            else
            {
                // AUTH-1: the role and the grants go to the tables that decide access. Normalised on the
                // way in: an unknown name is dropped rather than stored, so a typo grants nothing.
                var grants = Permissions.Store(r.Permissions);
                if (await UserRoleWriter.BeyondCallerAsync(db, entity, true, choice, grants, Decided(caller), ct) is { } beyond)
                    return GrantBeyondOwn(beyond);
                db.Users.Add(entity);
                await UserRoleWriter.AssignChoiceAsync(db, entity, choice, grants, CallerName(caller), ct);
                // "Sell untaxed at the till" on the account itself (owner, 2026-11-02): a new cashier starts without it.
                if (r.SellsUntaxed is bool sells && !await UntaxedSalesGrant.SetAsync(db, entity, sells,
                        ServerAuthorization.Has(caller, PermissionCatalog.SalesUntaxed), CallerName(caller), ct))
                    return GrantBeyondOwn();
            }
            // A till-scoped user sells, and the main warehouse does not (owner, 2026-10-05).
            var scope = await CashierTills.ScopeOfAsync(db, entity.RoleId, ct);
            if (scope == RoleScope.TillBranch && !await CashierTills.IsSellingBranchAsync(db, branch, ct))
                return CashierOnWarehouse();
            // The cashier's own till comes with them, so they never type a code online.
            await CashierTills.AlignAsync(db, entity, scope, ct);

            // The check above and this save are not one statement, so two administrators saving at the
            // same moment can still meet at the unique indexes. A drawn number someone else took meanwhile
            // is drawn again; a second account for the same employee is refused in words.
            var tried = new HashSet<string>();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await db.SaveWithVersionsAsync(ct, entity);
                    break;
                }
                catch (DbUpdateException) when (attempt < 4)
                {
                    if (await EmployeeAccounts.ActiveAccountAsync(db, employeeId, entity.Id, ct) is User raced)
                        return EmployeeHasAccount(raced);
                    if (!await LoginIds.InUseAsync(db, entity.LoginId!, ct)) throw;
                    tried.Add(entity.LoginId!);
                    entity.LoginId = entity.Username = await LoginIds.NewAsync(db, tried, ct);
                }
            }
            return Results.Created($"/api/users/{entity.Id}", await MapAsync(db, entity, ct));
        });

        // An account from before the rule, linked to its employee. It keeps signing in exactly as it did
        // (its username is not replaced by a Login ID); it takes the employee's name, and from now on it
        // follows the employee — switched off when they leave.
        group.MapPost("/{id:guid}/employee", async (Guid id, LinkUserEmployeeRequest r, PosDbContext db, CancellationToken ct) =>
        {
            var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
            if (entity is null) return Results.NotFound();
            if (entity.EmployeeId is not null)
                return Coded(StatusCodes.Status409Conflict, EmployeeAccounts.AlreadyLinkedCode, EmployeeAccounts.AlreadyLinkedMessage);

            var (employee, refusal, refusalMessage) = await EmployeeAccounts.EligibleAsync(db, r.EmployeeId, ct);
            if (refusal is not null) return Coded(StatusCodes.Status400BadRequest, refusal, refusalMessage!);
            if (entity.IsActive && await EmployeeAccounts.ActiveAccountAsync(db, r.EmployeeId, entity.Id, ct) is User existing)
                return EmployeeHasAccount(existing);

            entity.EmployeeId = r.EmployeeId;
            entity.FullName = employee!.FullName;
            await db.SaveWithVersionsAsync(ct, entity);
            return Results.Ok(await MapAsync(db, entity, ct));
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateUserRequest r, ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
            if (await UserRoleWriter.ChooseAsync(db, r.RoleId, r.Role, entity?.RoleId, ct) is not { } choice)
                return UnknownRole(r.RoleId);
            if (entity is null) return Results.NotFound();
            // A screen that opened the account before its role or grants changed is told, and changes nothing:
            // it sends the whole set of additional permissions, and would take away what it never saw.
            if (r.AuthorizationVersion is long seen && seen != entity.AuthorizationVersion)
                return Coded(StatusCodes.Status409Conflict, UserGrantEditor.UserChangedCode, UserGrantEditor.UserChangedMessage);
            // One's own account keeps its role and stays on (for the Admin, as before: Admin to Admin only).
            if (caller.FindFirstValue(JwtRegisteredClaimNames.Sub) == id.ToString()
                && (!r.IsActive || choice.RoleId != entity.RoleId))
                return Results.BadRequest("You cannot deactivate or demote your own account.");
            // A linked account carries its employee's name, which is changed in Staff, not here; only an
            // account with no employee still has a name of its own to edit.
            if (entity.EmployeeId is null && string.IsNullOrWhiteSpace(r.FullName)) return Results.BadRequest("Full name is required.");
            // Switching an account back on: its employee must still be working, and must not have been
            // given another active account in the meantime.
            if (r.IsActive && !entity.IsActive && entity.EmployeeId is Guid linked)
            {
                var (_, refusal, refusalMessage) = await EmployeeAccounts.EligibleAsync(db, linked, ct);
                if (refusal is not null) return Coded(StatusCodes.Status400BadRequest, refusal, refusalMessage!);
                if (await EmployeeAccounts.ActiveAccountAsync(db, linked, entity.Id, ct) is User other)
                    return EmployeeHasAccount(other);
            }

            // The last active Admin: counted on the role that decides access (AUTH-1); an inactive or
            // deleted Admin does not count. The data layer refuses the same save as a backstop.
            if (entity.RoleId == SystemRoles.AdminId && entity.IsActive && (!r.IsActive || choice.RoleId != SystemRoles.AdminId)
                && await ActiveAdminsAsync(db, ct) <= 1)
                return Results.Conflict("At least one active administrator must remain.");
            // Validated before anything on the row is touched, so a refused branch leaves the
            // user exactly as they were.
            // A branch may be changed, never removed: a user without one would be nobody's. An
            // absent key still leaves the row as it is, so an older client cannot trip on this.
            if (r.BranchId.IsSet && r.BranchId.Value is null)
                return Results.BadRequest(BranchLock.BranchNotClearableMessage);
            if (r.BranchId.IsSet && r.BranchId.Value is Guid branch && !await BranchExistsAsync(db, branch, ct))
                return Results.BadRequest("Unknown branch.");
            // A till-scoped user sells, and the main warehouse does not (owner, 2026-10-05). Checked on
            // the role and branch being saved, before the row is touched; a legacy cashier already on
            // the warehouse is refused only when the save would keep them there as a cashier.
            var targetBranch = r.BranchId.IsSet ? r.BranchId.Value : entity.BranchId;
            if (r.IsActive && choice.Scope == RoleScope.TillBranch
                && targetBranch is Guid target && !await CashierTills.IsSellingBranchAsync(db, target, ct))
                return CashierOnWarehouse();
            // Nobody gives or takes away more than they hold, by a role any more than by a tick (AUTH-1 Step 5).
            if (choice.RoleId != entity.RoleId && Beyond(caller, choice) is { } wider) return wider;
            UserGrantEditor.Plan? plan = null;
            var grants = r.Permissions is not null ? Permissions.Store(r.Permissions) : entity.Permissions;
            if (r.AdditionalPermissions is { } additional)
            {
                // The users screen's additional permissions (AUTH-1 §17.10): the whole set, any key of the catalogue.
                if (await UserRoleWriter.RoleBeyondCallerAsync(db, entity, false, choice, Decided(caller), ct) is { } roleBeyond)
                    return GrantBeyondOwn(roleBeyond);
                plan = await UserGrantEditor.PlanAsync(db, entity, false, choice.RoleId, additional, Decided(caller), ct);
                if (Refused(plan) is { } refused) return refused;
            }
            else if (await UserRoleWriter.BeyondCallerAsync(db, entity, false, choice, grants, Decided(caller), ct) is { } beyond)
                return GrantBeyondOwn(beyond);
            if (entity.EmployeeId is null) entity.FullName = r.FullName.Trim();
            entity.IsActive = r.IsActive;
            if (plan is not null)
            {
                UserRoleWriter.PlaceOnRole(entity, choice);
                UserGrantEditor.Apply(db, entity, plan, CallerName(caller));
            }
            else
            {
                // Omitted permissions mean "leave them as they are", so a client that predates named
                // permissions cannot silently revoke one by not knowing about it.
                await UserRoleWriter.AssignChoiceAsync(db, entity, choice, grants, CallerName(caller), ct);
                // Absent leaves "sell untaxed at the till" as it is; true or false sets the account's own tick.
                if (r.SellsUntaxed is bool sells && !await UntaxedSalesGrant.SetAsync(db, entity, sells,
                        ServerAuthorization.Has(caller, PermissionCatalog.SalesUntaxed), CallerName(caller), ct))
                    return GrantBeyondOwn();
            }
            // Absent leaves the branch, a guid moves the user (null was refused above). SaveWithVersionsAsync
            // bumps Version, which revokes the sessions a moved cashier still holds.
            if (r.BranchId.IsSet) entity.BranchId = r.BranchId.Value;
            // Their own till follows them: moved with the branch, off when they are off, on again
            // when they are back, and created for a user who just became a cashier.
            await CashierTills.AlignAsync(db, entity, await CashierTills.ScopeOfAsync(db, entity.RoleId, ct), ct);
            await db.SaveWithVersionsAsync(ct, entity);
            return Results.Ok(await MapAsync(db, entity, ct));
        });

        group.MapPost("/{id:guid}/password", async (Guid id, SetCredentialRequest r, PosDbContext db, CancellationToken ct) =>
        {
            if (!PasswordRule.IsAcceptable(r.Value))
                return Results.BadRequest(PasswordRule.TooShortMessage);

            var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
            if (entity is null) return Results.NotFound();

            entity.PasswordHash = BCrypt.Net.BCrypt.HashPassword(r.Value);
            // A new password forgets every computer trusted for two-step login.
            entity.TwoFactorTrustStamp = TwoFactor.NewStamp();
            await db.SaveWithVersionsAsync(ct, entity);
            return Results.NoContent();
        });

        // Two-step login is each user's own (owner, 2026-11-11); the administrator can only turn it
        // off — for someone who lost their phone and their recovery codes — never on, and never sees
        // the key. Every session the account holds ends with it, as after a password reset.
        group.MapPost("/{id:guid}/two-factor/reset", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
            if (entity is null) return Results.NotFound();
            TwoFactor.Clear(entity);
            await db.SaveWithVersionsAsync(ct, entity);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/pin", async (Guid id, SetCredentialRequest r, PosDbContext db, CancellationToken ct) =>
        {
            if (!IsValidPin(r.Value)) return Results.BadRequest("PIN must be 4 to 6 digits.");

            var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
            if (entity is null) return Results.NotFound();

            // The new hash reaches terminals on their next pull — a stale offline PIN keeps
            // working until then, which is expected of an offline-tolerant design.
            entity.PinHash = BCrypt.Net.BCrypt.HashPassword(r.Value);
            await db.SaveWithVersionsAsync(ct, entity);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/revoke-sessions", async (Guid id, PosDbContext db, CancellationToken ct) =>
        {
            var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
            if (entity is null) return Results.NotFound();
            await db.SaveWithVersionsAsync(ct, entity);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            var callerId = caller.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (callerId is not null && Guid.TryParse(callerId, out var callerGuid) && callerGuid == id)
                return Results.BadRequest("You cannot deactivate your own account.");

            var entity = await db.Users.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted, ct);
            if (entity is null) return Results.NotFound();

            if (entity.RoleId == SystemRoles.AdminId && entity.IsActive && await ActiveAdminsAsync(db, ct) <= 1)
                return Results.Conflict("At least one active administrator must remain.");
            entity.IsActive = false;
            entity.IsDeleted = true;
            // Their own till is switched off with them, never deleted: its receipts stay in the books.
            await CashierTills.AlignAsync(db, entity, await CashierTills.ScopeOfAsync(db, entity.RoleId, ct), ct);
            await db.SaveWithVersionsAsync(ct, entity);
            return Results.NoContent();
        });
    }

    private static bool IsValidPin(string pin) =>
        pin.Length is >= 4 and <= 6 && pin.All(char.IsDigit);

    private static IResult CashierOnWarehouse() =>
        Coded(StatusCodes.Status400BadRequest, CashierTills.CashierOnWarehouseCode, CashierTills.CashierOnWarehouseMessage);

    /// <summary>A refusal with a stable code the users tab translates, and the sentence for everyone else.</summary>
    private static IResult GrantBeyondOwn(string key = PermissionCatalog.SalesUntaxed) => Coded(StatusCodes.Status403Forbidden, "grant_beyond_own",
        $"You may give or take away only a permission you hold yourself; you do not hold '{key}'.");

    /// <summary>An additional permission the catalogue does not know, or one the caller may not give or take away.</summary>
    private static IResult? Refused(UserGrantEditor.Plan plan) =>
        plan.Unknown is { } unknown
            ? Coded(StatusCodes.Status400BadRequest, UserGrantEditor.UnknownPermissionCode, $"'{unknown}' is not a permission.")
            : plan.Beyond is { } beyond ? GrantBeyondOwn(beyond) : null;

    /// <summary>The caller's permissions as the server decided them for this request; none without a decision.</summary>
    private static IReadOnlySet<string> Decided(ClaimsPrincipal caller) =>
        ServerAuthorization.Read(caller)?.Permissions ?? new HashSet<string>();

    /// <summary>A role over the whole company is given only by someone who works across it.</summary>
    private static IResult? Beyond(ClaimsPrincipal caller, UserRoleWriter.RoleChoice choice) =>
        choice.Scope == RoleScope.Company && !ServerAuthorization.IsCompanyScoped(caller)
            ? Coded(StatusCodes.Status403Forbidden, "scope_beyond_own",
                "Only someone who works across the company may put an account on a role over the whole company.")
            : null;

    private static IResult UnknownRole(Guid? roleId) => roleId is null
        ? Results.BadRequest("Unknown role.")
        : Coded(StatusCodes.Status400BadRequest, "unknown_role", "That role does not exist or is switched off.");

    private static IResult Coded(int status, string code, string message) =>
        Results.Problem(message, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult EmployeeHasAccount(User existing) =>
        Results.Problem(EmployeeAccounts.EmployeeHasAccountMessage(EmployeeAccounts.SignInName(existing)),
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = EmployeeAccounts.EmployeeHasAccountCode,
                ["signIn"] = EmployeeAccounts.SignInName(existing),
            });

    /// <summary>The HR number of each linked user's employee (EMP-000001), by employee id.</summary>
    private static async Task<Dictionary<Guid, string>> EmployeeNumbersAsync(PosDbContext db, IEnumerable<User> users, CancellationToken ct)
    {
        var ids = users.Where(u => u.EmployeeId != null).Select(u => u.EmployeeId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
    }

    /// <summary>
    /// The FK is Restrict, so an unknown id would otherwise surface as a 500 from
    /// SaveChanges instead of a sentence the users tab can show.
    /// </summary>
    private static Task<bool> BranchExistsAsync(PosDbContext db, Guid id, CancellationToken ct) =>
        db.Branches.AnyAsync(b => b.Id == id && !b.IsDeleted, ct);

    private static Task<int> ActiveAdminsAsync(PosDbContext db, CancellationToken ct) =>
        db.Users.CountAsync(u => u.RoleId == SystemRoles.AdminId && u.IsActive && !u.IsDeleted, ct);

    private static string? CallerName(ClaimsPrincipal caller) =>
        caller.FindFirstValue("name") ?? caller.Identity?.Name;

    private static async Task<UserAdminDto> MapAsync(PosDbContext db, User u, CancellationToken ct) =>
        Map(u, (await UserRoleWriter.EffectiveAsync(db, [u], ct))[u.Id], (await OwnTillsAsync(db, [u.Id], ct)).GetValueOrDefault(u.Id),
            await EmployeeNumbersAsync(db, [u], ct), await UntaxedSalesGrant.RolesGrantingAsync(db, ct), await RolesAsync(db, ct),
            (await UserGrantEditor.OwnAsync(db, [u.Id], ct)).GetValueOrDefault(u.Id, []));

    /// <summary>Every role by id, for the name and scope each user's row shows. A company has a handful.</summary>
    private static async Task<Dictionary<Guid, Role>> RolesAsync(PosDbContext db, CancellationToken ct) =>
        await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);

    /// <summary>Each user's own till, by user (the code, and whether it is switched on).</summary>
    private static async Task<Dictionary<Guid, (string Code, bool IsActive)>> OwnTillsAsync(PosDbContext db, List<Guid> userIds, CancellationToken ct) =>
        (await db.Devices.AsNoTracking()
            .Where(d => d.UserId != null && userIds.Contains(d.UserId!.Value))
            .Select(d => new { UserId = d.UserId!.Value, d.Code, d.IsActive })
            .ToListAsync(ct))
        .GroupBy(d => d.UserId)
        .ToDictionary(g => g.Key, g => g.Select(d => (d.Code, d.IsActive)).OrderByDescending(d => d.IsActive).First());

    private static UserAdminDto Map(User u, IReadOnlySet<string> effective, (string Code, bool IsActive) till,
        IReadOnlyDictionary<Guid, string> employeeNumbers, IReadOnlySet<Guid> untaxedRoles, IReadOnlyDictionary<Guid, Role> roles,
        IReadOnlyList<string> own) =>
        new(u.Id, u.Username, u.FullName, u.Role.ToString(), u.IsActive, u.Version, u.IsDeleted,
            // What the screen ticked, and then what they actually hold — the second list comes from
            // the role tables (AUTH-1), in the names the current screen knows, which is how a screen
            // can show an Admin's as inherited rather than as something it may revoke.
            Permissions.Parse(u.Permissions).ToList(),
            ServerAuthorization.LegacyPermissions(effective).ToList(),
            u.BranchId,
            till.Code, till.Code is not null && till.IsActive,
            u.LoginId, u.EmployeeId,
            u.EmployeeId is Guid employee ? employeeNumbers.GetValueOrDefault(employee) : null,
            effective.Contains(PermissionCatalog.SalesUntaxed),
            u.RoleId is Guid roleId && untaxedRoles.Contains(roleId),
            u.RoleId,
            u.RoleId is Guid named ? roles.GetValueOrDefault(named)?.Name : null,
            u.RoleId is Guid scoped ? roles.GetValueOrDefault(scoped)?.Scope.ToString() : null,
            own,
            u.AuthorizationVersion,
            TwoFactor.IsOn(u));
}
