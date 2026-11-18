using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>
/// Where cash physically is. Nothing in the system tracked this before: a sale recorded a
/// tender and the Z-report counted a drawer, but the money had no location and so could not
/// be followed when the manager collected it from a branch.
/// <para>One box per branch; the main branch's box is the central safe.</para>
/// Server-side only — a till never needs the box list, and cash position is not something a
/// disconnected device can answer correctly anyway.
/// </summary>
public class CashBox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }

    [MaxLength(120)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(120)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(120)] public string NameEn { get; set; } = string.Empty;

    /// <summary>The central safe. Remittances from the branches land here.</summary>
    public bool IsMain { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum CashDirection { In = 0, Out = 1 }

/// <summary>
/// Why cash moved. Kept as an enum rather than a free string because each kind posts to the
/// ledger differently, and a typo in a reason must not silently become a new revenue line.
/// </summary>
public enum CashMovementKind
{
    /// <summary>Cash taken at the till for a sale.</summary>
    Sale = 0,
    /// <summary>Cash handed back — refund, or the unused part of a rental period.</summary>
    Refund = 1,
    /// <summary>The manager collecting a branch's takings for the central safe.</summary>
    Remittance = 2,
    /// <summary>Money in against a receipt voucher that is not a till sale.</summary>
    Receipt = 3,
    /// <summary>Money out against a payment voucher.</summary>
    Disbursement = 4,
    /// <summary>Counted difference. Recorded, never silently absorbed.</summary>
    Adjustment = 5,
    /// <summary>The float a box starts life with.</summary>
    Opening = 6
}

/// <summary>
/// One side of a cash movement. A remittance writes two rows — Out at the branch, In at the
/// central safe — linked by <see cref="RelatedCashBoxId"/>, exactly as
/// <see cref="StockMovement"/> already pairs the two halves of a stock transfer. The pattern
/// works and is reused rather than reinvented.
/// <para>
/// A remittance is <b>not</b> income at the safe and <b>not</b> an expense at the branch: the
/// revenue was recognised when the sale happened. In/Out describes direction at a location;
/// income and expense describe profit. Posting a remittance to the P&amp;L inflates both sides
/// while real profit never moved.
/// </para>
/// </summary>
public class CashMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CashBoxId { get; set; }
    public CashBox? CashBox { get; set; }

    public CashDirection Direction { get; set; }
    /// <summary>Always positive; <see cref="Direction"/> carries the sign.</summary>
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public CashMovementKind Kind { get; set; }

    /// <summary>Set on both rows of a remittance so the pair can be found and shown together.</summary>
    public Guid? RelatedCashBoxId { get; set; }

    /// <summary>Document this came from, so a figure in a report can always be traced back.</summary>
    [MaxLength(40)] public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }

    /// <summary>Who moved it. A remittance that never arrives needs a name against it.</summary>
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }

    [MaxLength(100)] public string? Reference { get; set; }
    [MaxLength(400)] public string? Note { get; set; }

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}
