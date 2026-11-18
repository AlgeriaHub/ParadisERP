using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Receivables;
using ParadisERP.Tests.Hosting;
using ParadisERP.Tests.Receivables;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadisERP.Tests.Security;

/// <summary>
/// The two tax controls of the accounting review (TAX-01, findings E#1 and E#2; owner, 2026-11-02):
/// "sell untaxed at the till" is a permission — the Admin's and the Manager's by role, a cashier's own tick —
/// that every cashier on file was given the day it arrived. An invoice over a sale made without tax follows the sale,
/// with no permission of its own (owner, 2026-11-05: VAT only when the customer asks, and every seller decides).
/// </summary>
[Collection(HostCollection.Name)]
public class UntaxedSalesTests
{
    private const string Password = "Essai@2026";

    // ---------- E#2: invoicing a sale made without tax ----------

    [Fact]
    public async Task An_untaxed_sale_is_invoiced_as_it_was_sold_by_anyone_who_may_invoice()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var untaxed = await h.PushSaleAsync(total: 10_000m, cash: 0m, customerId: ReceivableHarness.Contractor);
        var taxed = await h.PushSaleAsync(total: 5_000m, cash: 0m, customerId: ReceivableHarness.Contractor);
        await h.Db.Sales.Where(s => s.Id == untaxed).ExecuteUpdateAsync(u => u.SetProperty(s => s.TaxMode, TaxMode.Untaxed));
        h.Db.ChangeTracker.Clear();
        CreateInvoiceRequest Over(Guid sale) => new(DateTime.UtcNow.AddDays(30), SaleId: sale);

        // Invoiced as it was sold, without tax, and the taxed one with its tax: no permission either way.
        var issued = await h.Receivables.CreateInvoiceAsync(Over(untaxed), "manager");
        Assert.True(issued.Ok, issued.Message);
        Assert.Equal((TaxMode.Untaxed, 0m), (issued.Value!.TaxMode, issued.Value.TaxTotal));

        var plain = await h.Receivables.CreateInvoiceAsync(Over(taxed), "manager");
        Assert.True(plain.Ok, plain.Message);
        Assert.Equal(TaxMode.Taxed, plain.Value!.TaxMode);
    }

    // ---------- E#1: who may sell untaxed ----------

    private static async Task<(HttpClient Client, JsonElement Body)> SignedInAsync(PosHost host, string username, string password)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = host.SignInName(username), password, deviceCode = (string?)null });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.Clone();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return (client, body);
    }

    private static bool Reports(JsonElement login) =>
        login.GetProperty("user").GetProperty("capabilities").EnumerateArray().Any(c => c.GetString() == PermissionCatalog.SalesUntaxed);

    private static async Task<JsonElement> RowAsync(HttpClient admin, Guid id) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/users")).EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == id).Clone();

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient admin, JsonElement row, Guid branch, bool? sellsUntaxed) =>
        admin.PutAsJsonAsync($"/api/users/{row.GetProperty("id").GetGuid()}", sellsUntaxed is bool sells
            ? (object)new { fullName = row.GetProperty("fullName").GetString(), role = row.GetProperty("role").GetString(), isActive = true,
                permissions = Array.Empty<string>(), branchId = branch, sellsUntaxed = sells }
            : new { fullName = row.GetProperty("fullName").GetString(), role = row.GetProperty("role").GetString(), isActive = true,
                permissions = Array.Empty<string>(), branchId = branch });

    [Fact]
    public async Task A_cashier_sells_untaxed_by_their_own_tick_and_the_Admin_and_Manager_by_their_role()
    {
        using var host = new PosHost();
        var (admin, adminLogin) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        using (admin)
        {
            Assert.True(Reports(adminLogin));
            var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
            var created = await admin.PostAsJsonAsync("/api/catalog/branches", new
            {
                nameAr = "Alger", nameFr = "Alger", nameEn = "Alger", address = "", phone = "", isMain = false, allowNegativeStock = false, isActive = true,
            });
            var branch = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            // A new cashier starts without it: the till starts their sales taxed.
            var (cashier, _) = await TestUsers.CreateAsync(host, admin, "caisse.t", "Cashier", branch, Password, pin: "1234");
            var row = await RowAsync(admin, cashier);
            Assert.Equal((false, false), (row.GetProperty("sellsUntaxed").GetBoolean(), row.GetProperty("sellsUntaxedByRole").GetBoolean()));
            Assert.False(Reports((await SignedInAsync(host, "caisse.t", Password)).Body));

            // Ticked on the users screen: their own grant, reported at their next sign-in.
            Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(admin, row, branch, true)).StatusCode);
            row = await RowAsync(admin, cashier);
            Assert.Equal((true, false), (row.GetProperty("sellsUntaxed").GetBoolean(), row.GetProperty("sellsUntaxedByRole").GetBoolean()));
            Assert.True(Reports((await SignedInAsync(host, "caisse.t", Password)).Body));

            // A save that does not name it — the legacy grants rewritten, say — leaves it.
            Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(admin, row, branch, null)).StatusCode);
            Assert.True((await RowAsync(admin, cashier)).GetProperty("sellsUntaxed").GetBoolean());

            // Unticked: gone.
            Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(admin, row, branch, false)).StatusCode);
            Assert.False((await RowAsync(admin, cashier)).GetProperty("sellsUntaxed").GetBoolean());
            Assert.False(Reports((await SignedInAsync(host, "caisse.t", Password)).Body));

            // A Manager holds it by role, and a tick stores nothing of their own.
            var employee = await TestUsers.EmployeeAsync(admin, "gerant.t", branch);
            var manager = await TestUsers.CreatedAsync(await admin.PostAsJsonAsync("/api/users", new
            {
                employeeId = employee, role = "Manager", password = Password, pin = (string?)null, permissions = Array.Empty<string>(), branchId = branch,
                sellsUntaxed = true,
            }));
            host.Remember("gerant.t", manager.LoginId);
            row = await RowAsync(admin, manager.Id);
            Assert.Equal((true, true), (row.GetProperty("sellsUntaxed").GetBoolean(), row.GetProperty("sellsUntaxedByRole").GetBoolean()));
            Assert.True(Reports((await SignedInAsync(host, "gerant.t", Password)).Body));
            await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);
            Assert.False(await db.UserPermissions.AnyAsync(p => p.UserId == manager.Id && p.PermissionKey == PermissionCatalog.SalesUntaxed));
        }
    }

    // ---------- the day it arrived: nobody lost it ----------

    private const string SqliteBefore = "20261101182505_BookClosings";
    private const string SqlServerBefore = "20261101182519_BookClosings";

    [Fact]
    public async Task Sqlite_upgrade_gives_the_Manager_role_and_every_cashier_on_file_the_permission()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await VerifyUpgrade(db, SqliteBefore);
    }

    [SqlServerFact]
    public async Task SqlServer_upgrade_gives_the_Manager_role_and_every_cashier_on_file_the_permission()
    {
        await using var database = new SqlServerTestDatabase();
        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, PosDbProviders.SqlServer, database.ConnectionString);
        await using var db = new PosDbContext(options.Options);
        await VerifyUpgrade(db, SqlServerBefore);
    }

    private static async Task VerifyUpgrade(PosDbContext db, string before)
    {
        await db.Database.GetService<IMigrator>().MigrateAsync(before);

        // The system roles as an older build left them: the Manager without the key. Raw SQL, in the columns this
        // older schema has: today's model carries one it does not yet (Roles.NormalizedName, 2026-11-03), and a
        // save that adds users would otherwise write the missing Admin role through it.
        foreach (var (id, key, scope) in new[] { (SystemRoles.AdminId, SystemRoles.AdminKey, RoleScope.Company),
                     (SystemRoles.ManagerId, SystemRoles.ManagerKey, RoleScope.AssignedBranch), (SystemRoles.CashierId, SystemRoles.CashierKey, RoleScope.TillBranch) })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Roles\" (\"Id\", \"Key\", \"Name\", \"IsActive\", \"IsSystem\", \"Scope\", \"Version\", \"CreatedAtUtc\", \"UpdatedAtUtc\") " +
                "VALUES ({0}, {1}, {1}, {2}, {2}, {3}, {4}, {5}, {5})", id, key, true, (int)scope, 0L, DateTime.UtcNow);
        db.RolePermissions.AddRange(
            new RolePermission { RoleId = SystemRoles.ManagerId, PermissionKey = PermissionCatalog.SalesCreate },
            new RolePermission { RoleId = SystemRoles.CashierId, PermissionKey = PermissionCatalog.SalesCreate });
        User Person(string name, UserRole role, Guid? roleId, bool deleted = false) => new()
        {
            Username = name, FullName = name, Role = role, RoleId = roleId, IsDeleted = deleted, PasswordHash = "x", PinHash = "",
        };
        var cashier = Person("caisse.1", UserRole.Cashier, SystemRoles.CashierId);
        var ticked = Person("caisse.2", UserRole.Cashier, SystemRoles.CashierId);
        var legacy = Person("caisse.3", UserRole.Cashier, null);
        var gone = Person("caisse.4", UserRole.Cashier, SystemRoles.CashierId, deleted: true);
        var manager = Person("gerant.1", UserRole.Manager, SystemRoles.ManagerId);
        // Raw SQL again, in the columns this older schema has: today's User carries columns it does not yet
        // (two-step login and the photo, 2026-11-11).
        foreach (var u in new[] { cashier, ticked, legacy, gone, manager })
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Users\" (\"Id\", \"Username\", \"FullName\", \"PasswordHash\", \"PinHash\", \"Role\", \"IsActive\", \"Version\", \"UpdatedAtUtc\", \"IsDeleted\", \"RoleId\", \"AuthorizationVersion\") " +
                "VALUES ({0}, {1}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, " + (u.RoleId is null ? "NULL" : "{9}") + ", 0)",
                u.Id, u.Username, u.PasswordHash, u.PinHash, (int)u.Role, true, 0L, DateTime.UtcNow, u.IsDeleted, u.RoleId ?? Guid.Empty);
        db.UserPermissions.Add(new UserPermission { UserId = ticked.Id, PermissionKey = PermissionCatalog.SalesUntaxed, GrantedBy = "admin" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await db.Database.MigrateAsync();

        Assert.True(await db.RolePermissions.AnyAsync(p => p.RoleId == SystemRoles.ManagerId && p.PermissionKey == PermissionCatalog.SalesUntaxed));
        Assert.False(await db.RolePermissions.AnyAsync(p => p.RoleId == SystemRoles.CashierId && p.PermissionKey == PermissionCatalog.SalesUntaxed));
        var grants = await db.UserPermissions.AsNoTracking().Where(p => p.PermissionKey == PermissionCatalog.SalesUntaxed).ToListAsync();
        // Every cashier on file, one row each: the one already ticked keeps theirs, the deleted one and the
        // Manager (whose role gives it) get none.
        Assert.Equal(new[] { cashier.Id, legacy.Id, ticked.Id }.Order(), grants.Select(g => g.UserId).Order());
        Assert.Equal("admin", grants.Single(g => g.UserId == ticked.Id).GrantedBy);
        Assert.Equal("migration", grants.Single(g => g.UserId == cashier.Id).GrantedBy);
    }
}
