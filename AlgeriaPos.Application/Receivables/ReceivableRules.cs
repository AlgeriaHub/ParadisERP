using AlgeriaPos.Domain;

namespace AlgeriaPos.Application.Receivables;

/// <summary>
/// What one invoice still owes, from the four figures that decide it.
/// </summary>
/// <param name="Total">What the document says, tax included.</param>
/// <param name="Prepaid">Settled on the source document before the invoice existed. Never allocated.</param>
/// <param name="Credited">Given back on credit notes.</param>
/// <param name="Received">Allocated from vouchers that have been <b>confirmed</b>.</param>
/// <param name="Claimed">Allocated from vouchers still pending — spoken for, not paid.</param>
public readonly record struct InvoiceStanding(
    decimal Total, decimal Prepaid, decimal Credited, decimal Received, decimal Claimed)
{
    /// <summary>
    /// What the customer still owes on this invoice.
    ///
    /// <para>
    /// ⚠ <paramref name="Claimed"/> is deliberately absent. A cheque that has been handed over
    /// and written against this invoice has not paid it: until the money clears the debt is
    /// exactly what it was (§5.4). Subtracting paper here is how an invoice comes to look paid
    /// because paper arrived, and how a credit limit ends up extended on money that never came.
    /// </para>
    /// </summary>
    public decimal Outstanding => Total - Prepaid - Credited - Received;

    /// <summary>
    /// What may still be allocated to this invoice — outstanding, less what pending vouchers
    /// have already spoken for. Without the second term two cheques could each be written
    /// against the whole of the same invoice and both look legitimate.
    /// </summary>
    public decimal Allocatable => Outstanding - Claimed;

    public bool IsSettled => Outstanding <= 0;

    /// <summary>
    /// What a screen calls this invoice. Derived, never stored: a status column and a set of
    /// payments are two things that must agree, and eventually will not.
    /// </summary>
    public string Status =>
        Credited >= Total - Prepaid && Credited > 0 ? "Credited"
        : IsSettled ? "Settled"
        : Received + Credited > 0 ? "PartlyPaid"
        : "Open";
}

/// <summary>
/// The rules a receivable document has to satisfy, expressed without a database so they can be
/// checked on their own — the same split the rental module makes between its engine and the
/// service that feeds it real documents.
///
/// <para>
/// Note what is <b>not</b> here: nothing refuses a document because of a credit limit. A limit
/// is a warning in this system, never a block (CLAUDE.md, rule one), and the place that belief
/// would quietly get reversed is a validation helper like this one.
/// </para>
/// </summary>
public static class ReceivableRules
{
    /// <summary>Why this invoice cannot be raised, or null.</summary>
    public static string? InvoiceProblem(int lineCount, DateTime issuedAtUtc, DateTime dueDateUtc)
    {
        if (lineCount <= 0) return "An invoice needs at least one line.";
        // Due before it was issued is a typo, and it would land the invoice in an overdue band
        // the moment it was raised.
        if (dueDateUtc.Date < issuedAtUtc.Date) return "The due date cannot fall before the invoice date.";
        return null;
    }

    /// <summary>The longest a hire line may be billed for in one go: ten years of days.</summary>
    public const int MaxLineDays = 3660;

    /// <summary>Why this line cannot be billed, or null.</summary>
    /// <param name="days">The days a hire line is billed for; null is a line not priced by the day.</param>
    public static string? LineProblem(decimal qty, decimal unitPrice, decimal discount, decimal taxRate, int? days = null)
    {
        if (qty <= 0) return "A quantity above zero is required.";
        if (days is < 1 or > MaxLineDays) return $"Days must be a whole number from 1 to {MaxLineDays}.";
        // A negative price on an invoice line is a credit note written in the wrong document.
        if (unitPrice < 0) return "A price cannot be negative; reverse an invoice with a credit note.";
        if (discount < 0 || discount > LineGross(qty, unitPrice, days)) return "A line discount cannot exceed the line.";
        if (taxRate is < 0 or > 100) return "A tax rate must be between 0 and 100.";
        return null;
    }

    /// <summary>
    /// A line before its discount: quantity × price, times the days when it is priced by the day.
    /// Unrounded — the caller rounds once, after the discount, as the rental engine does.
    /// </summary>
    public static decimal LineGross(decimal qty, decimal unitPrice, int? days) => qty * unitPrice * (days ?? 1);

    /// <summary>Why this much cannot be allocated to that invoice, or null.</summary>
    public static string? AllocationProblem(decimal amount, InvoiceStanding standing)
    {
        if (amount <= 0) return "An allocation must be above zero.";
        if (amount > standing.Allocatable)
            return $"Only {standing.Allocatable:0.###} of this invoice is still open to allocation.";
        return null;
    }

    /// <summary>The refusal a bounce with no reason gets, word for word on both settle routes.</summary>
    public const string BounceNeedsReason = "Say why it bounced: the bank's reason is required.";

    /// <summary>
    /// Why a cheque or a transfer cannot be settled this way, or null (FR-2B, the owner,
    /// 2026-10-03). A bounce must say why — the bank's reason, in the existing note — because it
    /// is the one settlement that leaves a debt standing and somebody will have to chase it.
    /// Collecting needs nothing more than the fact, as before.
    /// </summary>
    public static string? SettlementProblem(PaymentStatus target, string? note) =>
        target == PaymentStatus.Bounced && string.IsNullOrWhiteSpace(note) ? BounceNeedsReason : null;

    /// <summary>Why this much cannot be credited off that line, or null.</summary>
    public static string? CreditProblem(decimal qty, decimal alreadyCredited, decimal invoicedQty)
    {
        if (qty <= 0) return "A credited quantity must be above zero.";
        if (alreadyCredited + qty > invoicedQty)
            return $"Only {invoicedQty - alreadyCredited:0.###} of this line is left to credit.";
        return null;
    }
}
