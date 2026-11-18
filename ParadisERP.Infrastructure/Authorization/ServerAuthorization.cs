using System.Security.Claims;
using ParadisERP.Application.Security;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Authorization;

/// <summary>What the server decided a signed-in user may do, and on which data, for this request.</summary>
public sealed record ServerAuthorizationContext(Guid RoleId, RoleScope Scope, IReadOnlySet<string> Permissions)
{
    public bool Has(string key) => Permissions.Contains(key);
}

/// <summary>
/// The authority on what a request may do (AUTH-1 Step 2, REDESIGN_PLAN.md §17.8): the caller's role,
/// its scope and their effective permissions, loaded from <c>Roles</c>, <c>RolePermissions</c> and
/// <c>UserPermissions</c> on every request and carried on the principal as an identity of its own.
///
/// <para>
/// <b>Never from the token.</b> The identity is added by the server after the session check, under an
/// authentication type no token can produce, and <see cref="Read"/> accepts exactly one such identity.
/// A token carrying permission-looking claims grants nothing; a principal without the identity holds
/// nothing and is scoped as narrowly as possible. Every permission requirement, the branch scope and
/// the voucher self-approval rule read this, and nothing else.
/// </para>
/// <para>
/// <b>Read fresh, loaded when it moved (Step 3).</b> Each request reads the user's
/// <see cref="AuthorizationStamp"/> in the session check's own statement; the permissions are loaded
/// only when that stamp differs from the one <see cref="AuthorizationCache"/> holds them under.
/// </para>
/// </summary>
public static class ServerAuthorization
{
    public const string AuthenticationType = "ParadisERP.ServerAuthorization";
    public const string PermissionClaim = "authz.permission";
    public const string ScopeClaim = "authz.scope";
    public const string RoleClaim = "authz.role";

    /// <summary>
    /// Effective permissions = the role's ∪ the user's additional grants. An inactive role grants
    /// nothing of its own (the Admin role cannot be inactive); unknown keys never count.
    /// </summary>
    public static IReadOnlySet<string> Combine(IEnumerable<string> rolePermissions, bool roleActive, IEnumerable<string> grants) =>
        (roleActive ? rolePermissions : []).Concat(grants).Where(PermissionCatalog.Contains).ToHashSet(StringComparer.Ordinal);

    /// <summary>The user row as the session check reads it, with the authorization stamp beside it.</summary>
    public sealed record SessionRow(bool IsActive, bool IsDeleted, long Version, UserRole Role, AuthorizationStamp? Stamp);

    /// <summary>
    /// One statement: the user's session columns and their role's state and version (AUTH-1 Step 3).
    /// Null for an unknown user; <see cref="SessionRow.Stamp"/> is null for a user without a role.
    /// </summary>
    public static async Task<SessionRow?> SessionRowAsync(PosDbContext db, Guid userId, CancellationToken ct)
    {
        var row = await (
                from u in db.Users.AsNoTracking()
                where u.Id == userId
                join r in db.Roles.AsNoTracking() on u.RoleId equals (Guid?)r.Id into roles
                from r in roles.DefaultIfEmpty()
                select new
                {
                    u.IsActive, u.IsDeleted, u.Version, u.Role, u.RoleId, u.AuthorizationVersion,
                    RoleVersion = (long?)r.Version, Scope = (RoleScope?)r.Scope, RoleActive = (bool?)r.IsActive,
                })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var stamp = row is { RoleId: Guid roleId, RoleVersion: long roleVersion, Scope: RoleScope scope, RoleActive: bool active }
            ? new AuthorizationStamp(roleId, roleVersion, row.AuthorizationVersion, scope, active)
            : null;
        return new SessionRow(row.IsActive, row.IsDeleted, row.Version, row.Role, stamp);
    }

    /// <summary>
    /// The effective permissions at <paramref name="stamp"/>, in one statement: the role's (unless it
    /// is inactive) and the user's grants. Read after the stamp, so what it returns is never older
    /// than the stamp it is filed under — a change and its version bump commit together.
    /// </summary>
    public static async Task<ServerAuthorizationContext> LoadAsync(PosDbContext db, Guid userId, AuthorizationStamp stamp, CancellationToken ct)
    {
        var grants = db.UserPermissions.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.PermissionKey);
        var keys = stamp.RoleActive
            ? await db.RolePermissions.AsNoTracking().Where(p => p.RoleId == stamp.RoleId).Select(p => p.PermissionKey)
                .Concat(grants).ToListAsync(ct)
            : await grants.ToListAsync(ct);
        return new ServerAuthorizationContext(stamp.RoleId, stamp.Scope, Combine(keys, true, []));
    }

    /// <summary>The user's authorization as the tables hold it now, uncached; null for an unknown user or one without a role.</summary>
    public static async Task<ServerAuthorizationContext?> ResolveAsync(PosDbContext db, Guid userId, CancellationToken ct) =>
        await SessionRowAsync(db, userId, ct) is { Stamp: { } stamp } ? await LoadAsync(db, userId, stamp, ct) : null;

    public static ClaimsIdentity CreateIdentity(ServerAuthorizationContext context)
    {
        var claims = new List<Claim>
        {
            new(RoleClaim, context.RoleId.ToString()),
            new(ScopeClaim, context.Scope.ToString()),
        };
        claims.AddRange(context.Permissions.Order(StringComparer.Ordinal).Select(p => new Claim(PermissionClaim, p)));
        return new ClaimsIdentity(claims, AuthenticationType);
    }

    /// <summary>The server's decision on this principal; null when there is none, or more than one.</summary>
    public static ServerAuthorizationContext? Read(ClaimsPrincipal? principal)
    {
        var identities = principal?.Identities.Where(i => i.AuthenticationType == AuthenticationType).ToList();
        if (identities is not { Count: 1 }) return null;
        var identity = identities[0];
        if (!Guid.TryParse(identity.FindFirst(RoleClaim)?.Value, out var roleId)
            || !Enum.TryParse<RoleScope>(identity.FindFirst(ScopeClaim)?.Value, out var scope))
            return null;
        var keys = identity.FindAll(PermissionClaim).Select(c => c.Value).Where(PermissionCatalog.Contains)
            .ToHashSet(StringComparer.Ordinal);
        return new ServerAuthorizationContext(roleId, scope, keys);
    }

    public static bool Has(ClaimsPrincipal? principal, string key) => Read(principal)?.Has(key) == true;

    /// <summary>The caller's scope; a principal the server has not decided on is treated as the narrowest.</summary>
    public static RoleScope ScopeOf(ClaimsPrincipal? principal) => Read(principal)?.Scope ?? RoleScope.TillBranch;

    /// <summary>Company scope: every branch, and the choice of branch.</summary>
    public static bool IsCompanyScoped(ClaimsPrincipal? principal) => Read(principal)?.Scope == RoleScope.Company;

    /// <summary>The permissions the pre-AUTH-1 screens and tokens list, in their order (presentation only).</summary>
    public static IReadOnlyList<string> LegacyPermissions(IReadOnlySet<string> effective) =>
        Domain.Permissions.All.Where(effective.Contains).ToList();

    /// <summary>The capability names the current screens read (presentation only), from the effective permissions.</summary>
    public static IReadOnlyList<string> LegacyCapabilities(IReadOnlySet<string> effective)
    {
        var list = new List<string>();
        if (effective.Contains(PermissionCatalog.CustomersCreate)) list.Add(Capabilities.CreateCustomers);
        if (effective.Contains(PermissionCatalog.VouchersApprove)) list.Add(Capabilities.ApprovePaymentVouchers);
        if (effective.Contains(PermissionCatalog.VouchersApproveOwn)) list.Add(Capabilities.ApproveOwnPaymentVouchers);
        if (effective.Contains(PermissionCatalog.CustomersOpeningBalance)) list.Add(Capabilities.SetOpeningBalances);
        if (effective.Contains(PermissionCatalog.SuppliersOpeningBalance)) list.Add(Capabilities.SetSupplierOpeningBalances);
        if (effective.Contains(PermissionCatalog.CustomersAccountsFilter)) list.Add(Capabilities.FilterCustomerStatements);
        if (effective.Contains(PermissionCatalog.InvoicesUntaxed)) list.Add(Capabilities.IssueUntaxedInvoices);
        if (effective.Contains(PermissionCatalog.InventoryTransferWriteOff)) list.Add(Capabilities.WriteOffTransferLoss);
        if (effective.Contains(PermissionCatalog.CustomersDocumentsView)) list.Add(Capabilities.ViewIdentityDocuments);
        if (effective.Contains(PermissionCatalog.SalesUntaxed)) list.Add(Capabilities.SellUntaxed);
        list.AddRange(Capabilities.Manufacturing.Where(effective.Contains));
        list.AddRange(Capabilities.Maintenance.Where(effective.Contains));
        return list;
    }

    /// <summary>The legacy role a scope is written back as, for rollback and older tills only.</summary>
    public static UserRole LegacyRoleFor(RoleScope scope) => scope switch
    {
        RoleScope.Company => UserRole.Admin,
        RoleScope.AssignedBranch => UserRole.Manager,
        _ => UserRole.Cashier,
    };
}
