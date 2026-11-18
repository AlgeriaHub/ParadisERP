using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ParadisERP.Infrastructure.Auth;

/// <summary>
/// Time-based one-time codes (RFC 6238, the authenticator apps' codes): HMAC-SHA1, six digits, a
/// new code every 30 seconds. Written here rather than taken from a package: it is thirty lines,
/// and every line of it is a security line someone should be able to read.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    /// <summary>A code from the step before or after is still accepted: phones' clocks drift.</summary>
    public const int Drift = 1;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step a typed code belongs to, when it is the code of a step within the drift and later than
    /// <paramref name="afterStep"/> (a code is good once); otherwise null. Spaces and dashes typed
    /// between the digits are ignored; anything else that is not six digits is simply wrong.
    /// </summary>
    public static long? Match(byte[] key, string? typed, DateTimeOffset now, long afterStep)
    {
        var digits = Normalise(typed);
        if (digits is null) return null;
        var current = StepAt(now);
        long? found = null;
        // Every candidate is compared, in fixed time, whatever matched first.
        for (var step = current - Drift; step <= current + Drift; step++)
        {
            var same = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Code(key, step)), Encoding.ASCII.GetBytes(digits));
            if (same && step > afterStep) found ??= step;
        }
        return found;
    }

    /// <summary>Six Latin digits, from what was typed; Arabic-Indic digits are read as their Latin twins.</summary>
    public static string? Normalise(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed) || typed.Length > 32) return null;
        var builder = new StringBuilder(Digits);
        foreach (var c in typed)
        {
            if (c is ' ' or '-' or ' ') continue;
            var value = char.IsDigit(c) ? (int)char.GetNumericValue(c) : -1;
            if (value is < 0 or > 9) return null;
            builder.Append((char)('0' + value));
        }
        return builder.Length == Digits ? builder.ToString() : null;
    }

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string ToBase32(byte[] data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    public static byte[]? FromBase32(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('='))
        {
            var value = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0) return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// The address an authenticator app reads from the QR code. The label is the company's name and the
    /// account's sign-in name, so a phone holding several companies tells them apart.
    /// </summary>
    public static string ProvisioningUri(string issuer, string account, string secretBase32)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        return $"otpauth://totp/{label}?secret={secretBase32}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }
}

/// <summary>
/// One-time recovery codes, for the day the phone is lost: ten of them, each good once, shown once and
/// kept only as SHA-256 hashes (each code carries 50 random bits, so a slow hash adds nothing).
/// </summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    /// <summary>Fresh codes to show (<c>xxxxx-xxxxx</c>) and what the row keeps of them.</summary>
    public static (IReadOnlyList<string> Codes, string Stored) Generate()
    {
        var codes = new List<string>(Count);
        for (var i = 0; i < Count; i++)
        {
            var chars = new char[10];
            for (var j = 0; j < chars.Length; j++) chars[j] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            codes.Add($"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}");
        }
        return (codes, string.Join(',', codes.Select(Hash)));
    }

    public static int Left(string? stored) =>
        string.IsNullOrEmpty(stored) ? 0 : stored.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>What the row keeps once <paramref name="typed"/> is used, or null when it is not one of them.</summary>
    public static string? Use(string? stored, string? typed)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrWhiteSpace(typed) || typed.Length > 32) return null;
        var clean = new string(typed.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToLowerInvariant();
        if (clean.Length != 10) return null;
        var hash = Hash($"{clean[..5]}-{clean[5..]}");
        var all = stored.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        var index = all.FindIndex(h => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(hash)));
        if (index < 0) return null;
        all.RemoveAt(index);
        return string.Join(',', all);
    }

    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();
}

/// <summary>What the second step of a sign-in decided.</summary>
public enum TwoFactorOutcome
{
    /// <summary>Two-step login is off for this account.</summary>
    Off,
    /// <summary>A "trust this computer" token from this account, still current, stood in for the code.</summary>
    Trusted,
    /// <summary>The code, or a recovery code, was right.</summary>
    Passed,
    /// <summary>Nothing was sent for the second step: ask for it.</summary>
    Required,
    /// <summary>A code was sent and it is not right (or was used already).</summary>
    Wrong
}

/// <summary>
/// Two-step login (owner, 2026-11-11): optional, each user turns it on for themselves with an
/// authenticator app; an administrator can only turn it off for someone who lost their phone.
///
/// <para>
/// The shared key is protected at rest with the server's Data Protection keys, so a copy of the
/// database alone does not give the codes away. Losing those keys makes every key unreadable: the
/// sign-in then refuses the code, and an administrator turns two-step login off for the account.
/// </para>
/// <para>
/// "Trust this computer for 30 days" is a signed token of its own audience, so it can never be
/// used as a session. It carries the account's <see cref="User.TwoFactorTrustStamp"/>, drawn again
/// on every change of password and of two-step login, which forgets every trusted computer at once.
/// </para>
/// </summary>
public sealed class TwoFactor(IDataProtectionProvider protection, JwtOptions jwt, TenantContext tenant, TimeProvider time)
{
    public const int TrustDays = 30;
    private const string TrustAudience = "pos-two-factor-trust";
    private readonly IDataProtector protector = protection.CreateProtector("ParadisERP.TwoFactor");

    public static bool IsOn(User user) => user.TwoFactorEnabledAtUtc is not null && !string.IsNullOrEmpty(user.TwoFactorSecret);

    public string Protect(byte[] key) => protector.Protect(Totp.ToBase32(key));

    /// <summary>The key, or null when the row holds none or it cannot be read (Data Protection keys lost).</summary>
    public byte[]? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return Totp.FromBase32(protector.Unprotect(stored)); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>A new key offered by the setup: kept as pending until a code from it is typed.</summary>
    public (string Base32, string Protected) NewKey()
    {
        var key = Totp.NewKey();
        return (Totp.ToBase32(key), Protect(key));
    }

    public static string NewStamp() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The second step of a sign-in whose password was right. A code is consumed atomically (the
    /// row's last step moves forward only if no one else moved it first), so the same code typed
    /// twice — or sent twice at the same instant — signs in once.
    /// </summary>
    public async Task<(TwoFactorOutcome Outcome, string? TrustToken)> CheckSignInAsync(
        PosDbContext db, User user, string? code, string? trustToken, bool remember, CancellationToken ct)
    {
        if (!IsOn(user)) return (TwoFactorOutcome.Off, null);
        if (string.IsNullOrWhiteSpace(code))
            return (TrustedBy(user, trustToken) ? TwoFactorOutcome.Trusted : TwoFactorOutcome.Required, null);

        if (!await ConsumeAsync(db, user, code, ct)) return (TwoFactorOutcome.Wrong, null);
        return (TwoFactorOutcome.Passed, remember ? CreateTrust(user) : null);
    }

    /// <summary>
    /// The code from the authenticator, or one of the recovery codes; true when it was right and is
    /// now spent. Used by the sign-in and by the screens that change two-step login.
    /// </summary>
    public async Task<bool> ConsumeAsync(PosDbContext db, User user, string? code, CancellationToken ct)
    {
        var key = Unprotect(user.TwoFactorSecret);
        if (key is not null && Totp.Match(key, code, time.GetUtcNow(), user.TwoFactorLastStep) is long step)
        {
            var taken = await db.Users
                .Where(u => u.Id == user.Id && u.TwoFactorLastStep < step)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorLastStep, step), ct);
            if (taken == 0) return false;
            user.TwoFactorLastStep = step;
            return true;
        }

        var before = user.TwoFactorRecoveryCodes;
        if (RecoveryCodes.Use(before, code) is not string after) return false;
        var used = await db.Users
            .Where(u => u.Id == user.Id && u.TwoFactorRecoveryCodes == before)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorRecoveryCodes, after), ct);
        if (used == 0) return false;
        user.TwoFactorRecoveryCodes = after;
        return true;
    }

    /// <summary>Turns two-step login off and forgets every trusted computer; the caller saves.</summary>
    public static void Clear(User user)
    {
        user.TwoFactorSecret = null;
        user.TwoFactorPendingSecret = null;
        user.TwoFactorEnabledAtUtc = null;
        user.TwoFactorRecoveryCodes = null;
        user.TwoFactorLastStep = 0;
        user.TwoFactorTrustStamp = NewStamp();
    }

    public string CreateTrust(User user)
    {
        user.TwoFactorTrustStamp ??= NewStamp();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key));
        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: TrustAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim("stamp", user.TwoFactorTrustStamp),
                new Claim("tenant", tenant.Current.Id),
            ],
            notBefore: time.GetUtcNow().UtcDateTime,
            expires: time.GetUtcNow().UtcDateTime.AddDays(TrustDays),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public bool TrustedBy(User user, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048 || string.IsNullOrEmpty(user.TwoFactorTrustStamp)) return false;
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidIssuer = jwt.Issuer,
                ValidAudience = TrustAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromMinutes(1),
                LifetimeValidator = (notBefore, expires, _, _) =>
                    expires is DateTime end && end > time.GetUtcNow().UtcDateTime,
            }, out _);
            return principal.FindFirstValue(JwtRegisteredClaimNames.Sub) == user.Id.ToString()
                && principal.FindFirstValue("tenant") == tenant.Current.Id
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(principal.FindFirstValue("stamp") ?? ""),
                    Encoding.UTF8.GetBytes(user.TwoFactorTrustStamp));
        }
        catch (Exception e) when (e is SecurityTokenException or ArgumentException)
        {
            return false;
        }
    }
}
