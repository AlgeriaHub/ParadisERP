using System.Security.Claims;
using ParadisERP.API.Identity;
using ParadisERP.Application.Integrations;
using ParadisERP.Application.Regional;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Integrations;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Sales;
using ParadisERP.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

public record UpdateIntegrationsRequest(IntegrationSettings Settings, SecretChanges? Secrets, long Revision, string Reason);
public record SendReceiptRequest(string Channel, string To);

public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app, PlatformOptions platform)
    {
        app.MapPost("/api/sales/{id:guid}/send-receipt", async (Guid id, SendReceiptRequest request, ClaimsPrincipal caller,
            PosDbContext db, TenantIntegrations integrations, IRegionalSettingsProvider regional, IntegrationHttp http,
            SmtpReceiptSender smtp, ReceiptSendLimiter limiter, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var channel = (request?.Channel ?? "").Trim().ToLowerInvariant();
            if (channel is not ("sms" or "email")) return Results.BadRequest("Channel must be sms or email.");
            var to = IntegrationSettingsValidator.NormalizeRecipient(channel, request!.To);
            if (to is null)
                return Results.BadRequest(channel == "sms" ? "Enter a valid phone number." : "Enter a valid e-mail address.");

            var (settings, secrets) = await integrations.GetAsync(ct);
            if (channel == "sms" ? !settings.SmsReady() : !settings.EmailReady())
                return Results.BadRequest("This receipt channel is not enabled for the company.");

            // A till only ever re-sends the receipt it just printed; another branch's sale is
            // "not found" for a cashier and, since P0.1b, for a branch manager — the same answer
            // GET /api/sales/{id} gives (docs/07 §2.3). Nothing is sent and the limit is not used.
            var sale = await SalesQueries.ForReceiptAsync(db, id, await BranchScope.ForAsync(caller, db, ct), ct);
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => !s.IsDeleted, ct);
            if (sale is null || store is null) return Results.NotFound();
            if (!limiter.TryAcquire(integrations.CompanyId, id))
                return Results.Problem("This receipt has already been sent several times.", statusCode: StatusCodes.Status429TooManyRequests);

            var profile = regional.Get();
            var productIds = sale.Lines.Select(l => l.ProductId).Distinct().ToList();
            var catalogue = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var body = ReceiptTextRenderer.Render(sale, store, profile, catalogue);
            var result = channel == "sms"
                ? await http.SendSmsAsync(settings.Sms, secrets.SmsToken, to, body, ct)
                : await smtp.SendAsync(settings.Email, secrets.SmtpPassword, to, ReceiptTextRenderer.Subject(sale, store, profile), body, ct);
            if (result.Ok) return Results.NoContent();
            // Gateway details stay in the server log; the till gets a plain message.
            loggers.CreateLogger(nameof(IntegrationEndpoints)).LogWarning("Receipt {Channel} for sale {Sale} failed: {Error}", channel, id, result.Error);
            return Results.Problem("The receipt could not be sent. Try again later.", statusCode: StatusCodes.Status502BadGateway);
        }).RequireAuthorization().WithTags("Integrations")
          .WithMetadata(new BranchScopedAttribute("a cashier re-sends only a receipt of the branch their till belongs to"));

        if (!platform.Enabled) return;
        var group = app.MapGroup("/api/platform/companies/{id}/integrations").WithTags("Platform")
            .RequireAuthorization(SsoConfiguration.OperatorPolicy);
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

        group.MapGet("", async (string id, TenantRegistry registry, PlatformIntegrationStore store, CancellationToken ct) =>
        {
            var company = registry.Tenants.FirstOrDefault(x => x.Id == id);
            if (company is null) return Results.NotFound();
            var snapshot = await store.ReadAsync(id, ct);
            await using var db = OpenCompany(company);
            var pending = await db.IntegrationOutbox.AsNoTracking().Where(x => x.SentAtUtc == null)
                .OrderByDescending(x => x.CreatedAtUtc).Select(x => new { x.Attempts, x.LastError }).ToListAsync(ct);
            return Results.Ok(new
            {
                settings = snapshot.Settings,
                snapshot.Revision,
                hasSmsToken = !string.IsNullOrEmpty(snapshot.Secrets.SmsToken),
                hasSmtpPassword = !string.IsNullOrEmpty(snapshot.Secrets.SmtpPassword),
                hasWebhookSecret = !string.IsNullOrEmpty(snapshot.Secrets.WebhookSecret),
                outbox = new
                {
                    pending = pending.Count,
                    failing = pending.Count(x => x.Attempts > 0),
                    lastError = pending.Select(x => x.LastError).FirstOrDefault(x => x is not null)
                }
            });
        });

        group.MapPut("", async (string id, UpdateIntegrationsRequest request, ClaimsPrincipal caller,
            TenantRegistry registry, PlatformIntegrationStore store, CancellationToken ct) =>
        {
            if (!registry.Tenants.Any(x => x.Id == id)) return Results.NotFound();
            if (request?.Settings is null || request.Revision < 0) return Results.BadRequest("Settings and a valid revision are required.");
            try
            {
                return await store.SaveAsync(id, request.Settings, request.Secrets, request.Revision, caller.FindFirstValue("sub")!, request.Reason, ct)
                    ? Results.NoContent() : Results.Conflict("Another operator changed these integrations. Reload before saving.");
            }
            catch (ArgumentException exception) { return Results.BadRequest(exception.Message); }
        });

        group.MapPost("/test-webhook", async (string id, TenantRegistry registry, PlatformIntegrationStore store,
            IntegrationHttp http, CancellationToken ct) =>
        {
            if (!registry.Tenants.Any(x => x.Id == id)) return Results.NotFound();
            var snapshot = await store.ReadAsync(id, ct);
            if (!snapshot.Settings.WebhookReady(snapshot.Secrets)) return Results.BadRequest("Enable the webhook and save a signing secret first.");
            var result = await http.PostWebhookAsync(snapshot.Settings.Webhook.Url, snapshot.Secrets.WebhookSecret!,
                SaleEvents.Test, SaleEvents.TestPayload(id), ct);
            return Results.Ok(new { result.Ok, result.Error });
        });
    }

    private static PosDbContext OpenCompany(TenantOptions company)
    {
        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, company.DatabaseProvider, company.ConnectionString);
        return new PosDbContext(options.Options);
    }
}
