using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Tests.Hosting;

namespace AlgeriaPos.Tests.Rentals;

/// <summary>
/// The routes the owner's paperwork answers added, through the real HTTP pipeline: a customer's
/// sites, the day cutoff hour on the rental settings, and the identity scan kept with a contract
/// (multipart in, the file streamed back with its own type, removed before the lock only).
/// </summary>
[Collection(HostCollection.Name)]
public class RentalPaperEndpointTests
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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CustomerAsync(HttpClient client, string code) =>
        (await JsonAsync(await client.PostAsJsonAsync("/api/catalog/customers", new
        {
            code, fullName = "Client " + code, phone = (string?)null, address = (string?)null, taxId = (string?)null,
            email = (string?)null, birthDate = new DateTime(1980, 3, 4), creditLimit = 0m, kind = "Person",
            idDocumentNumber = "109 876 543",
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    [Fact]
    public async Task Contract_create_and_edit_return_server_calculated_daily_totals_ignoring_client_amounts()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var customer = await CustomerAsync(client, "DAILY-01");
        var site = (await JsonAsync(await client.PostAsJsonAsync($"/api/customers/{customer}/sites", new { name = "Daily site" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var branch = (await JsonAsync(await client.GetAsync("/api/catalog/branches"), HttpStatusCode.OK))
            .EnumerateArray().First().GetProperty("id").GetGuid();
        var product = (await JsonAsync(await client.PostAsJsonAsync("/api/catalog/products", new
        {
            sku = "DAILY-01", nameAr = "مولد", nameFr = "Générateur", nameEn = "Generator",
            price = 0m, costPrice = 0m, taxRate = 0m, unit = "u", trackStock = true, isActive = true,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        object Request(decimal qty, int days, decimal dailyRate) => new
        {
            customerId = customer, branchId = branch, siteId = site, rentalTotal = 1m,
            lines = new[] { new { productId = product, qty, durationDays = days, rentUnitPrice = dailyRate,
                damageUnitPrice = 2000m, rentalTotal = 1m, fullPeriodPrice = 1m } }
        };
        var created = await JsonAsync(await client.PostAsJsonAsync("/api/rentals/contracts", Request(100, 30, 5)), HttpStatusCode.Created);
        Assert.Equal(15000m, created.GetProperty("rentalTotal").GetDecimal());
        Assert.Equal(15000m, created.GetProperty("lines")[0].GetProperty("rentalTotal").GetDecimal());
        var id = created.GetProperty("id").GetGuid();
        var edited = await JsonAsync(await client.PutAsJsonAsync($"/api/rentals/contracts/{id}", Request(2, 30, 1000)), HttpStatusCode.OK);
        Assert.Equal(60000m, edited.GetProperty("rentalTotal").GetDecimal());
        Assert.Equal(60000m, edited.GetProperty("lines")[0].GetProperty("rentalTotal").GetDecimal());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync($"/api/rentals/contracts/{id}", Request(0, 30, 1000))).StatusCode);
        var retained = await JsonAsync(await client.GetAsync($"/api/rentals/contracts/{id}"), HttpStatusCode.OK);
        Assert.Equal(60000m, retained.GetProperty("rentalTotal").GetDecimal());
        Assert.Equal(2m, retained.GetProperty("lines")[0].GetProperty("qty").GetDecimal());
    }

    [Fact]
    public async Task A_customer_s_sites_are_listed_created_and_edited()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var customer = await CustomerAsync(client, "SITE-01");

        var created = await JsonAsync(await client.PostAsJsonAsync($"/api/customers/{customer}/sites",
            new { name = "  Chantier 120 logements ", address = " Zone industrielle ", phone = "" }), HttpStatusCode.Created);
        Assert.Equal("Chantier 120 logements", created.GetProperty("name").GetString());
        Assert.Equal("Zone industrielle", created.GetProperty("address").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("phone").ValueKind);
        Assert.True(created.GetProperty("isActive").GetBoolean());
        var siteId = created.GetProperty("id").GetGuid();

        await JsonAsync(await client.PostAsJsonAsync($"/api/customers/{customer}/sites", new { name = "Chantier Bab Ezzouar" }),
            HttpStatusCode.Created);

        var closed = await JsonAsync(await client.PutAsJsonAsync($"/api/customers/{customer}/sites/{siteId}",
            new { name = "Chantier 120 logements", address = "Zone industrielle", isActive = false }), HttpStatusCode.OK);
        Assert.False(closed.GetProperty("isActive").GetBoolean());

        // Open sites first; the closed one stays listed, because a contract may still name it.
        var list = await JsonAsync(await client.GetAsync($"/api/customers/{customer}/sites"), HttpStatusCode.OK);
        Assert.Equal(["Chantier Bab Ezzouar", "Chantier 120 logements"],
            list.EnumerateArray().Select(s => s.GetProperty("name").GetString()));

        var blank = await client.PostAsJsonAsync($"/api/customers/{customer}/sites", new { name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("needs a name", await blank.Content.ReadAsStringAsync());

        var unknown = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/customers/{unknown}/sites")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"/api/customers/{unknown}/sites", new { name = "X" })).StatusCode);
        // Another customer's site is not reachable through this one.
        var other = await CustomerAsync(client, "SITE-02");
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/customers/{other}/sites/{siteId}", new { name = "X" })).StatusCode);
    }

    [Fact]
    public async Task The_day_cutoff_hour_travels_on_the_rental_settings()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var saved = await JsonAsync(await client.PutAsJsonAsync("/api/rentals/settings",
            new { overdueMultiplier = 1.5m, depositEnabled = false, taxRate = 0m, dayCutoffHour = 12 }), HttpStatusCode.OK);
        Assert.Equal(12, saved.GetProperty("dayCutoffHour").GetInt32());
        Assert.Equal(12, (await JsonAsync(await client.GetAsync("/api/rentals/settings"), HttpStatusCode.OK))
            .GetProperty("dayCutoffHour").GetInt32());

        var refused = await client.PutAsJsonAsync("/api/rentals/settings",
            new { overdueMultiplier = 1.5m, depositEnabled = false, taxRate = 0m, dayCutoffHour = 24 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("between 0 and 23", await refused.Content.ReadAsStringAsync());

        var off = await JsonAsync(await client.PutAsJsonAsync("/api/rentals/settings",
            new { overdueMultiplier = 1.5m, depositEnabled = false, taxRate = 0m }), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, off.GetProperty("dayCutoffHour").ValueKind);
    }

    [Fact]
    public async Task The_identity_scan_goes_up_as_multipart_comes_back_with_its_type_and_leaves_before_the_lock()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var customer = await CustomerAsync(client, "SCAN-01");
        var site = (await JsonAsync(await client.PostAsJsonAsync($"/api/customers/{customer}/sites", new { name = "Chantier" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var branch = (await JsonAsync(await client.GetAsync("/api/catalog/branches"), HttpStatusCode.OK))
            .EnumerateArray().First().GetProperty("id").GetGuid();
        var product = (await JsonAsync(await client.PostAsJsonAsync("/api/catalog/products", new
        {
            sku = "ECH-01", barcode = (string?)null, nameAr = "سقالة", nameFr = "Échafaudage", nameEn = "Scaffold",
            categoryId = (Guid?)null, price = 0m, costPrice = 1200m, taxRate = 0m, unit = "u", trackStock = true, isActive = true,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var contract = await JsonAsync(await client.PostAsJsonAsync("/api/rentals/contracts", new
        {
            customerId = customer, branchId = branch, siteId = site,
            lines = new[] { new { productId = product, qty = 5, durationDays = 30, rentUnitPrice = 3000m, damageUnitPrice = 2000m } },
        }), HttpStatusCode.Created);
        var contractId = contract.GetProperty("id").GetGuid();
        Assert.Equal("Chantier", contract.GetProperty("siteName").GetString());
        Assert.Equal("سقالة", contract.GetProperty("lines")[0].GetProperty("nameAr").GetString());

        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];
        using var form = new MultipartFormDataContent
        {
            { new StringContent("idDocument"), "kind" },
            { new ByteArrayContent(jpeg), "file", "permis.jpg" },
        };
        var uploaded = await JsonAsync(await client.PostAsync($"/api/rentals/contracts/{contractId}/attachments", form), HttpStatusCode.Created);
        Assert.Equal("image/jpeg", uploaded.GetProperty("contentType").GetString());
        var attachmentId = uploaded.GetProperty("id").GetGuid();

        // Listed on the contract, metadata only.
        var read = await JsonAsync(await client.GetAsync($"/api/rentals/contracts/{contractId}"), HttpStatusCode.OK);
        Assert.Equal("permis.jpg", read.GetProperty("attachments")[0].GetProperty("fileName").GetString());

        var file = await client.GetAsync($"/api/rentals/contracts/{contractId}/attachments/{attachmentId}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal(jpeg, await file.Content.ReadAsByteArrayAsync());

        using var notAFile = new MultipartFormDataContent { { new StringContent("idDocument"), "kind" } };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync($"/api/rentals/contracts/{contractId}/attachments", notAFile)).StatusCode);

        using var text = new MultipartFormDataContent
        {
            { new StringContent("idDocument"), "kind" },
            { new ByteArrayContent("hello"u8.ToArray()), "file", "carte.jpg" },
        };
        var refused = await client.PostAsync($"/api/rentals/contracts/{contractId}/attachments", text);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("JPEG, PNG or WebP", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/rentals/contracts/{contractId}/attachments/{attachmentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/rentals/contracts/{contractId}/attachments/{attachmentId}")).StatusCode);
    }
}
