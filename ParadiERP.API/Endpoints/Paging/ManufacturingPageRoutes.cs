using ParadiERP.Application.Catalog;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Manufacturing;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using static ParadiERP.API.Endpoints.ManufacturingEndpoints;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// The manufacturing lists a page at a time (owner, 2026-11-17: "I need to paginate in backend, not frontend").
/// Each <c>…/page</c> route sits beside the list route it pages, in the same group, and reads exactly what that
/// route reads — the same service call, so the same line reach (<see cref="ManufacturingAccess"/>), the same
/// branch scope and the same refusals. The filters the screens applied in the browser run here instead, with the
/// same meaning, over every row; only the page is sent. The lists are computed in memory by their services (a
/// status worked out in C#, the till's Arabic fold, a report built row by row), so they are paged with
/// <see cref="ListPaging.Of{T}"/>. The figures over a list (its tiles, the choices a filter offers) are computed
/// over the same rows the old screen summed — the report as the server answers it — and sent in the summary.
/// The list routes themselves are unchanged: pickers, the order form and the exports still read them.
/// </summary>
public static class ManufacturingPageRoutes
{
    // ---------- configuration (ManufacturingEndpoints' group, /api/manufacturing) ----------

    /// <summary><c>GET /api/manufacturing/units/page</c>: what a unit measures and whether it is on; the units' own order.</summary>
    public static void MapUnitsPage(RouteGroupBuilder group) =>
        group.MapGet("/units/page", async (string? dimension, string? state, int? skip, int? take,
            [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
        {
            var rows = (await settings.UnitsAsync(ct))
                .Where(u => (IsAll(dimension) || u.Dimension == dimension) && KeepsState(state, u.IsActive))
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        });

    /// <summary>
    /// <c>GET /api/manufacturing/materials/page</c>: every active product as the Materials screen listed it, searched
    /// on its code and names with the till's fold; <c>role</c> one role or <c>manufacturing</c> (any but ordinary
    /// merchandise); <c>till</c> the Till column's word (<c>also</c>, <c>sold</c>, <c>not</c>); <c>baseUnit</c>
    /// <c>set</c> or <c>missing</c>.
    /// </summary>
    public static void MapMaterialsPage(RouteGroupBuilder group) =>
        group.MapGet("/materials/page", async (string? q, string? role, string? till, string? baseUnit, int? skip, int? take,
            [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
        {
            var needle = ListText.Fold(q);
            var rows = (await settings.MaterialsAsync(null, null, false, ct))
                .Where(m => Found(needle, m.Sku, m.NameFr, m.NameAr, m.NameEn))
                // The same three words the Till column shows, in the same order of precedence.
                .Where(m => IsAll(till) || (m.AlsoForSale ? "also" : m.SoldAtTill ? "sold" : "not") == till)
                .Where(m => baseUnit switch
                {
                    "set" => !string.IsNullOrEmpty(m.BaseUnitCode),
                    "missing" => string.IsNullOrEmpty(m.BaseUnitCode),
                    _ => true,
                })
                .Where(m => role switch
                {
                    null or "" or "all" => true,
                    "manufacturing" => m.Role != "None",
                    _ => m.Role == role,
                })
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        });

    /// <summary><c>GET /api/manufacturing/job-roles/page</c>: active or switched off.</summary>
    public static void MapJobRolesPage(RouteGroupBuilder group) =>
        group.MapGet("/job-roles/page", async (string? state, int? skip, int? take,
            [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
        {
            var rows = (await settings.JobRolesAsync(ct)).Where(r => KeepsState(state, r.IsActive)).ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        });

    /// <summary><c>GET /api/manufacturing/stage-templates/page</c>: <c>product</c> <c>any</c> (shared) or <c>one</c> (meant for a product); active or not.</summary>
    public static void MapStageTemplatesPage(RouteGroupBuilder group) =>
        group.MapGet("/stage-templates/page", async (string? product, string? state, int? skip, int? take,
            [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
        {
            var rows = (await settings.StageTemplatesAsync(null, ct))
                .Where(t => product switch
                {
                    "any" => t.ProductId is null,
                    "one" => t.ProductId is not null,
                    _ => true,
                } && KeepsState(state, t.IsActive))
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        });

    /// <summary>
    /// <c>GET /api/manufacturing/lines/page</c>: the lines the caller reaches (<c>includeInactive</c> as the list
    /// route), with or without a manager, with or without an active recipe. The tiles count every line the state
    /// choice keeps, as the screen's tiles always did.
    /// </summary>
    public static void MapLinesPage(RouteGroupBuilder group) =>
        group.MapGet("/lines/page", async (bool? includeInactive, string? manager, string? boms, int? skip, int? take,
            [FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var all = await lines.ListAsync(caller, includeInactive == true, ct);
            var rows = all.Where(l => Keeps(manager, l.ManagerUserId is not null) && Keeps(boms, l.ActiveBomCount > 0)).ToList();
            var summary = new MfgLinesPageSummary(all.Count, all.Sum(l => l.EmployeeCount), all.Sum(l => l.ActiveBomCount));
            return Results.Ok(ListPaging.Of(rows, skip, take, summary));
        });

    /// <summary>
    /// <c>GET /api/manufacturing/lines/{id}/stock/page</c>: the line location's stock rows, by the product's
    /// manufacturing role. A line out of reach is 404, as on the list route.
    /// </summary>
    public static void MapLineStockPage(RouteGroupBuilder group) =>
        group.MapGet("/lines/{id:guid}/stock/page", async (Guid id, string? role, int? skip, int? take,
            [FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var result = await lines.StockAsync(caller, id, ct);
            if (!result.Ok) return Answer(result);
            var all = result.Value!;
            var rows = all.Where(s => IsAll(role) || s.Role == role).ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take, new MfgChoicesPageSummary(all.Count, FirstSeen(all, s => s.Role))));
        });

    /// <summary>
    /// <c>GET /api/manufacturing/boms/page</c>: the list route's own filters (product, line, status), the search on
    /// the product's code and names, and <c>scope</c> <c>every</c> (a recipe for every line) or <c>one</c> (a line's own).
    /// </summary>
    public static void MapBomsPage(RouteGroupBuilder group) =>
        group.MapGet("/boms/page", async (Guid? productId, Guid? lineId, string? status, string? q, string? scope, int? skip, int? take,
            [FromServices] BillsOfMaterials boms, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var needle = ListText.Fold(q);
            var rows = (await boms.ListAsync(caller, productId, lineId, status, ct))
                .Where(b => scope switch
                {
                    "every" => string.IsNullOrEmpty(b.ProductionLineCode),
                    "one" => !string.IsNullOrEmpty(b.ProductionLineCode),
                    _ => true,
                })
                .Where(b => Found(needle, b.ProductSku, b.ProductNameFr, b.ProductNameAr, b.ProductNameEn))
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        });

    // ---------- operations (ManufacturingOperationEndpoints' group, /api/manufacturing) ----------

    /// <summary>
    /// <c>GET /api/manufacturing/orders/page</c>: the list route's filters (line, status, product), the search on the
    /// order's number and product, <c>late</c> and <c>startedShort</c> (<c>yes</c>/<c>no</c>), and the planned end
    /// between two company days (<c>endFrom</c>, <c>endTo</c>, ISO). Newest first, as the list route.
    /// </summary>
    public static void MapOrdersPage(RouteGroupBuilder group) =>
        group.MapGet("/orders/page", async (Guid? lineId, string? status, Guid? productId, string? q, string? late, string? startedShort,
            DateOnly? endFrom, DateOnly? endTo, int? skip, int? take,
            [FromServices] ProductionOrders orders, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var needle = ListText.Fold(q);
            var rows = (await orders.ListAsync(caller, lineId, status, productId, ct))
                .Where(o => Keeps(late, o.Late) && Keeps(startedShort, o.StartedShort))
                // The planned end is a company day: it compares as written.
                .Where(o => (endFrom is null || o.PlannedEnd >= endFrom) && (endTo is null || o.PlannedEnd <= endTo))
                .Where(o => Found(needle, o.Number, o.ProductSku, o.ProductNameFr, o.ProductNameAr, o.ProductNameEn))
                .OrderByDescending(o => o.CreatedAtUtc).ThenBy(o => o.Id)
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        });

    /// <summary>
    /// <c>GET /api/manufacturing/logs/page</c>: the list route's own filters — line, the record's day between two
    /// company days (<c>from</c>, <c>to</c>) and its state. Newest day first.
    /// </summary>
    public static void MapLogsPage(RouteGroupBuilder group) =>
        group.MapGet("/logs/page", async (Guid? lineId, DateOnly? from, DateOnly? to, string? status, int? skip, int? take,
            [FromServices] ProductionLogs logs, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var rows = (await logs.ListAsync(caller, lineId, from, to, status, ct))
                .OrderByDescending(l => l.Day).ThenBy(l => l.Id).ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        });

    /// <summary>
    /// <c>GET /api/manufacturing/reports/pos-exceptions/page</c>: the report's own filters, then the product's
    /// manufacturing <c>role</c>. A branch user's report is their own branch's (CallersBranch, and the service
    /// again). The tiles are the report's, over every row it answers, whatever the role chosen — as before.
    /// </summary>
    public static void MapPosExceptionsPage(RouteGroupBuilder group) =>
        group.MapGet("/reports/pos-exceptions/page", async (DateOnly? from, DateOnly? to, Guid? branchId, Guid? productId, string? sale,
                string? stale, string? role, int? skip, int? take,
                [FromServices] ManufacturingReports reports, [FromServices] IRegionalSettingsProvider regional,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            {
                if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
                var result = await reports.PosExceptionsAsync(caller, from, to, branchId, productId, sale, stale, StoreDocumentHistory.Zone(regional), ct);
                if (!result.Ok) return Answer(result);
                var report = result.Value!;
                var rows = report.Rows.Where(x => IsAll(role) || x.Role == role).ToList();
                var summary = new MfgPosExceptionsPageSummary(
                    report.Total, report.TotalQuantity, report.StaleCount, report.AfterWithdrawalCount, report.UnknownCount,
                    FirstSeen(report.Rows, x => x.Role));
                return Results.Ok(ListPaging.Of(rows, skip, take, summary));
            })
            .WithMetadata(new BranchScopedAttribute("a branch user's report is their own branch's, whatever branchId says (CallersBranch, and the service again)"))
            .AtCallersBranch();

    /// <summary>
    /// <c>GET /api/manufacturing/reports/pending-movements/page</c>: the report for the line (or every line reached),
    /// then the document's <c>kind</c> and its company day between <c>from</c> and <c>to</c>. The tiles and the kinds
    /// offered are the report's, over every row of the line — as the screen showed them.
    /// </summary>
    public static void MapPendingMovementsPage(RouteGroupBuilder group) =>
        group.MapGet("/reports/pending-movements/page", async (Guid? lineId, string? kind, DateOnly? from, DateOnly? to, int? skip, int? take,
            [FromServices] ManufacturingReports reports, [FromServices] IRegionalSettingsProvider regional,
            PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var report = await reports.PendingMovementsAsync(caller, lineId, ct);
            var zone = StoreDocumentHistory.Zone(regional);
            var rows = report.Rows
                .Where(r => IsAll(kind) || r.Kind == kind)
                .Where(r => (from is null && to is null) || InDays(CompanyDays.Today(r.AtUtc, zone), from, to))
                // The report's order, with a tie-break so two rows of one moment keep their place between pages.
                .OrderBy(r => r.AtUtc).ThenBy(r => r.Document, StringComparer.Ordinal).ThenBy(r => r.Number, StringComparer.Ordinal)
                .ThenBy(r => r.ProductId)
                .ToList();
            var summary = new MfgPendingMovementsPageSummary(
                report.IssuedValue, report.ReturnedValue, report.ReleasedValue, report.WorkInProgressValue, report.ReleasesValued,
                report.Rows.Count, FirstSeen(report.Rows, r => r.Kind));
            return Results.Ok(ListPaging.Of(rows, skip, take, summary));
        });

    /// <summary><c>GET /api/manufacturing/reports/material-flow/page</c>: the differences found, by the check that found them.</summary>
    public static void MapMaterialFlowPage(RouteGroupBuilder group) =>
        group.MapGet("/reports/material-flow/page", async (string? kind, int? skip, int? take,
            [FromServices] ManufacturingReports reports, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var report = await reports.MaterialFlowAsync(caller, ct);
            var rows = report.Issues.Where(i => IsAll(kind) || i.Kind == kind).ToList();
            var summary = new MfgMaterialFlowPageSummary(
                report.Issues.Count, report.MovementsChecked, report.MaterialsChecked, report.StocksChecked, FirstSeen(report.Issues, i => i.Kind));
            return Results.Ok(ListPaging.Of(rows, skip, take, summary));
        });

    /// <summary>
    /// <c>GET /api/manufacturing/reports/work-in-progress/page</c>: the live orders of the line (or every line reached),
    /// by state and by whether their figures are still provisional. The tiles and the per-line table are the
    /// report's, withheld exactly where it withholds them.
    /// </summary>
    public static void MapWorkInProgressPage(RouteGroupBuilder group) =>
        group.MapGet("/reports/work-in-progress/page", async (Guid? lineId, string? status, string? provisional, int? skip, int? take,
            [FromServices] ProductionCosts costs, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var result = await costs.WorkInProgressAsync(caller, lineId, ct);
            if (!result.Ok) return Answer(result);
            var report = result.Value!;
            var rows = report.Rows.Where(r => (IsAll(status) || r.Status == status) && Keeps(provisional, r.Provisional)).ToList();
            var summary = new MfgWorkInProgressPageSummary(
                report.CostToDate, report.ReleasedValue, report.AtLineValue, report.WorkInProgress, report.Lines, report.MoneyShown,
                report.Rows.Count, FirstSeen(report.Rows, r => r.Status));
            return Results.Ok(ListPaging.Of(rows, skip, take, summary));
        });

    /// <summary>
    /// <c>GET /api/manufacturing/reports/labour-absorption/page</c>: one payroll month (the company's month today when
    /// none is asked) of the line (or every line reached), by the basis each employee's hours are read at. The
    /// summary is the report without its rows: its month, totals, per-line table and notes.
    /// </summary>
    public static void MapLabourAbsorptionPage(RouteGroupBuilder group) =>
        group.MapGet("/reports/labour-absorption/page", async (int? year, int? month, Guid? lineId, string? basis, int? skip, int? take,
            [FromServices] ProductionCosts costs, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var result = await costs.LabourAbsorptionAsync(caller, year, month, lineId, ct);
            if (!result.Ok) return Answer(result);
            var report = result.Value!;
            var rows = report.Rows.Where(r => IsAll(basis) || r.Basis == basis).ToList();
            var summary = new MfgLabourAbsorptionPageSummary(
                report.Year, report.Month, report.MoneyShown, report.OrderHours, report.OtherHours, report.EmployerCost,
                report.AbsorbedCost, report.UnabsorbedCost, report.EmployerSocialMissing, report.Lines, report.LabourAbsorption,
                report.PremiumInCharges, report.Rows.Count, FirstSeen(report.Rows, r => r.Basis),
                report.Rows.Any(r => r.Basis != "Actual"));
            return Results.Ok(ListPaging.Of(rows, skip, take, summary));
        });

    // ---------- the practice ledger (ManufacturingAccountingEndpoints' group, /api/manufacturing/accounting) ----------

    /// <summary>
    /// <c>GET /api/manufacturing/accounting/practice-ledger/entries/page</c>: one month's entries (the company's month
    /// today when none is asked) of the line (or every line reached), by <c>kind</c> and by the branch they are raised
    /// at (<c>entryBranchId</c> — not <c>branchId</c>: the month is company-wide, read under the costs key, and no
    /// branch rule rewrites it). The summary is the month without its entries and late costs — the model, the
    /// accounts, the checks, the postings, the totals — plus the kinds and branches the entries name.
    /// </summary>
    public static void MapPracticeLedgerEntriesPage(RouteGroupBuilder group) =>
        group.MapGet("/practice-ledger/entries/page", async (int? year, int? month, Guid? lineId, string? kind, Guid? entryBranchId,
            int? skip, int? take, [FromServices] PracticeLedgerReport ledger, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var result = await ledger.MonthAsync(caller, year, month, lineId, ct);
            if (!result.Ok) return Answer(result);
            var d = result.Value!;
            var rows = d.Entries.Where(e => (IsAll(kind) || e.Kind == kind) && (entryBranchId is null || e.BranchId == entryBranchId)).ToList();
            var branches = new List<MfgLedgerBranch>();
            foreach (var e in d.Entries)
                if (!branches.Any(b => b.Id == e.BranchId))
                    branches.Add(new MfgLedgerBranch(e.BranchId, e.BranchCode, e.BranchNameAr, e.BranchNameFr, e.BranchNameEn));
            var summary = new MfgPracticeLedgerPageSummary(
                d.Year, d.Month, d.MonthToDate, d.Model, d.MonthEndWorkInProgress, d.LateCostTreatment, d.Variances,
                d.Accounts, d.Checks, d.UnmappedSlots, d.Notes, d.TotalDebit, d.TotalCredit, d.PostingFromMonth, d.Postings,
                d.Entries.Count, d.LateCosts.Count, FirstSeen(d.Entries, e => e.Kind), branches);
            return Results.Ok(ListPaging.Of(rows, skip, take, summary));
        });

    /// <summary><c>GET /api/manufacturing/accounting/practice-ledger/late-costs/page</c>: the month's late costs, by cost element.</summary>
    public static void MapPracticeLedgerLateCostsPage(RouteGroupBuilder group) =>
        group.MapGet("/practice-ledger/late-costs/page", async (int? year, int? month, Guid? lineId, string? element, int? skip, int? take,
            [FromServices] PracticeLedgerReport ledger, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var result = await ledger.MonthAsync(caller, year, month, lineId, ct);
            if (!result.Ok) return Answer(result);
            var late = result.Value!.LateCosts;
            var rows = late.Where(l => IsAll(element) || l.Element == element).ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take, new MfgChoicesPageSummary(late.Count, FirstSeen(late, l => l.Element))));
        });

    // ---------- the filters' shared meanings ----------

    /// <summary>No choice made: the screens send nothing for "all", and "all" itself means the same.</summary>
    private static bool IsAll(string? choice) => string.IsNullOrEmpty(choice) || choice == "all";

    /// <summary>A yes/no filter: <c>yes</c> keeps the rows where it holds, <c>no</c> the others, anything else every row.</summary>
    private static bool Keeps(string? choice, bool value) => choice switch
    {
        "yes" => value,
        "no" => !value,
        _ => true,
    };

    /// <summary><c>active</c> or <c>inactive</c>; anything else every row.</summary>
    private static bool KeepsState(string? choice, bool active) => choice switch
    {
        "active" => active,
        "inactive" => !active,
        _ => true,
    };

    /// <summary>The till's search: the folded needle anywhere in the folded fields, as the screens matched it.</summary>
    private static bool Found(string needle, params string?[] fields) =>
        needle.Length == 0 || ListText.Fold(string.Join(' ', fields.Select(f => f ?? ""))).Contains(needle, StringComparison.Ordinal);

    private static bool InDays(DateOnly day, DateOnly? from, DateOnly? to) =>
        (from is null || day >= from) && (to is null || day <= to);

    /// <summary>The values a column holds, each once, in the order they first appear — what a filter offers.</summary>
    private static List<string> FirstSeen<T>(IEnumerable<T> rows, Func<T, string> value)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var row in rows)
            if (seen.Add(value(row))) list.Add(value(row));
        return list;
    }
}

/// <summary>The lines' tiles: every line the state choice keeps, its employees working now and its active recipes.</summary>
public sealed record MfgLinesPageSummary(int Lines, int Employees, int ActiveBoms);

/// <summary>A list's whole count and the values a filter offers (the stock's roles, the late costs' elements).</summary>
public sealed record MfgChoicesPageSummary(int All, IReadOnlyList<string> Choices);

/// <summary>The POS exceptions report's tiles, over every row it answered, and the roles those rows name.</summary>
public sealed record MfgPosExceptionsPageSummary(
    int Sales, decimal TotalQuantity, int StaleCount, int AfterWithdrawalCount, int UnknownCount, IReadOnlyList<string> Roles);

/// <summary>The pending movements' tiles (the report's), its row count and the document kinds it names.</summary>
public sealed record MfgPendingMovementsPageSummary(
    decimal IssuedValue, decimal ReturnedValue, decimal? ReleasedValue, decimal WorkInProgressValue, bool ReleasesValued,
    int All, IReadOnlyList<string> Kinds);

/// <summary>The material flow's tiles and the checks its differences name.</summary>
public sealed record MfgMaterialFlowPageSummary(int Issues, int MovementsChecked, int MaterialsChecked, int StocksChecked, IReadOnlyList<string> Kinds);

/// <summary>The work-in-progress report without its rows: its totals, per-line table, whether money is shown, the states it names.</summary>
public sealed record MfgWorkInProgressPageSummary(
    decimal? CostToDate, decimal? ReleasedValue, decimal AtLineValue, decimal? WorkInProgress,
    IReadOnlyList<WorkInProgressLineDto>? Lines, bool MoneyShown, int All, IReadOnlyList<string> Statuses);

/// <summary>
/// The labour report without its rows. <c>NotApproved</c>: some employee's month is read at another basis than the
/// approved payroll (the screen's hint), over every row.
/// </summary>
public sealed record MfgLabourAbsorptionPageSummary(
    int Year, int Month, bool MoneyShown, decimal OrderHours, decimal OtherHours, decimal? EmployerCost, decimal? AbsorbedCost,
    decimal? UnabsorbedCost, bool EmployerSocialMissing, IReadOnlyList<LabourLineMonthDto>? Lines, string LabourAbsorption,
    decimal? PremiumInCharges, int All, IReadOnlyList<string> Bases, bool NotApproved);

/// <summary>A branch the month's entries are raised at, as the branch filter offers it.</summary>
public sealed record MfgLedgerBranch(Guid Id, string Code, string NameAr, string NameFr, string NameEn);

/// <summary>The practice ledger's month without its entries and late costs, with their counts and the filters' choices.</summary>
public sealed record MfgPracticeLedgerPageSummary(
    int Year, int Month, bool MonthToDate, string Model, string MonthEndWorkInProgress, string LateCostTreatment, string Variances,
    IReadOnlyList<PracticeLedgerAccountDto> Accounts, IReadOnlyList<PracticeLedgerCheckDto> Checks,
    IReadOnlyList<string> UnmappedSlots, IReadOnlyList<string> Notes, decimal TotalDebit, decimal TotalCredit,
    string? PostingFromMonth, IReadOnlyList<PracticeLedgerPostingDto>? Postings,
    int EntryCount, int LateCostCount, IReadOnlyList<string> Kinds, IReadOnlyList<MfgLedgerBranch> Branches);
