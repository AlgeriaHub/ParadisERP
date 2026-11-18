using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Hr;
using Microsoft.EntityFrameworkCore;
using Engine = ParadisERP.Application.Payroll;

namespace ParadisERP.Tests.Hr;

/// <summary>
/// The sheet, the day grid and the clock against a real database: what is stored, what is
/// refused, and what the run will read afterwards.
/// </summary>
public class AttendanceSheetTests
{
    [Fact]
    public async Task The_sheet_upsert_replaces_a_row_and_never_duplicates_it()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");

        await h.SaveSheetAsync(2026, 3, HrHarness.Declared(ali.Id, 20m, paidLeave: 2m));
        var second = await h.SaveSheetAsync(2026, 3, HrHarness.Declared(ali.Id, 24m,
            overtime: [new AttendanceOvertimeDto("hs-jour", 6m)]));

        Assert.True(second.Ok, second.Message);
        Assert.Single(await h.Db.AttendanceDeclarations.AsNoTracking().ToListAsync());

        var row = Assert.Single(second.Value!.Rows);
        Assert.Equal(24m, row.DaysPresent);
        // The paid leave of the first submission is gone: the sheet is a statement of the month,
        // not a pile of corrections.
        Assert.Equal(0m, row.PaidLeaveDays);
        Assert.Equal(6m, Assert.Single(row.Overtime).Hours);
        Assert.Equal("Declared", row.Source);
        Assert.Equal("foreman", row.UpdatedBy);

        // And the band rows were replaced too, rather than accumulated.
        Assert.Single(await h.Db.AttendanceDeclarationOvertimes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Attendance_for_a_period_whose_run_is_locked_is_refused_and_the_run_is_named()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");
        await h.RunAsync(2026, 3, PayrollRunStatus.Locked, number: "RUN-000007");

        var refused = await h.SaveSheetAsync(2026, 3, HrHarness.Declared(ali.Id, 24m));

        Assert.False(refused.Ok);
        Assert.Equal(HrFailure.Conflict, refused.Failure);
        Assert.Equal(HrProblems.PeriodLocked, refused.Code);
        // The screen has to be able to say *which* run to unlock, not that something somewhere is
        // holding the month.
        Assert.Equal("RUN-000007", refused.RunNumber);
        Assert.Equal("Locked", refused.RunStatus);
        Assert.Empty(await h.Db.AttendanceDeclarations.AsNoTracking().ToListAsync());

        // The sheet still reads, and says so in the same words the refusal used.
        var sheet = await h.ReadSheetAsync(2026, 3);
        Assert.True(sheet.Value!.Locked);
        Assert.Equal("RUN-000007", sheet.Value.RunNumber);
    }

    [Fact]
    public async Task A_draft_run_locks_nothing_and_a_reversed_one_stops_locking()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");

        // Draft: the run has been created and somebody is still entering the last of the days.
        var draft = await h.RunAsync(2026, 3, PayrollRunStatus.Draft);
        Assert.True((await h.SaveSheetAsync(2026, 3, HrHarness.Declared(ali.Id, 20m))).Ok);

        draft.Status = PayrollRunStatus.Approved;
        await h.Db.SaveChangesAsync();
        Assert.False((await h.SaveSheetAsync(2026, 3, HrHarness.Declared(ali.Id, 21m))).Ok);

        // Reversed: the row stays, because it holds what was posted and what undid it, but it is
        // no longer the month's live run and must not hold the month closed for ever.
        draft.Status = PayrollRunStatus.Reversed;
        await h.Db.SaveChangesAsync();
        var after = await h.SaveSheetAsync(2026, 3, HrHarness.Declared(ali.Id, 22m));

        Assert.True(after.Ok, after.Message);
        Assert.False((await h.ReadSheetAsync(2026, 3)).Value!.Locked);
    }

    [Fact]
    public async Task One_bad_row_leaves_the_whole_sheet_unwritten()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");
        var omar = await h.HireAsync("Omar Ziane");

        var refused = await h.SaveSheetAsync(2026, 3,
            HrHarness.Declared(ali.Id, 24m),
            // A band the settings do not define. Storing it and ignoring it would be hours
            // somebody worked and nobody paid.
            HrHarness.Declared(omar.Id, 20m, overtime: [new AttendanceOvertimeDto("hs-lune", 4m)]));

        Assert.False(refused.Ok);
        Assert.Equal(HrProblems.OvertimeRuleUnknown, refused.Code);
        Assert.Equal("hs-lune", refused.Key);
        // Not even Ali's good row: a half-saved sheet leaves the clerk guessing what to retype.
        Assert.Empty(await h.Db.AttendanceDeclarations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task More_days_than_the_month_has_is_refused_and_so_is_a_negative_one()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");

        Assert.Equal(HrProblems.DaysExceedMonth,
            (await h.SaveSheetAsync(2026, 2, HrHarness.Declared(ali.Id, 26m, paidLeave: 4m))).Code);
        Assert.Equal(HrProblems.NegativeDays,
            (await h.SaveSheetAsync(2026, 3, HrHarness.Declared(ali.Id, -1m))).Code);
        Assert.Equal(HrProblems.PeriodInvalid,
            (await h.SaveSheetAsync(2026, 13, HrHarness.Declared(ali.Id, 1m))).Code);
    }

    [Fact]
    public async Task An_employee_of_another_branch_or_of_another_month_is_not_on_this_sheet()
    {
        await using var h = await HrHarness.NewAsync();
        var oranais = await h.HireAsync("Karim Saïdi", branchId: HrHarness.Oran);
        var newcomer = await h.HireAsync("Yacine Drif", hiredOn: new DateOnly(2026, 4, 15));

        Assert.Equal(HrProblems.EmployeeNotInBranch,
            (await h.SaveSheetAsync(2026, 3, HrHarness.Declared(oranais.Id, 24m))).Code);
        Assert.Equal(HrProblems.EmployeeOutsidePeriod,
            (await h.SaveSheetAsync(2026, 3, HrHarness.Declared(newcomer.Id, 24m))).Code);

        // And neither of them is on the Algiers sheet for March to begin with.
        var sheet = await h.ReadSheetAsync(2026, 3);
        Assert.Empty(sheet.Value!.Rows);
        Assert.Equal(0, sheet.Value.Total);
    }

    [Fact]
    public async Task A_recorded_employee_is_read_only_on_the_sheet_and_summed_from_their_days()
    {
        await using var h = await HrHarness.NewAsync();
        var clocked = await h.HireAsync("Sofiane Talbi", attendanceMode: "Recorded");

        var refused = await h.SaveSheetAsync(2026, 3, HrHarness.Declared(clocked.Id, 24m));
        Assert.Equal(HrProblems.RecordedRowNotEditable, refused.Code);

        var saved = await h.Attendance.SaveDaysAsync(new UpsertAttendanceDaysRequest(
        [
            new AttendanceDayDto(clocked.Id, new DateOnly(2026, 3, 2), "Present"),
            new AttendanceDayDto(clocked.Id, new DateOnly(2026, 3, 3), "Present", HoursWorked: 4m),
            new AttendanceDayDto(clocked.Id, new DateOnly(2026, 3, 4), "PaidLeave")
        ]), "foreman");
        Assert.True(saved.Ok, saved.Message);
        Assert.Equal(1.5m, saved.Value!.Summary.DaysPresent);

        var row = Assert.Single((await h.ReadSheetAsync(2026, 3)).Value!.Rows);
        Assert.True(row.ReadOnly);
        Assert.Equal("Recorded", row.Mode);
        Assert.Equal(1.5m, row.DaysPresent);
        Assert.Equal(1m, row.PaidLeaveDays);
    }

    [Fact]
    public async Task A_declared_employee_has_no_day_grid_to_write_to()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");

        var refused = await h.Attendance.SaveDaysAsync(new UpsertAttendanceDaysRequest(
            [new AttendanceDayDto(ali.Id, new DateOnly(2026, 3, 2), "Present")]), "foreman");

        Assert.Equal(HrProblems.DeclaredHasNoDayGrid, refused.Code);
        Assert.Empty(await h.Db.AttendanceDays.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_day_sent_with_no_status_is_cleared_back_to_nothing_recorded()
    {
        await using var h = await HrHarness.NewAsync();
        var clocked = await h.HireAsync("Sofiane Talbi", attendanceMode: "Recorded");

        await h.Attendance.SaveDaysAsync(new UpsertAttendanceDaysRequest(
        [
            new AttendanceDayDto(clocked.Id, new DateOnly(2026, 3, 2), "Absent"),
            new AttendanceDayDto(clocked.Id, new DateOnly(2026, 3, 3), "Present")
        ]), "foreman");

        // The mis-click undone. "Nothing was recorded" has to be reachable, or it becomes a
        // permanent absence on somebody's payslip.
        var cleared = await h.Attendance.SaveDaysAsync(new UpsertAttendanceDaysRequest(
            [new AttendanceDayDto(clocked.Id, new DateOnly(2026, 3, 2), null!)]), "foreman");

        Assert.True(cleared.Ok, cleared.Message);
        Assert.Equal(0m, cleared.Value!.Summary.UnpaidAbsenceDays);
        Assert.Equal(1m, cleared.Value.Summary.DaysPresent);
        // The day the request did not mention is untouched.
        Assert.Single(cleared.Value.Days);
    }

    [Fact]
    public async Task Clock_punches_summarise_into_days_and_an_unpaired_in_is_reported_on_the_row()
    {
        await using var h = await HrHarness.NewAsync();
        var clocked = await h.HireAsync("Sofiane Talbi", attendanceMode: "Recorded");

        await Punch(h, clocked.Id, new DateTime(2026, 3, 2, 6, 0, 0, DateTimeKind.Utc), "In");
        await Punch(h, clocked.Id, new DateTime(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc), "Out");
        var orphan = await Punch(h, clocked.Id, new DateTime(2026, 3, 3, 6, 0, 0, DateTimeKind.Utc), "In");

        Assert.True(orphan.Ok, orphan.Message);

        var row = Assert.Single((await h.ReadSheetAsync(2026, 3)).Value!.Rows);
        Assert.Equal(1m, row.DaysPresent);
        Assert.Equal("Recorded", row.Source);
        // Reported, never guessed: a missing clock-out is a question for the foreman.
        Assert.Equal(1, row.UnpairedClockIns);

        var grid = await h.Attendance.ReadDaysAsync(clocked.Id, 2026, 3);
        Assert.Single(grid.Value!.UnpairedClockInsUtc);
        // Nobody ticked a day, so the grid has none — the punches are the raw material.
        Assert.Empty(grid.Value.Days);
    }

    [Fact]
    public async Task A_punch_out_of_order_is_recorded_and_warned_never_refused()
    {
        await using var h = await HrHarness.NewAsync();
        var clocked = await h.HireAsync("Sofiane Talbi", attendanceMode: "Recorded");

        var first = await Punch(h, clocked.Id, new DateTime(2026, 3, 2, 6, 0, 0, DateTimeKind.Utc), "In");
        var again = await Punch(h, clocked.Id, new DateTime(2026, 3, 2, 7, 0, 0, DateTimeKind.Utc), "In");

        Assert.Null(first.Warning);
        Assert.True(again.Ok);
        Assert.Equal(HrProblems.ClockRepeatedIn, again.Warning);
        Assert.Equal(HrProblems.ClockRepeatedIn, again.Value!.Warning);
        // Two punches stored. The warning is a sentence beside the fact, not a reason to lose it.
        Assert.Equal(2, await h.Db.ClockEvents.CountAsync());
    }

    [Fact]
    public async Task A_badge_number_is_enough_to_clock_and_a_locked_month_still_refuses()
    {
        await using var h = await HrHarness.NewAsync();
        var clocked = await h.HireAsync("Sofiane Talbi", attendanceMode: "Recorded");

        var byNumber = await h.Attendance.ClockAsync(
            new ClockPunchRequest(EmployeeNumber: clocked.Number, Kind: "In",
                AtUtc: new DateTime(2026, 3, 2, 6, 0, 0, DateTimeKind.Utc), Source: "badge"), "gate");

        Assert.True(byNumber.Ok, byNumber.Message);
        Assert.Equal(clocked.Id, byNumber.Value!.Person.EmployeeId);
        Assert.Equal("Sofiane Talbi", byNumber.Value.Person.EmployeeName);

        await h.RunAsync(2026, 3, PayrollRunStatus.Paid, number: "RUN-000009");
        var refused = await h.Attendance.ClockAsync(
            new ClockPunchRequest(EmployeeId: clocked.Id, Kind: "Out",
                AtUtc: new DateTime(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc)), "gate");

        Assert.Equal(HrProblems.PeriodLocked, refused.Code);
        Assert.Equal("RUN-000009", refused.RunNumber);
        Assert.Equal(1, await h.Db.ClockEvents.CountAsync());
    }

    [Fact]
    public async Task A_month_nobody_entered_reads_as_none_rather_than_as_a_month_of_zero_days()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");

        var row = Assert.Single((await h.ReadSheetAsync(2026, 3)).Value!.Rows);

        Assert.Equal("None", row.Source);
        Assert.Equal(0m, row.DaysPresent);
        Assert.Null(row.UpdatedAtUtc);

        // Which is the difference the engine acts on: paid in full with a warning, never held
        // (project memory, 2026-09-24).
        var summaries = await h.Attendance.SummariesAsync(
            await h.Employees.PayableAsync(HrHarness.Algiers, new AttendancePeriod(2026, 3)),
            new AttendancePeriod(2026, 3));
        Assert.Equal(Engine.AttendanceSource.None, summaries[ali.Id].Source);
    }

    [Fact]
    public async Task The_run_is_fed_one_shape_whichever_mode_captured_the_month()
    {
        await using var h = await HrHarness.NewAsync();
        var declared = await h.HireAsync("Ali Benali");
        var clocked = await h.HireAsync("Sofiane Talbi", attendanceMode: "Recorded");

        await h.SaveSheetAsync(2026, 3, HrHarness.Declared(declared.Id, 3m, paidLeave: 1m,
            overtime: [new AttendanceOvertimeDto("hs-jour", 4m)]));
        await h.Attendance.SaveDaysAsync(new UpsertAttendanceDaysRequest(
        [
            new AttendanceDayDto(clocked.Id, new DateOnly(2026, 3, 2), "Present",
                Overtime: [new AttendanceOvertimeDto("hs-jour", 4m)]),
            new AttendanceDayDto(clocked.Id, new DateOnly(2026, 3, 3), "Present"),
            new AttendanceDayDto(clocked.Id, new DateOnly(2026, 3, 4), "Present"),
            new AttendanceDayDto(clocked.Id, new DateOnly(2026, 3, 5), "PaidLeave")
        ]), "foreman");

        var period = new AttendancePeriod(2026, 3);
        var summaries = await h.Attendance.SummariesAsync(
            await h.Employees.PayableAsync(HrHarness.Algiers, period), period);

        // Two capture paths, one shape. This is the whole point of the summariser: the engine
        // has one rule per figure rather than one per path.
        Assert.Equal(summaries[declared.Id].DaysPresent, summaries[clocked.Id].DaysPresent);
        Assert.Equal(summaries[declared.Id].PaidLeaveDays, summaries[clocked.Id].PaidLeaveDays);
        Assert.Equal(summaries[declared.Id].OvertimeHoursByRule, summaries[clocked.Id].OvertimeHoursByRule);
        Assert.Equal(4m, summaries[declared.Id].PaidDays);
    }

    [Fact]
    public async Task The_sheet_carries_the_company_default_and_the_overtime_bands_the_settings_define()
    {
        await using var h = await HrHarness.NewAsync();
        await h.HireAsync("Ali Benali");

        var sheet = (await h.ReadSheetAsync(2026, 3)).Value!;

        Assert.Equal("Declared", sheet.CompanyDefaultMode);
        // Columns come from the settings, never from a list in the code: which bands exist is the
        // company's configuration, and the labels are the admin's own words.
        Assert.Equal(["hs-jour", "hs-nuit", "hs-repos", "hs-ferie"], sheet.OvertimeRules.Select(r => r.Key));
        Assert.Equal("ساعات إضافية نهارية", sheet.OvertimeRules[0].Labels["ar"]);
    }

    [Fact]
    public async Task The_clock_log_reads_a_month_with_names_beside_the_punches_and_the_unpaired_named()
    {
        await using var h = await HrHarness.NewAsync();
        var clocked = await h.HireAsync("Sofiane Talbi", attendanceMode: "Recorded");
        var oranais = await h.HireAsync("Karim Saïdi", branchId: HrHarness.Oran, attendanceMode: "Recorded");

        await Punch(h, clocked.Id, new DateTime(2026, 3, 2, 6, 0, 0, DateTimeKind.Utc), "In");
        await Punch(h, clocked.Id, new DateTime(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc), "Out");
        await Punch(h, clocked.Id, new DateTime(2026, 3, 3, 6, 0, 0, DateTimeKind.Utc), "In");
        await Punch(h, oranais.Id, new DateTime(2026, 3, 2, 7, 0, 0, DateTimeKind.Utc), "In");
        // April, and therefore not in March's log.
        await Punch(h, clocked.Id, new DateTime(2026, 4, 1, 6, 0, 0, DateTimeKind.Utc), "In");

        var log = await h.Attendance.ReadClockAsync(null, HrHarness.Algiers, 2026, 3, 100);

        Assert.True(log.Ok, log.Message);
        // Algiers only, March only — the branch is narrowed in the query, so another branch's
        // punches cannot fill the page and leave this one's log short.
        Assert.Equal(3, log.Value!.Events.Count);
        Assert.All(log.Value.Events, e => Assert.Equal(clocked.Id, e.EmployeeId));
        Assert.Equal("Sofiane Talbi", Assert.Single(log.Value.People).EmployeeName);
        // The punch nobody closed, named rather than mended.
        Assert.Equal(new DateTime(2026, 3, 3, 6, 0, 0, DateTimeKind.Utc), Assert.Single(log.Value.Unpaired).AtUtc);
    }

    [Fact]
    public async Task The_clock_names_the_person_behind_a_number_before_anything_is_recorded()
    {
        await using var h = await HrHarness.NewAsync();
        var clocked = await h.HireAsync("Sofiane Talbi", attendanceMode: "Recorded");
        var march = new DateTime(2026, 3, 2, 6, 0, 0, DateTimeKind.Utc);

        var found = await h.Attendance.FindClockPersonAsync("  " + clocked.Number + " ", march);
        Assert.True(found.Ok, found.Message);
        Assert.Equal(clocked.Id, found.Value!.EmployeeId);
        Assert.Equal("Sofiane Talbi", found.Value.EmployeeName);

        // An unknown or empty number is refused here, before the question is asked — with the
        // code the clock itself would answer.
        Assert.Equal(HrProblems.EmployeeNotFound, (await h.Attendance.FindClockPersonAsync("NOPE-1", march)).Code);
        Assert.Equal(HrProblems.EmployeeRequired, (await h.Attendance.FindClockPersonAsync("  ", march)).Code);

        // Looking somebody up records nothing.
        Assert.Equal(0, await h.Db.ClockEvents.CountAsync());
    }

    private static Task<HrResult<ClockedDto>> Punch(HrHarness h, Guid employeeId, DateTime atUtc, string kind) =>
        h.Attendance.ClockAsync(new ClockPunchRequest(EmployeeId: employeeId, Kind: kind, AtUtc: atUtc), "gate");
}
