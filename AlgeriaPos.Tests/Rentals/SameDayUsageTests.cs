using AlgeriaPos.Application.Regional;
using AlgeriaPos.Application.Rentals;
using AlgeriaPos.Domain;
using RentalChargeKind = AlgeriaPos.Application.Rentals.RentalChargeKind;

namespace AlgeriaPos.Tests.Rentals;

public class SameDayUsageTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);
    private static readonly RegionalSettings Money = new() { CurrencyDecimals = 2 };
    private readonly Guid lineId = Guid.NewGuid();

    private RentalContractInput Contract(IReadOnlyList<ReturnEntry> returns, int minimum = 0,
        DateOnly? periodStart = null, decimal qty = 100) => new(
        [new(lineId, qty, 30, 100, 500, 150)],
        [new(Guid.NewGuid(), lineId, periodStart ?? Today, qty, Today)], returns, [], minimum);

    private ReturnEntry Returned(decimal qty, SameDayUsage usage, Guid? note = null) =>
        new(note ?? Guid.NewGuid(), lineId, Today, QtyOk: qty, ReturnLineId: Guid.NewGuid(), SameDayUsage: usage);

    [Theory]
    [InlineData(SameDayUsage.Used, 1000)]
    [InlineData(SameDayUsage.NotUsed, 0)]
    public void Same_day_overrides_minimum_and_cutoff(SameDayUsage usage, decimal amount)
    {
        var input = Contract([Returned(10, usage)], minimum: 15, periodStart: Today.AddDays(1), qty: 10);
        var result = RentalSettlement.Settle(input, Money);
        Assert.Equal(amount, result.ChargeTotal);
        Assert.Equal(30000 - amount, result.UnusedRent);
        Assert.Equal(0, result.StillOutQty);
    }

    [Fact]
    public void Partial_unused_return_does_not_settle_remaining_quantity()
    {
        var result = RentalSettlement.Settle(Contract([Returned(40, SameDayUsage.NotUsed)]), Money);
        Assert.Equal(0, result.ChargeTotal);
        Assert.Equal(60, result.StillOutQty);
        Assert.Equal(120000, result.UnusedRent);
    }

    [Fact]
    public void Duplicate_contract_lines_in_one_note_keep_independent_decisions()
    {
        var note = Guid.NewGuid();
        var input = Contract([Returned(20, SameDayUsage.Used, note), Returned(30, SameDayUsage.NotUsed, note)]);
        var result = RentalSettlement.Settle(input, Money);
        Assert.Equal(2000, result.ChargeTotal);
        Assert.Equal(50, result.StillOutQty);
        var allocations = RentalSettlement.AllocateLine(input.Lines[0], 30, input.Issues, input.Returns, [], Money);
        Assert.Equal(2, allocations.Select(a => a.Return.ReturnLineId).Distinct().Count());
        Assert.Equal(new[] { 1, 0 }, allocations.Select(a => a.Stay.ChargedDays));
    }

    [Fact]
    public void Missing_keeps_normal_minimum_rent_and_compensation_when_physical_return_unused()
    {
        var note = Returned(4, SameDayUsage.NotUsed) with { QtyMissing = 6 };
        var result = RentalSettlement.Settle(Contract([note], minimum: 5, qty: 10), Money);
        Assert.Equal(3000, result.Total(RentalChargeKind.Rent));
        Assert.Equal(3000, result.Total(RentalChargeKind.Missing));
        Assert.Equal(27000, result.UnusedRent);
    }

    [Fact]
    public void Damaged_goods_are_physical_returns_but_compensation_is_unchanged()
    {
        var note = Returned(0, SameDayUsage.NotUsed) with
            { QtyPartialDamage = 2, MaintenanceValue = 80, QtyTotalDamage = 3 };
        var result = RentalSettlement.Settle(Contract([note], qty: 5), Money);
        Assert.Equal(0, result.Total(RentalChargeKind.Rent));
        Assert.Equal(80, result.Total(RentalChargeKind.Maintenance));
        Assert.Equal(1500, result.Total(RentalChargeKind.Damage));
    }

    [Fact]
    public void Later_day_preserves_existing_day_calculation()
    {
        var current = Returned(10, SameDayUsage.NotApplicable) with { ReturnedOn = Today.AddDays(1) };
        var legacy = current with { SameDayUsage = SameDayUsage.LegacyUnknown };
        Assert.Equal(RentalSettlement.Settle(Contract([legacy]), Money).ChargeTotal,
            RentalSettlement.Settle(Contract([current]), Money).ChargeTotal);
    }

    [Fact]
    public void Legacy_same_day_preserves_minimum_and_aggregate_mixed_condition_pricing()
    {
        var legacy = Returned(4, SameDayUsage.LegacyUnknown) with { QtyMissing = 6 };
        var input = Contract([legacy], minimum: 5, qty: 10);
        Assert.Equal(5000, RentalSettlement.Settle(input, Money).Total(RentalChargeKind.Rent));
        var allocations = RentalSettlement.AllocateLine(input.Lines[0], 30, input.Issues, input.Returns, [], Money, 5);
        Assert.Single(allocations);
        Assert.Equal(RentalReturnCondition.Legacy, allocations[0].Condition);
    }

    [Fact]
    public void Same_day_requires_explicit_decision_except_masked_planning()
    {
        var input = Contract([Returned(10, SameDayUsage.NotApplicable)]);
        Assert.Throws<ArgumentException>(() => RentalSettlement.Settle(input, Money));
        Assert.Single(RentalSettlement.AllocateLine(input.Lines[0], 30, input.Issues, input.Returns, [], Money,
            allowMissingUsage: true));
    }

    [Fact]
    public void Mixed_issue_dates_apply_usage_only_to_same_day_batch()
    {
        var input = Contract([Returned(10, SameDayUsage.NotUsed)]) with
        {
            Issues = [new(Guid.NewGuid(), lineId, Today.AddDays(-2), 4, Today.AddDays(-2)),
                new(Guid.NewGuid(), lineId, Today, 6, Today)]
        };
        var result = RentalSettlement.Settle(input, Money);
        Assert.Equal(800, result.ChargeTotal);
        Assert.Equal(0, result.StillOutQty);
    }
}
