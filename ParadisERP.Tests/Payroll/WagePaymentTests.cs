using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Payroll;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Payroll;

/// <summary>
/// Payday — docs/07 §5.10, the first money-out document in the product.
///
/// <para>
/// Two failures sit behind these tests. If 421 is cleared at approval, the books say the staff
/// were paid while the cash is still in the safe. If nothing is written when the cash goes out,
/// the box is short with nothing to explain it. So every assertion here is about the pair moving
/// together, and about the one thing that is allowed to refuse: a run nobody has approved.
/// </para>
/// </summary>
public class WagePaymentTests
{
    [Fact]
    public async Task Paying_in_cash_leaves_the_branch_box_and_clears_421_for_the_paid_lines_only()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var first = await harness.HireAsync("Ben Ali Mohamed");
        var second = await harness.HireAsync("Karim Belkacem");
        var run = await harness.ApprovedRunAsync((first, 48_000m), (second, 52_000m));

        // The approval put the whole month on 421; this pays one of the two.
        Assert.Equal(-100_000m, await harness.AccountAsync(LedgerAccounts.WagesPayable));

        var line = (await harness.LinesAsync(run)).Single(l => l.EmployeeId == first.Id);
        var result = await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id,
            Lines: [new WagePaymentLineRequest(line.Id, 48_000m)]), "caissier");

        Assert.True(result.Ok);
        Assert.Equal("WP-000001", result.Value!.Number);
        Assert.Equal(48_000m, result.Value.Amount);

        Assert.Equal(4_952_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));
        // 52 000 of the month is still owed to the person who has not been paid.
        Assert.Equal(-52_000m, await harness.AccountAsync(LedgerAccounts.WagesPayable));
        Assert.Equal(-48_000m, await harness.AccountAsync(LedgerAccounts.Cash));

        var lines = await harness.LinesAsync(run);
        Assert.Equal(48_000m, lines.Single(l => l.EmployeeId == first.Id).PaidAmount);
        Assert.Equal(0m, lines.Single(l => l.EmployeeId == second.Id).PaidAmount);
        // Not Paid: a run is paid when no line has anything left on it.
        Assert.Equal(PayrollRunStatus.Approved, (await harness.ReloadAsync(run)).Status);
    }

    [Fact]
    public async Task Paying_by_bank_moves_no_cash_box_and_credits_512()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var salaried = await harness.HireAsync("Salima Kaci", payout: PayoutMethod.Bank);
        var run = await harness.ApprovedRunAsync((salaried, 90_000m));

        var result = await harness.Money.PayAsync(
            run.Id, new PayWagesRequest(run.Id, nameof(PayoutMethod.Bank)), "admin");

        Assert.True(result.Ok);
        Assert.Null(result.Value!.CashBoxId);
        // A bank is not a drawer — the receipt voucher's rule, and the reason a transfer can never
        // appear in the branch's Z-report.
        Assert.Empty(await harness.MovementsAsync(LedgerSources.WagePayment));
        Assert.Equal(5_000_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));
        Assert.Equal(-90_000m, await harness.AccountAsync(LedgerAccounts.Bank));
        Assert.Equal(0m, await harness.AccountAsync(LedgerAccounts.WagesPayable));
        Assert.Equal(PayrollRunStatus.Paid, (await harness.ReloadAsync(run)).Status);
    }

    [Fact]
    public async Task A_second_payment_takes_only_the_lines_still_unpaid_and_the_run_becomes_paid_when_none_remain()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var first = await harness.HireAsync("Ben Ali Mohamed");
        var second = await harness.HireAsync("Karim Belkacem");
        var run = await harness.ApprovedRunAsync((first, 48_000m), (second, 52_000m));

        var line = (await harness.LinesAsync(run)).Single(l => l.EmployeeId == first.Id);
        await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id,
            Lines: [new WagePaymentLineRequest(line.Id, 48_000m)]), "caissier");

        // Two days later, the man who was absent on the 2nd comes in. No lines named: take
        // whatever is still owed.
        var secondPayment = await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "caissier");

        Assert.True(secondPayment.Ok);
        Assert.Equal(52_000m, secondPayment.Value!.Amount);
        Assert.Equal(0m, await harness.AccountAsync(LedgerAccounts.WagesPayable));
        Assert.Equal(4_900_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));

        var reloaded = await harness.ReloadAsync(run);
        Assert.Equal(PayrollRunStatus.Paid, reloaded.Status);
        Assert.NotNull(reloaded.PaidAtUtc);

        // And a third payment has nothing to take, which is a state and not a mistake.
        var third = await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "caissier");
        Assert.Equal(PayrollFailure.Conflict, third.Failure);
    }

    [Fact]
    public async Task A_short_box_warns_and_does_not_refuse()
    {
        // Payday out of a drawer the morning's refill has not been counted into.
        await using var harness = await WageMoneyHarness.NewAsync(algiersFloat: 10_000m);
        var worker = await harness.HireAsync("Ben Ali Mohamed");
        var run = await harness.ApprovedRunAsync((worker, 48_000m));

        var result = await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "caissier");

        Assert.True(result.Ok);
        Assert.NotNull(result.Warning);
        Assert.Equal(-38_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));
        Assert.Equal(PayrollRunStatus.Paid, (await harness.ReloadAsync(run)).Status);
    }

    [Theory]
    [InlineData(PayrollRunStatus.Draft)]
    [InlineData(PayrollRunStatus.Locked)]
    [InlineData(PayrollRunStatus.Reversed)]
    public async Task Paying_a_run_that_is_not_approved_is_refused_and_the_refusal_names_the_status(
        PayrollRunStatus status)
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");
        var run = await harness.RunAsync(status, (worker, 48_000m));

        var result = await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "caissier");

        // The one hard gate in the module — and it says which status it found, so the screen can
        // tell the user what to do rather than "no".
        Assert.False(result.Ok);
        Assert.Equal(PayrollFailure.Conflict, result.Failure);
        Assert.Contains(status.ToString(), result.Message!);
        Assert.Equal(5_000_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));
        Assert.Empty(await harness.Db.WagePayments.ToListAsync());
    }

    [Fact]
    public async Task Overpaying_a_line_is_refused_and_says_by_how_much()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");
        var run = await harness.ApprovedRunAsync((worker, 48_000m));
        var line = (await harness.LinesAsync(run)).Single();

        var result = await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id,
            Lines: [new WagePaymentLineRequest(line.Id, 50_000m)]), "caissier");

        // Silently clamping would leave the cashier believing they handed over 50 000 while the
        // books say 48 000, and the difference would surface as an unexplained shortfall.
        Assert.False(result.Ok);
        Assert.Contains("2000", result.Message!.Replace(",", "").Replace(" ", ""));
        Assert.Contains("Ben Ali Mohamed", result.Message!);
        Assert.Empty(await harness.Db.WagePayments.ToListAsync());
    }

    [Fact]
    public async Task One_payment_cannot_pay_the_same_payslip_twice()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");
        var run = await harness.ApprovedRunAsync((worker, 48_000m));
        var line = (await harness.LinesAsync(run)).Single();

        // Two rows of 30 000 each pass the overpay check on their own and together hand over
        // 60 000 against a 48 000 payslip.
        var result = await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id,
            Lines:
            [
                new WagePaymentLineRequest(line.Id, 30_000m),
                new WagePaymentLineRequest(line.Id, 30_000m)
            ]), "caissier");

        Assert.False(result.Ok);
        Assert.Empty(await harness.Db.WagePayments.ToListAsync());
    }

    [Fact]
    public async Task The_automatic_selection_pays_only_the_people_this_method_pays()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var crew = await harness.HireAsync("Ben Ali Mohamed");
        var office = await harness.HireAsync("Salima Kaci", payout: PayoutMethod.Bank);
        var run = await harness.ApprovedRunAsync((crew, 48_000m), (office, 90_000m));

        var cash = await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "caissier");

        // The crew is paid over the counter; the salaried woman's salary must not be.
        Assert.True(cash.Ok);
        Assert.Equal(48_000m, cash.Value!.Amount);
        Assert.Equal(0m, (await harness.LinesAsync(run)).Single(l => l.EmployeeId == office.Id).PaidAmount);

        var bank = await harness.Money.PayAsync(
            run.Id, new PayWagesRequest(run.Id, nameof(PayoutMethod.Bank)), "admin");
        Assert.Equal(90_000m, bank.Value!.Amount);
        Assert.Equal(PayrollRunStatus.Paid, (await harness.ReloadAsync(run)).Status);
    }

    [Fact]
    public async Task Reversing_a_payment_puts_the_cash_back_and_reopens_the_lines()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");
        var run = await harness.ApprovedRunAsync((worker, 48_000m));

        var paid = await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "caissier");
        Assert.Equal(PayrollRunStatus.Paid, (await harness.ReloadAsync(run)).Status);

        var reversed = await harness.Money.ReversePaymentAsync(
            paid.Value!.Id, "Paid out of the wrong box.", "admin");

        Assert.True(reversed.Ok);
        Assert.Equal(5_000_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));
        // The liability stands again: the wage cost never went away, only the payment did.
        Assert.Equal(-48_000m, await harness.AccountAsync(LedgerAccounts.WagesPayable));
        Assert.Equal(0m, (await harness.LinesAsync(run)).Single().PaidAmount);
        Assert.Null((await harness.LinesAsync(run)).Single().WagePaymentId);

        var reloaded = await harness.ReloadAsync(run);
        Assert.Equal(PayrollRunStatus.Approved, reloaded.Status);
        Assert.Null(reloaded.PaidAtUtc);

        // The cash going back is its own counted correction, not a mirrored payout.
        var back = (await harness.MovementsAsync(LedgerSources.WagePayment))
            .Single(m => m.Direction == CashDirection.In);
        Assert.Equal(CashMovementKind.Adjustment, back.Kind);
        Assert.Equal("Paid out of the wrong box.", back.Note);

        // And the payment itself is still there, saying so.
        var row = Assert.Single(await harness.Money.ListPaymentsAsync(run.Id, null, 50));
        Assert.True(row.IsReversed);
        Assert.Equal("Paid out of the wrong box.", row.ReversalReason);
    }

    [Fact]
    public async Task A_payment_cannot_be_reversed_twice_and_a_reversal_needs_a_reason()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");
        var run = await harness.ApprovedRunAsync((worker, 48_000m));

        var paid = await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "caissier");

        var unexplained = await harness.Money.ReversePaymentAsync(paid.Value!.Id, "  ", "admin");
        Assert.Equal(PayrollFailure.Invalid, unexplained.Failure);

        await harness.Money.ReversePaymentAsync(paid.Value!.Id, "Wrong box.", "admin");
        var again = await harness.Money.ReversePaymentAsync(paid.Value!.Id, "Wrong box.", "admin");

        // Reversing twice doubles the correction and looks exactly as legitimate as the first.
        Assert.Equal(PayrollFailure.Conflict, again.Failure);
        Assert.Equal(5_000_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));
    }

    [Fact]
    public async Task Cash_and_ledger_agree_after_an_advance_and_a_wage_payment()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");

        await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, 20_000m, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id),
            "chef de chantier");

        var run = await harness.ApprovedRunAsync((worker, 28_000m));
        await harness.Money.PayAsync(run.Id, new PayWagesRequest(
            run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "caissier");

        // 20 000 out as an advance, 28 000 out as wages: the drawer and account 530 have to be the
        // same number, or one of the two reports is lying.
        var box = await harness.BoxBalanceAsync(harness.AlgiersBox.Id);
        var cash = await harness.AccountAsync(LedgerAccounts.Cash);
        Assert.Equal(4_952_000m, box);
        Assert.Equal(-48_000m, cash);
        Assert.Equal(5_000_000m + cash, box);

        // The advance is still standing — recovering it belongs to the run, not to payday.
        Assert.Equal(20_000m, await harness.AccountAsync(LedgerAccounts.StaffAdvances));
    }

    [Fact]
    public async Task A_run_whose_payment_was_reversed_can_be_reversed_in_its_turn()
    {
        // The trap this closes: reversing a payment restored the cash and then bricked the month.
        // The guard joined the payment lines with no test for whether the payment still stood, so
        // the run could never be reversed and its branch could never carry another run for that
        // period — while the screen went on offering the button and promising the month reopened.
        await using var harness = await WageMoneyHarness.NewAsync();
        var yacine = await harness.HireAsync("Yacine Haddad");
        var run = await harness.ApprovedRunAsync((yacine, 28_396m));

        var paid = await harness.Money.PayAsync(
            run.Id, new PayWagesRequest(run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "caissier");
        Assert.True(paid.Ok);

        var reversedPayment = await harness.Money.ReversePaymentAsync(
            paid.Value!.Id, "erreur de bénéficiaire", "caissier");
        Assert.True(reversedPayment.Ok);
        Assert.Equal(PayrollRunStatus.Approved, (await harness.ReloadAsync(run)).Status);
        Assert.All(await harness.LinesAsync(run), line => Assert.Equal(0m, line.PaidAmount));

        var reversedRun = await harness.Payroll.ReverseRunAsync(run.Id, "mois recalculé", "admin");

        Assert.True(reversedRun.Ok, reversedRun.Message);
        Assert.Equal(PayrollRunStatus.Reversed, (await harness.ReloadAsync(run)).Status);
        // And the month is open again, which is the whole point of a reversal.
        var again = await harness.Payroll.CreateAsync(run.BranchId, run.PeriodYear, run.PeriodMonth, "admin");
        Assert.True(again.Ok, again.Message);
    }

    [Fact]
    public async Task A_payment_that_still_stands_refuses_the_run_reversal_and_names_itself()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var yacine = await harness.HireAsync("Yacine Haddad");
        // A second person paid by transfer, so the cash payment settles part of the run and leaves
        // it Approved — a fully paid run is refused earlier, and for a different reason.
        var samir = await harness.HireAsync("Samir Belkacem", payout: PayoutMethod.Bank);
        var run = await harness.ApprovedRunAsync((yacine, 12_016m), (samir, 40_000m));

        var paid = await harness.Money.PayAsync(
            run.Id, new PayWagesRequest(run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "caissier");
        Assert.True(paid.Ok);
        Assert.Equal(PayrollRunStatus.Approved, (await harness.ReloadAsync(run)).Status);

        var refused = await harness.Payroll.ReverseRunAsync(run.Id, "mois recalculé", "admin");

        Assert.Equal(PayrollFailure.Conflict, refused.Failure);
        Assert.Equal(PayrollProblems.RunPaid, refused.Code);
        Assert.Equal(paid.Value!.Number, refused.Args!["payment"]);
    }

    [Fact]
    public async Task Overpaying_a_payslip_is_refused_with_a_code_the_screen_can_say_in_its_own_language()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var yacine = await harness.HireAsync("Yacine Haddad");
        var run = await harness.ApprovedRunAsync((yacine, 12_016m));
        var line = (await harness.LinesAsync(run)).Single();

        var refused = await harness.Money.PayAsync(
            run.Id,
            new PayWagesRequest(
                run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id,
                Lines: [new WagePaymentLineRequest(line.Id, 20_000m)]),
            "caissier");

        Assert.Equal(PayrollFailure.Invalid, refused.Failure);
        Assert.Equal(PayrollProblems.Overpaid, refused.Code);
        // Every figure the English sentence quotes is a parameter, so the French and Arabic
        // screens rebuild the sentence rather than printing this one over a translated form.
        Assert.Equal("Yacine Haddad", refused.Args!["name"]);
        Assert.Equal("7984", refused.Args["over"]);
        Assert.Equal("12016", refused.Args["owed"]);
    }

    [Fact]
    public async Task A_payment_and_its_entry_carry_the_day_the_money_left()
    {
        // The cash movement already carried PaidAtUtc. An entry dated "now" beside it put the same
        // payout in two months' reports, and nothing could ever reconcile the two.
        await using var harness = await WageMoneyHarness.NewAsync();
        var yacine = await harness.HireAsync("Yacine Haddad");
        var run = await harness.ApprovedRunAsync((yacine, 10_000m));
        var payday = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc);

        var paid = await harness.Money.PayAsync(
            run.Id,
            new PayWagesRequest(run.Id, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id, PaidAtUtc: payday),
            "caissier");
        Assert.True(paid.Ok);

        var entry = await harness.Db.LedgerEntries.AsNoTracking()
            .FirstAsync(e => e.SourceType == LedgerSources.WagePayment && e.SourceId == paid.Value!.Id);
        var movement = Assert.Single(await harness.MovementsAsync(LedgerSources.WagePayment));

        Assert.Equal(DateOnly.FromDateTime(payday), DateOnly.FromDateTime(entry.EntryDate));
        Assert.Equal(DateOnly.FromDateTime(payday), DateOnly.FromDateTime(movement.OccurredAtUtc));
    }
}
