namespace ParadiERP.Application.Contracts;

// ---------- Phase M5: the company profit report (docs/maintenance/plan.md §3.3; owner, 2026-11-09: company profit) ----------

/// <summary>
/// One job: its external revenue (the customer invoice's net less its credit notes, the recharge share, or none for an
/// internal job), its cost <c>C</c> (Phase M4) and the company profit = revenue − cost. <c>RevenueSource</c> is
/// <c>Invoice</c>, <c>NotInvoiced</c> (a customer job not yet invoiced: 0), <c>Recharge</c> or <c>None</c> (internal:
/// upkeep of own equipment). Labour money, the cost and the profit are null when withheld (plan §6.2).
/// </summary>
public record MaintenanceProfitRowDto(
    Guid JobId, string Number, string Kind, string Status, Guid BranchId, string BranchName,
    Guid? ProductId, string ProductSku, string ProductNameAr, string ProductNameFr, string ProductNameEn,
    string? Equipment, string? CustomerName, string Month, decimal Qty,
    string RevenueSource, decimal Revenue, string? InvoiceNumber,
    decimal Parts, decimal OutsideWork, decimal Overhead, decimal LabourHours, decimal? Labour, decimal? Cost, decimal? Profit,
    string CostStatus, bool Withheld, DateTime OpenedAtUtc,
    // Phase M7c (MS-37): a rework's own line names the job it redoes (revenue source "Rework", no revenue); under "added to
    // the original job" the original's line counts its reworks and their cost (already in Parts…Cost above; null when withheld).
    string? ReworkOfNumber = null, int Reworks = 0, decimal? ReworkCost = null);

/// <summary>
/// A breakdown's line (a month <c>yyyy-MM</c>, a branch, a kind, a product — <c>Key</c> empty for customer jobs that name
/// none): its jobs, revenue, cost and profit. The cost and profit are withheld unless every figure in them may be shown.
/// </summary>
public record MaintenanceProfitGroupDto(
    string Key, string Label, string? NameAr, string? NameFr, string? NameEn, int Jobs, decimal Revenue, decimal? Cost, decimal? Profit,
    bool Withheld);

/// <summary>Maintenance › Reports › Company profit: per job, by month, branch, kind and product, and the totals.</summary>
public record MaintenanceProfitReportDto(
    DateOnly? From, DateOnly? To, bool MoneyShown, IReadOnlyList<MaintenanceProfitRowDto> Rows,
    IReadOnlyList<MaintenanceProfitGroupDto> ByMonth, IReadOnlyList<MaintenanceProfitGroupDto> ByBranch,
    IReadOnlyList<MaintenanceProfitGroupDto> ByKind, IReadOnlyList<MaintenanceProfitGroupDto> ByProduct,
    decimal Revenue, decimal? Cost, decimal? Profit, bool TotalsWithheld,
    // Phase M7c: MS-37 as saved — how reworks under guarantee are reported.
    string Rework = "AddedToOriginalJob");
