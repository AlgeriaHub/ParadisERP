using System.Globalization;
using ParadisERP.Application.Regional;
using ParadisERP.Application.Rentals;

namespace ParadisERP.Tests.Rentals;

/// <summary>
/// The arithmetic every rental figure is built from: the daily unit rate, the day count, the
/// frozen overdue price, and the way a period is consumed before overdue can begin.
/// </summary>
public class RentalPricingTests
{
    private static RegionalSettings Money(int decimals = 2) => new() { CurrencyDecimals = decimals };

    [Theory]
    [InlineData(1, 1, 5, 5)]
    [InlineData(1, 30, 5, 150)]
    [InlineData(100, 30, 5, 15000)]
    [InlineData(2, 30, 1000, 60000)]
    public void Rent_is_quantity_times_days_times_daily_unit_rate(decimal qty, int days, decimal rate, decimal expected) =>
        Assert.Equal(expected, RentalPricing.Amount(Money(), rate, days, qty));

    [Fact]
    public void Contract_total_is_the_sum_of_line_totals() =>
        Assert.Equal(17000m, RentalPricing.Amount(Money(), 5, 30, 100)
            + RentalPricing.Amount(Money(), 100, 10, 2));

    [Theory]
    [InlineData("2026-03-01", "2026-03-31", 30)]
    [InlineData("2026-03-01", "2026-03-02", 1)]
    // Out and back the same afternoon is a day's hire, not a free one.
    [InlineData("2026-03-01", "2026-03-01", 1)]
    // A return note dated before its issue note is nonsense; it still cannot produce free days.
    [InlineData("2026-03-10", "2026-03-01", 1)]
    public void Days_run_from_the_issue_note_with_a_one_day_minimum(string issued, string returned, int expected) =>
        Assert.Equal(expected, RentalPricing.DaysOut(
            DateOnly.Parse(issued, CultureInfo.InvariantCulture), DateOnly.Parse(returned, CultureInfo.InvariantCulture)));

    [Fact]
    public void A_period_ends_on_the_issue_date_plus_the_days_agreed()
    {
        var issued = new DateOnly(2026, 3, 1);
        var ends = RentalPricing.PeriodEndsOn(issued, 30);

        Assert.Equal(new DateOnly(2026, 3, 31), ends);
        // Back on the last day: exactly the period, nothing overdue.
        Assert.Equal(30, RentalPricing.DaysOut(issued, ends));
        // One day later: one day over.
        Assert.Equal(31, RentalPricing.DaysOut(issued, ends.AddDays(1)));
    }

    [Fact]
    public void The_overdue_price_is_the_rental_price_times_the_multiplier_frozen_at_signing()
    {
        var settings = new RentalSettings(OverdueMultiplier: 1.5m);

        Assert.Equal(4500m, RentalPricing.OverdueUnitPrice(Money(), 3000, settings.MultiplierFor(null)));
        // A per-item override wins over the company figure.
        Assert.Equal(6000m, RentalPricing.OverdueUnitPrice(Money(), 3000, settings.MultiplierFor(2m)));
        // And the frozen price is a price on a signed document, so it is rounded like one.
        Assert.Equal(1500.05m, RentalPricing.OverdueUnitPrice(Money(), 1000.03m, 1.5m));
    }

    [Fact]
    public void The_daily_rate_is_never_rounded_before_it_is_multiplied()
    {
        Assert.Equal(700m, RentalPricing.Amount(Money(), dailyUnitRate: 1000m / 30m, days: 3, qty: 7));
        Assert.NotEqual(699.93m, RentalPricing.Amount(Money(), 1000m / 30m, 3, 7));
    }

    [Fact]
    public void Currency_decimals_are_a_country_setting_not_always_two()
    {
        // 1000/30 x 11 x 7 = 2566.666...
        Assert.Equal(2566.67m, RentalPricing.Amount(Money(2), 1000m / 30m, 11, 7));
        Assert.Equal(2567m, RentalPricing.Amount(Money(0), 1000m / 30m, 11, 7));
        Assert.Equal(2566.667m, RentalPricing.Amount(Money(3), 1000m / 30m, 11, 7));
    }

    [Fact]
    public void Days_are_poured_into_the_agreed_period_then_extensions_then_overdue()
    {
        var extension = new RentalExtension(Guid.NewGuid(), null, ExtraDays: 10, UnitPrice: 3000,
            RaisedOn: new DateOnly(2026, 3, 25));
        var entitlement = RentalPeriod.Entitlement(rentUnitPrice: 3000, durationDays: 30, [extension]);

        Assert.Equal(40, RentalPeriod.EntitledDays(entitlement));

        // Inside the agreed period: one rent stretch, nothing else.
        var early = RentalPeriod.Apportion(20, entitlement, overdueUnitPrice: 4500);
        Assert.Equal([(20, RentalChargeKind.Rent)], early.Select(s => (s.Days, s.Kind)));

        // Past it, but inside the extension: still rent, at the extension's own signed price.
        var extended = RentalPeriod.Apportion(35, entitlement, 4500);
        Assert.Equal([(30, RentalChargeKind.Rent), (5, RentalChargeKind.Rent)], extended.Select(s => (s.Days, s.Kind)));
        Assert.Equal(extension.Id, extended[1].ExtensionId);

        // Past the extension too: the tail is overdue at the frozen overdue price.
        var late = RentalPeriod.Apportion(45, entitlement, 4500);
        Assert.Equal([(30, RentalChargeKind.Rent), (10, RentalChargeKind.Rent), (5, RentalChargeKind.Overdue)],
            late.Select(s => (s.Days, s.Kind)));
        Assert.Equal(4500m, late[^1].UnitPrice);
    }

    [Fact]
    public void Extensions_are_consumed_in_the_order_they_were_signed()
    {
        var first = new RentalExtension(Guid.NewGuid(), null, 5, 3000, new DateOnly(2026, 3, 20));
        var second = new RentalExtension(Guid.NewGuid(), null, 5, 3600, new DateOnly(2026, 4, 2));
        var entitlement = RentalPeriod.Entitlement(3000, 30, [second, first]);

        var used = RentalPeriod.Apportion(38, entitlement, 4500);
        Assert.Equal([first.Id, second.Id], used.Skip(1).Select(s => s.ExtensionId));
        Assert.Equal([5, 3], used.Skip(1).Select(s => s.Days));
    }

    [Fact]
    public void A_note_is_dated_by_the_companys_own_day_not_by_utc()
    {
        var algiers = TimeZoneInfo.CreateCustomTimeZone("test-dz", TimeSpan.FromHours(1), "Algiers", "Algiers");

        // 00:30 on the 2nd in Algiers is still the 1st in UTC. Counting from the UTC date would
        // charge the customer a day they never had.
        var utc = new DateTime(2026, 3, 1, 23, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 3, 2), RentalPricing.LocalDay(utc, algiers));
        Assert.Equal(new DateOnly(2026, 3, 1), DateOnly.FromDateTime(utc));
    }

    [Fact]
    public void Fifo_closes_the_oldest_batch_first_and_reports_what_was_never_issued()
    {
        var line = Guid.NewGuid();
        var older = new IssueBatch(Guid.NewGuid(), line, new DateOnly(2026, 3, 1), 60);
        var newer = new IssueBatch(Guid.NewGuid(), line, new DateOnly(2026, 3, 10), 40);

        // Given newest-first on purpose: the rule is the issue date, not the caller's order.
        var matches = RentalFifo.Match([newer, older], [70m, 40m]);

        Assert.Equal([(older.IssueNoteId, 60m), (newer.IssueNoteId, 10m)],
            matches[0].Takes.Select(t => (t.Batch.IssueNoteId, t.Qty)));
        Assert.Equal(0m, matches[0].Unmatched);

        // The second return asks for 40 when only 30 are left: 10 were never issued.
        Assert.Equal([(newer.IssueNoteId, 30m)], matches[1].Takes.Select(t => (t.Batch.IssueNoteId, t.Qty)));
        Assert.Equal(10m, matches[1].Unmatched);
    }
}
