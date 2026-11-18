using ParadiERP.Application.Integrations;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ParadiERP.Infrastructure.Integrations;

public sealed class IntegrationOutboxOptions
{
    public int IntervalSeconds { get; set; } = 15;
    public int BatchSize { get; set; } = 20;
}

/// <summary>Delivers each company's pending webhook events, retrying with exponential backoff.</summary>
public sealed class IntegrationOutboxWorker(
    IServiceScopeFactory scopes, TenantRegistry registry, IntegrationOutboxOptions options, ILogger<IntegrationOutboxWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.IntervalSeconds)));
        do
        {
            foreach (var company in registry.Tenants)
            {
                try { await DeliverAsync(company, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Webhook delivery failed for company {Company}.", company.Id);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task DeliverAsync(TenantOptions company, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Initialize(company);
        var (settings, secrets) = await scope.ServiceProvider.GetRequiredService<ICompanyIntegrationsProvider>().GetAsync(company.Id, ct);
        // Events stay queued while the webhook is off, and go out once it is configured again.
        if (!settings.WebhookReady(secrets)) return;

        var db = scope.ServiceProvider.GetRequiredService<PosDbContext>();
        var now = DateTime.UtcNow;
        var due = await db.IntegrationOutbox
            .Where(x => x.SentAtUtc == null && x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.CreatedAtUtc)
            .Take(options.BatchSize)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        var http = scope.ServiceProvider.GetRequiredService<IntegrationHttp>();
        foreach (var item in due)
        {
            var result = await http.PostWebhookAsync(settings.Webhook.Url, secrets.WebhookSecret!, item.EventType, item.Payload, ct);
            item.Attempts++;
            if (result.Ok)
            {
                item.SentAtUtc = DateTime.UtcNow;
                item.LastError = null;
            }
            else
            {
                item.LastError = result.Error?[..Math.Min(result.Error.Length, 500)];
                item.NextAttemptAtUtc = DateTime.UtcNow + SaleEvents.Backoff(item.Attempts);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
