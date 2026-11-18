using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Domain;
using ParadiERP.Tests.Hosting;

namespace ParadiERP.Tests.Security;

/// <summary>
/// Answer 25 (owner, 2026-10-02): "the employee who has the cash-box permission" handles the money.
/// A cashier without the grant reaches no voucher; with it he raises and pays them; approving a
/// payment voucher stays the manager's whatever he holds.
/// </summary>
[Collection(HostCollection.Name)]
public class CashBoxPermissionTests
{
    // A cashier signs in on their own till (deviceCode null), the admin on C01 (2026-10-05).
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

    // An employee first, then their account (2026-10-05); the host remembers its Login ID by name.
    private static async Task CreateCashierAsync(PosHost host, HttpClient admin, string username, params string[] permissions)
    {
        var branchId = await TestBranches.SellingAsync(admin, "Ventes " + username);
        await TestUsers.CreateAsync(host, admin, username, nameof(UserRole.Cashier), branchId, "Caisse@2026", permissions: permissions);
    }

    [Fact]
    public async Task A_cashier_reaches_the_vouchers_only_with_the_cash_box_and_never_approves()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        await CreateCashierAsync(host, admin, "plain");
        await CreateCashierAsync(host, admin, "boxholder", Permissions.CashBox);

        using var plain = await SignedInAsync(host, "plain", "Caisse@2026", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/payment-vouchers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/receivables/receipts")).StatusCode);

        using var holder = await SignedInAsync(host, "boxholder", "Caisse@2026", null);
        Assert.Equal(HttpStatusCode.OK, (await holder.GetAsync("/api/payment-vouchers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await holder.GetAsync("/api/receivables/receipts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await holder.GetAsync("/api/receivables/invoices")).StatusCode);

        // Raising an invoice, the statement and approving stay the manager's.
        Assert.Equal(HttpStatusCode.Forbidden, (await holder.GetAsync("/api/receivables/ageing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await holder.PostAsJsonAsync($"/api/payment-vouchers/{Guid.NewGuid()}/approve", new { note = (string?)null })).StatusCode);
    }
}
