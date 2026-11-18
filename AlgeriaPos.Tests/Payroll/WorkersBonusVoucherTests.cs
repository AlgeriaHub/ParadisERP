using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Receivables;
using Microsoft.EntityFrameworkCore;
using Engine = AlgeriaPos.Application.Payroll;

namespace AlgeriaPos.Tests.Payroll;

/// <summary>
/// Workers' bonuses on a payment voucher (مكافآت عمال, owner 2026-11-08: "it reflected in his payroll", "bonus may be
/// assigned for more than one employee"). The voucher pays the total and books it to staff advances; the payroll run of
/// the workers' month shows each bonus as an earning and takes it back as paid by voucher, so nobody is paid twice and
/// the expense is booked once, through payroll.
/// </summary>
public class WorkersBonusVoucherTests
{
    private static PaymentVouchers Vouchers(PayrollRunHarness h) =>
        new(h.Db, new LedgerPoster(h.Db), new AlgeriaPos.Tests.Manufacturing.MfgHarness.Algeria());

    private static CreatePaymentVoucherRequest Bonus(params (Guid Employee, decimal Amount)[] lines) => new(
        PayrollRunHarness.Algiers, PaymentVoucherKind.Expense, 0m, "cash", ExpenseCategory: PaymentVouchers.WorkersBonus,
        Purpose: "Prime de fin de chantier",
        BonusLines: lines.Select(l => new PaymentVoucherBonusLineRequest(l.Employee, l.Amount)).ToList(),
        PayrollMonth: $"{PayrollRunHarness.Year}-{PayrollRunHarness.Month:D2}");

    private static async Task<PaymentVoucher> PaidAsync(PayrollRunHarness h, CreatePaymentVoucherRequest request)
    {
        var vouchers = Vouchers(h);
        var raised = await vouchers.CreateAsync(request, "gerant");
        Assert.True(raised.Ok, raised.Message);
        var actor = new VoucherActor(null, "admin", true);
        Assert.True((await vouchers.ApproveAsync(raised.Value.Voucher.Id, null, actor)).Ok);
        var paid = await vouchers.PayAsync(raised.Value.Voucher.Id, null, actor);
        Assert.True(paid.Ok, paid.Message);
        h.Db.ChangeTracker.Clear();
        return paid.Value.Voucher;
    }

    [Fact]
    public async Task One_voucher_pays_several_workers_and_books_the_total_to_staff_advances()
    {
        await using var h = await PayrollRunHarness.NewAsync();
        var amina = await h.HireAsync("Amina B.", 20_000m);
        var karim = await h.HireAsync("Karim T.", 30_000m);

        var voucher = await PaidAsync(h, Bonus((amina.Id, 5_000m), (karim.Id, 2_000m)));

        Assert.Equal(7_000m, voucher.Amount);
        Assert.Equal("Amina B., Karim T.", voucher.PayeeName);
        var lines = (await h.Db.PaymentVoucherBonusLines.AsNoTracking().ToListAsync()).OrderBy(l => l.Amount).ToList();
        Assert.Equal([(karim.Id, 2_000m), (amina.Id, 5_000m)], lines.Select(l => (l.EmployeeId, l.Amount)));
        Assert.All(lines, l => Assert.Equal((PayrollRunHarness.Year, PayrollRunHarness.Month), (l.PayrollYear, l.PayrollMonth)));
        // Not an expense yet: the payroll run books it as wages.
        Assert.Equal(7_000m, (await h.PostedAsync(LedgerSources.PaymentVoucher))[LedgerAccounts.StaffAdvances]);
    }

    [Fact]
    public async Task A_bonus_names_workers_of_its_branch_once_each_with_an_amount()
    {
        await using var h = await PayrollRunHarness.NewAsync();
        var amina = await h.HireAsync("Amina B.", 20_000m);
        var oran = await h.HireAsync("Yacine O.", 20_000m, branchId: PayrollRunHarness.Oran);
        var vouchers = Vouchers(h);

        Assert.False((await vouchers.CreateAsync(Bonus(), "gerant")).Ok);
        Assert.False((await vouchers.CreateAsync(Bonus((amina.Id, 1_000m), (amina.Id, 500m)), "gerant")).Ok);
        Assert.False((await vouchers.CreateAsync(Bonus((amina.Id, 0m)), "gerant")).Ok);
        Assert.False((await vouchers.CreateAsync(Bonus((Guid.NewGuid(), 1_000m)), "gerant")).Ok);
        var elsewhere = await vouchers.CreateAsync(Bonus((oran.Id, 1_000m)), "gerant");
        Assert.False(elsewhere.Ok);
        Assert.Contains("another branch", elsewhere.Message);
        Assert.False((await vouchers.CreateAsync(Bonus((amina.Id, 1_000m)) with { PayrollMonth = "March" }, "gerant")).Ok);
        // Pay, never a rental contract's expense (owner, 2026-11-09).
        Assert.False((await vouchers.CreateAsync(Bonus((amina.Id, 1_000m)) with { ContractId = Guid.NewGuid() }, "gerant")).Ok);
        // Only a bonus names workers.
        Assert.False((await vouchers.CreateAsync(Bonus((amina.Id, 1_000m)) with { ExpenseCategory = "fuel", PayeeName = "Naftal" }, "gerant")).Ok);
    }

    [Fact]
    public async Task The_payslip_shows_the_bonus_and_takes_it_back_so_net_pay_is_unchanged_and_the_books_balance()
    {
        await using var h = await PayrollRunHarness.NewAsync();
        var amina = await h.HireAsync("Amina B.", 20_000m);
        var karim = await h.HireAsync("Karim T.", 26_000m);
        await h.DeclareAsync(amina.Id, 26m);
        await h.DeclareAsync(karim.Id, 26m);
        await PaidAsync(h, Bonus((amina.Id, 5_000m), (karim.Id, 2_000m)));

        var run = await h.OpenAsync(calculateStatutoryDeductions: false);
        await h.ComputeAsync(run.Id);

        var lines = (await h.LinesAsync(run.Id)).ToDictionary(l => l.EmployeeId);
        Assert.Equal((25_000m, 5_000m, 20_000m), (lines[amina.Id].Gross, lines[amina.Id].PaidByVoucher, lines[amina.Id].Net));
        Assert.Equal((28_000m, 2_000m, 26_000m), (lines[karim.Id].Gross, lines[karim.Id].PaidByVoucher, lines[karim.Id].Net));
        var element = Assert.Single(lines[amina.Id].Elements, e => e.Key == Engine.PayrollEngine.VoucherBonusKey);
        Assert.Equal((PayrollElementKind.Adjustment, 5_000m, true, true), (element.Kind, element.Amount, element.InSocialBase, element.InTaxBase));
        Assert.Equal(7_000m, (await h.ReadAsync(run.Id)).PaidByVoucher);

        await h.ApproveAsync(run.Id);
        var posted = await h.PostedAsync();
        // The bonus is wages once, through payroll; 425 is back to nothing; the staff are owed their usual net.
        Assert.Equal(46_000m + 7_000m, posted[LedgerAccounts.Wages]);
        Assert.Equal(0m, posted[LedgerAccounts.StaffAdvances]);
        Assert.Equal(-46_000m, posted[LedgerAccounts.WagesPayable]);
    }

    [Fact]
    public async Task A_bonus_goes_on_one_run_only_and_a_reversal_gives_it_to_the_next()
    {
        await using var h = await PayrollRunHarness.NewAsync();
        var amina = await h.HireAsync("Amina B.", 20_000m);
        await h.DeclareAsync(amina.Id, 26m);
        await h.DeclareAsync(amina.Id, 26m, month: PayrollRunHarness.Month + 1);
        await h.DeclareAsync(amina.Id, 26m, month: PayrollRunHarness.Month - 1);
        await PaidAsync(h, Bonus((amina.Id, 5_000m)));

        // A month before the bonus's does not take it.
        var february = await h.OpenAsync(month: PayrollRunHarness.Month - 1, calculateStatutoryDeductions: false);
        await h.ComputeAsync(february.Id);
        Assert.Equal(0m, (await h.ReadAsync(february.Id)).PaidByVoucher);

        var march = await h.OpenAsync(calculateStatutoryDeductions: false);
        await h.ComputeAsync(march.Id);
        Assert.Equal(march.Id, (await h.Db.PaymentVoucherBonusLines.AsNoTracking().SingleAsync()).RunId);
        await h.ApproveAsync(march.Id);
        var line = await h.Db.PaymentVoucherBonusLines.AsNoTracking().SingleAsync();
        Assert.Equal(march.Id, line.RunId);

        // April, computed while March holds it, does not take it again.
        var april = await h.OpenAsync(month: PayrollRunHarness.Month + 1, calculateStatutoryDeductions: false);
        await h.ComputeAsync(april.Id);
        Assert.Equal(0m, (await h.ReadAsync(april.Id)).PaidByVoucher);

        // March reversed: the bonus is free again, and April's next compute puts it on her payslip.
        await h.ReverseAsync(march.Id);
        Assert.Null((await h.Db.PaymentVoucherBonusLines.AsNoTracking().SingleAsync()).RunId);
        await h.Payroll.UnlockAsync(april.Id, "admin");
        h.Db.ChangeTracker.Clear();
        await h.ComputeAsync(april.Id);
        Assert.Equal(5_000m, (await h.ReadAsync(april.Id)).PaidByVoucher);
        Assert.Equal(april.Id, (await h.Db.PaymentVoucherBonusLines.AsNoTracking().SingleAsync()).RunId);
    }

    [Fact]
    public async Task The_payroll_settings_say_whether_a_bonus_counts_in_the_contribution_and_tax_bases()
    {
        await using var h = await PayrollRunHarness.NewAsync(s => { s.VoucherBonusInSocialBase = false; s.VoucherBonusInTaxBase = false; });
        var amina = await h.HireAsync("Amina B.", 20_000m);
        await h.DeclareAsync(amina.Id, 26m);
        await PaidAsync(h, Bonus((amina.Id, 5_000m)));

        var run = await h.OpenAsync(calculateStatutoryDeductions: false);
        await h.ComputeAsync(run.Id);

        var element = (await h.LinesAsync(run.Id)).Single().Elements.Single(e => e.Key == Engine.PayrollEngine.VoucherBonusKey);
        Assert.Equal((false, false), (element.InSocialBase, element.InTaxBase));
    }

    [Fact]
    public void In_the_engine_the_bonus_comes_back_before_any_advance()
    {
        var regional = new AlgeriaPos.Tests.Manufacturing.MfgHarness.Algeria().Get();
        var input = new Engine.EmployeeInput(
            Guid.NewGuid(), Engine.PayBasis.Monthly, 20_000m,
            new Engine.AttendanceSummary(26m, 0m, 0m, new Dictionary<string, decimal>(), Engine.AttendanceSource.Declared),
            OpenAdvances: [new Engine.OpenAdvance(Guid.NewGuid(), 50_000m)],
            VoucherBonuses: [new Engine.VoucherBonusInput(Guid.NewGuid(), 5_000m, true, true)]);

        var line = Engine.PayrollEngine.ComputeOne(PayrollRunHarness.Profile(), regional, input, calculateStatutoryDeductions: false);

        // 25,000 in hand: the 5,000 already paid comes back whole, the advance takes the other 20,000.
        Assert.Equal((25_000m, 5_000m, 20_000m, 0m), (line.Gross, line.PaidByVoucher, line.AdvanceRecovered, line.Net));
    }
}
