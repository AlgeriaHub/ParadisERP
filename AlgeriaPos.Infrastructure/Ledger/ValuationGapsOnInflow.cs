using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgeriaPos.Infrastructure.Ledger;

/// <summary>
/// C-16 under the accountant's automatic setting (<see cref="StockValuationSettings.GapPosting"/> =
/// <see cref="GapPostingMode.Automatic"/>; owner, 2026-11-09: every accounting decision is a setting, the default — the
/// accountant posts them from the page — keeps today's behaviour). While sales are costed at the average, an inflow that
/// covers a negative quantity posts its gap in its own transaction: the same entry the page's button posts
/// (<see cref="SalesCostSwitch.GapDraft"/>, source <c>valuation-gap</c>, id the covering movement's), so it posts once and
/// the button never posts it again.
/// <para>
/// Which inflow covers, and by how much, is the replay's own rule — the one the page lists gaps by — not a second one: the
/// shadow is replayed for the inflow's products (its loader, engine and settings) and the gaps it settles on the movements
/// this save added are posted, in the same window the page uses (after the latest switch to the average). Every writer of
/// a stock movement goes through <see cref="PosDbContext.SaveChangesAsync(bool, CancellationToken)"/>, which calls this:
/// receiving, opening stock, adjustments, transfers in, rental returns, production, refunds, credit notes alike.
/// </para>
/// <para>
/// Never refuses the inflow. While the <c>valuation_difference</c> slot names no active account nothing posts and the gap
/// stays listed for the accountant; if the replay fails, the same, and it is logged. A gap dated in closed books is booked
/// on the first open day, as the button does.
/// </para>
/// </summary>
public static class ValuationGapsOnInflow
{
    /// <summary>
    /// Before a save: the inflow movements it adds, when gaps post automatically and sales are costed at the average;
    /// otherwise none. Reads the settings only when the save adds an inflow.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> CandidatesAsync(PosDbContext db, CancellationToken ct)
    {
        var inflows = db.ChangeTracker.Entries<StockMovement>()
            .Where(e => e.State == EntityState.Added && e.Entity.QtyDelta > 0)
            .Select(e => e.Entity.Id).ToList();
        if (inflows.Count == 0) return [];
        var settings = await db.StockValuationSettings.AsNoTracking()
            .Select(s => new { s.GapPosting, s.SalesCost }).FirstOrDefaultAsync(ct);
        return settings is { GapPosting: GapPostingMode.Automatic, SalesCost: SalesCostMethod.WeightedAverage } ? inflows : [];
    }

    /// <summary>
    /// After the save, in its transaction: adds the entry of each gap the <paramref name="movementIds"/> settle. The caller
    /// saves them. Never throws but on cancellation (or a ledger that refuses a balanced two-line entry, which is a bug).
    /// </summary>
    /// <returns>How many entries were added.</returns>
    public static async Task<int> PostAsync(PosDbContext db, IReadOnlyList<Guid> movementIds, ILogger logger, CancellationToken ct)
    {
        List<PostingDraft> drafts;
        try
        {
            drafts = await DraftsAsync(db, movementIds, logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "C-16: the gaps of {Count} inflow movement(s) could not be worked out; they stay listed for the accountant", movementIds.Count);
            return 0;
        }

        var poster = new LedgerPoster(db);
        var added = 0;
        foreach (var draft in drafts)
            if (await poster.PostAsync(await BookClosings.OntoOpenBooksAsync(db, draft, ct), ct) is not null) added++;
        return added;
    }

    private static async Task<List<PostingDraft>> DraftsAsync(
        PosDbContext db, IReadOnlyList<Guid> movementIds, ILogger logger, CancellationToken ct)
    {
        var settings = await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is not { GapPosting: GapPostingMode.Automatic, SalesCost: SalesCostMethod.WeightedAverage }) return [];
        if (await SalesCostSwitch.GapWindowAsync(db, ct) is not { } window) return [];

        var moved = await db.StockMovements.AsNoTracking().Where(m => movementIds.Contains(m.Id))
            .Select(m => new { m.Id, m.ProductId, m.UserName }).ToListAsync(ct);
        if (moved.Count == 0) return [];
        var productIds = moved.Select(m => m.ProductId).Distinct().ToList();

        // The page's rule, replayed for these products only: the same loader, engine and settings.
        var openings = await StockValuationShadow.LoadOpeningsAsync(db, productIds, ct);
        var (movements, lineCost) = await StockValuationShadow.LoadMovementsAsync(db, productIds, settings.StartedAtUtc, ct);
        var result = WeightedAverageShadow.Run(StockValuationShadow.PolicyOf(settings), openings, movements, lineCost);
        var ids = moved.ToDictionary(m => m.Id, m => m.UserName);
        var gaps = (result.Gaps ?? []).Where(g => ids.ContainsKey(g.MovementId) && SalesCostSwitch.InWindow(g, window)).ToList();
        if (gaps.Count == 0) return [];

        if (await StockValuationShadow.SlotAccountAsync(db, ManufacturingAccountSlots.ValuationDifference, ct) is not LedgerAccount account)
        {
            logger.LogWarning(
                "C-16: {Count} negative-stock gap(s) settled, but no account is named for valuation differences; they stay listed for the accountant",
                gaps.Count);
            return [];
        }

        var skus = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Sku, ct);
        return gaps.Select(g => SalesCostSwitch.GapDraft(
                g.MovementId, g.AtUtc, g.BranchId, skus.GetValueOrDefault(g.ProductId), g.DocumentNumber,
                SalesCostSwitch.Money(g.Gap), account.Code, ids[g.MovementId], automatic: true))
            .ToList();
    }
}
