using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Catalog;

/// <summary>
/// A warehouse holds the stock and sends it to the branches; it does not sell (the owner,
/// 2026-10-05). So no till is registered on one or moved onto one. A till already there — written
/// before the rule, recreated here directly in the database — can still be renamed, switched off,
/// moved away, and still signs in: the rule lives where tills are set up, never where a sale is made.
/// </summary>
[Collection(HostCollection.Name)]
public class WarehouseTillTests
{
    private static async Task<HttpClient> AdminAsync(PosHost host, string deviceCode = "C01")
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static async Task<(Guid Main, Guid Selling)> BranchesAsync(HttpClient admin)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var main = branches.EnumerateArray().Single(b => b.GetProperty("isMain").GetBoolean() && b.GetProperty("isWarehouse").GetBoolean()).GetProperty("id").GetGuid();
        var created = await admin.PostAsJsonAsync("/api/catalog/branches", new
        {
            nameAr = "أولاد موسى", nameFr = "Ouled Moussa", nameEn = "Ouled Moussa", address = "", phone = "",
            isMain = false, allowNegativeStock = false, isActive = true,
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return (main, body.RootElement.GetProperty("id").GetGuid());
    }

    /// <summary>A till left on a warehouse from before the rule: written straight into the database.</summary>
    private static async Task PutOnWarehouseAsync(PosHost host, string code, Guid warehouse)
    {
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);
        (await db.Devices.SingleAsync(d => d.Code == code)).BranchId = warehouse;
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> DeviceAsync(HttpClient admin, string code) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/devices"))
            .EnumerateArray().Single(d => d.GetProperty("code").GetString() == code);

    private static async Task AssertWarehouseRefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(DevicesEndpoints.WarehouseTillCode, body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_new_till_on_the_main_warehouse_is_refused_and_on_a_selling_branch_is_registered()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var (main, selling) = await BranchesAsync(admin);

        await AssertWarehouseRefusalAsync(
            await admin.PostAsJsonAsync("/api/devices", new { code = "W01", name = "Entrepôt", branchId = main, isActive = true }));
        Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>("/api/devices")).EnumerateArray(),
            d => d.GetProperty("code").GetString() == "W01");

        var created = await admin.PostAsJsonAsync("/api/devices", new { code = "C02", name = "Ouled Moussa 1", branchId = selling, isActive = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.False((await DeviceAsync(admin, "C02")).GetProperty("onWarehouse").GetBoolean());
    }

    [Fact]
    public async Task Moving_a_till_onto_the_main_warehouse_is_refused()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var (main, selling) = await BranchesAsync(admin);
        var created = await admin.PostAsJsonAsync("/api/devices", new { code = "C02", name = "Ouled Moussa 1", branchId = selling, isActive = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await DeviceAsync(admin, "C02")).GetProperty("id").GetGuid();

        await AssertWarehouseRefusalAsync(
            await admin.PutAsJsonAsync($"/api/devices/{id}", new { code = "C02", name = "Ouled Moussa 1", branchId = main, isActive = true }));
        Assert.Equal(selling, (await DeviceAsync(admin, "C02")).GetProperty("branchId").GetGuid());
    }

    [Fact]
    public async Task A_till_already_on_the_warehouse_is_flagged_can_be_renamed_switched_off_and_moved_away()
    {
        using var host = new PosHost();
        using var first = await AdminAsync(host);
        var (main, selling) = await BranchesAsync(first);
        // Work from a till on the selling branch: switching C01 off ends the session opened on it.
        Assert.Equal(HttpStatusCode.Created,
            (await first.PostAsJsonAsync("/api/devices", new { code = "C02", name = "Ouled Moussa 1", branchId = selling, isActive = true })).StatusCode);
        using var admin = await AdminAsync(host, "C02");

        // C01 left on the warehouse, as every company's first till was before 2026-10-05.
        await PutOnWarehouseAsync(host, "C01", main);
        var c01 = await DeviceAsync(admin, "C01");
        Assert.Equal(main, c01.GetProperty("branchId").GetGuid());
        Assert.True(c01.GetProperty("onWarehouse").GetBoolean());
        var id = c01.GetProperty("id").GetGuid();

        var renamed = await admin.PutAsJsonAsync($"/api/devices/{id}", new { code = "C01", name = "Ancienne caisse", branchId = main, isActive = false });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        c01 = await DeviceAsync(admin, "C01");
        Assert.Equal("Ancienne caisse", c01.GetProperty("name").GetString());
        Assert.False(c01.GetProperty("isActive").GetBoolean());

        var moved = await admin.PutAsJsonAsync($"/api/devices/{id}", new { code = "C01", name = "Ouled Moussa 2", branchId = selling, isActive = true });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.False((await DeviceAsync(admin, "C01")).GetProperty("onWarehouse").GetBoolean());
    }

    [Fact]
    public async Task A_till_already_on_the_warehouse_still_signs_in()
    {
        // Never block the sale: the rule is not checked when an existing till opens a session.
        using var host = new PosHost();
        using (var first = await AdminAsync(host))
        {
            var (main, _) = await BranchesAsync(first);
            await PutOnWarehouseAsync(host, "C01", main);
        }
        using var admin = await AdminAsync(host);
        Assert.True((await DeviceAsync(admin, "C01")).GetProperty("onWarehouse").GetBoolean());
    }
}
