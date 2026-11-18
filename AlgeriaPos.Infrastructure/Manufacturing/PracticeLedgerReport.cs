using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Manufacturing;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Manufacturing;

/// <summary>
/// Manufacturing › Practice ledger (Phase C2, docs/manufacturing/phase-c.md): one month of the entries the admin's
/// chosen accounting model would make, built by <see cref="PracticeLedger"/> from the orders' events and the cost engine.
/// Computed on every read and written nowhere: <c>LedgerEntries</c>, stock, payroll and the orders are only read.
///
/// <para>
/// Each order's cost at a moment comes from <see cref="ProductionCostEngine"/> fed with what was recorded before that
/// moment, valued as the cost sheets value it today (the employer burden counting only when the admin put it in stock
/// value). A release is valued as the order's sheet values it at the order's close — or today while it is open — the
/// last release of a closed order carrying the remainder, so an order's entries add up to its cost.
/// </para>
///
/// <para>
/// Every entry carries one order's labour, so the month is shown only to a reader who sees pay rates, as the cost
/// history is (<see cref="CostPrivacy"/>); an order or line out of the caller's reach is left out.
/// </para>
/// </summary>
public sealed class PracticeLedgerReport(
    PosDbContext db, ProductionCosts costs, ManufacturingCostingOptions options, IRegionalSettingsProvider regional, TimeProvider time)
{
    private static readonly ProductionOrderStatus[] Live =
        [ProductionOrderStatus.Open, ProductionOrderStatus.OnHold, ProductionOrderStatus.CompletionRequested];

    private sealed record Move(Guid MovementId, string Number, MaterialMovementKind Kind, DateTime AtUtc, Guid MaterialId, decimal BaseQty, decimal UnitCost);

    private sealed record Doc(string Number, DateTime AtUtc, bool IsReturn, IReadOnlyList<PracticeMaterialValue> Values)
    {
        public decimal Signed => (IsReturn ? -1m : 1m) * Values.Sum(v => v.Value);
    }

    public async Task<MfgResult<PracticeLedgerDto>> MonthAsync(MfgCaller caller, int? askedYear, int? askedMonth, Guid? lineId, CancellationToken ct)
    {
        if (!options.Enabled)
            return MfgFail.Conflict<PracticeLedgerDto>("costing_not_enabled", "Manufacturing costing is not switched on for this server.");
        if (!caller.Has(PermissionCatalog.ManufacturingCostsView))
            return MfgFail.Forbidden<PracticeLedgerDto>("Manufacturing cost reports need their own permission.");
        if (!CostPrivacy.RatesShown(caller))
            return MfgFail.Forbidden<PracticeLedgerDto>(CostPrivacy.WithheldMessage, CostPrivacy.WithheldCode);

        // No month asked: the company's month today, on the server's clock in the company's zone, never the browser's.
        var zone = StoreDocumentHistory.Zone(regional);
        var nowUtc = time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        var (year, month) = askedYear is int y && askedMonth is int m ? (y, m) : (today.Year, today.Month);
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
            return MfgFail.Invalid<PracticeLedgerDto>("invalid_period", "Choose a month.");
        var first = new DateOnly(year, month, 1);
        if (first > today) return MfgFail.Invalid<PracticeLedgerDto>("month_in_future", "That month has not begun.");
        var last = first.AddMonths(1).AddDays(-1);
        var startUtc = StartUtc(first, zone);
        var endUtc = StartUtc(first.AddMonths(1), zone);
        var nowExclusive = nowUtc.AddTicks(1);
        var monthToDate = endUtc > nowUtc;

        var reachable = ManufacturingAccess.Reachable(db, caller).Select(l => l.Id);
        var lineIds = await db.ProductionLines.AsNoTracking()
            .Where(l => reachable.Contains(l.Id) && (lineId == null || l.Id == lineId)).Select(l => l.Id).ToListAsync(ct);

        // The orders the month touched: live ones, those closed in it or after, and closed ones that gathered a cost in it.
        var lateActivity = (await db.LaborBookings.AsNoTracking()
                .Where(b => b.OrderKind == LaborOrderKind.Production && b.OrderId != null && b.LineId != null && lineIds.Contains(b.LineId.Value)
                    && b.Day >= first && b.Day <= last)
                .Select(b => b.OrderId!.Value).Distinct().ToListAsync(ct))
            .Concat(await db.ProductionOrderExpenses.AsNoTracking()
                .Where(e => e.AtUtc >= startUtc && e.AtUtc < endUtc).Select(e => e.OrderId).Distinct().ToListAsync(ct))
            .ToHashSet();
        var orders = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials).ThenInclude(x => x.Product)
            .Include(o => o.Stages).Include(o => o.Product).AsSplitQuery()
            .Where(o => lineIds.Contains(o.LineId) && o.CreatedAtUtc < endUtc
                && (Live.Contains(o.Status) || o.Status == ProductionOrderStatus.Cancelled
                    || (o.Status == ProductionOrderStatus.Completed && o.CompletedAtUtc >= startUtc) || lateActivity.Contains(o.Id)))
            .ToListAsync(ct);
        var orderIds = orders.Select(o => o.Id).ToList();
        var cancelledAt = (await db.ProductionOrderEvents.AsNoTracking()
                .Where(e => orderIds.Contains(e.OrderId) && e.Kind == ProductionOrderEventKind.Cancelled)
                .Select(e => new { e.OrderId, e.AtUtc }).ToListAsync(ct))
            .GroupBy(e => e.OrderId).ToDictionary(g => g.Key, g => g.Max(e => e.AtUtc));
        var moves = (await db.MaterialMovementLines.AsNoTracking()
                .Where(l => orderIds.Contains(l.Movement!.OrderId) && l.Movement.Kind != MaterialMovementKind.Scrap)
                .Select(l => new { l.Movement!.OrderId, l.MovementId, l.Movement.Number, l.Movement.Kind, l.Movement.AtUtc, l.OrderMaterialId, l.BaseQty, l.UnitCostSnapshot })
                .ToListAsync(ct))
            .GroupBy(l => l.OrderId)
            .ToDictionary(g => g.Key, g => g.Select(l => new Move(l.MovementId, l.Number, l.Kind, l.AtUtc, l.OrderMaterialId, l.BaseQty, l.UnitCostSnapshot)).ToList());
        var units = await db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct);

        var facts = new List<PracticeOrder>(orders.Count);
        foreach (var order in orders)
        {
            DateTime? closedAt = order.Status switch
            {
                ProductionOrderStatus.Completed => order.CompletedAtUtc,
                ProductionOrderStatus.Cancelled => cancelledAt.TryGetValue(order.Id, out var at) ? at : order.UpdatedAtUtc,
                _ => null,
            };
            // A cancelled order closed before the month, with nothing gathered in it, is not the month's.
            if (closedAt < startUtc && !lateActivity.Contains(order.Id)) continue;
            var (input, _, _) = await costs.InputAsync(order, units, ct, forStockValue: true);
            facts.Add(Facts(order, input, moves.GetValueOrDefault(order.Id) ?? [], closedAt, startUtc, endUtc, nowExclusive, zone));
        }

        var settings = await db.ManufacturingCostingSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new ManufacturingCostingSettings();
        var result = PracticeLedger.Build(
            new PracticeLedgerChoices(settings.Model, settings.MonthEndWorkInProgress, settings.LateCosts, settings.Variances,
                settings.ScrapRecovery == ScrapRecoveryBasis.PercentOfMaterial && settings.ScrapRecoveryPercent > 0m),
            year, month, facts, monthToDate);

        // ---------- slots to accounts ----------

        var map = await db.ManufacturingAccountMappings.AsNoTracking().Where(x => x.LedgerAccountId != null)
            .ToDictionaryAsync(x => x.Slot, x => x.LedgerAccountId!.Value, ct);
        var accountIds = map.Values.Distinct().ToList();
        var accounts = await db.LedgerAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        LedgerAccount? AccountOf(string slot) => map.TryGetValue(slot, out var id) ? accounts.GetValueOrDefault(id) : null;
        var branchIds = facts.Select(f => f.BranchId).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);

        var entries = result.Entries.Select(e =>
        {
            var branch = branches.GetValueOrDefault(e.BranchId);
            return new PracticeLedgerEntryDto(e.Kind, e.Day, e.OrderId, e.OrderNumber, e.BranchId, branch?.Code ?? "",
                branch?.NameAr ?? "", branch?.NameFr ?? "", branch?.NameEn ?? "", e.Document,
                e.Lines.Select(l =>
                {
                    var a = AccountOf(l.Slot);
                    return new PracticeLedgerLineDto(l.Slot, a?.Id, a?.Code, a?.NameAr, a?.NameFr, a?.NameEn, l.Debit, l.Credit);
                }).ToList());
        }).OrderBy(e => e.Day).ThenBy(e => e.OrderNumber, StringComparer.Ordinal).ToList();

        var lines = result.Entries.SelectMany(e => e.Lines).ToList();
        var used = lines.Select(l => l.Slot).ToHashSet();
        var totals = ManufacturingAccountSlots.All.Where(used.Contains).Select(slot =>
        {
            var a = AccountOf(slot);
            var mine = lines.Where(l => l.Slot == slot).ToList();
            return new PracticeLedgerAccountDto(slot, a?.Id, a?.Code, a?.NameAr, a?.NameFr, a?.NameEn, mine.Sum(l => l.Debit), mine.Sum(l => l.Credit));
        }).ToList();
        var unmapped = totals.Where(t => t.AccountId is null).Select(t => t.Slot).ToList();
        var checks = result.Checks.Select(c => new PracticeLedgerCheckDto(c.Code, c.Passed, c.Expected, c.Actual))
            // Phase D posts only to accounts the accountant named: an unmapped slot the month uses would block it.
            .Append(new PracticeLedgerCheckDto("accounts_mapped", unmapped.Count == 0, used.Count, used.Count - unmapped.Count))
            .ToList();

        // Phase D: the first month the accountant lets post, and this month's postings.
        var postings = await db.ManufacturingMonthPostings.AsNoTracking().Where(p => p.Year == year && p.Month == month)
            .OrderBy(p => p.Generation)
            .Select(p => new PracticeLedgerPostingDto(p.Generation, p.Entries, p.TotalDebit, p.PostedAtUtc, p.PostedBy, p.ReversedAtUtc, p.ReversedBy))
            .ToListAsync(ct);

        return MfgResult<PracticeLedgerDto>.Done(new PracticeLedgerDto(
            year, month, monthToDate,
            settings.Model.ToString(), settings.MonthEndWorkInProgress.ToString(), settings.LateCosts.ToString(), settings.Variances.ToString(),
            entries, totals,
            result.LateCosts.Select(l => new PracticeLedgerLateCostDto(l.OrderId, l.OrderNumber, l.Element, l.Amount)).ToList(),
            checks, unmapped, result.Notes, lines.Sum(l => l.Debit), lines.Sum(l => l.Credit),
            settings.PostingFromMonth?.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), postings));
    }

    /// <summary>One order as the month saw it: its cost at the month's start and end, its documents and releases in it.</summary>
    private static PracticeOrder Facts(
        ProductionOrder order, CostingInput input, IReadOnlyList<Move> moves, DateTime? closedAt,
        DateTime startUtc, DateTime endUtc, DateTime nowExclusive, TimeZoneInfo zone)
    {
        var roles = order.Materials.ToDictionary(m => m.Id, m => m.Role);
        // Each document valued once, per stock group, at three decimals: the entries and every cost point add these.
        var docs = moves.GroupBy(x => x.MovementId).Select(g =>
        {
            var head = g.First();
            var values = g.GroupBy(x => PracticeLedger.GroupOf(roles.GetValueOrDefault(x.MaterialId)))
                .OrderBy(v => v.Key)
                .Select(v => new PracticeMaterialValue(v.Key, Money(v.Sum(x => x.BaseQty * x.UnitCost)))).ToList();
            return new Doc(head.Number, head.AtUtc, head.Kind == MaterialMovementKind.Return, values);
        }).ToList();

        var closeExclusive = closedAt?.AddTicks(1);
        var valueAt = closeExclusive is DateTime c && c < nowExclusive ? c : nowExclusive;

        // Labour, overhead and expenses at a moment (exclusive), as the engine values what was recorded before it.
        (decimal Materials, decimal Labour, decimal Overhead, decimal Expenses, decimal Recovery, ProductionCostSheet Sheet) Elements(DateTime cutoff)
        {
            var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(cutoff.AddTicks(-1), DateTimeKind.Utc), zone));
            var sheet = ProductionCostEngine.Compute(AsOf(input, moves, cutoff, lastDay, completed: closeExclusive <= cutoff));
            return (docs.Where(d => d.AtUtc < cutoff).Sum(d => d.Signed),
                Money(sheet.Actual.Labour), Money(sheet.Actual.Overhead), Money(sheet.Actual.Expenses), Money(sheet.Actual.Recovery), sheet);
        }

        // The releases at the order's close (today while open): partial ones rounded, the last of a closed order the rest.
        var valued = Elements(valueAt);
        var releaseTotal = valued.Materials + valued.Labour + valued.Overhead + valued.Expenses - valued.Recovery;
        var releaseValues = new Dictionary<Guid, decimal>();
        var carried = 0m;
        foreach (var r in valued.Sheet.Releases)
        {
            var value = r.CarriesRemainder ? releaseTotal - carried : Money(r.Value);
            carried += value;
            releaseValues[r.Id] = value;
        }

        PracticeCostPoint Point(DateTime cutoff, bool stopAtClose)
        {
            if (stopAtClose && closeExclusive is DateTime close && close < cutoff) cutoff = close;
            if (nowExclusive < cutoff) cutoff = nowExclusive;
            var e = Elements(cutoff);
            var released = input.Releases.Where(r => r.AtUtc < cutoff).Sum(r => releaseValues.GetValueOrDefault(r.Id));
            return new PracticeCostPoint(e.Materials, e.Labour, e.Overhead, e.Expenses, released, e.Recovery);
        }

        DateOnly Day(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
        var closedBeforeEnd = closedAt is DateTime closed && closed < endUtc;
        return new PracticeOrder(
            order.Id, order.Number, order.LocationId,
            OpenAtStart: order.CreatedAtUtc < startUtc && (closedAt is null || closedAt >= startUtc),
            OpenAtEnd: closedAt is null || closedAt >= endUtc,
            ClosedDay: closedAt is DateTime at && at >= startUtc && at < endUtc ? Day(at) : null,
            Start: Point(startUtc, stopAtClose: true),
            End: Point(endUtc, stopAtClose: true),
            Documents: docs.Where(d => d.AtUtc >= startUtc && d.AtUtc < endUtc)
                .Select(d => new PracticeMaterialDoc(d.Number, Day(d.AtUtc), d.IsReturn, d.Values)).ToList(),
            Releases: input.Releases.Where(r => r.AtUtc >= startUtc && r.AtUtc < endUtc)
                .Select(r => new PracticeRelease(r.Number, r.Day, releaseValues.GetValueOrDefault(r.Id))).ToList(),
            // What it cost after it closed, within the month: from the later of its close and the month's start to the month's end.
            LateFrom: closedBeforeEnd ? Point(closeExclusive!.Value > startUtc ? closeExclusive.Value : startUtc, stopAtClose: false) : null,
            LateTo: closedBeforeEnd ? Point(endUtc, stopAtClose: false) : null);
    }

    /// <summary>The engine's input as it stood before a moment: the movements, bookings, expenses and releases recorded by then.</summary>
    private static CostingInput AsOf(CostingInput input, IReadOnlyList<Move> moves, DateTime cutoff, DateOnly lastDay, bool completed)
    {
        var before = moves.Where(x => x.AtUtc < cutoff).ToList();
        var materials = input.Materials.Select(m =>
        {
            var issued = before.Where(x => x.MaterialId == m.Id && x.Kind == MaterialMovementKind.Issue).ToList();
            var returned = before.Where(x => x.MaterialId == m.Id && x.Kind == MaterialMovementKind.Return).ToList();
            // Consumption and scrap only split what is still at the line; the cost to date and the releases do not read them.
            return m with
            {
                IssuedBaseQty = issued.Sum(x => x.BaseQty), ReturnedBaseQty = returned.Sum(x => x.BaseQty),
                ConsumedBaseQty = 0m, ScrappedBaseQty = 0m,
                IssuedValue = issued.Sum(x => x.BaseQty * x.UnitCost), ReturnedValue = returned.Sum(x => x.BaseQty * x.UnitCost),
            };
        }).ToList();
        var releases = input.Releases.Where(r => r.AtUtc < cutoff).ToList();
        return input with
        {
            Completed = completed,
            AcceptedBaseQty = releases.Sum(r => r.BaseQty),
            Materials = materials,
            Bookings = input.Bookings.Where(b => b.Day <= lastDay).ToList(),
            Expenses = input.Expenses.Where(e => e.AtUtc < cutoff).ToList(),
            Releases = releases,
        };
    }

    private static DateTime StartUtc(DateOnly day, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone);

    private static decimal Money(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
