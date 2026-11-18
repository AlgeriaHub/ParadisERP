using AlgeriaPos.Domain;

namespace AlgeriaPos.Application.Maintenance;

/// <summary>One product's parts used by a job (issued less returned) and its sale price before tax.</summary>
/// <param name="UnitPriceNet">The product's sale price per base unit, net of tax — see <see cref="MaintenancePricing.NetOfTax"/>.</param>
public sealed record MaintenancePricedPart(Guid ProductId, decimal Qty, decimal UnitPriceNet);

/// <summary>What a customer job's price is built from (Phase M5a, plan §3.2).</summary>
/// <param name="Parts">Parts the job used, at their sale price before tax.</param>
/// <param name="Hours">Hours booked on the job (Phase M3).</param>
/// <param name="LabourChargePerHour">MS-21.</param>
/// <param name="OutsideWork">Outside work recorded on the job, at cost (Phase M2).</param>
/// <param name="MarkupPercent">MS-22: on outside work under the itemised basis, on the cost under cost-plus.</param>
/// <param name="Cost">The job's cost C from Phase M4's <see cref="MaintenanceCostAdapter"/> (through <c>MaintenanceCosting</c>); null when the reader may not see it.</param>
public sealed record MaintenancePriceInputs(
    IReadOnlyList<MaintenancePricedPart> Parts, decimal Hours, decimal LabourChargePerHour, decimal OutsideWork,
    decimal MarkupPercent, decimal? Cost);

/// <summary>
/// The basis's proposal, with the figures it is made of. <c>Proposed</c> is null under Quoted (the price is typed) and
/// under cost-plus while no cost is available. The pieces that do not apply to the basis are null.
/// </summary>
public sealed record MaintenancePriceProposal(
    MaintenancePriceBasis Basis, decimal? Parts, decimal? Labour, decimal? OutsideWork, decimal? Markup, decimal? CostBase,
    decimal? Proposed);

/// <summary>
/// The customer job price bases of MS-20, pure (plan §3.2). Every amount is before tax: a stored price is the price
/// before tax unless the country profile says prices include it, in which case the part's tax is taken out first
/// (<see cref="NetOfTax"/>, CLAUDE.md "a stored price is the price before tax"). Money is rounded through the caller's
/// regional rule, once per figure, so the figures shown add up to the proposal exactly.
/// <list type="number">
/// <item><b>Quoted</b>: no proposal; the price is typed.</item>
/// <item><b>Itemised</b>: parts at sale price + hours × MS-21 + outside work + MS-22 % of the outside work.</item>
/// <item><b>Cost-plus</b>: the job's cost + MS-22 % of it; nothing while no cost is available.</item>
/// </list>
/// The proposal is a proposal: a <c>maintenance.pricing</c> holder may change it, and a price below the cost is never
/// refused (MS-25).
/// </summary>
public static class MaintenancePricing
{
    public static MaintenancePriceProposal Propose(
        MaintenancePriceBasis basis, MaintenancePriceInputs i, Func<decimal, decimal> roundMoney)
    {
        switch (basis)
        {
            case MaintenancePriceBasis.Itemised:
            {
                var parts = roundMoney(i.Parts.Sum(p => p.Qty * p.UnitPriceNet));
                var labour = roundMoney(i.Hours * i.LabourChargePerHour);
                var outside = roundMoney(i.OutsideWork);
                var markup = roundMoney(outside * i.MarkupPercent / 100m);
                return new(basis, parts, labour, outside, markup, null, parts + labour + outside + markup);
            }
            case MaintenancePriceBasis.CostPlus:
            {
                if (i.Cost is not decimal cost) return new(basis, null, null, null, null, null, null);
                var costBase = roundMoney(cost);
                var markup = roundMoney(costBase * i.MarkupPercent / 100m);
                return new(basis, null, null, null, markup, costBase, costBase + markup);
            }
            default:
                return new(MaintenancePriceBasis.Quoted, null, null, null, null, null, null);
        }
    }

    /// <summary>A shelf price before tax: as stored, or with the product's own rate taken out where the country profile stores prices tax-inclusive.</summary>
    public static decimal NetOfTax(decimal price, decimal taxRatePercent, bool pricesIncludeTax) =>
        pricesIncludeTax && taxRatePercent > 0 ? price / (1m + taxRatePercent / 100m) : price;

    /// <summary>
    /// MS-34 "parts and labour apart": the job's price before tax shared between its parts line — the parts used (issued
    /// less returned) at their sale price before tax, rounded once — and its labour and service line, the rest. Parts at sale
    /// price above the price are capped at the price, the labour line then 0. The two always add up to the price exactly.
    /// </summary>
    public static (decimal Parts, decimal Labour) SplitPartsAndLabour(
        decimal price, IReadOnlyList<MaintenancePricedPart> parts, Func<decimal, decimal> roundMoney)
    {
        var atSalePrice = Math.Max(0m, roundMoney(parts.Sum(p => p.Qty * p.UnitPriceNet)));
        var partsLine = Math.Min(atSalePrice, price);
        return (partsLine, price - partsLine);
    }

    /// <summary>MS-25: what the price loses against the cost, or null when it does not (or no cost is available).</summary>
    public static decimal? Loss(decimal price, decimal? cost) =>
        cost is decimal c && price < c ? c - price : null;
}
