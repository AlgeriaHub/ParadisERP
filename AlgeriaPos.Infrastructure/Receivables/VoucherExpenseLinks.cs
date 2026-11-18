using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Receivables;

/// <summary>
/// The A0 cap (costing plan F9, made one rule by maintenance plan §2.4): the expenses linked to one payment voucher, on
/// production orders and on maintenance jobs together, never exceed the voucher's amount. Both writers read this under
/// the version lock before adding a link, so an order and a job cannot both take the voucher's last part.
/// Summed in memory: SQLite stores decimal as text.
/// </summary>
public static class VoucherExpenseLinks
{
    /// <summary>What production orders' and maintenance jobs' expenses already link to <paramref name="voucherId"/>.</summary>
    public static async Task<decimal> LinkedAsync(PosDbContext db, Guid voucherId, CancellationToken ct) =>
        (await LinkedAsync(db, [voucherId], ct)).GetValueOrDefault(voucherId);

    /// <summary>The same, for several vouchers at once; a voucher nothing links to is absent.</summary>
    public static async Task<Dictionary<Guid, decimal>> LinkedAsync(PosDbContext db, IReadOnlyCollection<Guid> voucherIds, CancellationToken ct)
    {
        if (voucherIds.Count == 0) return new Dictionary<Guid, decimal>();
        var production = await db.ProductionOrderExpenses.AsNoTracking()
            .Where(e => e.PaymentVoucherId != null && voucherIds.Contains(e.PaymentVoucherId.Value))
            .Select(e => new { Voucher = e.PaymentVoucherId!.Value, e.Amount }).ToListAsync(ct);
        var maintenance = await db.MaintenanceJobExpenses.AsNoTracking()
            .Where(e => e.PaymentVoucherId != null && voucherIds.Contains(e.PaymentVoucherId.Value))
            .Select(e => new { Voucher = e.PaymentVoucherId!.Value, e.Amount }).ToListAsync(ct);
        return production.Concat(maintenance).GroupBy(x => x.Voucher).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
    }
}
