using ParadisERP.Domain;
using ParadisERP.Infrastructure.Hr;
using Engine = ParadisERP.Application.Payroll;

namespace ParadisERP.Tests.Hr;

/// <summary>
/// The reduction both capture modes end in, checked without a database, a server or a clock.
///
/// <para>
/// These are the rules that decide what everybody is paid, so they are pinned where they can be
/// read against a hand calculation: a declared month and the same month ticked day by day have
/// to arrive at the same figures, an unpaired punch has to be reported rather than mended, and a
/// month has to be a month in the company's zone rather than in UTC.
/// </para>
/// </summary>
public class AttendanceSummaryTests
{
    /// <summary>Algiers is UTC+1 all year. Built rather than looked up, so the test says what it means.</summary>
    private static readonly TimeZoneInfo Algiers =
        TimeZoneInfo.CreateCustomTimeZone("Test/Algiers", TimeSpan.FromHours(1), "Test/Algiers", "Test/Algiers");

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void A_declared_total_and_a_recorded_month_reduce_to_the_same_summary_shape()
    {
        var declared = AttendanceSummarizer.FromDeclaration(
            daysPresent: 3m, paidLeaveDays: 1m, unpaidAbsenceDays: 1m,
            overtime: [new AttendanceOvertimeEntry("hs-jour", 4m)]);

        var recorded = AttendanceSummarizer.FromDays(
        [
            new AttendanceDayEntry(new DateOnly(2026, 3, 2), AttendanceDayStatus.Present,
                Overtime: [new AttendanceOvertimeEntry("hs-jour", 4m)]),
            new AttendanceDayEntry(new DateOnly(2026, 3, 3), AttendanceDayStatus.Present),
            new AttendanceDayEntry(new DateOnly(2026, 3, 4), AttendanceDayStatus.Present),
            new AttendanceDayEntry(new DateOnly(2026, 3, 5), AttendanceDayStatus.PaidLeave),
            new AttendanceDayEntry(new DateOnly(2026, 3, 6), AttendanceDayStatus.Absent)
        ], hoursPerDay: 8m);

        Assert.Equal(declared.DaysPresent, recorded.DaysPresent);
        Assert.Equal(declared.PaidLeaveDays, recorded.PaidLeaveDays);
        Assert.Equal(declared.UnpaidAbsenceDays, recorded.UnpaidAbsenceDays);
        Assert.Equal(declared.OvertimeHoursByRule, recorded.OvertimeHoursByRule);

        // The one thing that is not the same, and must not be: where the figures came from. The
        // engine warns differently for a month nobody filled in than for one that is genuinely
        // empty, and that difference is carried here rather than inferred from the numbers.
        Assert.Equal(Engine.AttendanceSource.Declared, declared.Source);
        Assert.Equal(Engine.AttendanceSource.Recorded, recorded.Source);
        Assert.Equal(Engine.AttendanceSource.None, Engine.AttendanceSummary.Nothing.Source);
    }

    [Fact]
    public void Clock_events_pair_into_hours_and_an_unpaired_in_is_reported_not_guessed()
    {
        var first = Guid.NewGuid();
        var orphan = Guid.NewGuid();

        var pairing = AttendanceSummarizer.Pair(
        [
            new ClockPunch(first, Utc(2026, 3, 2, 6), ClockEventKind.In),
            new ClockPunch(Guid.NewGuid(), Utc(2026, 3, 2, 14), ClockEventKind.Out),
            // He came back after lunch and nobody clocked him out. The system must not close the
            // shift at midnight, at the next punch, or at eight hours — it must say so.
            new ClockPunch(orphan, Utc(2026, 3, 3, 6), ClockEventKind.In)
        ], Algiers);

        var shift = Assert.Single(pairing.Shifts);
        Assert.Equal(8m, shift.Hours);
        Assert.Equal(new DateOnly(2026, 3, 2), shift.LocalDay);

        var missing = Assert.Single(pairing.Unpaired);
        Assert.Equal(orphan, missing.Id);

        // And nothing was invented for him: one day present, not two.
        Assert.Equal(1m, AttendanceSummarizer.FromShifts(pairing.Shifts, hoursPerDay: 8m).DaysPresent);
    }

    [Fact]
    public void An_out_with_no_open_in_is_reported_rather_than_read_as_a_shift_from_midnight()
    {
        var stray = Guid.NewGuid();

        var pairing = AttendanceSummarizer.Pair(
            [new ClockPunch(stray, Utc(2026, 3, 2, 14), ClockEventKind.Out)], Algiers);

        Assert.Empty(pairing.Shifts);
        Assert.Equal(stray, Assert.Single(pairing.Unpaired).Id);
    }

    [Fact]
    public void A_shift_crossing_midnight_belongs_to_the_day_it_started_in_the_company_time_zone()
    {
        // 22:00 UTC on the 2nd is 23:00 in Algiers — still the 2nd. The shift ends at 07:00 local
        // on the 3rd, and the whole of it belongs to the evening it started.
        var pairing = AttendanceSummarizer.Pair(
        [
            new ClockPunch(Guid.NewGuid(), Utc(2026, 3, 2, 22), ClockEventKind.In),
            new ClockPunch(Guid.NewGuid(), Utc(2026, 3, 3, 6), ClockEventKind.Out)
        ], Algiers);

        var shift = Assert.Single(pairing.Shifts);
        Assert.Equal(new DateOnly(2026, 3, 2), shift.LocalDay);
        Assert.Equal(8m, shift.Hours);
    }

    [Fact]
    public void Overtime_hours_are_kept_per_band()
    {
        // Bands are never added together: each carries its own multiplier, and a total would
        // have to pick one of them.
        var summary = AttendanceSummarizer.FromDays(
        [
            new AttendanceDayEntry(new DateOnly(2026, 3, 2), AttendanceDayStatus.Present,
                Overtime: [new AttendanceOvertimeEntry("hs-jour", 2m), new AttendanceOvertimeEntry("hs-nuit", 3m)]),
            new AttendanceDayEntry(new DateOnly(2026, 3, 6), AttendanceDayStatus.WeeklyRest,
                Overtime: [new AttendanceOvertimeEntry("hs-repos", 5m)]),
            new AttendanceDayEntry(new DateOnly(2026, 3, 9), AttendanceDayStatus.Present,
                Overtime: [new AttendanceOvertimeEntry("hs-jour", 1m)])
        ], hoursPerDay: 8m);

        Assert.Equal(3m, summary.OvertimeHoursByRule["hs-jour"]);
        Assert.Equal(3m, summary.OvertimeHoursByRule["hs-nuit"]);
        // Hours worked on a rest day are exactly the band a rest-day rule exists for, so the
        // status of the day does not silence them.
        Assert.Equal(5m, summary.OvertimeHoursByRule["hs-repos"]);
    }

    [Fact]
    public void A_period_is_a_calendar_month_in_the_company_time_zone_not_in_utc()
    {
        // 23:30 UTC on 31 March is 00:30 on 1 April in Algiers. Read in UTC this punch would be
        // filed against March — the month a run may already have locked.
        var instant = Utc(2026, 3, 31, 23, 30);

        Assert.Equal(new AttendancePeriod(2026, 4), AttendancePeriod.Of(instant, Algiers));
        Assert.Equal(new AttendancePeriod(2026, 3), AttendancePeriod.Of(instant, TimeZoneInfo.Utc));
        Assert.Equal(new DateOnly(2026, 4, 1), AttendancePeriod.LocalDay(instant, Algiers));

        var march = new AttendancePeriod(2026, 3);
        Assert.Equal(31, march.DayCount);
        Assert.Equal(new DateOnly(2026, 3, 1), march.FirstDay);
        Assert.Equal(new DateOnly(2026, 3, 31), march.LastDay);
        Assert.False(march.Contains(new DateOnly(2026, 4, 1)));
    }

    [Fact]
    public void A_rest_day_and_a_public_holiday_are_neither_worked_nor_absent()
    {
        // Deliberate: whether a daily worker is paid for a public holiday is statutory, and this
        // module has no answer for it. Counting it present would invent a payment; counting it
        // absent would invent a deduction. It stays out, and the sheet still shows the day.
        var summary = AttendanceSummarizer.FromDays(
        [
            new AttendanceDayEntry(new DateOnly(2026, 3, 6), AttendanceDayStatus.WeeklyRest),
            new AttendanceDayEntry(new DateOnly(2026, 3, 8), AttendanceDayStatus.PublicHoliday),
            new AttendanceDayEntry(new DateOnly(2026, 3, 9), AttendanceDayStatus.UnpaidLeave)
        ], hoursPerDay: 8m);

        Assert.Equal(0m, summary.DaysPresent);
        Assert.Equal(0m, summary.PaidLeaveDays);
        Assert.Equal(1m, summary.UnpaidAbsenceDays);
    }

    [Fact]
    public void Part_of_a_day_is_part_of_a_day_and_a_long_day_is_never_more_than_one()
    {
        var summary = AttendanceSummarizer.FromDays(
        [
            new AttendanceDayEntry(new DateOnly(2026, 3, 2), AttendanceDayStatus.Present, HoursWorked: 4m),
            // Ten hours is a full day plus overtime, and the overtime is counted in its own band.
            // Paying 1.25 days here and the band as well would pay those hours twice.
            new AttendanceDayEntry(new DateOnly(2026, 3, 3), AttendanceDayStatus.Present, HoursWorked: 10m)
        ], hoursPerDay: 8m);

        Assert.Equal(1.5m, summary.DaysPresent);
    }

    [Fact]
    public void The_capture_mode_is_the_employee_s_override_or_the_company_default()
    {
        Assert.Equal(AttendanceMode.Declared,
            AttendanceModes.Resolve(null, Engine.AttendanceMode.Declared));
        Assert.Equal(AttendanceMode.Recorded,
            AttendanceModes.Resolve(AttendanceMode.Recorded, Engine.AttendanceMode.Declared));
        Assert.Equal(AttendanceMode.Declared,
            AttendanceModes.Resolve(AttendanceMode.Declared, Engine.AttendanceMode.Recorded));
    }

    [Fact]
    public void The_pay_basis_and_attendance_mode_enums_mirror_the_domain()
    {
        // AttendanceModes.Resolve casts between the two copies. Two enums that drift would put
        // every employee in the other capture mode, and nothing else would notice.
        Assert.Equal(
            Enum.GetNames<AttendanceMode>().Zip(Enum.GetValues<AttendanceMode>().Select(v => (int)v)).ToArray(),
            Enum.GetNames<Engine.AttendanceMode>()
                .Zip(Enum.GetValues<Engine.AttendanceMode>().Select(v => (int)v)).ToArray());

        Assert.Equal(
            Enum.GetNames<PayBasis>().Zip(Enum.GetValues<PayBasis>().Select(v => (int)v)).ToArray(),
            Enum.GetNames<Engine.PayBasis>().Zip(Enum.GetValues<Engine.PayBasis>().Select(v => (int)v)).ToArray());
    }
}
