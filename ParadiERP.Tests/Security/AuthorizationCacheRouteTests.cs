using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Authorization;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ParadiERP.Tests.Security;

/// <summary>
/// AUTH-1 Step 3 over HTTP: the server keeps each user's decision between requests, and every change
/// to what they may do is still seen on their very next request — removals first of all. Each test
/// also reads the cache's counters, so a pass cannot come from the cache simply not being used.
/// </summary>
[Collection(HostCollection.Name)]
public class AuthorizationCacheRouteTests
{
    private const string Password = "Essai@2026";

    private static async Task<HttpClient> SignedInAsync(PosHost host, string username, string password)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = host.SignInName(username), password, deviceCode = "C01" });
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

    // An employee first, then their account; the host remembers the Login ID under the name.
    private static async Task<Guid> CreateUserAsync(PosHost host, HttpClient admin, string username, UserRole role, Guid branchId, params string[] permissions) =>
        (await TestUsers.CreateAsync(host, admin, username, role.ToString(), branchId, Password, permissions: permissions)).Id;

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static async Task ChangeAsync(PosHost host, Func<PosDbContext, Task> change)
    {
        await using var db = Db(host);
        await change(db);
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> MainBranchAsync(HttpClient admin)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        return branches.EnumerateArray().Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean()).GetProperty("id").GetGuid();
    }

    private static AuthorizationCache Cache(PosHost host) => host.Services.GetRequiredService<AuthorizationCache>();

    /// <summary>The status of one request, and whether the cache had to load for it.</summary>
    private static async Task<(HttpStatusCode Status, bool Loaded)> CallAsync(PosHost host, HttpClient client, string path)
    {
        var before = Cache(host).Statistics.Misses;
        var status = (await client.GetAsync(path)).StatusCode;
        return (status, Cache(host).Statistics.Misses != before);
    }

    [Fact]
    public async Task A_grant_or_a_role_permission_added_or_removed_is_seen_on_the_next_request_and_reloads_only_whom_it_concerns()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var main = await MainBranchAsync(admin);
        var first = await CreateUserAsync(host, admin, "gerant-a", UserRole.Manager, main);
        await CreateUserAsync(host, admin, "gerant-b", UserRole.Manager, main);
        using var a = await SignedInAsync(host, "gerant-a", Password);
        using var b = await SignedInAsync(host, "gerant-b", Password);
        const string Employees = "/api/hr/employees"; // payroll.manage: a grant, not the Manager role's

        // Warm: the second request of each is answered from the cache.
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(host, a, Employees)).Status);
        Assert.Equal((HttpStatusCode.Forbidden, false), await CallAsync(host, a, Employees));
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(host, b, Employees)).Status);
        Assert.Equal((HttpStatusCode.Forbidden, false), await CallAsync(host, b, Employees));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(host, admin, "/api/users")).Status);
        Assert.Equal((HttpStatusCode.OK, false), await CallAsync(host, admin, "/api/users"));

        // 1. A grant added: allowed on the next request. The other Manager's entry is untouched.
        await ChangeAsync(host, db =>
        {
            db.UserPermissions.Add(new UserPermission { UserId = first, PermissionKey = PermissionCatalog.PayrollManage });
            return Task.CompletedTask;
        });
        Assert.Equal((HttpStatusCode.OK, true), await CallAsync(host, a, Employees));
        Assert.Equal((HttpStatusCode.OK, false), await CallAsync(host, a, Employees));
        Assert.Equal((HttpStatusCode.Forbidden, false), await CallAsync(host, b, Employees));

        // 2. The grant removed: refused on the very next request, though it was cached as allowed.
        await ChangeAsync(host, async db =>
            db.UserPermissions.Remove(await db.UserPermissions.SingleAsync(p => p.UserId == first)));
        Assert.Equal((HttpStatusCode.Forbidden, true), await CallAsync(host, a, Employees));
        Assert.Equal((HttpStatusCode.Forbidden, false), await CallAsync(host, b, Employees));

        // 3. Added to the Manager role: every member has it on the next request; the Admin is not reloaded.
        await ChangeAsync(host, db =>
        {
            db.RolePermissions.Add(new RolePermission { RoleId = SystemRoles.ManagerId, PermissionKey = PermissionCatalog.PayrollManage });
            return Task.CompletedTask;
        });
        Assert.Equal((HttpStatusCode.OK, true), await CallAsync(host, a, Employees));
        Assert.Equal((HttpStatusCode.OK, true), await CallAsync(host, b, Employees));
        Assert.Equal((HttpStatusCode.OK, false), await CallAsync(host, admin, "/api/users"));

        // 4. Removed from the role: every member loses it on the next request.
        await ChangeAsync(host, async db => db.RolePermissions.Remove(await db.RolePermissions.SingleAsync(p =>
            p.RoleId == SystemRoles.ManagerId && p.PermissionKey == PermissionCatalog.PayrollManage)));
        Assert.Equal((HttpStatusCode.Forbidden, true), await CallAsync(host, a, Employees));
        Assert.Equal((HttpStatusCode.Forbidden, true), await CallAsync(host, b, Employees));
        Assert.Equal((HttpStatusCode.Forbidden, false), await CallAsync(host, b, Employees));
    }

    [Fact]
    public async Task A_change_of_role_changes_the_permissions_and_the_scope_with_no_old_decision_kept()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var main = await MainBranchAsync(admin);
        var id = await CreateUserAsync(host, admin, "gerant-r", UserRole.Manager, main);
        using var user = await SignedInAsync(host, "gerant-r", Password);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(host, user, "/api/ledger/accounts")).Status);
        Assert.Equal((HttpStatusCode.OK, false), await CallAsync(host, user, "/api/ledger/accounts"));

        // 5a. Moved to a role of the same scope: no sign-in, the new role's permissions on the next request.
        var keeper = new Role { Name = "Magasinier", Scope = RoleScope.AssignedBranch };
        await ChangeAsync(host, async db =>
        {
            db.Roles.Add(keeper);
            db.RolePermissions.Add(new RolePermission { RoleId = keeper.Id, PermissionKey = PermissionCatalog.InventoryView });
            (await db.Users.SingleAsync(u => u.Id == id)).RoleId = keeper.Id;
        });
        Assert.Equal((HttpStatusCode.Forbidden, true), await CallAsync(host, user, "/api/ledger/accounts"));
        Assert.Equal((HttpStatusCode.OK, false), await CallAsync(host, user, "/api/catalog/products/low-stock"));

        // 5b. Moved to a role of another scope: the session rule refuses the old token outright
        // (its role no longer matches), and the new sign-in carries the new scope and permissions.
        await ChangeAsync(host, async db => (await db.Users.SingleAsync(u => u.Id == id)).RoleId = SystemRoles.CashierId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/catalog/products/low-stock")).StatusCode);
        using var again = await SignedInAsync(host, "gerant-r", Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await again.GetAsync("/api/catalog/products/low-stock")).StatusCode);
        await using var db = Db(host);
        var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        Assert.Equal((SystemRoles.CashierId, UserRole.Cashier), (row.RoleId!.Value, row.Role));
    }

    [Fact]
    public async Task A_change_of_branch_scope_is_seen_on_the_next_request_and_the_old_scope_is_not_kept()
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
        var id = await CreateUserAsync(host, admin, "superviseur", UserRole.Manager, oran);

        // A custom role with the company's scope: every branch, and the choice of branch.
        var supervisor = new Role { Name = "Superviseur", Scope = RoleScope.Company };
        await ChangeAsync(host, async db =>
        {
            db.Roles.Add(supervisor);
            db.RolePermissions.Add(new RolePermission { RoleId = supervisor.Id, PermissionKey = PermissionCatalog.VouchersView });
            (await db.Users.SingleAsync(u => u.Id == id)).RoleId = supervisor.Id;
        });
        using var user = await SignedInAsync(host, "superviseur", Password);
        async Task<HashSet<Guid>> BranchesSeenAsync() =>
            (await user.GetFromJsonAsync<JsonElement>($"/api/payment-vouchers?branchId={main}&take=100"))
            .EnumerateArray().Select(v => v.GetProperty("branchId").GetGuid()).ToHashSet();

        Assert.Equal([main], await BranchesSeenAsync());
        Assert.Equal([main], await BranchesSeenAsync()); // from the cache

        // 6. Narrowed to the assigned branch: the next request is Oran's alone — the cached company scope is gone.
        var misses = Cache(host).Statistics.Misses;
        await ChangeAsync(host, async db => (await db.Roles.SingleAsync(r => r.Id == supervisor.Id)).Scope = RoleScope.AssignedBranch);
        Assert.Equal([oran], await BranchesSeenAsync());
        Assert.Equal(misses + 1, Cache(host).Statistics.Misses);
        Assert.Equal([oran], await BranchesSeenAsync());

        // The branch the user works at is read on every request, never cached: moving them moves the lock.
        await ChangeAsync(host, async db => (await db.Users.SingleAsync(u => u.Id == id)).BranchId = main);
        Assert.Equal([main], await BranchesSeenAsync());

        // And a role made inactive grants nothing of its own from the next request.
        await ChangeAsync(host, async db => (await db.Roles.SingleAsync(r => r.Id == supervisor.Id)).IsActive = false);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/payment-vouchers?take=5")).StatusCode);
    }

    [Fact]
    public async Task The_Admin_baseline_repair_and_any_version_change_replace_a_stale_decision()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(host, admin, "/api/users")).Status);
        Assert.Equal((HttpStatusCode.OK, false), await CallAsync(host, admin, "/api/users"));

        // A hand edit of the database that takes users.manage from the Admin role and bumps its
        // version, as every save does: the next request is decided on what the table now says.
        await using (var db = Db(host))
        {
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM RolePermissions WHERE RoleId = {0} AND PermissionKey = 'users.manage'", SystemRoles.AdminId);
            await db.Database.ExecuteSqlRawAsync("UPDATE Roles SET Version = Version + 1 WHERE Id = {0}", SystemRoles.AdminId);
        }
        Assert.Equal((HttpStatusCode.Forbidden, true), await CallAsync(host, admin, "/api/users"));
        Assert.Equal((HttpStatusCode.Forbidden, false), await CallAsync(host, admin, "/api/users"));

        // 7. The start-up repair puts the baseline back; its save bumps the version, so the cached
        // refusal is not used again.
        await using (var db = Db(host))
        {
            var repaired = await AuthorizationBootstrap.RunAsync(db);
            Assert.Equal((1, 0), (repaired.AdminPermissionsAdded, repaired.AdminPermissionsRemoved));
        }
        Assert.Equal((HttpStatusCode.OK, true), await CallAsync(host, admin, "/api/users"));
    }

    [Fact]
    public async Task Revoked_deactivated_and_deleted_sessions_are_refused_at_once_whatever_is_cached()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var main = await MainBranchAsync(admin);
        foreach (var name in new[] { "gerant-x", "gerant-y", "gerant-z" })
            await CreateUserAsync(host, admin, name, UserRole.Manager, main);
        await using var read = Db(host);
        var ids = await read.Users.Where(u => u.FullName.StartsWith("gerant-")).ToDictionaryAsync(u => u.FullName, u => u.Id);
        using var x = await SignedInAsync(host, "gerant-x", Password);
        using var y = await SignedInAsync(host, "gerant-y", Password);
        using var z = await SignedInAsync(host, "gerant-z", Password);
        foreach (var client in new[] { x, y, z })
        {
            Assert.Equal(HttpStatusCode.OK, (await CallAsync(host, client, "/api/ledger/accounts")).Status);
            Assert.Equal((HttpStatusCode.OK, false), await CallAsync(host, client, "/api/ledger/accounts"));
        }

        // 9. Signed out by the administrator (every session of the user).
        Assert.True((await admin.PostAsync($"/api/users/{ids["gerant-x"]}/revoke-sessions", null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await x.GetAsync("/api/ledger/accounts")).StatusCode);

        // Deactivated, and deleted.
        await ChangeAsync(host, async db => (await db.Users.SingleAsync(u => u.Id == ids["gerant-y"])).IsActive = false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await y.GetAsync("/api/ledger/accounts")).StatusCode);
        Assert.True((await admin.DeleteAsync($"/api/users/{ids["gerant-z"]}")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await z.GetAsync("/api/ledger/accounts")).StatusCode);

        // A fresh sign-in of a revoked user works, and is decided afresh.
        using var back = await SignedInAsync(host, "gerant-x", Password);
        Assert.Equal(HttpStatusCode.OK, (await back.GetAsync("/api/ledger/accounts")).StatusCode);
    }
}
