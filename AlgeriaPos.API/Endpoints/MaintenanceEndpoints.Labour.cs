using System.Security.Claims;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Authorization;
using AlgeriaPos.Infrastructure.Maintenance;
using Microsoft.AspNetCore.Mvc;

namespace AlgeriaPos.API.Endpoints;

/// <summary>
/// Phase M3: labour on jobs (docs/maintenance/plan.md §2.5). Under the jobs group, so 409 <c>maintenance_off</c> while
/// MS-01 is off, and a job of another branch answers 404 (<see cref="CallersBranch.ForBranchOf{TBuilder}"/>). The hours
/// are read with maintenance.view; the people who may be booked, booking and removing need maintenance.jobs.manage
/// (RoutePermissions); passing the day's attendance needs maintenance.labour.override as well, checked here with a reason.
/// </summary>
public static partial class MaintenanceEndpoints
{
    private static void MapLabour(RouteGroupBuilder jobs)
    {
        jobs.MapGet("/jobs/{id:guid}/hours", async (Guid id, [FromServices] MaintenanceLabour labour, CancellationToken ct) =>
            Answer(await labour.HoursAsync(id, ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapGet("/jobs/{id:guid}/hours/candidates", async (Guid id, DateOnly? day, [FromServices] MaintenanceLabour labour,
            CancellationToken ct) =>
            Answer(await labour.CandidatesAsync(id, day, ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapPost("/jobs/{id:guid}/hours", async (Guid id, BookMaintenanceHoursRequest r, ClaimsPrincipal caller,
            [FromServices] MaintenanceLabour labour, CancellationToken ct) =>
            Answer(await labour.BookAsync(id, r, ServerAuthorization.Has(caller, PermissionCatalog.MaintenanceLabourOverride),
                Actor(caller), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapDelete("/jobs/{id:guid}/hours/{bookingId:guid}", async (Guid id, Guid bookingId, ClaimsPrincipal caller,
            [FromServices] MaintenanceLabour labour, CancellationToken ct) =>
            Answer(await labour.UnbookAsync(id, bookingId, Actor(caller), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);
    }
}
