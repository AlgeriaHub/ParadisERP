namespace AlgeriaPos.Application.Contracts;

// Manufacturing costing, Phase A (docs/manufacturing/costing-plan.md): computed on request, read-only,
// never frozen or posted. Money is rounded to three decimals and unit costs to six.
//
// Labour privacy, complete (owner, 2026-11-03; CostPrivacy): for a reader without both payroll.manage and
// manufacturing.costs.rates.view, every figure of one order that holds labour, or gives it back by subtraction,
// is null and the answer says so (OrderMoneyWithheld): labour, total and unit cost, released value, work in progress,
// what stays in charges, spoilage, the labour variance and the variance total. Its material lines stay.

/// <summary>The cost by element. <c>Labour</c> and <c>Total</c> are null where the order's labour money is withheld.</summary>
public record CostElementsDto(
    decimal Materials, decimal Consumables, decimal Accessories, decimal? Labour, decimal Overhead, decimal Expenses,
    decimal Recovery, decimal? Total);

public record CostMaterialLineDto(
    Guid Id, string Sku, string Role, string BaseUnitCode,
    decimal RequiredBaseQty, decimal EstimatedUnitCost, decimal EstimatedValue,
    decimal NetBaseQty, decimal NetValue, decimal IssueAverage,
    decimal ConsumedValue, decimal ScrapValue, decimal AtLineBaseQty, decimal AtLineValue,
    decimal? StandardBaseQty, decimal? UsageVariance, decimal? PriceVariance);

/// <summary>
/// One employee's payroll month on the order. <c>Rate</c> and <c>Cost</c> are null for a caller without
/// payroll.manage. <c>Hours</c> is null for such a caller when one employee worked the order, since the
/// order's labour over the hours would be that person's rate. <c>WithoutEmployerSocial</c>: the payroll it
/// comes from carries no employer social contribution, so the cost leaves it out (D11) — said, never shown
/// as part of the figure.
/// </summary>
/// <summary>
/// One employee-month on the order. <c>Rate</c> is what a normal hour costs; <c>OvertimeHours</c> of the hours were marked
/// overtime and cost <c>OvertimeRate</c> — the same rate under the blended choice (C-05), the normal rate plus the month's
/// overtime premium per hour under "premium to overtime hours". Rates and costs need pay-rate access; the overtime hours
/// are withheld with the hours.
/// </summary>
public record CostLabourLineDto(
    Guid EmployeeId, string EmployeeNumber, string EmployeeName, int Year, int Month, decimal? Hours,
    string Basis, string? Reason, decimal? Rate, decimal? Cost, bool WithoutEmployerSocial,
    decimal? OvertimeHours = 0m, decimal? OvertimeRate = null);

/// <summary>
/// What each actual element rests on: approved, recorded, none — or reference_cost, provisional,
/// incomplete, not_set, not_recorded, any of which keeps the sheet provisional (ProductionCostEngine).
/// </summary>
public record CostElementStatusesDto(
    string Materials, string Consumables, string Accessories, string Labour, string Overhead, string Expenses, string Recovery);

/// <summary>An output release. <c>Value</c> and <c>UnitValue</c> are null where the order's labour money is withheld.</summary>
public record CostReleaseDto(Guid Id, string Number, decimal BaseQty, decimal? Value, decimal? UnitValue, bool CarriesRemainder);

/// <summary>Creates the single immutable B1 cost sheet for an order. Reconstruction is an explicit, labelled historical exercise.</summary>
public record CreateManufacturingCostSnapshotRequest(string RequestId, bool Reconstruct = false);

/// <summary>Records a separately auditable adjustment after a sheet was frozen; it never rewrites that sheet.</summary>
public record CreateManufacturingLateCostRequest(string Kind, string SourceReference, decimal Amount, string Reason);

/// <summary>
/// The order's frozen sheet. Where the order's labour money is withheld (<c>OrderMoneyWithheld</c>), its total and unit
/// cost, its release values and its late costs' amounts are null: each gives the labour back by subtraction.
/// </summary>
public record ManufacturingCostSnapshotDto(
    Guid Id, Guid OrderId, Guid LineId, string Kind, string Status, string CalculationVersion, string RequestId,
    string SourceHash, string SourceState, decimal AcceptedBaseQty, decimal RejectedBaseQty, decimal? TotalCost, decimal? UnitCost,
    string? ProvisionalReasons, DateTime CreatedAtUtc, bool Reconstructed,
    IReadOnlyList<CostReleaseDto> Releases, IReadOnlyList<ManufacturingLateCostDto> LateCosts, bool OrderMoneyWithheld = false);

public record ManufacturingLateCostDto(Guid Id, string Kind, string SourceReference, decimal? Amount, string Reason, DateTime RecordedAtUtc);

/// <summary>
/// The variances against the estimate scaled to the accepted output. <c>Labour</c> is efficiency and rate
/// together. A caller without both rate keys gets neither the split, nor <c>Labour</c>, nor <c>Total</c>.
/// </summary>
public record CostVariancesDto(
    decimal MaterialUsage, decimal MaterialPrice, decimal? LabourEfficiency, decimal? LabourRate, decimal? Labour,
    decimal Overhead, decimal Expenses, decimal Recovery, decimal? Total);

/// <summary>
/// The estimate by element. <c>Labour</c> and <c>Total</c> are null for a caller without both rate keys: over the
/// standard hours they would be the crew's rate.
/// </summary>
public record CostEstimateDto(
    decimal Materials, decimal Consumables, decimal Accessories, decimal? Labour, decimal Overhead, decimal Expenses,
    decimal Recovery, decimal? Total);

public record CostNoteDto(string Code, decimal? Value);

/// <summary>
/// The order's cost sheet as the engine computes it now. <c>CostStatus</c> is <c>approved</c> only when every
/// element rests on approved inputs, else <c>provisional</c>; the estimate is always just that. <c>Completed</c>
/// says the order is closed; its cost is still recomputed on every read until Phase B freezes it (note
/// <c>cost_not_frozen</c>), so a completed order is never shown a final cost while an input is provisional.
/// <c>PerEmployeeMoneyShown</c> is false when the caller does not hold both payroll.manage and
/// manufacturing.costs.rates.view. For that caller the order's labour money and all that gives it back are null
/// (<c>OrderMoneyWithheld</c>), the labour estimate and estimated total too (<c>LabourEstimateWithheld</c>), and the
/// hours when one employee worked the order (<c>LabourHoursWithheld</c>).
/// </summary>
public record ProductionCostSheetDto(
    Guid OrderId, string OrderNumber, string Status, string BaseUnitCode,
    decimal PlannedBaseQty, decimal AcceptedBaseQty, decimal RejectedBaseQty, bool Completed,
    CostEstimateDto Estimate, decimal? EstimatedUnitCost,
    CostElementsDto Actual, decimal? ActualUnitCost,
    decimal AtLineValue, decimal? ReleasedValue, decimal? WorkInProgress, decimal? KeptInCharges,
    decimal? LabourHours, decimal StandardHours, decimal StandardHoursForOutput, decimal? StandardRate,
    decimal OverheadPerLabourHour, bool PerEmployeeMoneyShown, bool LabourHoursWithheld, bool LabourEstimateWithheld,
    IReadOnlyList<CostMaterialLineDto> Materials, IReadOnlyList<CostLabourLineDto> Labour, IReadOnlyList<CostReleaseDto> Releases,
    CostVariancesDto? Variances, decimal? Spoilage, IReadOnlyList<CostNoteDto> Notes,
    string CostStatus, CostElementStatusesDto Statuses, bool EmployerSocialMissing, bool OrderMoneyWithheld = false);

/// <summary>A live order. Its cost to date, released value and work in progress are null where its labour money is withheld.</summary>
public record WorkInProgressRowDto(
    Guid OrderId, string OrderNumber, string Status, Guid LineId, string LineCode, string ProductSku, string BaseUnitCode,
    decimal PlannedBaseQty, decimal AcceptedBaseQty, decimal? CostToDate, decimal? ReleasedValue, decimal AtLineValue,
    decimal? WorkInProgress, bool Provisional);

/// <summary>
/// A line's live orders added up. <c>Withheld</c>: for a reader without rate access, the sums of a line with fewer than
/// two orders or two workers are left out, since they would be one order's, or one person's.
/// </summary>
public record WorkInProgressLineDto(
    Guid LineId, string LineCode, int Orders, int Workers, decimal? CostToDate, decimal? ReleasedValue, decimal AtLineValue,
    decimal? WorkInProgress, bool Withheld);

/// <summary>
/// Every live order and each line's sum. <c>MoneyShown</c> is false for a reader without rate access: the orders' own
/// figures are then null, the lines carry the sums they may, and the report's totals are left out when any line's are.
/// </summary>
public record WorkInProgressReportDto(
    IReadOnlyList<WorkInProgressRowDto> Rows, decimal? CostToDate, decimal? ReleasedValue, decimal AtLineValue, decimal? WorkInProgress,
    IReadOnlyList<WorkInProgressLineDto>? Lines = null, bool MoneyShown = true);

/// <summary>
/// One employee's payroll month: the hours booked on orders and on other rows of the daily records, and —
/// for a caller holding payroll.manage and manufacturing.costs.rates.view — the hours payroll paid, what
/// payroll cost and how much of it the orders absorbed.
/// </summary>
/// <summary>
/// One employee's payroll month. C-05 (2026-11-09): <c>OvertimeHours</c>, the order hours marked overtime (shown with the
/// hours); with pay-rate access, <c>PaidOvertimeHours</c> and <c>OvertimePremium</c> (the employer cost of the premium above
/// the normal rate, from the payslips' overtime lines), and — under "premium to overtime hours" only — <c>PremiumInCharges</c>,
/// the part of that premium no order hour carried, which stays in the wage charges.
/// </summary>
public record LabourAbsorptionRowDto(
    Guid EmployeeId, string EmployeeNumber, string EmployeeName, decimal OrderHours, decimal OtherHours,
    string Basis, string? Reason, decimal? PaidHours, decimal? EmployerCost, decimal? Rate, decimal? AbsorbedCost, decimal? UnabsorbedCost,
    bool WithoutEmployerSocial, decimal OvertimeHours = 0m, decimal? PaidOvertimeHours = null, decimal? OvertimePremium = null,
    decimal? PremiumInCharges = null);

/// <summary>
/// <c>EmployerSocialMissing</c>: at least one row's payroll carries no employer social contribution, so its
/// employer cost and rate leave that charge out (D11).
/// </summary>
public record LabourAbsorptionReportDto(
    int Year, int Month, bool MoneyShown, IReadOnlyList<LabourAbsorptionRowDto> Rows,
    decimal OrderHours, decimal OtherHours, decimal? EmployerCost, decimal? AbsorbedCost, decimal? UnabsorbedCost,
    bool EmployerSocialMissing, IReadOnlyList<LabourLineMonthDto>? Lines = null,
    string LabourAbsorption = "BlendedActualRate", decimal? PremiumInCharges = null);

/// <summary>
/// One line's labour in the payroll month: the orders and workers booked, their order hours, and the labour the orders
/// absorbed at the month's rates. <c>Withheld</c>: for a reader without rate access, a line with fewer than two orders
/// or two workers shows no labour cost, since it would be one order's, or one person's.
/// </summary>
public record LabourLineMonthDto(Guid LineId, string LineCode, int Orders, int Workers, decimal OrderHours, decimal? AbsorbedCost, bool Withheld);

/// <summary>A discrepancy between manufacturing documents and stock. <c>Kind</c> names the check.</summary>
public record MaterialFlowIssueDto(
    string Kind, string Reference, Guid? OrderId, string? OrderNumber, Guid? ProductId, string? Sku,
    Guid? LocationId, string? LocationCode, decimal? Expected, decimal? Actual);

public record MaterialFlowReportDto(
    int MovementsChecked, int MaterialsChecked, int StocksChecked, IReadOnlyList<MaterialFlowIssueDto> Issues);
