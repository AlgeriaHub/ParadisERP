using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Catalog;

/// <summary>
/// Gives every location its three-digit code (<see cref="BranchCodes"/>; owner, 2026-10-05: "given
/// automatically to all locations").
///
/// <para>
/// <b>Inside every save, not only in the branches route.</b> <see cref="PosDbContext"/> calls
/// <see cref="AssignAsync"/> before it writes, so a location added anywhere — the branches route,
/// CompanySeeder's first branch and warehouse, DefaultBranches' repair at start, the demo seeder —
/// is numbered the same way and none can reach the table without a code. A code already on the row
/// is left alone: it is fixed for life, and the branches route sets nothing on an edit.
/// </para>
/// <para>
/// Several new locations in one save are numbered in a stable order — the main branch first, then
/// the main warehouse, then the rest as they were added — the order the BranchCodes migration gave
/// existing rows. Two saves at the same moment are kept apart by the write lock every catalog write
/// already takes (SubscriptionWriteFilter's <c>UPDATE SyncCounters</c>), with the unique index on
/// <c>Branches.Code</c> as the backstop.
/// </para>
/// </summary>
public static class BranchCodeAssignment
{
    /// <summary>The next free code, counting every location ever saved; null when every code is taken.</summary>
    public static async Task<string?> NextAsync(PosDbContext db, CancellationToken ct = default) =>
        BranchCodes.Next(await UsedAsync(db, ct));

    /// <summary>Numbers the locations this save adds without a code.</summary>
    /// <exception cref="InvalidOperationException">When the 900 codes are all taken.</exception>
    public static async Task AssignAsync(PosDbContext db, CancellationToken ct = default)
    {
        var unnumbered = Unnumbered(db);
        if (unnumbered.Count == 0) return;
        Number(unnumbered, await UsedAsync(db, ct));
    }

    /// <summary>The same, for the synchronous save.</summary>
    public static void Assign(PosDbContext db)
    {
        var unnumbered = Unnumbered(db);
        if (unnumbered.Count == 0) return;
        var used = db.Branches.AsNoTracking().Select(b => (string?)b.Code).ToList();
        used.AddRange(db.ChangeTracker.Entries<Branch>().Select(e => (string?)e.Entity.Code));
        Number(unnumbered, used);
    }

    private static void Number(List<Branch> unnumbered, List<string?> used)
    {
        foreach (var branch in unnumbered)
        {
            branch.Code = BranchCodes.Next(used) ?? throw new InvalidOperationException(BranchCodes.ExhaustedMessage);
            used.Add(branch.Code);
        }
    }

    private static List<Branch> Unnumbered(PosDbContext db) =>
        db.ChangeTracker.Entries<Branch>()
            .Where(e => e.State == EntityState.Added && string.IsNullOrWhiteSpace(e.Entity.Code))
            .Select((e, i) => (e.Entity, i))
            .OrderBy(x => x.Entity.IsMain && !x.Entity.IsWarehouse ? 0 : x.Entity.IsMain ? 1 : 2)
            .ThenBy(x => x.i)
            .Select(x => x.Entity)
            .ToList();

    /// <summary>Every code stored — deleted locations included — and every one already given in this save.</summary>
    private static async Task<List<string?>> UsedAsync(PosDbContext db, CancellationToken ct)
    {
        var used = await db.Branches.AsNoTracking().Select(b => (string?)b.Code).ToListAsync(ct);
        used.AddRange(db.ChangeTracker.Entries<Branch>().Select(e => (string?)e.Entity.Code));
        return used;
    }
}
