using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Authorization;

/// <summary>What one run of <see cref="AuthorizationBootstrap"/> changed. All zeros on a second run.</summary>
public sealed record AuthorizationBootstrapResult(
    int RolesCreated, int RolesRepaired, int AdminPermissionsAdded, int AdminPermissionsRemoved,
    int UsersMigrated, int GrantsKept, int GrantsDropped)
{
    public bool ChangedAnything =>
        RolesCreated + RolesRepaired + AdminPermissionsAdded + AdminPermissionsRemoved + UsersMigrated > 0;

    public override string ToString() =>
        $"roles created {RolesCreated}, repaired {RolesRepaired}; Admin baseline +{AdminPermissionsAdded}/-{AdminPermissionsRemoved}; " +
        $"users migrated {UsersMigrated}, grants kept {GrantsKept}, dropped {GrantsDropped}";
}

/// <summary>
/// AUTH-1 Step 1 at start-up, after the schema migration and before any seeder: refuses to run
/// with an unclassified permission, creates the system roles, holds the Admin role to its
/// baseline, and moves every user not yet moved onto a role with their effective grants.
///
/// <para>
/// <b>Idempotent.</b> A user is moved once — the marker is <c>RoleId</c> being set — the roles are
/// created once, and the Admin baseline is only rewritten where it differs. A second run changes
/// nothing and says so. The whole run is one save, so a crash part-way leaves nothing half-moved.
/// </para>
/// <para>
/// Nothing reads these tables to decide a request yet (Step 2 does), and nothing here touches a
/// user's <c>Version</c>, so no session is revoked by the upgrade.
/// </para>
/// </summary>
public static class AuthorizationBootstrap
{
    public static async Task<AuthorizationBootstrapResult> RunAsync(PosDbContext db, CancellationToken ct = default)
    {
        PermissionCatalog.Validate();
        AdminBaseline.Validate();
        Check(SystemRoles.ManagerSeed, "SystemRoles.ManagerSeed");
        Check(SystemRoles.CashierSeed, "SystemRoles.CashierSeed");

        using var allowance = db.AllowAdminBaselineWrite();

        var added = await SystemRoleStore.EnsureAsync(db, ct);
        var created = added.OfType<Role>().Count();

        // A system role keeps its key, scope, mark and — for the Admin — its active state. Drift
        // can only come from a hand edit of the database; it is put back and counted.
        var repaired = 0;
        foreach (var definition in SystemRoles.All)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == definition.Id, ct);
            if (role is null || db.Entry(role).State == EntityState.Added) continue;
            if (role.Key == definition.Key && role.Scope == definition.Scope && role.IsSystem
                && (definition.Id != SystemRoles.AdminId || role.IsActive)) continue;
            role.Key = definition.Key;
            role.Scope = definition.Scope;
            role.IsSystem = true;
            if (definition.Id == SystemRoles.AdminId) role.IsActive = true;
            role.UpdatedAtUtc = DateTime.UtcNow;
            repaired++;
        }

        // Every role's compared name as the code writes it. The upgrade fills the column in SQL, whose upper
        // case is ASCII-only on SQLite; a name with an accent, or one edited by hand, is put right here.
        foreach (var role in await db.Roles.ToListAsync(ct))
        {
            if (db.Entry(role).State == EntityState.Added || role.NormalizedName == RoleNames.Normalize(role.Name)) continue;
            role.NormalizedName = RoleNames.Normalize(role.Name);
            repaired++;
        }

        // The Admin role holds exactly the baseline.
        var addedCount = 0;
        var removedCount = 0;
        if (created == 0 || !added.OfType<Role>().Any(r => r.Id == SystemRoles.AdminId))
        {
            var held = await db.RolePermissions.Where(p => p.RoleId == SystemRoles.AdminId).ToListAsync(ct);
            var baseline = AdminBaseline.Included.ToHashSet(StringComparer.Ordinal);
            foreach (var extra in held.Where(p => !baseline.Contains(p.PermissionKey)))
            {
                db.RolePermissions.Remove(extra);
                removedCount++;
            }
            foreach (var key in baseline.Where(k => held.All(p => p.PermissionKey != k)))
            {
                db.RolePermissions.Add(new RolePermission { RoleId = SystemRoles.AdminId, PermissionKey = key });
                addedCount++;
            }
            // The save bumps the Admin role's version for these rows (AUTH-1 Step 3), so a decision
            // cached before the repair is not used after it.
            if (addedCount + removedCount > 0)
                (await db.Roles.FirstAsync(r => r.Id == SystemRoles.AdminId, ct)).UpdatedAtUtc = DateTime.UtcNow;
        }

        // Every user not yet on a role, deleted ones included: their history still names them.
        var users = await db.Users.Where(u => u.RoleId == null).ToListAsync(ct);
        var kept = 0;
        var dropped = 0;
        foreach (var user in users)
        {
            var outcome = await AuthorizationShadow.MigrateAsync(db, user, "migration", ct);
            kept += outcome.Kept.Count;
            dropped += outcome.Dropped.Count;
        }

        await db.SaveChangesAsync(ct);
        return new AuthorizationBootstrapResult(created, repaired, addedCount, removedCount, users.Count, kept, dropped);
    }

    private static void Check(IEnumerable<string> keys, string where)
    {
        var stray = keys.FirstOrDefault(k => !PermissionCatalog.Contains(k));
        if (stray is not null)
            throw new InvalidOperationException($"{where} names '{stray}', which is not in PermissionCatalog.All.");
    }
}
