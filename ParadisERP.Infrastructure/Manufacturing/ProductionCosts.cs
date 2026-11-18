using ParadisERP.Application.Contracts;
using ParadisERP.Application.Manufacturing;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Manufacturing;

/// <summary>
/// Manufacturing costing, Phase A (docs/manufacturing/costing-plan.md): an order's cost sheet, the work
/// in progress and the labour absorbed, computed from what Phase 2 recorded by
/// <see cref="ProductionCostEngine"/>. Read-only: nothing is frozen, posted or written, and
/// <c>Product.CostPrice</c>, payroll and attendance are only read. Off unless this server's
/// configuration says <c>Manufacturing:Costing</c>.
///
/// <para>
/// Reading costs needs <c>manufacturing.costs.view</c>. Labour money for one order needs pay-rate access too
/// (<see cref="CostPrivacy"/>; owner, 2026-11-03): without <c>payroll.manage</c> and
/// <c>manufacturing.costs.rates.view</c>, an order's labour and every figure that gives it back — its total and unit
/// cost, released value, work in progress, what stays in charges, spoilage, the labour estimate and the variance
/// total — are left out, and so are the hours of an order one employee worked and the hours payroll paid. Lines'
/// sums stand in for them, withheld for a line of one order or one worker. An order or line out of the caller's
/// reach is not found, as everywhere in manufacturing.
/// </para>
/// </summary>
public sealed class ProductionCosts(
    PosDbContext db, LabourRates rates, ManufacturingCostingOptions options, IRegionalSettingsProvider regional, TimeProvider time)
{
    private static readonly ProductionOrderStatus[] Open =
        [ProductionOrderStatus.Open, ProductionOrderStatus.OnHold, ProductionOrderStatus.CompletionRequested];

    /// <summary>
    /// Each line's crew rate and how many employees' standard rates it averages, read once per request: a
    /// report reads many orders of the same line.
    /// </summary>
    private readonly Dictionary<Guid, (decimal? Rate, int People)> crewRates = [];

    public async Task<MfgResult<ProductionCostSheetDto>> SheetAsync(MfgCaller caller, Guid id, CancellationToken ct)
    {
        if (Refusal<ProductionCostSheetDto>(caller) is { } refusal) return refusal;
        var order = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials).ThenInclude(m => m.Product)
            .Include(o => o.Stages).Include(o => o.Product).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || !await ManufacturingAccess.ReachesAsync(db, caller, order.LineId, ct))
            return MfgFail.NotFound<ProductionCostSheetDto>("Production order");

        var units = await UnitCodesAsync(ct);
        var (input, standardRate, _) = await InputAsync(order, units, ct);
        var sheet = ProductionCostEngine.Compute(input);
        var money = CostPrivacy.RatesShown(caller);
        var employeeIds = sheet.Labour.Select(l => l.EmployeeId).Distinct().ToList();
        // Without the rate keys, the order's labour money and whatever gives it back stay out (CostPrivacy); an order
        // one employee worked keeps its hours too, and the labour estimate over the standard hours is the crew's rate.
        var withheld = !money;
        var hoursWithheld = withheld && employeeIds.Count == 1;
        var estimateWithheld = withheld;
        var people = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new { e.Number, e.FullName }, ct);
        var actual = Elements(sheet.Actual);
        var total = Money(sheet.Actual.Total);
        var releases = Released(sheet, total);
        var released = releases.Sum(r => r.Value ?? 0m);
        var completed = order.Status == ProductionOrderStatus.Completed;

        return MfgResult<ProductionCostSheetDto>.Done(new ProductionCostSheetDto(
            order.Id, order.Number, order.Status.ToString(), BaseUnit(order, units),
            order.PlannedBaseQty, order.AcceptedBaseQty, order.RejectedBaseQty, completed,
            Estimate(sheet.Estimate, estimateWithheld), estimateWithheld ? null : Unit(sheet.EstimatedUnitCost),
            withheld ? actual with { Labour = null, Total = null } : actual, withheld ? null : Unit(sheet.ActualUnitCost),
            Money(sheet.AtLineValue), withheld ? null : released, withheld ? null : completed ? 0m : total - released,
            withheld ? null : Money(sheet.KeptInCharges),
            hoursWithheld ? null : sheet.LabourHours, input.StandardHours, Round(sheet.StandardHoursForOutput, 6),
            money ? Unit(standardRate) : null,
            Money(input.Policy.OverheadPerLabourHour), money, hoursWithheld, estimateWithheld,
            sheet.Materials.Select(m => new CostMaterialLineDto(
                m.Id, m.Sku, m.Role.ToString(), m.BaseUnitCode, m.RequiredBaseQty, Unit(m.EstimatedUnitCost)!.Value, Money(m.EstimatedValue),
                m.NetBaseQty, Money(m.NetValue), Unit(m.IssueAverage)!.Value, Money(m.ConsumedValue), Money(m.ScrapValue),
                m.AtLineBaseQty, Money(m.AtLineValue), m.StandardBaseQty is decimal q ? Round(q, 6) : null,
                Money(m.UsageVariance), Money(m.PriceVariance))).ToList(),
            sheet.Labour.Select(l =>
            {
                var person = people.GetValueOrDefault(l.EmployeeId);
                return new CostLabourLineDto(l.EmployeeId, person?.Number ?? "", person?.FullName ?? "", l.Year, l.Month,
                    hoursWithheld ? null : l.Hours,
                    l.Kind.ToString(), l.Reason, money ? Unit(l.Rate) : null, money ? Money(l.Cost) : null,
                    l.Kind != LabourRateKind.None && l.WithoutEmployerSocial,
                    hoursWithheld ? null : l.OvertimeHours, money ? Unit(l.OvertimeRate) : null);
            }).ToList(),
            withheld ? releases.Select(r => r with { Value = null, UnitValue = null }).ToList() : releases,
            sheet.Variances is { } v
                ? new CostVariancesDto(Money(v.MaterialUsage), Money(v.MaterialPrice),
                    money ? Money(v.LabourEfficiency) : null, money ? Money(v.LabourRate) : null,
                    withheld ? null : Money(v.LabourEfficiency + v.LabourRate),
                    Money(v.Overhead), Money(v.Expenses), Money(v.Recovery), withheld ? null : Money(v.Total))
                : null,
            withheld ? null : Money(sheet.Spoilage),
            // The labour notes count hours: on an order one employee worked they keep their word and lose the figure. The
            // last release's shortfall is the total less the others, so it goes with the total.
            sheet.Notes.Select(n => new CostNoteDto(n.Code,
                n.Value is decimal value
                && !(hoursWithheld && n.Code is ProductionCostEngine.NoteStandardRate or ProductionCostEngine.NoteUnvaluedLabour
                    or ProductionCostEngine.NoteOvertimeWithoutPremium)
                && !(withheld && n.Code == ProductionCostEngine.NoteFinalReleaseNegative)
                    ? Round(value, 6) : null)).ToList(),
            sheet.Status,
            new CostElementStatusesDto(sheet.Statuses.Materials, sheet.Statuses.Consumables, sheet.Statuses.Accessories,
                sheet.Statuses.Labour, sheet.Statuses.Overhead, sheet.Statuses.Expenses, sheet.Statuses.Recovery),
            sheet.EmployerSocialMissing, withheld));
    }

    /// <summary>
    /// R2: every live order of the lines the caller reaches, its cost to date against what it released, and each line's
    /// sum. Without rate access an order's own figures stay out, a line's sums are shown from two orders and two
    /// workers, and the report's totals only when no line's are withheld (else they would give that line back).
    /// </summary>
    public async Task<MfgResult<WorkInProgressReportDto>> WorkInProgressAsync(MfgCaller caller, Guid? lineId, CancellationToken ct)
    {
        if (Refusal<WorkInProgressReportDto>(caller) is { } refusal) return refusal;
        var reachable = ManufacturingAccess.Reachable(db, caller).Select(l => l.Id);
        var lines = await db.ProductionLines.AsNoTracking().Where(l => reachable.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Code, ct);
        var ids = lineId is Guid one ? lines.Keys.Where(k => k == one).ToList() : lines.Keys.ToList();
        var orders = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials).ThenInclude(m => m.Product)
            .Include(o => o.Stages).Include(o => o.Product).AsSplitQuery()
            .Where(o => ids.Contains(o.LineId) && Open.Contains(o.Status))
            .OrderBy(o => o.Number).ToListAsync(ct);

        var units = await UnitCodesAsync(ct);
        var money = CostPrivacy.RatesShown(caller);
        var full = new List<(WorkInProgressRowDto Row, IReadOnlyCollection<Guid> Workers)>(orders.Count);
        // One order at a time: this business runs a few dozen live orders, and each sheet is the one the order page shows.
        foreach (var order in orders)
        {
            var (input, _, _) = await InputAsync(order, units, ct);
            var sheet = ProductionCostEngine.Compute(input);
            var total = Money(sheet.Actual.Total);
            var released = Released(sheet, total).Sum(r => r.Value ?? 0m);
            full.Add((new WorkInProgressRowDto(
                order.Id, order.Number, order.Status.ToString(), order.LineId, lines.GetValueOrDefault(order.LineId, ""),
                order.Product?.Sku ?? "", BaseUnit(order, units), order.PlannedBaseQty, order.AcceptedBaseQty,
                total, released, Money(sheet.AtLineValue), total - released,
                sheet.Status != ProductionCostEngine.StatusApproved), input.Bookings.Select(b => b.EmployeeId).ToHashSet()));
        }

        var lineSums = full.GroupBy(r => r.Row.LineId).Select(g =>
        {
            var workers = g.SelectMany(r => r.Workers).Distinct().Count();
            var shown = CostPrivacy.LineSumShown(money, g.Count(), workers);
            return new WorkInProgressLineDto(g.Key, lines.GetValueOrDefault(g.Key, ""), g.Count(), workers,
                shown ? g.Sum(r => r.Row.CostToDate) : null, shown ? g.Sum(r => r.Row.ReleasedValue) : null,
                g.Sum(r => r.Row.AtLineValue), shown ? g.Sum(r => r.Row.WorkInProgress) : null, !shown);
        }).OrderBy(l => l.LineCode, StringComparer.Ordinal).ToList();
        var rows = money
            ? full.Select(r => r.Row).ToList()
            : full.Select(r => r.Row with { CostToDate = null, ReleasedValue = null, WorkInProgress = null }).ToList();
        var totalsShown = money || lineSums.All(l => !l.Withheld);
        return MfgResult<WorkInProgressReportDto>.Done(new WorkInProgressReportDto(
            rows,
            totalsShown ? full.Sum(r => r.Row.CostToDate) : null, totalsShown ? full.Sum(r => r.Row.ReleasedValue) : null,
            full.Sum(r => r.Row.AtLineValue), totalsShown ? full.Sum(r => r.Row.WorkInProgress) : null,
            lineSums, money));
    }

    /// <summary>
    /// R4: one payroll month, per employee booked on the lines the caller reaches — the hours on orders
    /// and on other rows, and (with payroll.manage) what payroll cost, what the orders absorbed at the
    /// month's rate, and what stayed in the wage charges. Under "premium to overtime hours" (C-05) the orders absorb
    /// their normal hours at the rate without the premium and their marked overtime hours at the overtime rate; the
    /// premium no order hour carried is said per employee and in total.
    /// </summary>
    public async Task<MfgResult<LabourAbsorptionReportDto>> LabourAbsorptionAsync(
        MfgCaller caller, int? askedYear, int? askedMonth, Guid? lineId, CancellationToken ct)
    {
        if (Refusal<LabourAbsorptionReportDto>(caller) is { } refusal) return refusal;
        // No month asked: the company's month today, on the server's clock in the company's zone, never the browser's.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), StoreDocumentHistory.Zone(regional)).DateTime);
        var (year, month) = askedYear is int y && askedMonth is int m ? (y, m) : (today.Year, today.Month);
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
            return MfgFail.Invalid<LabourAbsorptionReportDto>("invalid_period", "Choose a month.");
        var reachable = ManufacturingAccess.Reachable(db, caller).Select(l => l.Id);
        // Production hours only: maintenance hours (Phase M3) have no line and are not absorbed by a production line —
        // they have their own report; the attendance ceiling already counts both.
        var bookings = await db.LaborBookings.AsNoTracking()
            .Where(b => b.OrderKind == LaborOrderKind.Production && b.LineId != null && reachable.Contains(b.LineId.Value)
                && (lineId == null || b.LineId == lineId)
                && b.PayrollYear == year && b.PayrollMonth == month)
            .Select(b => new { b.EmployeeId, b.OrderId, LineId = b.LineId!.Value, b.Hours, b.OvertimeHours }).ToListAsync(ct);
        var employees = bookings.Select(b => b.EmployeeId).Distinct().ToList();
        var people = await db.Employees.AsNoTracking().Where(e => employees.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new { e.Number, e.FullName }, ct);
        var rated = await rates.ForAsync(employees.Select(e => (e, year, month)), ct);
        var policy = await PolicyAsync(false, ct);
        var premiumPolicy = policy.LabourAbsorption == LabourAbsorption.PremiumToOvertime;
        // What a booking's hours cost at the month's rates under the policy (C-05; the blended rate: hours × rate, as before).
        decimal Absorbed(Guid employee, decimal hours, decimal overtimeHours)
        {
            var rate = rated.GetValueOrDefault((employee, year, month)) ?? LabourRate.Missing("no_rate");
            var rates = premiumPolicy ? ProductionCostEngine.HourRatesOf(rate, policy with { EmployerBurdenPercent = 0m }) : null;
            if (rates is null || rates.Premium == 0m) return hours * (rate.PerHour ?? 0m);
            var overtime = Math.Clamp(overtimeHours, 0m, hours);
            return (hours - overtime) * rates.Normal!.Value + overtime * rates.Overtime!.Value;
        }
        var money = CostPrivacy.RatesShown(caller);
        // The order hours marked overtime and what they carried of the month's premium (C-05).
        decimal PremiumOnOrders(Guid employee, LabourRate rate)
        {
            if (!premiumPolicy) return 0m;
            var rates = ProductionCostEngine.HourRatesOf(rate, policy with { EmployerBurdenPercent = 0m });
            return rates.Premium * bookings.Where(b => b.EmployeeId == employee && b.OrderId != null).Sum(b => Math.Clamp(b.OvertimeHours, 0m, b.Hours));
        }

        var rows = employees.Select(id =>
        {
            var mine = bookings.Where(b => b.EmployeeId == id).ToList();
            var orderHours = mine.Where(b => b.OrderId != null).Sum(b => b.Hours);
            var otherHours = mine.Where(b => b.OrderId == null).Sum(b => b.Hours);
            var rate = rated.GetValueOrDefault((id, year, month)) ?? LabourRate.Missing("no_rate");
            var absorbed = premiumPolicy
                ? mine.Where(b => b.OrderId != null).Sum(b => Absorbed(id, b.Hours, b.OvertimeHours))
                : orderHours * (rate.PerHour ?? 0m);
            var overtimeHours = mine.Where(b => b.OrderId != null).Sum(b => Math.Clamp(b.OvertimeHours, 0m, b.Hours));
            var actual = rate.Kind == LabourRateKind.Actual;
            var person = people.GetValueOrDefault(id);
            return new LabourAbsorptionRowDto(
                id, person?.Number ?? "", person?.FullName ?? "", orderHours, otherHours, rate.Kind.ToString(), rate.Reason,
                // The hours payroll paid: with the month's pay they are the rate, so they go with the money.
                money ? rate.PaidHours : null,
                money ? Money(rate.EmployerCost) : null,
                money ? Unit(rate.PerHour) : null,
                money ? Money(absorbed) : null,
                money && rate.EmployerCost is decimal cost ? Money(cost - absorbed) : null,
                rate.Kind != LabourRateKind.None && rate.WithoutEmployerSocial,
                overtimeHours,
                money && actual ? rate.PaidOvertimeHours : null,
                money && actual ? Money(rate.OvertimePremium) : null,
                money && actual && premiumPolicy ? Money(rate.OvertimePremium - PremiumOnOrders(id, rate)) : null);
        }).OrderBy(r => r.EmployeeNumber, StringComparer.Ordinal).ToList();

        // Each line's labour on its orders this month (the owner's "by line and month", 2026-11-03): at the month's rates,
        // shown without rate access from two orders and two workers, since below that it is one order's or one person's.
        var lineIds = bookings.Where(b => b.OrderId != null).Select(b => b.LineId).Distinct().ToList();
        var lineCodes = await db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Code, ct);
        var lineSums = bookings.Where(b => b.OrderId != null).GroupBy(b => b.LineId).Select(g =>
        {
            var orders = g.Select(b => b.OrderId).Distinct().Count();
            var workers = g.Select(b => b.EmployeeId).Distinct().Count();
            var shown = CostPrivacy.LineSumShown(money, orders, workers);
            var absorbed = g.Sum(b => Absorbed(b.EmployeeId, b.Hours, b.OvertimeHours));
            return new LabourLineMonthDto(g.Key, lineCodes.GetValueOrDefault(g.Key, ""), orders, workers, g.Sum(b => b.Hours),
                shown ? Money(absorbed) : null, !shown);
        }).OrderBy(l => l.LineCode, StringComparer.Ordinal).ToList();

        // Payroll's cost is known only for the months it approved: the totals add those rows, and stay
        // unknown (null) — never 0 — when no row has one.
        var paid = rows.Where(r => r.EmployerCost is not null).ToList();
        return MfgResult<LabourAbsorptionReportDto>.Done(new LabourAbsorptionReportDto(
            year, month, money, rows, rows.Sum(r => r.OrderHours), rows.Sum(r => r.OtherHours),
            money && paid.Count > 0 ? paid.Sum(r => r.EmployerCost!.Value) : null,
            money ? rows.Sum(r => r.AbsorbedCost ?? 0m) : null,
            money && paid.Count > 0 ? paid.Sum(r => r.UnabsorbedCost ?? 0m) : null,
            rows.Any(r => r.WithoutEmployerSocial), lineSums,
            policy.LabourAbsorption.ToString(),
            money && premiumPolicy && rows.Any(r => r.PremiumInCharges is not null) ? rows.Sum(r => r.PremiumInCharges ?? 0m) : null));
    }

    /// <summary>Whether this caller is shown costs: the server computes them and the caller holds manufacturing.costs.view.</summary>
    public bool Shows(MfgCaller caller) => options.Enabled && caller.Has(PermissionCatalog.ManufacturingCostsView);

    /// <summary>The costing preview's value of every release of these orders, by release id, rounded as on the sheet.</summary>
    public async Task<Dictionary<Guid, CostReleaseDto>> ReleaseValuesAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, CostReleaseDto>();
        if (orderIds.Count == 0) return result;
        var orders = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials).ThenInclude(m => m.Product)
            .Include(o => o.Stages).AsSplitQuery().Where(o => orderIds.Contains(o.Id)).ToListAsync(ct);
        var units = await UnitCodesAsync(ct);
        foreach (var order in orders)
        {
            var (input, _, _) = await InputAsync(order, units, ct);
            var sheet = ProductionCostEngine.Compute(input);
            foreach (var release in Released(sheet, Money(sheet.Actual.Total))) result[release.Id] = release;
        }
        return result;
    }

    /// <summary>
    /// What the engine needs for one order, read once: the materials at their movements' values, the
    /// bookings and their rates, the expenses, and each release with the output approved by its moment.
    /// Returns the crew's standard rate the estimate used, too, and how many employees' rates it averages.
    /// </summary>
    /// <param name="forStockValue">
    /// True where the cost becomes a value — a frozen cost sheet, the practice ledger: the employer burden counts
    /// only when the admin put it in stock value (C-06 option 3). False for previews and reports, where it counts
    /// in option 2 as well.
    /// </param>
    internal async Task<(CostingInput Input, decimal? StandardRate, int StandardRatePeople)> InputAsync(
        ProductionOrder order, IReadOnlyDictionary<Guid, string> units, CancellationToken ct, bool forStockValue = false)
    {
        var moved = await db.MaterialMovementLines.AsNoTracking().Where(l => l.Movement!.OrderId == order.Id)
            .Select(l => new { l.OrderMaterialId, l.Movement!.Kind, l.BaseQty, l.UnitCostSnapshot }).ToListAsync(ct);
        decimal Valued(Guid material, MaterialMovementKind kind) =>
            moved.Where(l => l.OrderMaterialId == material && l.Kind == kind).Sum(l => l.BaseQty * l.UnitCostSnapshot);
        var materials = order.Materials.OrderBy(m => m.Sequence).Select(m => new CostingMaterial(
            m.Id, m.Product?.Sku ?? "", m.Role, units.GetValueOrDefault(m.BaseUnitId, ""), m.RequiredBaseQty, m.EstimatedUnitCost,
            m.IssuedBaseQty, m.ReturnedBaseQty, m.ConsumedBaseQty, m.ScrappedBaseQty,
            Valued(m.Id, MaterialMovementKind.Issue), Valued(m.Id, MaterialMovementKind.Return))).ToList();

        var bookings = await db.LaborBookings.AsNoTracking()
            .Where(b => b.OrderId == order.Id && b.OrderKind == LaborOrderKind.Production)
            .Select(b => new CostingBooking(b.EmployeeId, b.StageId ?? Guid.Empty, b.Day, b.PayrollYear, b.PayrollMonth, b.Hours, b.OvertimeHours))
            .ToListAsync(ct);
        var labourRates = await rates.ForAsync(bookings.Select(b => (b.EmployeeId, b.PayrollYear, b.PayrollMonth)), ct);

        var recorded = await db.ProductionOrderExpenses.AsNoTracking().Where(e => e.OrderId == order.Id)
            .Select(e => new { e.Id, e.Amount, e.AtUtc, e.PaymentVoucherId }).ToListAsync(ct);
        // An expense is backed once its voucher is approved or paid; read only, the voucher is never touched.
        var voucherIds = recorded.Where(e => e.PaymentVoucherId is not null).Select(e => e.PaymentVoucherId!.Value).Distinct().ToList();
        HashSet<Guid> backed = voucherIds.Count == 0 ? [] : (await db.PaymentVouchers.AsNoTracking()
            .Where(v => voucherIds.Contains(v.Id) && (v.Status == PaymentVoucherStatus.Approved || v.Status == PaymentVoucherStatus.Paid))
            .Select(v => v.Id).ToListAsync(ct)).ToHashSet();
        var expenses = recorded.Select(e => new CostingExpense(e.Id, e.Amount, e.AtUtc,
            e.PaymentVoucherId is Guid voucher && backed.Contains(voucher))).ToList();

        var zone = StoreDocumentHistory.Zone(regional);
        var outputStage = order.Stages.FirstOrDefault(s => s.IsOutput)?.Id;
        var approvedOutput = outputStage is Guid stage
            ? await db.ProductionLogRows.AsNoTracking()
                .Where(r => r.OrderId == order.Id && r.StageId == stage && r.Log!.Status == ProductionLogStatus.Approved)
                .Select(r => new { r.CompletedQty, r.Log!.ApprovedAtUtc }).ToListAsync(ct)
            : [];
        var releases = (await db.OutputReleases.AsNoTracking().Where(r => r.OrderId == order.Id).ToListAsync(ct))
            .Select(r => new CostingRelease(r.Id, r.Number, r.BaseQty, r.AtUtc,
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.AtUtc, DateTimeKind.Utc), zone)),
                approvedOutput.Where(o => o.ApprovedAtUtc <= r.AtUtc).Sum(o => o.CompletedQty)))
            .ToList();

        var standardHours = order.Stages.Sum(s => s.PlannedMinutes * Math.Max(1, s.ExpectedWorkers)) / 60m;
        var (standardRate, ratePeople) = await CrewRateAsync(order, bookings, ct);
        return (new CostingInput(
            order.PlannedBaseQty, order.AcceptedBaseQty, order.RejectedBaseQty, order.Status == ProductionOrderStatus.Completed,
            standardHours, standardRate, materials, bookings, labourRates, expenses, releases,
            await PolicyAsync(forStockValue, ct)), standardRate, ratePeople);
    }

    /// <summary>
    /// The costing policy as the admin set it (Phase C1, owner 2026-11-08), read once per request; the defaults
    /// when nothing was saved. Approved only once the accountant confirmed the choices in the panel (2026-11-09).
    /// </summary>
    private CostingPolicy? policy;
    private bool policyForValue;
    internal async Task<CostingPolicy> PolicyAsync(bool forStockValue, CancellationToken ct)
    {
        if (policy is not null && policyForValue == forStockValue) return policy;
        var s = await db.ManufacturingCostingSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is null) return policy = CostingPolicy.Default;
        policyForValue = forStockValue;
        return policy = PolicyOf(s, forStockValue);
    }

    /// <summary>
    /// The costing policy a settings row gives; the defaults without one. Shared with maintenance, whose job costs follow
    /// the burden, expense VAT and labour absorption chosen here unless Maintenance › Settings says otherwise (MS-13).
    /// </summary>
    internal static CostingPolicy PolicyOf(ManufacturingCostingSettings? s, bool forStockValue)
    {
        if (s is null) return CostingPolicy.Default;
        var burden = s.EmployerBurdenUse == EmployerBurdenUse.StockValue
            || (!forStockValue && s.EmployerBurdenUse == EmployerBurdenUse.ReportsOnly) ? s.EmployerBurdenPercent : 0m;
        return new CostingPolicy(s.OverheadPerLabourHour, Approved: s.AccountantApproved, (ReleaseValuationRule)(int)s.ReleaseRule, burden,
            ExpenseVatPercent: s.ExpenseBasis == ExpenseBasis.BeforeRecoverableVat ? s.ExpenseVatPercent : 0m,
            ScrapRecoveryPercent: s.ScrapRecovery == ScrapRecoveryBasis.PercentOfMaterial ? s.ScrapRecoveryPercent : 0m,
            LabourAbsorption: (LabourAbsorption)(int)s.LabourAbsorption);
    }

    /// <summary>
    /// The estimate's rate: the average standard rate of the line's current crew, or of the people who
    /// booked hours on the order when the line names nobody; null when neither gives a rate. Also how many
    /// employees' rates it averages: at one, the rate is that person's.
    /// </summary>
    private async Task<(decimal? Rate, int People)> CrewRateAsync(ProductionOrder order, IReadOnlyList<CostingBooking> bookings, CancellationToken ct)
    {
        if (crewRates.TryGetValue(order.LineId, out var known)) return known;
        var crew = await db.ProductionLineEmployees.AsNoTracking()
            .Where(e => e.LineId == order.LineId && e.EndedOn == null).Select(e => e.EmployeeId).Distinct().ToListAsync(ct);
        var named = crew.Count > 0;
        if (!named) crew = bookings.Select(b => b.EmployeeId).Distinct().ToList();
        var standard = (await rates.StandardAsync(crew, ct)).Values.Where(r => r.PerHour is not null).Select(r => r.PerHour!.Value).ToList();
        (decimal? Rate, int People) result = (standard.Count == 0 ? null : standard.Average(), standard.Count);
        // Only a named crew is the line's; the bookings' people are this order's own.
        if (named) crewRates[order.LineId] = result;
        return result;
    }

    private MfgResult<T>? Refusal<T>(MfgCaller caller)
    {
        if (!options.Enabled)
            return MfgFail.Conflict<T>("costing_not_enabled", "Manufacturing costing is not switched on for this server.");
        if (!caller.Has(PermissionCatalog.ManufacturingCostsView))
            return MfgFail.Forbidden<T>("Seeing manufacturing costs needs its own permission.");
        return null;
    }

    private static string BaseUnit(ProductionOrder order, IReadOnlyDictionary<Guid, string> units) =>
        order.Product?.BaseUnitId is Guid unit ? units.GetValueOrDefault(unit, "") : "";

    private Task<Dictionary<Guid, string>> UnitCodesAsync(CancellationToken ct) =>
        db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct);

    /// <summary>
    /// The releases at three decimals, adding up: each partial release rounded, and the last release of a
    /// completed order the rounded total less the others, so it absorbs the rounding (costing plan §2.1).
    /// </summary>
    private static List<CostReleaseDto> Released(ProductionCostSheet sheet, decimal roundedTotal)
    {
        var result = new List<CostReleaseDto>(sheet.Releases.Count);
        var carried = 0m;
        foreach (var r in sheet.Releases)
        {
            var value = r.CarriesRemainder ? roundedTotal - carried : Money(r.Value);
            carried += value;
            result.Add(new CostReleaseDto(r.Id, r.Number, r.BaseQty, value, Unit(r.UnitValue)!.Value, r.CarriesRemainder));
        }
        return result;
    }

    private static CostEstimateDto Estimate(CostElements e, bool labourWithheld) => new(
        Money(e.Materials), Money(e.Consumables), Money(e.Accessories), labourWithheld ? null : Money(e.Labour), Money(e.Overhead),
        Money(e.Expenses), Money(e.Recovery), labourWithheld ? null : Money(e.Total));

    private static CostElementsDto Elements(CostElements e) => new(
        Money(e.Materials), Money(e.Consumables), Money(e.Accessories), Money(e.Labour), Money(e.Overhead), Money(e.Expenses),
        Money(e.Recovery), Money(e.Total));

    private static decimal Money(decimal value) => Round(value, 3);
    private static decimal? Money(decimal? value) => value is decimal v ? Round(v, 3) : null;
    private static decimal? Unit(decimal? value) => value is decimal v ? Round(v, 6) : null;
    private static decimal Round(decimal value, int places) => Math.Round(value, places, MidpointRounding.AwayFromZero);
}
