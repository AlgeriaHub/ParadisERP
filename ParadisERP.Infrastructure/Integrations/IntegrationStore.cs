using System.Text.Json;
using ParadisERP.Application.Integrations;
using ParadisERP.Infrastructure.Platform;
using ParadisERP.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Integrations;

/// <summary>Encrypts integration secrets at rest. Losing the Data Protection keys makes them unreadable (re-enter them).</summary>
public sealed class IntegrationSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("ParadisERP.Integrations");

    public string Protect(IntegrationSecrets secrets) => protector.Protect(JsonSerializer.Serialize(secrets));

    public IntegrationSecrets Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return new();
        try { return JsonSerializer.Deserialize<IntegrationSecrets>(protector.Unprotect(value)) ?? new(); }
        catch (System.Security.Cryptography.CryptographicException) { return new(); }
    }
}

public sealed record IntegrationsSnapshot(IntegrationSettings Settings, IntegrationSecrets Secrets, long Revision);

public sealed class PlatformIntegrationStore(PlatformDbContext db, IntegrationSecretProtector protector)
{
    public async Task<IntegrationsSnapshot> ReadAsync(string companyId, CancellationToken ct)
    {
        var row = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId, ct);
        return row is null
            ? new(new IntegrationSettings(), new IntegrationSecrets(), 0)
            : new(JsonSerializer.Deserialize<IntegrationSettings>(row.SettingsJson) ?? new(), protector.Unprotect(row.ProtectedSecretsJson), row.Revision);
    }

    /// <summary>Validates and saves with a revision check. The audit entry records which secrets changed, never their values.</summary>
    public async Task<bool> SaveAsync(string companyId, IntegrationSettings settings, SecretChanges? changes, long revision,
        string actor, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("A reason (up to 500 characters) is required.");
        var row = await db.Integrations.SingleOrDefaultAsync(x => x.CompanyId == companyId, ct);
        if ((row?.Revision ?? 0) != revision) return false;
        var current = row is null ? new IntegrationSecrets() : protector.Unprotect(row.ProtectedSecretsJson);
        changes ??= new();
        var secrets = changes.ApplyTo(current);
        IntegrationSettingsValidator.NormalizeAndValidate(settings, secrets);

        if (row is null) { row = new CompanyIntegrationsRow { CompanyId = companyId }; db.Integrations.Add(row); }
        var before = row.SettingsJson;
        row.SettingsJson = JsonSerializer.Serialize(settings);
        row.ProtectedSecretsJson = protector.Protect(secrets);
        row.Revision = revision + 1;
        db.Audit.Add(new PlatformAudit
        {
            CompanyId = companyId, ActorSubject = actor, Action = "integrations.updated",
            Detail = JsonSerializer.Serialize(new
            {
                reason, before, after = row.SettingsJson, row.Revision,
                secretsChanged = new
                {
                    smsToken = current.SmsToken != secrets.SmsToken,
                    smtpPassword = current.SmtpPassword != secrets.SmtpPassword,
                    webhookSecret = current.WebhookSecret != secrets.WebhookSecret
                }
            })
        });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) when (db.ChangeTracker.HasChanges())
        {
            db.ChangeTracker.Clear();
            var latest = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId, ct);
            if ((latest?.Revision ?? 0) != revision) return false;
            throw;
        }
    }
}

public sealed class PlatformIntegrationsProvider(PlatformIntegrationStore store) : ICompanyIntegrationsProvider
{
    public async Task<(IntegrationSettings Settings, IntegrationSecrets Secrets)> GetAsync(string companyId, CancellationToken ct)
    {
        var snapshot = await store.ReadAsync(companyId, ct);
        return (snapshot.Settings, snapshot.Secrets);
    }
}

/// <summary>Single-shop installs: <c>Integrations</c> and <c>IntegrationSecrets</c> configuration sections.</summary>
public sealed class ConfiguredIntegrationsProvider : ICompanyIntegrationsProvider
{
    private readonly IntegrationSettings settings;
    private readonly IntegrationSecrets secrets;

    public ConfiguredIntegrationsProvider(IntegrationSettings? settings, IntegrationSecrets? secrets)
    {
        this.settings = settings ?? new();
        this.secrets = secrets ?? new();
        IntegrationSettingsValidator.NormalizeAndValidate(this.settings, this.secrets);
    }

    public Task<(IntegrationSettings Settings, IntegrationSecrets Secrets)> GetAsync(string companyId, CancellationToken ct) =>
        Task.FromResult((settings, secrets));
}

/// <summary>The current request's company integrations, loaded once per request.</summary>
public sealed class TenantIntegrations(TenantContext tenant, ICompanyIntegrationsProvider provider)
{
    private (IntegrationSettings Settings, IntegrationSecrets Secrets)? loaded;

    public string CompanyId => tenant.Current.Id;

    public async Task<(IntegrationSettings Settings, IntegrationSecrets Secrets)> GetAsync(CancellationToken ct) =>
        loaded ??= await provider.GetAsync(tenant.Current.Id, ct);

    public async Task<IntegrationsPublicView> PublicViewAsync(CancellationToken ct)
    {
        var (settings, _) = await GetAsync(ct);
        return settings.PublicView();
    }
}
