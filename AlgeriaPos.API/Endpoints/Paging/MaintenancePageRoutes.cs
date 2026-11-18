using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Infrastructure.Authorization;
using AlgeriaPos.Infrastructure.Maintenance;
using Microsoft.AspNetCore.Mvc;

namespace AlgeriaPos.API.Endpoints;

/// <summary>
/// The maintenance lists a page at a time (owner, 2026-11-17: "I need to paginate in backend, not frontend"). Each route is
/// <c>&lt;the list's own GET route&gt;/page</c> beside it, mapped on the same jobs group (409 <c>maintenance_off</c> while MS-01
/// is off; a parameter named <c>branchId</c> the caller's own branch for everyone but the Admin, CallersBranch; a job named by
/// id 404 at another branch), reads through the same service call with the same arguments, and so keeps its scope, its
/// permission (RoutePermissions: the base route's entry) and its figures. The filters the screens applied in the browser run
/// here over every row; only the page is sent, with the list's figures over all its filtered rows in <c>Summary</c>.
/// <para>
/// The lists are reports and registers the services build in memory (costs computed on read, a month's practice ledger,
/// coverage of return-note lines, the till's folded search), so they are paged with <see cref="ListPaging.Of{T}"/>; the jobs
/// list without words is counted and cut in SQL (<see cref="MaintenanceJobs.PageAsync"/>). A screen filter a non-admin could
/// aim at another branch (<c>branch</c>) only narrows the caller's scope, never widens it.
/// </para>
/// </summary>
public static class MaintenancePageRoutes
{
    private const string Scoped = "a non-admin's branchId is replaced by their own branch (CallersBranch)";

    /// <summary>Every maintenance page route, on the jobs group of <c>MapMaintenanceEndpoints</c>.</summary>
    public static void MapAll(RouteGroupBuilder jobs)
    {
        MapJobsPage(jobs);
        MapWaitingLinesPage(jobs);
        MapWaitingBucketsPage(jobs);
        MapHistoryPiecesPage(jobs);
        MapHistoryJobsPage(jobs);
        MapWorkshopPage(jobs);
        MapRepairRatesPage(jobs);
        MapJobHoursPage(jobs);
        MapRechargesReportPage(jobs);
        MapProductsReportPage(jobs);
        MapProfitReportPage(jobs);
        MapLedgerEntriesPage(jobs);
        MapLedgerPostedAtIssuePage(jobs);
        MapLedgerOpenJobsPage(jobs);
        MapLedgerInvoicesPage(jobs);
        MapLedgerProvisionsPage(jobs);
    }

    // ---------- Jobs (base: GET /jobs) ----------

    /// <summary>Maintenance › Jobs: the scope of <c>GET /jobs</c>, plus the screen's branch, day-opened and word filters.</summary>
    public static void MapJobsPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/jobs/page", async (Guid? branchId, Guid? branch, string? status, string? kind, Guid? productId, string? q,
            DateOnly? from, DateOnly? to, int? skip, int? take, [FromServices] MaintenanceJobs service, CancellationToken ct) =>
            Results.Ok(await service.PageAsync(branchId, branch, status, kind, productId, q, from, to, skip, take, ct)))
            .WithMetadata(new BranchScopedAttribute(Scoped));

    // ---------- Waiting for repair (base: GET /waiting) ----------

    /// <summary>
    /// The return-note lines no job covers in full, oldest return first, as <c>GET /waiting</c> lists them; filtered by the
    /// Admin's branch, the day returned (company calendar) and whether a recharge was typed at return.
    /// </summary>
    public static void MapWaitingLinesPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/waiting/lines/page", async (Guid? branchId, Guid? branch, DateOnly? from, DateOnly? to, string? recharge,
            int? skip, int? take, [FromServices] MaintenanceJobs service, [FromServices] IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var waiting = await service.WaitingAsync(branchId, ct);
            var zone = CompanyDays.Zone(regional.Get().TimeZone);
            var rows = waiting.Lines
                .Where(l => (branch is not Guid b || l.BranchId == b)
                    && (recharge switch { "yes" => l.Recharge > 0m, "no" => !(l.Recharge > 0m), _ => true })
                    && OnCompanyDays(l.ReturnedAtUtc, from, to, zone))
                .OrderBy(l => l.ReturnedAtUtc).ThenBy(l => l.ReturnNoteLineId)
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take, WaitingBranches(waiting)));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    /// <summary>The products in maintenance per branch, as <c>GET /waiting</c> lists them; filtered by branch and by free units.</summary>
    public static void MapWaitingBucketsPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/waiting/buckets/page", async (Guid? branchId, Guid? branch, string? free, int? skip, int? take,
            [FromServices] MaintenanceJobs service, CancellationToken ct) =>
        {
            var waiting = await service.WaitingAsync(branchId, ct);
            var rows = waiting.Buckets
                .Where(x => (branch is not Guid b || x.BranchId == b)
                    && (free switch { "some" => x.Free > 0m, "none" => !(x.Free > 0m), _ => true }))
                .OrderBy(x => x.BranchName, StringComparer.Ordinal).ThenBy(x => x.Sku, StringComparer.Ordinal)
                .ThenBy(x => x.BranchId).ThenBy(x => x.ProductId)
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take, WaitingBranches(waiting)));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    /// <summary>Both waiting lists offer the branches either holds, before any filter (the screen's one branch filter).</summary>
    private static MaintenanceBranchesPageSummary WaitingBranches(MaintenanceWaitingDto waiting) =>
        new(MaintenanceBranchOptions.Of(waiting.Lines.Select(l => (l.BranchId, l.BranchName))
            .Concat(waiting.Buckets.Select(x => (x.BranchId, x.BranchName)))));

    // ---------- Repair history (base: GET /history) ----------

    /// <summary>The pieces of the type that name a reference, as <c>GET /history</c> lists them (by reference).</summary>
    public static void MapHistoryPiecesPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/history/pieces/page", async (Guid productId, string? reference, Guid? branchId, int? skip, int? take,
            HttpContext http, [FromServices] MaintenanceCosting costing, CancellationToken ct) =>
        {
            var result = await costing.HistoryAsync(productId, reference, branchId, Has(http), ct);
            return result.Ok ? Results.Ok(ListPaging.Of(result.Value!.Pieces, skip, take)) : Refusal(result);
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    /// <summary>
    /// The jobs of the type, or of the piece asked, as <c>GET /history</c> lists them (opened first first); the summary is the
    /// history's head — the tiles total every job of it — and the references the piece box offers.
    /// </summary>
    public static void MapHistoryJobsPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/history/jobs/page", async (Guid productId, string? reference, Guid? branchId, int? skip, int? take,
            HttpContext http, [FromServices] MaintenanceCosting costing, CancellationToken ct) =>
        {
            var result = await costing.HistoryAsync(productId, reference, branchId, Has(http), ct);
            if (!result.Ok) return Refusal(result);
            var history = result.Value!;
            var summary = new MaintenanceHistoryPageSummary(history with { Jobs = [], Pieces = [] },
                history.Pieces.Select(p => p.Reference).ToList());
            return Results.Ok(ListPaging.Of(history.Jobs, skip, take, summary));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    // ---------- Workshop (base: GET /workshop) ----------

    /// <summary>
    /// The workshop list as <c>GET /workshop</c> reads it (by branch, then name), filtered by the Admin's branch and by each
    /// row's state in words (<c>bookable</c>, <c>moved</c>, <c>left</c>); the rest of that answer travels in the summary.
    /// </summary>
    public static void MapWorkshopPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/workshop/page", async (Guid? branchId, Guid? branch, string? state, int? skip, int? take,
            [FromServices] MaintenanceWorkshop workshop, CancellationToken ct) =>
        {
            var list = await workshop.ListAsync(branchId, ct);
            var all = string.IsNullOrWhiteSpace(state) || state == "all";
            var rows = list.Members
                .Where(m => (branch is not Guid b || m.BranchId == b) && (all || WorkshopState(m) == state))
                .OrderBy(m => m.BranchName, StringComparer.Ordinal).ThenBy(m => m.FullName, StringComparer.Ordinal).ThenBy(m => m.Id)
                .ToList();
            var summary = new MaintenanceWorkshopPageSummary(list.BookableEmployees, list.Available, list.Branches,
                MaintenanceBranchOptions.Of(list.Members.Select(m => (m.BranchId, m.BranchName))), list.Members.Count);
            return Results.Ok(ListPaging.Of(rows, skip, take, summary));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    /// <summary>The screen's <c>workshopMemberState</c>: left, moved to another branch, or bookable.</summary>
    private static string WorkshopState(MaintenanceWorkshopMemberDto m) =>
        !m.Employed ? "left" : m.EmployeeBranchId == m.BranchId ? "bookable" : "moved";

    // ---------- Repair rates (base: GET /repair-rates) ----------

    /// <summary>
    /// The MS-23 rates by SKU, as <c>GET /repair-rates</c> lists them; filtered by the product's words (folded), the day the
    /// rate was last set (company calendar) and one product (the form reading a product's rate whatever page it is on).
    /// </summary>
    public static void MapRepairRatesPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/repair-rates/page", async (string? q, DateOnly? from, DateOnly? to, Guid? productId, int? skip, int? take,
            [FromServices] MaintenanceRepairRates rates, [FromServices] IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var list = await rates.ListAsync(ct);
            var zone = CompanyDays.Zone(regional.Get().TimeZone);
            var needle = ListText.Fold(q);
            var rows = list.Rates
                .Where(r => (productId is not Guid p || r.ProductId == p)
                    && OnCompanyDays(r.UpdatedAtUtc, from, to, zone)
                    && (needle.Length == 0 || ListText.Fold($"{r.Sku} {r.NameFr} {r.NameAr} {r.NameEn}").Contains(needle, StringComparison.Ordinal)))
                .OrderBy(r => r.Sku, StringComparer.Ordinal).ThenBy(r => r.ProductId)
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take,
                new MaintenanceRepairRatesPageSummary(list.ReturnRecharge, list.Products, list.Rates.Count)));
        });

    // ---------- A job's hours (base: GET /jobs/{id}/hours) ----------

    /// <summary>The job's bookings, newest day first, as <c>GET /jobs/{id}/hours</c>; the summary carries their total and today.</summary>
    public static void MapJobHoursPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/jobs/{id:guid}/hours/page", async (Guid id, int? skip, int? take, [FromServices] MaintenanceLabour labour, CancellationToken ct) =>
        {
            var result = await labour.HoursAsync(id, ct);
            if (!result.Ok) return Refusal(result);
            var hours = result.Value!;
            var rows = hours.Bookings
                .OrderByDescending(b => b.Day).ThenBy(b => b.EmployeeName, StringComparer.Ordinal).ThenBy(b => b.AtUtc).ThenBy(b => b.Id)
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take, hours with { Bookings = [] }));
        })
            .ForBranchOf(DocumentBranches.MaintenanceJob);

    // ---------- Reports (base: GET /reports/recharges, /reports/products, /reports/profit) ----------

    /// <summary>Recharge against cost: the period's rows by job number; the summary is the report's totals line and words.</summary>
    public static void MapRechargesReportPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/reports/recharges/page", async (Guid? branchId, DateOnly? from, DateOnly? to, int? skip, int? take, HttpContext http,
            [FromServices] MaintenanceCosting costing, CancellationToken ct) =>
        {
            var result = await costing.RechargesAsync(branchId, from, to, Has(http), ct);
            if (!result.Ok) return Refusal(result);
            var report = result.Value!;
            return Results.Ok(ListPaging.Of(report.Rows, skip, take, report with { Rows = [] }));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    /// <summary>Cost per equipment type: the period's rows by SKU; the summary is the report's totals line.</summary>
    public static void MapProductsReportPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/reports/products/page", async (Guid? branchId, DateOnly? from, DateOnly? to, int? skip, int? take, HttpContext http,
            [FromServices] MaintenanceCosting costing, CancellationToken ct) =>
        {
            var result = await costing.ProductsAsync(branchId, from, to, Has(http), ct);
            if (!result.Ok) return Refusal(result);
            var report = result.Value!;
            var rows = report.Rows.OrderBy(r => r.Sku, StringComparer.Ordinal).ThenBy(r => r.ProductId).ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take, report with { Rows = [] }));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    /// <summary>Company profit: the period's job lines; the summary is the totals line and the four breakdowns, whole.</summary>
    public static void MapProfitReportPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/reports/profit/page", async (Guid? branchId, DateOnly? from, DateOnly? to, int? skip, int? take, HttpContext http,
            [FromServices] MaintenanceCosting costing, CancellationToken ct) =>
        {
            var result = await costing.ProfitAsync(branchId, from, to, Has(http), ct);
            if (!result.Ok) return Refusal(result);
            var report = result.Value!;
            return Results.Ok(ListPaging.Of(report.Rows, skip, take, report with { Rows = [] }));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    // ---------- Practice ledger (base: GET /practice-ledger) ----------

    /// <summary>
    /// The month's entries, in the ledger's order; the summary is the month without its five lists — tiles, account totals,
    /// checks, postings, notes, all over every entry — and how many rows each list below holds.
    /// </summary>
    public static void MapLedgerEntriesPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/practice-ledger/entries/page", async (int? year, int? month, Guid? branchId, int? skip, int? take, HttpContext http,
            [FromServices] MaintenancePracticeLedgerReport ledger, CancellationToken ct) =>
        {
            var result = await ledger.MonthAsync(year, month, branchId, Has(http), ct);
            if (!result.Ok) return Refusal(result);
            var l = result.Value!;
            var summary = new MaintenanceLedgerPageSummary(
                l with { Entries = [], PostedAtIssue = [], OpenCustomerJobs = [], Invoices = [], Provisions = [] },
                l.PostedAtIssue.Count, l.OpenCustomerJobs.Count, l.Invoices.Count, l.Provisions?.Count ?? 0);
            return Results.Ok(ListPaging.Of(l.Entries, skip, take, summary));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    /// <summary>The month's parts documents already posted at issue (MS-32), in the ledger's order.</summary>
    public static void MapLedgerPostedAtIssuePage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/practice-ledger/posted-at-issue/page", async (int? year, int? month, Guid? branchId, int? skip, int? take, HttpContext http,
            [FromServices] MaintenancePracticeLedgerReport ledger, CancellationToken ct) =>
        {
            var result = await ledger.MonthAsync(year, month, branchId, Has(http), ct);
            return result.Ok ? Results.Ok(ListPaging.Of(result.Value!.PostedAtIssue, skip, take)) : Refusal(result);
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    /// <summary>The open customer jobs at the month's end, filtered by branch code; the summary offers the codes they hold.</summary>
    public static void MapLedgerOpenJobsPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/practice-ledger/open-jobs/page", async (int? year, int? month, Guid? branchId, string? branchCode, int? skip, int? take,
            HttpContext http, [FromServices] MaintenancePracticeLedgerReport ledger, CancellationToken ct) =>
        {
            var result = await ledger.MonthAsync(year, month, branchId, Has(http), ct);
            if (!result.Ok) return Refusal(result);
            var open = result.Value!.OpenCustomerJobs;
            var rows = string.IsNullOrWhiteSpace(branchCode) ? open : open.Where(o => o.BranchCode == branchCode).ToList();
            var codes = open.Select(o => o.BranchCode).Distinct().Order(StringComparer.Ordinal).ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take, new MaintenanceOpenJobsPageSummary(codes)));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    /// <summary>The month's customer-job invoices, filtered by whether they name a job (<c>matched</c>, <c>unmatched</c>).</summary>
    public static void MapLedgerInvoicesPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/practice-ledger/invoices/page", async (int? year, int? month, Guid? branchId, string? match, int? skip, int? take,
            HttpContext http, [FromServices] MaintenancePracticeLedgerReport ledger, CancellationToken ct) =>
        {
            var result = await ledger.MonthAsync(year, month, branchId, Has(http), ct);
            if (!result.Ok) return Refusal(result);
            var rows = result.Value!.Invoices
                .Where(v => match switch { "matched" => v.JobMatched, "unmatched" => !v.JobMatched, _ => true })
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    /// <summary>The guarantee provisions the month moves (MS-37), filtered by whether some is still held at the month's end.</summary>
    public static void MapLedgerProvisionsPage(RouteGroupBuilder jobs) =>
        jobs.MapGet("/practice-ledger/provisions/page", async (int? year, int? month, Guid? branchId, string? held, int? skip, int? take,
            HttpContext http, [FromServices] MaintenancePracticeLedgerReport ledger, CancellationToken ct) =>
        {
            var result = await ledger.MonthAsync(year, month, branchId, Has(http), ct);
            if (!result.Ok) return Refusal(result);
            var rows = (result.Value!.Provisions ?? Array.Empty<MaintenanceLedgerProvisionDto>())
                .Where(p => held switch { "yes" => p.HeldAtEnd > 0m, "no" => !(p.HeldAtEnd > 0m), _ => true })
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        })
            .WithMetadata(new BranchScopedAttribute(Scoped));

    // ---------- shared ----------

    private static Func<string, bool> Has(HttpContext http) => key => ServerAuthorization.Has(http.User, key);

    /// <summary>A refusal as the base routes answer it (<c>MaintenanceEndpoints.Answer</c>): its status, words and code.</summary>
    private static IResult Refusal<T>(MaintenanceResult<T> result) =>
        Results.Problem(detail: result.Error, statusCode: result.Status,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code });

    /// <summary>
    /// Whether an instant falls on the company's days from <paramref name="from"/> to <paramref name="to"/> (either open): the
    /// screens' <c>localDay(utc, zone)</c> compared with the ISO days they send.
    /// </summary>
    private static bool OnCompanyDays(DateTime utc, DateOnly? from, DateOnly? to, TimeZoneInfo zone)
    {
        if (from is null && to is null) return true;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
        return (from is not DateOnly f || day >= f) && (to is not DateOnly t || day <= t);
    }
}
