using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Stock;

/// <summary>
/// A branch can buy from suppliers itself (owner, 2026-10-12). Receiving previously landed at the
/// main warehouse only and had no branch. Now a receiving note carries a branch: a branch-locked
/// user receives into their own branch, the Admin names any non-deleted branch, and naming none
/// lands at the main warehouse as before.
/// </summary>
[Collection(HostCollection.Name)]
public class ReceivingBranchTests
{
    private const string Password = "Essai@2026";

    private static async Task<HttpClient> SignedInAsync(PosHost host, string? username = null, string? password = null, string? deviceCode = "C01")
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = username is null ? PosHost.BootstrapUsername : host.SignInName(username),
            password = password ?? PosHost.BootstrapPassword,
            deviceCode,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> ProductAsync(PosHost host)
    {
        await using var db = Db(host);
        var product = new Product { Sku = "TUBE-48", NameFr = "Tube 48", Price = 1_000m, CostPrice = 500m };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string name, object? openingBalance = null) =>
        (await JsonAsync(await client.PostAsJsonAsync("/api/suppliers", new { name, phone = "0550 00 00 00", openingBalance }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<Guid> WarehouseAsync(HttpClient admin)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        return branches.EnumerateArray()
            .Single(b => b.GetProperty("isMain").GetBoolean() && b.GetProperty("isWarehouse").GetBoolean())
            .GetProperty("id").GetGuid();
    }

    private static async Task<Guid> BranchAsync(HttpClient admin, string name = "Oran")
    {
        var response = await admin.PostAsJsonAsync("/api/catalog/branches", new
        {
            nameAr = name == "Oran" ? "وهران" : name,
            nameFr = name,
            nameEn = name,
            address = "",
            phone = "",
            isMain = false,
            allowNegativeStock = false,
            isActive = true,
        });
        var created = await JsonAsync(response, HttpStatusCode.Created);
        return created.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task The_admin_receives_into_the_branch_he_names_and_everything_follows_it()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host);
        var warehouse = await WarehouseAsync(admin);
        var oran = await BranchAsync(admin, "Oran");
        var supplier = await CreateAsync(admin, "Fournisseur Oran");
        var product = await ProductAsync(host);

        var response = await admin.PostAsJsonAsync("/api/catalog/stock/receiving", new
        {
            lines = new[] { new { productId = product, qty = 10m, unitCost = 500m, assetUnitValue = (decimal?)null } },
            reference = "BL-ORAN-1",
            note = (string?)null,
            supplierId = (Guid?)supplier,
            branchId = (Guid?)oran,
        });
        var body = await JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal(oran, body.GetProperty("branchId").GetGuid());
        var documentId = body.GetProperty("documentId").GetGuid();

        await using var db = Db(host);
        var oranStock = await db.ProductStocks.AsNoTracking()
            .SingleAsync(s => s.ProductId == product && s.BranchId == oran);
        Assert.Equal(10m, oranStock.StockQty);

        var warehouseStocks = await db.ProductStocks.AsNoTracking()
            .Where(s => s.ProductId == product && s.BranchId == warehouse)
            .ToListAsync();
        Assert.True(warehouseStocks.All(s => s.StockQty <= 0));

        var movements = await db.StockMovements.AsNoTracking()
            .Where(m => m.DocumentId == documentId)
            .ToListAsync();
        Assert.NotEmpty(movements);
        Assert.All(movements, m => Assert.Equal(oran, m.BranchId));

        var ledgerEntry = await db.LedgerEntries.AsNoTracking()
            .SingleAsync(e => e.SourceType == LedgerSources.Receiving && e.SourceId == documentId);
        Assert.Equal(oran, ledgerEntry.BranchId);

        var sup = await db.Suppliers.AsNoTracking().SingleAsync(s => s.Id == supplier);
        Assert.Equal(5_000m, sup.Balance);
    }

    [Fact]
    public async Task A_branch_user_naming_another_branch_receives_into_their_own()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host);
        var warehouse = await WarehouseAsync(admin);
        var oran = await BranchAsync(admin, "Oran");
        var supplier = await CreateAsync(admin, "Fournisseur Oran");
        var product = await ProductAsync(host);

        await TestUsers.CreateAsync(host, admin, "gerant-recep", "Manager", oran, Password);
        // A Manager bound to Oran is refused on the main branch's till; the back office signs in with no terminal code.
        using var manager = await SignedInAsync(host, "gerant-recep", Password, deviceCode: null);

        // 1. Manager posts naming warehouse as branchId
        var response1 = await manager.PostAsJsonAsync("/api/catalog/stock/receiving", new
        {
            lines = new[] { new { productId = product, qty = 10m, unitCost = 500m, assetUnitValue = (decimal?)null } },
            reference = "BL-MGR-1",
            note = (string?)null,
            supplierId = (Guid?)supplier,
            branchId = (Guid?)warehouse,
        });
        var body1 = await JsonAsync(response1, HttpStatusCode.OK);
        Assert.Equal(oran, body1.GetProperty("branchId").GetGuid());
        var doc1 = body1.GetProperty("documentId").GetGuid();

        await using (var db1 = Db(host))
        {
            var stock1 = await db1.ProductStocks.AsNoTracking()
                .SingleAsync(s => s.ProductId == product && s.BranchId == oran);
            Assert.Equal(10m, stock1.StockQty);

            var ledger1 = await db1.LedgerEntries.AsNoTracking()
                .SingleAsync(e => e.SourceType == LedgerSources.Receiving && e.SourceId == doc1);
            Assert.Equal(oran, ledger1.BranchId);
        }

        // 2. Manager posts with branchId = null
        var response2 = await manager.PostAsJsonAsync("/api/catalog/stock/receiving", new
        {
            lines = new[] { new { productId = product, qty = 10m, unitCost = 500m, assetUnitValue = (decimal?)null } },
            reference = "BL-MGR-2",
            note = (string?)null,
            supplierId = (Guid?)supplier,
            branchId = (Guid?)null,
        });
        var body2 = await JsonAsync(response2, HttpStatusCode.OK);
        Assert.Equal(oran, body2.GetProperty("branchId").GetGuid());
        var doc2 = body2.GetProperty("documentId").GetGuid();

        await using (var db2 = Db(host))
        {
            var stock2 = await db2.ProductStocks.AsNoTracking()
                .SingleAsync(s => s.ProductId == product && s.BranchId == oran);
            Assert.Equal(20m, stock2.StockQty);

            var ledger2 = await db2.LedgerEntries.AsNoTracking()
                .SingleAsync(e => e.SourceType == LedgerSources.Receiving && e.SourceId == doc2);
            Assert.Equal(oran, ledger2.BranchId);
        }
    }

    [Fact]
    public async Task A_note_naming_no_branch_lands_at_the_main_warehouse()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host);
        var warehouse = await WarehouseAsync(admin);
        var product = await ProductAsync(host);

        // 1. branchId = (Guid?)null
        var response1 = await admin.PostAsJsonAsync("/api/catalog/stock/receiving", new
        {
            lines = new[] { new { productId = product, qty = 10m, unitCost = 500m, assetUnitValue = (decimal?)null } },
            reference = "BL-NULL-1",
            note = (string?)null,
            supplierId = (Guid?)null,
            branchId = (Guid?)null,
        });
        var body1 = await JsonAsync(response1, HttpStatusCode.OK);
        Assert.Equal(warehouse, body1.GetProperty("branchId").GetGuid());

        await using (var db1 = Db(host))
        {
            var stock1 = await db1.ProductStocks.AsNoTracking()
                .SingleAsync(s => s.ProductId == product && s.BranchId == warehouse);
            Assert.Equal(10m, stock1.StockQty);
        }

        // 2. anonymous object with no branchId property at all
        var response2 = await admin.PostAsJsonAsync("/api/catalog/stock/receiving", new
        {
            lines = new[] { new { productId = product, qty = 10m, unitCost = 500m, assetUnitValue = (decimal?)null } },
            reference = "BL-NO-BRANCH-PROP",
            note = (string?)null,
            supplierId = (Guid?)null,
        });
        var body2 = await JsonAsync(response2, HttpStatusCode.OK);
        Assert.Equal(warehouse, body2.GetProperty("branchId").GetGuid());

        await using (var db2 = Db(host))
        {
            var stock2 = await db2.ProductStocks.AsNoTracking()
                .SingleAsync(s => s.ProductId == product && s.BranchId == warehouse);
            Assert.Equal(20m, stock2.StockQty);
        }
    }

    [Fact]
    public async Task The_admin_naming_an_unknown_branch_is_refused()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host);
        var product = await ProductAsync(host);
        var unknownBranch = Guid.NewGuid();

        var response = await admin.PostAsJsonAsync("/api/catalog/stock/receiving", new
        {
            lines = new[] { new { productId = product, qty = 10m, unitCost = 500m, assetUnitValue = (decimal?)null } },
            reference = "BL-UNKNOWN",
            note = (string?)null,
            supplierId = (Guid?)null,
            branchId = (Guid?)unknownBranch,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("Unknown branch.", error);

        await using var db = Db(host);
        var movementExists = await db.StockMovements.AsNoTracking().AnyAsync(m => m.ProductId == product);
        Assert.False(movementExists);
    }
}
