using System.Security.Claims;
using ParadisERP.Application.Contracts;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Maintenance;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// MS-04's workshop list (docs/maintenance/plan.md, "MS-04 as built"): who of a branch may be booked on its jobs while MS-04
/// says "only the workshop list". Under the jobs group — 409 <c>maintenance_off</c> while MS-01 is off, and branch-held like
/// jobs: the list reads the caller's own branch for everyone but the Admin (CallersBranch), a person is added to the
/// caller's own branch's list (the request's branch replaced), and a row of another branch's list named by id answers 404.
/// Read with the settings' keys (maintenance.view, maintenance.settings or accounting.manage); kept under
/// maintenance.settings (RoutePermissions) — no key of its own.
/// </summary>
public static partial class MaintenanceEndpoints
{
    private static void MapWorkshop(RouteGroupBuilder jobs)
    {
        jobs.MapGet("/workshop", async (Guid? branchId, [FromServices] MaintenanceWorkshop workshop, CancellationToken ct) =>
            Results.Ok(await workshop.ListAsync(branchId, ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));

        jobs.MapPost("/workshop", async (AddMaintenanceWorkshopMemberRequest r, ClaimsPrincipal caller,
            [FromServices] MaintenanceWorkshop workshop, CancellationToken ct) =>
            // A non-admin's request already names their own branch (CallersBranch); the answer shows that branch's list.
            Answer(await workshop.AddAsync(r, Actor(caller), BranchAssignment.IsCompanyScoped(caller) ? null : r.BranchId, ct)));

        jobs.MapDelete("/workshop/{id:guid}", async (Guid id, ClaimsPrincipal caller, [FromServices] PosDbContext db,
            [FromServices] MaintenanceWorkshop workshop, CancellationToken ct) =>
        {
            var scope = BranchAssignment.IsCompanyScoped(caller) ? null : await BranchAssignment.ForCallerAsync(caller, db, ct);
            return Answer(await workshop.RemoveAsync(id, scope, ct));
        })
            .ForBranchOf(DocumentBranches.MaintenanceWorkshopMember);
    }
}
