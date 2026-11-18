using System.Security.Claims;
using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Valuation;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Valuation.StockDocumentsCostSwitchTests;

namespace ParadiERP.Tests.Valuation;

/// <summary>
/// C-01 for opening stock: once sales are costed at the weighted average, an opening restated downwards takes its units
/// out at the average, through the same costing and fallback as every other stock document; a restatement upwards (a
/// first opening included) comes in at its stated value, which is what the shadow takes an opening in at. Switched off
/// (the default) both post the stated value, as before. The shop is <see cref="StockDocumentsCostSwitchTests"/>' (30 pipes,
/// 700 each), plus Constantine, a branch not yet open for operation.
/// </summary>
public class OpeningStockCostSwitchTests
{
    private static readonly Guid Constantine = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static async Task AddConstantineAsync(PosDbContext db)
    {
        db.Branches.Add(new Branch { Id = Constantine, NameFr = "Constantine", IsOpenForOperation = false });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>States Constantine's quantity of each product through the real route; returns the document's movements by product.</summary>
    private static async Task<(Guid DocumentId, Dictionary<Guid, StockMovement> Movements)> OpenAsync(
        PosDbContext db, ClaimsPrincipal admin, params (Guid Product, decimal Qty, decimal? UnitValue)[] lines)
    {
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapStockEndpoints(), "POST",
            "/api/catalog/stock/opening", admin,
            JsonSerializer.Serialize(new
            {
                branchId = Constantine,
                lines = lines.Select(l => new { productId = l.Product, qty = l.Qty, unitValue = l.UnitValue }).ToArray(),
                note = (string?)null,
            }));
        Assert.True(status == 200, body);
        db.ChangeTracker.Clear();
        var documentId = JsonDocument.Parse(body).RootElement.GetProperty("documentId").GetGuid();
        var movements = await db.StockMovements.AsNoTracking().Where(m => m.DocumentId == documentId).ToListAsync();
        Assert.Equal(StockMovementKind.Opening, Assert.Single(movements.Select(m => m.Kind).Distinct()));
        return (documentId, movements.ToDictionary(m => m.ProductId));
    }

    private static Task<Dictionary<string, decimal>> OpeningPostedAsync(PosDbContext db, Guid documentId) =>
        PostedAsync(db, LedgerSources.OpeningBalance, documentId);

    [Fact]
    public async Task Switched_off_an_opening_restated_down_and_up_posts_its_stated_value_as_before()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await AddConstantineAsync(db);
        var admin = await AdminAsync(db);
        // The average is 700: nothing below may read it.

        var (first, opened) = await OpenAsync(db, admin, (TillHarness.Pipe, 10, 1_000m));
        Assert.Equal((10m, 1_000m, SaleCostBasis.CostPrice), (opened[TillHarness.Pipe].QtyDelta, opened[TillHarness.Pipe].UnitCost!.Value, opened[TillHarness.Pipe].CostBasis));
        var posted = await OpeningPostedAsync(db, first);
        Assert.Equal((10_000m, -10_000m), (posted[LedgerAccounts.Inventory], posted[LedgerAccounts.OpeningBalance]));

        // Down to 6, no value typed: the four leave at the cost price.
        var (down, lowered) = await OpenAsync(db, admin, (TillHarness.Pipe, 6, null));
        Assert.Equal((-4m, 600m, SaleCostBasis.CostPrice), (lowered[TillHarness.Pipe].QtyDelta, lowered[TillHarness.Pipe].UnitCost!.Value, lowered[TillHarness.Pipe].CostBasis));
        posted = await OpeningPostedAsync(db, down);
        Assert.Equal((-2_400m, 2_400m), (posted[LedgerAccounts.Inventory], posted[LedgerAccounts.OpeningBalance]));

        // Down to 5 with a value typed: the one leaves at the value typed.
        var (typedDown, _) = await OpenAsync(db, admin, (TillHarness.Pipe, 5, 1_250m));
        Assert.Equal(-1_250m, (await OpeningPostedAsync(db, typedDown))[LedgerAccounts.Inventory]);

        // Up to 8 at 1,250: in at the value typed.
        var (up, raised) = await OpenAsync(db, admin, (TillHarness.Pipe, 8, 1_250m));
        Assert.Equal((3m, 1_250m, SaleCostBasis.CostPrice), (raised[TillHarness.Pipe].QtyDelta, raised[TillHarness.Pipe].UnitCost!.Value, raised[TillHarness.Pipe].CostBasis));
        Assert.Equal(3_750m, (await OpeningPostedAsync(db, up))[LedgerAccounts.Inventory]);

        // The till still sells at the cost price.
        await SellAsync(db, "A-0001", 1, 1);
        Assert.Equal(600m, (await db.SaleLines.AsNoTracking().SingleAsync()).UnitCost);
        Assert.Equal(0, (await Switch(db).StatusAsync()).FallbackMovements);
    }

    [Fact]
    public async Task Switched_on_an_opening_restated_down_leaves_at_the_average_and_up_comes_in_at_its_value_so_the_books_stay_on_the_shadow()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await AddConstantineAsync(db);
        var admin = await AdminAsync(db);
        await SwitchOnAsync(db);

        // 30 at 700. Constantine opens with ten at 1,000: in at its value, 40 worth 31,000, 775 each.
        var (first, opened) = await OpenAsync(db, admin, (TillHarness.Pipe, 10, 1_000m));
        Assert.Equal((1_000m, SaleCostBasis.CostPrice), (opened[TillHarness.Pipe].UnitCost!.Value, opened[TillHarness.Pipe].CostBasis));
        Assert.Equal(10_000m, (await OpeningPostedAsync(db, first))[LedgerAccounts.Inventory]);

        // Restated down to 6, at a value typed: the four leave at the average all the same, 3,100; 36 worth 27,900.
        var (down, lowered) = await OpenAsync(db, admin, (TillHarness.Pipe, 6, 1_000m));
        Assert.Equal((-4m, 775m, SaleCostBasis.WeightedAverage), (lowered[TillHarness.Pipe].QtyDelta, lowered[TillHarness.Pipe].UnitCost!.Value, lowered[TillHarness.Pipe].CostBasis));
        var posted = await OpeningPostedAsync(db, down);
        Assert.Equal((-3_100m, 3_100m), (posted[LedgerAccounts.Inventory], posted[LedgerAccounts.OpeningBalance]));

        // Restated up to 8 at 1,250: in at its value, 2,500; 38 worth 30,400, 800 each.
        var (up, raised) = await OpenAsync(db, admin, (TillHarness.Pipe, 8, 1_250m));
        Assert.Equal((2m, 1_250m, SaleCostBasis.CostPrice), (raised[TillHarness.Pipe].QtyDelta, raised[TillHarness.Pipe].UnitCost!.Value, raised[TillHarness.Pipe].CostBasis));
        Assert.Equal(2_500m, (await OpeningPostedAsync(db, up))[LedgerAccounts.Inventory]);

        // The next sale takes the average the openings left.
        await SellAsync(db, "A-0001", 1, 1);
        Assert.Equal(800m, (await db.SaleLines.AsNoTracking().SingleAsync()).UnitCost);

        // The stock accounts hold exactly the shadow's figure: nothing drifted.
        var status = await Switch(db).StatusAsync();
        Assert.Equal(status.ShadowValue, status.BookValue);
        Assert.Equal(0m, status.Adjustment);
        Assert.Equal(0, status.FallbackMovements);
        Assert.Equal(37m * 800m, status.ShadowValue);
    }

    [Fact]
    public async Task Switched_on_a_product_with_no_average_restated_down_falls_back_to_its_stated_value_and_is_counted()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await AddConstantineAsync(db);
        var admin = await AdminAsync(db);
        await SwitchOnAsync(db);
        // A valve entered before the system and never counted in: no opening, nothing since. A gauge never held.
        var valve = new Product { Sku = "VALVE", NameFr = "Vanne", Price = 1000m, CostPrice = 450m, TrackStock = true };
        var gauge = new Product { Sku = "GAUGE", NameFr = "Manomètre", Price = 500m, CostPrice = 300m, TrackStock = true };
        db.Products.AddRange(valve, gauge);
        db.ProductStocks.Add(new ProductStock { ProductId = valve.Id, BranchId = Constantine, StockQty = 5 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var (document, moved) = await OpenAsync(db, admin, (valve.Id, 3, null), (gauge.Id, 4, null));

        // The valve's two leave at its cost price, flagged; the gauge's four come in at theirs, which is no fallback.
        Assert.Equal((-2m, 450m, SaleCostBasis.CostPriceFallback), (moved[valve.Id].QtyDelta, moved[valve.Id].UnitCost!.Value, moved[valve.Id].CostBasis));
        Assert.Equal((4m, 300m, SaleCostBasis.CostPrice), (moved[gauge.Id].QtyDelta, moved[gauge.Id].UnitCost!.Value, moved[gauge.Id].CostBasis));
        Assert.Equal(-900m + 1_200m, (await OpeningPostedAsync(db, document))[LedgerAccounts.Inventory]);
        var status = await Switch(db).StatusAsync();
        Assert.Equal(1, status.FallbackMovements);
        Assert.Equal(0, status.FallbackLines);
    }
}
