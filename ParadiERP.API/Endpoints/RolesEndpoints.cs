using ParadiERP.API.Subscriptions;
using ParadiERP.Application.Contracts;
using ParadiERP.Infrastructure.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// Administration › Roles &amp; permissions (AUTH-1 Step 5, REDESIGN_PLAN.md §17.8). Reading the roles and the
/// catalogue is open to whoever manages users or roles, so the users screen can offer every role; writing is
/// <c>roles.manage</c>'s (RoutePermissions). The rules are <see cref="RoleEditor"/>'s; a role edit reaches
/// every member's next request, with no sign-out.
/// </summary>
public static class RolesEndpoints
{
    public static void MapRolesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/permissions", () => Results.Ok(RoleEditor.Catalogue())).WithTags("Roles");

        var group = app.MapGroup("/api/roles").WithTags("Roles")
            .AddEndpointFilter<SubscriptionWriteFilter>();

        group.MapGet("/", async ([FromServices] RoleEditor roles, CancellationToken ct) => Results.Ok(await roles.ListAsync(ct)));
        PeoplePageRoutes.MapRolesPage(group);

        group.MapGet("/{id:guid}/members", async (Guid id, [FromServices] RoleEditor roles, CancellationToken ct) =>
            Answer(await roles.MembersAsync(id, ct)));

        group.MapPost("/", async (UpsertRoleRequest r, [FromServices] RoleEditor roles, HttpContext http, CancellationToken ct) =>
        {
            if (Caller(http) is not { } caller) return NoDecision();
            var result = await roles.CreateAsync(caller, r, ct);
            return result.Ok ? Results.Created($"/api/roles/{result.Value!.Id}", result.Value) : Refusal(result);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpsertRoleRequest r, [FromServices] RoleEditor roles, HttpContext http, CancellationToken ct) =>
            Caller(http) is { } caller ? Answer(await roles.UpdateAsync(caller, id, r, ct)) : NoDecision());

        group.MapDelete("/{id:guid}", async (Guid id, [FromServices] RoleEditor roles, CancellationToken ct) =>
        {
            var result = await roles.DeleteAsync(id, ct);
            return result.Ok ? Results.NoContent() : Refusal(result);
        });
    }

    /// <summary>The editor as the server decided them for this request — never as the token says.</summary>
    private static RoleEditorCaller? Caller(HttpContext http) =>
        ServerAuthorization.Read(http.User) is { } decided
            ? new RoleEditorCaller(decided.Permissions, decided.Scope, MovementAuthor.Of(http.User).UserName)
            : null;

    private static IResult NoDecision() => Results.Problem("Sign in again.", statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Answer<T>(RoleResult<T> result) => result.Ok ? Results.Ok(result.Value) : Refusal(result);

    private static IResult Refusal<T>(RoleResult<T> result) =>
        Results.Problem(result.Message, statusCode: result.Status,
            extensions: new Dictionary<string, object?> { ["code"] = result.Code });
}
