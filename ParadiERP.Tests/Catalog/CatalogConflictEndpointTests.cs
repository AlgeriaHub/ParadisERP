using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Tests.Hosting;

namespace ParadiERP.Tests.Catalog;

/// <summary>
/// Renaming a product to a SKU another product holds. The unique index always refused it, so no
/// duplicate was ever stored — but as an unhandled 500, which the back office can only show as
/// "something failed". The create route already answered 409 with the value in the sentence; the
/// edit route now says the same. A customer's code is no longer typed at all (owner, 2026-10-05):
/// the server draws it and an edit keeps it, so the customer case is pinned the other way round.
/// </summary>
[Collection(HostCollection.Name)]
public class CatalogConflictEndpointTests
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

    private static object Product(string sku) => new
    {
        sku, barcode = (string?)null, nameAr = "أنبوب", nameFr = "Tube", nameEn = "Tube",
        categoryId = (Guid?)null, price = 2000m, costPrice = 1200m, taxRate = 19m, unit = "u",
        trackStock = true, isActive = true,
    };

    private static object Customer(string code) => new
    {
        code, fullName = "Client " + code, phone = (string?)null, address = (string?)null, taxId = (string?)null,
        email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person",
    };

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage created)
    {
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Editing_a_product_onto_another_product_s_SKU_is_a_conflict_in_words()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/products", Product("TUBE-50")));
        var second = await CreatedIdAsync(await client.PostAsJsonAsync("/api/catalog/products", Product("TUBE-60")));

        var clash = await client.PutAsJsonAsync($"/api/catalog/products/{second}", Product("TUBE-50"));

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("TUBE-50", await clash.Content.ReadAsStringAsync());

        // Keeping its own SKU is not a clash with itself.
        var kept = await client.PutAsJsonAsync($"/api/catalog/products/{second}", Product("TUBE-60"));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    [Fact]
    public async Task Editing_a_customer_with_another_customer_s_code_keeps_its_own_and_is_no_clash()
    {
        // Since 2026-10-05 the server draws a customer's code and an edit never changes it (owner),
        // so another customer's code in the body is ignored rather than refused: there is no clash
        // left to report, and the index never sees one.
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var first = await client.PostAsJsonAsync("/api/catalog/customers", Customer("CL-001"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstCode = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
        var second = await client.PostAsJsonAsync("/api/catalog/customers", Customer("CL-002"));
        var created = await second.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = created.GetProperty("id").GetGuid();
        var secondCode = created.GetProperty("code").GetString();

        var edit = await client.PutAsJsonAsync($"/api/catalog/customers/{secondId}", Customer(firstCode!));

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(secondCode, (await edit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
