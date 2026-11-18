using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Valuation;
using AlgeriaPos.Tests.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Valuation;

/// <summary>
/// P0.4a and P0.4b against a real database: movements and sale lines record the cost they were valued
/// at, the shadow opens once, values the movements since, and writes nothing but its own settings.
/// </summary>
public class StockValuationShadowTests
{
    private static async Task<(SqliteConnection Connection, PosDbContext Db)> ShopAsync(decimal pipeCost = 600m)
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        (await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice = pipeCost;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (connection, db);
    }

    private static async Task PushAsync(PosDbContext db, params SalePush[] sales)
    {
        var result = await TillHarness.NewService(db).PushAsync(new PushRequest("C01", sales), "C01", CancellationToken.None);
        Assert.Equal(sales.Length, result.Accepted);
        db.ChangeTracker.Clear();
    }

    private static SalePush RefundOf(SalePush sale) => sale with
    {
        Id = Guid.NewGuid(), Reference = sale.Reference + "-R", LocalSeq = sale.LocalSeq + 100,
        Status = nameof(SaleStatus.Refunded), RefundOfSaleId = sale.Id,
        Lines = [sale.Lines[0] with { Id = Guid.NewGuid() }],
        Payments = [TillHarness.Tender("cash", sale.GrandTotalTtc)],
    };

    [Fact]
    public async Task The_opening_is_taken_once_from_what_every_location_holds()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;

        Assert.Equal(2, await StockValuationStart.EnsureAsync(db));
        Assert.Null(await StockValuationStart.EnsureAsync(db));

        var openings = await db.StockValuationOpenings.AsNoTracking().ToListAsync();
        Assert.Equal(20m, openings.Sum(o => o.Qty));
        Assert.All(openings, o => Assert.Equal(600m, o.UnitCost));
        Assert.Single(await db.StockValuationSettings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_sale_and_its_refund_record_their_kind_and_the_cost_their_entry_posted()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;

        var sale = TillHarness.Sale("A-0001", 1);
        await PushAsync(db, sale);
        await PushAsync(db, RefundOf(sale));

        var movements = await db.StockMovements.AsNoTracking().OrderBy(m => m.CreatedAtUtc).ToListAsync();
        Assert.Equal([StockMovementKind.Sale, StockMovementKind.Refund], movements.Select(m => m.Kind));
        Assert.All(movements, m => Assert.Equal(600m, m.UnitCost));
        Assert.All(await db.SaleLines.AsNoTracking().ToListAsync(), l => Assert.Equal(600m, l.UnitCost));
    }

    [Fact]
    public async Task An_uncosted_line_records_a_zero_cost_and_a_service_none()
    {
        var (connection, db) = await ShopAsync(pipeCost: 0m);
        await using var _ = connection;
        await using var __ = db;
        var service = new Product { Sku = "DELIV", NameFr = "Livraison", Price = 500m, TrackStock = false };
        db.Products.Add(service);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sale = TillHarness.Sale("A-0001", 1, paidAmount: 1500m) with
        {
            SubTotalHt = 1500m, GrandTotalTtc = 1500m,
            Lines =
            [
                new SaleLinePush(Guid.NewGuid(), TillHarness.Pipe, "PIPE-90", "Tube 90", 1m, 1000m, 0m, 0m, 1000m, 0m),
                new SaleLinePush(Guid.NewGuid(), service.Id, "DELIV", "Livraison", 1m, 500m, 0m, 0m, 500m, 0m),
            ],
        };
        await PushAsync(db, sale);

        var lines = await db.SaleLines.AsNoTracking().ToListAsync();
        Assert.Equal(0m, lines.Single(l => l.ProductId == TillHarness.Pipe).UnitCost);
        Assert.Null(lines.Single(l => l.ProductId == service.Id).UnitCost);
    }

    [Fact]
    public async Task The_shadow_values_the_movements_since_the_opening_beside_the_cost_price()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await StockValuationStart.EnsureAsync(db);
        var opened = (await db.StockValuationSettings.AsNoTracking().SingleAsync()).StartedAtUtc;

        // A later purchase at 900 moves the cost price to 900; the average does not jump with it.
        var pipe = await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe);
        pipe.CostPrice = 900m;
        db.StockMovements.Add(new StockMovement
        {
            ProductId = TillHarness.Pipe, BranchId = TillHarness.Algiers, QtyDelta = 10, MovedQty = 10, BalanceAfter = 20,
            Reason = "receiving", Kind = StockMovementKind.Receipt, UnitCost = 900m, CreatedAtUtc = opened.AddTicks(1),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await PushAsync(db, TillHarness.Sale("A-0001", 1));

        var report = await new StockValuationShadow(db).ReportAsync(null);
        var row = Assert.Single(report.Rows);
        Assert.Equal(29m, row.ClosingQty);
        Assert.Equal(700m, row.AverageCost);
        Assert.Equal(29m * 700m, row.ClosingValue);
        Assert.Equal(29m * 900m, row.ReferenceValue);
        Assert.Equal(900m, row.PostedCostOfSales);
        Assert.Equal(700m, row.ShadowCostOfSales);
        Assert.False(db.ChangeTracker.HasChanges());

        var detail = await new StockValuationShadow(db).ProductAsync(TillHarness.Pipe);
        Assert.NotNull(detail);
        Assert.Equal([nameof(StockMovementKind.Receipt), nameof(StockMovementKind.Sale)], detail!.Lines.Select(l => l.Kind));
    }

    [Fact]
    public async Task The_admin_chooses_the_open_answers_and_a_wrong_one_is_refused()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var shadow = new StockValuationShadow(db);

        Assert.NotNull((await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Planet", "SaleCost", 3), "admin")).Error);
        Assert.NotNull((await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Company", "SaleCost", 9), "admin")).Error);

        var (saved, ok) = await shadow.UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("location", "CurrentAverage", 4), "admin");
        Assert.Null(ok);
        Assert.Equal(("Location", "CurrentAverage", 4, "admin"), (saved!.Scope, saved.RefundCost, saved.UnitCostDecimals, saved.UpdatedBy));
        // Saving before start-up opened the shadow opens it, so the choice has an opening to apply to.
        Assert.NotNull(saved.StartedAtUtc);
        Assert.Equal(2, await db.StockValuationOpenings.CountAsync());

        var report = await shadow.ReportAsync(null);
        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(nameof(StockValuationScope.Location), report.Settings.Scope);
    }

    [Fact]
    public void The_shadow_writes_no_cost_price_no_movement_no_sale_and_no_ledger()
    {
        var folder = Path.Combine(RepoRoot(), "AlgeriaPos.Infrastructure", "Valuation");
        var sources = Directory.GetFiles(folder, "*.cs").ToDictionary(Path.GetFileName, File.ReadAllText);
        Assert.NotEmpty(sources);
        foreach (var (file, text) in sources)
        {
            Assert.DoesNotContain("CostPrice =", text);
            Assert.DoesNotContain("LedgerPoster", text);
            Assert.DoesNotContain("PostAsync", text);
            Assert.DoesNotContain("StockMovements.Add", text);
            Assert.DoesNotContain("ProductStocks.Add", text);
            Assert.DoesNotContain("Sales.Add", text);
            Assert.DoesNotContain("ExecuteUpdate", text);
            Assert.DoesNotContain("ExecuteDelete", text);
        }
        // Its two writes: the opening at start-up, and the admin's settings.
        Assert.Equal(1, Count(sources["StockValuationStart.cs"], "SaveChangesAsync"));
        Assert.Equal(1, Count(sources["StockValuationShadow.cs"], "SaveChangesAsync"));
        Assert.Equal(0, Count(sources["WeightedAverageShadow.cs"], "SaveChanges"));
    }

    private static int Count(string text, string word) => (text.Length - text.Replace(word, "").Length) / word.Length;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "AlgeriaPos.Infrastructure"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found above the test binaries.");
    }
}

/// <summary>Through the real endpoints: who reads the shadow's figures, at which branch.</summary>
public class StockValuationBranchTests
{
    private static Task<(int Status, string Body)> GetAsync(PosDbContext db, System.Security.Claims.ClaimsPrincipal caller, string query = "") =>
        BranchLockTests.InvokeAsync(db, app => AlgeriaPos.API.Endpoints.ValuationEndpoints.MapValuationEndpoints(app),
            "GET", "/api/valuation/shadow", caller, query);

    [Fact]
    public async Task A_branch_user_is_refused_the_company_wide_figures_and_reads_only_their_location_per_location()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        await StockValuationStart.EnsureAsync(db);
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, TillHarness.Oran);
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Oran);

        // One average for the company: it pools every branch, so a branch user is told so, in words.
        var refused = await GetAsync(db, manager);
        Assert.Equal(403, refused.Status);
        Assert.Contains(AlgeriaPos.API.Endpoints.ValuationEndpoints.CompanyWideCode, refused.Body);
        Assert.Equal(200, (await GetAsync(db, admin)).Status);

        // One average per location: the manager reads Oran, even when the URL asks for Algiers.
        await new StockValuationShadow(db).UpdateSettingsAsync(new UpdateStockValuationSettingsRequest("Location", "SaleCost", 3), "admin");
        db.ChangeTracker.Clear();
        var own = await GetAsync(db, manager, $"?branchId={TillHarness.Algiers}");
        Assert.Equal(200, own.Status);
        Assert.Contains(TillHarness.Oran.ToString(), own.Body);
        Assert.DoesNotContain(TillHarness.Algiers.ToString(), own.Body);

        var all = await GetAsync(db, admin);
        Assert.Contains(TillHarness.Algiers.ToString(), all.Body);
        Assert.Contains(TillHarness.Oran.ToString(), all.Body);
    }
}
