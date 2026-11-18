using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Rentals;

/// <summary>
/// When a rental contract closes (owner, 2026-10-02, answers 15 and 22): not when the goods are all
/// back, but when they are back <b>and</b> the contract is paid. The customer may pay at signing,
/// later or in instalments; a cheque counts once it is confirmed collected, never while it is
/// pending; and the Admin may close a contract short, with the reason written down.
///
/// <para>
/// <b>What the contract is worth</b> is what its own papers put on the customer's account: every
/// line on 411 the issue notes and the return notes posted, tax included, less what an early return
/// gave back. Read off the ledger, so it is the figure the statement shows and nothing re-derives it.
/// </para>
/// <para>
/// <b>What has been paid</b> is the confirmed receipts that name the contract, plus the part of any
/// other confirmed receipt allocated to an invoice over one of its notes, less the refunds paid out
/// to the customer on vouchers that name it.
/// </para>
/// </summary>
public static class RentalClosure
{
    /// <summary>Half a centime: a figure rounded on both sides never misses by more.</summary>
    private const decimal Tolerance = 0.005m;

    public sealed record Standing(decimal Owed, decimal Paid)
    {
        public decimal Remaining => Math.Max(0m, Owed - Paid);
        public bool IsPaid => Paid + Tolerance >= Owed;
    }

    /// <summary>What the contract is worth and what has come in against it, as of now.</summary>
    public static async Task<Standing> StandingAsync(PosDbContext db, Guid contractId, CancellationToken ct = default)
    {
        var issueIds = await db.IssueNotes.AsNoTracking().Where(n => n.ContractId == contractId).Select(n => n.Id).ToListAsync(ct);
        var returnIds = await db.ReturnNotes.AsNoTracking().Where(n => n.ContractId == contractId).Select(n => n.Id).ToListAsync(ct);
        var noteIds = issueIds.Concat(returnIds).ToList();

        var customersAccount = await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.Code == LedgerAccounts.Customers).Select(a => a.Id).FirstOrDefaultAsync(ct);
        // Summed here, not in the database: SQLite stores decimal as TEXT.
        var owedLines = await db.LedgerEntryLines.AsNoTracking()
            .Where(l => l.AccountId == customersAccount)
            .Join(db.LedgerEntries.AsNoTracking()
                    .Where(e => (e.SourceType == LedgerSources.RentalIssue || e.SourceType == LedgerSources.RentalReturn
                                || e.SourceType == LedgerSources.RentalIssueDailyCorrection
                                || e.SourceType == LedgerSources.RentalReturnDailyCorrection)
                                && noteIds.Contains(e.SourceId)),
                l => l.EntryId, e => e.Id, (l, e) => new { l.Debit, l.Credit })
            .ToListAsync(ct);
        var owed = owedLines.Sum(l => l.Debit - l.Credit);

        var named = await db.ReceiptVouchers.AsNoTracking()
            .Where(r => r.ContractId == contractId && r.Status == PaymentStatus.Confirmed)
            .Select(r => r.Amount).ToListAsync(ct);
        // SAL-13: what of a receipt naming the contract the company kept as other income pays nothing of it. None on the
        // default setting.
        var kept = await db.CustomerAdvanceApplications.AsNoTracking()
            .Where(a => a.Kind == AdvanceMoveKind.Kept)
            .Join(db.ReceiptVouchers.AsNoTracking().Where(r => r.ContractId == contractId && r.Status == PaymentStatus.Confirmed),
                a => a.ReceiptVoucherId, r => r.Id, (a, r) => a.Amount)
            .ToListAsync(ct);

        // A confirmed receipt that names no contract still pays this one where it was allocated to
        // an invoice over one of its notes.
        var allocated = await db.ReceiptAllocations.AsNoTracking()
            .Join(db.ReceiptVouchers.AsNoTracking().Where(r => r.ContractId == null && r.Status == PaymentStatus.Confirmed),
                a => a.ReceiptVoucherId, r => r.Id, (a, r) => a)
            .Join(db.Invoices.AsNoTracking().Where(i => i.SourceId != null && noteIds.Contains(i.SourceId!.Value)),
                a => a.InvoiceId, i => i.Id, (a, i) => a.Amount)
            .ToListAsync(ct);

        var refunded = await db.PaymentVouchers.AsNoTracking()
            .Where(v => v.ContractId == contractId && v.Kind == PaymentVoucherKind.CustomerRefund
                        && v.Status == PaymentVoucherStatus.Paid)
            .Select(v => v.Amount).ToListAsync(ct);

        return new Standing(owed, named.Sum() - kept.Sum() + allocated.Sum() - refunded.Sum());
    }

    /// <summary>
    /// Closes the contract when its goods are all back and it is paid; a contract already closed, or
    /// with goods still out, is left as it is. The caller saves. Answers whether it closed now.
    /// </summary>
    public static async Task<bool> TryCloseAsync(PosDbContext db, Guid contractId, CancellationToken ct = default)
    {
        var contract = await db.RentalContracts.FirstOrDefaultAsync(c => c.Id == contractId, ct);
        // Open only: a cancelled contract is never closed by a receipt that names it.
        if (contract is null || contract.Status != RentalContractStatus.Open || contract.GoodsBackAtUtc is null) return false;
        if (!(await StandingAsync(db, contractId, ct)).IsPaid) return false;
        contract.Status = RentalContractStatus.Closed;
        contract.ClosedAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>Every contract a set of receipts or vouchers touches, closed where it now can be.</summary>
    public static async Task TryCloseAllAsync(PosDbContext db, IEnumerable<Guid> contractIds, CancellationToken ct = default)
    {
        foreach (var id in contractIds.Distinct()) await TryCloseAsync(db, id, ct);
    }
}
