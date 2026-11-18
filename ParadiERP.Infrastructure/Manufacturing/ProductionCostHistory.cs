using ParadiERP.Application.Contracts;
using ParadiERP.Application.Manufacturing;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Application.Manufacturing.CostReconciliation;

namespace ParadiERP.Infrastructure.Manufacturing;

/// <summary>
/// Manufacturing costing B2 (docs/manufacturing/costing-b2.md): B1's frozen cost sheets, their late costs and their
/// reconciliation, read only. Every query is untracked and nothing is ever saved: no sheet, late cost, ledger entry,
/// stock value, <c>Product.CostPrice</c> or payroll row is written, and an integrity failure is reported, never
/// repaired. Figures come from the stored rows; the stored source capture is only replayed to check them and to
/// derive what B1 did not store (variances, scrap value). The capture itself is never returned.
///
/// <para>
/// Off unless <c>Manufacturing:Costing</c> is on; every route needs <c>manufacturing.costs.view</c> and, since the
/// owner chose the complete labour privacy (2026-11-03, <see cref="CostPrivacy"/>), pay-rate access too —
/// <c>manufacturing.costs.rates.view</c> and <c>payroll.manage</c> — because every register here is labour by
/// order. Without them each answer is refused in words (<c>payroll_details_withheld</c>). An order or line out of
/// the caller's reach is not found, and left out of every register's rows and totals. The redactions below for a
/// reader without rates (late costs' sources, the labour split, a one-employee order's hours) stay as a second line.
/// </para>
/// </summary>
public sealed class ProductionCostHistory(
    PosDbContext db, ProductionCosts costs, ManufacturingCostingOptions options, IRegionalSettingsProvider regional, TimeProvider time)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxLivePageSize = 50;
    public const int MaxExportRows = 5000;
    /// <summary>An export with the live comparison recomputes every row it lists, so it lists fewer.</summary>
    public const int MaxLiveExportRows = 200;
    public const int MaxPeriodDays = 366;

    public const string BasisCompleted = "completed";
    public const string BasisFrozen = "frozen";
    public const string BasisRecorded = "recorded";

    public const string StateFrozen = "frozen";
    public const string StateNotFrozen = "not_frozen";
    public const string StateNotFrozenAtDate = "not_frozen_at_date";
    public const string StateNotCompleted = "not_completed";

    private const string FinalizationNotImplemented = "not_implemented";

    /// <summary>What every B2 figure is, said once per answer (translated on screen and in exports).</summary>
    private static readonly string[] CommonLimitations =
    [
        "provisional_only", "record_time_not_accounting_date", "late_creator_not_recorded", "late_kind_free_text",
        "payroll_run_not_captured", "release_values_frozen_at_sheet_time", "history_guard_application_level",
    ];

    /// <summary>A register's filters. Days are company days; <c>All</c> ignores paging (exports).</summary>
    public sealed record Query(
        Guid? LineId = null, DateOnly? From = null, DateOnly? To = null, string? Basis = null, DateOnly? AsOf = null,
        int? Page = null, int? PageSize = null, string? Kind = null, string? SourceState = null, string? State = null,
        string? Element = null, string? Treatment = null, string? Flag = null, string? Code = null, string? Severity = null,
        bool Live = false, bool All = false);

    // ======================================================================================
    // R-B1: one order
    // ======================================================================================

    public async Task<MfgResult<OrderCostReconciliationDto>> OrderAsync(MfgCaller caller, Guid orderId, DateOnly? asOf, bool live, CancellationToken ct)
    {
        if (Refusal<OrderCostReconciliationDto>(caller) is { } refusal) return refusal;
        var order = await db.ProductionOrders.AsNoTracking().Where(o => o.Id == orderId)
            .Select(o => new OrderHead(o.Id, o.Number, o.Status, o.LineId, o.ProductId, o.CompletedAtUtc)).FirstOrDefaultAsync(ct);
        if (order is null || !await ManufacturingAccess.ReachesAsync(db, caller, order.LineId, ct))
            return MfgFail.NotFound<OrderCostReconciliationDto>("Production order");

        var zone = StoreDocumentHistory.Zone(regional);
        var today = Today(zone);
        var day = asOf ?? today;
        var asOfEnd = StartUtc(day.AddDays(1), zone);
        var lineCode = await db.ProductionLines.AsNoTracking().Where(l => l.Id == order.LineId).Select(l => l.Code).FirstAsync(ct);
        var product = await ProductsAsync([order.ProductId], ct);
        var (sku, unit) = product.GetValueOrDefault(order.ProductId, ("", ""));
        var ratesShown = RatesShown(caller);

        OrderCostReconciliationDto Bare(string state, CostSheetHeaderDto? header = null, decimal accepted = 0m, decimal rejected = 0m) => new(
            order.Id, order.Number, order.Status.ToString(), order.LineId, lineCode, sku, unit, state, header, day, accepted, rejected,
            null, [], null, [], null, null, null, null, [], state == StateNotFrozen ? [new CostGapDto("not_frozen", SeverityMissing, null, null, null)] : [],
            CommonLimitations, null);

        if (order.Status != ProductionOrderStatus.Completed || order.CompletedAtUtc is null || order.CompletedAtUtc >= asOfEnd)
            return MfgResult<OrderCostReconciliationDto>.Done(Bare(StateNotCompleted));
        var head = await SheetHeads(db.ManufacturingCostSheets.AsNoTracking().Where(s => s.OrderId == orderId)).FirstOrDefaultAsync(ct);
        if (head is null) return MfgResult<OrderCostReconciliationDto>.Done(Bare(StateNotFrozen));
        if (head.CreatedAtUtc >= asOfEnd) return MfgResult<OrderCostReconciliationDto>.Done(Bare(StateNotFrozenAtDate));

        var reach = await ReachableLinesAsync(caller, null, ct);
        var bundle = await LoadAsync([new SheetRef(head, order, lineCode)], reach.Keys.ToList(), asOfEnd, withJson: true, ct);
        var data = bundle.Sheets[0];
        var analysis = Analyze(data, bundle.Assessed, ratesShown, zone);

        var labour = await LabourAsync(data, ratesShown, ct);
        LiveComparisonDto? comparison = null;
        if (live)
        {
            var now = await LateRowsAsync(reach.Keys.ToList(), DateTime.MaxValue, ct);
            var assessedNow = AssessLate(now.Select(Input).ToList(), id => bundle.FrozenSources.GetValueOrDefault(id) ?? Empty);
            comparison = await LiveAsync(data, analysis, now.Where(l => l.SheetId == head.Id && l.RecordedAtUtc >= asOfEnd).ToList(),
                assessedNow, ratesShown || labour.Employees != 1, ct);
        }

        return MfgResult<OrderCostReconciliationDto>.Done(new OrderCostReconciliationDto(
            order.Id, order.Number, order.Status.ToString(), order.LineId, lineCode, sku, unit, StateFrozen, Header(head, order, zone), day,
            head.AcceptedBaseQty, head.RejectedBaseQty, analysis.Figures,
            analysis.Lines.Select(l => new CostElementReconciliationDto(l.Element, l.Status, l.Original, l.LateRecognized, l.Adjusted)).ToList(),
            analysis.Residual, LateRows(data, bundle.Assessed, ratesShown), ReleaseDto(analysis.Releases), ScrapDto(analysis.Scrap),
            VarianceDto(analysis.Variances), labour, analysis.Checks.Select(CheckDto).ToList(), analysis.Gaps.Select(GapDto).ToList(),
            live ? [.. CommonLimitations, "live_is_today"] : CommonLimitations, comparison));
    }

    // ======================================================================================
    // R-B2: historical cost sheets (with completed orders that have none), the overview's totals,
    // and the live comparison for the page shown
    // ======================================================================================

    public async Task<MfgResult<CostHistoryReportDto>> HistoryAsync(MfgCaller caller, Query q, CancellationToken ct)
    {
        if (Refusal<CostHistoryReportDto>(caller) is { } refusal) return refusal;
        if (Resolve(q, BasisCompleted, allowRecorded: false) is not { } p) return MfgFail.Invalid<CostHistoryReportDto>("invalid_period", PeriodError);
        if (q.State is not (null or "" or "all" or StateFrozen or StateNotFrozen))
            return MfgFail.Invalid<CostHistoryReportDto>("invalid_filter", "Choose frozen, not frozen or all.");
        var reach = await ReachableLinesAsync(caller, q.LineId, ct);
        var ratesShown = RatesShown(caller);
        var (bundle, missing) = await ScopeAsync(reach, p, q, withMissing: q.State is not StateFrozen && p.Basis == BasisCompleted, ct);

        var rows = new List<(CostHistoryRowDto Row, SheetData? Data, Analysis? Analysis)>();
        if (q.State is not StateNotFrozen)
        {
            foreach (var data in bundle.Sheets)
            {
                var a = Analyze(data, bundle.Assessed, ratesShown, p.Zone);
                rows.Add((new CostHistoryRowDto(data.Ref.Order.Id, data.Ref.Order.Number, data.Ref.LineCode, data.Sku, StateFrozen,
                    Header(data.Ref.Head, data.Ref.Order, p.Zone), data.Ref.Head.AcceptedBaseQty, data.Ref.Head.RejectedBaseQty, a.Figures,
                    data.Late.Count, a.Integrity, a.Gaps.Select(g => g.Code).Distinct().ToList(), null), data, a));
            }
        }
        rows.AddRange(missing.Select(m => ((CostHistoryRowDto Row, SheetData? Data, Analysis? Analysis))(new CostHistoryRowDto(
            m.Order.Id, m.Order.Number, m.LineCode, m.Sku, StateNotFrozen, null, 0m, 0m, null, 0, null, ["not_frozen"], null), null, null)));

        rows = rows.Where(r => Matches(r.Row, q)).OrderBy(r => r.Row.OrderNumber, StringComparer.Ordinal).ThenBy(r => r.Row.OrderId.ToString(), StringComparer.Ordinal).ToList();
        var frozen = rows.Where(r => r.Row.Figures is not null).Select(r => r.Row).ToList();
        var totals = new CostHistoryTotalsDto(
            frozen.Count, rows.Count - frozen.Count, frozen.Count(r => r.Sheet!.Kind == "reconstructed"),
            frozen.Count(r => r.Sheet!.SourceState != "complete"), frozen.Count(r => r.Integrity == Fail),
            frozen.Sum(r => r.Figures!.Original), frozen.Sum(r => r.Figures!.LateRecorded), frozen.Sum(r => r.Figures!.LateRecognized),
            frozen.Sum(r => r.Figures!.LateUnclassified), frozen.Sum(r => r.Figures!.LateUncertain), frozen.Sum(r => r.Figures!.AdjustedProvisional));

        if (q.All && q.Live && rows.Count > MaxLiveExportRows)
            return MfgFail.Conflict<CostHistoryReportDto>("export_too_large", $"An export with the live comparison holds at most {MaxLiveExportRows} rows; narrow the period or the line.");
        if (q.All && rows.Count > MaxExportRows)
            return MfgFail.Conflict<CostHistoryReportDto>("export_too_large", $"An export holds at most {MaxExportRows} rows; narrow the period or the line.");
        var (page, size) = Paging(q, q.Live ? MaxLivePageSize : MaxPageSize);
        var shown = q.All ? rows : rows.Skip((page - 1) * size).Take(size).ToList();
        var result = new List<CostHistoryRowDto>(shown.Count);
        foreach (var (row, data, analysis) in shown)
        {
            if (!q.Live || data is null || analysis is null) { result.Add(row); continue; }
            var labourEmployees = data.Labour.Select(l => l.EmployeeId).Distinct().Count();
            var comparison = await LiveAsync(data, analysis, [], bundle.Assessed, ratesShown || labourEmployees != 1, ct);
            result.Add(row with { Live = new CostLiveSummaryDto(comparison.Comparable, comparison.Reason, comparison.Live, comparison.Unexplained) });
        }
        return MfgResult<CostHistoryReportDto>.Done(new CostHistoryReportDto(p.Dto, result, new CostReportPageDto(q.All ? 1 : page, q.All ? rows.Count : size, rows.Count),
            totals, q.Live, q.Live ? [.. CommonLimitations, "live_is_today"] : CommonLimitations));
    }

    // ======================================================================================
    // R-B3: late costs
    // ======================================================================================

    public async Task<MfgResult<LateCostReportDto>> LateCostsAsync(MfgCaller caller, Query q, CancellationToken ct)
    {
        if (Refusal<LateCostReportDto>(caller) is { } refusal) return refusal;
        if (Resolve(q, BasisRecorded, allowRecorded: true) is not { } p) return MfgFail.Invalid<LateCostReportDto>("invalid_period", PeriodError);
        var reach = await ReachableLinesAsync(caller, q.LineId, ct);
        var ratesShown = RatesShown(caller);
        var (bundle, _) = await ScopeAsync(reach, p, q, withMissing: false, ct);

        var rows = bundle.Sheets.SelectMany(d => LateRows(d, bundle.Assessed, ratesShown)
                .Where(r => p.Basis != BasisRecorded || (r.RecordedAtUtc >= p.FromUtc && r.RecordedAtUtc < p.ToUtc)))
            .Where(r => q.Element is null or "" || (r.Element ?? TreatmentUnclassified) == q.Element)
            .Where(r => q.Treatment is null or "" || r.Treatment == q.Treatment)
            .Where(r => q.Flag is null or "" || r.Flags.Contains(q.Flag))
            .OrderBy(r => r.RecordedAtUtc).ThenBy(r => r.OrderNumber, StringComparer.Ordinal).ThenBy(r => r.Id.ToString(), StringComparer.Ordinal)
            .ToList();
        var byElement = rows.GroupBy(r => r.Element ?? TreatmentUnclassified)
            .Select(g => new LateElementTotalDto(g.Key, g.Sum(r => r.RecordedAmount), g.Sum(r => r.RecognizedEffect), g.Sum(r => r.UnclassifiedEffect), g.Sum(r => r.UncertainEffect)))
            .OrderBy(g => ElementOrder(g.Element)).ToList();
        if (q.All && rows.Count > MaxExportRows)
            return MfgFail.Conflict<LateCostReportDto>("export_too_large", $"An export holds at most {MaxExportRows} rows; narrow the period or the line.");
        var (page, size) = Paging(q, MaxPageSize);
        return MfgResult<LateCostReportDto>.Done(new LateCostReportDto(p.Dto, q.All ? rows : rows.Skip((page - 1) * size).Take(size).ToList(),
            new CostReportPageDto(q.All ? 1 : page, q.All ? rows.Count : size, rows.Count), byElement,
            rows.Sum(r => r.RecordedAmount), rows.Sum(r => r.RecognizedEffect), rows.Sum(r => r.UnclassifiedEffect), rows.Sum(r => r.UncertainEffect),
            CommonLimitations));
    }

    // ======================================================================================
    // R-B4: variances
    // ======================================================================================

    public async Task<MfgResult<CostVarianceReportDto>> VariancesAsync(MfgCaller caller, Query q, CancellationToken ct)
    {
        if (Refusal<CostVarianceReportDto>(caller) is { } refusal) return refusal;
        if (Resolve(q, BasisCompleted, allowRecorded: false) is not { } p) return MfgFail.Invalid<CostVarianceReportDto>("invalid_period", PeriodError);
        var reach = await ReachableLinesAsync(caller, q.LineId, ct);
        var ratesShown = RatesShown(caller);
        var (bundle, _) = await ScopeAsync(reach, p, q, withMissing: false, ct);
        var rows = bundle.Sheets.Select(d => (Data: d, A: Analyze(d, bundle.Assessed, ratesShown, p.Zone)))
            .Select(x => new CostVarianceRowDto(x.Data.Ref.Order.Id, x.Data.Ref.Order.Number, x.Data.Ref.LineCode, x.Data.Ref.Head.AcceptedBaseQty, VarianceDto(x.A.Variances)))
            .OrderBy(r => r.OrderNumber, StringComparer.Ordinal).ToList();
        var available = rows.Where(r => r.Variance.Available).ToList();
        var totals = available.SelectMany(r => r.Variance.Lines).GroupBy(l => (l.Component, l.Detail))
            .Select(g => new VarianceLineDto(g.Key.Component, g.Key.Detail, g.Sum(l => l.Original ?? 0m),
                g.Any(l => l.LateRecognized is not null) ? g.Sum(l => l.LateRecognized ?? 0m) : null,
                g.Any(l => l.Adjusted is not null) ? g.Sum(l => l.Adjusted ?? 0m) : null))
            .ToList();
        if (q.All && rows.Count > MaxExportRows)
            return MfgFail.Conflict<CostVarianceReportDto>("export_too_large", $"An export holds at most {MaxExportRows} rows; narrow the period or the line.");
        var (page, size) = Paging(q, MaxPageSize);
        return MfgResult<CostVarianceReportDto>.Done(new CostVarianceReportDto(p.Dto, q.All ? rows : rows.Skip((page - 1) * size).Take(size).ToList(),
            new CostReportPageDto(q.All ? 1 : page, q.All ? rows.Count : size, rows.Count), totals, rows.Count - available.Count, CommonLimitations));
    }

    // ======================================================================================
    // R-B5: partial releases
    // ======================================================================================

    public async Task<MfgResult<ReleaseReconciliationReportDto>> ReleasesAsync(MfgCaller caller, Query q, CancellationToken ct)
    {
        if (Refusal<ReleaseReconciliationReportDto>(caller) is { } refusal) return refusal;
        if (Resolve(q, BasisCompleted, allowRecorded: false) is not { } p) return MfgFail.Invalid<ReleaseReconciliationReportDto>("invalid_period", PeriodError);
        var reach = await ReachableLinesAsync(caller, q.LineId, ct);
        var (bundle, _) = await ScopeAsync(reach, p, q, withMissing: false, ct);
        var rows = bundle.Sheets.Select(d => (Data: d, A: Analyze(d, bundle.Assessed, RatesShown(caller), p.Zone)))
            .Select(x => new ReleaseReconciliationOrderDto(x.Data.Ref.Order.Id, x.Data.Ref.Order.Number, x.Data.Ref.LineCode,
                x.Data.Ref.Head.TotalCost, ReleaseDto(x.A.Releases)))
            .Where(r => q.Flag is null or "" || r.Reconciliation.Flags.Contains(q.Flag))
            .OrderBy(r => r.OrderNumber, StringComparer.Ordinal).ToList();
        if (q.All && rows.Count > MaxExportRows)
            return MfgFail.Conflict<ReleaseReconciliationReportDto>("export_too_large", $"An export holds at most {MaxExportRows} rows; narrow the period or the line.");
        var (page, size) = Paging(q, MaxPageSize);
        return MfgResult<ReleaseReconciliationReportDto>.Done(new ReleaseReconciliationReportDto(p.Dto,
            q.All ? rows : rows.Skip((page - 1) * size).Take(size).ToList(), new CostReportPageDto(q.All ? 1 : page, q.All ? rows.Count : size, rows.Count),
            rows.Sum(r => r.Original), rows.Sum(r => r.Reconciliation.ReleasedValue), rows.Sum(r => r.Reconciliation.KeptInCharges),
            rows.Sum(r => r.Reconciliation.LateNotAllocated), CommonLimitations));
    }

    // ======================================================================================
    // R-B6: scrap and rejected output
    // ======================================================================================

    public async Task<MfgResult<ScrapCostReportDto>> ScrapAsync(MfgCaller caller, Query q, CancellationToken ct)
    {
        if (Refusal<ScrapCostReportDto>(caller) is { } refusal) return refusal;
        if (Resolve(q, BasisCompleted, allowRecorded: false) is not { } p) return MfgFail.Invalid<ScrapCostReportDto>("invalid_period", PeriodError);
        var reach = await ReachableLinesAsync(caller, q.LineId, ct);
        var (bundle, _) = await ScopeAsync(reach, p, q, withMissing: false, ct);
        var rows = bundle.Sheets.Select(d => (Data: d, A: Analyze(d, bundle.Assessed, RatesShown(caller), p.Zone)))
            .Select(x => new ScrapCostRowDto(x.Data.Ref.Order.Id, x.Data.Ref.Order.Number, x.Data.Ref.LineCode, ScrapDto(x.A.Scrap)))
            .OrderBy(r => r.OrderNumber, StringComparer.Ordinal).ToList();
        if (q.All && rows.Count > MaxExportRows)
            return MfgFail.Conflict<ScrapCostReportDto>("export_too_large", $"An export holds at most {MaxExportRows} rows; narrow the period or the line.");
        var (page, size) = Paging(q, MaxPageSize);
        var withScrap = rows.Where(r => r.Scrap.MaterialScrapValue is not null).ToList();
        var withSpoilage = rows.Where(r => r.Scrap.SpoilageAbsorbed is not null).ToList();
        return MfgResult<ScrapCostReportDto>.Done(new ScrapCostReportDto(p.Dto, q.All ? rows : rows.Skip((page - 1) * size).Take(size).ToList(),
            new CostReportPageDto(q.All ? 1 : page, q.All ? rows.Count : size, rows.Count),
            rows.Sum(r => r.Scrap.AcceptedQty), rows.Sum(r => r.Scrap.RejectedQty),
            withScrap.Count == 0 ? null : withScrap.Sum(r => r.Scrap.MaterialScrapValue!.Value),
            withSpoilage.Count == 0 ? null : withSpoilage.Sum(r => r.Scrap.SpoilageAbsorbed!.Value), CommonLimitations));
    }

    // ======================================================================================
    // R-B7: integrity and missing costs
    // ======================================================================================

    public async Task<MfgResult<CostGapReportDto>> GapsAsync(MfgCaller caller, Query q, CancellationToken ct)
    {
        if (Refusal<CostGapReportDto>(caller) is { } refusal) return refusal;
        if (Resolve(q, BasisCompleted, allowRecorded: false) is not { } p) return MfgFail.Invalid<CostGapReportDto>("invalid_period", PeriodError);
        var reach = await ReachableLinesAsync(caller, q.LineId, ct);
        var ratesShown = RatesShown(caller);
        var (bundle, missing) = await ScopeAsync(reach, p, q, withMissing: p.Basis == BasisCompleted, ct);
        var rows = bundle.Sheets.Select(d =>
            {
                var a = Analyze(d, bundle.Assessed, ratesShown, p.Zone);
                return new CostGapRowDto(d.Ref.Order.Id, d.Ref.Order.Number, d.Ref.LineCode, StateFrozen, d.Ref.Head.Id, a.Integrity,
                    a.Checks.Select(CheckDto).ToList(), a.Gaps.Select(GapDto).ToList());
            })
            .Concat(missing.Select(m => new CostGapRowDto(m.Order.Id, m.Order.Number, m.LineCode, StateNotFrozen, null, null, [],
                [new CostGapDto("not_frozen", SeverityMissing, null, null, null)])))
            .Where(r => r.Gaps.Count > 0)
            .Where(r => q.Code is null or "" || r.Gaps.Any(g => g.Code == q.Code || (g.Code == "check_failed" && g.Detail == q.Code)))
            .Where(r => q.Severity is null or "" || r.Gaps.Any(g => g.Severity == q.Severity))
            .OrderBy(r => r.OrderNumber, StringComparer.Ordinal).ToList();
        var byCode = rows.SelectMany(r => r.Gaps.Select(g => g.Code == "check_failed" ? $"check_failed:{g.Detail}" : g.Code).Distinct())
            .GroupBy(c => c).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        var bySeverity = rows.SelectMany(r => r.Gaps.Select(g => g.Severity).Distinct())
            .GroupBy(s => s).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        if (q.All && rows.Count > MaxExportRows)
            return MfgFail.Conflict<CostGapReportDto>("export_too_large", $"An export holds at most {MaxExportRows} rows; narrow the period or the line.");
        var (page, size) = Paging(q, MaxPageSize);
        return MfgResult<CostGapReportDto>.Done(new CostGapReportDto(p.Dto, q.All ? rows : rows.Skip((page - 1) * size).Take(size).ToList(),
            new CostReportPageDto(q.All ? 1 : page, q.All ? rows.Count : size, rows.Count), byCode, bySeverity, CommonLimitations));
    }

    // ======================================================================================
    // analysis of one sheet
    // ======================================================================================

    private sealed record OrderHead(Guid Id, string Number, ProductionOrderStatus Status, Guid LineId, Guid ProductId, DateTime? CompletedAtUtc);

    private sealed record SheetHead(
        Guid Id, Guid OrderId, ManufacturingCostSnapshotKind Kind, string CalculationVersion, string SourceHash,
        ManufacturingCostSourceState SourceState, decimal AcceptedBaseQty, decimal RejectedBaseQty, decimal TotalCost, decimal? UnitCost,
        string? CreatedBy, DateTime CreatedAtUtc);

    private sealed record SheetRef(SheetHead Head, OrderHead Order, string LineCode);

    private sealed record MissingOrder(OrderHead Order, string LineCode, string Sku);

    private sealed class SheetData(SheetRef reference)
    {
        public SheetRef Ref { get; } = reference;
        public string Sku { get; set; } = "";
        public string? Json { get; set; }
        public List<StoredElement> Elements { get; } = [];
        public List<(Guid OrderMaterialId, decimal Quantity, decimal UnitCost, decimal TotalCost)> Materials { get; } = [];
        public List<ManufacturingLabourRateSnapshot> Labour { get; } = [];
        public List<(Guid ExpenseId, Guid? VoucherId, decimal Amount)> Expenses { get; } = [];
        public (decimal RejectedQty, decimal RecoveryValue)? Scrap { get; set; }
        public List<StoredRelease> Releases { get; } = [];
        /// <summary>Late costs recorded by the end of the chosen day.</summary>
        public List<ManufacturingLateCost> Late { get; } = [];
    }

    private sealed record Bundle(
        IReadOnlyList<SheetData> Sheets, IReadOnlyDictionary<Guid, LateAssessment> Assessed,
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> FrozenSources);

    private sealed record Analysis(
        CostingCaptureSource? Source, string? SourceReason, CostFiguresDto Figures, IReadOnlyList<ElementLine> Lines, decimal Residual,
        decimal Adjusted, ReleaseSummary Releases, ScrapFigures Scrap, VarianceFigures Variances, IReadOnlyList<CheckResult> Checks,
        IReadOnlyList<GapItem> Gaps, string Integrity);

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    private static Analysis Analyze(SheetData d, IReadOnlyDictionary<Guid, LateAssessment> assessed, bool ratesShown, TimeZoneInfo zone)
    {
        var head = d.Ref.Head;
        var (source, reason) = CostingCapture.Parse(d.Json, head.CalculationVersion, head.SourceState);
        var replay = source is null ? null : ProductionCostEngine.Compute(source.Input);
        var late = d.Late.Select(l => (Row: l, A: assessed[l.Id])).ToList();
        var totals = Totals(late.Select(l => (l.Row.Amount, l.A.Treatment)));
        var recognized = late.Where(l => l.A.Treatment == TreatmentRecognized)
            .GroupBy(l => l.A.Element!).ToDictionary(g => g.Key, g => g.Sum(l => l.Row.Amount));
        var (lines, residual) = ReconcileElements(head.TotalCost, d.Elements, recognized);
        var releases = CostReconciliation.Releases(head.TotalCost, head.AcceptedBaseQty, d.Releases, totals.Recognized);
        var recoveryStatus = d.Elements.FirstOrDefault(e => e.Element == Recovery)?.Status ?? ProductionCostEngine.ElementNone;
        var scrap = Scrap(head.TotalCost, head.AcceptedBaseQty, head.RejectedBaseQty, d.Scrap?.RecoveryValue ?? 0m, recoveryStatus, replay, reason);
        var labourHoursShown = ratesShown || d.Labour.Select(l => l.EmployeeId).Distinct().Count() != 1;
        var variances = Variances(replay, reason, recognized, labourSplit: ratesShown);
        var rows = new SheetRows(head.TotalCost, head.AcceptedBaseQty, head.RejectedBaseQty, head.UnitCost, StateName(head.SourceState), head.SourceHash,
            d.Json, d.Elements, d.Materials,
            d.Labour.Select(l => (l.EmployeeId, l.PayrollYear, l.PayrollMonth, l.Hours, l.Rate, l.TotalCost)).ToList(),
            d.Expenses.Select(e => (e.ExpenseId, e.Amount)).ToList(), d.Scrap, d.Releases);
        var checks = Checks(rows, source, reason, replay, late.Select(l => l.A).ToList());
        var gaps = Gaps(KindName(head.Kind), StateName(head.SourceState), LagDays(head, d.Ref.Order, zone), head.TotalCost, d.Elements,
            source?.Notes ?? [], releases, late.Select(l => l.A).ToList(), checks, labourHoursShown);
        var adjusted = head.TotalCost + totals.Recognized;
        var figures = new CostFiguresDto(head.TotalCost, totals.Recorded, totals.Recognized, totals.Unclassified, totals.Uncertain,
            adjusted, null, FinalizationNotImplemented, head.UnitCost);
        return new Analysis(source, reason, figures, lines, residual, adjusted, releases, scrap, variances, checks, gaps,
            checks.Any(c => c.Result == Fail) ? Fail : Pass);
    }

    // ======================================================================================
    // loading
    // ======================================================================================

    private static IQueryable<SheetHead> SheetHeads(IQueryable<ManufacturingCostSheet> sheets) => sheets.Select(s => new SheetHead(
        s.Id, s.OrderId, s.Kind, s.CalculationVersion, s.SourceHash, s.SourceState, s.AcceptedBaseQty, s.RejectedBaseQty, s.TotalCost, s.UnitCost,
        s.CreatedBy, s.CreatedAtUtc));

    /// <summary>The frozen sheets the filters select, and (when asked) the completed orders with none by the chosen day.</summary>
    private async Task<(Bundle Bundle, List<MissingOrder> Missing)> ScopeAsync(
        Dictionary<Guid, string> reach, Period p, Query q, bool withMissing, CancellationToken ct)
    {
        var lineIds = reach.Keys.ToList();
        var orders = db.ProductionOrders.AsNoTracking().Where(o => lineIds.Contains(o.LineId));
        var sheets = db.ManufacturingCostSheets.AsNoTracking().Where(s => s.CreatedAtUtc < p.AsOfEndUtc);
        if (p.Basis == BasisFrozen) sheets = sheets.Where(s => s.CreatedAtUtc >= p.FromUtc && s.CreatedAtUtc < p.ToUtc);
        if (p.Basis == BasisCompleted)
            orders = orders.Where(o => o.CompletedAtUtc != null && o.CompletedAtUtc >= p.FromUtc && o.CompletedAtUtc < p.ToUtc);
        if (p.Basis == BasisRecorded)
            sheets = sheets.Where(s => db.ManufacturingLateCosts.Any(l => l.SheetId == s.Id && l.RecordedAtUtc >= p.FromUtc
                && l.RecordedAtUtc < p.ToUtc && l.RecordedAtUtc < p.AsOfEndUtc));
        if (q.Kind is "created" or "reconstructed")
        {
            var kind = q.Kind == "created" ? ManufacturingCostSnapshotKind.Created : ManufacturingCostSnapshotKind.Reconstructed;
            sheets = sheets.Where(s => s.Kind == kind);
        }
        if (q.SourceState is "complete" or "incompletelegacy")
        {
            var state = q.SourceState == "complete" ? ManufacturingCostSourceState.Complete : ManufacturingCostSourceState.IncompleteLegacy;
            sheets = sheets.Where(s => s.SourceState == state);
        }

        var found = await (from s in sheets
                           join o in orders on s.OrderId equals o.Id
                           select new
                           {
                               s.Id, s.OrderId, s.Kind, s.CalculationVersion, s.SourceHash, s.SourceState, s.AcceptedBaseQty, s.RejectedBaseQty,
                               s.TotalCost, s.UnitCost, s.CreatedBy, s.CreatedAtUtc, o.Number, o.Status, o.LineId, o.ProductId, o.CompletedAtUtc,
                           }).ToListAsync(ct);
        var refs = found.Select(x => new SheetRef(
            new SheetHead(x.Id, x.OrderId, x.Kind, x.CalculationVersion, x.SourceHash, x.SourceState, x.AcceptedBaseQty, x.RejectedBaseQty,
                x.TotalCost, x.UnitCost, x.CreatedBy, x.CreatedAtUtc),
            new OrderHead(x.OrderId, x.Number, x.Status, x.LineId, x.ProductId, x.CompletedAtUtc),
            reach.GetValueOrDefault(x.LineId, ""))).ToList();
        var bundle = await LoadAsync(refs, lineIds, p.AsOfEndUtc, withJson: true, ct);

        var missing = new List<MissingOrder>();
        if (withMissing && q.Kind is null or "" && q.SourceState is null or "")
        {
            var frozenBy = db.ManufacturingCostSheets.Where(s => s.CreatedAtUtc < p.AsOfEndUtc).Select(s => s.OrderId);
            var open = await orders.Where(o => o.Status == ProductionOrderStatus.Completed && o.CompletedAtUtc != null
                    && o.CompletedAtUtc < p.AsOfEndUtc && !frozenBy.Contains(o.Id))
                .Select(o => new OrderHead(o.Id, o.Number, o.Status, o.LineId, o.ProductId, o.CompletedAtUtc)).ToListAsync(ct);
            var skus = await ProductsAsync(open.Select(o => o.ProductId).Distinct().ToList(), ct);
            missing = open.Select(o => new MissingOrder(o, reach.GetValueOrDefault(o.LineId, ""), skus.GetValueOrDefault(o.ProductId, (Sku: "", Unit: "")).Sku)).ToList();
        }
        return (bundle, missing);
    }

    /// <summary>Every stored row of these sheets, read in chunks (SQL Server's parameter limit), without tracking.</summary>
    private async Task<Bundle> LoadAsync(IReadOnlyList<SheetRef> refs, List<Guid> reachableLines, DateTime asOfEnd, bool withJson, CancellationToken ct)
    {
        var map = refs.ToDictionary(r => r.Head.Id, r => new SheetData(r));
        var ids = map.Keys.ToList();
        foreach (var chunk in ids.Chunk(500).Select(c => c.ToList()))
        {
            if (withJson)
                foreach (var x in await db.ManufacturingCostSheets.AsNoTracking().Where(s => chunk.Contains(s.Id))
                             .Select(s => new { s.Id, s.SourceSnapshotJson }).ToListAsync(ct))
                    map[x.Id].Json = x.SourceSnapshotJson;
            foreach (var e in await db.ManufacturingCostElementLines.AsNoTracking().Where(e => chunk.Contains(e.SheetId))
                         .Select(e => new { e.SheetId, e.Element, e.TotalCost, e.Status }).ToListAsync(ct))
                map[e.SheetId].Elements.Add(new StoredElement(e.Element, e.TotalCost, e.Status));
            foreach (var m in await db.ManufacturingMaterialCostSnapshots.AsNoTracking().Where(m => chunk.Contains(m.SheetId))
                         .Select(m => new { m.SheetId, m.OrderMaterialId, m.Quantity, m.UnitCost, m.TotalCost }).ToListAsync(ct))
                map[m.SheetId].Materials.Add((m.OrderMaterialId, m.Quantity, m.UnitCost, m.TotalCost));
            foreach (var l in await db.ManufacturingLabourRateSnapshots.AsNoTracking().Where(l => chunk.Contains(l.SheetId)).ToListAsync(ct))
                map[l.SheetId].Labour.Add(l);
            foreach (var e in await db.ManufacturingExpenseAllocationSnapshots.AsNoTracking().Where(e => chunk.Contains(e.SheetId))
                         .Select(e => new { e.SheetId, e.ExpenseId, e.PaymentVoucherId, e.Amount }).ToListAsync(ct))
                map[e.SheetId].Expenses.Add((e.ExpenseId, e.PaymentVoucherId, e.Amount));
            foreach (var s in await db.ManufacturingScrapRecoverySnapshots.AsNoTracking().Where(s => chunk.Contains(s.SheetId))
                         .Select(s => new { s.SheetId, s.RejectedQty, s.RecoveryValue }).ToListAsync(ct))
                map[s.SheetId].Scrap = (s.RejectedQty, s.RecoveryValue);
            var releases = await (from v in db.ManufacturingReleaseValueSnapshots.AsNoTracking()
                                  join r in db.OutputReleases.AsNoTracking() on v.OutputReleaseId equals r.Id into docs
                                  from r in docs.DefaultIfEmpty()
                                  where chunk.Contains(v.SheetId)
                                  select new { v.SheetId, v.OutputReleaseId, Number = r == null ? "" : r.Number, AtUtc = r == null ? DateTime.MinValue : r.AtUtc,
                                      v.Quantity, v.Value, v.UnitValue, Catalogue = r == null ? (decimal?)null : r.UnitCostSnapshot }).ToListAsync(ct);
            foreach (var r in releases)
                map[r.SheetId].Releases.Add(new StoredRelease(r.OutputReleaseId, r.Number, r.AtUtc, r.Quantity, r.Value, r.UnitValue, r.Catalogue));
        }

        var products = await ProductsAsync(refs.Select(r => r.Order.ProductId).Distinct().ToList(), ct);
        foreach (var d in map.Values) d.Sku = products.GetValueOrDefault(d.Ref.Order.ProductId, (Sku: "", Unit: "")).Sku;

        // A frozen sheet's own sources, as a late cost could name them: its expenses and their vouchers, by id and number.
        var voucherIds = map.Values.SelectMany(d => d.Expenses).Where(e => e.VoucherId is not null).Select(e => e.VoucherId!.Value).Distinct().ToList();
        var numbers = new Dictionary<Guid, string>();
        foreach (var chunk in voucherIds.Chunk(500).Select(c => c.ToList()))
            foreach (var v in await db.PaymentVouchers.AsNoTracking().Where(v => chunk.Contains(v.Id)).Select(v => new { v.Id, v.Number }).ToListAsync(ct))
                numbers[v.Id] = v.Number;
        var frozen = map.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value.Expenses.SelectMany(e => new[]
            {
                NormalizeSource(e.ExpenseId.ToString()),
                e.VoucherId is Guid v ? NormalizeSource(v.ToString()) : null,
                e.VoucherId is Guid n && numbers.TryGetValue(n, out var number) ? NormalizeSource(number) : null,
            }).OfType<string>().Where(s => s.Length > 0).ToHashSet());

        // Late costs are assessed against every reachable sheet's late costs recorded by the chosen day, so a source
        // repeated on another order is seen; each sheet keeps only its own.
        var late = await LateRowsAsync(reachableLines, asOfEnd, ct);
        foreach (var l in late) if (map.TryGetValue(l.SheetId, out var d)) d.Late.Add(l);
        foreach (var d in map.Values)
        {
            d.Late.Sort((a, b) => a.RecordedAtUtc != b.RecordedAtUtc ? a.RecordedAtUtc.CompareTo(b.RecordedAtUtc)
                : string.CompareOrdinal(a.Id.ToString(), b.Id.ToString()));
            d.Releases.Sort((a, b) => a.AtUtc != b.AtUtc ? a.AtUtc.CompareTo(b.AtUtc) : string.CompareOrdinal(a.Number, b.Number));
            // Rows come back in whatever order the provider stores them; every list is put in one order so
            // SQLite and SQL Server answer identically, gaps and checks included.
            d.Elements.Sort((a, b) => ElementOrder(a.Element).CompareTo(ElementOrder(b.Element)));
            d.Materials.Sort((a, b) => string.CompareOrdinal(a.OrderMaterialId.ToString(), b.OrderMaterialId.ToString()));
            d.Labour.Sort((a, b) => (a.PayrollYear, a.PayrollMonth) != (b.PayrollYear, b.PayrollMonth)
                ? (a.PayrollYear * 100 + a.PayrollMonth).CompareTo(b.PayrollYear * 100 + b.PayrollMonth)
                : string.CompareOrdinal(a.EmployeeId.ToString(), b.EmployeeId.ToString()));
            d.Expenses.Sort((a, b) => string.CompareOrdinal(a.ExpenseId.ToString(), b.ExpenseId.ToString()));
        }
        var assessed = AssessLate(late.Select(Input).ToList(), id => frozen.GetValueOrDefault(id) ?? Empty);
        return new Bundle(refs.Select(r => map[r.Head.Id]).ToList(), assessed, frozen);
    }

    private async Task<List<ManufacturingLateCost>> LateRowsAsync(List<Guid> reachableLines, DateTime before, CancellationToken ct) =>
        await (from l in db.ManufacturingLateCosts.AsNoTracking()
               join s in db.ManufacturingCostSheets.AsNoTracking() on l.SheetId equals s.Id
               join o in db.ProductionOrders.AsNoTracking() on s.OrderId equals o.Id
               where reachableLines.Contains(o.LineId) && l.RecordedAtUtc < before
               select l).ToListAsync(ct);

    private static LateInput Input(ManufacturingLateCost l) => new(l.Id, l.SheetId, l.Kind, l.SourceReference, l.Amount, l.RecordedAtUtc);

    private async Task<Dictionary<Guid, (string Sku, string Unit)>> ProductsAsync(List<Guid> ids, CancellationToken ct)
    {
        var units = await db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct);
        var result = new Dictionary<Guid, (string, string)>();
        foreach (var chunk in ids.Chunk(500).Select(c => c.ToList()))
            foreach (var p in await db.Products.AsNoTracking().Where(p => chunk.Contains(p.Id)).Select(p => new { p.Id, p.Sku, p.BaseUnitId }).ToListAsync(ct))
                result[p.Id] = (p.Sku, p.BaseUnitId is Guid u ? units.GetValueOrDefault(u, "") : "");
        return result;
    }

    private async Task<Dictionary<Guid, string>> ReachableLinesAsync(MfgCaller caller, Guid? lineId, CancellationToken ct)
    {
        var lines = await ManufacturingAccess.Reachable(db, caller).AsNoTracking().Select(l => new { l.Id, l.Code }).ToListAsync(ct);
        // A filter on a line out of reach selects nothing — the same answer as a line with no history.
        return lines.Where(l => lineId is null || l.Id == lineId).ToDictionary(l => l.Id, l => l.Code);
    }

    // ======================================================================================
    // labour (per employee), redacted
    // ======================================================================================

    private async Task<LabourSnapshotDto> LabourAsync(SheetData d, bool ratesShown, CancellationToken ct)
    {
        var ids = d.Labour.Select(l => l.EmployeeId).Distinct().ToList();
        var people = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).Select(e => new { e.Id, e.Number, e.FullName }).ToListAsync(ct);
        var withheld = !ratesShown && ids.Count == 1;
        var rows = d.Labour
            .OrderBy(l => l.PayrollYear).ThenBy(l => l.PayrollMonth)
            .ThenBy(l => people.FirstOrDefault(p => p.Id == l.EmployeeId)?.Number ?? "", StringComparer.Ordinal)
            .Select(l =>
            {
                var person = people.FirstOrDefault(p => p.Id == l.EmployeeId);
                return new LabourSnapshotRowDto(person?.Number ?? "", person?.FullName ?? "", l.PayrollYear, l.PayrollMonth,
                    withheld ? null : l.Hours, l.Status, l.Reason, ratesShown ? l.Rate : null, ratesShown ? l.TotalCost : null);
            }).ToList();
        return new LabourSnapshotDto(ratesShown, withheld, ids.Count, rows);
    }

    // ======================================================================================
    // the live comparison (B2.4): diagnostic, today's computation, nothing written
    // ======================================================================================

    private async Task<LiveComparisonDto> LiveAsync(
        SheetData d, Analysis a, IReadOnlyList<ManufacturingLateCost> after, IReadOnlyDictionary<Guid, LateAssessment> assessedNow,
        bool labourHoursShown, CancellationToken ct)
    {
        var f = a.Figures;
        var afterAsOf = after.Where(l => assessedNow.TryGetValue(l.Id, out var x) && x.Treatment == TreatmentRecognized).Sum(l => l.Amount);
        LiveComparisonDto NotComparable(string reason) => new(false, reason, f.Original, f.LateRecognized, f.AdjustedProvisional, null, null,
            afterAsOf, f.LateUnclassified, f.LateUncertain, [], []);
        if (d.Ref.Head.CalculationVersion != ProductionCostEngine.Version) return NotComparable("method_version_changed");

        var order = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials).ThenInclude(m => m.Product)
            .Include(o => o.Stages).Include(o => o.Product).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == d.Ref.Order.Id, ct);
        if (order is null) return NotComparable("order_not_found");
        var units = await db.UnitsOfMeasure.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Code, ct);
        var (input, _, _) = await costs.InputAsync(order, units, ct, forStockValue: true);
        // A costing choice changed since the freeze (Phase C1 settings): the difference would be the choice, not a late cost.
        if (a.Source is not null && a.Source.Input.Policy != input.Policy) return NotComparable("costing_policy_changed");
        var live = ProductionCostEngine.Compute(input);
        var (total, unexplained, elements) = Drift(live, a.Lines, a.Adjusted);
        IReadOnlyList<CostGapDto> changes = a.Source is null
            ? [new CostGapDto("source_changes_unavailable", SeverityInfo, null, null, a.SourceReason)]
            : await SourceChangesAsync(order.Id, a.Source, input, labourHoursShown, ct);
        return new LiveComparisonDto(true, null, f.Original, f.LateRecognized, f.AdjustedProvisional, total, unexplained, afterAsOf,
            f.LateUnclassified, f.LateUncertain,
            elements.Select(e => new CostElementDriftDto(e.Element, e.Adjusted, e.Live, e.Unexplained)).ToList(), changes);
    }

    /// <summary>Which recorded inputs changed since the freeze, by identity: added, removed or changed — counted, never corrected.</summary>
    private async Task<IReadOnlyList<CostGapDto>> SourceChangesAsync(
        Guid orderId, CostingCaptureSource source, CostingInput live, bool labourHoursShown, CancellationToken ct)
    {
        var changes = new List<CostGapDto>();
        void Add(string code, string? element, decimal? value) => changes.Add(new CostGapDto(code, SeverityReview, element, value, null));

        var frozenExpenses = source.Expenses.ToDictionary(e => e.Id);
        var liveExpenses = live.Expenses.ToDictionary(e => e.Id);
        var added = liveExpenses.Values.Where(e => !frozenExpenses.ContainsKey(e.Id)).ToList();
        var removed = frozenExpenses.Values.Where(e => !liveExpenses.ContainsKey(e.Id)).ToList();
        if (added.Count > 0) Add("expense_added", Expenses, Money(added.Sum(e => e.Amount)));
        if (removed.Count > 0) Add("expense_removed", Expenses, Money(removed.Sum(e => e.Amount)));
        if (frozenExpenses.Values.Count(e => liveExpenses.TryGetValue(e.Id, out var now) && now.Amount != e.Amount) is var changed and > 0)
            Add("expense_changed", Expenses, changed);

        var voucherIds = source.Vouchers.Select(v => v.Id).ToList();
        var statuses = await db.PaymentVouchers.AsNoTracking().Where(v => voucherIds.Contains(v.Id)).Select(v => new { v.Id, v.Status }).ToListAsync(ct);
        if (source.Vouchers.Count(v => statuses.FirstOrDefault(s => s.Id == v.Id) is { } now && now.Status != v.Status) is var moved and > 0)
            Add("voucher_status_changed", Expenses, moved);

        var bookings = await db.LaborBookings.AsNoTracking().Where(b => b.OrderId == orderId && b.OrderKind == LaborOrderKind.Production)
            .Select(b => new { b.Id, b.Hours }).ToListAsync(ct);
        var frozenBookings = source.Bookings.Select(b => b.Id).ToHashSet();
        var liveBookings = bookings.Select(b => b.Id).ToHashSet();
        if (bookings.Where(b => !frozenBookings.Contains(b.Id)).ToList() is { Count: > 0 } newBookings)
            Add("booking_added", Labour, labourHoursShown ? newBookings.Sum(b => b.Hours) : null);
        if (source.Bookings.Where(b => !liveBookings.Contains(b.Id)).ToList() is { Count: > 0 } gone)
            Add("booking_removed", Labour, labourHoursShown ? gone.Sum(b => b.Hours) : null);
        // A rate is payroll's: only how many employee-months moved, never whose or by how much. Under "premium to overtime
        // hours" (C-05) the month's premium and the hours it is spread over move a rate too.
        var premium = live.Policy.LabourAbsorption == LabourAbsorption.PremiumToOvertime;
        var rateMoves = source.Rates.Count(r => live.Rates.TryGetValue((r.EmployeeId, r.Year, r.Month), out var now)
            && (now.Kind != r.Kind || now.PerHour != r.PerHour
                || (premium && (now.OvertimePremium != r.OvertimePremium || now.PaidOvertimeHours != r.PaidOvertimeHours
                    || now.MarkedOvertimeHours != r.MarkedOvertimeHours))));
        if (rateMoves > 0) Add("rate_changed", Labour, rateMoves);

        var movementLines = await db.MaterialMovementLines.AsNoTracking().Where(l => l.Movement!.OrderId == orderId).Select(l => l.Id).ToListAsync(ct);
        var frozenLines = source.MaterialMovementLines.ToHashSet();
        if (movementLines.Count(id => !frozenLines.Contains(id)) is var newLines and > 0) Add("material_movement_added", Materials, newLines);

        var frozenReleases = source.OutputReleases.Select(r => r.Id).ToHashSet();
        if (live.Releases.Where(r => !frozenReleases.Contains(r.Id)).ToList() is { Count: > 0 } newReleases)
            Add("release_added", null, newReleases.Sum(r => r.BaseQty));
        return changes;
    }

    // ======================================================================================
    // mapping and redaction
    // ======================================================================================

    private static bool RatesShown(MfgCaller caller) => CostPrivacy.RatesShown(caller);

    /// <summary>
    /// A sheet's late costs as shown: a labour or unclassified record's source and reason are payroll-sensitive
    /// (they usually name an employee-month), so without rate access they are withheld and the row says so.
    /// </summary>
    private static List<LateCostRowDto> LateRows(SheetData d, IReadOnlyDictionary<Guid, LateAssessment> assessed, bool ratesShown) =>
        d.Late.Select(l =>
        {
            var a = assessed[l.Id];
            var redacted = !ratesShown && a.Element is null or Labour;
            return new LateCostRowDto(l.Id, d.Ref.Order.Id, d.Ref.Order.Number, d.Ref.LineCode, l.Kind, a.Element, a.Treatment, l.Amount,
                a.Treatment == TreatmentRecognized ? l.Amount : 0m, a.Treatment == TreatmentUnclassified ? l.Amount : 0m,
                a.Treatment == TreatmentUncertain ? l.Amount : 0m,
                redacted ? null : l.SourceReference, redacted ? null : l.Reason, redacted, l.RecordedAtUtc, a.Flags);
        }).ToList();

    private static CostSheetHeaderDto Header(SheetHead s, OrderHead o, TimeZoneInfo zone) => new(
        s.Id, KindName(s.Kind), StateName(s.SourceState), s.CalculationVersion, "provisional", s.CreatedAtUtc, s.CreatedBy, o.CompletedAtUtc,
        LagDays(s, o, zone), s.SourceHash);

    private static int? LagDays(SheetHead s, OrderHead o, TimeZoneInfo zone) =>
        o.CompletedAtUtc is DateTime completed ? CompanyDay(s.CreatedAtUtc, zone).DayNumber - CompanyDay(completed, zone).DayNumber : null;

    private static string KindName(ManufacturingCostSnapshotKind kind) => kind.ToString().ToLowerInvariant();
    private static string StateName(ManufacturingCostSourceState state) => state.ToString().ToLowerInvariant();

    private static ReleaseReconciliationDto ReleaseDto(ReleaseSummary r) => new(
        r.Lines.Select(l => new ReleaseReconciliationRowDto(l.ReleaseId, l.Number, l.AtUtc, l.Quantity, l.Value, l.UnitValue, l.CarriesRemainder,
            l.CatalogueReferenceValue)).ToList(),
        r.AcceptedQty, r.ReleasedQty, r.UnreleasedAcceptedQty, r.ReleasedValue, r.KeptInCharges, r.LateNotAllocated, r.Flags);

    private static ScrapCostDto ScrapDto(ScrapFigures s) => new(s.AcceptedQty, s.RejectedQty, s.RejectionRatePercent, s.MaterialScrapValue,
        s.RecoveryValue, s.RecoveryStatus, s.SpoilageAbsorbed, s.MaterialWastage, s.Unavailable);

    private static CostVarianceDto VarianceDto(VarianceFigures v) => new(v.Available, v.Reason,
        v.Lines.Select(l => new VarianceLineDto(l.Component, l.Detail, l.Original, l.LateRecognized, l.Adjusted)).ToList());

    private static CostCheckDto CheckDto(CheckResult c) => new(c.Code, c.Result, c.Expected, c.Actual, c.Detail);
    private static CostGapDto GapDto(GapItem g) => new(g.Code, g.Severity, g.Element, g.Value, g.Detail);

    private static int ElementOrder(string element) => Elements.ToList().IndexOf(element) is var i and >= 0 ? i : Elements.Count;

    private static bool Matches(CostHistoryRowDto row, Query q) =>
        (q.Code is null or "" || row.GapCodes.Contains(q.Code))
        && (q.Flag != "integrity_failed" || row.Integrity == Fail)
        && (q.Flag != "late" || row.LateCount > 0);

    // ======================================================================================
    // period, paging, refusal
    // ======================================================================================

    private sealed record Period(CostReportPeriodDto Dto, string Basis, DateTime FromUtc, DateTime ToUtc, DateTime AsOfEndUtc, TimeZoneInfo Zone);

    private const string PeriodError = "Choose a period of at most 366 days, its start not after its end, and a basis: completed, frozen or recorded.";

    private Period? Resolve(Query q, string defaultBasis, bool allowRecorded)
    {
        var zone = StoreDocumentHistory.Zone(regional);
        var today = Today(zone);
        var basis = string.IsNullOrWhiteSpace(q.Basis) ? defaultBasis : q.Basis;
        if (basis is not (BasisCompleted or BasisFrozen or BasisRecorded) || (basis == BasisRecorded && !allowRecorded)) return null;
        var from = q.From ?? new DateOnly(today.Year, today.Month, 1);
        var to = q.To ?? today;
        if (to < from || to.DayNumber - from.DayNumber + 1 > MaxPeriodDays) return null;
        var asOf = q.AsOf ?? today;
        return new Period(new CostReportPeriodDto(from, to, basis, asOf), basis, StartUtc(from, zone), StartUtc(to.AddDays(1), zone),
            StartUtc(asOf.AddDays(1), zone), zone);
    }

    private static (int Page, int Size) Paging(Query q, int max) =>
        (Math.Max(1, q.Page ?? 1), Math.Clamp(q.PageSize ?? DefaultPageSize, 1, max));

    private DateOnly Today(TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);

    private static DateTime StartUtc(DateOnly day, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone);

    private static DateOnly CompanyDay(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    /// <summary>The company's wall-clock time of a stored UTC moment (exports).</summary>
    public DateTime CompanyTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), StoreDocumentHistory.Zone(regional));

    /// <summary>Today in the company's time zone (file names).</summary>
    public DateOnly CompanyToday() => Today(StoreDocumentHistory.Zone(regional));

    private MfgResult<T>? Refusal<T>(MfgCaller caller)
    {
        if (!options.Enabled) return MfgFail.Conflict<T>("costing_not_enabled", "Manufacturing costing is not switched on for this server.");
        if (!caller.Has(PermissionCatalog.ManufacturingCostsView))
            return MfgFail.Forbidden<T>("Manufacturing cost reports need their own permission.");
        if (!CostPrivacy.RatesShown(caller)) return MfgFail.Forbidden<T>(CostPrivacy.WithheldMessage, CostPrivacy.WithheldCode);
        return null;
    }
}
