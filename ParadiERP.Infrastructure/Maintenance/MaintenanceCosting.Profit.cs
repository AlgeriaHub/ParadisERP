using System.Globalization;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Manufacturing;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Catalog;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Maintenance;

/// <summary>A job's cost C as pricing reads it (Phase M5a): the total, whether it is final, and whether this reader may see it.</summary>
/// <param name="Shown">The reader holds <c>maintenance.costs.view</c> and the figure gives no one's pay back (M4's rule).</param>
public sealed record MaintenanceCostFigure(decimal Total, bool Provisional, bool Shown);

/// <summary>
/// Phase M5 (docs/maintenance/plan.md §3.3): the job's cost as the price reads it, and the company profit report. The owner
/// chose <b>company profit</b> (2026-11-09): a job's profit is what the company earns from outside less what the job cost —
/// no internal charge, no workshop profit. External revenue is the customer invoice's net before tax (less its credit
/// notes), or the recharge share M4 computes (MS-26); an internal job has none, and shows its cost as upkeep.
/// </summary>
public sealed partial class MaintenanceCosting
{
    public const string RevenueInvoice = "Invoice";
    public const string RevenueNotInvoiced = "NotInvoiced";
    public const string RevenueRecharge = "Recharge";
    public const string RevenueNone = "None";
    /// <summary>Phase M7c: a free rework under guarantee earns nothing (MS-37 "its own job" or "a provision").</summary>
    public const string RevenueRework = "Rework";

    /// <summary>
    /// The cost C of one job, computed exactly as its Cost card (M4's adapter over the manufacturing engine), for the
    /// cost-plus price and MS-25's comparison. <c>Shown</c> follows M4's privacy rule plus <c>maintenance.costs.view</c>.
    /// </summary>
    public async Task<MaintenanceCostFigure?> FigureAsync(Guid jobId, Func<string, bool> has, CancellationToken ct = default)
    {
        var job = await db.MaintenanceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return null;
        var (_, policy) = await SettingsAsync(ct);
        var costed = (await CostAsync([job], policy, ct))[job.Id];
        var withheld = !MoneyShown(policy, has) && costed.HasLabour;
        return new MaintenanceCostFigure(Money(costed.Sheet.Total), costed.Sheet.Status != ProductionCostEngine.StatusApproved,
            has(PermissionCatalog.MaintenanceCostsView) && !withheld);
    }

    /// <summary>
    /// The job's outside work at cost, as its cost counts it (M4): each amount net of the expense VAT the policy takes out
    /// (C-07 / MS-13). The itemised price charges outside work at this cost plus MS-22.
    /// </summary>
    public async Task<decimal> OutsideWorkAtCostAsync(Guid jobId, CancellationToken ct = default)
    {
        var (_, policy) = await SettingsAsync(ct);
        var amounts = await db.MaintenanceJobExpenses.AsNoTracking().Where(e => e.JobId == jobId).Select(e => e.Amount).ToListAsync(ct);
        return Money(amounts.Sum(a => policy.ExpenseVatPercent > 0m ? a * 100m / (100m + policy.ExpenseVatPercent) : a));
    }

    /// <summary>
    /// The company profit report over the jobs opened in the period at <paramref name="branchId"/> (every branch when null):
    /// every job but drafts. Per job, and by month (of opening, company time), branch, kind and product. Privacy is M4's: a
    /// job's cost and profit are withheld when its labour money is; a breakdown line's only from two jobs with labour and two
    /// workers; the totals only when no line of any breakdown is withheld and the whole passes the same rule.
    /// </summary>
    public async Task<MaintenanceResult<MaintenanceProfitReportDto>> ProfitAsync(
        Guid? branchId, DateOnly? from, DateOnly? to, Func<string, bool> has, CancellationToken ct = default)
    {
        if (Period(from, to) is not { } period) return Invalid<MaintenanceProfitReportDto>();
        var (fromUtc, toUtc) = period;
        var (row, policy) = await SettingsAsync(ct);
        var jobs = await JobsAsync(branchId, fromUtc, toUtc, ct, j => j.Status != MaintenanceJobStatus.Draft);

        // Phase M7c, MS-37. "Added to the original job" (default): a rework under guarantee is no line of its own; its cost joins
        // the line of the job it redoes, wherever the rework falls in time, so that job's profit is its real result. "Its own
        // job" and "a provision": each rework is its own line, revenue none (the books take it from the provision under the
        // latter; the report shows the work as it was done).
        var addToOriginal = row.Rework == MaintenanceRework.AddedToOriginalJob;
        List<MaintenanceJob> reworks = [];
        if (addToOriginal)
        {
            var originalIds = jobs.Where(j => j.ReworkOfJobId == null && j.Kind == MaintenanceJobKind.Customer).Select(j => j.Id).ToList();
            reworks = originalIds.Count == 0 ? []
                : await db.MaintenanceJobs.AsNoTracking().Include(j => j.Product)
                    .Where(j => j.ReworkOfJobId != null && originalIds.Contains(j.ReworkOfJobId.Value) && j.Status != MaintenanceJobStatus.Draft)
                    .OrderBy(j => j.Number).ToListAsync(ct);
            jobs = jobs.Where(j => j.ReworkOfJobId == null).ToList();
        }
        var costs = await CostAsync(jobs.Concat(reworks).ToList(), policy, ct);
        var recharges = await RechargesAsync(jobs.Where(j => j.Kind == MaintenanceJobKind.Recharged).ToList(), row, ct);
        var invoiced = await InvoiceRevenueAsync(jobs, ct);
        var names = await NamesAsync(jobs, ct);
        var customerIds = jobs.Where(j => j.CustomerId != null).Select(j => j.CustomerId!.Value).Distinct().ToList();
        var customers = customerIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FullName, ct);
        var originalNumbers = await OriginalNumbersAsync(jobs, ct);
        var shown = MoneyShown(policy, has);
        var zone = StoreDocumentHistory.Zone(regional);

        var lines = jobs.Select(j =>
        {
            var mine = reworks.Where(r => r.ReworkOfJobId == j.Id).Select(r => costs[r.Id]).ToList();
            var all = mine.Prepend(costs[j.Id]).ToList();
            var (source, revenue, number) = j.Kind switch
            {
                _ when j.ReworkOfJobId is not null => (RevenueRework, 0m, (string?)null),
                MaintenanceJobKind.Customer => invoiced.TryGetValue(j.Id, out var inv) ? (RevenueInvoice, inv.Net, inv.Number) : (RevenueNotInvoiced, 0m, (string?)null),
                MaintenanceJobKind.Recharged => (RevenueRecharge, Money(recharges.GetValueOrDefault(j.Id)), null),
                _ => (RevenueNone, 0m, null),
            };
            var withheld = !shown && all.Any(c => c.HasLabour);
            var total = all.Sum(c => Money(c.Sheet.Total));
            var reworkCost = mine.Sum(c => Money(c.Sheet.Total));
            var status = all.Select(c => c.Sheet.Status).FirstOrDefault(s => s != ProductionCostEngine.StatusApproved) ?? ProductionCostEngine.StatusApproved;
            var month = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(j.CreatedAtUtc, DateTimeKind.Utc), zone)
                .ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var dto = new MaintenanceProfitRowDto(
                j.Id, j.Number, j.Kind.ToString(), j.Status.ToString(), j.BranchId, names.Branches.GetValueOrDefault(j.BranchId, ""),
                j.ProductId, j.Product?.Sku ?? "", j.Product?.NameAr ?? "", j.Product?.NameFr ?? "", j.Product?.NameEn ?? "",
                j.Equipment,
                j.CustomerId is Guid cu ? customers.GetValueOrDefault(cu) : j.ContractId is Guid ct2 ? names.Customers.GetValueOrDefault(ct2) : null,
                month, j.Qty, source, revenue, number,
                all.Sum(c => Money(c.Sheet.Parts)), all.Sum(c => Money(c.Sheet.OutsideWork)), all.Sum(c => Money(c.Sheet.Overhead)),
                all.Sum(c => c.Sheet.LabourHours),
                withheld ? null : all.Sum(c => Money(c.Sheet.Labour)), withheld ? null : total, withheld ? null : revenue - total,
                status, withheld, j.CreatedAtUtc,
                j.ReworkOfJobId is Guid of ? originalNumbers.GetValueOrDefault(of) : null, mine.Count,
                mine.Count == 0 || withheld ? null : reworkCost);
            return (Row: dto, Costed: (IReadOnlyList<Costed>)all, Total: total);
        }).ToList();

        List<MaintenanceProfitGroupDto> Breakdown<TKey>(Func<(MaintenanceProfitRowDto Row, IReadOnlyList<Costed> Costed, decimal Total), TKey> key,
            Func<TKey, (string Key, string Label, string? Ar, string? Fr, string? En)> label) where TKey : notnull =>
            lines.GroupBy(key).Select(g =>
            {
                var groupShown = GroupShown(shown, g.SelectMany(x => x.Costed));
                var revenue = g.Sum(x => x.Row.Revenue);
                var cost = g.Sum(x => x.Total);
                var l = label(g.Key);
                return new MaintenanceProfitGroupDto(l.Key, l.Label, l.Ar, l.Fr, l.En, g.Count(), revenue,
                    groupShown ? cost : null, groupShown ? revenue - cost : null, !groupShown);
            }).OrderBy(x => x.Label, StringComparer.Ordinal).ToList();

        var byMonth = Breakdown(x => x.Row.Month, m => (m, m, null, null, null));
        var byBranch = Breakdown(x => x.Row.BranchId, b => (b.ToString(), names.Branches.GetValueOrDefault(b, ""), null, null, null));
        var byKind = Breakdown(x => x.Row.Kind, k => (k, k, null, null, null));
        var products = jobs.Where(j => j.Product != null).GroupBy(j => j.ProductId!.Value).ToDictionary(g => g.Key, g => g.First().Product!);
        var byProduct = Breakdown(x => x.Row.ProductId ?? Guid.Empty, p => products.TryGetValue(p, out var product)
            ? (p.ToString(), product.Sku, product.NameAr, product.NameFr, product.NameEn)
            : ("", "", null, null, null));

        // The totals less the lines shown would give back any line withheld, so they follow the strictest line.
        var totalsShown = GroupShown(shown, lines.SelectMany(x => x.Costed))
            && new[] { byMonth, byBranch, byKind, byProduct }.All(b => b.All(g => !g.Withheld));
        var revenueTotal = lines.Sum(x => x.Row.Revenue);
        var costTotal = lines.Sum(x => x.Total);
        return MaintenanceResult<MaintenanceProfitReportDto>.Done(new MaintenanceProfitReportDto(
            from, to, shown, lines.Select(x => x.Row).ToList(), byMonth, byBranch, byKind, byProduct,
            revenueTotal, totalsShown ? costTotal : null, totalsShown ? revenueTotal - costTotal : null, !totalsShown,
            row.Rework.ToString()));
    }

    /// <summary>The numbers of the jobs these reworks redo (Phase M7c).</summary>
    private async Task<Dictionary<Guid, string>> OriginalNumbersAsync(IReadOnlyCollection<MaintenanceJob> jobs, CancellationToken ct)
    {
        var ids = jobs.Where(j => j.ReworkOfJobId != null).Select(j => j.ReworkOfJobId!.Value).Distinct().ToList();
        return ids.Count == 0 ? []
            : await db.MaintenanceJobs.AsNoTracking().Where(j => ids.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.Number, ct);
    }

    /// <summary>
    /// Each invoiced customer job's external revenue: its invoice's net before tax, less the net of the credit notes raised
    /// against it (a credit gives revenue back). Tax is the state's, never revenue.
    /// </summary>
    private async Task<Dictionary<Guid, (decimal Net, string Number)>> InvoiceRevenueAsync(IReadOnlyCollection<MaintenanceJob> jobs, CancellationToken ct)
    {
        var invoiceIds = jobs.Where(j => j.Kind == MaintenanceJobKind.Customer && j.InvoiceId != null).Select(j => j.InvoiceId!.Value).ToList();
        if (invoiceIds.Count == 0) return [];
        var invoices = await db.Invoices.AsNoTracking().Where(i => invoiceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Number, i.SubTotalHt, i.SourceId }).ToListAsync(ct);
        var credits = (await db.CreditNotes.AsNoTracking().Where(n => invoiceIds.Contains(n.InvoiceId))
                .Select(n => new { n.InvoiceId, n.SubTotalHt }).ToListAsync(ct))
            .GroupBy(n => n.InvoiceId).ToDictionary(g => g.Key, g => g.Sum(n => n.SubTotalHt));
        return invoices.Where(i => i.SourceId != null).ToDictionary(i => i.SourceId!.Value,
            i => (Money(i.SubTotalHt - credits.GetValueOrDefault(i.Id)), i.Number));
    }
}
