using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Domain;
using AlgeriaPos.Tests.Hosting;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// Who may approve a payment voucher, reported at login so the screen offers Approve by reading
/// the answer and never by naming a role (Phase 4, Finance). A cashier who holds the cash box
/// raises, pays and cancels vouchers, but does not approve them: the route refuses, and now the
/// login says so too. Nothing is granted to anyone and the route's gate is unchanged —
/// <c>EndpointAuthorizationTests</c> pins the reported decision to that gate.
/// </summary>
[Collection(HostCollection.Name)]
public class PaymentVoucherCapabilityTests
{
    [Theory]
    [InlineData(UserRole.Admin, true, true)]
    [InlineData(UserRole.Manager, true, false)]
    [InlineData(UserRole.Cashier, false, false)]
    public void Managers_and_admins_approve_and_only_the_admin_approves_their_own(UserRole role, bool approve, bool own)
    {
        Assert.Equal(approve, Capabilities.Holds(role, Capabilities.ApprovePaymentVouchers));
        Assert.Equal(own, Capabilities.Holds(role, Capabilities.ApproveOwnPaymentVouchers));
        Assert.Equal(approve, Capabilities.For(role).Contains(Capabilities.ApprovePaymentVouchers));
        Assert.Equal(own, Capabilities.For(role).Contains(Capabilities.ApproveOwnPaymentVouchers));
        // The approval's own rule (VoucherActor.IsAdmin, read from the Admin role) is the same answer.
        Assert.Equal(role == UserRole.Admin, own);
    }

    [Fact]
    public void Neither_is_a_permission_so_nobody_can_grant_it()
    {
        foreach (var capability in new[] { Capabilities.ApprovePaymentVouchers, Capabilities.ApproveOwnPaymentVouchers })
        {
            Assert.DoesNotContain(capability, Permissions.All);
            Assert.Equal("", Permissions.Store([capability]));
            Assert.Empty(Permissions.For(new User { Role = UserRole.Cashier, Permissions = capability }));
        }
    }

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

    private static string[] CapabilitiesOf(JsonElement user) =>
        user.TryGetProperty("capabilities", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => c.GetString()!).ToArray()
            : [];

    [Fact]
    public async Task The_login_reports_who_approves_and_the_cash_box_cashier_is_refused_as_before()
    {
        using var host = new PosHost();
        var (admin, adminUser) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        using (admin)
        {
            await CreateUserAsync(host, admin, "gerant-pv", UserRole.Manager);
            // A cash-box cashier, sent the capability as if it were a grant: it is dropped, not stored.
            await CreateUserAsync(host, admin, "caisse-pv", UserRole.Cashier, Permissions.CashBox, Capabilities.ApprovePaymentVouchers);

            Assert.Contains(Capabilities.ApprovePaymentVouchers, CapabilitiesOf(adminUser));
            Assert.Contains(Capabilities.ApproveOwnPaymentVouchers, CapabilitiesOf(adminUser));

            var (manager, managerUser) = await SignedInAsync(host, "gerant-pv", Password);
            using (manager)
            {
                Assert.Contains(Capabilities.ApprovePaymentVouchers, CapabilitiesOf(managerUser));
                Assert.DoesNotContain(Capabilities.ApproveOwnPaymentVouchers, CapabilitiesOf(managerUser));
            }

            var (cashier, cashierUser) = await SignedInAsync(host, "caisse-pv", Password, null);
            using (cashier)
            {
                Assert.DoesNotContain(Capabilities.ApprovePaymentVouchers, CapabilitiesOf(cashierUser));
                Assert.DoesNotContain(Capabilities.ApproveOwnPaymentVouchers, CapabilitiesOf(cashierUser));
                Assert.Contains(Permissions.CashBox,
                    cashierUser.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
                // The route refuses them by role, exactly as before; no voucher needs to exist for that.
                var refused = await cashier.PostAsJsonAsync($"/api/payment-vouchers/{Guid.NewGuid()}/approve", new { note = (string?)null });
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }
        }
    }
}
