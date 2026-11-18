using ParadiERP.Application.Contracts;
using ParadiERP.Application.Subscriptions;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.API.Subscriptions;

/// <summary>Protects online administration. Reads and sales reconciliation remain available.</summary>
public sealed class SubscriptionWriteFilter(ISubscriptionProvider subscriptions, PosDbContext db) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method)) return await next(context);
        var access = subscriptions.GetAccess();
        if (!access.CanOperate)
            return Results.Problem("Subscription inactive. Contact your subscription provider to renew.",
                statusCode: StatusCodes.Status403Forbidden, title: "Subscription required",
                extensions: new Dictionary<string, object?> { ["code"] = "subscription_inactive" });

        // Serialize capacity checks with the actual write, across API processes as well as
        // requests. The shared counter is already the serialization point for catalog writes.
        await using var transaction = await db.BeginWriteAsync(context.HttpContext.RequestAborted);
        await db.NextVersionAsync(context.HttpContext.RequestAborted);
        var ct = context.HttpContext.RequestAborted;
        var id = context.Arguments.OfType<Guid>().FirstOrDefault();
        string? exceeded = null;
        if (context.Arguments.OfType<CreateUserRequest>().Any()
            || context.Arguments.OfType<UpdateUserRequest>().Any(x => x.IsActive)
                && !await db.Users.AnyAsync(x => x.Id == id && x.IsActive && !x.IsDeleted, ct))
        {
            if (access.MaxUsers is int limit && await db.Users.CountAsync(x => x.IsActive && !x.IsDeleted, ct) >= limit)
                exceeded = "users";
        }
        if (context.Arguments.OfType<UpsertBranchRequest>().Any(x => x.IsActive)
            && !await db.Branches.AnyAsync(x => x.Id == id && x.IsActive && !x.IsDeleted, ct))
        {
            if (access.MaxBranches is int limit && await db.Branches.CountAsync(x => x.IsActive && !x.IsDeleted, ct) >= limit)
                exceeded = "branches";
        }
        if (context.Arguments.OfType<UpsertDeviceRequest>().Any(x => x.IsActive)
            && !await db.Devices.AnyAsync(x => x.Id == id && x.IsActive, ct))
        {
            if (access.MaxDevices is int limit && await db.Devices.CountAsync(x => x.IsActive, ct) >= limit)
                exceeded = "devices";
        }
        if (exceeded is not null)
            return Results.Problem($"Your plan's active {exceeded} limit has been reached.",
                statusCode: StatusCodes.Status409Conflict, title: "Plan limit reached",
                extensions: new Dictionary<string, object?> { ["code"] = "subscription_limit", ["resource"] = exceeded });

        var result = await next(context);
        // A failed endpoint may have staged writes; never commit them.
        if (result is IStatusCodeHttpResult { StatusCode: >= 400 }) return result;
        await transaction.CommitAsync(ct);
        return result;
    }
}
