using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Customers;

/// <summary>
/// A company customer's other fiscal identifiers — RC, AI, NIS — which the company's invoice prints
/// under the buyer (the owner's Facture N° 45, 2026-10-10). Read the way the company's and a
/// branch's are: profile keys only, trimmed, blanks dropped; none sent keeps what is stored, and a
/// person has none.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerFiscalIdTests
{
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

    /// <summary>The first save names the representative; an edit never does (the server answers 409).</summary>
    private static object Company(object? fiscalIds, bool firstSave = true) => new
    {
        fullName = "SARL BMLT CONSTRUCTION", phone = (string?)null, address = "Rue Ibn Khaldoun, Boufarik",
        taxId = "002609081352527", email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m,
        kind = "Company", representative = firstSave ? new { fullName = "Nadia Bouzid" } : null, fiscalIds,
    };

    private static object Person(object? fiscalIds) => new
    {
        fullName = "Karim Ben Ali", firstName = "Karim", familyName = "Ben Ali", phone = (string?)null,
        address = (string?)null, taxId = (string?)null, email = (string?)null, birthDate = (DateTime?)null,
        creditLimit = 0m, kind = "Person", fiscalIds,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_company_keeps_its_identifiers_trimmed_and_without_blanks_and_the_list_returns_them()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var created = await JsonAsync(await client.PostAsJsonAsync("/api/catalog/customers",
            Company(new Dictionary<string, string> { ["rc"] = " 26B0813525 00/09 ", ["ai"] = "21000478402", ["nis"] = "  " })),
            HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();

        var ids = created.GetProperty("fiscalIds");
        Assert.Equal("26B0813525 00/09", ids.GetProperty("rc").GetString());
        Assert.Equal("21000478402", ids.GetProperty("ai").GetString());
        Assert.False(ids.TryGetProperty("nis", out _));

        await using var db = Db(host);
        var stored = await db.Customers.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(2, stored.FiscalIds.Count);
    }

    [Fact]
    public async Task An_edit_that_sends_no_identifiers_leaves_them_as_they_are()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var id = (await JsonAsync(await client.PostAsJsonAsync("/api/catalog/customers",
            Company(new Dictionary<string, string> { ["rc"] = "26B0813525 00/09" })), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

        var edited = await JsonAsync(await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Company(null, firstSave: false)), HttpStatusCode.OK);

        Assert.Equal("26B0813525 00/09", edited.GetProperty("fiscalIds").GetProperty("rc").GetString());
    }

    [Fact]
    public async Task A_key_the_country_profile_does_not_define_is_refused()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var response = await client.PostAsJsonAsync("/api/catalog/customers",
            Company(new Dictionary<string, string> { ["siret"] = "123" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_person_has_no_fiscal_identifiers_whatever_is_sent()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var created = await JsonAsync(await client.PostAsJsonAsync("/api/catalog/customers",
            Person(new Dictionary<string, string> { ["rc"] = "26B0813525 00/09" })), HttpStatusCode.Created);

        Assert.Empty(created.GetProperty("fiscalIds").EnumerateObject());
    }
}
