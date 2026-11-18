using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// AUTH-1 Step 2 over HTTP, on a real host: every request is decided by the role tables as they stand
/// at that moment — a user's additional grant, a change to their role's permissions — and by nothing
/// the token or a screen says. Branch scope stays its own rule, and a route refuses a direct call
/// whatever a screen would have offered.
/// </summary>
[Collection(HostCollection.Name)]
public class RoleAuthorizationRouteTests
{
    private const string Password = "Essai@2026";

    // A cashier signs in on their own till (deviceCode null), everyone else on C01 (2026-10-05).
    private static async Task<HttpClient> SignedInAsync(PosHost host, string username, string password, string? deviceCode = "C01")
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = host.SignInName(username), password, deviceCode });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        return (root.TryGetProperty("voucher", out var voucher) ? voucher : root).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateUserAsync(PosHost host, HttpClient admin, string username, UserRole role, Guid branchId, params string[] permissions) =>
        // An employee first, then their account (2026-10-05); the host remembers its Login ID by name.
        (await TestUsers.CreateAsync(host, admin, username, role.ToString(), branchId, Password, permissions: permissions)).Id;


    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static async Task<Guid> MainBranchAsync(HttpClient admin)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        return branches.EnumerateArray().Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task An_additional_grant_and_a_role_change_are_read_on_the_very_next_request_and_only_for_whom_they_apply()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var main = await MainBranchAsync(admin);
        var first = await CreateUserAsync(host, admin, "gerant-a", UserRole.Manager, main);
        await CreateUserAsync(host, admin, "gerant-b", UserRole.Manager, main);
        using var a = await SignedInAsync(host, "gerant-a", Password);
        using var b = await SignedInAsync(host, "gerant-b", Password);

        // payroll.manage is a grant, not the Manager role's.
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/hr/employees")).StatusCode);

        // Granted to one Manager in the table — no new token, no sign-in: the next request reads it.
        await using (var db = Db(host))
        {
            db.UserPermissions.Add(new UserPermission { UserId = first, PermissionKey = PermissionCatalog.PayrollManage });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/hr/employees")).StatusCode);
        // Another Manager on the same role does not receive it.
        Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync("/api/hr/employees")).StatusCode);

        // Taken back: only that user loses it.
        await using (var db = Db(host))
        {
            db.UserPermissions.Remove(await db.UserPermissions.SingleAsync(p => p.UserId == first));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/hr/employees")).StatusCode);

        // A change to the Manager role reaches every member at once — and only by the role's table.
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/ledger/accounts")).StatusCode);
        await using (var db = Db(host))
        {
            db.RolePermissions.Remove(await db.RolePermissions.SingleAsync(p =>
                p.RoleId == SystemRoles.ManagerId && p.PermissionKey == PermissionCatalog.AccountingView));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/ledger/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync("/api/ledger/accounts")).StatusCode);
        // The Admin's baseline is its own and did not move.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/ledger/accounts")).StatusCode);

        await using (var db = Db(host))
        {
            db.RolePermissions.Add(new RolePermission { RoleId = SystemRoles.ManagerId, PermissionKey = PermissionCatalog.AccountingView });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/ledger/accounts")).StatusCode);
    }

    [Fact]
    public async Task A_direct_call_without_the_permission_is_refused_whatever_a_screen_would_offer()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var main = await MainBranchAsync(admin);
        // A cashier works at a selling branch, not the main warehouse (2026-10-05).
        await CreateUserAsync(host, admin, "caisse-box", UserRole.Cashier, await TestBranches.SellingAsync(admin, "Ventes"), Permissions.CashBox);
        using var cashier = await SignedInAsync(host, "caisse-box", Password, null);

        // The cash box opens the voucher list, and nothing beside it.
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync("/api/payment-vouchers?take=5")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await cashier.PostAsJsonAsync($"/api/payment-vouchers/{Guid.NewGuid()}/approve", new { note = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await cashier.PostAsJsonAsync("/api/catalog/customers", new { fullName = "Client essai", code = "X1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/ledger/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/hr/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/users")).StatusCode);

        // A Cashier given customers.create directly may create a customer; the role is unchanged.
        await using (var db = Db(host))
        {
            var id = await db.Users.Where(u => u.Username == host.SignInName("caisse-box")).Select(u => u.Id).SingleAsync();
            db.UserPermissions.Add(new UserPermission { UserId = id, PermissionKey = PermissionCatalog.CustomersCreate });
            await db.SaveChangesAsync();
        }
        Assert.NotEqual(HttpStatusCode.Forbidden,
            (await cashier.PostAsJsonAsync("/api/catalog/customers", new { fullName = "Client essai", code = "X1" })).StatusCode);
    }

    [Fact]
    public async Task Branch_scope_is_its_own_rule_and_a_permission_does_not_widen_it()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var main = await MainBranchAsync(admin);
        var oran = await IdOfAsync(await admin.PostAsJsonAsync("/api/catalog/branches", new
        {
            nameAr = "وهران", nameFr = "Oran", nameEn = "Oran", address = "", phone = "",
            isMain = false, allowNegativeStock = false, isActive = true,
        }));
        foreach (var branch in new[] { main, oran })
            await IdOfAsync(await admin.PostAsJsonAsync("/api/payment-vouchers", new
            {
                branchId = branch, kind = (int)PaymentVoucherKind.Expense, amount = 100m, methodKey = "cash",
                payeeName = "Naftal", expenseCategory = "fuel",
            }));
        await CreateUserAsync(host, admin, "gerant-oran", UserRole.Manager, oran, Permissions.ViewOtherBranchStock, Permissions.ManagePayroll);
        // A manager bound to Oran opens no Algiers till (security review, 2026-10-10): an office session.
        using var manager = await SignedInAsync(host, "gerant-oran", Password, deviceCode: null);

        // Every voucher permission there is, and still Oran's list when the main branch is asked for.
        var list = await manager.GetFromJsonAsync<JsonElement>($"/api/payment-vouchers?branchId={main}&take=100");
        Assert.NotEmpty(list.EnumerateArray());
        Assert.All(list.EnumerateArray(), v => Assert.Equal(oran, v.GetProperty("branchId").GetGuid()));
        // And the Admin, whose scope is the company, reads the branch asked for.
        var adminList = await admin.GetFromJsonAsync<JsonElement>($"/api/payment-vouchers?branchId={main}&take=100");
        Assert.All(adminList.EnumerateArray(), v => Assert.Equal(main, v.GetProperty("branchId").GetGuid()));
    }

    [Fact]
    public async Task The_users_screen_writes_the_role_tables_and_the_legacy_columns_never_write_back()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var main = await MainBranchAsync(admin);
        var id = await CreateUserAsync(host, admin, "caisse-x", UserRole.Cashier, await TestBranches.SellingAsync(admin, "Ventes"), Permissions.CashBox, Permissions.ManagePayroll);

        await using (var db = Db(host))
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
            Assert.Equal(SystemRoles.CashierId, user.RoleId);
            Assert.Equal([Permissions.CashBox], await db.UserPermissions.Where(p => p.UserId == id).Select(p => p.PermissionKey).ToListAsync());
            Assert.Contains(await db.AuthorizationReport.Where(r => r.UserId == id).Select(r => r.PermissionKey).ToListAsync(), k => k == Permissions.ManagePayroll);

            // A write to the legacy column alone is refused: it may never say what the user may do.
            var row = await db.Users.SingleAsync(u => u.Id == id);
            row.Role = UserRole.Admin;
            await Assert.ThrowsAsync<AlgeriaPos.Infrastructure.Authorization.AuthorizationIntegrityException>(() => db.SaveChangesAsync());
        }
        await using (var db = Db(host))
        {
            // Nor does the legacy grants column: changing it moves nothing in the tables.
            var row = await db.Users.SingleAsync(u => u.Id == id);
            row.Permissions = "treasury.cash_box,payroll.manage,stock.view_other_branches";
            await db.SaveChangesAsync();
            Assert.Equal([Permissions.CashBox], await db.UserPermissions.Where(p => p.UserId == id).Select(p => p.PermissionKey).ToListAsync());
        }

        // Promoted through the screen: the role table moves, the grants follow the rule, the legacy role follows the role.
        var promoted = await admin.PutAsJsonAsync($"/api/users/{id}", new
        {
            fullName = "Essai caisse-x", role = "Manager", isActive = true, permissions = new[] { Permissions.ManagePayroll },
        });
        Assert.True(promoted.IsSuccessStatusCode, await promoted.Content.ReadAsStringAsync());
        await using (var db = Db(host))
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
            Assert.Equal((SystemRoles.ManagerId, UserRole.Manager), (user.RoleId!.Value, user.Role));
            Assert.Equal([Permissions.ManagePayroll], await db.UserPermissions.Where(p => p.UserId == id).Select(p => p.PermissionKey).ToListAsync());
        }
    }
}
