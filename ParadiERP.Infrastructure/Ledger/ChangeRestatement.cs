using Microsoft.EntityFrameworkCore;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;

namespace ParadiERP.Infrastructure.Ledger;

/// <summary>
/// Puts right the cash every branch was recorded as holding, once, for the sales taken before
/// the till stopped netting change off twice.
///
/// <para>
/// <b>The defect.</b> The till sent its cash payment line already reduced by the change it had
/// handed back, and the server reduced it again. Every cash sale that gave change therefore
/// recorded the branch short by exactly that change — every day, on every such sale. The books
/// stayed balanced because the shortfall was parked in suspense, which is why a trial balance
/// showed a growing suspense figure rather than an error.
/// </para>
/// <para>
/// <b>Why this corrects rather than rewrites.</b> The movements and entries already recorded
/// are what the system believed at the time, and a ledger whose history can be edited is not
/// evidence of anything. So the shortfall is measured and posted as one dated correction per
/// branch — cash up, suspense down — exactly as a bookkeeper would put right a period that had
/// closed wrong. The old rows keep their running balances; the correction explains the step.
/// </para>
/// <para>
/// <b>Why it runs exactly once.</b> The entry carries a fixed <c>SourceId</c> per branch, and
/// <see cref="LedgerPoster"/> already refuses a second posting for a source it has seen. A
/// restart, a second deployment or a re-provisioned instance therefore cannot double it, and
/// there is no flag to forget to set.
/// </para>
/// </summary>
public static class ChangeRestatement
{
    /// <summary>
    /// Fixed so the correction is recognisable in an account statement, and so posting it twice
    /// is refused by the same guard that protects every other document.
    /// </summary>
    public const string Source = "correction.change-double-counted";

    /// <summary>
    /// Derives a stable id per branch: the same branch always yields the same id, so the
    /// correction is idempotent without storing a marker anywhere.
    /// </summary>
    private static Guid CorrectionIdFor(Guid branchId)
    {
        // Fold a fixed namespace into the branch id, so two branches never collide and the
        // same branch never changes its mind.
        Span<byte> bytes = stackalloc byte[16];
        branchId.TryWriteBytes(bytes);
        ReadOnlySpan<byte> salt = [0x9c, 0x4e, 0x11, 0x63, 0x2a, 0x7f, 0x48, 0xd1,
                                   0xb0, 0x55, 0x3e, 0x8a, 0x6d, 0x21, 0xc7, 0x09];
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= salt[i];
        return new Guid(bytes);
    }

    /// <summary>
    /// Measures the shortfall per branch and posts the corrections. Safe to call on every
    /// startup: a company with nothing to correct writes nothing.
    /// </summary>
    /// <returns>What was corrected, per branch, for the startup log.</returns>
    public static async Task<IReadOnlyList<(Guid BranchId, decimal Amount)>> ApplyAsync(
        PosDbContext db, LedgerPoster ledger, CancellationToken ct = default)
    {
        // Only completed sales that actually gave change and actually took cash. A refund gave
        // no change, and a sale settled entirely by card or on account never touched a drawer.
        var candidates = await db.Sales.AsNoTracking()
            .Where(s => s.ChangeAmount > 0 && s.Status == SaleStatus.Completed)
            .Select(s => new { s.Id, s.BranchId, s.ChangeAmount })
            .ToListAsync(ct);
        if (candidates.Count == 0) return [];

        var saleIds = candidates.Select(s => s.Id).ToList();
        var cashPaid = await db.SalePayments.AsNoTracking()
            .Where(p => saleIds.Contains(p.SaleId)
                        && p.Behavior == PaymentBehavior.Cash
                        && p.Status == PaymentStatus.Confirmed)
            .Select(p => new { p.SaleId, p.Amount })
            .ToListAsync(ct);

        // Decimals are summed here rather than in the query: SQLite stores them as TEXT, so a
        // server-side SUM would be comparing strings.
        var cashBySale = cashPaid
            .GroupBy(p => p.SaleId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

        var shortfallByBranch = new Dictionary<Guid, decimal>();
        foreach (var sale in candidates)
        {
            // A sale with change but no cash line cannot have been short-changed by this
            // defect; skip it rather than invent a correction for it.
            if (!cashBySale.ContainsKey(sale.Id)) continue;
            shortfallByBranch[sale.BranchId] =
                shortfallByBranch.GetValueOrDefault(sale.BranchId) + sale.ChangeAmount;
        }

        var corrected = new List<(Guid, decimal)>();
        foreach (var (branchId, amount) in shortfallByBranch)
        {
            if (amount <= 0) continue;

            var correctionId = CorrectionIdFor(branchId);
            var draft = new PostingDraft
            {
                EntryDate = DateTime.UtcNow,
                BranchId = branchId,
                SourceType = Source,
                SourceId = correctionId,
                Description = "Cash restated: change had been deducted twice on every cash sale"
            }
                .AddIfAny(PostingLine.Signed(LedgerAccounts.Cash, amount))
                .AddIfAny(PostingLine.Signed(LedgerAccounts.Suspense, -amount));

            // Returns null when this branch has already been corrected, which is what makes a
            // second startup a no-op rather than a second correction.
            if (await ledger.PostAsync(draft, ct) is null) continue;

            var box = await db.CashBoxes.FirstOrDefaultAsync(b => b.BranchId == branchId && b.IsActive, ct);
            if (box is not null)
            {
                var movements = await db.CashMovements.AsNoTracking()
                    .Where(m => m.CashBoxId == box.Id)
                    .Select(m => new { m.Direction, m.Amount })
                    .ToListAsync(ct);
                var balance = movements.Sum(m => m.Direction == CashDirection.In ? m.Amount : -m.Amount);

                db.CashMovements.Add(new CashMovement
                {
                    CashBoxId = box.Id,
                    Direction = CashDirection.In,
                    Amount = amount,
                    BalanceAfter = balance + amount,
                    // A counted difference, which is exactly what this is: the drawer always
                    // held this money and the record did not.
                    Kind = CashMovementKind.Adjustment,
                    SourceType = Source,
                    SourceId = correctionId,
                    Note = "Change had been deducted twice on every cash sale; cash restated."
                });
            }

            corrected.Add((branchId, amount));
        }

        if (corrected.Count > 0) await db.SaveChangesAsync(ct);
        return corrected;
    }
}
