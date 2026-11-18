using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Rentals;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Rentals.RentalTestPaper;

namespace ParadiERP.Tests.Rentals;

/// <summary>
/// A person customer signs himself or through an agent (owner, 2026-10-06, reversing answer 11 for
/// persons). Himself: his record is the paper and the first issue note does not ask for his
/// identity. An agent: the name and identity are the agent's, as typed, and the first note asks
/// for the document and its number. A company keeps signing through its representative.
/// </summary>
public partial class RentalDocumentTests
{
    [Fact]
    public async Task An_agent_signs_with_his_own_name_and_paper_not_the_customer_s()
    {
        await using var h = await NewAsync();
        var created = await h.Rentals.CreateContractAsync(
            Contract() with
            {
                SignedByAgent = true, SignatoryName = " Son frère ", SignatoryIdType = "passport", SignatoryIdNumber = "P 000 111",
            }, "admin");
        Assert.True(created.Ok, created.Message);

        var stored = await StoredAsync(h, created.Value!.Id);
        Assert.True(stored.SignedByAgent);
        Assert.Equal("Son frère", stored.SignatoryName);
        Assert.Equal(("passport", "P 000 111"), (stored.SignatoryIdType, stored.SignatoryIdNumber));
        // Nothing of the customer's own record rides along with the agent.
        Assert.Null(stored.SignatoryIdIssuer);
        // The request's own birth date, never the customer's on file (1980-03-04).
        Assert.NotEqual(new DateOnly(1980, 3, 4), stored.SignatoryBirthDate);

        var issued = await h.Rentals.IssueAsync(created.Value.Id, Issue(created.Value.Lines.Single().Id), null, null);
        Assert.True(issued.Ok, issued.Message);
    }

    [Fact]
    public async Task An_agent_with_no_paper_is_a_draft_refused_at_the_first_note()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(
            Contract() with { SignedByAgent = true, SignatoryName = "Son frère" }, "admin")).Value!;

        var refused = await h.Rentals.IssueAsync(contract.Id, Issue(contract.Lines.Single().Id), null, null);
        Assert.Equal(RentalFailure.Invalid, refused.Failure);
        Assert.StartsWith("The signatory's identity is incomplete: the identity document type and its number are missing.", refused.Message);

        var nameless = (await h.Rentals.CreateContractAsync(Contract() with { SignedByAgent = true }, "admin")).Value!;
        var noName = await h.Rentals.IssueAsync(nameless.Id, Issue(nameless.Lines.Single().Id), null, null);
        Assert.Equal(RentalFailure.Invalid, noName.Failure);
        Assert.StartsWith("This contract has no signatory.", noName.Message);
    }

    [Fact]
    public async Task A_person_who_signs_himself_is_not_asked_for_an_identity_his_record_lacks()
    {
        await using var h = await NewAsync();
        var customerId = Guid.NewGuid();
        h.Db.Customers.Add(new Customer { Id = customerId, Code = "C010", FullName = "Sans Papiers", Kind = CustomerKind.Person });
        var siteId = Guid.NewGuid();
        h.Db.CustomerSites.Add(Site(siteId, customerId));
        await h.Db.SaveChangesAsync();

        var contract = (await h.Rentals.CreateContractAsync(Contract(customerId: customerId) with { SiteId = siteId }, "admin")).Value!;
        var stored = await StoredAsync(h, contract.Id);
        Assert.False(stored.SignedByAgent);
        Assert.Equal("Sans Papiers", stored.SignatoryName);
        Assert.Null(stored.SignatoryIdNumber);

        var issued = await h.Rentals.IssueAsync(contract.Id, Issue(contract.Lines.Single().Id), null, null);
        Assert.True(issued.Ok, issued.Message);
    }

    [Fact]
    public async Task A_company_has_no_agent_its_representative_signs()
    {
        await using var h = await NewAsync();
        var created = await h.Rentals.CreateContractAsync(Contract(customerId: Company) with { SignedByAgent = true }, "admin");
        Assert.True(created.Ok, created.Message);

        var stored = await StoredAsync(h, created.Value!.Id);
        Assert.False(stored.SignedByAgent);
        Assert.Equal("Nadia Bouzid", stored.SignatoryName);
    }

    [Fact]
    public async Task An_extension_keeps_the_agent_who_signed()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(
            Contract() with { SignedByAgent = true, SignatoryName = "Son frère", SignatoryIdType = "passport", SignatoryIdNumber = "P 000 111" },
            "admin")).Value!;
        Assert.True((await h.Rentals.IssueAsync(contract.Id, Issue(contract.Lines.Single().Id), null, null)).Ok);

        var extended = await h.Rentals.ExtendAsNewAsync(contract.Id, new ExtendAsNewContractRequest(20, Day("2026-03-05")), null, "gerant");
        Assert.True(extended.Ok, extended.Message);
        var second = await StoredAsync(h, extended.Value!.Id);
        Assert.True(second.SignedByAgent);
        Assert.Equal("Son frère", second.SignatoryName);
    }
}
