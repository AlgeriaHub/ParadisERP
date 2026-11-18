using System.Globalization;
using ParadisERP.Application.Regional;
using ParadisERP.Application.Rentals;

namespace ParadisERP.Tests.Rentals;

/// <summary>
/// The delivery hour and the return day (answers 5 and 6, 2026-09-30), pinned on the owner's own
/// example: "the contract from 27 to 29 counts two days only, because the day the goods come back
/// is not counted (if they come back in the morning, and as the branch manager decides)".
///
/// <para>
/// One unit at 3,000 a month is 100 a day, so every amount below is the number of days × 100.
/// The cutoff is 12:00, local time. The front office's mirror in <c>core/rentals.spec.ts</c>
/// asserts the same figures.
/// </para>
/// </summary>
public class RentalDaysTests
{
    private static RegionalSettings Money() => new() { CurrencyDecimals = 2 };

    private static DateTime At(string iso) => DateTime.Parse(iso, CultureInfo.InvariantCulture);

    private static DateOnly Day(string iso) => DateOnly.Parse(iso, CultureInfo.InvariantCulture);

    private static RentalLine Line(int durationDays) =>
        new(Guid.NewGuid(), 1, durationDays, RentUnitPrice: 100, DamageUnitPrice: 2000, OverdueUnitPrice: 100);

    /// <summary>What one unit issued and returned at these local times is charged, under this cutoff and decision.</summary>
    private static ContractSettlement Hire(
        int durationDays, string issuedLocal, string returnedLocal, int? cutoff, bool? decision = null)
    {
        var line = Line(durationDays);
        var issuedOn = RentalDays.PeriodStartsOn(At(issuedLocal), cutoff);
        var returned = At(returnedLocal);
        var counts = decision ?? RentalDays.CountsReturnDayByDefault(returned, cutoff);
        return RentalSettlement.Settle(
            new RentalContractInput(
                [line],
                [new IssueBatch(Guid.NewGuid(), line.Id, issuedOn, 1)],
                [new ReturnEntry(Guid.NewGuid(), line.Id, DateOnly.FromDateTime(returned), QtyOk: 1, CountsReturnDay: counts)],
                []),
            Money());
    }

    private static int DaysCharged(ContractSettlement s) => s.Charges.Where(c => c.Kind is RentalChargeKind.Rent or RentalChargeKind.Overdue).Sum(c => c.Days);

    // ---------- the delivery hour ----------

    [Fact]
    public void Goods_out_after_the_cutoff_start_their_period_the_next_day()
    {
        Assert.Equal(Day("2026-09-27"), RentalDays.PeriodStartsOn(At("2026-09-26T15:00"), 12));
        // At the hour itself the afternoon has begun.
        Assert.Equal(Day("2026-09-27"), RentalDays.PeriodStartsOn(At("2026-09-26T12:00"), 12));
        Assert.Equal(Day("2026-09-26"), RentalDays.PeriodStartsOn(At("2026-09-26T11:59"), 12));
    }

    [Fact]
    public void With_no_cutoff_the_delivery_day_counts_and_the_return_day_never_does()
    {
        Assert.Equal(Day("2026-09-26"), RentalDays.PeriodStartsOn(At("2026-09-26T23:30"), null));
        Assert.False(RentalDays.CountsReturnDayByDefault(At("2026-09-29T23:30"), null));

        // Today's arithmetic, unchanged: out on the 26th, back on the 29th, three days.
        Assert.Equal(3, DaysCharged(Hire(3, "2026-09-26T15:00", "2026-09-29T16:00", cutoff: null)));
    }

    [Fact]
    public void A_three_day_hire_starting_on_the_27th_is_due_back_on_the_morning_of_the_30th()
    {
        var start = RentalDays.PeriodStartsOn(At("2026-09-26T15:00"), 12);
        // Its three days are the 27th, 28th and 29th; back by the morning of the 30th owes nothing more.
        Assert.Equal(Day("2026-09-30"), RentalPricing.PeriodEndsOn(start, 3));
    }

    // ---------- the owner's example ----------

    [Fact]
    public void Back_on_the_29th_in_the_morning_is_two_days()
    {
        var s = Hire(3, "2026-09-26T15:00", "2026-09-29T09:00", cutoff: 12);

        Assert.Equal(2, DaysCharged(s));
        Assert.Equal(200m, s.Total(RentalChargeKind.Rent));
        // The third day was invoiced when the goods went out and is given back.
        Assert.Equal(100m, s.UnusedRent);
    }

    [Fact]
    public void Back_on_the_29th_in_the_afternoon_is_three_days()
    {
        var s = Hire(3, "2026-09-26T15:00", "2026-09-29T16:00", cutoff: 12);

        Assert.Equal(3, DaysCharged(s));
        Assert.Equal(300m, s.Total(RentalChargeKind.Rent));
        Assert.Equal(0m, s.UnusedRent);
    }

    [Fact]
    public void The_manager_may_waive_the_afternoon_and_it_is_two_days_again()
    {
        var s = Hire(3, "2026-09-26T15:00", "2026-09-29T16:00", cutoff: 12, decision: false);

        Assert.Equal(2, DaysCharged(s));
        Assert.Equal(200m, s.Total(RentalChargeKind.Rent));
        // ...but only with a reason, because it departs from the rule.
        Assert.Equal("returnDayNeedsReason", RentalDays.ReturnDayProblem(false, byDefault: true, reason: null));
        Assert.Equal("returnDayNeedsReason", RentalDays.ReturnDayProblem(false, byDefault: true, reason: "   "));
        Assert.Null(RentalDays.ReturnDayProblem(false, byDefault: true, reason: "Client fidèle, retour à 12h10"));
    }

    [Fact]
    public void The_manager_may_count_a_morning_too_with_a_reason()
    {
        Assert.Equal(3, DaysCharged(Hire(3, "2026-09-26T15:00", "2026-09-29T09:00", cutoff: 12, decision: true)));
        Assert.Equal("returnDayNeedsReason", RentalDays.ReturnDayProblem(true, byDefault: false, reason: ""));
        // Confirming what the rule already says is no departure and needs nothing.
        Assert.Null(RentalDays.ReturnDayProblem(false, byDefault: false, reason: null));
        Assert.Null(RentalDays.ReturnDayProblem(null, byDefault: true, reason: null));
    }

    [Fact]
    public void On_a_two_day_hire_the_counted_afternoon_is_a_day_overdue()
    {
        // "27 to 29 counts two days": due back on the 29th. In the morning it is exactly the
        // period; in the afternoon the counted return day is one day past it.
        var morning = Hire(2, "2026-09-26T15:00", "2026-09-29T09:00", cutoff: 12);
        Assert.Equal(0m, morning.Total(RentalChargeKind.Overdue));
        Assert.Equal(200m, morning.Total(RentalChargeKind.Rent));

        var afternoon = Hire(2, "2026-09-26T15:00", "2026-09-29T16:00", cutoff: 12);
        Assert.Equal(200m, afternoon.Total(RentalChargeKind.Rent));
        Assert.Equal(100m, afternoon.Total(RentalChargeKind.Overdue));
    }

    [Fact]
    public void Out_and_back_the_same_afternoon_is_still_a_days_hire()
    {
        // Starts tomorrow, back today after the cutoff: the minimum of one day still holds.
        Assert.Equal(1, DaysCharged(Hire(3, "2026-09-26T15:00", "2026-09-26T17:00", cutoff: 12)));
    }

    [Fact]
    public void A_reported_loss_stops_rent_on_the_report_day_whatever_the_hour_of_the_note()
    {
        var line = Line(30);
        var entry = new ReturnEntry(
            Guid.NewGuid(), line.Id, Day("2026-09-29"), QtyMissing: 1, ReportedOn: Day("2026-09-28"), CountsReturnDay: true);
        Assert.Equal(Day("2026-09-28"), entry.RentStopsOn(Day("2026-09-27")));
        Assert.Equal(Day("2026-09-30"), (entry with { ReportedOn = null }).RentStopsOn(Day("2026-09-27")));
    }

    // ---------- the setting ----------

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(12, true)]
    [InlineData(23, true)]
    [InlineData(24, false)]
    [InlineData(-1, false)]
    public void The_cutoff_is_an_hour_of_the_day_or_none(int? hour, bool valid)
    {
        Assert.Equal(valid, RentalDays.CutoffProblem(hour) is null);
        var settings = new RentalSettings(DayCutoffHour: hour);
        if (valid) RentalRules.Validate(settings);
        else Assert.Throws<ArgumentException>(() => RentalRules.Validate(settings));
    }

    [Fact]
    public void Local_time_is_the_company_s_not_the_server_s()
    {
        var algiers = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");
        // 11:30 UTC is 12:30 in Algiers: after a noon cutoff there, before it in UTC.
        var local = RentalDays.LocalTime(new DateTime(2026, 9, 26, 11, 30, 0, DateTimeKind.Utc), algiers);
        Assert.Equal(12, local.Hour);
        Assert.Equal(Day("2026-09-27"), RentalDays.PeriodStartsOn(local, 12));
    }
}
