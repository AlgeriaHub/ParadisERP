using AlgeriaPos.Domain;

namespace AlgeriaPos.Infrastructure.Valuation;

/// <summary>A stock movement as the shadow reads it, with the links it needs resolved by the loader.</summary>
public sealed record ShadowMovement(
    Guid Id, DateTime AtUtc, Guid ProductId, Guid BranchId, StockMovementKind Kind,
    decimal QtyDelta, decimal MovedQty, decimal? UnitCost, string? DocumentNumber, Guid? DocumentId,
    Guid? SaleId,
    /// <summary>For a refund: the sale it gives back.</summary>
    Guid? RefundOfSaleId,
    /// <summary>For a production issue or return: its order.</summary>
    Guid? OrderId,
    /// <summary>For a transfer movement: its transfer.</summary>
    Guid? TransferId);

public sealed record ShadowOpening(Guid ProductId, Guid? BranchId, Guid? TransferId, decimal Qty, decimal UnitCost);

public sealed record ShadowPolicy(StockValuationScope Scope, RefundValuation RefundCost, int Decimals);

/// <summary>One movement as valued, for the product detail.</summary>
public sealed record ShadowLine(
    ShadowMovement Movement, decimal QtyIn, decimal QtyOut, decimal? ShadowUnitCost, decimal ShadowValue,
    decimal PoolQtyAfter, decimal? AverageAfter, decimal Gap, IReadOnlyList<string> Notes);

/// <summary>What one pool — a product, or a product at one location — went through.</summary>
public sealed class ShadowPool
{
    public Guid ProductId { get; init; }
    /// <summary>The location under the per-location scope; null for the company's pool.</summary>
    public Guid? BranchId { get; init; }

    public decimal Qty { get; internal set; }
    public decimal Value { get; internal set; }
    /// <summary>The last average the pool held while it had stock: what goes out at while it has none.</summary>
    public decimal? LastAverage { get; internal set; }
    public decimal? Average => Qty > 0 ? Value / Qty : LastAverage;

    public decimal OpeningQty { get; internal set; }
    public decimal OpeningValue { get; internal set; }
    public decimal InQty { get; internal set; }
    public decimal InValue { get; internal set; }
    public decimal OutQty { get; internal set; }
    public decimal OutValue { get; internal set; }
    /// <summary>Into this location from another one (per-location scope only): transfers, rental returns elsewhere.</summary>
    public decimal MovedInQty { get; internal set; }
    public decimal MovedInValue { get; internal set; }
    public decimal MovedOutQty { get; internal set; }
    public decimal MovedOutValue { get; internal set; }
    /// <summary>
    /// C-16: goods that went out below zero at the average of the moment, settled when an inflow later
    /// covered them at its own cost — the inflow's value for those units minus what they went out at.
    /// Positive when the goods turned out dearer. Kept out of the pool's value, reported per product.
    /// </summary>
    public decimal NegativeStockGap { get; internal set; }

    public decimal PostedCostOfSales { get; internal set; }
    public decimal ShadowCostOfSales { get; internal set; }
    public int OutputsAtReference { get; internal set; }
    public int OutflowsWithoutAverage { get; internal set; }
    public int Unclassified { get; internal set; }
}

public sealed record ShadowResult(
    IReadOnlyList<ShadowPool> Pools,
    /// <summary>Per product, the value still travelling on open transfers (per-location scope; 0 under the company scope, where it is in the pool).</summary>
    IReadOnlyDictionary<Guid, decimal> InTransitValue,
    IReadOnlyList<ShadowLine> Lines,
    /// <summary>Every negative-stock gap settled (C-16), by the movement that covered it, in replay order.</summary>
    IReadOnlyList<ShadowGap>? Gaps = null);

/// <summary>
/// What <see cref="WeightedAverageEngine.Apply"/> did with one movement: <paramref name="Value"/> moved in or out of a pool
/// (<paramref name="Moved"/> false when it moved none: a state change, or a transfer under the company scope), and whether
/// it went out with no average, at its own reference cost (<paramref name="NoAverage"/>).
/// </summary>
public readonly record struct ShadowApplied(decimal Value, bool Moved, bool NoAverage);

/// <summary>A negative-stock gap (C-16) settled by an inflow: the inflow's value for the covered units less what they went out at.</summary>
public sealed record ShadowGap(
    Guid MovementId, DateTime AtUtc, Guid ProductId, Guid BranchId, StockMovementKind Kind, string? DocumentNumber, decimal Gap);

/// <summary>
/// The moving weighted average, replayed from the opening over the movements in order (P0.4b). Pure:
/// it reads what it is given and returns figures; it touches no database, no cost price and no ledger.
/// <list type="bullet">
/// <item>Goods in at a cost raise or lower the average; goods out leave at it, rounded to the policy's
/// decimals, and the last unit out of a pool takes whatever value is left, so rounding never strands value.</item>
/// <item>Under the company scope transfers and rental returns elsewhere change nothing: the goods stay the
/// company's. Under the per-location scope they leave at the source's average and arrive at that cost.</item>
/// <item>Stock below zero goes out at the last average; the inflow that covers it settles the difference
/// as the negative-stock gap (C-16).</item>
/// <item>Goods out with no average yet (nothing came in since the opening) leave at the movement's own
/// reference cost, flagged.</item>
/// </list>
/// The rules live in <see cref="WeightedAverageEngine"/>, which the live sale costing (C-01) runs too, so the
/// cost a sale posts and the shadow's figure for it come from one set of rules.
/// </summary>
public static class WeightedAverageShadow
{
    public const string NoteInternal = "internal";
    public const string NoteNoAverage = "no_average";
    public const string NoteOutputAtReference = "output_at_reference";
    public const string NoteRefundAtSaleCost = "refund_at_sale_cost";
    public const string NoteRefundAtPostedCost = "refund_at_posted_cost";
    public const string NoteRefundAtAverage = "refund_at_average";
    public const string NoteReturnAtIssueCost = "return_at_issue_cost";
    public const string NoteUnclassified = "unclassified";
    public const string NoteNegativeStock = "negative_stock";

    /// <param name="originalLineCost">For a refund whose sale came before the opening: the cost per unit its sale line posted, by (sale, product).</param>
    /// <param name="detailProductId">The product whose movements are returned line by line; null returns none.</param>
    public static ShadowResult Run(
        ShadowPolicy policy, IEnumerable<ShadowOpening> openings, IEnumerable<ShadowMovement> movements,
        IReadOnlyDictionary<(Guid Sale, Guid Product), decimal> originalLineCost, Guid? detailProductId = null)
    {
        var engine = new WeightedAverageEngine(policy, originalLineCost, detailProductId);
        engine.Open(openings);
        foreach (var m in WeightedAverageEngine.InReplayOrder(movements)) engine.Apply(m);
        return engine.Result();
    }
}

/// <summary>
/// The replay's state, one movement at a time: what <see cref="WeightedAverageShadow.Run"/> folds over the whole
/// history, and what the live sale costing (C-01) keeps per product within a request and asks "what would this
/// sale take out at?" before the sale's own movement exists (<see cref="PreviewOut"/>, <see cref="PreviewRefund"/>).
/// Movements must be applied in <see cref="InReplayOrder"/>. Pure, like the shadow.
/// </summary>
public sealed class WeightedAverageEngine
{
    private readonly ShadowPolicy policy;
    private readonly bool byLocation;
    private readonly int decimals;
    private readonly Guid? detailProductId;
    private readonly Dictionary<(Guid, Guid?), ShadowPool> pools = new();
    private readonly Dictionary<(Guid Product, Guid Transfer), ShadowPool> transit = new();
    private readonly Dictionary<(Guid, Guid), (decimal Qty, decimal Value)> saleOut = new();
    private readonly Dictionary<(Guid, Guid), (decimal Qty, decimal Value)> issued = new();
    // Per-location rental returns elsewhere: the value the out row took, for the in row of the same note.
    private readonly Dictionary<(Guid, Guid), (decimal Qty, decimal Value)> pairs = new();
    private readonly Dictionary<(Guid Sale, Guid Product), decimal> originalLineCost;
    private readonly List<ShadowLine> lines = new();
    private readonly List<ShadowGap> gaps = new();

    public WeightedAverageEngine(
        ShadowPolicy policy, IReadOnlyDictionary<(Guid Sale, Guid Product), decimal>? originalLineCost = null, Guid? detailProductId = null)
    {
        this.policy = policy;
        byLocation = policy.Scope == StockValuationScope.Location;
        decimals = Math.Clamp(policy.Decimals, 0, 6);
        this.detailProductId = detailProductId;
        this.originalLineCost = originalLineCost is null ? new() : new(originalLineCost);
    }

    public ShadowPolicy Policy => policy;

    /// <summary>
    /// Two rows of one act can share an instant: the side that leaves goes first, so a pair moves value that
    /// exists; then the id, so the replay is the same every time.
    /// </summary>
    public static IOrderedEnumerable<ShadowMovement> InReplayOrder(IEnumerable<ShadowMovement> movements) =>
        movements.OrderBy(m => m.AtUtc).ThenBy(m => m.QtyDelta).ThenBy(m => m.Id);

    /// <summary>For a refund whose sale came before the opening: the cost per unit its line posted.</summary>
    public void KnowLineCost(Guid sale, Guid product, decimal unitCost) => originalLineCost[(sale, product)] = unitCost;

    private ShadowPool Pool(Guid product, Guid? branch)
    {
        var key = (product, byLocation ? branch : null);
        if (!pools.TryGetValue(key, out var pool)) pools[key] = pool = new ShadowPool { ProductId = product, BranchId = key.Item2 };
        return pool;
    }

    private ShadowPool Transit(Guid product, Guid transfer)
    {
        if (!transit.TryGetValue((product, transfer), out var pool)) transit[(product, transfer)] = pool = new ShadowPool { ProductId = product };
        return pool;
    }

    /// <summary>The pool a sale of <paramref name="product"/> at <paramref name="branch"/> draws on; null while nothing touched it.</summary>
    public ShadowPool? PoolOf(Guid product, Guid branch) =>
        pools.TryGetValue((product, byLocation ? branch : null), out var pool) ? pool : null;

    public void Open(IEnumerable<ShadowOpening> openings)
    {
        foreach (var o in openings)
        {
            if (o.Qty == 0) continue;
            var pool = byLocation && o.TransferId is Guid t ? Transit(o.ProductId, t) : Pool(o.ProductId, o.BranchId);
            var value = o.Qty * o.UnitCost;
            pool.Qty += o.Qty; pool.Value += value;
            pool.OpeningQty += o.Qty; pool.OpeningValue += value;
            if (pool.Qty > 0) pool.LastAverage = pool.Value / pool.Qty;
        }
    }

    public ShadowApplied Apply(ShadowMovement m)
    {
        var notes = new List<string>();
        decimal qtyIn = 0, qtyOut = 0, value = 0, gap = 0;
        decimal? unit = null;
        var pool = Pool(m.ProductId, m.BranchId);

        switch (m.Kind)
        {
            case StockMovementKind.TransferOut when !byLocation:
            case StockMovementKind.TransferIn when !byLocation:
            case StockMovementKind.TransferCancel when !byLocation:
            case StockMovementKind.RentalReturnTransfer when !byLocation:
                // The company still holds them: in transit, or at the other location.
                notes.Add(WeightedAverageShadow.NoteInternal);
                if (m.QtyDelta < 0) qtyOut = -m.QtyDelta; else qtyIn = m.QtyDelta;
                break;

            case StockMovementKind.TransferOut:
            {
                qtyOut = -m.QtyDelta;
                value = Out(pool, qtyOut, m.UnitCost, decimals, notes, moved: true);
                if (m.TransferId is Guid t) In(Transit(m.ProductId, t), qtyOut, value, moved: true, out _);
                break;
            }
            case StockMovementKind.TransferIn:
            case StockMovementKind.TransferCancel:
            {
                qtyIn = m.QtyDelta;
                value = m.TransferId is Guid t ? Out(Transit(m.ProductId, t), qtyIn, m.UnitCost, decimals, notes, moved: true)
                    : qtyIn * (m.UnitCost ?? 0m);
                In(pool, qtyIn, value, moved: true, out gap);
                break;
            }
            case StockMovementKind.TransferLoss:
            {
                // Delta 0: the goods left the source at dispatch. What leaves now is the company's.
                qtyOut = m.MovedQty;
                var from = byLocation && m.TransferId is Guid t ? Transit(m.ProductId, t) : pool;
                // Per location, the loss leaves the transfer, not a location: the line shows its value.
                value = Out(from, qtyOut, m.UnitCost, decimals, notes, moved: false);
                break;
            }
            case StockMovementKind.RentalReturnTransfer:
            {
                var key = (m.DocumentId ?? m.Id, m.ProductId);
                if (m.QtyDelta < 0)
                {
                    qtyOut = -m.QtyDelta;
                    value = Out(pool, qtyOut, m.UnitCost, decimals, notes, moved: true);
                    var had = pairs.GetValueOrDefault(key);
                    pairs[key] = (had.Qty + qtyOut, had.Value + value);
                }
                else
                {
                    qtyIn = m.QtyDelta;
                    // At what the out row took; any part with no out row behind it at its reference cost.
                    var had = pairs.GetValueOrDefault(key);
                    var take = Math.Min(qtyIn, had.Qty);
                    var part = take > 0 ? had.Value * take / had.Qty : 0m;
                    if (take > 0) pairs[key] = (had.Qty - take, had.Value - part);
                    value = part + (qtyIn - take) * (m.UnitCost ?? 0m);
                    In(pool, qtyIn, value, moved: true, out gap);
                }
                break;
            }

            default:
                if (m.Kind == StockMovementKind.Unclassified) { notes.Add(WeightedAverageShadow.NoteUnclassified); pool.Unclassified++; }
                if (m.QtyDelta > 0)
                {
                    qtyIn = m.QtyDelta;
                    var cost = InboundCost(m, pool, policy, saleOut, issued, originalLineCost, notes);
                    if (m.Kind == StockMovementKind.ManufacturingOutput) pool.OutputsAtReference++;
                    value = Round(qtyIn * cost, 10);
                    In(pool, qtyIn, value, moved: false, out gap);
                    // A credit note's goods back on an invoice (P0.3) unwind its cost of sales as a refund does.
                    if (m.Kind is StockMovementKind.Refund or StockMovementKind.InvoiceReturn)
                    {
                        pool.PostedCostOfSales -= qtyIn * (m.UnitCost ?? 0m);
                        pool.ShadowCostOfSales -= value;
                    }
                }
                else if (m.QtyDelta < 0)
                {
                    qtyOut = -m.QtyDelta;
                    value = Out(pool, qtyOut, m.UnitCost, decimals, notes, moved: false);
                    if (m.Kind == StockMovementKind.Sale && m.SaleId is Guid sale)
                    {
                        var had = saleOut.GetValueOrDefault((sale, m.ProductId));
                        saleOut[(sale, m.ProductId)] = (had.Qty + qtyOut, had.Value + value);
                        pool.PostedCostOfSales += qtyOut * (m.UnitCost ?? 0m);
                        pool.ShadowCostOfSales += value;
                    }
                    else if (m.Kind == StockMovementKind.InvoiceSale)
                    {
                        // Goods sold on a back-office invoice (P0.3): cost of sales like the till's, posted at UnitCost.
                        pool.PostedCostOfSales += qtyOut * (m.UnitCost ?? 0m);
                        pool.ShadowCostOfSales += value;
                    }
                    else if (m.Kind == StockMovementKind.ManufacturingIssue && m.OrderId is Guid order)
                    {
                        var had = issued.GetValueOrDefault((order, m.ProductId));
                        issued[(order, m.ProductId)] = (had.Qty + qtyOut, had.Value + value);
                    }
                }
                break;
        }

        if (gap != 0)
            gaps.Add(new ShadowGap(m.Id, m.AtUtc, m.ProductId, m.BranchId, m.Kind, m.DocumentNumber, gap));
        if (gap != 0 || pool.Qty < 0) notes.Add(WeightedAverageShadow.NoteNegativeStock);
        var moved = qtyIn + qtyOut;
        if (moved > 0 && !notes.Contains(WeightedAverageShadow.NoteInternal)) unit = value / moved;
        if (detailProductId == m.ProductId)
            lines.Add(new ShadowLine(m, qtyIn, qtyOut, unit is decimal u ? Round(u, Math.Max(decimals, 4)) : null, value,
                pool.Qty, pool.Average, gap, notes.Distinct().ToList()));
        return new ShadowApplied(value, moved > 0 && !notes.Contains(WeightedAverageShadow.NoteInternal),
            notes.Contains(WeightedAverageShadow.NoteNoAverage));
    }

    public ShadowResult Result()
    {
        var inTransit = transit.Values.GroupBy(p => p.ProductId).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
        return new ShadowResult(pools.Values.ToList(), inTransit, lines.ToList(), gaps.ToList());
    }

    /// <summary>
    /// The value <paramref name="qty"/> units of <paramref name="product"/> would leave <paramref name="branch"/>'s
    /// pool at if they went out now, as a sale does, without taking them out. <paramref name="noAverage"/>: the pool
    /// has never had an average, so the value is <paramref name="fallbackUnit"/>'s, as the replay would value it.
    /// </summary>
    public decimal PreviewOut(Guid product, Guid branch, decimal qty, decimal fallbackUnit, out bool noAverage)
    {
        noAverage = false;
        if (qty <= 0) return 0m;
        var pool = PoolOf(product, branch);
        return OutValue(pool?.Qty ?? 0m, pool?.Value ?? 0m, pool?.LastAverage, qty, fallbackUnit, decimals, out noAverage);
    }

    /// <summary>
    /// The value <paramref name="qty"/> units of <paramref name="product"/> given back on a refund of
    /// <paramref name="refundOfSale"/> would come back at under the refund setting, without bringing them in.
    /// <paramref name="note"/> is the replay's note for it; <paramref name="noAverage"/> says the setting asked for an
    /// average the pool has never had, so <paramref name="fallbackUnit"/> was used.
    /// </summary>
    public decimal PreviewRefund(
        Guid product, Guid branch, decimal qty, Guid? refundOfSale, decimal fallbackUnit, out string note, out bool noAverage)
    {
        var pool = PoolOf(product, branch) ?? new ShadowPool { ProductId = product };
        var m = new ShadowMovement(Guid.Empty, DateTime.MaxValue, product, branch, StockMovementKind.Refund, qty, qty, fallbackUnit,
            null, null, null, refundOfSale, null, null);
        var notes = new List<string>();
        var cost = InboundCost(m, pool, policy, saleOut, issued, originalLineCost, notes);
        note = notes.LastOrDefault() ?? WeightedAverageShadow.NoteRefundAtAverage;
        noAverage = note == WeightedAverageShadow.NoteRefundAtAverage && pool.Average is null;
        return qty <= 0 ? 0m : Round(qty * cost, 10);
    }

    private static decimal InboundCost(
        ShadowMovement m, ShadowPool pool, ShadowPolicy policy,
        Dictionary<(Guid, Guid), (decimal Qty, decimal Value)> saleOut,
        Dictionary<(Guid, Guid), (decimal Qty, decimal Value)> issued,
        IReadOnlyDictionary<(Guid Sale, Guid Product), decimal> originalLineCost, List<string> notes)
    {
        decimal Fallback() => pool.Average ?? m.UnitCost ?? 0m;
        switch (m.Kind)
        {
            case StockMovementKind.Receipt:
            case StockMovementKind.Opening:
                return m.UnitCost ?? Fallback();
            case StockMovementKind.ManufacturingOutput:
                // The production value is Phase D's; until then the release's reference snapshot. Never
                // the order's cost sheet: it would hand an order's labour money to whoever reads this.
                notes.Add(WeightedAverageShadow.NoteOutputAtReference);
                return m.UnitCost ?? Fallback();
            case StockMovementKind.ManufacturingReturn:
                if (m.OrderId is Guid order && issued.TryGetValue((order, m.ProductId), out var iss) && iss.Qty > 0)
                {
                    notes.Add(WeightedAverageShadow.NoteReturnAtIssueCost);
                    return iss.Value / iss.Qty;
                }
                return m.UnitCost ?? Fallback();
            case StockMovementKind.Refund when policy.RefundCost == RefundValuation.SaleCost:
                if (m.RefundOfSaleId is Guid sale)
                {
                    if (saleOut.TryGetValue((sale, m.ProductId), out var sold) && sold.Qty > 0)
                    {
                        notes.Add(WeightedAverageShadow.NoteRefundAtSaleCost);
                        return sold.Value / sold.Qty;
                    }
                    if (originalLineCost.TryGetValue((sale, m.ProductId), out var posted))
                    {
                        notes.Add(WeightedAverageShadow.NoteRefundAtPostedCost);
                        return posted;
                    }
                }
                notes.Add(WeightedAverageShadow.NoteRefundAtAverage);
                return Fallback();
            case StockMovementKind.Refund:
                notes.Add(WeightedAverageShadow.NoteRefundAtAverage);
                return Fallback();
            case StockMovementKind.InvoiceReturn:
                // Back at the cost its invoice line took it out at (P0.3) — the figure the credit note posted.
                return m.UnitCost ?? Fallback();
            case StockMovementKind.MaintenanceReturn:
                // Parts back from a maintenance job (M2) at what the job's issues went out at, frozen on the return's row.
                notes.Add(WeightedAverageShadow.NoteReturnAtIssueCost);
                return m.UnitCost ?? Fallback();
            default:
                // Found in a count, back from a rental, an unknown kind: at the average, which moves nothing.
                return Fallback();
        }
    }

    /// <summary>
    /// What <paramref name="qty"/> leaving a pool of <paramref name="poolQty"/> worth <paramref name="poolValue"/> takes
    /// out: the average rounded to the decimals, the last units whatever value is left, below zero the last average,
    /// and with no average ever <paramref name="fallback"/> (<paramref name="noAverage"/>). The one formula for the
    /// replay and for a preview.
    /// </summary>
    private static decimal OutValue(
        decimal poolQty, decimal poolValue, decimal? lastAverage, decimal qty, decimal? fallback, int decimals, out bool noAverage)
    {
        noAverage = false;
        // The last units of the pool take what value is left, so rounding never strands any.
        if (poolQty > 0 && qty >= poolQty) return poolValue + (qty - poolQty) * Round(poolValue / poolQty, decimals);
        if (poolQty > 0) return qty * Round(poolValue / poolQty, decimals);
        if (lastAverage is decimal last) return qty * Round(last, decimals);
        noAverage = true;
        return qty * (fallback ?? 0m);
    }

    /// <summary>Takes <paramref name="qty"/> out of the pool and returns the value that left.</summary>
    private static decimal Out(ShadowPool pool, decimal qty, decimal? fallback, int decimals, List<string> notes, bool moved)
    {
        if (qty <= 0) return 0;
        var value = OutValue(pool.Qty, pool.Value, pool.LastAverage, qty, fallback, decimals, out var noAverage);
        if (noAverage)
        {
            pool.LastAverage = fallback ?? 0m;
            pool.OutflowsWithoutAverage++;
            notes.Add(WeightedAverageShadow.NoteNoAverage);
        }

        pool.Qty -= qty;
        pool.Value -= value;
        if (pool.Qty == 0) pool.Value = 0;
        else if (pool.Qty > 0) pool.LastAverage = pool.Value / pool.Qty;
        if (moved) { pool.MovedOutQty += qty; pool.MovedOutValue += value; }
        else { pool.OutQty += qty; pool.OutValue += value; }
        return value;
    }

    /// <summary>Adds <paramref name="qty"/> worth <paramref name="value"/>; below zero, settles the covered units first.</summary>
    private static void In(ShadowPool pool, decimal qty, decimal value, bool moved, out decimal gap)
    {
        gap = 0;
        if (qty <= 0) return;
        if (pool.Qty < 0)
        {
            var covered = Math.Min(qty, -pool.Qty);
            // What those units went out at: the whole negative value when they are all covered.
            var settled = covered == -pool.Qty ? -pool.Value : covered * (pool.Value / pool.Qty);
            var coveredValue = value * covered / qty;
            gap = coveredValue - settled;
            pool.NegativeStockGap += gap;
        }
        pool.Qty += qty;
        pool.Value += value - gap;
        if (pool.Qty == 0) pool.Value = 0;
        else if (pool.Qty > 0) pool.LastAverage = pool.Value / pool.Qty;
        if (moved) { pool.MovedInQty += qty; pool.MovedInValue += value; }
        else { pool.InQty += qty; pool.InValue += value; }
    }

    private static decimal Round(decimal value, int decimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);
}
