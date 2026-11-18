using ParadiERP.Application.Subscriptions;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.API.Endpoints;

public static class SubscriptionEndpoints
{
    public static void MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/subscription", (ISubscriptionProvider provider) => Results.Ok(provider.GetAccess()))
            .RequireAuthorization().WithTags("Subscription");

        app.MapGet("/api/subscription/usage", async (PosDbContext db, CancellationToken ct) => Results.Ok(new
        {
            users = await db.Users.CountAsync(x => x.IsActive && !x.IsDeleted, ct),
            branches = await db.Branches.CountAsync(x => x.IsActive && !x.IsDeleted, ct),
            devices = await db.Devices.CountAsync(x => x.IsActive, ct)
        })).WithTags("Subscription");
    }
}
