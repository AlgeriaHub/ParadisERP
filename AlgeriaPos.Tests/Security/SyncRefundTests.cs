using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Loyalty;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// A refund stands on a sale (owner, security review 2026-10-10: "a refund must name the original
/// sale and never exceed what is left of it"). Before, a till pushed "Refunded" with nothing behind
/// it and the server wiped the customer's debt, credited the gift card and took the cash out of the
/// drawer. And what a till receives is what it needs: its own branch's cashiers, and gift cards by
/// the hash of their code.
/// </summary>
public class SyncRefundTests
{
    private static SalePush Refund(SalePush original, decimal amount, Guid? customerId = null, decimal qty = 1m,
        params SalePaymentPush[] payments) => original with
    {
        Id = Guid.NewGuid(),
        Reference = "T-R" + Guid.NewGuid().ToString("n")[..6],
        LocalSeq = original.LocalSeq + 100,
        Status = nameof(SaleStatus.Refunded),
        RefundOfSaleId = original.Id,
        CustomerId = customerId ?? original.CustomerId,
        GrandTotalTtc = amount,
        SubTotalHt = amount,
        PaidAmount = amount,
        Lines = [new SaleLinePush(Guid.NewGuid(), TillHarness.Pipe, "PIPE-90", "Tube 90", qty, amount / qty, 0m, 0m, amount, 0m)],
        Payments = payments.Length > 0 ? payments : [TillHarness.Tender("cash", amount)],
    };

    private static async Task<(PosDbContext Db, SyncService Sync, IAsyncDisposable Connection)> NewAsync()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        return (db, TillHarness.NewService(db), connection);
    }

    [Fact]
    public async Task A_refund_with_no_sale_behind_it_moves_nothing()
    {
        var (db, sync, connection) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;
        var customer = await TillHarness.AddCustomerAsync(db, owing: 50_000m);

        // The attack the review found: "Refunded", a credit tender, the friend's debt.
        var invented = TillHarness.Sale("T-0001", 1, customer.Id, nameof(PaymentMethod.Credit),
            payments: TillHarness.Tender("credit", 1000m)) with { Status = nameof(SaleStatus.Refunded) };

        var response = await sync.PushAsync(new PushRequest("C01", [invented]), "C01", CancellationToken.None);

        Assert.Equal(1, response.Rejected);
        Assert.Equal("refund_needs_original", response.Results.Single().Code);
        Assert.Equal(50_000m, (await db.Customers.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(0m, await TillHarness.CashInBoxAsync(db, TillHarness.Algiers));
    }

    [Fact]
    public async Task A_refund_gives_back_what_the_sale_took_and_never_more_all_refunds_together()
    {
        var (db, sync, connection) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;
        var sale = TillHarness.Sale("T-0001", 1, payments: TillHarness.Tender("cash", 1000m));
        Assert.Equal(1, (await sync.PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None)).Accepted);

        var half = Refund(sale, 600m, qty: 0.6m);
        Assert.Equal(1, (await sync.PushAsync(new PushRequest("C01", [half]), "C01", CancellationToken.None)).Accepted);

        // 600 back already; another 600 would come to more than the 1 000 it took.
        var again = Refund(sale, 600m, qty: 0.6m);
        var refused = await sync.PushAsync(new PushRequest("C01", [again]), "C01", CancellationToken.None);
        Assert.Equal("refund_exceeds_sale", refused.Results.Single().Code);

        var rest = Refund(sale, 400m, qty: 0.4m);
        Assert.Equal(1, (await sync.PushAsync(new PushRequest("C01", [rest]), "C01", CancellationToken.None)).Accepted);
        Assert.Equal(0m, await TillHarness.CashInBoxAsync(db, TillHarness.Algiers));
    }

    [Fact]
    public async Task A_debt_goes_back_only_to_the_customer_who_ran_it_up()
    {
        var (db, sync, connection) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;
        var buyer = await TillHarness.AddCustomerAsync(db, owing: 0m);
        var friend = new Customer { Code = "C-2", FullName = "Ami", Balance = 80_000m };
        db.Customers.Add(friend);
        await db.SaveChangesAsync();

        var sale = TillHarness.Sale("T-0001", 1, buyer.Id, nameof(PaymentMethod.Credit), payments: TillHarness.Tender("credit", 1000m));
        await sync.PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None);

        var toFriend = Refund(sale, 1000m, friend.Id, payments: TillHarness.Tender("credit", 1000m));
        var refused = await sync.PushAsync(new PushRequest("C01", [toFriend]), "C01", CancellationToken.None);

        Assert.Equal("refund_wrong_customer", refused.Results.Single().Code);
        Assert.Equal(80_000m, (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == friend.Id)).Balance);

        var toBuyer = Refund(sale, 1000m, buyer.Id, payments: TillHarness.Tender("credit", 1000m));
        Assert.Equal(1, (await sync.PushAsync(new PushRequest("C01", [toBuyer]), "C01", CancellationToken.None)).Accepted);
        Assert.Equal(0m, (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == buyer.Id)).Balance);
    }

    [Fact]
    public async Task A_refund_whose_sale_has_not_arrived_yet_waits_rather_than_being_lost()
    {
        var (db, sync, connection) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;
        var sale = TillHarness.Sale("T-0001", 1);

        var early = await sync.PushAsync(new PushRequest("C01", [Refund(sale, 1000m)]), "C01", CancellationToken.None);

        // The till keeps it and sends it again (sync.service.ts treats this code as retryable).
        Assert.Equal(SyncService.RefundOriginalPendingCode, early.Results.Single().Code);
    }

    [Fact]
    public async Task Another_branch_s_sale_is_not_refunded_at_this_till()
    {
        var (db, sync, connection) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;
        var oranSale = TillHarness.Sale("T-0001", 1);
        await sync.PushAsync(new PushRequest("C02", [oranSale]), "C02", CancellationToken.None);

        var response = await sync.PushAsync(new PushRequest("C01", [Refund(oranSale, 1000m)]), "C01", CancellationToken.None);

        Assert.Equal("refund_other_branch", response.Results.Single().Code);
    }

    [Fact]
    public async Task An_account_without_the_refund_permission_cannot_refund_and_one_without_sales_cannot_sell()
    {
        var (db, sync, connection) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;
        var sale = TillHarness.Sale("T-0001", 1);
        await sync.PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None);

        var noRefund = new SyncActor(Guid.NewGuid(), "clerk", CanSell: true, CanRefund: false);
        var refused = await sync.PushAsync(new PushRequest("C01", [Refund(sale, 1000m)]), "C01", CancellationToken.None, noRefund);
        Assert.Equal("forbidden", refused.Results.Single().Code);

        var noSales = new SyncActor(Guid.NewGuid(), "accountant", CanSell: false, CanRefund: false);
        var sold = await sync.PushAsync(new PushRequest("C01", [TillHarness.Sale("T-0002", 2)]), "C01", CancellationToken.None, noSales);
        Assert.Equal("forbidden", sold.Results.Single().Code);
    }

    [Fact]
    public async Task A_cashier_who_is_not_a_user_of_this_branch_is_replaced_by_the_account_that_pushed()
    {
        var (db, sync, connection) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;
        var pusher = new User { Username = "11112222", FullName = "Caissier Alger", BranchId = TillHarness.Algiers, PasswordHash = "x" };
        db.Users.Add(pusher);
        await db.SaveChangesAsync();

        // The till names a cashier nobody knows (or one of another branch), with a made-up name.
        var sale = TillHarness.Sale("T-0001", 1) with { CashierUserId = Guid.NewGuid(), CashierName = "Someone Else" };
        await sync.PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None,
            new SyncActor(pusher.Id, pusher.Username, true, true));

        var stored = await db.Sales.AsNoTracking().SingleAsync();
        Assert.Equal(pusher.Id, stored.CashierUserId);
        Assert.Equal("Caissier Alger", stored.CashierName);
    }

    [Fact]
    public async Task A_till_receives_its_own_branch_cashiers_and_gift_cards_without_their_codes()
    {
        var (db, sync, connection) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;
        db.Users.Add(new User { Username = "alger", FullName = "A", BranchId = TillHarness.Algiers, Role = UserRole.Cashier, PinHash = "h", PasswordHash = "x" });
        db.Users.Add(new User { Username = "oran", FullName = "O", BranchId = TillHarness.Oran, Role = UserRole.Cashier, PinHash = "h", PasswordHash = "x" });
        db.GiftCards.Add(new GiftCard { Code = "ABCDEFGHJK23", InitialAmount = 500m, Balance = 500m });
        await db.SaveChangesAsync();
        // The harness has no version stamping: place the rows above the cursor by hand.
        var from = await db.CurrentVersionAsync();
        foreach (var u in db.Users.Local) u.Version = from + 1;
        foreach (var g in db.GiftCards.Local) g.Version = from + 1;
        (await db.SyncCounters.SingleAsync(c => c.Id == 1)).Value = from + 1;
        await db.SaveChangesAsync();

        var pull = await sync.PullAsync(0, "C01", CancellationToken.None, new SyncActor(Guid.NewGuid(), "c", true, true));

        Assert.Equal(["alger"], pull.Users.Select(u => u.Username));
        var card = Assert.Single(pull.GiftCards!);
        Assert.DoesNotContain("ABCDEFGHJK23", card.Code);
        Assert.Equal(GiftCardRules.CodeHash("abcdefghjk23"), card.CodeHash);

        // An office session (no till) gets neither PIN hashes nor gift cards nor customers.
        var office = await sync.PullAsync(0, null, CancellationToken.None, new SyncActor(Guid.NewGuid(), "a", true, true));
        Assert.Empty(office.Users);
        Assert.Empty(office.GiftCards!);
    }
}
