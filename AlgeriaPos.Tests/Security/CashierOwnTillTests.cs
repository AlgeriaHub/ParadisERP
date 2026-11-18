using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// A cashier's own till (owner, 2026-10-05: "the till code is created automatically when a
/// cashier is created", "no need to sign in with a till code except offline"). The till comes
/// with the cashier, follows them, and opens when they sign in online without typing a code; a
/// shared counter till is still opened by its code. Receipt numbers never collide into a refusal.
/// </summary>
[Collection(HostCollection.Name)]
public class CashierOwnTillTests
{
    private const string Password = "Essai@2026";

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password, string? deviceCode) =>
        await client.PostAsJsonAsync("/api/auth/login", new { username, password, deviceCode });

    private static async Task<(HttpClient Client, JsonElement Body)> SignedInAsync(PosHost host, string username, string password, string? deviceCode)
    {
        var client = host.CreateClient();
        var login = await LoginAsync(client, host.SignInName(username), password, deviceCode);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.Clone();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return (client, body);
    }

    private static Task<(HttpClient Client, JsonElement Body)> AdminAsync(PosHost host) =>
        SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, null);

    private static async Task<Guid> SellingBranchAsync(HttpClient admin, string name)
    {
        var created = await admin.PostAsJsonAsync("/api/catalog/branches", new
        {
            nameAr = name, nameFr = name, nameEn = name, address = "", phone = "",
            isMain = false, allowNegativeStock = false, isActive = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> MainBranchAsync(HttpClient admin)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        return branches.EnumerateArray().Single(b => b.GetProperty("isMain").GetBoolean() && b.GetProperty("isWarehouse").GetBoolean()).GetProperty("id").GetGuid();
    }

    // An employee on that branch first, then their account (2026-10-05). The answer is returned as it
    // comes, so a test can read a refusal; an account that was created is remembered by name.
    private static async Task<HttpResponseMessage> CreateUserAsync(PosHost host, HttpClient admin, string username, UserRole role, Guid branchId)
    {
        var employee = await TestUsers.EmployeeAsync(admin, username, branchId);
        var response = await TestUsers.PostAsync(admin, employee, role.ToString(), Password, branchId, pin: "1234");
        await response.Content.LoadIntoBufferAsync();
        if (response.StatusCode == HttpStatusCode.Created)
            host.Remember(username, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("loginId").GetString()!);
        return response;
    }

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static PosDbContext Open(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static object Sale(Guid id, long seq, string reference, Guid cashier) => new
    {
        id, reference, localSeq = seq, soldAtUtc = DateTime.UtcNow, customerId = (Guid?)null,
        cashierUserId = cashier, cashierName = "Essai", subTotalHt = 10m, taxTotal = 0m, discountTotal = 0m,
        grandTotalTtc = 10m, roundingAdjustment = 0m, paidAmount = 10m, changeAmount = 0m,
        paymentMethod = "Cash", status = "Completed", refundOfSaleId = (Guid?)null, note = (string?)null, taxMode = "Untaxed",
        lines = new[] { new { id = Guid.NewGuid(), productId = Guid.NewGuid(), sku = "X", nameSnapshot = "X", qty = 1m,
            unitPrice = 10m, taxRate = 0m, lineDiscount = 0m, lineTotalTtc = 10m, lineTaxAmount = 0m } },
        payments = new[] { new { id = Guid.NewGuid(), method = "Cash", amount = 10m, reference = (string?)null } },
    };

    // ---------- the code rule, without HTTP ----------

    [Fact]
    public async Task The_next_code_follows_every_till_ever_used_and_never_reuses_one()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        Assert.Equal("C01", await CashierTills.NextCodeAsync(db, default));

        var branch = new Branch { NameFr = "Alger" };
        db.Branches.Add(branch);
        db.Devices.Add(new Device { Code = "C01", BranchId = branch.Id });
        db.Devices.Add(new Device { Code = "BAR", BranchId = branch.Id }); // a code outside the pattern is ignored
        await db.SaveChangesAsync();
        Assert.Equal("C02", await CashierTills.NextCodeAsync(db, default));

        // A till whose row is gone still owns its receipt numbers: its code is never handed out again.
        db.Sales.Add(new Sale { Id = Guid.NewGuid(), Reference = "C07-000001", DeviceCode = "C07", LocalSeq = 1, BranchId = branch.Id });
        await db.SaveChangesAsync();
        Assert.Equal("C08", await CashierTills.NextCodeAsync(db, default));
    }

    // ---------- the users routes ----------

    [Fact]
    public async Task A_cashier_created_on_a_selling_branch_gets_their_own_till_at_once()
    {
        using var host = new PosHost();
        var (admin, _) = await AdminAsync(host);
        using (admin)
        {
            var algiers = await SellingBranchAsync(admin, "Alger");
            var first = await CreatedAsync(await CreateUserAsync(host, admin, "caisse.a", UserRole.Cashier, algiers));
            var second = await CreatedAsync(await CreateUserAsync(host, admin, "caisse.b", UserRole.Cashier, algiers));

            // C01 is the company's first till; each cashier takes the next code.
            Assert.Equal("C02", first.GetProperty("tillCode").GetString());
            Assert.True(first.GetProperty("tillActive").GetBoolean());
            Assert.Equal("C03", second.GetProperty("tillCode").GetString());

            var devices = await admin.GetFromJsonAsync<JsonElement>("/api/devices");
            var own = devices.EnumerateArray().Single(d => d.GetProperty("code").GetString() == "C02");
            Assert.Equal(algiers, own.GetProperty("branchId").GetGuid());
            Assert.True(own.GetProperty("isActive").GetBoolean());
            // The till is named after its cashier, whose name is their employee's (2026-10-05).
            Assert.Equal("caisse.a", own.GetProperty("name").GetString());

            // The list shows the till beside the cashier; a manager has none.
            var manager = await CreatedAsync(await CreateUserAsync(host, admin, "gerant.a", UserRole.Manager, algiers));
            Assert.Equal(JsonValueKind.Null, manager.GetProperty("tillCode").ValueKind);
            var users = await admin.GetFromJsonAsync<JsonElement>("/api/users");
            Assert.Equal("C02", users.EnumerateArray().Single(u => u.GetProperty("username").GetString() == host.SignInName("caisse.a")).GetProperty("tillCode").GetString());
        }
    }

    [Fact]
    public async Task A_cashier_is_not_placed_on_the_main_warehouse_and_is_told_why()
    {
        using var host = new PosHost();
        var (admin, _) = await AdminAsync(host);
        using (admin)
        {
            var main = await MainBranchAsync(admin);
            var refused = await CreateUserAsync(host, admin, "caisse.depot", UserRole.Cashier, main);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(CashierTills.CashierOnWarehouseCode, await CodeOfAsync(refused));

            // A manager works at the warehouse as before, with no till.
            Assert.Equal(HttpStatusCode.Created, (await CreateUserAsync(host, admin, "gerant.depot", UserRole.Manager, main)).StatusCode);

            // Moving a cashier onto it is refused too, and leaves them where they were.
            var algiers = await SellingBranchAsync(admin, "Alger");
            var cashier = await CreatedAsync(await CreateUserAsync(host, admin, "caisse.mobile", UserRole.Cashier, algiers));
            var moved = await admin.PutAsJsonAsync($"/api/users/{cashier.GetProperty("id").GetGuid()}",
                new { fullName = "Essai", role = "Cashier", isActive = true, branchId = main });
            Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
            Assert.Equal(CashierTills.CashierOnWarehouseCode, await CodeOfAsync(moved));
        }
    }

    [Fact]
    public async Task The_till_follows_its_cashier_moved_switched_off_back_on_and_retired()
    {
        using var host = new PosHost();
        var (admin, _) = await AdminAsync(host);
        using (admin)
        {
            var algiers = await SellingBranchAsync(admin, "Alger");
            var oran = await SellingBranchAsync(admin, "Oran");
            var cashier = await CreatedAsync(await CreateUserAsync(host, admin, "caisse.c", UserRole.Cashier, algiers));
            var id = cashier.GetProperty("id").GetGuid();

            async Task<JsonElement> PutAsync(bool active, Guid branch)
            {
                var put = await admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "Essai", role = "Cashier", isActive = active, branchId = branch });
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);
                return (await put.Content.ReadFromJsonAsync<JsonElement>()).Clone();
            }

            await PutAsync(true, oran);
            await using (var db = Open(host))
            {
                var till = await db.Devices.SingleAsync(d => d.UserId == id);
                Assert.Equal(oran, till.BranchId);
                Assert.Equal("C02", till.Code); // moved, not replaced
            }

            Assert.False((await PutAsync(false, oran)).GetProperty("tillActive").GetBoolean());
            Assert.True((await PutAsync(true, oran)).GetProperty("tillActive").GetBoolean());

            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{id}")).StatusCode);
            await using (var db = Open(host))
            {
                var till = await db.Devices.SingleAsync(d => d.UserId == id);
                Assert.False(till.IsActive); // switched off, never deleted: its receipts stay in the books
            }
        }
    }

    // ---------- signing in ----------

    [Fact]
    public async Task A_cashier_signs_in_online_with_no_code_on_their_own_till()
    {
        using var host = new PosHost();
        var (admin, _) = await AdminAsync(host);
        Guid algiers;
        using (admin)
        {
            algiers = await SellingBranchAsync(admin, "Alger");
            await CreatedAsync(await CreateUserAsync(host, admin, "caisse.d", UserRole.Cashier, algiers));
        }

        var (cashier, body) = await SignedInAsync(host, "caisse.d", Password, null);
        using (cashier)
        {
            Assert.Equal("C02", body.GetProperty("deviceCode").GetString());
            Assert.Equal(algiers, body.GetProperty("deviceBranchId").GetGuid());
            Assert.Equal(0, body.GetProperty("lastLocalSeq").GetInt64());

            var user = body.GetProperty("user").GetProperty("id").GetGuid();
            var push = await cashier.PostAsJsonAsync("/api/sync/push", new { deviceCode = "C02", sales = new[] { Sale(Guid.NewGuid(), 1, "C02-000001", user) } });
            Assert.Equal(1, (await push.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
        }

        // The next sign-in, from any computer, continues after the number the server holds.
        var (again, next) = await SignedInAsync(host, "caisse.d", Password, "");
        using (again) Assert.Equal(1, next.GetProperty("lastLocalSeq").GetInt64());
    }

    [Fact]
    public async Task A_cashier_from_before_own_tills_gets_one_at_their_first_sign_in_without_a_code()
    {
        using var host = new PosHost();
        var (admin, _) = await AdminAsync(host);
        Guid id;
        using (admin)
        {
            var algiers = await SellingBranchAsync(admin, "Alger");
            id = (await CreatedAsync(await CreateUserAsync(host, admin, "caisse.ancien", UserRole.Cashier, algiers))).GetProperty("id").GetGuid();
        }
        // Older data: the cashier exists, the till does not.
        await using (var db = Open(host))
        {
            db.Devices.RemoveRange(db.Devices.Where(d => d.UserId == id));
            await db.SaveChangesAsync();
        }

        var (cashier, body) = await SignedInAsync(host, "caisse.ancien", Password, null);
        // C02 had no row left and no sale in the books, so it is free again; a code with sales never is
        // (The_next_code_follows_every_till_ever_used_and_never_reuses_one).
        using (cashier) Assert.Equal("C02", body.GetProperty("deviceCode").GetString());
        await using (var db = Open(host))
            Assert.Single(await db.Devices.Where(d => d.UserId == id && d.IsActive).ToListAsync());
    }

    [Fact]
    public async Task A_shared_counter_till_is_still_opened_by_its_code()
    {
        using var host = new PosHost();
        var (admin, _) = await AdminAsync(host);
        using (admin)
        {
            var algiers = await SellingBranchAsync(admin, "Alger");
            await CreatedAsync(await CreateUserAsync(host, admin, "caisse.e", UserRole.Cashier, algiers));
            var shared = await admin.PostAsJsonAsync("/api/devices", new { code = "COMPTOIR", name = "Comptoir", branchId = algiers, isActive = true });
            Assert.Equal(HttpStatusCode.Created, shared.StatusCode);
        }

        var (cashier, body) = await SignedInAsync(host, "caisse.e", Password, "comptoir");
        using (cashier) Assert.Equal("COMPTOIR", body.GetProperty("deviceCode").GetString());
    }

    [Fact]
    public async Task A_cashier_who_can_have_no_till_is_told_so_in_words()
    {
        using var host = new PosHost();
        // A legacy cashier left on the main warehouse, which hosts no till.
        await using (var db = Open(host))
        {
            var main = await db.Branches.SingleAsync(b => b.IsMain && b.IsWarehouse);
            var role = await db.Roles.SingleAsync(r => r.Scope == RoleScope.TillBranch && r.IsSystem);
            db.Users.Add(new User
            {
                Username = "caisse.depot", FullName = "Dépôt", Role = UserRole.Cashier, RoleId = role.Id, IsActive = true,
                BranchId = main.Id, PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            });
            await db.SaveChangesAsync();
        }

        using var client = host.CreateClient();
        var refused = await LoginAsync(client, "caisse.depot", Password, null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(BranchScope.TillRequiredCode, await CodeOfAsync(refused));
        Assert.Contains("no till of its own", await refused.Content.ReadAsStringAsync());
    }

    // ---------- receipt numbers ----------

    [Fact]
    public async Task A_receipt_number_used_twice_on_one_till_is_recorded_not_refused()
    {
        using var host = new PosHost();
        var (admin, _) = await AdminAsync(host);
        using (admin)
        {
            var algiers = await SellingBranchAsync(admin, "Alger");
            await CreatedAsync(await CreateUserAsync(host, admin, "caisse.f", UserRole.Cashier, algiers));
        }

        // The same cashier's till open on two computers, both selling before either signs in again.
        var (shop, body) = await SignedInAsync(host, "caisse.f", Password, null);
        var (tablet, _) = await SignedInAsync(host, "caisse.f", Password, null);
        var user = body.GetProperty("user").GetProperty("id").GetGuid();
        using (shop)
        using (tablet)
        {
            var first = await shop.PostAsJsonAsync("/api/sync/push", new { deviceCode = "C02", sales = new[] { Sale(Guid.NewGuid(), 5, "C02-000005", user) } });
            Assert.Equal(1, (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());

            var secondId = Guid.NewGuid();
            var second = await tablet.PostAsJsonAsync("/api/sync/push", new { deviceCode = "C02", sales = new[] { Sale(secondId, 5, "C02-000005", user) } });
            var result = (await second.Content.ReadFromJsonAsync<JsonElement>()).Clone();
            Assert.Equal(1, result.GetProperty("accepted").GetInt32());
            Assert.Equal(0, result.GetProperty("rejected").GetInt32());
            Assert.Contains("already been used", result.GetProperty("results")[0].GetProperty("message").GetString());

            await using var db = Open(host);
            var stored = await db.Sales.SingleAsync(s => s.Id == secondId);
            Assert.Equal(6, stored.LocalSeq);
            Assert.Equal("C02-000005/R6", stored.Reference);
        }
    }
}
