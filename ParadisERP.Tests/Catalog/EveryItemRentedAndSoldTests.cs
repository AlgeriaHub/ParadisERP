using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Catalog;

/// <summary>
/// Every item is rented and sold, from one stock (owner, 2026-10-06): there is no rental box any
/// more, so a save marks the item rentable whatever the request says, and the till receives it.
/// </summary>
[Collection(HostCollection.Name)]
public class EveryItemRentedAndSoldTests
{
    private static async Task<HttpClient> AdminAtTillAsync(PosHost host)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    [Fact]
    public async Task An_item_saved_as_not_rental_is_still_rented_and_sold()
    {
        using var host = new PosHost();
        using var admin = await AdminAtTillAsync(host);

        var created = await admin.PostAsJsonAsync("/api/catalog/products", new
        {
            sku = "BOTH-1", barcode = (string?)null, nameAr = "صنف", nameFr = "Article", nameEn = "Item",
            categoryId = (Guid?)null, price = 450m, costPrice = 0m, taxRate = 0m, unit = "pcs",
            trackStock = true, isActive = true, isRentable = false, assetUnitValue = 0m,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await using (var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options))
            Assert.True((await db.Products.SingleAsync(p => p.Sku == "BOTH-1")).IsRentable);

        // The till receives it, at its shelf price.
        var pulled = await admin.GetFromJsonAsync<JsonElement>("/api/sync/pull?since=0");
        var item = pulled.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("sku").GetString() == "BOTH-1");
        Assert.Equal(450m, item.GetProperty("price").GetDecimal());
    }
}
