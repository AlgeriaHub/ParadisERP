using AlgeriaPos.Domain;

namespace AlgeriaPos.Infrastructure.Manufacturing;

/// <summary>
/// Converting a quantity from one unit to another — explicitly, or not at all (audit section 7).
///
/// <para>
/// Two routes exist and nothing else. Within one dimension, through each unit's factor to the
/// dimension's reference (1 kg = 1,000 g, 1 m = 100 cm). For one product, through a factor written on
/// it (a carton of these nails is 25 kg). A unit of the <see cref="UnitDimension.Other"/> dimension, or
/// two units of different dimensions with no product factor between them, do not convert: the answer
/// is "no conversion", never a guess, because a wrong factor becomes the same error in every quantity
/// and later in every cost.
/// </para>
/// <para>Pure, so the arithmetic is tested on its own.</para>
/// </summary>
public static class UnitConversions
{
    /// <summary>What a conversion needs to know about a unit.</summary>
    public sealed record UnitInfo(Guid Id, string Code, UnitDimension Dimension, decimal FactorToReference);

    /// <summary>
    /// What a conversion needs to know about a product: its base unit and the factor to it of each
    /// alternate unit written on it.
    /// </summary>
    public sealed record ProductUnits(Guid? BaseUnitId, IReadOnlyDictionary<Guid, decimal> FactorsToBase)
    {
        public static readonly ProductUnits None = new(null, new Dictionary<Guid, decimal>());
    }

    /// <summary>Places a converted quantity keeps: far more than a scale reads, never enough to drift.</summary>
    public const int Places = 6;

    public static UnitInfo Info(UnitOfMeasure unit) => new(unit.Id, unit.Code, unit.Dimension, unit.FactorToReference);

    /// <summary>
    /// How many <paramref name="to"/> one <paramref name="from"/> is, by the standard route; null when the
    /// two do not convert by it.
    /// </summary>
    public static decimal? StandardFactor(UnitInfo from, UnitInfo to)
    {
        if (from.Id == to.Id) return 1m;
        if (from.Dimension != to.Dimension || from.Dimension == UnitDimension.Other) return null;
        if (from.FactorToReference <= 0 || to.FactorToReference <= 0) return null;
        return from.FactorToReference / to.FactorToReference;
    }

    /// <summary>
    /// How many of the product's base unit one <paramref name="from"/> is: 1 for the base unit itself,
    /// the product's own factor when one is written for that unit, else the standard route to the base
    /// unit. Null when none applies — the caller says so in words.
    /// </summary>
    public static decimal? FactorToBase(UnitInfo from, UnitInfo baseUnit, ProductUnits product)
    {
        if (from.Id == baseUnit.Id) return 1m;
        if (product.FactorsToBase.TryGetValue(from.Id, out var own) && own > 0) return own;
        return StandardFactor(from, baseUnit);
    }

    /// <summary>
    /// How many <paramref name="to"/> one <paramref name="from"/> is, for one product: through its base
    /// unit, so a carton converts to grams as surely as to kilograms. Null when either side has no route.
    /// </summary>
    public static decimal? Factor(UnitInfo from, UnitInfo to, UnitInfo baseUnit, ProductUnits product)
    {
        if (from.Id == to.Id) return 1m;
        if (StandardFactor(from, to) is decimal direct) return direct;
        var fromBase = FactorToBase(from, baseUnit, product);
        var toBase = FactorToBase(to, baseUnit, product);
        if (fromBase is null || toBase is null || toBase == 0) return null;
        return fromBase.Value / toBase.Value;
    }

    /// <summary>A converted quantity, kept to <see cref="Places"/> places.</summary>
    public static decimal Apply(decimal quantity, decimal factor) =>
        Math.Round(quantity * factor, Places, MidpointRounding.AwayFromZero);
}
