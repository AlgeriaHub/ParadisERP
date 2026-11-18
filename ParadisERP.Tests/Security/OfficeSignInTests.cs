using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Tests.Hosting;

namespace ParadisERP.Tests.Security;

/// <summary>
/// An office sign-in (owner, 2026-10-05: "admin can log in without a till — why C01?"): no
/// terminal code means a back-office session from a computer that is not a till. It used to be
/// read as "C01", so every office computer acted as the first till. Only a role whose scope
/// reaches beyond a till's branch may hold one; the till's own routes answer it in words.
/// </summary>
[Collection(HostCollection.Name)]
public class OfficeSignInTests
{
    private const string Password = "Essai@2026";

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password, string? deviceCode) =>
        await client.PostAsJsonAsync("/api/auth/login", new { username, password, deviceCode });

    private static async Task<(HttpClient Client, JsonElement Body)> SignedInAsync(PosHost host, string username, string password, string? deviceCode)
    {
        var client = host.CreateClient();
        var login = await LoginAsync(client, host.SignInName(username), password, deviceCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.Clone();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return (client, body);
    }

    private static async Task CreateUserAsync(PosHost host, HttpClient admin, string username, UserRole role)
    {
        // A cashier works at a selling branch (2026-10-05): the main warehouse hosts no till.
        var branchResponse = await admin.PostAsJsonAsync("/api/catalog/branches", new
        {
            nameAr = "Bureau " + username, nameFr = "Bureau " + username, nameEn = "Office " + username, address = "", phone = "",
            isMain = false, allowNegativeStock = false, isActive = true,
        });
        var branchId = (await branchResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        // An employee first, then their account (2026-10-05); the host remembers its Login ID by name.
        await TestUsers.CreateAsync(host, admin, username, role.ToString(), branchId, Password);

    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public void The_rule_follows_the_role_scope_not_its_name()
    {
        Assert.True(BranchScope.AllowsOfficeSession(RoleScope.Company));
        Assert.True(BranchScope.AllowsOfficeSession(RoleScope.AssignedBranch));
        Assert.False(BranchScope.AllowsOfficeSession(RoleScope.TillBranch));
    }

    [Fact]
    public async Task An_admin_signs_in_without_a_till_and_works_in_the_back_office()
    {
        using var host = new PosHost();
        var (admin, body) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, null);
        using (admin)
        {
            // No longer "C01": the session names no terminal and no terminal's branch.
            Assert.Equal("", body.GetProperty("deviceCode").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("deviceBranchId").ValueKind);

            // The token is valid on every request (the session check accepts it) and opens the back office.
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/catalog/branches")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
            // The catalogue pull still answers, on the main branch's figures.
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/sync/pull?since=0")).StatusCode);
        }
    }

    [Fact]
    public async Task A_blank_code_is_an_office_sign_in_too()
    {
        using var host = new PosHost();
        var (admin, body) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, "   ");
        using (admin) Assert.Equal("", body.GetProperty("deviceCode").GetString());
    }

    [Fact]
    public async Task An_office_session_cannot_record_sales_and_is_told_why()
    {
        using var host = new PosHost();
        var (admin, _) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, null);
        using (admin)
        {
            var push = await admin.PostAsJsonAsync("/api/sync/push", new { deviceCode = "C01", sales = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.Conflict, push.StatusCode);
            Assert.Equal(BranchScope.NoTerminalCode, await CodeOfAsync(push));
        }
    }

    [Fact]
    public async Task A_manager_signs_in_without_a_till_and_a_cashier_on_their_own()
    {
        using var host = new PosHost();
        var (admin, _) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, "C01");
        using (admin)
        {
            await CreateUserAsync(host, admin, "gerant.bureau", UserRole.Manager);
            await CreateUserAsync(host, admin, "caisse.bureau", UserRole.Cashier);
        }

        var (manager, managerBody) = await SignedInAsync(host, "gerant.bureau", Password, null);
        using (manager)
        {
            Assert.Equal("", managerBody.GetProperty("deviceCode").GetString());
            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/catalog/branches")).StatusCode);
        }

        // A cashier with no code is not an office session: they open their own till (2026-10-05).
        var (cashier, cashierBody) = await SignedInAsync(host, "caisse.bureau", Password, null);
        using (cashier) Assert.NotEqual("", cashierBody.GetProperty("deviceCode").GetString());

        // A typed code still goes through the terminal binding: C01 is the warehouse's, not their branch's.
        using var client = host.CreateClient();
        var refused = await LoginAsync(client, host.SignInName("caisse.bureau"), Password, "C01");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(BranchScope.WrongTerminalCode, await CodeOfAsync(refused));
    }

    [Fact]
    public async Task A_till_sign_in_is_unchanged()
    {
        using var host = new PosHost();
        var (till, body) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, "c01");
        using (till)
        {
            Assert.Equal("C01", body.GetProperty("deviceCode").GetString());
            Assert.NotEqual(JsonValueKind.Null, body.GetProperty("deviceBranchId").ValueKind);
            var push = await till.PostAsJsonAsync("/api/sync/push", new { deviceCode = "C01", sales = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        }

        // An unregistered code is still refused: an office sign-in is asked for by leaving the code out.
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await LoginAsync(client, PosHost.BootstrapUsername, PosHost.BootstrapPassword, "C99")).StatusCode);
    }
}
