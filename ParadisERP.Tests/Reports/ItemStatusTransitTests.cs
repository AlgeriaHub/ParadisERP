using System.Security.Claims;
using System.Text.Json;
using ParadisERP.API.Endpoints;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Reports;

/// <summary>
/// The Item Movement &amp; Status report with transfers in transit (owner, 2026-10-12): total =
/// available + on rent + maintenance + scrap + in transit, each unit counted once, at the location
/// that dispatched it until it is received.
/// </summary>
public class ItemStatusTransitTests
{
    private static readonly Guid Algiers = TillHarness.Algiers;
    private static readonly Guid Oran = TillHarness.Oran;
    private static readonly Guid Pipe = TillHarness.Pipe;

    /// <summary>Algiers dispatches 4 to Oran and Oran receives 1: 3 are in transit, counted at Algiers.</summary>
    private static async Task<(SqliteHandle Handle, ClaimsPrincipal Admin, Guid TransferId)> InTransitAsync(int receive = 1)
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, Algiers);
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapStockTransferEndpoints(), "POST",
            "/api/catalog/stock/transfers", admin,
            $$"""{"fromBranchId":"{{Algiers}}","toBranchId":"{{Oran}}","note":"truck","lines":[{"productId":"{{Pipe}}","qty":4}]}""");
        Assert.True(status == 201, body);
        var t = JsonDocument.Parse(body).RootElement;
        var id = t.GetProperty("id").GetGuid();
        if (receive > 0)
        {
            var line = t.GetProperty("lines")[0].GetProperty("id").GetGuid();
            (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapStockTransferEndpoints(), "POST",
                "/api/catalog/stock/transfers/{id:guid}/receive", admin,
                $$"""{"lines":[{"lineId":"{{line}}","qty":{{receive}}}],"note":null}""", ("id", id.ToString()));
            Assert.True(status == 200, body);
        }
        db.ChangeTracker.Clear();
        return (new SqliteHandle(connection, db), admin, id);
    }

    private sealed record SqliteHandle(Microsoft.Data.Sqlite.SqliteConnection Connection, PosDbContext Db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    private static async Task<JsonElement> ReadAsync(PosDbContext db, ClaimsPrincipal caller, string pattern, string query, params (string, string)[] route)
    {
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET", pattern, caller, query, route);
        Assert.True(status == 200, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement PipeRow(JsonElement report) =>
        report.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == Pipe);

    private static decimal D(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    [Fact]
    public async Task The_company_total_carries_what_is_in_transit_once()
    {
        var (h, admin, _) = await InTransitAsync();
        await using var _ = h;

        var row = PipeRow(await ReadAsync(h.Db, admin, "/api/reports/inventory/items", ""));

        Assert.Equal(3m, D(row, "inTransit"));
        // Algiers 6 + Oran 11 on the rows, 3 on the road: the 20 the company had.
        Assert.Equal(17m, D(row, "available"));
        Assert.Equal(20m, D(row, "total"));
        Assert.Equal(D(row, "warehouses") + D(row, "branches") + D(row, "rented") + D(row, "maintenance") + D(row, "scrap") + D(row, "inTransit"),
            D(row, "total"));
    }

    [Fact]
    public async Task In_transit_counts_at_the_source_and_is_shown_but_not_counted_at_the_destination()
    {
        var (h, admin, transferId) = await InTransitAsync();
        await using var _ = h;

        var source = PipeRow(await ReadAsync(h.Db, admin, "/api/reports/inventory/items", $"?branchId={Algiers}"));
        Assert.Equal(3m, D(source, "inTransit"));
        Assert.Equal(9m, D(source, "total"));

        var destination = PipeRow(await ReadAsync(h.Db, admin, "/api/reports/inventory/items", $"?branchId={Oran}"));
        Assert.Equal(0m, D(destination, "inTransit"));
        Assert.Equal(11m, D(destination, "total"));

        var detail = await ReadAsync(h.Db, admin, "/api/reports/inventory/items/{productId:guid}", $"?branchId={Oran}", ("productId", Pipe.ToString()));
        var incoming = Assert.Single(detail.GetProperty("inTransit").EnumerateArray());
        Assert.Equal(transferId, incoming.GetProperty("transferId").GetGuid());
        Assert.Equal("TRF-000001", incoming.GetProperty("number").GetString());
        Assert.Equal("PartlyReceived", incoming.GetProperty("status").GetString());
        Assert.Equal(3m, D(incoming, "qty"));
        Assert.False(incoming.GetProperty("countedHere").GetBoolean());

        var atSource = await ReadAsync(h.Db, admin, "/api/reports/inventory/items/{productId:guid}", $"?branchId={Algiers}", ("productId", Pipe.ToString()));
        Assert.True(Assert.Single(atSource.GetProperty("inTransit").EnumerateArray()).GetProperty("countedHere").GetBoolean());
        var location = Assert.Single(atSource.GetProperty("locations").EnumerateArray());
        Assert.Equal(3m, D(location, "inTransit"));
        Assert.Equal(9m, D(location, "total"));
    }

    [Fact]
    public async Task The_history_names_the_transfer_steps_and_an_open_transfer_adds_no_unrecorded_difference()
    {
        var (h, admin, _) = await InTransitAsync();
        await using var _ = h;

        var detail = await ReadAsync(h.Db, admin, "/api/reports/inventory/items/{productId:guid}", $"?branchId={Algiers}", ("productId", Pipe.ToString()));
        var movements = detail.GetProperty("movements");
        var dispatch = movements.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("type").GetString() == "transfer-out");
        Assert.Equal("truck", dispatch.GetProperty("detail").GetString());
        Assert.Equal(Algiers, dispatch.GetProperty("fromBranchId").GetGuid());
        Assert.Equal(Oran, dispatch.GetProperty("toBranchId").GetGuid());
        // The harness seeds Algiers with 10 and no movement: that is the only gap. The 3 on the road are not one.
        Assert.Equal(10m, movements.GetProperty("unrecordedDifference").GetDecimal());
    }

    [Fact]
    public async Task A_received_or_cancelled_transfer_adds_nothing()
    {
        var (h, admin, _) = await InTransitAsync(receive: 4);
        await using var _ = h;
        var row = PipeRow(await ReadAsync(h.Db, admin, "/api/reports/inventory/items", ""));
        Assert.Equal(0m, D(row, "inTransit"));
        Assert.Equal(20m, D(row, "total"));

        var (h2, admin2, id) = await InTransitAsync(receive: 0);
        await using var __ = h2;
        var (status, body) = await BranchLockTests.InvokeAsync(h2.Db, app => app.MapStockTransferEndpoints(), "POST",
            "/api/catalog/stock/transfers/{id:guid}/cancel", admin2, "{}", ("id", id.ToString()));
        Assert.True(status == 200, body);
        h2.Db.ChangeTracker.Clear();
        row = PipeRow(await ReadAsync(h2.Db, admin2, "/api/reports/inventory/items", ""));
        Assert.Equal(0m, D(row, "inTransit"));
        Assert.Equal(20m, D(row, "total"));
    }

    [Fact]
    public async Task A_reported_shortfall_counts_in_transit_until_the_source_confirms_it()
    {
        var (h, admin, id) = await InTransitAsync(receive: 0);
        await using var _ = h;
        var (_, oran) = await BranchLockTests.UserAsync(h.Db, UserRole.Manager, Oran);
        var line = (await h.Db.StockTransferLines.AsNoTracking().SingleAsync(l => l.TransferId == id)).Id;

        // Oran receives 3 and reports the fourth short (owner, 2026-10-13): it is still on the road.
        var (status, body) = await BranchLockTests.InvokeAsync(h.Db, app => app.MapStockTransferEndpoints(), "POST",
            "/api/catalog/stock/transfers/{id:guid}/receive", oran,
            $$"""{"lines":[{"lineId":"{{line}}","qty":3,"short":1}],"note":null,"shortReason":"carton missing"}""", ("id", id.ToString()));
        Assert.True(status == 200, body);
        h.Db.ChangeTracker.Clear();

        var row = PipeRow(await ReadAsync(h.Db, admin, "/api/reports/inventory/items", ""));
        Assert.Equal(1m, D(row, "inTransit"));
        Assert.Equal(20m, D(row, "total"));
        Assert.Single((await ReadAsync(h.Db, admin, "/api/reports/inventory/items", "?status=transit")).GetProperty("rows").EnumerateArray());
        var detail = await ReadAsync(h.Db, admin, "/api/reports/inventory/items/{productId:guid}", $"?branchId={Algiers}", ("productId", Pipe.ToString()));
        Assert.Equal(1m, D(Assert.Single(detail.GetProperty("inTransit").EnumerateArray()), "qty"));

        // The source confirms it: written off, no longer in transit, and gone from the company's total.
        (status, body) = await BranchLockTests.InvokeAsync(h.Db, app => app.MapStockTransferEndpoints(), "POST",
            "/api/catalog/stock/transfers/{id:guid}/shortfall/confirm", admin,
            $$"""{"lines":[{"lineId":"{{line}}","qty":1}],"reason":null}""", ("id", id.ToString()));
        Assert.True(status == 200, body);
        h.Db.ChangeTracker.Clear();

        row = PipeRow(await ReadAsync(h.Db, admin, "/api/reports/inventory/items", ""));
        Assert.Equal(0m, D(row, "inTransit"));
        Assert.Equal(19m, D(row, "total"));
        Assert.Empty((await ReadAsync(h.Db, admin, "/api/reports/inventory/items", "?status=transit")).GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task The_transit_filter_keeps_the_articles_on_the_road()
    {
        var (h, admin, _) = await InTransitAsync();
        await using var _ = h;
        var report = await ReadAsync(h.Db, admin, "/api/reports/inventory/items", "?status=transit");
        Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal(3m, D(report.GetProperty("totals"), "inTransit"));
    }
}
