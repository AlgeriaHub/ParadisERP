using System.Security.Claims;
using ParadiERP.Application.Regional;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Sales;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// The sales reads. Any signed-in user may call them — a refund needs the original and a till
/// may one day print its Z-report from here — so every one of them is branch-scoped for a
/// cashier: the branch comes from the token's device claim, never from the query string, and
/// a lookup outside it answers 404 (docs/07 §2.3). The query bodies live in
/// <see cref="SalesQueries"/> so the rule is tested on the harness, not through HTTP.
/// </summary>
public static class SalesEndpoints
{
    public static void MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Sales").RequireAuthorization();

        group.MapGet("/sales", async (
            DateTime? from, DateTime? to, string? deviceCode, Guid? branchId, int? take,
            ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            var scope = await BranchScope.ForAsync(caller, db, ct);
            return Results.Ok(await SalesQueries.ListAsync(db, from, to, deviceCode, branchId, take, scope, ct));
        }).WithMetadata(new BranchScopedAttribute("a cashier's branchId filter is overridden by the till's branch, not honoured; a manager's by their own branch (CallersBranch)"))
        .AtCallersBranch();

        group.MapGet("/sales/{id:guid}", async (Guid id, ClaimsPrincipal caller, PosDbContext db, CancellationToken ct) =>
        {
            var sale = await SalesQueries.FindAsync(db, id, await BranchScope.ForAsync(caller, db, ct), ct);
            return sale is null ? Results.NotFound() : Results.Ok(sale);
        }).WithMetadata(new BranchScopedAttribute("another branch's sale is not found, not forbidden — a 403 would say it exists"));

        group.MapGet("/reports/summary", async (DateTime? from, DateTime? to, Guid? branchId,
            ClaimsPrincipal caller, PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var fromUtc = from ?? DateTime.UtcNow.Date;
            var toUtc = to ?? fromUtc.AddDays(1);
            if (toUtc <= fromUtc) return Results.BadRequest("'to' must be after 'from'.");
            if ((toUtc - fromUtc).TotalDays > SalesQueries.MaxSummaryDays)
                return Results.BadRequest($"Date range too wide — max {SalesQueries.MaxSummaryDays} days per request.");

            var scope = await BranchScope.ForAsync(caller, db, ct);
            return Results.Ok(await SalesQueries.SummaryAsync(db, fromUtc, toUtc, branchId, scope, regional.Get(), ct));
        }).WithMetadata(new BranchScopedAttribute("a cashier's Z-report is their own branch's and names no other; a manager's summary is their own branch's (CallersBranch)"))
        .AtCallersBranch();

        // SAL-02: sales whose goods had no cost, so their margin is incomplete. Days on the
        // company's clock, never the browser's; the current month when none are given.
        group.MapGet("/reports/missing-cost", async (DateOnly? from, DateOnly? to, Guid? branchId,
            ClaimsPrincipal caller, PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            var zone = StoreDocumentHistory.Zone(regional);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
            var start = from ?? new DateOnly(today.Year, today.Month, 1);
            var end = to ?? start.AddMonths(1).AddDays(-1);
            if (end < start) return Results.BadRequest("'to' must not be before 'from'.");
            if (end.DayNumber - start.DayNumber + 1 > SalesQueries.MaxMissingCostDays)
                return Results.BadRequest($"Date range too wide — max {SalesQueries.MaxMissingCostDays} days per request.");

            var scope = await BranchScope.ForAsync(caller, db, ct);
            return Results.Ok(await SalesQueries.MissingCostAsync(db, start, end, branchId, scope, zone, regional.Get(), ct));
        }).WithMetadata(new BranchScopedAttribute("a branch user's report is their own branch's, whatever branchId says (CallersBranch)"))
        .AtCallersBranch();
        FinancePageRoutes.MapSalesPage(group); FinancePageRoutes.MapMissingCostPage(group);
    }
}
