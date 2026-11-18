using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Payroll;
using ParadiERP.Tests.Receivables;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Rentals.RentalTestPaper;

namespace ParadiERP.Tests.Ledger;

/// <summary>
/// COR-E01 (the owner with the accountant, 2026-11-05): a mistake in a closed month is corrected in the current month,
/// naming the original, and the closed month never changes. The three acts that belong to a closed month and used to
/// be refused — confirming a cheque received in it, approving a payroll run for it, extending a rental whose period
/// ended in it — are booked on the first open day instead, their text saying so (the owner chose the first open day,
/// the rule the till's late sales already follow). A document typed today with a closed-month date is still refused.
/// </summary>
public class ClosedMonthCorrectionTests
{
    private static readonly TimeZoneInfo Algiers = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");
    private static readonly DateOnly EndOfMarch = new(2026, 3, 31);
    /// <summary>1 April 2026 at midnight in Algiers: 31 March at 23:00 UTC.</summary>
    private static readonly DateTime FirstOpenDay = BookClosings.OpenFrom(EndOfMarch, Algiers);

    private static DateTime Day(string iso) => DateTime.Parse(iso + "T09:00:00Z").ToUniversalTime();

    private static async Task CloseMarchAsync(PosDbContext db)
    {
        var closed = await BookClosings.CloseAsync(db, EndOfMarch, null, null, "Admin", Algiers, DateTime.UtcNow, CancellationToken.None);
        Assert.True(closed.Ok, closed.Problem);
    }

    [Fact]
    public async Task A_cheque_received_in_a_closed_month_is_confirmed_on_the_first_open_day()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var cheque = await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers, 50_000m, "cheque", ReceivedAtUtc: Day("2026-03-20")), "caissier");
        Assert.True(cheque.Ok, cheque.Message);
        await CloseMarchAsync(h.Db);

        var confirmed = await h.Receivables.SettleReceiptAsync(cheque.Value!.Id, nameof(PaymentStatus.Confirmed), null, "admin");
        Assert.True(confirmed.Ok, confirmed.Message);

        var entry = await h.Db.LedgerEntries.AsNoTracking()
            .SingleAsync(e => e.SourceType == LedgerSources.CustomerReceipt && e.SourceId == cheque.Value.Id);
        Assert.Equal(FirstOpenDay, entry.EntryDate);
        Assert.Contains("first open day", entry.Description);
        Assert.Equal(-50_000m, await h.TradeDebtAsync());

        // A receipt typed today with a day in the closed month is still refused: whoever typed it learns the books are closed.
        await Assert.ThrowsAsync<PeriodClosedException>(() => h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers, 1_000m, "cash", ReceivedAtUtc: Day("2026-03-25")), "caissier"));
    }

    [Fact]
    public async Task A_payroll_run_approved_after_its_month_closed_is_booked_on_the_first_open_day()
    {
        await using var h = await PayrollRunHarness.NewAsync();
        var employee = await h.HireAsync("Amina B.", 20_000m);
        await h.DeclareAsync(employee.Id, 20m);
        var run = await h.OpenAsync();   // March 2026
        await h.ComputeAsync(run.Id);
        await CloseMarchAsync(h.Db);

        await h.ApproveAsync(run.Id);

        var entry = await h.Db.LedgerEntries.AsNoTracking()
            .SingleAsync(e => e.SourceType == LedgerSources.PayrollRun && e.SourceId == run.Id && !e.IsReversal);
        Assert.Equal(FirstOpenDay, entry.EntryDate);
        Assert.Contains("first open day", entry.Description);
    }

    [Fact]
    public async Task A_rental_extended_after_its_period_ended_in_a_closed_month_bills_the_new_period_on_the_first_open_day()
    {
        await using var h = await ReceivableHarness.NewAsync();
        // Ten scaffolds for ten days from 1 March: the period ends on the 11th.
        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 10, 10, RentUnitPrice: 1_000m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        Assert.True((await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers, [new IssueNoteLineRequest(contract.Lines.Single().Id, 10)], Day("2026-03-01"))), null, "m")).Ok);
        await CloseMarchAsync(h.Db);

        var extended = await h.Rentals.ExtendAsNewAsync(contract.Id, new ExtendAsNewContractRequest(20), null, "gerant");
        Assert.True(extended.Ok, extended.Message);

        var carried = await h.Db.IssueNotes.AsNoTracking().SingleAsync(n => n.ContractId == extended.Value!.Id);
        var billed = await h.Db.LedgerEntries.AsNoTracking()
            .SingleAsync(e => e.SourceType == LedgerSources.RentalIssue && e.SourceId == carried.Id);
        Assert.Equal(FirstOpenDay, billed.EntryDate);
        Assert.Contains("first open day", billed.Description);
    }
}
