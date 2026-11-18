using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.API.Authorization;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// Administration › Roles &amp; permissions, the API (AUTH-1 Step 5, REDESIGN_PLAN.md §17.8–§17.9): roles the
/// company creates, assigned to accounts, decide their next request with no sign-out; the Admin role is
/// read-only, the Manager and Cashier roles change only in their permissions, a role with accounts on it
/// keeps its scope, stays on and stays; nobody hands out more than they hold; names are unique case aside.
/// </summary>
[Collection(HostCollection.Name)]
public class RolesApiTests
{
    private const string Password = "Essai@2026";

    private static async Task<(HttpClient Client, JsonElement Body)> SignedInAsync(PosHost host, string username, string password)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = host.SignInName(username), password, deviceCode = (string?)null });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.Clone();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return (client, body);
    }

    private static async Task<JsonElement> RolesAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/roles")).Clone();

    private static JsonElement RoleOf(JsonElement roles, Guid id) =>
        roles.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await BodyAsync(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    private static async Task<Guid> CreatedRoleAsync(HttpClient admin, object request)
    {
        var created = await admin.PostAsJsonAsync("/api/roles", request);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return (await BodyAsync(created)).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid Id, string LoginId)> AccountOnAsync(PosHost host, HttpClient admin, string name, Guid roleId, Guid branch)
    {
        var employee = await TestUsers.EmployeeAsync(admin, name, branch);
        var created = await TestUsers.CreatedAsync(await admin.PostAsJsonAsync("/api/users", new
        {
            employeeId = employee, roleId, password = Password, pin = (string?)null, permissions = Array.Empty<string>(), branchId = branch,
        }));
        host.Remember(name, created.LoginId);
        return created;
    }

    private static Task<HttpResponseMessage> SaveUserAsync(HttpClient admin, JsonElement row, Guid branch, Guid? roleId, string? legacyRole = null) =>
        admin.PutAsJsonAsync($"/api/users/{row.GetProperty("id").GetGuid()}", roleId is Guid id
            ? (object)new { fullName = row.GetProperty("fullName").GetString(), role = (string?)null, roleId = id, isActive = true,
                permissions = Array.Empty<string>(), branchId = branch }
            : new { fullName = row.GetProperty("fullName").GetString(), role = legacyRole ?? row.GetProperty("role").GetString(), isActive = true,
                permissions = Array.Empty<string>(), branchId = branch });

    private static async Task<JsonElement> UserRowAsync(HttpClient admin, Guid id) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/users")).EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == id).Clone();

    [Fact]
    public async Task The_catalogue_and_the_system_roles_are_listed_with_what_each_lets_the_screen_change()
    {
        using var host = new PosHost();
        var (admin, _) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        using (admin)
        {
            var catalogue = await admin.GetFromJsonAsync<JsonElement>("/api/permissions");
            var keys = catalogue.EnumerateArray().SelectMany(m => m.GetProperty("keys").EnumerateArray().Select(k => k.GetString())).ToList();
            Assert.Equal(PermissionCatalog.All.Select(p => p.Key), keys);
            Assert.Equal(PermissionCatalog.Modules, catalogue.EnumerateArray().Select(m => m.GetProperty("module").GetString()));

            var roles = await RolesAsync(admin);
            Assert.Equal(new[] { SystemRoles.AdminId, SystemRoles.ManagerId, SystemRoles.CashierId },
                roles.EnumerateArray().Take(3).Select(r => r.GetProperty("id").GetGuid()));
            var adminRole = RoleOf(roles, SystemRoles.AdminId);
            Assert.False(adminRole.GetProperty("permissionsEditable").GetBoolean());
            Assert.False(adminRole.GetProperty("detailsEditable").GetBoolean());
            Assert.Equal(AdminBaseline.Included.Count, adminRole.GetProperty("permissions").GetArrayLength());
            Assert.True(adminRole.GetProperty("members").GetInt32() >= 1);
            var manager = RoleOf(roles, SystemRoles.ManagerId);
            Assert.True(manager.GetProperty("permissionsEditable").GetBoolean());
            Assert.False(manager.GetProperty("detailsEditable").GetBoolean());
            Assert.False(manager.GetProperty("deletable").GetBoolean());
            Assert.Equal("AssignedBranch", manager.GetProperty("scope").GetString());
        }
    }

    [Fact]
    public async Task A_role_the_company_creates_decides_its_members_access_on_their_next_request()
    {
        using var host = new PosHost();
        var (admin, _) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        using (admin)
        {
            var branch = await TestBranches.SellingAsync(admin, "Alger");
            var accountant = await CreatedRoleAsync(admin, new
            {
                name = "Comptable", description = "Tient les livres.", scope = "AssignedBranch",
                permissions = new[] { PermissionCatalog.AccountingView, PermissionCatalog.ReportsAccounting },
            });
            var (member, _) = await AccountOnAsync(host, admin, "compta", accountant, branch);

            // The users list names the role; the login reports it, its scope and every key it gives.
            var row = await UserRowAsync(admin, member);
            Assert.Equal((accountant, "Comptable", "AssignedBranch", "Manager"),
                (row.GetProperty("roleId").GetGuid(), row.GetProperty("roleName").GetString(), row.GetProperty("roleScope").GetString(),
                    row.GetProperty("role").GetString()));
            var (client, login) = await SignedInAsync(host, "compta", Password);
            using (client)
            {
                var user = login.GetProperty("user");
                Assert.Equal("Comptable", user.GetProperty("roleName").GetString());
                Assert.Equal("AssignedBranch", user.GetProperty("scope").GetString());
                Assert.Equal(new[] { PermissionCatalog.AccountingView, PermissionCatalog.ReportsAccounting },
                    user.GetProperty("permissionKeys").EnumerateArray().Select(k => k.GetString()));

                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/ledger/accounts")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/catalog/products")).StatusCode);

                // The role gains a key: the member's very next request has it, signed in as they were.
                var opened = RoleOf(await RolesAsync(admin), accountant);
                var edited = await admin.PutAsJsonAsync($"/api/roles/{accountant}", new
                {
                    permissions = new[] { PermissionCatalog.AccountingView, PermissionCatalog.ReportsAccounting, PermissionCatalog.ProductsView },
                    version = opened.GetProperty("version").GetInt64(),
                });
                Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());
                Assert.True((await BodyAsync(edited)).GetProperty("version").GetInt64() > opened.GetProperty("version").GetInt64());
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/products")).StatusCode);

                // A screen that opened the role before that edit is told, and changes nothing.
                var stale = await admin.PutAsJsonAsync($"/api/roles/{accountant}", new
                {
                    permissions = Array.Empty<string>(), version = opened.GetProperty("version").GetInt64(),
                });
                Assert.Equal((HttpStatusCode.Conflict, "role_changed"), (stale.StatusCode, await CodeAsync(stale)));
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/products")).StatusCode);

                // Members are listed, and an older users screen saving them under the name their scope reads as
                // ("Manager") leaves them on the role.
                var members = await admin.GetFromJsonAsync<JsonElement>($"/api/roles/{accountant}/members");
                Assert.Equal(member, members.EnumerateArray().Single().GetProperty("id").GetGuid());
                Assert.Equal(HttpStatusCode.OK, (await SaveUserAsync(admin, row, branch, null, "Manager")).StatusCode);
                Assert.Equal(accountant, (await UserRowAsync(admin, member)).GetProperty("roleId").GetGuid());
            }
        }
    }

    [Fact]
    public async Task The_system_roles_are_protected_and_a_role_with_accounts_keeps_its_scope_stays_on_and_stays()
    {
        using var host = new PosHost();
        var (admin, _) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        using (admin)
        {
            var branch = await TestBranches.SellingAsync(admin, "Alger");

            var adminEdit = await admin.PutAsJsonAsync($"/api/roles/{SystemRoles.AdminId}", new { permissions = new[] { PermissionCatalog.SalesView } });
            Assert.Equal((HttpStatusCode.Conflict, "admin_role_protected"), (adminEdit.StatusCode, await CodeAsync(adminEdit)));
            var rename = await admin.PutAsJsonAsync($"/api/roles/{SystemRoles.ManagerId}", new { name = "Chef d'agence" });
            Assert.Equal((HttpStatusCode.BadRequest, "system_role_fixed"), (rename.StatusCode, await CodeAsync(rename)));
            var deleteSystem = await admin.DeleteAsync($"/api/roles/{SystemRoles.CashierId}");
            Assert.Equal((HttpStatusCode.Conflict, "system_role"), (deleteSystem.StatusCode, await CodeAsync(deleteSystem)));

            // The Manager and Cashier roles' permissions do change: the Cashier role gains the gift-card list.
            var cashier = RoleOf(await RolesAsync(admin), SystemRoles.CashierId);
            var keys = cashier.GetProperty("permissions").EnumerateArray().Select(k => k.GetString()!).Append(PermissionCatalog.GiftCardsView).ToArray();
            var grown = await admin.PutAsJsonAsync($"/api/roles/{SystemRoles.CashierId}", new { permissions = keys });
            Assert.True(grown.IsSuccessStatusCode, await grown.Content.ReadAsStringAsync());
            Assert.Contains(PermissionCatalog.GiftCardsView, (await BodyAsync(grown)).GetProperty("permissions").EnumerateArray().Select(k => k.GetString()));

            // A role with an account on it.
            var keeper = await CreatedRoleAsync(admin, new { name = "Magasinier", permissions = new[] { PermissionCatalog.InventoryView } });
            var (member, _) = await AccountOnAsync(host, admin, "magasin", keeper, branch);
            foreach (var change in new object[] { new { scope = "TillBranch" }, new { isActive = false } })
            {
                var refused = await admin.PutAsJsonAsync($"/api/roles/{keeper}", change);
                Assert.Equal((HttpStatusCode.Conflict, "role_has_members"), (refused.StatusCode, await CodeAsync(refused)));
            }
            var busy = await admin.DeleteAsync($"/api/roles/{keeper}");
            Assert.Equal((HttpStatusCode.Conflict, "role_has_members"), (busy.StatusCode, await CodeAsync(busy)));

            // Moved off it, the role may change scope and be switched off; a role no account ever named is deleted.
            Assert.Equal(HttpStatusCode.OK, (await SaveUserAsync(admin, await UserRowAsync(admin, member), branch, SystemRoles.ManagerId)).StatusCode);
            var moved = await admin.PutAsJsonAsync($"/api/roles/{keeper}", new { scope = "TillBranch", isActive = false });
            Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
            var once = await admin.DeleteAsync($"/api/roles/{keeper}");
            Assert.Equal(HttpStatusCode.NoContent, once.StatusCode);

            // A role only a removed account still names is switched off instead, never deleted.
            var temp = await CreatedRoleAsync(admin, new { name = "Saisonnier", permissions = Array.Empty<string>() });
            var (seasonal, _) = await AccountOnAsync(host, admin, "saison", temp, branch);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{seasonal}")).StatusCode);
            var history = await admin.DeleteAsync($"/api/roles/{temp}");
            Assert.Equal((HttpStatusCode.Conflict, "role_in_history"), (history.StatusCode, await CodeAsync(history)));
            Assert.False(RoleOf(await RolesAsync(admin), temp).GetProperty("deletable").GetBoolean());

            // A switched-off role is offered to nobody new.
            var off = await CreatedRoleAsync(admin, new { name = "En pause", isActive = false });
            var employee = await TestUsers.EmployeeAsync(admin, "pause", branch);
            var onOff = await admin.PostAsJsonAsync("/api/users", new { employeeId = employee, roleId = off, password = Password, branchId = branch });
            Assert.Equal((HttpStatusCode.BadRequest, "unknown_role"), (onOff.StatusCode, await CodeAsync(onOff)));
        }
    }

    [Fact]
    public async Task Names_are_unique_case_and_spacing_aside_and_only_catalogue_keys_are_stored()
    {
        using var host = new PosHost();
        var (admin, _) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        using (admin)
        {
            var first = await CreatedRoleAsync(admin, new { name = "Agent de location", permissions = new[] { PermissionCatalog.RentalsView } });
            foreach (var twin in new[] { "agent de location", "  AGENT   de location ", "Manager", "cashier" })
            {
                var refused = await admin.PostAsJsonAsync("/api/roles", new { name = twin });
                Assert.Equal((HttpStatusCode.Conflict, "role_name_taken"), (refused.StatusCode, await CodeAsync(refused)));
            }
            var renamed = await admin.PutAsJsonAsync($"/api/roles/{first}", new { name = "MANAGER" });
            Assert.Equal((HttpStatusCode.Conflict, "role_name_taken"), (renamed.StatusCode, await CodeAsync(renamed)));
            // Its own name, in other letters, is not a clash.
            var recased = await admin.PutAsJsonAsync($"/api/roles/{first}", new { name = "Agent De Location" });
            Assert.True(recased.IsSuccessStatusCode, await recased.Content.ReadAsStringAsync());

            var stray = await admin.PostAsJsonAsync("/api/roles", new { name = "Fantôme", permissions = new[] { "sales.everything" } });
            Assert.Equal((HttpStatusCode.BadRequest, "unknown_permission"), (stray.StatusCode, await CodeAsync(stray)));
            var blank = await admin.PostAsJsonAsync("/api/roles", new { name = "   " });
            Assert.Equal((HttpStatusCode.BadRequest, "role_name_required"), (blank.StatusCode, await CodeAsync(blank)));
            var scope = await admin.PostAsJsonAsync("/api/roles", new { name = "Partout", scope = "Everywhere" });
            Assert.Equal((HttpStatusCode.BadRequest, "role_scope_unknown"), (scope.StatusCode, await CodeAsync(scope)));

            // The Admin may give hr.attendance although their role does not hold it: payroll.manage opens all it opens.
            var foreman = await CreatedRoleAsync(admin, new { name = "Contremaître", permissions = new[] { PermissionCatalog.HrAttendance } });
            Assert.Contains(PermissionCatalog.HrAttendance,
                RoleOf(await RolesAsync(admin), foreman).GetProperty("permissions").EnumerateArray().Select(k => k.GetString()));
        }
    }

    [Fact]
    public async Task Nobody_hands_out_more_than_they_hold_by_a_role_or_a_scope()
    {
        using var host = new PosHost();
        var (admin, _) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        using (admin)
        {
            var branch = await TestBranches.SellingAsync(admin, "Alger");
            // Someone trusted with users and roles, but not the books.
            var delegated = await CreatedRoleAsync(admin, new
            {
                name = "Responsable du personnel",
                permissions = new[] { PermissionCatalog.UsersManage, PermissionCatalog.RolesManage, PermissionCatalog.ProductsView,
                    PermissionCatalog.PayrollManage, "hr.attendance", PermissionCatalog.ManufacturingView },
            });
            await AccountOnAsync(host, admin, "rh", delegated, branch);
            var (hr, _) = await SignedInAsync(host, "rh", Password);
            using (hr)
            {
                var wider = await hr.PostAsJsonAsync("/api/roles", new { name = "Comptable", permissions = new[] { PermissionCatalog.AccountingView } });
                Assert.Equal((HttpStatusCode.Forbidden, "grant_beyond_own"), (wider.StatusCode, await CodeAsync(wider)));
                var company = await hr.PostAsJsonAsync("/api/roles", new { name = "Partout", scope = "Company" });
                Assert.Equal((HttpStatusCode.Forbidden, "scope_beyond_own"), (company.StatusCode, await CodeAsync(company)));
                // Within their own: fine.
                var within = await hr.PostAsJsonAsync("/api/roles", new { name = "Vendeur", permissions = new[] { PermissionCatalog.ProductsView } });
                Assert.Equal(HttpStatusCode.Created, within.StatusCode);

                // Taking a key away from a role is giving it back to nobody: also only one they hold.
                var cashier = RoleOf(await RolesAsync(hr), SystemRoles.CashierId);
                var keys = cashier.GetProperty("permissions").EnumerateArray().Select(k => k.GetString()!).Where(k => k != PermissionCatalog.SalesCreate);
                var taken = await hr.PutAsJsonAsync($"/api/roles/{SystemRoles.CashierId}", new { permissions = keys });
                Assert.Equal((HttpStatusCode.Forbidden, "grant_beyond_own"), (taken.StatusCode, await CodeAsync(taken)));

                // An account on the Admin role, or on the Manager role, which carries what they do not hold: refused.
                var employee = await TestUsers.EmployeeAsync(admin, "nouveau", branch);
                foreach (var role in new[] { SystemRoles.AdminId, SystemRoles.ManagerId })
                {
                    var promoted = await hr.PostAsJsonAsync("/api/users", new { employeeId = employee, roleId = role, password = Password, branchId = branch });
                    Assert.Equal(HttpStatusCode.Forbidden, promoted.StatusCode);
                }
                Assert.Equal(HttpStatusCode.Created,
                    (await hr.PostAsJsonAsync("/api/users", new { employeeId = employee, roleId = delegated, password = Password, branchId = branch })).StatusCode);
            }
        }
    }

    /// <summary>The narrower key the roles API lets a holder of the wider one give must stay narrower.</summary>
    [Fact]
    public void Every_route_a_covered_key_opens_is_opened_by_the_key_that_covers_it()
    {
        foreach (var (narrow, wide) in PermissionCatalog.CoveredBy)
        {
            var routes = RoutePermissions.ByRoute.Where(r => r.Value.AnyOf.Contains(narrow)).ToList();
            Assert.NotEmpty(routes);
            Assert.All(routes, r => Assert.Contains(wide, r.Value.AnyOf));
        }
    }

    // ---------- the upgrade ----------

    private const string SqliteBefore = "20261103090603_RentalContractTaxMode";
    private const string SqlServerBefore = "20261103090623_RentalContractTaxMode";

    [Fact]
    public async Task Sqlite_upgrade_fills_the_compared_names_and_the_index_refuses_a_twin()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await VerifyUpgrade(db, SqliteBefore);
    }

    [SqlServerFact]
    public async Task SqlServer_upgrade_fills_the_compared_names_and_the_index_refuses_a_twin()
    {
        await using var database = new SqlServerTestDatabase();
        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, PosDbProviders.SqlServer, database.ConnectionString);
        await using var db = new PosDbContext(options.Options);
        await VerifyUpgrade(db, SqlServerBefore);
    }

    /// <summary>Two roles from before, one with spaces around its name: both filled, and a twin of the second refused.</summary>
    private static async Task VerifyUpgrade(PosDbContext db, string before)
    {
        await db.Database.GetService<IMigrator>().MigrateAsync(before);
        const string Insert = "INSERT INTO \"Roles\" (\"Id\", \"Key\", \"Name\", \"IsActive\", \"IsSystem\", \"Scope\", \"Version\", \"CreatedAtUtc\", \"UpdatedAtUtc\") ";
        await db.Database.ExecuteSqlRawAsync(Insert + "VALUES ({0}, {1}, 'Manager', 1, 1, 1, 0, {2}, {2})", SystemRoles.ManagerId, SystemRoles.ManagerKey, DateTime.UtcNow);
        await db.Database.ExecuteSqlRawAsync(Insert + "VALUES ({0}, NULL, ' Magasinier ', 1, 0, 1, 0, {1}, {1})", Guid.NewGuid(), DateTime.UtcNow);

        await db.Database.MigrateAsync();

        var names = await db.Roles.AsNoTracking().OrderBy(r => r.NormalizedName).Select(r => r.NormalizedName).ToListAsync();
        Assert.Equal(new[] { "MAGASINIER", "MANAGER" }, names);
        db.Roles.Add(new Role { Name = "magasinier", NormalizedName = "MAGASINIER" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
