using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Loyalty;

/// <summary>
/// SAL-17 (owner, 2026-10-31 and 2026-11-01): the money paid for a gift card is owed to the customer until the
/// card is spent. A card is sold for cash at a branch — the cash goes into that branch's box and the books show
/// it against the card liability (4181). A paid top-up does the same; a cash refund of part of the balance is
/// the reverse; a correction moves no money and books nothing. Called through the real routes.
/// </summary>
public class GiftCardCashTests
{
    private static readonly Guid Algiers = TillHarness.Algiers;
    private static readonly Guid Oran = TillHarness.Oran;

    private static async Task<(int Status, JsonElement Body)> IssueAsync(PosDbContext db, System.Security.Claims.ClaimsPrincipal who, string json)
    {
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapGiftCardEndpoints(), "POST", "/api/catalog/gift-cards", who, json);
        Assert.True(status is 201, $"{status}: {body}");
        db.ChangeTracker.Clear();
        return (status, JsonDocument.Parse(body).RootElement.Clone());
    }

    private static Task<(int Status, string Body)> AdjustAsync(PosDbContext db, System.Security.Claims.ClaimsPrincipal who, Guid card, string json) =>
        BranchLockTests.InvokeAsync(db, app => app.MapGiftCardEndpoints(), "POST", "/api/catalog/gift-cards/{id:guid}/adjust", who, json,
            ("id", card.ToString()));

    /// <summary>Every posted line on one account, by branch, debit minus credit.</summary>
    private static async Task<Dictionary<Guid, decimal>> OnAccountAsync(PosDbContext db, string code)
    {
        var account = await db.LedgerAccounts.AsNoTracking().SingleAsync(a => a.Code == code);
        return (await db.LedgerEntryLines.AsNoTracking().Where(l => l.AccountId == account.Id)
                .Select(l => new { l.BranchId, l.Debit, l.Credit }).ToListAsync())
            .GroupBy(l => l.BranchId).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    private static async Task<(SqliteConnection, PosDbContext)> ShopAsync() => await TillHarness.NewDbAsync();

    [Fact]
    public async Task A_card_sold_at_a_branch_puts_the_cash_in_its_box_and_owes_it_to_the_customer()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, Algiers);

        await IssueAsync(db, admin, $$"""{"code":"CARD-0001","amount":5000,"branchId":"{{Oran}}"}""");

        Assert.Equal(5000m, (await OnAccountAsync(db, LedgerAccounts.Cash))[Oran]);
        Assert.Equal(-5000m, (await OnAccountAsync(db, LedgerAccounts.GiftCardLiability))[Oran]);
        Assert.Equal(5000m, await TillHarness.CashInBoxAsync(db, Oran));
        Assert.Equal(0m, await TillHarness.CashInBoxAsync(db, Algiers));
        var entry = await db.LedgerEntries.AsNoTracking().SingleAsync();
        Assert.Equal(LedgerSources.GiftCardSale, entry.SourceType);
    }

    [Fact]
    public async Task A_manager_sells_at_their_own_branch_whatever_branch_they_name_and_no_branch_means_the_main_one()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, Oran);
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, Oran);

        await IssueAsync(db, manager, $$"""{"amount":1000,"branchId":"{{Algiers}}"}""");
        await IssueAsync(db, admin, """{"amount":700}""");

        Assert.Equal(1000m, await TillHarness.CashInBoxAsync(db, Oran));
        Assert.Equal(700m, await TillHarness.CashInBoxAsync(db, Algiers));   // the main branch
    }

    [Fact]
    public async Task A_paid_top_up_takes_cash_a_cash_refund_hands_it_back_and_a_correction_books_nothing()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, Algiers);
        var (_, card) = await IssueAsync(db, admin, $$"""{"amount":2000,"branchId":"{{Algiers}}"}""");
        var id = card.GetProperty("id").GetGuid();

        var topUp = await AdjustAsync(db, admin, id, $$"""{"delta":500,"reason":"Recharge","kind":"topUp","branchId":"{{Algiers}}"}""");
        Assert.True(topUp.Status == 200, topUp.Body);
        var cashOut = await AdjustAsync(db, admin, id, $$"""{"delta":-300,"reason":"Rendu au client","kind":"cashOut","branchId":"{{Algiers}}"}""");
        Assert.True(cashOut.Status == 200, cashOut.Body);
        var correction = await AdjustAsync(db, admin, id, """{"delta":-100,"reason":"Erreur de saisie"}""");
        Assert.True(correction.Status == 200, correction.Body);
        db.ChangeTracker.Clear();

        // 2,000 sold + 500 topped up − 300 handed back: the box and the liability agree; the correction is not money.
        Assert.Equal(2200m, await TillHarness.CashInBoxAsync(db, Algiers));
        Assert.Equal(-2200m, (await OnAccountAsync(db, LedgerAccounts.GiftCardLiability))[Algiers]);
        Assert.Equal(2100m, (await db.GiftCards.AsNoTracking().SingleAsync()).Balance);
        var reasons = await db.GiftCardMovements.AsNoTracking().OrderBy(m => m.CreatedAtUtc).Select(m => m.Reason).ToListAsync();
        Assert.Equal(["issue", "topup", "cashout", "adjustment"], reasons);
        Assert.Equal(3, await db.LedgerEntries.CountAsync());
        Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.SourceType == LedgerSources.GiftCardCashOut));
    }

    [Fact]
    public async Task A_top_up_that_takes_away_or_a_refund_that_adds_is_refused_in_words()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, Algiers);
        var (_, card) = await IssueAsync(db, admin, """{"amount":1000}""");
        var id = card.GetProperty("id").GetGuid();

        Assert.Equal(400, (await AdjustAsync(db, admin, id, """{"delta":-50,"reason":"x","kind":"topUp"}""")).Status);
        Assert.Equal(400, (await AdjustAsync(db, admin, id, """{"delta":50,"reason":"x","kind":"cashOut"}""")).Status);
        Assert.Equal(400, (await AdjustAsync(db, admin, id, """{"delta":50,"reason":"x","kind":"gift"}""")).Status);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.LedgerEntries.CountAsync());
        Assert.Equal(1000m, (await db.GiftCards.AsNoTracking().SingleAsync()).Balance);
    }
}
