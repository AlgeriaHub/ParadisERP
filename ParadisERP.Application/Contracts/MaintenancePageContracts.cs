using ParadisERP.Application.Catalog;

namespace ParadisERP.Application.Contracts;

// The maintenance lists a page at a time (owner, 2026-11-17: "I need to paginate in backend, not frontend"). Each
// `…/page` route answers a PageResult<T> of the list's own rows; these are the summaries beside them — the figures and the
// filter choices computed over every row of the list, never over the page alone.

/// <summary>A branch a list holds, offered by its branch filter (several only for the Admin).</summary>
public record MaintenanceBranchOptionDto(Guid Id, string Name);

/// <summary>Jobs, Waiting for repair: the branches the list holds before any filter.</summary>
public record MaintenanceBranchesPageSummary(IReadOnlyList<MaintenanceBranchOptionDto> Branches);

/// <summary>
/// Repair history: the history's head (its tiles and totals over every job of the type or the piece, its jobs and pieces
/// left out) and the piece references the reference box offers.
/// </summary>
public record MaintenanceHistoryPageSummary(MaintenanceRepairHistoryDto History, IReadOnlyList<string> References);

/// <summary>
/// The workshop list: MS-04 in force, who may be added, each branch's head count (the empty-list warnings), the branches whose
/// lists hold someone (the branch filter), and how many rows every list holds before any filter.
/// </summary>
public record MaintenanceWorkshopPageSummary(
    string BookableEmployees, IReadOnlyList<MaintenanceWorkshopEmployeeDto> Available, IReadOnlyList<MaintenanceWorkshopBranchDto> Branches,
    IReadOnlyList<MaintenanceBranchOptionDto> MemberBranches, int Members);

/// <summary>Repair rates: MS-23 as saved, the products a rate may be set for, and how many rates exist before any filter.</summary>
public record MaintenanceRepairRatesPageSummary(string ReturnRecharge, IReadOnlyList<MaintenanceProductOptionDto> Products, int Rates);

/// <summary>
/// The practice ledger's month beside its entries: everything but the five lists (tiles, account totals, checks, postings,
/// notes), and how many rows each list below the entries holds, so a card shows only when it has some.
/// </summary>
public record MaintenanceLedgerPageSummary(
    MaintenancePracticeLedgerDto Ledger, int PostedAtIssue, int OpenCustomerJobs, int Invoices, int Provisions);

/// <summary>The month's open customer jobs: the branch codes they hold, for their branch filter.</summary>
public record MaintenanceOpenJobsPageSummary(IReadOnlyList<string> Branches);

public static class MaintenanceBranchOptions
{
    /// <summary>Each branch once, by name as the reader's language orders it.</summary>
    public static IReadOnlyList<MaintenanceBranchOptionDto> Of(IEnumerable<(Guid Id, string Name)> rows) =>
        rows.GroupBy(r => r.Id)
            .Select(g => new MaintenanceBranchOptionDto(g.Key, g.First().Name))
            .OrderBy(b => b.Name, Comparer<string>.Create((a, b) => ListText.Compare(a, b, null)))
            .ThenBy(b => b.Id)
            .ToList();
}
