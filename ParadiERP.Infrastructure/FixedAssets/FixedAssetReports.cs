using System.Globalization;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.FixedAssets;

/// <summary>The month a register read is about: its bounds on the company's clock, and whether it is still running.</summary>
internal sealed record RegisterMonth(DateOnly First, DateOnly Last, DateTime EndUtc, DateTime CutUtc, bool MonthToDate)
{
    /// <summary>The month asked (the company's month today when none is), or why it cannot be read.</summary>
    public static (RegisterMonth? Month, string? Code, string? Error) Of(int? year, int? month, TimeZoneInfo zone, DateTime nowUtc)
    {
        var today = FixedAssetEngine.MonthOf(nowUtc, zone);
        var (y, m) = year is int yy && month is int mm ? (yy, mm) : (today.Year, today.Month);
        if (y is < 2000 or > 2100 || m is < 1 or > 12) return (null, "invalid_period", "Choose a month.");
        var first = new DateOnly(y, m, 1);
        if (first > today) return (null, "month_in_future", "That month has not begun.");
        var end = TimeZoneInfo.ConvertTimeToUtc(first.AddMonths(1).ToDateTime(TimeOnly.MinValue), zone);
        var toDate = end > nowUtc;
        return (new RegisterMonth(first, first.AddMonths(1).AddDays(-1), end, toDate ? nowUtc.AddTicks(1) : end, toDate), null, null);
    }
}

/// <summary>
/// The register of hire equipment (REN-12, Phase M7a): every pool — a hire product at a branch — with its units, cost,
/// accumulated depreciation, net book value and life, as at a month's end, and a pool's history. Computed from the stored
/// events on every read; writes nothing. A reader held to a branch reads that branch's pools (the route's <c>branchId</c>).
/// </summary>
public sealed class FixedAssetRegisterReport(PosDbContext db, TimeProvider time)
{
    public async Task<FixedAssetResult<FixedAssetRegisterDto>> RegisterAsync(int? year, int? month, Guid? branchId, CancellationToken ct = default)
    {
        var s = await FixedAssetData.CurrentAsync(db, ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (!FixedAssetData.Active(s))
        {
            var today = FixedAssetEngine.MonthOf(now, FixedAssetData.Zone(s));
            return FixedAssetResult<FixedAssetRegisterDto>.Done(new FixedAssetRegisterDto(false, Month(s.SwitchMonth), today.Year, today.Month,
                true, branchId, [], 0m, 0m, 0m, 0m, null));
        }
        var zone = FixedAssetData.Zone(s);
        var (asked, code, error) = RegisterMonth.Of(year, month, zone, now);
        if (asked is null) return FixedAssetResult<FixedAssetRegisterDto>.Fail(400, code!, error!);

        var policy = await FixedAssetData.PolicyAsync(db, s, ct);
        var rows = await FixedAssetData.RowsAsync(db, null, asked.CutUtc, ct);
        // A month still running is not depreciated yet: the register stands at the end of the month before.
        var through = asked.MonthToDate ? asked.First.AddMonths(-1) : asked.First;
        var engine = FixedAssetData.Replay(s, policy, rows, through);
        var pools = await PoolsAsync(engine, policy, branchId, asked.MonthToDate, ct);
        var posted = await PostedThroughAsync(ct);
        return FixedAssetResult<FixedAssetRegisterDto>.Done(new FixedAssetRegisterDto(true, Month(s.SwitchMonth), asked.First.Year, asked.First.Month,
            asked.MonthToDate, branchId, pools, pools.Sum(p => p.Cost), pools.Sum(p => p.AccumulatedDepreciation),
            pools.Sum(p => p.NetBookValue), pools.Sum(p => p.Units), posted));
    }

    /// <summary>One pool's events and months of depreciation (through the last month ended), oldest first.</summary>
    public async Task<FixedAssetResult<FixedAssetHistoryDto>> HistoryAsync(Guid productId, Guid branchId, CancellationToken ct = default)
    {
        var product = await db.Products.AsNoTracking().Where(p => p.Id == productId)
            .Select(p => new { p.Sku, p.NameAr, p.NameFr, p.NameEn }).FirstOrDefaultAsync(ct);
        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, ct);
        if (product is null || branch is null) return FixedAssetResult<FixedAssetHistoryDto>.Fail(404, "not_found", "No such pool.");
        var s = await FixedAssetData.CurrentAsync(db, ct);
        if (!FixedAssetData.Active(s))
            return FixedAssetResult<FixedAssetHistoryDto>.Done(new FixedAssetHistoryDto(productId, product.Sku, product.NameAr, product.NameFr,
                product.NameEn, branchId, branch.Code, null, []));

        var zone = FixedAssetData.Zone(s);
        var now = time.GetUtcNow().UtcDateTime;
        var through = FixedAssetEngine.MonthOf(now, zone).AddMonths(-1);
        var policy = await FixedAssetData.PolicyAsync(db, s, ct);
        var rows = await FixedAssetData.RowsAsync(db, [productId], null, ct);
        var engine = FixedAssetData.Replay(s, policy, rows, through);
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var live = await LiveMonthsAsync(ct);
        // Phase M7d: a capitalised repair's entry posts with the maintenance practice ledger of its month.
        var maintenanceLive = (await db.MaintenanceMonthPostings.AsNoTracking().Where(p => p.ReversedAtUtc == null)
                .Select(p => new { p.Year, p.Month }).ToListAsync(ct))
            .Select(p => (p.Year, p.Month)).ToHashSet();
        bool Posted(FixedAssetMovement m)
        {
            if (m.Kind != FixedAssetMovementKind.CapitalisedRepair) return m.SourceType is not null || m.Kind == FixedAssetMovementKind.Opening;
            var month = FixedAssetEngine.MonthOf(m.AtUtc, zone);
            return maintenanceLive.Contains((month.Year, month.Month));
        }

        var lines = rows.Where(m => m.BranchId == branchId || (m.Kind == FixedAssetMovementKind.Transfer && m.ToBranchId == branchId))
            .Select(m =>
            {
                var incoming = m.Kind == FixedAssetMovementKind.Transfer && m.ToBranchId == branchId && m.BranchId != branchId;
                var kind = m.Kind == FixedAssetMovementKind.Transfer ? (incoming ? "TransferIn" : "TransferOut") : m.Kind.ToString();
                Guid? other = m.Kind == FixedAssetMovementKind.Transfer ? (incoming ? m.BranchId : m.ToBranchId) : null;
                return new FixedAssetHistoryLineDto(m.AtUtc, kind, m.Qty, m.Cost, m.Depreciation, other,
                    other is Guid o ? branches.GetValueOrDefault(o) : null, m.DocumentNumber, m.Reason, m.By, Posted(m), m.Reference);
            })
            .Concat(engine.Charges.Where(c => c.ProductId == productId && c.BranchId == branchId).Select(c =>
                new FixedAssetHistoryLineDto(LedgerMonthPoster.Noon(c.Month.AddMonths(1).AddDays(-1), zone), "Depreciation", 0m, 0m, c.Amount,
                    null, null, null, null, null, live.Contains((c.Month.Year, c.Month.Month)))))
            .OrderBy(l => l.AtUtc).ToList();
        var pool = (await PoolsAsync(engine, policy, branchId, false, ct)).FirstOrDefault(p => p.ProductId == productId);
        return FixedAssetResult<FixedAssetHistoryDto>.Done(new FixedAssetHistoryDto(productId, product.Sku, product.NameAr, product.NameFr,
            product.NameEn, branchId, branch.Code, pool, lines, Schedule(s, policy, rows, productId, branchId, through, zone)));
    }

    /// <summary>
    /// A pool's schedule year by year (declining balance, 2026-11-10; straight line alike): from the switch-over to the end of
    /// its last layer's life, the months ended as computed and the rest projected at the units held. The switch-over's opening
    /// is the first year's opening value; every later event is in <c>Movements</c> at the net book value it moved.
    /// </summary>
    internal static List<FixedAssetScheduleYearDto> Schedule(FixedAssetSettings s, FixedAssetPolicy policy, IReadOnlyList<FixedAssetMovement> rows,
        Guid productId, Guid branchId, DateOnly endedThrough, TimeZoneInfo zone)
    {
        var engine = FixedAssetData.Replay(s, policy, rows, null);
        var layers = engine.Pools.SelectMany(p => p.Layers).Where(l => l.LifeMonths > 0).ToList();
        var end = layers.Select(l => l.StartMonth.AddMonths(l.LifeMonths - 1)).DefaultIfEmpty(endedThrough).Max();
        if (end < endedThrough) end = endedThrough;
        engine.DepreciateThrough(end);

        var charges = engine.Charges.Where(c => c.ProductId == productId && c.BranchId == branchId)
            .GroupBy(c => c.Month.Year).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount));
        var opened = new Dictionary<int, decimal>();
        var moved = new Dictionary<int, decimal>();
        foreach (var m in rows)
        {
            var year = FixedAssetEngine.MonthOf(m.AtUtc, zone).Year;
            var mine = m.BranchId == branchId;
            var incoming = m.Kind == FixedAssetMovementKind.Transfer && m.ToBranchId == branchId && m.BranchId != branchId;
            var value = m.Kind switch
            {
                FixedAssetMovementKind.Acquisition or FixedAssetMovementKind.Found or FixedAssetMovementKind.BackIntoService
                    or FixedAssetMovementKind.CapitalisedRepair when mine => m.Cost,
                FixedAssetMovementKind.Disposal or FixedAssetMovementKind.Scrapped when mine => -(m.Cost - m.Depreciation),
                FixedAssetMovementKind.Transfer when incoming => m.Cost - m.Depreciation,
                FixedAssetMovementKind.Transfer when mine && m.ToBranchId != branchId => -(m.Cost - m.Depreciation),
                _ => 0m,
            };
            if (m.Kind == FixedAssetMovementKind.Opening && mine) opened[year] = opened.GetValueOrDefault(year) + m.Cost;
            else if (value != 0m) moved[year] = moved.GetValueOrDefault(year) + value;
        }

        // The years a declining-balance layer of this pool turns to straight line.
        var residual = policy.ResidualPercent / 100m;
        var switches = (engine.Find(productId, branchId)?.Layers ?? []).Where(l => l.Terms.Declining && l.LifeMonths > 0)
            .SelectMany(l => FixedAssetDeclining.Schedule(l.UnitCost, l.UnitCost * residual, l.StartMonth, l.LifeMonths, l.Terms.Coefficient,
                l.Terms.SwitchToStraightLine).Where(y => y.Switches).Select(y => y.Year))
            .ToHashSet();

        var years = new List<FixedAssetScheduleYearDto>();
        var value0 = 0m;
        for (var y = s.SwitchMonth!.Value.Year; y <= end.Year; y++)
        {
            var opening = value0 + opened.GetValueOrDefault(y);
            var charge = charges.GetValueOrDefault(y);
            var movements = moved.GetValueOrDefault(y);
            var closing = opening - charge + movements;
            if (opening != 0m || charge != 0m || movements != 0m)
                years.Add(new FixedAssetScheduleYearDto(y, opening, charge, movements, closing, switches.Contains(y), new DateOnly(y, 12, 1) > endedThrough));
            value0 = closing;
        }
        return years;
    }

    private async Task<List<FixedAssetPoolDto>> PoolsAsync(
        FixedAssetEngine engine, FixedAssetPolicy policy, Guid? branchId, bool withStock, CancellationToken ct)
    {
        var pools = engine.Pools.Where(p => branchId is null || p.BranchId == branchId).ToList();
        // What the stock buckets hold of the hire products now: beside the register's units, so a difference shows.
        var hire = await db.Products.AsNoTracking().Where(p => p.IsRentable && !p.IsDeleted).Select(p => p.Id).ToListAsync(ct);
        var ids = hire.Concat(pools.Select(p => p.ProductId)).Distinct().ToList();
        var stock = (await db.ProductStocks.AsNoTracking().Where(x => ids.Contains(x.ProductId) && (branchId == null || x.BranchId == branchId))
                .Select(x => new { x.ProductId, x.BranchId, x.StockQty, x.OnRentQty, x.MaintenanceQty }).ToListAsync(ct))
            .ToDictionary(x => (x.ProductId, x.BranchId), x => x.StockQty + x.OnRentQty + x.MaintenanceQty);
        var transit = (await db.StockTransferLines.AsNoTracking().Where(l => ids.Contains(l.ProductId))
                .Select(l => new { l.ProductId, l.Transfer!.FromBranchId, l.QtyDispatched, l.QtyReceived, l.QtyLost, l.QtyReturned }).ToListAsync(ct))
            .GroupBy(l => (l.ProductId, l.FromBranchId))
            .ToDictionary(g => g.Key, g => g.Sum(l => l.QtyDispatched - l.QtyReceived - l.QtyLost - l.QtyReturned));
        decimal InStock((Guid, Guid) key) => stock.GetValueOrDefault(key) + Math.Max(0m, transit.GetValueOrDefault(key));

        var keys = pools.Select(p => (p.ProductId, p.BranchId)).ToList();
        if (withStock)
            keys.AddRange(stock.Where(x => x.Value > 0 && hire.Contains(x.Key.ProductId)).Select(x => x.Key).Except(keys).ToList());
        var productIds = keys.Select(k => k.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn }).ToDictionaryAsync(p => p.Id, ct);
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, ct);
        return keys.Select(k =>
        {
            var pool = engine.Find(k.ProductId, k.BranchId);
            var p = products.GetValueOrDefault(k.ProductId);
            var b = branches.GetValueOrDefault(k.BranchId);
            var terms = policy.TermsOf(k.ProductId);
            return new FixedAssetPoolDto(k.ProductId, p?.Sku ?? "", p?.NameAr ?? "", p?.NameFr ?? "", p?.NameEn ?? "", k.BranchId, b?.Code ?? "",
                b?.NameAr ?? "", b?.NameFr ?? "", b?.NameEn ?? "", pool?.Qty ?? 0m, InStock(k), pool?.Cost ?? 0m, pool?.Depreciation ?? 0m,
                pool?.NetBookValue ?? 0m, policy.LifeOf(k.ProductId), pool?.LastCharge ?? 0m, pool?.ScrapQty ?? 0m, pool?.ScrapValue ?? 0m,
                terms.Method.ToString(), terms.Declining ? terms.Coefficient : null, terms.Declining && terms.SwitchToStraightLine);
        })
            .Where(p => p.Units != 0 || p.Cost != 0 || p.AccumulatedDepreciation != 0 || p.UnitsInStock != 0 || p.ScrapQty != 0)
            .OrderBy(p => p.Sku, StringComparer.Ordinal).ThenBy(p => p.BranchCode, StringComparer.Ordinal).ToList();
    }

    private async Task<HashSet<(int, int)>> LiveMonthsAsync(CancellationToken ct) =>
        (await db.FixedAssetMonthPostings.AsNoTracking().Where(p => p.ReversedAtUtc == null).Select(p => new { p.Year, p.Month }).ToListAsync(ct))
        .Select(p => (p.Year, p.Month)).ToHashSet();

    private async Task<string?> PostedThroughAsync(CancellationToken ct)
    {
        var live = await LiveMonthsAsync(ct);
        if (live.Count == 0) return null;
        var (y, m) = live.MaxBy(x => x.Item1 * 12 + x.Item2);
        return $"{y:0000}-{m:00}";
    }

    internal static string? Month(DateOnly? month) => month?.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}

/// <summary>
/// The register's practice ledger (REN-12, Phase M7a): one month of straight-line depreciation — an entry per pool, Dr the
/// depreciation charge, Cr accumulated depreciation, at the accounts the map names — computed on every read and written
/// nowhere until the accountant posts the ended month (<see cref="FixedAssetLedgerPostings"/>).
/// </summary>
public sealed class FixedAssetDepreciationReport(PosDbContext db, TimeProvider time)
{
    public const string Kind = "depreciation";

    public async Task<FixedAssetResult<FixedAssetDepreciationDto>> MonthAsync(int? year, int? month, Guid? branchId, CancellationToken ct = default)
    {
        var s = await FixedAssetData.CurrentAsync(db, ct);
        var zone = FixedAssetData.Zone(s);
        var (asked, code, error) = RegisterMonth.Of(year, month, zone, time.GetUtcNow().UtcDateTime);
        if (asked is null) return FixedAssetResult<FixedAssetDepreciationDto>.Fail(400, code!, error!);
        var postings = await db.FixedAssetMonthPostings.AsNoTracking().Where(p => p.Year == asked.First.Year && p.Month == asked.First.Month)
            .OrderBy(p => p.Generation)
            .Select(p => new PracticeLedgerPostingDto(p.Generation, p.Entries, p.TotalDebit, p.PostedAtUtc, p.PostedBy, p.ReversedAtUtc, p.ReversedBy))
            .ToListAsync(ct);
        var notes = new List<string>();
        if (asked.MonthToDate) notes.Add("month_to_date");
        FixedAssetDepreciationDto Empty(string note) => new(asked.First.Year, asked.First.Month, asked.MonthToDate, branchId,
            FixedAssetData.Active(s), FixedAssetRegisterReport.Month(s.SwitchMonth), [], [], [], [], notes.Append(note).ToList(), 0m, 0m, postings);
        if (!FixedAssetData.Active(s)) return FixedAssetResult<FixedAssetDepreciationDto>.Done(Empty("register_off"));
        if (asked.First < s.SwitchMonth!.Value) return FixedAssetResult<FixedAssetDepreciationDto>.Done(Empty("before_switch_over"));

        var policy = await FixedAssetData.PolicyAsync(db, s, ct);
        var rows = await FixedAssetData.RowsAsync(db, null, asked.CutUtc, ct);
        var engine = FixedAssetData.Replay(s, policy, rows, asked.First);
        var charges = engine.Charges.Where(c => c.Month == asked.First && (branchId == null || c.BranchId == branchId)).ToList();

        var accounts = await FixedAssetData.AccountsAsync(db, ct);
        var productIds = charges.Select(c => c.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Sku, ct);
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, ct);
        PracticeLedgerLineDto Line(string slot, decimal debit, decimal credit)
        {
            var a = accounts.GetValueOrDefault(slot);
            return new PracticeLedgerLineDto(slot, a?.Id, a?.Code, a?.NameAr, a?.NameFr, a?.NameEn, debit, credit);
        }
        var entries = charges.Select(c =>
        {
            var b = branches.GetValueOrDefault(c.BranchId);
            return new PracticeLedgerEntryDto(Kind, asked.Last, c.ProductId, products.GetValueOrDefault(c.ProductId) ?? "", c.BranchId,
                b?.Code ?? "", b?.NameAr ?? "", b?.NameFr ?? "", b?.NameEn ?? "", null,
                [Line(FixedAssetAccountSlots.DepreciationExpense, c.Amount, 0m), Line(FixedAssetAccountSlots.AccumulatedDepreciation, 0m, c.Amount)]);
        }).OrderBy(e => e.BranchCode, StringComparer.Ordinal).ThenBy(e => e.OrderNumber, StringComparer.Ordinal).ToList();

        var lines = entries.SelectMany(e => e.Lines).ToList();
        var used = lines.Select(l => l.Slot).ToHashSet();
        var totals = FixedAssetAccountSlots.All.Where(used.Contains).Select(slot =>
        {
            var a = accounts.GetValueOrDefault(slot);
            var mine = lines.Where(l => l.Slot == slot).ToList();
            return new PracticeLedgerAccountDto(slot, a?.Id, a?.Code, a?.NameAr, a?.NameFr, a?.NameEn, mine.Sum(l => l.Debit), mine.Sum(l => l.Credit));
        }).ToList();
        var unmapped = totals.Where(t => t.AccountId is null).Select(t => t.Slot).ToList();
        var unbalanced = entries.Count(e => e.Lines.Sum(l => l.Debit) != e.Lines.Sum(l => l.Credit));
        var checks = new List<PracticeLedgerCheckDto>
        {
            new("entries_balance", unbalanced == 0, 0, unbalanced),
            new("accounts_mapped", unmapped.Count == 0, used.Count, used.Count - unmapped.Count),
        };
        return FixedAssetResult<FixedAssetDepreciationDto>.Done(new FixedAssetDepreciationDto(asked.First.Year, asked.First.Month, asked.MonthToDate,
            branchId, true, FixedAssetRegisterReport.Month(s.SwitchMonth), entries, totals, checks, unmapped, notes,
            lines.Sum(l => l.Debit), lines.Sum(l => l.Credit), postings));
    }
}

/// <summary>
/// The accountant posts an ended month of depreciation to the ledger (REN-12, Phase M7a) through the month poster the
/// practice ledgers share (<see cref="LedgerMonthPoster"/>), under source <c>fixed-assets</c>: one entry per branch, a
/// line per pool. From the switch-over month, in order, never twice, closed books refusing it; reversed by mirror entries,
/// the last month first, and posted again under its next generation.
/// </summary>
public sealed class FixedAssetLedgerPostings(
    PosDbContext db, FixedAssetDepreciationReport report, LedgerPoster poster, IRegionalSettingsProvider regional)
{
    public const string SourceType = LedgerSources.FixedAssetDepreciation;

    /// <param name="companyScoped">The caller reads every branch: a month is posted whole.</param>
    public async Task<FixedAssetResult<FixedAssetDepreciationDto>> PostAsync(int year, int month, bool companyScoped, string? by, CancellationToken ct = default)
    {
        var s = await FixedAssetData.CurrentAsync(db, ct);
        if (!FixedAssetData.Active(s))
            return Fail(409, "register_off", "The register is not switched on: post the switch-over first.");
        if (year is < 2000 or > 2100 || month is < 1 or > 12) return Fail(400, "invalid_period", "Choose a month.");
        if (new DateOnly(year, month, 1) < s.SwitchMonth!.Value)
            return Fail(409, "before_posting_start", "This month is before the switch-over month.");
        if (!companyScoped) return Fail(403, "post_needs_every_branch", "A month is posted whole: it needs a reader of every branch.");

        var read = await report.MonthAsync(year, month, null, ct);
        if (read.Value is not FixedAssetDepreciationDto practice) return read;
        var entries = practice.Entries.GroupBy(e => e.BranchId).Select(g => new MonthPostingEntry(
            practice.Entries.First().Day, g.Key, $"Depreciation of hire equipment {year:0000}-{month:00}",
            g.SelectMany(e => e.Lines.Select(l => new PostingLine(l.AccountCode ?? "", l.Debit, l.Credit, Memo: e.OrderNumber))).ToList(),
            SkipWhenOpening: false)).ToList();
        var refused = await LedgerMonthPoster.PostAsync<FixedAssetMonthPosting>(db, poster, regional, SourceType, year, month, s.SwitchMonth.Value,
            practice.MonthToDate, practice.Checks.All(c => c.Passed), entries, by, ct);
        if (refused is not null) return Fail(refused.Status, refused.Code, refused.Message);
        return await report.MonthAsync(year, month, null, ct);
    }

    public async Task<FixedAssetResult<FixedAssetDepreciationDto>> ReverseAsync(int year, int month, bool companyScoped, string? by, CancellationToken ct = default)
    {
        if (!companyScoped) return Fail(403, "post_needs_every_branch", "A month is reversed whole: it needs a reader of every branch.");
        var refused = await LedgerMonthPoster.ReverseAsync<FixedAssetMonthPosting>(db, poster, SourceType, year, month,
            $"Depreciation {year:0000}-{month:00} reversed", by, ct);
        if (refused is not null) return Fail(refused.Status, refused.Code, refused.Message);
        return await report.MonthAsync(year, month, null, ct);
    }

    public static Guid EntryId(int year, int month, int generation, int index) =>
        LedgerMonthPoster.EntryId(SourceType, year, month, generation, index);

    private static FixedAssetResult<FixedAssetDepreciationDto> Fail(int status, string code, string error) =>
        FixedAssetResult<FixedAssetDepreciationDto>.Fail(status, code, error);
}
