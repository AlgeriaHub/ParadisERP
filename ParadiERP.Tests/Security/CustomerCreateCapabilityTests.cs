using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Domain;
using ParadiERP.Tests.Hosting;

namespace ParadiERP.Tests.Security;

/// <summary>
/// FR-1: who may create a customer is one server decision, <see cref="Capabilities.CreateCustomers"/>.
/// The route enforces it and the login reports it, so the rental form and the till offer the
/// button by reading the login, never by naming a role. Admin and Manager keep the right they had;
/// a Cashier gains nothing, and cannot be given it on the users screen.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerCreateCapabilityTests
{
    // ---------- the decision, pure ----------

    [Theory]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, true)]
    [InlineData(UserRole.Cashier, false)]
    public void Admin_and_Manager_hold_it_and_a_Cashier_does_not(UserRole role, bool holds)
    {
        // AUTH-1: a permission of the role now, seeded exactly where the capability was held.
        Assert.Equal(holds, SystemRoles.For(role).Permissions.Contains(PermissionCatalog.CustomersCreate));
        Assert.Equal(Capabilities.CreateCustomers, PermissionCatalog.CustomersCreate);
    }

    [Fact]
    public void The_legacy_users_screen_still_cannot_grant_it()
    {
        // The old request shape knows five names; customers.create is not one of them, so the screen
        // that sends it cannot hand it out. Only the roles model can (an additional permission).
        Assert.DoesNotContain(Capabilities.CreateCustomers, Permissions.All);
        Assert.Equal("", Permissions.Store([Capabilities.CreateCustomers]));
        Assert.Empty(LegacyGrants.Map(UserRole.Cashier, Capabilities.CreateCustomers).Kept);
    }

    // ---------- the login and the route, over HTTP ----------

    private const string Password = "Essai@2026";

    // A cashier signs in on their own till (deviceCode null), everyone else on C01 (2026-10-05).
    private static async Task<(HttpClient Client, JsonElement User)> SignedInAsync(PosHost host, string username, string password, string? deviceCode = "C01")
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = host.SignInName(username), password, deviceCode });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.Clone();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return (client, body.GetProperty("user"));
    }

    private static async Task CreateUserAsync(PosHost host, HttpClient admin, string username, UserRole role, params string[] permissions)
    {
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        // A cashier works at a selling branch; the first listed is the main warehouse.
        var branchId = role == UserRole.Cashier
            ? await TestBranches.SellingAsync(admin, "Ventes " + username)
            : branches.EnumerateArray().First().GetProperty("id").GetGuid();
        // An employee first, then their account (2026-10-05); the host remembers its Login ID by name.
        await TestUsers.CreateAsync(host, admin, username, role.ToString(), branchId, Password, permissions: permissions);

    }

    private static object Person(string code) => new
    {
        code, fullName = "Client " + code, phone = (string?)null, address = (string?)null, taxId = (string?)null,
        email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person",
    };

    private static string[] CapabilitiesOf(JsonElement user) =>
        user.TryGetProperty("capabilities", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => c.GetString()!).ToArray()
            : [];

    [Fact]
    public async Task The_login_reports_the_capability_the_route_then_enforces()
    {
        using var host = new PosHost();
        var (admin, adminUser) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        using (admin)
        {
            await CreateUserAsync(host, admin, "gerant", UserRole.Manager);
            // A cashier sent the capability as if it were a grant: it is dropped, not stored.
            await CreateUserAsync(host, admin, "caisse", UserRole.Cashier, Capabilities.CreateCustomers);

            Assert.Contains(Capabilities.CreateCustomers, CapabilitiesOf(adminUser));
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/catalog/customers", Person("CAP-ADM"))).StatusCode);

            var (manager, managerUser) = await SignedInAsync(host, "gerant", Password);
            using (manager)
            {
                Assert.Contains(Capabilities.CreateCustomers, CapabilitiesOf(managerUser));
                Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync("/api/catalog/customers", Person("CAP-MGR"))).StatusCode);
            }

            var (cashier, cashierUser) = await SignedInAsync(host, "caisse", Password, null);
            using (cashier)
            {
                Assert.DoesNotContain(Capabilities.CreateCustomers, CapabilitiesOf(cashierUser));
                Assert.DoesNotContain(Capabilities.CreateCustomers,
                    cashierUser.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
                Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/catalog/customers", Person("CAP-CSH"))).StatusCode);
            }

            // Only the two allowed callers left a customer behind. Told apart by name: the code in
            // the body is ignored and the server draws one (owner, 2026-10-05).
            var names = (await admin.GetFromJsonAsync<JsonElement>("/api/catalog/customers"))
                .EnumerateArray().Select(c => c.GetProperty("fullName").GetString()).ToList();
            Assert.Contains("Client CAP-ADM", names);
            Assert.Contains("Client CAP-MGR", names);
            Assert.DoesNotContain("Client CAP-CSH", names);
        }
    }
}
