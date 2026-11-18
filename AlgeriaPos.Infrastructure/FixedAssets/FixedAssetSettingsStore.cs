using System.Globalization;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.FixedAssets;

/// <summary>A register answer: the value, or a refusal with its status, code and words.</summary>
public sealed record FixedAssetResult<T>(T? Value, int Status, string? Code, string? Error)
{
    public static FixedAssetResult<T> Done(T value) => new(value, 200, null, null);
    public static FixedAssetResult<T> Fail(int status, string code, string error) => new(default, status, code, error);
}

/// <summary>
/// REN-12's settings (Phase M7a, docs/accounting/fixed-assets.md): read with the accounting reports' key, changed under
/// accounting.manage (the route's). Every default is today's behaviour: the treatment <c>Stock</c>, so nothing in the
/// register posts until the accountant chooses fixed assets, a month, a life and the accounts, and posts the switch-over.
/// <para>
/// Once the switch-over is posted the figures that every depreciated month and every disposal were valued with are fixed —
/// the default life, the residual, the opening value, the month — and the treatment cannot go back to stock here; a type's
/// own life can still be set while the register holds none of it. The slots it posts to cannot be unmapped.
/// </para>
/// <para>
/// The method (straight line or declining balance, with its coefficient and its switch to straight line) is the company's
/// default and each type's own, and is fixed type by type: once depreciation is posted on a type (a live month posting
/// charging it, or an event carrying its accumulated depreciation out), neither its own row nor the company's default may
/// change what it is depreciated by (<see cref="MethodLockedCode"/>).
/// </para>
/// </summary>
public sealed class FixedAssetSettingsStore(PosDbContext db, IRegionalSettingsProvider regional)
{
    public const string InvalidCode = "invalid_fixed_asset_settings";
    /// <summary>A type's method (or the company's, for a type following it) changed after depreciation was posted on the type.</summary>
    public const string MethodLockedCode = "method_locked";
    public const string LockedCode = "depreciation_locked";
    public const string LifeLockedCode = "life_locked";
    public const string InvalidMapCode = "invalid_account_map";
    public const string SlotInUseCode = "slot_in_use";
    public const int MaxLifeMonths = 600;

    public async Task<FixedAssetSettingsDto> SettingsAsync(CancellationToken ct = default) =>
        await ToDtoAsync(await FixedAssetData.CurrentAsync(db, ct), ct);

    public async Task<FixedAssetResult<FixedAssetSettingsDto>> UpdateAsync(UpdateFixedAssetSettingsRequest r, string? by, CancellationToken ct = default)
    {
        if (!Enum.TryParse<RentalEquipmentTreatment>(r.Treatment, true, out var treatment) || !Enum.IsDefined(treatment))
            return Fail(400, InvalidCode, $"The treatment is one of: {string.Join(", ", Enum.GetNames<RentalEquipmentTreatment>())}.");
        if (!Enum.TryParse<DepreciationMethod>(r.Method, true, out var method) || !Enum.IsDefined(method))
            return Fail(400, InvalidCode, $"The method is one of: {string.Join(", ", Enum.GetNames<DepreciationMethod>())}.");
        if (!Enum.TryParse<FixedAssetOpeningValue>(r.OpeningValue, true, out var opening) || !Enum.IsDefined(opening))
            return Fail(400, InvalidCode, $"The opening value is one of: {string.Join(", ", Enum.GetNames<FixedAssetOpeningValue>())}.");
        if (r.DefaultUsefulLifeMonths is < 1 or > MaxLifeMonths)
            return Fail(400, InvalidCode, $"A useful life is between 1 and {MaxLifeMonths} months.");
        if (r.ResidualPercent is < 0 or >= 100)
            return Fail(400, InvalidCode, "The residual value is a percentage from 0 up to, not including, 100.");
        DateOnly? month = null;
        if (!string.IsNullOrWhiteSpace(r.SwitchMonth))
        {
            if (!DateOnly.TryParseExact(r.SwitchMonth.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                || parsed.Year is < 2000 or > 2100)
                return Fail(400, InvalidCode, "The switch-over month is a month, yyyy-MM.");
            month = parsed;
        }
        var lives = r.Lives ?? [];
        if (lives.Any(l => l.UsefulLifeMonths is < 1 or > MaxLifeMonths))
            return Fail(400, InvalidCode, $"A useful life is between 1 and {MaxLifeMonths} months.");
        if (lives.GroupBy(l => l.ProductId).Any(g => g.Count() > 1))
            return Fail(400, InvalidCode, "A type is named twice.");
        var lifeIds = lives.Select(l => l.ProductId).ToList();
        var hire = await FixedAssetData.HireAsync(db, lifeIds, ct);
        if (hire.Count != lifeIds.Count)
            return Fail(400, InvalidCode, "A useful life is set on hire equipment only (a product hired out).");
        // Declining balance (2026-11-10): each type's own method, coefficient and switch; an empty field follows the company.
        var methodLines = r.Methods ?? [];
        var methods = new Dictionary<Guid, (DepreciationMethod? Method, decimal? Coefficient, bool? Switch)>();
        foreach (var line in methodLines)
        {
            DepreciationMethod? own = null;
            if (!string.IsNullOrWhiteSpace(line.Method))
            {
                if (!Enum.TryParse<DepreciationMethod>(line.Method, true, out var parsed) || !Enum.IsDefined(parsed))
                    return Fail(400, InvalidCode, $"The method is one of: {string.Join(", ", Enum.GetNames<DepreciationMethod>())}.");
                own = parsed;
            }
            if (line.DecliningCoefficient is decimal c && (c < FixedAssetDeclining.MinCoefficient || c > FixedAssetDeclining.MaxCoefficient))
                return Fail(400, InvalidCode, $"A declining coefficient is between {FixedAssetDeclining.MinCoefficient} and {FixedAssetDeclining.MaxCoefficient}.");
            if (!methods.TryAdd(line.ProductId, (own, line.DecliningCoefficient is decimal k ? Math.Round(k, 3) : null, line.SwitchToStraightLine)))
                return Fail(400, InvalidCode, "A type is named twice.");
        }
        var methodIds = methods.Keys.ToList();
        if ((await FixedAssetData.HireAsync(db, methodIds, ct)).Count != methodIds.Count)
            return Fail(400, InvalidCode, "A depreciation method is set on hire equipment only (a product hired out).");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        var existing = await db.FixedAssetSettings.FirstOrDefaultAsync(ct);
        var row = existing ?? new FixedAssetSettings();
        var switchToStraightLine = r.SwitchToStraightLine ?? row.SwitchToStraightLine;
        if (row.SwitchedAtUtc is not null)
        {
            if (treatment != RentalEquipmentTreatment.FixedAssets)
                return Fail(409, LockedCode, "The switch-over is posted: the hire equipment is fixed assets and is not moved back to stock here.");
            if (r.DefaultUsefulLifeMonths != row.DefaultUsefulLifeMonths || r.ResidualPercent != row.ResidualPercent
                || opening != row.OpeningValue || month != row.SwitchMonth)
                return Fail(409, LockedCode,
                    "The switch-over is posted: the default life, the residual value, the opening value and the month are fixed, as every month depreciated and every disposal was valued with them.");
            // The method is fixed type by type, once depreciation is posted on it: every type with depreciation posted keeps
            // the method, coefficient and switch it was depreciated by, whatever the company's default or its own row says.
            var before = await FixedAssetData.PolicyAsync(db, row, ct);
            var posted = await FixedAssetData.DepreciationPostedAsync(db, row, before, ct);
            if (posted.Count > 0)
            {
                var after = before with
                {
                    DefaultMethod = method, DefaultSwitchToStraightLine = switchToStraightLine,
                    Methods = Merge(before.Methods ?? new Dictionary<Guid, FixedAssetMethodChoice>(), methods),
                };
                var moved = posted.Where(id => before.TermsOf(id) != after.TermsOf(id)).ToList();
                if (moved.Count > 0)
                {
                    var skus = await db.Products.AsNoTracking().Where(p => moved.Contains(p.Id)).Select(p => p.Sku).ToListAsync(ct);
                    return Fail(409, MethodLockedCode,
                        $"Depreciation is posted on {string.Join(", ", skus.Order(StringComparer.Ordinal))}: its method, coefficient and switch to straight line are fixed.");
                }
            }
            var held = (await db.FixedAssetMovements.AsNoTracking().Where(m => lifeIds.Contains(m.ProductId))
                .Select(m => m.ProductId).Distinct().ToListAsync(ct)).ToHashSet();
            var current = await db.FixedAssetLives.AsNoTracking().Where(l => lifeIds.Contains(l.ProductId))
                .ToDictionaryAsync(l => l.ProductId, l => (int?)l.UsefulLifeMonths, ct);
            if (lives.Any(l => held.Contains(l.ProductId) && current.GetValueOrDefault(l.ProductId) != l.UsefulLifeMonths))
                return Fail(409, LifeLockedCode, "The register holds this type: its useful life is fixed since its pool opened.");
        }

        row.Treatment = treatment;
        row.Method = method;
        row.SwitchToStraightLine = switchToStraightLine;
        row.DefaultUsefulLifeMonths = r.DefaultUsefulLifeMonths;
        row.ResidualPercent = r.ResidualPercent;
        row.OpeningValue = opening;
        row.SwitchMonth = month;
        row.TimeZoneId = regional.Get().TimeZone;
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = by;
        if (existing is null) db.FixedAssetSettings.Add(row);

        var rows = await db.FixedAssetLives.Where(l => lifeIds.Contains(l.ProductId)).ToDictionaryAsync(l => l.ProductId, ct);
        foreach (var line in lives)
        {
            rows.TryGetValue(line.ProductId, out var life);
            if (line.UsefulLifeMonths is not int months)
            {
                if (life is not null) db.FixedAssetLives.Remove(life);
                continue;
            }
            if (life is null) db.FixedAssetLives.Add(life = new FixedAssetLife { ProductId = line.ProductId });
            else if (life.UsefulLifeMonths == months) continue;
            life.UsefulLifeMonths = months;
            life.UpdatedAtUtc = row.UpdatedAtUtc.Value;
            life.UpdatedBy = by;
        }

        var methodRows = await db.FixedAssetMethods.Where(m => methodIds.Contains(m.ProductId)).ToDictionaryAsync(m => m.ProductId, ct);
        foreach (var (productId, (own, coefficient, ownSwitch)) in methods)
        {
            methodRows.TryGetValue(productId, out var choice);
            if (own is null && coefficient is null && ownSwitch is null)
            {
                if (choice is not null) db.FixedAssetMethods.Remove(choice);
                continue;
            }
            if (choice is null) db.FixedAssetMethods.Add(choice = new FixedAssetMethodChoice { ProductId = productId });
            else if (choice.Method == own && choice.DecliningCoefficient == coefficient && choice.SwitchToStraightLine == ownSwitch) continue;
            choice.Method = own;
            choice.DecliningCoefficient = coefficient;
            choice.SwitchToStraightLine = ownSwitch;
            choice.UpdatedAtUtc = row.UpdatedAtUtc.Value;
            choice.UpdatedBy = by;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return FixedAssetResult<FixedAssetSettingsDto>.Done(await SettingsAsync(ct));
    }

    /// <summary>Saves the register's slots of the account map; slots not named keep their account. Only <c>fixed_assets.</c> slots.</summary>
    public async Task<FixedAssetResult<FixedAssetSettingsDto>> UpdateMapAsync(UpdateManufacturingAccountMapRequest r, string? by, CancellationToken ct = default)
    {
        if (r.Lines is null || r.Lines.Count == 0) return Fail(400, InvalidMapCode, "Name at least one slot.");
        var unknown = r.Lines.Select(l => l.Slot).Where(s => !FixedAssetAccountSlots.All.Contains(s)).ToList();
        if (unknown.Count > 0) return Fail(400, InvalidMapCode, $"Unknown fixed-asset slot: {string.Join(", ", unknown)}.");
        if (r.Lines.GroupBy(l => l.Slot).Any(g => g.Count() > 1)) return Fail(400, InvalidMapCode, "A slot is named twice.");
        var ids = r.Lines.Where(l => l.LedgerAccountId is not null).Select(l => l.LedgerAccountId!.Value).Distinct().ToList();
        var active = ids.Count == 0 ? [] : await db.LedgerAccounts.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.IsActive).Select(a => a.Id).ToListAsync(ct);
        if (active.Count != ids.Count) return Fail(400, InvalidMapCode, "An account named is not in the chart, or is not active.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        var slots = r.Lines.Select(l => l.Slot).ToList();
        var rows = await db.ManufacturingAccountMappings.Where(m => slots.Contains(m.Slot)).ToDictionaryAsync(m => m.Slot, ct);
        // The entries the register posts from now on name these: once the switch-over is posted they are changed, never emptied.
        if (FixedAssetData.Active(await FixedAssetData.CurrentAsync(db, ct))
            && r.Lines.Any(l => l.LedgerAccountId is null && FixedAssetAccountSlots.Required.Contains(l.Slot)
                && rows.GetValueOrDefault(l.Slot)?.LedgerAccountId is not null))
            return Fail(409, SlotInUseCode, "The register posts to this slot: once the switch-over is posted it names an account, never none.");
        var now = DateTime.UtcNow;
        foreach (var line in r.Lines)
        {
            if (rows.TryGetValue(line.Slot, out var row) && row.LedgerAccountId == line.LedgerAccountId) continue;
            if (row is null) db.ManufacturingAccountMappings.Add(row = new ManufacturingAccountMapping { Slot = line.Slot });
            row.LedgerAccountId = line.LedgerAccountId;
            row.UpdatedAtUtc = now;
            row.UpdatedBy = by;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return FixedAssetResult<FixedAssetSettingsDto>.Done(await SettingsAsync(ct));
    }

    /// <summary>The types' own method rows with the request's lines applied (every field empty: none).</summary>
    private static Dictionary<Guid, FixedAssetMethodChoice> Merge(
        IReadOnlyDictionary<Guid, FixedAssetMethodChoice> saved, Dictionary<Guid, (DepreciationMethod? Method, decimal? Coefficient, bool? Switch)> lines)
    {
        var merged = saved.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (id, (own, coefficient, ownSwitch)) in lines)
        {
            if (own is null && coefficient is null && ownSwitch is null) merged.Remove(id);
            else merged[id] = new FixedAssetMethodChoice { ProductId = id, Method = own, DecliningCoefficient = coefficient, SwitchToStraightLine = ownSwitch };
        }
        return merged;
    }

    private async Task<FixedAssetSettingsDto> ToDtoAsync(FixedAssetSettings s, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.IsRentable && !p.IsDeleted)
            .Select(p => new { p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn }).ToListAsync(ct);
        var lives = await db.FixedAssetLives.AsNoTracking().ToDictionaryAsync(l => l.ProductId, l => l.UsefulLifeMonths, ct);
        var held = s.SwitchedAtUtc is null ? []
            : (await db.FixedAssetMovements.AsNoTracking().Select(m => m.ProductId).Distinct().ToListAsync(ct)).ToHashSet();
        var policy = await FixedAssetData.PolicyAsync(db, s, ct);
        var posted = await FixedAssetData.DepreciationPostedAsync(db, s, policy, ct);
        var lifeRows = products.OrderBy(p => p.Sku, StringComparer.Ordinal)
            .Select(p =>
            {
                var own = policy.Methods?.GetValueOrDefault(p.Id);
                var terms = policy.TermsOf(p.Id);
                return new FixedAssetLifeDto(p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn,
                    lives.TryGetValue(p.Id, out var life) ? life : null, held.Contains(p.Id),
                    own?.Method?.ToString(), own?.DecliningCoefficient, own?.SwitchToStraightLine,
                    FixedAssetDeclining.LegalCoefficient(policy.LifeOf(p.Id)), posted.Contains(p.Id),
                    terms.Method.ToString(), terms.Declining ? terms.Coefficient : null, terms.Declining && terms.SwitchToStraightLine);
            })
            .ToList();
        // The company's method is fixed once a type that follows it (its method, or its switch under declining) has depreciation posted.
        var methodLocked = posted.Any(id =>
        {
            var own = policy.Methods?.GetValueOrDefault(id);
            return own?.Method is null || (policy.TermsOf(id).Declining && own.SwitchToStraightLine is null);
        });
        return new FixedAssetSettingsDto(
            s.Treatment.ToString(), s.Method.ToString(), s.DefaultUsefulLifeMonths, s.ResidualPercent, s.OpeningValue.ToString(),
            s.SwitchMonth?.ToString("yyyy-MM", CultureInfo.InvariantCulture), s.SwitchedAtUtc, s.SwitchedBy, FixedAssetData.Active(s),
            s.SwitchedAtUtc is not null, lifeRows, await SlotsAsync(ct), s.UpdatedAtUtc, s.UpdatedBy,
            s.SwitchToStraightLine, methodLocked);
    }

    public async Task<IReadOnlyList<FixedAssetSlotDto>> SlotsAsync(CancellationToken ct = default)
    {
        var slots = FixedAssetAccountSlots.All;
        var rows = await db.ManufacturingAccountMappings.AsNoTracking().Where(m => slots.Contains(m.Slot)).ToDictionaryAsync(m => m.Slot, ct);
        var ids = rows.Values.Where(m => m.LedgerAccountId is not null).Select(m => m.LedgerAccountId!.Value).Distinct().ToList();
        var accounts = ids.Count == 0 ? new Dictionary<Guid, LedgerAccount>()
            : await db.LedgerAccounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        return slots.Select(slot =>
        {
            rows.TryGetValue(slot, out var m);
            LedgerAccount? a = m?.LedgerAccountId is Guid id && accounts.TryGetValue(id, out var found) ? found : null;
            return new FixedAssetSlotDto(slot, a?.Id, a?.Code, a?.NameAr, a?.NameFr, a?.NameEn, FixedAssetAccountSlots.HintFor(slot),
                FixedAssetAccountSlots.Required.Contains(slot), m?.UpdatedAtUtc, m?.UpdatedBy);
        }).ToList();
    }

    /// <summary>The company's month today, on the server's clock in the company's zone.</summary>
    public static DateOnly CompanyMonth(IRegionalSettingsProvider regional, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, StoreDocumentHistory.Zone(regional));
        return new DateOnly(local.Year, local.Month, 1);
    }

    private static FixedAssetResult<FixedAssetSettingsDto> Fail(int status, string code, string error) =>
        FixedAssetResult<FixedAssetSettingsDto>.Fail(status, code, error);
}
