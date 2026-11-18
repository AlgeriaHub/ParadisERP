using ParadiERP.Domain;
using ParadiERP.Infrastructure.Sync;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Sync;

/// <summary>
/// One cursor serves every replicated table, so a pull may advance it only as far as the
/// shortest truncated page reached. It used to take the highest row returned from any table,
/// which jumped a table that still had rows waiting behind its 500th: the product that never
/// reached the till, on the first sync of any catalogue larger than one page.
/// </summary>
public class PullCursorTests
{
    [Fact]
    public async Task A_later_edit_in_another_table_does_not_skip_the_products_behind_a_full_page()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;

        // Everything the harness seeded sits at or below this; every row added here is above it.
        var from = await db.CurrentVersionAsync();
        var batch = SyncService.PullBatchSize;
        db.Products.AddRange(Enumerable.Range(1, batch + 1).Select(i => new Product
        {
            Sku = $"P-{i:D4}", NameFr = $"Article {i}", Price = 100m, Version = from + i,
        }));
        // One cashier edited after the whole catalogue — the row that dragged the cursor past product 501.
        db.Users.Add(new User { Username = "caissier-2", FullName = "Caissier", Role = UserRole.Cashier, BranchId = TillHarness.Algiers, Version = from + batch + 2 });
        (await db.SyncCounters.SingleAsync(c => c.Id == 1)).Value = from + batch + 2;
        await db.SaveChangesAsync();

        var sync = TillHarness.NewService(db);
        var first = await sync.PullAsync(from, "C01", CancellationToken.None);

        Assert.Equal(from + batch, first.Cursor);
        Assert.True(first.HasMore);
        Assert.Equal(batch, first.Products.Count);
        // Held back rather than lost: it is past the cursor, so the next pull brings it once.
        Assert.DoesNotContain(first.Users, u => u.Username == "caissier-2");

        var second = await sync.PullAsync(first.Cursor, "C01", CancellationToken.None);

        Assert.False(second.HasMore);
        Assert.Equal($"P-{batch + 1:D4}", Assert.Single(second.Products).Sku);
        Assert.Single(second.Users, u => u.Username == "caissier-2");
        Assert.Equal(batch + 1, first.Products.Concat(second.Products).Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task A_pull_whose_pages_all_came_back_whole_goes_straight_to_the_ceiling()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;

        var from = await db.CurrentVersionAsync();
        db.Products.Add(new Product { Sku = "P-ONE", NameFr = "Article", Price = 100m, Version = from + 1 });
        db.Users.Add(new User { Username = "caissier-3", FullName = "Caissier", Role = UserRole.Cashier, BranchId = TillHarness.Algiers, Version = from + 2 });
        (await db.SyncCounters.SingleAsync(c => c.Id == 1)).Value = from + 2;
        await db.SaveChangesAsync();

        var pull = await TillHarness.NewService(db).PullAsync(from, "C01", CancellationToken.None);

        Assert.Equal(from + 2, pull.Cursor);
        Assert.False(pull.HasMore);
        Assert.Single(pull.Products, p => p.Sku == "P-ONE");
        Assert.Single(pull.Users, u => u.Username == "caissier-3");
    }
}
