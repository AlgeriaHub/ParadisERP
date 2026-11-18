using System.Security.Claims;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Auth;

/// <summary>
/// Which branch a caller may read (docs/07 §2).
///
/// <para>
/// A cashier sees one branch: the branch of the terminal the session was opened on, derived
/// from the token's <c>device</c> claim through <c>Device.BranchId</c> — never from a
/// <c>branchId</c> or <c>deviceCode</c> the request carries. Sync has always worked that way
/// (SyncService, pinned by SyncDeviceBindingTests); this extends the same derivation to the
/// other routes a till may call. A role with the company's scope (the Admin) sees every branch.
/// A role scoped to its assigned branch (the Manager, and by default every role an Admin creates)
/// sees that branch — the one <see cref="BranchAssignment.ForCallerAsync"/> reads, the branch its
/// lists and reports already used: since P0.1b (owner, 2026-10-25) a manager no longer opens
/// another branch's sale by id, nor sends its receipt. The only named permission,
/// <see cref="Permissions.ViewOtherBranchStock"/>, is about stock and stays stock-only (§8 Q3).
/// </para>
/// <para>
/// Read-only and never throwing, because nothing on this path may block a sale. An unknown
/// device scopes to <b>nothing</b> (<see cref="Guid.Empty"/> matches no branch) rather than to
/// everything; an unknown or inactive device already fails token validation
/// (UserSessionValidator), so that branch is defence, not the control.
/// </para>
/// </summary>
public static class BranchScope
{
    /// <summary>The claim TokenService stamps the terminal code into.</summary>
    public const string DeviceClaim = "device";

    /// <summary>
    /// The branch this caller is confined to, or <c>null</c> for a caller who sees all of them:
    /// <c>null</c> for the company's scope; the assigned branch (else the main branch, for a legacy
    /// account with none) for an assigned-branch role; the till's branch for a cashier.
    /// </summary>
    public static async Task<Guid?> ForAsync(ClaimsPrincipal user, PosDbContext db, CancellationToken ct)
    {
        switch (Authorization.ServerAuthorization.ScopeOf(user))
        {
            case RoleScope.Company:
                return null;
            case RoleScope.AssignedBranch:
                return await BranchAssignment.ForCallerAsync(user, db, ct) ?? Guid.Empty;
            default:
                return await DeviceBranchAsync(user, db, ct) ?? Guid.Empty;
        }
    }

    /// <summary>
    /// The login refusal of §2.2, kept as a pure rule so it can be pinned without an HTTP host:
    /// anyone bound to a branch may only open a session on that branch's terminals. Since the
    /// security review of 2026-10-10 that covers a branch-scoped manager too (owner-approved,
    /// Phase 1): a till session writes into its branch's drawer, stock and receipt numbers, so a
    /// manager of branch A signed in on branch B's till was writing B's books. A company-wide role
    /// (the administrator) and an account with no binding may still use any till.
    /// </summary>
    public static bool RefusesTerminal(User user, RoleScope scope, Device device) =>
        scope != RoleScope.Company && user.BranchId is Guid bound && device.BranchId != bound;

    /// <summary>The stable code the login and SSO routes answer a refused terminal with.</summary>
    public const string WrongTerminalCode = "wrong_terminal";

    /// <summary>The sentence that travels with <see cref="WrongTerminalCode"/>.</summary>
    public const string WrongTerminalMessage = "This account may not open a session on that terminal.";

    /// <summary>
    /// An office sign-in: a session opened with no terminal code, to work in the back office from a
    /// computer that is not a till (owner, 2026-10-05: "admin can log in without a till"). Its
    /// token carries an empty <see cref="DeviceClaim"/>. Only a role that reads beyond a till's
    /// branch may hold one — a till-scoped role's whole scope is "the till's branch", so without a
    /// till it would have nothing to stand on. Decided from the role's scope, never its name.
    /// </summary>
    public static bool AllowsOfficeSession(RoleScope scope) => scope != RoleScope.TillBranch;

    /// <summary>The stable code the login answers a till-scoped account that gave no terminal code.</summary>
    public const string TillRequiredCode = "till_required";

    /// <summary>The sentence that travels with <see cref="TillRequiredCode"/>.</summary>
    public const string TillRequiredMessage = "This account signs in at a till: enter the till's code.";

    /// <summary>The stable code a till-only route answers an office session with.</summary>
    public const string NoTerminalCode = "no_terminal";

    /// <summary>The sentence that travels with <see cref="NoTerminalCode"/>.</summary>
    public const string NoTerminalMessage = "This session was opened without a till. Sign in with a till's code to sell.";

    /// <summary>
    /// The branch a <b>stock</b> read is confined to for a caller without
    /// <see cref="Permissions.ViewOtherBranchStock"/>: their own binding, else their device's
    /// branch, else nothing. <c>null</c> when the claim is held. This is the §2.3 rule for the
    /// two catalog routes (<c>/products/low-stock</c>, <c>/products/{id}/stock</c>) that listed
    /// every branch's position around the permission StockEndpoints enforces.
    /// <para>
    /// <c>User.BranchId</c> is read for cashiers only, the same way <see cref="ForAsync"/> reads
    /// it: the column binds cashiers, and a manager promoted out of a branch keeps the stale row
    /// the users tab already says is ignored for them. A manager without the permission is still
    /// confined — to the branch of the till they are signed in at, not to yesterday's posting.
    /// </para>
    /// </summary>
    public static async Task<Guid?> ForStockAsync(ClaimsPrincipal user, PosDbContext db, CancellationToken ct)
    {
        if (Authorization.ServerAuthorization.Has(user, PermissionCatalog.StockViewOtherBranches)) return null;
        if (!SeesEveryBranch(user) && Guid.TryParse(user.FindFirstValue("sub"), out var userId))
        {
            var bound = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.BranchId)
                .FirstOrDefaultAsync(ct);
            if (bound is Guid b) return b;
        }
        return await DeviceBranchAsync(user, db, ct) ?? Guid.Empty;
    }

    /// <summary>
    /// Every scope but <see cref="RoleScope.TillBranch"/> reads across branches by id (AUTH-1: the
    /// role's scope as the server resolved it; a principal it has not decided on is till-bound).
    /// </summary>
    private static bool SeesEveryBranch(ClaimsPrincipal user) =>
        Authorization.ServerAuthorization.ScopeOf(user) != RoleScope.TillBranch;

    private static async Task<Guid?> DeviceBranchAsync(ClaimsPrincipal user, PosDbContext db, CancellationToken ct)
    {
        var code = user.FindFirstValue(DeviceClaim);
        if (string.IsNullOrWhiteSpace(code)) return null;
        return await db.Devices.AsNoTracking()
            .Where(d => d.Code == code)
            .Select(d => (Guid?)d.BranchId)
            .FirstOrDefaultAsync(ct);
    }
}
