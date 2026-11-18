using System.Security.Claims;
using System.Text.Json;
using ParadiERP.API.Identity;
using ParadiERP.Application.Regional;
using ParadiERP.Application.Subscriptions;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Platform;
using ParadiERP.Infrastructure.Subscriptions;
using ParadiERP.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.API.Endpoints;

public record UpdateSubscriptionRequest(SubscriptionOptions Settings, long Revision, string Reason);
public record LinkIdentityRequest(string Subject, Guid UserId);
public record UpdateRegionalRequest(RegionalSettings Settings, long Revision, string Reason);
public record SaveLanguagePackRequest(LanguagePack Pack, long Revision, string Reason);

public static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app, PlatformOptions options)
    {
        if (!options.Enabled) return;
        var group = MapPlatformGroup(app);
        group.MapGet("/session", (HttpContext context, IAntiforgery antiforgery) => Results.Ok(new
        { name = context.User.FindFirstValue("name"), csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken }));
        group.MapGet("/companies", async (TenantRegistry registry, PlatformStore store, CompanyProvisioner provisioner, CancellationToken ct) =>
        {
            var companies = new List<object>();
            var provisioned = await provisioner.ListAsync(ct);
            foreach (var company in registry.Tenants)
            {
                var row = provisioned.GetValueOrDefault(company.Id);
                var (overridden, revision) = await store.ReadAsync(company.Id, ct);
                var settings = overridden ?? company.Subscription;
                var (regionalOverride, regionalRevision) = await store.ReadRegionalAsync(company.Id, ct);
                companies.Add(new { company.Id, company.Hosts, settings, revision, access = SubscriptionPolicy.Evaluate(
                    settings.Plan, settings.Status, settings.ExpiresAtUtc, settings.GraceDays, settings.MaxUsers,
                    settings.MaxBranches, settings.MaxDevices, DateTimeOffset.UtcNow),
                    regional = regionalOverride ?? company.ConfiguredRegional(), regionalRevision,
                    source = company.Source.ToString(), state = company.Archived ? CompanyStates.Archived : CompanyStates.Active,
                    companyRevision = row?.Revision ?? 0, createdAtUtc = row?.CreatedAtUtc });
            }
            return Results.Ok(companies);
        });
        group.MapGet("/regional-presets", () => Results.Ok(
            RegionalPresets.Countries.ToDictionary(code => code, code => RegionalPresets.Create(code))));
        group.MapPut("/companies/{id}/regional", async (string id, UpdateRegionalRequest request,
            ClaimsPrincipal caller, TenantRegistry registry, PlatformStore store, CancellationToken ct) =>
        {
            if (!registry.Tenants.Any(x => x.Id == id)) return Results.NotFound();
            if (request.Settings is null || request.Revision < 0) return Results.BadRequest("Settings and a valid revision are required.");
            try
            {
                return await store.UpdateRegionalAsync(id, request.Settings, request.Revision, caller.FindFirstValue("sub")!, request.Reason, ct)
                    ? Results.NoContent() : Results.Conflict("Another operator changed this country profile. Reload before saving.");
            }
            catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
        });
        group.MapPut("/companies/{id}/subscription", async (string id, UpdateSubscriptionRequest request,
            ClaimsPrincipal caller, TenantRegistry registry, PlatformStore store, CancellationToken ct) =>
        {
            if (!registry.Tenants.Any(x => x.Id == id)) return Results.NotFound();
            if (request.Settings is null || request.Revision < 0) return Results.BadRequest("Settings and a valid revision are required.");
            try
            {
                return await store.UpdateAsync(id, request.Settings, request.Revision, caller.FindFirstValue("sub")!, request.Reason, ct)
                    ? Results.NoContent() : Results.Conflict("Another operator changed this subscription. Reload before saving.");
            }
            catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
        });
        group.MapGet("/language-packs", async (PlatformStore store, CancellationToken ct) =>
            Results.Ok((await store.ListPacksAsync(ct)).Select(x => new
            { x.Pack.Code, x.Pack.Label, x.Pack.Rtl, x.Pack.FallbackLanguage, strings = x.Pack.Strings.Count, x.Revision })));
        PeoplePageRoutes.MapLanguagePacksPage(group);
        group.MapGet("/language-packs/{code}", async (string code, PlatformStore store, CancellationToken ct) =>
        {
            var pack = (await store.ListPacksAsync(ct)).FirstOrDefault(x => x.Pack.Code == code).Pack;
            return pack is null ? Results.NotFound() : Results.Ok(pack);
        });
        group.MapPut("/language-packs/{code}", async (string code, SaveLanguagePackRequest request, ClaimsPrincipal caller,
            PlatformStore store, CancellationToken ct) =>
        {
            if (request.Pack is null || request.Revision < 0) return Results.BadRequest("A pack and a valid revision are required.");
            if (!string.Equals(request.Pack.Code?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("The pack's code must match the address.");
            try
            {
                return await store.SavePackAsync(request.Pack, request.Revision, caller.FindFirstValue("sub")!, request.Reason, ct)
                    ? Results.NoContent() : Results.Conflict("Another operator changed this language pack. Reload before saving.");
            }
            catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
        });
        group.MapDelete("/language-packs/{code}", async (string code, ClaimsPrincipal caller, TenantRegistry registry,
            PlatformStore store, CancellationToken ct) =>
        {
            try
            {
                return await store.DeletePackAsync(code, caller.FindFirstValue("sub")!,
                    language => registry.Tenants.Any(t => t.ConfiguredRegional().Languages.Contains(language)), ct)
                    ? Results.NoContent() : Results.NotFound();
            }
            catch (InvalidOperationException exception) { return Results.Conflict(exception.Message); }
        });
        group.MapGet("/companies/{id}/audit", async (string id, PlatformDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Audit.AsNoTracking().Where(x => x.CompanyId == id).OrderByDescending(x => x.CreatedAtUtc).Take(50).ToListAsync(ct)));
        group.MapGet("/companies/{id}/users", async (string id, TenantRegistry registry, CancellationToken ct) =>
        {
            var company = registry.Tenants.FirstOrDefault(x => x.Id == id);
            if (company is null) return Results.NotFound();
            await using var db = OpenCompany(company);
            return Results.Ok(await db.Users.AsNoTracking().Where(x => !x.IsDeleted).OrderBy(x => x.Username)
                .Select(x => new { x.Id, x.Username, x.FullName, role = x.Role.ToString(), x.IsActive }).ToListAsync(ct));
        });
        group.MapGet("/companies/{id}/identities", async (string id, PlatformDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Identities.AsNoTracking().Where(x => x.CompanyId == id).ToListAsync(ct)));
        group.MapPost("/companies/{id}/identities", async (string id, LinkIdentityRequest request, ClaimsPrincipal caller,
            TenantRegistry registry, SsoOptions sso, PlatformDbContext db, CancellationToken ct) =>
        {
            var company = registry.Tenants.FirstOrDefault(x => x.Id == id);
            if (company is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 200)
                return Results.BadRequest("A valid identity subject is required.");
            await using var companyDb = OpenCompany(company);
            if (!await companyDb.Users.AnyAsync(x => x.Id == request.UserId && !x.IsDeleted, ct)) return Results.BadRequest("Unknown company user.");
            var identity = new ExternalIdentity { CompanyId = id, Issuer = sso.Authority.TrimEnd('/'), Subject = request.Subject.Trim(), UserId = request.UserId };
            db.Identities.Add(identity);
            db.Audit.Add(new PlatformAudit { CompanyId = id, ActorSubject = caller.FindFirstValue("sub")!, Action = "identity.linked", Detail = JsonSerializer.Serialize(new { identity.Subject, identity.UserId }) });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                if (await db.Identities.AsNoTracking().AnyAsync(x => x.CompanyId == id && x.Subject == identity.Subject && x.Issuer == identity.Issuer, ct))
                    return Results.Conflict("That identity is already linked in this company.");
                throw;
            }
            return Results.Created($"/api/platform/companies/{id}/identities/{identity.Id}", identity);
        });
        group.MapDelete("/companies/{id}/identities/{identityId:guid}", async (string id, Guid identityId,
            ClaimsPrincipal caller, PlatformDbContext db, CancellationToken ct) =>
        {
            var identity = await db.Identities.SingleOrDefaultAsync(x => x.CompanyId == id && x.Id == identityId, ct);
            if (identity is null) return Results.NotFound();
            db.Identities.Remove(identity);
            db.Audit.Add(new PlatformAudit { CompanyId = id, ActorSubject = caller.FindFirstValue("sub")!, Action = "identity.unlinked", Detail = JsonSerializer.Serialize(new { identity.Subject, identity.UserId }) });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>Operator-only routes: SSO operator policy, no caching, antiforgery on every write.</summary>
    internal static RouteGroupBuilder MapPlatformGroup(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform").WithTags("Platform").RequireAuthorization(SsoConfiguration.OperatorPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsGet(context.HttpContext.Request.Method))
            {
                try { await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext); }
                catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid request token. Reload and try again."); }
            }
            return await next(context);
        });
        return group;
    }

    private static PosDbContext OpenCompany(TenantOptions company)
    {
        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, company.DatabaseProvider, company.ConnectionString);
        return new PosDbContext(options.Options);
    }
}
