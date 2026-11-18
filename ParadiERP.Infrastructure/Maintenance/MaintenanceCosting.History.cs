using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Maintenance;

public sealed partial class MaintenanceCosting
{
    /// <summary>
    /// Maintenance › Repair history (Phase M7d, MS-38, MNT-10): every job but drafts and customer jobs on
    /// <paramref name="productId"/> at <paramref name="branchId"/> (every branch when null), or — a reference asked — on that
    /// piece (trimmed, case ignored). Under either MS-38 option the history reads the same jobs; "per piece" only says which
    /// types name a piece on every job. Dates, kind, units, hours, parts and outside work for every reader of jobs; each job's
    /// cost and its capitalised part (MS-30) only to a <paramref name="has"/> <c>maintenance.costs.view</c> holder, under M4's
    /// privacy rule; the totals of those under its sum rule.
    /// </summary>
    public async Task<MaintenanceResult<MaintenanceRepairHistoryDto>> HistoryAsync(
        Guid productId, string? reference, Guid? branchId, Func<string, bool> has, CancellationToken ct = default)
    {
        var product = await db.Products.AsNoTracking().Where(p => p.Id == productId)
            .Select(p => new { p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn, p.AssetUnitValue, p.CostPrice }).FirstOrDefaultAsync(ct);
        if (product is null) return MaintenanceResult<MaintenanceRepairHistoryDto>.Fail(404, "not_found", "Product not found.");
        var (row, policy) = await SettingsAsync(ct);
        var tracked = (await MaintenancePieces.TrackedAsync(db, row, [productId], ct)).Contains(productId);

        var query = db.MaintenanceJobs.AsNoTracking().Include(j => j.Product)
            .Where(j => j.ProductId == productId && j.Kind != MaintenanceJobKind.Customer && j.Status != MaintenanceJobStatus.Draft);
        if (branchId is Guid b) query = query.Where(j => j.BranchId == b);
        var all = await query.OrderBy(j => j.CreatedAtUtc).ThenBy(j => j.Number).Take(5000).ToListAsync(ct);
        var pieces = all.Where(j => !string.IsNullOrWhiteSpace(j.ItemReference))
            .GroupBy(j => MaintenancePieces.Key(j.ItemReference!))
            .Select(g => new MaintenancePieceDto(g.First().ItemReference!.Trim(), g.Count(), g.Max(j => j.CreatedAtUtc)))
            .OrderBy(p => p.Reference, StringComparer.Ordinal).ToList();
        var asked = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        var jobs = asked is null ? all
            : all.Where(j => j.ItemReference is not null && MaintenancePieces.Key(j.ItemReference) == MaintenancePieces.Key(asked)).ToList();

        var costs = await CostAsync(jobs, policy, ct);
        var names = await NamesAsync(jobs, ct);
        var partIds = costs.Values.SelectMany(c => c.Input.Parts.Select(p => p.ProductId)).Distinct().ToList();
        var partNames = await db.Products.AsNoTracking().Where(p => partIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => new { p.NameAr, p.NameFr, p.NameEn }, ct);
        var costsShown = has(PermissionCatalog.MaintenanceCostsView);
        var shown = costsShown && MoneyShown(policy, has);

        var rows = jobs.Select(j =>
        {
            var c = costs[j.Id];
            var withheld = costsShown && !shown && c.HasLabour;
            var visible = costsShown && !withheld;
            return new MaintenanceHistoryJobDto(
                j.Id, j.Number, j.Kind.ToString(), j.Status.ToString(), j.BranchId, names.Branches.GetValueOrDefault(j.BranchId, ""),
                j.ItemReference, j.Qty, j.RepairedQty, j.ScrappedQty, j.OpenedAtUtc ?? j.CreatedAtUtc, j.CompletedAtUtc,
                c.Sheet.LabourHours, Money(c.Sheet.Parts),
                c.Sheet.PartLines.Where(p => p.NetBaseQty != 0m || p.NetValue != 0m).Select(p =>
                {
                    var n = partNames.GetValueOrDefault(p.Id);
                    return new MaintenanceHistoryPartDto(p.Id, p.Sku, n?.NameAr ?? "", n?.NameFr ?? "", n?.NameEn ?? "", p.NetBaseQty, Money(p.NetValue));
                }).ToList(),
                Money(c.Sheet.OutsideWork),
                visible ? Money(c.Sheet.Total) : null, withheld,
                visible ? j.CapitalisedAmount : null, c.Sheet.Status);
        }).ToList();

        var totalsShown = costsShown && GroupShown(shown, costs.Values);
        return MaintenanceResult<MaintenanceRepairHistoryDto>.Done(new MaintenanceRepairHistoryDto(
            product.Id, product.Sku, product.NameAr, product.NameFr, product.NameEn,
            row.RepairHistory.ToString(), tracked, MaintenancePieces.UnitValue(product.AssetUnitValue, product.CostPrice),
            row.RepairHistory == MaintenanceHistory.PerPiece ? row.PerPieceAboveValue : 0m, asked, pieces, rows,
            rows.Sum(r => r.LabourHours), rows.Sum(r => r.Parts), rows.Sum(r => r.OutsideWork),
            totalsShown ? costs.Values.Sum(c => Money(c.Sheet.Total)) : null,
            totalsShown ? jobs.Sum(j => j.CapitalisedAmount ?? 0m) : null,
            costsShown, costsShown && !totalsShown));
    }
}
