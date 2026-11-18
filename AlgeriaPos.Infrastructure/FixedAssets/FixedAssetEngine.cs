using AlgeriaPos.Domain;

namespace AlgeriaPos.Infrastructure.FixedAssets;

/// <summary>What the depreciation of the register follows: the method's figures, read from the settings.</summary>
/// <param name="DefaultLifeMonths">The useful life of a type without its own; null while not chosen.</param>
/// <param name="Lives">Each type's own useful life, in months.</param>
/// <param name="ResidualPercent">The share of cost not depreciated.</param>
/// <param name="DefaultMethod">The company's method, for every type without its own (straight line unless the accountant chose).</param>
/// <param name="DefaultSwitchToStraightLine">Declining balance: switch to straight line when it is higher (on by default).</param>
/// <param name="Methods">Each type's own method; an empty field follows the company.</param>
public sealed record FixedAssetPolicy(
    int? DefaultLifeMonths, IReadOnlyDictionary<Guid, int> Lives, decimal ResidualPercent,
    DepreciationMethod DefaultMethod = DepreciationMethod.StraightLine, bool DefaultSwitchToStraightLine = true,
    IReadOnlyDictionary<Guid, FixedAssetMethodChoice>? Methods = null)
{
    public int? LifeOf(Guid productId) => Lives.TryGetValue(productId, out var own) ? own : DefaultLifeMonths;

    /// <summary>The method a type is depreciated by: its own fields, the company's where it has none.</summary>
    public FixedAssetMethodTerms TermsOf(Guid productId)
    {
        var own = Methods?.GetValueOrDefault(productId);
        var method = own?.Method ?? DefaultMethod;
        if (method != DepreciationMethod.DecliningBalance) return FixedAssetMethodTerms.StraightLine;
        var coefficient = own?.DecliningCoefficient ?? FixedAssetDeclining.LegalCoefficient(LifeOf(productId)) ?? FixedAssetDeclining.LowestLegal;
        return new FixedAssetMethodTerms(method, coefficient, own?.SwitchToStraightLine ?? DefaultSwitchToStraightLine);
    }
}

/// <summary>How a layer is depreciated: straight line, or declining balance at a coefficient, switching to straight line or not.</summary>
public sealed record FixedAssetMethodTerms(DepreciationMethod Method, decimal Coefficient, bool SwitchToStraightLine)
{
    public static readonly FixedAssetMethodTerms StraightLine = new(DepreciationMethod.StraightLine, 1m, false);
    public bool Declining => Method == DepreciationMethod.DecliningBalance;
}

/// <summary>One calendar year of a declining-balance schedule.</summary>
/// <param name="Months">The months of the life in that year (the first and the last may be fewer than twelve).</param>
/// <param name="StraightLine">The year is charged on the straight line (the switch reached).</param>
/// <param name="Switches">The year the straight line takes over.</param>
public sealed record FixedAssetScheduleYear(int Year, int Months, decimal Opening, decimal Charge, decimal Closing, bool StraightLine, bool Switches);

/// <summary>
/// Declining balance (SCF; the tax code's "amortissement dégressif"), as built on 2026-11-10: the straight-line rate × a
/// coefficient, on the net book value at the start of each calendar year.
/// <list type="bullet">
/// <item>The first year is prorated by its months from the month the layer starts (the month a straight-line layer starts
/// too); the life runs its months from there, so a mid-year start ends mid-year, its last year prorated the same way.</item>
/// <item>Each year: declining = opening × rate × months / 12; straight line = (opening − residual) × months / months left.
/// When the switch is on and the straight line exceeds the declining charge, the straight line takes over for the rest of the
/// life. The last year takes what is left down to the residual value, so the life ends exactly at it.</item>
/// <item>A year's charge is spread evenly over its months; the engine charges the rounded cumulative figure less the one a
/// month before, so rounding is absorbed in the last month.</item>
/// </list>
/// </summary>
public static class FixedAssetDeclining
{
    public const decimal LowestLegal = 1.5m;
    public const decimal MinCoefficient = 1m;
    public const decimal MaxCoefficient = 5m;

    /// <summary>The tax code's coefficient for a life: 1.5 up to 4 years (3–4), 2 for 5–6 years, 2.5 above 6. None without a life.</summary>
    public static decimal? LegalCoefficient(int? lifeMonths) => lifeMonths switch
    {
        null or <= 0 => null,
        > 72 => 2.5m,
        >= 60 => 2m,
        _ => LowestLegal,
    };

    /// <summary>The schedule of <paramref name="cost"/>, depreciated to <paramref name="residual"/>, year by year.</summary>
    public static List<FixedAssetScheduleYear> Schedule(
        decimal cost, decimal residual, DateOnly start, int lifeMonths, decimal coefficient, bool switchToStraightLine)
    {
        var years = new List<FixedAssetScheduleYear>();
        if (lifeMonths <= 0) return years;
        var rate = coefficient * 12m / lifeMonths;
        var value = cost;
        var left = lifeMonths;
        var year = start.Year;
        var months = Math.Min(13 - start.Month, left);
        var switched = false;
        while (left > 0)
        {
            var declining = value * rate * months / 12m;
            var straight = (value - residual) * months / left;
            var switches = !switched && switchToStraightLine && straight > declining;
            switched |= switches;
            var charge = switched ? straight : declining;
            // The last year, or a charge that would pass the residual value: what is left, and no more.
            if (months == left || charge > value - residual) charge = value - residual;
            if (charge < 0) charge = 0;
            years.Add(new FixedAssetScheduleYear(year, months, value, charge, value - charge, switched, switches));
            value -= charge;
            left -= months;
            year++;
            months = Math.Min(12, left);
        }
        return years;
    }

    /// <summary>
    /// The depreciation accumulated by one unit after each month of its life (index 0: none; the last: cost − residual
    /// exactly), each year's charge spread evenly over its months.
    /// </summary>
    public static decimal[] Cumulative(decimal unitCost, decimal residualPercent, DateOnly start, int lifeMonths, FixedAssetMethodTerms terms)
    {
        var residual = unitCost * residualPercent / 100m;
        var cumulative = new decimal[Math.Max(lifeMonths, 0) + 1];
        var k = 0;
        var before = 0m;
        foreach (var y in Schedule(unitCost, residual, start, lifeMonths, terms.Coefficient, terms.SwitchToStraightLine))
        {
            for (var j = 1; j <= y.Months; j++) cumulative[++k] = before + y.Charge * j / y.Months;
            before += y.Charge;
            cumulative[k] = before;
        }
        if (lifeMonths > 0) cumulative[lifeMonths] = unitCost - residual;
        return cumulative;
    }
}

/// <summary>
/// A part of a pool that started depreciating the same month at the same cost per unit: the opening, one receiving note,
/// units found or put back into service. A disposal or a transfer takes the same share of every layer of its pool.
/// </summary>
public sealed class FixedAssetLayer
{
    public required decimal Qty { get; set; }
    public required decimal UnitCost { get; init; }
    public required DateOnly StartMonth { get; init; }
    public required int LifeMonths { get; init; }
    /// <summary>
    /// A capitalised repair (Phase M7d): cost spread over <see cref="Qty"/> units that are already counted by the pool's other
    /// layers. It is depreciated and leaves pro rata like any layer, but adds no unit to the pool.
    /// </summary>
    public bool CostOnly { get; init; }
    /// <summary>The method the layer is depreciated by: its type's when it was added (straight line unless chosen otherwise).</summary>
    public FixedAssetMethodTerms Terms { get; init; } = FixedAssetMethodTerms.StraightLine;
    private decimal[]? cumulative;
    private decimal cumulativeResidual = -1m;

    /// <summary>Declining balance: one unit's depreciation accumulated after each month of the life (computed once).</summary>
    internal decimal[] Cumulative(decimal residualPercent)
    {
        if (cumulative is null || cumulativeResidual != residualPercent)
        {
            cumulative = FixedAssetDeclining.Cumulative(UnitCost, residualPercent, StartMonth, LifeMonths, Terms);
            cumulativeResidual = residualPercent;
        }
        return cumulative;
    }

    /// <summary>The same layer with <paramref name="qty"/> units (a share taken out by a disposal or a transfer).</summary>
    internal FixedAssetLayer With(decimal qty) => new()
    {
        Qty = qty, UnitCost = UnitCost, StartMonth = StartMonth, LifeMonths = LifeMonths, CostOnly = CostOnly, Terms = Terms,
        cumulative = cumulative, cumulativeResidual = cumulativeResidual,
    };
}

/// <summary>One pool of the register: a hire product at a branch.</summary>
public sealed class FixedAssetPool(Guid productId, Guid branchId)
{
    public Guid ProductId { get; } = productId;
    public Guid BranchId { get; } = branchId;
    public List<FixedAssetLayer> Layers { get; } = [];
    /// <summary>The units the pool holds: its layers' units, a capitalised repair's layer (cost only) adding none.</summary>
    public decimal Qty => Layers.Where(l => !l.CostOnly).Sum(l => l.Qty);
    /// <summary>The cost the pool carries: what its events added less what they took out, as posted.</summary>
    public decimal Cost { get; internal set; }
    /// <summary>Its accumulated depreciation: every month's charge computed so far, less what the events took out.</summary>
    public decimal Depreciation { get; internal set; }
    public decimal NetBookValue => Cost - Depreciation;
    /// <summary>The hire product's scrap at this branch and the value the register carries it at (318).</summary>
    public decimal ScrapQty { get; internal set; }
    public decimal ScrapValue { get; internal set; }
    /// <summary>The month's charge computed last (the last month depreciated).</summary>
    public decimal LastCharge { get; internal set; }
}

/// <summary>One month's depreciation of one pool.</summary>
public sealed record FixedAssetCharge(DateOnly Month, Guid ProductId, Guid BranchId, decimal Amount);

/// <summary>
/// The register's arithmetic (REN-12, Phase M7a), pure and replayable: the stored events of a pool, in time order, and
/// straight-line depreciation by month between them.
/// <list type="bullet">
/// <item>A layer starts in its <c>StartMonth</c>: the switch-over month for the opening, the month after for a receipt.
/// Each month it is depreciated, its units at the month's end are charged one month of
/// <c>unit cost × (1 − residual) / life</c>, until the life is spent. The charge is the rounded cumulative figure less the
/// rounded figure a month before, so a layer that keeps its units is depreciated to the thousandth (2,000 × 6,500 over
/// 60 months: 2,600,000 a year, 5,200,000 left after three).</item>
/// <item>Units leave a pool pro rata: a disposal or a transfer of q of the pool's Q units takes q/Q of its cost and of its
/// accumulated depreciation (through the month before the event; the month of a disposal is not depreciated), and q/Q of
/// every layer, so the net book value per unit is what it was.</item>
/// <item>Money is the ledger's (three decimals); a stored event carries exactly what its entry posted.</item>
/// </list>
/// </summary>
public sealed class FixedAssetEngine(DateOnly switchMonth, FixedAssetPolicy policy)
{
    private readonly Dictionary<(Guid ProductId, Guid BranchId), FixedAssetPool> pools = [];
    private readonly List<FixedAssetCharge> charges = [];

    public DateOnly SwitchMonth { get; } = new(switchMonth.Year, switchMonth.Month, 1);
    /// <summary>The last month depreciated; before the switch-over month while none is.</summary>
    public DateOnly DepreciatedThrough { get; private set; } = new DateOnly(switchMonth.Year, switchMonth.Month, 1).AddMonths(-1);
    public IReadOnlyCollection<FixedAssetPool> Pools => pools.Values;
    public IReadOnlyList<FixedAssetCharge> Charges => charges;

    public FixedAssetPool Pool(Guid productId, Guid branchId)
    {
        if (!pools.TryGetValue((productId, branchId), out var pool)) pools[(productId, branchId)] = pool = new FixedAssetPool(productId, branchId);
        return pool;
    }

    public FixedAssetPool? Find(Guid productId, Guid branchId) => pools.GetValueOrDefault((productId, branchId));

    /// <summary>Depreciates every pool for each month after the last one depreciated, up to <paramref name="month"/> included.</summary>
    public void DepreciateThrough(DateOnly month)
    {
        var target = new DateOnly(month.Year, month.Month, 1);
        while (DepreciatedThrough < target)
        {
            var m = DepreciatedThrough.AddMonths(1);
            foreach (var pool in pools.Values)
            {
                var charge = pool.Layers.Sum(l => LayerCharge(l, m, policy.ResidualPercent));
                pool.LastCharge = charge;
                if (charge == 0) continue;
                pool.Depreciation += charge;
                charges.Add(new FixedAssetCharge(m, pool.ProductId, pool.BranchId, charge));
            }
            DepreciatedThrough = m;
        }
    }

    /// <summary>One month of a layer's depreciation: its units at the month's end, the rounded cumulative figures' difference.</summary>
    internal static decimal LayerCharge(FixedAssetLayer layer, DateOnly month, decimal residualPercent)
    {
        if (layer.Qty <= 0 || layer.LifeMonths <= 0) return 0m;
        var k = Months(layer.StartMonth, month) + 1;
        if (k <= 0 || k > layer.LifeMonths) return 0m;
        if (layer.Terms.Declining)
        {
            // Declining balance (2026-11-10): the year's charge spread over its months, rounding absorbed in the last month.
            var cumulative = layer.Cumulative(residualPercent);
            return Money(layer.Qty * cumulative[k]) - Money(layer.Qty * cumulative[k - 1]);
        }
        var basis = layer.UnitCost * (1m - residualPercent / 100m);
        return Money(layer.Qty * basis * k / layer.LifeMonths) - Money(layer.Qty * basis * (k - 1) / layer.LifeMonths);
    }

    /// <summary>
    /// What <paramref name="qty"/> units take out of a pool now: the share of its cost and of its accumulated depreciation.
    /// More units than the pool holds take all of it; a pool holding none gives nothing.
    /// </summary>
    public (decimal Cost, decimal Depreciation) Share(Guid productId, Guid branchId, decimal qty)
    {
        var pool = Find(productId, branchId);
        if (pool is null || qty <= 0) return (0m, 0m);
        var held = pool.Qty;
        if (held <= 0) return (0m, 0m);
        if (qty >= held) return (pool.Cost, pool.Depreciation);
        return (Money(pool.Cost * qty / held), Money(pool.Depreciation * qty / held));
    }

    /// <summary>What <paramref name="qty"/> units of the pool's scrap are carried at: their share of the scrap's value.</summary>
    public decimal ScrapShare(Guid productId, Guid branchId, decimal qty)
    {
        var pool = Find(productId, branchId);
        if (pool is null || qty <= 0 || pool.ScrapQty <= 0) return 0m;
        return qty >= pool.ScrapQty ? pool.ScrapValue : Money(pool.ScrapValue * qty / pool.ScrapQty);
    }

    /// <summary>
    /// Applies one stored event. The months before its own are depreciated first (<paramref name="month"/> is the company
    /// month the event happened in), so it takes out the depreciation accumulated through the month before.
    /// </summary>
    public void Apply(FixedAssetMovement m, DateOnly month)
    {
        DepreciateThrough(new DateOnly(month.Year, month.Month, 1).AddMonths(-1));
        var pool = Pool(m.ProductId, m.BranchId);
        switch (m.Kind)
        {
            case FixedAssetMovementKind.Opening:
            case FixedAssetMovementKind.Acquisition:
            case FixedAssetMovementKind.Found:
            case FixedAssetMovementKind.BackIntoService:
                AddLayer(pool, m.Qty, m.Cost, m.StartMonth ?? new DateOnly(month.Year, month.Month, 1).AddMonths(1));
                if (m.Kind == FixedAssetMovementKind.BackIntoService)
                {
                    pool.ScrapQty -= m.Qty;
                    pool.ScrapValue -= m.Cost;
                }
                break;
            case FixedAssetMovementKind.Disposal:
                TakeOut(pool, m.Qty, m.Cost, m.Depreciation);
                break;
            case FixedAssetMovementKind.Scrapped:
                TakeOut(pool, m.Qty, m.Cost, m.Depreciation);
                pool.ScrapQty += m.Qty;
                pool.ScrapValue += m.Cost - m.Depreciation;
                break;
            case FixedAssetMovementKind.Transfer:
                var moved = TakeOut(pool, m.Qty, m.Cost, m.Depreciation);
                var to = Pool(m.ProductId, m.ToBranchId ?? m.BranchId);
                to.Layers.AddRange(moved);
                to.Cost += m.Cost;
                to.Depreciation += m.Depreciation;
                break;
            case FixedAssetMovementKind.ScrapOpening:
                pool.ScrapQty += m.Qty;
                pool.ScrapValue += m.Cost;
                break;
            case FixedAssetMovementKind.ScrapOut:
                pool.ScrapQty -= m.Qty;
                pool.ScrapValue -= m.Cost;
                break;
            case FixedAssetMovementKind.CapitalisedRepair:
                // Phase M7d: the repair's cost over the repaired units, which the pool already counts.
                AddLayer(pool, m.Qty, m.Cost, m.StartMonth ?? new DateOnly(month.Year, month.Month, 1).AddMonths(1), costOnly: true);
                break;
        }
    }

    private void AddLayer(FixedAssetPool pool, decimal qty, decimal cost, DateOnly start, bool costOnly = false)
    {
        pool.Cost += cost;
        if (qty <= 0) return;
        pool.Layers.Add(new FixedAssetLayer
        {
            Qty = qty, UnitCost = cost / qty, StartMonth = new DateOnly(start.Year, start.Month, 1),
            LifeMonths = policy.LifeOf(pool.ProductId) ?? 0, CostOnly = costOnly, Terms = policy.TermsOf(pool.ProductId),
        });
    }

    /// <summary>Takes q/Q of every layer out of the pool and the posted amounts off its balances; returns the parts taken.</summary>
    private static List<FixedAssetLayer> TakeOut(FixedAssetPool pool, decimal qty, decimal cost, decimal depreciation)
    {
        pool.Cost -= cost;
        pool.Depreciation -= depreciation;
        var held = pool.Qty;
        var taken = new List<FixedAssetLayer>();
        if (held <= 0 || qty <= 0) return taken;
        var all = qty >= held;
        var left = Math.Min(qty, held);
        var lastUnitLayer = pool.Layers.FindLastIndex(l => !l.CostOnly);
        for (var i = 0; i < pool.Layers.Count; i++)
        {
            var layer = pool.Layers[i];
            // Each layer gives its share (to the quantity column's ten decimals); the last unit layer gives what is left, so the
            // units taken are exactly the units asked. A capitalised repair's layer (cost only) gives its share too and counts
            // for no unit.
            var part = all ? layer.Qty
                : !layer.CostOnly && i == lastUnitLayer ? Math.Min(layer.Qty, left)
                : Math.Min(layer.Qty, Math.Round(layer.Qty * qty / held, 10, MidpointRounding.AwayFromZero));
            if (!layer.CostOnly) left -= part;
            if (part == 0) continue;
            layer.Qty -= part;
            taken.Add(layer.With(part));
        }
        pool.Layers.RemoveAll(l => l.Qty <= 0);
        return taken;
    }

    public static int Months(DateOnly from, DateOnly to) => (to.Year - from.Year) * 12 + to.Month - from.Month;

    public static DateOnly MonthOf(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return new DateOnly(local.Year, local.Month, 1);
    }

    public static decimal Money(decimal v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
}
