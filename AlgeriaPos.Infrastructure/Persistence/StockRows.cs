using Microsoft.EntityFrameworkCore;
using AlgeriaPos.Domain;

namespace AlgeriaPos.Infrastructure.Persistence;

/// <summary>
/// The per-(product, branch) stock rows one operation touches, held for the length of that
/// operation.
///
/// <para>
/// Querying for a row each time looks equivalent and is not: a row added to the change tracker
/// but not yet saved is invisible to a fresh query. So two lines of one receiving note for the
/// same product at the same branch each fail to find the row the other just added, add a second
/// one, and the save fails on the unique index that the whole per-branch stock model rests on.
/// A note listing the same product twice is an ordinary thing for a warehouse clerk to do.
/// </para>
/// <para>
/// Hold one of these for the duration of a document and ask it for every row. It exists as a
/// shared type because the same trap was hit twice, in receiving and in rental issue notes,
/// and a second copy of the answer is a second place for it to be got wrong.
/// </para>
/// <para>
/// <b>Two writers at once.</b> A stock figure is read, changed in memory and written back. Two
/// tills pushing a sale of the same article each read 100 and each wrote 99 — on SQL Server, where
/// two transactions can interleave; SQLite lets one writer in at a time and never showed it. So
/// the first ask takes the version lock (<see cref="PosDbContext.LockVersionsAsync"/>) before
/// anything is read, and every row is read from the database under it, never from an earlier
/// tracked copy. The lock is held to the commit, which is why this only works inside a
/// transaction and refuses to run outside one.
/// </para>
/// </summary>
public sealed class StockRows(PosDbContext db)
{
    private readonly Dictionary<(Guid Product, Guid Branch), ProductStock> held = [];
    private bool locked;

    /// <summary>
    /// The row for this product at this branch, creating and tracking it on first ask.
    /// Repeated asks return the same instance, including one added moments ago.
    /// </summary>
    public async Task<ProductStock> GetAsync(Guid productId, Guid branchId, CancellationToken ct = default)
    {
        var stock = await FindAsync(productId, branchId, ct);
        if (stock is not null) return stock;

        stock = new ProductStock { ProductId = productId, BranchId = branchId };
        db.ProductStocks.Add(stock);
        held[(productId, branchId)] = stock;
        return stock;
    }

    /// <summary>The row for this product at this branch as it stands now, or null when there is none.</summary>
    public async Task<ProductStock?> FindAsync(Guid productId, Guid branchId, CancellationToken ct = default)
    {
        if (held.TryGetValue((productId, branchId), out var known)) return known;

        if (!locked)
        {
            await db.LockVersionsAsync(ct);
            locked = true;
        }

        // A copy the request loaded before the lock holds the figure from before it; a query would
        // hand that same instance back unchanged, so it is reloaded instead. One added and not yet
        // saved has nothing in the database to reload.
        var tracked = db.ProductStocks.Local.FirstOrDefault(s => s.ProductId == productId && s.BranchId == branchId);
        ProductStock? stock;
        if (tracked is null)
        {
            stock = await db.ProductStocks
                .FirstOrDefaultAsync(s => s.ProductId == productId && s.BranchId == branchId, ct);
        }
        else
        {
            if (db.Entry(tracked).State != EntityState.Added) await db.Entry(tracked).ReloadAsync(ct);
            stock = db.Entry(tracked).State == EntityState.Detached ? null : tracked;
        }

        if (stock is not null) held[(productId, branchId)] = stock;
        return stock;
    }
}
