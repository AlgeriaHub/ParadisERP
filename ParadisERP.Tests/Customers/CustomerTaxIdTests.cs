using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.API.Endpoints;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Customers;

/// <summary>
/// "الرقم الجبائي للشركات فقط" (owner, 2026-10-05): the customer's tax number is a company's; a
/// natural person has none. A person is refused a new or changed one with a stable code, the
/// store's "require a tax number" asks it of companies only, and a person saved with one before
/// the rule keeps it until somebody clears it — never wiped silently, never changed.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerTaxIdTests
{
    private const string Nif = "000116001234567";

    private static async Task<HttpClient> SignedInAsync(PosHost host)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static async Task RequireTaxIdAsync(PosHost host)
    {
        await using var db = Db(host);
        var store = await db.Stores.SingleAsync(s => !s.IsDeleted);
        store.RequireCustomerTaxId = true;
        await db.SaveChangesAsync();
    }

    private static object Person(string code, string? taxId, string? phone = null) => new
    {
        code, fullName = "Karim Ben Ali", phone, address = (string?)null, taxId,
        email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person",
    };

    private static object Company(string code, string? taxId, bool withRepresentative = true) => new
    {
        code, fullName = "SARL El Injaz Travaux", phone = (string?)null, address = (string?)null, taxId,
        email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Company",
        representative = withRepresentative ? new { fullName = "Nadia Bouzid" } : null,
    };

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AssertCompanyOnlyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("taxIdCompanyOnly", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(CatalogEndpoints.TaxIdCompanyOnlyMessage, body.RootElement.GetProperty("detail").GetString());
    }

    private static async Task<Customer> StoredAsync(PosHost host, Guid id)
    {
        await using var db = Db(host);
        return await db.Customers.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    // ---------- create ----------

    [Fact]
    public async Task A_person_created_with_a_tax_number_is_refused_with_a_code_and_nothing_is_saved()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        await AssertCompanyOnlyAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person("CT-100", Nif)));

        await using var db = Db(host);
        Assert.False(await db.Customers.AnyAsync());
    }

    [Fact]
    public async Task A_person_needs_no_tax_number_even_when_the_store_requires_one()
    {
        using var host = new PosHost();
        await RequireTaxIdAsync(host);
        using var client = await SignedInAsync(host);

        var id = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person("CT-110", "  ")));
        // Blank is none: stored as null, not as spaces.
        Assert.Null((await StoredAsync(host, id)).TaxId);
    }

    [Fact]
    public async Task A_company_is_asked_for_its_tax_number_when_the_store_requires_one()
    {
        using var host = new PosHost();
        await RequireTaxIdAsync(host);
        using var client = await SignedInAsync(host);

        var missing = await client.PostAsJsonAsync("/api/catalog/customers", Company("CT-120", null));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("tax ID", await missing.Content.ReadAsStringAsync());

        var id = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/customers", Company("CT-120", $" {Nif} ")));
        Assert.Equal(Nif, (await StoredAsync(host, id)).TaxId);
    }

    // ---------- a person saved with one before the rule ----------

    [Fact]
    public async Task A_legacy_persons_number_is_kept_by_an_edit_cleared_on_request_and_never_changed()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var id = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person("CT-200", null)));
        // What production may hold: a person saved with a tax number before 2026-10-05.
        await using (var db = Db(host))
        {
            var row = await db.Customers.SingleAsync(c => c.Id == id);
            row.TaxId = Nif;
            await db.SaveChangesAsync();
        }

        // A phone fix that sends the number back as it is goes through and keeps it.
        var kept = await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("CT-200", Nif, phone: "+213 555 10 20 30"));
        Assert.True(kept.StatusCode == HttpStatusCode.OK, await kept.Content.ReadAsStringAsync());
        var afterKeep = await StoredAsync(host, id);
        Assert.Equal(Nif, afterKeep.TaxId);
        // Stored as a mobile in international form since 2026-11-17 (PhoneNumbers).
        Assert.Equal("+213555102030", afterKeep.Phone);

        // A different number is a new one, and a person takes none.
        await AssertCompanyOnlyAsync(await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("CT-200", "000116009999999")));
        Assert.Equal(Nif, (await StoredAsync(host, id)).TaxId);

        // Sending it blank is how it goes.
        var cleared = await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("CT-200", null));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await StoredAsync(host, id)).TaxId);

        // And once gone it cannot come back.
        await AssertCompanyOnlyAsync(await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("CT-200", Nif)));
    }

    // ---------- a company becoming a person ----------

    [Fact]
    public async Task A_company_made_a_person_must_clear_its_tax_number_in_the_same_save()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var id = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/customers", Company("CT-300", Nif)));

        await AssertCompanyOnlyAsync(await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("CT-300", Nif)));
        var refused = await StoredAsync(host, id);
        Assert.Equal(CustomerKind.Company, refused.Kind);
        Assert.Equal(Nif, refused.TaxId);

        var turned = await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("CT-300", null));
        Assert.True(turned.StatusCode == HttpStatusCode.OK, await turned.Content.ReadAsStringAsync());
        var person = await StoredAsync(host, id);
        Assert.Equal(CustomerKind.Person, person.Kind);
        Assert.Null(person.TaxId);
    }
}
