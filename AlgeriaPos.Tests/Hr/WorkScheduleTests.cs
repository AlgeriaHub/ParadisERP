using AlgeriaPos.Application.Payroll;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Hr;
using AlgeriaPos.Application.Contracts;

namespace AlgeriaPos.Tests.Hr;

/// <summary>
/// The working day as an admin setting, with dated periods such as Ramadan (owner, 2026-10-10:
/// "08:00 to 17:00, a setting the admin decides, and keep Ramadan in mind").
/// </summary>
public class WorkScheduleTests
{
    private static readonly DateOnly Monday = new(2026, 3, 2);
    private static readonly DateOnly Friday = new(2026, 3, 6);

    private static WorkSchedule WithRamadan() => new()
    {
        Periods = [new WorkSchedulePeriod
        {
            Name = "Ramadan 2027", From = new(2027, 2, 8), To = new(2027, 3, 9), Start = "08:00", End = "14:00", BreakMinutes = 0
        }]
    };

    // ---------- which hours apply ----------

    [Fact]
    public void A_normal_day_is_eight_to_five_less_an_hour_and_friday_is_the_rest_day()
    {
        var day = WorkScheduleRules.ForDay(new WorkSchedule(), Monday);

        Assert.Equal(new TimeOnly(8, 0), day.Start);
        Assert.Equal(new TimeOnly(17, 0), day.End);
        Assert.Equal(8m, day.PaidHours);
        Assert.False(day.IsRestDay);
        Assert.Null(day.PeriodName);
        Assert.True(WorkScheduleRules.ForDay(new WorkSchedule(), Friday).IsRestDay);
    }

    [Fact]
    public void A_day_inside_ramadan_takes_the_period_s_hours_from_its_first_day_to_its_last()
    {
        var schedule = WithRamadan();

        foreach (var inside in new DateOnly[] { new(2027, 2, 8), new(2027, 2, 20), new(2027, 3, 9) })
        {
            var day = WorkScheduleRules.ForDay(schedule, inside);
            Assert.Equal("Ramadan 2027", day.PeriodName);
            Assert.Equal(new TimeOnly(14, 0), day.End);
            Assert.Equal(6m, day.PaidHours);
        }

        foreach (var outside in new DateOnly[] { new(2027, 2, 7), new(2027, 3, 10) })
        {
            var day = WorkScheduleRules.ForDay(schedule, outside);
            Assert.Null(day.PeriodName);
            Assert.Equal(8m, day.PaidHours);
        }
    }

    [Fact]
    public void A_full_day_is_the_company_s_hours_per_day_except_inside_a_period()
    {
        // A company that had set 7 hours keeps reading a normal day exactly as before the schedule.
        var settings = new PayrollSettings { HoursPerDay = 7m, Schedule = WithRamadan() };

        Assert.Equal(7m, WorkScheduleRules.FullDayHours(settings, new DateOnly(2027, 1, 12)));
        Assert.Equal(6m, WorkScheduleRules.FullDayHours(settings, new DateOnly(2027, 2, 15)));
    }

    // ---------- what is refused ----------

    [Fact]
    public void Two_periods_claiming_the_same_day_are_refused()
    {
        var schedule = WithRamadan();
        schedule.Periods.Add(new WorkSchedulePeriod
        {
            Name = "Inventaire", From = new(2027, 3, 9), To = new(2027, 3, 12), Start = "07:00", End = "13:00"
        });

        var refused = Assert.Throws<ArgumentException>(() => WorkScheduleRules.NormalizeAndValidate(schedule));
        Assert.Contains("overlap", refused.Message);
    }

    [Theory]
    [InlineData("17:00", "08:00", 60)]   // ends before it starts
    [InlineData("08:00", "09:00", 60)]   // a break as long as the day
    [InlineData("8h", "17:00", 60)]      // not a time
    [InlineData("08:00", "17:00", -5)]   // a negative break
    public void A_working_day_that_cannot_be_read_is_refused(string start, string end, int breakMinutes)
    {
        var schedule = new WorkSchedule { Start = start, End = end, BreakMinutes = breakMinutes };
        Assert.Throws<ArgumentException>(() => WorkScheduleRules.NormalizeAndValidate(schedule));
    }

    [Fact]
    public void A_period_needs_a_name_and_must_not_end_before_it_starts_and_a_week_needs_a_working_day()
    {
        Assert.Throws<ArgumentException>(() => WorkScheduleRules.NormalizeAndValidate(new WorkSchedule
        {
            Periods = [new WorkSchedulePeriod { Name = " ", From = new(2027, 2, 8), To = new(2027, 3, 9) }]
        }));
        Assert.Throws<ArgumentException>(() => WorkScheduleRules.NormalizeAndValidate(new WorkSchedule
        {
            Periods = [new WorkSchedulePeriod { Name = "Ramadan", From = new(2027, 3, 9), To = new(2027, 2, 8) }]
        }));
        Assert.Throws<ArgumentException>(() => WorkScheduleRules.NormalizeAndValidate(new WorkSchedule
        {
            RestDays = Enum.GetValues<DayOfWeek>().ToList()
        }));
    }

    [Fact]
    public void Times_are_tidied_to_hours_and_minutes_and_periods_put_in_date_order()
    {
        var schedule = new WorkSchedule
        {
            Start = "8:00",
            End = "17:00:00",
            Periods =
            [
                new WorkSchedulePeriod { Name = "B", From = new(2027, 5, 1), To = new(2027, 5, 2), Start = "8:00", End = "12:00" },
                new WorkSchedulePeriod { Name = "A", From = new(2027, 2, 8), To = new(2027, 3, 9), Start = "08:00", End = "14:00" }
            ]
        };

        WorkScheduleRules.NormalizeAndValidate(schedule);

        Assert.Equal("08:00", schedule.Start);
        Assert.Equal("17:00", schedule.End);
        Assert.Equal(["A", "B"], schedule.Periods.Select(p => p.Name));
    }

    // ---------- a day read against its schedule ----------

    private static DaySchedule Normal(DateOnly? date = null) => WorkScheduleRules.ForDay(new WorkSchedule(), date ?? Monday);

    [Fact]
    public void Arriving_after_the_start_is_late_by_the_minutes_beyond_the_grace()
    {
        Assert.Equal(7, WorkScheduleRules.Review(Normal(), 0, new(8, 7), new(17, 0), 480, false).LateMinutes);
        Assert.Equal(0, WorkScheduleRules.Review(Normal(), 10, new(8, 7), new(17, 0), 480, false).LateMinutes);
        Assert.Equal(5, WorkScheduleRules.Review(Normal(), 10, new(8, 15), new(17, 0), 480, false).LateMinutes);
        // Early is never late, and does not wrap round midnight into a day's lateness.
        Assert.Equal(0, WorkScheduleRules.Review(Normal(), 0, new(7, 50), new(17, 0), 480, false).LateMinutes);
    }

    [Fact]
    public void Leaving_before_the_end_is_an_early_leave_and_after_it_possible_overtime()
    {
        var early = WorkScheduleRules.Review(Normal(), 0, new(8, 0), new(16, 30), 450, false);
        Assert.Equal(30, early.EarlyLeaveMinutes);
        Assert.Equal(0, early.PossibleOvertimeMinutes);

        var late = WorkScheduleRules.Review(Normal(), 0, new(8, 0), new(17, 45), 525, false);
        Assert.Equal(0, late.EarlyLeaveMinutes);
        Assert.Equal(45, late.PossibleOvertimeMinutes);

        var exact = WorkScheduleRules.Review(Normal(), 0, new(8, 0), new(17, 0), 480, false);
        Assert.False(exact.HasFlag);
    }

    [Fact]
    public void An_entry_with_no_exit_is_a_missing_check_out_and_nothing_is_invented_for_it()
    {
        var review = WorkScheduleRules.Review(Normal(), 0, new(8, 0), null, 0, true);

        Assert.True(review.MissingCheckOut);
        Assert.Equal(0, review.EarlyLeaveMinutes);
        Assert.Equal(0, review.PossibleOvertimeMinutes);
    }

    [Fact]
    public void Work_on_the_rest_day_is_all_possible_overtime_and_never_late()
    {
        var review = WorkScheduleRules.Review(Normal(Friday), 0, new(9, 0), new(13, 0), 240, false);

        Assert.True(review.WorkedOnRestDay);
        Assert.Equal(240, review.PossibleOvertimeMinutes);
        Assert.Equal(0, review.LateMinutes);
        Assert.Equal(0, review.EarlyLeaveMinutes);
    }

    [Fact]
    public void In_ramadan_leaving_at_two_is_on_time_and_staying_till_five_is_three_hours_more()
    {
        var day = WorkScheduleRules.ForDay(WithRamadan(), new DateOnly(2027, 2, 15));

        Assert.False(WorkScheduleRules.Review(day, 0, new(8, 0), new(14, 0), 360, false).HasFlag);
        Assert.Equal(180, WorkScheduleRules.Review(day, 0, new(8, 0), new(17, 0), 540, false).PossibleOvertimeMinutes);
    }

    // ---------- what a day counts for ----------

    [Fact]
    public void Six_hours_worked_on_a_ramadan_day_is_a_full_day_and_on_a_normal_day_three_quarters()
    {
        var settings = new PayrollSettings { HoursPerDay = 8m, Schedule = WithRamadan() };
        decimal fullDay(DateOnly d) => WorkScheduleRules.FullDayHours(settings, d);

        var ramadan = AttendanceSummarizer.FromShifts(
            [new ClockShift(new(2027, 2, 15), DateTime.UtcNow, DateTime.UtcNow, 6m)], fullDay);
        var normal = AttendanceSummarizer.FromShifts(
            [new ClockShift(new(2027, 1, 12), DateTime.UtcNow, DateTime.UtcNow, 6m)], fullDay);
        var ticked = AttendanceSummarizer.FromDays(
            [new AttendanceDayEntry(new(2027, 2, 16), AttendanceDayStatus.Present, 6m)], fullDay);

        Assert.Equal(1m, ramadan.DaysPresent);
        Assert.Equal(0.75m, normal.DaysPresent);
        Assert.Equal(1m, ticked.DaysPresent);
    }

    // ---------- who may change it ----------

    [Fact]
    public void The_schedule_is_the_company_s_own_and_a_screen_that_sends_none_keeps_it()
    {
        var current = PayrollPresets.Create("DZ")!;
        current.Schedule = WithRamadan();

        var edit = PayrollPresets.Create("DZ")!;
        edit.Schedule = new WorkSchedule { Start = "07:30", End = "16:30" };
        var saved = PayrollOwnership.ApplyCompanyEdit(current, edit);
        Assert.Equal("07:30", saved.Schedule.Start);

        var older = PayrollPresets.Create("DZ")!;
        older.Schedule = null!;
        var kept = PayrollOwnership.ApplyCompanyEdit(current, older);
        Assert.Equal("Ramadan 2027", Assert.Single(kept.Schedule.Periods).Name);

        // A statutory save copies settings through the hand-written clone: the hours must survive it.
        var statutory = PayrollOwnership.ApplyOperatorEdit(current, reviewed: false);
        Assert.Equal("Ramadan 2027", Assert.Single(statutory.Schedule.Periods).Name);
        Assert.Equal(PayrollFieldOwner.Company, PayrollOwnership.Fields[nameof(PayrollSettings.Schedule)]);
    }

    // ---------- against a database ----------

    [Fact]
    public async Task A_month_of_punches_is_read_against_the_day_s_schedule_and_saved_hours_come_back()
    {
        await using var h = await HrHarness.NewAsync();
        var worker = await h.HireAsync("Youcef Hidouche", attendanceMode: "Recorded");

        var read = await h.Settings.ReadAsync();
        var edited = read.Settings;
        edited.Schedule = new WorkSchedule
        {
            Periods = [new WorkSchedulePeriod
            {
                Name = "Ramadan", From = new(2026, 3, 9), To = new(2026, 3, 10), Start = "08:00", End = "14:00"
            }]
        };
        var saved = await h.Settings.ApplyCompanyEditAsync(edited, read.Revision, "admin");
        Assert.True(saved.Ok, saved.Message);
        Assert.Equal("Ramadan", Assert.Single((await h.Settings.ReadAsync()).Settings.Schedule.Periods).Name);

        // Algiers is UTC+1: 07:07Z is 08:07 local.
        await Punch(h, worker.Id, new DateTime(2026, 3, 2, 7, 7, 0, DateTimeKind.Utc), "In");    // Mon 08:07, late 7
        await Punch(h, worker.Id, new DateTime(2026, 3, 2, 16, 45, 0, DateTimeKind.Utc), "Out"); // 17:45, +45
        await Punch(h, worker.Id, new DateTime(2026, 3, 3, 7, 0, 0, DateTimeKind.Utc), "In");    // Tue, no exit
        await Punch(h, worker.Id, new DateTime(2026, 3, 9, 7, 0, 0, DateTimeKind.Utc), "In");    // Ramadan Mon
        await Punch(h, worker.Id, new DateTime(2026, 3, 9, 13, 0, 0, DateTimeKind.Utc), "Out");  // 14:00, on time
        await Punch(h, worker.Id, new DateTime(2026, 3, 13, 8, 0, 0, DateTimeKind.Utc), "In");   // Friday 09:00
        await Punch(h, worker.Id, new DateTime(2026, 3, 13, 12, 0, 0, DateTimeKind.Utc), "Out"); // 13:00

        var review = new AttendanceReview(h.Db, h.Settings, new AlgeriaZone());
        var all = await review.ReadAsync(null, HrHarness.Algiers, 2026, 3, flaggedOnly: false);
        Assert.True(all.Ok, all.Message);
        var days = all.Value!.Days.ToDictionary(d => d.WorkDate);

        Assert.Equal(7, days[new(2026, 3, 2)].LateMinutes);
        Assert.Equal(45, days[new(2026, 3, 2)].PossibleOvertimeMinutes);
        Assert.Equal("08:07", days[new(2026, 3, 2)].FirstIn);
        Assert.True(days[new(2026, 3, 3)].MissingCheckOut);
        Assert.Equal("Ramadan", days[new(2026, 3, 9)].PeriodName);
        Assert.Equal("14:00", days[new(2026, 3, 9)].ScheduledEnd);
        Assert.Equal(0, days[new(2026, 3, 9)].EarlyLeaveMinutes);
        Assert.True(days[new(2026, 3, 13)].WorkedOnRestDay);
        Assert.Equal(240, days[new(2026, 3, 13)].PossibleOvertimeMinutes);
        Assert.Equal("Youcef Hidouche", Assert.Single(all.Value.People).EmployeeName);

        var flagged = await review.ReadAsync(worker.Id, null, 2026, 3, flaggedOnly: true);
        Assert.DoesNotContain(flagged.Value!.Days, d => d.WorkDate == new DateOnly(2026, 3, 9));

        // Monday is a full day, the Ramadan day's six hours are a full day too (not three quarters),
        // Friday's four hours are half of the normal eight, and Tuesday's lone entry counts nothing.
        var row = Assert.Single((await h.ReadSheetAsync(2026, 3)).Value!.Rows);
        Assert.Equal(2.5m, row.DaysPresent);
    }

    [Fact]
    public async Task A_month_that_is_not_a_month_or_names_nobody_is_refused()
    {
        await using var h = await HrHarness.NewAsync();
        var review = new AttendanceReview(h.Db, h.Settings, new AlgeriaZone());

        Assert.Equal(HrProblems.PeriodInvalid, (await review.ReadAsync(null, HrHarness.Algiers, 2026, 13, false)).Code);
        Assert.Equal(HrProblems.EmployeeRequired, (await review.ReadAsync(null, null, 2026, 3, false)).Code);
    }

    private sealed class AlgeriaZone : AlgeriaPos.Application.Regional.IRegionalSettingsProvider
    {
        public AlgeriaPos.Application.Regional.RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    private static Task<HrResult<ClockedDto>> Punch(HrHarness h, Guid employeeId, DateTime atUtc, string kind) =>
        h.Attendance.ClockAsync(new ClockPunchRequest(EmployeeId: employeeId, Kind: kind, AtUtc: atUtc), "gate");
}
