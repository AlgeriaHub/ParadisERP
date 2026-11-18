using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Ledger;

/// <summary>
/// How sales are costed (C-01), switched by the accountant in the panel (owner, 2026-11-09: every accounting decision is a
/// setting; the default keeps today's behaviour). Off — the product's cost price — until someone holding
/// accounting.manage switches it on, as a deliberate act:
/// <list type="number">
/// <item>The page shows the figure first: stock at the shadow's average (in transit included) against the balance of the
/// stock accounts (310, 318 and any stock slot of the account map), and the difference.</item>
/// <item>Switching on states that figure. The server recomputes it under the version lock — the lock every sale takes —
/// and refuses if it moved, if the <c>opening_adjustment</c> slot (C-02) names no active account, or if the books are
/// closed today. Otherwise it posts the difference once, Dr/Cr 310 against that account (per location when there is one
/// average per location, else at the main branch), records the switch and turns it on, in one transaction.</item>
/// <item>From then on every sale is costed at the average of the moment and every refund under the refund setting
/// (<see cref="LiveSaleCosting"/>, in the sync).</item>
/// <item>A negative-stock gap settled by a later inflow while it is on (C-16) is posted by the accountant from the same
/// page, one entry per covering movement, Dr/Cr the <c>valuation_difference</c> slot's account against 310 — or, under the
/// automatic setting (<see cref="StockValuationSettings.GapPosting"/>), by the inflow's own transaction
/// (<see cref="ValuationGapsOnInflow"/>), the same entry. While that slot is unmapped the gaps are listed and nothing is
/// guessed.</item>
/// <item>Switching back off posts nothing: sales return to the cost price from that moment. Switching on again posts the
/// difference of that moment, which also takes in any gap left unposted from before.</item>
/// </list>
/// </summary>
public sealed class SalesCostSwitch(PosDbContext db, StockValuationShadow shadow, LedgerPoster poster)
{
    public const string InvalidMethodCode = "invalid_method";
    public const string UnchangedCode = "sales_cost_unchanged";
    public const string OpeningAdjustmentUnmappedCode = "opening_adjustment_unmapped";
    public const string AdjustmentChangedCode = "adjustment_changed";
    public const string ValuationDifferenceUnmappedCode = "valuation_difference_unmapped";
    public const string NoGapsCode = "no_gaps";
    public const string BooksClosedCode = "books_closed";

    /// <summary>The account map's stock slots: goods held under them are in the shadow's pools too.</summary>
    public static readonly IReadOnlyList<string> StockSlots =
    [
        ManufacturingAccountSlots.RawMaterialStock, ManufacturingAccountSlots.SuppliesStock, ManufacturingAccountSlots.SemiFinishedStock,
        ManufacturingAccountSlots.FinishedGoodsStock, ManufacturingAccountSlots.ScrapStock,
    ];

    /// <summary>The outcome of a write: the new state, or a refusal with its status, code and words.</summary>
    public sealed record Outcome(SalesCostingDto? Value, int Status, string? Code, string? Error)
    {
        public static Outcome Ok(SalesCostingDto value) => new(value, 200, null, null);
        public static Outcome Refused(int status, string code, string error, SalesCostingDto? value = null) => new(value, status, code, error);
    }

    private sealed record Figures(
        StockValuationSettings? Settings, Guid MainBranchId, IReadOnlyDictionary<Guid, decimal> DifferenceByBranch,
        decimal ShadowValue, decimal BookValue, decimal Adjustment, IReadOnlyList<string> StockCodes,
        ValuationAccountDto OpeningAdjustment, LedgerAccount? OpeningAdjustmentAccount,
        ValuationAccountDto ValuationDifference, LedgerAccount? ValuationDifferenceAccount,
        IReadOnlyList<ValuationGapDto> PendingGaps);

    /// <summary>How sales are costed now, and what switching would post. Writes nothing.</summary>
    public async Task<SalesCostingDto> StatusAsync(CancellationToken ct = default) => await ToDtoAsync(await FiguresAsync(ct), ct);

    public async Task<Outcome> SwitchAsync(SwitchSalesCostRequest r, string? by, CancellationToken ct = default)
    {
        if (!Enum.TryParse<SalesCostMethod>(r.Method, true, out var method) || !Enum.IsDefined(method))
            return Outcome.Refused(400, InvalidMethodCode, $"The method is one of: {string.Join(", ", Enum.GetNames<SalesCostMethod>())}.");

        // A database made without start-up has no opening yet: take it, so there is an average to switch to.
        await StockValuationStart.EnsureAsync(db, ct);
        try
        {
            await using var tx = await db.BeginWriteScopeAsync(ct);
            // The lock every sale takes before it reads: no sale lands between the figure and the switch.
            await db.LockVersionsAsync(ct);
            var row = await db.StockValuationSettings.FirstAsync(ct);
            if (row.SalesCost == method)
                return Outcome.Refused(409, UnchangedCode, method == SalesCostMethod.WeightedAverage
                    ? "Sales are already costed at the weighted average."
                    : "Sales are already costed at the cost price.");

            var f = await FiguresAsync(ct);
            var now = DateTime.UtcNow;
            var change = new StockValuationSwitch
            {
                Method = method, AtUtc = now, By = by,
                Scope = row.Scope, RefundCost = row.RefundCost, UnitCostDecimals = row.UnitCostDecimals,
                ShadowValue = f.ShadowValue, BookValue = f.BookValue,
            };

            if (method == SalesCostMethod.WeightedAverage)
            {
                if (f.OpeningAdjustmentAccount is not LedgerAccount account)
                    return Outcome.Refused(409, OpeningAdjustmentUnmappedCode,
                        "Name the account for the opening adjustment (C-02) in Manufacturing › Costing settings first: the difference at the switch is posted to it, and it is never guessed.");
                if (r.ExpectedAdjustment is not decimal expected || Math.Abs(expected - f.Adjustment) > 0.0005m)
                    return Outcome.Refused(409, AdjustmentChangedCode,
                        "The opening adjustment has changed since it was shown: check the new figure and confirm again.",
                        await ToDtoAsync(f, ct));

                var draft = new PostingDraft
                {
                    EntryDate = now,
                    BranchId = f.MainBranchId,
                    SourceType = LedgerSources.ValuationSwitch,
                    SourceId = change.Id,
                    Description = "Sales costed at the weighted average: opening adjustment (C-02)",
                    CreatedBy = by,
                };
                foreach (var (branch, difference) in f.DifferenceByBranch.Where(x => x.Value != 0))
                {
                    draft.Add(PostingLine.Signed(LedgerAccounts.Inventory, difference, branch, "Stock at the weighted average"));
                    draft.Add(PostingLine.Signed(account.Code, -difference, branch, "Opening adjustment (C-02)"));
                }
                change.Adjustment = f.Adjustment;
                change.LedgerEntryId = (await poster.PostAsync(draft, ct))?.Id;
            }

            db.StockValuationSwitches.Add(change);
            row.SalesCost = method;
            row.SalesCostChangedAtUtc = now;
            row.SalesCostChangedBy = by;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (PeriodClosedException closed)
        {
            db.ChangeTracker.Clear();
            return Outcome.Refused(409, BooksClosedCode, closed.Message);
        }
        db.ChangeTracker.Clear();
        return Outcome.Ok(await StatusAsync(ct));
    }

    /// <summary>
    /// Posts every negative-stock gap settled since sales were last switched to the average (and while they were), one
    /// entry per covering movement, idempotent on that movement. A gap dated in closed books is booked on the first open day.
    /// </summary>
    public async Task<Outcome> PostGapsAsync(string? by, CancellationToken ct = default)
    {
        try
        {
            await using var tx = await db.BeginWriteScopeAsync(ct);
            await db.LockVersionsAsync(ct);
            var f = await FiguresAsync(ct);
            if (f.ValuationDifferenceAccount is not LedgerAccount account)
                return Outcome.Refused(409, ValuationDifferenceUnmappedCode,
                    "Name the account for valuation differences (C-16) in Manufacturing › Costing settings first: the gaps are listed until then, never guessed.");
            if (f.PendingGaps.Count == 0)
                return Outcome.Refused(409, NoGapsCode, "No negative-stock gap is waiting to post.");

            foreach (var gap in f.PendingGaps)
            {
                var draft = GapDraft(gap.MovementId, gap.AtUtc, gap.BranchId, gap.Sku, gap.DocumentNumber, gap.Amount, account.Code, by, automatic: false);
                await poster.PostAsync(await BookClosings.OntoOpenBooksAsync(db, draft, ct), ct);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (PeriodClosedException closed)
        {
            db.ChangeTracker.Clear();
            return Outcome.Refused(409, BooksClosedCode, closed.Message);
        }
        db.ChangeTracker.Clear();
        return Outcome.Ok(await StatusAsync(ct));
    }

    /// <summary>
    /// One covered gap's entry, the same whoever posts it — the accountant from the page, or the inflow itself under the
    /// automatic setting (<see cref="ValuationGapsOnInflow"/>): Dr/Cr the valuation-difference account against 310 for
    /// the gap (positive: the goods turned out dearer), dated at the covering movement, its id the movement's.
    /// </summary>
    internal static PostingDraft GapDraft(
        Guid movementId, DateTime atUtc, Guid branchId, string? sku, string? documentNumber, decimal amount, string accountCode,
        string? by, bool automatic)
    {
        var what = string.Join(' ', new[] { "Negative-stock gap", sku, documentNumber }.Where(x => !string.IsNullOrEmpty(x)));
        var draft = new PostingDraft
        {
            EntryDate = atUtc,
            BranchId = branchId,
            SourceType = LedgerSources.ValuationGap,
            SourceId = movementId,
            Description = what + (automatic ? " (C-16, posted with the inflow)" : " (C-16)"),
            CreatedBy = by,
        };
        draft.Add(PostingLine.Signed(accountCode, amount, memo: "Valuation difference (C-16)"));
        draft.Add(PostingLine.Signed(LedgerAccounts.Inventory, -amount, memo: "Stock at the weighted average"));
        return draft;
    }

    /// <summary>
    /// The window gaps are posted in: after the latest switch to the average and up to the switch back that followed it
    /// (null while still on). Null when sales were never switched on. Anything before that switch was taken in by its
    /// opening adjustment.
    /// </summary>
    internal static async Task<(DateTime On, DateTime? Off)?> GapWindowAsync(PosDbContext db, CancellationToken ct)
    {
        var switches = await db.StockValuationSwitches.AsNoTracking().OrderBy(s => s.AtUtc).ToListAsync(ct);
        var on = switches.LastOrDefault(s => s.Method == SalesCostMethod.WeightedAverage);
        if (on is null) return null;
        return (on.AtUtc, switches.FirstOrDefault(s => s.AtUtc > on.AtUtc && s.Method != SalesCostMethod.WeightedAverage)?.AtUtc);
    }

    /// <summary>Whether a settled gap is one to post: in the window, and not nothing at the ledger's scale.</summary>
    internal static bool InWindow(ShadowGap g, (DateTime On, DateTime? Off) window) =>
        g.AtUtc > window.On && (window.Off is null || g.AtUtc <= window.Off) && Money(g.Gap) != 0;

    private async Task<Figures> FiguresAsync(CancellationToken ct)
    {
        var mainBranchId = await db.Branches.AsNoTracking().OrderByDescending(b => b.IsMain).ThenBy(b => b.Id)
            .Select(b => b.Id).FirstOrDefaultAsync(ct);

        // The account map: the two slots the switch posts to, and the stock slots whose balance is stock too.
        var map = await db.ManufacturingAccountMappings.AsNoTracking().Where(m => m.LedgerAccountId != null)
            .ToDictionaryAsync(m => m.Slot, m => m.LedgerAccountId!.Value, ct);
        var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        LedgerAccount? Slot(string slot) =>
            map.TryGetValue(slot, out var id) && accounts.TryGetValue(id, out var a) && a.IsActive ? a : null;
        ValuationAccountDto Dto(string slot, LedgerAccount? a) => new(slot, a is not null, a?.Code, a?.NameAr, a?.NameFr, a?.NameEn);
        var opening = Slot(ManufacturingAccountSlots.OpeningAdjustment);
        var difference = Slot(ManufacturingAccountSlots.ValuationDifference);

        var stockIds = StockSlots.Where(map.ContainsKey).Select(s => map[s]).ToHashSet();
        // REN-12 (Phase M7a): once the fixed-asset register holds the hire equipment, its cost left 310 for the asset account
        // while the shadow still counts the goods: the asset account is stock here too, or the switch would post it back.
        if (FixedAssets.FixedAssetData.Active(await FixedAssets.FixedAssetData.CurrentAsync(db, ct))
            && map.TryGetValue(FixedAssetAccountSlots.Asset, out var assetAccount))
            stockIds.Add(assetAccount);
        var stock = accounts.Values
            .Where(a => a.Code == LedgerAccounts.Inventory || a.Code == LedgerAccounts.ScrapInventory || stockIds.Contains(a.Id))
            .ToList();
        var stockAccountIds = stock.Select(a => a.Id).ToList();
        // Summed here, like every ledger total (SQLite cannot sum decimals).
        var bookLines = await db.LedgerEntryLines.AsNoTracking().Where(l => stockAccountIds.Contains(l.AccountId))
            .Select(l => new { l.BranchId, l.Debit, l.Credit }).ToListAsync(ct);
        var bookTotal = bookLines.Sum(l => l.Debit - l.Credit);

        var live = await shadow.LiveFiguresAsync(ct);
        var settings = live?.Settings ?? await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var shadowTotal = live is { } l0 ? l0.ValueByBranch.Values.Sum() + l0.InTransit : 0m;

        // Per location when there is one average per location (goods in transit belong to none: the main branch's
        // line carries them); otherwise the stock is one figure for the company, at the main branch.
        var byBranch = new Dictionary<Guid, decimal>();
        void Add(Guid branch, decimal amount) => byBranch[branch] = byBranch.GetValueOrDefault(branch) + amount;
        if (settings?.Scope == StockValuationScope.Location && live is { } l1)
        {
            foreach (var (branch, value) in l1.ValueByBranch) Add(branch, value);
            Add(mainBranchId, l1.InTransit);
            foreach (var line in bookLines) Add(line.BranchId, -(line.Debit - line.Credit));
        }
        else
        {
            Add(mainBranchId, shadowTotal - bookTotal);
        }
        var rounded = byBranch.ToDictionary(x => x.Key, x => Money(x.Value));

        return new Figures(settings, mainBranchId, rounded, Money(shadowTotal), Money(bookTotal), rounded.Values.Sum(),
            stock.Select(a => a.Code).Order(StringComparer.Ordinal).ToList(),
            Dto(ManufacturingAccountSlots.OpeningAdjustment, opening), opening,
            Dto(ManufacturingAccountSlots.ValuationDifference, difference), difference,
            live is { } l2 ? await PendingGapsAsync(l2.Gaps, ct) : []);
    }

    /// <summary>
    /// The gaps settled after the latest switch to the average and before the switch back (if any), not yet posted.
    /// Anything before that switch was taken in by its opening adjustment.
    /// </summary>
    private async Task<List<ValuationGapDto>> PendingGapsAsync(IReadOnlyList<ShadowGap> gaps, CancellationToken ct)
    {
        if (await GapWindowAsync(db, ct) is not { } span) return [];
        var window = gaps.Where(g => InWindow(g, span)).ToList();
        if (window.Count == 0) return [];

        var ids = window.Select(g => g.MovementId).ToList();
        var posted = (await db.LedgerEntries.AsNoTracking()
            .Where(e => e.SourceType == LedgerSources.ValuationGap && ids.Contains(e.SourceId) && !e.IsReversal)
            .Select(e => e.SourceId).ToListAsync(ct)).ToHashSet();
        var productIds = window.Select(g => g.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn }).ToDictionaryAsync(p => p.Id, ct);
        return window.Where(g => !posted.Contains(g.MovementId)).Select(g =>
        {
            var p = products.GetValueOrDefault(g.ProductId);
            return new ValuationGapDto(g.MovementId, g.AtUtc, g.ProductId, p?.Sku ?? "", p?.NameAr ?? "", p?.NameFr ?? "", p?.NameEn ?? "",
                g.BranchId, g.Kind.ToString(), g.DocumentNumber, Money(g.Gap));
        }).ToList();
    }

    private async Task<SalesCostingDto> ToDtoAsync(Figures f, CancellationToken ct)
    {
        var fallbackLines = await db.SaleLines.AsNoTracking().CountAsync(l => l.CostBasis == SaleCostBasis.CostPriceFallback, ct);
        // The other stock documents' rows (C-01 for every document): only they set a movement's basis.
        var fallbackMovements = await db.StockMovements.AsNoTracking().CountAsync(m => m.CostBasis == SaleCostBasis.CostPriceFallback, ct);
        var history = await db.StockValuationSwitches.AsNoTracking().OrderByDescending(s => s.AtUtc).Take(20)
            .Select(s => new SalesCostSwitchDto(s.Id, s.Method.ToString(), s.AtUtc, s.By, s.ShadowValue, s.BookValue, s.Adjustment, s.LedgerEntryId))
            .ToListAsync(ct);
        return new SalesCostingDto(
            StockValuationShadow.ToDto(f.Settings), f.ShadowValue, f.BookValue, f.Adjustment, f.StockCodes,
            f.OpeningAdjustment, f.ValuationDifference, f.PendingGaps, f.PendingGaps.Sum(g => g.Amount), fallbackLines, history,
            fallbackMovements);
    }

    internal static decimal Money(decimal v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
}
