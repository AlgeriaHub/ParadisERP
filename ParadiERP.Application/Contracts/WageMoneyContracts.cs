namespace ParadiERP.Application.Contracts;

// ---------------------------------------------------------------------------------------------
// The money side of payroll — docs/07 §5.8 and §5.10.
//
// Deliberately short. `WageAdvanceDto`, `GrantWageAdvanceRequest`, `WagePaymentDto`,
// `PayWagesRequest`, `PayrollStatutoryPaymentDto` and `RecordStatutoryPaymentRequest` were all
// shipped with the payroll foundation and live in `Dtos.cs`; the routes below use those. What is
// here is only what paying and reversing turned out to need and the foundation did not carry.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Undoing a wage payment. The reason is required and is what the reversing ledger entry carries:
/// wages taken back with nothing said cannot be explained to the person they were paid to.
/// </summary>
public record ReverseWagePaymentRequest(string? Reason = null);

/// <summary>
/// A payment with the one fact the payment itself cannot hold: whether it has been reversed.
///
/// <para>
/// There is no <c>IsReversed</c> column and there should not be one — the reversing entry is the
/// record, and a flag beside it is a second thing that can be wrong. So the list reads it from the
/// ledger and says it here, in words, beside the payment rather than in place of it.
/// </para>
/// </summary>
public record WagePaymentRowDto(
    WagePaymentDto Payment,
    bool IsReversed,
    DateTime? ReversedAtUtc = null,
    string? ReversalReason = null);

/// <summary>
/// What one payroll liability still holds, read out of the ledger rather than from a column that
/// could drift from it.
/// </summary>
/// <param name="Accrued">Everything the approved runs put on it.</param>
/// <param name="Paid">Everything the statutory payments have taken off it.</param>
/// <param name="Outstanding">
/// The difference. docs/07 §5.14's risk read the other way round: a screen can only say "this is
/// settled from the bank outside the system" if it can first say how much is standing.
/// </param>
public record PayrollLiabilityDto(
    string AccountCode,
    string AccountName,
    Guid? BranchId,
    decimal Accrued,
    decimal Paid,
    decimal Outstanding);
