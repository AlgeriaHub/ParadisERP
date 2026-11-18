using System.Security.Claims;
using ParadiERP.Application.Contracts;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Authorization;
using ParadiERP.Infrastructure.Maintenance;
using Microsoft.AspNetCore.Mvc;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// Phase M5a: customer jobs, their price and their invoice, the MS-23 repair rates; and Phase M5's company profit report
/// (docs/maintenance/plan.md §3.2, §3.3). Under the jobs group — 409 <c>maintenance_off</c> while MS-01 is off, a job of
/// another branch 404, a report's <c>branchId</c> the caller's own for everyone but the Admin — except the return-note
/// screen's suggestions, which answer "not enabled" rather than an error so the rental screen is untouched while MS-01 or
/// MS-23 is off. Keys (RoutePermissions): the New job options maintenance.jobs.manage; the price read maintenance.view, set
/// maintenance.pricing; the invoice finance.invoices.create, plus maintenance.view checked in the service; the rates read
/// maintenance.view or maintenance.pricing, kept maintenance.pricing; the suggestions rentals.return or maintenance.view;
/// the profit report maintenance.costs.view (labour money under M4's privacy rule). Phase M7c adds the rework candidates
/// (maintenance.jobs.manage, branch-scoped): a customer's completed jobs whose guarantee still runs.
/// </summary>
public static partial class MaintenanceEndpoints
{
    private static void MapCustomerJobs(RouteGroupBuilder open, RouteGroupBuilder jobs)
    {
        jobs.MapGet("/new-job-options", async ([FromServices] MaintenanceCustomerJobs service, CancellationToken ct) =>
            Results.Ok(await service.NewJobOptionsAsync(ct)));

        // Phase M7c (MS-27, MS-37): the customer's completed jobs at the branch whose guarantee still runs, for a free rework.
        jobs.MapGet("/rework-candidates", async (Guid? branchId, Guid customerId, [FromServices] MaintenanceJobs service, CancellationToken ct) =>
            Results.Ok(await service.ReworkCandidatesAsync(branchId, customerId, ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));

        jobs.MapGet("/jobs/{id:guid}/price", async (Guid id, HttpContext http, [FromServices] MaintenanceCustomerJobs service, CancellationToken ct) =>
            Answer(await service.PriceAsync(id, key => ServerAuthorization.Has(http.User, key), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapPut("/jobs/{id:guid}/price", async (Guid id, SetMaintenanceJobPriceRequest r, HttpContext http,
            [FromServices] MaintenanceCustomerJobs service, CancellationToken ct) =>
            Answer(await service.SetPriceAsync(id, r, Actor(http.User), key => ServerAuthorization.Has(http.User, key), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapPost("/jobs/{id:guid}/invoice", async (Guid id, RaiseMaintenanceJobInvoiceRequest r, HttpContext http,
            [FromServices] MaintenanceCustomerJobs service, CancellationToken ct) =>
            Answer(await service.InvoiceAsync(id, r, Actor(http.User), key => ServerAuthorization.Has(http.User, key), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapGet("/repair-rates", async ([FromServices] MaintenanceRepairRates rates, CancellationToken ct) =>
            Results.Ok(await rates.ListAsync(ct)));

        jobs.MapPut("/repair-rates/{productId:guid}", async (Guid productId, SetMaintenanceRepairRateRequest r, ClaimsPrincipal caller,
            [FromServices] MaintenanceRepairRates rates, CancellationToken ct) =>
            Answer(await rates.SetAsync(productId, r, MovementAuthor.Of(caller).UserName, ct)));

        jobs.MapDelete("/repair-rates/{productId:guid}", async (Guid productId, [FromServices] MaintenanceRepairRates rates, CancellationToken ct) =>
            Answer(await rates.RemoveAsync(productId, ct)));

        jobs.MapGet("/reports/profit", async (Guid? branchId, DateOnly? from, DateOnly? to, HttpContext http,
            [FromServices] MaintenanceCosting costing, CancellationToken ct) =>
            Answer(await costing.ProfitAsync(branchId, from, to, key => ServerAuthorization.Has(http.User, key), ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));

        // Outside the module filter on purpose: the return-note screen asks whether to suggest, and is told "no" while the
        // module or MS-23 is off — never an error that would change the rental screen.
        open.MapGet("/repair-rates/suggestions", async ([FromServices] MaintenanceRepairRates rates, CancellationToken ct) =>
            Results.Ok(await rates.SuggestionsAsync(ct)));
    }
}
