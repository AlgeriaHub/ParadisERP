using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Payroll;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Payroll;

/// <summary>
/// Cash drawn against a wage before the month closes — docs/07 §5.8.
///
/// <para>
/// The failure this exists to stop is quiet: money leaves a drawer for a person, and without a
/// document the Z-report is short with no name against it and the next payslip has nothing to
/// deduct. So every test here asks the two questions together — what the box says, and what the
/// ledger says.
/// </para>
/// </summary>
public class WageAdvanceTests
{
    [Fact]
    public async Task Granting_an_advance_takes_cash_from_the_chosen_box_and_debits_425_at_the_employees_branch()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");

        var result = await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, 20_000m, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id),
            "chef de chantier");

        Assert.True(result.Ok);
        Assert.Null(result.Warning);
        Assert.Equal("ADV-000001", result.Value!.Number);
        Assert.Equal(WageAdvanceStatus.Open, result.Value.Status);
        // The employee's branch, frozen — not the box's, and not whatever a later transfer says.
        Assert.Equal(WageMoneyHarness.Algiers, result.Value.BranchId);

        Assert.Equal(4_980_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));
        Assert.Equal(20_000m, await harness.AccountAsync(LedgerAccounts.StaffAdvances, WageMoneyHarness.Algiers));
        Assert.Equal(-20_000m, await harness.AccountAsync(LedgerAccounts.Cash, WageMoneyHarness.Algiers));
    }

    [Fact]
    public async Task The_cash_row_is_a_disbursement_so_the_treasury_screen_names_it_without_a_new_label()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");

        await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, 20_000m, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id),
            "chef de chantier");

        // treasury.kind.disbursement already exists in fr/ar/en and the treasury tab renders
        // treasury.kind.<kind> for whatever arrives, so this asserts the existing label is what a
        // movement written here shows — not that a label was added.
        var movement = Assert.Single(await harness.MovementsAsync(LedgerSources.WageAdvance));
        Assert.Equal(CashMovementKind.Disbursement, movement.Kind);
        Assert.Equal(CashDirection.Out, movement.Direction);
        Assert.Equal("ADV-000001", movement.Reference);
    }

    [Fact]
    public async Task An_advance_paid_from_another_branchs_box_names_that_branch_and_stays_in_the_group_figures()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        await harness.FundAsync(harness.OranBox, 1_000_000m);
        var worker = await harness.HireAsync("Ben Ali Mohamed", WageMoneyHarness.Algiers);

        // Sent to Oran for the week and paid out of Oran's box: the claim stays at Algiers, the
        // cash leaves Oran, and the entry has to say the two are different branches.
        var result = await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, 15_000m, nameof(PayoutMethod.Cash), harness.OranBox.Id),
            "admin");

        Assert.True(result.Ok);
        Assert.Equal(15_000m, await harness.AccountAsync(LedgerAccounts.StaffAdvances, WageMoneyHarness.Algiers));
        Assert.Equal(-15_000m, await harness.AccountAsync(LedgerAccounts.Cash, WageMoneyHarness.Oran));
        Assert.Equal(0m, await harness.AccountAsync(LedgerAccounts.Cash, WageMoneyHarness.Algiers));

        var entry = Assert.Single(
            await harness.Db.LedgerEntries.Where(e => e.SourceType == LedgerSources.WageAdvance).ToListAsync());
        // P0.2: named, never marked inter-branch, so the group's balance sheet keeps it.
        Assert.False(entry.IsInterBranch);
        Assert.Equal(WageMoneyHarness.Oran, entry.CounterpartBranchId);

        var dto = Assert.Single(await harness.Money.ListAdvancesAsync(null, null, null, 50));
        Assert.True(dto.IsInterBranch);

        // The group's trial balance carries the advance and the cash that left (P0.2) — and still does for an entry
        // marked inter-branch before the fix, which no posted row is edited to change.
        var reports = new ParadisERP.Infrastructure.Ledger.LedgerReports(harness.Db);
        decimal Group(TrialBalanceDto tb, string code) => tb.Rows.Where(r => r.Code == code).Sum(r => r.Debit - r.Credit);
        var from = DateTime.UtcNow.AddYears(-1);
        var to = DateTime.UtcNow.AddDays(1);
        var group = await reports.TrialBalanceAsync(from, to, null, default);
        Assert.Equal(15_000m, Group(group, LedgerAccounts.StaffAdvances));
        await harness.Db.LedgerEntries.Where(e => e.SourceType == LedgerSources.WageAdvance)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.IsInterBranch, true));
        var before = await reports.TrialBalanceAsync(from, to, null, default);
        Assert.Equal(15_000m, Group(before, LedgerAccounts.StaffAdvances));
        Assert.True(before.Balanced);
    }

    [Fact]
    public async Task A_short_box_warns_and_does_not_refuse()
    {
        // No float at all: the drawer is empty and the foreman is holding the money anyway.
        await using var harness = await WageMoneyHarness.NewAsync(algiersFloat: 0m);
        var worker = await harness.HireAsync("Ben Ali Mohamed");

        var result = await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, 20_000m, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id),
            "chef de chantier");

        Assert.True(result.Ok);
        Assert.NotNull(result.Warning);
        Assert.Contains("20000", result.Warning!.Replace(",", "").Replace(" ", ""));
        // And the money really left: a warning is a sentence, not a rollback.
        Assert.Equal(-20_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));
    }

    [Fact]
    public async Task An_advance_by_bank_moves_no_cash_box_and_credits_512()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Salima Kaci", payout: PayoutMethod.Bank);

        var result = await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, 30_000m, nameof(PayoutMethod.Bank)), "admin");

        Assert.True(result.Ok);
        Assert.Null(result.Value!.CashBoxId);
        // A bank is not a drawer: nothing here may appear in a Z-report.
        Assert.Empty(await harness.MovementsAsync(LedgerSources.WageAdvance));
        Assert.Equal(5_000_000m, await harness.BoxBalanceAsync(harness.AlgiersBox.Id));
        Assert.Equal(-30_000m, await harness.AccountAsync(LedgerAccounts.Bank));
        Assert.Equal(30_000m, await harness.AccountAsync(LedgerAccounts.StaffAdvances));
    }

    [Fact]
    public async Task Cash_without_a_box_is_refused_rather_than_guessed_at()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");

        var result = await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, 10_000m, nameof(PayoutMethod.Cash)), "admin");

        // Picking "the branch's box" for them would put a shortfall in a drawer nobody chose.
        Assert.False(result.Ok);
        Assert.Equal(PayrollFailure.Invalid, result.Failure);
        Assert.Empty(await harness.Db.WageAdvances.ToListAsync());
    }

    [Fact]
    public async Task An_advance_for_nothing_is_refused()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");

        var zero = await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, 0m, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "admin");
        var negative = await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, -5_000m, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id), "admin");

        Assert.False(zero.Ok);
        Assert.False(negative.Ok);
        Assert.Empty(await harness.Db.CashMovements.Where(m => m.Kind == CashMovementKind.Disbursement).ToListAsync());
    }

    [Fact]
    public async Task An_advance_to_somebody_who_has_left_is_granted_and_says_no_run_will_take_it_back()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var leaver = await harness.HireAsync(
            "Karim Belkacem", leftOn: new DateOnly(2026, 8, 31));

        var result = await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(leaver.Id, 12_000m, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id),
            "admin");

        // A final settlement is a real thing to hand over. What the person paying it cannot know
        // without being told is that no run will ever recover it.
        Assert.True(result.Ok);
        Assert.Contains("no run", result.Warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_advance_is_listed_with_what_is_still_owed_on_it()
    {
        await using var harness = await WageMoneyHarness.NewAsync();
        var worker = await harness.HireAsync("Ben Ali Mohamed");

        var granted = await harness.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, 20_000m, nameof(PayoutMethod.Cash), harness.AlgiersBox.Id),
            "admin");

        // What the run will try to take back. Sent rather than left to the client to subtract: it
        // is the figure the advances list is chased on.
        var advance = await harness.Db.WageAdvances.FirstAsync(a => a.Id == granted.Value!.Id);
        advance.Recovered = 8_000m;
        await harness.Db.SaveChangesAsync();

        var dto = Assert.Single(await harness.Money.ListAdvancesAsync(worker.Id, null, "Open", 50));
        Assert.Equal(20_000m, dto.Amount);
        Assert.Equal(8_000m, dto.Recovered);
        Assert.Equal(12_000m, dto.Outstanding);
        Assert.Equal("Ben Ali Mohamed", dto.EmployeeName);
        Assert.Equal("Alger-Centre", dto.BranchName);
    }
}
