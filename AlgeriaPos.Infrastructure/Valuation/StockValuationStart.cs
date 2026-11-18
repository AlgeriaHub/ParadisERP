using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Valuation;

/// <summary>
/// Takes the weighted-average shadow's opening (P0.4b) once, at start-up: what every location held of
/// each stock-tracked product and what was in transit, at the product's cost price — the only cost the
/// system held before movements carried one. The settings row is written in the same transaction, and
/// its existence is what says the opening was taken, so a company with nothing in stock is not
/// re-opened at every start and the movements in between are never lost to the shadow.
/// <para>Writes only its own two tables. Nothing here reads back into a sale, a cost price or the ledger.</para>
/// </summary>
public static class StockValuationStart
{
    /// <summary>The number of opening rows written; null when the opening already existed.</summary>
    public static async Task<int?> EnsureAsync(PosDbContext db, CancellationToken ct = default)
    {
        if (await db.StockValuationSettings.AnyAsync(ct)) return null;

        await using var tx = await db.BeginWriteScopeAsync(ct);
        if (await db.StockValuationSettings.AnyAsync(ct)) return null;

        var at = DateTime.UtcNow;
        var costs = await db.Products.AsNoTracking().Where(p => p.TrackStock)
            .Select(p => new { p.Id, p.CostPrice }).ToDictionaryAsync(p => p.Id, p => p.CostPrice, ct);

        var held = await db.ProductStocks.AsNoTracking()
            .Select(s => new { s.ProductId, s.BranchId, s.StockQty, s.OnRentQty, s.MaintenanceQty, s.ScrapQty })
            .ToListAsync(ct);
        var rows = new List<StockValuationOpening>();
        foreach (var s in held)
        {
            var qty = s.StockQty + s.OnRentQty + s.MaintenanceQty + s.ScrapQty;
            if (qty == 0 || !costs.TryGetValue(s.ProductId, out var cost)) continue;
            rows.Add(new StockValuationOpening { ProductId = s.ProductId, BranchId = s.BranchId, TakenAtUtc = at, Qty = qty, UnitCost = cost });
        }

        // In transit: the open remainder of each transfer line — company-owned, held at no location.
        var travelling = await db.StockTransferLines.AsNoTracking()
            .Where(l => l.Transfer!.Status == StockTransferStatus.Dispatched || l.Transfer!.Status == StockTransferStatus.PartlyReceived)
            .ToListAsync(ct);
        foreach (var l in travelling)
        {
            if (l.InTransitQty <= 0 || !costs.TryGetValue(l.ProductId, out var cost)) continue;
            rows.Add(new StockValuationOpening { ProductId = l.ProductId, TransferId = l.TransferId, TakenAtUtc = at, Qty = l.InTransitQty, UnitCost = cost });
        }

        db.StockValuationOpenings.AddRange(rows);
        db.StockValuationSettings.Add(new StockValuationSettings { StartedAtUtc = at });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Count;
    }
}
