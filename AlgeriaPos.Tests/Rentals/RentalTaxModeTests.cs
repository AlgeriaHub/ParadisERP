using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Rentals;
using AlgeriaPos.Tests.Hosting;
using AlgeriaPos.Tests.Receivables;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Rentals.RentalTestPaper;

namespace AlgeriaPos.Tests.Rentals;

/// <summary>
/// VAT is optional when the bill is made, rental bills included (owner, 2026-11-02). A rental contract is taxed
/// or untaxed: its lines freeze the company's hire rate or nothing, and every bill raised on it follows. Making one
/// untaxed needs the untaxed-invoice permission, like an untaxed invoice; taking it back to taxed does not.
/// </summary>
[Collection(HostCollection.Name)]
public class RentalTaxModeTests
{
    private const decimal Rate = 19m;
    private const string Password = "Essai@2026";

    private static UpsertRentalContractRequest Contract(string? taxMode, decimal qty = 10) => ContractorSited(new UpsertRentalContractRequest(
        ReceivableHarness.Contractor, ReceivableHarness.Algiers,
        [new RentalContractLineRequest(ReceivableHarness.Scaffold, qty, 30, RentUnitPrice: 100m, DamageUnitPrice: 2_000m)],
        TaxMode: taxMode));

    private static async Task<IssueOutcome> IssueAllAsync(ReceivableHarness h, RentalContract contract)
    {
        var line = contract.Lines.Single();
        var issued = await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            ReceivableHarness.Algiers, [new IssueNoteLineRequest(line.Id, line.Qty)], DateTime.UtcNow)), null, "magasinier");
        Assert.True(issued.Ok, issued.Message);
        return issued.Value!;
    }

    private static async Task<RentalContractLine> LineAsync(ReceivableHarness h, Guid contractId) =>
        await h.Db.RentalContractLines.AsNoTracking().SingleAsync(l => l.ContractId == contractId);

    [Fact]
    public async Task An_untaxed_contract_freezes_no_rate_and_bills_without_VAT_while_a_taxed_one_takes_the_company_rate()
    {
        await using var h = await ReceivableHarness.NewAsync(rentalTaxRate: Rate);

        // Not naming it is taxed, at the company's hire rate, as every contract was before the choice.
        var taxed = await h.Rentals.CreateContractAsync(Contract(null), "admin");
        Assert.True(taxed.Ok, taxed.Message);
        Assert.Equal(TaxMode.Taxed, taxed.Value!.TaxMode);
        Assert.Equal(Rate, (await LineAsync(h, taxed.Value.Id)).TaxRate);

        var untaxed = await h.Rentals.CreateContractAsync(Contract("untaxed"), "admin");
        Assert.True(untaxed.Ok, untaxed.Message);
        Assert.Equal(TaxMode.Untaxed, untaxed.Value!.TaxMode);
        Assert.Equal(0m, (await LineAsync(h, untaxed.Value.Id)).TaxRate);

        // Goods out on the untaxed one: the customer owes the rent and nothing on top, and no tax is collected.
        var issued = await IssueAllAsync(h, untaxed.Value);
        Assert.Equal(30_000m, issued.PeriodRent);
        Assert.Equal(30_000m, await h.TradeDebtAsync());
        Assert.Equal(0m, (await h.PostedAsync()).GetValueOrDefault(LedgerAccounts.TaxCollected));

        // Its invoice is an untaxed one.
        var invoice = await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow, IssueNoteId: issued.Note.Id), "admin");
        Assert.True(invoice.Ok, invoice.Message);
        Assert.Equal(TaxMode.Untaxed, invoice.Value!.TaxMode);
        Assert.Equal(0m, invoice.Value.TaxTotal);

        // And the taxed one, issued beside it, collects the tax at the rate it froze.
        await IssueAllAsync(h, taxed.Value);
        Assert.Equal(-5_700m, (await h.PostedAsync()).GetValueOrDefault(LedgerAccounts.TaxCollected));
        Assert.Equal(30_000m + 35_700m, await h.TradeDebtAsync());

        // A mode that is neither is refused, not guessed.
        var odd = await h.Rentals.CreateContractAsync(Contract("Exempt"), "admin");
        Assert.Equal(RentalFailure.Invalid, odd.Failure);
    }

    /// <summary>
    /// The owner (2026-11-05): VAT only when the customer asks for a tax invoice, and every seller decides when the
    /// contract is made. With or without VAT is open to whoever creates or edits the draft, no permission either way;
    /// an edit that does not name the mode, from an older screen, keeps the stored one.
    /// </summary>
    [Fact]
    public async Task Whoever_makes_the_contract_chooses_with_or_without_VAT_and_may_change_it_while_it_is_a_draft()
    {
        await using var h = await ReceivableHarness.NewAsync(rentalTaxRate: Rate);

        var draft = (await h.Rentals.CreateContractAsync(Contract("Untaxed"), "vendeur")).Value!;
        Assert.Equal((TaxMode.Untaxed, 0m), (draft.TaxMode, (await LineAsync(h, draft.Id)).TaxRate));

        // The customer asks for a tax invoice after all: ticked, at the rate in force now.
        Assert.True((await h.Rentals.UpdateContractAsync(draft.Id, Contract("Taxed"))).Ok);
        Assert.Equal(Rate, (await LineAsync(h, draft.Id)).TaxRate);

        // And unticked again; an edit of anything else that does not name the mode keeps it.
        Assert.True((await h.Rentals.UpdateContractAsync(draft.Id, Contract("Untaxed", qty: 12))).Ok);
        Assert.True((await h.Rentals.UpdateContractAsync(draft.Id, Contract(null, qty: 14))).Ok);
        var kept = await LineAsync(h, draft.Id);
        Assert.Equal((14m, 0m), (kept.Qty, kept.TaxRate));
        Assert.Equal(TaxMode.Untaxed, (await h.Db.RentalContracts.AsNoTracking().SingleAsync(c => c.Id == draft.Id)).TaxMode);
    }

    [Fact]
    public async Task The_first_issue_note_freezes_the_choice_and_an_extension_carries_it()
    {
        await using var h = await ReceivableHarness.NewAsync(rentalTaxRate: Rate);
        var contract = (await h.Rentals.CreateContractAsync(Contract("Untaxed"), "admin")).Value!;
        await IssueAllAsync(h, contract);

        var locked = await h.Rentals.UpdateContractAsync(contract.Id, Contract("Taxed"));
        Assert.Equal(RentalFailure.Conflict, locked.Failure);

        var extension = await h.Rentals.ExtendAsNewAsync(contract.Id, new ExtendAsNewContractRequest(30), null, "admin");
        Assert.True(extension.Ok, extension.Message);
        Assert.Equal(TaxMode.Untaxed, extension.Value!.TaxMode);
        Assert.Equal(0m, (await LineAsync(h, extension.Value.Id)).TaxRate);
    }

    /// <summary>
    /// The accountant, through the owner (2026-11-05): 19 % on the whole rental bill (REN-01). On a taxed contract a
    /// return note's charges for condition — the repair (707), the wrecked and the missing units (757) — carry VAT at
    /// the rate the line froze, exactly as the rent did; on an untaxed contract none of them does.
    /// </summary>
    [Theory]
    [InlineData("Taxed", 2_090)]
    [InlineData("Untaxed", 0)]
    public async Task A_return_note_charges_VAT_on_repair_wrecked_and_missing_units_at_the_lines_rate(string mode, int expectedTax)
    {
        await using var h = await ReceivableHarness.NewAsync(rentalTaxRate: Rate);
        // A one-day hire, so the return gives no unused days back and the note's charges are all there is to tax.
        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 10, 1, RentUnitPrice: 100m, DamageUnitPrice: 2_000m)],
            TaxMode: mode)), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        await IssueAllAsync(h, contract);

        // Back the same day, used: 4 sound, 3 repairable with 5,000 of repair, 2 wrecked and 1 never seen again, at
        // 2,000 a unit.
        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            ReceivableHarness.Algiers,
            [new ReturnNoteLineRequest(lineId, QtyOk: 4, QtyPartialDamage: 3, MaintenanceValue: 5_000m, QtyTotalDamage: 2, QtyMissing: 1,
                SameDayUsage: "Used")],
            DateTime.UtcNow)), null, "magasinier");
        Assert.True(back.Ok, back.Message);
        Assert.Equal(0m, back.Value!.Note.UnusedRent);

        var charged = back.Value.Charges.Where(c => c.IsPosted).ToList();
        Assert.Equal(5_000m, charged.Where(c => c.Kind == RentalChargeKind.Maintenance).Sum(c => c.Amount));
        Assert.Equal(4_000m, charged.Where(c => c.Kind == RentalChargeKind.Damage).Sum(c => c.Amount));
        Assert.Equal(2_000m, charged.Where(c => c.Kind == RentalChargeKind.Missing).Sum(c => c.Amount));
        Assert.Equal(11_000m, charged.Sum(c => c.Amount));

        // 19 % of the 11,000 the note charges, on a taxed contract; nothing on an untaxed one.
        var posted = await h.PostedAsync(LedgerSources.RentalReturn);
        Assert.Equal(-(decimal)expectedTax, posted.GetValueOrDefault(LedgerAccounts.TaxCollected));
        // The income lines themselves stay net: repair on 707, wrecked and missing on 757; the customer owes the gross.
        Assert.Equal(-5_000m, posted.GetValueOrDefault(LedgerAccounts.MaintenanceIncome));
        Assert.Equal(-6_000m, posted.GetValueOrDefault(LedgerAccounts.DamageCompensation));
        Assert.Equal(11_000m + expectedTax, posted.GetValueOrDefault(LedgerAccounts.Customers));
    }

    // ---------- over HTTP ----------

    private static async Task<HttpClient> SignedInAsync(PosHost host, string username, string password)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password, deviceCode = "C01" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_manager_without_the_untaxed_permission_makes_an_untaxed_contract_over_HTTP_as_the_admin_does()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        await JsonAsync(await admin.PutAsJsonAsync("/api/rentals/settings",
            new UpdateRentalSettingsRequest(1.5m, false, TaxRate: Rate)), HttpStatusCode.OK);

        var branch = (await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches")).EnumerateArray()
            .Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean()).GetProperty("id").GetGuid();
        var customer = (await JsonAsync(await admin.PostAsJsonAsync("/api/catalog/customers", new
        {
            code = "TVA-01", fullName = "Client TVA", phone = (string?)null, address = (string?)null, taxId = (string?)null,
            email = (string?)null, birthDate = new DateTime(1980, 3, 4), creditLimit = 0m, kind = "Person",
            idDocumentNumber = "109 876 544",
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var site = (await JsonAsync(await admin.PostAsJsonAsync($"/api/customers/{customer}/sites", new { name = "Chantier" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var product = (await JsonAsync(await admin.PostAsJsonAsync("/api/catalog/products", new
        {
            sku = "ECH-TVA", nameAr = "سقالة", nameFr = "Échafaudage", nameEn = "Scaffold",
            price = 900m, costPrice = 0m, taxRate = 0m, unit = "u", trackStock = true, isActive = true,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        object Body(string taxMode) => new
        {
            customerId = customer, branchId = branch, siteId = site, taxMode,
            lines = new[] { new { productId = product, qty = 5m, durationDays = 30, rentUnitPrice = 50m, damageUnitPrice = 900m } },
        };

        await TestUsers.CreateAsync(host, admin, "gerant-tva", "Manager", branch, Password);
        using var manager = await SignedInAsync(host, host.SignInName("gerant-tva"), Password);

        // Every seller decides (owner, 2026-11-05): a manager without finance.invoices.untaxed leaves VAT off too.
        var managersUntaxed = await JsonAsync(await manager.PostAsJsonAsync("/api/rentals/contracts", Body("Untaxed")), HttpStatusCode.Created);
        Assert.Equal("Untaxed", managersUntaxed.GetProperty("taxMode").GetString());
        Assert.Equal(0m, managersUntaxed.GetProperty("lines")[0].GetProperty("taxRate").GetDecimal());

        var managers = await JsonAsync(await manager.PostAsJsonAsync("/api/rentals/contracts", Body("Taxed")), HttpStatusCode.Created);
        Assert.Equal("Taxed", managers.GetProperty("taxMode").GetString());
        Assert.Equal(Rate, managers.GetProperty("lines")[0].GetProperty("taxRate").GetDecimal());

        var admins = await JsonAsync(await admin.PostAsJsonAsync("/api/rentals/contracts", Body("Untaxed")), HttpStatusCode.Created);
        Assert.Equal("Untaxed", admins.GetProperty("taxMode").GetString());
        Assert.Equal(0m, admins.GetProperty("lines")[0].GetProperty("taxRate").GetDecimal());
    }
}
