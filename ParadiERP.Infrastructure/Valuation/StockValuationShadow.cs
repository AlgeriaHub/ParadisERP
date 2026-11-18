using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Valuation;

/// <summary>
/// The weighted-average shadow as a report (P0.4b): loads the opening and the movements since it, runs
/// <see cref="WeightedAverageShadow"/> and shapes the figures. Every read is no-tracking; the only write
/// is the settings row, through <see cref="UpdateSettingsAsync"/>. Nothing here writes a cost price, a
/// sale, a movement or the ledger — an architecture test holds it to that.
/// </summary>
public sealed class StockValuationShadow(PosDbContext db)
{
    public const int MaxDecimals = 6;
    public const string SalesCostLocked =
        "Sales are costed at the weighted average: switch them back to the cost price before changing these choices; switching on again posts the difference the change makes.";

    public async Task<StockValuationSettingsDto> SettingsAsync(CancellationToken ct = default) =>
        ToDto(await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct));

    /// <returns>The saved settings, or an error message for the caller to answer 400 with.</returns>
    public async Task<(StockValuationSettingsDto? Saved, string? Error)> UpdateSettingsAsync(
        UpdateStockValuationSettingsRequest r, string? by, CancellationToken ct = default)
    {
        if (!Enum.TryParse<StockValuationScope>(r.Scope, true, out var scope) || !Enum.IsDefined(scope))
            return (null, $"The scope is one of: {string.Join(", ", Enum.GetNames<StockValuationScope>())}.");
        if (!Enum.TryParse<RefundValuation>(r.RefundCost, true, out var refund) || !Enum.IsDefined(refund))
            return (null, $"The refund cost is one of: {string.Join(", ", Enum.GetNames<RefundValuation>())}.");
        if (r.UnitCostDecimals is < 0 or > MaxDecimals)
            return (null, $"The decimals of a unit cost are between 0 and {MaxDecimals}.");
        ReceiptCostPriceRule? receipt = null;
        if (r.ReceiptCostRule is not null)
        {
            if (!Enum.TryParse<ReceiptCostPriceRule>(r.ReceiptCostRule, true, out var parsed) || !Enum.IsDefined(parsed))
                return (null, $"The cost price on receipt is one of: {string.Join(", ", Enum.GetNames<ReceiptCostPriceRule>())}.");
            receipt = parsed;
        }
        if (!Optional<GapPostingMode>(r.GapPosting, out var gapPosting))
            return (null, $"The posting of negative-stock gaps is one of: {string.Join(", ", Enum.GetNames<GapPostingMode>())}.");
        if (!Optional<CostPriceRevaluationMode>(r.CostPriceRevaluation, out var revaluation))
            return (null, $"The revaluation on a cost price change is one of: {string.Join(", ", Enum.GetNames<CostPriceRevaluationMode>())}.");
        if (!Optional<ZeroCostReceiptValue>(r.ZeroCostReceipt, out var zeroCost))
            return (null, $"A receipt at cost 0 is valued at one of: {string.Join(", ", Enum.GetNames<ZeroCostReceiptValue>())}.");

        // Saved before the server ever opened the shadow (a database made without start-up): open it now.
        await StockValuationStart.EnsureAsync(db, ct);
        await using var tx = await db.BeginWriteScopeAsync(ct);
        var row = await db.StockValuationSettings.FirstAsync(ct);
        // While sales post the average, the replay is what the stock accounts were booked from: a changed choice
        // would revalue every movement since the opening behind entries already posted. Switched back first, a
        // change is followed by a new switch, which posts the difference it makes (C-02).
        if (row.SalesCost == SalesCostMethod.WeightedAverage
            && (row.Scope != scope || row.RefundCost != refund || row.UnitCostDecimals != r.UnitCostDecimals))
            return (null, SalesCostLocked);
        // A revaluation is posted to this account and is never guessed: the choice waits until the account is named.
        if (revaluation == CostPriceRevaluationMode.StockOnHand && row.CostPriceRevaluation != CostPriceRevaluationMode.StockOnHand
            && await SlotAccountAsync(db, ManufacturingAccountSlots.ValuationDifference, ct) is null)
            return (null, RevaluationUnmapped);
        row.Scope = scope;
        row.RefundCost = refund;
        row.UnitCostDecimals = r.UnitCostDecimals;
        // Not locked while sales post the average: it changes no movement the replay reads (B-F01).
        if (receipt is ReceiptCostPriceRule rule) row.ReceiptCostRule = rule;
        // The three below are not locked either: none changes a movement the replay reads, only what a later inflow, cost
        // price change or receipt does (2026-11-09).
        if (gapPosting is GapPostingMode gaps) row.GapPosting = gaps;
        if (revaluation is CostPriceRevaluationMode revalue) row.CostPriceRevaluation = revalue;
        if (zeroCost is ZeroCostReceiptValue zero) row.ZeroCostReceipt = zero;
        row.UpdatedAtUtc = DateTime.UtcNow;
        row.UpdatedBy = by;
        // P0.3: not locked by the sales-cost switch — it changes nothing already posted, only what the next direct
        // invoice does. Its own change is stamped, so the accountant can say since when invoices move stock.
        if (r.DirectInvoiceStock is bool directStock && directStock != row.DirectInvoiceStock)
        {
            row.DirectInvoiceStock = directStock;
            row.DirectInvoiceStockChangedAtUtc = row.UpdatedAtUtc;
            row.DirectInvoiceStockChangedBy = by;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (ToDto(row), null);
    }

    public const string RevaluationUnmapped =
        "Name the account for valuation differences in Manufacturing › Costing settings first: a revaluation of the stock on hand is posted to it, and it is never guessed.";

    /// <summary>A choice the request may leave out (null: keep the saved one); false when it names no member.</summary>
    private static bool Optional<T>(string? sent, out T? value) where T : struct, Enum
    {
        value = null;
        if (sent is null) return true;
        if (!Enum.TryParse<T>(sent, true, out var parsed) || !Enum.IsDefined(parsed)) return false;
        value = parsed;
        return true;
    }

    /// <summary>The active account a slot of the account map names (Manufacturing › Costing settings); null while it names none.</summary>
    public static async Task<LedgerAccount?> SlotAccountAsync(PosDbContext db, string slot, CancellationToken ct = default)
    {
        var id = await db.ManufacturingAccountMappings.AsNoTracking()
            .Where(m => m.Slot == slot && m.LedgerAccountId != null).Select(m => m.LedgerAccountId).FirstOrDefaultAsync(ct);
        return id is null ? null : await db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.IsActive, ct);
    }

    /// <param name="branchId">Under the per-location scope, one location's rows; ignored under the company scope.</param>
    public async Task<StockValuationReportDto> ReportAsync(Guid? branchId, CancellationToken ct = default)
    {
        var (settings, result, products, branches) = await RunAsync(null, ct);
        var rows = result.Pools
            .Where(p => settings?.Scope != StockValuationScope.Location || branchId is null || p.BranchId == branchId)
            .Select(p => Row(p, products, branches))
            .Where(r => r is not null).Select(r => r!)
            .OrderBy(r => r.Sku, StringComparer.Ordinal).ThenBy(r => r.BranchNameFr, StringComparer.Ordinal)
            .ToList();
        var inTransit = branchId is null ? result.InTransitValue.Values.Sum() : 0m;
        var totals = new StockValuationTotalsDto(
            rows.Sum(r => r.OpeningValue), rows.Sum(r => r.InValue), rows.Sum(r => r.OutValue),
            rows.Sum(r => r.ClosingValue), rows.Sum(r => r.ReferenceValue), rows.Sum(r => r.Difference),
            rows.Sum(r => r.NegativeStockGap), inTransit, rows.Sum(r => r.PostedCostOfSales), rows.Sum(r => r.ShadowCostOfSales));
        return new StockValuationReportDto(ToDto(settings), DateTime.UtcNow, rows, totals);
    }

    /// <param name="branchId">Under the per-location scope, one location's places and movements only (a branch user's).</param>
    /// <returns>Null when the product does not exist.</returns>
    public async Task<StockValuationProductDto?> ProductAsync(Guid productId, Guid? branchId = null, CancellationToken ct = default)
    {
        if (!await db.Products.AsNoTracking().AnyAsync(p => p.Id == productId, ct)) return null;
        var (settings, result, products, branches) = await RunAsync(productId, ct);
        var byLocation = settings?.Scope == StockValuationScope.Location;
        var places = result.Pools.Where(p => p.ProductId == productId && (!byLocation || branchId is null || p.BranchId == branchId))
            .Select(p => Row(p, products, branches)).Where(r => r is not null).Select(r => r!)
            .OrderBy(r => r.BranchNameFr, StringComparer.Ordinal).ToList();
        var summary = places.Count switch
        {
            0 => null,
            1 => places[0],
            _ => Sum(places),
        };
        var lines = result.Lines.Where(l => !byLocation || branchId is null || l.Movement.BranchId == branchId).Select(l =>
        {
            branches.TryGetValue(l.Movement.BranchId, out var b);
            return new StockValuationLineDto(
                l.Movement.Id, l.Movement.AtUtc, l.Movement.Kind.ToString(), l.Movement.BranchId,
                b?.NameAr, b?.NameFr, b?.NameEn, l.Movement.DocumentNumber,
                l.QtyIn, l.QtyOut, l.Movement.UnitCost, l.ShadowUnitCost, l.ShadowValue,
                l.PoolQtyAfter, l.AverageAfter, l.Gap, l.Notes);
        }).ToList();
        return new StockValuationProductDto(ToDto(settings), summary, places, lines);
    }

    /// <summary>
    /// The weighted average per unit of each of <paramref name="productIds"/> for the company as a whole, after every
    /// movement written so far (a receipt saved in the caller's transaction included), with the shadow's own loader,
    /// rules and settings — the figure the report shows. One average for the company: that pool's. One per location:
    /// the locations' value over their quantity, as the product dialog's summary row (in transit left out, as there).
    /// Read only, unrounded. A product is absent when the company holds none of it (or less than none), or when the
    /// shadow was never opened: the caller decides what that means (B-F01: the cost price on receipt).
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> CompanyAveragesAsync(
        IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        var averages = new Dictionary<Guid, decimal>();
        if (productIds.Count == 0) return averages;
        var settings = await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null) return averages;
        var openings = await LoadOpeningsAsync(db, productIds, ct);
        var (movements, originalLineCost) = await LoadMovementsAsync(db, productIds, settings.StartedAtUtc, ct);
        var result = WeightedAverageShadow.Run(PolicyOf(settings), openings, movements, originalLineCost);
        foreach (var product in result.Pools.GroupBy(p => p.ProductId))
        {
            var qty = product.Sum(p => p.Qty);
            if (qty > 0) averages[product.Key] = product.Sum(p => p.Value) / qty;
        }
        return averages;
    }

    private sealed record ProductInfo(string Sku, string NameAr, string NameFr, string NameEn, decimal CostPrice);
    private sealed record BranchInfo(string NameAr, string NameFr, string NameEn);

    private async Task<(StockValuationSettings?, ShadowResult, Dictionary<Guid, ProductInfo>, Dictionary<Guid, BranchInfo>)> RunAsync(
        Guid? productId, CancellationToken ct)
    {
        var settings = await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var products = await db.Products.AsNoTracking()
            .Where(p => productId == null || p.Id == productId)
            .Select(p => new { p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn, p.CostPrice })
            .ToDictionaryAsync(p => p.Id, p => new ProductInfo(p.Sku, p.NameAr, p.NameFr, p.NameEn, p.CostPrice), ct);
        var branches = await db.Branches.AsNoTracking()
            .Select(b => new { b.Id, b.NameAr, b.NameFr, b.NameEn })
            .ToDictionaryAsync(b => b.Id, b => new BranchInfo(b.NameAr, b.NameFr, b.NameEn), ct);
        if (settings is null)
            return (null, new ShadowResult([], new Dictionary<Guid, decimal>(), []), products, branches);

        var only = productId is Guid one ? new[] { one } : null;
        var openings = await LoadOpeningsAsync(db, only, ct);
        var (movements, originalLineCost) = await LoadMovementsAsync(db, only, settings.StartedAtUtc, ct);
        return (settings, WeightedAverageShadow.Run(PolicyOf(settings), openings, movements, originalLineCost, productId), products, branches);
    }

    internal static ShadowPolicy PolicyOf(StockValuationSettings settings) =>
        new(settings.Scope, settings.RefundCost, settings.UnitCostDecimals);

    /// <summary>The opening rows of <paramref name="productIds"/> (every product when null).</summary>
    internal static Task<List<ShadowOpening>> LoadOpeningsAsync(PosDbContext db, IReadOnlyCollection<Guid>? productIds, CancellationToken ct) =>
        db.StockValuationOpenings.AsNoTracking()
            .Where(o => productIds == null || productIds.Contains(o.ProductId))
            .Select(o => new ShadowOpening(o.ProductId, o.BranchId, o.TransferId, o.Qty, o.UnitCost))
            .ToListAsync(ct);

    /// <summary>
    /// The movements of <paramref name="productIds"/> (every product when null) created after <paramref name="after"/>,
    /// with the links the replay needs resolved: a refund's sale, a production movement's order, a transfer step's
    /// transfer; and, for refunds of sales the replay will not see, the cost their lines posted. Shared by the report
    /// and the live sale costing (C-01), so both replay exactly the same rows.
    /// </summary>
    internal static async Task<(List<ShadowMovement> Movements, Dictionary<(Guid, Guid), decimal> OriginalLineCost)> LoadMovementsAsync(
        PosDbContext db, IReadOnlyCollection<Guid>? productIds, DateTime after, CancellationToken ct)
    {
        var raw = await db.StockMovements.AsNoTracking()
            .Where(m => m.CreatedAtUtc > after && (productIds == null || productIds.Contains(m.ProductId)))
            .Select(m => new
            {
                m.Id, m.CreatedAtUtc, m.ProductId, m.BranchId, m.Kind, m.QtyDelta, m.MovedQty, m.UnitCost,
                m.DocumentNumber, m.DocumentId, m.SaleId,
            })
            .ToListAsync(ct);

        // The links the replay needs: a refund's sale, a production movement's order, a transfer step's transfer.
        var refundSaleIds = raw.Where(m => m.Kind == StockMovementKind.Refund && m.SaleId != null).Select(m => m.SaleId!.Value).Distinct().ToList();
        var refundOf = refundSaleIds.Count == 0 ? new Dictionary<Guid, Guid>()
            : await db.Sales.AsNoTracking().Where(s => refundSaleIds.Contains(s.Id) && s.RefundOfSaleId != null)
                .Select(s => new { s.Id, Original = s.RefundOfSaleId!.Value }).ToDictionaryAsync(s => s.Id, s => s.Original, ct);
        var originalLineCost = await LineCostsAsync(db, refundOf.Values.Distinct().ToList(), ct);

        var production = new[] { StockMovementKind.ManufacturingIssue, StockMovementKind.ManufacturingReturn };
        var productionDocs = raw.Where(m => production.Contains(m.Kind) && m.DocumentId != null).Select(m => m.DocumentId!.Value).Distinct().ToList();
        var orderOf = productionDocs.Count == 0 ? new Dictionary<Guid, Guid>()
            : await db.MaterialMovements.AsNoTracking().Where(x => productionDocs.Contains(x.Id))
                .Select(x => new { x.Id, x.OrderId }).ToDictionaryAsync(x => x.Id, x => x.OrderId, ct);

        var transferKinds = new[] { StockMovementKind.TransferOut, StockMovementKind.TransferIn, StockMovementKind.TransferCancel, StockMovementKind.TransferLoss };
        var steps = raw.Where(m => transferKinds.Contains(m.Kind) && m.DocumentId != null).Select(m => m.DocumentId!.Value).Distinct().ToList();
        var transferOf = steps.Count == 0 ? new Dictionary<Guid, Guid>()
            : await db.StockTransferEvents.AsNoTracking().Where(e => steps.Contains(e.Id))
                .Select(e => new { e.Id, e.TransferId }).ToDictionaryAsync(e => e.Id, e => e.TransferId, ct);

        var movements = raw.Select(m => new ShadowMovement(
            m.Id, m.CreatedAtUtc, m.ProductId, m.BranchId, m.Kind, m.QtyDelta, m.MovedQty ?? Math.Abs(m.QtyDelta), m.UnitCost,
            m.DocumentNumber, m.DocumentId, m.SaleId,
            m.SaleId is Guid s && refundOf.TryGetValue(s, out var o) ? o : null,
            m.DocumentId is Guid d && orderOf.TryGetValue(d, out var order) ? order : null,
            m.DocumentId is Guid d2 && transferOf.TryGetValue(d2, out var t) ? t : null)).ToList();
        return (movements, originalLineCost);
    }

    /// <summary>The cost per unit each (sale, product) of <paramref name="saleIds"/> posted, weighted by quantity.</summary>
    internal static async Task<Dictionary<(Guid, Guid), decimal>> LineCostsAsync(PosDbContext db, IReadOnlyCollection<Guid> saleIds, CancellationToken ct)
    {
        var originalLineCost = new Dictionary<(Guid, Guid), decimal>();
        if (saleIds.Count == 0) return originalLineCost;
        var costed = await db.SaleLines.AsNoTracking()
            .Where(l => saleIds.Contains(l.SaleId) && l.UnitCost != null)
            .Select(l => new { l.SaleId, l.ProductId, l.Qty, Cost = l.UnitCost!.Value }).ToListAsync(ct);
        foreach (var g in costed.GroupBy(l => (l.SaleId, l.ProductId)))
        {
            var qty = g.Sum(l => l.Qty);
            originalLineCost[g.Key] = qty == 0 ? g.First().Cost : g.Sum(l => l.Qty * l.Cost) / qty;
        }
        return originalLineCost;
    }

    /// <summary>
    /// What the switch (C-01) weighs: the stock at the average per location (null: in transit, held at none; under
    /// the company scope everything is under null), and every negative-stock gap settled since the opening.
    /// Null when the shadow has not been opened.
    /// </summary>
    public async Task<(StockValuationSettings Settings, IReadOnlyDictionary<Guid, decimal> ValueByBranch, decimal InTransit, IReadOnlyList<ShadowGap> Gaps)?> LiveFiguresAsync(
        CancellationToken ct = default)
    {
        var settings = await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null) return null;
        var openings = await LoadOpeningsAsync(db, null, ct);
        var (movements, originalLineCost) = await LoadMovementsAsync(db, null, settings.StartedAtUtc, ct);
        var result = WeightedAverageShadow.Run(PolicyOf(settings), openings, movements, originalLineCost);
        var byBranch = result.Pools.Where(p => p.BranchId is not null)
            .GroupBy(p => p.BranchId!.Value).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
        var unplaced = result.Pools.Where(p => p.BranchId is null).Sum(p => p.Value) + result.InTransitValue.Values.Sum();
        return (settings, byBranch, unplaced, result.Gaps ?? []);
    }

    private static StockValuationRowDto? Row(ShadowPool p, Dictionary<Guid, ProductInfo> products, Dictionary<Guid, BranchInfo> branches)
    {
        if (!products.TryGetValue(p.ProductId, out var product)) return null;
        BranchInfo? branch = p.BranchId is Guid b && branches.TryGetValue(b, out var found) ? found : null;
        var referenceValue = p.Qty * product.CostPrice;
        return new StockValuationRowDto(
            p.ProductId, product.Sku, product.NameAr, product.NameFr, product.NameEn,
            p.BranchId, branch?.NameAr, branch?.NameFr, branch?.NameEn,
            p.OpeningQty, Money(p.OpeningValue), p.InQty, Money(p.InValue), p.OutQty, Money(p.OutValue),
            p.MovedInQty, Money(p.MovedInValue), p.MovedOutQty, Money(p.MovedOutValue),
            p.Qty, Money(p.Value), p.Average is decimal a ? Math.Round(a, 6, MidpointRounding.AwayFromZero) : null,
            product.CostPrice, Money(referenceValue), Money(p.Value - referenceValue),
            Money(p.NegativeStockGap), Money(p.PostedCostOfSales), Money(p.ShadowCostOfSales),
            p.OutputsAtReference, p.OutflowsWithoutAverage, p.Unclassified);
    }

    private static StockValuationRowDto Sum(IReadOnlyList<StockValuationRowDto> places)
    {
        var f = places[0];
        var qty = places.Sum(p => p.ClosingQty);
        var value = places.Sum(p => p.ClosingValue);
        return f with
        {
            BranchId = null, BranchNameAr = null, BranchNameFr = null, BranchNameEn = null,
            OpeningQty = places.Sum(p => p.OpeningQty), OpeningValue = places.Sum(p => p.OpeningValue),
            InQty = places.Sum(p => p.InQty), InValue = places.Sum(p => p.InValue),
            OutQty = places.Sum(p => p.OutQty), OutValue = places.Sum(p => p.OutValue),
            MovedInQty = places.Sum(p => p.MovedInQty), MovedInValue = places.Sum(p => p.MovedInValue),
            MovedOutQty = places.Sum(p => p.MovedOutQty), MovedOutValue = places.Sum(p => p.MovedOutValue),
            ClosingQty = qty, ClosingValue = value,
            AverageCost = qty > 0 ? Math.Round(value / qty, 6, MidpointRounding.AwayFromZero) : null,
            ReferenceValue = places.Sum(p => p.ReferenceValue), Difference = places.Sum(p => p.Difference),
            NegativeStockGap = places.Sum(p => p.NegativeStockGap),
            PostedCostOfSales = places.Sum(p => p.PostedCostOfSales), ShadowCostOfSales = places.Sum(p => p.ShadowCostOfSales),
            OutputsAtReference = places.Sum(p => p.OutputsAtReference),
            OutflowsWithoutAverage = places.Sum(p => p.OutflowsWithoutAverage), Unclassified = places.Sum(p => p.Unclassified),
        };
    }

    /// <summary>Money as the ledger keeps it (3 decimals); the replay itself never rounds a value but a unit cost.</summary>
    private static decimal Money(decimal v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    internal static StockValuationSettingsDto ToDto(StockValuationSettings? s) => s is null
        ? new(nameof(StockValuationScope.Company), nameof(RefundValuation.SaleCost), 3, null, null, null)
        : new(s.Scope.ToString(), s.RefundCost.ToString(), s.UnitCostDecimals, s.StartedAtUtc, s.UpdatedAtUtc, s.UpdatedBy,
            s.SalesCost.ToString(), s.SalesCostChangedAtUtc, s.SalesCostChangedBy,
            s.DirectInvoiceStock, s.DirectInvoiceStockChangedAtUtc, s.DirectInvoiceStockChangedBy, s.ReceiptCostRule.ToString(),
            s.GapPosting.ToString(), s.CostPriceRevaluation.ToString(), s.ZeroCostReceipt.ToString());
}
