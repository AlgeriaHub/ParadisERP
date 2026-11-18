using System.Globalization;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.FixedAssets;

/// <summary>
/// REN-12's switch-over (Phase M7a): the hire equipment the company holds — on hand, on rent, in maintenance and in transit
/// from a branch — leaves merchandise stock (310) for the asset account, branch by branch, and the register's pools open
/// with it. Modelled on the C-01 opening adjustment (<see cref="SalesCostSwitch"/>):
/// <list type="number">
/// <item>The page previews every pool: the quantity, the value 310 carries it at under the cost method in force (the cost
/// price, or the shadow's weighted average once sales are costed at it, C-01), and the value it opens at (the same, or the
/// product's asset value with the difference to the opening adjustment slot).</item>
/// <item>Posting states both totals. The server recomputes them under the version lock — the lock every sale and stock
/// document takes — and refuses if they moved, if a needed slot is unmapped, if a type has no useful life, if it is not the
/// switch-over month, or if the books are closed on its first day. Otherwise it posts one entry dated the month's first day
/// (Dr asset, Cr 310, the difference to the opening adjustment, per branch), writes the pools' opening events (and the hire
/// scrap each branch carries in 318, which stays there) and marks the switch, in one transaction. Once.</item>
/// </list>
/// </summary>
public sealed class FixedAssetSwitchOver(PosDbContext db, LedgerPoster poster, IRegionalSettingsProvider regional, TimeProvider time)
{
    public const string ChangedCode = "switch_over_changed";
    public const string BooksClosedCode = "books_closed";

    private sealed record Figures(FixedAssetSwitchDto Dto, FixedAssetSettings Settings, IReadOnlyDictionary<string, LedgerAccount> Accounts,
        Guid MainBranchId, DateOnly? Month);

    public async Task<FixedAssetSwitchDto> PreviewAsync(CancellationToken ct = default) => (await FiguresAsync(ct)).Dto;

    public async Task<FixedAssetResult<FixedAssetSwitchDto>> PostAsync(PostFixedAssetSwitchRequest r, string? by, CancellationToken ct = default)
    {
        try
        {
            await using var tx = await db.BeginWriteScopeAsync(ct);
            await db.LockVersionsAsync(ct);
            var f = await FiguresAsync(ct);
            if (f.Dto.Blocking.FirstOrDefault() is { } blocking)
                return FixedAssetResult<FixedAssetSwitchDto>.Fail(409, blocking.Code, blocking.Message);
            if (Math.Abs(r.ExpectedStockValue - f.Dto.StockValue) > 0.0005m || Math.Abs(r.ExpectedOpeningValue - f.Dto.OpeningTotal) > 0.0005m)
                return FixedAssetResult<FixedAssetSwitchDto>.Fail(409, ChangedCode,
                    "The figures have changed since they were shown: check the new ones and confirm again.");

            var month = f.Month!.Value;
            var now = time.GetUtcNow().UtcDateTime;
            var sourceId = Guid.NewGuid();
            var asset = f.Accounts[FixedAssetAccountSlots.Asset].Code;
            var draft = new PostingDraft
            {
                EntryDate = LedgerMonthPoster.Noon(month, StoreDocumentHistory.Zone(regional)),
                BranchId = f.MainBranchId, SourceType = LedgerSources.FixedAssetSwitch, SourceId = sourceId,
                Description = $"Hire equipment moved from stock to fixed assets (REN-12), {month:yyyy-MM}", CreatedBy = by,
            };
            foreach (var branch in f.Dto.Lines.Where(l => l.Qty > 0).GroupBy(l => l.BranchId).OrderBy(g => g.First().BranchCode, StringComparer.Ordinal))
            {
                var stock = branch.Sum(l => l.StockValue);
                var opened = branch.Sum(l => l.OpeningValue);
                draft.AddIfAny(PostingLine.Dr(asset, opened, branch.Key, "Hire equipment at the switch-over"));
                draft.AddIfAny(PostingLine.Cr(LedgerAccounts.Inventory, stock, branch.Key, "Hire equipment out of stock"));
                if (stock != opened)
                    draft.Add(PostingLine.Signed(f.Accounts[FixedAssetAccountSlots.OpeningAdjustment].Code, stock - opened, branch.Key,
                        "Opening adjustment (asset value)"));
            }
            await poster.PostAsync(draft, ct);

            foreach (var line in f.Dto.Lines)
            {
                if (line.Qty > 0)
                    db.FixedAssetMovements.Add(new FixedAssetMovement
                    {
                        ProductId = line.ProductId, BranchId = line.BranchId, Kind = FixedAssetMovementKind.Opening, Qty = line.Qty,
                        Cost = line.OpeningValue, StartMonth = month, AtUtc = now, SourceType = LedgerSources.FixedAssetSwitch,
                        SourceId = sourceId, Reason = "switch-over", By = by,
                    });
                if (line.ScrapQty > 0)
                    db.FixedAssetMovements.Add(new FixedAssetMovement
                    {
                        ProductId = line.ProductId, BranchId = line.BranchId, Kind = FixedAssetMovementKind.ScrapOpening,
                        Qty = line.ScrapQty, Cost = line.ScrapValue, AtUtc = now, Reason = "scrap held at the switch-over", By = by,
                    });
            }
            var row = await db.FixedAssetSettings.FirstAsync(ct);
            row.SwitchedAtUtc = now;
            row.SwitchedBy = by;
            row.SwitchEntrySourceId = sourceId;
            row.TimeZoneId = regional.Get().TimeZone;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (PeriodClosedException closed)
        {
            db.ChangeTracker.Clear();
            return FixedAssetResult<FixedAssetSwitchDto>.Fail(409, BooksClosedCode, closed.Message);
        }
        db.ChangeTracker.Clear();
        return FixedAssetResult<FixedAssetSwitchDto>.Done(await PreviewAsync(ct));
    }

    private async Task<Figures> FiguresAsync(CancellationToken ct)
    {
        var s = await FixedAssetData.CurrentAsync(db, ct);
        var accounts = await FixedAssetData.AccountsAsync(db, ct);
        var policy = await FixedAssetData.PolicyAsync(db, s, ct);
        var mainBranchId = await db.Branches.AsNoTracking().OrderByDescending(b => b.IsMain).ThenBy(b => b.Id).Select(b => b.Id).FirstOrDefaultAsync(ct);
        var valuation = await db.StockValuationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var average = valuation?.SalesCost == SalesCostMethod.WeightedAverage;
        var month = s.SwitchMonth?.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        if (s.SwitchedAtUtc is not null)
            return new Figures(await PostedAsync(s, average, ct), s, accounts, mainBranchId, s.SwitchMonth);

        var products = await db.Products.AsNoTracking().Where(p => p.IsRentable && !p.IsDeleted)
            .Select(p => new { p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn, p.CostPrice, p.AssetUnitValue }).ToDictionaryAsync(p => p.Id, ct);
        var ids = products.Keys.ToList();
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, ct);
        // Added here, not in the query (SQLite stores decimals as text).
        var stocks = await db.ProductStocks.AsNoTracking().Where(x => ids.Contains(x.ProductId))
            .Select(x => new { x.ProductId, x.BranchId, x.StockQty, x.OnRentQty, x.MaintenanceQty, x.ScrapQty }).ToListAsync(ct);
        var transit = (await db.StockTransferLines.AsNoTracking().Where(l => ids.Contains(l.ProductId))
                .Select(l => new { l.ProductId, l.Transfer!.FromBranchId, l.QtyDispatched, l.QtyReceived, l.QtyLost, l.QtyReturned }).ToListAsync(ct))
            .GroupBy(l => (l.ProductId, l.FromBranchId))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.QtyDispatched - l.QtyReceived - l.QtyLost - l.QtyReturned));
        var averages = average ? await AveragesAsync(valuation!, ids, ct) : new Dictionary<(Guid, Guid?), decimal>();

        var keys = stocks.Select(x => (x.ProductId, x.BranchId)).Concat(transit.Keys).Distinct().ToList();
        var lines = new List<FixedAssetSwitchLineDto>();
        foreach (var (productId, branchId) in keys)
        {
            if (!products.TryGetValue(productId, out var p) || !branches.TryGetValue(branchId, out var b)) continue;
            var row = stocks.FirstOrDefault(x => x.ProductId == productId && x.BranchId == branchId);
            var onHand = row?.StockQty ?? 0m;
            var onRent = row?.OnRentQty ?? 0m;
            var maintenance = row?.MaintenanceQty ?? 0m;
            var inTransit = Math.Max(0m, transit.GetValueOrDefault((productId, branchId)));
            var qty = onHand + onRent + maintenance + inTransit;
            var scrap = row?.ScrapQty ?? 0m;
            if (qty <= 0 && scrap <= 0) continue;
            var unit = average
                ? averages.TryGetValue((productId, valuation!.Scope == StockValuationScope.Location ? branchId : null), out var a) ? a : p.CostPrice
                : p.CostPrice;
            var held = Math.Max(0m, qty);
            var stockValue = Money(held * unit);
            var openingValue = s.OpeningValue == FixedAssetOpeningValue.AssetUnitValue && p.AssetUnitValue > 0
                ? Money(held * p.AssetUnitValue) : stockValue;
            lines.Add(new FixedAssetSwitchLineDto(productId, p.Sku, p.NameAr, p.NameFr, p.NameEn, branchId, b.Code, b.NameAr, b.NameFr, b.NameEn,
                onHand, onRent, maintenance, inTransit, held, unit, stockValue, openingValue, stockValue - openingValue,
                policy.LifeOf(productId), Math.Max(0m, scrap), Money(Math.Max(0m, scrap) * unit)));
        }
        lines = lines.OrderBy(l => l.Sku, StringComparer.Ordinal).ThenBy(l => l.BranchCode, StringComparer.Ordinal).ToList();

        // What stands in the way, in the order the accountant meets it.
        var blocking = new List<FixedAssetCheckDto>();
        var today = FixedAssetSettingsStore.CompanyMonth(regional, time.GetUtcNow().UtcDateTime);
        if (s.Treatment != RentalEquipmentTreatment.FixedAssets)
            blocking.Add(new("treatment_stock", "Choose fixed assets as the treatment of hire equipment in the settings first."));
        if (s.SwitchMonth is not DateOnly chosen)
            blocking.Add(new("month_not_chosen", "Choose the switch-over month in the settings first."));
        else if (chosen > today)
            blocking.Add(new("month_not_begun", $"The switch-over is posted during its month, {chosen:MM/yyyy}: it has not begun."));
        else if (chosen < today)
            blocking.Add(new("month_passed", $"The switch-over is posted during its month, {chosen:MM/yyyy}: it has passed. Choose this month."));
        foreach (var slot in FixedAssetAccountSlots.Required.Where(x => !accounts.ContainsKey(x)))
            blocking.Add(new("slot_unmapped", $"Name the account of the slot {slot} in the account map first: it is never guessed."));
        if (lines.Any(l => l.Difference != 0) && !accounts.ContainsKey(FixedAssetAccountSlots.OpeningAdjustment))
            blocking.Add(new("slot_unmapped", $"Name the account of the slot {FixedAssetAccountSlots.OpeningAdjustment} first: the difference between the stock value and the asset value is posted to it."));
        if (lines.Any(l => l.Qty > 0 && l.UsefulLifeMonths is null))
            blocking.Add(new("life_missing", "Give every hire type a useful life, or a default life for all of them, first."));
        if (s.SwitchMonth is DateOnly first && await BookClosings.CurrentAsync(db, ct) is { OpenFromUtc: DateTime openFrom }
            && LedgerMonthPoster.Noon(first, StoreDocumentHistory.Zone(regional)) < openFrom)
            blocking.Add(new(BooksClosedCode, "The books are closed on the switch-over month's first day."));

        var dto = new FixedAssetSwitchDto(month, average ? nameof(SalesCostMethod.WeightedAverage) : nameof(SalesCostMethod.CostPrice),
            s.OpeningValue.ToString(), false, null, null, lines, lines.Sum(l => l.StockValue), lines.Sum(l => l.OpeningValue),
            lines.Sum(l => l.Difference), blocking);
        return new Figures(dto, s, accounts, mainBranchId, s.SwitchMonth);
    }

    /// <summary>The switch-over as posted: its opening events, read back.</summary>
    private async Task<FixedAssetSwitchDto> PostedAsync(FixedAssetSettings s, bool average, CancellationToken ct)
    {
        var rows = await db.FixedAssetMovements.AsNoTracking()
            .Where(m => m.Kind == FixedAssetMovementKind.Opening || m.Kind == FixedAssetMovementKind.ScrapOpening).ToListAsync(ct);
        var ids = rows.Select(m => m.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn }).ToDictionaryAsync(p => p.Id, ct);
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, ct);
        var policy = await FixedAssetData.PolicyAsync(db, s, ct);
        var lines = rows.GroupBy(m => (m.ProductId, m.BranchId)).Select(g =>
        {
            var p = products.GetValueOrDefault(g.Key.ProductId);
            var b = branches.GetValueOrDefault(g.Key.BranchId);
            var opening = g.FirstOrDefault(m => m.Kind == FixedAssetMovementKind.Opening);
            var scrap = g.FirstOrDefault(m => m.Kind == FixedAssetMovementKind.ScrapOpening);
            var qty = opening?.Qty ?? 0m;
            var cost = opening?.Cost ?? 0m;
            return new FixedAssetSwitchLineDto(g.Key.ProductId, p?.Sku ?? "", p?.NameAr ?? "", p?.NameFr ?? "", p?.NameEn ?? "",
                g.Key.BranchId, b?.Code ?? "", b?.NameAr ?? "", b?.NameFr ?? "", b?.NameEn ?? "", 0m, 0m, 0m, 0m, qty,
                qty > 0 ? cost / qty : 0m, cost, cost, 0m, policy.LifeOf(g.Key.ProductId), scrap?.Qty ?? 0m, scrap?.Cost ?? 0m);
        }).OrderBy(l => l.Sku, StringComparer.Ordinal).ThenBy(l => l.BranchCode, StringComparer.Ordinal).ToList();
        // What left 310 is the entry's credit; the opening, its debit to the asset account.
        var entry = await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == LedgerSources.FixedAssetSwitch && l.Entry.SourceId == s.SwitchEntrySourceId && !l.Entry.IsReversal)
            .Select(l => new { l.Account!.Code, l.Debit, l.Credit }).ToListAsync(ct);
        var stock = entry.Where(l => l.Code == LedgerAccounts.Inventory).Sum(l => l.Credit - l.Debit);
        var opened = lines.Sum(l => l.OpeningValue);
        return new FixedAssetSwitchDto(s.SwitchMonth?.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            average ? nameof(SalesCostMethod.WeightedAverage) : nameof(SalesCostMethod.CostPrice), s.OpeningValue.ToString(),
            true, s.SwitchedAtUtc, s.SwitchedBy, lines, stock, opened, stock - opened, [new("already_posted", "The switch-over is posted.")]);
    }

    /// <summary>The shadow's average of each hire product (per location when there is one per location; else the company's).</summary>
    private async Task<Dictionary<(Guid, Guid?), decimal>> AveragesAsync(StockValuationSettings settings, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var openings = await StockValuationShadow.LoadOpeningsAsync(db, ids, ct);
        var (movements, original) = await StockValuationShadow.LoadMovementsAsync(db, ids, settings.StartedAtUtc, ct);
        var result = WeightedAverageShadow.Run(StockValuationShadow.PolicyOf(settings), openings, movements, original);
        var averages = new Dictionary<(Guid, Guid?), decimal>();
        foreach (var pool in result.Pools)
            if (pool.Average is decimal a) averages[(pool.ProductId, pool.BranchId)] = a;
        return averages;
    }

    private static decimal Money(decimal v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
}
