using ParadisERP.Application.Contracts;
using ParadisERP.Application.Receivables;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Receivables;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Rentals.RentalTestPaper;

namespace ParadisERP.Tests.Receivables;

/// <summary>
/// The company's own invoice, Facture N° 45 (2026-10-10): quantity, daily price and days in their
/// own columns, the buyer's RC, NIF and AI under the name, and the way of paying in words.
/// </summary>
public class CompanyPaperTests
{
    [Fact]
    public async Task A_typed_hire_line_is_quantity_times_price_times_days_and_the_bill_adds_up()
    {
        await using var h = await ReceivableHarness.NewAsync();

        // The owner's Facture N° 45, line for line.
        var raised = await h.RaiseAsync(new CreateInvoiceRequest(
            DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            Lines:
            [
                new InvoiceLineRequest(null, null, "PIED DROIT 4,00 M", 955, 10m, 19m, Days: 30),
                new InvoiceLineRequest(null, null, "POTO 0,40*3,00 M", 12, 750m, 19m, Days: 1),
                new InvoiceLineRequest(null, null, "POTO 0,30*3,00 M", 10, 908m, 19m, Days: 10)
            ]));

        Assert.True(raised.Ok, raised.Message);
        var invoice = raised.Value!;
        Assert.Equal([286_500m, 9_000m, 90_800m], invoice.Lines.Select(l => l.LineNet));
        Assert.Equal([30, 1, 10], invoice.Lines.Select(l => l.Days ?? 0));
        // The N° column: the order entered, kept, so every print agrees.
        Assert.Equal([1, 2, 3], invoice.Lines.Select(l => l.Position));
        Assert.Equal(386_300m, invoice.SubTotalHt);
        Assert.Equal(73_397m, invoice.TaxTotal);
        Assert.Equal(459_697m, invoice.GrandTotalTtc);

        var posted = await h.PostedAsync(LedgerSources.Invoice);
        Assert.Equal(459_697m, posted[LedgerAccounts.Customers]);
    }

    [Fact]
    public async Task A_line_without_days_multiplies_by_one_as_every_line_did_before()
    {
        await using var h = await ReceivableHarness.NewAsync();

        var raised = await h.RaiseAsync(qty: 10, unitPrice: 12_000m);

        Assert.True(raised.Ok, raised.Message);
        Assert.Null(raised.Value!.Lines.Single().Days);
        Assert.Equal(120_000m, raised.Value.SubTotalHt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(ReceivableRules.MaxLineDays + 1)]
    public async Task Days_outside_one_to_the_maximum_are_refused(int days)
    {
        await using var h = await ReceivableHarness.NewAsync();

        var raised = await h.RaiseAsync(new CreateInvoiceRequest(
            DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            Lines: [new InvoiceLineRequest(null, null, "Étai", 5, 10m, 19m, Days: days)]));

        Assert.False(raised.Ok);
        Assert.Equal(ReceivableFailure.Invalid, raised.Failure);
    }

    [Fact]
    public async Task A_discount_may_take_the_whole_hire_line_and_no_more()
    {
        await using var h = await ReceivableHarness.NewAsync();

        // 5 × 10 × 3 = 150: a discount of 150 is the line, 151 is more than it.
        var whole = await h.RaiseAsync(new CreateInvoiceRequest(
            DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            Lines: [new InvoiceLineRequest(null, null, "Étai", 5, 10m, 19m, Discount: 100m, Days: 3)]));
        Assert.True(whole.Ok, whole.Message);
        Assert.Equal(50m, whole.Value!.Lines.Single().LineNet);

        var over = await h.RaiseAsync(new CreateInvoiceRequest(
            DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            Lines: [new InvoiceLineRequest(null, null, "Étai", 5, 10m, 19m, Discount: 151m, Days: 3)]));
        Assert.False(over.Ok);
    }

    [Fact]
    public async Task A_credit_note_inherits_the_days_and_gives_back_the_line_pro_rata()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync(new CreateInvoiceRequest(
            DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            Lines: [new InvoiceLineRequest(null, null, "PIED DROIT 4,00 M", 955, 10m, 19m, Days: 30)]))).Value!;

        var credited = await h.Receivables.CreateCreditNoteAsync(invoice.Id,
            new CreateCreditNoteRequest("retour partiel", Lines: [new CreditNoteLineRequest(invoice.Lines.Single().Id, 100)]), "admin");

        Assert.True(credited.Ok, credited.Message);
        var line = credited.Value!.Lines.Single();
        Assert.Equal(30, line.Days);
        Assert.Equal(10m, line.UnitPrice);
        Assert.Equal(30_000m, line.LineNet);   // 100 × 10 × 30
    }

    [Fact]
    public async Task The_buyers_fiscal_identifiers_are_frozen_on_the_invoice_at_issue()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var customer = await h.Db.Customers.SingleAsync(c => c.Id == ReceivableHarness.Contractor);
        customer.Kind = CustomerKind.Company;
        customer.TaxId = "002609081352527";
        customer.FiscalIds = new() { ["rc"] = "26B0813525 00/09", ["ai"] = "21000478402" };
        await h.Db.SaveChangesAsync();

        var invoice = (await h.RaiseAsync()).Value!;

        // A later edit of the customer must not move the paper already handed over.
        customer.FiscalIds = new() { ["rc"] = "changed" };
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var read = await h.Db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal("002609081352527", read.BuyerTaxId);
        Assert.Equal("26B0813525 00/09", read.BuyerFiscalIds!["rc"]);
        Assert.Equal("21000478402", read.BuyerFiscalIds["ai"]);
    }

    [Fact]
    public async Task The_way_of_paying_is_recorded_in_words_and_settles_nothing()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var request = ReceivableHarness.DirectInvoice() with { PaymentMethodKey = " Cheque " };

        var raised = await h.RaiseAsync(request);

        Assert.True(raised.Ok, raised.Message);
        Assert.Equal("cheque", raised.Value!.PaymentMethodKey);
        // Paper is not payment, and a word on the invoice is not even paper.
        Assert.Equal(raised.Value.GrandTotalTtc, await h.OutstandingAsync(raised.Value.Id));
    }

    [Theory]
    [InlineData("credit")]
    [InlineData("giftcard")]
    [InlineData("loyalty")]
    public async Task An_invoice_cannot_promise_a_tender_that_cannot_settle_an_account(string key)
    {
        await using var h = await ReceivableHarness.NewAsync();

        var raised = await h.RaiseAsync(ReceivableHarness.DirectInvoice() with { PaymentMethodKey = key });

        Assert.False(raised.Ok);
        Assert.Equal(ReceivableFailure.Invalid, raised.Failure);
    }

    [Fact]
    public async Task An_unknown_way_of_paying_is_refused()
    {
        await using var h = await ReceivableHarness.NewAsync();

        var raised = await h.RaiseAsync(ReceivableHarness.DirectInvoice() with { PaymentMethodKey = "bitcoin" });

        Assert.False(raised.Ok);
        Assert.Equal(ReceivableFailure.NotFound, raised.Failure);
    }

    [Fact]
    public async Task An_invoice_over_an_issue_note_prints_quantity_daily_price_and_days_in_their_own_columns()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 100, 30, RentUnitPrice: 100m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        var issued = (await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers,
            [new IssueNoteLineRequest(contract.Lines.Single().Id, 60)], DateTime.UtcNow)), null, "magasinier")).Value!;

        var invoice = (await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), IssueNoteId: issued.Note.Id), "admin")).Value!;

        var line = invoice.Lines.Single();
        Assert.Equal(60m, line.Qty);
        Assert.Equal(100m, line.UnitPrice);
        Assert.Equal(30, line.Days);
        Assert.Equal(180_000m, line.LineNet);
        Assert.Equal(line.Qty * line.UnitPrice * line.Days!.Value, line.LineNet);
        // The detail is in the columns now, not repeated in the name.
        Assert.DoesNotContain("/jour", line.NameSnapshot);
        Assert.Equal(180_000m, invoice.GrandTotalTtc);
        Assert.Equal(0m, invoice.PrepaidAmount);
    }
}
