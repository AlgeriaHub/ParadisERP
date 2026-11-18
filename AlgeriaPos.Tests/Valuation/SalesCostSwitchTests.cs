using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Valuation;
using AlgeriaPos.Tests.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Valuation;

/// <summary>
/// C-01 against a real database: sales stay at the cost price until the accountant switches them to the weighted average;
/// the switch posts the opening adjustment once (C-02); a sale then posts the average of the moment, a refund its sale's
/// cost, a product with no average its cost price (never refused); a covered negative quantity posts its gap (C-16).
/// The shop: two branches with ten pipes each, carried at 600.
/// </summary>
public class SalesCostSwitchTests
{
    private const string OpeningAccount = LedgerAccounts.OpeningBalance;   // 890, for the test's opening adjustment
    private const string DifferenceAccount = LedgerAccounts.StockLoss;    // 657, for the test's valuation differences

    private static async Task<(SqliteConnection Connection, PosDbContext Db)> ShopAsync()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        (await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice = 600m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await StockValuationStart.EnsureAsync(db);
        db.ChangeTracker.Clear();
        return (connection, db);
    }

    private static SalesCostSwitch Switch(PosDbContext db) => new(db, new StockValuationShadow(db), new LedgerPoster(db));

    private static async Task MapAsync(PosDbContext db, string slot, string code)
    {
        var account = await db.LedgerAccounts.SingleAsync(a => a.Code == code);
        db.ManufacturingAccountMappings.Add(new ManufacturingAccountMapping { Slot = slot, LedgerAccountId = account.Id, UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>A receiving note's movement at <paramref name="cost"/>, and — when <paramref name="booked"/> — its entry, Dr 310 / Cr 401.</summary>
    private static async Task<Guid> ReceiveAsync(PosDbContext db, decimal qty, decimal cost, bool booked = false, DateTime? at = null)
    {
        var movement = new StockMovement
        {
            ProductId = TillHarness.Pipe, BranchId = TillHarness.Algiers, QtyDelta = qty, MovedQty = qty, Reason = "receiving",
            Kind = StockMovementKind.Receipt, UnitCost = cost, CreatedAtUtc = at ?? DateTime.UtcNow,
        };
        db.StockMovements.Add(movement);
        if (booked)
        {
            var draft = new PostingDraft { EntryDate = DateTime.UtcNow, BranchId = TillHarness.Algiers, SourceType = LedgerSources.Receiving, SourceId = movement.Id };
            draft.Add(PostingLine.Dr(LedgerAccounts.Inventory, qty * cost)).Add(PostingLine.Cr(LedgerAccounts.Suppliers, qty * cost));
            await new LedgerPoster(db).PostAsync(draft);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return movement.Id;
    }

    private static SalePush SaleOf(string reference, long seq, decimal qty, Guid? product = null) =>
        TillHarness.Sale(reference, seq, paidAmount: qty * 1000m) with
        {
            SubTotalHt = qty * 1000m, GrandTotalTtc = qty * 1000m,
            Lines = [new SaleLinePush(Guid.NewGuid(), product ?? TillHarness.Pipe, "PIPE-90", "Tube 90", qty, 1000m, 0m, 0m, qty * 1000m, 0m)],
        };

    private static SalePush RefundOf(SalePush sale) => sale with
    {
        Id = Guid.NewGuid(), Reference = sale.Reference + "-R", LocalSeq = sale.LocalSeq + 100,
        Status = nameof(SaleStatus.Refunded), RefundOfSaleId = sale.Id,
        Lines = [sale.Lines[0] with { Id = Guid.NewGuid() }],
        Payments = [TillHarness.Tender("cash", sale.GrandTotalTtc)],
    };

    private static async Task PushAsync(PosDbContext db, params SalePush[] sales)
    {
        var result = await TillHarness.NewService(db).PushAsync(new PushRequest("C01", sales), "C01", CancellationToken.None);
        Assert.Equal(sales.Length, result.Accepted);
        db.ChangeTracker.Clear();
    }

    /// <summary>What an entry raised by <paramref name="sourceId"/> put on <paramref name="code"/>: debit less credit.</summary>
    private static async Task<decimal> OnAccountAsync(PosDbContext db, string sourceType, Guid sourceId, string code)
    {
        var lines = await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == sourceType && l.Entry.SourceId == sourceId).ToListAsync();
        Assert.NotEmpty(lines);
        return lines.Where(l => l.Account!.Code == code).Sum(l => l.Debit - l.Credit);
    }

    private static async Task<SaleLine> LineOfAsync(PosDbContext db, SalePush sale) =>
        await db.SaleLines.AsNoTracking().SingleAsync(l => l.SaleId == sale.Id);

    private static async Task<SalesCostingDto> SwitchOnAsync(PosDbContext db)
    {
        var status = await Switch(db).StatusAsync();
        var outcome = await Switch(db).SwitchAsync(new SwitchSalesCostRequest("WeightedAverage", status.Adjustment), "accountant");
        Assert.Null(outcome.Code);
        db.ChangeTracker.Clear();
        return outcome.Value!;
    }

    [Fact]
    public async Task Sales_are_costed_at_the_cost_price_until_the_accountant_switches()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await ReceiveAsync(db, 10, 900m);

        var sale = SaleOf("A-0001", 1, 1);
        await PushAsync(db, sale);

        var line = await LineOfAsync(db, sale);
        Assert.Equal((600m, SaleCostBasis.CostPrice), (line.UnitCost!.Value, line.CostBasis));
        Assert.Equal(600m, await OnAccountAsync(db, LedgerSources.Sale, sale.Id, LedgerAccounts.CostOfGoodsSold));
        var status = await Switch(db).StatusAsync();
        Assert.Equal(nameof(SalesCostMethod.CostPrice), status.Settings.SalesCost);
        Assert.Empty(status.History);
    }

    [Fact]
    public async Task Switching_on_needs_the_adjustment_account_and_the_figure_shown_then_posts_it_once()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        // 20 at 600 opened, 10 bought at 900 and booked: the average holds 21,000, the stock account 9,000.
        await ReceiveAsync(db, 10, 900m, booked: true);

        var status = await Switch(db).StatusAsync();
        Assert.Equal((21_000m, 9_000m, 12_000m), (status.ShadowValue, status.BookValue, status.Adjustment));
        Assert.Contains(LedgerAccounts.Inventory, status.StockAccounts);
        Assert.False(status.OpeningAdjustmentAccount.Mapped);

        var unmapped = await Switch(db).SwitchAsync(new SwitchSalesCostRequest("WeightedAverage", 12_000m), "accountant");
        Assert.Equal(SalesCostSwitch.OpeningAdjustmentUnmappedCode, unmapped.Code);

        await MapAsync(db, ManufacturingAccountSlots.OpeningAdjustment, OpeningAccount);
        var stale = await Switch(db).SwitchAsync(new SwitchSalesCostRequest("WeightedAverage", 11_000m), "accountant");
        Assert.Equal(SalesCostSwitch.AdjustmentChangedCode, stale.Code);
        Assert.Equal(12_000m, stale.Value!.Adjustment);
        Assert.False(await db.LedgerEntries.AnyAsync(e => e.SourceType == LedgerSources.ValuationSwitch));
        Assert.Equal(SalesCostMethod.CostPrice, (await db.StockValuationSettings.AsNoTracking().SingleAsync()).SalesCost);

        var on = await Switch(db).SwitchAsync(new SwitchSalesCostRequest("WeightedAverage", 12_000m), "accountant");
        Assert.Null(on.Code);
        Assert.Equal(nameof(SalesCostMethod.WeightedAverage), on.Value!.Settings.SalesCost);
        Assert.Equal("accountant", on.Value.Settings.SalesCostChangedBy);
        var change = Assert.Single(on.Value.History);
        Assert.Equal(12_000m, change.Adjustment);

        var entry = await db.LedgerEntries.AsNoTracking().SingleAsync(e => e.SourceType == LedgerSources.ValuationSwitch);
        Assert.Equal(change.LedgerEntryId, entry.Id);
        Assert.Equal(12_000m, await OnAccountAsync(db, LedgerSources.ValuationSwitch, change.Id, LedgerAccounts.Inventory));
        Assert.Equal(-12_000m, await OnAccountAsync(db, LedgerSources.ValuationSwitch, change.Id, OpeningAccount));
        // The stock account now holds the average's figure, and nothing is left to adjust.
        Assert.Equal(0m, (await Switch(db).StatusAsync()).Adjustment);

        Assert.Equal(SalesCostSwitch.UnchangedCode, (await Switch(db).SwitchAsync(new SwitchSalesCostRequest("WeightedAverage", 0m), "accountant")).Code);
    }

    [Fact]
    public async Task With_one_average_per_location_the_adjustment_and_the_sales_are_per_location()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        Assert.Null((await new StockValuationShadow(db).UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Location", "SaleCost", 3), "admin")).Error);
        db.ChangeTracker.Clear();
        // Algiers: 10 at 600 and 10 bought at 900, booked at 9,000. Oran: 10 at 600, nothing booked.
        await ReceiveAsync(db, 10, 900m, booked: true);
        await MapAsync(db, ManufacturingAccountSlots.OpeningAdjustment, OpeningAccount);

        var change = (await SwitchOnAsync(db)).History[0];
        Assert.Equal(12_000m, change.Adjustment);
        var lines = await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == LedgerSources.ValuationSwitch).ToListAsync();
        decimal On(Guid branch, string code) => lines.Where(l => l.BranchId == branch && l.Account!.Code == code).Sum(l => l.Debit - l.Credit);
        Assert.Equal((6_000m, -6_000m), (On(TillHarness.Algiers, LedgerAccounts.Inventory), On(TillHarness.Algiers, OpeningAccount)));
        Assert.Equal((6_000m, -6_000m), (On(TillHarness.Oran, LedgerAccounts.Inventory), On(TillHarness.Oran, OpeningAccount)));

        var algiers = SaleOf("A-0001", 1, 1);
        await PushAsync(db, algiers);
        var oran = SaleOf("O-0001", 1, 1);
        var pushed = await TillHarness.NewService(db).PushAsync(new PushRequest("C02", [oran]), "C02", CancellationToken.None);
        Assert.Equal(1, pushed.Accepted);
        db.ChangeTracker.Clear();
        Assert.Equal(750m, (await LineOfAsync(db, algiers)).UnitCost);
        Assert.Equal(600m, (await LineOfAsync(db, oran)).UnitCost);
    }

    [Fact]
    public async Task Once_on_a_sale_posts_the_average_of_the_moment_and_the_shadow_agrees_with_what_was_posted()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await ReceiveAsync(db, 10, 900m);
        await MapAsync(db, ManufacturingAccountSlots.OpeningAdjustment, OpeningAccount);
        await SwitchOnAsync(db);

        // 20 at 600 and 10 at 900: 700 each.
        var first = SaleOf("A-0001", 1, 1);
        await PushAsync(db, first);
        var line = await LineOfAsync(db, first);
        Assert.Equal((700m, SaleCostBasis.WeightedAverage, false), (line.UnitCost!.Value, line.CostBasis, line.CostMissing));
        Assert.Equal(700m, await OnAccountAsync(db, LedgerSources.Sale, first.Id, LedgerAccounts.CostOfGoodsSold));
        Assert.Equal(-700m, await OnAccountAsync(db, LedgerSources.Sale, first.Id, LedgerAccounts.Inventory));
        Assert.Equal(700m, (await db.StockMovements.AsNoTracking().SingleAsync(m => m.SaleId == first.Id)).UnitCost);

        // A batch of three in one push, then a purchase at 1,000 and one more sale: each at the average of its moment.
        await PushAsync(db, SaleOf("A-0002", 2, 2), SaleOf("A-0003", 3, 3), SaleOf("A-0004", 4, 1));
        await ReceiveAsync(db, 10, 1000m);
        var last = SaleOf("A-0005", 5, 1);
        await PushAsync(db, last);
        // 23 left worth 16,100, plus 10 at 1,000: 26,100 for 33.
        Assert.Equal(Math.Round(26_100m / 33m, 3), (await LineOfAsync(db, last)).UnitCost);

        var report = await new StockValuationShadow(db).ReportAsync(null);
        Assert.Equal(report.Totals.ShadowCostOfSales, report.Totals.PostedCostOfSales);
        Assert.Equal(700m * 7m + Math.Round(26_100m / 33m, 3), report.Totals.PostedCostOfSales);
    }

    [Fact]
    public async Task A_refund_comes_back_at_the_cost_its_sale_took_out()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await ReceiveAsync(db, 10, 900m);
        await MapAsync(db, ManufacturingAccountSlots.OpeningAdjustment, OpeningAccount);
        await SwitchOnAsync(db);

        var sale = SaleOf("A-0001", 1, 1);
        await PushAsync(db, sale);
        await ReceiveAsync(db, 10, 1000m);
        var refund = RefundOf(sale);
        await PushAsync(db, refund);

        var line = await LineOfAsync(db, refund);
        Assert.Equal((700m, SaleCostBasis.SaleCost), (line.UnitCost!.Value, line.CostBasis));
        // The refund puts back, and takes off the cost of sales, exactly what the sale posted.
        Assert.Equal(700m, await OnAccountAsync(db, LedgerSources.Sale, refund.Id, LedgerAccounts.Inventory));
        Assert.Equal(-700m, await OnAccountAsync(db, LedgerSources.Sale, refund.Id, LedgerAccounts.CostOfGoodsSold));
    }

    [Fact]
    public async Task A_product_with_no_average_is_sold_at_its_cost_price_and_the_line_says_it_fell_back()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await MapAsync(db, ManufacturingAccountSlots.OpeningAdjustment, OpeningAccount);
        await SwitchOnAsync(db);
        var valve = new Product { Sku = "VALVE", NameFr = "Vanne", Price = 1000m, CostPrice = 450m, TrackStock = true };
        db.Products.Add(valve);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sale = SaleOf("A-0001", 1, 2, valve.Id);
        await PushAsync(db, sale);

        var line = await LineOfAsync(db, sale);
        Assert.Equal((450m, SaleCostBasis.CostPriceFallback), (line.UnitCost!.Value, line.CostBasis));
        Assert.Equal(900m, await OnAccountAsync(db, LedgerSources.Sale, sale.Id, LedgerAccounts.CostOfGoodsSold));
        Assert.Equal(1, (await Switch(db).StatusAsync()).FallbackLines);
    }

    [Fact]
    public async Task Switching_back_posts_nothing_and_the_choices_are_locked_only_while_on()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await ReceiveAsync(db, 10, 900m);
        await MapAsync(db, ManufacturingAccountSlots.OpeningAdjustment, OpeningAccount);
        await SwitchOnAsync(db);
        var shadow = new StockValuationShadow(db);

        Assert.Equal(StockValuationShadow.SalesCostLocked,
            (await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Location", "SaleCost", 3), "admin")).Error);
        Assert.Null((await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Company", "SaleCost", 3), "admin")).Error);
        db.ChangeTracker.Clear();

        var off = await Switch(db).SwitchAsync(new SwitchSalesCostRequest("CostPrice", null), "accountant");
        Assert.Null(off.Code);
        Assert.Equal(2, off.Value!.History.Count);
        Assert.Equal(0m, off.Value.History[0].Adjustment);
        Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.SourceType == LedgerSources.ValuationSwitch));

        var sale = SaleOf("A-0001", 1, 1);
        await PushAsync(db, sale);
        var line = await LineOfAsync(db, sale);
        Assert.Equal((600m, SaleCostBasis.CostPrice), (line.UnitCost!.Value, line.CostBasis));
        Assert.Null((await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Location", "SaleCost", 3), "admin")).Error);
    }

    [Fact]
    public async Task A_negative_quantity_covered_later_posts_its_gap_to_the_valuation_difference_account_once()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await MapAsync(db, ManufacturingAccountSlots.OpeningAdjustment, OpeningAccount);
        await SwitchOnAsync(db);

        // 25 sold with 20 held: all at 600. Then 10 bought at 900 cover the 5 below zero: a gap of 5 × 300.
        var sale = SaleOf("A-0001", 1, 25);
        await PushAsync(db, sale);
        Assert.Equal(15_000m, await OnAccountAsync(db, LedgerSources.Sale, sale.Id, LedgerAccounts.CostOfGoodsSold));
        var receipt = await ReceiveAsync(db, 10, 900m, booked: true);

        var status = await Switch(db).StatusAsync();
        var gap = Assert.Single(status.PendingGaps);
        Assert.Equal((receipt, 1_500m, "PIPE-90"), (gap.MovementId, gap.Amount, gap.Sku));
        Assert.False(status.ValuationDifferenceAccount.Mapped);

        // Unmapped: reported, never guessed.
        Assert.Equal(SalesCostSwitch.ValuationDifferenceUnmappedCode, (await Switch(db).PostGapsAsync("accountant")).Code);
        Assert.False(await db.LedgerEntries.AnyAsync(e => e.SourceType == LedgerSources.ValuationGap));

        await MapAsync(db, ManufacturingAccountSlots.ValuationDifference, DifferenceAccount);
        var posted = await Switch(db).PostGapsAsync("accountant");
        Assert.Null(posted.Code);
        Assert.Empty(posted.Value!.PendingGaps);
        Assert.Equal(1_500m, await OnAccountAsync(db, LedgerSources.ValuationGap, receipt, DifferenceAccount));
        Assert.Equal(-1_500m, await OnAccountAsync(db, LedgerSources.ValuationGap, receipt, LedgerAccounts.Inventory));
        Assert.Equal(SalesCostSwitch.NoGapsCode, (await Switch(db).PostGapsAsync("accountant")).Code);

        // The stock account and the average agree again: nothing left to adjust.
        Assert.Equal(0m, (await Switch(db).StatusAsync()).Adjustment);
    }

    [Fact]
    public async Task A_request_replays_a_product_once_then_reads_only_what_was_written_since()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var settings = await db.StockValuationSettings.SingleAsync();
        settings.SalesCost = SalesCostMethod.WeightedAverage;
        settings.SalesCostChangedAtUtc = settings.StartedAtUtc;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var started = settings.StartedAtUtc;
        var costing = new LiveSaleCosting(db);
        var pipe = new Dictionary<Guid, (decimal, decimal)> { [TillHarness.Pipe] = (1m, 600m) };

        Assert.Equal(600m, (await costing.CostsAsync(TillHarness.Algiers, false, null, pipe, default))![TillHarness.Pipe].UnitCost);
        await ReceiveAsync(db, 10, 900m, at: started.AddSeconds(2));
        Assert.Equal(700m, (await costing.CostsAsync(TillHarness.Algiers, false, null, pipe, default))![TillHarness.Pipe].UnitCost);
        Assert.Equal(1, costing.Replays);

        // A movement written late with an earlier time: the product replays from the opening, in order.
        await ReceiveAsync(db, 10, 300m, at: started.AddSeconds(1));
        Assert.Equal(600m, (await costing.CostsAsync(TillHarness.Algiers, false, null, pipe, default))![TillHarness.Pipe].UnitCost);
        Assert.Equal(2, costing.Replays);

        // Switched off: today's rule, no figure.
        (await db.StockValuationSettings.SingleAsync()).SalesCost = SalesCostMethod.CostPrice;
        await db.SaveChangesAsync();
        Assert.Null(await costing.CostsAsync(TillHarness.Algiers, false, null, pipe, default));
    }
}

/// <summary>The switch through its real routes: who may see and press it.</summary>
public class SalesCostSwitchRouteTests
{
    private static Task<(int Status, string Body)> CallAsync(PosDbContext db, System.Security.Claims.ClaimsPrincipal caller,
        string method, string pattern, string json = "") =>
        BranchLockTests.InvokeAsync(db, app => AlgeriaPos.API.Endpoints.ValuationEndpoints.MapValuationEndpoints(app),
            method, pattern, caller, json);

    [Fact]
    public async Task A_branch_user_is_refused_and_a_company_accountant_reads_the_figure_and_switches()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        (await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice = 600m;
        var account = await db.LedgerAccounts.SingleAsync(a => a.Code == LedgerAccounts.OpeningBalance);
        db.ManufacturingAccountMappings.Add(new ManufacturingAccountMapping
        {
            Slot = ManufacturingAccountSlots.OpeningAdjustment, LedgerAccountId = account.Id, UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await StockValuationStart.EnsureAsync(db);
        db.ChangeTracker.Clear();
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, TillHarness.Oran);
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Oran);

        var refused = await CallAsync(db, manager, "GET", "/api/valuation/sales-cost");
        Assert.Equal(403, refused.Status);
        Assert.Contains(AlgeriaPos.API.Endpoints.ValuationEndpoints.CompanyWideCode, refused.Body);
        Assert.Equal(403, (await CallAsync(db, manager, "POST", "/api/valuation/sales-cost/switch",
            """{"method":"WeightedAverage","expectedAdjustment":12000}""")).Status);

        var read = await CallAsync(db, admin, "GET", "/api/valuation/sales-cost");
        Assert.Equal(200, read.Status);
        using (var json = System.Text.Json.JsonDocument.Parse(read.Body))
            Assert.Equal(12_000m, json.RootElement.GetProperty("adjustment").GetDecimal());

        var stale = await CallAsync(db, admin, "POST", "/api/valuation/sales-cost/switch", """{"method":"WeightedAverage","expectedAdjustment":1}""");
        Assert.Equal(409, stale.Status);
        Assert.Contains(SalesCostSwitch.AdjustmentChangedCode, stale.Body);

        db.ChangeTracker.Clear();
        var on = await CallAsync(db, admin, "POST", "/api/valuation/sales-cost/switch", """{"method":"WeightedAverage","expectedAdjustment":12000}""");
        Assert.Equal(200, on.Status);
        Assert.Contains("\"salesCost\":\"WeightedAverage\"", on.Body);

        db.ChangeTracker.Clear();
        var gaps = await CallAsync(db, admin, "POST", "/api/valuation/sales-cost/gaps/post");
        Assert.Equal(409, gaps.Status);
        Assert.Contains(SalesCostSwitch.ValuationDifferenceUnmappedCode, gaps.Body);
    }
}
