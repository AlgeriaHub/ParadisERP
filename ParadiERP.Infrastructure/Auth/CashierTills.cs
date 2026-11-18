using System.Globalization;
using System.Text.RegularExpressions;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Auth;

/// <summary>
/// A cashier's own till (owner, 2026-10-05: "the till code is created automatically when a
/// cashier is created", and "no need to sign in with a till code except offline").
///
/// <para>
/// A till-scoped user working at a selling branch owns one <see cref="Device"/>
/// (<see cref="Device.UserId"/>): created with them, moved with them, switched off with them. The
/// password sign-in uses it when no code is typed, so the cashier never types one online; the
/// browser remembers it for the offline PIN sign-in. A shared counter till — a device with no
/// owner — is still opened by typing its code.
/// </para>
/// <para>
/// Decided from the role's scope (AUTH-1), never its name. Nothing here runs at the sale: it is
/// configuration, and the sign-in only ever adds a till, never refuses one that exists.
/// </para>
/// </summary>
public static class CashierTills
{
    private static readonly Regex CodePattern = new(@"^C(\d{1,6})$", RegexOptions.CultureInvariant);

    /// <summary>The stable code the users routes answer a cashier placed on a warehouse with.</summary>
    public const string CashierOnWarehouseCode = "cashier_on_warehouse";

    /// <summary>The sentence that travels with <see cref="CashierOnWarehouseCode"/>.</summary>
    public const string CashierOnWarehouseMessage =
        "A cashier works at a selling branch: a warehouse does not sell. Choose the cashier's branch.";

    /// <summary>The sentence a till-scoped sign-in without a code gets when no till of its own can exist.</summary>
    public const string NoOwnTillMessage =
        "This account has no till of its own: ask the administrator to place it on a selling branch, or open a shared till by its code.";

    /// <summary>The role scope of a role row; a user with no role is treated as till-bound, as ServerAuthorization does.</summary>
    public static async Task<RoleScope> ScopeOfAsync(PosDbContext db, Guid? roleId, CancellationToken ct) =>
        roleId is Guid id
            ? await db.Roles.AsNoTracking().Where(r => r.Id == id).Select(r => (RoleScope?)r.Scope).FirstOrDefaultAsync(ct) ?? RoleScope.TillBranch
            : RoleScope.TillBranch;

    /// <summary>Whether a branch can host a till: it exists, is not deleted and is not a warehouse (any of them).</summary>
    public static Task<bool> IsSellingBranchAsync(PosDbContext db, Guid branchId, CancellationToken ct) =>
        db.Branches.AnyAsync(b => b.Id == branchId && !b.IsDeleted && !b.IsWarehouse, ct);

    /// <summary>
    /// The next free till code in the C01, C02… pattern: one past the highest number any till ever
    /// carried — every device row, and every code a recorded sale names, so a code whose device row
    /// is gone is never handed out again (its receipt numbers are still in the books).
    /// </summary>
    public static async Task<string> NextCodeAsync(PosDbContext db, CancellationToken ct)
    {
        var codes = await db.Devices.AsNoTracking().Select(d => d.Code).ToListAsync(ct);
        // Tracked, not yet saved: two cashiers created in one request must not share a code.
        codes.AddRange(db.ChangeTracker.Entries<Device>().Select(e => e.Entity.Code));
        codes.AddRange(await db.Sales.AsNoTracking().Select(s => s.DeviceCode).Distinct().ToListAsync(ct));

        var highest = 0;
        foreach (var code in codes)
        {
            var match = CodePattern.Match(code ?? "");
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                highest = Math.Max(highest, n);
        }
        return "C" + (highest + 1).ToString("D2", CultureInfo.InvariantCulture);
    }

    /// <summary>The till a user owns, if any (tracked, so the caller can change it).</summary>
    public static Task<Device?> OwnAsync(PosDbContext db, Guid userId, CancellationToken ct) =>
        db.Devices.FirstOrDefaultAsync(d => d.UserId == userId, ct);

    /// <summary>
    /// Brings a user's own till in line with the user after a save: a working till-scoped user on a
    /// selling branch has an active till on that branch, created if missing; a user who is switched
    /// off, deleted, no longer till-scoped or not on a selling branch has theirs switched off — never
    /// deleted, its receipt numbers stay in the books. Changes are tracked; the caller saves.
    /// </summary>
    public static async Task<Device?> AlignAsync(PosDbContext db, User user, RoleScope scope, CancellationToken ct)
    {
        var own = await OwnAsync(db, user.Id, ct);
        var working = user.IsActive && !user.IsDeleted && scope == RoleScope.TillBranch;
        var branch = user.BranchId is Guid b && await IsSellingBranchAsync(db, b, ct) ? b : (Guid?)null;

        if (!working || branch is null)
        {
            if (own is not null) own.IsActive = false;
            return own;
        }

        if (own is null)
        {
            own = new Device
            {
                Code = await NextCodeAsync(db, ct),
                Name = TillName(user),
                BranchId = branch.Value,
                UserId = user.Id,
                IsActive = true,
            };
            db.Devices.Add(own);
            return own;
        }

        own.BranchId = branch.Value;
        own.IsActive = true;
        return own;
    }

    /// <summary>
    /// The till a till-scoped user signs in on when they type no code: their own, as it stands —
    /// an administrator who switched it off meant it, so it is not switched back on here. A cashier
    /// from before own tills existed gets theirs now, saved at once, if their branch can host one.
    /// Null when no till of their own can exist (no selling branch).
    /// </summary>
    public static async Task<Device?> ForSignInAsync(PosDbContext db, User user, RoleScope scope, CancellationToken ct)
    {
        if (await OwnAsync(db, user.Id, ct) is { } own) return own;
        var created = await AlignAsync(db, user, scope, ct);
        if (created is null) return null;
        await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// The highest receipt sequence the server holds for a till, so a browser signing in on it
    /// continues after it instead of numbering from its own counter (two computers, one till).
    /// </summary>
    public static async Task<long> LastSequenceAsync(PosDbContext db, string deviceCode, CancellationToken ct) =>
        await db.Sales.AsNoTracking().Where(s => s.DeviceCode == deviceCode).MaxAsync(s => (long?)s.LocalSeq, ct) ?? 0;

    private static string TillName(User user)
    {
        var name = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName.Trim();
        return name.Length <= 120 ? name : name[..120];
    }
}
