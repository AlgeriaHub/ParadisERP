using Microsoft.EntityFrameworkCore;
using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.API.Subscriptions;

namespace ParadiERP.API.Endpoints;

/// <summary>
/// Back-office till registration. AuthEndpoints refuses to log a device in unless it is
/// registered here first — auto-provisioning on login would let a deactivated till simply
/// pick a new code and keep selling.
/// </summary>
public static class DevicesEndpoints
{
    /// <summary>The refusal's stable code, read by the devices screen to say it in words.</summary>
    public const string WarehouseTillCode = "warehouse_till";
    public const string WarehouseTillMessage = "A warehouse does not sell: register this till on a selling branch.";

    public static void MapDevicesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").WithTags("Devices")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        // branchId is the caller's own branch for everyone but the Admin (CallersBranch).
        group.MapGet("/", async (Guid? branchId, PosDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Devices.AsNoTracking()
                .Where(d => branchId == null || d.BranchId == branchId)
                .OrderBy(d => d.Code)
                .Join(db.Branches.AsNoTracking(), d => d.BranchId, b => b.Id, (d, b) => new DeviceAdminDto(
                    d.Id, d.Code, d.Name, d.BranchId, b.NameFr, d.IsActive, d.LastSyncAtUtc, d.LastCursor, b.IsWarehouse))
                .ToListAsync(ct)));
        PeoplePageRoutes.MapDevicesPage(group);

        group.MapPost("/", async (UpsertDeviceRequest r, PosDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(r.Code)) return Results.BadRequest("Code is required.");
            var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == r.BranchId && !b.IsDeleted, ct);
            if (branch is null) return Results.BadRequest("Unknown branch.");
            if (branch.IsWarehouse) return WarehouseTill();

            var code = r.Code.Trim().ToUpperInvariant();
            if (await db.Devices.AnyAsync(d => d.Code == code, ct))
                return Results.Conflict($"Terminal code '{code}' already exists.");

            var entity = new Device { Code = code, Name = r.Name, BranchId = r.BranchId, IsActive = r.IsActive };
            db.Devices.Add(entity);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/devices/{entity.Id}", entity);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpsertDeviceRequest r, PosDbContext db, CancellationToken ct) =>
        {
            var entity = await db.Devices.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (entity is null) return Results.NotFound();
            var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == r.BranchId && !b.IsDeleted, ct);
            if (branch is null) return Results.BadRequest("Unknown branch.");
            // Moving a till onto a warehouse is refused. A till already there (one registered
            // before the rule) can still be renamed or switched off,
            // so it can be retired without first being moved.
            if (branch.IsWarehouse && entity.BranchId != branch.Id) return WarehouseTill();

            entity.Name = r.Name;
            entity.BranchId = r.BranchId;
            entity.IsActive = r.IsActive;
            await db.SaveChangesAsync(ct);
            return Results.Ok(entity);
        }).ForBranchOf(DocumentBranches.Device);
    }

    /// <summary>
    /// A warehouse holds the stock and sends it to the branches; it does not sell
    /// (the owner, 2026-10-05). Enforced here, where tills are set up — never at the moment of a
    /// sale, a sync push or the sign-in of a till that already exists, which must not be blocked.
    /// </summary>
    private static IResult WarehouseTill() =>
        Results.Problem(WarehouseTillMessage, statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = WarehouseTillCode });
}
