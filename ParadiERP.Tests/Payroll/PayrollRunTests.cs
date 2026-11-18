using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Payroll;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Engine = ParadiERP.Application.Payroll;

namespace ParadiERP.Tests.Payroll;

/// <summary>
/// The run's lifecycle — docs/07 §5.7 — and what approving one leaves in the books, §5.9.
///
/// <para>
/// The rules worth a test are the ones that cost money when they are wrong: two live runs for
/// one month, a recompute that wipes a payslip somebody has been paid against, a reversal that
/// forgets the advance it recovered, and an employee edit that quietly rewrites last March's
/// paper.
/// </para>
/// </summary>
public class PayrollRunTests
{
    // ---------- one live run per branch and month ----------

    [Fact]
    public async Task Two_live_runs_for_the_same_branch_and_period_are_refused()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        await harness.OpenAsync();

        var second = await harness.Payroll.CreateAsync(
            PayrollRunHarness.Algiers, PayrollRunHarness.Year, PayrollRunHarness.Month, "admin");

        Assert.False(second.Ok);
        Assert.Equal(PayrollFailure.Conflict, second.Failure);
        // The sentence names the run in the way, so the admin can go and look at it.
        Assert.Contains("RUN-000001", second.Message);

        // The same month at another branch is a different document entirely.
        var elsewhere = await harness.Payroll.CreateAsync(
            PayrollRunHarness.Oran, PayrollRunHarness.Year, PayrollRunHarness.Month, "admin");
        Assert.True(elsewhere.Ok, elsewhere.Message);
    }

    [Fact]
    public async Task Approving_a_draft_run_is_refused()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var run = await harness.OpenAsync();

        var approved = await harness.Payroll.ApproveAsync(run.Id, "admin");

        Assert.False(approved.Ok);
        Assert.Equal(PayrollRunStatus.Draft, (await harness.ReadAsync(run.Id)).Status);
        Assert.Empty(await harness.EntriesAsync());
    }

    [Fact]
    public async Task Compute_is_refused_while_the_tax_scale_is_not_configured_and_the_run_stays_draft()
    {
        await using var harness = await PayrollRunHarness.NewAsync(
            s => { s.IncomeTaxMode = Engine.IncomeTaxMode.NotConfigured; s.IncomeTaxBrackets = []; });
        await harness.HireAsync("Amina B.", 20_000m);
        var run = await harness.OpenAsync();

        var computed = await harness.Payroll.ComputeAsync(run.Id, "admin");

        // The one refusal payroll has. An empty scale must never be answered with zero tax: an
        // unnoticed zero is a company that has withheld nothing all year.
        Assert.False(computed.Ok);
        Assert.Equal(PayrollFailure.Refused, computed.Failure);
        Assert.Equal("income_tax_not_configured", computed.Refusal!.Code);
        Assert.Equal(Permissions.EditPayrollStatutory, computed.Refusal.FixableBy);
        Assert.NotEmpty(computed.Refusal.MissingFields);

        var stored = await harness.ReadAsync(run.Id);
        Assert.Equal(PayrollRunStatus.Draft, stored.Status);
        Assert.Empty(await harness.LinesAsync(run.Id));
    }

    // ---------- computing ----------

    [Fact]
    public async Task Recompute_replaces_the_lines_and_never_duplicates_them()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        Assert.Equal(20_000m, (await harness.LinesAsync(run.Id)).Single().Gross);

        // A one-off figure is an input, not an element: the recompute re-reads it instead of
        // wiping it, which is exactly why it is not stored as a line.
        var adjustment = await harness.Payroll.UpsertAdjustmentAsync(
            run.Id, employee.Id,
            new Application.Contracts.UpsertPayrollAdjustmentRequest(
                employee.Id, "bonus", 1_000m, "Prime de chantier"),
            "admin");
        Assert.True(adjustment.Ok, adjustment.Message);

        await harness.ComputeAsync(run.Id);

        var lines = await harness.LinesAsync(run.Id);
        Assert.Single(lines);
        Assert.Equal(21_000m, lines[0].Gross);
        // And no orphan elements behind the replaced line.
        Assert.Equal(lines[0].Elements.Count, await harness.Db.PayrollRunLineElements.CountAsync());
    }

    [Fact]
    public async Task A_run_snapshots_the_settings_it_computed_with_and_a_later_change_does_not_move_it_until_recomputed()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        Assert.Equal(2_000m, (await harness.ReadAsync(run.Id)).SocialEmployee);

        // The finance law moves in April. February's closed payslips must not move with it.
        await harness.SaveSettingsAsync(PayrollRunHarness.Profile(s => s.SocialEmployeeRate = 25m));

        Assert.Equal(2_000m, (await harness.ReadAsync(run.Id)).SocialEmployee);
        Assert.Equal(10m, PayrollDocuments.Snapshot(await harness.ReadAsync(run.Id))!.SocialEmployeeRate);

        await harness.ComputeAsync(run.Id);
        Assert.Equal(5_000m, (await harness.ReadAsync(run.Id)).SocialEmployee);
        Assert.Equal(25m, PayrollDocuments.Snapshot(await harness.ReadAsync(run.Id))!.SocialEmployeeRate);
    }

    [Fact]
    public async Task Editing_an_employee_after_a_run_does_not_change_the_run_line_snapshot()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);

        var row = await harness.Db.Employees.FirstAsync(e => e.Id == employee.Id);
        row.FullName = "Amina Benali";
        row.BranchId = PayrollRunHarness.Oran;
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var line = (await harness.LinesAsync(run.Id)).Single();
        // The same rule the invoice's buyer block follows: a corrected spelling or a transfer
        // next year must not change what last March's paper said.
        Assert.Equal("Amina B.", line.EmployeeNameSnapshot);
        Assert.Equal(PayrollRunHarness.Algiers, line.BranchIdSnapshot);
    }

    [Fact]
    public async Task An_employee_moved_to_another_branch_mid_month_is_paid_whole_by_the_branch_holding_them_at_compute_time()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Karim T.", 30_000m, branchId: PayrollRunHarness.Algiers);
        await harness.DeclareAsync(employee.Id, 20m);

        // The transfer happens before either run is computed.
        var row = await harness.Db.Employees.FirstAsync(e => e.Id == employee.Id);
        row.BranchId = PayrollRunHarness.Oran;
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var algiers = await harness.OpenAsync(PayrollRunHarness.Algiers);
        var oran = await harness.OpenAsync(PayrollRunHarness.Oran);
        await harness.ComputeAsync(algiers.Id);
        await harness.ComputeAsync(oran.Id);

        // Whole, once, at the branch that holds them now. Half a payslip at each of two branches
        // is not something anybody can read, and there is no dated branch history to split it by.
        Assert.Empty(await harness.LinesAsync(algiers.Id));
        Assert.Equal(30_000m, (await harness.LinesAsync(oran.Id)).Single().Gross);
    }

    [Fact]
    public async Task A_monthly_employee_with_no_attendance_is_paid_in_full_and_the_run_says_so()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        await harness.HireAsync("Amina B.", 20_000m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);

        // Never held: a salary is what a salary is, and a foreman's missing sheet is a warning
        // beside the figure rather than a month nobody gets paid for.
        Assert.Equal(20_000m, (await harness.LinesAsync(run.Id)).Single().Gross);
        var warnings = await harness.Payroll.WarningCountsAsync(run.Id);
        Assert.Contains(warnings, w => w.Code == Engine.PayrollWarnings.MonthlyWithoutAttendance && w.Lines == 1);
    }

    [Fact]
    public async Task A_run_computed_against_unconfirmed_figures_is_provisional_and_stays_so_afterwards()
    {
        await using var harness = await PayrollRunHarness.NewAsync(s =>
        {
            s.Confirmed = false;
            s.ConfirmedAtUtc = null;
        });
        await harness.HireAsync("Amina B.", 20_000m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        Assert.True((await harness.ReadAsync(run.Id)).IsProvisional);

        // Confirming the figures afterwards does not un-say what the payslip already said.
        await harness.SaveSettingsAsync(PayrollRunHarness.Profile());
        Assert.True((await harness.ReadAsync(run.Id)).IsProvisional);
    }

    [Fact]
    public async Task A_branch_ten_times_the_size_computes_with_the_same_number_of_queries()
    {
        // The rule §5.7 is built around: SubscriptionWriteFilter holds the sync-counter row for
        // the whole handler, so a compute that issued a query per employee would stall every till
        // pushing at month end. Counting the reads is the only way to keep that true — a number
        // that merely looks small today is what an N+1 looks like before the branch grows.
        await using var small = await PayrollRunHarness.NewAsync();
        await small.HireCrewAsync(20);
        var one = await small.OpenAsync();
        small.Queries.Reset();
        await small.ComputeAsync(one.Id);
        var reads = small.Queries.Selects;

        await using var large = await PayrollRunHarness.NewAsync();
        await large.HireCrewAsync(200);
        var many = await large.OpenAsync();
        large.Queries.Reset();
        await large.ComputeAsync(many.Id);

        Assert.Equal(reads, large.Queries.Selects);
        Assert.Equal(200, (await large.ReadAsync(many.Id)).HeadCount);
        // Six bulk reads, the settings row, the run itself and the paid-lines guard; since 2026-11-08 also the staff ids
        // and the workers' bonuses paid by voucher, one read each (the bonus lines must be read with tracking, so they
        // cannot be composed with the no-tracking staff query). The bound is stated so that another read has to be a
        // deliberate act rather than an accident.
        Assert.True(reads <= 13, $"A compute took {reads} reads; §5.7 allows a bounded handful.");
    }

    // ---------- the ledger ----------

    [Fact]
    public async Task A_run_posts_one_balanced_entry_per_branch_not_one_per_employee()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        foreach (var name in new[] { "Amina B.", "Karim T.", "Yacine M." })
        {
            var employee = await harness.HireAsync(name, 20_000m);
            await harness.DeclareAsync(employee.Id, 20m);
        }

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        await harness.ApproveAsync(run.Id);

        var entries = await harness.EntriesAsync();
        Assert.Single(entries);
        Assert.Equal(entries[0].Lines.Sum(l => l.Debit), entries[0].Lines.Sum(l => l.Credit));

        var posted = await harness.PostedAsync();
        Assert.Equal(60_000m, posted[LedgerAccounts.Wages]);
        Assert.Equal(12_000m, posted[LedgerAccounts.EmployerSocialContributions]);
        Assert.Equal(-18_000m, posted[LedgerAccounts.SocialSecurityPayable]);
        Assert.Equal(-54_000m, posted[LedgerAccounts.WagesPayable]);
        Assert.Equal(run.Id, entries[0].SourceId);
        Assert.Equal(LedgerSources.PayrollRun, entries[0].SourceType);
        Assert.Equal((await harness.ReadAsync(run.Id)).LedgerEntryId, entries[0].Id);
    }

    [Fact]
    public async Task Approving_twice_posts_once()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        await harness.ApproveAsync(run.Id);

        // A double click, or a retried request. The state machine refuses the second, and the
        // poster would refuse it again on the document's own id.
        var again = await harness.Payroll.ApproveAsync(run.Id, "admin");
        Assert.False(again.Ok);
        Assert.Single(await harness.EntriesAsync());
    }

    // ---------- payments guard the lines ----------

    [Fact]
    public async Task A_line_that_has_been_paid_stops_a_recompute_and_the_refusal_names_the_payment()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        await harness.ApproveAsync(run.Id);

        var line = (await harness.LinesAsync(run.Id)).Single();
        var payment = await harness.PayLineAsync(run.Id, line.Id, line.Net);

        // Unlocked back to a computable state the only way an approved run could be: it cannot,
        // so the guard is checked on the compute of a run that has payments against it.
        var reopened = await harness.Db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        reopened.Status = PayrollRunStatus.Locked;
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var computed = await harness.Payroll.ComputeAsync(run.Id, "admin");
        Assert.False(computed.Ok);
        Assert.Contains(payment.Number, computed.Message);
        // The line is still there: a foreign-key error at the database would have been the other
        // way this ends, and it is not a sentence anybody can act on.
        Assert.Single(await harness.LinesAsync(run.Id));
    }

    // ---------- reversal ----------

    [Fact]
    public async Task A_reversed_month_can_be_run_again_and_the_reversed_run_keeps_both_ledger_links()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        await harness.ApproveAsync(run.Id);

        var reversed = await harness.Payroll.ReverseRunAsync(run.Id, "Sheet was wrong", "admin");
        Assert.True(reversed.Ok, reversed.Message);

        var stored = await harness.ReadAsync(run.Id);
        Assert.Equal(PayrollRunStatus.Reversed, stored.Status);
        Assert.NotNull(stored.LedgerEntryId);
        Assert.NotNull(stored.ReversalEntryId);
        Assert.Equal("Sheet was wrong", stored.ReversalReason);

        // The books are back where they were, with both figures visible.
        var posted = await harness.PostedAsync();
        Assert.Equal(0m, posted[LedgerAccounts.Wages]);
        Assert.Equal(0m, posted[LedgerAccounts.WagesPayable]);

        // And the month is open again — which a plain unique index over (branch, period) would
        // have made impossible for ever.
        var second = await harness.Payroll.CreateAsync(
            PayrollRunHarness.Algiers, PayrollRunHarness.Year, PayrollRunHarness.Month, "admin");
        Assert.True(second.Ok, second.Message);
        await harness.ComputeAsync(second.Value!.Id);
    }

    [Fact]
    public async Task A_reversal_without_a_reason_is_refused()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        await harness.ApproveAsync(run.Id);

        var refused = await harness.Payroll.ReverseRunAsync(run.Id, "   ", "admin");
        Assert.False(refused.Ok);
        Assert.Equal(PayrollRunStatus.Approved, (await harness.ReadAsync(run.Id)).Status);
    }

    [Fact]
    public async Task A_paid_run_is_final_and_cannot_be_reversed()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        await harness.ApproveAsync(run.Id);

        var paid = await harness.Db.PayrollRuns.FirstAsync(r => r.Id == run.Id);
        paid.Status = PayrollRunStatus.Paid;
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var refused = await harness.Payroll.ReverseRunAsync(run.Id, "Changed my mind", "admin");
        Assert.False(refused.Ok);
        // Corrected in the next run instead — the nothing-is-edited rule the ledger and the
        // invoice already follow.
        Assert.Equal(PayrollRunStatus.Paid, (await harness.ReadAsync(run.Id)).Status);
    }

    // ---------- advances ----------

    [Fact]
    public async Task An_advance_is_recovered_when_the_run_is_approved_and_a_reversal_reopens_it()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);
        var advance = await harness.AdvanceAsync(employee.Id, 5_000m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);

        // Compute alone recovers nothing: a run is computed many times before anybody agrees it.
        Assert.Equal(0m, (await harness.Db.WageAdvances.AsNoTracking().FirstAsync(a => a.Id == advance.Id)).Recovered);
        Assert.Equal(5_000m, (await harness.ReadAsync(run.Id)).AdvancesRecovered);
        Assert.Equal(13_000m, (await harness.ReadAsync(run.Id)).Net);

        await harness.ApproveAsync(run.Id);
        var recovered = await harness.Db.WageAdvances.AsNoTracking().FirstAsync(a => a.Id == advance.Id);
        Assert.Equal(5_000m, recovered.Recovered);
        Assert.Equal(WageAdvanceStatus.Recovered, recovered.Status);
        Assert.Equal(run.Id, recovered.RecoveredInRunId);

        var posted = await harness.PostedAsync();
        Assert.Equal(-5_000m, posted[LedgerAccounts.StaffAdvances]);
        Assert.Equal(-13_000m, posted[LedgerAccounts.WagesPayable]);

        await harness.Payroll.ReverseRunAsync(run.Id, "Wrong month", "admin");
        var reopened = await harness.Db.WageAdvances.AsNoTracking().FirstAsync(a => a.Id == advance.Id);
        // The man still owes it. A reversal that forgot this would write the advance off by
        // arithmetic nobody decided.
        Assert.Equal(0m, reopened.Recovered);
        Assert.Equal(WageAdvanceStatus.Open, reopened.Status);
        Assert.Null(reopened.RecoveredInRunId);
        Assert.Equal(0m, (await harness.PostedAsync())[LedgerAccounts.StaffAdvances]);
    }

    [Fact]
    public async Task An_advance_bigger_than_the_month_is_taken_to_the_net_and_the_rest_stays_open()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);
        var advance = await harness.AdvanceAsync(employee.Id, 30_000m);

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        await harness.ApproveAsync(run.Id);

        var stored = await harness.Db.WageAdvances.AsNoTracking().FirstAsync(a => a.Id == advance.Id);
        // Never a negative payslip, and never a write-off: what the month could not cover is
        // still owed and comes out of the next one.
        Assert.Equal(18_000m, stored.Recovered);
        Assert.Equal(WageAdvanceStatus.Open, stored.Status);
        Assert.Equal(0m, (await harness.LinesAsync(run.Id)).Single().Net);
    }

    // ---------- the chart on an existing company ----------

    [Fact]
    public async Task The_seeder_adds_the_payroll_accounts_to_an_existing_company_and_leaves_a_renamed_one_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(
            new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        // The seeder's other half stamps sync versions on the tenders it adds, and that reads the
        // counter row every company has.
        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);

        // A company that upgraded from before payroll existed: no 421, no 631, and an accountant
        // who renamed one of the accounts they do have.
        var payroll = new[]
        {
            LedgerAccounts.WagesPayable, LedgerAccounts.StaffAdvances, LedgerAccounts.SocialSecurityPayable,
            LedgerAccounts.IncomeTaxWithheld, LedgerAccounts.OtherPayrollTaxesPayable,
            LedgerAccounts.Wages, LedgerAccounts.EmployerSocialContributions, LedgerAccounts.EmployerPayrollTaxes
        };
        db.LedgerAccounts.RemoveRange(await db.LedgerAccounts.Where(a => payroll.Contains(a.Code)).ToListAsync());
        var cash = await db.LedgerAccounts.FirstAsync(a => a.Code == LedgerAccounts.Cash);
        cash.NameFr = "Caisse magasin";
        await db.SaveChangesAsync();

        await CoreDataSeeder.EnsureAsync(db);

        var codes = await db.LedgerAccounts.AsNoTracking().Select(a => a.Code).ToListAsync();
        Assert.All(payroll, code => Assert.Contains(code, codes));
        Assert.Equal(
            LedgerAccountType.Liability,
            (await db.LedgerAccounts.AsNoTracking().FirstAsync(a => a.Code == LedgerAccounts.WagesPayable)).Type);
        // And a deliberate rename is still a deliberate rename.
        Assert.Equal("Caisse magasin",
            (await db.LedgerAccounts.AsNoTracking().FirstAsync(a => a.Code == LedgerAccounts.Cash)).NameFr);
    }

    // ---------- who the run pays, and from which sheet ----------

    [Fact]
    public async Task Somebody_who_left_mid_month_is_still_paid_for_the_month_they_worked()
    {
        // The exact failure the directory exists to stop. `POST /employees/{id}/leave` writes a
        // leaving date *and* clears IsActive, so a run that filtered on the flag gave the man no
        // payslip at all for his last twenty days — no line, no net, a head count one short, and
        // no error anywhere.
        await using var harness = await PayrollRunHarness.NewAsync();
        var ali = await harness.HireAsync("Ali Bensalah", 30_000m);
        await harness.DeclareAsync(ali.Id, daysPresent: 14m);
        await harness.LeaveAsync(ali.Id, new DateOnly(PayrollRunHarness.Year, PayrollRunHarness.Month, 20));

        var run = await harness.ComputeAsync((await harness.OpenAsync()).Id);

        var line = Assert.Single(await harness.LinesAsync(run.Id));
        Assert.Equal(ali.Id, line.EmployeeId);
        Assert.Equal(1, run.HeadCount);
        Assert.True(line.Net > 0m);
    }

    [Fact]
    public async Task The_month_after_somebody_leaves_carries_no_line_for_them()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var ali = await harness.HireAsync("Ali Bensalah", 30_000m);
        await harness.LeaveAsync(ali.Id, new DateOnly(PayrollRunHarness.Year, PayrollRunHarness.Month, 20));

        var next = await harness.ComputeAsync(
            (await harness.OpenAsync(month: PayrollRunHarness.Month + 1)).Id);

        Assert.Empty(await harness.LinesAsync(next.Id));
        Assert.Equal(0, next.HeadCount);
    }

    [Fact]
    public async Task A_month_of_clock_punches_reaches_the_payslip()
    {
        // Punches materialise no AttendanceDay. A run that read only day rows found nothing, paid
        // a daily worker zero and warned about a month nobody had entered — while the sheet beside
        // it showed twenty days, source Recorded.
        await using var harness = await PayrollRunHarness.NewAsync();
        var karim = await harness.HireAsync(
            "Karim Ait", 1_500m, PayBasis.Daily, mode: AttendanceMode.Recorded);

        for (var day = 1; day <= 20; day++)
            await harness.PunchAsync(
                karim.Id,
                new DateTime(PayrollRunHarness.Year, PayrollRunHarness.Month, day, 6, 0, 0, DateTimeKind.Utc),
                hours: 10m);

        var run = await harness.ComputeAsync((await harness.OpenAsync()).Id);

        var line = Assert.Single(await harness.LinesAsync(run.Id));
        Assert.Equal(20m, line.DaysPresent);
        Assert.Equal(30_000m, line.Gross);
        Assert.Equal(AttendanceSource.Recorded, line.AttendanceSource);
        Assert.Empty(PayrollDocuments.Warnings(line.WarningsJson));
    }

    [Fact]
    public async Task A_half_day_is_half_a_day_on_the_payslip_too()
    {
        // The working day is ten hours in this profile. Four hours is 0.4 of a day on the sheet,
        // and counting the row as a whole day here overpaid a daily worker by 900 a time.
        await using var harness = await PayrollRunHarness.NewAsync();
        var karim = await harness.HireAsync(
            "Karim Ait", 1_500m, PayBasis.Daily, mode: AttendanceMode.Recorded);

        await harness.RecordDayAsync(
            karim.Id, new DateOnly(PayrollRunHarness.Year, PayrollRunHarness.Month, 2),
            AttendanceDayStatus.Present, hours: 4m);
        await harness.RecordDayAsync(
            karim.Id, new DateOnly(PayrollRunHarness.Year, PayrollRunHarness.Month, 3),
            AttendanceDayStatus.Present);

        var run = await harness.ComputeAsync((await harness.OpenAsync()).Id);

        var line = Assert.Single(await harness.LinesAsync(run.Id));
        Assert.Equal(1.4m, line.DaysPresent);
        Assert.Equal(2_100m, line.Gross);
    }

    // ---------- an advance is recovered once ----------

    [Fact]
    public async Task Two_computed_runs_cannot_both_recover_the_same_advance()
    {
        // A recovery only reaches WageAdvance.Recovered at Approve. Read raw, the second run saw
        // the whole balance still outstanding, deducted it again, and 425 was credited 10 000 for
        // a 5 000 loan — with the employee docked twice and no warning anywhere.
        await using var harness = await PayrollRunHarness.NewAsync();
        var ali = await harness.HireAsync("Ali Bensalah", 60_000m);
        await harness.DeclareAsync(ali.Id, daysPresent: 20m);
        await harness.DeclareAsync(ali.Id, daysPresent: 20m, month: PayrollRunHarness.Month + 1);
        var advance = await harness.AdvanceAsync(ali.Id, 5_000m);

        var march = await harness.ComputeAsync((await harness.OpenAsync()).Id);
        var april = await harness.ComputeAsync(
            (await harness.OpenAsync(month: PayrollRunHarness.Month + 1)).Id);

        Assert.Equal(5_000m, march.AdvancesRecovered);
        Assert.Equal(0m, april.AdvancesRecovered);

        await harness.ApproveAsync(march.Id);
        await harness.ApproveAsync(april.Id);

        Assert.Equal(5_000m, (await harness.AdvanceRowAsync(advance.Id)).Recovered);
        Assert.Equal(-5_000m, (await harness.PostedAsync())[LedgerAccounts.StaffAdvances]);
    }

    [Fact]
    public async Task Reversing_the_earlier_of_two_runs_that_part_recovered_one_advance_gives_its_share_back()
    {
        // The stamp on the advance holds one run, so the later recovery overwrote it and the
        // earlier run's reversal found nothing at all to give back: the payslip was voided and the
        // advance went on reading as repaid, with the company out the difference.
        await using var harness = await PayrollRunHarness.NewAsync();
        var ali = await harness.HireAsync("Ali Bensalah", 1_500m, PayBasis.Daily);
        await harness.DeclareAsync(ali.Id, daysPresent: 2m);
        await harness.DeclareAsync(ali.Id, daysPresent: 20m, month: PayrollRunHarness.Month + 1);
        var advance = await harness.AdvanceAsync(ali.Id, 10_000m);

        // Two days at 1 500 is 3 000 gross, so March cannot take the whole 10 000: the engine
        // holds the net at zero rather than handing the man a bill, and the rest waits for April.
        var march = await harness.ComputeAsync((await harness.OpenAsync()).Id);
        await harness.ApproveAsync(march.Id);
        var taken = march.AdvancesRecovered;
        Assert.True(taken is > 0m and < 10_000m);

        var april = await harness.ComputeAsync(
            (await harness.OpenAsync(month: PayrollRunHarness.Month + 1)).Id);
        await harness.ApproveAsync(april.Id);
        Assert.Equal(10_000m, (await harness.AdvanceRowAsync(advance.Id)).Recovered);

        await harness.ReverseAsync(march.Id);

        var after = await harness.AdvanceRowAsync(advance.Id);
        Assert.Equal(10_000m - taken, after.Recovered);
        Assert.Equal(WageAdvanceStatus.Open, after.Status);
    }

    [Fact]
    public async Task An_advance_recorded_after_the_compute_cannot_steal_the_recovery_the_payslip_showed()
    {
        // Back-dating is a legitimate entry — a forgotten cash advance gets the day it was handed
        // over. What must not follow is that it sorts ahead of the advance the payslip actually
        // deducted, closes itself against a document that never mentioned it, and leaves the first
        // one open. The order is the recorded one, which no date can move.
        await using var harness = await PayrollRunHarness.NewAsync();
        var ali = await harness.HireAsync("Ali Bensalah", 60_000m);
        await harness.DeclareAsync(ali.Id, daysPresent: 20m);
        var first = await harness.AdvanceAsync(ali.Id, 5_000m);

        var run = await harness.ComputeAsync((await harness.OpenAsync()).Id);
        Assert.Equal(5_000m, run.AdvancesRecovered);

        var backdated = await harness.AdvanceAsync(
            ali.Id, 5_000m, grantedAtUtc: new DateTime(2026, 2, 20, 9, 0, 0, DateTimeKind.Utc));

        await harness.ApproveAsync(run.Id);

        Assert.Equal(5_000m, (await harness.AdvanceRowAsync(first.Id)).Recovered);
        Assert.Equal(0m, (await harness.AdvanceRowAsync(backdated.Id)).Recovered);
    }

    // ---------- an adjustment is never approved around ----------

    [Fact]
    public async Task A_bonus_added_after_the_compute_refuses_the_approval_until_the_month_is_computed_again()
    {
        // Approved around, the adjustment is unreachable for good: the run leaves Locked so it can
        // no longer be edited, the payslip never showed it, and the screen goes on listing a bonus
        // the paper denies.
        await using var harness = await PayrollRunHarness.NewAsync();
        var ali = await harness.HireAsync("Ali Bensalah", 60_000m);
        await harness.DeclareAsync(ali.Id, daysPresent: 20m);
        var run = await harness.ComputeAsync((await harness.OpenAsync()).Id);

        var added = await harness.Payroll.UpsertAdjustmentAsync(
            run.Id, ali.Id,
            new UpsertPayrollAdjustmentRequest(ali.Id, "prime", 20_000m, "fin de chantier"), "admin");
        Assert.Equal(PayrollDocuments.RecomputeNeeded, added.Warning);
        harness.Db.ChangeTracker.Clear();

        var refused = await harness.Payroll.ApproveAsync(run.Id, "admin");
        Assert.Equal(PayrollFailure.Conflict, refused.Failure);
        Assert.Equal(PayrollProblems.NotRecomputed, refused.Code);
        Assert.Equal(PayrollRunStatus.Locked, (await harness.ReadAsync(run.Id)).Status);

        // Computed again, the bonus is on the payslip and the month approves.
        await harness.ComputeAsync(run.Id);
        var approved = await harness.ApproveAsync(run.Id);
        Assert.Equal(PayrollRunStatus.Approved, approved.Status);
        Assert.Contains(
            (await harness.LinesAsync(run.Id))[0].Elements,
            e => e.Kind == PayrollElementKind.Adjustment && e.Amount == 20_000m);
    }

    [Fact]
    public async Task A_raise_typed_after_the_compute_refuses_the_approval_until_the_month_is_computed_again()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var ali = await harness.HireAsync("Ali Bensalah", 60_000m);
        await harness.DeclareAsync(ali.Id, daysPresent: 20m);
        var run = await harness.ComputeAsync((await harness.OpenAsync()).Id);

        var employee = await harness.Db.Employees.SingleAsync(e => e.Id == ali.Id);
        employee.BaseRate = 70_000m;
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        Assert.True(await harness.Payroll.PayMovedAsync(await harness.ReadAsync(run.Id)));

        var refused = await harness.Payroll.ApproveAsync(run.Id, "admin");
        Assert.Equal(PayrollFailure.Conflict, refused.Failure);
        Assert.Equal(PayrollProblems.PayMoved, refused.Code);
        Assert.Equal(PayrollRunStatus.Locked, (await harness.ReadAsync(run.Id)).Status);

        await harness.ComputeAsync(run.Id);
        Assert.Equal(70_000m, (await harness.LinesAsync(run.Id)).Single().BaseRate);
        Assert.False(await harness.Payroll.PayMovedAsync(await harness.ReadAsync(run.Id)));
        var approved = await harness.ApproveAsync(run.Id);
        Assert.Equal(PayrollRunStatus.Approved, approved.Status);

        employee = await harness.Db.Employees.SingleAsync(e => e.Id == ali.Id);
        employee.BaseRate = 80_000m;
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        Assert.False(await harness.Payroll.PayMovedAsync(await harness.ReadAsync(run.Id)));
        Assert.Equal(70_000m, (await harness.LinesAsync(run.Id)).Single().BaseRate);
    }

    // ---------- the entry carries the month it is about ----------

    [Fact]
    public async Task The_accrual_is_dated_by_the_month_it_accrues_and_not_by_the_day_it_was_approved()
    {
        await using var harness = await PayrollRunHarness.NewAsync();
        var ali = await harness.HireAsync("Ali Bensalah", 60_000m);
        await harness.DeclareAsync(ali.Id, daysPresent: 20m);
        var run = await harness.ComputeAsync((await harness.OpenAsync()).Id);
        await harness.ApproveAsync(run.Id);

        var entry = Assert.Single(await harness.EntriesAsync());
        Assert.Equal(
            new DateOnly(PayrollRunHarness.Year, PayrollRunHarness.Month, 31),
            DateOnly.FromDateTime(entry.EntryDate));
        Assert.Equal(run.Id, entry.SourceId);
    }
}
