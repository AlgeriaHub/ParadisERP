using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Maintenance;

/// <summary>
/// MS-23 repair rates (Phase M5a, REN-11): a product's usual recharge for repairing one unit, kept under
/// <c>maintenance.pricing</c>. <b>Only a suggestion on the return-note screen</b>, offered while the module is on and MS-23
/// says "suggested from a rate": the clerk still types the recharge, and the return-note writer, the rental engine, its rules
/// and its postings never read this table (a test runs the same return with and without rates and finds the same note,
/// charges and ledger). Company-wide, like the settings.
/// </summary>
public sealed class MaintenanceRepairRates(PosDbContext db, IRegionalSettingsProvider regional)
{
    public const decimal MaxRate = 1_000_000_000m;

    public async Task<MaintenanceRepairRatesDto> ListAsync(CancellationToken ct = default)
    {
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        var rates = await db.MaintenanceRepairRates.AsNoTracking().Include(r => r.Product)
            .Select(r => new MaintenanceRepairRateDto(r.ProductId, r.Product!.Sku, r.Product.NameAr, r.Product.NameFr, r.Product.NameEn,
                r.RatePerUnit, r.UpdatedBy, r.UpdatedAtUtc))
            .ToListAsync(ct);
        var products = await db.Products.AsNoTracking().Where(p => p.IsRentable && !p.IsDeleted).OrderBy(p => p.Sku).Take(2000)
            .Select(p => new MaintenanceProductOptionDto(p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn)).ToListAsync(ct);
        return new MaintenanceRepairRatesDto(settings.ReturnRecharge.ToString(),
            rates.OrderBy(r => r.Sku, StringComparer.Ordinal).ToList(), products);
    }

    public async Task<MaintenanceResult<MaintenanceRepairRatesDto>> SetAsync(
        Guid productId, SetMaintenanceRepairRateRequest r, string? by, CancellationToken ct = default)
    {
        if (r.RatePerUnit <= 0 || r.RatePerUnit > MaxRate)
            return MaintenanceResult<MaintenanceRepairRatesDto>.Fail(400, "invalid_rate", "A repair rate is above zero; remove it to stop suggesting one.");
        await using (var scope = await db.BeginWriteScopeAsync(ct))
        {
            await db.LockVersionsAsync(ct);
            if (!await db.Products.AnyAsync(p => p.Id == productId && !p.IsDeleted, ct))
                return MaintenanceResult<MaintenanceRepairRatesDto>.Fail(404, "not_found", "Product not found.");
            var row = await db.MaintenanceRepairRates.FirstOrDefaultAsync(x => x.ProductId == productId, ct);
            if (row is null) db.MaintenanceRepairRates.Add(row = new MaintenanceRepairRate { ProductId = productId });
            row.RatePerUnit = regional.Get().RoundMoney(r.RatePerUnit);
            (row.UpdatedBy, row.UpdatedAtUtc) = (by, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
        }
        return MaintenanceResult<MaintenanceRepairRatesDto>.Done(await ListAsync(ct));
    }

    public async Task<MaintenanceResult<MaintenanceRepairRatesDto>> RemoveAsync(Guid productId, CancellationToken ct = default)
    {
        await using (var scope = await db.BeginWriteScopeAsync(ct))
        {
            await db.LockVersionsAsync(ct);
            var row = await db.MaintenanceRepairRates.FirstOrDefaultAsync(x => x.ProductId == productId, ct);
            if (row is null) return MaintenanceResult<MaintenanceRepairRatesDto>.Fail(404, "not_found", "This product has no repair rate.");
            db.MaintenanceRepairRates.Remove(row);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
        }
        return MaintenanceResult<MaintenanceRepairRatesDto>.Done(await ListAsync(ct));
    }

    /// <summary>
    /// What the return-note screen reads: whether to suggest (the module on and MS-23 "suggested from a rate") and, if so,
    /// every product's rate. Answers <c>Enabled = false</c> and no rates otherwise — never an error, so the rental screen
    /// behaves exactly as before while the setting is off.
    /// </summary>
    public async Task<MaintenanceRepairSuggestionsDto> SuggestionsAsync(CancellationToken ct = default)
    {
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        if (settings is not { Module: MaintenanceSwitch.On, ReturnRecharge: MaintenanceRechargeEntry.SuggestedFromRate })
            return new MaintenanceRepairSuggestionsDto(false, []);
        var rates = await db.MaintenanceRepairRates.AsNoTracking()
            .Select(r => new MaintenanceRepairSuggestionDto(r.ProductId, r.RatePerUnit)).ToListAsync(ct);
        return new MaintenanceRepairSuggestionsDto(true, rates);
    }
}
