using AlgeriaPos.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace AlgeriaPos.API.Authorization;

/// <summary>A route needs at least one of these permissions, as the server resolved them for this request.</summary>
public sealed class PermissionRequirement(IReadOnlyList<string> anyOf) : IAuthorizationRequirement
{
    public IReadOnlyList<string> AnyOf { get; } = anyOf;
}

/// <summary>
/// Reads only the server's own identity (<see cref="ServerAuthorization"/>): a claim in the token that
/// looks like a permission counts for nothing, and a principal the server has not decided on holds nothing.
/// </summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var decided = ServerAuthorization.Read(context.User);
        if (decided is not null && requirement.AnyOf.Any(decided.Has)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>Marks an endpoint with the permissions <see cref="RoutePermissions"/> gave it, for inspection and tests.</summary>
public sealed class RoutePermissionAttribute(RouteRequirement requirement) : Attribute
{
    public RouteRequirement Requirement { get; } = requirement;
}

/// <summary>
/// Applies <see cref="RoutePermissions"/> to every endpoint mapped under the group it is put on —
/// Program puts it on the one group every endpoint is mapped under. Fails closed: a company route the
/// table does not name stops the start-up with the route in the message, and so does a route the table
/// calls open that is not marked anonymous.
/// </summary>
public static class RoutePermissionConvention
{
    public const string PlatformPrefix = "/api/platform";

    public static RouteGroupBuilder WithRoutePermissions(this RouteGroupBuilder group)
    {
        // Finally: after every convention of the endpoint itself (AllowAnonymous, its own policies).
        ((IEndpointConventionBuilder)group).Finally(Apply);
        return group;
    }

    public static string Pattern(RouteEndpointBuilder route)
    {
        var raw = route.RoutePattern.RawText ?? "";
        return "/" + raw.Trim('/');
    }

    private static void Apply(EndpointBuilder builder)
    {
        if (builder is not RouteEndpointBuilder route) return;
        var pattern = Pattern(route);
        if (pattern.StartsWith(PlatformPrefix, StringComparison.Ordinal)) return;

        var methods = builder.Metadata.OfType<HttpMethodMetadata>().LastOrDefault()?.HttpMethods ?? [];
        if (methods.Count == 0)
            throw new InvalidOperationException($"Route {pattern} names no HTTP method, so RoutePermissions cannot place it.");

        var requirements = methods.Select(m => RoutePermissions.ByRoute.TryGetValue($"{m} {pattern}", out var r)
                ? r
                : throw new InvalidOperationException(
                    $"Route '{m} {pattern}' is not in RoutePermissions. Decide who may call it (AlgeriaPos.API/Authorization/RoutePermissions.cs)."))
            .Distinct()
            .ToList();
        if (requirements.Count != 1)
            throw new InvalidOperationException($"Route {pattern} has a different requirement per method; map them separately.");
        var requirement = requirements[0];
        builder.Metadata.Add(new RoutePermissionAttribute(requirement));

        var anonymous = builder.Metadata.OfType<IAllowAnonymous>().Any();
        switch (requirement.Kind)
        {
            case "open":
                if (!anonymous)
                    throw new InvalidOperationException($"RoutePermissions calls {pattern} open, but the endpoint is not anonymous.");
                break;
            case "signed-in":
                if (anonymous)
                    throw new InvalidOperationException($"RoutePermissions requires a signed-in caller for {pattern}, but the endpoint allows anonymous.");
                // The endpoint names its own scheme where it needs one (the SSO cookie routes); otherwise the bearer default.
                if (!builder.Metadata.OfType<IAuthorizeData>().Any() && !builder.Metadata.OfType<AuthorizationPolicy>().Any())
                    builder.Metadata.Add(new AuthorizeAttribute());
                break;
            default:
                if (anonymous)
                    throw new InvalidOperationException($"RoutePermissions requires a permission for {pattern}, but the endpoint allows anonymous.");
                builder.Metadata.Add(new AuthorizeAttribute());
                builder.Metadata.Add(new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(requirement.AnyOf)).Build());
                break;
        }
    }
}
