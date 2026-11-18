using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Maintenance;

/// <summary>
/// MS-31, the account map's maintenance slots (Phase M6, docs/maintenance/plan.md §5.2 and §5.3): rows of the same
/// <c>ManufacturingAccountMappings</c> table as manufacturing's slots, under their <c>maintenance.</c> keys
/// (<see cref="MaintenanceAccountSlots"/>). Seeded empty: a slot with no account stays "not mapped" in the practice ledger and
/// is never guessed; the hint (today's account for the same event) is only shown. Read with the settings; changed under
/// <c>accounting.manage</c> (the route's). Manufacturing's stock slots a part may leave are shown beside them, read only here:
/// they are mapped in Manufacturing › Costing settings.
/// </summary>
public sealed class MaintenanceAccountMap(PosDbContext db)
{
    /// <summary>The maintenance slots in their order, then the shared stock slots, each with the account it names or none.</summary>
    public async Task<IReadOnlyList<MaintenanceAccountSlotDto>> MapAsync(CancellationToken ct = default)
    {
        var slots = MaintenanceAccountSlots.All.Concat(MaintenanceAccountSlots.SharedStock).ToList();
        var rows = await db.ManufacturingAccountMappings.AsNoTracking().Where(m => slots.Contains(m.Slot)).ToDictionaryAsync(m => m.Slot, ct);
        var ids = rows.Values.Where(m => m.LedgerAccountId is not null).Select(m => m.LedgerAccountId!.Value).Distinct().ToList();
        var accounts = ids.Count == 0 ? new Dictionary<Guid, LedgerAccount>()
            : await db.LedgerAccounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        return slots.Select(slot =>
        {
            rows.TryGetValue(slot, out var m);
            LedgerAccount? a = m?.LedgerAccountId is Guid id && accounts.TryGetValue(id, out var found) ? found : null;
            return new MaintenanceAccountSlotDto(slot, a?.Id, a?.Code, a?.NameAr, a?.NameFr, a?.NameEn,
                MaintenanceAccountSlots.HintFor(slot), MaintenanceAccountSlots.SharedStock.Contains(slot), m?.UpdatedAtUtc, m?.UpdatedBy);
        }).ToList();
    }

    /// <returns>The map as saved, or the reason it was refused (400). Slots not named keep their account; only maintenance's slots are taken.</returns>
    public async Task<(IReadOnlyList<MaintenanceAccountSlotDto>? Saved, string? Error)> UpdateAsync(
        UpdateManufacturingAccountMapRequest r, string? by, CancellationToken ct = default)
    {
        if (r.Lines is null || r.Lines.Count == 0) return (null, "Name at least one slot.");
        var unknown = r.Lines.Select(l => l.Slot).Where(s => !MaintenanceAccountSlots.All.Contains(s)).ToList();
        if (unknown.Count > 0)
            return (null, $"Unknown maintenance slot: {string.Join(", ", unknown)}. The stock slots are mapped in Manufacturing › Costing settings.");
        if (r.Lines.GroupBy(l => l.Slot).Any(g => g.Count() > 1)) return (null, "A slot is named twice.");
        var ids = r.Lines.Where(l => l.LedgerAccountId is not null).Select(l => l.LedgerAccountId!.Value).Distinct().ToList();
        var active = ids.Count == 0 ? [] : await db.LedgerAccounts.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.IsActive).Select(a => a.Id).ToListAsync(ct);
        if (active.Count != ids.Count) return (null, "An account named is not in the chart, or is not active.");
        // MS-34: the service income slot names an income account of class 7, never a cost or stock account.
        if (r.Lines.FirstOrDefault(l => l.Slot == MaintenanceAccountSlots.ServiceIncome)?.LedgerAccountId is Guid incomeId
            && !MaintenanceAccountSlots.IsIncomeAccount(await db.LedgerAccounts.AsNoTracking().FirstAsync(a => a.Id == incomeId, ct)))
            return (null, "Repair service income (maintenance.service_income) is an active income account of class 7.");
        // MS-34: while customer repair income is on its own account, that account stays named (the settings were saved on it).
        if (r.Lines.Any(l => l.LedgerAccountId is null && l.Slot == MaintenanceAccountSlots.ServiceIncome)
            && (await MaintenanceSettingsStore.CurrentAsync(db, ct)).CustomerIncome != MaintenanceIncomeAccount.SameAsRecharges)
            return (null, "Customer repair income (MS-34) is booked on this account: go back to the account recharges use before clearing it.");
        // Phase M7c: while MS-37 sets a provision aside, its two accounts stay named (the settings were saved on them).
        if (r.Lines.Any(l => l.LedgerAccountId is null && MaintenanceAccountSlots.Provision.Contains(l.Slot))
            && (await MaintenanceSettingsStore.CurrentAsync(db, ct)).Rework == MaintenanceRework.Provision)
            return (null, "The provision for rework under guarantee (MS-37) posts to these accounts: choose another treatment of rework before clearing them.");

        // Phase M7d: while MS-30 capitalises repairs, own equipment repairs and own work capitalised stay named.
        if (r.Lines.Any(l => l.LedgerAccountId is null && (l.Slot == MaintenanceAccountSlots.OwnParts || l.Slot == MaintenanceAccountSlots.Capitalised))
            && (await MaintenanceSettingsStore.CurrentAsync(db, ct)).OwnRepairs == MaintenanceOwnRepairs.CapitalisedAboveAmount)
            return (null, "Capitalised repairs (MS-30) post to these accounts: keep repairs an expense before clearing them.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        var slots = r.Lines.Select(l => l.Slot).ToList();
        var rows = await db.ManufacturingAccountMappings.Where(m => slots.Contains(m.Slot)).ToDictionaryAsync(m => m.Slot, ct);
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
        return (await MapAsync(ct), null);
    }

    /// <summary>
    /// Each slot to the account it names, for the practice ledger and the posting at issue: the map's rows, and plain
    /// merchandise's stock as the chart's stock account. A slot absent from the result is not mapped.
    /// </summary>
    public static async Task<Dictionary<string, LedgerAccount>> AccountsAsync(PosDbContext db, CancellationToken ct = default)
    {
        var map = await db.ManufacturingAccountMappings.AsNoTracking().Where(x => x.LedgerAccountId != null)
            .ToDictionaryAsync(x => x.Slot, x => x.LedgerAccountId!.Value, ct);
        var ids = map.Values.Distinct().ToList();
        var accounts = await db.LedgerAccounts.AsNoTracking()
            .Where(a => ids.Contains(a.Id) || a.Code == LedgerAccounts.Inventory).ToListAsync(ct);
        var byId = accounts.ToDictionary(a => a.Id);
        var result = map.Where(kv => byId.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => byId[kv.Value]);
        if (accounts.FirstOrDefault(a => a.Code == LedgerAccounts.Inventory) is { } stock)
            result[MaintenanceAccountSlots.MerchandiseStock] = stock;
        return result;
    }

    /// <summary>
    /// MS-34: the account the service income slot names, while it is still an active income account of class 7; null
    /// otherwise (unmapped, or the account since made inactive) — never guessed.
    /// </summary>
    public static async Task<LedgerAccount?> ServiceIncomeAsync(PosDbContext db, CancellationToken ct = default)
    {
        var id = await db.ManufacturingAccountMappings.AsNoTracking()
            .Where(m => m.Slot == MaintenanceAccountSlots.ServiceIncome).Select(m => m.LedgerAccountId).FirstOrDefaultAsync(ct);
        if (id is not Guid accountId) return null;
        var account = await db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        return account is not null && MaintenanceAccountSlots.IsIncomeAccount(account) ? account : null;
    }
}
