using System.Security.Claims;
using ParadiERP.API.Endpoints;
using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Valuation;
using ParadiERP.Tests.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Valuation;

/// <summary>
/// Three of the accountant's choices (2026-11-09; every default keeps today's behaviour):
/// <list type="bullet">
/// <item>C-16: a negative-stock gap covered by a later inflow is posted by the accountant (default) or with the inflow.</item>
/// <item>B-F01's open part: a cost price change does not revalue the stock on hand (default), or does, while sales are
/// costed at the cost price.</item>
/// <item>A receiving line stated at 0 is valued at 0 (default) or at the product's cost price.</item>
/// </list>
/// The shop: two branches with ten pipes each, carried at 600, the shadow opened.
/// </summary>
public class ValuationPostingChoicesTests
{
    private const string OpeningAccount = LedgerAccounts.OpeningBalance;  // 890, the test's opening adjustment
    private const string DifferenceAccount = LedgerAccounts.StockLoss;    // 657, the test's valuation differences

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

    private static async Task MapAsync(PosDbContext db, string slot, string code)
    {
        var account = await db.LedgerAccounts.SingleAsync(a => a.Code == code);
        db.ManufacturingAccountMappings.Add(new ManufacturingAccountMapping { Slot = slot, LedgerAccountId = account.Id, UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task UnmapAsync(PosDbContext db, string slot)
    {
        db.ManufacturingAccountMappings.RemoveRange(db.ManufacturingAccountMappings.Where(m => m.Slot == slot));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task<StockValuationSettingsDto> ChooseAsync(
        PosDbContext db, string? gaps = null, string? revaluation = null, string? zero = null, string? receipt = null)
    {
        var (saved, error) = await new StockValuationShadow(db).UpdateSettingsAsync(
            new UpdateStockValuationSettingsRequest("Company", "SaleCost", 3, ReceiptCostRule: receipt,
                GapPosting: gaps, CostPriceRevaluation: revaluation, ZeroCostReceipt: zero), "accountant");
        Assert.Null(error);
        db.ChangeTracker.Clear();
        return saved!;
    }

    private static SalesCostSwitch Switch(PosDbContext db) => new(db, new StockValuationShadow(db), new LedgerPoster(db));

    private static async Task SwitchOnAsync(PosDbContext db)
    {
        await MapAsync(db, ManufacturingAccountSlots.OpeningAdjustment, OpeningAccount);
        var status = await Switch(db).StatusAsync();
        var outcome = await Switch(db).SwitchAsync(new SwitchSalesCostRequest("WeightedAverage", status.Adjustment), "accountant");
        Assert.Null(outcome.Code);
        db.ChangeTracker.Clear();
    }

    /// <summary>25 pipes sold at Algiers through the till's push.</summary>
    private static async Task SellAsync(PosDbContext db, long seq)
    {
        var sale = TillHarness.Sale("A-000" + seq, seq, paidAmount: 25_000m) with
        {
            SubTotalHt = 25_000m, GrandTotalTtc = 25_000m,
            Lines = [new SaleLinePush(Guid.NewGuid(), TillHarness.Pipe, "PIPE-90", "Tube 90", 25m, 1000m, 0m, 0m, 25_000m, 0m)],
        };
        var result = await TillHarness.NewService(db).PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None);
        Assert.Equal(1, result.Accepted);
        db.ChangeTracker.Clear();
    }

    /// <summary>Receives pipes at Algiers through the real route; returns the note's document id.</summary>
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

    /// <summary>Types a new cost price on the pipe's card through the real route.</summary>
    private static async Task TypeCostPriceAsync(PosDbContext db, ClaimsPrincipal admin, decimal costPrice)
    {
        var pipe = await db.Products.AsNoTracking().SingleAsync(p => p.Id == TillHarness.Pipe);
        var json = System.Text.Json.JsonSerializer.Serialize(new UpsertProductRequest(
            pipe.Sku, pipe.Barcode, pipe.NameAr, pipe.NameFr, pipe.NameEn, pipe.CategoryId, pipe.Price, costPrice, pipe.TaxRate,
            pipe.Unit, pipe.TrackStock, pipe.IsActive), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapCatalogEndpoints(), "PUT",
            "/api/catalog/products/{id:guid}", admin, json, ("id", TillHarness.Pipe.ToString()));
        Assert.True(status == 200, $"status {status}: {body}");
        db.ChangeTracker.Clear();
        Assert.Equal(costPrice, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice);
    }

    private static async Task<List<LedgerEntryLine>> LinesAsync(PosDbContext db, string sourceType) =>
        await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == sourceType).ToListAsync();

    private static decimal On(IEnumerable<LedgerEntryLine> lines, string code, Guid? branch = null) =>
        lines.Where(l => l.Account!.Code == code && (branch is null || l.BranchId == branch)).Sum(l => l.Debit - l.Credit);

    private static void Balanced(IEnumerable<LedgerEntryLine> lines)
    {
        foreach (var entry in lines.GroupBy(l => l.EntryId))
            Assert.Equal(entry.Sum(l => l.Debit), entry.Sum(l => l.Credit));
    }

    /// <summary>The note's movement unit cost, and what its receiving entry put on 310 and on 401.</summary>
    private static async Task<(decimal UnitCost, decimal Stock, decimal Suppliers)> ReceiptAsync(PosDbContext db, Guid documentId)
    {
        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.DocumentId == documentId);
        var lines = (await LinesAsync(db, LedgerSources.Receiving)).Where(l => l.Entry!.SourceId == documentId).ToList();
        return (movement.UnitCost!.Value, On(lines, LedgerAccounts.Inventory), On(lines, LedgerAccounts.Suppliers));
    }

    // ---------------------------------------------------------------- the defaults

    [Fact]
    public async Task The_defaults_are_today_s_behaviour_and_an_older_client_keeps_the_saved_choices()
    {
        var (connection, db, _) = await ShopAsync();
        await using var _c = connection;
        await using var _d = db;

        var settings = await new StockValuationShadow(db).SettingsAsync();
        Assert.Equal(("Accountant", "None", "Zero"), (settings.GapPosting, settings.CostPriceRevaluation, settings.ZeroCostReceipt));

        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);
        await ChooseAsync(db, gaps: "Automatic", revaluation: "StockOnHand", zero: "CostPrice");
        // A client that sends none of the three changes none of them.
        var kept = await ChooseAsync(db);
        Assert.Equal(("Automatic", "StockOnHand", "CostPrice"), (kept.GapPosting, kept.CostPriceRevaluation, kept.ZeroCostReceipt));

        var shadow = new StockValuationShadow(db);
        Assert.NotNull((await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Company", "SaleCost", 3, GapPosting: "Sometimes"), "a")).Error);
        Assert.NotNull((await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Company", "SaleCost", 3, CostPriceRevaluation: "7"), "a")).Error);
        Assert.NotNull((await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Company", "SaleCost", 3, ZeroCostReceipt: "Free"), "a")).Error);
    }

    [Fact]
    public async Task By_default_nothing_new_posts_the_gap_waits_for_the_accountant_and_nothing_is_revalued()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _c = connection;
        await using var _d = db;
        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);

        // The card and a receiving note change the cost price while sales are at the cost price: no revaluation.
        await TypeCostPriceAsync(db, admin, 800m);
        var note = await ReceiveAsync(db, admin, (10m, 900m), (5m, 0m));
        Assert.Empty(await LinesAsync(db, LedgerSources.CostPriceRevaluation));
        Assert.Equal(900m, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice);
        // The note as before: 10 at 900 and the free 5 at 0 — 9,000 on 310 and on 401.
        var receipt = await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == LedgerSources.Receiving && l.Entry.SourceId == note).ToListAsync();
        Assert.Equal((9_000m, -9_000m), (On(receipt, LedgerAccounts.Inventory), On(receipt, LedgerAccounts.Suppliers)));
        Assert.Equal(new[] { 0m, 900m }, (await db.StockMovements.AsNoTracking().Where(m => m.DocumentId == note)
            .Select(m => m.UnitCost!.Value).ToListAsync()).Order());

        // Sales at the average and a negative quantity covered: listed, not posted.
        await SwitchOnAsync(db);
        await SellAsync(db, 1);   // 35 held (20 + 15): 25 sold, none below zero yet
        await SellAsync(db, 2);   // 15 below zero
        var cover = await ReceiveAsync(db, admin, (20m, 1_000m));
        Assert.Empty(await LinesAsync(db, LedgerSources.ValuationGap));
        var gap = Assert.Single((await Switch(db).StatusAsync()).PendingGaps);
        Assert.Equal(cover, (await db.StockMovements.AsNoTracking().SingleAsync(m => m.Id == gap.MovementId)).DocumentId);
    }

    // ---------------------------------------------------------------- C-16: gaps with the inflow

    [Fact]
    public async Task Automatic_posts_the_gap_with_the_inflow_once_and_the_button_never_posts_it_again()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _c = connection;
        await using var _d = db;
        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);
        await ChooseAsync(db, gaps: "Automatic");
        await SwitchOnAsync(db);

        // 25 sold with 20 held: 5 below zero at 600. 10 bought at 900 cover them: a gap of 5 × 300.
        await SellAsync(db, 1);
        var note = await ReceiveAsync(db, admin, (10m, 900m));
        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.DocumentId == note);

        var lines = await LinesAsync(db, LedgerSources.ValuationGap);
        Assert.All(lines, l => Assert.Equal(movement.Id, l.Entry!.SourceId));
        Assert.Equal((1_500m, -1_500m), (On(lines, DifferenceAccount), On(lines, LedgerAccounts.Inventory)));
        Balanced(lines);
        Assert.Single(lines.Select(l => l.EntryId).Distinct());

        // Nothing waits, the button finds nothing, and the stock account agrees with the average.
        var status = await Switch(db).StatusAsync();
        Assert.Empty(status.PendingGaps);
        Assert.Equal(SalesCostSwitch.NoGapsCode, (await Switch(db).PostGapsAsync("accountant")).Code);
        Assert.Single((await LinesAsync(db, LedgerSources.ValuationGap)).Select(l => l.EntryId).Distinct());
        Assert.Equal(0m, (await Switch(db).StatusAsync()).Adjustment);
    }

    [Fact]
    public async Task Automatic_with_the_account_unmapped_accepts_the_inflow_and_leaves_the_gap_for_the_button()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _c = connection;
        await using var _d = db;
        await ChooseAsync(db, gaps: "Automatic");
        await SwitchOnAsync(db);

        await SellAsync(db, 1);
        var note = await ReceiveAsync(db, admin, (10m, 900m));

        Assert.Empty(await LinesAsync(db, LedgerSources.ValuationGap));
        var gap = Assert.Single((await Switch(db).StatusAsync()).PendingGaps);
        Assert.Equal(1_500m, gap.Amount);
        Assert.Equal(note, (await db.StockMovements.AsNoTracking().SingleAsync(m => m.Id == gap.MovementId)).DocumentId);

        // Named afterwards: the accountant posts it, once.
        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);
        Assert.Null((await Switch(db).PostGapsAsync("accountant")).Code);
        Assert.Equal(1_500m, On(await LinesAsync(db, LedgerSources.ValuationGap), DifferenceAccount));
    }

    [Fact]
    public async Task Automatic_covers_every_writer_of_an_inflow_and_nothing_while_sales_are_at_the_cost_price()
    {
        var (connection, db, _) = await ShopAsync();
        await using var _c = connection;
        await using var _d = db;
        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);
        await ChooseAsync(db, gaps: "Automatic");

        // Sales at the cost price: no gap is a ledger figure, and none posts.
        await SellAsync(db, 1);
        await AddReceiptAsync(db, 10m, 900m);
        Assert.Empty(await LinesAsync(db, LedgerSources.ValuationGap));

        // On, a movement saved by any writer (here, straight through the context) posts its gap in its own save.
        await SwitchOnAsync(db);
        await SellAsync(db, 2);   // 5 held at 900, 25 sold: 20 below zero at 900
        var covering = await AddReceiptAsync(db, 30m, 1_200m);
        var gap = Assert.Single((await new StockValuationShadow(db).LiveFiguresAsync())!.Value.Gaps, g => g.MovementId == covering);
        var lines = await LinesAsync(db, LedgerSources.ValuationGap);
        Assert.Equal(Math.Round(gap.Gap, 3), On(lines, DifferenceAccount));
        Assert.Equal(-Math.Round(gap.Gap, 3), On(lines, LedgerAccounts.Inventory));
        Balanced(lines);
        Assert.Empty((await Switch(db).StatusAsync()).PendingGaps);
    }

    private static async Task<Guid> AddReceiptAsync(PosDbContext db, decimal qty, decimal cost)
    {
        var movement = new StockMovement
        {
            ProductId = TillHarness.Pipe, BranchId = TillHarness.Algiers, QtyDelta = qty, MovedQty = qty, Reason = "receiving",
            Kind = StockMovementKind.Receipt, UnitCost = cost, CreatedAtUtc = DateTime.UtcNow,
        };
        db.StockMovements.Add(movement);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return movement.Id;
    }

    // ---------------------------------------------------------------- B-F01: revaluing the stock on hand

    [Fact]
    public async Task The_revaluation_is_refused_while_the_account_is_unmapped()
    {
        var (connection, db, _) = await ShopAsync();
        await using var _c = connection;
        await using var _d = db;
        var shadow = new StockValuationShadow(db);

        var (saved, error) = await shadow.UpdateSettingsAsync(
            new UpdateStockValuationSettingsRequest("Company", "SaleCost", 3, CostPriceRevaluation: "StockOnHand"), "accountant");
        Assert.Null(saved);
        Assert.Equal(StockValuationShadow.RevaluationUnmapped, error);
        db.ChangeTracker.Clear();
        Assert.Equal(CostPriceRevaluationMode.None, (await db.StockValuationSettings.AsNoTracking().SingleAsync()).CostPriceRevaluation);

        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);
        Assert.Equal("StockOnHand", (await ChooseAsync(db, revaluation: "StockOnHand")).CostPriceRevaluation);
    }

    [Fact]
    public async Task A_price_typed_on_the_card_revalues_the_stock_held_at_each_branch()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _c = connection;
        await using var _d = db;
        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);
        await ChooseAsync(db, revaluation: "StockOnHand");

        // 10 at each branch, 600 → 800: 2,000 each.
        await TypeCostPriceAsync(db, admin, 800m);

        var lines = await LinesAsync(db, LedgerSources.CostPriceRevaluation);
        Assert.Equal((2_000m, -2_000m), (On(lines, LedgerAccounts.Inventory, TillHarness.Algiers), On(lines, DifferenceAccount, TillHarness.Algiers)));
        Assert.Equal((2_000m, -2_000m), (On(lines, LedgerAccounts.Inventory, TillHarness.Oran), On(lines, DifferenceAccount, TillHarness.Oran)));
        Balanced(lines);

        // Down again: the other way. The same price typed twice posts nothing.
        await TypeCostPriceAsync(db, admin, 700m);
        await TypeCostPriceAsync(db, admin, 700m);
        lines = await LinesAsync(db, LedgerSources.CostPriceRevaluation);
        Assert.Equal(2, lines.Select(l => l.EntryId).Distinct().Count());
        Assert.Equal(2_000m, On(lines, LedgerAccounts.Inventory));
        Assert.Equal(-2_000m, On(lines, DifferenceAccount));
    }

    [Fact]
    public async Task A_receiving_note_under_the_last_cost_revalues_what_was_held_before_it()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _c = connection;
        await using var _d = db;
        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);
        await ChooseAsync(db, revaluation: "StockOnHand");

        // 20 held at 600; 10 received at 900 become the cost price. The 20 are revalued (+6,000); the 10 are on 310
        // at 900 by the note's own entry.
        var note = await ReceiveAsync(db, admin, (10m, 900m));

        Assert.Equal((900m, 9_000m, -9_000m), await ReceiptAsync(db, note));
        var lines = await LinesAsync(db, LedgerSources.CostPriceRevaluation);
        Assert.All(lines, l => Assert.Equal(note, l.Entry!.SourceId));
        Assert.Equal((3_000m, 3_000m), (On(lines, LedgerAccounts.Inventory, TillHarness.Algiers), On(lines, LedgerAccounts.Inventory, TillHarness.Oran)));
        Assert.Equal(-6_000m, On(lines, DifferenceAccount));
        Balanced(lines);

        // 310 now carries the 30 at the cost price.
        var stock = (await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).ToListAsync())
            .Where(l => l.Account!.Code == LedgerAccounts.Inventory).Sum(l => l.Debit - l.Credit);
        Assert.Equal(15_000m, stock);   // 9,000 + 6,000: the test shop's first 20 were never booked

        // A note that leaves the cost price where it is (a free delivery) posts nothing.
        await ReceiveAsync(db, admin, (5m, 0m));
        Assert.Single((await LinesAsync(db, LedgerSources.CostPriceRevaluation)).Select(l => l.EntryId).Distinct());
    }

    [Fact]
    public async Task Nothing_is_revalued_under_the_average_under_the_average_receipt_rule_or_once_the_account_is_unmapped()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _c = connection;
        await using var _d = db;
        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);
        await ChooseAsync(db, revaluation: "StockOnHand", receipt: "WeightedAverage");

        // The weighted average receipt rule restates the price to the shadow's average, not to a price: nothing posts.
        await ReceiveAsync(db, admin, (10m, 900m));
        Assert.Equal(700m, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice);
        Assert.Empty(await LinesAsync(db, LedgerSources.CostPriceRevaluation));

        // Unmapped after the choice was saved: the card still saves, and nothing posts.
        await UnmapAsync(db, ManufacturingAccountSlots.ValuationDifference);
        await TypeCostPriceAsync(db, admin, 750m);
        Assert.Empty(await LinesAsync(db, LedgerSources.CostPriceRevaluation));

        // Sales at the average: there is no cost-price valuation to revalue.
        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);
        await SwitchOnAsync(db);
        await TypeCostPriceAsync(db, admin, 800m);
        await ChooseAsync(db, receipt: "LastCost");
        await ReceiveAsync(db, admin, (1m, 950m));
        Assert.Empty(await LinesAsync(db, LedgerSources.CostPriceRevaluation));
    }

    // ---------------------------------------------------------------- a receipt at cost 0

    [Fact]
    public async Task A_line_at_zero_is_valued_at_the_cost_price_when_chosen_and_the_price_is_never_replaced_by_zero()
    {
        var (connection, db, admin) = await ShopAsync();
        await using var _c = connection;
        await using var _d = db;

        // The default: a free delivery, at 0, as before.
        Assert.Equal((0m, 0m, 0m), await ReceiptAsync(db, await ReceiveAsync(db, admin, (5m, 0m))));

        await ChooseAsync(db, zero: "CostPrice");
        var unknown = await ReceiveAsync(db, admin, (5m, 0m));
        // The cost is unknown, not free: at the 600 the pipe stands at, on 310 and owed on 401 like any receipt.
        Assert.Equal((600m, 3_000m, -3_000m), await ReceiptAsync(db, unknown));
        Assert.Equal(600m, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice);

        // A stated price is still the price; the shadow weighs the unknown line at the cost price too.
        Assert.Equal((900m, 9_000m, -9_000m), await ReceiptAsync(db, await ReceiveAsync(db, admin, (10m, 900m))));
        var row = Assert.Single((await new StockValuationShadow(db).ReportAsync(null)).Rows);
        Assert.Equal(20m * 600m + 5m * 0m + 5m * 600m + 10m * 900m, row.ClosingValue);
    }

    [Fact]
    public async Task A_line_at_zero_for_a_product_carried_at_zero_stays_at_zero()
    {
        var (connection, db, admin) = await ShopAsync(pipeCost: 0m);
        await using var _c = connection;
        await using var _d = db;
        await ChooseAsync(db, zero: "CostPrice");

        Assert.Equal((0m, 0m, 0m), await ReceiptAsync(db, await ReceiveAsync(db, admin, (5m, 0m))));
    }
}
