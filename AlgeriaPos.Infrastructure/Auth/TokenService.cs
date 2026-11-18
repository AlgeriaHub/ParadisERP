using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Tenancy;

namespace AlgeriaPos.Infrastructure.Auth;

public class JwtOptions
{
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "pos-api";
    public string Audience { get; set; } = "pos-app";
    /// <summary>A till's session: long-lived by design, a terminal may stay offline for days between syncs.</summary>
    public int ExpiryDays { get; set; } = 30;
    /// <summary>
    /// An office session — signed in with no till (owner, security review 2026-10-10): a working
    /// day. The offline reason for 30 days is a till's alone; an office token copied from a shared
    /// computer stayed good for a month.
    /// </summary>
    public int OfficeSessionHours { get; set; } = 10;
}

public class TokenService(JwtOptions options, TenantContext tenant)
{
    /// <param name="displayPermissions">
    /// The permission names to carry for the screens. AUTH-1: display only — every route reads the
    /// permissions the server resolves per request, never these claims.
    /// </param>
    public (string Token, DateTime ExpiresAtUtc) Create(User user, string deviceCode, IEnumerable<string> displayPermissions)
    {
        var expires = string.IsNullOrWhiteSpace(deviceCode)
            ? DateTime.UtcNow.AddHours(options.OfficeSessionHours)
            : DateTime.UtcNow.AddDays(options.ExpiryDays);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("name", user.Username),
            new("role", user.Role.ToString()),
            new("user_version", user.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("tenant", tenant.Current.Id),
            new("device", deviceCode)
        };

        claims.AddRange(displayPermissions.Select(p => new Claim(Permissions.ClaimType, p)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key));
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
