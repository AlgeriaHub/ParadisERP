using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Receivables;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using static ParadiERP.Tests.Rentals.RentalTestPaper;

namespace ParadiERP.Tests.Rentals;

public class RentalDailyPricingUpgradeTests
{
    [Fact]
    public async Task Migration_queues_only_contracts_existing_at_upgrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        // The migration just before the correction: entities saved here use the current model.
        await db.GetService<IMigrator>().MigrateAsync("ContractSignedByAgent");
        var branch = new Branch { Code = "01", NameEn = "Test" };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        // The customer by hand too, for the same reason: the current model names its fiscal
        // identifiers (2026-10-10), which this table lacks yet.
        var customer = new Customer { Code = "TEST", FullName = "Synthetic customer" };
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Customers (Id, Version, UpdatedAtUtc, IsDeleted, Code, FullName, Kind, CreditLimit, Balance, LoyaltyPoints) " +
            "VALUES ({0}, 0, {1}, 0, 'TEST', 'Synthetic customer', 0, '0.0', '0.0', 0)",
            customer.Id, DateTime.UtcNow);
        // The contract by hand, naming only the columns it had then: the current model also names
        // the ones later migrations add (the cancellation, 2026-10-06), which this table lacks yet.
        var first = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO RentalContracts (Id, Number, CustomerId, BranchId, Status, DepositAmount, SignatoryRequired, " +
            "SignedByAgent, SignedAtUtc, CreatedAtUtc) VALUES ({0}, 'TEST-1', {1}, {2}, 0, '0.0', 0, 0, {3}, {3})",
            first, customer.Id, branch.Id, DateTime.UtcNow);
        await db.Database.MigrateAsync();
        Assert.Equal(first, (await db.RentalDailyPricingCorrections.SingleAsync()).ContractId);
        db.RentalContracts.Add(new() { CustomerId = customer.Id, BranchId = branch.Id, Number = "TEST-2" });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.Equal(1, await db.RentalDailyPricingCorrections.CountAsync());
    }

    private sealed class Regional : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new() { CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers" };
    }

    private static async Task<(Guid ContractId, Guid LineId, Invoice Invoice)> Issued(ReceivableHarness h)
    {
        // Generate legacy-valued documents through the real services, then restore the stored
        // rate that the old monthly engine would have used (30 / 30 = 1 per day).
        var contract = await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 2, 30, RentUnitPrice: 1m, OverdueUnitPrice: 2m, DamageUnitPrice: 100m)])), "test");
        Assert.True(contract.Ok, contract.Message);
        var lineId = contract.Value!.Lines.Single().Id;
        var issue = await h.Rentals.IssueAsync(contract.Value.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers, [new IssueNoteLineRequest(lineId, 2)], new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc))), null, "test");
        Assert.True(issue.Ok, issue.Message);
        var invoice = await h.Receivables.CreateInvoiceAsync(new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), IssueNoteId: issue.Value!.Note.Id), "test");
        Assert.True(invoice.Ok, invoice.Message);
        return (contract.Value.Id, lineId, invoice.Value!);
    }

    private static async Task QueueLegacy(ReceivableHarness h, Guid id)
    {
        foreach (var line in await h.Db.RentalContractLines.Where(l => l.ContractId == id).ToListAsync())
        { line.RentUnitPrice *= 30; line.OverdueUnitPrice *= 30; }
        foreach (var charge in await h.Db.RentalCharges.Where(c => c.ContractId == id).ToListAsync())
            if (charge.Kind is RentalChargeKind.Rent or RentalChargeKind.Overdue) charge.UnitPrice *= 30;
        h.Db.RentalDailyPricingCorrections.Add(new() { ContractId = id });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    public async Task Corrects_invoice_credit_tax_balance_preserves_receipt_and_is_idempotent(decimal tax)
    {
        await using var h = await ReceivableHarness.NewAsync(rentalTaxRate: tax);
        var old = await Issued(h);
        var credit = await h.Receivables.CreateCreditNoteAsync(old.Invoice.Id,
            new CreateCreditNoteRequest("Partial credit", Lines: [new CreditNoteLineRequest(old.Invoice.Lines.Single().Id, old.Invoice.Lines.Single().Qty / 2)]), "test");
        Assert.True(credit.Ok, credit.Message);
        Assert.True((await h.ReceiveAsync(10, invoiceId: old.Invoice.Id)).Ok);
        var originals = await h.Db.LedgerEntries.AsNoTracking().Select(e => e.Id).ToListAsync();
        await QueueLegacy(h, old.ContractId);
        var upgrade = new RentalDailyPricingUpgrade(h.Db, new Regional());
        Assert.Equal(1, await upgrade.ApplyAsync());
        var total = 1800m * (1 + tax / 100m);
        var invoice = await h.Db.Invoices.FindAsync(old.Invoice.Id);
        Assert.Equal(total, invoice!.GrandTotalTtc);
        Assert.Equal(total / 2, (await h.Db.CreditNotes.FindAsync(credit.Value!.Id))!.GrandTotalTtc);
        Assert.Equal(total / 2 - 10, await h.BalanceAsync());
        Assert.Equal(await h.BalanceAsync(), await h.TradeDebtAsync());
        Assert.Equal(total / 2 - 10, await h.OutstandingAsync(invoice.Id));
        var statement = await h.Statements.BuildAsync(ReceivableHarness.Contractor, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        Assert.Equal(await h.BalanceAsync(), statement!.ClosingBalance);
        var correction = Assert.Single(statement.Rows.Where(r => r.SourceType == LedgerSources.RentalIssueDailyCorrection));
        Assert.Equal(invoice.Number, correction.InvoiceNumber);
        Assert.False(string.IsNullOrEmpty(correction.DocumentNumber));
        Assert.Equal(10m, (await h.Db.ReceiptVouchers.SingleAsync()).Amount);
        Assert.Equal(10m, (await h.Db.ReceiptAllocations.SingleAsync()).Amount);
        Assert.Equal(originals.Count, await h.Db.LedgerEntries.CountAsync(e => originals.Contains(e.Id)));
        var audit = await h.Db.RentalDailyPricingCorrections.SingleAsync();
        Assert.NotNull(audit.CompletedAtUtc);
        Assert.Contains("60", audit.BeforeJson!);
        Assert.Contains("1800", audit.AfterJson!);
        var count = await h.Db.LedgerEntries.CountAsync();
        Assert.Equal(0, await upgrade.ApplyAsync());
        Assert.Equal(count, await h.Db.LedgerEntries.CountAsync());
        foreach (var entry in await h.Db.LedgerEntries.Include(e => e.Lines).ToListAsync())
            Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));
    }

    [Fact]
    public async Task Early_return_relief_recomputed_and_paid_contract_reopened()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var old = await Issued(h);
        Assert.True((await h.ReceiveAsync(60, invoiceId: old.Invoice.Id)).Ok);
        var returned = await h.Rentals.ReturnAsync(old.ContractId, Driven(new ReturnNoteRequest(
            ReceivableHarness.Algiers, [new ReturnNoteLineRequest(old.LineId, QtyOk: 2)],
            new DateTime(2026, 3, 11, 12, 0, 0, DateTimeKind.Utc))), null, "test");
        Assert.True(returned.Ok, returned.Message);
        Assert.Equal(RentalContractStatus.Closed, (await h.Db.RentalContracts.FindAsync(old.ContractId))!.Status);
        await QueueLegacy(h, old.ContractId);
        Assert.Equal(1, await new RentalDailyPricingUpgrade(h.Db, new Regional()).ApplyAsync());
        var note = await h.Db.ReturnNotes.SingleAsync();
        Assert.Equal(1200m, note.UnusedRent);
        Assert.Equal(1200m, note.ReliefDebtReduction);
        Assert.Equal(0m, note.ReliefPaymentVoucher);
        Assert.Equal(540m, await h.BalanceAsync());
        Assert.Equal(await h.BalanceAsync(), await h.TradeDebtAsync());
        Assert.Equal(RentalContractStatus.Open, (await h.Db.RentalContracts.FindAsync(old.ContractId))!.Status);
        Assert.Equal(600m, (await RentalClosure.StandingAsync(h.Db, old.ContractId)).Owed);
    }

    [Fact]
    public async Task Ambiguous_invoice_rolls_back_all_financial_changes()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var old = await Issued(h);
        old.Invoice.Lines.Single().LineNet = 123m;
        await h.Db.SaveChangesAsync();
        await QueueLegacy(h, old.ContractId);
        var count = await h.Db.LedgerEntries.CountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RentalDailyPricingUpgrade(h.Db, new Regional()).ApplyAsync());
        h.Db.ChangeTracker.Clear();
        Assert.Equal(count, await h.Db.LedgerEntries.CountAsync());
        Assert.Equal(60m, await h.BalanceAsync());
        Assert.Null((await h.Db.RentalDailyPricingCorrections.SingleAsync()).CompletedAtUtc);
        Assert.Null((await h.Db.RentalDailyPricingCorrections.SingleAsync()).BeforeJson);
    }

    [Fact]
    public async Task Overdue_return_invoice_corrected_without_inflating_damage()
    {
        await using var h = await ReceivableHarness.NewAsync(rentalTaxRate: 19);
        var old = await Issued(h);
        var returned = await h.Rentals.ReturnAsync(old.ContractId, Driven(new ReturnNoteRequest(
            ReceivableHarness.Algiers, [new ReturnNoteLineRequest(old.LineId, QtyOk: 1, QtyTotalDamage: 1)],
            new DateTime(2026, 4, 2, 12, 0, 0, DateTimeKind.Utc))), null, "test");
        Assert.True(returned.Ok, returned.Message);
        var bill = await h.Receivables.CreateInvoiceAsync(new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30),
            ReturnNoteId: returned.Value!.Note.Id), "test");
        Assert.True(bill.Ok, bill.Message);
        var damage = await h.Db.RentalCharges.AsNoTracking().SingleAsync(c => c.Kind == RentalChargeKind.Damage);
        var overdue = await h.Db.RentalCharges.AsNoTracking().SingleAsync(c => c.Kind == RentalChargeKind.Overdue);
        await QueueLegacy(h, old.ContractId);
        await new RentalDailyPricingUpgrade(h.Db, new Regional()).ApplyAsync();
        Assert.Equal(damage.Amount, (await h.Db.RentalCharges.FindAsync(damage.Id))!.Amount);
        Assert.Equal(overdue.Qty * overdue.Days * overdue.UnitPrice * 30, (await h.Db.RentalCharges.FindAsync(overdue.Id))!.Amount);
        var invoice = await h.Db.Invoices.FindAsync(bill.Value!.Id);
        Assert.Equal(damage.Amount + overdue.Amount * 30, invoice!.SubTotalHt);
        Assert.Equal(invoice.SubTotalHt * 1.19m, invoice.GrandTotalTtc);
        Assert.Equal(await h.BalanceAsync(), await h.TradeDebtAsync());
    }

    [Fact]
    public async Task Already_daily_historical_contract_has_no_extra_postings()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var old = await Issued(h);
        h.Db.RentalDailyPricingCorrections.Add(new() { ContractId = old.ContractId });
        await h.Db.SaveChangesAsync();
        var count = await h.Db.LedgerEntries.CountAsync();
        Assert.Equal(1, await new RentalDailyPricingUpgrade(h.Db, new Regional()).ApplyAsync());
        Assert.Equal(count, await h.Db.LedgerEntries.CountAsync());
        Assert.Equal(60m, await h.BalanceAsync());
    }

    [Fact]
    public async Task New_daily_contract_not_queued_is_untouched()
    {
        await using var h = await ReceivableHarness.NewAsync();
        await Issued(h);
        Assert.Equal(0, await new RentalDailyPricingUpgrade(h.Db, new Regional()).ApplyAsync());
        Assert.Equal(60m, await h.BalanceAsync());
    }
}
