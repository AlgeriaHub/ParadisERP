using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.API.Endpoints;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Tests.Hosting;

namespace ParadisERP.Tests.Security;

/// <summary>
/// Two-step login (owner, 2026-11-11): optional, each user turns it on for themselves with an
/// authenticator app; an administrator can only turn it off; a computer may be trusted for 30 days.
/// </summary>
public class TotpTests
{
    // RFC 6238, appendix B, SHA-1 key; the last six of each eight-digit vector.
    private static readonly byte[] RfcKey = "12345678901234567890"u8.ToArray();

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Codes_match_the_RFC_vectors(long unixSeconds, string expected) =>
        Assert.Equal(expected, Totp.Code(RfcKey, unixSeconds / Totp.StepSeconds));

    [Fact]
    public void A_code_is_accepted_within_one_step_of_drift_once_and_typed_loosely()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
        var step = Totp.StepAt(now);
        var code = Totp.Code(RfcKey, step);
        Assert.Equal(step, Totp.Match(RfcKey, code, now, 0));
        Assert.Equal(step, Totp.Match(RfcKey, code[..3] + " " + code[3..], now, 0));
        // Arabic-Indic digits are read as Latin.
        var arabic = new string(code.Select(c => (char)('٠' + (c - '0'))).ToArray());
        Assert.Equal(step, Totp.Match(RfcKey, arabic, now, 0));
        Assert.Equal(step + 1, Totp.Match(RfcKey, Totp.Code(RfcKey, step + 1), now, 0));
        Assert.Null(Totp.Match(RfcKey, Totp.Code(RfcKey, step + 2), now, 0));
        // Already used: the row's last step is this one.
        Assert.Null(Totp.Match(RfcKey, code, now, step));
        Assert.Null(Totp.Match(RfcKey, "12345", now, 0));
        Assert.Null(Totp.Match(RfcKey, "abcdef", now, 0));
    }

    [Fact]
    public void Base32_round_trips()
    {
        var key = Totp.NewKey();
        Assert.Equal(key, Totp.FromBase32(Totp.ToBase32(key)));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.ToBase32(RfcKey));
    }

    [Fact]
    public void A_recovery_code_is_good_once()
    {
        var (codes, stored) = RecoveryCodes.Generate();
        Assert.Equal(RecoveryCodes.Count, codes.Distinct().Count());
        Assert.Equal(RecoveryCodes.Count, RecoveryCodes.Left(stored));
        var after = RecoveryCodes.Use(stored, codes[3].ToUpperInvariant());
        Assert.NotNull(after);
        Assert.Equal(RecoveryCodes.Count - 1, RecoveryCodes.Left(after));
        Assert.Null(RecoveryCodes.Use(after, codes[3]));
        Assert.Null(RecoveryCodes.Use(stored, "aaaaa-aaaaa"));
    }
}

[Collection(HostCollection.Name)]
public class TwoStepLoginTests
{
    private const string Password = "Essai@2026";

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password,
        string? code = null, string? trust = null, bool remember = false) =>
        client.PostAsJsonAsync("/api/auth/login", new { username, password, deviceCode = (string?)null, twoFactorCode = code, trustToken = trust, rememberDevice = remember });

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        var body = await BodyAsync(response);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static void Bear(HttpClient client, JsonElement session) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("token").GetString());

    private static async Task<HttpClient> SignedInAsync(PosHost host, string name, string password)
    {
        var client = host.CreateClient();
        var login = await LoginAsync(client, host.SignInName(name), password);
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        Bear(client, await BodyAsync(login));
        return client;
    }

    private static async Task<(PosHost Host, HttpClient Admin, Guid UserId)> HostWithAsync(string name)
    {
        var host = new PosHost();
        var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var created = await admin.PostAsJsonAsync("/api/catalog/branches", new
        {
            nameAr = "Agence " + name, nameFr = "Agence " + name, nameEn = "Agence " + name, address = "", phone = "",
            isMain = false, allowNegativeStock = false, isActive = true,
        });
        var branch = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (id, _) = await TestUsers.CreateAsync(host, admin, name, "Manager", branch, Password);
        return (host, admin, id);
    }

    /// <summary>Turns two-step login on for the signed-in client; answers the key and the recovery codes.</summary>
    private static async Task<(byte[] Key, List<string> Codes)> EnableAsync(HttpClient client)
    {
        var setup = await client.PostAsJsonAsync("/api/auth/two-factor/setup", new { password = Password });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var offered = await BodyAsync(setup);
        var key = Totp.FromBase32(offered.GetProperty("secret").GetString()!.Replace(" ", ""))!;
        Assert.StartsWith("otpauth://totp/", offered.GetProperty("provisioningUri").GetString());

        var enabled = await client.PostAsJsonAsync("/api/auth/two-factor/enable",
            new { code = Totp.Code(key, Totp.StepAt(DateTimeOffset.UtcNow)) });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var codes = (await BodyAsync(enabled)).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(RecoveryCodes.Count, codes.Count);
        return (key, codes);
    }

    [Fact]
    public async Task On_it_asks_for_the_code_refuses_a_wrong_or_reused_one_and_accepts_the_right_one_and_a_recovery_code()
    {
        var (host, admin, _) = await HostWithAsync("deux.etapes");
        using (host)
        using (admin)
        {
            var user = await SignedInAsync(host, "deux.etapes", Password);

            // The setup asks for the password again, and a code from the key it offered.
            var refused = await user.PostAsJsonAsync("/api/auth/two-factor/setup", new { password = "pas-le-bon" });
            Assert.Equal(AuthEndpoints.PasswordWrongCode, await CodeOfAsync(refused));
            var (key, recovery) = await EnableAsync(user);

            var client = host.CreateClient();
            var name = host.SignInName("deux.etapes");

            var asked = await LoginAsync(client, name, Password);
            Assert.Equal(HttpStatusCode.Unauthorized, asked.StatusCode);
            Assert.Equal(AuthEndpoints.TwoFactorRequiredCode, await CodeOfAsync(asked));

            var wrong = await LoginAsync(client, name, Password, code: "000000");
            Assert.Equal(AuthEndpoints.TwoFactorWrongCode, await CodeOfAsync(wrong));

            // A wrong password with a code is still just a wrong password.
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, name, "pas-le-bon", code: "000000")).StatusCode);

            // The enabling code's step is spent; the next step's code is within the drift.
            var next = Totp.Code(key, Totp.StepAt(DateTimeOffset.UtcNow) + 1);
            var signedIn = await LoginAsync(client, name, Password, code: next, remember: true);
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            var trust = (await BodyAsync(signedIn)).GetProperty("twoFactorTrust").GetString();
            Assert.False(string.IsNullOrEmpty(trust));

            // The same code twice signs in once.
            Assert.Equal(AuthEndpoints.TwoFactorWrongCode, await CodeOfAsync(await LoginAsync(client, name, Password, code: next)));

            // The trusted computer needs no code; a trust token is never a session.
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, name, Password, trust: trust)).StatusCode);
            using (var bearer = host.CreateClient())
            {
                bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", trust);
                Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/auth/profile")).StatusCode);
            }

            // A recovery code works once.
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, name, Password, code: recovery[0])).StatusCode);
            Assert.Equal(AuthEndpoints.TwoFactorWrongCode, await CodeOfAsync(await LoginAsync(client, name, Password, code: recovery[0])));

            var status = await BodyAsync(await user.GetAsync("/api/auth/two-factor"));
            Assert.True(status.GetProperty("enabled").GetBoolean());
            Assert.Equal(RecoveryCodes.Count - 1, status.GetProperty("recoveryCodesLeft").GetInt32());

            user.Dispose();
        }
    }

    [Fact]
    public async Task A_new_password_forgets_every_trusted_computer()
    {
        var (host, admin, userId) = await HostWithAsync("deux.oubli");
        using (host)
        using (admin)
        {
            var user = await SignedInAsync(host, "deux.oubli", Password);
            var (_, recovery) = await EnableAsync(user);
            var client = host.CreateClient();
            var name = host.SignInName("deux.oubli");
            var trust = (await BodyAsync(await LoginAsync(client, name, Password, code: recovery[0], remember: true)))
                .GetProperty("twoFactorTrust").GetString();

            // The administrator sets a new password: the trusted computer must give the code again.
            Assert.Equal(HttpStatusCode.NoContent,
                (await admin.PostAsJsonAsync($"/api/users/{userId}/password", new { value = "Nouveau@2026" })).StatusCode);
            var asked = await LoginAsync(client, name, "Nouveau@2026", trust: trust);
            Assert.Equal(AuthEndpoints.TwoFactorRequiredCode, await CodeOfAsync(asked));
            user.Dispose();
        }
    }

    [Fact]
    public async Task The_administrator_can_only_turn_it_off_and_the_user_can_turn_it_off_with_the_password()
    {
        var (host, admin, userId) = await HostWithAsync("deux.reset");
        using (host)
        using (admin)
        {
            var user = await SignedInAsync(host, "deux.reset", Password);
            await EnableAsync(user);
            var name = host.SignInName("deux.reset");

            var list = await BodyAsync(await admin.GetAsync("/api/users"));
            Assert.True(list.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == userId).GetProperty("twoFactorOn").GetBoolean());

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/users/{userId}/two-factor/reset", null)).StatusCode);
            using var client = host.CreateClient();
            var signedIn = await LoginAsync(client, name, Password);
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            // The reset ended the sessions the account held.
            Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/auth/two-factor")).StatusCode);

            // On again, then off by the user, who gives the password.
            Bear(user, await BodyAsync(signedIn));
            await EnableAsync(user);
            Assert.Equal(AuthEndpoints.PasswordWrongCode,
                await CodeOfAsync(await user.PostAsJsonAsync("/api/auth/two-factor/disable", new { password = "pas-le-bon" })));
            var off = await user.PostAsJsonAsync("/api/auth/two-factor/disable", new { password = Password });
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            Assert.False((await BodyAsync(off)).GetProperty("enabled").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, name, Password)).StatusCode);
            user.Dispose();
        }
    }

    [Fact]
    public async Task The_profile_says_who_the_user_is_and_keeps_only_a_picture_as_the_photo()
    {
        var (host, admin, userId) = await HostWithAsync("deux.photo");
        using (host)
        using (admin)
        using (var user = await SignedInAsync(host, "deux.photo", Password))
        {
            var profile = await BodyAsync(await user.GetAsync("/api/auth/profile"));
            Assert.Equal(userId, profile.GetProperty("id").GetGuid());
            Assert.Equal("deux.photo", profile.GetProperty("fullName").GetString());
            Assert.Equal(JsonValueKind.Null, profile.GetProperty("photoVersion").ValueKind);
            Assert.Equal(JsonValueKind.Object, profile.GetProperty("employee").ValueKind);
            Assert.Equal("Agence deux.photo", profile.GetProperty("branchNameFr").GetString());
            Assert.False(profile.GetProperty("twoFactor").GetProperty("enabled").GetBoolean());
            Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync("/api/auth/profile/photo")).StatusCode);

            // A PNG: kept, and served back to its owner.
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];
            var saved = await user.PostAsync("/api/auth/profile/photo", Photo(png, "me.png", "image/png"));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.False(string.IsNullOrEmpty((await BodyAsync(saved)).GetProperty("photoVersion").GetString()));
            var served = await user.GetAsync("/api/auth/profile/photo");
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
            Assert.Equal(png, await served.Content.ReadAsByteArrayAsync());

            // An SVG or a PDF is not a photo, whatever the browser calls it.
            var svg = await user.PostAsync("/api/auth/profile/photo",
                Photo("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray(), "me.png", "image/png"));
            Assert.Equal(AccountEndpoints.PhotoInvalidCode, await CodeOfAsync(svg));
            var pdf = await user.PostAsync("/api/auth/profile/photo", Photo("%PDF-1.4"u8.ToArray(), "me.jpg", "image/jpeg"));
            Assert.Equal(AccountEndpoints.PhotoInvalidCode, await CodeOfAsync(pdf));

            Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync("/api/auth/profile/photo")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync("/api/auth/profile/photo")).StatusCode);
        }
    }

    private static MultipartFormDataContent Photo(byte[] bytes, string name, string type)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return new MultipartFormDataContent { { content, "file", name } };
    }
}
