using ParadisERP.Infrastructure.Authorization;
using ParadisERP.Infrastructure.Maintenance;
using Microsoft.AspNetCore.Mvc;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Phase M4: a job's cost and the maintenance cost reports (docs/maintenance/plan.md §3.1, §6.2, §7). Under the jobs group,
/// so 409 <c>maintenance_off</c> while MS-01 is off; every route needs maintenance.costs.view (RoutePermissions). A job of
/// another branch answers 404 (<see cref="CallersBranch.ForBranchOf{TBuilder}"/>); a report's <c>branchId</c> is the
/// caller's own branch for everyone but the Admin. Whether labour money is shown is decided in the service from the
/// caller's keys (pay-rate access: payroll.manage + manufacturing.costs.rates.view, the manufacturing rule).
/// </summary>
public static partial class MaintenanceEndpoints
{
    private static void MapCosts(RouteGroupBuilder jobs)
    {
        jobs.MapGet("/jobs/{id:guid}/cost", async (Guid id, HttpContext http, [FromServices] MaintenanceCosting costing, CancellationToken ct) =>
            Answer(await costing.JobAsync(id, key => ServerAuthorization.Has(http.User, key), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapGet("/reports/recharges", async (Guid? branchId, DateOnly? from, DateOnly? to, HttpContext http,
            [FromServices] MaintenanceCosting costing, CancellationToken ct) =>
            Answer(await costing.RechargesAsync(branchId, from, to, key => ServerAuthorization.Has(http.User, key), ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));

        jobs.MapGet("/reports/products", async (Guid? branchId, DateOnly? from, DateOnly? to, HttpContext http,
            [FromServices] MaintenanceCosting costing, CancellationToken ct) =>
            Answer(await costing.ProductsAsync(branchId, from, to, key => ServerAuthorization.Has(http.User, key), ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));

        // Phase M7d (MS-38, MNT-10): the repair history of an equipment type, or of one piece of it. maintenance.view; the costs
        // in it only to maintenance.costs.view, under M4's privacy rule (decided in the service).
        jobs.MapGet("/history", async (Guid productId, string? reference, Guid? branchId, HttpContext http,
            [FromServices] MaintenanceCosting costing, CancellationToken ct) =>
            Answer(await costing.HistoryAsync(productId, reference, branchId, key => ServerAuthorization.Has(http.User, key), ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));
    }
}
