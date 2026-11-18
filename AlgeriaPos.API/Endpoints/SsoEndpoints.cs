using System.Security.Claims;
using AlgeriaPos.API.Identity;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Infrastructure.Integrations;
using AlgeriaPos.Application.Subscriptions;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Platform;
using AlgeriaPos.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.API.Endpoints;

public static class SsoEndpoints
{
    public static void MapSsoEndpoints(this IEndpointRouteBuilder app, SsoOptions sso, PlatformOptions platform)
    {
        app.MapGet("/api/auth/sso/config", (HttpContext context) => Results.Ok(new
        { enabled = sso.Enabled, isPlatform = platform.Enabled && context.Request.Host.Host == platform.Host })).AllowAnonymous();
        if (!sso.Enabled) return;
        app.MapGet("/api/auth/sso/login", (HttpContext context) =>
        {
            var properties = new AuthenticationProperties
            { RedirectUri = string.Equals(context.Request.Host.Host, platform.Host, StringComparison.OrdinalIgnoreCase) ? "/#/platform" : "/?sso=complete#/login" };
            properties.Items["origin_host"] = context.Request.Host.Host;
            return Results.Challenge(properties, [SsoConfiguration.OidcScheme]);
        }).AllowAnonymous();

        app.MapGet("/api/auth/sso/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        }).RequireAuthorization(policy => policy.AddAuthenticationSchemes(SsoConfiguration.CookieScheme).RequireAuthenticatedUser());

        app.MapPost("/api/auth/sso/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid request token."); }
            await context.SignOutAsync(SsoConfiguration.CookieScheme);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.AddAuthenticationSchemes(SsoConfiguration.CookieScheme).RequireAuthenticatedUser());

        app.MapGet("/api/auth/sso/session", async (string? deviceCode, HttpContext context, TenantContext tenant,
            PlatformDbContext platformDb, PosDbContext db, TokenService tokens, ISubscriptionProvider subscriptions,
            IRegionalSettingsProvider regional, ILanguagePackStore languagePacks, TenantIntegrations integrations, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var issuer = context.User.FindFirstValue("idp_iss");
            var subject = context.User.FindFirstValue("sub");
            var link = await platformDb.Identities.AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyId == tenant.Current.Id && x.Issuer == issuer && x.Subject == subject, ct);
            if (link is null) return Results.Problem("Your SSO identity is not linked to this company. Contact your operator.", statusCode: 403);
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == link.UserId && x.IsActive && !x.IsDeleted, ct);

            // No terminal code: an office session, or a cashier's own till — the same rule as the
            // password login.
            string code;
            if (string.IsNullOrWhiteSpace(deviceCode))
            {
                if (user is null) return Results.Forbid();
                var office = await AlgeriaPos.Infrastructure.Authorization.ServerAuthorization.ResolveAsync(db, user.Id, ct);
                if (office is null) return Results.Forbid();
                if (BranchScope.AllowsOfficeSession(office.Scope))
                {
                    var officeUser = await BranchAssignment.LoginUserAsync(user, db, ct);
                    var (officeToken, officeExpires) = tokens.Create(user, "", officeUser.Permissions ?? []);
                    return Results.Ok(new LoginResponse(officeToken, officeExpires,
                        officeUser, "", subscriptions.GetAccess(), regional.Get(),
                        await languagePacks.RevisionsForAsync(regional.Get(), ct),
                        await integrations.PublicViewAsync(ct), null));
                }
                var tracked = await db.Users.FirstAsync(x => x.Id == user.Id, ct);
                var own = await CashierTills.ForSignInAsync(db, tracked, office.Scope, ct);
                if (own is null)
                    return Results.Problem(CashierTills.NoOwnTillMessage, statusCode: StatusCodes.Status403Forbidden,
                        extensions: new Dictionary<string, object?> { ["code"] = BranchScope.TillRequiredCode });
                code = own.Code;
            }
            else
            {
                code = deviceCode.Trim().ToUpperInvariant();
            }
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code && x.IsActive, ct);
            if (user is null || device is null) return Results.Forbid();
            // The same terminal binding as the password login (docs/07 §2.2): SSO changes who
            // vouches for the person, not which tills a cashier may open.
            var decided = await AlgeriaPos.Infrastructure.Authorization.ServerAuthorization.ResolveAsync(db, user.Id, ct);
            if (decided is null) return Results.Forbid();
            if (BranchScope.RefusesTerminal(user, decided.Scope, device))
                return Results.Problem(BranchScope.WrongTerminalMessage, statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["code"] = BranchScope.WrongTerminalCode });
            var loginUser = await BranchAssignment.LoginUserAsync(user, db, ct);
            var (token, expires) = tokens.Create(user, code, loginUser.Permissions ?? []);
            return Results.Ok(new LoginResponse(token, expires,
                loginUser, code, subscriptions.GetAccess(), regional.Get(),
                await languagePacks.RevisionsForAsync(regional.Get(), ct),
                await integrations.PublicViewAsync(ct), device.BranchId,
                await CashierTills.LastSequenceAsync(db, code, ct)));
        }).RequireAuthorization(policy => policy.AddAuthenticationSchemes(SsoConfiguration.CookieScheme).RequireAuthenticatedUser());
    }
}
