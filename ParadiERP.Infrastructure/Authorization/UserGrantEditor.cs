using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Authorization;

/// <summary>
/// A person's additional permissions, the whole catalogue at once (AUTH-1 §17.10, the users screen's
/// "additional permissions"): any key given to one account on top of its role.
///
/// <para>
/// The screen sends the account's full set. The writers that came before it — the five legacy ticks
/// (<see cref="UserRoleWriter"/>), "sell untaxed" (<see cref="UntaxedSalesGrant"/>) and the manufacturing
/// access page — each touch only their own keys; this one owns every key, so it is guarded by the account's
/// <c>AuthorizationVersion</c>: a screen that opened the account before someone else changed its grants or
/// its role is told, and changes nothing, rather than taking away what it never saw.
/// </para>
/// <para>The rules, each refused in words with a code:</para>
/// <list type="bullet">
/// <item>an unknown key (<c>unknown_permission</c>), never dropped in silence;</item>
/// <item>a key given or taken away that the caller neither holds nor covers (<c>grant_beyond_own</c>),
/// so nobody hands out more than their own authority (§17.3);</item>
/// <item>a key the account's role already gives is not stored a second time, and a copy stored before the role
/// gave it goes on the next save, as with the other writers: what the account holds beyond its role is always what
/// the screen shows as its own, never a hidden row that would outlive a change of role.</item>
/// </list>
/// <para>
/// The legacy column <c>Users.Permissions</c> is written beside it with the five legacy keys among the
/// grants, for rollback and for a screen from before; nothing reads it to decide access.
/// </para>
/// </summary>
public static class UserGrantEditor
{
    public const string UnknownPermissionCode = "unknown_permission";
    public const string UserChangedCode = "user_changed";
    public const string UserChangedMessage =
        "Someone changed this account's role or permissions after you opened it. Open it again to see them.";

    /// <summary>What a save would add and remove, or the first key that refuses it.</summary>
    public sealed record Plan(IReadOnlyList<string> Added, IReadOnlyList<UserPermission> Removed, IReadOnlyList<string> Kept)
    {
        public string? Unknown { get; init; }
        public string? Beyond { get; init; }
    }

    /// <summary>
    /// The change from what the account holds of its own to <paramref name="wanted"/>, on the role it is being
    /// saved on. Nothing is written; <see cref="Apply"/> does that once every check of the save has passed.
    /// </summary>
    public static async Task<Plan> PlanAsync(PosDbContext db, User user, bool isNew, Guid roleId, IEnumerable<string> wanted,
        IReadOnlySet<string> caller, CancellationToken ct)
    {
        var want = wanted.Where(k => k is not null).Select(k => k.Trim()).ToHashSet(StringComparer.Ordinal);
        if (want.FirstOrDefault(k => !PermissionCatalog.Contains(k)) is string unknown)
            return new Plan([], [], []) { Unknown = unknown };

        // What the role gives, read as the server's authorization reads it: nothing from a role switched off.
        var role = await db.Roles.AsNoTracking().Where(r => r.Id == roleId).Select(r => new { r.IsActive }).FirstOrDefaultAsync(ct);
        var fromRole = role is { IsActive: true }
            ? (await db.RolePermissions.AsNoTracking().Where(p => p.RoleId == roleId).Select(p => p.PermissionKey).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal)
            : [];
        var current = isNew ? [] : await db.UserPermissions.Where(p => p.UserId == user.Id).ToListAsync(ct);

        var store = want.Where(k => !fromRole.Contains(k)).ToHashSet(StringComparer.Ordinal);
        var added = store.Where(k => current.All(c => c.PermissionKey != k)).OrderBy(Order).ToList();
        var removed = current.Where(c => !store.Contains(c.PermissionKey)).ToList();
        var kept = store.OrderBy(Order).ToList();

        // A copy the role now gives anyway changes nobody's access by going, so it needs no authority of the caller's.
        var beyond = added.Concat(removed.Where(r => !fromRole.Contains(r.PermissionKey)).Select(r => r.PermissionKey))
            .FirstOrDefault(k => PermissionCatalog.Contains(k) && !PermissionCatalog.Covers(caller, k));
        return new Plan(added, removed, kept) { Beyond = beyond };
    }

    /// <summary>Writes a plan that passed: the rows, and the legacy column beside them.</summary>
    public static void Apply(PosDbContext db, User user, Plan plan, string? grantedBy)
    {
        foreach (var gone in plan.Removed) db.UserPermissions.Remove(gone);
        foreach (var key in plan.Added)
            db.UserPermissions.Add(new UserPermission { UserId = user.Id, PermissionKey = key, GrantedBy = grantedBy });
        user.Permissions = Domain.Permissions.Store(plan.Kept.Where(k => Domain.Permissions.All.Contains(k, StringComparer.Ordinal)));
    }

    /// <summary>The account's own keys, in the catalogue's order, for the users list.</summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<string>>> OwnAsync(PosDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        (await db.UserPermissions.AsNoTracking().Where(p => userIds.Contains(p.UserId)).ToListAsync(ct))
            .Where(p => PermissionCatalog.Contains(p.PermissionKey))
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.PermissionKey).Distinct(StringComparer.Ordinal).OrderBy(Order).ToList());

    private static readonly Dictionary<string, int> Positions = PermissionCatalog.All
        .Select((p, i) => (p.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);

    private static int Order(string key) => Positions.GetValueOrDefault(key, int.MaxValue);
}
