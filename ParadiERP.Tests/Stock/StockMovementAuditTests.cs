using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Tests.Hosting;
using ParadiERP.Tests.Rentals;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Stock;

/// <summary>
/// Every stock movement says who made it, on which document and how many moved (owner, 2026-10-12),
/// so the item movement report can name them; and "on rent" is the contracts' to move, never a
/// state typed in or out of by hand.
/// </summary>
[Collection(HostCollection.Name)]
public class StockMovementAuditTests
{
    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    private static async Task<HttpClient> SignedInAsync(PosHost host)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static async Task<Guid> WarehouseAsync(HttpClient admin)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        return branches.EnumerateArray()
            .Single(b => b.GetProperty("isMain").GetBoolean() && b.GetProperty("isWarehouse").GetBoolean())
            .GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_receiving_records_the_user_the_reference_the_supplier_and_the_quantity()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host);
        var warehouse = await WarehouseAsync(admin);
        var supplier = (await (await admin.PostAsJsonAsync("/api/suppliers", new { name = "Fournisseur Alger", phone = "0550 00 00 00" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Guid product;
        await using (var db = Db(host))
        {
            var p = new Product { Sku = "TUBE-48", NameFr = "Tube 48", Price = 1_000m, CostPrice = 500m };
            db.Products.Add(p);
            await db.SaveChangesAsync();
            product = p.Id;
        }

        var response = await admin.PostAsJsonAsync("/api/catalog/stock/receiving", new
        {
            lines = new[] { new { productId = product, qty = 12m, unitCost = 500m, assetUnitValue = (decimal?)null } },
            reference = "BL-2026-0041", note = (string?)null, supplierId = (Guid?)supplier, branchId = (Guid?)warehouse,
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        await using var check = Db(host);
        var movement = await check.StockMovements.AsNoTracking().SingleAsync(m => m.ProductId == product);
        var admin1 = await check.Users.AsNoTracking().SingleAsync(u => u.Username == PosHost.BootstrapUsername);
        Assert.Equal(admin1.Id, movement.UserId);
        Assert.False(string.IsNullOrWhiteSpace(movement.UserName));
        Assert.Equal("BL-2026-0041", movement.DocumentNumber);
        Assert.Equal(supplier, movement.SupplierId);
        Assert.Equal(12m, movement.MovedQty);
    }

    [Theory]
    [InlineData("OnRent", "Available")]
    [InlineData("Available", "OnRent")]
    public async Task On_rent_is_refused_as_a_source_or_a_destination_and_nothing_moves(string from, string to)
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers);
        var stock = await db.ProductStocks.SingleAsync(s => s.ProductId == TillHarness.Pipe && s.BranchId == TillHarness.Algiers);
        stock.OnRentQty = 3m;
        await db.SaveChangesAsync();

        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapStockEndpoints(), "POST",
            "/api/catalog/stock/state", admin,
            JsonSerializer.Serialize(new { productId = TillHarness.Pipe, branchId = TillHarness.Algiers, qty = 1m, from, to, reason = (string?)null }));

        Assert.Equal(400, status);
        Assert.Contains("issue and return notes", body);
        var after = await db.ProductStocks.AsNoTracking().SingleAsync(s => s.Id == stock.Id);
        Assert.Equal(10m, after.StockQty);
        Assert.Equal(3m, after.OnRentQty);
        Assert.False(await db.StockMovements.AnyAsync());
    }

    [Fact]
    public async Task A_state_change_records_the_user_and_the_quantity_moved()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (user, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers);

        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapStockEndpoints(), "POST",
            "/api/catalog/stock/state", admin,
            JsonSerializer.Serialize(new { productId = TillHarness.Pipe, branchId = TillHarness.Algiers, qty = 2m, from = "Available", to = "Maintenance", reason = "seal" }));

        Assert.True(status == 200, body);
        var movement = await db.StockMovements.AsNoTracking().SingleAsync();
        Assert.Equal(0m, movement.QtyDelta);
        Assert.Equal(2m, movement.MovedQty);
        Assert.Equal(user.Id, movement.UserId);
    }

    [Fact]
    public async Task A_rental_issue_records_the_note_number_its_user_and_the_quantity_issued()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var pipe = await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe);
        pipe.IsRentable = true;
        var customer = RentalTestPaper.Identified(new Customer { Code = "C001", FullName = "Client Test", Kind = CustomerKind.Person });
        db.Customers.Add(customer);
        var site = RentalTestPaper.Site(Guid.NewGuid(), customer.Id);
        db.CustomerSites.Add(site);
        db.Stores.Add(new Store { NameFr = "Store", RentalOverdueMultiplier = 1.5m });
        await db.SaveChangesAsync();

        var rentals = new RentalDocuments(db, new LedgerPoster(db), new Algeria());
        var created = await rentals.CreateContractAsync(new UpsertRentalContractRequest(customer.Id, TillHarness.Algiers,
            [new RentalContractLineRequest(pipe.Id, 10m, 15, RentUnitPrice: 100m, DamageUnitPrice: 500m)])
        {
            SiteId = site.Id, SignatoryBirthDate = new DateOnly(1980, 3, 4)
        }, "admin");
        Assert.True(created.Ok, created.Message);
        var line = created.Value!.Lines.Single();
        var userId = Guid.NewGuid();

        var issued = await rentals.IssueAsync(created.Value.Id, RentalTestPaper.Driven(new IssueNoteRequest(
            TillHarness.Algiers, [new IssueNoteLineRequest(line.Id, 4m)], DateTime.UtcNow)), userId, "Karim");
        Assert.True(issued.Ok, issued.Message);

        var note = await db.IssueNotes.AsNoTracking().SingleAsync();
        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.Reason == "rental-issue");
        Assert.Equal(4m, movement.MovedQty);
        Assert.Equal(note.Number, movement.DocumentNumber);
        Assert.Equal(userId, movement.UserId);
        Assert.Equal("Karim", movement.UserName);
    }

    [Fact]
    public async Task A_pushed_sale_records_its_cashier_and_its_reference()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);

        var push = TillHarness.Sale("A-0001", 1, payments: TillHarness.Tender("cash", 1000m));
        Assert.Equal(1, (await sync.PushAsync(new PushRequest("C01", [push]), "C01", CancellationToken.None)).Accepted);

        var sale = await db.Sales.AsNoTracking().SingleAsync();
        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.SaleId == sale.Id);
        Assert.Equal(sale.CashierUserId, movement.UserId);
        Assert.Equal(sale.CashierName, movement.UserName);
        Assert.Equal("A-0001", movement.DocumentNumber);
        Assert.Equal(1m, movement.MovedQty);
    }
}
