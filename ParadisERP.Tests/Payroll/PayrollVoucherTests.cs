using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Receivables;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Payroll;

/// <summary>
/// Answer 24 (owner, 2026-10-02): "any amount that leaves the company needs a payment voucher, even
/// the salaries". Each payroll payout carries one numbered voucher, already paid and pointing at it,
/// which moves no cash and posts nothing of its own — the box and the ledger move once.
/// </summary>
public class PayrollVoucherTests
{
    private static Task<List<PaymentVoucher>> VouchersAsync(WageMoneyHarness h) =>
        h.Db.PaymentVouchers.AsNoTracking().OrderBy(v => v.Number).ToListAsync();

    [Fact]
    public async Task An_advance_is_on_a_paid_voucher_and_the_cash_leaves_once()
    {
        await using var h = await WageMoneyHarness.NewAsync();
        var worker = await h.HireAsync("Ben Ali Mohamed");

        var advance = await h.Money.GrantAdvanceAsync(
            new GrantWageAdvanceRequest(worker.Id, 20_000m, nameof(PayoutMethod.Cash), h.AlgiersBox.Id), "chef");
        Assert.True(advance.Ok);

        var voucher = Assert.Single(await VouchersAsync(h));
        Assert.Equal("PV-000001", voucher.Number);
        Assert.Equal(PaymentVoucherKind.Payroll, voucher.Kind);
        Assert.Equal(PaymentVoucherStatus.Paid, voucher.Status);
        Assert.Equal(LinkedPaymentVouchers.Advance, voucher.ExpenseCategory);
        Assert.Equal(20_000m, voucher.Amount);
        Assert.Equal(LedgerSources.WageAdvance, voucher.SourceType);
        Assert.Equal(advance.Value!.Id, voucher.SourceId);
        Assert.Contains(advance.Value.Number, voucher.Purpose);

        // One movement and one posting: the voucher records, it does not pay a second time.
        Assert.Equal(4_980_000m, await h.BoxBalanceAsync(h.AlgiersBox.Id));
        Assert.Empty(await h.Db.LedgerEntries.AsNoTracking().Where(e => e.SourceType == LedgerSources.PaymentVoucher).ToListAsync());
    }

    [Fact]
    public async Task Wages_are_on_a_voucher_and_a_reversal_cancels_it_with_its_reason()
    {
        await using var h = await WageMoneyHarness.NewAsync();
        var worker = await h.HireAsync("Ben Ali Mohamed");
        var run = await h.ApprovedRunAsync((worker, 48_000m));

        var paid = await h.Money.PayAsync(run.Id, new PayWagesRequest(run.Id, nameof(PayoutMethod.Cash), h.AlgiersBox.Id), "caissier");
        Assert.True(paid.Ok);
        var voucher = Assert.Single(await VouchersAsync(h));
        Assert.Equal(LinkedPaymentVouchers.Wages, voucher.ExpenseCategory);
        Assert.Equal(paid.Value!.Amount, voucher.Amount);
        Assert.Contains(run.Number, voucher.Purpose);

        Assert.True((await h.Money.ReversePaymentAsync(paid.Value.Id, "Paid out of the wrong box.", "admin")).Ok);
        var cancelled = Assert.Single(await VouchersAsync(h));
        Assert.Equal(PaymentVoucherStatus.Cancelled, cancelled.Status);
        Assert.Equal("Paid out of the wrong box.", cancelled.StatusNote);
    }

    [Fact]
    public async Task A_statutory_payment_gets_a_number_through_its_voucher()
    {
        await using var h = await WageMoneyHarness.NewAsync();
        var worker = await h.HireAsync("Ben Ali Mohamed");
        await h.ApprovedRunAsync((worker, 48_000m));

        var result = await h.Money.PayStatutoryAsync(new RecordStatutoryPaymentRequest(
            WageMoneyHarness.Algiers, LedgerAccounts.SocialSecurityPayable, 1_000m,
            nameof(PayoutMethod.Bank), PeriodYear: 2026, PeriodMonth: 9, Reference: "VIR-2026-09"), "comptable");
        Assert.True(result.Ok);

        var voucher = Assert.Single(await VouchersAsync(h));
        Assert.Equal(LinkedPaymentVouchers.Statutory, voucher.ExpenseCategory);
        Assert.Equal("bank", voucher.MethodKey);
        Assert.Equal("Cotisations et impôts 431 09/2026", voucher.Purpose);
    }
}
