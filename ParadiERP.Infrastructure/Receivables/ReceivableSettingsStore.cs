using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Receivables;

/// <summary>What a save answered: the saved settings, or the status, code and reason it was refused.</summary>
public sealed record ReceivableSettingsResult(ReceivableSettingsDto? Saved, int Status = 200, string? Code = null, string? Error = null)
{
    public static ReceivableSettingsResult Refused(int status, string code, string error) => new(null, status, code, error);
}

/// <summary>
/// The debtor book's accounting choices (M7b; SAL-12, D45, owner 2026-11-09: every accounting decision is a setting the
/// accountant sets, the default today's behaviour, never waiting for a signature). One row, company-wide, changed under
/// <c>accounting.manage</c> (the route's). Defaults: an advance is an unallocated receipt on 411 and carries no VAT —
/// exactly what every receipt has always posted.
/// <list type="bullet">
/// <item>419 is refused while account 419 is missing from the chart or inactive (409 <c>accounts_not_mapped</c>).</item>
/// <item>VAT when the advance is received is refused unless advances are held on 419 (400): an advance on 411 is an
/// ordinary credit and has no place to keep its VAT apart.</item>
/// <item>Back to 411 is refused while maintenance advances (MS-35) are taken (409 <c>maintenance_advances_taken</c>).
/// Advances already on 419 stay there and are still applied: each knows where it was posted.</item>
/// <item>SAL-13: kept advances "as other income" is refused while no active income account of class 7 is chosen
/// (409 <c>accounts_not_mapped</c>). Not allowed by default.</item>
/// </list>
/// </summary>
public sealed class ReceivableSettingsStore(PosDbContext db)
{
    public const string InvalidCode = "invalid_settings";
    public const string AccountsNotMappedCode = "accounts_not_mapped";
    public const string MaintenanceAdvancesCode = "maintenance_advances_taken";

    /// <summary>The choices in force: the saved row, or the defaults before the first save. Read only, never tracked.</summary>
    public static async Task<ReceivableSettings> CurrentAsync(PosDbContext db, CancellationToken ct = default) =>
        await db.ReceivableSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new ReceivableSettings();

    /// <summary>Whether advances are held on 419 now: what lifts MS-35's "not yet" and lets a receipt be an advance.</summary>
    public static async Task<bool> AdvancesOn419Async(PosDbContext db, CancellationToken ct = default) =>
        (await CurrentAsync(db, ct)).AdvancesHeldOn == CustomerAdvanceAccount.AdvancesAccount;

    public async Task<ReceivableSettingsDto> SettingsAsync(CancellationToken ct = default) =>
        await ToDtoAsync(await CurrentAsync(db, ct), ct);

    public async Task<ReceivableSettingsResult> UpdateAsync(UpdateReceivableSettingsRequest r, string? by, CancellationToken ct = default)
    {
        if (!Enum.TryParse<CustomerAdvanceAccount>(r.AdvancesHeldOn, true, out var heldOn) || !Enum.IsDefined(heldOn))
            return ReceivableSettingsResult.Refused(400, InvalidCode,
                $"Where advances are held is one of: {string.Join(", ", Enum.GetNames<CustomerAdvanceAccount>())}.");
        if (!Enum.TryParse<AdvanceVatTiming>(r.AdvanceVat, true, out var vat) || !Enum.IsDefined(vat))
            return ReceivableSettingsResult.Refused(400, InvalidCode,
                $"When an advance's VAT is due is one of: {string.Join(", ", Enum.GetNames<AdvanceVatTiming>())}.");
        if (vat == AdvanceVatTiming.WhenReceived && heldOn != CustomerAdvanceAccount.AdvancesAccount)
            return ReceivableSettingsResult.Refused(400, InvalidCode,
                "VAT due when an advance is received needs advances held on 419 until the invoice.");
        KeptAdvancePolicy? keptAsked = null;
        if (r.KeptAdvances is not null)
        {
            if (!Enum.TryParse<KeptAdvancePolicy>(r.KeptAdvances, true, out var parsed) || !Enum.IsDefined(parsed))
                return ReceivableSettingsResult.Refused(400, InvalidCode,
                    $"Kept advances is one of: {string.Join(", ", Enum.GetNames<KeptAdvancePolicy>())}.");
            keptAsked = parsed;
        }
        var accountAsked = r.KeptIncomeAccount?.Trim();
        if (accountAsked is { Length: > 16 })
            return ReceivableSettingsResult.Refused(400, InvalidCode, "An account code is at most 16 characters.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        var existing = await db.ReceivableSettings.FirstOrDefaultAsync(ct);
        var row = existing ?? new ReceivableSettings();

        // SAL-13: left out, the two keep what is saved; an empty account clears it.
        var kept = keptAsked ?? row.KeptAdvances;
        var keptAccount = r.KeptIncomeAccount is null ? row.KeptAdvanceIncomeAccount
            : string.IsNullOrEmpty(accountAsked) ? null : accountAsked;
        if (kept == KeptAdvancePolicy.OtherIncome && !await IncomeAccountReadyAsync(db, keptAccount, ct))
            return ReceivableSettingsResult.Refused(409, AccountsNotMappedCode,
                "Keeping an advance as other income needs an active income account (class 7) chosen to credit it to.");

        if (heldOn == CustomerAdvanceAccount.AdvancesAccount && row.AdvancesHeldOn != heldOn && !await AccountReadyAsync(ct))
            return ReceivableSettingsResult.Refused(409, AccountsNotMappedCode,
                $"Account {LedgerAccounts.CustomerDeposits} (customer advances) is not an active account of the chart: add or reactivate it first.");
        if (heldOn == CustomerAdvanceAccount.CustomerAccount && row.AdvancesHeldOn != heldOn
            && (await db.MaintenanceSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.Advances == MaintenanceAdvances.Taken)
            return ReceivableSettingsResult.Refused(409, MaintenanceAdvancesCode,
                "Advances on customer maintenance jobs (MS-35) are taken on 419: set them back to Not taken in Maintenance › Settings first.");

        if (existing is null) db.ReceivableSettings.Add(row);
        row.AdvancesHeldOn = heldOn;
        row.AdvanceVat = vat;
        row.KeptAdvances = kept;
        row.KeptAdvanceIncomeAccount = keptAccount;
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ReceivableSettingsResult(await ToDtoAsync(row, ct));
    }

    private Task<bool> AccountReadyAsync(CancellationToken ct) =>
        db.LedgerAccounts.AsNoTracking().AnyAsync(a => a.Code == LedgerAccounts.CustomerDeposits && a.IsActive, ct);

    /// <summary>The chart's active income accounts of class 7: what a kept advance may be credited to (SAL-13).</summary>
    private static IQueryable<LedgerAccount> IncomeAccounts(PosDbContext db) =>
        db.LedgerAccounts.AsNoTracking()
            .Where(a => a.IsActive && a.Type == LedgerAccountType.Income && a.Code.StartsWith("7"));

    /// <summary>SAL-13: the code names an active income account of class 7.</summary>
    public static async Task<bool> IncomeAccountReadyAsync(PosDbContext db, string? code, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(code) && await IncomeAccounts(db).AnyAsync(a => a.Code == code, ct);

    private async Task<ReceivableSettingsDto> ToDtoAsync(ReceivableSettings s, CancellationToken ct) => new(
        s.AdvancesHeldOn.ToString(), s.AdvanceVat.ToString(), await AccountReadyAsync(ct),
        (await CustomerAdvances.OpenAsync(db, customerId: null, ct)).Values.Sum(),
        s.UpdatedAtUtc, s.UpdatedBy,
        s.KeptAdvances.ToString(), s.KeptAdvanceIncomeAccount,
        (await IncomeAccounts(db).OrderBy(a => a.Code).ToListAsync(ct))
            .Select(a => new IncomeAccountChoiceDto(a.Code, a.NameAr, a.NameFr, a.NameEn)).ToList());
}

/// <summary>What is still held on 419 for each advance (M7b): its amount less every application and release so far.</summary>
public static class CustomerAdvances
{
    /// <summary>
    /// The confirmed advances still holding something on 419, by receipt id — one customer's, or everyone's. A pending
    /// cheque taken as an advance is not here: it posted nothing yet. Summed in memory: SQLite stores decimal as TEXT.
    /// </summary>
    public static async Task<Dictionary<Guid, decimal>> OpenAsync(PosDbContext db, Guid? customerId, CancellationToken ct = default)
    {
        var query = db.ReceiptVouchers.AsNoTracking()
            .Where(v => v.Kind == ReceiptKind.Advance && v.Status == PaymentStatus.Confirmed);
        if (customerId is Guid one) query = query.Where(v => v.CustomerId == one);
        var advances = await query.Select(v => new { v.Id, v.Amount }).ToListAsync(ct);
        if (advances.Count == 0) return [];
        var ids = advances.Select(a => a.Id).ToList();
        var applied = (await db.CustomerAdvanceApplications.AsNoTracking()
                .Where(a => ids.Contains(a.ReceiptVoucherId))
                .Select(a => new { a.ReceiptVoucherId, a.Amount })
                .ToListAsync(ct))
            .GroupBy(a => a.ReceiptVoucherId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        return advances
            .Select(a => (a.Id, Open: a.Amount - applied.GetValueOrDefault(a.Id)))
            .Where(a => a.Open > 0)
            .ToDictionary(a => a.Id, a => a.Open);
    }
}
