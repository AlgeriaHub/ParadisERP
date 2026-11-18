using System.Security.Claims;
using ParadiERP.API.Endpoints;
using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Valuation;
using ParadiERP.Tests.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ParadiERP.Tests.Valuation;

/// <summary>
/// B-F01 / B-F06 (2026-11-09): what a receiving note does to the product's cost price is the accountant's setting —
/// the last cost (today's rule, the default), the shadow's weighted average, or unchanged — and in every mode a stated
/// cost of 0 never replaces a non-zero cost price. The receipt itself is valued at what it states in every mode.
/// The shop: two branches with ten pipes each, carried at 600, the shadow opened.
/// </summary>
public class ReceivingCostPriceTests
{
    private static async Task<(SqliteConnection Connection, PosDbContext Db, ClaimsPrincipal Admin)> ShopAsync(decimal pipeCost = 600m)
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        (await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice = pipeCost;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await StockValuationStart.EnsureAsync(db);
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers);
        db.ChangeTracker.Clear();
        return (connection, db, admin);
    }

    private static async Task RuleAsync(PosDbContext db, ReceiptCostPriceRule rule)
    {
        var (saved, error) = await new StockValuationShadow(db).UpdateSettingsAsync(
            new UpdateStockValuationSettingsRequest("Company", "SaleCost", 3, ReceiptCostRule: rule.ToString()), "accountant");
        Assert.Null(error);
        Assert.Equal(rule.ToString(), saved!.ReceiptCostRule);
        db.ChangeTracker.Clear();
    }

    /// <summary>Receives through the real route; returns the note's document id.</summary>
    private static async Task<Guid> ReceiveAsync(PosDbContext db, ClaimsPrincipal admin, params (decimal Qty, decimal? Cost)[] lines)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            lines = lines.Select(l => new { productId = TillHarness.Pipe, qty = l.Qty, unitCost = l.Cost, assetUnitValue = (decimal?)null }),
            reference = "BL-" + Guid.NewGuid().ToString("N")[..6],
            note = (string?)null,
            supplierId = (Guid?)null,
            branchId = TillHarness.Algiers,
        });
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapStockEndpoints(), "POST",
            "/api/catalog/stock/receiving", admin, json);
        Assert.True(status == 200, $"status {status}: {body}");
        db.ChangeTracker.Clear();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("documentId").GetGuid();
    }

    private static async Task<decimal> CostPriceAsync(PosDbContext db) =>
        (await db.Products.AsNoTracking().SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice;

    /// <summary>The note's movements' unit cost and what its entry put on 310.</summary>
    private static async Task<(decimal UnitCost, decimal Stock)> ReceiptAsync(PosDbContext db, Guid documentId)
    {
        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.DocumentId == documentId);
        var lines = await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == LedgerSources.Receiving && l.Entry.SourceId == documentId).ToListAsync();
        return (movement.UnitCost!.Value, lines.Where(l => l.Account!.Code == LedgerAccounts.Inventory).Sum(l => l.Debit - l.Credit));
    }

    [Fact]
    public async Task By_default_the_last_cost_becomes_the_cost_price_as_before()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;

        Assert.Equal(nameof(ReceiptCostPriceRule.LastCost), (await new StockValuationShadow(db).SettingsAsync()).ReceiptCostRule);
        var note = await ReceiveAsync(db, admin, (10m, 900m));

        Assert.Equal(900m, await CostPriceAsync(db));
        Assert.Equal((900m, 9000m), await ReceiptAsync(db, note));
    }

    [Theory]
    [InlineData(ReceiptCostPriceRule.LastCost)]
    [InlineData(ReceiptCostPriceRule.Unchanged)]
    public async Task A_stated_cost_of_zero_never_replaces_a_non_zero_cost_price(ReceiptCostPriceRule rule)
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await RuleAsync(db, rule);

        // Until 2026-11-09 the last cost rule wrote the 0 over the 600 (B-F06).
        var note = await ReceiveAsync(db, admin, (5m, 0m));

        Assert.Equal(600m, await CostPriceAsync(db));
        // The receipt itself is still valued at what it states: a free delivery adds nothing to 310.
        Assert.Equal((0m, 0m), await ReceiptAsync(db, note));
    }

    [Fact]
    public async Task A_zero_after_a_price_on_the_same_note_keeps_the_price()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;

        await ReceiveAsync(db, admin, (10m, 900m), (2m, 0m));

        Assert.Equal(900m, await CostPriceAsync(db));
    }

    [Fact]
    public async Task A_product_carried_at_zero_still_takes_its_first_price()
    {
        var (connection, db, admin) = await ShopAsync(pipeCost: 0m);
        await using var _ = connection;
        await using var __ = db;

        await ReceiveAsync(db, admin, (10m, 0m));
        Assert.Equal(0m, await CostPriceAsync(db));
        await ReceiveAsync(db, admin, (10m, 750m));
        Assert.Equal(750m, await CostPriceAsync(db));
    }

    [Fact]
    public async Task Unchanged_never_writes_the_cost_price_and_the_receipt_is_valued_at_what_it_states()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await RuleAsync(db, ReceiptCostPriceRule.Unchanged);

        var stated = await ReceiveAsync(db, admin, (10m, 900m));
        var unstated = await ReceiveAsync(db, admin, (4m, null));

        Assert.Equal(600m, await CostPriceAsync(db));
        Assert.Equal((900m, 9000m), await ReceiptAsync(db, stated));
        // Nothing stated: at the cost price as it stands, as before.
        Assert.Equal((600m, 2400m), await ReceiptAsync(db, unstated));
    }

    [Fact]
    public async Task Weighted_average_makes_the_cost_price_the_report_s_average_after_the_receipt()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await RuleAsync(db, ReceiptCostPriceRule.WeightedAverage);

        // 20 at 600 + 10 at 900 = 30 at 700.
        var note = await ReceiveAsync(db, admin, (10m, 900m));

        Assert.Equal(700m, await CostPriceAsync(db));
        Assert.Equal((900m, 9000m), await ReceiptAsync(db, note));
        var row = Assert.Single((await new StockValuationShadow(db).ReportAsync(null)).Rows);
        Assert.Equal(row.AverageCost, await CostPriceAsync(db));

        // A free delivery weighs in at 0, as the report counts it: 30 at 700 + 5 at 0 = 35 at 600.
        await ReceiveAsync(db, admin, (5m, 0m));
        Assert.Equal(600m, await CostPriceAsync(db));
    }

    [Fact]
    public async Task Weighted_average_rounds_to_the_cost_price_s_three_decimals()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await RuleAsync(db, ReceiptCostPriceRule.WeightedAverage);

        // 20 at 600 + 1 at 601 = 12601 / 21 = 600.047619…
        await ReceiveAsync(db, admin, (1m, 601m));

        Assert.Equal(600.048m, await CostPriceAsync(db));
    }

    [Fact]
    public async Task Weighted_average_never_lands_a_zero_on_a_non_zero_cost_price()
    {
        // The opening carried the pipes at 0; the cost price was typed on the card afterwards.
        var (connection, db, admin) = await ShopAsync(pipeCost: 0m);
        await using var _ = connection;
        await using var __ = db;
        (await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice = 600m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await RuleAsync(db, ReceiptCostPriceRule.WeightedAverage);

        await ReceiveAsync(db, admin, (5m, 0m));

        Assert.Equal(600m, await CostPriceAsync(db));
    }

    [Fact]
    public async Task With_no_average_the_weighted_average_falls_back_to_the_last_cost()
    {
        var (connection, db, _) = await ShopAsync();
        await using var __ = connection;
        await using var ___ = db;
        // A product nothing has moved and the opening never held: the shadow has no average for it.
        var valve = new Product { Sku = "VALVE", NameFr = "Vanne", Price = 1000m, CostPrice = 450m, TrackStock = true };
        db.Products.Add(valve);
        await db.SaveChangesAsync();

        var pricing = new ReceivingCostPrice(db, new StockValuationShadow(db), NullLogger<ReceivingCostPrice>.Instance);
        var changed = await pricing.ApplyAverageAsync("BL-1", [(valve, 500m), (valve, 0m)]);

        Assert.Same(valve, Assert.Single(changed));
        Assert.Equal(500m, valve.CostPrice);
    }

    [Fact]
    public async Task The_setting_is_validated_kept_when_not_sent_and_open_while_sales_post_the_average()
    {
        var (connection, db, _) = await ShopAsync();
        await using var __ = connection;
        await using var ___ = db;
        var shadow = new StockValuationShadow(db);

        Assert.NotNull((await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Company", "SaleCost", 3, ReceiptCostRule: "Planet"), "a")).Error);
        await RuleAsync(db, ReceiptCostPriceRule.Unchanged);
        // An older client sends no receipt rule: the saved one stays.
        var (kept, _) = await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Company", "SaleCost", 4), "a");
        Assert.Equal(nameof(ReceiptCostPriceRule.Unchanged), kept!.ReceiptCostRule);

        // Sales at the average lock the shadow's choices, not this one: it changes no movement the replay reads.
        var row = await db.StockValuationSettings.SingleAsync();
        row.SalesCost = SalesCostMethod.WeightedAverage;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var (saved, error) = await shadow.UpdateSettingsAsync(
            new UpdateStockValuationSettingsRequest("Company", "SaleCost", 4, ReceiptCostRule: "WeightedAverage"), "a");
        Assert.Null(error);
        Assert.Equal(nameof(ReceiptCostPriceRule.WeightedAverage), saved!.ReceiptCostRule);
        Assert.Equal(StockValuationShadow.SalesCostLocked,
            (await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Location", "SaleCost", 4, ReceiptCostRule: "WeightedAverage"), "a")).Error);
    }
}
