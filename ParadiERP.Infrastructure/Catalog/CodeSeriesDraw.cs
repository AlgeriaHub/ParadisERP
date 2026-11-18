using ParadiERP.Application.Catalog;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Catalog;

/// <summary>
/// The database half of the customer and employee codes (<see cref="CodeSeries"/> holds the shape):
/// reads the codes already in a counter and names the next one.
///
/// <para>
/// <b>Drawn at save time, and only there</b> (owner, 2026-10-05: "the code must be taken when Save
/// is clicked, so several admins with the create page open at the same time never get a
/// duplicate"). Nothing reserves or shows a code earlier — there is no "next code" route — so a
/// form that is abandoned burns nothing and two open forms cannot be promised the same number.
/// </para>
/// <para>
/// <b>Why two saves at the same moment do not meet.</b> The customer and employee create routes run
/// behind <c>SubscriptionWriteFilter</c>, whose transaction starts with <c>UPDATE SyncCounters</c>:
/// the row stays write-locked until that request commits, on SQLite and on SQL Server alike, so
/// every catalog write — this draw and the insert after it included — runs one at a time, and each
/// draw sees the previous one's committed code. The unique indexes on <c>Customers.Code</c> and
/// <c>Employees.Number</c> are the backstop for anything that writes outside that lock; the callers
/// draw again when one trips.
/// </para>
/// </summary>
public static class CodeSeriesDraw
{
    /// <summary>
    /// The draw itself: the sequence the counter says comes next, in; the one tried, out. Replaced by
    /// a test only, to force a collision the way <c>LoginIds.Draw</c> is; production never sets it.
    /// </summary>
    public static Func<int, int> Draw { get; set; } = next => next;

    /// <summary>The next code for a new customer of <paramref name="kind"/> created at a location.</summary>
    public static async Task<string> CustomerAsync(
        PosDbContext db, CustomerKind kind, Guid branchId, DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var stem = CodeSeries.Stem(CodeSeries.CustomerPrefix(kind), await BranchCodeAsync(db, branchId, ct), CodeSeries.Month(now, zone));
        // Every row with the stem, deleted ones included: a number handed out once stays taken. The
        // prefix match is only a narrowing (a LIKE the unique index serves); the shape is decided in
        // CodeSeries, so a look-alike such as Cust-1126100042 (a leading zero on a four-digit tail,
        // which Format never writes) does not move the counter.
        var codes = await db.Customers.AsNoTracking().Where(c => c.Code.StartsWith(stem)).Select(c => c.Code).ToListAsync(ct);
        return CodeSeries.Format(stem, Draw(CodeSeries.NextSequence(codes, stem)));
    }

    /// <summary>The next number for a new employee of a location.</summary>
    public static async Task<string> EmployeeAsync(
        PosDbContext db, Guid branchId, DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var stem = CodeSeries.Stem(CodeSeries.EmployeePrefix, await BranchCodeAsync(db, branchId, ct), CodeSeries.Month(now, zone));
        var numbers = await db.Employees.AsNoTracking().Where(e => e.Number.StartsWith(stem)).Select(e => e.Number).ToListAsync(ct);
        return CodeSeries.Format(stem, Draw(CodeSeries.NextSequence(numbers, stem)));
    }

    /// <summary>Whether a stored customer already carries this code (any kind, deleted or not).</summary>
    public static Task<bool> CustomerTakenAsync(PosDbContext db, string code, CancellationToken ct = default) =>
        db.Customers.AsNoTracking().AnyAsync(c => c.Code == code, ct);

    /// <summary>Whether a stored employee already carries this number.</summary>
    public static Task<bool> EmployeeTakenAsync(PosDbContext db, string number, CancellationToken ct = default) =>
        db.Employees.AsNoTracking().AnyAsync(e => e.Number == number, ct);

    /// <exception cref="InvalidOperationException">For a location that does not exist.</exception>
    private static async Task<string> BranchCodeAsync(PosDbContext db, Guid branchId, CancellationToken ct) =>
        await db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Code).FirstOrDefaultAsync(ct)
        ?? throw new InvalidOperationException($"Location {branchId} does not exist.");
}
