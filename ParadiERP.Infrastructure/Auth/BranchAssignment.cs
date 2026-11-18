using System.Security.Claims;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Security;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Authorization;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Auth;

/// <summary>
/// The database half of <see cref="BranchLock"/>: which branch a signed-in user works at, read
/// from their row by the token's user id — never from anything the request carries.
/// </summary>
public static class BranchAssignment
{
    /// <summary>
    /// True when the caller's role has <see cref="RoleScope.Company"/> scope — every branch, and the
    /// choice of branch. Read from the server's decision (<see cref="ServerAuthorization"/>), never the token.
    /// </summary>
    public static bool IsCompanyScoped(ClaimsPrincipal user) => ServerAuthorization.IsCompanyScoped(user);

    /// <summary>The company's main branch, or null when none is flagged.</summary>
    public static Task<Guid?> MainBranchAsync(PosDbContext db, CancellationToken ct) =>
        db.Branches.AsNoTracking()
            .Where(b => b.IsMain && !b.IsWarehouse && !b.IsDeleted)
            .OrderBy(b => b.NameFr)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The branch this user works at: their assigned branch, else the main branch
    /// (<see cref="BranchLock.EffectiveBranch"/>).
    /// </summary>
    public static async Task<Guid?> EffectiveBranchAsync(Guid? assigned, PosDbContext db, CancellationToken ct) =>
        BranchLock.EffectiveBranch(assigned, assigned is null ? await MainBranchAsync(db, ct) : null);

    /// <summary>The caller's branch, looked up by the token's <c>sub</c>.</summary>
    public static async Task<Guid?> ForCallerAsync(ClaimsPrincipal caller, PosDbContext db, CancellationToken ct)
    {
        Guid? assigned = null;
        if (Guid.TryParse(caller.FindFirstValue("sub"), out var userId))
            assigned = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.BranchId)
                .FirstOrDefaultAsync(ct);
        return await EffectiveBranchAsync(assigned, db, ct);
    }

    /// <summary>
    /// The user as the login and SSO responses describe them: with the branch they work at and
    /// whether they may choose another, so a screen never has to load branches to decide — and with
    /// the capabilities the server's own decision gives them, so a screen never names a role to decide.
    /// </summary>
    /// <para>
    /// AUTH-1: the lock, the permissions and the capabilities come from the user's role and grants
    /// as the server resolves them. The two lists keep the shape the current screens read — the
    /// pre-AUTH-1 permission names and capability names — and are for presentation only.
    /// </para>
    public static async Task<UserDto> LoginUserAsync(User user, PosDbContext db, CancellationToken ct)
    {
        var decided = await ServerAuthorization.ResolveAsync(db, user.Id, ct);
        var effective = decided?.Permissions ?? new HashSet<string>();
        var roleName = decided is null
            ? null
            : await db.Roles.AsNoTracking().Where(r => r.Id == decided.RoleId).Select(r => r.Name).FirstOrDefaultAsync(ct);
        return new(user.Id, user.Username, user.FullName, user.Role.ToString(),
            await EffectiveBranchAsync(user.BranchId, db, ct),
            BranchLock.IsLocked(decided?.Scope ?? RoleScope.TillBranch),
            ServerAuthorization.LegacyPermissions(effective).ToList(),
            ServerAuthorization.LegacyCapabilities(effective),
            decided?.RoleId, roleName, decided?.Scope.ToString(),
            // Every key, in the catalogue's order, so the screens decide from permissions (AUTH-1 Step 6).
            PermissionCatalog.All.Select(p => p.Key).Where(effective.Contains).ToList());
    }
}
