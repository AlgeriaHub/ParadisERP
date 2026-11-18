namespace ParadisERP.Application.Contracts;

// ---------- Phase M4: job cost and privacy (docs/maintenance/plan.md §3.1, §6.2) ----------

/// <summary>What each element rests on, in the cost engine's words, or <c>excluded</c> when MS-10 leaves it out.</summary>
public record MaintenanceCostStatusesDto(string Parts, string Labour, string Overhead, string OutsideWork);

/// <summary>One product's parts in the cost: what the job kept (issued less returned) at the values frozen on its movements.</summary>
public record MaintenanceCostPartDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, decimal NetQty, decimal UnitCost, decimal Value);

/// <summary>
/// One employee-month of hours in the cost. <c>RateKind</c> is <c>Actual</c> (payroll approved), <c>Standard</c> (not yet),
/// <c>None</c> (no rate) or <c>Workshop</c> (MS-12). <c>Rate</c> and <c>Cost</c> are null for a reader who may not see them.
/// </summary>
public record MaintenanceCostLabourDto(
    Guid EmployeeId, string EmployeeNumber, string EmployeeName, int Year, int Month, decimal Hours, decimal OvertimeHours,
    string RateKind, string? Reason, decimal? Rate, decimal? Cost, bool WithoutEmployerSocial);

/// <summary>Outside work in the cost: as recorded, as counted (before recoverable VAT under C-07/MS-13), and whether its voucher backs it.</summary>
public record MaintenanceCostOutsideDto(Guid Id, string Description, decimal Recorded, decimal Counted, bool Settled, string? VoucherNumber);

/// <summary>
/// A job's cost (Phase M4): C = parts + labour + outside work + overhead, computed on read through the manufacturing cost
/// engine, never stored. <c>Status</c> is <c>approved</c> (final) or <c>provisional</c>. Without pay-rate access
/// (<c>LabourMoneyShown</c> false) the labour money, the total and the recharge less cost are null, and so are each labour
/// line's rate and cost; hours, parts, outside work and overhead stay. Under MS-12 "one workshop rate", or with labour out
/// of the cost (MS-10), nothing gives a pay rate back and everything is shown.
/// </summary>
public record MaintenanceJobCostDto(
    Guid JobId, string Number, string Kind, string JobStatus, string Status, bool PolicyApproved, bool LabourMoneyShown,
    string CostIncludes, string LabourRate, string BurdenAndVat,
    decimal OverheadPerLabourHour, decimal? WorkshopRatePerHour, decimal EmployerBurdenPercent, decimal ExpenseVatPercent,
    decimal Parts, decimal? Labour, decimal Overhead, decimal OutsideWork, decimal? Total, decimal LabourHours,
    decimal? Recharge, decimal? RechargeLessCost,
    MaintenanceCostStatusesDto Statuses,
    IReadOnlyList<MaintenanceCostPartDto> PartLines,
    IReadOnlyList<MaintenanceCostLabourDto> LabourLines,
    IReadOnlyList<MaintenanceCostOutsideDto> OutsideWorkLines,
    IReadOnlyList<CostNoteDto> Notes,
    // Phase M7d (MS-30): the repair capitalised into the equipment's register at completion: when, and how much (null when
    // kept an expense, or withheld with the total).
    DateTime? CapitalisedAtUtc = null, decimal? Capitalised = null);

/// <summary>
/// One recharged job against its recharge (REN-11): the return-note line's recharge typed at return, the job's share of it
/// (MS-26), and the job's cost. <c>Labour</c>, <c>Total</c> and <c>Difference</c> are null when withheld (§6.2).
/// </summary>
public record MaintenanceRechargeRowDto(
    Guid JobId, string Number, string Status, Guid BranchId, string BranchName,
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, decimal Qty,
    string? ContractNumber, string? ReturnNoteNumber, string? CustomerName, decimal LineRecharge, decimal Recharge,
    decimal Parts, decimal? Labour, decimal Overhead, decimal OutsideWork, decimal? Total, decimal? Difference,
    decimal LabourHours, string CostStatus, bool Withheld, DateTime CreatedAtUtc);

/// <summary>
/// Maintenance › Reports, recharge against cost: every recharged job of the period (opened from <c>From</c> to <c>To</c>,
/// company days) at the branches the caller reaches. The totals' cost figures are null while withheld: without pay-rate
/// access they are shown only from two jobs with labour and two workers.
/// </summary>
public record MaintenanceRechargeReportDto(
    DateOnly? From, DateOnly? To, string RechargeSharing, bool LabourMoneyShown,
    IReadOnlyList<MaintenanceRechargeRowDto> Rows,
    decimal Recharge, decimal Parts, decimal Overhead, decimal OutsideWork, decimal LabourHours,
    decimal? Labour, decimal? Total, decimal? Difference, bool TotalsWithheld);

/// <summary>
/// One equipment type's maintenance cost over the period, all its jobs together (drafts left out; a cancelled job keeps what
/// it used). The labour and total are null when withheld: without pay-rate access a product's sum shows from two jobs with
/// labour and two workers. <c>Provisional</c>: at least one of its jobs' costs is.
/// </summary>
public record MaintenanceProductCostRowDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn,
    int Jobs, decimal Units, decimal RepairedQty, decimal ScrappedQty,
    decimal Parts, decimal Overhead, decimal OutsideWork, decimal LabourHours, decimal? Labour, decimal? Total,
    decimal Recharge, int Workers, bool Withheld, bool Provisional,
    // Phase M7d (MS-30): of the cost, what was capitalised into the register at completion, and what stays an expense (null when
    // the total is withheld).
    decimal? Capitalised = null, decimal? Expensed = null);

/// <summary>Maintenance › Reports, cumulative cost per product. Totals' labour and total only when no product's are withheld.</summary>
public record MaintenanceProductCostReportDto(
    DateOnly? From, DateOnly? To, bool LabourMoneyShown, IReadOnlyList<MaintenanceProductCostRowDto> Rows,
    int Jobs, decimal Parts, decimal Overhead, decimal OutsideWork, decimal LabourHours, decimal Recharge,
    decimal? Labour, decimal? Total, bool TotalsWithheld,
    // Phase M7d (MS-30): capitalised vs expensed, beside the total and under the same rule.
    decimal? Capitalised = null, decimal? Expensed = null);
