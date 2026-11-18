using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Rentals.RentalTestPaper;

namespace ParadiERP.Tests.Receivables;

public class RentalUsageCreditTests
{
    [Theory]
    [InlineData(0, 0, 1000)]
    [InlineData(0, 400, 600)]
    [InlineData(19, 0, 1190)]
    public async Task Exact_return_credit_preserves_invoice_and_posts_nothing_twice(
        decimal tax, decimal earned, decimal expected)
    {
        await using var h = await ReceivableHarness.NewAsync(rentalTaxRate: tax);
        var (invoice, note, allocation) = await SeedAsync(h, earned);
        var balance = await h.BalanceAsync();
        var count = await h.Db.LedgerEntries.CountAsync();
        var originalTotal = invoice.GrandTotalTtc;
        var result = await h.Receivables.CreditEarlyReturnAsync(note.Id, "return-clerk");
        Assert.NotNull(result);
        Assert.True(result.Ok, result.Message);
        var credit = result.Value!;
        Assert.Equal(expected, credit.GrandTotalTtc);
        if (tax == 0)
        {
            Assert.Equal(TaxMode.Untaxed, invoice.TaxMode);
            Assert.Equal(0m, credit.TaxTotal);
        }
        else
        {
            Assert.Equal(TaxMode.Taxed, invoice.TaxMode);
            Assert.True(credit.TaxTotal > 0);
        }
        Assert.Equal(note.Id, credit.ReturnNoteId);
        Assert.Equal(note.ContractId, credit.ContractId);
        Assert.Equal(allocation.Id, credit.Lines.Single().ReturnAllocationId);
        Assert.Equal(10m, credit.Lines.Single().ReturnedQty);
        Assert.Equal(originalTotal, (await h.Db.Invoices.AsNoTracking().SingleAsync()).GrandTotalTtc);
        Assert.Equal(balance, await h.BalanceAsync());
        Assert.Equal(count, await h.Db.LedgerEntries.CountAsync());
        var repeat = await h.Receivables.CreditEarlyReturnAsync(note.Id, "return-clerk");
        Assert.Equal(credit.Id, repeat!.Value!.Id);
        Assert.Equal(1, await h.Db.CreditNotes.CountAsync());
        Assert.Equal(invoice.Lines.Single().Qty * (1000m - earned) / 1000m,
            (await h.Receivables.CreditedQtyByLineAsync(invoice.Id))[invoice.Lines.Single().Id]);
    }

    [Fact]
    public async Task Existing_manual_credit_cannot_be_credited_twice_by_return()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (invoice, note, _) = await SeedAsync(h, 0);
        Assert.True((await h.Receivables.CreateCreditNoteAsync(invoice.Id,
            new CreateCreditNoteRequest("Existing correction"), "finance")).Ok);
        var result = await h.Receivables.CreditEarlyReturnAsync(note.Id, "return-clerk");
        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.Equal(1, await h.Db.CreditNotes.CountAsync());
    }

    [Fact]
    public async Task Historic_return_without_allocations_is_not_reinterpreted()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (_, note, _) = await SeedAsync(h, 0);
        h.Db.Set<ReturnNoteLineAllocation>().RemoveRange(await h.Db.Set<ReturnNoteLineAllocation>().ToListAsync());
        await h.Db.SaveChangesAsync();
        Assert.Null(await h.Receivables.CreditEarlyReturnAsync(note.Id, "return-clerk"));
        Assert.Empty(await h.Db.CreditNotes.ToListAsync());
    }

    private static async Task<(Invoice, ReturnNote, ReturnNoteLineAllocation)> SeedAsync(
        ReceivableHarness h, decimal earned)
    {
        var contractResult = await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 10, 1, RentUnitPrice: 100m, DamageUnitPrice: 2000m)])), "admin");
        Assert.True(contractResult.Ok, contractResult.Message);
        var contract = contractResult.Value!;
        var issue = await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers, [new IssueNoteLineRequest(contract.Lines.Single().Id, 10)],
            DateTime.UtcNow.AddHours(-2))), null, "issuer");
        Assert.True(issue.Ok, issue.Message);
        var invoiced = await h.Receivables.CreateInvoiceAsync(new CreateInvoiceRequest(
            DateTime.UtcNow.AddDays(30), IssueNoteId: issue.Value!.Note.Id), "finance");
        Assert.True(invoiced.Ok, invoiced.Message);
        var invoice = invoiced.Value!;
        var note = new ReturnNote
        {
            ContractId = contract.Id, BranchId = ReceivableHarness.Algiers, Number = "RET-TEST",
            ReturnedAtUtc = DateTime.UtcNow, UnusedRent = 1000m - earned,
            CreatedAtUtc = invoice.CreatedAtUtc.AddSeconds(1)
        };
        var line = new ReturnNoteLine
        {
            ReturnNoteId = note.Id, ContractLineId = contract.Lines.Single().Id, QtyOk = 10
        };
        var allocation = new ReturnNoteLineAllocation
        {
            ReturnNoteLineId = line.Id, IssueNoteId = issue.Value.Note.Id,
            ContractLineId = line.ContractLineId, Qty = 10, UnusedRentNet = 1000m - earned,
            ActualRentNet = earned, TaxRate = contract.Lines.Single().TaxRate,
            IssuedAtUtc = issue.Value.Note.IssuedAtUtc, ReturnedAtUtc = note.ReturnedAtUtc
        };
        line.Allocations.Add(allocation);
        note.Lines.Add(line);
        h.Db.ReturnNotes.Add(note);
        await h.Db.SaveChangesAsync();
        return (invoice, note, allocation);
    }
}
