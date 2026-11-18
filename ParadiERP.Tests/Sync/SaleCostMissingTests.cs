using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Sales;
using ParadiERP.Tests.Hosting;
using ParadiERP.Tests.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadiERP.Tests.Sync;

/// <summary>
/// SAL-02 (owner, 2026-10-31): a sale is never refused because its goods have no cost. The goods
/// leave stock with nothing posted to cost of goods sold, the line is flagged, and a report says
/// which sales' margin is incomplete so the cost can be corrected.
/// </summary>
public class SaleCostMissingTests
{
    private static readonly RegionalSettings Money = RegionalPresets.Create(RegionalPresets.DefaultCountry)!;
    private static readonly TimeZoneInfo Algiers = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");
    private static readonly Guid Service = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Algiers));

    private static async Task<(SqliteConnection Connection, PosDbContext Db)> ShopAsync(decimal pipeCost)
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        (await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice = pipeCost;
        db.Products.Add(new Product { Id = Service, Sku = "DELIV", NameFr = "Livraison", Price = 500m, TrackStock = false });
        await db.SaveChangesAsync();
        return (connection, db);
    }

    private static async Task PushAsync(PosDbContext db, string device, params SalePush[] sales)
    {
        var result = await TillHarness.NewService(db).PushAsync(new PushRequest(device, sales), device, CancellationToken.None);
        Assert.Equal(sales.Length, result.Accepted);
        db.ChangeTracker.Clear();
    }

    private static async Task<decimal> CostPostedAsync(PosDbContext db)
    {
        var cogs = await db.LedgerAccounts.AsNoTracking().SingleAsync(a => a.Code == LedgerAccounts.CostOfGoodsSold);
        return (await db.LedgerEntryLines.AsNoTracking().Where(l => l.AccountId == cogs.Id).Select(l => l.Debit - l.Credit).ToListAsync()).Sum();
    }

    [Fact]
    public async Task Goods_with_no_cost_are_sold_flagged_and_post_no_cost()
    {
        var (connection, db) = await ShopAsync(pipeCost: 0m);
        await using var _ = connection;
        await using var __ = db;

        await PushAsync(db, "C01", TillHarness.Sale("A-0001", 1));

        var line = await db.SaleLines.AsNoTracking().SingleAsync();
        Assert.True(line.CostMissing);
        Assert.Equal(0m, await CostPostedAsync(db));
        // Never blocked: the goods still left the shelf.
        Assert.Equal(9m, (await db.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == TillHarness.Pipe && s.BranchId == TillHarness.Algiers)).StockQty);
    }

    [Fact]
    public async Task Costed_goods_and_services_are_not_flagged()
    {
        var (connection, db) = await ShopAsync(pipeCost: 600m);
        await using var _ = connection;
        await using var __ = db;

        var sale = TillHarness.Sale("A-0001", 1, paidAmount: 1500m) with
        {
            SubTotalHt = 1500m, GrandTotalTtc = 1500m,
            Lines =
            [
                new SaleLinePush(Guid.NewGuid(), TillHarness.Pipe, "PIPE-90", "Tube 90", 1m, 1000m, 0m, 0m, 1000m, 0m),
                new SaleLinePush(Guid.NewGuid(), Service, "DELIV", "Livraison", 1m, 500m, 0m, 0m, 500m, 0m),
            ],
        };
        await PushAsync(db, "C01", sale);

        Assert.All(await db.SaleLines.AsNoTracking().ToListAsync(), l => Assert.False(l.CostMissing));
        Assert.Equal(600m, await CostPostedAsync(db));
    }

    [Fact]
    public async Task The_report_names_what_sold_without_a_cost_refunds_subtract_and_a_branch_reads_its_own()
    {
        var (connection, db) = await ShopAsync(pipeCost: 0m);
        await using var _ = connection;
        await using var __ = db;

        var first = TillHarness.Sale("A-0001", 1);
        await PushAsync(db, "C01", first, TillHarness.Sale("A-0002", 2), TillHarness.Sale("A-0003", 3));
        await PushAsync(db, "C02", TillHarness.Sale("O-0001", 1));
        var refund = TillHarness.Sale("A-0004", 4) with { Id = Guid.NewGuid(), Status = nameof(SaleStatus.Refunded), RefundOfSaleId = first.Id };
        await PushAsync(db, "C01", refund);

        // The cost is set afterwards: the sales stay flagged, and the report says what they would have cost.
        (await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe)).CostPrice = 600m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var all = await SalesQueries.MissingCostAsync(db, Today, Today, null, null, Algiers, Money, CancellationToken.None);
        Assert.Equal(3000m, all.SalesHt);           // four sales, one refunded
        Assert.Equal(3000m, all.UncostedHt);
        Assert.Equal(4, all.UncostedSales);
        Assert.Equal(0, all.ProductsWithoutCost);   // the pipe has a cost now; the service never needs one
        var row = Assert.Single(all.Rows);
        Assert.Equal(TillHarness.Pipe, row.ProductId);
        Assert.Equal(3m, row.Qty);
        Assert.Equal(3000m, row.RevenueHt);
        Assert.Equal(600m, row.CostNow);
        Assert.Equal(1800m, row.ValueAtCostNow);

        // A branch user reads their own branch, whatever they ask for.
        var oran = await SalesQueries.MissingCostAsync(db, Today, Today, TillHarness.Algiers, TillHarness.Oran, Algiers, Money, CancellationToken.None);
        Assert.Equal(1000m, oran.UncostedHt);
        Assert.Equal(1m, Assert.Single(oran.Rows).Qty);

        // Another period has nothing.
        var yesterday = await SalesQueries.MissingCostAsync(db, Today.AddDays(-1), Today.AddDays(-1), null, null, Algiers, Money, CancellationToken.None);
        Assert.Empty(yesterday.Rows);
        Assert.Equal(0m, yesterday.SalesHt);
    }

    [Fact]
    public async Task A_product_still_without_a_cost_shows_no_estimate_and_is_counted()
    {
        var (connection, db) = await ShopAsync(pipeCost: 0m);
        await using var _ = connection;
        await using var __ = db;
        await PushAsync(db, "C01", TillHarness.Sale("A-0001", 1));

        var report = await SalesQueries.MissingCostAsync(db, Today, Today, null, null, Algiers, Money, CancellationToken.None);
        var row = Assert.Single(report.Rows);
        Assert.Equal(0m, row.CostNow);
        Assert.Null(row.ValueAtCostNow);
        Assert.Equal(1, report.ProductsWithoutCost);
    }

    // ---------- the backfill: sales already on the books when the column arrives ----------

    private const string SqliteBefore = "20261029150947_ManufacturingCostSnapshotProvenance";
    private const string SqlServerBefore = "20261029150951_ManufacturingCostSnapshotProvenance";

    [Fact]
    public async Task Sqlite_upgrade_flags_past_sales_that_posted_no_cost()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await VerifyBackfill(db, SqliteBefore);
    }

    [SqlServerFact]
    public async Task SqlServer_upgrade_flags_past_sales_that_posted_no_cost()
    {
        await using var database = new SqlServerTestDatabase();
        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, PosDbProviders.SqlServer, database.ConnectionString);
        await using var db = new PosDbContext(options.Options);
        await VerifyBackfill(db, SqlServerBefore);
    }

    private static async Task VerifyBackfill(PosDbContext db, string before)
    {
        await db.Database.GetService<IMigrator>().MigrateAsync(before);
        var sqlite = db.Database.IsSqlite();
        object Id(Guid g) => sqlite ? g.ToString().ToUpperInvariant() : g;

        var branch = new Branch { Code = "10", NameFr = "Alger", IsMain = true };
        var tracked = new Product { Sku = "T", NameFr = "Tracked", TrackStock = true };
        var service = new Product { Sku = "S", NameFr = "Service", TrackStock = false };
        var cogs = new LedgerAccount { Code = "600", NameFr = "CMV", Type = LedgerAccountType.Expense };
        var sales = new LedgerAccount { Code = "700", NameFr = "Ventes", Type = LedgerAccountType.Income };
        db.AddRange(branch, tracked, service, cogs, sales);
        Sale NewSale(string reference, long seq) => new()
        {
            Reference = reference, DeviceCode = "C01", LocalSeq = seq, BranchId = branch.Id,
            SoldAtUtc = DateTime.UtcNow, ReceivedAtUtc = DateTime.UtcNow
        };
        var uncosted = NewSale("U", 1);   // posted, no 600 line
        var costed = NewSale("C", 2);     // posted with a 600 line
        var unposted = NewSale("N", 3);   // no entry at all
        db.AddRange(uncosted, costed, unposted);
        LedgerEntry Entry(Sale s, params (LedgerAccount Account, decimal Debit, decimal Credit)[] lines)
        {
            var e = new LedgerEntry { EntryDate = DateTime.UtcNow, BranchId = branch.Id, SourceType = LedgerSources.Sale, SourceId = s.Id };
            foreach (var (account, debit, credit) in lines)
                e.Lines.Add(new LedgerEntryLine { EntryId = e.Id, AccountId = account.Id, BranchId = branch.Id, Debit = debit, Credit = credit });
            return e;
        }
        db.LedgerEntries.AddRange(
            Entry(uncosted, (sales, 0m, 1500m)),
            Entry(costed, (sales, 0m, 1000m), (cogs, 600m, 0m)));
        await db.SaveChangesAsync();

        // The lines as the table stood before the column: written by hand, since the model has it already.
        var lineIds = new Dictionary<string, Guid>();
        async Task Line(string key, Sale sale, Product product)
        {
            var id = Guid.NewGuid();
            lineIds[key] = id;
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO [SaleLines] ([Id], [SaleId], [ProductId], [Sku], [NameSnapshot], [Qty], [UnitPrice], [TaxRate], [LineDiscount], [LineTotalTtc], [LineTaxAmount]) " +
                "VALUES ({0}, {1}, {2}, {3}, {3}, 1, 1000, 0, 0, 1000, 0)",
                Id(id), Id(sale.Id), Id(product.Id), product.Sku);
        }
        await Line("uncosted-tracked", uncosted, tracked);
        await Line("uncosted-service", uncosted, service);
        await Line("costed-tracked", costed, tracked);
        await Line("unposted-tracked", unposted, tracked);
        db.ChangeTracker.Clear();

        await db.Database.MigrateAsync();

        var flags = await db.SaleLines.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.CostMissing);
        Assert.True(flags[lineIds["uncosted-tracked"]]);
        Assert.False(flags[lineIds["uncosted-service"]]);
        Assert.False(flags[lineIds["costed-tracked"]]);
        Assert.False(flags[lineIds["unposted-tracked"]]);
    }
}
