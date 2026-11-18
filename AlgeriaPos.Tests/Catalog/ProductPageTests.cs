using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Catalog;

/// <summary>
/// "I need to paginate in backend, not frontend" (owner, 2026-11-17): the products list is asked a
/// page at a time; the search (the till's fold), the filters and the sort run on the server over the
/// whole catalogue.
/// </summary>
[Collection(HostCollection.Name)]
public class ProductPageTests
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

    /// <summary>Panneau 1…23 at 100×n, every third without a barcode, every fifth inactive; one Arabic scaffold.</summary>
    private static async Task SeedAsync(PosHost host)
    {
        await using var db = Db(host);
        // Soft-deleted, not removed: stock rows may point at the seeded products.
        foreach (var seeded in db.Products) seeded.IsDeleted = true;
        for (var i = 1; i <= 23; i++)
        {
            db.Products.Add(new Product
            {
                Sku = $"PAN-{i}", NameFr = $"Panneau {i}", NameAr = $"لوح {i}", NameEn = $"Panel {i}",
                Price = 100m * i, TaxRate = i % 2 == 0 ? 9m : 19m,
                Barcode = i % 3 == 0 ? null : $"61300000{i:00}", IsActive = i % 5 != 0, TrackStock = i != 7,
            });
        }
        db.Products.Add(new Product { Sku = "SC-1", NameFr = "Échafaudage", NameAr = "سقالة", NameEn = "Scaffold", Price = 5m, TaxRate = 19m });
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> PageAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/catalog/products/page{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] Skus(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("sku").GetString()!).ToArray();

    [Fact]
    public async Task Ten_rows_first_and_the_total()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        await SeedAsync(host);

        var first = await PageAsync(client, "");
        Assert.Equal(24, first.GetProperty("total").GetInt32());
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        // No tax filter and so no rates (owner, 2026-11-18: the rate is applied when the invoice is made).
        Assert.Equal(JsonValueKind.Null, first.GetProperty("summary").ValueKind);
        Assert.Equal(4, (await PageAsync(client, "?skip=20&take=10")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_search_folds_arabic_and_accents_like_the_till()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        await SeedAsync(host);

        Assert.Equal(["SC-1"], Skus(await PageAsync(client, "?q=" + Uri.EscapeDataString("سقاله"))));
        Assert.Equal(["SC-1"], Skus(await PageAsync(client, "?q=echafaudage")));
        Assert.Equal(["PAN-11"], Skus(await PageAsync(client, "?q=6130000011")));
    }

    [Fact]
    public async Task The_filters_and_the_sort_run_over_the_whole_catalogue()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        await SeedAsync(host);

        Assert.Equal(4, (await PageAsync(client, "?status=inactive")).GetProperty("total").GetInt32());
        Assert.Equal(["PAN-7"], Skus(await PageAsync(client, "?stock=untracked")));
        Assert.Equal(8, (await PageAsync(client, "?barcode=no")).GetProperty("total").GetInt32()); // 3,6,…,21 and the scaffold

        // Numbers as numbers: Panneau 2 before Panneau 10; highest price first when asked.
        var byName = Skus(await PageAsync(client, "?sort=name&lang=fr&take=4"));
        Assert.Equal(["SC-1", "PAN-1", "PAN-2", "PAN-3"], byName);
        Assert.Equal(["PAN-23", "PAN-22"], Skus(await PageAsync(client, "?sort=price&dir=desc&take=2")).Take(2));
    }

    [Fact]
    public void The_fold_is_the_tills()
    {
        Assert.Equal("سقاله", ListText.Fold("سقالة"));
        Assert.Equal("اسمنت", ListText.Fold("إسمنت"));
        Assert.Equal("echafaudage 12", ListText.Fold(" Échafaudage ١٢ "));
        Assert.True(ListText.Compare("Panneau 2", "Panneau 10", "fr") < 0);
        Assert.True(ListText.Compare("panneau", "Panneau", "fr") == 0);
    }
}
