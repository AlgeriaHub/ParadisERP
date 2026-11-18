using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Hr;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Hr;

/// <summary>
/// There are no daily workers on payroll (owner, 2026-10-05, confirming answers 41/47 of 2026-10-02):
/// a day worker's wage is a payment voucher under Daily labour. Nobody is hired onto Daily or moved
/// onto it; a legacy Daily row — production may hold some — keeps its basis until someone moves it to
/// Monthly by hand, and keeps computing exactly as before (the engine's own tests are untouched).
/// </summary>
public class DailyPayBasisRetiredTests
{
    private static UpsertEmployeeRequest Request(string name, string? basis, decimal rate = 52_000m) =>
        new(name, HrHarness.Algiers, basis!, rate, "Cash", new DateOnly(2020, 1, 1));

    /// <summary>A Daily employee from before the rule, written as the old screens left it.</summary>
    private static async Task<Employee> LegacyDailyAsync(HrHarness h, string name)
    {
        var employee = new Employee
        {
            Number = "EMP-000001", FullName = name, BranchId = HrHarness.Algiers,
            HiredOn = new DateOnly(2020, 1, 1), PayBasis = PayBasis.Daily, BaseRate = 1_500m,
        };
        h.Db.Employees.Add(employee);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return employee;
    }

    private static async Task<PayBasis> StoredBasisAsync(HrHarness h, Guid id) =>
        (await h.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == id)).PayBasis;

    [Theory]
    [InlineData("Daily")]
    [InlineData("daily")]
    // Enum.TryParse reads the number as the name; the rule does not care how Daily was spelled.
    [InlineData("0")]
    public async Task Nobody_is_hired_onto_daily_and_the_refusal_says_where_a_day_worker_is_paid(string basis)
    {
        await using var h = await HrHarness.NewAsync();

        var refused = await h.Employees.CreateAsync(Request("Karim Ait", basis, 1_500m));

        Assert.False(refused.Ok);
        Assert.Equal(HrFailure.Invalid, refused.Failure);
        Assert.Equal(HrProblems.PayBasisDailyRetired, refused.Code);
        Assert.Equal(EmployeeDirectory.DailyRetiredMessage, refused.Message);
        Assert.Equal(
            "There are no daily workers on payroll: pay a day worker with a payment voucher under Daily labour.",
            refused.Message);
        Assert.Equal(0, await h.Db.Employees.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_new_employee_with_no_basis_is_monthly(string? basis)
    {
        await using var h = await HrHarness.NewAsync();

        var created = await h.Employees.CreateAsync(Request("Omar Ziane", basis));

        Assert.True(created.Ok, created.Message);
        Assert.Equal("Monthly", created.Value!.PayBasis);
        Assert.Equal(PayBasis.Monthly, await StoredBasisAsync(h, created.Value.Id));
    }

    [Fact]
    public async Task A_monthly_employee_cannot_be_moved_onto_daily()
    {
        await using var h = await HrHarness.NewAsync();
        var omar = await h.HireAsync("Omar Ziane");

        var refused = await h.Employees.UpdateAsync(omar.Id, Request("Omar Ziane", "Daily", 1_500m));

        Assert.Equal(HrProblems.PayBasisDailyRetired, refused.Code);
        // Refused whole: neither the basis nor the rate that came with it was written.
        var stored = await h.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == omar.Id);
        Assert.Equal(PayBasis.Monthly, stored.PayBasis);
        Assert.Equal(52_000m, stored.BaseRate);
    }

    [Fact]
    public async Task A_legacy_daily_employee_edited_for_something_else_stays_daily()
    {
        await using var h = await HrHarness.NewAsync();
        var karim = await LegacyDailyAsync(h, "Karim Ait");

        // The form sends the basis it read back: Daily, unchanged.
        var renamed = await h.Employees.UpdateAsync(karim.Id, Request("Karim Ait Ali", "Daily", 1_500m));
        Assert.True(renamed.Ok, renamed.Message);
        Assert.Equal("Karim Ait Ali", renamed.Value!.FullName);
        Assert.Equal("Daily", renamed.Value.PayBasis);

        // An edit that does not name the basis leaves it as it is — never converts it to Monthly.
        var unnamed = await h.Employees.UpdateAsync(karim.Id, Request("Karim Ait", "", 1_600m));
        Assert.True(unnamed.Ok, unnamed.Message);
        Assert.Equal("Daily", unnamed.Value!.PayBasis);
        Assert.Equal(PayBasis.Daily, await StoredBasisAsync(h, karim.Id));
    }

    [Fact]
    public async Task A_legacy_daily_employee_may_move_to_monthly_and_then_cannot_go_back()
    {
        await using var h = await HrHarness.NewAsync();
        var karim = await LegacyDailyAsync(h, "Karim Ait");

        var monthly = await h.Employees.UpdateAsync(karim.Id, Request("Karim Ait", "Monthly", 45_000m));
        Assert.True(monthly.Ok, monthly.Message);
        Assert.Equal("Monthly", monthly.Value!.PayBasis);
        Assert.Equal(PayBasis.Monthly, await StoredBasisAsync(h, karim.Id));

        // Monthly now, so Daily is closed to them like to everyone else.
        var back = await h.Employees.UpdateAsync(karim.Id, Request("Karim Ait", "Daily", 1_500m));
        Assert.Equal(HrProblems.PayBasisDailyRetired, back.Code);
        Assert.Equal(PayBasis.Monthly, await StoredBasisAsync(h, karim.Id));
    }

    [Fact]
    public async Task A_basis_that_is_not_one_is_still_unknown_rather_than_retired()
    {
        await using var h = await HrHarness.NewAsync();

        Assert.Equal(HrProblems.PayBasisUnknown, (await h.Employees.CreateAsync(Request("Ali Benali", "Weekly"))).Code);
        // A number nobody defined is not a basis either.
        Assert.Equal(HrProblems.PayBasisUnknown, (await h.Employees.CreateAsync(Request("Ali Benali", "7"))).Code);
    }
}
