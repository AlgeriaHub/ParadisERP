using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// A pushed sale is trusted: it physically happened at a counter and the server may not refuse
/// it after the fact. That trust is about <i>whether</i> a sale is recorded — it was never
/// meant to extend to figures that describe something which cannot have happened.
///
/// <para>
/// The push validated quantities, the sale total and the tax, and stopped there. Three figures
/// it did not check each moved real money: a negative tender, a change amount larger than the
/// cash that went in the drawer, and <c>PaidAmount</c>, which nothing anywhere validates and
/// which decided how much a credit sale added to a customer's debt. For a supplier whose
/// receivables are the business, the last one is the expensive one — one push from a till in
/// the wrong hands could clear an account outright, and the ledger, which never reads
/// <c>PaidAmount</c>, would not have agreed with it.
/// </para>
/// </summary>
public class TillMoneyLimitsTests
{
    [Fact]
    public async Task A_negative_tender_is_refused_rather_than_recorded()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);
        var customer = await TillHarness.AddCustomerAsync(db, owing: 4_000_000m);

        // A refund is expressed by Status, never by a sign — the rule the quantity checks
        // already state. A negative credit line is a customer's debt being written off from a
        // counter; a negative cash line is the branch drawer being emptied on paper.
        var wipeDebt = TillHarness.Sale("T-0001", 1, customer.Id, nameof(PaymentMethod.Credit),
            payments: [TillHarness.Tender("credit", -4_000_000m), TillHarness.Tender("cash", 4_001_000m)]);

        var response = await sync.PushAsync(new PushRequest("C01", [wipeDebt]), "C01", CancellationToken.None);

        Assert.Equal(1, response.Rejected);
        Assert.Equal(0, response.Accepted);
        Assert.Equal(4_000_000m, (await db.Customers.AsNoTracking().SingleAsync()).Balance);
        Assert.Empty(await db.Sales.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task An_absurd_paid_amount_cannot_pay_off_what_a_customer_owes()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);
        var customer = await TillHarness.AddCustomerAsync(db, owing: 4_000_000m);

        // The shape an older till uses: PaymentMethod = Credit and no payment line to match.
        // PaidAmount used to be believed, so a 1 000 sale claiming five million paid turned
        // into a five-million credit against the account.
        var sale = TillHarness.Sale("T-0001", 1, customer.Id, nameof(PaymentMethod.Credit), paidAmount: 5_000_000m);

        var response = await sync.PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None);

        Assert.Equal(1, response.Accepted);
        // The sale is recorded, and it raises the debt the invoice is worth. It never reduces it.
        Assert.Equal(4_001_000m, (await db.Customers.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task A_sale_never_reduces_a_customer_s_balance_however_it_is_tendered()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);
        var customer = await TillHarness.AddCustomerAsync(db, owing: 4_000_000m);

        // Deciding money arrived is a back-office act with its own role gate and its own ledger
        // entry (TreasuryPosting.Settlement). A counter cannot do it by describing a sale.
        var overTendered = TillHarness.Sale("T-0001", 1, customer.Id, nameof(PaymentMethod.Credit),
            paidAmount: 9_000_000m, payments: TillHarness.Tender("cash", 9_000_000m));

        await sync.PushAsync(new PushRequest("C01", [overTendered]), "C01", CancellationToken.None);

        Assert.True((await db.Customers.AsNoTracking().SingleAsync()).Balance >= 4_000_000m);
    }

    [Fact]
    public async Task Change_can_never_exceed_the_cash_that_went_into_the_drawer()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);

        // 1 000 in and a claimed 999 999 out would have written a cash movement draining the
        // branch's whole recorded position, and the ledger's cash account with it.
        var sale = TillHarness.Sale("T-0001", 1, changeAmount: 999_999m, payments: TillHarness.Tender("cash", 1000m));

        var response = await sync.PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None);

        Assert.Equal(1, response.Accepted);
        Assert.Contains("not possible", response.Results.Single().Message);
        // The sale is kept and the back office is told. At worst everything that came in went
        // straight back out — the drawer can reach zero on this sale, never below it, and the
        // branch's whole position no longer moves on one till's arithmetic.
        Assert.Equal(0m, await TillHarness.CashInBoxAsync(db, TillHarness.Algiers));
        Assert.True(await TillHarness.CashInBoxAsync(db, TillHarness.Algiers) >= 0m);
    }

    [Fact]
    public async Task An_ordinary_cash_sale_with_change_is_untouched()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);

        // The clamp must not touch the case it exists to leave alone: change smaller than the
        // cash tendered is an ordinary counter transaction and is recorded exactly as sent.
        var sale = TillHarness.Sale("T-0001", 1, changeAmount: 200m, payments: TillHarness.Tender("cash", 1200m));

        var response = await sync.PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None);

        Assert.Equal(1, response.Accepted);
        Assert.Null(response.Results.Single().Message);
        Assert.Equal(1000m, await TillHarness.CashInBoxAsync(db, TillHarness.Algiers));
    }
}
