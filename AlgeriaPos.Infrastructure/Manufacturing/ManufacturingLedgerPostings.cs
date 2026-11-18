using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Manufacturing;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Manufacturing;

/// <summary>
/// Phase D: the accountant posts a month of production to the ledger from the practice ledger, in the panel (owner,
/// 2026-11-09: "make everything as settings and accountant set it in cpanel"). What posts is exactly the month the
/// practice ledger shows, through <see cref="LedgerPoster"/>, one ledger entry per practice entry, at the accounts of the
/// account map. Nothing posts by itself: the accountant chooses the first month in Costing settings and posts each
/// closed month in turn; a month is undone by reversal entries, never by deleting, and may then be posted again.
///
/// <para>
/// A month posts only when it has ended, it is on or after the chosen first month, the month before it is posted
/// (unless it is the first), every check of the practice ledger passes — every slot it uses names an account — and the
/// poster sees every production line. The first month leaves out the reversal of the month before, which was never
/// posted. Closed books refuse it, as they refuse everything.
/// </para>
/// </summary>
public sealed class ManufacturingLedgerPostings(
    PosDbContext db, PracticeLedgerReport ledger, LedgerPoster poster, IRegionalSettingsProvider regional)
{
    public const string SourceType = "manufacturing";

    public async Task<MfgResult<PracticeLedgerDto>> PostAsync(MfgCaller caller, int year, int month, CancellationToken ct)
    {
        if (!caller.Has(PermissionCatalog.AccountingManage))
            return MfgFail.Forbidden<PracticeLedgerDto>("Posting production to the ledger needs accounting.manage.");
        var settings = await db.ManufacturingCostingSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings?.PostingFromMonth is not DateOnly from)
            return MfgFail.Conflict<PracticeLedgerDto>("posting_off", "Choose the first month to post in Costing settings first.");
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
            return MfgFail.Invalid<PracticeLedgerDto>("invalid_period", "Choose a month.");
        var first = new DateOnly(year, month, 1);
        if (first < from)
            return MfgFail.Conflict<PracticeLedgerDto>("before_posting_start", "This month is before the first month to post.");
        if (await ManufacturingAccess.Reachable(db, caller).CountAsync(ct) < await db.ProductionLines.CountAsync(ct))
            return MfgFail.Forbidden<PracticeLedgerDto>("A month is posted whole: it needs a reader of every production line.", "post_needs_every_line");

        var read = await ledger.MonthAsync(caller, year, month, null, ct);
        if (read.Value is not PracticeLedgerDto practice) return read;
        // The rest of the sequence is the month poster shared with maintenance (docs/maintenance/plan.md §5.3), unchanged.
        var entries = practice.Entries.Select(e => new MonthPostingEntry(
            e.Day, e.BranchId,
            string.Join(' ', new[] { e.Kind, e.OrderNumber, e.Document }.Where(x => !string.IsNullOrEmpty(x))),
            e.Lines.Select(l => new PostingLine(l.AccountCode!, l.Debit, l.Credit)).ToList(),
            SkipWhenOpening: e.Kind == PracticeLedger.KindWipReversal)).ToList();
        var refused = await LedgerMonthPoster.PostAsync<ManufacturingMonthPosting>(db, poster, regional, SourceType, year, month, from,
            practice.MonthToDate, practice.Checks.All(c => c.Passed), entries, caller.Name, ct);
        if (refused is not null) return MfgFail.Conflict<PracticeLedgerDto>(refused.Code, refused.Message);
        return await ledger.MonthAsync(caller, year, month, null, ct);
    }

    /// <summary>Takes a month's posting back with reversal entries; only the last month posted, so the months stay in order.</summary>
    public async Task<MfgResult<PracticeLedgerDto>> ReverseAsync(MfgCaller caller, int year, int month, CancellationToken ct)
    {
        if (!caller.Has(PermissionCatalog.AccountingManage))
            return MfgFail.Forbidden<PracticeLedgerDto>("Reversing a posting needs accounting.manage.");
        var refused = await LedgerMonthPoster.ReverseAsync<ManufacturingMonthPosting>(db, poster, SourceType, year, month,
            $"Production {year:0000}-{month:00} reversed", caller.Name, ct);
        if (refused is not null) return MfgFail.Conflict<PracticeLedgerDto>(refused.Code, refused.Message);
        return await ledger.MonthAsync(caller, year, month, null, ct);
    }

    /// <summary>A practice entry's source id in the ledger: the same month, posting and place give the same id.</summary>
    public static Guid EntryId(int year, int month, int generation, int index) =>
        LedgerMonthPoster.EntryId(SourceType, year, month, generation, index);
}
