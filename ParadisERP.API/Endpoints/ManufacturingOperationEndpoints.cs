using Microsoft.AspNetCore.Mvc;
using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Manufacturing;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Reports;
using static ParadisERP.API.Endpoints.ManufacturingEndpoints;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Manufacturing, Phase 2 (docs/manufacturing/phase-2.md): production orders, their materials and output,
/// the daily production record and its hours, and the manufacturing reports. The HTTP shape only; the rules
/// live in <c>Infrastructure/Manufacturing</c>.
/// <list type="bullet">
/// <item>Who may call a route is <see cref="Authorization.RoutePermissions"/>'s; which line, order or record
/// they reach is <see cref="ManufacturingAccess"/>'s — out of reach is 404.</item>
/// <item>Every operational write is refused with <c>operations_not_enabled</c> unless the server's
/// configuration switches manufacturing stock operations on (<see cref="ManufacturingOptions"/>).</item>
/// <item>A refusal is a ProblemDetails with a stable <c>code</c>.</item>
/// </list>
/// </summary>
public static class ManufacturingOperationEndpoints
{
    public static void MapManufacturingOperationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/manufacturing").WithTags("Manufacturing")
            .AddEndpointFilter<SubscriptionWriteFilter>();

        group.MapGet("/operations/status", ([FromServices] ManufacturingOptions options, [FromServices] ManufacturingCostingOptions costing) =>
            Results.Ok(new ManufacturingOperationsStatusDto(options.StockOperations, costing.Enabled)));

        // ---------- orders ----------
        group.MapGet("/orders", async (Guid? lineId, string? status, Guid? productId, [FromServices] ProductionOrders orders,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            await CallerAsync(http, db, ct) is MfgCaller caller ? Results.Ok(await orders.ListAsync(caller, lineId, status, productId, ct)) : NoCaller());
        ManufacturingPageRoutes.MapOrdersPage(group);
        group.MapGet("/orders/{id:guid}", (Guid id, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.GetAsync(caller, id, ct)));
        group.MapPost("/orders/preview", (CreateProductionOrderRequest r, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.PreviewAsync(caller, r, ct)));
        group.MapPost("/orders", (CreateProductionOrderRequest r, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.CreateAsync(caller, r, ct), created: o => $"/api/manufacturing/orders/{o.Order.Id}"));
        group.MapPost("/orders/{id:guid}/hold", (Guid id, OrderReasonRequest r, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.HoldAsync(caller, id, r, ct)));
        group.MapPost("/orders/{id:guid}/resume", (Guid id, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.ResumeAsync(caller, id, ct)));
        group.MapPost("/orders/{id:guid}/cancel", (Guid id, OrderReasonRequest r, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.CancelAsync(caller, id, r, ct)));
        group.MapPost("/orders/{id:guid}/materials/{materialId:guid}/release-reservation", (Guid id, Guid materialId, ReleaseReservationRequest r,
                [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.ReleaseReservationAsync(caller, id, materialId, r, ct)));
        group.MapPost("/orders/{id:guid}/expenses", (Guid id, OrderExpenseRequest r, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.AddExpenseAsync(caller, id, r, ct)));
        group.MapPost("/orders/{id:guid}/movements", (Guid id, MaterialMovementRequest r, [FromServices] ProductionMovements movements, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => movements.MoveAsync(caller, id, r, ct)));
        group.MapPost("/orders/{id:guid}/consumptions", (Guid id, ConsumptionRequest r, [FromServices] ProductionMovements movements, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => movements.ConsumeAsync(caller, id, r, ct)));
        group.MapPost("/orders/{id:guid}/releases", (Guid id, OutputReleaseRequest r, [FromServices] ProductionMovements movements, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => movements.ReleaseAsync(caller, id, r, ct)));
        group.MapPost("/orders/{id:guid}/request-completion", (Guid id, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.RequestCompletionAsync(caller, id, ct)));
        group.MapPost("/orders/{id:guid}/approve-completion", (Guid id, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.ApproveCompletionAsync(caller, id, ct)));
        group.MapPost("/orders/{id:guid}/refuse-completion", (Guid id, OrderReasonRequest r, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.RefuseCompletionAsync(caller, id, r, ct)));
        group.MapGet("/orders/{id:guid}/cost-inputs", (Guid id, [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => orders.CostInputsAsync(caller, id, ct)));
        // Costing, Phase A: computed on request, nothing written (docs/manufacturing/costing-plan.md).
        group.MapGet("/orders/{id:guid}/costing", (Guid id, [FromServices] ProductionCosts costs, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => costs.SheetAsync(caller, id, ct)));
        // B1: an intentional, immutable record of the Phase A calculation. Completion does not create it
        // and it remains provisional until a future accountant-approved financial-finalization policy exists.
        group.MapGet("/orders/{id:guid}/cost-snapshot", (Guid id, [FromServices] ProductionCostSnapshots snapshots, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => snapshots.GetAsync(caller, id, ct)));
        group.MapPost("/orders/{id:guid}/cost-snapshot", (Guid id, CreateManufacturingCostSnapshotRequest r, [FromServices] ProductionCostSnapshots snapshots, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => snapshots.CreateAsync(caller, id, r, ct), created: s => $"/api/manufacturing/orders/{s.OrderId}/cost-snapshot"));
        group.MapPost("/orders/{id:guid}/cost-snapshot/late-costs", (Guid id, CreateManufacturingLateCostRequest r, [FromServices] ProductionCostSnapshots snapshots, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => snapshots.AddLateCostAsync(caller, id, r, ct)));

        // ---------- B2: cost reconciliation and reporting, read only (docs/manufacturing/costing-b2.md) ----------
        // format=xlsx answers the same report as a workbook: the same route, permission, line scope and redaction.
        group.MapGet("/orders/{id:guid}/cost-reconciliation", (Guid id, DateOnly? asOf, bool? live, string? format, string? lang,
                [FromServices] ProductionCostHistory history, PosDbContext db, HttpContext http, CancellationToken ct) =>
            Report(http, db, ct, caller => history.OrderAsync(caller, id, asOf, live == true, ct), format,
                o => CostReportWorkbook.Order(o, CostReportLabels.For(lang), history.CompanyTime), o => CostReportWorkbook.FileName($"order-{o.OrderNumber}", history.CompanyToday())));
        group.MapGet("/reports/cost-history", ([AsParameters] CostReportArgs a, [FromServices] ProductionCostHistory history, PosDbContext db, HttpContext http, CancellationToken ct) =>
            Report(http, db, ct, caller => history.HistoryAsync(caller, a.Query(), ct), a.Format,
                r => CostReportWorkbook.History(r, CostReportLabels.For(a.Lang), history.CompanyTime), _ => CostReportWorkbook.FileName("cost-history", history.CompanyToday())));
        group.MapGet("/reports/late-costs", ([AsParameters] CostReportArgs a, [FromServices] ProductionCostHistory history, PosDbContext db, HttpContext http, CancellationToken ct) =>
            Report(http, db, ct, caller => history.LateCostsAsync(caller, a.Query(), ct), a.Format,
                r => CostReportWorkbook.LateCosts(r, CostReportLabels.For(a.Lang), history.CompanyTime), _ => CostReportWorkbook.FileName("late-costs", history.CompanyToday())));
        group.MapGet("/reports/cost-variances", ([AsParameters] CostReportArgs a, [FromServices] ProductionCostHistory history, PosDbContext db, HttpContext http, CancellationToken ct) =>
            Report(http, db, ct, caller => history.VariancesAsync(caller, a.Query(), ct), a.Format,
                r => CostReportWorkbook.Variances(r, CostReportLabels.For(a.Lang)), _ => CostReportWorkbook.FileName("cost-variances", history.CompanyToday())));
        group.MapGet("/reports/release-reconciliation", ([AsParameters] CostReportArgs a, [FromServices] ProductionCostHistory history, PosDbContext db, HttpContext http, CancellationToken ct) =>
            Report(http, db, ct, caller => history.ReleasesAsync(caller, a.Query(), ct), a.Format,
                r => CostReportWorkbook.Releases(r, CostReportLabels.For(a.Lang), history.CompanyTime), _ => CostReportWorkbook.FileName("partial-releases", history.CompanyToday())));
        group.MapGet("/reports/scrap-costs", ([AsParameters] CostReportArgs a, [FromServices] ProductionCostHistory history, PosDbContext db, HttpContext http, CancellationToken ct) =>
            Report(http, db, ct, caller => history.ScrapAsync(caller, a.Query(), ct), a.Format,
                r => CostReportWorkbook.Scrap(r, CostReportLabels.For(a.Lang)), _ => CostReportWorkbook.FileName("scrap-costs", history.CompanyToday())));
        group.MapGet("/reports/cost-gaps", ([AsParameters] CostReportArgs a, [FromServices] ProductionCostHistory history, PosDbContext db, HttpContext http, CancellationToken ct) =>
            Report(http, db, ct, caller => history.GapsAsync(caller, a.Query(), ct), a.Format,
                r => CostReportWorkbook.Gaps(r, CostReportLabels.For(a.Lang)), _ => CostReportWorkbook.FileName("cost-gaps", history.CompanyToday())));

        // ---------- daily production records ----------
        group.MapGet("/logs", async (Guid? lineId, DateOnly? from, DateOnly? to, string? status, [FromServices] ProductionLogs logs,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            await CallerAsync(http, db, ct) is MfgCaller caller ? Results.Ok(await logs.ListAsync(caller, lineId, from, to, status, ct)) : NoCaller());
        ManufacturingPageRoutes.MapLogsPage(group);
        group.MapGet("/logs/{id:guid}", (Guid id, [FromServices] ProductionLogs logs, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => logs.GetAsync(caller, id, ct)));
        group.MapPost("/logs", (OpenProductionLogRequest r, [FromServices] ProductionLogs logs, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => logs.OpenAsync(caller, r, ct)));
        group.MapPut("/logs/{id:guid}", (Guid id, SaveProductionLogRequest r, [FromServices] ProductionLogs logs, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => logs.SaveAsync(caller, id, r, ct)));
        group.MapPost("/logs/{id:guid}/submit", (Guid id, [FromServices] ProductionLogs logs, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => logs.SubmitAsync(caller, id, ct)));
        group.MapPost("/logs/{id:guid}/return", (Guid id, OrderReasonRequest r, [FromServices] ProductionLogs logs, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => logs.ReturnAsync(caller, id, r, ct)));
        group.MapPost("/logs/{id:guid}/approve", (Guid id, [FromServices] ProductionLogs logs, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => logs.ApproveAsync(caller, id, ct)));
        group.MapPost("/logs/{id:guid}/rows/{rowId:guid}/bookings", (Guid id, Guid rowId, LaborBookingRequest r, [FromServices] ProductionLogs logs,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => logs.BookAsync(caller, id, rowId, r, ct)));
        group.MapDelete("/logs/{id:guid}/bookings/{bookingId:guid}", (Guid id, Guid bookingId, [FromServices] ProductionLogs logs,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => logs.UnbookAsync(caller, id, bookingId, ct)));
        group.MapGet("/logs/{id:guid}/employee-candidates", (Guid id, [FromServices] ProductionLogs logs, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => logs.CandidatesAsync(caller, id, ct)));

        // ---------- reports ----------
        group.MapGet("/reports/pos-exceptions", (DateOnly? from, DateOnly? to, Guid? branchId, Guid? productId, string? sale, string? stale,
                [FromServices] ManufacturingReports reports, [FromServices] IRegionalSettingsProvider regional,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => reports.PosExceptionsAsync(caller, from, to, branchId, productId, sale, stale, StoreDocumentHistory.Zone(regional), ct)))
            .WithMetadata(new BranchScopedAttribute("a branch user's report is their own branch's, whatever branchId says (CallersBranch, and the service again)"))
            .AtCallersBranch();
        ManufacturingPageRoutes.MapPosExceptionsPage(group);
        group.MapGet("/reports/pending-movements", async (Guid? lineId, [FromServices] ManufacturingReports reports,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            await CallerAsync(http, db, ct) is MfgCaller caller ? Results.Ok(await reports.PendingMovementsAsync(caller, lineId, ct)) : NoCaller());
        ManufacturingPageRoutes.MapPendingMovementsPage(group);
        group.MapGet("/reports/material-flow", async ([FromServices] ManufacturingReports reports, PosDbContext db, HttpContext http, CancellationToken ct) =>
            await CallerAsync(http, db, ct) is MfgCaller caller ? Results.Ok(await reports.MaterialFlowAsync(caller, ct)) : NoCaller());
        ManufacturingPageRoutes.MapMaterialFlowPage(group);
        group.MapGet("/reports/work-in-progress", (Guid? lineId, [FromServices] ProductionCosts costs, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => costs.WorkInProgressAsync(caller, lineId, ct)));
        ManufacturingPageRoutes.MapWorkInProgressPage(group);
        group.MapGet("/reports/labour-absorption", (int? year, int? month, Guid? lineId, [FromServices] ProductionCosts costs,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => costs.LabourAbsorptionAsync(caller, year, month, lineId, ct)));
        ManufacturingPageRoutes.MapLabourAbsorptionPage(group);
    }

    /// <summary>
    /// A B2 report as JSON, or with <c>format=xlsx</c> as a workbook written from that same answer — so an export is
    /// refused, scoped and redacted exactly as the report is, and never shows more.
    /// </summary>
    private static async Task<IResult> Report<T>(HttpContext http, PosDbContext db, CancellationToken ct,
        Func<MfgCaller, Task<MfgResult<T>>> act, string? format, Func<T, byte[]> workbook, Func<T, string> fileName)
    {
        if (format is null or "" or "json") return await As(http, db, ct, act);
        if (format != "xlsx")
            return Results.Problem(detail: "Choose json or xlsx.", statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_format" });
        if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
        var result = await act(caller);
        return result.Ok ? Results.File(workbook(result.Value!), XlsxWriter.ContentType, fileName(result.Value!)) : Answer(result);
    }
}

/// <summary>The filters every B2 register takes (query string); an xlsx export reads every row, not a page.</summary>
public sealed record CostReportArgs(
    Guid? LineId, DateOnly? From, DateOnly? To, string? Basis, DateOnly? AsOf, int? Page, int? PageSize, string? Kind, string? SourceState,
    string? State, string? Element, string? Treatment, string? Flag, string? Code, string? Severity, bool? Live, string? Format, string? Lang)
{
    public ProductionCostHistory.Query Query() => new(
        LineId, From, To, Basis, AsOf, Page, PageSize, Kind, SourceState, State, Element, Treatment, Flag, Code, Severity,
        Live == true, All: Format == "xlsx");
}
