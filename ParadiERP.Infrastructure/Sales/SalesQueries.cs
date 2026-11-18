using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Sales;

/// <summary>
/// The sales reads behind <c>GET /api/sales</c>, <c>GET /api/sales/{id}</c>,
/// <c>GET /api/reports/summary</c> and the receipt re-send, separate from their endpoints the
/// way LedgerReports is separate from LedgerEndpoints, so the branch rule can be tested on the
/// TillHarness without an HTTP host.
///
/// <para>
/// Every method takes <paramref name="scope"/>: <c>null</c> is "every branch" (a manager or
/// admin), a value confines the read to that branch (a cashier — BranchScope). The scope is
/// applied <b>after</b> the caller's optional filters, so a cashier's own <c>branchId</c>
/// parameter is overridden, not honoured, and a lookup by id outside the scope answers "not
/// found" rather than "forbidden": a 403 would tell the caller the sale exists (§2.1).
/// </para>
/// <para>
/// Projected to flat shapes rather than returned as entities: EF fixup populates the inverse
/// SaleLine.Sale navigation even under AsNoTracking, and serialising that graph would throw on
/// the reference cycle mid-response. Money is summed in memory after <c>ToListAsync</c>, like
/// every other total here — SQLite stores decimal as TEXT.
/// </para>
/// </summary>
public static class SalesQueries
{
    /// <summary>Hard cap on one list call, whatever <c>take</c> asks for.</summary>
    public const int MaxTake = 1000;

    /// <summary>
    /// Longest window one summary will load. Every other list endpoint caps how much it loads
    /// in one call; a date-range report had no such cap, so a wide range over a year or two of
    /// history could load the entire matching sale+line set into memory. 92 days covers any
    /// real reporting need (a quarter) without one.
    /// </summary>
    public const int MaxSummaryDays = 92;

    public static async Task<IReadOnlyList<SaleRowDto>> ListAsync(
        PosDbContext db, DateTime? from, DateTime? to, string? deviceCode, Guid? branchId, int? take,
        Guid? scope, CancellationToken ct)
    {
        var query = db.Sales.AsNoTracking();
        if (from is not null) query = query.Where(s => s.SoldAtUtc >= from);
        if (to is not null) query = query.Where(s => s.SoldAtUtc < to);
        if (!string.IsNullOrWhiteSpace(deviceCode)) query = query.Where(s => s.DeviceCode == deviceCode);
        if (branchId is not null) query = query.Where(s => s.BranchId == branchId);
        if (scope is Guid own) query = query.Where(s => s.BranchId == own);

        return await query
            .OrderByDescending(s => s.SoldAtUtc)
            .Take(Math.Clamp(take ?? 200, 1, MaxTake))
            .Select(s => new SaleRowDto(
                s.Id, s.Reference, s.DeviceCode, s.BranchId, s.SoldAtUtc, s.ReceivedAtUtc,
                s.CashierName, s.CustomerId, s.SubTotalHt, s.TaxTotal, s.DiscountTotal,
                s.GrandTotalTtc, s.RoundingAdjustment, s.PaidAmount, s.ChangeAmount,
                s.PaymentMethod.ToString(), s.Status.ToString(), s.StockWarning, s.Lines.Count))
            .ToListAsync(ct);
    }

    public static Task<SaleDetailDto?> FindAsync(PosDbContext db, Guid id, Guid? scope, CancellationToken ct) =>
        db.Sales.AsNoTracking()
            .Where(s => s.Id == id && (scope == null || s.BranchId == scope))
            .Select(s => new SaleDetailDto(
                s.Id, s.Reference, s.DeviceCode, s.BranchId, s.LocalSeq, s.SoldAtUtc, s.ReceivedAtUtc,
                s.CustomerId, s.CashierUserId, s.CashierName,
                s.SubTotalHt, s.TaxTotal, s.DiscountTotal, s.GrandTotalTtc, s.RoundingAdjustment,
                s.PaidAmount, s.ChangeAmount,
                s.PaymentMethod.ToString(), s.Status.ToString(),
                s.RefundOfSaleId, s.Note, s.StockWarning,
                s.Lines.Select(l => new SaleLineDto(
                    l.Id, l.ProductId, l.Sku, l.NameSnapshot, l.Qty, l.UnitPrice,
                    l.TaxRate, l.LineDiscount, l.LineTotalTtc, l.LineTaxAmount)).ToList(),
                s.Payments.Select(p => new SalePaymentDto(p.Id, p.Method.ToString(), p.Amount, p.Reference)).ToList()))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The sale as the receipt renderer wants it — lines and payments loaded — under the same
    /// rule as <see cref="FindAsync"/>: outside the scope it does not exist.
    /// </summary>
    public static Task<Sale?> ForReceiptAsync(PosDbContext db, Guid id, Guid? scope, CancellationToken ct) =>
        db.Sales.AsNoTracking().Include(s => s.Lines).Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == id && (scope == null || s.BranchId == scope), ct);

    /// <summary>
    /// Z-report style summary with the tax breakdown by rate a shop needs for its declaration,
    /// rounded to the currency's decimals. Refunds carry positive totals with Status = Refunded,
    /// so every figure subtracts them rather than counting them as extra revenue.
    ///
    /// <para>
    /// A scoped caller's <paramref name="branchId"/> is replaced by the scope before anything
    /// is read, and the per-branch breakdown — which names the other branches — is only built
    /// when no branch was asked for, so a cashier's Z-report never names another branch.
    /// </para>
    /// </summary>
    public static async Task<SalesSummaryDto> SummaryAsync(
        PosDbContext db, DateTime fromUtc, DateTime toUtc, Guid? branchId, Guid? scope,
        RegionalSettings money, CancellationToken ct)
    {
        if (scope is Guid own) branchId = own;

        var salesQuery = db.Sales.AsNoTracking()
            .Include(s => s.Lines)
            .Include(s => s.Payments)
            .Where(s => s.SoldAtUtc >= fromUtc && s.SoldAtUtc < toUtc);
        if (branchId is not null) salesQuery = salesQuery.Where(s => s.BranchId == branchId);
        var sales = await salesQuery.ToListAsync(ct);

        static int Sign(Sale s) => s.Status == SaleStatus.Refunded ? -1 : 1;

        var signedLines = sales
            .SelectMany(s => s.Lines.Select(l => (Line: l, Sign: Sign(s))))
            .ToList();

        var taxBuckets = signedLines
            .GroupBy(x => x.Line.TaxRate)
            .Select(g => new TaxBucketDto(
                g.Key,
                money.RoundMoney(g.Sum(x => x.Sign * (x.Line.LineTotalTtc - x.Line.LineTaxAmount))),
                money.RoundMoney(g.Sum(x => x.Sign * x.Line.LineTaxAmount))))
            .OrderByDescending(t => t.Rate)
            .ToList();

        var topProducts = signedLines
            .GroupBy(x => new { x.Line.ProductId, x.Line.NameSnapshot })
            .Select(g => new TopProductDto(
                g.Key.ProductId, g.Key.NameSnapshot,
                g.Sum(x => x.Sign * x.Line.Qty),
                money.RoundMoney(g.Sum(x => x.Sign * x.Line.LineTotalTtc))))
            .OrderByDescending(p => p.Revenue)
            .Take(20)
            .ToList();

        var refunds = sales.Where(s => s.Status == SaleStatus.Refunded).ToList();

        var branchBreakdown = new List<BranchSummaryDto>();
        if (branchId is null && sales.Count > 0)
        {
            var branchNames = await db.Branches.AsNoTracking()
                .Where(b => sales.Select(s => s.BranchId).Distinct().Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);
            branchBreakdown = sales
                .GroupBy(s => s.BranchId)
                .Select(g => new BranchSummaryDto(
                    g.Key,
                    branchNames.GetValueOrDefault(g.Key, "—"),
                    g.Count(s => s.Status != SaleStatus.Refunded),
                    money.RoundMoney(g.Sum(s => Sign(s) * s.GrandTotalTtc))))
                .OrderByDescending(b => b.GrossTtc)
                .ToList();
        }

        return new SalesSummaryDto(
            fromUtc, toUtc,
            sales.Count - refunds.Count,
            refunds.Count,
            money.RoundMoney(sales.Sum(s => Sign(s) * s.GrandTotalTtc)),
            money.RoundMoney(sales.Sum(s => Sign(s) * s.SubTotalHt)),
            money.RoundMoney(sales.Sum(s => Sign(s) * s.TaxTotal)),
            money.RoundMoney(sales.Sum(s => Sign(s) * s.DiscountTotal)),
            money.RoundMoney(refunds.Sum(s => s.GrandTotalTtc)),
            taxBuckets,
            topProducts,
            branchBreakdown,
            money.RoundMoney(sales.Sum(s => Sign(s) * s.RoundingAdjustment)),
            money.RoundMoney(sales.Sum(s => Sign(s) * s.Payments.Where(p => p.Method == PaymentMethod.GiftCard).Sum(p => p.Amount))),
            money.RoundMoney(sales.Sum(s => Sign(s) * s.Payments.Where(p => p.Method == PaymentMethod.Loyalty).Sum(p => p.Amount))));
    }

    /// <summary>
    /// The widest period one missing-cost report reads: a fiscal year and a margin, as the ledger
    /// reports allow. It projects a few columns per line rather than loading sales whole, which is
    /// why it can go further back than the summary's quarter.
    /// </summary>
    public const int MaxMissingCostDays = 400;

    /// <summary>
    /// Sales whose margin is incomplete because their goods had no cost (SAL-02), by product,
    /// for the days <paramref name="from"/> to <paramref name="to"/> inclusive on the company's
    /// clock (<paramref name="zone"/>). Branch-scoped like <see cref="SummaryAsync"/>: a scoped
    /// caller reads their own branch whatever they ask. Refunds subtract, as everywhere.
    /// Amounts are before tax, from the lines (what the posting credits to sales).
    /// </summary>
    public static async Task<MissingCostReportDto> MissingCostAsync(
        PosDbContext db, DateOnly from, DateOnly to, Guid? branchId, Guid? scope,
        TimeZoneInfo zone, RegionalSettings money, CancellationToken ct)
    {
        if (scope is Guid own) branchId = own;
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone);
        var toUtc = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);

        var lines = db.SaleLines.AsNoTracking()
            .Where(l => l.Sale!.SoldAtUtc >= fromUtc && l.Sale.SoldAtUtc < toUtc);
        if (branchId is not null) lines = lines.Where(l => l.Sale!.BranchId == branchId);

        var all = await lines
            .Select(l => new { l.LineTotalTtc, l.LineTaxAmount, Refund = l.Sale!.Status == SaleStatus.Refunded })
            .ToListAsync(ct);
        var uncosted = await lines.Where(l => l.CostMissing)
            .Select(l => new
            {
                l.SaleId, l.ProductId, l.Sku, l.NameSnapshot, l.Qty, l.LineTotalTtc, l.LineTaxAmount,
                Refund = l.Sale!.Status == SaleStatus.Refunded, l.Sale.SoldAtUtc
            })
            .ToListAsync(ct);

        var productIds = uncosted.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn, p.CostPrice })
            .ToDictionaryAsync(p => p.Id, ct);
        // Compared in memory: SQLite keeps decimals as TEXT, where "0" and "0.0" are not equal.
        var withoutCost = (await db.Products.AsNoTracking()
                .Where(p => !p.IsDeleted && p.IsActive && p.TrackStock)
                .Select(p => p.CostPrice)
                .ToListAsync(ct))
            .Count(c => c == 0);

        var rows = uncosted
            .GroupBy(l => l.ProductId)
            .Select(g =>
            {
                var product = products.GetValueOrDefault(g.Key);
                var last = g.OrderByDescending(l => l.SoldAtUtc).First();
                var qty = g.Sum(l => (l.Refund ? -1 : 1) * l.Qty);
                var costNow = product?.CostPrice ?? 0m;
                return new MissingCostRowDto(
                    g.Key, product?.Sku ?? last.Sku,
                    product?.NameAr ?? "", product?.NameFr ?? last.NameSnapshot, product?.NameEn ?? "",
                    qty,
                    money.RoundMoney(g.Sum(l => (l.Refund ? -1 : 1) * (l.LineTotalTtc - l.LineTaxAmount))),
                    g.Where(l => !l.Refund).Select(l => l.SaleId).Distinct().Count(),
                    last.SoldAtUtc,
                    costNow,
                    costNow > 0 ? money.RoundMoney(qty * costNow) : null);
            })
            .OrderByDescending(r => r.RevenueHt)
            .ToList();

        return new MissingCostReportDto(
            from, to,
            money.RoundMoney(all.Sum(l => (l.Refund ? -1 : 1) * (l.LineTotalTtc - l.LineTaxAmount))),
            money.RoundMoney(uncosted.Sum(l => (l.Refund ? -1 : 1) * (l.LineTotalTtc - l.LineTaxAmount))),
            uncosted.Where(l => !l.Refund).Select(l => l.SaleId).Distinct().Count(),
            withoutCost,
            rows);
    }
}
