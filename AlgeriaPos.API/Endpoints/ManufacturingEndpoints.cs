using Microsoft.AspNetCore.Mvc;
using AlgeriaPos.API.Subscriptions;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Authorization;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Manufacturing;
using AlgeriaPos.Infrastructure.Persistence;

namespace AlgeriaPos.API.Endpoints;

/// <summary>
/// Manufacturing, Phase 1 (docs/manufacturing/phase-1.md): production lines, the materials' roles and
/// units, units and conversions, job roles, stage templates, bills of materials and who holds the
/// manufacturing permissions. Configuration only — nothing here moves stock, values it or posts.
/// <list type="bullet">
/// <item>Who may call a route is <see cref="Authorization.RoutePermissions"/>'s: the five
/// <c>manufacturing.*</c> keys, and <c>users.manage</c> for the access page.</item>
/// <item>Which line a caller reaches, and what they may do on it, is <see cref="ManufacturingAccess"/>'s:
/// a line out of reach — and a BOM on it — is answered 404.</item>
/// <item>A refusal is a ProblemDetails with a stable <c>code</c> the screens translate.</item>
/// </list>
/// The rules themselves live in <c>Infrastructure/Manufacturing</c>; this file is the HTTP shape.
/// </summary>
public static class ManufacturingEndpoints
{
    public static void MapManufacturingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/manufacturing").WithTags("Manufacturing")
            .AddEndpointFilter<SubscriptionWriteFilter>();

        // ---------- units ----------
        group.MapGet("/units", async ([FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.UnitsAsync(ct)));
        ManufacturingPageRoutes.MapUnitsPage(group);
        group.MapPost("/units", async (UpsertUnitRequest r, [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.SaveUnitAsync(null, r, ct), created: u => $"/api/manufacturing/units/{u.Id}"));
        group.MapPut("/units/{id:guid}", async (Guid id, UpsertUnitRequest r, [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.SaveUnitAsync(id, r, ct)));
        group.MapGet("/units/convert", async (decimal quantity, Guid fromUnitId, Guid toUnitId, Guid? productId,
                [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.ConvertAsync(quantity, fromUnitId, toUnitId, productId, ct)));

        // ---------- specification fields ----------
        group.MapGet("/spec-fields", async ([FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.SpecAttributesAsync(ct)));
        group.MapPost("/spec-fields", async (UpsertSpecAttributeRequest r, [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.SaveSpecAttributeAsync(null, r, ct), created: a => $"/api/manufacturing/spec-fields/{a.Id}"));
        group.MapPut("/spec-fields/{id:guid}", async (Guid id, UpsertSpecAttributeRequest r, [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.SaveSpecAttributeAsync(id, r, ct)));

        // ---------- materials: each product's role and units ----------
        group.MapGet("/materials", async (string? role, string? search, bool? includeInactive,
                [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.MaterialsAsync(role, search, includeInactive == true, ct)));
        ManufacturingPageRoutes.MapMaterialsPage(group);
        group.MapGet("/materials/{productId:guid}", async (Guid productId, [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.MaterialAsync(productId, ct)));
        group.MapPut("/materials/{productId:guid}", async (Guid productId, ClassifyMaterialRequest r,
                [FromServices] ManufacturingSettings settings, HttpContext http, CancellationToken ct) =>
        {
            // Who changed it, for the sale-status audit (ProductSaleStatusChange).
            var author = MovementAuthor.Of(http.User);
            return Answer(await settings.ClassifyAsync(productId, r, author.UserId, author.UserName, ct));
        });

        // ---------- job roles ----------
        group.MapGet("/job-roles", async ([FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.JobRolesAsync(ct)));
        ManufacturingPageRoutes.MapJobRolesPage(group);
        group.MapPost("/job-roles", async (UpsertJobRoleRequest r, [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.SaveJobRoleAsync(null, r, ct), created: j => $"/api/manufacturing/job-roles/{j.Id}"));
        group.MapPut("/job-roles/{id:guid}", async (Guid id, UpsertJobRoleRequest r, [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.SaveJobRoleAsync(id, r, ct)));

        // ---------- stage templates ----------
        group.MapGet("/stage-templates", async (Guid? productId, [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Results.Ok(await settings.StageTemplatesAsync(productId, ct)));
        ManufacturingPageRoutes.MapStageTemplatesPage(group);
        group.MapGet("/stage-templates/{id:guid}", async (Guid id, [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.StageTemplateAsync(id, ct)));
        group.MapPost("/stage-templates", async (UpsertStageTemplateRequest r, [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.SaveStageTemplateAsync(null, r, ct), created: t => $"/api/manufacturing/stage-templates/{t.Id}"));
        group.MapPut("/stage-templates/{id:guid}", async (Guid id, UpsertStageTemplateRequest r,
                [FromServices] ManufacturingSettings settings, CancellationToken ct) =>
            Answer(await settings.SaveStageTemplateAsync(id, r, ct)));

        // ---------- production lines ----------
        group.MapGet("/lines", async (bool? includeInactive, [FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
            await CallerAsync(http, db, ct) is MfgCaller caller
                ? Results.Ok(await lines.ListAsync(caller, includeInactive == true, ct))
                : NoCaller());
        ManufacturingPageRoutes.MapLinesPage(group);
        group.MapGet("/lines/{id:guid}", (Guid id, [FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => lines.GetAsync(caller, id, ct)));
        group.MapPost("/lines", (UpsertProductionLineRequest r, [FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => lines.SaveAsync(caller, null, r, ct), created: l => $"/api/manufacturing/lines/{l.Line.Id}"));
        group.MapPut("/lines/{id:guid}", (Guid id, UpsertProductionLineRequest r, [FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => lines.SaveAsync(caller, id, r, ct)));
        group.MapPut("/lines/{id:guid}/members", (Guid id, SetLineMembersRequest r, [FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => lines.SetMembersAsync(caller, id, r, ct)));
        group.MapPut("/lines/{id:guid}/products", (Guid id, SetLineProductsRequest r, [FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => lines.SetProductsAsync(caller, id, r, ct)));
        group.MapPost("/lines/{id:guid}/employees", (Guid id, AssignLineEmployeeRequest r, [FromServices] ProductionLines lines,
                [FromServices] IRegionalSettingsProvider regional, [FromServices] TimeProvider time, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => lines.AssignEmployeeAsync(caller, id, r, Today(regional, time), ct)));
        group.MapPost("/lines/{id:guid}/employees/{assignmentId:guid}/end", (Guid id, Guid assignmentId, EndLineEmployeeRequest r,
                [FromServices] ProductionLines lines, [FromServices] IRegionalSettingsProvider regional, [FromServices] TimeProvider time,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => lines.EndEmployeeAsync(caller, id, assignmentId, r, Today(regional, time), ct)));
        group.MapGet("/lines/{id:guid}/stock", (Guid id, [FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => lines.StockAsync(caller, id, ct)));
        ManufacturingPageRoutes.MapLineStockPage(group);
        group.MapGet("/lines/{id:guid}/employee-candidates", (Guid id, [FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => lines.EmployeeCandidatesAsync(caller, id, ct)));
        // What the line form offers: company scope only, like the form itself.
        group.MapGet("/line-locations", async ([FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
            await CallerAsync(http, db, ct) is MfgCaller caller && ManufacturingAccess.CanEditLines(caller)
                ? Results.Ok(await lines.LocationsAsync(ct))
                : Forbidden());
        group.MapGet("/line-user-candidates", async ([FromServices] ProductionLines lines, PosDbContext db, HttpContext http, CancellationToken ct) =>
            await CallerAsync(http, db, ct) is MfgCaller caller && ManufacturingAccess.CanEditLines(caller)
                ? Results.Ok(await lines.UserCandidatesAsync(ct))
                : Forbidden());

        // ---------- bills of materials ----------
        group.MapGet("/boms", async (Guid? productId, Guid? lineId, string? status, [FromServices] BillsOfMaterials boms,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            await CallerAsync(http, db, ct) is MfgCaller caller
                ? Results.Ok(await boms.ListAsync(caller, productId, lineId, status, ct))
                : NoCaller());
        ManufacturingPageRoutes.MapBomsPage(group);
        group.MapGet("/boms/{id:guid}", (Guid id, [FromServices] BillsOfMaterials boms, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => boms.GetAsync(caller, id, ct)));
        group.MapPost("/boms", (UpsertBomRequest r, [FromServices] BillsOfMaterials boms, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => boms.CreateAsync(caller, r, ct), created: b => $"/api/manufacturing/boms/{b.Bom.Id}"));
        group.MapPut("/boms/{id:guid}", (Guid id, UpsertBomRequest r, [FromServices] BillsOfMaterials boms, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => boms.UpdateAsync(caller, id, r, ct)));
        group.MapDelete("/boms/{id:guid}", async (Guid id, [FromServices] BillsOfMaterials boms, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
            var result = await boms.DeleteDraftAsync(caller, id, ct);
            return result.Ok ? Results.NoContent() : Refusal(result);
        });
        group.MapPost("/boms/{id:guid}/activate", (Guid id, [FromServices] BillsOfMaterials boms, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => boms.ActivateAsync(caller, id, ct)));
        group.MapPost("/boms/{id:guid}/retire", (Guid id, [FromServices] BillsOfMaterials boms, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => boms.RetireAsync(caller, id, ct)));
        group.MapPost("/boms/{id:guid}/new-version", (Guid id, [FromServices] BillsOfMaterials boms, PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => boms.NewVersionAsync(caller, id, ct), created: b => $"/api/manufacturing/boms/{b.Bom.Id}"));
        group.MapGet("/boms/{id:guid}/requirements", (Guid id, decimal quantity, Guid? unitId, [FromServices] BillsOfMaterials boms,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => boms.RequirementsAsync(caller, id, quantity, unitId, ct)));

        // ---------- access: who holds the manufacturing permissions ----------
        // Within the caller's own authority and reach (ManufacturingGrants): a key they hold, a user they reach.
        group.MapGet("/access", async ([FromServices] ManufacturingGrants grants, PosDbContext db, HttpContext http, CancellationToken ct) =>
            await CallerAsync(http, db, ct) is MfgCaller caller ? Results.Ok(await grants.ListAsync(caller, ct)) : NoCaller());
        group.MapPut("/access/{userId:guid}", (Guid userId, SetManufacturingAccessRequest r, [FromServices] ManufacturingGrants grants,
                PosDbContext db, HttpContext http, CancellationToken ct) =>
            As(http, db, ct, caller => grants.SetAsync(caller, userId, r, ct)));
    }

    /// <summary>
    /// The caller as manufacturing reads them: the token's user, company scope or the branch they work at,
    /// and the permissions the server resolved. Null without a user id.
    /// </summary>
    internal static async Task<MfgCaller?> CallerAsync(HttpContext http, PosDbContext db, CancellationToken ct)
    {
        var (userId, name) = MovementAuthor.Of(http.User);
        if (userId is not Guid id) return null;
        var keys = ServerAuthorization.Read(http.User)?.Permissions ?? new HashSet<string>();
        var company = BranchAssignment.IsCompanyScoped(http.User);
        var branch = company ? null : await BranchAssignment.ForCallerAsync(http.User, db, ct);
        return new MfgCaller(id, name ?? "", company, branch, keys);
    }

    /// <summary>The company's day today, in its own time zone — never the host's.</summary>
    private static DateOnly Today(IRegionalSettingsProvider regional, TimeProvider time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), StoreDocumentHistory.Zone(regional)).DateTime);

    internal static async Task<IResult> As<T>(HttpContext http, PosDbContext db, CancellationToken ct,
        Func<MfgCaller, Task<MfgResult<T>>> act, Func<T, string>? created = null)
    {
        if (await CallerAsync(http, db, ct) is not MfgCaller caller) return NoCaller();
        return Answer(await act(caller), created);
    }

    internal static IResult Answer<T>(MfgResult<T> result, Func<T, string>? created = null) =>
        !result.Ok ? Refusal(result)
        : created is not null ? Results.Created(created(result.Value!), result.Value)
        : Results.Ok(result.Value);

    private static IResult Refusal<T>(MfgResult<T> result) =>
        Results.Problem(detail: result.Error, statusCode: result.Status,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code });

    internal static IResult NoCaller() =>
        Results.Problem(detail: "The session names no user.", statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?> { ["code"] = "no_user" });

    private static IResult Forbidden() =>
        Results.Problem(detail: "This needs company-wide scope and the right to run production lines.", statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["code"] = "forbidden" });
}
