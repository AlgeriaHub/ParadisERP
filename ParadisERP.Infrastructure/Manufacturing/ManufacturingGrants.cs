using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Manufacturing;

/// <summary>
/// Who holds the manufacturing permissions, and giving them to one login (audit D36: "permissions granted
/// per user on the users screen, until the roles screens of AUTH-1 exist").
///
/// <para>
/// The grants are ordinary additional permissions in <c>UserPermissions</c>, read by the server's own
/// authorization like every other key; saving one bumps the user's authorization version, so the next
/// request already sees it. This writer touches the five manufacturing keys only, and the users screen's
/// writer touches the five legacy keys only, so neither can remove what the other gave.
/// </para>
/// <para>
/// Delegation (hardening review, 2026-10-26). The route needs <c>users.manage</c>, which only the Admin
/// holds today; these rules hold the day a custom role carries it too:
/// <list type="bullet">
/// <item>A caller gives or takes away only a manufacturing permission they hold themselves — never more
/// than their own authority, and never to themselves anything they lack.</item>
/// <item>A caller without company scope sees and changes only the users of the branch they work at, and
/// never a user whose role spans the company: such a user is "not found", as another branch's document is.</item>
/// <item>A key outside the five is refused, so this page can never hand out another module's permission.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ManufacturingGrants(PosDbContext db)
{
    private static readonly HashSet<string> Keys = Capabilities.Manufacturing.ToHashSet(StringComparer.Ordinal);

    public async Task<List<ManufacturingAccessDto>> ListAsync(MfgCaller caller, CancellationToken ct)
    {
        var users = await Reachable(caller).AsNoTracking().ToListAsync(ct);
        var roleIds = users.Where(u => u.RoleId != null).Select(u => u.RoleId!.Value).Distinct().ToList();
        var roles = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var fromRoles = (await db.RolePermissions.AsNoTracking().Where(p => roleIds.Contains(p.RoleId)).ToListAsync(ct))
            .Where(p => Keys.Contains(p.PermissionKey)).GroupBy(p => p.RoleId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.PermissionKey).ToList());
        var userIds = users.Select(u => u.Id).ToList();
        var grants = (await db.UserPermissions.AsNoTracking().Where(p => userIds.Contains(p.UserId)).ToListAsync(ct))
            .Where(p => Keys.Contains(p.PermissionKey)).GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.PermissionKey).ToList());

        return users.OrderBy(u => u.FullName, StringComparer.CurrentCulture).Select(u =>
        {
            var role = u.RoleId is Guid r ? roles.GetValueOrDefault(r) : null;
            var held = role is { IsActive: true } ? fromRoles.GetValueOrDefault(role.Id, []) : [];
            return new ManufacturingAccessDto(u.Id, u.Username, u.FullName, role?.Name ?? u.Role.ToString(), u.BranchId,
                Ordered(held), Ordered(grants.GetValueOrDefault(u.Id, [])));
        }).ToList();
    }

    /// <summary>
    /// Sets the user's manufacturing grants to exactly these keys. A key the user's role already holds is
    /// not stored a second time; a key outside the manufacturing module is refused, and so is a change to a
    /// key the caller does not hold.
    /// </summary>
    public async Task<MfgResult<ManufacturingAccessDto>> SetAsync(
        MfgCaller caller, Guid userId, SetManufacturingAccessRequest r, CancellationToken ct)
    {
        var user = await Reachable(caller).FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return MfgFail.NotFound<ManufacturingAccessDto>("User");
        var wanted = (r.Keys ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.FirstOrDefault(k => !Keys.Contains(k)) is string stray)
            return MfgFail.Invalid<ManufacturingAccessDto>("unknown_permission", $"'{stray}' is not a manufacturing permission.");

        // What the user's role gives them, read as the server's authorization reads it: nothing from a role
        // that is switched off (ServerAuthorization.Combine), so a grant is not skipped for a role that
        // would not have given it.
        var role = user.RoleId is Guid roleId ? await db.Roles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == roleId, ct) : null;
        var fromRole = role is { IsActive: true }
            ? await db.RolePermissions.AsNoTracking().Where(p => p.RoleId == role.Id).Select(p => p.PermissionKey).ToListAsync(ct)
            : [];
        var store = wanted.Where(k => !fromRole.Contains(k)).ToHashSet(StringComparer.Ordinal);
        var current = (await db.UserPermissions.Where(p => p.UserId == userId).ToListAsync(ct))
            .Where(p => Keys.Contains(p.PermissionKey)).ToList();
        var added = store.Where(k => current.All(c => c.PermissionKey != k)).ToList();
        var removed = current.Where(c => !store.Contains(c.PermissionKey)).ToList();

        if (added.Concat(removed.Select(g => g.PermissionKey)).FirstOrDefault(k => !caller.Has(k)) is string beyond)
            return MfgFail.Forbidden<ManufacturingAccessDto>(
                $"You may give or take away only a permission you hold yourself; you do not hold '{beyond}'.", "grant_beyond_own");

        foreach (var gone in removed) db.UserPermissions.Remove(gone);
        foreach (var key in added)
            db.UserPermissions.Add(new UserPermission { UserId = userId, PermissionKey = key, GrantedBy = caller.Name });
        await db.SaveChangesAsync(ct);

        var row = (await ListAsync(caller, ct)).FirstOrDefault(a => a.UserId == userId);
        return row is null ? MfgFail.NotFound<ManufacturingAccessDto>("User") : MfgResult<ManufacturingAccessDto>.Done(row);
    }

    /// <summary>
    /// The users this caller may see and change here: every active user for company scope; otherwise the
    /// users of the caller's own branch whose role does not span the company.
    /// </summary>
    private IQueryable<User> Reachable(MfgCaller caller)
    {
        var users = db.Users.Where(u => !u.IsDeleted && u.IsActive);
        if (caller.CompanyScoped) return users;
        var branch = caller.BranchId;
        if (branch is null) return users.Where(_ => false);
        return users.Where(u => u.BranchId == branch
            && !db.Roles.Any(x => x.Id == u.RoleId && x.Scope == RoleScope.Company));
    }

    private static List<string> Ordered(IEnumerable<string> keys) =>
        Capabilities.Manufacturing.Where(keys.Contains).ToList();
}
