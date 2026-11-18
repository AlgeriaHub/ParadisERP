using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Tests.Hosting;

namespace ParadisERP.Tests.Rentals;

/// <summary>
/// Calling a contract off before its exit note (owner, 2026-10-06): "if there is an exit note the
/// contract can't be cancelled", and once cancelled it is hidden from everyone but the Admin. And,
/// with one stock for renting and selling, a till's pull sees what an exit note took.
/// </summary>
[Collection(HostCollection.Name)]
public class ContractCancelTests
{
    private const string Password = "Essai@2026";

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

    private sealed record Setup(Guid Branch, Guid Customer, Guid Site, Guid Product);

    private static async Task<Setup> SetupAsync(HttpClient admin)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var branch = branches.EnumerateArray()
            .Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean()).GetProperty("id").GetGuid();
        var customer = (await JsonAsync(await admin.PostAsJsonAsync("/api/catalog/customers", new
        {
            code = "CANCEL-01", fullName = "Client annulation", phone = (string?)null, address = (string?)null, taxId = (string?)null,
            email = (string?)null, birthDate = new DateTime(1980, 3, 4), creditLimit = 0m, kind = "Person",
            idDocumentNumber = "109 876 543",
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var site = (await JsonAsync(await admin.PostAsJsonAsync($"/api/customers/{customer}/sites", new { name = "Chantier" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var product = (await JsonAsync(await admin.PostAsJsonAsync("/api/catalog/products", new
        {
            sku = "ECH-C1", nameAr = "سقالة", nameFr = "Échafaudage", nameEn = "Scaffold",
            price = 900m, costPrice = 0m, taxRate = 0m, unit = "u", trackStock = true, isActive = true,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await admin.PostAsJsonAsync($"/api/catalog/products/{product}/stock",
            new { branchId = branch, qtyDelta = 100m, reason = "opening", reorderLevel = (decimal?)null }), HttpStatusCode.OK);
        return new Setup(branch, customer, site, product);
    }

    private static async Task<JsonElement> ContractAsync(HttpClient client, Setup s, decimal qty = 5) =>
        await JsonAsync(await client.PostAsJsonAsync("/api/rentals/contracts", new
        {
            customerId = s.Customer, branchId = s.Branch, siteId = s.Site,
            lines = new[] { new { productId = s.Product, qty, durationDays = 30, rentUnitPrice = 50m, damageUnitPrice = 900m } },
        }), HttpStatusCode.Created);

    private static Task<HttpResponseMessage> IssueAsync(HttpClient client, Setup s, JsonElement contract, decimal qty) =>
        client.PostAsJsonAsync($"/api/rentals/contracts/{contract.GetProperty("id").GetGuid()}/issue-notes", new
        {
            branchId = s.Branch, customerTransport = true,
            lines = new[] { new { contractLineId = contract.GetProperty("lines")[0].GetProperty("id").GetGuid(), qty } },
        });

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, Guid id, string? reason) =>
        client.PostAsJsonAsync($"/api/rentals/contracts/{id}/cancel", new { reason });

    [Fact]
    public async Task A_contract_with_no_exit_note_is_cancelled_with_a_reason_and_then_only_the_admin_sees_it()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var s = await SetupAsync(admin);
        await TestUsers.CreateAsync(host, admin, "caissier-loc", "Manager", s.Branch, Password);
        using var clerk = await SignedInAsync(host, host.SignInName("caissier-loc"), Password);

        // Raised and called off by the same clerk, who holds rentals.create but not rentals.cancelled.
        var contract = await ContractAsync(clerk, s);
        var id = contract.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await CancelAsync(clerk, id, "  ")).StatusCode);
        var cancelled = await JsonAsync(await CancelAsync(clerk, id, "Client changed his mind"), HttpStatusCode.OK);
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("Client changed his mind", cancelled.GetProperty("cancelReason").GetString());
        Assert.False(string.IsNullOrEmpty(cancelled.GetProperty("cancelledBy").GetString()));

        // Gone for the clerk: not in any list, and not by id.
        foreach (var path in new[] { "/api/rentals/contracts", "/api/rentals/contracts?status=Cancelled", "/api/rentals/contracts?status=Open" })
        {
            var rows = await JsonAsync(await clerk.GetAsync(path), HttpStatusCode.OK);
            Assert.DoesNotContain(rows.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.GetAsync($"/api/rentals/contracts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.GetAsync($"/api/rentals/contracts/{id}/settlement")).StatusCode);
        // Nor offered to a receipt or a voucher.
        var choices = await JsonAsync(await admin.GetAsync($"/api/receivables/contracts?customerId={s.Customer}"), HttpStatusCode.OK);
        Assert.DoesNotContain(choices.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);

        // The Admin still sees it, with who, when and why.
        var listed = await JsonAsync(await admin.GetAsync("/api/rentals/contracts?status=Cancelled"), HttpStatusCode.OK);
        var row = Assert.Single(listed.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal("Client changed his mind", row.GetProperty("cancelReason").GetString());
        var read = await JsonAsync(await admin.GetAsync($"/api/rentals/contracts/{id}"), HttpStatusCode.OK);
        Assert.Equal("Cancelled", read.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("cancelledAtUtc").ValueKind);

        // And it takes nothing more: no exit note, no edit, no second cancel.
        Assert.Equal(HttpStatusCode.Conflict, (await IssueAsync(admin, s, read, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(admin, id, "again")).StatusCode);
    }

    [Fact]
    public async Task A_contract_with_an_exit_note_is_not_cancelled()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var s = await SetupAsync(admin);
        var contract = await ContractAsync(admin, s);
        await JsonAsync(await IssueAsync(admin, s, contract, 2), HttpStatusCode.Created);

        var refused = await CancelAsync(admin, contract.GetProperty("id").GetGuid(), "Too late");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("exit note", await refused.Content.ReadAsStringAsync());
        var read = await JsonAsync(await admin.GetAsync($"/api/rentals/contracts/{contract.GetProperty("id").GetGuid()}"), HttpStatusCode.OK);
        Assert.Equal("Open", read.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_till_pull_sees_the_stock_an_exit_note_took()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var s = await SetupAsync(admin);
        var contract = await ContractAsync(admin, s, qty: 40);
        var before = await admin.GetFromJsonAsync<JsonElement>("/api/sync/pull?since=0");
        var cursor = before.GetProperty("cursor").GetInt64();

        await JsonAsync(await IssueAsync(admin, s, contract, 40), HttpStatusCode.Created);

        // 100 on the shelf, 40 out on rent: the till may sell 60, and is told so on its next pull.
        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/sync/pull?since={cursor}");
        var item = after.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == s.Product);
        Assert.Equal(60m, item.GetProperty("stockQty").GetDecimal());
    }
}
