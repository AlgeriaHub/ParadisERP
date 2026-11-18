using System.Globalization;
using ParadisERP.Application.Regional;
using ParadisERP.Application.Rentals;

namespace ParadisERP.Tests.Rentals;

public class RentalStayTests
{
    private static RegionalSettings Money => new() { CurrencyDecimals = 2 };

    private static DateOnly Day(string iso) => DateOnly.Parse(iso, CultureInfo.InvariantCulture);

    [Fact]
    public void Owners_example_settles_to_13000_and_any_split_of_the_window_adds_up_to_the_same()
    {
        var lineId = Guid.NewGuid();
        var line = new RentalLine(lineId, 100, 60, RentUnitPrice: 5, DamageUnitPrice: 0, OverdueUnitPrice: 7.5m);
        var issue = new IssueBatch(Guid.NewGuid(), lineId, Day("2026-01-01"), 100);
        var return1 = new ReturnEntry(Guid.NewGuid(), lineId, Day("2026-01-21"), QtyOk: 40);
        var return2 = new ReturnEntry(Guid.NewGuid(), lineId, Day("2026-01-31"), QtyOk: 60);

        var settlement = RentalSettlement.Settle(
            new RentalContractInput([line], [issue], [return1, return2], []),
            Money);
        Assert.Equal(13_000m, settlement.ChargeTotal);

        var entitlement = RentalSettlement.EntitlementFor(line, 60, []);
        var stay1 = RentalStay.Price(
            Day("2026-01-01"), Day("2026-01-21"), entitlement, 60, 7.5m, settledOn: Day("2026-01-21"));
        var stay2 = RentalStay.Price(
            Day("2026-01-01"), Day("2026-01-31"), entitlement, 60, 7.5m, settledOn: Day("2026-01-31"));

        var full1 = RentalStay.Value(Money, RentalStay.Clip(stay1.Stretches, Day("2026-01-01"), Day("2026-01-31")), 40);
        var full2 = RentalStay.Value(Money, RentalStay.Clip(stay2.Stretches, Day("2026-01-01"), Day("2026-01-31")), 60);
        Assert.Equal(13_000m, full1 + full2);

        var w1_take1 = RentalStay.Value(Money, RentalStay.Clip(stay1.Stretches, Day("2026-01-01"), Day("2026-01-15")), 40);
        var w1_take2 = RentalStay.Value(Money, RentalStay.Clip(stay2.Stretches, Day("2026-01-01"), Day("2026-01-15")), 60);
        var w1 = w1_take1 + w1_take2;
        Assert.Equal(7_500m, w1);

        var w2_take1 = RentalStay.Value(Money, RentalStay.Clip(stay1.Stretches, Day("2026-01-16"), Day("2026-01-31")), 40);
        var w2_take2 = RentalStay.Value(Money, RentalStay.Clip(stay2.Stretches, Day("2026-01-16"), Day("2026-01-31")), 60);
        var w2 = w2_take1 + w2_take2;
        Assert.Equal(13_000m, w1 + w2);
    }

    [Fact]
    public void Stretches_are_dated_from_the_start_and_overdue_follows_the_period()
    {
        var entitlement = RentalPeriod.Entitlement(100, 10, []);
        var stay = RentalStay.Price(
            Day("2026-03-01"), Day("2026-03-15"), entitlement, 10, 150, settledOn: Day("2026-03-15"));

        Assert.Equal(14, stay.ChargedDays);
        Assert.Equal(2, stay.Stretches.Count);
        Assert.Equal(new RentalStretch(Day("2026-03-01"), 10, 100, RentalChargeKind.Rent), stay.Stretches[0]);
        Assert.Equal(new RentalStretch(Day("2026-03-11"), 4, 150, RentalChargeKind.Overdue), stay.Stretches[1]);
    }

    [Fact]
    public void A_waived_note_prices_no_overdue_stretch()
    {
        var entitlement = RentalPeriod.Entitlement(100, 10, []);
        var stay = RentalStay.Price(
            Day("2026-03-01"), Day("2026-03-15"), entitlement, 10, 150, waivesOverdue: true, settledOn: Day("2026-03-15"));

        Assert.Single(stay.Stretches);
        Assert.Equal(new RentalStretch(Day("2026-03-01"), 10, 100, RentalChargeKind.Rent), stay.Stretches[0]);
    }

    [Fact]
    public void An_extension_is_its_own_stretch_at_its_own_price()
    {
        var extId = Guid.NewGuid();
        var extension = new RentalExtension(extId, null, 5, 120, Day("2026-03-05"));
        var entitlement = RentalPeriod.Entitlement(100, 10, [extension]);

        var stay = RentalStay.Price(
            Day("2026-03-01"), Day("2026-03-20"), entitlement, 10, 150, settledOn: Day("2026-03-20"));

        Assert.Equal(3, stay.Stretches.Count);
        Assert.Equal(new RentalStretch(Day("2026-03-01"), 10, 100, RentalChargeKind.Rent, null), stay.Stretches[0]);
        Assert.Equal(new RentalStretch(Day("2026-03-11"), 5, 120, RentalChargeKind.Rent, extId), stay.Stretches[1]);
        Assert.Equal(new RentalStretch(Day("2026-03-16"), 4, 150, RentalChargeKind.Overdue, null), stay.Stretches[2]);
    }

    [Fact]
    public void The_minimum_top_up_belongs_to_the_window_holding_the_return()
    {
        var entitlement = RentalPeriod.Entitlement(100, 30, []);
        var stay = RentalStay.Price(
            Day("2026-03-01"), Day("2026-03-04"), entitlement, 30, 100, minimumDays: 7, settledOn: Day("2026-03-04"));

        Assert.Equal(3, stay.DaysOut);
        Assert.Equal(7, stay.ChargedDays);
        Assert.Equal(2, stay.Stretches.Count);
        Assert.Equal(new RentalStretch(Day("2026-03-01"), 3, 100, RentalChargeKind.Rent), stay.Stretches[0]);
        Assert.Equal(new RentalStretch(Day("2026-03-04"), 4, 100, RentalChargeKind.Rent, MinimumTopUp: true), stay.Stretches[1]);

        Assert.Single(stay.Spans);
        Assert.Equal(new RentalSpan(7, 100, RentalChargeKind.Rent), stay.Spans[0]);

        var clip1 = RentalStay.Clip(stay.Stretches, Day("2026-03-01"), Day("2026-03-03"));
        Assert.Single(clip1);
        Assert.Equal(3, clip1[0].Days);
        Assert.False(clip1[0].MinimumTopUp);

        var clip2 = RentalStay.Clip(stay.Stretches, Day("2026-03-04"), Day("2026-03-31"));
        Assert.Single(clip2);
        Assert.Equal(4, clip2[0].Days);
        Assert.True(clip2[0].MinimumTopUp);
    }

    [Fact]
    public void Goods_still_out_are_priced_through_the_day_with_no_minimum()
    {
        var entitlement = RentalPeriod.Entitlement(100, 5, []);
        var stay = RentalStay.Price(
            Day("2026-03-01"), Day("2026-03-09"), entitlement, 5, 150, minimumDays: 7, settledOn: null);

        Assert.Equal(8, stay.ChargedDays);
        Assert.Equal(2, stay.Stretches.Count);
        Assert.Equal(new RentalStretch(Day("2026-03-01"), 5, 100, RentalChargeKind.Rent), stay.Stretches[0]);
        Assert.Equal(new RentalStretch(Day("2026-03-06"), 3, 150, RentalChargeKind.Overdue), stay.Stretches[1]);
        Assert.DoesNotContain(stay.Stretches, s => s.MinimumTopUp);
    }
}
