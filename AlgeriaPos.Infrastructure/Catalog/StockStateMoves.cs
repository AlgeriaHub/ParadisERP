using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.FixedAssets;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgeriaPos.Infrastructure.Catalog;

/// <summary>What a state move did: the row as it stands after it, or why it could not move (404 or 409, in words).</summary>
public sealed record StockStateMoveResult(ProductStock? Stock, int Status, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// One quantity moving between two states of one product at one branch — the body of
/// <c>POST /api/catalog/stock/state</c>, kept in one place so a maintenance job's completion (Phase M1,
/// docs/maintenance/plan.md §2.2) moves its units with exactly the same rules, the same <see cref="StockMovement"/>
/// and the same ledger entry as the manual route, and never a copy of them.
/// <para>
/// The caller has checked the states (on rent and written off are refused before this) and holds a transaction: the
/// bucket is read under the version lock through <paramref name="rows"/>, so two moves out of one bucket cannot both
/// pass against the same count. The caller saves and commits.
/// </para>
/// </summary>
public static class StockStateMoves
{
    /// <param name="reason">The operator's words: the movement's label after its prefix (lower-cased, cut to fit) and the
    /// ledger entry's description, exactly as the manual route has always written them.</param>
    /// <param name="documentNumber">The number of the document behind the move (a job's <c>MJ-…</c>); the manual route has
    /// none. When there is no reason, the entry's description names it instead.</param>
    public static async Task<StockStateMoveResult> MoveAsync(
        PosDbContext db, LedgerPoster ledger, StockRows rows,
        Guid productId, Guid branchId, StockState from, StockState to, decimal qty,
        string? reason, string? documentNumber, Guid? userId, string? userName, CancellationToken ct = default,
        ILogger? log = null)
    {
        var stock = await rows.FindAsync(productId, branchId, ct);
        if (stock is null) return new(null, 404, "This product has no stock row at that branch.");

        var held = stock.Held(from);
        if (held < qty)
            return new(stock, 409, $"Only {held:0.##} is {from}, so {qty:0.##} cannot be moved.");

        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);

        // WrittenOff is the one destination that is not a bucket: the quantity leaves the
        // business entirely. That is what separates a loss from a state — and the rental
        // module moves the same buckets on the same rules, so the transition lives on the
        // entity where both can reach one copy of it.
        stock.Move(from, to, qty);

        var documentId = Guid.NewGuid();
        var movement = new StockMovement
        {
            ProductId = productId,
            BranchId = branchId,
            // Only a write-off changes what the company holds; the rest is the same goods
            // in a different state, so the ledger of quantities must not double-count them.
            QtyDelta = to == StockState.WrittenOff ? -qty : 0,
            BalanceAfter = stock.StockQty,
            DocumentId = documentId,
            Reason = StockMovement.Label($"{from}->{to}".ToLowerInvariant(), reason?.ToLowerInvariant()),
            Kind = to == StockState.WrittenOff ? StockMovementKind.WriteOff : StockMovementKind.StateChange,
            UnitCost = product?.CostPrice,
            UserId = userId,
            UserName = userName,
            DocumentNumber = documentNumber,
            MovedQty = qty
        };
        db.StockMovements.Add(movement);

        // At the cost price; once sales are costed at the weighted average (C-01), at the average: a write-off
        // takes the goods out at it, and a move into or out of scrap carries them between 310 and 318 at it. A job's
        // completion goes through here too, so it follows the same rule. Never refused: no average, the cost price.
        var value = qty * (product?.CostPrice ?? 0m);
        var costs = await new LiveSaleCosting(db).MovementCostsOrFallbackAsync(
            [new PlannedMovement(movement, product?.CostPrice ?? 0m)], log ?? NullLogger.Instance, ct);
        if (costs?.GetValueOrDefault(movement.Id) is { } cost)
        {
            movement.UnitCost = cost.UnitCost;
            movement.CostBasis = cost.Basis;
            value = cost.Value;
        }

        // A move within the owned-stock states is not an accounting event at all and
        // produces no entry; scrap and write-off are. StockPosting decides which.
        // REN-12 (Phase M7a): a hire product held by the fixed-asset register leaves its pool at its net book value — into
        // scrap, or written off to the disposal account — and its scrap leaves at the value the register carries it at.
        // Off (the default), or any other product, today's entry.
        var (registered, assetEntry) = await new FixedAssetBook(db).StateChangeAsync(
            new FixedAssetEvent(productId, branchId, qty, product?.Sku, documentId, documentNumber, reason, userName), from, to, ct);
        var stateEntry = registered ? assetEntry : StockPosting.StateChange(
            documentId, branchId, from, to, value, product?.Sku, reason ?? documentNumber);
        if (stateEntry is not null) await ledger.PostAsync(stateEntry, ct);

        // Back from maintenance is stock a till can sell again: its pull watches the product's version.
        if (await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct) is Product touched)
            await db.TouchAsync(touched, ct);
        return new(stock, 200, null);
    }
}
