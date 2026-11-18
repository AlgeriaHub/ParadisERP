using ParadisERP.Domain;

namespace ParadisERP.Infrastructure.Ledger;

/// <summary>One line of a stock document: what moved, and what it is worth.</summary>
/// <param name="Account">
/// The account a receipt debits instead of 310: a hire product's asset account while the fixed-asset register holds it
/// (REN-12, Phase M7a). Null — every other line, and every line while the register is off — is 310, as always.
/// </param>
public readonly record struct ValuedLine(string? Sku, decimal Value, string? Account = null);

/// <summary>
/// What each stock document means in the ledger.
///
/// <para>
/// Pure and static, like <see cref="SalePosting"/>, and for the same reason: these are the
/// rules that decide whether a first period shows a cost that never happened, and a rule that
/// can be checked without a server is a rule that gets checked. The endpoints do the database
/// work and call in here for the accounting.
/// </para>
/// </summary>
public static class StockPosting
{
    /// <summary>
    /// Goods entering the company at the branch that receives them. Inventory up, against the supplier.
    /// <para>
    /// Against payables rather than cash, deliberately: nothing in this system yet records
    /// paying a supplier, and debiting cash here would move a figure the branch's cash box
    /// never saw. The ledger and the cash position would then disagree with no document
    /// between them to explain why.
    /// </para>
    /// </summary>
    public static PostingDraft Receiving(
        Guid documentId, Guid branchId, string? reference, IEnumerable<ValuedLine> lines, Guid? supplierId = null)
    {
        var draft = new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = branchId,
            SourceType = LedgerSources.Receiving,
            SourceId = documentId,
            Description = string.IsNullOrWhiteSpace(reference) ? "Receiving" : $"Receiving {reference}"
        };

        var total = 0m;
        foreach (var line in lines)
        {
            total += line.Value;
            draft.AddIfAny(PostingLine.Dr(line.Account ?? LedgerAccounts.Inventory, line.Value, memo: line.Sku));
        }
        // The supplier, when the note names one, rides on the 401 side: that is what puts the
        // delivery on their statement. The reference is the memo the statement shows as its number.
        draft.AddIfAny(PostingLine.Cr(LedgerAccounts.Suppliers, total,
            memo: string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), supplierId: supplierId));
        return draft;
    }

    /// <summary>
    /// What a branch already held on the day the company adopted the system.
    ///
    /// <para>
    /// ⚠ <b>Against opening-balance equity — never a supplier, never cost of sales, never
    /// revenue.</b> These goods were bought years ago by a company that was not using this
    /// system. Post the entry as a purchase and the first period shows a cost that never
    /// occurred; post it as income and it shows a profit that never happened (design §5.3).
    /// This is the same class of error as treating a remittance as income.
    /// </para>
    /// </summary>
    public static PostingDraft Opening(
        Guid documentId, Guid branchId, string branchName, IEnumerable<ValuedLine> lines)
    {
        var draft = new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = branchId,
            SourceType = LedgerSources.OpeningBalance,
            SourceId = documentId,
            Description = $"Opening balance — {branchName}"
        };

        var total = 0m;
        foreach (var line in lines)
        {
            total += line.Value;
            // Signed, because restating an opening line entered wrong is a correction
            // downwards, not a second helping of stock.
            draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Inventory, line.Value, memo: line.Sku));
        }
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.OpeningBalance, -total));
        return draft;
    }

    /// <summary>
    /// A quantity moving between states.
    ///
    /// <para>
    /// Returns null when nothing accounting-relevant happened. Available, on-rent and
    /// in-maintenance stock all sit in the same asset account — it is the same merchandise in
    /// a different place, and the quantity buckets carry that distinction. Only crossing into
    /// scrap, or out of the business altogether, is an accounting event.
    /// </para>
    /// </summary>
    public static PostingDraft? StateChange(
        Guid documentId, Guid branchId, StockState from, StockState to, decimal value, string? sku, string? reason)
    {
        var fromAccount = LedgerAccounts.ForStockState(from);
        var toAccount = LedgerAccounts.ForStockState(to);
        if (fromAccount == toAccount) return null;

        return new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = branchId,
            SourceType = LedgerSources.StockState,
            SourceId = documentId,
            Description = $"{from} → {to}" + (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")
        }
            .AddIfAny(PostingLine.Dr(toAccount, value, memo: sku))
            .AddIfAny(PostingLine.Cr(fromAccount, value, memo: sku));
    }

    /// <summary>
    /// Goods moving between two of the company's own branches: the same asset account at two
    /// different branches, marked inter-branch so consolidation drops it.
    /// <para>
    /// Valued at cost and booked as a purchase by the receiving branch it would be counted
    /// twice and group profit would come out larger than reality — the single most common
    /// error in multi-branch reporting (design §5.10). Nothing here reaches the P&amp;L.
    /// </para>
    /// </summary>
    public static PostingDraft Transfer(
        Guid documentId, Guid fromBranchId, Guid toBranchId, string fromName, string toName, decimal value, string? sku) =>
        new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = fromBranchId,
            SourceType = LedgerSources.StockTransfer,
            SourceId = documentId,
            Description = $"Transfer {fromName} → {toName}",
            IsInterBranch = true,
            CounterpartBranchId = toBranchId
        }
            .AddIfAny(PostingLine.Dr(LedgerAccounts.Inventory, value, toBranchId, sku))
            .AddIfAny(PostingLine.Cr(LedgerAccounts.Inventory, value, fromBranchId, sku));

    /// <summary>
    /// Goods arrived at the destination branch: the inter-branch move of what arrived.
    /// Dispatch and cancel post nothing (in-transit goods stay in 310 at the source);
    /// receipt is the inter-branch move of what arrived.
    /// </summary>
    public static PostingDraft TransferReceipt(Guid eventId, Guid fromBranchId, Guid toBranchId,
        string fromName, string toName, string number, IEnumerable<ValuedLine> lines)
    {
        var draft = new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = fromBranchId,
            SourceType = LedgerSources.StockTransfer,
            SourceId = eventId,
            Description = $"Transfer {number} {fromName} → {toName}",
            IsInterBranch = true,
            CounterpartBranchId = toBranchId
        };
        foreach (var line in lines)
        {
            draft.AddIfAny(PostingLine.Dr(LedgerAccounts.Inventory, line.Value, toBranchId, line.Sku));
            draft.AddIfAny(PostingLine.Cr(LedgerAccounts.Inventory, line.Value, fromBranchId, line.Sku));
        }
        return draft;
    }

    /// <summary>
    /// Goods declared lost in transit, written off at the branch that dispatched them.
    /// A real loss for the group so it is not inter-branch.
    /// </summary>
    public static PostingDraft TransferLoss(Guid eventId, Guid fromBranchId, string number,
        IEnumerable<ValuedLine> lines, string reason)
    {
        var desc = $"Lost in transit {number}: {reason}";
        if (desc.Length > 400) desc = desc[..400];

        var draft = new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = fromBranchId,
            SourceType = LedgerSources.StockTransferLoss,
            SourceId = eventId,
            Description = desc
        };
        foreach (var line in lines)
        {
            draft.AddIfAny(PostingLine.Dr(LedgerAccounts.StockLoss, line.Value, memo: line.Sku));
            draft.AddIfAny(PostingLine.Cr(LedgerAccounts.Inventory, line.Value, memo: line.Sku));
        }
        return draft;
    }

    /// <summary>
    /// A stocktake, breakage or correction — a real gain or loss, not a bookkeeping tidy-up.
    /// Left unposted the inventory account drifts away from the count it is supposed to
    /// describe, and the drift is invisible because nothing compares them.
    /// </summary>
    public static PostingDraft Adjustment(Guid documentId, Guid branchId, decimal value, string? sku, string? reason) =>
        new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = branchId,
            SourceType = LedgerSources.StockAdjustment,
            SourceId = documentId,
            Description = string.IsNullOrWhiteSpace(reason) ? "Stock adjustment" : $"Stock adjustment: {reason}"
        }
            .AddIfAny(PostingLine.Signed(LedgerAccounts.Inventory, value, memo: sku))
            .AddIfAny(PostingLine.Signed(LedgerAccounts.StockLoss, -value, memo: sku));
}

/// <summary>Where the money physically is, and whether it has actually arrived.</summary>
public static class TreasuryPosting
{
    /// <summary>
    /// The manager collecting a branch's takings for the central safe.
    ///
    /// <para>
    /// ⚠ <b>Not income at the main branch and not an expense at the branch.</b> It is cash
    /// moving between two asset locations, with the branch dimension carrying which is which;
    /// the revenue was recognised when the sale happened.
    /// </para>
    /// <para>
    /// The branch calls it <i>صادر</i> because money left its box, and that is the right word
    /// for a cash report — but <i>صادر/وارد</i> describes direction at a location while
    /// income and expense describe profit. They are different axes. Post a remittance to the
    /// P&amp;L and profit inflates on both sides while nothing real changed (design §5.8).
    /// </para>
    /// </summary>
    public static PostingDraft Remittance(
        Guid movementId, Guid fromBranchId, Guid toBranchId, string fromName, string toName,
        decimal amount, string? reference, string? by) =>
        new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = fromBranchId,
            SourceType = LedgerSources.Remittance,
            SourceId = movementId,
            Description = $"Remittance {fromName} → {toName}",
            CreatedBy = by,
            IsInterBranch = true,
            CounterpartBranchId = toBranchId
        }
            .AddIfAny(PostingLine.Dr(LedgerAccounts.Cash, amount, toBranchId, reference))
            .AddIfAny(PostingLine.Cr(LedgerAccounts.Cash, amount, fromBranchId, reference));

    /// <summary>
    /// A cheque or transfer clearing, or failing to.
    ///
    /// <para>
    /// ⚠ <b>A cheque clearing moves a receivable; it does not create revenue.</b> The sale was
    /// recognised when it happened, and recognising it again here would report the same money
    /// twice. Confirming turns paper into money at the bank. Bouncing turns it back into
    /// ordinary trade debt, because the customer still owes it — nothing arrived and nothing
    /// about their position changed. Either way the P&amp;L does not move.
    /// </para>
    /// </summary>
    /// <param name="customerId">
    /// Who handed the paper over. Both sides of a bounce belong to them — the debt leaves 413
    /// and lands on 411 without the customer's position changing at all — so the attribution has
    /// to survive the move or their statement would show the debt vanishing and reappearing
    /// under nobody's name.
    /// </param>
    public static PostingDraft Settlement(
        SalePayment payment, PaymentStatus target, Guid branchId, string? by, Guid? customerId = null) =>
        new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = branchId,
            SourceType = LedgerSources.PaymentSettlement,
            SourceId = payment.Id,
            Description = $"{target} — {payment.MethodKey} {payment.InstrumentNumber}".TrimEnd(),
            CreatedBy = by
        }
            .AddIfAny(PostingLine.Dr(
                target == PaymentStatus.Confirmed ? LedgerAccounts.Bank : LedgerAccounts.Customers,
                payment.Amount,
                // The bank side belongs to no customer; the trade-debt side does.
                customerId: target == PaymentStatus.Confirmed ? null : customerId))
            .AddIfAny(PostingLine.Cr(LedgerAccounts.NotesReceivable, payment.Amount, customerId: customerId));
}
