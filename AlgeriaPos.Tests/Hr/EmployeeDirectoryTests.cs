using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Hr;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Hr;

/// <summary>
/// The employee record: numbering, leaving, overrides — and what the payroll run is allowed to
/// see when it asks who this branch pays.
/// </summary>
public class EmployeeDirectoryTests
{
    [Fact]
    public async Task An_employee_is_numbered_by_the_server_and_two_of_them_never_share_one()
    {
        // Emp- + the branch's code + the company's YYMM + the month's sequence (owner, 2026-10-05);
        // EmployeeNumberTests holds the rest of the rule. Alger-Centre is location 361 here.
        await using var h = await HrHarness.NewAsync(new Hosting.TestClock(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero)));

        var first = await h.HireAsync("Ali Benali");
        var second = await h.HireAsync("Omar Ziane");

        Assert.Equal("Emp-3612610001", first.Number);
        Assert.Equal("Emp-3612610002", second.Number);
        Assert.Equal(2, await h.Db.Employees.CountAsync());
        // The branch travels back by name, because a list that shows a Guid is a list nobody reads.
        Assert.Equal("Alger-Centre", first.BranchName);
    }

    [Fact]
    public async Task Leaving_is_a_date_and_never_a_delete()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");

        var left = await h.Employees.SetLeftAsync(ali.Id, new EmployeeLeaveRequest(new DateOnly(2026, 3, 20), "fin de contrat"));

        Assert.True(left.Ok, left.Message);
        Assert.Equal(new DateOnly(2026, 3, 20), left.Value!.LeftOn);
        // Gone is also not waiting to be picked up by next month's run.
        Assert.False(left.Value.IsActive);
        // And the row is still there, because payslips, advances and postings all name it.
        Assert.Equal(1, await h.Db.Employees.CountAsync());

        var period = new AttendancePeriod(2026, 3);
        // Paid for the month they left in...
        Assert.Contains(await h.Employees.PayableAsync(HrHarness.Algiers, period), e => e.Id == ali.Id);
        // ...and not for the one after.
        Assert.Empty(await h.Employees.PayableAsync(HrHarness.Algiers, new AttendancePeriod(2026, 4)));
    }

    [Fact]
    public async Task A_leaving_date_before_the_hire_date_is_refused()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali", hiredOn: new DateOnly(2026, 1, 10));

        var refused = await h.Employees.SetLeftAsync(ali.Id, new EmployeeLeaveRequest(new DateOnly(2025, 12, 31)));

        Assert.Equal(HrProblems.LeftBeforeHired, refused.Code);
        Assert.Null((await h.Employees.ReadAsync(ali.Id))!.LeftOn);
    }

    [Fact]
    public async Task An_allowance_override_naming_an_allowance_nobody_defined_is_refused()
    {
        await using var h = await HrHarness.NewAsync();

        var refused = await h.Employees.CreateAsync(new UpsertEmployeeRequest(
            "Ali Benali", HrHarness.Algiers, "Monthly", 52_000m, "Cash", new DateOnly(2020, 1, 1),
            Allowances: [new EmployeeAllowanceDto("prime-fantome", 5_000m)]));

        Assert.Equal(HrProblems.AllowanceUnknown, refused.Code);
        Assert.Equal("prime-fantome", refused.Key);
        // Refused rather than dropped: an override saved against a key nobody defined is an
        // allowance the admin believes they set and no payslip will ever show.
        Assert.Empty(await h.Db.Employees.ToListAsync());
    }

    [Fact]
    public async Task An_allowance_override_is_the_whole_set_and_an_absent_list_leaves_it_alone()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali",
            allowances: [new EmployeeAllowanceDto("transport", 4_000m), new EmployeeAllowanceDto("panier", 3_000m)]);

        Assert.Equal(2, ali.Allowances!.Count);

        // A form that does not edit allowances sends none, and none is not "remove them".
        var untouched = await h.Employees.UpdateAsync(ali.Id, new UpsertEmployeeRequest(
            "Ali Benali", HrHarness.Algiers, "Monthly", 55_000m, "Cash", new DateOnly(2020, 1, 1)));
        Assert.Equal(2, untouched.Value!.Allowances!.Count);

        // A list is the set: what it leaves out is removed.
        var replaced = await h.Employees.UpdateAsync(ali.Id, new UpsertEmployeeRequest(
            "Ali Benali", HrHarness.Algiers, "Monthly", 55_000m, "Cash", new DateOnly(2020, 1, 1),
            Allowances: [new EmployeeAllowanceDto("transport", 6_000m)]));

        var only = Assert.Single(replaced.Value!.Allowances!);
        Assert.Equal("transport", only.AllowanceKey);
        Assert.Equal(6_000m, only.Value);
        Assert.Single(await h.Db.EmployeeAllowances.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_capture_mode_in_force_is_resolved_on_the_server()
    {
        await using var h = await HrHarness.NewAsync();

        var follower = await h.HireAsync("Ali Benali");
        var clocked = await h.HireAsync("Sofiane Talbi", attendanceMode: "Recorded");

        // Null override, company default — the screen is told the answer rather than left to
        // work it out and get it wrong on one screen out of three.
        Assert.Null(follower.AttendanceModeOverride);
        Assert.Equal("Declared", follower.AttendanceMode);
        Assert.Equal("Recorded", clocked.AttendanceModeOverride);
        Assert.Equal("Recorded", clocked.AttendanceMode);
    }

    [Fact]
    public async Task What_is_still_owed_against_advances_is_on_the_employee()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");
        await h.AdvanceAsync(ali.Id, 20_000m, recovered: 5_000m);
        await h.AdvanceAsync(ali.Id, 10_000m);

        var read = await h.Employees.ReadAsync(ali.Id);

        // Summed in memory: SQLite stores decimal as TEXT, so a server-side Sum adds strings.
        Assert.Equal(25_000m, read!.OpenAdvances);
    }

    [Fact]
    public async Task A_figure_that_would_pay_somebody_backwards_is_refused()
    {
        await using var h = await HrHarness.NewAsync();

        Assert.Equal(HrProblems.BaseRateNegative, (await h.Employees.CreateAsync(new UpsertEmployeeRequest(
            "Ali Benali", HrHarness.Algiers, "Monthly", -1m, "Cash", new DateOnly(2020, 1, 1)))).Code);
        Assert.Equal(HrProblems.NameRequired, (await h.Employees.CreateAsync(new UpsertEmployeeRequest(
            "  ", HrHarness.Algiers, "Monthly", 1m, "Cash", new DateOnly(2020, 1, 1)))).Code);
        Assert.Equal(HrProblems.PayBasisUnknown, (await h.Employees.CreateAsync(new UpsertEmployeeRequest(
            "Ali Benali", HrHarness.Algiers, "Weekly", 1m, "Cash", new DateOnly(2020, 1, 1)))).Code);
        Assert.Equal(HrProblems.BranchUnknown, (await h.Employees.CreateAsync(new UpsertEmployeeRequest(
            "Ali Benali", Guid.NewGuid(), "Monthly", 1m, "Cash", new DateOnly(2020, 1, 1)))).Code);
    }

    [Fact]
    public async Task The_list_filters_by_branch_by_state_and_by_a_typed_term()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");
        await h.HireAsync("Omar Ziane");
        var oranais = await h.HireAsync("Karim Saïdi", branchId: HrHarness.Oran);
        await h.Employees.SetLeftAsync(ali.Id, new EmployeeLeaveRequest(new DateOnly(2026, 3, 20)));

        var stillHere = await h.Employees.ListAsync(HrHarness.Algiers, active: true, null, 0, 50);
        Assert.Equal("Omar Ziane", Assert.Single(stillHere.Rows).FullName);

        var gone = await h.Employees.ListAsync(null, active: false, null, 0, 50);
        Assert.Equal("Ali Benali", Assert.Single(gone.Rows).FullName);

        var searched = await h.Employees.ListAsync(null, null, "Saïdi", 0, 50);
        Assert.Equal(oranais.Id, Assert.Single(searched.Rows).Id);

        var paged = await h.Employees.ListAsync(null, null, null, 1, 1);
        Assert.Equal(3, paged.Total);
        Assert.Single(paged.Rows);
    }

    [Fact]
    public async Task An_employee_moved_mid_month_is_paid_by_the_branch_holding_them_now()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await h.HireAsync("Ali Benali");

        var moved = await h.Employees.UpdateAsync(ali.Id, new UpsertEmployeeRequest(
            "Ali Benali", HrHarness.Oran, "Monthly", 52_000m, "Cash", new DateOnly(2020, 1, 1)));

        Assert.True(moved.Ok, moved.Message);
        Assert.Equal("Oran", moved.Value!.BranchName);

        // Whole-month, by the branch that holds them at compute time (project memory,
        // 2026-09-24). There is no dated branch history, and this is where that is visible.
        var period = new AttendancePeriod(2026, 3);
        Assert.Empty(await h.Employees.PayableAsync(HrHarness.Algiers, period));
        Assert.Single(await h.Employees.PayableAsync(HrHarness.Oran, period));
    }
}
