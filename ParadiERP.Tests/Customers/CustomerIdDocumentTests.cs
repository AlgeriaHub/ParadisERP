using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Application.Customers;
using ParadiERP.Tests.Hosting;

namespace ParadiERP.Tests.Customers;

/// <summary>
/// The identity paper of a person customer and of a company's representative (owner, 2026-09-30,
/// answer 10): which paper — national card, driving licence or passport — the number, who issued
/// it and when. Optional here; the rental contract is where they become mandatory, and it reads
/// them from these rows, so they must come back exactly as they were saved.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerIdDocumentTests
{
    // ---------- the rule, pure ----------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("nationalCard", "nationalCard")]
    [InlineData(" DRIVINGLICENCE ", "drivingLicence")]
    [InlineData("passport", "passport")]
    public void The_type_is_one_of_three_keys_or_nothing(string? typed, string? stored)
    {
        Assert.Null(IdDocumentTypes.Problem(typed, null));
        Assert.Equal(stored, IdDocumentTypes.Normalize(typed));
    }

    [Fact]
    public void Any_other_type_or_an_issuer_past_its_column_is_refused_in_words()
    {
        Assert.Contains("nationalCard, drivingLicence, passport", IdDocumentTypes.Problem("carte", null));
        Assert.Contains("120", IdDocumentTypes.Problem(null, new string('x', 121)));
    }

    // ---------- the endpoints ----------

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

    private static object Person(string code, string? type = "drivingLicence") => new
    {
        code, fullName = "Karim Ben Ali", phone = (string?)null, address = (string?)null, taxId = (string?)null,
        email = (string?)null, birthDate = new DateTime(1985, 3, 14), creditLimit = 0m, kind = "Person",
        idDocumentNumber = " 109876543 ", idDocumentType = type, idDocumentIssuer = " Daïra de Rouiba ",
        idDocumentIssuedOn = "2019-06-02",
    };

    private static object Company(string code, string? repType = "passport", string? ownType = "nationalCard") => new
    {
        code, fullName = "SARL El Injaz Travaux", phone = (string?)null, address = (string?)null, taxId = "000116001234567",
        email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Company",
        // A company has no paper of its own: whatever is sent here is cleared, never stored.
        idDocumentNumber = "should-not-stay", idDocumentType = ownType, idDocumentIssuer = "nobody",
        idDocumentIssuedOn = "2020-01-01",
        representative = new
        {
            fullName = "Nadia Bouzid", idDocumentNumber = "118765432", phone = (string?)null, note = "gérante",
            idDocumentType = repType, idDocumentIssuer = "APC Bab Ezzouar", idDocumentIssuedOn = "2021-11-30",
        },
    };

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ListedAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/catalog/customers"))
            .EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task A_persons_paper_is_stored_trimmed_and_read_back_whole()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var id = (await CreatedAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person("CL-100")))).GetProperty("id").GetGuid();

        var row = await ListedAsync(client, id);
        Assert.Equal("109876543", row.GetProperty("idDocumentNumber").GetString());
        Assert.Equal("drivingLicence", row.GetProperty("idDocumentType").GetString());
        Assert.Equal("Daïra de Rouiba", row.GetProperty("idDocumentIssuer").GetString());
        Assert.Equal("2019-06-02", row.GetProperty("idDocumentIssuedOn").GetString());
        // The birth date the contract also asks for is still the customer's own field.
        Assert.StartsWith("1985-03-14", row.GetProperty("birthDate").GetString());

        // An edit that says no type clears it, as it clears a blank number.
        var edit = await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("CL-100", type: null));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ListedAsync(client, id)).GetProperty("idDocumentType").ValueKind);
    }

    [Fact]
    public async Task An_unknown_type_is_refused_on_create_and_on_edit()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var refused = await client.PostAsJsonAsync("/api/catalog/customers", Person("CL-101", type: "carteGrise"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("identity document type", await refused.Content.ReadAsStringAsync());

        var id = (await CreatedAsync(await client.PostAsJsonAsync("/api/catalog/customers", Person("CL-101")))).GetProperty("id").GetGuid();
        var edit = await client.PutAsJsonAsync($"/api/catalog/customers/{id}", Person("CL-101", type: "visa"));
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
        Assert.Equal("drivingLicence", (await ListedAsync(client, id)).GetProperty("idDocumentType").GetString());
    }

    [Fact]
    public async Task A_company_keeps_no_paper_of_its_own_and_its_representative_keeps_theirs()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var created = await CreatedAsync(await client.PostAsJsonAsync("/api/catalog/customers", Company("CL-200")));
        var id = created.GetProperty("id").GetGuid();

        var row = await ListedAsync(client, id);
        foreach (var field in new[] { "idDocumentNumber", "idDocumentType", "idDocumentIssuer", "idDocumentIssuedOn" })
            Assert.Equal(JsonValueKind.Null, row.GetProperty(field).ValueKind);

        var rep = row.GetProperty("representative");
        Assert.Equal("passport", rep.GetProperty("idDocumentType").GetString());
        Assert.Equal("APC Bab Ezzouar", rep.GetProperty("idDocumentIssuer").GetString());
        Assert.Equal("2021-11-30", rep.GetProperty("idDocumentIssuedOn").GetString());
    }

    [Fact]
    public async Task A_representative_with_an_unknown_type_is_refused_and_nothing_is_saved()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var refused = await client.PostAsJsonAsync("/api/catalog/customers", Company("CL-201", repType: "badge"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        // By name: the code in the body is never stored (the server draws one).
        var all = await client.GetFromJsonAsync<JsonElement>("/api/catalog/customers");
        Assert.DoesNotContain(all.EnumerateArray(), c => c.GetProperty("fullName").GetString() == "SARL El Injaz Travaux");
    }

    [Fact]
    public async Task A_change_of_representative_carries_the_new_persons_paper_into_the_history()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var id = (await CreatedAsync(await client.PostAsJsonAsync("/api/catalog/customers", Company("CL-202")))).GetProperty("id").GetGuid();

        var bad = await client.PostAsJsonAsync($"/api/catalog/customers/{id}/representatives", new
        {
            fullName = "Samir Haddad", idDocumentType = "permis",
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var changed = await client.PostAsJsonAsync($"/api/catalog/customers/{id}/representatives", new
        {
            fullName = "Samir Haddad", idDocumentNumber = "200123456", idDocumentType = "nationalCard",
            idDocumentIssuer = "Commune d'Alger-Centre", idDocumentIssuedOn = "2022-05-10",
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var history = (await changed.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(2, history.Count);

        var current = history.Single(r => r.GetProperty("toUtc").ValueKind == JsonValueKind.Null);
        Assert.Equal("nationalCard", current.GetProperty("idDocumentType").GetString());
        Assert.Equal("Commune d'Alger-Centre", current.GetProperty("idDocumentIssuer").GetString());
        Assert.Equal("2022-05-10", current.GetProperty("idDocumentIssuedOn").GetString());
        // The one who left keeps the paper they signed under.
        var previous = history.Single(r => r.GetProperty("toUtc").ValueKind != JsonValueKind.Null);
        Assert.Equal("passport", previous.GetProperty("idDocumentType").GetString());
    }
}
