using ParadiERP.Domain;

namespace ParadiERP.Application.Contracts;

// ---------- payment vouchers (money going out) ----------

/// <summary>
/// One payment voucher as the back office reads it — every stored field plus the names the
/// screen and the print need resolved (<paramref name="BranchName"/>, <paramref name="CustomerName"/>,
/// <paramref name="MethodName"/>). Server-only, like the entity: a till never sees one.
/// </summary>
public record PaymentVoucherDto(
    Guid Id, string Number, Guid BranchId, string BranchName,
    PaymentVoucherKind Kind,
    Guid? CustomerId, string? CustomerName,
    string? PayeeName, string? ExpenseCategory, string? ExpenseAccountCode,
    decimal Amount, string MethodKey, string MethodName,
    string? InstrumentNumber, string? InstrumentBank,
    string? Purpose,
    Guid? ContractId, Guid? ReturnNoteId, int? UnusedDays, decimal DeductedFromDebt,
    PaymentVoucherStatus Status,
    string? RequestedBy, DateTime CreatedAtUtc,
    Guid? ApprovedByUserId, string? ApprovedByName, DateTime? ApprovedAtUtc,
    DateTime? PaidAtUtc, string? PaidByName,
    DateTime? CancelledAtUtc,
    string? StatusNote, string? Notes,
    // For the paper and the list: the numbers behind the two ids, and what the return note
    // itself decided when the goods came back — the unused rent and the part of it taken off the
    // debt there and then. Null when the voucher names neither.
    string? ContractNumber = null, string? ReturnNoteNumber = null,
    decimal? ReturnNoteUnusedRent = null, decimal? ReturnNoteDebtReduction = null,
    // The supplier a supplier payment pays (owner, 2026-10-10); its name is PayeeName.
    Guid? SupplierId = null,
    // A workers' bonus (owner, 2026-11-08): who received what, and the payroll month it goes on.
    IReadOnlyList<PaymentVoucherBonusLineDto>? BonusLines = null);

/// <summary>
/// One worker's share of a workers' bonus voucher. <paramref name="RunNumber"/> names the payroll run that put it on the
/// payslip; <paramref name="Settled"/> says that run is approved or paid.
/// </summary>
public record PaymentVoucherBonusLineDto(
    Guid EmployeeId, string EmployeeNumber, string EmployeeName, decimal Amount, int PayrollYear, int PayrollMonth,
    Guid? RunId, string? RunNumber, bool Settled);

/// <summary>A worker a workers' bonus may name: who they are, never what they earn.</summary>
public record BonusWorkerDto(Guid Id, string Number, string FullName);

/// <summary>One worker and their bonus, on a new workers' bonus voucher.</summary>
public record PaymentVoucherBonusLineRequest(Guid EmployeeId, decimal Amount);

/// <summary>
/// What a create, an approval, a payment or a cancellation hands back: the voucher, and anything
/// the user should be told about a voucher that <i>was</i> recorded — the box looked short, part
/// of the refund was taken off the debt. <paramref name="WarningCode"/> and
/// <paramref name="WarningArgs"/> let the screen say it in the reader's language; the sentence
/// is the English fallback.
/// </summary>
public record PaymentVoucherResponse(
    PaymentVoucherDto Voucher, string? Warning = null, string? WarningCode = null,
    IReadOnlyDictionary<string, string>? WarningArgs = null);

/// <summary>
/// A return note whose early-return refund was left owed as a payment voucher, and how much of
/// it is still to be raised. <paramref name="Payable"/> is what a voucher raised now would pay
/// out: the owed figure, capped by what the customer's account still holds in their favour
/// (answer 27 — a refund to someone who owes the company comes off the debt).
/// </summary>
public record RefundableReturnNoteDto(
    Guid ReturnNoteId, string Number, Guid ContractId, string ContractNumber,
    Guid CustomerId, string CustomerName, Guid BranchId, DateTime ReturnedAtUtc,
    decimal UnusedRent, decimal ReliefDebtReduction, decimal ReliefPaymentVoucher,
    decimal AlreadyRaised, decimal StillOwed, decimal CustomerCredit, decimal Payable,
    int? UnusedDays);

/// <summary>
/// What may still be refunded on one contract for the period its customer did not use (owner,
/// 2026-10-06), whatever its return notes did with the money at the time.
/// <paramref name="UnusedRent"/> is what its return notes gave back; <paramref name="AlreadyRaised"/>
/// what refund vouchers not cancelled already carry against it, by the contract or by one of its
/// notes; <paramref name="Payable"/> is the lesser of what is left and the customer's credit — the
/// same two limits a refund over one return note has.
/// </summary>
public record RefundableContractDto(
    Guid ContractId, string Number, Guid CustomerId, string CustomerName,
    decimal UnusedRent, decimal AlreadyRaised, decimal StillOwed,
    decimal CustomerCredit, decimal Payable);

/// <summary>The receipt's default "in exchange for" line for a set of invoices; null when none of them is rental.</summary>
public record ReceiptPurposeDto(string? Purpose);

/// <summary>
/// Raise a voucher. A <see cref="PaymentVoucherKind.CustomerRefund"/> names the customer and,
/// when it refunds unused rent, the return note; an <see cref="PaymentVoucherKind.Expense"/>
/// names the payee and the category (fuel | transport | repair | purchase | other).
/// </summary>
/// <param name="BranchId">
/// Honoured for the Admin only. For anyone else it is <b>ignored</b> and the voucher is raised at the
/// signed-in user's assigned branch (the main branch for a legacy row with none), read from their
/// user row — the cash leaves the box of the branch that pays, even for a refund over a return note
/// taken at another branch. Kept so an older client still binds. See <see cref="IRaisedAtBranch"/>.
/// </param>
public partial record CreatePaymentVoucherRequest(
    Guid BranchId, PaymentVoucherKind Kind, decimal Amount, string MethodKey,
    Guid? CustomerId = null, string? PayeeName = null, string? ExpenseCategory = null,
    string? Purpose = null, Guid? ReturnNoteId = null,
    string? InstrumentNumber = null, string? InstrumentBank = null, string? Notes = null,
    // An expense for a rental contract names it; required for the categories the company says.
    Guid? ContractId = null,
    // A supplier payment names the supplier it pays, instead of a free-text payee (owner, 2026-10-10).
    Guid? SupplierId = null,
    // A workers' bonus names each worker and their amount; the total is the voucher's amount (owner, 2026-11-08).
    IReadOnlyList<PaymentVoucherBonusLineRequest>? BonusLines = null,
    // Its payroll month, "yyyy-MM"; the company's current month when not given.
    string? PayrollMonth = null);

/// <summary>A rental contract as a voucher form offers it: its number, its customer and its return notes.</summary>
public record VoucherContractChoiceDto(
    Guid Id, string Number, Guid CustomerId, string? CustomerName, string Status,
    IReadOnlyList<VoucherReturnNoteChoiceDto> ReturnNotes);

public record VoucherReturnNoteChoiceDto(Guid Id, string Number, DateTime ReturnedAtUtc);

/// <summary>An expense category, the account it posts to, and whether its voucher must name a contract.</summary>
public record ExpenseCategoryDto(string Key, string AccountCode, bool ContractRequired);

/// <summary>The categories whose voucher must name a contract; every other category may still name one.</summary>
public record ExpenseCategoriesRequest(IReadOnlyList<string> ContractRequired);

/// <summary>The note that goes with an approval, a refusal, a payment or a cancellation.</summary>
public record PaymentVoucherDecisionRequest(string? Note);
