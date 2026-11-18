using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Authorization;

/// <summary>Who is editing: the permissions and the scope the server resolved for them, and their name.</summary>
public sealed record RoleEditorCaller(IReadOnlySet<string> Permissions, RoleScope Scope, string? Name)
{
    public bool Covers(string key) => PermissionCatalog.Covers(Permissions, key);
}

/// <summary>A roles-API answer: the value, or a refusal with its status, a stable code and a sentence.</summary>
public sealed record RoleResult<T>(T? Value, int Status = 200, string? Code = null, string? Message = null)
{
    public bool Ok => Code is null;
    public static RoleResult<T> Done(T value) => new(value);
    public static RoleResult<T> Refused(int status, string code, string message) => new(default, status, code, message);
}

/// <summary>
/// The roles API's rules (AUTH-1 Step 5, REDESIGN_PLAN.md §17.3, §17.6, §17.8), refused in words before
/// anything is written. The data layer (<see cref="AuthorizationShadow"/>) refuses the same saves as a
/// backstop, and bumps the role's version in the save's transaction, so every member's next request is
/// decided on the new permissions with no sign-out.
///
/// <list type="bullet">
/// <item>The Admin role is read-only: its permissions are the Admin baseline, written by code.</item>
/// <item>The Manager and Cashier roles keep their name, description, scope and status; their permissions
/// may change. No system role may be deleted.</item>
/// <item>A role the company created may change anything — but its scope only while no account is on it
/// (its members' tills and branch rules follow the scope), and it is switched off or deleted only then too.
/// It is deleted only when no account, removed ones included, names it; otherwise it is switched off.</item>
/// <item>Only catalogue keys. Nobody gives or takes away a permission they do not hold
/// (<see cref="PermissionCatalog.Covers"/>), and nobody makes a role wider in scope than their own.</item>
/// <item>Names are unique as <see cref="RoleNames.Normalize"/> compares them, held by a unique index.</item>
/// </list>
/// </summary>
public sealed class RoleEditor(PosDbContext db)
{
    public const string RoleNameTaken = "role_name_taken";
    public const string RoleHasMembers = "role_has_members";

    /// <summary>The catalogue by module, in the screens' order.</summary>
    public static IReadOnlyList<PermissionModuleDto> Catalogue() =>
        PermissionCatalog.Modules
            .Select(m => new PermissionModuleDto(m, PermissionCatalog.All.Where(p => p.Module == m).Select(p => p.Key).ToList()))
            .ToList();

    public async Task<List<RoleDto>> ListAsync(CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().ToListAsync(ct);
        var permissions = (await db.RolePermissions.AsNoTracking().ToListAsync(ct))
            .GroupBy(p => p.RoleId).ToDictionary(g => g.Key, g => g.Select(p => p.PermissionKey).ToHashSet(StringComparer.Ordinal));
        var counts = await db.Users.AsNoTracking().Where(u => u.RoleId != null)
            .GroupBy(u => u.RoleId!.Value)
            .Select(g => new { Role = g.Key, Members = g.Count(u => !u.IsDeleted), All = g.Count() })
            .ToDictionaryAsync(x => x.Role, ct);

        // The system roles first, in their own order, then the company's by name.
        var system = SystemRoles.All.Select(d => d.Id).ToList();
        return roles
            .OrderBy(r => system.IndexOf(r.Id) is var i && i >= 0 ? i : system.Count)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(r =>
            {
                var count = counts.GetValueOrDefault(r.Id);
                return Map(r, permissions.GetValueOrDefault(r.Id) ?? [], count?.Members ?? 0, count?.All ?? 0);
            })
            .ToList();
    }

    public async Task<RoleResult<List<RoleMemberDto>>> MembersAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == id, ct)) return NotFound<List<RoleMemberDto>>();
        var members = await db.Users.AsNoTracking().Where(u => u.RoleId == id && !u.IsDeleted)
            .OrderBy(u => u.FullName)
            .Select(u => new RoleMemberDto(u.Id, u.FullName, u.LoginId ?? u.Username, u.BranchId, u.IsActive))
            .ToListAsync(ct);
        return RoleResult<List<RoleMemberDto>>.Done(members);
    }

    public async Task<RoleResult<RoleDto>> CreateAsync(RoleEditorCaller caller, UpsertRoleRequest r, CancellationToken ct)
    {
        var name = RoleNames.Clean(r.Name);
        if (NameRefusal(name) is { } badName) return badName;
        if (DescriptionRefusal(r.Description) is { } badDescription) return badDescription;
        if (!TryScope(r.Scope, RoleScope.AssignedBranch, out var scope)) return BadScope();
        if (scope == RoleScope.Company && caller.Scope != RoleScope.Company) return ScopeBeyondOwn();

        var wanted = (r.Permissions ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (KeyRefusal(caller, wanted, wanted) is { } badKey) return badKey;
        if (await NameTakenAsync(name, null, ct)) return NameTaken(name);

        var role = new Role
        {
            Name = name, Description = Description(r.Description), Scope = scope, IsActive = r.IsActive ?? true,
            IsSystem = false, Key = null, Version = 1,
        };
        db.Roles.Add(role);
        foreach (var key in wanted) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionKey = key });
        if (await SaveAsync(name, ct) is { } raced) return raced;
        return RoleResult<RoleDto>.Done(Map(role, wanted.ToHashSet(StringComparer.Ordinal), 0, 0));
    }

    public async Task<RoleResult<RoleDto>> UpdateAsync(RoleEditorCaller caller, Guid id, UpsertRoleRequest r, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (role is null) return NotFound<RoleDto>();
        if (r.Version is long opened && opened != role.Version)
            return RoleResult<RoleDto>.Refused(409, "role_changed",
                "This role was changed by someone else after you opened it. Open it again to see the change, then make yours.");
        if (role.Id == SystemRoles.AdminId)
            return RoleResult<RoleDto>.Refused(409, "admin_role_protected",
                "The Admin role is protected: its permissions are set by the application and cannot be changed here.");

        var members = await db.Users.CountAsync(u => u.RoleId == id && !u.IsDeleted, ct);
        var name = r.Name is null ? role.Name : RoleNames.Clean(r.Name);
        var description = r.Description is null ? role.Description : Description(r.Description);
        if (!TryScope(r.Scope, role.Scope, out var scope)) return BadScope();
        var active = r.IsActive ?? role.IsActive;

        if (role.IsSystem)
        {
            if (name != role.Name || description != role.Description || scope != role.Scope || active != role.IsActive)
                return RoleResult<RoleDto>.Refused(400, "system_role_fixed",
                    "The Manager and Cashier roles keep their name, description, scope and status; only their permissions can change.");
        }
        else
        {
            if (NameRefusal(name) is { } badName) return badName;
            if (DescriptionRefusal(r.Description) is { } badDescription) return badDescription;
            if (scope != role.Scope)
            {
                if (members > 0) return HasMembers(members, "its scope can change");
                if (scope == RoleScope.Company && caller.Scope != RoleScope.Company) return ScopeBeyondOwn();
            }
            if (!active && role.IsActive && members > 0) return HasMembers(members, "it can be switched off");
            if (RoleNames.Normalize(name) != role.NormalizedName && await NameTakenAsync(name, role.Id, ct)) return NameTaken(name);
        }

        var current = await db.RolePermissions.Where(p => p.RoleId == id).ToListAsync(ct);
        var held = current.Select(p => p.PermissionKey).ToHashSet(StringComparer.Ordinal);
        var wanted = r.Permissions is null ? held : r.Permissions.ToHashSet(StringComparer.Ordinal);
        var added = wanted.Where(k => !held.Contains(k)).ToList();
        var removed = current.Where(p => !wanted.Contains(p.PermissionKey)).ToList();
        // A key the catalogue no longer has, left on a role from before, goes with any permissions edit unasked.
        if (KeyRefusal(caller, r.Permissions ?? [], added.Concat(removed.Select(p => p.PermissionKey)).Where(PermissionCatalog.Contains))
            is { } badKey) return badKey;

        role.Name = name;
        role.Description = description;
        role.Scope = scope;
        role.IsActive = active;
        foreach (var gone in removed) db.RolePermissions.Remove(gone);
        foreach (var key in added) db.RolePermissions.Add(new RolePermission { RoleId = id, PermissionKey = key });
        if (db.ChangeTracker.HasChanges()) role.UpdatedAtUtc = DateTime.UtcNow;
        if (await SaveAsync(name, ct) is { } raced) return raced;

        var all = await db.Users.CountAsync(u => u.RoleId == id, ct);
        return RoleResult<RoleDto>.Done(Map(role, wanted, members, all));
    }

    public async Task<RoleResult<bool>> DeleteAsync(Guid id, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (role is null) return NotFound<bool>();
        if (role.IsSystem)
            return RoleResult<bool>.Refused(409, "system_role", "The Admin, Manager and Cashier roles cannot be deleted.");
        var members = await db.Users.CountAsync(u => u.RoleId == id && !u.IsDeleted, ct);
        if (members > 0)
            return RoleResult<bool>.Refused(409, RoleHasMembers,
                $"{members} account(s) are on this role. Move them to another role before it can be deleted.");
        if (await db.Users.AnyAsync(u => u.RoleId == id, ct))
            return RoleResult<bool>.Refused(409, "role_in_history",
                "Removed accounts still name this role in their history, so it cannot be deleted. Switch it off instead.");

        db.RolePermissions.RemoveRange(await db.RolePermissions.Where(p => p.RoleId == id).ToListAsync(ct));
        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);
        return RoleResult<bool>.Done(true);
    }

    // ---------- rules ----------

    private static RoleResult<RoleDto>? NameRefusal(string name) =>
        name.Length == 0 ? RoleResult<RoleDto>.Refused(400, "role_name_required", "A role needs a name.")
        : name.Length > RoleNames.MaxLength ? RoleResult<RoleDto>.Refused(400, "role_name_too_long", $"A role's name is at most {RoleNames.MaxLength} characters.")
        : null;

    private static RoleResult<RoleDto>? DescriptionRefusal(string? description) =>
        description is not null && description.Trim().Length > 400
            ? RoleResult<RoleDto>.Refused(400, "role_description_too_long", "A role's description is at most 400 characters.")
            : null;

    /// <summary>Every key must be the catalogue's; every key that changes, one the caller holds or covers.</summary>
    private static RoleResult<RoleDto>? KeyRefusal(RoleEditorCaller caller, IEnumerable<string> wanted, IEnumerable<string> changing)
    {
        if (wanted.FirstOrDefault(k => !PermissionCatalog.Contains(k)) is { } stray)
            return RoleResult<RoleDto>.Refused(400, "unknown_permission", $"'{stray}' is not a permission of the catalogue.");
        if (changing.FirstOrDefault(k => !caller.Covers(k)) is { } beyond)
            return RoleResult<RoleDto>.Refused(403, "grant_beyond_own",
                $"You may give or take away only a permission you hold yourself; you do not hold '{beyond}'.");
        return null;
    }

    private static bool TryScope(string? requested, RoleScope absent, out RoleScope scope)
    {
        scope = absent;
        if (string.IsNullOrWhiteSpace(requested)) return true;
        return Enum.TryParse(requested.Trim(), true, out scope) && Enum.IsDefined(scope) && !int.TryParse(requested, out _);
    }

    private static string? Description(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();

    private Task<bool> NameTakenAsync(string name, Guid? except, CancellationToken ct)
    {
        var normalized = RoleNames.Normalize(name);
        return db.Roles.AnyAsync(r => r.NormalizedName == normalized && r.Id != except, ct);
    }

    /// <summary>Saves; two screens naming two roles alike at the same moment meet at the unique index, said in words.</summary>
    private async Task<RoleResult<RoleDto>?> SaveAsync(string name, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await NameTakenAsync(name, null, ct)) return NameTaken(name);
            throw;
        }
    }

    private static RoleResult<RoleDto> NameTaken(string name) =>
        RoleResult<RoleDto>.Refused(409, RoleNameTaken, $"A role named '{name}' already exists.");

    private static RoleResult<RoleDto> BadScope() =>
        RoleResult<RoleDto>.Refused(400, "role_scope_unknown", "The scope is AssignedBranch, TillBranch or Company.");

    private static RoleResult<RoleDto> ScopeBeyondOwn() =>
        RoleResult<RoleDto>.Refused(403, "scope_beyond_own", "Only someone who works across the company may give a role the whole company.");

    private static RoleResult<RoleDto> HasMembers(int members, string what) =>
        RoleResult<RoleDto>.Refused(409, RoleHasMembers, $"{members} account(s) are on this role. Move them to another role before {what}.");

    private static RoleResult<T> NotFound<T>() => RoleResult<T>.Refused(404, "role_not_found", "That role does not exist.");

    private static RoleDto Map(Role r, IReadOnlySet<string> permissions, int members, int all) =>
        new(r.Id, r.Key, r.Name, r.Description, r.IsActive, r.IsSystem, r.Scope.ToString(), r.Version,
            PermissionCatalog.All.Select(p => p.Key).Where(permissions.Contains).ToList(), members,
            PermissionsEditable: r.Id != SystemRoles.AdminId,
            DetailsEditable: !r.IsSystem,
            Deletable: !r.IsSystem && all == 0);
}
