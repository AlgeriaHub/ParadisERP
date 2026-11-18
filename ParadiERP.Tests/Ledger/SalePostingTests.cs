using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;

namespace ParadiERP.Tests.Ledger;

/// <summary>
/// What a sale means in the ledger. These matter more than the rest put together: a sale is
/// the only document a till raises, it arrives unattended and possibly twice, and it is the
/// one posting that must never be allowed to fail — a sale that physically happened in the
/// shop cannot be refused after the fact.
/// </summary>
public class SalePostingTests
{
    private static readonly Guid Branch = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Cement = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>1 000 net + 190 tax, one line of cement at a cost of 700.</summary>
    private static Sale Sale(
        decimal grandTotal = 1190m, decimal tax = 190m,
        SaleStatus status = SaleStatus.Completed, TaxMode taxMode = TaxMode.Taxed,
        decimal change = 0m, decimal rounding = 0m, Guid? customerId = null,
        params SalePayment[] payments) => new()
    {
        Id = Guid.NewGuid(),
        Reference = "T-0001",
        BranchId = Branch,
        SoldAtUtc = new DateTime(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc),
        CashierName = "Caissier 1",
        CustomerId = customerId,
        Status = status,
        TaxMode = taxMode,
        GrandTotalTtc = grandTotal,
        TaxTotal = tax,
        SubTotalHt = grandTotal - tax,
        ChangeAmount = change,
        RoundingAdjustment = rounding,
        PaidAmount = payments.Sum(p => p.Amount),
        Lines = [new SaleLine { ProductId = Cement, Sku = "CIM50", Qty = 10m, LineTotalTtc = grandTotal, LineTaxAmount = tax }],
        Payments = [.. payments]
    };

    private static SalePayment Tender(
        PaymentBehavior behavior, decimal amount, PaymentStatus status = PaymentStatus.Confirmed) =>
        new() { Id = Guid.NewGuid(), Behavior = behavior, Amount = amount, Status = status, MethodKey = behavior.ToString().ToLowerInvariant() };

    private static readonly Dictionary<Guid, decimal> Costs = new() { [Cement] = 70m };

    /// <summary>Net movement per account code, the way a trial balance would see it.</summary>
    private static Dictionary<string, decimal> Balances(PostingDraft draft) => draft.Lines
        .GroupBy(l => l.AccountCode)
        .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));

    private static void AssertBalances(PostingDraft draft)
    {
        Assert.Equal(draft.Lines.Sum(l => l.Debit), draft.Lines.Sum(l => l.Credit));
        Assert.All(draft.Lines, l => Assert.True(l.Debit >= 0 && l.Credit >= 0));
        Assert.All(draft.Lines, l => Assert.True(l.Debit == 0 || l.Credit == 0));
    }

    [Fact]
    public void A_cash_sale_splits_into_revenue_tax_cash_and_the_cost_of_what_left()
    {
        var draft = SalePosting.Build(Sale(payments: Tender(PaymentBehavior.Cash, 1190m)), Costs);
        AssertBalances(draft);
        var balances = Balances(draft);

        // Revenue is net of tax: the 190 is the state's, not the company's, and reporting it
        // as turnover overstates every margin computed from it.
        Assert.Equal(-1000m, balances[LedgerAccounts.Sales]);
        Assert.Equal(-190m, balances[LedgerAccounts.TaxCollected]);
        Assert.Equal(1190m, balances[LedgerAccounts.Cash]);

        // And the goods left: 10 bags at 70. Without this the period shows revenue against no
        // cost and every month looks like pure margin.
        Assert.Equal(700m, balances[LedgerAccounts.CostOfGoodsSold]);
        Assert.Equal(-700m, balances[LedgerAccounts.Inventory]);
    }

    [Fact]
    public void Change_handed_back_came_out_of_the_same_drawer()
    {
        // 1 500 tendered against 1 190, 310 back. The drawer gained the net, which is what the
        // Z-report reconciles against; counting the 1 500 would show a permanent surplus.
        var draft = SalePosting.Build(Sale(change: 310m, payments: Tender(PaymentBehavior.Cash, 1500m)), Costs);
        AssertBalances(draft);
        Assert.Equal(1190m, Balances(draft)[LedgerAccounts.Cash]);
    }

    [Fact]
    public void An_untaxed_invoice_carries_no_tax_line_at_all()
    {
        var draft = SalePosting.Build(
            Sale(grandTotal: 1000m, tax: 0m, taxMode: TaxMode.Untaxed, payments: Tender(PaymentBehavior.Cash, 1000m)),
            Costs);
        AssertBalances(draft);

        // Not a zero row: the account simply does not appear, so the tax report and the
        // liability both stay clean rather than accumulating rows that mean nothing.
        Assert.DoesNotContain(draft.Lines, l => l.AccountCode == LedgerAccounts.TaxCollected);
        Assert.Equal(-1000m, Balances(draft)[LedgerAccounts.Sales]);
    }

    [Fact]
    public void A_credit_sale_raises_a_receivable_rather_than_cash()
    {
        var customer = Guid.NewGuid();
        var draft = SalePosting.Build(
            Sale(customerId: customer, payments: Tender(PaymentBehavior.Credit, 1190m)), Costs);
        AssertBalances(draft);
        var balances = Balances(draft);

        Assert.Equal(1190m, balances[LedgerAccounts.Customers]);
        Assert.DoesNotContain(draft.Lines, l => l.AccountCode == LedgerAccounts.Cash);
        // Revenue is recognised all the same: the goods went out and the invoice is raised.
        Assert.Equal(-1000m, balances[LedgerAccounts.Sales]);
    }

    [Fact]
    public void A_pure_credit_sale_with_no_payment_line_still_lands_on_the_customer()
    {
        // Older tills send only PaymentMethod = Credit and no payment line to match. The
        // general answer is the residual, not a special case bolted on beside it.
        var sale = Sale(customerId: Guid.NewGuid());
        sale.PaymentMethod = PaymentMethod.Credit;

        var draft = SalePosting.Build(sale, Costs);
        AssertBalances(draft);
        Assert.Equal(1190m, Balances(draft)[LedgerAccounts.Customers]);
    }

    [Fact]
    public void A_cheque_at_the_counter_is_paper_and_not_money()
    {
        var draft = SalePosting.Build(
            Sale(customerId: Guid.NewGuid(),
                 payments: Tender(PaymentBehavior.Generic, 1190m, PaymentStatus.Pending)), Costs);
        AssertBalances(draft);
        var balances = Balances(draft);

        // It sits in notes receivable until it clears, which is also why the customer still
        // counts as unpaid. Treating it as cash would put it in a drawer nobody can count and
        // let a credit limit be extended against money that never arrived.
        Assert.Equal(1190m, balances[LedgerAccounts.NotesReceivable]);
        Assert.DoesNotContain(draft.Lines, l => l.AccountCode == LedgerAccounts.Cash);
        Assert.DoesNotContain(draft.Lines, l => l.AccountCode == LedgerAccounts.Bank);
    }

    [Fact]
    public void Spending_a_gift_card_settles_a_liability_rather_than_taking_money()
    {
        var draft = SalePosting.Build(
            Sale(payments: [Tender(PaymentBehavior.GiftCard, 500m), Tender(PaymentBehavior.Cash, 690m)]), Costs);
        AssertBalances(draft);
        var balances = Balances(draft);

        Assert.Equal(500m, balances[LedgerAccounts.GiftCardLiability]);
        Assert.Equal(690m, balances[LedgerAccounts.Cash]);
        Assert.Equal(-1000m, balances[LedgerAccounts.Sales]);
    }

    [Fact]
    public void Cash_rounding_gets_its_own_line_instead_of_bending_the_tax_figures()
    {
        // 1 190 becomes 1 190.4 to pay under a 0.5 rounding step. The 0.4 is not revenue and
        // certainly not tax; folded into either, the declared figures drift from the invoice
        // the customer checked.
        var draft = SalePosting.Build(
            Sale(rounding: 0.4m, payments: Tender(PaymentBehavior.Cash, 1190.4m)), Costs);
        AssertBalances(draft);
        var balances = Balances(draft);

        Assert.Equal(-0.4m, balances[LedgerAccounts.RoundingDifference]);
        Assert.Equal(-190m, balances[LedgerAccounts.TaxCollected]);
        Assert.Equal(1190.4m, balances[LedgerAccounts.Cash]);
    }

    [Fact]
    public void A_refund_is_the_same_document_with_both_sides_swapped()
    {
        var draft = SalePosting.Build(
            Sale(status: SaleStatus.Refunded, payments: Tender(PaymentBehavior.Cash, 1190m)), Costs);
        AssertBalances(draft);
        var balances = Balances(draft);

        Assert.Equal(1000m, balances[LedgerAccounts.Sales]);        // revenue given back
        Assert.Equal(190m, balances[LedgerAccounts.TaxCollected]);
        Assert.Equal(-1190m, balances[LedgerAccounts.Cash]);        // money out of the drawer
        Assert.Equal(700m, balances[LedgerAccounts.Inventory]);     // goods back on the shelf
        Assert.Equal(-700m, balances[LedgerAccounts.CostOfGoodsSold]);
    }

    [Fact]
    public void A_sale_whose_tenders_do_not_add_up_still_balances_and_says_so()
    {
        // The till is offline, unattended and occasionally wrong, and the customer has already
        // paid and left. Refusing the record would lose the sale; silently absorbing the
        // difference into revenue would corrupt the one number the business is judged on. So
        // it lands in suspense, where a trial balance shows it as something to investigate.
        var draft = SalePosting.Build(Sale(payments: Tender(PaymentBehavior.Cash, 900m)), Costs);
        AssertBalances(draft);
        var balances = Balances(draft);

        Assert.Equal(290m, balances[LedgerAccounts.Suspense]);
        Assert.Equal(-1000m, balances[LedgerAccounts.Sales]);
        Assert.Equal(900m, balances[LedgerAccounts.Cash]);
    }

    [Fact]
    public void A_service_with_no_cost_recorded_posts_no_cost_of_sales()
    {
        // Concrete pumping holds no stock and has no cost price. Inventing one would be worse
        // than leaving the margin unstated.
        var draft = SalePosting.Build(Sale(payments: Tender(PaymentBehavior.Cash, 1190m)), new Dictionary<Guid, decimal>());
        AssertBalances(draft);
        Assert.DoesNotContain(draft.Lines, l => l.AccountCode is LedgerAccounts.CostOfGoodsSold or LedgerAccounts.Inventory);
    }

    [Fact]
    public void Every_shape_a_till_can_push_produces_an_entry_that_balances()
    {
        // The property that matters most: whatever arrives, the entry adds up, because the
        // alternative is a push that fails and a day's takings stuck in an outbox.
        SalePayment[][] shapes =
        [
            [Tender(PaymentBehavior.Cash, 1190m)],
            [Tender(PaymentBehavior.Card, 1190m)],
            [Tender(PaymentBehavior.Cash, 500m), Tender(PaymentBehavior.Card, 690m)],
            [Tender(PaymentBehavior.Credit, 1190m)],
            [Tender(PaymentBehavior.Loyalty, 1190m)],
            [Tender(PaymentBehavior.Generic, 1190m, PaymentStatus.Pending)],
            [Tender(PaymentBehavior.Generic, 1190m, PaymentStatus.Bounced)],
            [Tender(PaymentBehavior.Cash, 0m)],
            []
        ];

        foreach (var shape in shapes)
        {
            AssertBalances(SalePosting.Build(Sale(customerId: Guid.NewGuid(), payments: shape), Costs));
            AssertBalances(SalePosting.Build(Sale(payments: shape), Costs));
            AssertBalances(SalePosting.Build(Sale(status: SaleStatus.Refunded, payments: shape), Costs));
        }
    }
}
