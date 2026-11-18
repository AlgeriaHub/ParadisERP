using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Rentals.RentalTestPaper;

namespace ParadisERP.Tests.Receivables;

/// <summary>
/// What the printed invoice and credit note read from the server (docs/07 §4.4): the buyer block
/// frozen at issue, the number of the paper an invoice bills, and one credit note by id.
/// </summary>
public class PrintFoundationTests
{
    [Fact]
    public async Task An_invoice_freezes_the_buyer_at_issue_and_a_later_rename_does_not_reach_it()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var customer = await h.Db.Customers.FirstAsync(c => c.Id == ReceivableHarness.Contractor);
        customer.Address = "12 rue Didouche Mourad, Alger";
        customer.TaxId = "000116001234567";
        await h.Db.SaveChangesAsync();

        var invoice = (await h.RaiseAsync()).Value!;
        Assert.Equal("Ben Ali BTP", invoice.BuyerName);
        Assert.Equal("12 rue Didouche Mourad, Alger", invoice.BuyerAddress);
        Assert.Equal("000116001234567", invoice.BuyerTaxId);

        // The customer moves and changes its name; the paper already handed over does not.
        customer.FullName = "Ben Ali BTP SPA";
        customer.Address = "Zone industrielle, Rouiba";
        customer.TaxId = null;
        await h.Db.SaveChangesAsync();

        var stored = await h.Db.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal("Ben Ali BTP", stored.BuyerName);
        Assert.Equal("12 rue Didouche Mourad, Alger", stored.BuyerAddress);
        Assert.Equal("000116001234567", stored.BuyerTaxId);
    }

    [Fact]
    public async Task Source_numbers_name_the_sale_and_the_rental_note_and_nothing_for_a_direct_invoice()
    {
        await using var h = await ReceivableHarness.NewAsync();

        var direct = (await h.RaiseAsync()).Value!;

        var sale = await h.PushSaleAsync(total: 100_000m, cash: 30_000m);
        var overSale = (await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), SaleId: sale), "admin")).Value!;

        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 100, 30, RentUnitPrice: 3_000m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        var issued = (await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers,
            [new IssueNoteLineRequest(contract.Lines.Single().Id, 60)], DateTime.UtcNow)), null, "magasinier")).Value!;
        var overIssue = (await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), IssueNoteId: issued.Note.Id), "admin")).Value!;

        var invoices = await h.Db.Invoices.AsNoTracking().ToListAsync();
        var numbers = await h.Receivables.SourceNumbersAsync(invoices);

        Assert.False(numbers.ContainsKey(direct.Id));
        Assert.Equal((await h.Db.Sales.AsNoTracking().FirstAsync(s => s.Id == sale)).Reference, numbers[overSale.Id]);
        Assert.Equal(issued.Note.Number, numbers[overIssue.Id]);
        Assert.StartsWith("ISS-", numbers[overIssue.Id]);
    }

    [Fact]
    public async Task A_credit_note_is_read_by_id_with_its_lines_and_an_unknown_id_is_null()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync(qty: 10)).Value!;
        var note = (await h.Receivables.CreateCreditNoteAsync(
            invoice.Id, new CreateCreditNoteRequest("Quantité livrée en moins",
                Lines: [new CreditNoteLineRequest(invoice.Lines.Single().Id, 2)]), "admin")).Value!;

        var read = await h.Receivables.GetCreditNoteAsync(note.Id);
        Assert.NotNull(read);
        Assert.Equal(note.Number, read!.Number);
        Assert.Equal(2m, read.Lines.Single().Qty);
        Assert.Equal("Quantité livrée en moins", read.Reason);

        Assert.Null(await h.Receivables.GetCreditNoteAsync(Guid.NewGuid()));
    }
}
