using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ParadisERP.Infrastructure.Catalog;

/// <summary>
/// What a receiving note does to the product's company-wide cost price (B-F01, B-F06), under the accountant's setting
/// <see cref="StockValuationSettings.ReceiptCostRule"/> (owner, 2026-11-09: every accounting decision is a setting; the
/// default keeps today's behaviour).
/// <list type="bullet">
/// <item><see cref="ReceiptCostPriceRule.LastCost"/> (the default): the cost the line states, as since the first release.</item>
/// <item><see cref="ReceiptCostPriceRule.WeightedAverage"/>: once the note's movements are saved, the shadow's weighted
/// average for the product, company-wide (<see cref="StockValuationShadow.CompanyAveragesAsync"/>: the same loader, rules
/// and settings as the report and the C-01 sale costing), to the 3 decimals the cost price is kept at. When none can be
/// computed — the company holds none of it, the shadow was never opened, the replay failed — the last cost, and it is
/// logged. A receipt is never refused for it.</item>
/// <item><see cref="ReceiptCostPriceRule.Unchanged"/>: the note never writes it.</item>
/// </list>
/// In every mode a stated cost of 0 never replaces a non-zero cost price: a delivery at 0 is a free or unknown cost, not
/// a price (B-F06). Until 2026-11-09 it did. The receipt itself is still valued at what the line states, 0 included.
/// </summary>
public sealed class ReceivingCostPrice(PosDbContext db, StockValuationShadow shadow, ILogger<ReceivingCostPrice> logger)
{
    /// <summary>The scale <see cref="Product.CostPrice"/> is stored at (decimal(18,3)).</summary>
    public const int CostPriceDecimals = 3;

    /// <summary>The setting in force; <see cref="ReceiptCostPriceRule.LastCost"/> while the settings row does not exist.</summary>
    public async Task<ReceiptCostPriceRule> RuleAsync(CancellationToken ct = default) =>
        await db.StockValuationSettings.AsNoTracking()
            .Select(s => (ReceiptCostPriceRule?)s.ReceiptCostRule).FirstOrDefaultAsync(ct)
        ?? ReceiptCostPriceRule.LastCost;

    /// <summary>What a line stated at cost 0 is valued at (the accountant's setting); <see cref="ZeroCostReceiptValue.Zero"/> while there is no settings row.</summary>
    public async Task<ZeroCostReceiptValue> ZeroCostAsync(CancellationToken ct = default) =>
        await db.StockValuationSettings.AsNoTracking()
            .Select(s => (ZeroCostReceiptValue?)s.ZeroCostReceipt).FirstOrDefaultAsync(ct)
        ?? ZeroCostReceiptValue.Zero;

    /// <summary>
    /// What one line was bought at: the cost it states, or — none stated, or a negative one — the product's cost price as it
    /// stands. A stated 0 is a free delivery, valued at 0 (the default, as always), or, when the accountant chose
    /// <see cref="ZeroCostReceiptValue.CostPrice"/>, an unknown cost, valued at the cost price as it stands. The figure the
    /// line's movement and the ledger entry carry, in every mode.
    /// </summary>
    public static decimal UnitCostOf(decimal? stated, decimal costPrice, ZeroCostReceiptValue zero = ZeroCostReceiptValue.Zero) =>
        stated is decimal cost && cost >= 0 && !(cost == 0 && zero == ZeroCostReceiptValue.CostPrice) ? cost : costPrice;

    /// <summary>
    /// The line's own effect, before anything is saved: under <see cref="ReceiptCostPriceRule.LastCost"/> the stated cost
    /// becomes the cost price, unless it is 0 over a non-zero one. The other modes write nothing here.
    /// </summary>
    /// <returns>Whether the cost price changed.</returns>
    public static bool ApplyLine(Product product, decimal? stated, ReceiptCostPriceRule rule) =>
        rule == ReceiptCostPriceRule.LastCost && Restate(product, stated);

    /// <summary>
    /// Under <see cref="ReceiptCostPriceRule.WeightedAverage"/>, after the note's movements are saved in the caller's
    /// transaction: each received product's cost price becomes its company-wide average. A product with no average takes
    /// the last cost rule instead, and that is logged. Never throws but on cancellation.
    /// </summary>
    /// <param name="received">Every line of the note, in order: its product (the tracked instance) and the cost it stated.</param>
    /// <returns>The products whose cost price changed.</returns>
    public async Task<IReadOnlyList<Product>> ApplyAverageAsync(
        string? reference, IReadOnlyList<(Product Product, decimal? Stated)> received, CancellationToken ct = default)
    {
        var changed = new List<Product>();
        if (received.Count == 0) return changed;
        var products = received.Select(r => r.Product).Distinct().ToList();

        IReadOnlyDictionary<Guid, decimal> averages;
        try
        {
            averages = await shadow.CompanyAveragesAsync(products.Select(p => p.Id).ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Receiving {Reference}: the weighted average could not be worked out; the cost prices take the last cost", reference);
            averages = new Dictionary<Guid, decimal>();
        }

        foreach (var product in products)
        {
            if (averages.TryGetValue(product.Id, out var average))
            {
                var rounded = Math.Round(average, CostPriceDecimals, MidpointRounding.AwayFromZero);
                // A 0 never replaces a non-zero cost price, whichever way it was reached.
                if (rounded == product.CostPrice || (rounded == 0 && product.CostPrice != 0)) continue;
                product.CostPrice = rounded;
                changed.Add(product);
                continue;
            }

            logger.LogWarning(
                "Receiving {Reference}: product {ProductId} has no weighted average (none held, or the shadow is not opened); its cost price takes the last cost",
                reference, product.Id);
            var restated = false;
            foreach (var (p, stated) in received)
                if (p == product) restated |= Restate(product, stated);
            if (restated) changed.Add(product);
        }
        return changed;
    }

    /// <summary>The last cost rule: a stated, non-negative cost becomes the cost price, except 0 over a non-zero one.</summary>
    private static bool Restate(Product product, decimal? stated)
    {
        if (stated is not decimal cost || cost < 0) return false;
        if (cost == 0 && product.CostPrice != 0) return false;
        if (cost == product.CostPrice) return false;
        product.CostPrice = cost;
        return true;
    }
}
