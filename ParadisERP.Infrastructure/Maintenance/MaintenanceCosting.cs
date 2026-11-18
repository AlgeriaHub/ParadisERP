using ParadisERP.Application.Contracts;
using ParadisERP.Application.Maintenance;
using ParadisERP.Application.Manufacturing;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Manufacturing;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Maintenance;

/// <summary>
/// A job's cost and the maintenance cost reports, Phase M4 (docs/maintenance/plan.md §3.1, §6.2). Computed on read through
/// <see cref="MaintenanceCostAdapter"/> over the manufacturing cost engine, unchanged; nothing is stored, frozen or posted.
/// <list type="bullet">
/// <item><b>Parts</b>: issued less returned, at the values frozen on their movements (M2).</item>
/// <item><b>Labour</b>: the job's <c>LaborBookings</c> (<see cref="LaborOrderKind.Maintenance"/>) at the payroll rate of each
/// employee-month (<see cref="LabourRates"/>: standard until the month's payroll is approved, actual after — as production),
/// or at the one workshop rate MS-12 sets; out of the cost altogether under MS-10 "parts and outside work only".</item>
/// <item><b>Outside work</b>: the job's expenses, settled once their voucher is approved or paid.</item>
/// <item><b>Overhead</b>: MS-11 per labour hour, only under MS-10 "with overhead".</item>
/// <item>Employer burden (C-06) and expense VAT (C-07) as Manufacturing › Costing settings has them for reports, or MS-13's own
/// values; C-05 as manufacturing. Provisional until the accountant confirms (MS-40) and every element is final.</item>
/// </list>
/// <para>
/// <b>Privacy</b> (<see cref="CostPrivacy"/>, one rule for both modules): labour valued at payroll rates is shown only to a
/// reader who sees pay rates (<c>payroll.manage</c> + <c>manufacturing.costs.rates.view</c>). Anyone else with
/// <c>maintenance.costs.view</c> sees hours, parts, outside work, overhead and the recharge, but not a job's labour money,
/// its total or the recharge less its cost; a sum over several jobs (a product, a report's totals) only from two jobs with
/// labour and two workers. Under MS-12 "one workshop rate", or with labour out of the cost, no figure gives anyone's pay
/// back, and everything is shown.
/// </para>
/// </summary>
public sealed partial class MaintenanceCosting(PosDbContext db, LabourRates rates, IRegionalSettingsProvider regional)
{
    public const string RateKindWorkshop = "Workshop";
    public const string InvalidPeriodCode = "invalid_period";

    // ---------- the job ----------

    /// <param name="has">Whether the caller holds a key: decides whether labour money is shown (<see cref="CostPrivacy.RatesShown(Func{string, bool})"/>).</param>
    public async Task<MaintenanceResult<MaintenanceJobCostDto>> JobAsync(Guid jobId, Func<string, bool> has, CancellationToken ct = default)
    {
        var job = await db.MaintenanceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return MaintenanceResult<MaintenanceJobCostDto>.Fail(404, "not_found", "Maintenance job not found.");
        var (row, policy) = await SettingsAsync(ct);
        var costed = (await CostAsync([job], policy, ct))[job.Id];
        var recharge = job.Kind == MaintenanceJobKind.Recharged ? (await RechargesAsync([job], row, ct)).GetValueOrDefault(job.Id) : (decimal?)null;
        var shown = MoneyShown(policy, has);
        var withheld = !shown && costed.HasLabour;
        var sheet = costed.Sheet;

        var productIds = costed.Input.Parts.Select(p => p.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => new { p.NameAr, p.NameFr, p.NameEn }, ct);
        var employeeIds = sheet.LabourLines.Select(l => l.EmployeeId).Distinct().ToList();
        var people = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new { e.Number, e.FullName }, ct);
        var expenses = await db.MaintenanceJobExpenses.AsNoTracking().Where(e => e.JobId == job.Id).OrderBy(e => e.AtUtc).ToListAsync(ct);
        var voucherIds = expenses.Where(e => e.PaymentVoucherId != null).Select(e => e.PaymentVoucherId!.Value).Distinct().ToList();
        var vouchers = voucherIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.PaymentVouchers.AsNoTracking().Where(v => voucherIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Number, ct);
        var settled = costed.Input.Expenses.ToDictionary(e => e.Id, e => e.Settled);

        var total = Money(sheet.Total);
        return MaintenanceResult<MaintenanceJobCostDto>.Done(new MaintenanceJobCostDto(
            job.Id, job.Number, job.Kind.ToString(), job.Status.ToString(), sheet.Status, policy.Approved, !withheld,
            row.CostIncludes.ToString(), row.LabourRate.ToString(), row.BurdenAndVat.ToString(),
            policy.OverheadIncluded ? policy.OverheadPerLabourHour : 0m, policy.WorkshopRatePerHour,
            policy.WorkshopRatePerHour is null ? policy.EmployerBurdenPercent : 0m, policy.ExpenseVatPercent,
            Money(sheet.Parts), withheld ? null : Money(sheet.Labour), Money(sheet.Overhead), Money(sheet.OutsideWork),
            withheld ? null : total, sheet.LabourHours,
            recharge is decimal r ? Money(r) : null, recharge is decimal r2 && !withheld ? Money(r2) - total : (decimal?)null,
            new MaintenanceCostStatusesDto(sheet.Statuses.Parts, sheet.Statuses.Labour, sheet.Statuses.Overhead, sheet.Statuses.OutsideWork),
            sheet.PartLines.Where(p => p.NetBaseQty != 0m || p.NetValue != 0m).Select(p =>
            {
                var names = products.GetValueOrDefault(p.Id);
                return new MaintenanceCostPartDto(p.Id, p.Sku, names?.NameAr ?? "", names?.NameFr ?? "", names?.NameEn ?? "",
                    p.NetBaseQty, Math.Round(p.IssueAverage, 6, MidpointRounding.AwayFromZero), Money(p.NetValue));
            }).ToList(),
            sheet.LabourLines.Select(l =>
            {
                var person = people.GetValueOrDefault(l.EmployeeId);
                return new MaintenanceCostLabourDto(l.EmployeeId, person?.Number ?? "", person?.FullName ?? "", l.Year, l.Month,
                    l.Hours, l.OvertimeHours, policy.WorkshopRatePerHour is null ? l.Kind.ToString() : RateKindWorkshop, l.Reason,
                    withheld ? null : l.Rate is decimal rate ? Math.Round(rate, 6, MidpointRounding.AwayFromZero) : null,
                    withheld ? null : Money(l.Cost),
                    l.Kind != LabourRateKind.None && l.WithoutEmployerSocial && policy.WorkshopRatePerHour is null);
            }).ToList(),
            expenses.Select(e => new MaintenanceCostOutsideDto(e.Id, e.Description, e.Amount,
                Money(policy.ExpenseVatPercent > 0m ? e.Amount * 100m / (100m + policy.ExpenseVatPercent) : e.Amount),
                settled.GetValueOrDefault(e.Id),
                e.PaymentVoucherId is Guid v ? vouchers.GetValueOrDefault(v) : null)).ToList(),
            sheet.Notes.Select(n => new CostNoteDto(n.Code, n.Value is decimal value ? Math.Round(value, 6, MidpointRounding.AwayFromZero) : null)).ToList(),
            job.CapitalisedAtUtc, withheld ? null : job.CapitalisedAmount));
    }

    // ---------- the reports ----------

    /// <summary>Every recharged job of the period at <paramref name="branchId"/> (every branch when null), against its share of the recharge.</summary>
    public async Task<MaintenanceResult<MaintenanceRechargeReportDto>> RechargesAsync(
        Guid? branchId, DateOnly? from, DateOnly? to, Func<string, bool> has, CancellationToken ct = default)
    {
        if (Period(from, to) is not { } period) return Invalid<MaintenanceRechargeReportDto>();
        var (fromUtc, toUtc) = period;
        var (row, policy) = await SettingsAsync(ct);
        var jobs = await JobsAsync(branchId, fromUtc, toUtc, ct, j => j.Kind == MaintenanceJobKind.Recharged
            && j.Status != MaintenanceJobStatus.Draft && j.Status != MaintenanceJobStatus.Cancelled);
        var costs = await CostAsync(jobs, policy, ct);
        var recharges = await RechargesAsync(jobs, row, ct);
        var lineIds = jobs.Where(j => j.ReturnNoteLineId != null).Select(j => j.ReturnNoteLineId!.Value).Distinct().ToList();
        var lines = await db.ReturnNoteLines.AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.MaintenanceValue, ct);
        var names = await NamesAsync(jobs, ct);
        var shown = MoneyShown(policy, has);

        var rows = jobs.Select(j =>
        {
            var c = costs[j.Id];
            var withheld = !shown && c.HasLabour;
            var total = Money(c.Sheet.Total);
            var recharge = Money(recharges.GetValueOrDefault(j.Id));
            return new MaintenanceRechargeRowDto(
                j.Id, j.Number, j.Status.ToString(), j.BranchId, names.Branches.GetValueOrDefault(j.BranchId, ""),
                j.ProductId ?? Guid.Empty, j.Product?.Sku ?? "", j.Product?.NameAr ?? "", j.Product?.NameFr ?? "", j.Product?.NameEn ?? "", j.Qty,
                j.ContractId is Guid ct1 ? names.Contracts.GetValueOrDefault(ct1) : null,
                j.ReturnNoteId is Guid n ? names.Notes.GetValueOrDefault(n) : null,
                j.ContractId is Guid ct2 ? names.Customers.GetValueOrDefault(ct2) : null,
                Money(j.ReturnNoteLineId is Guid l ? lines.GetValueOrDefault(l) : 0m), recharge,
                Money(c.Sheet.Parts), withheld ? null : Money(c.Sheet.Labour), Money(c.Sheet.Overhead), Money(c.Sheet.OutsideWork),
                withheld ? null : total, withheld ? null : recharge - total,
                c.Sheet.LabourHours, c.Sheet.Status, withheld, j.CreatedAtUtc);
        }).ToList();

        var totalsShown = GroupShown(shown, costs.Values);
        var labour = Money(costs.Values.Sum(c => c.Sheet.Labour));
        var cost = costs.Values.Sum(c => Money(c.Sheet.Total));
        var recharged = rows.Sum(r => r.Recharge);
        return MaintenanceResult<MaintenanceRechargeReportDto>.Done(new MaintenanceRechargeReportDto(
            from, to, row.RechargeSharing.ToString(), shown, rows,
            recharged, rows.Sum(r => r.Parts), rows.Sum(r => r.Overhead), rows.Sum(r => r.OutsideWork), rows.Sum(r => r.LabourHours),
            totalsShown ? labour : null, totalsShown ? cost : null, totalsShown ? recharged - cost : null, !totalsShown));
    }

    /// <summary>
    /// Each equipment type's maintenance cost over the period, at <paramref name="branchId"/> (every branch when null): every
    /// job but drafts (a cancelled job keeps the parts and hours it used).
    /// </summary>
    public async Task<MaintenanceResult<MaintenanceProductCostReportDto>> ProductsAsync(
        Guid? branchId, DateOnly? from, DateOnly? to, Func<string, bool> has, CancellationToken ct = default)
    {
        if (Period(from, to) is not { } period) return Invalid<MaintenanceProductCostReportDto>();
        var (fromUtc, toUtc) = period;
        var (row, policy) = await SettingsAsync(ct);
        // Phase M5a: a customer job repairs outside equipment, not the company's: it is no equipment type's cost here.
        var jobs = await JobsAsync(branchId, fromUtc, toUtc, ct, j => j.Status != MaintenanceJobStatus.Draft && j.Kind != MaintenanceJobKind.Customer);
        var costs = await CostAsync(jobs, policy, ct);
        var recharges = await RechargesAsync(jobs.Where(j => j.Kind == MaintenanceJobKind.Recharged && j.Status != MaintenanceJobStatus.Cancelled).ToList(), row, ct);
        var shown = MoneyShown(policy, has);

        var rows = jobs.GroupBy(j => j.ProductId ?? Guid.Empty).Select(g =>
        {
            var product = g.First().Product;
            var mine = g.Select(j => costs[j.Id]).ToList();
            var groupShown = GroupShown(shown, mine);
            // Phase M7d (MS-30): what of the cost went into the register at completion, and what stays an expense.
            var capitalised = g.Sum(j => j.CapitalisedAmount ?? 0m);
            var total = mine.Sum(c => Money(c.Sheet.Total));
            return new MaintenanceProductCostRowDto(
                g.Key, product?.Sku ?? "", product?.NameAr ?? "", product?.NameFr ?? "", product?.NameEn ?? "",
                g.Count(), g.Sum(j => j.Qty), g.Sum(j => j.RepairedQty), g.Sum(j => j.ScrappedQty),
                mine.Sum(c => Money(c.Sheet.Parts)), mine.Sum(c => Money(c.Sheet.Overhead)), mine.Sum(c => Money(c.Sheet.OutsideWork)),
                mine.Sum(c => c.Sheet.LabourHours),
                groupShown ? mine.Sum(c => Money(c.Sheet.Labour)) : null, groupShown ? mine.Sum(c => Money(c.Sheet.Total)) : null,
                g.Sum(j => Money(recharges.GetValueOrDefault(j.Id))),
                mine.SelectMany(c => c.Workers).Distinct().Count(), !groupShown,
                mine.Any(c => c.Sheet.Status != ProductionCostEngine.StatusApproved),
                groupShown ? capitalised : null, groupShown ? total - capitalised : null);
        }).OrderBy(r => r.Sku, StringComparer.Ordinal).ToList();

        // The totals give back any product's sum withheld (total less the others), so they follow the strictest row.
        var totalsShown = rows.All(r => !r.Withheld);
        return MaintenanceResult<MaintenanceProductCostReportDto>.Done(new MaintenanceProductCostReportDto(
            from, to, shown, rows, rows.Sum(r => r.Jobs), rows.Sum(r => r.Parts), rows.Sum(r => r.Overhead), rows.Sum(r => r.OutsideWork),
            rows.Sum(r => r.LabourHours), rows.Sum(r => r.Recharge),
            totalsShown ? rows.Sum(r => r.Labour ?? 0m) : null, totalsShown ? rows.Sum(r => r.Total ?? 0m) : null, !totalsShown,
            totalsShown ? rows.Sum(r => r.Capitalised ?? 0m) : null, totalsShown ? rows.Sum(r => r.Expensed ?? 0m) : null));
    }

    // ---------- privacy ----------

    /// <summary>
    /// Whether this reader sees the labour money: always when labour is not valued at payroll rates (MS-12 workshop rate) or
    /// not in the cost (MS-10); otherwise only with pay-rate access.
    /// </summary>
    public static bool MoneyShown(MaintenanceCostSettings policy, Func<string, bool> has) =>
        !policy.LabourIncluded || policy.WorkshopRatePerHour is not null || CostPrivacy.RatesShown(has);

    /// <summary>
    /// Whether a sum over these jobs may be shown to a reader who does not see the labour money: from two jobs with labour and
    /// two workers (<see cref="CostPrivacy.LineSumShown"/>); a sum with no labour in it gives nothing back.
    /// </summary>
    private static bool GroupShown(bool moneyShown, IEnumerable<Costed> jobs)
    {
        var labour = jobs.Where(j => j.HasLabour).ToList();
        return moneyShown || labour.Count == 0
            || CostPrivacy.LineSumShown(false, labour.Count, labour.SelectMany(j => j.Workers).Distinct().Count());
    }

    // ---------- reading ----------

    /// <summary>One job's cost and what it rests on: its input, whether labour money is in it, and the people who worked it.</summary>
    private sealed record Costed(MaintenanceCostInput Input, MaintenanceCostSheet Sheet, bool HasLabour, IReadOnlyCollection<Guid> Workers);

    /// <summary>
    /// The settings row and the policy it gives: MS-10 to MS-13 and MS-40 from Maintenance › Settings; under MS-13 "follow
    /// manufacturing", the burden (C-06, as it counts in reports) and the expense VAT (C-07) of Manufacturing › Costing
    /// settings; C-05 always from there.
    /// </summary>
    internal async Task<(MaintenanceSettings Row, MaintenanceCostSettings Policy)> SettingsAsync(CancellationToken ct)
    {
        var row = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        var mfg = ProductionCosts.PolicyOf(await db.ManufacturingCostingSettings.AsNoTracking().FirstOrDefaultAsync(ct), forStockValue: false);
        var own = row.BurdenAndVat == MaintenanceBurdenSource.OwnValues;
        return (row, new MaintenanceCostSettings(
            LabourIncluded: row.CostIncludes != MaintenanceCostScope.PartsAndOutsideWorkOnly,
            OverheadIncluded: row.CostIncludes == MaintenanceCostScope.PartsLabourOutsideWorkOverhead,
            OverheadPerLabourHour: row.OverheadPerLabourHour,
            WorkshopRatePerHour: row.LabourRate == MaintenanceLabourRate.WorkshopRate ? row.WorkshopRatePerHour : null,
            EmployerBurdenPercent: own ? row.EmployerBurdenPercent : mfg.EmployerBurdenPercent,
            ExpenseVatPercent: own ? row.ExpenseVatPercent : mfg.ExpenseVatPercent,
            LabourAbsorption: mfg.LabourAbsorption,
            Approved: row.AccountantApproved));
    }

    /// <summary>Each job's cost, read in one pass: their parts, hours, rates and outside work.</summary>
    private async Task<Dictionary<Guid, Costed>> CostAsync(IReadOnlyList<MaintenanceJob> jobs, MaintenanceCostSettings policy, CancellationToken ct)
    {
        var ids = jobs.Select(j => j.Id).ToList();
        var orderIds = ids.Select(id => (Guid?)id).ToList();
        var parts = await db.MaintenanceJobParts.AsNoTracking().Where(p => ids.Contains(p.JobId))
            .Select(p => new { p.JobId, p.ProductId, p.Kind, p.Qty, p.Value, p.Product!.Sku, p.Product.ManufacturingRole, p.Product.BaseUnitId })
            .ToListAsync(ct);
        var unitIds = parts.Where(p => p.BaseUnitId != null).Select(p => p.BaseUnitId!.Value).Distinct().ToList();
        var units = unitIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.UnitsOfMeasure.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Code, ct);

        var bookings = await db.LaborBookings.AsNoTracking()
            .Where(b => b.OrderKind == LaborOrderKind.Maintenance && orderIds.Contains(b.OrderId))
            .Select(b => new { JobId = b.OrderId!.Value, b.EmployeeId, b.Day, b.PayrollYear, b.PayrollMonth, b.Hours, b.OvertimeHours })
            .ToListAsync(ct);
        // Payroll rates are read only when they value something: not under the workshop rate, nor with labour out of the cost.
        var labourRates = policy.LabourIncluded && policy.WorkshopRatePerHour is null && bookings.Count > 0
            ? await rates.ForAsync(bookings.Select(b => (b.EmployeeId, b.PayrollYear, b.PayrollMonth)), ct)
            : new Dictionary<(Guid EmployeeId, int Year, int Month), LabourRate>();

        var recorded = await db.MaintenanceJobExpenses.AsNoTracking().Where(e => ids.Contains(e.JobId))
            .Select(e => new { e.Id, e.JobId, e.Amount, e.AtUtc, e.PaymentVoucherId }).ToListAsync(ct);
        // An expense is backed once its voucher is approved or paid; read only, the voucher is never touched.
        var voucherIds = recorded.Where(e => e.PaymentVoucherId != null).Select(e => e.PaymentVoucherId!.Value).Distinct().ToList();
        HashSet<Guid> backed = voucherIds.Count == 0 ? [] : (await db.PaymentVouchers.AsNoTracking()
            .Where(v => voucherIds.Contains(v.Id) && (v.Status == PaymentVoucherStatus.Approved || v.Status == PaymentVoucherStatus.Paid))
            .Select(v => v.Id).ToListAsync(ct)).ToHashSet();

        var result = new Dictionary<Guid, Costed>(jobs.Count);
        foreach (var job in jobs)
        {
            var jobParts = parts.Where(p => p.JobId == job.Id).GroupBy(p => p.ProductId).Select(g =>
            {
                var first = g.First();
                var issued = g.Where(p => p.Kind == MaintenancePartKind.Issue).ToList();
                var returned = g.Where(p => p.Kind == MaintenancePartKind.Return).ToList();
                return new MaintenanceCostPart(g.Key, first.Sku, first.ManufacturingRole,
                    first.BaseUnitId is Guid unit ? units.GetValueOrDefault(unit, "") : "",
                    issued.Sum(p => p.Qty), returned.Sum(p => p.Qty), issued.Sum(p => p.Value), returned.Sum(p => p.Value));
            }).OrderBy(p => p.Sku, StringComparer.Ordinal).ToList();
            var jobBookings = bookings.Where(b => b.JobId == job.Id)
                .Select(b => new CostingBooking(b.EmployeeId, job.Id, b.Day, b.PayrollYear, b.PayrollMonth, b.Hours, b.OvertimeHours)).ToList();
            var jobExpenses = recorded.Where(e => e.JobId == job.Id)
                .Select(e => new CostingExpense(e.Id, e.Amount, e.AtUtc, e.PaymentVoucherId is Guid v && backed.Contains(v))).ToList();
            var input = new MaintenanceCostInput(job.Qty,
                job.Status is MaintenanceJobStatus.Completed or MaintenanceJobStatus.Invoiced,
                jobParts, jobBookings, labourRates, jobExpenses, policy);
            var sheet = MaintenanceCostAdapter.Compute(input);
            result[job.Id] = new Costed(input, sheet, policy.LabourIncluded && jobBookings.Count > 0,
                jobBookings.Select(b => b.EmployeeId).ToHashSet());
        }
        return result;
    }

    /// <summary>
    /// Each recharged job's share of its return-note line's recharge (MS-26): by units — the line's recharge × the job's units ÷
    /// the line's repairable units —, or all of it to the line's first job (by when it was opened); a cancelled job earns none.
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> RechargesAsync(IReadOnlyCollection<MaintenanceJob> jobs, MaintenanceSettings row, CancellationToken ct)
    {
        var lineIds = jobs.Where(j => j.ReturnNoteLineId != null).Select(j => j.ReturnNoteLineId!.Value).Distinct().ToList();
        if (lineIds.Count == 0) return [];
        var lines = await db.ReturnNoteLines.AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => new { l.QtyPartialDamage, l.MaintenanceValue }, ct);
        var lineIdsNullable = lineIds.Select(id => (Guid?)id).ToList();
        var firsts = (await db.MaintenanceJobs.AsNoTracking()
                .Where(j => lineIdsNullable.Contains(j.ReturnNoteLineId) && j.Status != MaintenanceJobStatus.Cancelled)
                .Select(j => new { j.Id, Line = j.ReturnNoteLineId!.Value, j.CreatedAtUtc, j.Number }).ToListAsync(ct))
            .GroupBy(j => j.Line)
            .ToDictionary(g => g.Key, g => g.OrderBy(j => j.CreatedAtUtc).ThenBy(j => j.Number, StringComparer.Ordinal).First().Id);

        var result = new Dictionary<Guid, decimal>();
        foreach (var job in jobs)
        {
            if (job.ReturnNoteLineId is not Guid lineId || !lines.TryGetValue(lineId, out var line)) continue;
            if (job.Status == MaintenanceJobStatus.Cancelled) { result[job.Id] = 0m; continue; }
            result[job.Id] = row.RechargeSharing == MaintenanceRechargeShare.AllToFirstJob
                ? (firsts.GetValueOrDefault(lineId) == job.Id ? line.MaintenanceValue : 0m)
                : line.QtyPartialDamage > 0 ? line.MaintenanceValue * Math.Min(job.Qty, line.QtyPartialDamage) / line.QtyPartialDamage : 0m;
        }
        return result;
    }

    private async Task<List<MaintenanceJob>> JobsAsync(Guid? branchId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct,
        System.Linq.Expressions.Expression<Func<MaintenanceJob, bool>> which)
    {
        var query = db.MaintenanceJobs.AsNoTracking().Include(j => j.Product).Where(which);
        if (branchId is Guid b) query = query.Where(j => j.BranchId == b);
        if (fromUtc is DateTime f) query = query.Where(j => j.CreatedAtUtc >= f);
        if (toUtc is DateTime t) query = query.Where(j => j.CreatedAtUtc < t);
        return await query.OrderBy(j => j.Number).Take(5000).ToListAsync(ct);
    }

    private sealed record Names(
        IReadOnlyDictionary<Guid, string> Branches, IReadOnlyDictionary<Guid, string> Contracts,
        IReadOnlyDictionary<Guid, string> Notes, IReadOnlyDictionary<Guid, string> Customers);

    private async Task<Names> NamesAsync(IReadOnlyCollection<MaintenanceJob> jobs, CancellationToken ct)
    {
        var branchIds = jobs.Select(j => j.BranchId).Distinct().ToList();
        var contractIds = jobs.Where(j => j.ContractId != null).Select(j => j.ContractId!.Value).Distinct().ToList();
        var noteIds = jobs.Where(j => j.ReturnNoteId != null).Select(j => j.ReturnNoteId!.Value).Distinct().ToList();
        var contracts = await db.RentalContracts.AsNoTracking().Where(c => contractIds.Contains(c.Id))
            .Join(db.Customers, c => c.CustomerId, x => x.Id, (c, x) => new { c.Id, c.Number, x.FullName }).ToListAsync(ct);
        return new Names(
            await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.NameFr, ct),
            contracts.ToDictionary(c => c.Id, c => c.Number),
            noteIds.Count == 0 ? new Dictionary<Guid, string>()
                : await db.ReturnNotes.AsNoTracking().Where(n => noteIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Number, ct),
            contracts.ToDictionary(c => c.Id, c => c.FullName));
    }

    /// <summary>The period's bounds in UTC: from the start of <paramref name="from"/> to the end of <paramref name="to"/>, company days.</summary>
    private (DateTime? From, DateTime? To)? Period(DateOnly? from, DateOnly? to)
    {
        if (from is DateOnly f && to is DateOnly t && f > t) return null;
        var zone = StoreDocumentHistory.Zone(regional);
        DateTime? Start(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone);
        return (from is DateOnly a ? Start(a) : null, to is DateOnly b ? Start(b.AddDays(1)) : null);
    }

    private static MaintenanceResult<T> Invalid<T>() =>
        MaintenanceResult<T>.Fail(400, InvalidPeriodCode, "The period's first day comes after its last.");

    private decimal Money(decimal value) => regional.Get().RoundMoney(value);
}
