using AlgeriaPos.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Stock;

/// <summary>
/// Several tills at one branch selling the same article at the same moment, on SQL Server.
/// <para>
/// A push read the stock row, took the line's quantity off in memory and wrote the figure back.
/// Two pushes that both read 100 both wrote 99: two units left the shop and one came off the
/// shelf. The movements were all recorded — they are inserts — so the history and the stored
/// figure disagreed with nothing to explain it. SQLite cannot show this (one writer at a time),
/// which is why it is a SQL Server test.
/// </para>
/// </summary>
[Collection(HostCollection.Name)]
public class ConcurrentStockTests
{
    private const decimal Opening = 1000m;
    private const int Tills = 8;
    private const int SalesPerTill = 6;

    [SqlServerFact]
    public async Task Tills_pushing_at_the_same_moment_take_every_unit_off_the_shelf()
    {
        await using var fleet = await TillFleet.StartAsync(Tills, Opening);

        await fleet.PushAllAtOnceAsync(SalesPerTill, (till, seq) => fleet.Sale(till, seq));

        const int sold = Tills * SalesPerTill;
        await using var db = fleet.Open();
        var movements = await db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == fleet.Product && m.BranchId == fleet.Branch && m.Reason == "sale")
            .Select(m => new { m.QtyDelta, m.BalanceAfter })
            .ToListAsync();
        Assert.Equal(sold, movements.Count);
        Assert.Equal(-sold, movements.Sum(m => m.QtyDelta));

        // The shelf: every unit sold is a unit gone.
        var stock = await db.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == fleet.Product && s.BranchId == fleet.Branch);
        Assert.Equal(Opening - sold, stock.StockQty);

        // And each sale saw the figure the one before it left: no two sales started from the same count.
        Assert.Equal(
            Enumerable.Range(1, sold).Select(n => Opening - n).OrderBy(x => x),
            movements.Select(m => m.BalanceAfter).OrderBy(x => x));
    }
}
