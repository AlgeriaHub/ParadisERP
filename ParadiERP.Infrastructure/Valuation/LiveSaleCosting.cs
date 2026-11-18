using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ParadiERP.Infrastructure.Valuation;

/// <summary>One product's cost on a sale or refund under the weighted average (C-01): per unit, and where it came from.</summary>
public sealed record LiveLineCost(decimal UnitCost, SaleCostBasis Basis);

/// <summary>
/// A movement a stock document is about to write (C-01 for the other documents): the row itself, built and not yet saved,
/// what its document values it at under today's rule (<paramref name="ReferenceUnit"/>: the cost price, or a transfer
/// line's cost) — the figure it falls back to — and, for a transfer step, its transfer (the row's document is the step,
/// which the database does not hold yet).
/// </summary>
public sealed record PlannedMovement(StockMovement Movement, decimal ReferenceUnit, Guid? TransferId = null);

/// <summary>
/// What one planned movement is worth under the weighted average: <paramref name="Value"/> is what its entry posts for it,
/// <paramref name="UnitCost"/> the same per unit (10 decimals, as the movement keeps it), <paramref name="Basis"/> where it
/// came from.
/// </summary>
public sealed record LiveMovementCost(decimal Value, decimal UnitCost, SaleCostBasis Basis)
{
    /// <summary>Today's rule, said: the reference cost, flagged as a fallback.</summary>
    public static LiveMovementCost Fallback(decimal qty, decimal referenceUnit) =>
        new(qty * referenceUnit, referenceUnit, SaleCostBasis.CostPriceFallback);
}

/// <summary>
/// The weighted average at the moment a sale reaches the server, once sales are switched to it (C-01). Read only:
/// it answers a figure and writes nothing; the sale's own movement, line and entry are written by the sync.
///
/// <para>
/// <b>Why a per-request replay and not a stored running average.</b> The figure must be the shadow's own: the same
/// rules (<see cref="WeightedAverageEngine"/>), over the same rows (<see cref="StockValuationShadow.LoadMovementsAsync"/>),
/// so the cost a sale posts is the cost the report shows for it, and a changed choice cannot leave a stored figure
/// behind. A running average kept by the twelve movement writers would be a second copy of those rules in twelve
/// places. Instead each product a request sells is replayed once — its opening and its movements since — and kept
/// for the rest of the request; every later sale in the same push batch only reads the movements written since
/// (one count and, when there are new ones, one fetch), so a batch of N sales of a product replays its history once,
/// not N times. A movement found written out of time order (its count does not add up) makes that product replay
/// from the opening again, so the figure never drifts from the report's.
/// </para>
/// <para>
/// A request's cache lives as long as the scoped sync service that owns it. The cost of the first sale of a product
/// in a request grows with that product's movements since the opening; if that ever becomes the bottleneck, the
/// next step is a stored checkpoint of each pool, not a second set of rules.
/// </para>
/// </summary>
public sealed class LiveSaleCosting(PosDbContext db)
{
    private sealed class ProductState(WeightedAverageEngine engine)
    {
        public WeightedAverageEngine Engine { get; } = engine;
        public HashSet<Guid> Applied { get; } = [];
        public ShadowMovement? Last { get; private set; }

        public void Apply(ShadowMovement m)
        {
            Engine.Apply(m);
            Applied.Add(m.Id);
            Last = m;
        }
    }

    private readonly Dictionary<Guid, ProductState> cache = new();
    private (ShadowPolicy Policy, DateTime Started, DateTime? Since)? cacheKey;

    /// <summary>How many products this request replayed from the opening (for the tests that pin the caching).</summary>
    public int Replays { get; private set; }

    /// <summary>Forgets every product: after a failure, the next sale starts from what the database holds.</summary>
    public void Reset()
    {
        cache.Clear();
        cacheKey = null;
    }

    /// <summary>
    /// The cost per unit of each product of a sale or refund at <paramref name="branchId"/>, or null when sales are
    /// costed at the cost price (the default: the caller keeps today's rule). <paramref name="products"/>: per product,
    /// the quantity the document moves (summed over its lines) and the product's cost price, which is what a product
    /// with no average yet goes out at, flagged <see cref="SaleCostBasis.CostPriceFallback"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, LiveLineCost>?> CostsAsync(
        Guid branchId, bool isRefund, Guid? refundOfSaleId,
        IReadOnlyDictionary<Guid, (decimal Qty, decimal CostPrice)> products, CancellationToken ct)
    {
        var settings = await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null || settings.SalesCost != SalesCostMethod.WeightedAverage) return null;
        var result = new Dictionary<Guid, LiveLineCost>();
        if (products.Count == 0) return result;

        var policy = StockValuationShadow.PolicyOf(settings);
        var key = (policy, settings.StartedAtUtc, settings.SalesCostChangedAtUtc);
        if (cacheKey != key)
        {
            cache.Clear();
            cacheKey = key;
        }
        await CatchUpAsync(products.Keys.ToList(), settings.StartedAtUtc, policy, ct);

        // A refund of a sale the replay never saw (before the opening) comes back at what its line posted.
        var lineCosts = isRefund && refundOfSaleId is Guid original
            ? await StockValuationShadow.LineCostsAsync(db, [original], ct)
            : new Dictionary<(Guid, Guid), decimal>();

        foreach (var (product, (qty, costPrice)) in products)
        {
            var engine = cache[product].Engine;
            if (qty <= 0)
            {
                // Nothing leaves or comes back: today's rule, said.
                result[product] = new LiveLineCost(costPrice, SaleCostBasis.CostPriceFallback);
                continue;
            }

            decimal value;
            SaleCostBasis basis;
            if (isRefund)
            {
                if (refundOfSaleId is Guid sale && lineCosts.TryGetValue((sale, product), out var posted))
                    engine.KnowLineCost(sale, product, posted);
                value = engine.PreviewRefund(product, branchId, qty, refundOfSaleId, costPrice, out var note, out var noAverage);
                basis = noAverage ? SaleCostBasis.CostPriceFallback
                    : note is WeightedAverageShadow.NoteRefundAtSaleCost or WeightedAverageShadow.NoteRefundAtPostedCost
                        ? SaleCostBasis.SaleCost
                        : SaleCostBasis.WeightedAverage;
            }
            else
            {
                value = engine.PreviewOut(product, branchId, qty, costPrice, out var noAverage);
                basis = noAverage ? SaleCostBasis.CostPriceFallback : SaleCostBasis.WeightedAverage;
            }
            result[product] = new LiveLineCost(Math.Round(value / qty, 10, MidpointRounding.AwayFromZero), basis);
        }
        return result;
    }

    /// <summary>
    /// What each movement of one stock document — an adjustment, a write-off, a state change, a transfer step, a rental
    /// return — is worth once sales are costed at the weighted average, or null when they are costed at the cost price
    /// (the default: the caller keeps today's rule). Read only, like <see cref="CostsAsync"/>.
    /// <para>
    /// The products of the document are replayed from the opening with the shadow's own engine and loader, then the
    /// document's own rows are applied to that replay in the replay's order, exactly as the report will apply them once
    /// they are saved. So every row is valued at what the replay will value it at: a second row of the same product
    /// sees what the first took out, and the book value of the stock accounts stays the shadow's. A row the replay
    /// moves no value for — a state change, or a transfer step under one average for the company — is valued at what its
    /// units would leave their pool at now (<see cref="WeightedAverageEngine.PreviewOut"/>), without taking them out.
    /// No average: the row's reference cost, flagged <see cref="SaleCostBasis.CostPriceFallback"/>.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, LiveMovementCost>?> MovementCostsAsync(
        IReadOnlyList<PlannedMovement> planned, CancellationToken ct)
    {
        var settings = await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null || settings.SalesCost != SalesCostMethod.WeightedAverage) return null;
        var result = new Dictionary<Guid, LiveMovementCost>();
        if (planned.Count == 0) return result;

        var ids = planned.Select(p => p.Movement.ProductId).Distinct().ToList();
        var openings = await StockValuationShadow.LoadOpeningsAsync(db, ids, ct);
        var (movements, lineCosts) = await StockValuationShadow.LoadMovementsAsync(db, ids, settings.StartedAtUtc, ct);
        var engine = new WeightedAverageEngine(StockValuationShadow.PolicyOf(settings), lineCosts);
        engine.Open(openings);
        foreach (var m in WeightedAverageEngine.InReplayOrder(movements)) engine.Apply(m);
        Replays++;

        var plans = planned.ToDictionary(p => p.Movement.Id);
        var rows = planned.Select(p => new ShadowMovement(
            p.Movement.Id, p.Movement.CreatedAtUtc, p.Movement.ProductId, p.Movement.BranchId, p.Movement.Kind,
            p.Movement.QtyDelta, p.Movement.MovedQty ?? Math.Abs(p.Movement.QtyDelta), p.ReferenceUnit,
            p.Movement.DocumentNumber, p.Movement.DocumentId, p.Movement.SaleId, null, null, p.TransferId));
        foreach (var m in WeightedAverageEngine.InReplayOrder(rows))
        {
            var reference = plans[m.Id].ReferenceUnit;
            var qty = m.MovedQty;
            if (qty <= 0)
            {
                result[m.Id] = new LiveMovementCost(0m, reference, SaleCostBasis.CostPriceFallback);
                continue;
            }
            // Before the row is applied: what its units would leave the pool at (the figure of a row that moves no
            // value), and whether goods coming in at the average find one.
            var preview = engine.PreviewOut(m.ProductId, m.BranchId, qty, reference, out var previewNoAverage);
            var inboundNoAverage = m.QtyDelta > 0 && !IsTransferStep(m.Kind) && engine.PoolOf(m.ProductId, m.BranchId)?.Average is null;
            var applied = engine.Apply(m);

            var (value, noAverage) = applied.Moved
                ? (applied.Value, applied.NoAverage || inboundNoAverage)
                : (preview, previewNoAverage);
            result[m.Id] = new LiveMovementCost(value, Math.Round(value / qty, 10, MidpointRounding.AwayFromZero),
                noAverage ? SaleCostBasis.CostPriceFallback : SaleCostBasis.WeightedAverage);
        }
        return result;
    }

    /// <summary>Steps that bring goods in out of a transfer or a return elsewhere: the replay's own "no average" note says it for them.</summary>
    private static bool IsTransferStep(StockMovementKind kind) =>
        kind is StockMovementKind.TransferIn or StockMovementKind.TransferCancel or StockMovementKind.RentalReturnTransfer;

    /// <summary>
    /// <see cref="MovementCostsAsync"/>, never throwing: a stock document that physically happened is never refused
    /// because its average could not be worked out (rule 1). On a failure every row falls back to its reference cost,
    /// flagged <see cref="SaleCostBasis.CostPriceFallback"/> so the page counts it, and the failure is logged.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, LiveMovementCost>?> MovementCostsOrFallbackAsync(
        IReadOnlyList<PlannedMovement> planned, ILogger? logger, CancellationToken ct)
    {
        try
        {
            return await MovementCostsAsync(planned, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex,
                "Stock document {DocumentId}: the weighted average could not be worked out; its goods are valued at their reference cost",
                planned.FirstOrDefault()?.Movement.DocumentId);
            return planned.ToDictionary(p => p.Movement.Id,
                p => LiveMovementCost.Fallback(p.Movement.MovedQty ?? Math.Abs(p.Movement.QtyDelta), p.ReferenceUnit));
        }
    }

    private async Task CatchUpAsync(List<Guid> ids, DateTime started, ShadowPolicy policy, CancellationToken ct)
    {
        // How many movements each product has had since the opening: what was applied plus what is new must add up,
        // or a movement was written out of time order and the product replays from the opening.
        var counts = await db.StockMovements.AsNoTracking()
            .Where(m => ids.Contains(m.ProductId) && m.CreatedAtUtc > started)
            .GroupBy(m => m.ProductId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var fresh = new List<Guid>();
        var behind = new List<Guid>();
        foreach (var id in ids)
        {
            if (!cache.TryGetValue(id, out var state)) fresh.Add(id);
            else if (state.Applied.Count != counts.GetValueOrDefault(id)) behind.Add(id);
        }

        if (behind.Count > 0)
        {
            var since = behind.Min(id => cache[id].Last?.AtUtc ?? started);
            var (news, lineCosts) = await StockValuationShadow.LoadMovementsAsync(db, behind, since.AddTicks(-1), ct);
            foreach (var id in behind)
            {
                var state = cache[id];
                var mine = news.Where(m => m.ProductId == id && !state.Applied.Contains(m.Id)).ToList();
                if (mine.Count + state.Applied.Count != counts.GetValueOrDefault(id) || mine.Any(m => Before(m, state.Last)))
                {
                    cache.Remove(id);
                    fresh.Add(id);
                    continue;
                }
                foreach (var ((sale, product), cost) in lineCosts)
                    if (product == id) state.Engine.KnowLineCost(sale, product, cost);
                foreach (var m in WeightedAverageEngine.InReplayOrder(mine)) state.Apply(m);
            }
        }

        if (fresh.Count == 0) return;
        var openings = await StockValuationShadow.LoadOpeningsAsync(db, fresh, ct);
        var (movements, originalLineCost) = await StockValuationShadow.LoadMovementsAsync(db, fresh, started, ct);
        foreach (var id in fresh)
        {
            var engine = new WeightedAverageEngine(policy, originalLineCost.Where(x => x.Key.Item2 == id).ToDictionary(x => x.Key, x => x.Value));
            engine.Open(openings.Where(o => o.ProductId == id));
            var state = new ProductState(engine);
            foreach (var m in WeightedAverageEngine.InReplayOrder(movements.Where(m => m.ProductId == id))) state.Apply(m);
            cache[id] = state;
            Replays++;
        }
    }

    /// <summary>Whether <paramref name="m"/> comes before <paramref name="last"/> in the replay's order.</summary>
    private static bool Before(ShadowMovement m, ShadowMovement? last)
    {
        if (last is null) return false;
        if (m.AtUtc != last.AtUtc) return m.AtUtc < last.AtUtc;
        if (m.QtyDelta != last.QtyDelta) return m.QtyDelta < last.QtyDelta;
        return m.Id.CompareTo(last.Id) < 0;
    }
}
