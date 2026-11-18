using System.Security.Claims;
using AlgeriaPos.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace AlgeriaPos.API.Identity;

public sealed class SsoOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public sealed class PlatformOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public string ConnectionString { get; set; } = "Data Source=platform.db";
    public string[] OperatorSubjects { get; set; } = [];
    /// <summary>Where SQLite databases of portal-created companies go; defaults to "companies" next to the platform database.</summary>
    public string CompanyDataDirectory { get; set; } = "";
}

public static class SsoConfiguration
{
    public const string CookieScheme = "sso-cookie";
    public const string OidcScheme = "keycloak";
    public const string OperatorPolicy = "platform-operator";

    public static void AddPosSso(this IServiceCollection services, SsoOptions sso, PlatformOptions platform, bool development)
    {
        services.AddSingleton(sso);
        services.AddSingleton(platform);
        if (!sso.Enabled) return;
        if (!platform.Enabled || !Uri.TryCreate(sso.Authority, UriKind.Absolute, out var authority)
            || (!development && authority.Scheme != "https") || string.IsNullOrWhiteSpace(sso.ClientId)
            || string.IsNullOrWhiteSpace(sso.ClientSecret))
            throw new InvalidOperationException("SSO requires the platform store, an HTTPS authority, a client ID, and a client secret.");

        services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        services.AddAuthentication()
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = development ? "pos.sso" : "__Host-pos-sso";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
                options.SlidingExpiration = false;
                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = context =>
                    {
                        if (!string.Equals(context.Principal?.FindFirstValue("pos_host"), context.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
                            context.RejectPrincipal();
                        return Task.CompletedTask;
                    },
                    OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; },
                    OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; }
                };
            })
            .AddOpenIdConnect(OidcScheme, options =>
            {
                options.SignInScheme = CookieScheme;
                options.Authority = sso.Authority.TrimEnd('/');
                options.ClientId = sso.ClientId;
                options.ClientSecret = sso.ClientSecret;
                options.RequireHttpsMetadata = !development;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                if (development)
                {
                    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                    options.NonceCookie.SameSite = SameSiteMode.Lax;
                }
                options.UsePkce = true;
                options.SaveTokens = false;
                options.MapInboundClaims = false;
                options.Scope.Clear(); options.Scope.Add("openid"); options.Scope.Add("profile");
                options.Events = new OpenIdConnectEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (context.Properties is null || !context.Properties.Items.TryGetValue("origin_host", out var originHost)
                            || !string.Equals(originHost, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
                            || string.IsNullOrEmpty(context.Principal?.FindFirstValue("sub")))
                        { context.Fail("Invalid SSO origin or subject."); return Task.CompletedTask; }
                        // Keep only verified identity data. Provider roles never become POS permissions.
                        // The claim type is "idp_iss", not the registered "iss": a claim literally typed
                        // "iss" is silently dropped somewhere in the cookie sign-in/ticket round-trip
                        // (reproduced with ASP.NET Core 8's cookie + OIDC handlers here) even though every
                        // other claim on the same identity survives, so avoid that exact name.
                        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim("sub", context.Principal!.FindFirstValue("sub")!),
                            new Claim("idp_iss", context.SecurityToken.Issuer),
                            new Claim("name", context.Principal.FindFirstValue("name") ?? "SSO user"),
                            new Claim("pos_host", context.Request.Host.Host.ToLowerInvariant())
                        }, CookieScheme, "name", "role"));
                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = context =>
                    {
                        context.Response.Redirect(string.Equals(context.Request.Host.Host, platform.Host, StringComparison.OrdinalIgnoreCase) ? "/?error=sso#/platform" : "/?sso=failed#/login");
                        context.HandleResponse();
                        return Task.CompletedTask;
                    }
                };
            });
        services.AddAuthorization(options => options.AddPolicy(OperatorPolicy, policy => policy
            .AddAuthenticationSchemes(CookieScheme).RequireAuthenticatedUser()
            .RequireAssertion(context => platform.OperatorSubjects.Contains(context.User.FindFirstValue("sub"), StringComparer.Ordinal)
                && context.User.FindFirstValue("pos_host") == platform.Host)));
    }
}
