using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;

namespace AlgeriaPos.Infrastructure.FixedAssets;

/// <summary>One stock event of a hire product as the register sees it: which pool, how many units, and the document behind it.</summary>
public sealed record FixedAssetEvent(
    Guid ProductId, Guid BranchId, decimal Qty, string? Sku, Guid? DocumentId, string? DocumentNumber, string? Reason, string? By,
    string? Reference = null);

/// <summary>
/// The register's side of the stock documents (REN-12, Phase M7a). While the treatment is <c>FixedAssets</c> and the
/// switch-over is posted, a document moving a hire product (<see cref="Product.IsRentable"/>) asks here instead of posting
/// 310: each call records the register event (<see cref="FixedAssetMovement"/>) and returns the entry it posts, valued
/// from the pool — cost and accumulated depreciation pro rata, at their net book value. Off (the default), or for any other
/// product, <see cref="HoldsAsync"/> answers false and the document posts exactly as it always has.
/// <para>
/// One instance per unit of work: the pools a document touches are replayed once from their stored events (and any added in
/// this unit of work), then kept in memory, so a second line of the same document sees what the first took out. The caller
/// posts the entry with its own <see cref="LedgerPoster"/>, in its own transaction, and saves.
/// </para>
/// <para>
/// Never refuses: a document is never blocked by the register. Units the pool does not hold leave at nothing.
/// </para>
/// </summary>
public sealed class FixedAssetBook(PosDbContext db)
{
    private sealed class State
    {
        public required FixedAssetSettings Settings { get; init; }
        public required FixedAssetPolicy Policy { get; init; }
        public required TimeZoneInfo Zone { get; init; }
        public required string Asset { get; init; }
        public required string AccumulatedDepreciation { get; init; }
        public required string Disposal { get; init; }
        public Dictionary<Guid, bool> Hire { get; } = [];
        public Dictionary<Guid, FixedAssetEngine> Engines { get; } = [];
        public Dictionary<Guid, DateTime> Last { get; } = [];
    }

    private bool loaded;
    private State? state;

    /// <summary>Whether the register holds this product now: active, its required slots mapped, and a hire product.</summary>
    public async Task<bool> HoldsAsync(Guid productId, CancellationToken ct = default)
    {
        if (await StateAsync(ct) is not State s) return false;
        if (!s.Hire.TryGetValue(productId, out var hire))
            s.Hire[productId] = hire = (await FixedAssetData.HireAsync(db, [productId], ct)).Contains(productId);
        return hire;
    }

    /// <summary>
    /// A receiving note's line of a hire product: the units join the pool at their cost, depreciated from the month after
    /// (docs/accounting/fixed-assets.md, "Purchases after the switch-over"). Returns the account the note's entry debits
    /// instead of 310. Call only when <see cref="HoldsAsync"/> said so.
    /// </summary>
    public async Task<string> AcquireAsync(FixedAssetEvent e, decimal cost, CancellationToken ct = default)
    {
        var (s, engine, at, month) = await PrepareAsync(e.ProductId, ct);
        Record(engine, month, Row(e, at, FixedAssetMovementKind.Acquisition, FixedAssetEngine.Money(cost), 0m, month.AddMonths(1)));
        return s.Asset;
    }

    /// <summary>
    /// Units leaving the company — lost on rent, written off, counted missing, lost in transit: out of the pool at their net
    /// book value, which goes to the disposal account. Dr accumulated depreciation, Dr disposal, Cr asset.
    /// </summary>
    public async Task<PostingDraft?> DisposeAsync(FixedAssetEvent e, CancellationToken ct = default)
    {
        var (s, engine, at, month) = await PrepareAsync(e.ProductId, ct);
        var (cost, depreciation) = engine.Share(e.ProductId, e.BranchId, e.Qty);
        var row = Record(engine, month, Row(e, at, FixedAssetMovementKind.Disposal, cost, depreciation, null));
        return Posted(row, Draft(row, e, $"Fixed asset disposed of: {e.Sku} × {e.Qty:0.###}")
            .AddIfAny(PostingLine.Dr(s.AccumulatedDepreciation, depreciation, memo: e.Sku))
            .AddIfAny(PostingLine.Dr(s.Disposal, cost - depreciation, memo: e.Sku))
            .AddIfAny(PostingLine.Cr(s.Asset, cost, memo: e.Sku)));
    }

    /// <summary>
    /// Units wrecked into scrap: out of the pool at their net book value, into scrap (318) at that value — as today they move
    /// from 310 to 318 at cost, with no loss until the scrap leaves. Dr accumulated depreciation, Dr 318, Cr asset.
    /// </summary>
    public async Task<PostingDraft?> ScrapAsync(FixedAssetEvent e, CancellationToken ct = default)
    {
        var (s, engine, at, month) = await PrepareAsync(e.ProductId, ct);
        var (cost, depreciation) = engine.Share(e.ProductId, e.BranchId, e.Qty);
        var row = Record(engine, month, Row(e, at, FixedAssetMovementKind.Scrapped, cost, depreciation, null));
        return Posted(row, Draft(row, e, $"Fixed asset wrecked into scrap: {e.Sku} × {e.Qty:0.###}")
            .AddIfAny(PostingLine.Dr(s.AccumulatedDepreciation, depreciation, memo: e.Sku))
            .AddIfAny(PostingLine.Dr(LedgerAccounts.ScrapInventory, cost - depreciation, memo: e.Sku))
            .AddIfAny(PostingLine.Cr(s.Asset, cost, memo: e.Sku)));
    }

    /// <summary>
    /// Units moving to another branch (a transfer received, goods hired out from one branch and returned to another): their
    /// cost and accumulated depreciation move with them, inter-branch so the group sees nothing.
    /// </summary>
    public async Task<PostingDraft?> TransferAsync(FixedAssetEvent e, Guid toBranchId, CancellationToken ct = default)
    {
        var (s, engine, at, month) = await PrepareAsync(e.ProductId, ct);
        var (cost, depreciation) = engine.Share(e.ProductId, e.BranchId, e.Qty);
        var row = Row(e, at, FixedAssetMovementKind.Transfer, cost, depreciation, null);
        row.ToBranchId = toBranchId;
        Record(engine, month, row);
        var draft = new PostingDraft
        {
            EntryDate = row.AtUtc, BranchId = e.BranchId, SourceType = LedgerSources.FixedAsset, SourceId = row.Id,
            Description = Words($"Fixed asset moved between branches: {e.Sku} × {e.Qty:0.###}", e), CreatedBy = e.By,
            IsInterBranch = true, CounterpartBranchId = toBranchId,
        }
            .AddIfAny(PostingLine.Dr(s.Asset, cost, toBranchId, e.Sku))
            .AddIfAny(PostingLine.Cr(s.Asset, cost, e.BranchId, e.Sku))
            .AddIfAny(PostingLine.Dr(s.AccumulatedDepreciation, depreciation, e.BranchId, e.Sku))
            .AddIfAny(PostingLine.Cr(s.AccumulatedDepreciation, depreciation, toBranchId, e.Sku));
        return Posted(row, draft);
    }

    /// <summary>
    /// Units found at a count: back into the pool at the value the count gives them (<paramref name="value"/>, what today's
    /// entry would post), depreciated from the month after. Dr asset, Cr disposal.
    /// </summary>
    public async Task<PostingDraft?> FoundAsync(FixedAssetEvent e, decimal value, CancellationToken ct = default)
    {
        var (s, engine, at, month) = await PrepareAsync(e.ProductId, ct);
        var amount = FixedAssetEngine.Money(value);
        var row = Record(engine, month, Row(e, at, FixedAssetMovementKind.Found, amount, 0m, month.AddMonths(1)));
        return Posted(row, Draft(row, e, $"Fixed asset found: {e.Sku} × {e.Qty:0.###}")
            .AddIfAny(PostingLine.Dr(s.Asset, amount, memo: e.Sku))
            .AddIfAny(PostingLine.Cr(s.Disposal, amount, memo: e.Sku)));
    }

    /// <summary>
    /// A hire product's scrap written off: at the value the register carries it at (scrap held at the switch-over at its value
    /// then, scrap wrecked since at its net book value). Dr stock losses (657), Cr 318 — today's accounts.
    /// </summary>
    public async Task<PostingDraft?> ScrapOutAsync(FixedAssetEvent e, CancellationToken ct = default)
    {
        var (_, engine, at, month) = await PrepareAsync(e.ProductId, ct);
        var value = engine.ScrapShare(e.ProductId, e.BranchId, e.Qty);
        var row = Record(engine, month, Row(e, at, FixedAssetMovementKind.ScrapOut, value, 0m, null));
        return Posted(row, Draft(row, e, $"Scrap of a fixed asset written off: {e.Sku} × {e.Qty:0.###}")
            .AddIfAny(PostingLine.Dr(LedgerAccounts.StockLoss, value, memo: e.Sku))
            .AddIfAny(PostingLine.Cr(LedgerAccounts.ScrapInventory, value, memo: e.Sku)));
    }

    /// <summary>A hire product's scrap put back into service: into the pool at the value it was carried at. Dr asset, Cr 318.</summary>
    public async Task<PostingDraft?> BackIntoServiceAsync(FixedAssetEvent e, CancellationToken ct = default)
    {
        var (s, engine, at, month) = await PrepareAsync(e.ProductId, ct);
        var value = engine.ScrapShare(e.ProductId, e.BranchId, e.Qty);
        var row = Record(engine, month, Row(e, at, FixedAssetMovementKind.BackIntoService, value, 0m, month.AddMonths(1)));
        return Posted(row, Draft(row, e, $"Scrap of a fixed asset back into service: {e.Sku} × {e.Qty:0.###}")
            .AddIfAny(PostingLine.Dr(s.Asset, value, memo: e.Sku))
            .AddIfAny(PostingLine.Cr(LedgerAccounts.ScrapInventory, value, memo: e.Sku)));
    }

    /// <summary>
    /// A quantity of a hire product moving between states, as <see cref="StockPosting.StateChange"/> would post it today:
    /// from an owned state (available, on rent, in maintenance) into scrap or written off, or out of scrap. Between the owned
    /// states nothing posts, as today. <c>Handled</c> is false when the register does not hold the product: the caller then
    /// posts today's entry.
    /// </summary>
    public async Task<(bool Handled, PostingDraft? Draft)> StateChangeAsync(
        FixedAssetEvent e, StockState from, StockState to, CancellationToken ct = default)
    {
        if (!await HoldsAsync(e.ProductId, ct)) return (false, null);
        var owned = from != StockState.Scrap && from != StockState.WrittenOff;
        PostingDraft? draft = (owned, to) switch
        {
            (true, StockState.Scrap) => await ScrapAsync(e, ct),
            (true, StockState.WrittenOff) => await DisposeAsync(e, ct),
            (false, StockState.WrittenOff) when from == StockState.Scrap => await ScrapOutAsync(e, ct),
            (false, StockState.Available or StockState.Maintenance or StockState.OnRent) when from == StockState.Scrap
                => await BackIntoServiceAsync(e, ct),
            _ => null,
        };
        return (true, draft);
    }

    /// <summary>
    /// The units of a hire product's pool at a branch now, as the register holds them (0 when it holds none, or is off).
    /// Phase M7d: a repair is capitalised only onto a pool holding units.
    /// </summary>
    public async Task<decimal> PoolUnitsAsync(Guid productId, Guid branchId, CancellationToken ct = default)
    {
        if (!await HoldsAsync(productId, ct)) return 0m;
        var (_, engine, _, _) = await PrepareAsync(productId, ct);
        return engine.Find(productId, branchId)?.Qty ?? 0m;
    }

    /// <summary>
    /// Phase M7d (MS-30): a repair capitalised at its job's completion. Its cost joins the pool as a layer of cost only, spread
    /// over the repaired units (<see cref="FixedAssetEvent.Qty"/>, which the pool already counts), depreciated over the type's
    /// life from the month after, like a purchase. Nothing posts here: the entry (Dr asset / Cr the accounts the repair was
    /// charged to) belongs to the maintenance practice ledger of the completion month, posted with it. Returns the movement.
    /// </summary>
    public async Task<FixedAssetMovement> CapitaliseRepairAsync(FixedAssetEvent e, decimal cost, CancellationToken ct = default)
    {
        var (_, engine, at, month) = await PrepareAsync(e.ProductId, ct);
        return Record(engine, month, Row(e, at, FixedAssetMovementKind.CapitalisedRepair, FixedAssetEngine.Money(cost), 0m, month.AddMonths(1)));
    }

    // ---------- the plumbing ----------

    private async Task<State?> StateAsync(CancellationToken ct)
    {
        if (loaded) return state;
        loaded = true;
        var settings = await FixedAssetData.CurrentAsync(db, ct);
        if (!FixedAssetData.Active(settings)) return null;
        var accounts = await FixedAssetData.AccountsAsync(db, ct);
        // The map refuses to unmap these while the register is active; were one gone all the same, the documents post as
        // they did before the switch rather than fail.
        if (!accounts.TryGetValue(FixedAssetAccountSlots.Asset, out var asset)
            || !accounts.TryGetValue(FixedAssetAccountSlots.AccumulatedDepreciation, out var depreciation)
            || !accounts.TryGetValue(FixedAssetAccountSlots.Disposal, out var disposal))
            return null;
        return state = new State
        {
            Settings = settings, Policy = await FixedAssetData.PolicyAsync(db, settings, ct), Zone = FixedAssetData.Zone(settings),
            Asset = asset.Code, AccumulatedDepreciation = depreciation.Code, Disposal = disposal.Code,
        };
    }

    private async Task<(State State, FixedAssetEngine Engine, DateTime At, DateOnly Month)> PrepareAsync(Guid productId, CancellationToken ct)
    {
        var s = await StateAsync(ct) ?? throw new InvalidOperationException("The fixed-asset register is not active: ask HoldsAsync first.");
        if (!s.Engines.TryGetValue(productId, out var engine))
        {
            var rows = await FixedAssetData.RowsAsync(db, [productId], null, ct);
            s.Engines[productId] = engine = FixedAssetData.Replay(s.Settings, s.Policy, rows, null);
            s.Last[productId] = rows.Count == 0 ? DateTime.MinValue : rows[^1].AtUtc;
        }
        // Strictly after the pool's last event, so a replay puts the events of one document in the order they were made.
        var now = DateTime.UtcNow;
        var last = s.Last.GetValueOrDefault(productId);
        var at = now > last ? now : last.AddTicks(1);
        s.Last[productId] = at;
        var month = FixedAssetEngine.MonthOf(at, s.Zone);
        engine.DepreciateThrough(month.AddMonths(-1));
        return (s, engine, at, month);
    }

    private FixedAssetMovement Record(FixedAssetEngine engine, DateOnly month, FixedAssetMovement row)
    {
        db.FixedAssetMovements.Add(row);
        engine.Apply(row, month);
        return row;
    }

    private static FixedAssetMovement Row(FixedAssetEvent e, DateTime at, FixedAssetMovementKind kind, decimal cost, decimal depreciation, DateOnly? start) => new()
    {
        ProductId = e.ProductId, BranchId = e.BranchId, Kind = kind, Qty = e.Qty, Cost = cost, Depreciation = depreciation,
        StartMonth = start, AtUtc = at, DocumentId = e.DocumentId, DocumentNumber = Cut(e.DocumentNumber, 64),
        Reason = Cut(e.Reason, 200), By = Cut(e.By, 200), Reference = Cut(e.Reference, 64),
    };

    private static PostingDraft Draft(FixedAssetMovement row, FixedAssetEvent e, string what) => new()
    {
        EntryDate = row.AtUtc, BranchId = e.BranchId, SourceType = LedgerSources.FixedAsset, SourceId = row.Id,
        Description = Words(what, e), CreatedBy = e.By,
    };

    /// <summary>The entry, marked on its row when it has anything to post; null when it has nothing.</summary>
    private static PostingDraft? Posted(FixedAssetMovement row, PostingDraft draft)
    {
        if (draft.Lines.Count == 0) return null;
        row.SourceType = draft.SourceType;
        row.SourceId = draft.SourceId;
        return draft;
    }

    private static string Words(string what, FixedAssetEvent e) =>
        string.Join(" — ", new[] { what, e.DocumentNumber, e.Reason }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
