using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>An issue of parts to a job, or a return of unused parts from it (Phase M2).</summary>
public enum MaintenancePartKind { Issue = 0, Return = 1 }

/// <summary>
/// One line of a parts document on a maintenance job (Phase M2, docs/maintenance/plan.md §2.3): one product taken out of
/// the job's branch stock to the job (an issue, <c>MJI-000001</c>), or brought back from it (a return, <c>MJR-000001</c>).
/// The lines of one request share its <see cref="DocumentId"/>, <see cref="DocumentNumber"/> and
/// <see cref="ClientRequestId"/>; a retried request finds them and moves nothing again.
/// <para>
/// <b>Cost, frozen.</b> <see cref="UnitCost"/> and <see cref="Value"/> are what the stock movement beside it was valued at:
/// on an issue, the cost method in force (the product's cost price, or the weighted average once sales are costed at it,
/// C-01, falling back to the cost price — <see cref="CostBasis"/> says which); on a return, the average of what the job's
/// issues of that product went out at, so goods come back at the cost they left at. The job's parts cost is the issues'
/// values less the returns' (plan §3.1).
/// </para>
/// <para>Server-only, never a <c>SyncEntity</c>. Posts nothing (MS-32, see the M2 record in the plan).</para>
/// </summary>
public class MaintenanceJobPart
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public MaintenancePartKind Kind { get; set; }
    /// <summary>The document the line belongs to: the <c>StockMovement.DocumentId</c> of its movement.</summary>
    public Guid DocumentId { get; set; }
    [MaxLength(20)] public string DocumentNumber { get; set; } = string.Empty;
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>In the product's base unit, always above zero; the kind says which way it went.</summary>
    public decimal Qty { get; set; }
    public decimal UnitCost { get; set; }
    /// <summary>What the line moved, as its movement was valued: Qty × UnitCost, unrounded as the average gave it.</summary>
    public decimal Value { get; set; }
    public SaleCostBasis CostBasis { get; set; }
    public Guid StockMovementId { get; set; }
    [MaxLength(64)] public string ClientRequestId { get; set; } = string.Empty;
    [MaxLength(400)] public string? Note { get; set; }
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Outside work on a maintenance job (Phase M2, plan §2.4): a welder, a painter, a part machined outside. The shape of
/// <see cref="ProductionOrderExpense"/>: a typed amount that may point at the expense payment voucher that paid it. The
/// voucher posts where it always has (category <c>repair</c> → 615); the link only puts the amount into the job's cost.
/// The amounts linked to one voucher, on production orders and jobs together, never exceed the voucher's amount.
/// Posts nothing.
/// </summary>
public class MaintenanceJobExpense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    [MaxLength(300)] public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public Guid? PaymentVoucherId { get; set; }
    [MaxLength(64)] public string ClientRequestId { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}
