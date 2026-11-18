namespace ParadisERP.Application.Contracts;

// Manufacturing, Phase 2 (docs/manufacturing/phase-2.md): production orders, their materials, the daily
// production record, labour bookings, releases and the manufacturing reports. Back office only. Every
// quantity named "Base" is in the product's base unit; money figures are reference costs, never postings.

/// <summary>Whether this server lets manufacturing move stock (configuration <c>Manufacturing:StockOperations</c>).</summary>
public record ManufacturingOperationsStatusDto(bool StockOperationsEnabled, bool CostingEnabled);

// ---------- orders ----------

public record CreateProductionOrderRequest(
    Guid LineId, Guid ProductId, decimal PlannedQty, Guid PlannedUnitId,
    DateOnly PlannedStart, DateOnly PlannedEnd, string? Notes, string ClientRequestId,
    bool StartShort = false, string? ShortageReason = null);

/// <summary>One material's need against what the line can promise, before an order is made.</summary>
public record OrderMaterialAvailabilityDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, string BaseUnitCode,
    decimal RequiredBaseQty, decimal StockBaseQty, decimal ReservedByOthersBaseQty, decimal FreeBaseQty, decimal ShortBaseQty);

public record ProductionOrderPreviewDto(
    Guid BomId, int BomVersion, decimal PlannedBaseQty, string BaseUnitCode, decimal Batches,
    IReadOnlyList<OrderMaterialAvailabilityDto> Materials, bool Short);

public record ProductionOrderSummaryDto(
    Guid Id, string Number, Guid LineId, string LineCode, Guid ProductId, string ProductSku,
    string ProductNameAr, string ProductNameFr, string ProductNameEn, string Status,
    decimal PlannedBaseQty, string BaseUnitCode, decimal AcceptedBaseQty, decimal RejectedBaseQty,
    DateOnly PlannedStart, DateOnly PlannedEnd, DateOnly? ActualEnd, int OverdueDays, bool Late, bool StartedShort,
    DateTime CreatedAtUtc);

public record OrderMaterialDto(
    Guid Id, int Sequence, Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn, string Role,
    Guid BaseUnitId, string BaseUnitCode, decimal NetBaseQty, decimal WasteBaseQty, decimal RequiredBaseQty,
    decimal EstimatedUnitCost, string? Specification,
    decimal IssuedBaseQty, decimal ReturnedBaseQty, decimal ConsumedBaseQty, decimal ScrappedBaseQty,
    decimal ReservationReleasedBaseQty, decimal ReservedBaseQty, decimal AtLineBaseQty);

public record OrderStageDto(
    Guid Id, int Sequence, string NameAr, string NameFr, string NameEn, decimal PlannedMinutes, int ExpectedWorkers,
    bool IsQualityCheckpoint, bool IsOutput, decimal GoodBaseQty, decimal RejectedBaseQty, decimal BookedHours);

public record OrderEventDto(string Kind, string? Detail, string? UserName, DateTime AtUtc);

public record MaterialMovementLineDto(
    Guid OrderMaterialId, Guid ProductId, string Sku, decimal EnteredQty, string UnitCode, decimal BaseQty, decimal UnitCostSnapshot);

public record MaterialMovementDto(
    Guid Id, string Number, string Kind, string PostingState, string? Note, string? UserName, DateTime AtUtc,
    IReadOnlyList<MaterialMovementLineDto> Lines);

public record ConsumptionDto(
    Guid Id, Guid OrderMaterialId, string Sku, Guid? LogRowId, decimal BaseQty, string Reason, string? Note, string? UserName, DateTime AtUtc);

public record OutputReleaseDto(
    Guid Id, string Number, decimal EnteredQty, string UnitCode, decimal BaseQty, decimal UnitCostSnapshot,
    string PostingState, string? Note, string? UserName, DateTime AtUtc);

public record OrderExpenseDto(Guid Id, string Description, decimal Amount, Guid? PaymentVoucherId, string? UserName, DateTime AtUtc);

/// <summary>
/// An order with everything on it. <c>AwaitingReleaseBaseQty</c> is the output stage's approved good
/// quantity not yet released; <c>Remaining</c> is planned less accepted. The <c>Can…</c> flags are the
/// server's answer for this caller, so a screen offers only what will be accepted.
/// </summary>
public record ProductionOrderDto(
    ProductionOrderSummaryDto Order, Guid BomId, int BomVersion, decimal PlannedQty, string PlannedUnitCode, decimal Batches,
    string? ShortageReason, string? Notes, decimal EstimatedMaterialCost,
    decimal ProducedBaseQty, decimal AwaitingReleaseBaseQty, decimal RemainingBaseQty,
    string? CreatedBy, string? CompletionRequestedBy, DateTime? CompletionRequestedAtUtc, string? CompletedBy, DateTime? CompletedAtUtc,
    IReadOnlyList<OrderMaterialDto> Materials, IReadOnlyList<OrderStageDto> Stages,
    IReadOnlyList<MaterialMovementDto> Movements, IReadOnlyList<ConsumptionDto> Consumptions,
    IReadOnlyList<OutputReleaseDto> Releases, IReadOnlyList<OrderExpenseDto> Expenses, IReadOnlyList<OrderEventDto> Events,
    bool CanManage, bool CanMoveMaterials, bool CanRelease, bool CanRequestCompletion, bool CanApproveCompletion);

public record OrderReasonRequest(string? Reason);

public record ReleaseReservationRequest(decimal BaseQty, string? Reason);

public record OrderExpenseRequest(string Description, decimal Amount, Guid? PaymentVoucherId, string ClientRequestId);

public record MaterialMovementLineRequest(Guid OrderMaterialId, decimal Qty, Guid UnitId);

/// <summary>Kind: Issue, Return or Scrap.</summary>
public record MaterialMovementRequest(string Kind, IReadOnlyList<MaterialMovementLineRequest>? Lines, string? Note, string ClientRequestId);

/// <summary>Reason: Used, Offcut or CountDifference. <c>BaseQty</c> is in the material's base unit.</summary>
public record ConsumptionRequest(Guid OrderMaterialId, decimal BaseQty, string Reason, Guid? LogRowId, string? Note, string ClientRequestId);

public record OutputReleaseRequest(decimal Qty, Guid UnitId, string? Note, string ClientRequestId);

// ---------- costing inputs ----------

public record CostMaterialDto(
    Guid OrderMaterialId, string Sku, string Role, string BaseUnitCode,
    decimal RequiredBaseQty, decimal EstimatedUnitCost, decimal EstimatedCost,
    decimal IssuedBaseQty, decimal ReturnedBaseQty, decimal ConsumedBaseQty, decimal ScrappedBaseQty,
    decimal UsedBaseQty, decimal UsedCostAtSnapshot);

/// <summary>Hours only: the rate is read from payroll by the costing, never stored or shown here.</summary>
public record CostLabourDto(
    Guid EmployeeId, string EmployeeNumber, string EmployeeName, Guid? StageId, decimal Hours,
    Guid PayrollBranchId, int PayrollYear, int PayrollMonth, Guid? PayrollRunId, string? PayrollRunNumber, string? PayrollRunStatus);

public record ProductionCostInputsDto(
    Guid OrderId, string Number, decimal PlannedBaseQty, decimal AcceptedBaseQty, decimal RejectedBaseQty,
    decimal EstimatedMaterialCost, IReadOnlyList<CostMaterialDto> Materials, IReadOnlyList<CostLabourDto> Labour,
    decimal LabourHours, IReadOnlyList<OrderExpenseDto> Expenses, decimal ExpensesTotal);

// ---------- the daily production record ----------

/// <summary><c>OvertimeHours</c>: how many of the hours were overtime (C-05); 0 when none were marked.</summary>
public record LaborBookingDto(
    Guid Id, Guid EmployeeId, string EmployeeNumber, string EmployeeName, decimal Hours, decimal AttendanceHours,
    string? OverrideReason, string? OverriddenBy, string? UserName, DateTime AtUtc, decimal OvertimeHours = 0m);

public record ProductionLogRowDto(
    Guid Id, int Sequence, Guid? OrderId, string? OrderNumber, Guid? StageId, string? StageNameAr, string? StageNameFr,
    string? StageNameEn, bool IsOutputStage, string? Category, Guid? ProductId, string? ProductSku, string? BaseUnitCode,
    string? Specification, decimal PlannedQty, decimal CompletedQty, decimal RejectedQty, string? RejectReason, string? Notes,
    IReadOnlyList<LaborBookingDto> Bookings, IReadOnlyList<ConsumptionDto> Consumptions);

public record ProductionLogSummaryDto(
    Guid Id, string Number, Guid LineId, string LineCode, DateOnly Day, string Status, int RowCount, decimal Hours,
    string? SubmittedBy, string? ApprovedBy);

/// <summary>
/// Said, never refused (amendment 5): a stage's good quantity, with this record counted, above the
/// previous stage's — more units welded than were cut. Code <c>stage_ahead_of_previous</c>.
/// </summary>
public record ProductionLogWarningDto(
    string Code, string OrderNumber, string StageNameAr, string StageNameFr, string StageNameEn,
    string PreviousNameAr, string PreviousNameFr, string PreviousNameEn, decimal StageGood, decimal PreviousGood);

public record ProductionLogDto(
    ProductionLogSummaryDto Log, string? Notes, string? ReturnedReason, string? CreatedBy,
    DateTime? SubmittedAtUtc, DateTime? ApprovedAtUtc, IReadOnlyList<ProductionLogRowDto> Rows,
    IReadOnlyList<ProductionLogWarningDto> Warnings, bool CanEdit, bool CanApprove);

public record OpenProductionLogRequest(Guid LineId, DateOnly Day);

/// <summary>A row to keep (<c>Id</c> set) or add (no id). Either an order and its stage, or a non-order category.</summary>
public record ProductionLogRowRequest(
    Guid? Id, Guid? OrderId, Guid? StageId, string? Category, string? Specification,
    decimal PlannedQty, decimal CompletedQty, decimal RejectedQty, string? RejectReason, string? Notes);

/// <summary>A draft, sent whole: rows left out are removed (refused while they hold hours or consumption).</summary>
public record SaveProductionLogRequest(string? Notes, IReadOnlyList<ProductionLogRowRequest>? Rows);

/// <summary><c>OvertimeHours</c>: how many of <c>Hours</c> were overtime, 0 (the default) to <c>Hours</c> (C-05).</summary>
public record LaborBookingRequest(Guid EmployeeId, decimal Hours, string? OverrideReason, decimal OvertimeHours = 0m);

/// <summary>An employee the record may book: assigned to the line that day, with the day's attendance and what is booked already.</summary>
public record LogEmployeeCandidateDto(
    Guid EmployeeId, string Number, string FullName, decimal? AttendanceHours, string AttendanceSource,
    decimal BookedHours, bool PayrollLocked);

// ---------- reports ----------

public record PosExceptionRowDto(
    Guid Id, Guid SaleId, string Reference, DateTime SoldAtUtc, DateTime ReceivedAtUtc, Guid BranchId, string BranchCode,
    string BranchNameAr, string BranchNameFr, string DeviceCode, Guid ProductId, string Sku, string ProductNameAr,
    string ProductNameFr, string ProductNameEn, decimal Quantity, string Role, string? CashierName,
    DateTime? WithdrawnAtUtc, string Stale);

public record PosExceptionReportDto(
    IReadOnlyList<PosExceptionRowDto> Rows, int Total, decimal TotalQuantity, int StaleCount, int AfterWithdrawalCount, int UnknownCount);

/// <summary>
/// One pending movement. A material line carries its reference cost snapshot; a release carries the costing
/// preview's unit value, or null when it is not valued (see <see cref="PendingMovementsDto"/>).
/// </summary>
public record PendingMovementRowDto(
    string Document, string Number, string Kind, Guid OrderId, string OrderNumber, Guid LineId, string LineCode,
    Guid ProductId, string Sku, decimal BaseQty, decimal? UnitCostSnapshot, decimal? Value, string Direction,
    string? UserName, DateTime AtUtc);

/// <summary>
/// The manufacturing movements stock has seen and the ledger has not, per line: materials issued to orders
/// less returned (work in progress) at their reference cost snapshot, and finished goods released. A
/// release is valued by the costing preview only when this server computes costs and the caller may see
/// them (<c>ReleasesValued</c>); otherwise it is not valued at all. Its own snapshot is the finished
/// product's catalogue cost, not what it cost to make, and is never shown as its value (costing plan F1).
/// </summary>
public record PendingMovementsDto(
    IReadOnlyList<PendingMovementRowDto> Rows, decimal IssuedValue, decimal ReturnedValue, decimal? ReleasedValue,
    decimal WorkInProgressValue, bool ReleasesValued);
