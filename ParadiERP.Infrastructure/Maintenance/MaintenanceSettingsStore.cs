using System.Globalization;
using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.FixedAssets;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Maintenance;

/// <summary>What a save answered: the saved settings, or the status, code and reason it was refused.</summary>
public sealed record MaintenanceSettingsResult(MaintenanceSettingsDto? Saved, int Status = 200, string? Code = null, string? Error = null)
{
    public static MaintenanceSettingsResult Refused(int status, string code, string error) => new(null, status, code, error);
}

/// <summary>
/// Maintenance › Settings (Phase M0, docs/maintenance/plan.md §4): every maintenance decision as a setting, one row,
/// each default the suggested answer and today's behaviour (the module off). Company-wide, so no branch rule. Writes
/// only its own table; nothing reads the choices yet, and nothing here posts.
/// <para>
/// Who changes what (plan §6.1): MS-01 to MS-27 need <c>maintenance.settings</c>, MS-30 to MS-40 need
/// <c>accounting.manage</c>. The page sends the whole panel, so a request is compared with the saved row and only a
/// field that would change asks for its key (403, naming which). The options the plan keeps for a later phase are
/// stored as enum values but refused for now (400 <c>maintenance_option_not_yet</c>). Booking only a workshop list (MS-04)
/// is taken since the list exists (Maintenance › Workshop), even while a branch's list is empty: the page warns which
/// branches would then book nobody, and booking there refuses everyone not on it. Advances (MS-35, M7b) are taken only while the receivables settings hold
/// customer advances on 419. Capitalising own repairs (MS-30) and per-piece history (MS-38) follow their own rules (M7d).
/// Posting parts at issue (MS-32, refused until M6) is taken once the account map names own equipment repairs' account.
/// The guarantee provision (MS-37, Phase M7c) is saved only while the provision expense and liability slots both name
/// an account, with a rate above 0 and a guarantee (MS-27). Customer
/// repair income on its own account (MS-34's two other options) is saved only while the service income slot names an
/// active income account of class 7 (409 <c>accounts_not_mapped</c> otherwise). Phase M7d took MS-30
/// "capitalised above an amount" (only while the REN-12 register is active, MS-39 names a first month and its slots are mapped)
/// and MS-38 "per piece above a value", each with its figure.
/// </para>
/// </summary>
public sealed class MaintenanceSettingsStore(PosDbContext db)
{
    public const decimal MaxAmount = 1_000_000m;
    public const decimal MaxPercent = 100m;
    public const decimal MaxMarkupPercent = 1_000m;
    public const int MaxGuaranteeMonths = 60;
    /// <summary>MS-30's amount and MS-38's value (Phase M7d): a repair or a piece of equipment can be worth more than MaxAmount.</summary>
    public const decimal MaxThreshold = 100_000_000m;

    public const string InvalidCode = "invalid_settings";
    public const string NotYetCode = "maintenance_option_not_yet";
    public const string OperationalForbiddenCode = "maintenance_settings_required";
    public const string AccountingForbiddenCode = "accounting_manage_required";
    public const string AccountsNotMappedCode = "accounts_not_mapped";
    public const string RegisterNotActiveCode = "register_not_active";
    public const string PostingOffCode = "posting_off";

    public async Task<MaintenanceSettingsDto> SettingsAsync(CancellationToken ct = default) =>
        ToDto(await CurrentAsync(db, ct), FixedAssetData.Active(await FixedAssetData.CurrentAsync(db, ct)));

    /// <summary>
    /// The choices in force, for the code that follows them (Phase M1: the module switch, MS-02, MS-03, MS-05): the saved
    /// row, or a fresh row's defaults before the first save. Read only, never tracked.
    /// </summary>
    public static async Task<MaintenanceSettings> CurrentAsync(PosDbContext db, CancellationToken ct = default) =>
        await db.MaintenanceSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new MaintenanceSettings();

    /// <param name="has">Whether the caller holds a permission, as the server decided it.</param>
    public async Task<MaintenanceSettingsResult> UpdateAsync(
        UpdateMaintenanceSettingsRequest r, Func<string, bool> has, string? by, CancellationToken ct = default)
    {
        var (wanted, error) = Parse(r);
        if (wanted is null) return MaintenanceSettingsResult.Refused(400, InvalidCode, error!);

        await using var tx = await db.BeginWriteScopeAsync(ct);
        // The first save's row is added only once nothing refuses it, so a refusal leaves nothing tracked behind.
        var existing = await db.MaintenanceSettings.FirstOrDefaultAsync(ct);
        var row = existing ?? new MaintenanceSettings();

        // Only what would change asks for its key: the page sends both halves whoever saves.
        if (Operational(row) != Operational(wanted) && !has(PermissionCatalog.MaintenanceSettings))
            return MaintenanceSettingsResult.Refused(403, OperationalForbiddenCode,
                "Changing the module, job, cost, pricing or guarantee settings (MS-01 to MS-27) needs maintenance.settings.");
        if (Accounting(row) != Accounting(wanted) && !has(PermissionCatalog.AccountingManage))
            return MaintenanceSettingsResult.Refused(403, AccountingForbiddenCode,
                "Changing the accounting settings or the accountant's confirmation (MS-30 to MS-40) needs accounting.manage.");

        if (NotYet(wanted) is string notYet) return MaintenanceSettingsResult.Refused(400, NotYetCode, notYet);
        // MS-35 (M7b): advances on customer jobs are taken on 419, so "Taken" is chosen only while the receivables settings
        // hold advances there. A row already saved as Taken stays saveable (the receivables setting cannot leave 419 under it).
        if (wanted.Advances == MaintenanceAdvances.Taken && row.Advances != MaintenanceAdvances.Taken
            && !await Receivables.ReceivableSettingsStore.AdvancesOn419Async(db, ct))
            return MaintenanceSettingsResult.Refused(400, NotYetCode,
                "Advances on customer jobs (MS-35) are held on 419: choose \"419 until the invoice\" for customer advances in Finance › Settings first.");
        // MS-32 "at issue" (M6) posts Dr maintenance.own_parts at once: chosen only once that slot names an account, never guessed.
        if (wanted.PartsPosting == MaintenancePartsPosting.AtIssue && row.PartsPosting != MaintenancePartsPosting.AtIssue
            && !(await MaintenanceAccountMap.AccountsAsync(db, ct)).ContainsKey(MaintenanceAccountSlots.OwnParts))
            return MaintenanceSettingsResult.Refused(409, AccountsNotMappedCode,
                "Posting parts at each issue (MS-32) needs the account of own equipment repairs (maintenance.own_parts) in the account map first.");

        // MS-37 "provision" (M7c) books Dr provision expense / Cr provision liability: saved only while both name an account.
        if (wanted.Rework == MaintenanceRework.Provision)
        {
            var mapped = await MaintenanceAccountMap.AccountsAsync(db, ct);
            if (MaintenanceAccountSlots.Provision.Any(slot => !mapped.ContainsKey(slot)))
                return MaintenanceSettingsResult.Refused(409, AccountsNotMappedCode,
                    "A provision for rework under guarantee (MS-37) needs the provision expense and provision liability accounts in the account map first.");
        }

        // MS-34 one service account, or parts and labour apart: the service is credited to the service income slot, saved
        // only while it names an active income account of class 7, never guessed.
        if (wanted.CustomerIncome != MaintenanceIncomeAccount.SameAsRecharges && await MaintenanceAccountMap.ServiceIncomeAsync(db, ct) is null)
            return MaintenanceSettingsResult.Refused(409, AccountsNotMappedCode,
                "Customer repair income on its own account (MS-34) needs the repair service income account (maintenance.service_income, an income account of class 7) in the account map first.");

        // MS-30 "capitalised above an amount" (M7d) adds a repair's cost to the REN-12 register and posts it with the practice
        // ledger: chosen only while the register holds the hire equipment, the ledger posts (MS-39), and its slots name accounts.
        if (wanted.OwnRepairs == MaintenanceOwnRepairs.CapitalisedAboveAmount)
        {
            if (!FixedAssetData.Active(await FixedAssetData.CurrentAsync(db, ct)))
                return MaintenanceSettingsResult.Refused(409, RegisterNotActiveCode,
                    "Capitalising repairs on own equipment (MS-30) adds their cost to the equipment's register: switch hire equipment over to fixed assets first (Reports › Fixed assets).");
            if (wanted.PostingFromMonth is null)
                return MaintenanceSettingsResult.Refused(409, PostingOffCode,
                    "A capitalised repair posts with the maintenance practice ledger: choose the first month it posts (MS-39) first.");
            var mapped = await MaintenanceAccountMap.AccountsAsync(db, ct);
            var labourWaits = wanted.CostIncludes != MaintenanceCostScope.PartsAndOutsideWorkOnly;
            if (!mapped.ContainsKey(FixedAssetAccountSlots.Asset) || !mapped.ContainsKey(MaintenanceAccountSlots.OwnParts)
                || (labourWaits && !mapped.ContainsKey(MaintenanceAccountSlots.Capitalised)))
                return MaintenanceSettingsResult.Refused(409, AccountsNotMappedCode,
                    "Capitalising repairs (MS-30) needs the register's asset account, own equipment repairs (maintenance.own_parts) and, while labour is in a job's cost, own work capitalised (maintenance.capitalised) in the account map first.");
        }

        if (existing is null) db.MaintenanceSettings.Add(row);
        var before = Covered(row);
        var wasApproved = row.AccountantApproved;
        Copy(wanted, row);
        // The confirmation covers the cost and accounting choices it was given on: a change confirmed again is
        // confirmed by whoever saved it, if they may confirm; a change by someone who may not leaves costs provisional.
        var changed = before != Covered(row);
        row.AccountantApproved = wanted.AccountantApproved;
        if (!row.AccountantApproved) (row.ApprovedBy, row.ApprovedAtUtc) = (null, null);
        else if (!wasApproved || changed)
        {
            if (has(PermissionCatalog.AccountingManage)) (row.ApprovedBy, row.ApprovedAtUtc) = (by, DateTime.UtcNow);
            else (row.AccountantApproved, row.ApprovedBy, row.ApprovedAtUtc) = (false, null, null);
        }
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new MaintenanceSettingsResult(ToDto(row, FixedAssetData.Active(await FixedAssetData.CurrentAsync(db, ct))));
    }

    /// <summary>The request as a row, its amounts kept only where their option uses them; or why it cannot be.</summary>
    private static (MaintenanceSettings? Wanted, string? Error) Parse(UpdateMaintenanceSettingsRequest r)
    {
        string? error = null;
        T Choice<T>(string? value, string what) where T : struct, Enum
        {
            if (Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)) return parsed;
            error ??= $"{what} is one of: {string.Join(", ", Enum.GetNames<T>())}.";
            return default;
        }
        var s = new MaintenanceSettings
        {
            Module = Choice<MaintenanceSwitch>(r.Module, "The maintenance module (MS-01)"),
            UnitsLeaveMaintenance = Choice<MaintenanceUnitRelease>(r.UnitsLeaveMaintenance, "How units leave maintenance (MS-02)"),
            JobsFromReturnNotes = Choice<MaintenanceReturnNoteJobs>(r.JobsFromReturnNotes, "Jobs from return notes (MS-03)"),
            BookableEmployees = Choice<MaintenanceBookable>(r.BookableEmployees, "Who may be booked on a job (MS-04)"),
            JobCloser = Choice<MaintenanceJobCloser>(r.JobCloser, "Who closes a job (MS-05)"),
            CustomerJobs = Choice<MaintenanceSwitch>(r.CustomerJobs, "Jobs for outside customers (MS-06)"),
            CostIncludes = Choice<MaintenanceCostScope>(r.CostIncludes, "What a job's cost includes (MS-10)"),
            LabourRate = Choice<MaintenanceLabourRate>(r.LabourRate, "The labour rate (MS-12)"),
            BurdenAndVat = Choice<MaintenanceBurdenSource>(r.BurdenAndVat, "The employer burden and expense VAT (MS-13)"),
            CustomerPrice = Choice<MaintenancePriceBasis>(r.CustomerPrice, "The customer job price (MS-20)"),
            ReturnRecharge = Choice<MaintenanceRechargeEntry>(r.ReturnRecharge, "The return-note recharge (MS-23)"),
            PriceBelowCost = Choice<MaintenanceBelowCost>(r.PriceBelowCost, "A customer price below cost (MS-25)"),
            RechargeSharing = Choice<MaintenanceRechargeShare>(r.RechargeSharing, "A recharge shared over several jobs (MS-26)"),
            OwnRepairs = Choice<MaintenanceOwnRepairs>(r.OwnRepairs, "Repairs on own equipment (MS-30)"),
            PartsPosting = Choice<MaintenancePartsPosting>(r.PartsPosting, "When parts reach the ledger (MS-32)"),
            LabourInBooks = Choice<MaintenanceLabourBooks>(r.LabourInBooks, "Labour in the books (MS-33)"),
            CustomerIncome = Choice<MaintenanceIncomeAccount>(r.CustomerIncome, "Customer repair income (MS-34)"),
            Advances = Choice<MaintenanceAdvances>(r.Advances, "Advances on customer jobs (MS-35)"),
            OpenJobsAtPeriodEnd = Choice<MaintenanceOpenJobs>(r.OpenJobsAtPeriodEnd, "Open customer jobs at period end (MS-36)"),
            Rework = Choice<MaintenanceRework>(r.Rework, "Free rework under guarantee (MS-37)"),
            RepairHistory = Choice<MaintenanceHistory>(r.RepairHistory, "The repair history (MS-38)"),
            AccountantApproved = r.AccountantApproved,
        };
        if (error is not null) return (null, error);

        if (r.OverheadPerLabourHour is < 0 or > MaxAmount)
            return (null, $"The overhead per labour hour is between 0 and {MaxAmount:0}.");
        if (r.WorkshopRatePerHour is < 0 or > MaxAmount)
            return (null, $"The workshop rate per hour is between 0 and {MaxAmount:0}.");
        if (r.LabourChargePerHour is < 0 or > MaxAmount)
            return (null, $"The labour charge rate per hour is between 0 and {MaxAmount:0}.");
        if (r.EmployerBurdenPercent is < 0 or > MaxPercent)
            return (null, $"The employer burden is between 0 and {MaxPercent:0} %.");
        if (r.ExpenseVatPercent is < 0 or > MaxPercent)
            return (null, $"The recoverable VAT rate is between 0 and {MaxPercent:0} %.");
        if (r.MarkupPercent is < 0 or > MaxMarkupPercent)
            return (null, $"The markup is between 0 and {MaxMarkupPercent:0} %.");
        if (r.GuaranteeMonths is < 0 or > MaxGuaranteeMonths)
            return (null, $"The guarantee is between 0 and {MaxGuaranteeMonths} months.");
        if (r.ProvisionPercent is < 0 or > MaxPercent)
            return (null, $"The provision rate is between 0 and {MaxPercent:0} %.");
        if (r.CapitaliseAboveAmount is < 0 or > MaxThreshold)
            return (null, $"The amount above which a repair is capitalised is between 0 and {MaxThreshold:0}.");
        if (r.PerPieceAboveValue is < 0 or > MaxThreshold)
            return (null, $"The unit value above which a piece is tracked is between 0 and {MaxThreshold:0}.");

        var withOverhead = s.CostIncludes == MaintenanceCostScope.PartsLabourOutsideWorkOverhead;
        if (withOverhead && r.OverheadPerLabourHour == 0)
            return (null, "Give the overhead per labour hour, or choose a cost without overhead.");
        if (s.LabourRate == MaintenanceLabourRate.WorkshopRate && r.WorkshopRatePerHour == 0)
            return (null, "Give the workshop rate per hour, or choose the payroll-derived rate.");
        s.OverheadPerLabourHour = withOverhead ? r.OverheadPerLabourHour : 0m;
        s.WorkshopRatePerHour = s.LabourRate == MaintenanceLabourRate.WorkshopRate ? r.WorkshopRatePerHour : 0m;
        var own = s.BurdenAndVat == MaintenanceBurdenSource.OwnValues;
        s.EmployerBurdenPercent = own ? r.EmployerBurdenPercent : 0m;
        s.ExpenseVatPercent = own ? r.ExpenseVatPercent : 0m;
        s.LabourChargePerHour = s.CustomerPrice == MaintenancePriceBasis.Itemised ? r.LabourChargePerHour : 0m;
        s.MarkupPercent = s.CustomerPrice == MaintenancePriceBasis.Quoted ? 0m : r.MarkupPercent;
        // Phase M7c: MS-27's months, and MS-37's rate kept only under the provision, where it is required, as is a guarantee
        // for it to run over.
        s.GuaranteeMonths = r.GuaranteeMonths;
        var provision = s.Rework == MaintenanceRework.Provision;
        if (provision && r.ProvisionPercent == 0)
            return (null, "Give the provision rate, or choose another treatment of rework (MS-37).");
        if (provision && s.GuaranteeMonths == 0)
            return (null, "A provision for rework runs over the guarantee: give the guarantee in months (MS-27), or choose another treatment of rework (MS-37).");
        s.ProvisionPercent = provision ? r.ProvisionPercent : 0m;
        // Phase M7d: MS-30's amount and MS-38's value, each kept only under its option, where it is required.
        var capitalise = s.OwnRepairs == MaintenanceOwnRepairs.CapitalisedAboveAmount;
        if (capitalise && r.CapitaliseAboveAmount == 0)
            return (null, "Give the amount above which a repair is capitalised, or keep repairs an expense (MS-30).");
        s.CapitaliseAboveAmount = capitalise ? r.CapitaliseAboveAmount : 0m;
        var perPiece = s.RepairHistory == MaintenanceHistory.PerPiece;
        if (perPiece && r.PerPieceAboveValue == 0)
            return (null, "Give the unit value above which a piece is tracked on its own, or keep the history per equipment type (MS-38).");
        s.PerPieceAboveValue = perPiece ? r.PerPieceAboveValue : 0m;

        if (!string.IsNullOrWhiteSpace(r.PostingFromMonth))
        {
            if (!DateOnly.TryParseExact(r.PostingFromMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var month) || month.Year is < 2000 or > 2100)
                return (null, "The first month to post is a month, yyyy-MM.");
            s.PostingFromMonth = month;
        }
        return (s, null);
    }

    /// <summary>The options the plan keeps for a later phase: stored as values, refused until then.</summary>
    private static string? NotYet(MaintenanceSettings s) =>
        // MS-04 (workshop list) and MS-34 (income accounts) were the last options held back; both are built (2026-11-10).
        null;

    private static void Copy(MaintenanceSettings from, MaintenanceSettings to)
    {
        to.Module = from.Module;
        to.UnitsLeaveMaintenance = from.UnitsLeaveMaintenance;
        to.JobsFromReturnNotes = from.JobsFromReturnNotes;
        to.BookableEmployees = from.BookableEmployees;
        to.JobCloser = from.JobCloser;
        to.CustomerJobs = from.CustomerJobs;
        to.CostIncludes = from.CostIncludes;
        to.OverheadPerLabourHour = from.OverheadPerLabourHour;
        to.LabourRate = from.LabourRate;
        to.WorkshopRatePerHour = from.WorkshopRatePerHour;
        to.BurdenAndVat = from.BurdenAndVat;
        to.EmployerBurdenPercent = from.EmployerBurdenPercent;
        to.ExpenseVatPercent = from.ExpenseVatPercent;
        to.CustomerPrice = from.CustomerPrice;
        to.LabourChargePerHour = from.LabourChargePerHour;
        to.MarkupPercent = from.MarkupPercent;
        to.ReturnRecharge = from.ReturnRecharge;
        to.PriceBelowCost = from.PriceBelowCost;
        to.RechargeSharing = from.RechargeSharing;
        to.GuaranteeMonths = from.GuaranteeMonths;
        to.ProvisionPercent = from.ProvisionPercent;
        to.CapitaliseAboveAmount = from.CapitaliseAboveAmount;
        to.PerPieceAboveValue = from.PerPieceAboveValue;
        to.OwnRepairs = from.OwnRepairs;
        to.PartsPosting = from.PartsPosting;
        to.LabourInBooks = from.LabourInBooks;
        to.CustomerIncome = from.CustomerIncome;
        to.Advances = from.Advances;
        to.OpenJobsAtPeriodEnd = from.OpenJobsAtPeriodEnd;
        to.Rework = from.Rework;
        to.RepairHistory = from.RepairHistory;
        to.PostingFromMonth = from.PostingFromMonth;
    }

    /// <summary>MS-01 to MS-26: maintenance.settings's half. Amounts compare by value, so 50 and 50.00 are the same.</summary>
    private static string Operational(MaintenanceSettings s) => string.Join('|',
        s.Module, s.UnitsLeaveMaintenance, s.JobsFromReturnNotes, s.BookableEmployees, s.JobCloser, s.CustomerJobs,
        s.CostIncludes, Amount(s.OverheadPerLabourHour), s.LabourRate, Amount(s.WorkshopRatePerHour), s.BurdenAndVat,
        Amount(s.EmployerBurdenPercent), Amount(s.ExpenseVatPercent),
        s.CustomerPrice, Amount(s.LabourChargePerHour), Amount(s.MarkupPercent), s.ReturnRecharge, s.PriceBelowCost, s.RechargeSharing,
        s.GuaranteeMonths);

    /// <summary>MS-30 to MS-40: accounting.manage's half, the confirmation included.</summary>
    private static string Accounting(MaintenanceSettings s) => string.Join('|',
        s.OwnRepairs, s.PartsPosting, s.LabourInBooks, s.CustomerIncome, s.Advances, s.OpenJobsAtPeriodEnd, s.Rework, s.RepairHistory,
        s.PostingFromMonth?.ToString("yyyy-MM", CultureInfo.InvariantCulture), s.AccountantApproved, Amount(s.ProvisionPercent),
        Amount(s.CapitaliseAboveAmount), Amount(s.PerPieceAboveValue));

    /// <summary>What the accountant's confirmation covers: the cost choices (MS-10 to MS-13) and the accounting ones (MS-30 to MS-38).</summary>
    private static string Covered(MaintenanceSettings s) => string.Join('|',
        s.CostIncludes, Amount(s.OverheadPerLabourHour), s.LabourRate, Amount(s.WorkshopRatePerHour), s.BurdenAndVat,
        Amount(s.EmployerBurdenPercent), Amount(s.ExpenseVatPercent),
        s.OwnRepairs, s.PartsPosting, s.LabourInBooks, s.CustomerIncome, s.Advances, s.OpenJobsAtPeriodEnd, s.Rework, s.RepairHistory,
        Amount(s.ProvisionPercent), Amount(s.CapitaliseAboveAmount), Amount(s.PerPieceAboveValue));

    private static string Amount(decimal d) => d.ToString("0.############################", CultureInfo.InvariantCulture);

    private static MaintenanceSettingsDto ToDto(MaintenanceSettings s, bool registerActive) => new(
        s.Module.ToString(), s.UnitsLeaveMaintenance.ToString(), s.JobsFromReturnNotes.ToString(), s.BookableEmployees.ToString(),
        s.JobCloser.ToString(), s.CustomerJobs.ToString(),
        s.CostIncludes.ToString(), s.OverheadPerLabourHour, s.LabourRate.ToString(), s.WorkshopRatePerHour, s.BurdenAndVat.ToString(),
        s.EmployerBurdenPercent, s.ExpenseVatPercent,
        s.CustomerPrice.ToString(), s.LabourChargePerHour, s.MarkupPercent, s.ReturnRecharge.ToString(), s.PriceBelowCost.ToString(),
        s.RechargeSharing.ToString(),
        s.OwnRepairs.ToString(), s.PartsPosting.ToString(), s.LabourInBooks.ToString(), s.CustomerIncome.ToString(), s.Advances.ToString(),
        s.OpenJobsAtPeriodEnd.ToString(), s.Rework.ToString(), s.RepairHistory.ToString(),
        s.PostingFromMonth?.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        s.AccountantApproved, s.ApprovedBy, s.ApprovedAtUtc, s.UpdatedAtUtc, s.UpdatedBy,
        s.GuaranteeMonths, s.ProvisionPercent, s.CapitaliseAboveAmount, s.PerPieceAboveValue, registerActive);
}
