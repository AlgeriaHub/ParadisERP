using Microsoft.EntityFrameworkCore;
using AlgeriaPos.Domain;

namespace AlgeriaPos.Infrastructure.Persistence;

/// <summary>
/// What a cash box holds: every movement in, less every movement out.
///
/// <para>
/// Summed here, in memory, because SQLite stores decimal as TEXT and a server-side Sum there adds
/// strings. It was written out six times — the push, the treasury, wages, receipts, payment
/// vouchers — which is six places for the next fix to be missed.
/// </para>
/// <para>
/// A write records the box's "balance after" from this figure, so it reads it under the version
/// lock (<see cref="ForWriteAsync"/>): two writers that summed at the same moment both recorded
/// the same balance after, and a remittance's "not more than the box holds" check could pass twice
/// against one count. A screen or report reads <see cref="OfAsync"/>, which takes no lock.
/// </para>
/// </summary>
public static class CashBoxBalance
{
    /// <summary>The balance a movement about to be written starts from. Needs a transaction.</summary>
    public static async Task<decimal> ForWriteAsync(PosDbContext db, Guid cashBoxId, CancellationToken ct)
    {
        await db.LockVersionsAsync(ct);
        return await OfAsync(db, cashBoxId, ct);
    }

    /// <summary>The balance as committed, for reading only.</summary>
    public static async Task<decimal> OfAsync(PosDbContext db, Guid cashBoxId, CancellationToken ct)
    {
        var rows = await db.CashMovements.AsNoTracking()
            .Where(m => m.CashBoxId == cashBoxId)
            .Select(m => new { m.Direction, m.Amount })
            .ToListAsync(ct);
        return rows.Sum(m => m.Direction == CashDirection.In ? m.Amount : -m.Amount);
    }
}
