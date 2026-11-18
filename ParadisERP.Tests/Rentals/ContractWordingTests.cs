using ParadisERP.Application.Catalog;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Rentals;
using ParadisERP.Tests.Receivables;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Rentals.RentalTestPaper;

namespace ParadisERP.Tests.Rentals;

/// <summary>
/// Several named rental-contract wordings (owner, PinSay #302, 2026-10-05: "enable multi contracts and
/// enhance the creation to be as template"). The company keeps a list of named wordings beside the
/// standard one; each is a document text under its own key, counted and kept in history like any
/// kind; a contract names the one it prints, chosen while it is a draft and carried by its extension.
/// </summary>
public class ContractWordingTests
{
    private const string Scaffolding = "rentalContract.scaf01";
    private const string Formwork = "rentalContract.form02";

    private static readonly Guid Algiers = ReceivableHarness.Algiers;
    private static readonly Guid Contractor = ReceivableHarness.Contractor;

    private static ContractWordingDto Wording(string key, string fr, string? ar = null) =>
        new(key, ar is null ? new Dictionary<string, string> { ["fr"] = fr } : new Dictionary<string, string> { ["fr"] = fr, ["ar"] = ar });

    private static StoreDocumentsDto Dto(
        IReadOnlyList<ContractWordingDto>? wordings, params (string Kind, string Lang, string Text)[] texts) =>
        new(texts.GroupBy(t => t.Kind).ToDictionary(
                g => g.Key, g => (IReadOnlyDictionary<string, string>)g.ToDictionary(t => t.Lang, t => t.Text)),
            ContractWordings: wordings);

    private static StoreDocumentTexts.ChangeAllowance Allowance(params (string Kind, int Used)[] used) =>
        new(3, 2026, used.ToDictionary(u => u.Kind, u => u.Used));

    // ---------- the list and its texts ----------

    [Fact]
    public void A_named_wording_is_stored_with_its_name_and_its_text_under_its_key()
    {
        var problem = StoreDocumentTexts.TryNormalize(
            Dto([Wording(Scaffolding, " Échafaudage ", "سقالات")],
                ("rentalContract", "fr", "Conditions générales"), (Scaffolding, "fr", " Conditions échafaudage ")),
            out var values);
        Assert.Null(problem);

        var wording = Assert.Single(values.Wordings!);
        Assert.Equal(Scaffolding, wording.Key);
        Assert.Equal("Échafaudage", wording.Names["fr"]);
        Assert.Equal("سقالات", wording.Names["ar"]);
        Assert.Equal("Conditions échafaudage", values.Texts![Scaffolding]["fr"]);

        var store = new Store();
        StoreDocumentTexts.Apply(store, values);
        var dto = StoreDocumentTexts.ToDto(store);
        Assert.Equal(Scaffolding, Assert.Single(dto.ContractWordings!).Key);
        Assert.Equal("Conditions échafaudage", dto.DocumentTexts[Scaffolding]["fr"]);
    }

    [Theory]
    [InlineData("rentalContract.", "is not a contract wording key")]
    [InlineData("rentalContract.UPPER", "is not a contract wording key")]
    [InlineData("invoice.scaf01", "is not a contract wording key")]
    [InlineData("rentalContract.abcdefghijklmnopq", "is not a contract wording key")]
    public void A_key_of_the_wrong_shape_is_refused(string key, string expected)
    {
        var problem = StoreDocumentTexts.TryNormalize(Dto([Wording(key, "Échafaudage")], (key, "fr", "x")), out _);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void A_wording_needs_a_name_and_a_text_and_is_listed_once()
    {
        Assert.Contains("needs a name",
            StoreDocumentTexts.TryNormalize(Dto([new ContractWordingDto(Scaffolding, new Dictionary<string, string> { ["fr"] = "  " })],
                (Scaffolding, "fr", "Conditions")), out _));
        // A name promises terms; a contract printed under it with none would carry no terms at all.
        Assert.Contains("\"Échafaudage\" has no text",
            StoreDocumentTexts.TryNormalize(Dto([Wording(Scaffolding, "Échafaudage")], (Scaffolding, "fr", "   ")), out _));
        Assert.Contains("listed twice",
            StoreDocumentTexts.TryNormalize(Dto([Wording(Scaffolding, "A"), Wording(Scaffolding, "B")], (Scaffolding, "fr", "x")), out _));
    }

    [Fact]
    public void A_text_under_a_wording_nobody_listed_is_refused()
    {
        var problem = StoreDocumentTexts.TryNormalize(Dto([], (Formwork, "fr", "Conditions coffrage")), out _);
        Assert.Contains("not one of this company's contract wordings", problem);
    }

    [Fact]
    public void An_older_screen_that_sends_no_list_keeps_the_wordings_and_their_texts()
    {
        var store = new Store
        {
            ContractWordings = [new ContractWording { Key = Scaffolding, Names = new() { ["fr"] = "Échafaudage" } }],
            DocumentTexts = new()
            {
                ["rentalContract"] = new() { ["fr"] = "Conditions générales" },
                [Scaffolding] = new() { ["fr"] = "Conditions échafaudage" },
            },
        };
        // The older screen sends the kinds it knows, and no list.
        var problem = StoreDocumentTexts.TryNormalize(
            Dto(null, ("rentalContract", "fr", "Conditions générales")), out var values, store.ContractWordings);
        Assert.Null(problem);

        // Nothing changed: the wording's text stood, so its allowance is untouched.
        Assert.Empty(StoreDocumentTexts.Decide(store.DocumentTexts, StoreDocumentTexts.TextsAfter(store, values), Allowance(),
            StoreDocumentTexts.WordingLabels(store.ContractWordings)));
        StoreDocumentTexts.Apply(store, values);
        Assert.Equal(Scaffolding, Assert.Single(store.ContractWordings!).Key);
        Assert.Equal("Conditions échafaudage", store.DocumentTexts![Scaffolding]["fr"]);
    }

    // ---------- the yearly allowance ----------

    [Fact]
    public void Each_wording_is_counted_on_its_own_and_a_refusal_names_it()
    {
        var stored = new Dictionary<string, Dictionary<string, string>>
        {
            ["rentalContract"] = new() { ["fr"] = "Conditions générales" },
            [Scaffolding] = new() { ["fr"] = "Ancien" },
        };
        var incoming = new Dictionary<string, Dictionary<string, string>>
        {
            ["rentalContract"] = new() { ["fr"] = "Conditions générales" },
            [Scaffolding] = new() { ["fr"] = "Nouveau" },
            [Formwork] = new() { ["fr"] = "Conditions coffrage" },
        };
        var labels = new Dictionary<string, string> { [Scaffolding] = "Échafaudage", [Formwork] = "Coffrage" };

        // The standard wording's allowance is spent; rewriting another wording does not touch it,
        // and adding one is that wording's first change.
        var decisions = StoreDocumentTexts.Decide(stored, incoming, Allowance(("rentalContract", 3)), labels);
        Assert.Equal([Scaffolding, Formwork], decisions.Select(d => d.Kind));
        Assert.All(decisions, d => Assert.Equal(StoreDocumentTexts.ChangeVerdict.Allowed, d.Verdict));
        Assert.Equal("Ancien", decisions[0].PreviousTexts["fr"]);
        Assert.Empty(decisions[1].PreviousTexts);

        var refused = Assert.Single(StoreDocumentTexts.Decide(stored, incoming, Allowance((Scaffolding, 3), (Formwork, 0)), labels)
            .Where(d => d.Verdict == StoreDocumentTexts.ChangeVerdict.Refused));
        Assert.Contains("rental contract \"Échafaudage\"", refused.Refusal);
    }

    [Fact]
    public void Removing_a_wording_is_a_change_that_keeps_its_last_text()
    {
        var stored = new Dictionary<string, Dictionary<string, string>> { [Scaffolding] = new() { ["fr"] = "Conditions échafaudage" } };
        var decision = Assert.Single(StoreDocumentTexts.Decide(stored, null, Allowance()));
        Assert.Equal(Scaffolding, decision.Kind);
        Assert.Equal("Conditions échafaudage", decision.PreviousTexts["fr"]);

        var store = new Store { ContractWordings = [new ContractWording { Key = Scaffolding, Names = new() { ["fr"] = "É" } }] };
        StoreDocumentTexts.TryNormalize(Dto([]), out var values, store.ContractWordings);
        Assert.Equal([Scaffolding], StoreDocumentTexts.RemovedWordings(store.ContractWordings, values));
    }

    [Fact]
    public void The_allowance_is_stated_for_each_wording_too()
    {
        var kinds = StoreDocumentTexts.KindsOf([new ContractWording { Key = Scaffolding, Names = new() { ["fr"] = "É" } }]);
        var allowances = StoreDocumentTexts.Allowances(3, 2026, DateTime.UtcNow, new Dictionary<string, int> { [Scaffolding] = 1 }, kinds);
        Assert.Equal(DocumentTextKinds.All.Count + 1, allowances.Count);
        Assert.Equal(2, allowances.Single(a => a.Kind == Scaffolding).Remaining);
    }

    // ---------- the contract names the wording it prints ----------

    private static async Task WithWordingsAsync(ReceivableHarness h)
    {
        var store = await h.Db.Stores.FirstAsync();
        store.ContractWordings =
        [
            new ContractWording { Key = Scaffolding, Names = new() { ["fr"] = "Échafaudage" } },
            new ContractWording { Key = Formwork, Names = new() { ["fr"] = "Coffrage" } },
        ];
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static UpsertRentalContractRequest Request(string? wordingKey) =>
        ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 10, 10, RentUnitPrice: 1_000m, DamageUnitPrice: 2_000m)],
            WordingKey: wordingKey));

    private static Task<RentalContract> StoredAsync(ReceivableHarness h, Guid id) =>
        h.Db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == id);

    [Fact]
    public async Task A_contract_prints_the_wording_chosen_for_it_and_the_standard_one_by_default()
    {
        await using var h = await ReceivableHarness.NewAsync();
        await WithWordingsAsync(h);

        var chosen = await h.Rentals.CreateContractAsync(Request(Scaffolding), "admin");
        Assert.True(chosen.Ok, chosen.Message);
        Assert.Equal(Scaffolding, (await StoredAsync(h, chosen.Value!.Id)).WordingKey);

        var standard = await h.Rentals.CreateContractAsync(Request(null), "admin");
        Assert.Null((await StoredAsync(h, standard.Value!.Id)).WordingKey);

        var unknown = await h.Rentals.CreateContractAsync(Request("rentalContract.nope"), "admin");
        Assert.Equal(RentalFailure.Invalid, unknown.Failure);
        Assert.Contains("does not exist", unknown.Message);
    }

    [Fact]
    public async Task A_draft_changes_its_wording_an_older_client_keeps_it_and_blank_is_the_standard_one()
    {
        await using var h = await ReceivableHarness.NewAsync();
        await WithWordingsAsync(h);
        var id = (await h.Rentals.CreateContractAsync(Request(Scaffolding), "admin")).Value!.Id;
        h.Db.ChangeTracker.Clear();

        Assert.True((await h.Rentals.UpdateContractAsync(id, Request(Formwork))).Ok);
        Assert.Equal(Formwork, (await StoredAsync(h, id)).WordingKey);
        h.Db.ChangeTracker.Clear();

        // An older client sends no wording at all: the contract keeps the one it has.
        Assert.True((await h.Rentals.UpdateContractAsync(id, Request(null))).Ok);
        Assert.Equal(Formwork, (await StoredAsync(h, id)).WordingKey);
        h.Db.ChangeTracker.Clear();

        Assert.True((await h.Rentals.UpdateContractAsync(id, Request(""))).Ok);
        Assert.Null((await StoredAsync(h, id)).WordingKey);
    }

    [Fact]
    public async Task An_extension_prints_the_wording_its_contract_was_signed_under()
    {
        await using var h = await ReceivableHarness.NewAsync();
        await WithWordingsAsync(h);
        var contract = (await h.Rentals.CreateContractAsync(Request(Scaffolding), "admin")).Value!;
        Assert.True((await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            Algiers, [new IssueNoteLineRequest(contract.Lines.Single().Id, 10)],
            DateTime.Parse("2026-03-01T09:00:00Z").ToUniversalTime())), null, "m")).Ok);

        var extended = await h.Rentals.ExtendAsNewAsync(contract.Id, new ExtendAsNewContractRequest(20), null, "gerant");
        Assert.True(extended.Ok, extended.Message);
        Assert.Equal(Scaffolding, (await StoredAsync(h, extended.Value!.Id)).WordingKey);
    }
}
