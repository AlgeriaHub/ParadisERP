using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Valuation;

namespace AlgeriaPos.Tests.Valuation;

/// <summary>
/// The weighted-average shadow's replay (P0.4b), on its own: figures in, figures out, no database.
/// The examples are the ones the accountant was sent (C-01, C-16).
/// </summary>
public class WeightedAverageShadowTests
{
    private static readonly Guid Product = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Algiers = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Oran = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTime T0 = new(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<(Guid, Guid), decimal> NoLines = new Dictionary<(Guid, Guid), decimal>();

    private static ShadowPolicy Company(RefundValuation refund = RefundValuation.SaleCost, int decimals = 3) =>
        new(StockValuationScope.Company, refund, decimals);

    private static int _seq;
    private static ShadowMovement M(StockMovementKind kind, decimal delta, decimal? unitCost, Guid? branch = null,
        Guid? sale = null, Guid? refundOf = null, Guid? order = null, Guid? transfer = null, decimal? moved = null, Guid? doc = null) =>
        new(Guid.NewGuid(), T0.AddMinutes(Interlocked.Increment(ref _seq)), Product, branch ?? Algiers, kind, delta,
            moved ?? Math.Abs(delta), unitCost, null, doc, sale, refundOf, order, transfer);

    private static void AssertIdentity(ShadowPool p) =>
        Assert.Equal(p.OpeningValue + p.InValue - p.OutValue + p.MovedInValue - p.MovedOutValue - p.NegativeStockGap, p.Value, 6);

    [Fact]
    public void Two_purchases_at_different_prices_cost_the_sales_at_their_average_and_leave_nothing()
    {
        // C-01's example: 100 at 2,000 and 100 at 2,400; all 200 sold. The last purchase price would post
        // 480,000 and leave 310 at −40,000; the average posts 440,000 and leaves 0.
        var sale = Guid.NewGuid();
        var result = WeightedAverageShadow.Run(Company(), [], [
            M(StockMovementKind.Receipt, 100, 2000m),
            M(StockMovementKind.Receipt, 100, 2400m),
            M(StockMovementKind.Sale, -200, 2400m, sale: sale),
        ], NoLines);

        var pool = Assert.Single(result.Pools);
        Assert.Equal(440_000m, pool.OutValue);
        Assert.Equal(440_000m, pool.ShadowCostOfSales);
        Assert.Equal(480_000m, pool.PostedCostOfSales);
        Assert.Equal(0m, pool.Qty);
        Assert.Equal(0m, pool.Value);
        AssertIdentity(pool);
    }

    [Fact]
    public void The_opening_is_carried_at_its_cost_and_a_purchase_moves_the_average()
    {
        var result = WeightedAverageShadow.Run(Company(), [new ShadowOpening(Product, Algiers, null, 10, 100m)], [
            M(StockMovementKind.Receipt, 10, 130m),
        ], NoLines);

        var pool = Assert.Single(result.Pools);
        Assert.Equal(1000m, pool.OpeningValue);
        Assert.Equal(115m, pool.Average);
        AssertIdentity(pool);
    }

    [Fact]
    public void A_unit_cost_is_rounded_going_out_and_the_last_unit_takes_what_is_left()
    {
        // 3 units worth 100: an average of 33.333…; each unit leaves at the average rounded to 3 decimals
        // (33.333, then 33.334 as the average drifts to 33.3335), and the last takes exactly what is left.
        var result = WeightedAverageShadow.Run(Company(decimals: 3), [], [
            M(StockMovementKind.Receipt, 3, 100m / 3m),
            M(StockMovementKind.Sale, -1, 0m, sale: Guid.NewGuid()),
            M(StockMovementKind.Sale, -1, 0m, sale: Guid.NewGuid()),
            M(StockMovementKind.Sale, -1, 0m, sale: Guid.NewGuid()),
        ], NoLines, Product);

        Assert.Equal([33.333m, 33.334m], result.Lines.Skip(1).Take(2).Select(l => l.ShadowValue));
        Assert.Equal(100m, result.Lines.Skip(1).Sum(l => l.ShadowValue), 9);
        var pool = Assert.Single(result.Pools);
        Assert.Equal(0m, pool.Value);
        AssertIdentity(pool);
    }

    [Fact]
    public void Goods_sold_below_zero_and_bought_in_later_leave_the_gap_C16_describes()
    {
        // C-16's example: 10 couplers sold with none in stock at 1,200; the delivery costs 1,300: gap 1,000.
        var result = WeightedAverageShadow.Run(Company(), [new ShadowOpening(Product, Algiers, null, 5, 1200m)], [
            M(StockMovementKind.Sale, -5, 1200m, sale: Guid.NewGuid()),
            M(StockMovementKind.Sale, -10, 1200m, sale: Guid.NewGuid()),
            M(StockMovementKind.Receipt, 10, 1300m),
        ], NoLines);

        var pool = Assert.Single(result.Pools);
        Assert.Equal(18_000m, pool.OutValue);
        Assert.Equal(1000m, pool.NegativeStockGap);
        Assert.Equal(0m, pool.Qty);
        Assert.Equal(0m, pool.Value);
        AssertIdentity(pool);
    }

    [Fact]
    public void A_delivery_bigger_than_the_shortfall_settles_it_and_carries_the_rest_at_its_cost()
    {
        var result = WeightedAverageShadow.Run(Company(), [new ShadowOpening(Product, Algiers, null, 2, 100m)], [
            M(StockMovementKind.Sale, -5, 100m, sale: Guid.NewGuid()),
            M(StockMovementKind.Receipt, 10, 120m),
        ], NoLines);

        var pool = Assert.Single(result.Pools);
        Assert.Equal(3m * 20m, pool.NegativeStockGap);
        Assert.Equal(7m, pool.Qty);
        Assert.Equal(840m, pool.Value);
        Assert.Equal(120m, pool.Average);
        AssertIdentity(pool);
    }

    [Fact]
    public void Goods_out_before_anything_came_in_leave_at_their_own_reference_cost_flagged()
    {
        var result = WeightedAverageShadow.Run(Company(), [], [M(StockMovementKind.Sale, -2, 75m, sale: Guid.NewGuid())], NoLines, Product);

        var pool = Assert.Single(result.Pools);
        Assert.Equal(150m, pool.OutValue);
        Assert.Equal(1, pool.OutflowsWithoutAverage);
        Assert.Contains(WeightedAverageShadow.NoteNoAverage, result.Lines[0].Notes);
    }

    [Fact]
    public void Under_the_company_scope_a_transfer_changes_nothing_and_a_loss_leaves_at_the_average()
    {
        var transfer = Guid.NewGuid();
        var result = WeightedAverageShadow.Run(Company(), [new ShadowOpening(Product, Algiers, null, 10, 50m)], [
            M(StockMovementKind.Receipt, 10, 70m, branch: Oran),
            M(StockMovementKind.TransferOut, -4, 50m, transfer: transfer),
            M(StockMovementKind.TransferIn, 3, 50m, branch: Oran, transfer: transfer),
            M(StockMovementKind.TransferLoss, 0, 50m, transfer: transfer, moved: 1),
        ], NoLines);

        var pool = Assert.Single(result.Pools);
        Assert.Equal(19m, pool.Qty);
        Assert.Equal(60m, pool.OutValue);
        Assert.Equal(60m, pool.Average);
        AssertIdentity(pool);
    }

    [Fact]
    public void Under_the_location_scope_a_transfer_carries_the_source_average_to_the_destination()
    {
        var transfer = Guid.NewGuid();
        var policy = new ShadowPolicy(StockValuationScope.Location, RefundValuation.SaleCost, 3);
        var result = WeightedAverageShadow.Run(policy, [
            new ShadowOpening(Product, Algiers, null, 10, 50m),
            new ShadowOpening(Product, Oran, null, 10, 80m),
        ], [
            M(StockMovementKind.TransferOut, -4, 50m, transfer: transfer),
            M(StockMovementKind.TransferIn, 3, 50m, branch: Oran, transfer: transfer),
            M(StockMovementKind.TransferLoss, 0, 50m, transfer: transfer, moved: 1),
        ], NoLines);

        var algiers = result.Pools.Single(p => p.BranchId == Algiers);
        var oran = result.Pools.Single(p => p.BranchId == Oran);
        Assert.Equal(300m, algiers.Value);
        Assert.Equal(200m, algiers.MovedOutValue);
        Assert.Equal(13m, oran.Qty);
        Assert.Equal(950m, oran.Value);
        Assert.Equal(0m, result.InTransitValue[Product]);
        AssertIdentity(algiers);
        AssertIdentity(oran);
    }

    [Fact]
    public void A_refund_comes_back_at_what_its_sale_took_out_or_at_the_average_as_the_admin_chooses()
    {
        var sale = Guid.NewGuid();
        var refund = Guid.NewGuid();
        ShadowMovement[] Moves() => [
            M(StockMovementKind.Receipt, 10, 100m),
            M(StockMovementKind.Sale, -2, 100m, sale: sale),
            M(StockMovementKind.Receipt, 10, 200m),
            M(StockMovementKind.Refund, 1, 200m, sale: refund, refundOf: sale),
        ];

        var atSale = Assert.Single(WeightedAverageShadow.Run(Company(RefundValuation.SaleCost), [], Moves(), NoLines).Pools);
        var atAverage = Assert.Single(WeightedAverageShadow.Run(Company(RefundValuation.CurrentAverage), [], Moves(), NoLines).Pools);

        // 8 at 100 + 10 at 200 = 2,800 for 18, an average of 155.555…
        Assert.Equal(2800m + 100m, atSale.Value);
        Assert.Equal(200m - 100m, atSale.ShadowCostOfSales);
        Assert.Equal(2800m + 155.556m, atAverage.Value, 3);
        AssertIdentity(atSale);
        AssertIdentity(atAverage);
    }

    [Fact]
    public void A_refund_of_a_sale_from_before_the_opening_comes_back_at_the_cost_its_line_posted()
    {
        var original = Guid.NewGuid();
        var lines = new Dictionary<(Guid, Guid), decimal> { [(original, Product)] = 90m };
        var result = WeightedAverageShadow.Run(Company(), [new ShadowOpening(Product, Algiers, null, 10, 100m)], [
            M(StockMovementKind.Refund, 1, 100m, sale: Guid.NewGuid(), refundOf: original),
        ], lines, Product);

        Assert.Equal(90m, result.Lines[0].ShadowValue);
        Assert.Contains(WeightedAverageShadow.NoteRefundAtPostedCost, result.Lines[0].Notes);
    }

    [Fact]
    public void A_production_return_goes_back_at_its_issues_cost_and_output_comes_in_at_its_reference_flagged()
    {
        var order = Guid.NewGuid();
        var result = WeightedAverageShadow.Run(Company(), [new ShadowOpening(Product, Algiers, null, 10, 40m)], [
            M(StockMovementKind.Receipt, 10, 60m),
            M(StockMovementKind.ManufacturingIssue, -4, 60m, order: order),
            M(StockMovementKind.Receipt, 4, 100m),
            M(StockMovementKind.ManufacturingReturn, 1, 60m, order: order),
            M(StockMovementKind.ManufacturingOutput, 2, 500m),
        ], NoLines, Product);

        Assert.Equal(50m, result.Lines[3].ShadowValue);
        Assert.Contains(WeightedAverageShadow.NoteReturnAtIssueCost, result.Lines[3].Notes);
        Assert.Equal(1000m, result.Lines[4].ShadowValue);
        var pool = Assert.Single(result.Pools);
        Assert.Equal(1, pool.OutputsAtReference);
        AssertIdentity(pool);
    }

    [Fact]
    public void A_state_change_and_a_rental_issue_move_no_value()
    {
        var result = WeightedAverageShadow.Run(Company(), [new ShadowOpening(Product, Algiers, null, 10, 40m)], [
            M(StockMovementKind.StateChange, 0, 40m, moved: 2),
            M(StockMovementKind.RentalIssue, 0, null, moved: 3),
            M(StockMovementKind.WriteOff, -1, 40m),
        ], NoLines);

        var pool = Assert.Single(result.Pools);
        Assert.Equal(9m, pool.Qty);
        Assert.Equal(360m, pool.Value);
        AssertIdentity(pool);
    }
}
