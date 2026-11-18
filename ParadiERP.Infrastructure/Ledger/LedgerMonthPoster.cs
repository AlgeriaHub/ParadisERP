using System.Security.Cryptography;
using System.Text;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Ledger;

/// <summary>One practice entry ready to post: its company day, branch, words and lines at the accounts the map names.</summary>
/// <param name="SkipWhenOpening">
/// A reversal on the 1st of a month-end value carried from the month before: the first month posted leaves it out, since
/// the value it takes back never went to the ledger.
/// </param>
public sealed record MonthPostingEntry(DateOnly Day, Guid BranchId, string Description, IReadOnlyList<PostingLine> Lines, bool SkipWhenOpening);

/// <summary>Why a month did not post or reverse: the status, the code and the sentence the caller answers with.</summary>
public sealed record MonthPostingRefusal(int Status, string Code, string Message);

/// <summary>
/// The month poster shared by manufacturing (Phase D) and maintenance (Phase M6, docs/maintenance/plan.md §5.3): a closed,
/// checked month of practice entries becomes ledger entries through <see cref="LedgerPoster"/>, one per practice entry,
/// under the module's source and a source id drawn from the month, the posting's generation and the entry's place; a
/// posting is taken back by mirror entries, never deleted, and the month may then post again under its next generation.
/// <para>
/// What each module checks first (its permission, its first month chosen, the month asked, that the caller reads every line
/// or branch) and how it reads the practice month stay its own. This is the rest of the sequence, in manufacturing's order:
/// the month has ended, every check passes, it is not posted already, the month before is posted (unless it is the first),
/// then the entries in one transaction, closed books refusing it. Each module keeps its own postings table
/// (<typeparamref name="TPosting"/>); the codes and sentences are the ones manufacturing has always answered with.
/// </para>
/// </summary>
public static class LedgerMonthPoster
{
    /// <summary>Posts the month, or says why not. <paramref name="from"/> is the first month the accountant chose.</summary>
    public static async Task<MonthPostingRefusal?> PostAsync<TPosting>(
        PosDbContext db, LedgerPoster poster, IRegionalSettingsProvider regional, string source,
        int year, int month, DateOnly from, bool monthToDate, bool checksPassed, IReadOnlyList<MonthPostingEntry> entries,
        string? by, CancellationToken ct)
        where TPosting : class, ILedgerMonthPosting, new()
    {
        if (monthToDate)
            return new(409, "month_not_ended", "A month is posted once it has ended.");
        if (!checksPassed)
            return new(409, "checks_not_passed", "Every check of the month passes before it is posted: map each account it uses.");

        var postings = await db.Set<TPosting>().AsNoTracking().Where(p => p.Year == year && p.Month == month).ToListAsync(ct);
        if (postings.Any(p => p.ReversedAtUtc == null))
            return new(409, "already_posted", "This month is posted; reverse it first to post it again.");
        var first = new DateOnly(year, month, 1);
        if (first > from)
        {
            var before = first.AddMonths(-1);
            if (!await db.Set<TPosting>().AnyAsync(p => p.Year == before.Year && p.Month == before.Month && p.ReversedAtUtc == null, ct))
                return new(409, "previous_month_not_posted", "Post the month before first.");
        }

        var generation = postings.Count + 1;
        var opening = first == from;
        var zone = StoreDocumentHistory.Zone(regional);
        var count = 0;
        var total = 0m;
        try
        {
            await using var tx = await db.BeginWriteScopeAsync(ct);
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                // The first month posted does not take back a month-end value that never went to the ledger.
                if (opening && e.SkipWhenOpening) continue;
                var draft = new PostingDraft
                {
                    EntryDate = Noon(e.Day, zone),
                    BranchId = e.BranchId,
                    SourceType = source,
                    SourceId = EntryId(source, year, month, generation, i),
                    Description = e.Description,
                    CreatedBy = by,
                };
                foreach (var l in e.Lines) draft.Add(l);
                if (await poster.PostAsync(draft, ct) is null) continue;
                count++;
                total += e.Lines.Sum(l => l.Debit);
            }
            db.Set<TPosting>().Add(new TPosting
            {
                Year = year, Month = month, Generation = generation, Entries = count, Indexes = entries.Count,
                TotalDebit = total, PostedAtUtc = DateTime.UtcNow, PostedBy = by,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (PeriodClosedException closed)
        {
            db.ChangeTracker.Clear();
            return new(409, "books_closed", closed.Message);
        }
        return null;
    }

    /// <summary>Takes a month's posting back with reversal entries; only the last month posted, so the months stay in order.</summary>
    public static async Task<MonthPostingRefusal?> ReverseAsync<TPosting>(
        PosDbContext db, LedgerPoster poster, string source, int year, int month, string reason, string? by, CancellationToken ct)
        where TPosting : class, ILedgerMonthPosting
    {
        var live = await db.Set<TPosting>().Where(p => p.ReversedAtUtc == null).ToListAsync(ct);
        var posting = live.FirstOrDefault(p => p.Year == year && p.Month == month);
        if (posting is null) return new(409, "not_posted", "This month is not posted.");
        if (live.Any(p => p.Year * 12 + p.Month > year * 12 + month))
            return new(409, "later_month_posted", "Reverse the later months first.");

        var ids = Enumerable.Range(0, posting.Indexes).Select(i => EntryId(source, year, month, posting.Generation, i)).ToList();
        try
        {
            await using var tx = await db.BeginWriteScopeAsync(ct);
            var entries = await db.LedgerEntries.Include(e => e.Lines)
                .Where(e => e.SourceType == source && ids.Contains(e.SourceId) && !e.IsReversal).ToListAsync(ct);
            foreach (var entry in entries)
                await poster.ReverseAsync(entry, reason, by, ct);
            posting.ReversedAtUtc = DateTime.UtcNow;
            posting.ReversedBy = by;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (PeriodClosedException closed)
        {
            db.ChangeTracker.Clear();
            return new(409, "books_closed", closed.Message);
        }
        return null;
    }

    /// <summary>A practice entry's source id in the ledger: the same source, month, posting and place give the same id.</summary>
    public static Guid EntryId(string source, int year, int month, int generation, int index) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{source}/{year:0000}-{month:00}/{generation}/{index}")));

    /// <summary>Midday of the company's day, so the entry falls on that day whatever the zone.</summary>
    public static DateTime Noon(DateOnly day, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(12, 0)), zone);
}
