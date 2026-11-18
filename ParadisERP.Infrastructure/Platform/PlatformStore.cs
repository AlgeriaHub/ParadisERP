using System.Text.Json;
using ParadisERP.Application.Regional;
using ParadisERP.Application.Subscriptions;
using ParadisERP.Infrastructure.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Platform;

public sealed class PlatformStore(PlatformDbContext db)
{
    public async Task<(SubscriptionOptions? Settings, long Revision)> ReadAsync(string companyId, CancellationToken ct)
    {
        var row = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId, ct);
        return row is null ? (null, 0) : (JsonSerializer.Deserialize<SubscriptionOptions>(row.SettingsJson), row.Revision);
    }

    public async Task<bool> UpdateAsync(string companyId, SubscriptionOptions settings, long revision, string actor, string reason, CancellationToken ct)
    {
        _ = SubscriptionPolicy.Evaluate(settings.Plan, settings.Status, settings.ExpiresAtUtc, settings.GraceDays,
            settings.MaxUsers, settings.MaxBranches, settings.MaxDevices, DateTimeOffset.UtcNow);
        if (string.IsNullOrWhiteSpace(settings.Plan) || settings.Plan.Length > 100 || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("A plan name and a reason (up to 500 characters) are required.");
        var row = await db.Subscriptions.SingleOrDefaultAsync(x => x.CompanyId == companyId, ct);
        if ((row?.Revision ?? 0) != revision) return false;
        var before = row?.SettingsJson;
        if (row is null) { row = new SubscriptionOverride { CompanyId = companyId }; db.Subscriptions.Add(row); }
        row.SettingsJson = JsonSerializer.Serialize(settings);
        row.Revision = revision + 1;
        db.Audit.Add(new PlatformAudit
        {
            CompanyId = companyId, ActorSubject = actor, Action = "subscription.updated",
            Detail = JsonSerializer.Serialize(new { reason, before, after = row.SettingsJson, row.Revision })
        });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) when (db.ChangeTracker.HasChanges())
        {
            // Distinguish an edit race from an unrelated database failure.
            db.ChangeTracker.Clear();
            var current = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId, ct);
            if ((current?.Revision ?? 0) != revision) return false;
            throw;
        }
    }

    public async Task<(RegionalSettings? Settings, long Revision)> ReadRegionalAsync(string companyId, CancellationToken ct)
    {
        var row = await db.Regional.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId, ct);
        return row is null ? (null, 0) : (JsonSerializer.Deserialize<RegionalSettings>(row.SettingsJson), row.Revision);
    }

    /// <summary>Validates, then saves with the same revision check and audit trail as subscriptions.</summary>
    public async Task<bool> UpdateRegionalAsync(string companyId, RegionalSettings settings, long revision, string actor, string reason, CancellationToken ct)
    {
        var packs = await db.LanguagePacks.AsNoTracking().Select(x => x.Code).ToListAsync(ct);
        RegionalSettingsValidator.NormalizeAndValidate(settings, packs.Contains);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("A reason (up to 500 characters) is required.");
        var row = await db.Regional.SingleOrDefaultAsync(x => x.CompanyId == companyId, ct);
        if ((row?.Revision ?? 0) != revision) return false;
        var before = row?.SettingsJson;
        if (row is null) { row = new RegionalOverride { CompanyId = companyId }; db.Regional.Add(row); }
        row.SettingsJson = JsonSerializer.Serialize(settings);
        row.Revision = revision + 1;
        db.Audit.Add(new PlatformAudit
        {
            CompanyId = companyId, ActorSubject = actor, Action = "regional.updated",
            Detail = JsonSerializer.Serialize(new { reason, before, after = row.SettingsJson, row.Revision })
        });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) when (db.ChangeTracker.HasChanges())
        {
            db.ChangeTracker.Clear();
            var current = await db.Regional.AsNoTracking().SingleOrDefaultAsync(x => x.CompanyId == companyId, ct);
            if ((current?.Revision ?? 0) != revision) return false;
            throw;
        }
    }

    /// <summary>Audit rows for platform-wide changes use this in place of a company ID.</summary>
    public const string PlatformScope = "*platform*";

    public async Task<IReadOnlyList<(LanguagePack Pack, long Revision)>> ListPacksAsync(CancellationToken ct) =>
        (await db.LanguagePacks.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct))
            .Select(x => (JsonSerializer.Deserialize<LanguagePack>(x.PackJson)!, x.Revision)).ToList();

    public async Task<bool> SavePackAsync(LanguagePack pack, long revision, string actor, string reason, CancellationToken ct)
    {
        LanguagePack.NormalizeAndValidate(pack);
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("A reason (up to 500 characters) is required.");
        var row = await db.LanguagePacks.SingleOrDefaultAsync(x => x.Code == pack.Code, ct);
        if ((row?.Revision ?? 0) != revision) return false;
        if (row is null) { row = new LanguagePackRow { Code = pack.Code }; db.LanguagePacks.Add(row); }
        row.PackJson = JsonSerializer.Serialize(pack);
        row.Revision = revision + 1;
        db.Audit.Add(new PlatformAudit
        {
            CompanyId = PlatformScope, ActorSubject = actor, Action = "language.saved",
            Detail = JsonSerializer.Serialize(new { reason, pack.Code, pack.Label, strings = pack.Strings.Count, row.Revision })
        });
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) when (db.ChangeTracker.HasChanges())
        {
            db.ChangeTracker.Clear();
            var current = await db.LanguagePacks.AsNoTracking().SingleOrDefaultAsync(x => x.Code == pack.Code, ct);
            if ((current?.Revision ?? 0) != revision) return false;
            throw;
        }
    }

    /// <summary>Refuses while any company's saved profile (or <paramref name="inUseElsewhere"/>) still enables the language.</summary>
    public async Task<bool> DeletePackAsync(string code, string actor, Func<string, bool> inUseElsewhere, CancellationToken ct)
    {
        var row = await db.LanguagePacks.SingleOrDefaultAsync(x => x.Code == code, ct);
        if (row is null) return false;
        var profiles = await db.Regional.AsNoTracking().Select(x => x.SettingsJson).ToListAsync(ct);
        if (inUseElsewhere(code) || profiles.Any(json => JsonSerializer.Deserialize<RegionalSettings>(json)!.Languages.Contains(code)))
            throw new InvalidOperationException("A company still uses this language. Remove it from their country settings first.");
        db.LanguagePacks.Remove(row);
        db.Audit.Add(new PlatformAudit { CompanyId = PlatformScope, ActorSubject = actor, Action = "language.deleted", Detail = JsonSerializer.Serialize(new { code }) });
        await db.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class PlatformLanguagePackStore(PlatformDbContext db) : ILanguagePackStore
{
    public async Task<IReadOnlyDictionary<string, long>> RevisionsAsync(CancellationToken ct) =>
        await db.LanguagePacks.AsNoTracking().ToDictionaryAsync(x => x.Code, x => x.Revision, ct);

    public async Task<LanguagePack?> GetAsync(string code, CancellationToken ct)
    {
        var row = await db.LanguagePacks.AsNoTracking().SingleOrDefaultAsync(x => x.Code == code, ct);
        return row is null ? null : JsonSerializer.Deserialize<LanguagePack>(row.PackJson);
    }
}

/// <summary>Packs from the <c>LanguagePacks</c> configuration section, for installs without the platform portal.</summary>
public sealed class ConfiguredLanguagePackStore : ILanguagePackStore
{
    private readonly Dictionary<string, (LanguagePack Pack, long Revision)> packs = new();

    public ConfiguredLanguagePackStore(IEnumerable<LanguagePack> configured)
    {
        foreach (var pack in configured)
        {
            LanguagePack.NormalizeAndValidate(pack);
            // Content hash: an edited file gives a new revision, so tills download it again.
            var hash = System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(pack));
            if (!packs.TryAdd(pack.Code, (pack, Math.Abs(BitConverter.ToInt64(hash, 0) % long.MaxValue))))
                throw new ArgumentException($"Language pack '{pack.Code}' is configured twice.");
        }
    }

    public bool Contains(string code) => packs.ContainsKey(code);

    public Task<IReadOnlyDictionary<string, long>> RevisionsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(packs.ToDictionary(x => x.Key, x => x.Value.Revision));

    public Task<LanguagePack?> GetAsync(string code, CancellationToken ct) =>
        Task.FromResult(packs.TryGetValue(code, out var entry) ? entry.Pack : null);
}
