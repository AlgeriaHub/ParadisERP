using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Tests.Hosting;

namespace AlgeriaPos.Tests.Rentals;

[Collection(HostCollection.Name)]
public class QuantityStatementEndpointTests
{
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

    private sealed record Setup(Guid Branch, Guid Customer, Guid Site, Guid Product, JsonElement Contract);

    private static async Task<Setup> SetupAsync(HttpClient admin)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var branch = branches.EnumerateArray()
            .Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean())
            .GetProperty("id").GetGuid();
        var customer = (await JsonAsync(await admin.PostAsJsonAsync("/api/catalog/customers", new
        {
            code = "QTY-01", fullName = "Client quantite", phone = (string?)null, address = (string?)null, taxId = (string?)null,
            email = (string?)null, birthDate = new DateTime(1980, 3, 4), creditLimit = 0m, kind = "Person",
            idDocumentNumber = "109 876 543",
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var site = (await JsonAsync(await admin.PostAsJsonAsync($"/api/customers/{customer}/sites", new { name = "Chantier" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var product = (await JsonAsync(await admin.PostAsJsonAsync("/api/catalog/products", new
        {
            sku = "QTY-ECH", nameAr = "سقالة", nameFr = "Échafaudage", nameEn = "Scaffold",
            price = 900m, costPrice = 0m, taxRate = 0m, unit = "u", trackStock = true, isActive = true,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await admin.PostAsJsonAsync($"/api/catalog/products/{product}/stock",
            new { branchId = branch, qtyDelta = 100m, reason = "opening", reorderLevel = (decimal?)null }), HttpStatusCode.OK);

        var contract = await JsonAsync(await admin.PostAsJsonAsync("/api/rentals/contracts", new
        {
            customerId = customer, branchId = branch, siteId = site,
            lines = new[] { new { productId = product, qty = 20m, durationDays = 30, rentUnitPrice = 50m, damageUnitPrice = 900m } },
        }), HttpStatusCode.Created);

        var contractId = contract.GetProperty("id").GetGuid();
        var contractLineId = contract.GetProperty("lines")[0].GetProperty("id").GetGuid();

        var issue = await admin.PostAsJsonAsync($"/api/rentals/contracts/{contractId}/issue-notes", new
        {
            branchId = branch, customerTransport = true,
            lines = new[] { new { contractLineId, qty = 20m } },
        });
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);

        return new Setup(branch, customer, site, product, contract);
    }

    [Fact]
    public async Task Current_quantities_returns_items_and_contracts_as_of_today()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var s = await SetupAsync(admin);

        var response = await admin.GetAsync($"/api/rentals/quantity-statement?customerId={s.Customer}&current=true");
        var statement = await JsonAsync(response, HttpStatusCode.OK);

        Assert.True(statement.GetProperty("current").GetBoolean());
        var items = statement.GetProperty("items").EnumerateArray().ToList();
        var item = Assert.Single(items);
        Assert.Equal(20m, item.GetProperty("remaining").GetDecimal());
        Assert.Equal(20m, item.GetProperty("issued").GetDecimal());

        var contracts = statement.GetProperty("contracts").EnumerateArray().ToList();
        var contract = Assert.Single(contracts);
        Assert.Equal(s.Contract.GetProperty("number").GetString(), contract.GetProperty("number").GetString());
    }

    [Fact]
    public async Task Past_window_before_issue_returns_empty_items()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var s = await SetupAsync(admin);

        var fromStr = DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var toStr = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var response = await admin.GetAsync($"/api/rentals/quantity-statement?customerId={s.Customer}&from={fromStr}&to={toStr}");
        var statement = await JsonAsync(response, HttpStatusCode.OK);

        Assert.Equal(0, statement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Invalid_query_parameters_are_refused_or_not_found()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var s = await SetupAsync(admin);

        // Missing to without current -> 400
        var missingTo = await admin.GetAsync($"/api/rentals/quantity-statement?customerId={s.Customer}&from=2026-03-01");
        Assert.Equal(HttpStatusCode.BadRequest, missingTo.StatusCode);

        // to before from -> 400
        var toBeforeFrom = await admin.GetAsync($"/api/rentals/quantity-statement?customerId={s.Customer}&from=2026-03-10&to=2026-03-01");
        Assert.Equal(HttpStatusCode.BadRequest, toBeforeFrom.StatusCode);

        // status=bogus -> 400
        var bogusStatus = await admin.GetAsync($"/api/rentals/quantity-statement?customerId={s.Customer}&current=true&status=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bogusStatus.StatusCode);

        // unknown customer -> 404
        var unknownCustomer = await admin.GetAsync($"/api/rentals/quantity-statement?customerId={Guid.NewGuid()}&current=true");
        Assert.Equal(HttpStatusCode.NotFound, unknownCustomer.StatusCode);
    }

    [Fact]
    public async Task Account_statement_reads_plain_dates_in_company_zone()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var s = await SetupAsync(admin);

        var response = await admin.GetAsync($"/api/receivables/statement?customerId={s.Customer}&from=2026-01-01&to=2026-01-31");
        var statement = await JsonAsync(response, HttpStatusCode.OK);

        var fromDate = statement.GetProperty("from").GetDateTime().ToUniversalTime();
        var toDate = statement.GetProperty("to").GetDateTime().ToUniversalTime();

        Assert.Equal(31, (toDate - fromDate).TotalDays);
        if (fromDate == new DateTime(2025, 12, 31, 23, 0, 0, DateTimeKind.Utc))
        {
            Assert.Equal(new DateTime(2026, 1, 31, 23, 0, 0, DateTimeKind.Utc), toDate);
        }
    }

    [Fact]
    public async Task Export_totals_equal_the_screen()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var s = await SetupAsync(admin);

        // A window around today wide enough that the company's zone cannot move the issue out of it.
        // Invariant: on a Hijri-calendar machine (ar-SA) `yyyy` would otherwise write the year as 1448.
        var query = string.Create(CultureInfo.InvariantCulture,
            $"customerId={s.Customer}&from={DateTime.UtcNow.AddDays(-5):yyyy-MM-dd}&to={DateTime.UtcNow.AddDays(5):yyyy-MM-dd}");
        var screen = await JsonAsync(await admin.GetAsync($"/api/rentals/quantity-statement?{query}"), HttpStatusCode.OK);
        var summary = screen.GetProperty("summary");

        var export = await admin.GetAsync($"/api/rentals/quantity-statement/export?{query}&lang=fr");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(AlgeriaPos.Infrastructure.Reports.XlsxWriter.ContentType, export.Content.Headers.ContentType?.MediaType);
        var sheet = AlgeriaPos.Infrastructure.Catalog.XlsxReader.Read(await export.Content.ReadAsStreamAsync());

        var totals = sheet.Rows[^1];
        Assert.Equal("Total", totals[1]!.Text);
        Assert.Equal(summary.GetProperty("issued").GetDecimal(), totals[4]!.Number);
        Assert.Equal(summary.GetProperty("returned").GetDecimal(), totals[5]!.Number);
        Assert.Equal(summary.GetProperty("withCustomer").GetDecimal(), totals[8]!.Number);
        Assert.Equal(summary.GetProperty("value").GetDecimal(), totals[10]!.Number);
        Assert.Equal(20m, totals[4]!.Number);
        Assert.True(summary.GetProperty("value").GetDecimal() > 0);
    }
}
