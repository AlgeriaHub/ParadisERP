using ParadiERP.Application.Contracts;
using ParadiERP.Application.Maintenance;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;

namespace ParadiERP.Infrastructure.Maintenance;

/// <summary>
/// Phase M6 (docs/maintenance/plan.md §5.3, MS-39): the accountant posts a closed month of maintenance to the ledger from its
/// practice ledger, the way production's months post (Phase D) — through the same month poster
/// (<see cref="LedgerMonthPoster"/>), under source <c>maintenance</c>, its postings kept in <c>MaintenanceMonthPostings</c>.
/// <para>
/// A month posts when the first month to post is chosen (MS-39), the month is on or after it, the caller reads every branch,
/// it has ended, every check passes (every slot it uses names an account), it is not posted already and the month before is
/// (unless it is the first). The first month leaves out the reversal on the 1st of work in progress that never posted. Closed
/// books refuse it. Reversing takes the last month posted back with mirror entries, never deletes, and the month may post
/// again under its next generation. With labour in the entries at payroll rates, posting needs pay-rate access, as reading.
/// </para>
/// </summary>
public sealed class MaintenanceLedgerPostings(
    PosDbContext db, MaintenancePracticeLedgerReport ledger, LedgerPoster poster, IRegionalSettingsProvider regional)
{
    public const string SourceType = "maintenance";

    /// <param name="companyScoped">The caller reads every branch (the company's roles): a month is posted whole.</param>
    public async Task<MaintenanceResult<MaintenancePracticeLedgerDto>> PostAsync(
        int year, int month, Func<string, bool> has, bool companyScoped, string? by, CancellationToken ct = default)
    {
        if (!has(PermissionCatalog.AccountingManage))
            return Fail(403, "forbidden", "Posting maintenance to the ledger needs accounting.manage.");
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        if (settings.PostingFromMonth is not DateOnly from)
            return Fail(409, "posting_off", "Choose the first month to post in Maintenance › Settings first (MS-39).");
        if (year is < 2000 or > 2100 || month is < 1 or > 12) return Fail(400, "invalid_period", "Choose a month.");
        if (new DateOnly(year, month, 1) < from)
            return Fail(409, "before_posting_start", "This month is before the first month to post.");
        if (!companyScoped)
            return Fail(403, "post_needs_every_branch", "A month is posted whole: it needs a reader of every branch.");

        var read = await ledger.MonthAsync(year, month, null, has, ct);
        if (read.Value is not MaintenancePracticeLedgerDto practice) return read;
        var entries = practice.Entries.Select(e => new MonthPostingEntry(
            e.Day, e.BranchId,
            string.Join(' ', new[] { e.Kind, e.OrderNumber, e.Document }.Where(x => !string.IsNullOrEmpty(x))),
            e.Lines.Select(l => new PostingLine(l.AccountCode!, l.Debit, l.Credit)).ToList(),
            SkipWhenOpening: e.Kind == MaintenancePracticeLedger.KindWipReversal)).ToList();
        var refused = await LedgerMonthPoster.PostAsync<MaintenanceMonthPosting>(db, poster, regional, SourceType, year, month, from,
            practice.MonthToDate, practice.Checks.All(c => c.Passed), entries, by, ct);
        if (refused is not null) return Fail(refused.Status, refused.Code, refused.Message);
        return await ledger.MonthAsync(year, month, null, has, ct);
    }

    /// <summary>Takes a month's posting back with reversal entries; only the last month posted, so the months stay in order.</summary>
    public async Task<MaintenanceResult<MaintenancePracticeLedgerDto>> ReverseAsync(
        int year, int month, Func<string, bool> has, bool companyScoped, string? by, CancellationToken ct = default)
    {
        if (!has(PermissionCatalog.AccountingManage))
            return Fail(403, "forbidden", "Reversing a posting needs accounting.manage.");
        if (!companyScoped)
            return Fail(403, "post_needs_every_branch", "A month is reversed whole: it needs a reader of every branch.");
        var refused = await LedgerMonthPoster.ReverseAsync<MaintenanceMonthPosting>(db, poster, SourceType, year, month,
            $"Maintenance {year:0000}-{month:00} reversed", by, ct);
        if (refused is not null) return Fail(refused.Status, refused.Code, refused.Message);
        return await ledger.MonthAsync(year, month, null, has, ct);
    }

    /// <summary>A practice entry's source id in the ledger: the same month, posting and place give the same id.</summary>
    public static Guid EntryId(int year, int month, int generation, int index) =>
        LedgerMonthPoster.EntryId(SourceType, year, month, generation, index);

    private static MaintenanceResult<MaintenancePracticeLedgerDto> Fail(int status, string code, string error) =>
        MaintenanceResult<MaintenancePracticeLedgerDto>.Fail(status, code, error);
}
