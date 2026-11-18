using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Tests.Hosting;

namespace ParadiERP.Tests.Security;

/// <summary>
/// A signed-in person changes their own password (owner, 2026-10-05: "keep the password created from
/// admin side, and the user after login can change password"). The administrator still types the first
/// one; afterwards the person may replace it, giving the current one again. The change revokes every
/// session the account held and hands the caller a fresh one on the same till, or the same office.
/// </summary>
[Collection(HostCollection.Name)]
public class ChangePasswordTests
{
    private const string Password = "Essai@2026";
    private const string NewPassword = "Nouveau@2026";

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password, string? deviceCode) =>
        await client.PostAsJsonAsync("/api/auth/login", new { username, password, deviceCode });

    private static async Task<(HttpClient Client, JsonElement Body)> SignedInAsync(PosHost host, string username, string password, string? deviceCode)
    {
        var client = host.CreateClient();
        var login = await LoginAsync(client, host.SignInName(username), password, deviceCode);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.Clone();
        Bear(client, body);
        return (client, body);
    }

    private static void Bear(HttpClient client, JsonElement session) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("token").GetString());

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string? currentPassword, string? newPassword) =>
        client.PostAsJsonAsync("/api/auth/password", new { currentPassword, newPassword });

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

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

    /// <summary>A host with one account under <paramref name="name"/>, created the way the back office does.</summary>
    private static async Task<PosHost> HostWithAsync(string name, string role, string? pin = null)
    {
        var host = new PosHost();
        try
        {
            var (admin, _) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, null);
            using (admin)
            {
                var branch = await SellingBranchAsync(admin, "Agence " + name);
                await TestUsers.CreateAsync(host, admin, name, role, branch, Password, pin);
            }
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    [Fact]
    public async Task The_new_password_signs_in_the_old_one_does_not_and_only_the_returned_session_survives()
    {
        using var host = await HostWithAsync("gerant.mdp", "Manager");
        var (first, _) = await SignedInAsync(host, "gerant.mdp", Password, null);
        var (other, _) = await SignedInAsync(host, "gerant.mdp", Password, null);
        using (first)
        using (other)
        {
            var changed = await ChangeAsync(first, Password, NewPassword);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var session = JsonDocument.Parse(await changed.Content.ReadAsStringAsync()).RootElement.Clone();

            // The same shape the sign-in answers with, for the same office session it came from.
            Assert.Equal("", session.GetProperty("deviceCode").GetString());
            Assert.Equal(JsonValueKind.Null, session.GetProperty("deviceBranchId").ValueKind);
            Assert.Equal("gerant.mdp", session.GetProperty("user").GetProperty("fullName").GetString());
            Assert.True(session.TryGetProperty("expiresAtUtc", out _));
            Assert.True(session.TryGetProperty("regional", out _));

            // Every session the account held is revoked — the caller's own token and the other computer's.
            Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/catalog/branches")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/catalog/branches")).StatusCode);

            // The returned one carries on.
            Bear(first, session);
            Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/catalog/branches")).StatusCode);
        }

        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, host.SignInName("gerant.mdp"), Password, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, host.SignInName("gerant.mdp"), NewPassword, null)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_short_or_unchanged_password_is_refused_in_words_and_changes_nothing()
    {
        using var host = await HostWithAsync("gerant.refus", "Manager");
        var (client, _) = await SignedInAsync(host, "gerant.refus", Password, null);
        using (client)
        {
            var wrong = await ChangeAsync(client, "pas-le-bon", NewPassword);
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal(AuthEndpoints.PasswordWrongCode, await CodeOfAsync(wrong));

            // The administrator's own minimum, not a second rule (PasswordRule).
            var tooShort = await ChangeAsync(client, Password, new string('x', PasswordRule.MinLength - 1));
            Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
            Assert.Equal(AuthEndpoints.PasswordTooShortCode, await CodeOfAsync(tooShort));
            var blank = await ChangeAsync(client, Password, "        ");
            Assert.Equal(AuthEndpoints.PasswordTooShortCode, await CodeOfAsync(blank));

            var same = await ChangeAsync(client, Password, Password);
            Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
            Assert.Equal(AuthEndpoints.PasswordSameCode, await CodeOfAsync(same));

            // Nothing was saved: the session still holds and the password is still the old one.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/branches")).StatusCode);
        }

        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(anonymous, host.SignInName("gerant.refus"), Password, null)).StatusCode);
    }

    [Fact]
    public async Task Without_a_session_there_is_nothing_to_change()
    {
        using var host = new PosHost();
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await ChangeAsync(anonymous, PosHost.BootstrapPassword, NewPassword)).StatusCode);

        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await LoginAsync(client, PosHost.BootstrapUsername, PosHost.BootstrapPassword, null)).StatusCode);
    }

    [Fact]
    public async Task A_cashier_changes_theirs_on_their_till_and_stays_on_it()
    {
        using var host = await HostWithAsync("caisse.mdp", "Cashier", pin: "1234");
        var (till, signedIn) = await SignedInAsync(host, "caisse.mdp", Password, null);
        using (till)
        {
            var code = signedIn.GetProperty("deviceCode").GetString();
            Assert.False(string.IsNullOrEmpty(code));

            var changed = await ChangeAsync(till, Password, NewPassword);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var session = JsonDocument.Parse(await changed.Content.ReadAsStringAsync()).RootElement.Clone();

            // The same till, its branch and its receipt sequence, as the sign-in gave them.
            Assert.Equal(code, session.GetProperty("deviceCode").GetString());
            Assert.Equal(signedIn.GetProperty("deviceBranchId").GetGuid(), session.GetProperty("deviceBranchId").GetGuid());
            Assert.Equal(signedIn.GetProperty("lastLocalSeq").GetInt64(), session.GetProperty("lastLocalSeq").GetInt64());

            Assert.Equal(HttpStatusCode.Unauthorized, (await till.GetAsync("/api/sync/pull?since=0")).StatusCode);
            Bear(till, session);
            Assert.Equal(HttpStatusCode.OK, (await till.GetAsync("/api/sync/pull?since=0")).StatusCode);
            var push = await till.PostAsJsonAsync("/api/sync/push", new { deviceCode = code, sales = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        }

        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, host.SignInName("caisse.mdp"), NewPassword, null)).StatusCode);
    }

    [Fact]
    public async Task Guessing_the_current_password_meets_the_login_s_own_cap()
    {
        using var host = await HostWithAsync("gerant.cap", "Manager");
        var (client, _) = await SignedInAsync(host, "gerant.cap", Password, null);
        using (client)
        {
            for (var i = 0; i < LoginThrottle.MaxFailures; i++)
                Assert.Equal(AuthEndpoints.PasswordWrongCode, await CodeOfAsync(await ChangeAsync(client, $"essai-{i}", NewPassword)));

            var capped = await ChangeAsync(client, Password, NewPassword);
            Assert.Equal(HttpStatusCode.TooManyRequests, capped.StatusCode);
            Assert.Equal("too_many_attempts", await CodeOfAsync(capped));
        }

        // One count per account: the sign-in waits too, which is what keeps a stolen session from
        // guessing faster than the login screen could.
        using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await LoginAsync(anonymous, host.SignInName("gerant.cap"), Password, null)).StatusCode);
    }
}
