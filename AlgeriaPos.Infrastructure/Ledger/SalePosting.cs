using AlgeriaPos.Domain;

namespace AlgeriaPos.Infrastructure.Ledger;

/// <summary>
/// Turns a sale into its accounting entry.
///
/// <para>
/// Pure and static on purpose: what a sale means in the ledger is a rule, not a database
/// operation, and a rule that can be checked without a server is a rule that gets checked.
/// </para>
/// </summary>
public static class SalePosting
{
    /// <summary>
    /// Builds the entry for one pushed sale or refund.
    /// </summary>
    /// <param name="unitCosts">
    /// Cost per unit by product, for the cost-of-sales side. Missing or zero means no cost is
    /// known — a service line, or a product nobody has priced — and nothing is posted for it
    /// rather than a guess being invented.
    /// </param>
    /// <remarks>
    /// <b>This must never throw and must never fail to balance</b>, because a sale that
    /// physically happened in the shop cannot be refused (CLAUDE.md, "never block the sale").
    /// Anything that does not add up lands in suspense, where it shows on the trial balance as
    /// a thing to investigate instead of blocking a till or quietly distorting revenue.
    /// </remarks>
    /// <param name="roundingCap">
    /// The most a cash rounding may be (one step of the profile): beyond it the excess is posted to
    /// suspense rather than to the rounding account (security review, 2026-10-10).
    /// </param>
    public static PostingDraft Build(Sale sale, IReadOnlyDictionary<Guid, decimal> unitCosts, decimal roundingCap = decimal.MaxValue)
    {
        var isRefund = sale.Status == SaleStatus.Refunded;
        // A refund is the same document with both sides swapped. Expressing it as a sign
        // rather than a second set of rules means the two can never drift apart.
        var sign = isRefund ? -1m : 1m;

        var draft = new PostingDraft
        {
            EntryDate = sale.SoldAtUtc,
            BranchId = sale.BranchId,
            SourceType = LedgerSources.Sale,
            SourceId = sale.Id,
            Description = (isRefund ? "Refund " : "Sale ") + sale.Reference,
            CreatedBy = sale.CashierName
        };

        // ---------- what was earned ----------
        // Revenue is derived from the two figures the push validates against each other
        // (lines sum to the total, line tax sums to the declared tax) rather than from
        // SubTotalHt, which nothing checks. An order-level discount has already been spread
        // across the lines before it gets here, so this is net of it; DiscountTotal is
        // reported for analytics and is not an account.
        var revenue = sale.GrandTotalTtc - sale.TaxTotal;
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Sales, -sign * revenue, memo: sale.Reference));
        // An untaxed invoice carries no tax at all, so this is simply zero and no line appears.
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.TaxCollected, -sign * sale.TaxTotal));
        // Cash rounding is kept off the tax figures deliberately, so it needs its own home or
        // the entry is short by a few centimes on every rounded sale.
        var rounding = Math.Clamp(sale.RoundingAdjustment, -roundingCap, roundingCap);
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.RoundingDifference, -sign * rounding));
        // A rounding larger than a step is not rounding: whatever is beyond it waits in suspense.
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Suspense, -sign * (sale.RoundingAdjustment - rounding),
            memo: "Cash rounding beyond the rounding step"));

        // ---------- what was received ----------
        var tenders = Tenders(sale);
        // Change came out of the same drawer, so the drawer only gained the net. Mirrors the
        // cash-box movement exactly; if the two used different arithmetic the ledger and the
        // Z-report would disagree and there would be no way to say which was right.
        var cash = tenders.Cash - ChangeFromDrawer(sale, tenders.Cash);

        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Cash, sign * cash));
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Bank, sign * tenders.Bank));
        // Both third-party lines carry whose debt they are: without that the ledger cannot
        // answer "411 for this customer" and a statement could never be reconciled against it.
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Customers, sign * tenders.Credit, customerId: sale.CustomerId));
        // Paper, not money. A cheque handed over at the counter leaves the customer owing it
        // until it clears, which is why it is a receivable and not cash (design §5.4).
        draft.AddIfAny(PostingLine.Signed(
            LedgerAccounts.NotesReceivable, sign * tenders.Pending, customerId: sale.CustomerId));
        // Spending a gift card or points settles a liability the company already carries.
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.GiftCardLiability, sign * tenders.GiftCard));
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.LoyaltyLiability, sign * tenders.Loyalty));

        // Whatever the tenders did not cover. A pure credit sale often arrives with no credit
        // payment line at all — only PaymentMethod = Credit — so this is the general answer
        // rather than a special case, and it is a receivable when there is a customer to owe
        // it. With no customer there is nobody to bill and the difference is a fault, so it
        // goes to suspense where a trial balance will show it.
        var due = sale.GrandTotalTtc + sale.RoundingAdjustment;
        var settled = cash + tenders.Bank + tenders.Credit + tenders.Pending + tenders.GiftCard + tenders.Loyalty;
        var residual = Math.Round(due - settled, 3, MidpointRounding.AwayFromZero);
        if (residual != 0)
        {
            var billable = residual > 0 && sale.CustomerId is not null;
            draft.Add(PostingLine.Signed(
                billable ? LedgerAccounts.Customers : LedgerAccounts.Suspense,
                sign * residual,
                memo: residual > 0 && sale.CustomerId is null ? "Unsettled with no customer on the sale" : null,
                // Suspense belongs to nobody by definition: it is there precisely because there
                // is no customer to attribute it to.
                customerId: billable ? sale.CustomerId : null));
        }

        // ---------- what it cost ----------
        // Goods leave inventory and become an expense at the moment they are sold; a refund
        // brings them back. Without this the P&L shows revenue against no cost and every
        // period looks like pure margin.
        var cost = sale.Lines.Sum(l => l.Qty * unitCosts.GetValueOrDefault(l.ProductId));
        if (cost != 0)
        {
            draft.Add(PostingLine.Signed(LedgerAccounts.CostOfGoodsSold, sign * cost));
            draft.Add(PostingLine.Signed(LedgerAccounts.Inventory, -sign * cost));
        }

        return draft;
    }

    /// <summary>
    /// The change actually handed back out of the drawer.
    ///
    /// <para>
    /// Never negative, and never more than the cash that went into the drawer in the first
    /// place — a till declaring otherwise is describing something that cannot physically have
    /// happened, and before this was clamped a single pushed sale could drive a branch's whole
    /// recorded cash position negative by whatever figure it felt like sending.
    /// </para>
    /// <para>
    /// It lives here because it is needed twice, once for the cash box and once for this entry.
    /// If the two ever computed it differently the ledger and the Z-report would disagree with
    /// nothing to say which was right, which is the one thing this module exists to prevent.
    /// </para>
    /// </summary>
    public static decimal ChangeFromDrawer(Sale sale, decimal cashTaken) =>
        sale.Status == SaleStatus.Refunded ? 0m : Math.Clamp(sale.ChangeAmount, 0m, Math.Max(0m, cashTaken));

    /// <summary>
    /// Splits the payments into the accounts they land in. A partition, not a set of overlapping
    /// filters: every payment lands in exactly one bucket, or the entry counts money twice.
    /// </summary>
    private static (decimal Cash, decimal Bank, decimal Credit, decimal Pending, decimal GiftCard, decimal Loyalty)
        Tenders(Sale sale)
    {
        decimal cash = 0, bank = 0, credit = 0, pending = 0, giftCard = 0, loyalty = 0;

        foreach (var p in sale.Payments)
        {
            // A tender that has not cleared is not a payment yet, whatever it claims to be —
            // and a bounced one never arrived at all, so neither is money anywhere.
            if (p.Status != PaymentStatus.Confirmed && p.Behavior != PaymentBehavior.Credit)
            {
                if (p.Status == PaymentStatus.Pending) pending += p.Amount;
                continue;
            }

            switch (p.Behavior)
            {
                case PaymentBehavior.Cash: cash += p.Amount; break;
                // A card payment and a cleared transfer are both money at the bank, not in
                // the branch drawer — which is exactly why neither may be remitted.
                case PaymentBehavior.Card:
                case PaymentBehavior.Generic: bank += p.Amount; break;
                case PaymentBehavior.Credit: credit += p.Amount; break;
                case PaymentBehavior.GiftCard: giftCard += p.Amount; break;
                case PaymentBehavior.Loyalty: loyalty += p.Amount; break;
            }
        }

        return (cash, bank, credit, pending, giftCard, loyalty);
    }
}
