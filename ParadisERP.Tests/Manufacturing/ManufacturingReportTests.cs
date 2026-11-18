using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Sync;
using ParadisERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Manufacturing.MfgHarness;
using static ParadisERP.Tests.Manufacturing.ProductionWorld;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// The manufacturing reports (Phase 2, part G): materials sold at POS, read from Phase 1's exception rows with
/// every filter the owner listed and branch scope; and the movements awaiting posting, at their snapshots.
/// </summary>
public class ManufacturingReportTests
{
    private const string Pos = "/api/manufacturing/reports/pos-exceptions";

    [Fact]
    public async Task Materials_sold_at_pos_filter_by_branch_product_period_sale_and_stale_catalogue_within_the_caller_s_branch()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        // Withdrawn from sale through the screen, so the history knows when.
        await OkAsync(s.Db, admin, "PUT", "/api/manufacturing/materials/{productId:guid}", """{"role":"RawMaterial","baseUnitId":null,"units":[]}""",
            ("productId", TillHarness.Pipe.ToString()));
        var sync = TillHarness.NewService(s.Db);
        var queued = TillHarness.Sale("T-OLD-1", 1) with { SoldAtUtc = DateTime.UtcNow.AddDays(-2) };
        var algiers = TillHarness.Sale("T-NEW-1", 2);
        var oran = TillHarness.Sale("T-ORAN-1", 1);
        Assert.Equal(2, (await sync.PushAsync(new PushRequest("C01", [queued, algiers]), "C01", CancellationToken.None)).Accepted);
        Assert.Equal(1, (await sync.PushAsync(new PushRequest("C02", [oran]), "C02", CancellationToken.None)).Accepted);
        // One more row with no withdrawal on file: a product made a raw material before the audit existed.
        s.Db.ManufacturingSaleExceptions.Add(new ManufacturingSaleException
        {
            SaleId = algiers.Id, SaleLineId = Guid.NewGuid(), ProductId = TillHarness.Pipe, Sku = "PIPE-90", BranchId = TillHarness.Algiers,
            DeviceCode = "C01", Reference = "T-LEGACY", Quantity = 2, SoldAtUtc = DateTime.UtcNow.AddDays(-40), ReceivedAtUtc = DateTime.UtcNow.AddDays(-40),
            Role = ManufacturingRole.RawMaterial,
        });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var all = await OkAsync(s.Db, admin, "GET", Pos);
        Assert.Equal((4, 5m, 1, 2, 1), (all.GetProperty("total").GetInt32(), all.GetProperty("totalQuantity").GetDecimal(),
            all.GetProperty("staleCount").GetInt32(), all.GetProperty("afterWithdrawalCount").GetInt32(), all.GetProperty("unknownCount").GetInt32()));
        var first = all.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("reference").GetString() == "T-OLD-1");
        Assert.Equal(("PIPE-90", "stale", "C01", 1m), (first.GetProperty("sku").GetString(), first.GetProperty("stale").GetString(),
            first.GetProperty("deviceCode").GetString(), first.GetProperty("quantity").GetDecimal()));

        int Count(System.Text.Json.JsonElement report) => report.GetProperty("total").GetInt32();
        Assert.Equal(1, Count(await OkAsync(s.Db, admin, "GET", Pos, $"?branchId={TillHarness.Oran}")));
        Assert.Equal(1, Count(await OkAsync(s.Db, admin, "GET", Pos, "?sale=ORAN")));
        Assert.Equal(1, Count(await OkAsync(s.Db, admin, "GET", Pos, "?stale=stale")));
        Assert.Equal(2, Count(await OkAsync(s.Db, admin, "GET", Pos, "?stale=after_withdrawal")));
        Assert.Equal(1, Count(await OkAsync(s.Db, admin, "GET", Pos, "?stale=unknown")));
        Assert.Equal(4, Count(await OkAsync(s.Db, admin, "GET", Pos, $"?productId={TillHarness.Pipe}")));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(1, Count(await OkAsync(s.Db, admin, "GET", Pos, $"?from={today.AddDays(-35):yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}")));
        await RefusedAsync(s.Db, admin, 400, "invalid_filter", "GET", Pos, "?stale=maybe");

        // A branch manager of Oran sees Oran's, whatever branch they ask for.
        var (_, oranManager) = await ManagerAsync(s.Db, TillHarness.Oran, PermissionCatalog.ManufacturingReportsView);
        var own = await OkAsync(s.Db, oranManager, "GET", Pos, $"?branchId={TillHarness.Algiers}");
        Assert.Equal(["T-ORAN-1"], own.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("reference").GetString()));
    }

    [Fact]
    public async Task Movements_awaiting_posting_show_work_in_progress_at_the_snapshots_per_line()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var id = OrderId(order);
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", MaterialId(order), 100), Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Return", MaterialId(order), 10), Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Scrap", MaterialId(order), 5), Of(id));

        var pending = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/pending-movements");
        Assert.Equal((15000m, 1500m, 13500m, 0m), (pending.GetProperty("issuedValue").GetDecimal(), pending.GetProperty("returnedValue").GetDecimal(),
            pending.GetProperty("workInProgressValue").GetDecimal(), pending.GetProperty("releasedValue").GetDecimal()));
        // Scrap moved no stock, so it is not a pending stock movement.
        Assert.Equal(["MI-000001", "MR-000001"], pending.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("number").GetString()));
        Assert.Empty((await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/reports/pending-movements", $"?lineId={w.LineB}"))
            .GetProperty("rows").EnumerateArray());
    }
}
