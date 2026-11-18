using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Stock;

/// <summary>
/// The two rules that make <see cref="StockRows"/> safe with two writers at once, pinned where
/// SQLite can pin them. The two writers themselves are <see cref="ConcurrentStockTests"/>, on SQL Server.
/// </summary>
public class StockRowsTests
{
    [Fact]
    public async Task Outside_a_transaction_it_refuses_rather_than_take_a_lock_that_ends_at_once()
    {
        await using var connection = await OpenAsync();
        await using var db = Context(connection);
        var (product, branch) = await SeedAsync(db, 10m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new StockRows(db).GetAsync(product, branch));
    }

    [Fact]
    public async Task A_row_the_request_loaded_before_the_lock_is_read_again_under_it()
    {
        await using var connection = await OpenAsync();
        await using var db = Context(connection);
        var (product, branch) = await SeedAsync(db, 10m);

        // Loaded early — the way a handler reads a row to validate before it starts writing.
        var early = await db.ProductStocks.SingleAsync(s => s.ProductId == product && s.BranchId == branch);
        Assert.Equal(10m, early.StockQty);

        // Another till's sale lands in between.
        await using (var other = Context(connection))
        {
            (await other.ProductStocks.SingleAsync()).StockQty = 7m;
            await other.SaveChangesAsync();
        }

        await using var tx = await db.BeginWriteScopeAsync();
        var stock = await new StockRows(db).GetAsync(product, branch);

        Assert.Same(early, stock);
        Assert.Equal(7m, stock.StockQty);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        return connection;
    }

    private static PosDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

    private static async Task<(Guid Product, Guid Branch)> SeedAsync(PosDbContext db, decimal qty)
    {
        var branch = new Branch { NameFr = "Alger" };
        var product = new Product { Sku = "TUBE-50", NameFr = "Tube", Price = 10m, TrackStock = true };
        db.Branches.Add(branch);
        db.Products.Add(product);
        db.ProductStocks.Add(new ProductStock { ProductId = product.Id, BranchId = branch.Id, StockQty = qty });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (product.Id, branch.Id);
    }
}
