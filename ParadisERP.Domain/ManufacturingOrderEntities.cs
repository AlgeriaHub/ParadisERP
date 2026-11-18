using System.ComponentModel.DataAnnotations;

namespace ParadisERP.Domain;

// Manufacturing, Phase 2 (docs/manufacturing/phase-2-plan.md, owner's approval of 2026-10-26): production
// orders on a line, their materials, the daily production record, labour bookings and finished-goods
// releases. Stock moves (issues, returns, releases) through the ordinary stock rows; nothing here posts
// to the ledger, reads or writes Product.CostPrice beyond a snapshot, or values stock. Every movement is
// marked Pending until the accountant approves the manufacturing accounting treatment.
//
// Every table is server-only and every foreign key Restrict, except a document's own children.

/// <summary>
/// Whether a manufacturing movement has reached the ledger. Only <see cref="Pending"/> exists today:
/// postings are switched on with the accountant (owner, 2026-10-26, decision 1). Values are only appended.
/// </summary>
public enum ManufacturingPostingState
{
    /// <summary>Moved in stock, recorded in full, not posted.</summary>
    Pending = 0,
}

/// <summary>
/// A production order's life (Revision 2 §4). Nothing changes it on its own: a passed planned date only
/// shows "late"; a person with the right permission makes every transition, and each is an event.
/// </summary>
public enum ProductionOrderStatus
{
    Open = 0,
    OnHold = 1,
    /// <summary>The line asked to complete it; a production manager of the line approves or refuses.</summary>
    CompletionRequested = 2,
    Completed = 3,
    Cancelled = 4,
}

/// <summary>
/// One production order (MO-): a quantity of one product to make on one line, from one approved BOM
/// version, copied at creation so a later version never changes it.
/// </summary>
public class ProductionOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(20)] public string Number { get; set; } = string.Empty;
    public Guid LineId { get; set; }
    public ProductionLine? Line { get; set; }
    /// <summary>The line's location when the order was made: where every movement of it happens.</summary>
    public Guid LocationId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid BomId { get; set; }
    public int BomVersion { get; set; }

    /// <summary>What was asked, as typed, and in the product's base unit.</summary>
    public decimal PlannedQty { get; set; }
    public Guid PlannedUnitId { get; set; }
    public decimal PlannedBaseQty { get; set; }
    /// <summary>Batches of the BOM the planned quantity is.</summary>
    public decimal Batches { get; set; }
    public DateOnly PlannedStart { get; set; }
    public DateOnly PlannedEnd { get; set; }

    public ProductionOrderStatus Status { get; set; }
    /// <summary>Started although materials were short, by someone allowed to, with a reason.</summary>
    public bool StartedShort { get; set; }
    [MaxLength(400)] public string? ShortageReason { get; set; }

    /// <summary>Released to stock, in the base unit — the accepted finished quantity.</summary>
    public decimal AcceptedBaseQty { get; set; }
    /// <summary>Rejected on approved daily records, all stages.</summary>
    public decimal RejectedBaseQty { get; set; }

    /// <summary>The estimate frozen at creation: the BOM materials at their reference cost then.</summary>
    public decimal EstimatedMaterialCost { get; set; }

    [MaxLength(1000)] public string? Notes { get; set; }
    /// <summary>The request that created it: a retried creation is the same order.</summary>
    [MaxLength(64)] public string ClientRequestId { get; set; } = string.Empty;

    public Guid? CreatedByUserId { get; set; }
    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CompletionRequestedByUserId { get; set; }
    public DateTime? CompletionRequestedAtUtc { get; set; }
    public Guid? CompletedByUserId { get; set; }
    [MaxLength(200)] public string? CompletedBy { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    /// <summary>The company day it was approved complete: with <see cref="PlannedEnd"/>, the overdue days.</summary>
    public DateOnly? ActualEnd { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<ProductionOrderMaterial> Materials { get; set; } = new();
    public List<ProductionOrderStage> Stages { get; set; } = new();
}

/// <summary>
/// One material of an order, copied from its BOM line, with what happened to it since. Per material:
/// issued − returned − consumed − scrapped is what is still at the line for this order, never below zero,
/// and zero before completion can be requested (amendment 8).
/// </summary>
public class ProductionOrderMaterial
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public ProductionOrder? Order { get; set; }
    public int Sequence { get; set; }
    public Guid? BomLineId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public ManufacturingRole Role { get; set; }
    public Guid BaseUnitId { get; set; }

    /// <summary>For the planned quantity, in the base unit: net, expected waste and their sum.</summary>
    public decimal NetBaseQty { get; set; }
    public decimal WasteBaseQty { get; set; }
    public decimal RequiredBaseQty { get; set; }
    /// <summary>The product's reference cost when the order was made (CostPrice, read only): the estimate's.</summary>
    public decimal EstimatedUnitCost { get; set; }
    /// <summary>The BOM line's specification, as text, kept with the order.</summary>
    [MaxLength(600)] public string? Specification { get; set; }

    public decimal IssuedBaseQty { get; set; }
    public decimal ReturnedBaseQty { get; set; }
    public decimal ConsumedBaseQty { get; set; }
    public decimal ScrappedBaseQty { get; set; }
    /// <summary>What a production manager released from the reservation for another order, with the event.</summary>
    public decimal ReservationReleasedBaseQty { get; set; }
}

/// <summary>One stage of an order, copied from its BOM, with its progress from approved daily records.</summary>
public class ProductionOrderStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public ProductionOrder? Order { get; set; }
    public int Sequence { get; set; }
    [MaxLength(200)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameEn { get; set; } = string.Empty;
    /// <summary>The BOM's minutes per batch times the order's batches.</summary>
    public decimal PlannedMinutes { get; set; }
    public int ExpectedWorkers { get; set; }
    public bool IsQualityCheckpoint { get; set; }
    /// <summary>Only this stage's good quantity is finished output; the others are progress on the same units.</summary>
    public bool IsOutput { get; set; }
    public decimal GoodBaseQty { get; set; }
    public decimal RejectedBaseQty { get; set; }
}

public enum ProductionOrderEventKind
{
    Created = 0,
    StartedShort = 1,
    Held = 2,
    Resumed = 3,
    ReservationReleased = 4,
    OutputReleased = 5,
    CompletionRequested = 6,
    CompletionApproved = 7,
    CompletionRefused = 8,
    Cancelled = 9,
    ExpenseRecorded = 10,
}

/// <summary>What happened to an order, by whom, when and why. Append-only.</summary>
public class ProductionOrderEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public ProductionOrderEventKind Kind { get; set; }
    [MaxLength(400)] public string? Detail { get; set; }
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An expense of the order outside materials and labour (subcontracted cutting, transport): a recorded
/// input for the cost sheet, optionally pointing at the payment voucher that paid it. Posts nothing.
/// </summary>
public class ProductionOrderExpense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    [MaxLength(300)] public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public Guid? PaymentVoucherId { get; set; }
    [MaxLength(64)] public string ClientRequestId { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

public enum MaterialMovementKind
{
    /// <summary>From the line's stock to the order. Moves stock out of the location.</summary>
    Issue = 0,
    /// <summary>Unused material from the order back to the line's stock.</summary>
    Return = 1,
    /// <summary>Issued material spoiled or cut away and thrown out: leaves the order, moves no stock.</summary>
    Scrap = 2,
}

/// <summary>
/// A material document of an order (MI-, MR-, MS-). Source and destination follow from the kind: an issue
/// goes from the line's location to the order, a return from the order to the location, scrap from the
/// order to nowhere. Pending until postings are approved.
/// </summary>
public class MaterialMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(20)] public string Number { get; set; } = string.Empty;
    public MaterialMovementKind Kind { get; set; }
    public Guid OrderId { get; set; }
    public Guid LineId { get; set; }
    public Guid LocationId { get; set; }
    /// <summary>The transaction identifier: a retried request is this same document, not a second one.</summary>
    [MaxLength(64)] public string ClientRequestId { get; set; } = string.Empty;
    [MaxLength(400)] public string? Note { get; set; }
    public ManufacturingPostingState PostingState { get; set; }
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public List<MaterialMovementLine> Lines { get; set; } = new();
}

public class MaterialMovementLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MovementId { get; set; }
    public MaterialMovement? Movement { get; set; }
    public Guid OrderMaterialId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>As typed, in the unit chosen, with the factor to the base unit as it stood.</summary>
    public decimal EnteredQty { get; set; }
    public Guid UnitId { get; set; }
    public decimal Factor { get; set; }
    public decimal BaseQty { get; set; }
    /// <summary>The product's reference cost at this moment (CostPrice, read only).</summary>
    public decimal UnitCostSnapshot { get; set; }
    /// <summary>The stock row it wrote, for an issue or a return.</summary>
    public Guid? StockMovementId { get; set; }
}

public enum ConsumptionReason
{
    /// <summary>Used up making the product.</summary>
    Used = 0,
    /// <summary>Offcuts too short to use, kept or not.</summary>
    Offcut = 1,
    /// <summary>The leftover count found less than the books said.</summary>
    CountDifference = 2,
}

/// <summary>
/// What was actually used, out of the issued balance, against an order and optionally the daily record row
/// it was used on (amendment 8). Moves no stock and posts nothing: the cost sheet reads it.
/// </summary>
public class MaterialConsumption
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid OrderMaterialId { get; set; }
    public Guid? LogRowId { get; set; }
    public decimal BaseQty { get; set; }
    public ConsumptionReason Reason { get; set; }
    [MaxLength(400)] public string? Note { get; set; }
    [MaxLength(64)] public string ClientRequestId { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Finished goods released from an order into the line's stock (OR-), approved by a production manager of
/// the line: at most the output stage's approved good quantity not yet released. Pending until postings.
/// </summary>
public class OutputRelease
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(20)] public string Number { get; set; } = string.Empty;
    public Guid OrderId { get; set; }
    public Guid LineId { get; set; }
    public Guid LocationId { get; set; }
    public decimal EnteredQty { get; set; }
    public Guid UnitId { get; set; }
    public decimal Factor { get; set; }
    public decimal BaseQty { get; set; }
    /// <summary>The finished product's reference cost at release (CostPrice, read only).</summary>
    public decimal UnitCostSnapshot { get; set; }
    public Guid? StockMovementId { get; set; }
    [MaxLength(64)] public string ClientRequestId { get; set; } = string.Empty;
    [MaxLength(400)] public string? Note { get; set; }
    public ManufacturingPostingState PostingState { get; set; }
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

public enum ProductionLogStatus
{
    Draft = 0,
    Submitted = 1,
    Approved = 2,
}

/// <summary>
/// The daily production record (PL-): one per line per day, holding its operation rows. Submitted by the
/// line, approved by a production manager of the line who did not submit it; an approved record is frozen.
/// </summary>
public class ProductionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(20)] public string Number { get; set; } = string.Empty;
    public Guid LineId { get; set; }
    public DateOnly Day { get; set; }
    public ProductionLogStatus Status { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    /// <summary>Why a manager sent it back to draft, until it is submitted again.</summary>
    [MaxLength(400)] public string? ReturnedReason { get; set; }
    public Guid? CreatedByUserId { get; set; }
    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? SubmittedByUserId { get; set; }
    [MaxLength(200)] public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    [MaxLength(200)] public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public List<ProductionLogRow> Rows { get; set; } = new();
}

/// <summary>Time on the line spent on no order, logged so paid hours are explained without a product.</summary>
public enum NonOrderCategory
{
    Cleaning = 0,
    Breakdown = 1,
    WaitingForMaterial = 2,
    Training = 3,
    Other = 4,
}

/// <summary>
/// One operation of the day (amendment 5): one stage of one order and the quantity processed at it — or a
/// stretch of non-order time. Only the output stage's completed quantity is finished output; the other
/// stages record progress on the same units, so an item cut and then welded counts once.
/// </summary>
public class ProductionLogRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LogId { get; set; }
    public ProductionLog? Log { get; set; }
    public int Sequence { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? StageId { get; set; }
    public NonOrderCategory? Category { get; set; }
    /// <summary>The order's product, kept on the row as it was.</summary>
    public Guid? ProductId { get; set; }
    /// <summary>The product's specification and dimensions, copied as text and editable on the row.</summary>
    [MaxLength(600)] public string? Specification { get; set; }
    /// <summary>In the order product's base unit.</summary>
    public decimal PlannedQty { get; set; }
    public decimal CompletedQty { get; set; }
    public decimal RejectedQty { get; set; }
    [MaxLength(300)] public string? RejectReason { get; set; }
    [MaxLength(400)] public string? Notes { get; set; }
}

/// <summary>
/// What a booking's hours were spent on: a production order's stage on a daily record, or a maintenance job (Phase M3,
/// docs/maintenance/plan.md §2.5), booked straight on the job with no line and no record row.
/// </summary>
public enum LaborOrderKind
{
    Production = 0,
    Maintenance = 1,
}

/// <summary>
/// Hours one employee worked on one day: on one row of a day's production record (<see cref="LaborOrderKind.Production"/>,
/// the only record of manufacturing labour, amendment 7), or on a maintenance job (<see cref="LaborOrderKind.Maintenance"/>,
/// Phase M3: <see cref="OrderId"/> is the job, and there is no line, row or stage). Its total per employee and day, across
/// every line and order kind, never exceeds the attendance the employee's day shows, unless a controlled override says
/// why. It holds no rate and no money; it names the payroll branch and month the costing will read the rate from. Unique
/// per employee and row (production), per employee, job and day (maintenance, enforced in code under the version lock).
/// </summary>
public class LaborBooking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly Day { get; set; }
    public LaborOrderKind OrderKind { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? StageId { get; set; }
    /// <summary>The production line; null on a maintenance booking (Phase M3).</summary>
    public Guid? LineId { get; set; }
    /// <summary>The daily record's row; null on a maintenance booking (Phase M3).</summary>
    public Guid? LogRowId { get; set; }
    public decimal Hours { get; set; }
    /// <summary>
    /// How many of <see cref="Hours"/> were overtime (C-05, 2026-11-09): 0 (the default) to <see cref="Hours"/>. Under the
    /// "premium to overtime hours" costing choice, payroll's overtime premium for the month is spread over these hours only;
    /// under the blended rate (the default) they cost what every other hour costs.
    /// </summary>
    public decimal OvertimeHours { get; set; }
    /// <summary>The payroll the hours belong to: the employee's branch and the day's month.</summary>
    public Guid PayrollBranchId { get; set; }
    public int PayrollYear { get; set; }
    public int PayrollMonth { get; set; }
    /// <summary>The attendance the day showed when booked, and the override that allowed more.</summary>
    public decimal AttendanceHours { get; set; }
    [MaxLength(400)] public string? OverrideReason { get; set; }
    public Guid? OverriddenByUserId { get; set; }
    [MaxLength(200)] public string? OverriddenBy { get; set; }
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

public enum LaborBookingAction
{
    Created = 0,
    Removed = 1,
}

/// <summary>What happened to a booking, kept after it is removed — that is when it matters. No foreign key.</summary>
public class LaborBookingChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly Day { get; set; }
    /// <summary>The daily record's row; null for a maintenance booking (Phase M3).</summary>
    public Guid? LogRowId { get; set; }
    /// <summary>What the booking was on (Phase M3): production, or a maintenance job named by <see cref="OrderId"/>. Rows from before M3 are production.</summary>
    public LaborOrderKind OrderKind { get; set; }
    /// <summary>The booking's order or job (Phase M3); null on rows from before M3.</summary>
    public Guid? OrderId { get; set; }
    public LaborBookingAction Action { get; set; }
    public decimal Hours { get; set; }
    /// <summary>The booking's overtime hours (C-05); 0 for a booking made before they were recorded.</summary>
    public decimal OvertimeHours { get; set; }
    [MaxLength(400)] public string? OverrideReason { get; set; }
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}
