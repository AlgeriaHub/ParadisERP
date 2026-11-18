using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgeriaPos.Infrastructure.Ledger;

/// <summary>
/// B-F01, its open part, under the accountant's setting <see cref="StockValuationSettings.CostPriceRevaluation"/> (owner,
/// 2026-11-09: every accounting decision is a setting; the default — never revalued — keeps today's behaviour).
/// <para>
/// While sales are costed at the cost price, a change of a product's cost price — a receiving note under the last cost
/// rule, or someone typing it on the product card — revalues the stock on hand with one entry: the change × the quantity
/// held at each branch (available, on rent and in maintenance: the goods 310 carries; scrap sits on 318 at what it was
/// scrapped at and is left alone), Dr/Cr 310 against the <c>valuation_difference</c> slot's account, one pair of lines per
/// product and branch. Source <c>cost-price-revaluation</c>, its id the receiving note's (or a fresh one for the card).
/// </para>
/// <para>
/// On a receiving note "on hand" is what was held before the note: the note's own units are already on 310 at what it
/// states, by its own entry. A quantity below zero is revalued too, with its sign: it is goods sold at the old price and
/// not yet replaced, which 310 already carries below zero.
/// </para>
/// <para>
/// Under the weighted average (C-01) there is no cost-price valuation to revalue, and nothing posts; under the weighted
/// average receipt rule (B-F01) the note restates the cost price to the shadow's average, not to a price, and nothing posts
/// either. Never refuses the note or the save: an unmapped slot (the setting is saved only while it is mapped, but it can
/// be unmapped afterwards) or a failure is logged and nothing posts. Dated now; in closed books it would be booked on the
/// first open day (<see cref="BookClosings.OntoOpenBooksAsync"/>), as the other automatic postings are.
/// </para>
/// </summary>
public sealed class CostPriceRevaluationPosting(PosDbContext db, LedgerPoster poster, ILogger<CostPriceRevaluationPosting> logger)
{
    private sealed record Change(Guid ProductId, string Sku, decimal Before, decimal After);

    /// <summary>The cost price as the database holds it, read under the version lock: what a card's save changes it from.</summary>
    public async Task<decimal?> LockedCostPriceAsync(Guid productId, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null) await db.LockVersionsAsync(ct);
        return await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => (decimal?)p.CostPrice).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// After the product card's save, in its transaction: the revaluation of a cost price typed from
    /// <paramref name="before"/>, added for the caller to save.
    /// </summary>
    /// <returns>Whether an entry was added.</returns>
    public async Task<bool> ProductCardAsync(Product product, decimal? before, string? by, CancellationToken ct = default)
    {
        if (before is not decimal from || from == product.CostPrice) return false;
        var main = await db.Branches.AsNoTracking().OrderByDescending(b => b.IsMain).ThenBy(b => b.Id).Select(b => b.Id).FirstOrDefaultAsync(ct);
        return await PostAsync([new Change(product.Id, product.Sku, from, product.CostPrice)], Guid.NewGuid(), main,
            $"Cost price of {product.Sku} changed on the product card", null, by, ct);
    }

    /// <summary>
    /// After a receiving note's movements are saved, in its transaction: the revaluation of each product whose cost price
    /// the note changed, of what was held before the note. <paramref name="costsBefore"/>: each product's cost price
    /// before the note; <paramref name="received"/>: the note's lines, all at <paramref name="branchId"/>.
    /// </summary>
    /// <returns>Whether an entry was added.</returns>
    public async Task<bool> ReceivingAsync(
        Guid documentId, string? reference, Guid branchId, IReadOnlyDictionary<Guid, decimal> costsBefore,
        IEnumerable<Product> products, IEnumerable<(Guid ProductId, decimal Qty)> received, string? by, CancellationToken ct = default)
    {
        var changes = products.Distinct()
            .Where(p => costsBefore.TryGetValue(p.Id, out var before) && before != p.CostPrice)
            .Select(p => new Change(p.Id, p.Sku, costsBefore[p.Id], p.CostPrice)).ToList();
        if (changes.Count == 0) return false;
        var own = received.GroupBy(l => (l.ProductId, branchId)).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty));
        var what = string.IsNullOrWhiteSpace(reference) ? "Cost price changed by a receiving note" : $"Cost price changed by receiving {reference.Trim()}";
        return await PostAsync(changes, documentId, branchId, what, own, by, ct);
    }

    private async Task<bool> PostAsync(
        IReadOnlyList<Change> changes, Guid sourceId, Guid entryBranch, string what,
        IReadOnlyDictionary<(Guid, Guid), decimal>? notYetHeld, string? by, CancellationToken ct)
    {
        try
        {
            var settings = await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings is not { CostPriceRevaluation: CostPriceRevaluationMode.StockOnHand, SalesCost: SalesCostMethod.CostPrice })
                return false;
            if (await StockValuationShadow.SlotAccountAsync(db, ManufacturingAccountSlots.ValuationDifference, ct) is not LedgerAccount account)
            {
                logger.LogWarning(
                    "{What}: the stock on hand is to be revalued, but no account is named for valuation differences; nothing was posted", what);
                return false;
            }

            // REN-12 (Phase M7a): hire equipment the fixed-asset register holds is carried at its historical cost there, not
            // in 310 at the cost price: a changed cost price revalues none of it.
            var held = await FixedAssets.FixedAssetData.HeldAsync(db, changes.Select(c => c.ProductId).ToList(), ct);
            changes = changes.Where(c => !held.Contains(c.ProductId)).ToList();
            if (changes.Count == 0) return false;
            var ids = changes.Select(c => c.ProductId).ToList();
            // Added here, not in the query (SQLite stores decimals as text).
            var onHand = (await db.ProductStocks.AsNoTracking().Where(s => ids.Contains(s.ProductId))
                    .Select(s => new { s.ProductId, s.BranchId, s.StockQty, s.OnRentQty, s.MaintenanceQty })
                    .ToListAsync(ct))
                .Select(s => new { s.ProductId, s.BranchId, Qty = s.StockQty + s.OnRentQty + s.MaintenanceQty })
                .ToList();

            var draft = new PostingDraft
            {
                EntryDate = DateTime.UtcNow,
                BranchId = entryBranch,
                SourceType = LedgerSources.CostPriceRevaluation,
                SourceId = sourceId,
                Description = $"{what}: stock on hand revalued ({string.Join(", ", changes.Select(c => $"{c.Sku} {c.Before:0.###} → {c.After:0.###}"))})",
                CreatedBy = by,
            };
            foreach (var change in changes)
                foreach (var row in onHand.Where(h => h.ProductId == change.ProductId).OrderBy(h => h.BranchId))
                {
                    var qty = row.Qty - (notYetHeld?.GetValueOrDefault((row.ProductId, row.BranchId)) ?? 0m);
                    var amount = Math.Round((change.After - change.Before) * qty, 3, MidpointRounding.AwayFromZero);
                    if (amount == 0) continue;
                    draft.Add(PostingLine.Signed(LedgerAccounts.Inventory, amount, row.BranchId, $"{change.Sku}: {qty:0.###} revalued"));
                    draft.Add(PostingLine.Signed(account.Code, -amount, row.BranchId, $"{change.Sku}: cost price revaluation"));
                }
            return await poster.PostAsync(await BookClosings.OntoOpenBooksAsync(db, draft, ct), ct) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{What}: the stock on hand could not be revalued; nothing was posted", what);
            return false;
        }
    }
}
