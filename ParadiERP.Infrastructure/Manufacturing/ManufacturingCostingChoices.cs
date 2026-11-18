using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Manufacturing;

/// <summary>
/// The manufacturing costing choices and the account map (Phase C1): what the accountant has not answered, set
/// by the admin meanwhile (owner, 2026-11-08). Writes only its own two tables; the costing previews read the
/// choices through <see cref="ProductionCosts"/>, and nothing here posts.
/// </summary>
public sealed class ManufacturingCostingChoices(PosDbContext db, ManufacturingOptions? server = null)
{
    public const decimal MaxOverheadPerHour = 1_000_000m;
    public const decimal MaxBurdenPercent = 100m;
    public const decimal MaxVatPercent = 100m;
    public const decimal MaxScrapPercent = 100m;

    public async Task<ManufacturingCostingSettingsDto> SettingsAsync(CancellationToken ct = default) =>
        ToDto(await db.ManufacturingCostingSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new ManufacturingCostingSettings());

    /// <summary>Whether the panel lets manufacturing move stock (C-12); the server's configuration can only add to it.</summary>
    public static bool StockOperations(PosDbContext db) =>
        db.ManufacturingCostingSettings.AsNoTracking().Any(s => s.StockOperations);

    /// <returns>The saved choices, or the reason they were refused (400).</returns>
    public async Task<(ManufacturingCostingSettingsDto? Saved, string? Error)> UpdateSettingsAsync(
        UpdateManufacturingCostingSettingsRequest r, string? by, CancellationToken ct = default)
    {
        string? error = null;
        T Parse<T>(string? value, string what) where T : struct, Enum
        {
            if (Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)) return parsed;
            error ??= $"{what} is one of: {string.Join(", ", Enum.GetNames<T>())}.";
            return default;
        }
        var model = Parse<ManufacturingAccountingModel>(r.Model, "The accounting model");
        var rule = Parse<ManufacturingReleaseRule>(r.ReleaseRule, "The release rule");
        var burdenUse = Parse<EmployerBurdenUse>(r.EmployerBurdenUse, "The employer burden's use");
        var monthEnd = Parse<MonthEndWorkInProgress>(r.MonthEndWorkInProgress, "The month-end work in progress");
        var late = Parse<LateCostTreatment>(r.LateCosts, "The late costs");
        var variances = Parse<VarianceTreatment>(r.Variances, "The variances");
        var expenseBasis = Parse<ExpenseBasis>(r.ExpenseBasis, "The expense basis");
        var scrap = Parse<ScrapRecoveryBasis>(r.ScrapRecovery, "The scrap recovery");
        var labourAbsorption = Parse<ManufacturingLabourAbsorption>(r.LabourAbsorption, "The labour absorption");
        if (error is not null) return (null, error);
        if (r.ExpenseVatPercent is < 0 or > MaxVatPercent)
            return (null, $"The recoverable VAT rate is between 0 and {MaxVatPercent:0} %.");
        if (r.ScrapRecoveryPercent is < 0 or > MaxScrapPercent)
            return (null, $"The scrap recovery is between 0 and {MaxScrapPercent:0} %.");
        if (scrap != ScrapRecoveryBasis.None && r.ScrapRecoveryPercent == 0)
            return (null, "Give the scrap recovery's percentage, or choose no recovery.");
        DateOnly? postingFrom = null;
        if (!string.IsNullOrWhiteSpace(r.PostingFromMonth))
        {
            if (!DateOnly.TryParseExact(r.PostingFromMonth + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed) || parsed.Year is < 2000 or > 2100)
                return (null, "The first month to post is a month, yyyy-MM.");
            postingFrom = parsed;
        }
        if (r.OverheadPerLabourHour is < 0 or > MaxOverheadPerHour)
            return (null, $"The overhead per labour hour is between 0 and {MaxOverheadPerHour:0}.");
        if (r.EmployerBurdenPercent is < 0 or > MaxBurdenPercent)
            return (null, $"The employer burden is between 0 and {MaxBurdenPercent:0} %.");
        if (burdenUse != EmployerBurdenUse.None && r.EmployerBurdenPercent == 0)
            return (null, "Give the employer burden's percentage, or choose not to use one.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        var row = await db.ManufacturingCostingSettings.FirstOrDefaultAsync(ct);
        if (row is null) db.ManufacturingCostingSettings.Add(row = new ManufacturingCostingSettings());
        var before = Choices(row);
        var wasApproved = row.AccountantApproved;
        row.Model = model;
        row.OverheadPerLabourHour = r.OverheadPerLabourHour;
        row.ReleaseRule = rule;
        row.EmployerBurdenUse = burdenUse;
        row.EmployerBurdenPercent = burdenUse == EmployerBurdenUse.None ? 0m : r.EmployerBurdenPercent;
        row.MonthEndWorkInProgress = monthEnd;
        row.LateCosts = late;
        row.Variances = variances;
        row.ExpenseBasis = expenseBasis;
        row.ExpenseVatPercent = expenseBasis == ExpenseBasis.AsRecorded ? 0m : r.ExpenseVatPercent;
        row.ScrapRecovery = scrap;
        row.ScrapRecoveryPercent = scrap == ScrapRecoveryBasis.None ? 0m : r.ScrapRecoveryPercent;
        row.LabourAbsorption = labourAbsorption;
        row.StockOperations = r.StockOperations;
        row.PostingFromMonth = postingFrom;
        // The accountant's confirmation covers the choices it was given on: a change confirmed again is confirmed by
        // whoever saved it, and a change not confirmed leaves the costs provisional.
        var changed = before != Choices(row);
        row.AccountantApproved = r.AccountantApproved;
        if (!r.AccountantApproved) (row.ApprovedBy, row.ApprovedAtUtc) = (null, null);
        else if (!wasApproved || changed) (row.ApprovedBy, row.ApprovedAtUtc) = (by, DateTime.UtcNow);
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (ToDto(row), null);
    }

    /// <summary>Every slot, in its fixed order, with the account it names or none.</summary>
    public async Task<IReadOnlyList<ManufacturingAccountSlotDto>> AccountMapAsync(CancellationToken ct = default)
    {
        var rows = await db.ManufacturingAccountMappings.AsNoTracking().ToDictionaryAsync(m => m.Slot, ct);
        var ids = rows.Values.Where(m => m.LedgerAccountId is not null).Select(m => m.LedgerAccountId!.Value).Distinct().ToList();
        var accounts = ids.Count == 0 ? new Dictionary<Guid, LedgerAccount>()
            : await db.LedgerAccounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        return ManufacturingAccountSlots.All.Select(slot =>
        {
            rows.TryGetValue(slot, out var m);
            LedgerAccount? a = m?.LedgerAccountId is Guid id && accounts.TryGetValue(id, out var found) ? found : null;
            return new ManufacturingAccountSlotDto(slot, a?.Id, a?.Code, a?.NameAr, a?.NameFr, a?.NameEn, m?.UpdatedAtUtc, m?.UpdatedBy);
        }).ToList();
    }

    /// <returns>The map as saved, or the reason it was refused (400). Slots not named keep their account.</returns>
    public async Task<(IReadOnlyList<ManufacturingAccountSlotDto>? Saved, string? Error)> UpdateAccountMapAsync(
        UpdateManufacturingAccountMapRequest r, string? by, CancellationToken ct = default)
    {
        if (r.Lines is null || r.Lines.Count == 0) return (null, "Name at least one slot.");
        var unknown = r.Lines.Select(l => l.Slot).Where(s => !ManufacturingAccountSlots.All.Contains(s)).ToList();
        if (unknown.Count > 0) return (null, $"Unknown slot: {string.Join(", ", unknown)}.");
        if (r.Lines.GroupBy(l => l.Slot).Any(g => g.Count() > 1)) return (null, "A slot is named twice.");
        var ids = r.Lines.Where(l => l.LedgerAccountId is not null).Select(l => l.LedgerAccountId!.Value).Distinct().ToList();
        var active = ids.Count == 0 ? [] : await db.LedgerAccounts.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.IsActive).Select(a => a.Id).ToListAsync(ct);
        if (active.Count != ids.Count) return (null, "An account named is not in the chart, or is not active.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        var rows = await db.ManufacturingAccountMappings.ToDictionaryAsync(m => m.Slot, ct);
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
        return (await AccountMapAsync(ct), null);
    }

    /// <summary>The choices the accountant's confirmation covers (not the stock-operations switch, which posts nothing).</summary>
    private static string Choices(ManufacturingCostingSettings s) => string.Join('|',
        s.Model, s.OverheadPerLabourHour, s.ReleaseRule, s.EmployerBurdenUse, s.EmployerBurdenPercent, s.MonthEndWorkInProgress,
        s.LateCosts, s.Variances, s.ExpenseBasis, s.ExpenseVatPercent, s.ScrapRecovery, s.ScrapRecoveryPercent, s.LabourAbsorption);

    private ManufacturingCostingSettingsDto ToDto(ManufacturingCostingSettings s) => new(
        s.Model.ToString(), s.OverheadPerLabourHour, s.ReleaseRule.ToString(), s.EmployerBurdenUse.ToString(), s.EmployerBurdenPercent,
        s.MonthEndWorkInProgress.ToString(), s.LateCosts.ToString(), s.Variances.ToString(), s.UpdatedAtUtc, s.UpdatedBy,
        s.ExpenseBasis.ToString(), s.ExpenseVatPercent, s.ScrapRecovery.ToString(), s.ScrapRecoveryPercent,
        s.StockOperations, server?.ByConfiguration == true, s.AccountantApproved, s.ApprovedBy, s.ApprovedAtUtc,
        s.PostingFromMonth?.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        s.LabourAbsorption.ToString());
}
