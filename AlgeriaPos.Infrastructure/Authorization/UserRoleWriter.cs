using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Authorization;

/// <summary>
/// AUTH-1 Step 2, transitional: what the current users screen sends — a role name and a list of the
/// five legacy permission names — written into the tables that now decide access.
///
/// <para>
/// The request is translated by the same rule as the migration (<see cref="LegacyGrants"/>): the
/// role becomes its system role, and a grant is stored only where it gives the user something; a
/// grant that would give nothing is reported, as before. The legacy columns are written beside it
/// from the same request, only so the previous build can be rolled back to; nothing reads them to
/// decide access. Step 5 replaces this with the roles API (a role id and additional permissions).
/// </para>
/// </summary>
public static class UserRoleWriter
{
    public static async Task AssignAsync(PosDbContext db, User user, UserRole role, string? storedGrants, string? grantedBy, CancellationToken ct)
    {
        var outcome = LegacyGrants.Map(role, storedGrants);
        user.Role = role;
        user.Permissions = storedGrants;
        user.RoleId = outcome.RoleId;
        await AuthorizationShadow.WriteGrantsAsync(db, user, outcome, "user-save", grantedBy, ct);
    }

    /// <summary>The role a users-screen save names, with what the rest of the save reads from it.</summary>
    public sealed record RoleChoice(Guid RoleId, RoleScope Scope, UserRole Legacy, bool IsSystem);

    /// <summary>
    /// The role a save names (AUTH-1 Step 5): by id, any role switched on — or the one the user is already on,
    /// even switched off; or, from a screen that predates role ids, a system role by its legacy name. That older
    /// screen knows no other role, so a user on a role the company created, saved from it under the name their
    /// role's scope reads as, stays on their role rather than being moved to the system one. Null when unknown.
    /// </summary>
    public static async Task<RoleChoice?> ChooseAsync(PosDbContext db, Guid? roleId, string? legacyName, Guid? currentRoleId, CancellationToken ct)
    {
        if (roleId is Guid id)
        {
            var role = await db.Roles.AsNoTracking().Where(r => r.Id == id)
                .Select(r => new { r.Id, r.Scope, r.IsActive, r.IsSystem }).FirstOrDefaultAsync(ct);
            if (role is null || (!role.IsActive && role.Id != currentRoleId)) return null;
            return new RoleChoice(role.Id, role.Scope, ServerAuthorization.LegacyRoleFor(role.Scope), role.IsSystem);
        }
        if (!Enum.TryParse<UserRole>(legacyName, true, out var legacy) || !Enum.IsDefined(legacy)) return null;
        if (currentRoleId is Guid current && SystemRoles.All.All(d => d.Id != current))
        {
            var custom = await db.Roles.AsNoTracking().Where(r => r.Id == current).Select(r => (RoleScope?)r.Scope).FirstOrDefaultAsync(ct);
            if (custom is RoleScope scope && ServerAuthorization.LegacyRoleFor(scope) == legacy)
                return new RoleChoice(current, scope, legacy, false);
        }
        var system = SystemRoles.For(legacy);
        return new RoleChoice(system.Id, system.Scope, legacy, true);
    }

    /// <summary>
    /// Puts the user on the chosen role with the legacy ticks the screen sent. A system role goes through
    /// <see cref="AssignAsync"/> exactly as before; on a role the company created, a tick is stored where the
    /// role does not already give it.
    /// </summary>
    public static async Task AssignChoiceAsync(PosDbContext db, User user, RoleChoice choice, string? storedGrants, string? grantedBy, CancellationToken ct)
    {
        if (choice.IsSystem)
        {
            await AssignAsync(db, user, choice.Legacy, storedGrants, grantedBy, ct);
            return;
        }
        user.Permissions = storedGrants;
        user.RoleId = choice.RoleId;
        await AuthorizationShadow.WriteGrantsAsync(db, user,
            new LegacyGrants.Outcome(choice.RoleId, await KeptAsync(db, choice, storedGrants, ct), []), "user-save", grantedBy, ct);
    }

    /// <summary>
    /// The first permission the save would give or take away that the caller does not hold (or cover): every
    /// permission of the role left and of the role taken, when the role changes, and each legacy tick that
    /// changes. Null when the caller holds them all — always so for the Admin. Nobody hands out more than
    /// their own authority (AUTH-1, §17.3), by a role any more than by a tick.
    /// </summary>
    public static async Task<string?> BeyondCallerAsync(PosDbContext db, User user, bool isNew, RoleChoice choice, string? storedGrants,
        IReadOnlySet<string> caller, CancellationToken ct)
    {
        var keys = await RoleKeysMovedAsync(db, user, isNew, choice, ct);
        var before = isNew
            ? []
            : (await db.UserPermissions.AsNoTracking().Where(p => p.UserId == user.Id).Select(p => p.PermissionKey).ToListAsync(ct))
                .Where(k => Domain.Permissions.All.Contains(k, StringComparer.Ordinal)).ToList();
        var after = await KeptAsync(db, choice, storedGrants, ct);
        keys.AddRange(before.Except(after, StringComparer.Ordinal));
        keys.AddRange(after.Except(before, StringComparer.Ordinal));
        return keys.FirstOrDefault(k => PermissionCatalog.Contains(k) && !PermissionCatalog.Covers(caller, k));
    }

    /// <summary>
    /// The first permission a change of role would give or take away that the caller does not hold (or cover):
    /// every permission of the role left and of the role taken. Null when the role does not change.
    /// </summary>
    public static async Task<string?> RoleBeyondCallerAsync(PosDbContext db, User user, bool isNew, RoleChoice choice,
        IReadOnlySet<string> caller, CancellationToken ct) =>
        (await RoleKeysMovedAsync(db, user, isNew, choice, ct))
            .FirstOrDefault(k => PermissionCatalog.Contains(k) && !PermissionCatalog.Covers(caller, k));

    private static async Task<List<string>> RoleKeysMovedAsync(PosDbContext db, User user, bool isNew, RoleChoice choice, CancellationToken ct)
    {
        if (!isNew && user.RoleId == choice.RoleId) return [];
        var roles = isNew || user.RoleId is null ? [choice.RoleId] : new[] { choice.RoleId, user.RoleId.Value };
        return await db.RolePermissions.AsNoTracking().Where(p => roles.Contains(p.RoleId)).Select(p => p.PermissionKey).ToListAsync(ct);
    }

    /// <summary>
    /// Puts the user on the chosen role and nothing else: their grants are the caller's to write
    /// (<see cref="UserGrantEditor"/>). The legacy role follows the role at save time.
    /// </summary>
    public static void PlaceOnRole(User user, RoleChoice choice)
    {
        user.RoleId = choice.RoleId;
        user.Role = choice.Legacy;
    }

    /// <summary>The legacy ticks that are stored for this role: the migration rule's on a system role, else those it lacks.</summary>
    private static async Task<IReadOnlyList<string>> KeptAsync(PosDbContext db, RoleChoice choice, string? storedGrants, CancellationToken ct)
    {
        if (choice.IsSystem) return LegacyGrants.Map(choice.Legacy, storedGrants).Kept;
        var fromRole = await db.RolePermissions.AsNoTracking().Where(p => p.RoleId == choice.RoleId).Select(p => p.PermissionKey).ToListAsync(ct);
        return Domain.Permissions.Parse(storedGrants).Where(k => !fromRole.Contains(k)).ToList();
    }

    /// <summary>The effective permissions of many users at once, for a list (presentation).</summary>
    public static async Task<Dictionary<Guid, IReadOnlySet<string>>> EffectiveAsync(PosDbContext db, IReadOnlyCollection<User> users, CancellationToken ct)
    {
        var roleIds = users.Where(u => u.RoleId is not null).Select(u => u.RoleId!.Value).Distinct().ToList();
        var userIds = users.Select(u => u.Id).ToList();
        var active = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.IsActive, ct);
        var rolePermissions = (await db.RolePermissions.AsNoTracking().Where(p => roleIds.Contains(p.RoleId)).ToListAsync(ct))
            .GroupBy(p => p.RoleId).ToDictionary(g => g.Key, g => g.Select(p => p.PermissionKey).ToList());
        var grants = (await db.UserPermissions.AsNoTracking().Where(p => userIds.Contains(p.UserId)).ToListAsync(ct))
            .GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.Select(p => p.PermissionKey).ToList());

        return users.ToDictionary(u => u.Id, u => u.RoleId is Guid r
            ? ServerAuthorization.Combine(rolePermissions.GetValueOrDefault(r, []), active.GetValueOrDefault(r), grants.GetValueOrDefault(u.Id, []))
            : (IReadOnlySet<string>)new HashSet<string>());
    }
}
