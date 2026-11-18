using System.Globalization;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Ledger;

/// <summary>What one close or reopen asked for answered: the state now in force, or why it was refused.</summary>
public sealed record BookClosingResult(BookClosing? Closing, string? Problem)
{
    public bool Ok => Problem is null;
    public static BookClosingResult Refused(string problem) => new(null, problem);
}

/// <summary>
/// Closing the books (CLO-01; owner, 2026-10-31: "close periods after review; block back-dated postings into closed
/// periods; controlled corrections with an audit trail", and 2026-11-01: the Admin closes and reopens).
///
/// <para>
/// The books close by whole months, forward only: "closed through" is always the last day of a month that has
/// ended, on the company's clock. Going back is a reopen — to an earlier month end, or to nothing closed — and it
/// always says why. Every close and reopen is a row, kept: the latest is in force and the rows are the audit trail.
/// </para>
/// <para>
/// <see cref="LedgerPoster"/> refuses any entry dated before the first open instant. The one exception is the till:
/// a sale is never refused (rule 1), so a sale dated in a closed month is booked on the first open day instead
/// (owner, 2026-11-01; the accountant confirms under SAL-14). A mistake in a closed month is corrected in the current
/// month, naming the original, and the closed month stays as it is (COR-E01, the owner with the accountant,
/// 2026-11-05): a reversal is dated today and carries its original's id and date. Reopening remains for what cannot
/// be corrected that way; it is not how a correction is made.
/// </para>
/// </summary>
public static class BookClosings
{
    /// <summary>The close or reopen in force, or null when the books have never been closed.</summary>
    public static Task<BookClosing?> CurrentAsync(PosDbContext db, CancellationToken ct) =>
        db.BookClosings.AsNoTracking().OrderByDescending(c => c.AtUtc).FirstOrDefaultAsync(ct);

    /// <summary>Every close and reopen, newest first.</summary>
    public static async Task<IReadOnlyList<BookClosing>> HistoryAsync(PosDbContext db, int take, CancellationToken ct) =>
        await db.BookClosings.AsNoTracking().OrderByDescending(c => c.AtUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);

    /// <summary>
    /// Closes the books through <paramref name="through"/>, the last day of a month that has ended on the company's
    /// clock and later than what is already closed. Months in between close with it.
    /// </summary>
    public static async Task<BookClosingResult> CloseAsync(
        PosDbContext db, DateOnly through, string? note, Guid? userId, string? userName, TimeZoneInfo zone, DateTime nowUtc,
        CancellationToken ct)
    {
        if (through != MonthEnd(through))
            return BookClosingResult.Refused("The books close through the last day of a month.");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        if (through >= new DateOnly(today.Year, today.Month, 1))
            return BookClosingResult.Refused("Only a month that has ended can be closed.");
        var current = await CurrentAsync(db, ct);
        if (current?.ClosedThrough is DateOnly closed && through <= closed)
            return BookClosingResult.Refused($"The books are already closed through {closed:yyyy-MM-dd}. To go back, reopen them.");
        if (note is { Length: > 400 }) return BookClosingResult.Refused("The note is at most 400 characters.");

        return await WriteAsync(db, new BookClosing
        {
            ClosedThrough = through, OpenFromUtc = OpenFrom(through, zone), IsReopen = false,
            Reason = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), UserId = userId, UserName = userName, AtUtc = nowUtc
        }, ct);
    }

    /// <summary>
    /// Reopens the books back to <paramref name="through"/> — an earlier month end — or opens every month when it is
    /// null. A reason is required: a reopen is how a closed month is corrected, and the trail must say why.
    /// </summary>
    public static async Task<BookClosingResult> ReopenAsync(
        PosDbContext db, DateOnly? through, string? reason, Guid? userId, string? userName, TimeZoneInfo zone, DateTime nowUtc,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 400)
            return BookClosingResult.Refused("A reason (up to 400 characters) is required to reopen the books.");
        var current = await CurrentAsync(db, ct);
        if (current?.ClosedThrough is not DateOnly closed)
            return BookClosingResult.Refused("No month is closed.");
        if (through is DateOnly to)
        {
            if (to != MonthEnd(to)) return BookClosingResult.Refused("The books close through the last day of a month.");
            if (to >= closed) return BookClosingResult.Refused($"Reopening goes back before {closed:yyyy-MM-dd}.");
        }

        return await WriteAsync(db, new BookClosing
        {
            ClosedThrough = through, OpenFromUtc = through is DateOnly t ? OpenFrom(t, zone) : null, IsReopen = true,
            Reason = reason.Trim(), UserId = userId, UserName = userName, AtUtc = nowUtc
        }, ct);
    }

    /// <summary>
    /// An entry that belongs to a month already closed, booked on the first open day instead, its text saying so and
    /// naming its document (COR-E01 with CLO-01; owner, 2026-11-05): confirming a cheque or transfer received in the
    /// closed month, approving a payroll run for it, extending a rental whose period ended in it. The closed month never
    /// changes. A draft dated in open books comes back as it is. Not for a document typed with a closed-month date: that
    /// one is still refused, so whoever typed it sees the books are closed.
    /// </summary>
    public static async Task<PostingDraft> OntoOpenBooksAsync(PosDbContext db, PostingDraft draft, CancellationToken ct)
    {
        if (await CurrentAsync(db, ct) is not { OpenFromUtc: DateTime openFrom, ClosedThrough: DateOnly closedThrough }
            || draft.EntryDate >= openFrom)
            return draft;
        var through = closedThrough.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        return draft.DatedOn(openFrom, $"{draft.Description} — belongs to the books closed through {through}: booked on the first open day");
    }

    /// <summary>Midnight after <paramref name="through"/> on the company's clock, as the UTC instant the ledger stores.</summary>
    public static DateTime OpenFrom(DateOnly through, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(through.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone);

    private static DateOnly MonthEnd(DateOnly day) => new DateOnly(day.Year, day.Month, 1).AddMonths(1).AddDays(-1);

    private static async Task<BookClosingResult> WriteAsync(PosDbContext db, BookClosing row, CancellationToken ct)
    {
        db.BookClosings.Add(row);
        await db.SaveChangesAsync(ct);
        return new BookClosingResult(row, null);
    }
}
