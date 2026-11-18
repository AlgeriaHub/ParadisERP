using System.Security.Claims;
using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Authorization;
using ParadisERP.Infrastructure.Maintenance;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Maintenance (docs/maintenance/plan.md).
/// <para>
/// Phase M0: Maintenance › Settings, read by whoever reads or sets maintenance or manages the accounts, changed under
/// maintenance.settings (MS-01 to MS-26) or accounting.manage (MS-30 to MS-40) — the store compares the request with the
/// saved row and asks for the key of whatever would change. Company-wide settings with no branch and no person's money:
/// no branch rule applies, as Costing settings. The settings route answers whether the module is on or off.
/// </para>
/// <para>
/// Phase M1: jobs and units. Every route but the settings answers 409 <c>maintenance_off</c> while MS-01 is off. Jobs
/// are branch-held documents: the list and Waiting for repair read the caller's own branch for everyone but the Admin
/// (CallersBranch), a new job is opened at the caller's branch, and a job of another branch named by id answers 404
/// (<see cref="CallersBranch.ForBranchOf{TBuilder}"/>). Reads need maintenance.view; opening, taking a draft and editing
/// need maintenance.jobs.manage; completing and cancelling need maintenance.jobs.close (RoutePermissions).
/// </para>
/// </summary>
public static partial class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/maintenance").WithTags("Maintenance");

        group.MapGet("/settings", async ([FromServices] MaintenanceSettingsStore store, CancellationToken ct) =>
            Results.Ok(await store.SettingsAsync(ct)));

        group.MapPut("/settings", async (UpdateMaintenanceSettingsRequest r, HttpContext http,
            [FromServices] MaintenanceSettingsStore store, CancellationToken ct) =>
        {
            var result = await store.UpdateAsync(r, key => ServerAuthorization.Has(http.User, key),
                MovementAuthor.Of(http.User).UserName, ct);
            return result.Error is null
                ? Results.Ok(result.Saved)
                : Results.Problem(detail: result.Error, statusCode: result.Status,
                    extensions: new Dictionary<string, object?> { ["code"] = result.Code });
        });

        // Phase M6: the account map's maintenance slots (MS-31), beside the settings.
        MapAccountMap(group);

        // ---------- Phase M1: jobs and units ----------
        var jobs = app.MapGroup("/api/maintenance").WithTags("Maintenance")
            .AddEndpointFilter(ModuleOnAsync)
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();
        MapLabour(jobs);
        MapCosts(jobs);
        MapCustomerJobs(group, jobs);
        MapLedger(jobs);
        MapWorkshop(jobs);
        MaintenancePageRoutes.MapAll(jobs);

        jobs.MapGet("/jobs", async (Guid? branchId, string? status, string? kind, Guid? productId,
            [FromServices] MaintenanceJobs service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(branchId, status, kind, productId, ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));

        jobs.MapGet("/waiting", async (Guid? branchId, [FromServices] MaintenanceJobs service, CancellationToken ct) =>
            Results.Ok(await service.WaitingAsync(branchId, ct)))
            .WithMetadata(new BranchScopedAttribute("a non-admin's branchId is replaced by their own branch (CallersBranch)"));

        jobs.MapGet("/jobs/{id:guid}", async (Guid id, [FromServices] MaintenanceJobs service, CancellationToken ct) =>
            Answer(await service.GetAsync(id, ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapPost("/jobs", async (CreateMaintenanceJobRequest r, ClaimsPrincipal caller,
            [FromServices] MaintenanceJobs service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(r, Actor(caller), ct);
            return result.Ok ? Results.Created($"/api/maintenance/jobs/{result.Value!.Id}", result.Value) : Answer(result);
        });

        jobs.MapPut("/jobs/{id:guid}", async (Guid id, UpdateMaintenanceJobRequest r, ClaimsPrincipal caller,
            [FromServices] MaintenanceJobs service, CancellationToken ct) =>
            Answer(await service.UpdateAsync(id, r, Actor(caller), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapPost("/jobs/{id:guid}/open", async (Guid id, ClaimsPrincipal caller,
            [FromServices] MaintenanceJobs service, CancellationToken ct) =>
            Answer(await service.OpenAsync(id, Actor(caller), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapPost("/jobs/{id:guid}/complete", async (Guid id, CompleteMaintenanceJobRequest r, ClaimsPrincipal caller,
            [FromServices] MaintenanceJobs service, CancellationToken ct) =>
            Answer(await service.CompleteAsync(id, r, Actor(caller), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapPost("/jobs/{id:guid}/cancel", async (Guid id, CancelMaintenanceJobRequest r, ClaimsPrincipal caller,
            [FromServices] MaintenanceJobs service, CancellationToken ct) =>
            Answer(await service.CancelAsync(id, r, Actor(caller), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        // ---------- Phase M2: parts and outside work ----------
        // Read with maintenance.view; issuing, returning and recording outside work (and the lists to pick from) under
        // maintenance.jobs.manage. Another branch's job answers 404, as every route by id.
        jobs.MapGet("/jobs/{id:guid}/work", async (Guid id, [FromServices] MaintenanceJobParts service, CancellationToken ct) =>
            Answer(await service.WorkAsync(id, ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapGet("/jobs/{id:guid}/work-options", async (Guid id, [FromServices] MaintenanceJobParts service, CancellationToken ct) =>
            Answer(await service.OptionsAsync(id, ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapPost("/jobs/{id:guid}/parts", async (Guid id, MaintenancePartsRequest r, ClaimsPrincipal caller,
            [FromServices] MaintenanceJobParts service, CancellationToken ct) =>
            Answer(await service.MoveAsync(id, r, Actor(caller), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);

        jobs.MapPost("/jobs/{id:guid}/expenses", async (Guid id, MaintenanceJobExpenseRequest r, ClaimsPrincipal caller,
            [FromServices] MaintenanceJobParts service, CancellationToken ct) =>
            Answer(await service.AddExpenseAsync(id, r, Actor(caller), BranchAssignment.IsCompanyScoped(caller), ct)))
            .ForBranchOf(DocumentBranches.MaintenanceJob);
    }

    public const string ModuleOff = "The maintenance module is off (Maintenance › Settings, MS-01).";

    /// <summary>MS-01: while the module is off, every route of it but the settings answers 409 <c>maintenance_off</c>.</summary>
    private static async ValueTask<object?> ModuleOnAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<PosDbContext>();
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, context.HttpContext.RequestAborted);
        if (settings.Module != MaintenanceSwitch.On)
            return Results.Problem(detail: ModuleOff, statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = MaintenanceJobs.OffCode });
        return await next(context);
    }

    private static MaintenanceActor Actor(ClaimsPrincipal caller)
    {
        var author = MovementAuthor.Of(caller);
        return new MaintenanceActor(author.UserId, author.UserName);
    }

    private static IResult Answer<T>(MaintenanceResult<T> result) => result.Ok
        ? Results.Ok(result.Value)
        : Results.Problem(detail: result.Error, statusCode: result.Status,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code });
}
