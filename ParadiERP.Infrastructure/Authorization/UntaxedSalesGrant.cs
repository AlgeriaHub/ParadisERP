using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Authorization;

/// <summary>
/// "Sell untaxed at the till" for one login (TAX-01, finding E#1; owner, 2026-11-02).
///
/// <para>
/// The Admin and Manager roles hold <c>sales.untaxed</c>. A cashier holds it as an additional permission in
/// <c>UserPermissions</c>, ticked on the users screen, until the roles screens of AUTH-1 exist. It lies outside
/// the five legacy keys, so the users screen's own grant writer (<c>AuthorizationShadow.WriteGrantsAsync</c>)
/// never removes it, just as the manufacturing grants (<c>ManufacturingGrants</c>).
/// </para>
/// <para>
/// A caller gives or takes it away only when they hold it themselves. A role that already gives it keeps no
/// second row, so ticking it for a Manager stores nothing.
/// </para>
/// </summary>
public static class UntaxedSalesGrant
{
    /// <summary>The active roles that give <c>sales.untaxed</c> — read as the server's authorization reads a role.</summary>
    public static async Task<HashSet<Guid>> RolesGrantingAsync(PosDbContext db, CancellationToken ct) =>
        (await db.RolePermissions.AsNoTracking()
            .Where(p => p.PermissionKey == PermissionCatalog.SalesUntaxed && db.Roles.Any(r => r.Id == p.RoleId && r.IsActive))
            .Select(p => p.RoleId).ToListAsync(ct)).ToHashSet();

    /// <summary>
    /// Sets the user's own grant to <paramref name="wanted"/>. Returns false, and changes nothing, when the
    /// change would need a key the caller does not hold.
    /// </summary>
    public static async Task<bool> SetAsync(
        PosDbContext db, User user, bool wanted, bool callerHolds, string? grantedBy, CancellationToken ct)
    {
        var byRole = user.RoleId is Guid roleId && (await RolesGrantingAsync(db, ct)).Contains(roleId);
        var store = wanted && !byRole;
        var isNew = db.Entry(user).State == EntityState.Added;
        var tracked = db.ChangeTracker.Entries<UserPermission>()
            .Where(e => e.Entity.UserId == user.Id && e.State == EntityState.Added && e.Entity.PermissionKey == PermissionCatalog.SalesUntaxed)
            .Select(e => e.Entity);
        var current = (isNew ? [] : await db.UserPermissions
                .Where(p => p.UserId == user.Id && p.PermissionKey == PermissionCatalog.SalesUntaxed).ToListAsync(ct))
            .Concat(tracked).Distinct(ReferenceEqualityComparer.Instance).Cast<UserPermission>().ToList();

        if (store == (current.Count > 0)) return true;
        if (!callerHolds) return false;
        if (store)
            db.UserPermissions.Add(new UserPermission { UserId = user.Id, PermissionKey = PermissionCatalog.SalesUntaxed, GrantedBy = grantedBy });
        else
            foreach (var grant in current) db.UserPermissions.Remove(grant);
        return true;
    }
}
