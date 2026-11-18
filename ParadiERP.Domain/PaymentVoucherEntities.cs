using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>Why money leaves under a payment voucher.</summary>
public enum PaymentVoucherKind
{
    /// <summary>Money back to a customer — the unused part of a rental period, an overpayment.</summary>
    CustomerRefund = 1,
    /// <summary>A cost the branch paid: fuel, transport, a repair, a purchase.</summary>
    Expense = 2,
    /// <summary>
    /// The voucher a payroll payout carries — an advance, wages, a statutory payment (answer 24).
    /// Raised paid by the payroll document it points at, which moved the cash and posted.
    /// </summary>
    Payroll = 3,
    /// <summary>
    /// Money paid to a supplier against what the company owes them (owner, 2026-10-10): debits 401
    /// for that supplier, so it comes off their balance and shows on their statement.
    /// </summary>
    SupplierPayment = 4
}

/// <summary>
/// A payment voucher is asked for, approved, paid, or dropped. Pending moves nothing: like a
/// pending receipt voucher, it is a piece of paper until the money actually leaves.
/// </summary>
public enum PaymentVoucherStatus
{
    Pending = 1,
    Approved = 2,
    Paid = 3,
    Cancelled = 4
}

/// <summary>
/// Money going out — the mirror of <see cref="ReceiptVoucher"/>. Either a refund to a customer
/// or an expense a branch paid, on a numbered paper ("PV-000001") that the owner signs.
///
/// <para>
/// <b>Server-only, and deliberately not a <see cref="SyncEntity"/></b>, for the reason every
/// treasury row is: a till neither raises one nor reads one, and the pull is driven by the base
/// type. Restrict on every foreign key: a voucher names a branch, a customer, a contract and a
/// return note as historical facts, and none of them may vanish from under it.
/// </para>
/// <para>
/// A refund raised from a return note carries the note, the contract, the unused days and how
/// much of the refund was taken off the customer's debt first — only the remainder is paid out,
/// and that remainder is <see cref="Amount"/>.
/// </para>
/// </summary>
public class PaymentVoucher
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The number on the paper. Unique, generated server-side as "PV-000001".</summary>
    [MaxLength(20)] public string Number { get; set; } = string.Empty;

    /// <summary>Where the money leaves from. A cash payment comes out of this branch's box.</summary>
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }

    public PaymentVoucherKind Kind { get; set; }

    /// <summary>The customer refunded. Null on an expense.</summary>
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>Who received the money, as written on the paper — the supplier, the driver, the customer's delegate.</summary>
    [MaxLength(160)] public string? PayeeName { get; set; }

    /// <summary>The supplier a <see cref="PaymentVoucherKind.SupplierPayment"/> pays; null on every other kind.</summary>
    public Guid? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>On an expense: fuel | transport | repair | purchase | other.</summary>
    [MaxLength(40)] public string? ExpenseCategory { get; set; }

    /// <summary>The ledger account the expense posts to, resolved from the category when it is paid.</summary>
    [MaxLength(10)] public string? ExpenseAccountCode { get; set; }

    /// <summary>What actually leaves. On a refund, the part not already taken off the debt.</summary>
    public decimal Amount { get; set; }

    /// <summary>The <see cref="PaymentMethodDef.Key"/> it is paid under, stored not referenced.</summary>
    [MaxLength(40)] public string MethodKey { get; set; } = string.Empty;

    [MaxLength(60)] public string? InstrumentNumber { get; set; }
    [MaxLength(120)] public string? InstrumentBank { get; set; }

    /// <summary>The "in exchange for" line on the printed voucher.</summary>
    [MaxLength(300)] public string? Purpose { get; set; }

    /// <summary>The rental contract a refund relates to, when it does.</summary>
    public Guid? ContractId { get; set; }
    public RentalContract? Contract { get; set; }

    /// <summary>The return note whose unused rent this refunds, when it does.</summary>
    public Guid? ReturnNoteId { get; set; }
    public ReturnNote? ReturnNote { get; set; }

    /// <summary>Days of the agreed period the customer paid for and never used, on a refund.</summary>
    public int? UnusedDays { get; set; }

    /// <summary>How much of the refund was set against what the customer still owed before anything was paid out. Zero on an expense.</summary>
    public decimal DeductedFromDebt { get; set; }

    public PaymentVoucherStatus Status { get; set; } = PaymentVoucherStatus.Pending;

    [MaxLength(100)] public string? RequestedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? ApprovedByUserId { get; set; }
    [MaxLength(120)] public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }

    public DateTime? PaidAtUtc { get; set; }
    [MaxLength(120)] public string? PaidByName { get; set; }

    public DateTime? CancelledAtUtc { get; set; }

    /// <summary>On a workers' bonus voucher: who received what. Empty on every other voucher.</summary>
    public List<PaymentVoucherBonusLine> BonusLines { get; set; } = [];

    /// <summary>Why it was approved, refused or cancelled — the decision's note.</summary>
    [MaxLength(300)] public string? StatusNote { get; set; }

    [MaxLength(500)] public string? Notes { get; set; }

    /// <summary>
    /// The document a payroll voucher records (<see cref="PaymentVoucherKind.Payroll"/>): its ledger
    /// source tag and id — "wage-advance", "wage-payment", "payroll-statutory-payment". Null on a
    /// voucher raised on its own.
    /// </summary>
    [MaxLength(40)] public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }
}

/// <summary>
/// One worker's share of a workers' bonus voucher (مكافآت عمال, owner 2026-11-08: "it reflected in his payroll", "bonus
/// may be assigned for more than one employee"). The voucher pays the total out of the box and books it to staff
/// advances (425); the first payroll run of the worker's month or later puts the bonus on the payslip as an earning and
/// takes the same amount back as paid by voucher, crediting 425 — so the expense is booked once, through payroll, and
/// nobody is paid twice.
/// </summary>
public class PaymentVoucherBonusLine
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VoucherId { get; set; }
    public PaymentVoucher? Voucher { get; set; }

    /// <summary>Restrict: the worker is never removed from under the money they received.</summary>
    public Guid EmployeeId { get; set; }

    /// <summary>The name as it stood on the voucher, so the paper reads the same after a rename.</summary>
    [MaxLength(200)] public string EmployeeNameSnapshot { get; set; } = string.Empty;
    [MaxLength(40)] public string EmployeeNumberSnapshot { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>The payroll month it belongs to; a run of this month or a later one takes it.</summary>
    public int PayrollYear { get; set; }
    public int PayrollMonth { get; set; }

    /// <summary>
    /// The run that put it on a payslip: set when a run computes it, cleared when that run is reversed. Settled once that
    /// run is approved. A line held by another live run is never taken twice.
    /// </summary>
    public Guid? RunId { get; set; }
}
