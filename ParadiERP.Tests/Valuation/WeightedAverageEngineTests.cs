using ParadiERP.Domain;
using ParadiERP.Infrastructure.Valuation;

namespace ParadiERP.Tests.Valuation;

/// <summary>
/// The replay's engine on its own (C-01): what a sale or refund would be valued at before its movement exists must be
/// exactly what the replay values it at once it does, and applying the history in two halves gives the whole.
/// </summary>
public class WeightedAverageEngineTests
{
    private static readonly Guid Product = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Algiers = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Oran = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTime T0 = new(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
    private static int _seq;

    private static ShadowPolicy Policy(RefundValuation refund = RefundValuation.SaleCost, int decimals = 3,
        StockValuationScope scope = StockValuationScope.Company) => new(scope, refund, decimals);

    private static ShadowMovement M(StockMovementKind kind, decimal delta, decimal? unitCost, Guid? branch = null, Guid? sale = null, Guid? refundOf = null) =>
        new(Guid.NewGuid(), T0.AddMinutes(Interlocked.Increment(ref _seq)), Product, branch ?? Algiers, kind, delta,
            Math.Abs(delta), unitCost, null, null, sale, refundOf, null, null);

    [Theory]
    [InlineData(3)]   // a part of the pool
    [InlineData(10)]  // exactly the pool: the last unit takes what is left
    [InlineData(14)]  // past zero: the rest at the last average
    public void A_sale_previewed_is_valued_exactly_as_the_replay_then_values_it(int qty)
    {
        ShadowMovement[] history = [M(StockMovementKind.Receipt, 3, 100m / 3m), M(StockMovementKind.Receipt, 7, 41.17m), M(StockMovementKind.Sale, -1, 0m, sale: Guid.NewGuid())];
        var engine = new WeightedAverageEngine(Policy());
        engine.Open([new ShadowOpening(Product, Algiers, null, 1, 12.5m)]);
        foreach (var m in history) engine.Apply(m);

        var previewed = engine.PreviewOut(Product, Algiers, qty, 999m, out var noAverage);
        var sale = M(StockMovementKind.Sale, -qty, previewed / qty, sale: Guid.NewGuid());
        var replay = WeightedAverageShadow.Run(Policy(), [new ShadowOpening(Product, Algiers, null, 1, 12.5m)], [.. history, sale],
            new Dictionary<(Guid, Guid), decimal>(), Product);

        Assert.False(noAverage);
        Assert.Equal(replay.Lines[^1].ShadowValue, previewed);
        // The preview took nothing out.
        Assert.Equal(10m, engine.PoolOf(Product, Algiers)!.Qty);
    }

    [Fact]
    public void A_sale_with_no_average_ever_is_previewed_at_the_cost_price_and_said()
    {
        var engine = new WeightedAverageEngine(Policy());
        var value = engine.PreviewOut(Product, Algiers, 2, 450m, out var noAverage);
        Assert.True(noAverage);
        Assert.Equal(900m, value);
    }

    [Fact]
    public void A_refund_is_previewed_at_its_sale_s_cost_or_at_the_average_as_the_setting_says()
    {
        var sale = Guid.NewGuid();
        ShadowMovement[] history = [
            M(StockMovementKind.Receipt, 10, 100m),
            M(StockMovementKind.Sale, -2, 100m, sale: sale),
            M(StockMovementKind.Receipt, 10, 200m),
        ];
        var atSale = new WeightedAverageEngine(Policy(RefundValuation.SaleCost));
        var atAverage = new WeightedAverageEngine(Policy(RefundValuation.CurrentAverage));
        foreach (var m in history) { atSale.Apply(m); atAverage.Apply(m); }

        Assert.Equal(100m, atSale.PreviewRefund(Product, Algiers, 1, sale, 999m, out var n1, out var f1));
        Assert.Equal(WeightedAverageShadow.NoteRefundAtSaleCost, n1);
        Assert.False(f1);
        // 8 at 100 and 10 at 200: 2,800 for 18.
        Assert.Equal(Math.Round(2800m / 18m, 10), atAverage.PreviewRefund(Product, Algiers, 1, sale, 999m, out var n2, out var f2));
        Assert.Equal(WeightedAverageShadow.NoteRefundAtAverage, n2);
        Assert.False(f2);

        // A sale from before the opening comes back at what its line posted, once told.
        var older = Guid.NewGuid();
        atSale.KnowLineCost(older, Product, 90m);
        Assert.Equal(180m, atSale.PreviewRefund(Product, Algiers, 2, older, 999m, out var n3, out _));
        Assert.Equal(WeightedAverageShadow.NoteRefundAtPostedCost, n3);

        // Nothing ever came in: the cost price, said.
        var empty = new WeightedAverageEngine(Policy(RefundValuation.CurrentAverage));
        Assert.Equal(450m, empty.PreviewRefund(Product, Algiers, 1, sale, 450m, out _, out var f4));
        Assert.True(f4);
    }

    [Fact]
    public void Under_the_location_scope_a_sale_is_valued_from_its_own_location()
    {
        var engine = new WeightedAverageEngine(Policy(scope: StockValuationScope.Location));
        engine.Open([new ShadowOpening(Product, Algiers, null, 10, 50m), new ShadowOpening(Product, Oran, null, 10, 80m)]);
        Assert.Equal(100m, engine.PreviewOut(Product, Algiers, 2, 0m, out _));
        Assert.Equal(160m, engine.PreviewOut(Product, Oran, 2, 0m, out _));
    }

    [Fact]
    public void History_applied_in_two_halves_gives_what_the_whole_replay_gives_and_the_gaps_are_listed()
    {
        ShadowMovement[] history = [
            M(StockMovementKind.Receipt, 4, 100m),
            M(StockMovementKind.Sale, -6, 100m, sale: Guid.NewGuid()),
            M(StockMovementKind.Receipt, 5, 130m),
            M(StockMovementKind.Sale, -1, 0m, sale: Guid.NewGuid()),
        ];
        var whole = WeightedAverageShadow.Run(Policy(), [], history, new Dictionary<(Guid, Guid), decimal>());

        var engine = new WeightedAverageEngine(Policy());
        foreach (var m in WeightedAverageEngine.InReplayOrder(history.Take(2))) engine.Apply(m);
        foreach (var m in WeightedAverageEngine.InReplayOrder(history.Skip(2))) engine.Apply(m);
        var halves = engine.Result();

        var a = Assert.Single(whole.Pools);
        var b = Assert.Single(halves.Pools);
        Assert.Equal((a.Qty, a.Value, a.NegativeStockGap), (b.Qty, b.Value, b.NegativeStockGap));
        // 2 sold below zero at 100, covered at 130: a gap of 60, by the second receipt.
        var gap = Assert.Single(halves.Gaps!);
        Assert.Equal(60m, gap.Gap);
        Assert.Equal(history[2].Id, gap.MovementId);
        Assert.Equal(StockMovementKind.Receipt, gap.Kind);
    }
}
