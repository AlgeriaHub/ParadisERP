using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.FixedAssets;

/// <summary>The reads every part of the register shares (REN-12, Phase M7a): the settings, the map's slots, the stored events.</summary>
public static class FixedAssetData
{
    /// <summary>The settings in force: the saved row, or a fresh row's defaults before the first save. Never tracked.</summary>
    public static async Task<FixedAssetSettings> CurrentAsync(PosDbContext db, CancellationToken ct = default) =>
        await db.FixedAssetSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new FixedAssetSettings();

    /// <summary>Whether the register holds the hire equipment: the treatment is on and the switch-over is posted.</summary>
    public static bool Active(FixedAssetSettings s) =>
        s.Treatment == RentalEquipmentTreatment.FixedAssets && s.SwitchedAtUtc is not null && s.SwitchMonth is not null;

    public static async Task<FixedAssetPolicy> PolicyAsync(PosDbContext db, FixedAssetSettings s, CancellationToken ct = default)
    {
        var lives = await db.FixedAssetLives.AsNoTracking().ToDictionaryAsync(l => l.ProductId, l => l.UsefulLifeMonths, ct);
        var methods = await db.FixedAssetMethods.AsNoTracking().ToDictionaryAsync(m => m.ProductId, ct);
        return new FixedAssetPolicy(s.DefaultUsefulLifeMonths, lives, s.ResidualPercent, s.Method, s.SwitchToStraightLine, methods);
    }

    /// <summary>
    /// The types with depreciation posted on them, whose method is therefore fixed: a live (not reversed) month posting charged
    /// them, or a stored event (a disposal, a wreck, a transfer) carried their accumulated depreciation out in its own entry.
    /// None before the switch-over.
    /// </summary>
    public static async Task<HashSet<Guid>> DepreciationPostedAsync(PosDbContext db, FixedAssetSettings s, FixedAssetPolicy policy, CancellationToken ct = default)
    {
        if (s.SwitchedAtUtc is null || s.SwitchMonth is null) return [];
        // Decimals compare client-side (SQLite keeps them as text).
        var posted = (await db.FixedAssetMovements.AsNoTracking().Select(m => new { m.ProductId, m.Depreciation }).ToListAsync(ct))
            .Where(m => m.Depreciation != 0m).Select(m => m.ProductId).ToHashSet();
        var live = (await db.FixedAssetMonthPostings.AsNoTracking().Where(p => p.ReversedAtUtc == null)
                .Select(p => new { p.Year, p.Month }).ToListAsync(ct))
            .Select(p => new DateOnly(p.Year, p.Month, 1)).ToHashSet();
        if (live.Count == 0) return posted;
        var rows = await RowsAsync(db, null, null, ct);
        var engine = Replay(s, policy, rows, live.Max());
        posted.UnionWith(engine.Charges.Where(c => c.Amount != 0m && live.Contains(c.Month)).Select(c => c.ProductId));
        return posted;
    }

    /// <summary>Each fixed-asset slot to the active account it names; a slot absent from the result is not mapped.</summary>
    public static async Task<Dictionary<string, LedgerAccount>> AccountsAsync(PosDbContext db, CancellationToken ct = default)
    {
        var slots = FixedAssetAccountSlots.All;
        var map = await db.ManufacturingAccountMappings.AsNoTracking()
            .Where(m => slots.Contains(m.Slot) && m.LedgerAccountId != null)
            .ToDictionaryAsync(m => m.Slot, m => m.LedgerAccountId!.Value, ct);
        var ids = map.Values.Distinct().ToList();
        var accounts = ids.Count == 0 ? new Dictionary<Guid, LedgerAccount>()
            : await db.LedgerAccounts.AsNoTracking().Where(a => ids.Contains(a.Id) && a.IsActive).ToDictionaryAsync(a => a.Id, ct);
        return map.Where(kv => accounts.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => accounts[kv.Value]);
    }

    /// <summary>The company's zone as the register saw it last; UTC when none was ever saved.</summary>
    public static TimeZoneInfo Zone(FixedAssetSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.TimeZoneId)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// The stored events of <paramref name="productIds"/> (every product when null) before <paramref name="beforeUtc"/> (all when
    /// null), with those added in this unit of work and not saved yet, in the order they happened.
    /// </summary>
    public static async Task<List<FixedAssetMovement>> RowsAsync(
        PosDbContext db, IReadOnlyCollection<Guid>? productIds, DateTime? beforeUtc, CancellationToken ct = default)
    {
        var saved = await db.FixedAssetMovements.AsNoTracking()
            .Where(m => (productIds == null || productIds.Contains(m.ProductId)) && (beforeUtc == null || m.AtUtc < beforeUtc))
            .ToListAsync(ct);
        var ids = saved.Select(m => m.Id).ToHashSet();
        var pending = db.ChangeTracker.Entries<FixedAssetMovement>()
            .Where(e => e.State == EntityState.Added).Select(e => e.Entity)
            .Where(m => !ids.Contains(m.Id) && (productIds == null || productIds.Contains(m.ProductId)) && (beforeUtc == null || m.AtUtc < beforeUtc));
        return saved.Concat(pending).OrderBy(m => m.AtUtc).ThenBy(m => m.Kind == FixedAssetMovementKind.Opening ? 0 : 1).ToList();
    }

    /// <summary>The register replayed from its events, depreciated through <paramref name="through"/> when given.</summary>
    public static FixedAssetEngine Replay(FixedAssetSettings s, FixedAssetPolicy policy, IEnumerable<FixedAssetMovement> rows, DateOnly? through)
    {
        var zone = Zone(s);
        var engine = new FixedAssetEngine(s.SwitchMonth!.Value, policy);
        foreach (var row in rows) engine.Apply(row, FixedAssetEngine.MonthOf(row.AtUtc, zone));
        if (through is DateOnly month) engine.DepreciateThrough(month);
        return engine;
    }

    /// <summary>The hire products among <paramref name="productIds"/>: the ones the register holds while it is active.</summary>
    public static async Task<HashSet<Guid>> HireAsync(PosDbContext db, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) =>
        productIds.Count == 0 ? []
            : (await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id) && p.IsRentable).Select(p => p.Id).ToListAsync(ct)).ToHashSet();

    /// <summary>
    /// The products whose cost the register carries now (active, and rentable): a cost-price revaluation of the stock on
    /// hand leaves them out — the register keeps their historical cost.
    /// </summary>
    public static async Task<HashSet<Guid>> HeldAsync(PosDbContext db, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default) =>
        Active(await CurrentAsync(db, ct)) ? await HireAsync(db, productIds, ct) : [];
}
