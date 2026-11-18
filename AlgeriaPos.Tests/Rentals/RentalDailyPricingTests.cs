using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using static AlgeriaPos.Tests.Rentals.RentalTestPaper;

namespace AlgeriaPos.Tests.Rentals;

public partial class RentalDocumentTests
{
    [Fact]
    public async Task Daily_rates_agree_on_created_edited_contract_issue_invoice_source_and_ledger()
    {
        await using var h = await NewAsync();
        var request = Contract() with
        {
            Lines = [new RentalContractLineRequest(Scaffold, 1, 1, 5, 2000)]
        };
        var created = await h.Rentals.CreateContractAsync(request, "admin");
        Assert.True(created.Ok, created.Message);
        var id = created.Value!.Id;
        var docs = (await h.Rentals.ReadAsync(id))!;
        Assert.Equal(5m, h.Rentals.ContractLineRent(docs, docs.Lines.Single()));

        var edited = await h.Rentals.UpdateContractAsync(id, request with
        {
            Lines = [new RentalContractLineRequest(Scaffold, 100, 30, 5, 2000),
                new RentalContractLineRequest(Coupler, 2, 10, 100, 500)]
        });
        Assert.True(edited.Ok, edited.Message);
        docs = (await h.Rentals.ReadAsync(id))!;
        Assert.Equal([15000m, 2000m], docs.Lines.Select(l => h.Rentals.ContractLineRent(docs, l)).OrderDescending());
        Assert.Equal(17000m, docs.Lines.Sum(l => h.Rentals.ContractLineRent(docs, l)));

        var issued = await h.Rentals.IssueAsync(id, Driven(new IssueNoteRequest(Algiers,
            docs.Lines.Select(l => new IssueNoteLineRequest(l.Id, l.Qty)).ToList(), Day("2026-03-01"))), null, "admin");
        Assert.True(issued.Ok, issued.Message);
        Assert.Equal(17000m, issued.Value!.PeriodRent);
        Assert.Equal(17000m, h.Rentals.PeriodRent(docs, issued.Value.Note));
        var billing = (await h.Rentals.IssueNoteBillingAsync(issued.Value.Note.Id))!;
        Assert.Equal(17000m, billing.Sum(l => l.Net));
        // Priced by the day: quantity × daily price × days is each line, with the days on it.
        Assert.All(billing, l => Assert.Equal(l.Net, l.Qty * l.UnitPrice * l.Days!.Value));
        var posted = await h.PostedAsync(LedgerSources.RentalIssue);
        Assert.Equal(17000m, posted[LedgerAccounts.Customers]);
    }
}
