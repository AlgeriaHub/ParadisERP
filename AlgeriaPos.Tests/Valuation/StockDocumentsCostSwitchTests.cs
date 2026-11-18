using System.Security.Claims;
using System.Text.Json;
using AlgeriaPos.API.Endpoints;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Rentals;
using AlgeriaPos.Infrastructure.Valuation;
using AlgeriaPos.Tests.Rentals;
using AlgeriaPos.Tests.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Valuation;

/// <summary>
/// C-01 for every stock document: once sales are costed at the weighted average, adjustments, write-offs, state changes,
/// transfers and rental returns value their goods at it too, through the same costing and fallback as a sale, so the
/// stock accounts stay the shadow's figure. Switched off (the default) every document posts the cost price, as before.
/// The shop: two branches with ten pipes each, carried at 600; ten more bought at 900 at Algiers.
/// </summary>
public class StockDocumentsCostSwitchTests
{
    private const string OpeningAccount = LedgerAccounts.OpeningBalance;

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    private static readonly TransferActor Company = new(null, "admin", null, MayTransfer: true, MayWriteOff: true);

    internal static async Task<(SqliteConnection Connection, PosDbContext Db)> ShopAsync(StockValuationScope scope = StockValuationScope.Company)
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        var pipe = await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe);
        pipe.CostPrice = 600m;
        pipe.IsRentable = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await StockValuationStart.EnsureAsync(db);
        db.ChangeTracker.Clear();
        if (scope == StockValuationScope.Location)
        {
            Assert.Null((await new StockValuationShadow(db).UpdateSettingsAsync(
                new UpdateStockValuationSettingsRequest("Location", "SaleCost", 3), "admin")).Error);
            db.ChangeTracker.Clear();
        }
        var account = await db.LedgerAccounts.SingleAsync(a => a.Code == OpeningAccount);
        db.ManufacturingAccountMappings.Add(new ManufacturingAccountMapping
        {
            Slot = ManufacturingAccountSlots.OpeningAdjustment, LedgerAccountId = account.Id, UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await ReceiveAsync(db, 10, 900m);
        return (connection, db);
    }

    internal static SalesCostSwitch Switch(PosDbContext db) => new(db, new StockValuationShadow(db), new LedgerPoster(db));

    internal static async Task SwitchOnAsync(PosDbContext db)
    {
        var status = await Switch(db).StatusAsync();
        var outcome = await Switch(db).SwitchAsync(new SwitchSalesCostRequest("WeightedAverage", status.Adjustment), "accountant");
        Assert.Null(outcome.Code);
        db.ChangeTracker.Clear();
    }

    /// <summary>A receiving note at Algiers: its movement and its entry, Dr 310 / Cr 401.</summary>
    private static async Task ReceiveAsync(PosDbContext db, decimal qty, decimal cost)
    {
        var movement = new StockMovement
        {
            ProductId = TillHarness.Pipe, BranchId = TillHarness.Algiers, QtyDelta = qty, MovedQty = qty, Reason = "receiving",
            Kind = StockMovementKind.Receipt, UnitCost = cost,
        };
        db.StockMovements.Add(movement);
        (await db.ProductStocks.SingleAsync(s => s.ProductId == TillHarness.Pipe && s.BranchId == TillHarness.Algiers)).StockQty += qty;
        var draft = new PostingDraft { EntryDate = DateTime.UtcNow, BranchId = TillHarness.Algiers, SourceType = LedgerSources.Receiving, SourceId = movement.Id };
        draft.Add(PostingLine.Dr(LedgerAccounts.Inventory, qty * cost)).Add(PostingLine.Cr(LedgerAccounts.Suppliers, qty * cost));
        await new LedgerPoster(db).PostAsync(draft);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    internal static async Task SellAsync(PosDbContext db, string reference, long seq, decimal qty, string device = "C01")
    {
        var sale = TillHarness.Sale(reference, seq, paidAmount: qty * 1000m) with
        {
            SubTotalHt = qty * 1000m, GrandTotalTtc = qty * 1000m,
            Lines = [new SaleLinePush(Guid.NewGuid(), TillHarness.Pipe, "PIPE-90", "Tube 90", qty, 1000m, 0m, 0m, qty * 1000m, 0m)],
        };
        var result = await TillHarness.NewService(db).PushAsync(new PushRequest(device, [sale]), device, CancellationToken.None);
        Assert.Equal(1, result.Accepted);
        db.ChangeTracker.Clear();
    }

    internal static async Task<ClaimsPrincipal> AdminAsync(PosDbContext db)
    {
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, null);
        db.ChangeTracker.Clear();
        return admin;
    }

    /// <summary>A stocktake through the real route; returns its movement.</summary>
    internal static async Task<StockMovement> AdjustAsync(PosDbContext db, ClaimsPrincipal admin, decimal qtyDelta, Guid? product = null)
    {
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapCatalogEndpoints(), "POST",
            "/api/catalog/products/{id:guid}/stock", admin,
            JsonSerializer.Serialize(new { branchId = TillHarness.Algiers, qtyDelta, reason = "stocktake", reorderLevel = (decimal?)null }),
            ("id", (product ?? TillHarness.Pipe).ToString()));
        Assert.True(status == 200, body);
        db.ChangeTracker.Clear();
        return await db.StockMovements.AsNoTracking().Where(m => m.Kind == StockMovementKind.Adjustment)
            .OrderByDescending(m => m.CreatedAtUtc).FirstAsync();
    }

    /// <summary>A move between states through the real route; returns its movement.</summary>
    internal static async Task<StockMovement> StateAsync(PosDbContext db, ClaimsPrincipal admin, decimal qty, string from, string to)
    {
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapStockEndpoints(), "POST",
            "/api/catalog/stock/state", admin,
            JsonSerializer.Serialize(new { productId = TillHarness.Pipe, branchId = TillHarness.Algiers, qty, from, to, reason = "count" }));
        Assert.True(status == 200, body);
        db.ChangeTracker.Clear();
        return await db.StockMovements.AsNoTracking()
            .Where(m => m.Kind == StockMovementKind.StateChange || m.Kind == StockMovementKind.WriteOff)
            .OrderByDescending(m => m.CreatedAtUtc).FirstAsync();
    }

    /// <summary>Dispatches <paramref name="qty"/> pipes from Algiers to Oran; returns the transfer.</summary>
    internal static async Task<StockTransfer> DispatchAsync(PosDbContext db, decimal qty)
    {
        var sent = await new StockTransfers(db, new LedgerPoster(db)).DispatchAsync(new DispatchStockTransferRequest(
            TillHarness.Algiers, TillHarness.Oran, null, [new StockTransferLineRequest(TillHarness.Pipe, qty)]), Company);
        Assert.True(sent.Status == 200, sent.Error);
        db.ChangeTracker.Clear();
        return sent.Transfer!;
    }

    internal static async Task ReceiveTransferAsync(PosDbContext db, StockTransfer transfer, decimal qty)
    {
        var got = await new StockTransfers(db, new LedgerPoster(db)).ReceiveAsync(transfer.Id,
            new ReceiveStockTransferRequest([new ReceiveLineQty(transfer.Lines.Single().Id, qty)], null), Company);
        Assert.True(got.Status == 200, got.Error);
        db.ChangeTracker.Clear();
    }

    internal static async Task LoseInTransitAsync(PosDbContext db, StockTransfer transfer, decimal qty)
    {
        var lost = await new StockTransfers(db, new LedgerPoster(db)).DeclareLostAsync(transfer.Id,
            new DeclareTransferLossRequest([new TransferLineQty(transfer.Lines.Single().Id, qty)], "fell off the lorry"), Company);
        Assert.True(lost.Status == 200, lost.Error);
        db.ChangeTracker.Clear();
    }

    /// <summary>Hires out <paramref name="issued"/> pipes at Algiers and takes them back: some sound, some wrecked, some lost.</summary>
    internal static async Task RentAndReturnAsync(
        PosDbContext db, decimal issued, decimal ok, decimal wrecked, decimal lost, Guid? issuedFrom = null)
    {
        var customer = RentalTestPaper.Identified(new Customer { Code = "C-R1", FullName = "Client Test", Kind = CustomerKind.Person });
        db.Customers.Add(customer);
        var site = RentalTestPaper.Site(Guid.NewGuid(), customer.Id);
        db.CustomerSites.Add(site);
        if (!await db.Stores.AnyAsync()) db.Stores.Add(new Store { NameFr = "Store", RentalOverdueMultiplier = 1.5m });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var rentals = new RentalDocuments(db, new LedgerPoster(db), new Algeria());
        var created = await rentals.CreateContractAsync(new UpsertRentalContractRequest(customer.Id, TillHarness.Algiers,
            [new RentalContractLineRequest(TillHarness.Pipe, issued, 15, RentUnitPrice: 100m, DamageUnitPrice: 500m)])
        {
            SiteId = site.Id, SignatoryBirthDate = new DateOnly(1980, 3, 4),
        }, "admin");
        Assert.True(created.Ok, created.Message);
        var line = created.Value!.Lines.Single();
        var out_ = await rentals.IssueAsync(created.Value.Id, RentalTestPaper.Driven(new IssueNoteRequest(
            issuedFrom ?? TillHarness.Algiers, [new IssueNoteLineRequest(line.Id, issued)], DateTime.UtcNow.AddDays(-3))), null, "Karim");
        Assert.True(out_.Ok, out_.Message);
        var back = await rentals.ReturnAsync(created.Value.Id, RentalTestPaper.Driven(new ReturnNoteRequest(
            TillHarness.Algiers, [new ReturnNoteLineRequest(line.Id, QtyOk: ok, QtyTotalDamage: wrecked, QtyMissing: lost)],
            DateTime.UtcNow.AddMinutes(-5))), null, "storeman");
        Assert.True(back.Ok, back.Message);
        db.ChangeTracker.Clear();
    }

    /// <summary>What the entries of one source put on each account code: debit less credit.</summary>
    internal static async Task<Dictionary<string, decimal>> PostedAsync(PosDbContext db, string sourceType, Guid? sourceId = null)
    {
        var lines = await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == sourceType && (sourceId == null || l.Entry.SourceId == sourceId)).ToListAsync();
        return lines.GroupBy(l => l.Account!.Code).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    /// <summary>The balance of the stock accounts (310, 318) per branch.</summary>
    private static async Task<Dictionary<Guid, decimal>> BookByBranchAsync(PosDbContext db)
    {
        var codes = new[] { LedgerAccounts.Inventory, LedgerAccounts.ScrapInventory };
        var lines = await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account)
            .Where(l => codes.Contains(l.Account!.Code)).Select(l => new { l.BranchId, l.Debit, l.Credit }).ToListAsync();
        return lines.GroupBy(l => l.BranchId).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    // ---------- switched off: today's figures ----------

    [Fact]
    public async Task Switched_off_every_document_posts_the_cost_price_as_before()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var admin = await AdminAsync(db);
        // The average is 700 (20 at 600, 10 at 900): nothing below may read it.

        var adjusted = await AdjustAsync(db, admin, -2);
        Assert.Equal((600m, SaleCostBasis.CostPrice), (adjusted.UnitCost!.Value, adjusted.CostBasis));
        var adjustment = await PostedAsync(db, LedgerSources.StockAdjustment, adjusted.DocumentId);
        Assert.Equal((-1_200m, 1_200m), (adjustment[LedgerAccounts.Inventory], adjustment[LedgerAccounts.StockLoss]));
        var found = await AdjustAsync(db, admin, 1);
        Assert.Equal(600m, (await PostedAsync(db, LedgerSources.StockAdjustment, found.DocumentId))[LedgerAccounts.Inventory]);

        var writeOff = await StateAsync(db, admin, 1, "Available", "WrittenOff");
        Assert.Equal((600m, SaleCostBasis.CostPrice), (writeOff.UnitCost!.Value, writeOff.CostBasis));
        Assert.Equal(600m, (await PostedAsync(db, LedgerSources.StockState, writeOff.DocumentId))[LedgerAccounts.StockLoss]);
        var scrap = await StateAsync(db, admin, 2, "Available", "Scrap");
        Assert.Equal(1_200m, (await PostedAsync(db, LedgerSources.StockState, scrap.DocumentId))[LedgerAccounts.ScrapInventory]);

        var transfer = await DispatchAsync(db, 3);
        Assert.Equal(600m, transfer.Lines.Single().UnitCost);
        await ReceiveTransferAsync(db, transfer, 2);
        await LoseInTransitAsync(db, transfer, 1);
        Assert.Equal(1_200m, await DebitAtAsync(db, LedgerSources.StockTransfer, TillHarness.Oran));
        Assert.Equal(600m, (await PostedAsync(db, LedgerSources.StockTransferLoss))[LedgerAccounts.StockLoss]);

        await RentAndReturnAsync(db, issued: 4, ok: 1, wrecked: 1, lost: 2);
        var rental = await PostedAsync(db, LedgerSources.StockState);
        // The state route's write-off (600) and scrap (1,200) above, then the return's wreck (600) and its two lost (1,200).
        Assert.Equal(1_800m, rental[LedgerAccounts.ScrapInventory]);
        Assert.Equal(1_800m, rental[LedgerAccounts.StockLoss]);

        Assert.All(await db.StockMovements.AsNoTracking().Where(m => m.Kind != StockMovementKind.Receipt && m.Kind != StockMovementKind.RentalIssue).ToListAsync(),
            m => Assert.Equal((600m, SaleCostBasis.CostPrice), (m.UnitCost!.Value, m.CostBasis)));
    }

    /// <summary>What one source's entries debited to 310 at <paramref name="branch"/>.</summary>
    private static async Task<decimal> DebitAtAsync(PosDbContext db, string sourceType, Guid branch)
    {
        var lines = await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == sourceType && l.BranchId == branch && l.Account!.Code == LedgerAccounts.Inventory).ToListAsync();
        return lines.Sum(l => l.Debit - l.Credit);
    }

    // ---------- switched on ----------

    [Fact]
    public async Task Switched_on_a_sale_an_adjustment_a_write_off_a_transfer_and_a_rental_loss_keep_the_books_on_the_shadow()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var admin = await AdminAsync(db);
        await SwitchOnAsync(db);
        Assert.Equal(0m, (await Switch(db).StatusAsync()).Adjustment);

        // 30 at 700. A sale, then two missing at the count: 27 left, still 700.
        await SellAsync(db, "A-0001", 1, 1);
        var missing = await AdjustAsync(db, admin, -2);
        Assert.Equal((700m, SaleCostBasis.WeightedAverage), (missing.UnitCost!.Value, missing.CostBasis));
        var counted = await PostedAsync(db, LedgerSources.StockAdjustment, missing.DocumentId);
        Assert.Equal((-1_400m, 1_400m), (counted[LedgerAccounts.Inventory], counted[LedgerAccounts.StockLoss]));

        // Five at 1,000 move the average to 23,900 / 32 = 746.875; one found comes in at it.
        await ReceiveAsync(db, 5, 1000m);
        var found = await AdjustAsync(db, admin, 1);
        Assert.Equal(746.875m, found.UnitCost);
        Assert.Equal(746.875m, (await PostedAsync(db, LedgerSources.StockAdjustment, found.DocumentId))[LedgerAccounts.Inventory]);

        // A write-off leaves at the average; a move into scrap carries the goods to 318 at it.
        var writeOff = await StateAsync(db, admin, 1, "Available", "WrittenOff");
        Assert.Equal((746.875m, SaleCostBasis.WeightedAverage), (writeOff.UnitCost!.Value, writeOff.CostBasis));
        Assert.Equal(746.875m, (await PostedAsync(db, LedgerSources.StockState, writeOff.DocumentId))[LedgerAccounts.StockLoss]);
        var scrap = await StateAsync(db, admin, 2, "Available", "Scrap");
        var scrapped = await PostedAsync(db, LedgerSources.StockState, scrap.DocumentId);
        Assert.Equal((1_493.75m, -1_493.75m), (scrapped[LedgerAccounts.ScrapInventory], scrapped[LedgerAccounts.Inventory]));

        // A transfer under one average for the company: its line shows the average, the receipt moves the goods between
        // the branches at it, and a loss in transit leaves the company at it.
        var transfer = await DispatchAsync(db, 3);
        Assert.Equal(746.875m, transfer.Lines.Single().UnitCost);
        await ReceiveTransferAsync(db, transfer, 2);
        Assert.Equal(1_493.75m, await DebitAtAsync(db, LedgerSources.StockTransfer, TillHarness.Oran));
        await LoseInTransitAsync(db, transfer, 1);
        Assert.Equal(746.875m, (await PostedAsync(db, LedgerSources.StockTransferLoss))[LedgerAccounts.StockLoss]);

        // A rental: four out, one back sound, one wrecked, two never seen again — lost at the average.
        await RentAndReturnAsync(db, issued: 4, ok: 1, wrecked: 1, lost: 2);
        var lostRows = await db.StockMovements.AsNoTracking().Where(m => m.Kind == StockMovementKind.RentalReturn).ToListAsync();
        Assert.All(lostRows, m => Assert.Equal((746.875m, SaleCostBasis.WeightedAverage), (m.UnitCost!.Value, m.CostBasis)));

        // The stock accounts hold exactly the shadow's figure: nothing drifted.
        var status = await Switch(db).StatusAsync();
        Assert.Equal(status.ShadowValue, status.BookValue);
        Assert.Equal(0m, status.Adjustment);
        Assert.Equal(0, status.FallbackMovements);
        // 33 at 746.875 less the write-off, the loss in transit and the two lost on rent: 29 left.
        Assert.Equal(29m * 746.875m, status.ShadowValue);
        var report = await new StockValuationShadow(db).ReportAsync(null);
        Assert.Equal(report.Totals.ShadowCostOfSales, report.Totals.PostedCostOfSales);
    }

    [Fact]
    public async Task With_one_average_per_location_the_receiving_location_takes_the_sending_location_s_average()
    {
        var (connection, db) = await ShopAsync(StockValuationScope.Location);
        await using var _ = connection;
        await using var __ = db;
        var admin = await AdminAsync(db);
        await SwitchOnAsync(db);

        // Algiers: 10 at 600 and 10 at 900, 750 each. Oran: 10 at 600.
        var transfer = await DispatchAsync(db, 2);
        Assert.Equal(750m, transfer.Lines.Single().UnitCost);
        await ReceiveTransferAsync(db, transfer, 2);
        Assert.Equal(1_500m, await DebitAtAsync(db, LedgerSources.StockTransfer, TillHarness.Oran));
        Assert.Equal(-1_500m, await DebitAtAsync(db, LedgerSources.StockTransfer, TillHarness.Algiers));

        // Oran now holds 12 worth 7,500: 625 each, which its next sale takes out.
        await SellAsync(db, "O-0001", 1, 1, device: "C02");
        Assert.Equal(625m, (await db.SaleLines.AsNoTracking().SingleAsync()).UnitCost);

        // Two more on the road, one lost: it leaves the transfer at what it left Algiers at.
        var second = await DispatchAsync(db, 2);
        await LoseInTransitAsync(db, second, 1);
        Assert.Equal(750m, (await PostedAsync(db, LedgerSources.StockTransferLoss))[LedgerAccounts.StockLoss]);
        await AdjustAsync(db, admin, -1);

        // Branch by branch, the stock accounts hold the shadow's figure (the one still in transit sits in Algiers' 310,
        // as the switch counts it on the main branch).
        var book = await BookByBranchAsync(db);
        var live = (await new StockValuationShadow(db).LiveFiguresAsync())!.Value;
        Assert.Equal(live.ValueByBranch[TillHarness.Algiers] + live.InTransit, book[TillHarness.Algiers]);
        Assert.Equal(live.ValueByBranch[TillHarness.Oran], book[TillHarness.Oran]);
        Assert.Equal(6_875m, book[TillHarness.Oran]);
        Assert.Equal(0m, (await Switch(db).StatusAsync()).Adjustment);
    }

    [Fact]
    public async Task With_one_average_per_location_goods_rented_from_another_branch_come_back_at_their_branch_s_average()
    {
        var (connection, db) = await ShopAsync(StockValuationScope.Location);
        await using var _ = connection;
        await using var __ = db;
        await SwitchOnAsync(db);

        // An Algiers contract served from Oran (600 each, Algiers holds 750): the lost one is written off at Oran, the
        // others travel to Algiers at Oran's average, the wrecked one then into Algiers' scrap at that value.
        await RentAndReturnAsync(db, issued: 4, ok: 2, wrecked: 1, lost: 1, issuedFrom: TillHarness.Oran);

        Assert.Equal(1_800m, await DebitAtAsync(db, LedgerSources.StockTransfer, TillHarness.Algiers));
        var states = await PostedAsync(db, LedgerSources.StockState);
        Assert.Equal((600m, 600m), (states[LedgerAccounts.ScrapInventory], states[LedgerAccounts.StockLoss]));
        Assert.All(await db.StockMovements.AsNoTracking()
                .Where(m => m.Kind == StockMovementKind.RentalReturn || m.Kind == StockMovementKind.RentalReturnTransfer).ToListAsync(),
            m => Assert.Equal((600m, SaleCostBasis.WeightedAverage), (m.UnitCost!.Value, m.CostBasis)));

        var book = await BookByBranchAsync(db);
        var live = (await new StockValuationShadow(db).LiveFiguresAsync())!.Value;
        Assert.Equal(live.ValueByBranch[TillHarness.Algiers], book[TillHarness.Algiers]);
        Assert.Equal(live.ValueByBranch[TillHarness.Oran], book[TillHarness.Oran]);
        Assert.Equal(15_000m + 1_800m, book[TillHarness.Algiers]);
    }

    [Fact]
    public async Task A_product_with_no_average_is_valued_at_its_cost_price_and_the_row_says_it_fell_back()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var admin = await AdminAsync(db);
        await SwitchOnAsync(db);
        // Bought before the system and never counted in: no opening, nothing since.
        var valve = new Product { Sku = "VALVE", NameFr = "Vanne", Price = 1000m, CostPrice = 450m, TrackStock = true };
        db.Products.Add(valve);
        db.ProductStocks.Add(new ProductStock { ProductId = valve.Id, BranchId = TillHarness.Algiers, StockQty = 5 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var missing = await AdjustAsync(db, admin, -1, valve.Id);

        Assert.Equal((450m, SaleCostBasis.CostPriceFallback), (missing.UnitCost!.Value, missing.CostBasis));
        Assert.Equal(-450m, (await PostedAsync(db, LedgerSources.StockAdjustment, missing.DocumentId))[LedgerAccounts.Inventory]);
        var status = await Switch(db).StatusAsync();
        Assert.Equal(1, status.FallbackMovements);
        Assert.Equal(0, status.FallbackLines);
    }

    [Fact]
    public async Task A_failure_to_work_out_the_average_never_refuses_the_document()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        var broken = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await broken.DisposeAsync();
        var movement = new StockMovement { ProductId = TillHarness.Pipe, BranchId = TillHarness.Algiers, QtyDelta = -2, MovedQty = 2, Kind = StockMovementKind.Adjustment };

        var costs = await new LiveSaleCosting(broken).MovementCostsOrFallbackAsync([new PlannedMovement(movement, 600m)], null, default);

        Assert.Equal(new LiveMovementCost(1_200m, 600m, SaleCostBasis.CostPriceFallback), costs![movement.Id]);
        await db.DisposeAsync();
    }
}
