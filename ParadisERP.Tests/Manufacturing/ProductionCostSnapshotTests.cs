using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using static ParadisERP.Tests.Manufacturing.MfgHarness;
using static ParadisERP.Tests.Manufacturing.ProductionWorld;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>B1: an operational completion and its immutable cost freeze are separate actions.</summary>
public sealed class ProductionCostSnapshotTests
{
    private const string Snapshot = "/api/manufacturing/orders/{id:guid}/cost-snapshot";

    private static async Task<Guid> CompletedOrderAsync(PosDbContext db, ProductionWorld world)
    {
        var order = await world.CreateOrderAsync(db, world.LineA, 100m);
        var id = OrderId(order);
        var row = await db.ProductionOrders.SingleAsync(x => x.Id == id);
        row.Status = ProductionOrderStatus.Completed;
        row.AcceptedBaseQty = 100m;
        row.CompletedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Creation_is_idempotent_and_the_one_order_identity_prevents_a_second_freeze()
    {
        await using var s = await NewAsync();
        var world = await SetupAsync(s.Db);
        var id = await CompletedOrderAsync(s.Db, world);
        var first = await OkAsync(s.Db, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-freeze-1\"}", Of(id));
        var retry = await OkAsync(s.Db, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-freeze-1\"}", Of(id));
        Assert.Equal(first.GetProperty("id").GetGuid(), retry.GetProperty("id").GetGuid());
        await RefusedAsync(s.Db, world.AdminPrincipal, 409, "snapshot_exists", "POST", Snapshot, "{\"requestId\":\"b1-freeze-2\"}", Of(id));
        Assert.Equal(1, await s.Db.ManufacturingCostSheets.CountAsync(x => x.OrderId == id));
        Assert.Equal("provisional", first.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Snapshot_is_unchanged_when_the_BOM_and_product_reference_cost_change_afterward()
    {
        await using var s = await NewAsync();
        var world = await SetupAsync(s.Db);
        var id = await CompletedOrderAsync(s.Db, world);
        var created = await OkAsync(s.Db, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-freeze-immutable\"}", Of(id));
        var original = created.GetProperty("totalCost").GetDecimal();
        var order = await s.Db.ProductionOrders.SingleAsync(x => x.Id == id);
        var product = await s.Db.Products.SingleAsync(x => x.Id == order.ProductId);
        product.CostPrice += 999m;
        // The order copied its BOM at creation; changing the source BOM after its freeze cannot revise it.
        var bom = await s.Db.BillsOfMaterials.SingleAsync(x => x.Id == order.BomId);
        bom.Notes = "Changed after snapshot";
        await s.Db.SaveChangesAsync();
        var frozen = await OkAsync(s.Db, world.AdminPrincipal, "GET", Snapshot, "", Of(id));
        Assert.Equal(original, frozen.GetProperty("totalCost").GetDecimal());
        Assert.Equal(ManufacturingCostSnapshotKind.Created.ToString().ToLowerInvariant(), frozen.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Snapshot_captures_a_hashed_reconstructable_source_set_and_the_persistence_boundary_refuses_rewrites()
    {
        await using var s = await NewAsync();
        var world = await SetupAsync(s.Db);
        var id = await CompletedOrderAsync(s.Db, world);
        var created = await OkAsync(s.Db, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-source-capture\"}", Of(id));
        var sheet = await s.Db.ManufacturingCostSheets.SingleAsync(x => x.OrderId == id);
        var source = sheet.SourceSnapshotJson;

        Assert.Contains("IssuedBaseQty", source);
        Assert.Contains("Bookings", source);
        Assert.Contains("Expenses", source);
        Assert.Contains("MaterialMovements", source);
        Assert.Contains("MaterialConsumptions", source);
        Assert.Contains("LabourBookings", source);
        Assert.Contains("OutputReleases", source);
        Assert.Contains("ReleaseAllocations", source);
        Assert.Equal(sheet.SourceHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        Assert.Equal(sheet.SourceHash, created.GetProperty("sourceHash").GetString());
        Assert.Equal("complete", created.GetProperty("sourceState").GetString());

        sheet.TotalCost += 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
        s.Db.ChangeTracker.Clear();

        var frozen = await s.Db.ManufacturingCostSheets.AsNoTracking().SingleAsync(x => x.OrderId == id);
        Assert.Equal(source, frozen.SourceSnapshotJson);
        Assert.Equal(sheet.SourceHash, frozen.SourceHash);

        s.Db.Remove(await s.Db.ManufacturingCostSheets.SingleAsync(x => x.Id == frozen.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Snapshot_creation_has_its_own_permission_and_does_not_grant_or_need_payroll_access()
    {
        await using var s = await NewAsync();
        var world = await SetupAsync(s.Db);
        var id = await CompletedOrderAsync(s.Db, world);
        var withoutCreate = AllGrants.Where(x => x != PermissionCatalog.ManufacturingCostSnapshotCreate).ToArray();
        var (_, productionManager) = await ManagerAsync(s.Db, WarehouseA, withoutCreate);

        await RefusedAsync(s.Db, productionManager, 403, "forbidden", "POST", Snapshot, "{\"requestId\":\"b1-no-payroll\"}", Of(id));
        var (_, snapshotWriter) = await ManagerAsync(s.Db, WarehouseA, AllGrants);
        var created = await OkAsync(s.Db, snapshotWriter, "POST", Snapshot, "{\"requestId\":\"b1-without-payroll\"}", Of(id));
        Assert.Equal("provisional", created.GetProperty("status").GetString());
        await OkAsync(s.Db, productionManager, "GET", Snapshot, "", Of(id));
    }

    [Fact]
    public async Task Foreign_order_and_request_id_are_indistinguishable_from_a_missing_order_on_every_snapshot_path()
    {
        await using var s = await NewAsync();
        var world = await SetupAsync(s.Db);
        var foreign = await CompletedOrderAsync(s.Db, world);
        await OkAsync(s.Db, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-foreign-known\"}", Of(foreign));
        var (_, otherLine) = await ManagerAsync(s.Db, WarehouseB, AllGrants);
        var missing = Guid.NewGuid();

        await RefusedAsync(s.Db, otherLine, 404, "not_found", "GET", Snapshot, "", Of(foreign));
        await RefusedAsync(s.Db, otherLine, 404, "not_found", "POST", Snapshot, "{\"requestId\":\"b1-foreign-known\"}", Of(foreign));
        await RefusedAsync(s.Db, otherLine, 404, "not_found", "POST", Snapshot, "{\"requestId\":\"b1-foreign-known\"}", Of(missing));
        await RefusedAsync(s.Db, otherLine, 404, "not_found", "POST", "/api/manufacturing/orders/{id:guid}/cost-snapshot/late-costs",
            "{\"kind\":\"expense\",\"sourceReference\":\"PV-foreign\",\"amount\":1,\"reason\":\"guess\"}", Of(foreign));

        // Company-scoped branch access is not enough: a named manager of another line remains unable
        // to probe this line's sheet or its request-id path.
        var (lineBUser, otherProductionLine) = await ManagerAsync(s.Db, null, AllGrants);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = world.LineB, UserId = lineBUser.Id, Role = ProductionLineMemberRole.Manager });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        await RefusedAsync(s.Db, otherProductionLine, 404, "not_found", "GET", Snapshot, "", Of(foreign));
        await RefusedAsync(s.Db, otherProductionLine, 404, "not_found", "POST", Snapshot, "{\"requestId\":\"b1-foreign-known\"}", Of(foreign));
    }

    [Fact]
    public async Task Completing_an_order_never_creates_a_snapshot_until_the_explicit_B1_request()
    {
        await using var s = await NewAsync();
        var world = await SetupAsync(s.Db);
        var id = await CompletedOrderAsync(s.Db, world);

        Assert.Equal(0, await s.Db.ManufacturingCostSheets.CountAsync(x => x.OrderId == id));
        await OkAsync(s.Db, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-explicit\"}", Of(id));
        Assert.Equal(1, await s.Db.ManufacturingCostSheets.CountAsync(x => x.OrderId == id));
    }

    [Fact]
    public async Task Competing_snapshot_requests_return_the_one_committed_provisional_sheet()
    {
        await using var seed = await NewAsync();
        var world = await SetupAsync(seed.Db);
        var id = await CompletedOrderAsync(seed.Db, world);
        var database = Path.Combine(Path.GetTempPath(), $"algeriapos-b1-{Guid.NewGuid():N}.db");
        try
        {
            await using (var copy = new SqliteConnection($"Data Source={database};Cache=Shared"))
            {
                await copy.OpenAsync();
                seed.Connection.BackupDatabase(copy);
            }
            var options = new DbContextOptionsBuilder<PosDbContext>().UseSqlite($"Data Source={database};Cache=Shared").Options;
            await using var first = new PosDbContext(options);
            await using var second = new PosDbContext(options);
            await first.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
            await second.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");

            var replies = await Task.WhenAll(
                OkAsync(first, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-race\"}", Of(id)),
                OkAsync(second, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-race\"}", Of(id)));

            Assert.Equal(replies[0].GetProperty("id").GetGuid(), replies[1].GetProperty("id").GetGuid());
            await using var inspection = new PosDbContext(options);
            Assert.Equal(1, await inspection.ManufacturingCostSheets.CountAsync(x => x.OrderId == id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(database)) File.Delete(database);
        }
    }

    [Fact]
    public async Task Concurrent_different_request_ids_leave_exactly_one_sheet_and_do_not_turn_a_race_into_a_replay()
    {
        await using var seed = await NewAsync();
        var world = await SetupAsync(seed.Db);
        var id = await CompletedOrderAsync(seed.Db, world);
        var database = Path.Combine(Path.GetTempPath(), $"algeriapos-b1-different-{Guid.NewGuid():N}.db");
        try
        {
            await using (var copy = new SqliteConnection($"Data Source={database};Cache=Shared")) { await copy.OpenAsync(); seed.Connection.BackupDatabase(copy); }
            var options = new DbContextOptionsBuilder<PosDbContext>().UseSqlite($"Data Source={database};Cache=Shared").Options;
            await using var first = new PosDbContext(options);
            await using var second = new PosDbContext(options);
            await first.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
            await second.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
            var replies = await Task.WhenAll(
                CallAsync(first, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-race-a\"}", Of(id)),
                CallAsync(second, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-race-b\"}", Of(id)));
            Assert.Contains(replies, x => x.Status is >= 200 and < 300);
            Assert.Contains(replies, x => x.Status == 409 && x.Body.Contains("snapshot_exists", StringComparison.Ordinal));
            await using var inspection = new PosDbContext(options);
            Assert.Equal(1, await inspection.ManufacturingCostSheets.CountAsync(x => x.OrderId == id));
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(database)) File.Delete(database); }
    }

    [Fact]
    public async Task Late_costs_are_append_only_and_deduplicated_by_kind_and_source()
    {
        await using var s = await NewAsync();
        var world = await SetupAsync(s.Db);
        var id = await CompletedOrderAsync(s.Db, world);
        var sheet = await OkAsync(s.Db, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-late\"}", Of(id));
        var original = sheet.GetProperty("totalCost").GetDecimal();
        const string late = "/api/manufacturing/orders/{id:guid}/cost-snapshot/late-costs";
        var one = await OkAsync(s.Db, world.AdminPrincipal, "POST", late,
            "{\"kind\":\"expense\",\"sourceReference\":\"PV-701\",\"amount\":125.5,\"reason\":\"Late approved freight\"}", Of(id));
        var retry = await OkAsync(s.Db, world.AdminPrincipal, "POST", late,
            "{\"kind\":\"expense\",\"sourceReference\":\"PV-701\",\"amount\":125.5,\"reason\":\"Late approved freight\"}", Of(id));
        Assert.Equal(one.GetProperty("id").GetGuid(), retry.GetProperty("id").GetGuid());
        var frozen = await OkAsync(s.Db, world.AdminPrincipal, "GET", Snapshot, "", Of(id));
        Assert.Equal(original, frozen.GetProperty("totalCost").GetDecimal());
        Assert.Single(frozen.GetProperty("lateCosts").EnumerateArray());
    }

    [Fact]
    public async Task Concurrent_late_cost_requests_return_one_immutable_late_record()
    {
        await using var seed = await NewAsync();
        var world = await SetupAsync(seed.Db);
        var id = await CompletedOrderAsync(seed.Db, world);
        await OkAsync(seed.Db, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-late-race-sheet\"}", Of(id));
        var database = Path.Combine(Path.GetTempPath(), $"algeriapos-b1-late-{Guid.NewGuid():N}.db");
        const string late = "/api/manufacturing/orders/{id:guid}/cost-snapshot/late-costs";
        try
        {
            await using (var copy = new SqliteConnection($"Data Source={database};Cache=Shared")) { await copy.OpenAsync(); seed.Connection.BackupDatabase(copy); }
            var options = new DbContextOptionsBuilder<PosDbContext>().UseSqlite($"Data Source={database};Cache=Shared").Options;
            await using var first = new PosDbContext(options);
            await using var second = new PosDbContext(options);
            await first.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
            await second.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
            const string payload = "{\"kind\":\"expense\",\"sourceReference\":\"PV-race\",\"amount\":25,\"reason\":\"late\"}";
            var replies = await Task.WhenAll(
                OkAsync(first, world.AdminPrincipal, "POST", late, payload, Of(id)),
                OkAsync(second, world.AdminPrincipal, "POST", late, payload, Of(id)));
            Assert.Equal(replies[0].GetProperty("id").GetGuid(), replies[1].GetProperty("id").GetGuid());
            await using var inspection = new PosDbContext(options);
            Assert.Equal(1, await inspection.ManufacturingLateCosts.CountAsync());
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(database)) File.Delete(database); }
    }

    [Fact]
    public async Task Reconstruction_is_explicit_and_a_caller_outside_the_line_cannot_read_it()
    {
        await using var s = await NewAsync();
        var world = await SetupAsync(s.Db);
        var id = await CompletedOrderAsync(s.Db, world);
        var reconstructed = await OkAsync(s.Db, world.AdminPrincipal, "POST", Snapshot, "{\"requestId\":\"b1-reconstruct\",\"reconstruct\":true}", Of(id));
        Assert.True(reconstructed.GetProperty("reconstructed").GetBoolean());
        Assert.Equal("reconstructed", reconstructed.GetProperty("kind").GetString());
        var (_, other) = await ManagerAsync(s.Db, WarehouseB, AllGrants);
        await RefusedAsync(s.Db, other, 404, "not_found", "GET", Snapshot, "", Of(id));
    }
}
