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
/// Administration › Users, the additional permissions (AUTH-1 §17.10, §17.9 "Roles"): any key of the catalogue given
/// to one account on top of its role. One member of a role gets a key and another member does not; taking it away
/// touches only that account; nobody gives or takes away a key they do not hold; an unknown key is refused; a screen
/// that opened the account before its grants changed is told, and changes nothing.
/// </summary>
[Collection(HostCollection.Name)]
public class UserAdditionalPermissionsTests
{
    private const string Password = "Essai@2026";

    private static async Task<HttpClient> SignedInAsync(PosHost host, string username, string password)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = host.SignInName(username), password, deviceCode = (string?)null });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await BodyAsync(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    private static async Task<Guid> RoleAsync(HttpClient admin, string name, string scope, params string[] permissions)
    {
        var created = await admin.PostAsJsonAsync("/api/roles", new { name, scope, permissions });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await BodyAsync(created)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AccountAsync(PosHost host, HttpClient admin, string name, Guid roleId, Guid branch, params string[] additional)
    {
        var employee = await TestUsers.EmployeeAsync(admin, name, branch);
        var created = await TestUsers.CreatedAsync(await admin.PostAsJsonAsync("/api/users", new
        {
            employeeId = employee, roleId, password = Password, branchId = branch, additionalPermissions = additional,
        }));
        host.Remember(name, created.LoginId);
        return created.Id;
    }

    private static async Task<JsonElement> RowAsync(HttpClient admin, Guid id) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/users")).EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == id).Clone();

    private static string[] Additional(JsonElement row) =>
        row.GetProperty("additionalPermissions").EnumerateArray().Select(k => k.GetString()!).ToArray();

    /// <summary>The users screen's save: the row as it was opened, with the whole set of additional keys.</summary>
    private static Task<HttpResponseMessage> SaveAsync(HttpClient admin, JsonElement row, Guid branch, string[] additional,
        Guid? roleId = null, long? version = null, object? extra = null) =>
        admin.PutAsJsonAsync($"/api/users/{row.GetProperty("id").GetGuid()}", new Dictionary<string, object?>
        {
            ["fullName"] = row.GetProperty("fullName").GetString(),
            ["role"] = null,
            ["roleId"] = roleId ?? row.GetProperty("roleId").GetGuid(),
            ["isActive"] = true,
            ["branchId"] = branch,
            ["additionalPermissions"] = additional,
            ["authorizationVersion"] = version ?? row.GetProperty("authorizationVersion").GetInt64(),
            // An older field sent beside the new one, which must then be ignored.
            ["sellsUntaxed"] = extra is null ? null : true,
        });

    [Fact]
    public async Task A_key_given_to_one_member_reaches_that_member_only_and_taking_it_away_touches_no_one_else()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var branch = await TestBranches.SellingAsync(admin, "Alger");
        var accountant = await RoleAsync(admin, "Comptable", "AssignedBranch", PermissionCatalog.AccountingView);
        // Created with an extra key straight away; their colleague on the same role without it.
        var first = await AccountAsync(host, admin, "compta-a", accountant, branch, PermissionCatalog.ProductsView);
        var second = await AccountAsync(host, admin, "compta-b", accountant, branch);

        var row = await RowAsync(admin, first);
        Assert.Equal(new[] { PermissionCatalog.ProductsView }, Additional(row));
        Assert.Empty(Additional(await RowAsync(admin, second)));
        using (var a = await SignedInAsync(host, "compta-a", Password))
        using (var b = await SignedInAsync(host, "compta-b", Password))
        {
            Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/catalog/products")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync("/api/catalog/products")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/ledger/accounts")).StatusCode);
        }

        // Any module: a key from payroll and one from manufacturing beside it. A key the role already gives is not
        // stored a second time; a legacy key goes to the legacy column too, which a screen from before reads.
        var grown = await SaveAsync(admin, row, branch,
            [PermissionCatalog.ProductsView, PermissionCatalog.PayrollManage, PermissionCatalog.ManufacturingView, PermissionCatalog.AccountingView]);
        Assert.True(grown.IsSuccessStatusCode, await grown.Content.ReadAsStringAsync());
        var saved = await BodyAsync(grown);
        Assert.Equal(new[] { PermissionCatalog.ProductsView, PermissionCatalog.PayrollManage, PermissionCatalog.ManufacturingView }, Additional(saved));
        Assert.Equal(new[] { PermissionCatalog.PayrollManage }, saved.GetProperty("permissions").EnumerateArray().Select(k => k.GetString()));
        Assert.True(saved.GetProperty("authorizationVersion").GetInt64() > row.GetProperty("authorizationVersion").GetInt64());

        // Taken away again: only that member loses it.
        var shrunk = await SaveAsync(admin, saved, branch, [PermissionCatalog.PayrollManage]);
        Assert.True(shrunk.IsSuccessStatusCode, await shrunk.Content.ReadAsStringAsync());
        var after = await BodyAsync(shrunk);
        Assert.Equal(new[] { PermissionCatalog.PayrollManage }, Additional(after));
        Assert.Equal(new[] { PermissionCatalog.PayrollManage }, after.GetProperty("permissions").EnumerateArray().Select(k => k.GetString()));
        using (var a = await SignedInAsync(host, "compta-a", Password))
            Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync("/api/catalog/products")).StatusCode);
        Assert.Empty(Additional(await RowAsync(admin, second)));
    }

    [Fact]
    public async Task A_cashier_given_customers_create_creates_a_customer_and_their_colleague_still_cannot()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var branch = await TestBranches.SellingAsync(admin, "Oran");
        await AccountAsync(host, admin, "caisse-a", SystemRoles.CashierId, branch, PermissionCatalog.CustomersCreate, PermissionCatalog.SalesUntaxed);
        var other = await AccountAsync(host, admin, "caisse-b", SystemRoles.CashierId, branch);

        var customer = new
        {
            fullName = "Client Comptoir", phone = (string?)null, address = (string?)null, taxId = (string?)null,
            email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person",
        };
        using (var a = await SignedInAsync(host, "caisse-a", Password))
            Assert.Equal(HttpStatusCode.Created, (await a.PostAsJsonAsync("/api/catalog/customers", customer)).StatusCode);
        using (var b = await SignedInAsync(host, "caisse-b", Password))
            Assert.Equal(HttpStatusCode.Forbidden, (await b.PostAsJsonAsync("/api/catalog/customers", customer)).StatusCode);

        // "Sell untaxed" is one key among the others now; the older field sent beside the set is ignored.
        var row = await RowAsync(admin, other);
        Assert.False(row.GetProperty("sellsUntaxed").GetBoolean());
        var saved = await SaveAsync(admin, row, branch, [], extra: true);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.False((await BodyAsync(saved)).GetProperty("sellsUntaxed").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_key_and_a_screen_opened_before_a_change_are_refused_and_change_nothing()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var branch = await TestBranches.SellingAsync(admin, "Blida");
        var user = await AccountAsync(host, admin, "vendeur", SystemRoles.CashierId, branch);
        var opened = await RowAsync(admin, user);

        var stray = await SaveAsync(admin, opened, branch, ["sales.everything"]);
        Assert.Equal((HttpStatusCode.BadRequest, "unknown_permission"), (stray.StatusCode, await CodeAsync(stray)));
        var created = await admin.PostAsJsonAsync("/api/users", new
        {
            employeeId = await TestUsers.EmployeeAsync(admin, "fantome", branch), roleId = SystemRoles.CashierId, password = Password,
            branchId = branch, additionalPermissions = new[] { "sales.everything" },
        });
        Assert.Equal((HttpStatusCode.BadRequest, "unknown_permission"), (created.StatusCode, await CodeAsync(created)));

        // Someone gives a manufacturing key on its own page meanwhile; the users screen still holds the old view.
        await using (var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options))
        {
            db.UserPermissions.Add(new UserPermission { UserId = user, PermissionKey = PermissionCatalog.ManufacturingView, GrantedBy = "test" });
            await db.SaveChangesAsync();
        }
        var stale = await SaveAsync(admin, opened, branch, [PermissionCatalog.GiftCardsView]);
        Assert.Equal((HttpStatusCode.Conflict, "user_changed"), (stale.StatusCode, await CodeAsync(stale)));
        Assert.Equal(new[] { PermissionCatalog.ManufacturingView }, Additional(await RowAsync(admin, user)));

        // Opened again, the save goes through and keeps what it now shows.
        var fresh = await RowAsync(admin, user);
        var saved = await SaveAsync(admin, fresh, branch, [PermissionCatalog.ManufacturingView, PermissionCatalog.GiftCardsView]);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal(new[] { PermissionCatalog.GiftCardsView, PermissionCatalog.ManufacturingView }, Additional(await BodyAsync(saved)));
    }

    [Fact]
    public async Task Nobody_gives_or_takes_away_a_key_they_do_not_hold()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var branch = await TestBranches.SellingAsync(admin, "Setif");
        // Someone trusted with users, products and payroll, but not the books.
        var delegated = await RoleAsync(admin, "Responsable du personnel", "AssignedBranch",
            PermissionCatalog.UsersManage, PermissionCatalog.RolesManage, PermissionCatalog.ProductsView, PermissionCatalog.PayrollManage);
        await AccountAsync(host, admin, "rh", delegated, branch);
        var target = await AccountAsync(host, admin, "employe", SystemRoles.CashierId, branch, PermissionCatalog.AccountingView);

        using var hr = await SignedInAsync(host, "rh", Password);
        var row = await RowAsync(hr, target);
        // Taking away the books, which they do not hold: refused, and so is giving them.
        var taken = await SaveAsync(hr, row, branch, []);
        Assert.Equal((HttpStatusCode.Forbidden, "grant_beyond_own"), (taken.StatusCode, await CodeAsync(taken)));
        var given = await SaveAsync(hr, row, branch, [PermissionCatalog.AccountingView, PermissionCatalog.ReportsAccounting]);
        Assert.Equal((HttpStatusCode.Forbidden, "grant_beyond_own"), (given.StatusCode, await CodeAsync(given)));
        Assert.Equal(new[] { PermissionCatalog.AccountingView }, Additional(await RowAsync(admin, target)));

        // Within their own: products, and attendance, which payroll covers although they do not hold it by name.
        var within = await SaveAsync(hr, row, branch, [PermissionCatalog.AccountingView, PermissionCatalog.ProductsView, PermissionCatalog.HrAttendance]);
        Assert.True(within.IsSuccessStatusCode, await within.Content.ReadAsStringAsync());
        Assert.Equal(new[] { PermissionCatalog.ProductsView, PermissionCatalog.AccountingView, PermissionCatalog.HrAttendance },
            Additional(await BodyAsync(within)));

        // A new account with a key beyond their own: refused before anything is written.
        var employee = await TestUsers.EmployeeAsync(admin, "nouveau", branch);
        var beyond = await hr.PostAsJsonAsync("/api/users", new
        {
            employeeId = employee, roleId = SystemRoles.CashierId, password = Password, branchId = branch,
            additionalPermissions = new[] { PermissionCatalog.AccountingClose },
        });
        Assert.Equal((HttpStatusCode.Forbidden, "grant_beyond_own"), (beyond.StatusCode, await CodeAsync(beyond)));
    }
}
