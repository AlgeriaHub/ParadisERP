using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.API.Endpoints;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Catalog;

/// <summary>
/// Locations are of two kinds — a branch, which sells, and a warehouse, which holds stock — and of
/// each kind exactly one is primary (owner, 2026-10-05). Ticking "Set as primary" on another of the
/// same kind moves the flag in one transaction; the two kinds never affect each other; the database
/// itself refuses a second primary; and a change of kind or primary rewrites the row in place.
/// </summary>
[Collection(HostCollection.Name)]
public class BranchKindTests
{
    private static async Task<HttpClient> AdminAsync(PosHost host)
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

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static object Location(string name, bool warehouse, bool primary) => new
    {
        nameAr = name, nameFr = name, nameEn = name, address = "", phone = "",
        isMain = primary, isWarehouse = warehouse, allowNegativeStock = false, isActive = true,
    };

    private static async Task<List<JsonElement>> ListAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches")).EnumerateArray().ToList();

    private static Guid PrimaryOf(List<JsonElement> rows, bool warehouse) =>
        rows.Single(b => b.GetProperty("isMain").GetBoolean() && b.GetProperty("isWarehouse").GetBoolean() == warehouse)
            .GetProperty("id").GetGuid();

    private static async Task<Guid> CreateAsync(HttpClient admin, string name, bool warehouse, bool primary)
    {
        var created = await admin.PostAsJsonAsync("/api/catalog/branches", Location(name, warehouse, primary));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    private static async Task<List<JsonElement>> CashBoxesAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/treasury/cash-boxes")).EnumerateArray().ToList();

    [Fact]
    public async Task A_new_company_has_a_primary_branch_and_a_primary_warehouse_at_once()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var rows = await ListAsync(admin);

        Assert.Equal(2, rows.Count);
        var branch = PrimaryOf(rows, warehouse: false);
        var warehouse = PrimaryOf(rows, warehouse: true);
        Assert.NotEqual(branch, warehouse);

        // The first till belongs to the primary branch, which sells, and is not flagged.
        var c01 = (await admin.GetFromJsonAsync<JsonElement>("/api/devices")).EnumerateArray().Single(d => d.GetProperty("code").GetString() == "C01");
        Assert.Equal(branch, c01.GetProperty("branchId").GetGuid());
        Assert.False(c01.GetProperty("onWarehouse").GetBoolean());

        // One cash box: the primary branch's, which is the central safe. The warehouse has none.
        var box = Assert.Single(await CashBoxesAsync(admin));
        Assert.Equal(branch, box.GetProperty("branchId").GetGuid());
        Assert.True(box.GetProperty("isMain").GetBoolean());
    }

    [Fact]
    public async Task A_second_branch_set_as_primary_takes_the_flag_and_the_primary_warehouse_is_untouched()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var before = await ListAsync(admin);
        var oldBranch = PrimaryOf(before, warehouse: false);
        var warehouse = PrimaryOf(before, warehouse: true);

        var oran = await CreateAsync(admin, "Oran", warehouse: false, primary: true);

        var after = await ListAsync(admin);
        Assert.Equal(oran, PrimaryOf(after, warehouse: false));
        Assert.False(after.Single(b => b.GetProperty("id").GetGuid() == oldBranch).GetProperty("isMain").GetBoolean());
        Assert.Equal(warehouse, PrimaryOf(after, warehouse: true));

        // The central safe followed the primary branch.
        var safes = (await CashBoxesAsync(admin)).Where(b => b.GetProperty("isMain").GetBoolean()).ToList();
        Assert.Equal(oran, Assert.Single(safes).GetProperty("branchId").GetGuid());
    }

    [Fact]
    public async Task A_second_warehouse_set_as_primary_takes_the_flag_and_the_primary_branch_is_untouched()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var before = await ListAsync(admin);
        var branch = PrimaryOf(before, warehouse: false);
        var oldWarehouse = PrimaryOf(before, warehouse: true);

        var skikda = await CreateAsync(admin, "Dépôt Skikda", warehouse: true, primary: false);
        // Added without the tick: no primary moved.
        var middle = await ListAsync(admin);
        Assert.Equal(oldWarehouse, PrimaryOf(middle, warehouse: true));
        Assert.Equal(branch, PrimaryOf(middle, warehouse: false));

        // Ticked on edit: the flag moves, in place.
        var put = await admin.PutAsJsonAsync($"/api/catalog/branches/{skikda}", Location("Dépôt Skikda", warehouse: true, primary: true));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await ListAsync(admin);
        Assert.Equal(skikda, PrimaryOf(after, warehouse: true));
        Assert.False(after.Single(b => b.GetProperty("id").GetGuid() == oldWarehouse).GetProperty("isMain").GetBoolean());
        Assert.Equal(branch, PrimaryOf(after, warehouse: false));
        // A warehouse handles no money: the new primary warehouse has no cash box.
        Assert.DoesNotContain(await CashBoxesAsync(admin), b => b.GetProperty("branchId").GetGuid() == skikda);
    }

    [Fact]
    public async Task Both_kinds_are_primary_at_once_and_changing_one_never_changes_the_other()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var warehouse = PrimaryOf(await ListAsync(admin), warehouse: true);

        var b2 = await CreateAsync(admin, "Blida", warehouse: false, primary: true);
        var b3 = await CreateAsync(admin, "Tlemcen", warehouse: false, primary: true);
        var rows = await ListAsync(admin);
        Assert.Equal(b3, PrimaryOf(rows, warehouse: false));
        Assert.Equal(warehouse, PrimaryOf(rows, warehouse: true));
        Assert.False(rows.Single(b => b.GetProperty("id").GetGuid() == b2).GetProperty("isMain").GetBoolean());
        Assert.Equal(2, rows.Count(b => b.GetProperty("isMain").GetBoolean()));
    }

    [Fact]
    public async Task The_only_primary_of_a_kind_cannot_be_unticked_change_kind_or_be_deleted()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var rows = await ListAsync(admin);
        var branch = PrimaryOf(rows, warehouse: false);
        var warehouse = PrimaryOf(rows, warehouse: true);

        await AssertRefusedAsync(await admin.PutAsJsonAsync($"/api/catalog/branches/{branch}", Location("Siège", false, false)),
            BranchEndpoints.MainBranchRequiredCode);
        await AssertRefusedAsync(await admin.PutAsJsonAsync($"/api/catalog/branches/{branch}", Location("Siège", true, true)),
            BranchEndpoints.MainBranchRequiredCode);
        await AssertRefusedAsync(await admin.DeleteAsync($"/api/catalog/branches/{branch}"), BranchEndpoints.NotDeletableCode);
        await AssertRefusedAsync(await admin.PutAsJsonAsync($"/api/catalog/branches/{warehouse}", Location("Dépôt", true, false)),
            BranchEndpoints.MainWarehouseRequiredCode);
        await AssertRefusedAsync(await admin.DeleteAsync($"/api/catalog/branches/{warehouse}"), BranchEndpoints.NotDeletableCode);

        var after = await ListAsync(admin);
        Assert.Equal(branch, PrimaryOf(after, warehouse: false));
        Assert.Equal(warehouse, PrimaryOf(after, warehouse: true));
    }

    [Fact]
    public async Task A_change_of_kind_is_allowed_only_when_safe_and_keeps_the_row()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);

        // A branch with a till cannot become a warehouse.
        var oran = await CreateAsync(admin, "Oran", warehouse: false, primary: false);
        Assert.Equal(HttpStatusCode.Created,
            (await admin.PostAsJsonAsync("/api/devices", new { code = "C07", name = "Oran 1", branchId = oran, isActive = true })).StatusCode);
        await AssertRefusedAsync(await admin.PutAsJsonAsync($"/api/catalog/branches/{oran}", Location("Oran", true, false)),
            BranchEndpoints.WarehouseHasTillsCode);

        // An empty branch can, keeps its id, and its unused cash box is switched off.
        var tlemcen = await CreateAsync(admin, "Tlemcen", warehouse: false, primary: false);
        Assert.Contains(await CashBoxesAsync(admin), b => b.GetProperty("branchId").GetGuid() == tlemcen);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/catalog/branches/{tlemcen}", Location("Tlemcen", true, false))).StatusCode);
        var row = (await ListAsync(admin)).Single(b => b.GetProperty("id").GetGuid() == tlemcen);
        Assert.True(row.GetProperty("isWarehouse").GetBoolean());
        Assert.DoesNotContain(await CashBoxesAsync(admin), b => b.GetProperty("branchId").GetGuid() == tlemcen);

        // A warehouse holding stock cannot become a branch; an empty one can, and gets a cash box.
        var full = await CreateAsync(admin, "Dépôt plein", warehouse: true, primary: false);
        await using (var db = Db(host))
        {
            var product = new Product { Sku = "T-1", NameFr = "Article", Price = 1 };
            db.Products.Add(product);
            db.ProductStocks.Add(new ProductStock { ProductId = product.Id, BranchId = full, StockQty = 5 });
            await db.SaveChangesAsync();
        }
        await AssertRefusedAsync(await admin.PutAsJsonAsync($"/api/catalog/branches/{full}", Location("Dépôt plein", false, false)),
            BranchEndpoints.WarehouseHasStockCode);

        var empty = await CreateAsync(admin, "Dépôt vide", warehouse: true, primary: false);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/catalog/branches/{empty}", Location("Dépôt vide", false, false))).StatusCode);
        Assert.Contains(await CashBoxesAsync(admin), b => b.GetProperty("branchId").GetGuid() == empty);
    }

    [Fact]
    public async Task Two_locations_never_share_a_name()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var oran = await CreateAsync(admin, "Oran", warehouse: false, primary: false);

        // Trimmed and without case: " oran " is Oran.
        await AssertRefusedAsync(await admin.PostAsJsonAsync("/api/catalog/branches", Location(" oran ", false, false)), BranchEndpoints.NameTakenCode);
        var annaba = await CreateAsync(admin, "Annaba", warehouse: false, primary: false);
        await AssertRefusedAsync(await admin.PutAsJsonAsync($"/api/catalog/branches/{annaba}", Location("Oran", false, false)), BranchEndpoints.NameTakenCode);
        // Saving a location under its own name is no clash with itself.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/catalog/branches/{oran}", Location("Oran", false, false))).StatusCode);
    }

    [Fact]
    public async Task Stock_levels_read_one_branch_and_nothing_without_one()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>("/api/catalog/stock/levels")).GetArrayLength());
        var warehouse = PrimaryOf(await ListAsync(admin), warehouse: true);
        var levels = await admin.GetAsync($"/api/catalog/stock/levels?branchId={warehouse}");
        Assert.Equal(HttpStatusCode.OK, levels.StatusCode);
        Assert.Equal(JsonValueKind.Array, (await levels.Content.ReadFromJsonAsync<JsonElement>()).ValueKind);
    }

    [Fact]
    public async Task A_branch_is_never_deleted_and_keeps_its_stock()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var annaba = await CreateAsync(admin, "Annaba", warehouse: false, primary: false);

        await AssertRefusedAsync(await admin.DeleteAsync($"/api/catalog/branches/{annaba}"), BranchEndpoints.NotDeletableCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/catalog/branches/{Guid.NewGuid()}")).StatusCode);

        await using var db = Db(host);
        var row = await db.Branches.AsNoTracking().SingleAsync(b => b.Id == annaba);
        Assert.False(row.IsDeleted);
        Assert.True(row.IsActive);
    }

    [Fact]
    public async Task The_database_refuses_two_primaries_of_one_kind_written_directly()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var second = await CreateAsync(admin, "Dépôt 2", warehouse: true, primary: false);
        var otherBranch = await CreateAsync(admin, "Annaba", warehouse: false, primary: false);

        await using (var db = Db(host))
        {
            (await db.Branches.SingleAsync(b => b.Id == second)).IsMain = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = Db(host))
        {
            (await db.Branches.SingleAsync(b => b.Id == otherBranch)).IsMain = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        // One of each kind is not two of one kind, and a deleted row never counts.
        await using (var db = Db(host))
        {
            Assert.Equal(2, await db.Branches.CountAsync(b => b.IsMain && !b.IsDeleted));
            var deleted = await db.Branches.SingleAsync(b => b.Id == otherBranch);
            deleted.IsDeleted = true;
            deleted.IsMain = true;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Ids_stock_and_references_are_unchanged_by_a_change_of_primary_or_kind()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var oldWarehouse = PrimaryOf(await ListAsync(admin), warehouse: true);
        var second = await CreateAsync(admin, "Dépôt 2", warehouse: true, primary: false);

        Guid productId, stockId, movementId;
        await using (var db = Db(host))
        {
            var product = new Product { Sku = "T-2", NameFr = "Article", Price = 1 };
            db.Products.Add(product);
            var stock = new ProductStock { ProductId = product.Id, BranchId = oldWarehouse, StockQty = 972 };
            db.ProductStocks.Add(stock);
            var movement = new StockMovement { ProductId = product.Id, BranchId = oldWarehouse, QtyDelta = 972, BalanceAfter = 972, Reason = "opening" };
            db.StockMovements.Add(movement);
            await db.SaveChangesAsync();
            (productId, stockId, movementId) = (product.Id, stock.Id, movement.Id);
        }

        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/api/catalog/branches/{second}", Location("Dépôt 2", warehouse: true, primary: true))).StatusCode);
        var tlemcen = await CreateAsync(admin, "Tlemcen", warehouse: false, primary: false);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/api/catalog/branches/{tlemcen}", Location("Tlemcen", warehouse: true, primary: false))).StatusCode);

        await using (var db = Db(host))
        {
            var stock = await db.ProductStocks.AsNoTracking().SingleAsync(s => s.Id == stockId);
            Assert.Equal(oldWarehouse, stock.BranchId);
            Assert.Equal(972m, stock.StockQty);
            Assert.Equal(oldWarehouse, (await db.StockMovements.AsNoTracking().SingleAsync(m => m.Id == movementId)).BranchId);
            Assert.True(await db.Branches.AnyAsync(b => b.Id == oldWarehouse && !b.IsDeleted && b.IsWarehouse && !b.IsMain));
            Assert.True(await db.Branches.AnyAsync(b => b.Id == second && b.IsWarehouse && b.IsMain));
            Assert.True(await db.Branches.AnyAsync(b => b.Id == tlemcen && b.IsWarehouse));
            Assert.Equal(productId, stock.ProductId);
        }
    }
}
