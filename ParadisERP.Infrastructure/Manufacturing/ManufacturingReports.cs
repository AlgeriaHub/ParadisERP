using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Manufacturing;

/// <summary>
/// The manufacturing reports (Phase 2 §2G; owner, 2026-10-26, decisions 1 and 8).
/// <list type="bullet">
/// <item><b>Materials sold at POS</b>: the exceptions Phase 1 records for every till sale of something not for
/// sale — product, branch, quantity, receipt, date, and whether the till sold it before the product was
/// withdrawn (stale offline catalogue), after it, or with no withdrawal on file. A caller without company
/// scope sees only the branch they work at.</item>
/// <item><b>Movements awaiting posting</b>: what manufacturing moved in stock that the ledger has not seen, at
/// the reference cost snapshot of each movement — the reconciliation the accountant reviews before postings
/// are switched on. Per line, within the caller's reach.</item>
/// </list>
/// </summary>
public sealed class ManufacturingReports(PosDbContext db, ProductionCosts costs)
{
    public const string StaleYes = "stale";
    public const string StaleNo = "after_withdrawal";
    public const string StaleUnknown = "unknown";

    private const int MaxRows = 2000;

    public async Task<MfgResult<PosExceptionReportDto>> PosExceptionsAsync(
        MfgCaller caller, DateOnly? from, DateOnly? to, Guid? branchId, Guid? productId, string? sale, string? stale,
        TimeZoneInfo zone, CancellationToken ct)
    {
        var query = db.ManufacturingSaleExceptions.AsNoTracking().AsQueryable();
        // Branch scope like every other report: a branch user's own branch, whatever they ask for.
        if (!caller.CompanyScoped)
        {
            if (caller.BranchId is not Guid own) return MfgResult<PosExceptionReportDto>.Done(new([], 0, 0, 0, 0, 0));
            query = query.Where(e => e.BranchId == own);
        }
        else if (branchId is Guid b) query = query.Where(e => e.BranchId == b);
        if (productId is Guid p) query = query.Where(e => e.ProductId == p);
        if (from is DateOnly f) query = query.Where(e => e.SoldAtUtc >= TimeZoneInfo.ConvertTimeToUtc(f.ToDateTime(TimeOnly.MinValue), zone));
        if (to is DateOnly t) query = query.Where(e => e.SoldAtUtc < TimeZoneInfo.ConvertTimeToUtc(t.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));
        if (MfgText.Clean(sale) is string reference) query = query.Where(e => e.Reference.Contains(reference));
        switch (stale)
        {
            case null or "": break;
            case StaleYes: query = query.Where(e => e.SoldBeforeWithdrawal); break;
            case StaleNo: query = query.Where(e => !e.SoldBeforeWithdrawal && e.WithdrawnAtUtc != null); break;
            case StaleUnknown: query = query.Where(e => e.WithdrawnAtUtc == null); break;
            default: return MfgFail.Invalid<PosExceptionReportDto>("invalid_filter", "Unknown stale filter.");
        }

        var rows = await query.OrderByDescending(e => e.SoldAtUtc).Take(MaxRows).ToListAsync(ct);
        var branchIds = rows.Select(r => r.BranchId).Distinct().ToList();
        var productIds = rows.Select(r => r.ProductId).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking().Where(x => branchIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var products = await db.Products.AsNoTracking().Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var dtos = rows.Select(e =>
        {
            var branch = branches.GetValueOrDefault(e.BranchId);
            var product = products.GetValueOrDefault(e.ProductId);
            return new PosExceptionRowDto(
                e.Id, e.SaleId, e.Reference, e.SoldAtUtc, e.ReceivedAtUtc, e.BranchId, branch?.Code ?? "", branch?.NameAr ?? "",
                branch?.NameFr ?? "", e.DeviceCode, e.ProductId, e.Sku, product?.NameAr ?? "", product?.NameFr ?? "", product?.NameEn ?? "",
                e.Quantity, e.Role.ToString(), e.CashierName, e.WithdrawnAtUtc,
                e.WithdrawnAtUtc is null ? StaleUnknown : e.SoldBeforeWithdrawal ? StaleYes : StaleNo);
        }).ToList();
        return MfgResult<PosExceptionReportDto>.Done(new PosExceptionReportDto(
            dtos, dtos.Count, dtos.Sum(r => r.Quantity),
            dtos.Count(r => r.Stale == StaleYes), dtos.Count(r => r.Stale == StaleNo), dtos.Count(r => r.Stale == StaleUnknown)));
    }

    public async Task<PendingMovementsDto> PendingMovementsAsync(MfgCaller caller, Guid? lineId, CancellationToken ct)
    {
        var reachable = ManufacturingAccess.Reachable(db, caller).Select(l => l.Id);
        var lines = await db.ProductionLines.AsNoTracking().Where(l => reachable.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Code, ct);
        var ids = lineId is Guid one ? lines.Keys.Where(k => k == one).ToList() : lines.Keys.ToList();

        var materials = await db.MaterialMovementLines.AsNoTracking()
            .Where(l => ids.Contains(l.Movement!.LineId) && l.Movement.PostingState == ManufacturingPostingState.Pending
                && l.Movement.Kind != MaterialMovementKind.Scrap)
            .Select(l => new
            {
                l.Movement!.Id, l.Movement.Number, l.Movement.Kind, l.Movement.OrderId, l.Movement.LineId, l.ProductId, l.BaseQty,
                l.UnitCostSnapshot, l.Movement.UserName, l.Movement.AtUtc,
            }).ToListAsync(ct);
        var releases = await db.OutputReleases.AsNoTracking()
            .Where(r => ids.Contains(r.LineId) && r.PostingState == ManufacturingPostingState.Pending).ToListAsync(ct);
        var orderIds = materials.Select(m => m.OrderId).Concat(releases.Select(r => r.OrderId)).Distinct().ToList();
        var numbers = await db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        var releaseProducts = await db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.ProductId, ct);
        var allProducts = materials.Select(m => m.ProductId).Concat(releaseProducts.Values).Distinct().ToList();
        var skus = await db.Products.AsNoTracking().Where(p => allProducts.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Sku, ct);

        // A release's own snapshot is the finished product's catalogue cost, not what it cost to make (costing
        // plan F1): it is valued by the costing preview for a caller shown costs and pay rates, since its value is the
        // order's cost less the other releases (CostPrivacy), and not valued otherwise.
        var valued = costs.Shows(caller) && CostPrivacy.RatesShown(caller);
        var releaseValues = valued ? await costs.ReleaseValuesAsync(releases.Select(r => r.OrderId).Distinct().ToList(), ct) : [];

        var rows = materials.Select(m => new PendingMovementRowDto(
                "material", m.Number, m.Kind.ToString(), m.OrderId, numbers.GetValueOrDefault(m.OrderId, ""), m.LineId, lines.GetValueOrDefault(m.LineId, ""),
                m.ProductId, skus.GetValueOrDefault(m.ProductId, ""), m.BaseQty, m.UnitCostSnapshot, m.BaseQty * m.UnitCostSnapshot,
                m.Kind == MaterialMovementKind.Issue ? "line_to_order" : "order_to_line", m.UserName, m.AtUtc))
            .Concat(releases.Select(r =>
            {
                var product = releaseProducts.GetValueOrDefault(r.OrderId);
                var value = releaseValues.GetValueOrDefault(r.Id);
                return new PendingMovementRowDto(
                    "release", r.Number, "Release", r.OrderId, numbers.GetValueOrDefault(r.OrderId, ""), r.LineId, lines.GetValueOrDefault(r.LineId, ""),
                    product, skus.GetValueOrDefault(product, ""), r.BaseQty, Money(value?.UnitValue, 6), Money(value?.Value, 3),
                    "order_to_line", r.UserName, r.AtUtc);
            }))
            .OrderBy(r => r.AtUtc).Take(MaxRows).ToList();

        var issued = materials.Where(m => m.Kind == MaterialMovementKind.Issue).Sum(m => m.BaseQty * m.UnitCostSnapshot);
        var returned = materials.Where(m => m.Kind == MaterialMovementKind.Return).Sum(m => m.BaseQty * m.UnitCostSnapshot);
        decimal? released = valued ? Money(releases.Sum(r => releaseValues.GetValueOrDefault(r.Id)?.Value ?? 0m), 3) : null;
        return new PendingMovementsDto(rows, issued, returned, released, issued - returned, valued);
    }

    /// <summary>
    /// R3 of the costing plan: manufacturing documents against stock, on the lines the caller reaches.
    /// Lists every manufacturing stock movement no document names, every tracked document line with no
    /// stock movement or a different quantity, every order material whose counters differ from its
    /// documents, and every line-location stock that differs from the sum of its movements.
    /// </summary>
    public async Task<MaterialFlowReportDto> MaterialFlowAsync(MfgCaller caller, CancellationToken ct)
    {
        var reachable = ManufacturingAccess.Reachable(db, caller).Select(l => l.Id);
        var lines = await db.ProductionLines.AsNoTracking().Where(l => reachable.Contains(l.Id))
            .Select(l => new { l.Id, l.LocationId }).ToListAsync(ct);
        var lineIds = lines.Select(l => l.Id).ToList();
        var locations = lines.Select(l => l.LocationId).Distinct().ToList();
        var locationCodes = await db.Branches.AsNoTracking().Where(b => locations.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        string[] reasons = [ProductionMovements.IssueReason, ProductionMovements.ReturnReason, ProductionMovements.OutputReason];
        var issues = new List<MaterialFlowIssueDto>();

        var stockMoves = await db.StockMovements.AsNoTracking()
            .Where(m => locations.Contains(m.BranchId) && reasons.Contains(m.Reason))
            .Select(m => new { m.Id, m.ProductId, m.BranchId, m.QtyDelta, m.DocumentNumber }).ToListAsync(ct);
        var documentLines = await db.MaterialMovementLines.AsNoTracking()
            .Where(l => lineIds.Contains(l.Movement!.LineId))
            .Select(l => new { l.OrderMaterialId, l.ProductId, l.BaseQty, l.StockMovementId, l.Movement!.Kind, l.Movement.Number,
                l.Movement.OrderId, l.Movement.LocationId }).ToListAsync(ct);
        var releases = await db.OutputReleases.AsNoTracking().Where(r => lineIds.Contains(r.LineId))
            .Select(r => new { r.Number, r.OrderId, r.BaseQty, r.StockMovementId, r.LocationId }).ToListAsync(ct);
        var orders = await db.ProductionOrders.AsNoTracking().Where(o => lineIds.Contains(o.LineId))
            .Select(o => new { o.Id, o.Number, o.ProductId }).ToListAsync(ct);
        var orderNumbers = orders.ToDictionary(o => o.Id, o => o.Number);
        var orderProduct = orders.ToDictionary(o => o.Id, o => o.ProductId);
        var stocks = await db.ProductStocks.AsNoTracking().Where(s => locations.Contains(s.BranchId))
            .Select(s => new { s.ProductId, s.BranchId, s.StockQty }).ToListAsync(ct);
        var productIds = stockMoves.Select(m => m.ProductId).Concat(documentLines.Select(l => l.ProductId))
            .Concat(orderProduct.Values).Concat(stocks.Select(s => s.ProductId)).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => new { p.Sku, p.TrackStock }, ct);
        bool Tracked(Guid product) => products.GetValueOrDefault(product)?.TrackStock ?? false;
        MaterialFlowIssueDto Issue(string kind, string reference, Guid? order, Guid? product, Guid? location, decimal? expected, decimal? actual) =>
            new(kind, reference, order, order is Guid o ? orderNumbers.GetValueOrDefault(o) : null, product,
                product is Guid p ? products.GetValueOrDefault(p)?.Sku : null, location, location is Guid l ? locationCodes.GetValueOrDefault(l) : null,
                expected, actual);

        // Each document line or release that moved stock names its movement, with the same quantity.
        var named = new Dictionary<Guid, decimal>();
        foreach (var l in documentLines.Where(l => l.Kind != MaterialMovementKind.Scrap))
        {
            if (l.StockMovementId is Guid id) named[id] = l.Kind == MaterialMovementKind.Issue ? -l.BaseQty : l.BaseQty;
            else if (Tracked(l.ProductId)) issues.Add(Issue("document_without_stock_movement", l.Number, l.OrderId, l.ProductId, l.LocationId, l.BaseQty, null));
        }
        foreach (var r in releases)
        {
            var product = orderProduct.GetValueOrDefault(r.OrderId);
            if (r.StockMovementId is Guid id) named[id] = r.BaseQty;
            else if (Tracked(product)) issues.Add(Issue("document_without_stock_movement", r.Number, r.OrderId, product, r.LocationId, r.BaseQty, null));
        }
        foreach (var m in stockMoves)
        {
            if (!named.TryGetValue(m.Id, out var expected))
                issues.Add(Issue("stock_movement_orphan", m.DocumentNumber ?? "", null, m.ProductId, m.BranchId, null, m.QtyDelta));
            else if (expected != m.QtyDelta)
                issues.Add(Issue("stock_movement_quantity", m.DocumentNumber ?? "", null, m.ProductId, m.BranchId, expected, m.QtyDelta));
        }

        // Each order material's counters against its documents.
        var materials = await db.ProductionOrderMaterials.AsNoTracking().Where(m => lineIds.Contains(m.Order!.LineId))
            .Select(m => new { m.Id, m.OrderId, m.ProductId, m.IssuedBaseQty, m.ReturnedBaseQty, m.ScrappedBaseQty, m.ConsumedBaseQty })
            .ToListAsync(ct);
        var materialIds = materials.Select(m => m.Id).ToList();
        var consumed = (await db.MaterialConsumptions.AsNoTracking().Where(c => materialIds.Contains(c.OrderMaterialId))
                .Select(c => new { c.OrderMaterialId, c.BaseQty }).ToListAsync(ct))
            .GroupBy(c => c.OrderMaterialId).ToDictionary(g => g.Key, g => g.Sum(c => c.BaseQty));
        foreach (var m in materials)
        {
            decimal Documented(MaterialMovementKind kind) =>
                documentLines.Where(l => l.OrderMaterialId == m.Id && l.Kind == kind).Sum(l => l.BaseQty);
            foreach (var (kind, counter, documented) in new[]
            {
                ("issued", m.IssuedBaseQty, Documented(MaterialMovementKind.Issue)),
                ("returned", m.ReturnedBaseQty, Documented(MaterialMovementKind.Return)),
                ("scrapped", m.ScrappedBaseQty, Documented(MaterialMovementKind.Scrap)),
                ("consumed", m.ConsumedBaseQty, consumed.GetValueOrDefault(m.Id)),
            })
            {
                if (counter != documented)
                    issues.Add(Issue($"order_material_{kind}", orderNumbers.GetValueOrDefault(m.OrderId, ""), m.OrderId, m.ProductId, null, documented, counter));
            }
        }

        // Each stock at a line location against the sum of its movements.
        var moved = (await db.StockMovements.AsNoTracking().Where(m => locations.Contains(m.BranchId))
                .Select(m => new { m.ProductId, m.BranchId, m.QtyDelta }).ToListAsync(ct))
            .GroupBy(m => (m.ProductId, m.BranchId)).ToDictionary(g => g.Key, g => g.Sum(m => m.QtyDelta));
        foreach (var s in stocks)
        {
            var sum = moved.GetValueOrDefault((s.ProductId, s.BranchId));
            if (sum != s.StockQty)
                issues.Add(Issue("stock_balance_mismatch", "", null, s.ProductId, s.BranchId, sum, s.StockQty));
        }

        return new MaterialFlowReportDto(stockMoves.Count, materials.Count, stocks.Count, issues.Take(MaxRows).ToList());
    }

    private static decimal? Money(decimal? value, int places) =>
        value is decimal v ? Math.Round(v, places, MidpointRounding.AwayFromZero) : null;
}
