using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Payroll;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Payroll;

/// <summary>
/// The social fund and the tax office actually being paid — docs/07 §5.9 and the §5.14 risk.
///
/// <para>
/// Every approved run credits 431, 442 and 447 and nothing ever debits them. Without this
/// document the balance sheet shows a company that has never in its life paid a contribution, and
/// the figure grows every month. So what these tests care about is that the liability really comes
/// down, that only a payroll liability can be settled this way, and that a transfer covering
/// several months is not forced to invent a period.
/// </para>
/// </summary>
public class StatutoryPaymentTests
{
    /// <summary>
    /// A month's accrual with something withheld, so 431 and 442 have a balance to settle. Built
    /// through <see cref="PayrollPosting.Accrual"/> rather than by hand: the figures under test
    /// are the ones a real approval raises.
    /// </summary>
    private static async Task AccrueAsync(WageMoneyHarness harness, Guid branchId)
    {
        var accrual = new PayrollAccrual(
            Gross: 100_000m, WageCostDeductions: 0m, OtherDeductions: 0m,
            SocialEmployee: 9_000m, SocialEmployer: 26_000m, IncomeTax: 11_000m,
            EmployerLevies: 0m, AdvancesRecovered: 0m,
            Net: 100_000m - 9_000m - 11_000m);

        var draft = PayrollPosting.Accrual(
            Guid.NewGuid(), branchId, "RUN-000009", accrual, "admin",
            new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc));
        await new LedgerPoster(harness.Db).PostAsync(draft!);
        await harness.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_statutory_payment_clears_the_liability_it_names_and_credits_the_bank()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        await AccrueAsync(harness, WageMoneyHarness.Algiers);

        // Both halves of the contribution sit on 431: the employee's 9 000 withheld and the
        // employer's own 26 000.
        var before = await harness.Money.LiabilityAsync(
            LedgerAccounts.SocialSecurityPayable, WageMoneyHarness.Algiers);
        Assert.Equal(35_000m, before.Outstanding);

        var result = await harness.Money.PayStatutoryAsync(new RecordStatutoryPaymentRequest(
            WageMoneyHarness.Algiers, LedgerAccounts.SocialSecurityPayable, 35_000m,
            nameof(PayoutMethod.Bank), PeriodYear: 2026, PeriodMonth: 9,
            Reference: "VIR-2026-09"), "comptable");

        Assert.True(result.Ok);
        Assert.Null(result.Warning);

        var after = await harness.Money.LiabilityAsync(
            LedgerAccounts.SocialSecurityPayable, WageMoneyHarness.Algiers);
        Assert.Equal(35_000m, after.Accrued);
        Assert.Equal(35_000m, after.Paid);
        Assert.Equal(0m, after.Outstanding);

        Assert.Equal(-35_000m, await harness.AccountAsync(LedgerAccounts.Bank));
        // A transfer moves no drawer.
        Assert.Empty(await harness.MovementsAsync(LedgerSources.PayrollStatutoryPayment));

        var dto = Assert.Single(await harness.Money.ListStatutoryPaymentsAsync(null, null, null, 50));
        Assert.Equal("431", dto.AccountCode);
        Assert.Equal(2026, dto.PeriodYear);
        Assert.Equal("Alger-Centre", dto.BranchName);
        Assert.NotEqual("", dto.AccountName);
    }

    [Fact]
    public async Task Paying_the_fund_in_cash_leaves_the_box_as_a_disbursement()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        await AccrueAsync(harness, WageMoneyHarness.Algiers);

        var result = await harness.Money.PayStatutoryAsync(new RecordStatutoryPaymentRequest(
            WageMoneyHarness.Algiers, LedgerAccounts.IncomeTaxWithheld, 11_000m,
            nameof(PayoutMethod.Cash), CashBoxId: harness.AlgiersBox.Id), "comptable");

        Assert.True(result.Ok);
        Assert.Equal(4_989_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));

        var movement = Assert.Single(await harness.MovementsAsync(LedgerSources.PayrollStatutoryPayment));
        Assert.Equal(CashMovementKind.Disbursement, movement.Kind);
        Assert.Equal(0m, (await harness.Money.LiabilityAsync(
            LedgerAccounts.IncomeTaxWithheld, WageMoneyHarness.Algiers)).Outstanding);
    }

    [Fact]
    public async Task A_payment_against_an_account_that_is_not_a_payroll_liability_is_refused()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        await AccrueAsync(harness, WageMoneyHarness.Algiers);

        // 421 is what the staff are owed. A transfer posted there would read as wages paid and
        // leave the fund still owed — the one mistake this check exists for.
        var wages = await harness.Money.PayStatutoryAsync(new RecordStatutoryPaymentRequest(
            WageMoneyHarness.Algiers, LedgerAccounts.WagesPayable, 10_000m, nameof(PayoutMethod.Bank)),
            "comptable");
        var nonsense = await harness.Money.PayStatutoryAsync(new RecordStatutoryPaymentRequest(
            WageMoneyHarness.Algiers, "999", 10_000m, nameof(PayoutMethod.Bank)), "comptable");

        Assert.Equal(PayrollFailure.Invalid, wages.Failure);
        Assert.Contains("431", wages.Message!);
        Assert.Equal(PayrollFailure.Invalid, nonsense.Failure);
        Assert.Empty(await harness.Db.PayrollStatutoryPayments.ToListAsync());
    }

    [Fact]
    public async Task A_transfer_covering_several_months_carries_no_period()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        await AccrueAsync(harness, WageMoneyHarness.Algiers);
        await AccrueAsync(harness, WageMoneyHarness.Algiers);

        // One arrears transfer for two months. Forcing a period onto it would have the accountant
        // inventing one, which is why the column is nullable (§5.4).
        var result = await harness.Money.PayStatutoryAsync(new RecordStatutoryPaymentRequest(
            WageMoneyHarness.Algiers, LedgerAccounts.SocialSecurityPayable, 70_000m,
            nameof(PayoutMethod.Bank), Note: "Régularisation août-septembre"), "comptable");

        Assert.True(result.Ok);
        Assert.Null(result.Value!.PeriodYear);
        Assert.Null(result.Value.PeriodMonth);
        Assert.Equal(0m, (await harness.Money.LiabilityAsync(
            LedgerAccounts.SocialSecurityPayable, WageMoneyHarness.Algiers)).Outstanding);
    }

    [Fact]
    public async Task Half_a_period_is_refused_rather_than_stored()
    {
        await using var harness = await WageMoneyHarness.NewAsync();

        var yearOnly = await harness.Money.PayStatutoryAsync(new RecordStatutoryPaymentRequest(
            WageMoneyHarness.Algiers, LedgerAccounts.SocialSecurityPayable, 1_000m,
            nameof(PayoutMethod.Bank), PeriodYear: 2026), "comptable");
        var thirteenth = await harness.Money.PayStatutoryAsync(new RecordStatutoryPaymentRequest(
            WageMoneyHarness.Algiers, LedgerAccounts.SocialSecurityPayable, 1_000m,
            nameof(PayoutMethod.Bank), PeriodYear: 2026, PeriodMonth: 13), "comptable");

        // A year with no month is a period nobody can file against.
        Assert.Equal(PayrollFailure.Invalid, yearOnly.Failure);
        Assert.Equal(PayrollFailure.Invalid, thirteenth.Failure);
    }

    [Fact]
    public async Task Paying_more_than_the_liability_warns_and_does_not_refuse()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        await AccrueAsync(harness, WageMoneyHarness.Algiers);

        // A round figure, an arrears settlement, or a month whose run is not approved yet — all
        // ordinary, none of them a reason to refuse the accountant's own record of a transfer.
        var result = await harness.Money.PayStatutoryAsync(new RecordStatutoryPaymentRequest(
            WageMoneyHarness.Algiers, LedgerAccounts.SocialSecurityPayable, 40_000m,
            nameof(PayoutMethod.Bank)), "comptable");

        Assert.True(result.Ok);
        Assert.NotNull(result.Warning);
        Assert.Contains("431", result.Warning!);
        // Overpaid, and the ledger says so rather than hiding it.
        Assert.Equal(-5_000m, (await harness.Money.LiabilityAsync(
            LedgerAccounts.SocialSecurityPayable, WageMoneyHarness.Algiers)).Outstanding);
    }

    [Fact]
    public async Task The_three_liabilities_are_read_together_for_the_accountants_screen()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        await AccrueAsync(harness, WageMoneyHarness.Algiers);

        var rows = await harness.Money.LiabilitiesAsync(WageMoneyHarness.Algiers);

        Assert.Equal(3, rows.Count);
        Assert.Equal(35_000m, rows.Single(r => r.AccountCode == "431").Outstanding);
        Assert.Equal(11_000m, rows.Single(r => r.AccountCode == "442").Outstanding);
        // 447 carries nothing while no employer levy is configured, and says zero rather than
        // being absent — a row that vanishes reads as a screen that failed to load.
        Assert.Equal(0m, rows.Single(r => r.AccountCode == "447").Outstanding);
    }
}
