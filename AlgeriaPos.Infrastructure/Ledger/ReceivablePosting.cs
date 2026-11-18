using AlgeriaPos.Domain;

namespace AlgeriaPos.Infrastructure.Ledger;

/// <summary>One income line of a document, already netted: which account it belongs on, and how much.</summary>
public readonly record struct IncomeAmount(string AccountCode, decimal Net, string? Memo);

/// <summary>
/// What an invoice, a receipt and a credit note mean in the ledger.
///
/// <para>
/// Pure and static like <see cref="SalePosting"/>, <see cref="StockPosting"/> and
/// <see cref="RentalPosting"/>, for the same reason: these are the rules that decide whether the
/// debtor book is right, and a rule that can be checked without a server is a rule that gets
/// checked.
/// </para>
/// <para>
/// <b>The one fact this whole module hangs off:</b> a sale and a rental note already post their
/// own revenue and their own receivable the moment they are raised. An invoice built over one of
/// them is paper — a number, a due date, a presentation — and posts <b>nothing</b>. Only an
/// invoice raised directly is itself the economic event, and only that one posts. Get this wrong
/// and every invoiced sale reports its revenue twice and every contractor owes double.
/// </para>
/// </summary>
public static class ReceivablePosting
{
    /// <summary>
    /// Whether an invoice of this origin is itself the economic event and posts: one raised directly, or one raised from
    /// a customer maintenance job (Phase M5a), which posted nothing of its own. Over a sale or a rental note: never.
    /// </summary>
    public static bool PostsItself(InvoiceOrigin origin) => origin is InvoiceOrigin.Direct or InvoiceOrigin.MaintenanceJob;

    /// <summary>
    /// An invoice raised directly in the back office: the customer owes it, the company earned
    /// it, and the tax on it is collected on the state's behalf.
    /// </summary>
    /// <returns>
    /// Null for an invoice over a sale or a rental note — see the note on the class. Null too
    /// for an invoice that comes to nothing, which an entry with no lines would only clutter
    /// every report with.
    /// </returns>
    /// <param name="cost">
    /// What the invoice's goods cost (P0.3): Σ quantity × the cost per unit each line left stock at, 0 when no line took
    /// goods out — the switch off (the default), lines that track no stock, or no cost recorded. Posted in the same entry,
    /// Dr 600 / Cr 310, as a till sale posts its own; nothing at 0, so the entry is exactly today's.
    /// </param>
    public static PostingDraft? Invoice(
        Invoice invoice, IEnumerable<IncomeAmount> income, string? by, decimal cost = 0m)
    {
        if (!PostsItself(invoice.Origin)) return null;

        var draft = new PostingDraft
        {
            EntryDate = invoice.IssuedAtUtc,
            BranchId = invoice.BranchId,
            SourceType = LedgerSources.Invoice,
            SourceId = invoice.Id,
            Description = $"Invoice {invoice.Number}",
            CreatedBy = by
        };

        // The customer is billed the whole document, tax included; only the net is income. The
        // gross is what the business chases and what the statement has to show, so it is the
        // gross that lands on 411.
        draft.AddIfAny(PostingLine.Signed(
            LedgerAccounts.Customers, invoice.GrandTotalTtc,
            memo: invoice.Number, customerId: invoice.CustomerId));

        foreach (var line in income)
            draft.AddIfAny(PostingLine.Signed(line.AccountCode, -line.Net, memo: line.Memo ?? invoice.Number));

        // Zero on an untaxed invoice, so no line appears at all and it stays out of the tax
        // report by construction rather than by a filter someone has to remember (§5.7).
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.TaxCollected, -invoice.TaxTotal, memo: invoice.Number));

        // The goods leave stock and become an expense the moment they are invoiced (P0.3), as at the till.
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.CostOfGoodsSold, cost, memo: invoice.Number));
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Inventory, -cost, memo: invoice.Number));

        return draft.Lines.Count == 0 ? null : draft;
    }

    /// <summary>
    /// Money actually received from a customer, against their account.
    ///
    /// <para>
    /// ⚠ <b>Only ever called for a confirmed voucher.</b> A pending cheque is paper: it reduces
    /// no debt, settles no invoice and reaches no account until it clears (§5.4). There is
    /// deliberately no posting for taking one in — the alternative, moving the debt from 411 to
    /// 413 the moment the paper arrives, would drop the customer's trade balance and make the
    /// invoice look paid, which is the exact failure this rule exists to prevent.
    /// </para>
    /// <para>
    /// It is not income either. The revenue was recognised when the goods were sold or hired;
    /// this is a receivable turning into money, and the P&amp;L does not move.
    /// </para>
    /// </summary>
    public static PostingDraft? Receipt(ReceiptVoucher voucher, string? by)
    {
        if (voucher.Status != PaymentStatus.Confirmed || voucher.Amount == 0) return null;

        // Cash lands in the branch's box and nowhere else; a card, a transfer or a cleared
        // cheque is money at the bank, which is exactly why none of them may be remitted.
        var account = voucher.Behavior == PaymentBehavior.Cash ? LedgerAccounts.Cash : LedgerAccounts.Bank;

        var draft = new PostingDraft
        {
            EntryDate = voucher.ReceivedAtUtc,
            BranchId = voucher.BranchId,
            SourceType = LedgerSources.CustomerReceipt,
            SourceId = voucher.Id,
            Description = $"Receipt {voucher.Number} — {voucher.MethodKey}",
            CreatedBy = by
        }
            .AddIfAny(PostingLine.Dr(account, voucher.Amount, memo: voucher.Reference ?? voucher.Number));

        // M7b: an advance (only ever taken while the accountant holds advances on 419) waits on 419 until it is applied
        // to an invoice. Its VAT, when the accountant made it due on receipt and the seller ticked it, goes to 4457 now;
        // the line carries the customer so their statement still reads the whole sum they handed over.
        if (voucher.Kind == ReceiptKind.Advance)
            return draft
                .AddIfAny(PostingLine.Cr(
                    LedgerAccounts.CustomerDeposits, voucher.Amount - voucher.AdvanceTaxAmount,
                    memo: voucher.Number, customerId: voucher.CustomerId))
                .AddIfAny(PostingLine.Cr(
                    LedgerAccounts.TaxCollected, voucher.AdvanceTaxAmount,
                    memo: voucher.Number, customerId: voucher.CustomerId));

        return draft.AddIfAny(PostingLine.Cr(
            LedgerAccounts.Customers, voucher.Amount,
            memo: voucher.Number, customerId: voucher.CustomerId));
    }

    /// <summary>
    /// Part of an advance held on 419 moved to the customer's account (M7b): Dr 419 for the part net of its VAT, Dr 4457
    /// for the advance's VAT released with it (that VAT was declared when the advance came in; the invoice it pays declares
    /// the whole invoice's VAT again, so this is what keeps the same tax from being counted twice), Cr 411 for the whole
    /// part. Every line is the customer's, so their statement nets the entry to nothing: the money was theirs before and
    /// after. Balanced by construction.
    /// </summary>
    public static PostingDraft? AdvanceApplication(
        CustomerAdvanceApplication application, ReceiptVoucher advance, string? invoiceNumber, string? by)
    {
        if (application.Amount <= 0) return null;
        var memo = invoiceNumber is null ? advance.Number : $"{advance.Number} → {invoiceNumber}";
        return new PostingDraft
        {
            EntryDate = application.AppliedAtUtc,
            BranchId = advance.BranchId,
            SourceType = LedgerSources.CustomerAdvanceApplication,
            SourceId = application.Id,
            Description = invoiceNumber is null
                ? $"Advance {advance.Number} released to the customer's account"
                : $"Advance {advance.Number} applied to invoice {invoiceNumber}",
            CreatedBy = by
        }
            .AddIfAny(PostingLine.Dr(
                LedgerAccounts.CustomerDeposits, application.Amount - application.TaxAmount,
                memo: memo, customerId: advance.CustomerId))
            .AddIfAny(PostingLine.Dr(
                LedgerAccounts.TaxCollected, application.TaxAmount, memo: memo, customerId: advance.CustomerId))
            .AddIfAny(PostingLine.Cr(
                LedgerAccounts.Customers, application.Amount, memo: memo, customerId: advance.CustomerId));
    }

    /// <summary>
    /// Part of a customer's money kept on a cancelled order (SAL-13), moved to the income account the accountant chose.
    /// <list type="bullet">
    /// <item>From an advance on 419: Dr 419 for the part net of its VAT / Cr income. Its VAT part stays on 4457 — a kept
    /// advance is consideration, so its VAT stays due and is not given back as an application gives it back — but moves off
    /// the customer (Dr 4457 with the customer / Cr 4457 without): the customer no longer has that money with us, and their
    /// statement reads the advance's 4457 lines.</item>
    /// <item>From a receipt's unallocated credit on 411: Dr 411 / Cr income.</item>
    /// </list>
    /// The 411, 419, 4457 (customer's) and income lines carry the customer. Dated when it was kept. Balanced by construction.
    /// </summary>
    public static PostingDraft? AdvanceKept(CustomerAdvanceApplication kept, ReceiptVoucher receipt, string? by)
    {
        if (kept.Amount <= 0 || kept.IncomeAccountCode is null) return null;
        var memo = receipt.Number;
        var draft = new PostingDraft
        {
            EntryDate = kept.AppliedAtUtc,
            BranchId = receipt.BranchId,
            SourceType = LedgerSources.CustomerAdvanceKept,
            SourceId = kept.Id,
            Description = $"Advance kept — {kept.Reason}",
            CreatedBy = by
        };
        if (receipt.Kind == ReceiptKind.Advance)
            draft
                .AddIfAny(PostingLine.Dr(
                    LedgerAccounts.CustomerDeposits, kept.Amount - kept.TaxAmount, memo: memo, customerId: receipt.CustomerId))
                .AddIfAny(PostingLine.Dr(LedgerAccounts.TaxCollected, kept.TaxAmount, memo: memo, customerId: receipt.CustomerId))
                .AddIfAny(PostingLine.Cr(LedgerAccounts.TaxCollected, kept.TaxAmount, memo: memo));
        else
            draft.AddIfAny(PostingLine.Dr(LedgerAccounts.Customers, kept.Amount, memo: memo, customerId: receipt.CustomerId));
        return draft.AddIfAny(PostingLine.Cr(
            kept.IncomeAccountCode, kept.Amount - kept.TaxAmount, memo: memo, customerId: receipt.CustomerId));
    }

    /// <summary>
    /// An invoice reversed, in whole or in part.
    ///
    /// <para>
    /// This posts whatever raised the invoice, and that is not an inconsistency with
    /// <see cref="Invoice"/>: the revenue being given back is in the ledger either way — put
    /// there by the sale, by the rental note, or by the invoice itself — and giving it back is a
    /// real event on the day it happens. It comes off the account it went on to, which is why
    /// the income account is carried on the invoice line and inherited by the credit note line
    /// rather than assumed to be merchandise sales.
    /// </para>
    /// </summary>
    /// <param name="returnedCost">
    /// What the goods this note brought back into stock had cost (P0.3): Σ quantity returned × the cost their invoice
    /// line took them out at. Posted in the same entry, Dr 310 / Cr 600; nothing at 0 (a credit of money only).
    /// </param>
    public static PostingDraft? CreditNote(
        CreditNote note, string invoiceNumber, IEnumerable<IncomeAmount> income, string? by, decimal returnedCost = 0m)
    {
        var draft = new PostingDraft
        {
            EntryDate = note.IssuedAtUtc,
            BranchId = note.BranchId,
            SourceType = LedgerSources.CreditNote,
            SourceId = note.Id,
            Description = $"Credit note {note.Number} against {invoiceNumber}",
            CreatedBy = by
        };

        foreach (var line in income)
            draft.AddIfAny(PostingLine.Signed(line.AccountCode, line.Net, memo: line.Memo ?? note.Number));

        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.TaxCollected, note.TaxTotal, memo: note.Number));
        draft.AddIfAny(PostingLine.Signed(
            LedgerAccounts.Customers, -note.GrandTotalTtc,
            memo: note.Number, customerId: note.CustomerId));

        // The goods are back on the shelf: out of the expense and into stock, at what they left at.
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Inventory, returnedCost, memo: note.Number));
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.CostOfGoodsSold, -returnedCost, memo: note.Number));

        return draft.Lines.Count == 0 ? null : draft;
    }
}
