namespace ParadisERP.Application.Contracts;

// Manufacturing costing B2 (docs/manufacturing/costing-b2.md): read-only reconciliation of B1's frozen sheets.
// Nothing here is financially final: FinanciallyFinalized is always null and FinalizationState "not_implemented".
// Money is at three decimals, unit values at six. Times are record times (UTC); no accounting effective date exists.

/// <summary>
/// The four figures, kept apart. <c>LateRecorded</c> is every late amount as entered up to the chosen day;
/// <c>LateRecognized</c> the part whose kind names a cost element and whose source is not in doubt — the only part
/// in <c>AdjustedProvisional</c> = <c>Original</c> + <c>LateRecognized</c>. <c>LateUnclassified</c> (kind names no
/// element) and <c>LateUncertain</c> (duplicated or already-frozen source) stay outside it.
/// </summary>
public record CostFiguresDto(
    decimal Original, decimal LateRecorded, decimal LateRecognized, decimal LateUnclassified, decimal LateUncertain,
    decimal AdjustedProvisional, decimal? FinanciallyFinalized, string FinalizationState, decimal? OriginalUnitCost);

/// <summary>A sheet's identity and provenance. <c>FrozenAtUtc</c> is when the record was made; <c>CompletedAtUtc</c> when the order was completed.</summary>
public record CostSheetHeaderDto(
    Guid SheetId, string Kind, string SourceState, string CalculationVersion, string Status,
    DateTime FrozenAtUtc, string? FrozenBy, DateTime? CompletedAtUtc, int? FreezeLagDays, string SourceHash);

/// <summary>An element's signed contribution to the cost (recovery negative): frozen, recognized late effect, adjusted.</summary>
public record CostElementReconciliationDto(string Element, string Status, decimal Original, decimal LateRecognized, decimal Adjusted);

/// <summary>
/// A late-cost record. <c>RecordedAmount</c> is what was entered; exactly one of the three effects carries it, by
/// <c>Treatment</c>. <c>SourceReference</c> and <c>Reason</c> are null (<c>Redacted</c>) for a labour or
/// unclassified record when the caller may not see payroll rates. <c>RecordedAtUtc</c> is the record's creation
/// time, not an accounting date.
/// </summary>
public record LateCostRowDto(
    Guid Id, Guid OrderId, string OrderNumber, string LineCode, string Kind, string? Element, string Treatment,
    decimal RecordedAmount, decimal RecognizedEffect, decimal UnclassifiedEffect, decimal UncertainEffect,
    string? SourceReference, string? Reason, bool Redacted, DateTime RecordedAtUtc, IReadOnlyList<string> Flags);

/// <summary>An integrity diagnostic: <c>pass</c>, <c>fail</c> or <c>not_applicable</c>. Nothing is ever repaired.</summary>
public record CostCheckDto(string Code, string Result, decimal? Expected, decimal? Actual, string? Detail);

/// <summary>Something a sheet lacks or rests on. <c>Severity</c>: missing, integrity, incomplete, provisional, review, info.</summary>
public record CostGapDto(string Code, string Severity, string? Element, decimal? Value, string? Detail);

/// <summary>A release as B1 froze it. <c>CatalogueReferenceValue</c>: quantity × the catalogue cost the release document recorded — reference only.</summary>
public record ReleaseReconciliationRowDto(
    Guid ReleaseId, string Number, DateTime AtUtc, decimal Quantity, decimal Value, decimal UnitValue, bool CarriesRemainder,
    decimal? CatalogueReferenceValue);

/// <summary>
/// B1's release rule: the last release carries the remainder, so the values add up to the original cost; with no
/// release the cost is kept in the charges. <c>LateNotAllocated</c>: recognized late effects, which no approved
/// policy assigns to a release.
/// </summary>
public record ReleaseReconciliationDto(
    IReadOnlyList<ReleaseReconciliationRowDto> Rows, decimal AcceptedQty, decimal ReleasedQty, decimal UnreleasedAcceptedQty,
    decimal ReleasedValue, decimal KeptInCharges, decimal LateNotAllocated, IReadOnlyList<string> Flags);

/// <summary>Rejected output: spoilage is absorbed in the cost (part of it, never added). <c>Unavailable</c> says why replayed figures are missing.</summary>
public record ScrapCostDto(
    decimal AcceptedQty, decimal RejectedQty, decimal? RejectionRatePercent, decimal? MaterialScrapValue,
    decimal RecoveryValue, string RecoveryStatus, decimal? SpoilageAbsorbed, decimal? MaterialWastage, string? Unavailable);

public record VarianceLineDto(string Component, bool Detail, decimal? Original, decimal? LateRecognized, decimal? Adjusted);

public record CostVarianceDto(bool Available, string? Reason, IReadOnlyList<VarianceLineDto> Lines);

/// <summary>
/// The frozen labour. <c>Rate</c> and <c>Cost</c> only with manufacturing.costs.rates.view and payroll.manage.
/// <c>HoursWithheld</c>: without them, an order worked by a single employee shows no hours, since its labour total
/// over its hours would be that person's rate.
/// </summary>
public record LabourSnapshotRowDto(
    string EmployeeNumber, string EmployeeName, int Year, int Month, decimal? Hours, string Basis, string? Reason, decimal? Rate, decimal? Cost);

public record LabourSnapshotDto(bool PerEmployeeMoneyShown, bool HoursWithheld, int Employees, IReadOnlyList<LabourSnapshotRowDto> Rows);

public record CostElementDriftDto(string Element, decimal Adjusted, decimal Live, decimal Unexplained);

/// <summary>
/// Diagnostic only. <c>Live</c> is today's computation; <c>Unexplained</c> = Live − AdjustedProvisional as of the
/// chosen day. <c>LateRecognizedAfterAsOf</c> is recognized late effect recorded after that day. No adjustment is
/// ever created from it.
/// </summary>
public record LiveComparisonDto(
    bool Comparable, string? Reason, decimal Original, decimal LateRecognized, decimal AdjustedProvisional, decimal? Live,
    decimal? Unexplained, decimal LateRecognizedAfterAsOf, decimal LateUnclassified, decimal LateUncertain,
    IReadOnlyList<CostElementDriftDto> Elements, IReadOnlyList<CostGapDto> SourceChanges);

/// <summary>
/// One order's cost history. <c>State</c>: frozen; not_frozen (completed, no snapshot — never a zero cost);
/// not_frozen_at_date (its sheet was made after the chosen day); not_completed.
/// </summary>
public record OrderCostReconciliationDto(
    Guid OrderId, string OrderNumber, string OrderStatus, Guid LineId, string LineCode, string ProductSku, string BaseUnitCode,
    string State, CostSheetHeaderDto? Sheet, DateOnly AsOf, decimal AcceptedBaseQty, decimal RejectedBaseQty,
    CostFiguresDto? Figures, IReadOnlyList<CostElementReconciliationDto> Elements, decimal? RoundingResidual,
    IReadOnlyList<LateCostRowDto> LateCosts, ReleaseReconciliationDto? Releases, ScrapCostDto? Scrap, CostVarianceDto? Variances,
    LabourSnapshotDto? Labour, IReadOnlyList<CostCheckDto> Checks, IReadOnlyList<CostGapDto> Gaps,
    IReadOnlyList<string> Limitations, LiveComparisonDto? Live);

public record CostReportPageDto(int Page, int PageSize, int Total);

/// <summary>The period a register covers, in company days, and the basis it filtered on.</summary>
public record CostReportPeriodDto(DateOnly From, DateOnly To, string Basis, DateOnly AsOf);

// ---------- historical cost sheets (and the overview) ----------

public record CostLiveSummaryDto(bool Comparable, string? Reason, decimal? Live, decimal? Unexplained);

public record CostHistoryRowDto(
    Guid OrderId, string OrderNumber, string LineCode, string ProductSku, string State, CostSheetHeaderDto? Sheet,
    decimal AcceptedBaseQty, decimal RejectedBaseQty, CostFiguresDto? Figures, int LateCount, string? Integrity,
    IReadOnlyList<string> GapCodes, CostLiveSummaryDto? Live);

public record CostHistoryTotalsDto(
    int Frozen, int NotFrozen, int Reconstructed, int IncompleteLegacy, int IntegrityFailures,
    decimal Original, decimal LateRecorded, decimal LateRecognized, decimal LateUnclassified, decimal LateUncertain,
    decimal AdjustedProvisional);

public record CostHistoryReportDto(
    CostReportPeriodDto Period, IReadOnlyList<CostHistoryRowDto> Rows, CostReportPageDto Page, CostHistoryTotalsDto Totals,
    bool LiveIncluded, IReadOnlyList<string> Limitations);

// ---------- late costs ----------

public record LateElementTotalDto(string Element, decimal Recorded, decimal Recognized, decimal Unclassified, decimal Uncertain);

public record LateCostReportDto(
    CostReportPeriodDto Period, IReadOnlyList<LateCostRowDto> Rows, CostReportPageDto Page,
    IReadOnlyList<LateElementTotalDto> ByElement, decimal Recorded, decimal Recognized, decimal Unclassified, decimal Uncertain,
    IReadOnlyList<string> Limitations);

// ---------- variances ----------

public record CostVarianceRowDto(Guid OrderId, string OrderNumber, string LineCode, decimal AcceptedBaseQty, CostVarianceDto Variance);

public record CostVarianceReportDto(
    CostReportPeriodDto Period, IReadOnlyList<CostVarianceRowDto> Rows, CostReportPageDto Page,
    IReadOnlyList<VarianceLineDto> Totals, int Unavailable, IReadOnlyList<string> Limitations);

// ---------- partial releases ----------

public record ReleaseReconciliationOrderDto(Guid OrderId, string OrderNumber, string LineCode, decimal Original, ReleaseReconciliationDto Reconciliation);

public record ReleaseReconciliationReportDto(
    CostReportPeriodDto Period, IReadOnlyList<ReleaseReconciliationOrderDto> Rows, CostReportPageDto Page,
    decimal Original, decimal ReleasedValue, decimal KeptInCharges, decimal LateNotAllocated, IReadOnlyList<string> Limitations);

// ---------- scrap and rejected output ----------

public record ScrapCostRowDto(Guid OrderId, string OrderNumber, string LineCode, ScrapCostDto Scrap);

public record ScrapCostReportDto(
    CostReportPeriodDto Period, IReadOnlyList<ScrapCostRowDto> Rows, CostReportPageDto Page,
    decimal AcceptedQty, decimal RejectedQty, decimal? MaterialScrapValue, decimal? SpoilageAbsorbed, IReadOnlyList<string> Limitations);

// ---------- integrity and missing costs ----------

public record CostGapRowDto(
    Guid OrderId, string OrderNumber, string LineCode, string State, Guid? SheetId, string? Integrity,
    IReadOnlyList<CostCheckDto> Checks, IReadOnlyList<CostGapDto> Gaps);

public record CostGapReportDto(
    CostReportPeriodDto Period, IReadOnlyList<CostGapRowDto> Rows, CostReportPageDto Page,
    IReadOnlyDictionary<string, int> CountByCode, IReadOnlyDictionary<string, int> CountBySeverity, IReadOnlyList<string> Limitations);
