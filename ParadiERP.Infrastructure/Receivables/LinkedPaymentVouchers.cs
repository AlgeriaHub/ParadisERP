using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Receivables;

/// <summary>
/// The payment voucher every payroll payout carries (owner, 2026-10-02, answer 24: "any amount that
/// leaves the company needs a payment voucher, even the salaries — so I can count the money").
///
/// <para>
/// An advance, a wage payment and a statutory payment each stay the document that moves the cash
/// and posts the ledger; this adds the numbered voucher beside it, already paid, pointing back at
/// it. It moves no cash and posts nothing — the source document did both — so the voucher list is
/// the whole of what went out without anything being counted twice. Written inside the caller's
/// write scope, so it is numbered on the same series as every other voucher and lives or dies with
/// the document it records.
/// </para>
/// </summary>
public static class LinkedPaymentVouchers
{
    /// <summary>The categories a payroll voucher is filed under; never an expense category.</summary>
    public const string Advance = "advance";
    public const string Wages = "wages";
    public const string Statutory = "statutory";

    public static async Task<PaymentVoucher> AddPaidAsync(
        PosDbContext db, Guid branchId, decimal amount, PayoutMethod method, string category,
        string? payee, string purpose, string sourceType, Guid sourceId, string? by, DateTime paidAtUtc,
        CancellationToken ct)
    {
        var voucher = new PaymentVoucher
        {
            BranchId = branchId,
            Kind = PaymentVoucherKind.Payroll,
            MethodKey = await MethodKeyAsync(db, method, ct),
            ExpenseCategory = category,
            PayeeName = Trim(payee, 160),
            Amount = amount,
            Purpose = Trim(purpose, 300),
            Status = PaymentVoucherStatus.Paid,
            RequestedBy = Trim(by, 100),
            // The payroll screens are their own approval: the run was approved, the advance granted.
            ApprovedByName = Trim(by, 120),
            ApprovedAtUtc = paidAtUtc,
            PaidAtUtc = paidAtUtc,
            PaidByName = Trim(by, 120),
            SourceType = sourceType,
            SourceId = sourceId
        };
        // The same series and the same rule as PaymentVouchers.CreateAsync: counted inside the
        // write scope the caller holds, with the unique index as the backstop.
        voucher.Number = $"PV-{await db.PaymentVouchers.CountAsync(ct) + 1:D6}";
        db.PaymentVouchers.Add(voucher);
        return voucher;
    }

    /// <summary>
    /// The source document was reversed, so its voucher no longer records money that left. Marked
    /// cancelled with the reason — the one way a paid voucher is ever cancelled, because the payout
    /// it recorded has itself been undone and the cash put back.
    /// </summary>
    public static async Task CancelForAsync(
        PosDbContext db, string sourceType, Guid sourceId, string? reason, CancellationToken ct)
    {
        var vouchers = await db.PaymentVouchers
            .Where(v => v.SourceType == sourceType && v.SourceId == sourceId && v.Status != PaymentVoucherStatus.Cancelled)
            .ToListAsync(ct);
        foreach (var voucher in vouchers)
        {
            voucher.Status = PaymentVoucherStatus.Cancelled;
            voucher.CancelledAtUtc = DateTime.UtcNow;
            voucher.StatusNote = Trim(reason, 300);
        }
    }

    /// <summary>The tender a payout went out by: the company's cash method, or "bank".</summary>
    private static async Task<string> MethodKeyAsync(PosDbContext db, PayoutMethod method, CancellationToken ct)
    {
        if (method == PayoutMethod.Bank) return "bank";
        return await db.PaymentMethodDefs.AsNoTracking()
            .Where(m => m.Behavior == PaymentBehavior.Cash && m.IsActive && !m.IsDeleted)
            .OrderBy(m => m.SortOrder).Select(m => m.Key).FirstOrDefaultAsync(ct) ?? "cash";
    }

    private static string? Trim(string? value, int max)
    {
        var text = (value ?? "").Trim();
        return text.Length == 0 ? null : text.Length <= max ? text : text[..max];
    }
}
