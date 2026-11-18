using AlgeriaPos.Domain;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Payroll;

/// <summary>
/// Statutory exemption through a real run: new and draft runs omit CNAS/IRG, while completed
/// documents remain the record of the calculation that was approved.
/// </summary>
public class NetPayPayrollRunTests
{
    [Fact]
    public async Task An_approved_run_keeps_its_contributions_after_the_new_payslip_policy()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);
        var run = await harness.OpenAsync();
        var historical = await harness.Db.PayrollRuns.SingleAsync(r => r.Id == run.Id);
        historical.CalculateStatutoryDeductions = true;
        await harness.Db.SaveChangesAsync();
        await harness.ComputeAsync(run.Id);
        await harness.ApproveAsync(run.Id);
        Assert.Equal(2_000m, (await harness.ReadAsync(run.Id)).SocialEmployee);

        // An approved month is historical and cannot be recomputed into the new policy.
        var after = await harness.ReadAsync(run.Id);
        Assert.Equal(2_000m, after.SocialEmployee);
        Assert.Equal(18_000m, after.Net);
        Assert.False((await harness.Payroll.ComputeAsync(run.Id, "admin")).Ok);
        Assert.Equal(2_000m, (await harness.ReadAsync(run.Id)).SocialEmployee);
    }

    [Fact]
    public async Task A_new_run_posts_wages_without_cnas_or_irg_but_keeps_other_deductions()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Karim T.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);

        var run = await harness.OpenAsync(calculateStatutoryDeductions: false);
        await harness.ComputeAsync(run.Id);
        var computed = await harness.ReadAsync(run.Id);
        Assert.Equal(0m, computed.SocialEmployee);
        Assert.Equal(20_000m, computed.Net);

        await harness.ApproveAsync(run.Id);
        var entry = Assert.Single(await harness.EntriesAsync());
        Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));
        var posted = await harness.PostedAsync();
        Assert.Equal(20_000m, posted[LedgerAccounts.Wages]);
        Assert.Equal(-20_000m, posted[LedgerAccounts.WagesPayable]);
        Assert.Equal(0m, posted.GetValueOrDefault(LedgerAccounts.SocialSecurityPayable));
        Assert.Equal(0m, posted.GetValueOrDefault(LedgerAccounts.EmployerSocialContributions));
    }

    [Fact]
    public async Task Unlocking_a_legacy_locked_run_makes_its_draft_statutory_exempt_on_recompute()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Meriem B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);
        var run = await harness.OpenAsync();
        var legacy = await harness.Db.PayrollRuns.SingleAsync(r => r.Id == run.Id);
        legacy.CalculateStatutoryDeductions = true;
        await harness.Db.SaveChangesAsync();

        await harness.ComputeAsync(run.Id);
        Assert.Equal(2_000m, (await harness.ReadAsync(run.Id)).SocialEmployee);

        var unlocked = await harness.Payroll.UnlockAsync(run.Id, "admin");
        Assert.True(unlocked.Ok, unlocked.Message);
        Assert.False((await harness.ReadAsync(run.Id)).CalculateStatutoryDeductions);

        await harness.ComputeAsync(run.Id);
        var recomputed = await harness.ReadAsync(run.Id);
        Assert.Equal(0m, recomputed.SocialEmployee);
        Assert.Equal(0m, recomputed.IncomeTax);
    }
}
