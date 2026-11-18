using System.Security.Claims;
using System.Text.Json;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Sync;
using ParadiERP.Tests.Manufacturing;
using ParadiERP.Tests.Security;
using static ParadiERP.Tests.Manufacturing.MfgHarness;
using static ParadiERP.Tests.Manufacturing.ProductionWorld;

namespace ParadiERP.Tests.Paging;

/// <summary>
/// "I need to paginate in backend, not frontend" (owner, 2026-11-17): every manufacturing list is asked a page
/// at a time — ten rows to begin with, never more than a hundred — beside its unchanged list route. The filters
/// the screens applied in the browser run on the server over every row, with the same meaning, and the figures
/// over a list (its tiles, the choices a filter offers) are the ones the old screen showed.
/// </summary>
public class ManufacturingPageTests
{
    private const string Mfg = "/api/manufacturing";

    private static Task<JsonElement> PageAsync(PosDbContext db, ClaimsPrincipal who, string route, string query = "",
        params (string Key, string Value)[] values) =>
        OkAsync(db, who, "GET", route, query, values);

    private static string[] Column(JsonElement page, string field) =>
        page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty(field).GetString()!).ToArray();

    private static int Total(JsonElement page) => page.GetProperty("total").GetInt32();

    private static JsonElement Summary(JsonElement page) => page.GetProperty("summary");

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!).ToArray();

    // ---------- configuration ----------

    [Fact]
    public async Task Job_roles_come_ten_at_a_time_and_filter_on_their_state_over_every_role()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        for (var i = 1; i <= 25; i++)
            s.Db.ManufacturingJobRoles.Add(new ManufacturingJobRole { Code = $"R{i:00}", NameAr = $"R{i:00}", NameFr = $"R{i:00}", NameEn = $"R{i:00}", IsActive = i is not (3 or 7) });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        const string route = Mfg + "/job-roles/page";

        var first = await PageAsync(s.Db, admin, route);
        Assert.Equal((25, 10, 0), (Total(first), first.GetProperty("take").GetInt32(), first.GetProperty("skip").GetInt32()));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"R{i:00}"), Column(first, "code"));
        Assert.Equal(["R21", "R22", "R23", "R24", "R25"], Column(await PageAsync(s.Db, admin, route, "?skip=20&take=10"), "code"));

        Assert.Equal(["R03", "R07"], Column(await PageAsync(s.Db, admin, route, "?state=inactive"), "code"));
        Assert.Equal(23, Total(await PageAsync(s.Db, admin, route, "?state=active")));

        // A page past the end, after a filter shrank the list, is answered with the last page.
        var past = await PageAsync(s.Db, admin, route, "?skip=90&take=20");
        Assert.Equal((20, 5), (past.GetProperty("skip").GetInt32(), past.GetProperty("items").GetArrayLength()));
        Assert.Equal(100, (await PageAsync(s.Db, admin, route, "?take=5000")).GetProperty("take").GetInt32());
    }

    [Fact]
    public async Task Units_filter_on_what_they_measure_and_their_state_in_the_units_own_order()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        // The seven system units, and twelve of the company's own with no standard.
        for (var i = 1; i <= 12; i++)
            s.Db.UnitsOfMeasure.Add(new UnitOfMeasure { Code = $"O{i:00}", NameAr = $"O{i:00}", NameFr = $"O{i:00}", NameEn = $"O{i:00}", Dimension = UnitDimension.Other, IsActive = i != 5 });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        const string route = Mfg + "/units/page";

        var first = await PageAsync(s.Db, admin, route);
        Assert.Equal(19, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        Assert.Equal("pcs", Column(first, "code")[0]);
        // Within a dimension, from the smallest to the largest.
        Assert.Equal(["g", "kg"], Column(await PageAsync(s.Db, admin, route, "?dimension=Mass"), "code"));
        Assert.Equal(["O05"], Column(await PageAsync(s.Db, admin, route, "?dimension=Other&state=inactive"), "code"));
        Assert.Equal(18, Total(await PageAsync(s.Db, admin, route, "?state=active")));

        var past = await PageAsync(s.Db, admin, route, "?skip=90&take=10");
        Assert.Equal((10, 9), (past.GetProperty("skip").GetInt32(), past.GetProperty("items").GetArrayLength()));
    }

    [Fact]
    public async Task Stage_templates_filter_on_the_product_they_are_meant_for_and_their_state()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var frame = await ProductAsync(s.Db, "ZT-FRAME", ManufacturingRole.SemiFinished, SystemUnits.Piece);
        for (var i = 1; i <= 12; i++)
            s.Db.StageTemplates.Add(new StageTemplate
            {
                Code = $"T{i:00}", NameAr = $"T{i:00}", NameFr = $"T{i:00}", NameEn = $"T{i:00}",
                ProductId = i == 2 ? frame.Id : null, IsActive = i != 4,
            });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        const string route = Mfg + "/stage-templates/page";

        var first = await PageAsync(s.Db, admin, route);
        Assert.Equal(12, Total(first));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"T{i:00}"), Column(first, "code"));
        Assert.Equal(["T11", "T12"], Column(await PageAsync(s.Db, admin, route, "?skip=10"), "code"));

        var meant = await PageAsync(s.Db, admin, route, "?product=one");
        Assert.Equal(["T02"], Column(meant, "code"));
        Assert.Equal(["ZT-FRAME"], Column(meant, "productSku"));
        Assert.Equal(["T04"], Column(await PageAsync(s.Db, admin, route, "?product=any&state=inactive"), "code"));
    }

    [Fact]
    public async Task Materials_are_searched_with_the_till_s_fold_and_filtered_on_role_till_and_base_unit()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        for (var i = 1; i <= 12; i++)
            s.Db.Products.Add(new Product
            {
                Sku = $"ZQ-{i:00}", NameFr = $"Fil {i}", NameAr = i == 7 ? "سقالة" : $"سلك {i}", NameEn = $"Wire {i}", Unit = "kg", Price = 0m,
                ManufacturingRole = ManufacturingRole.RawMaterial, BaseUnitId = SystemUnits.Kilogram, AlsoForSale = i == 5,
            });
        s.Db.Products.Add(new Product { Sku = "ZQ-X", NameFr = "Marchandise", NameAr = "بضاعة", NameEn = "Goods", Unit = "pcs", Price = 10m });
        // Switched off: the list route leaves it out, and so does its page.
        s.Db.Products.Add(new Product
        {
            Sku = "ZQ-OFF", NameFr = "Ancien", NameAr = "قديم", NameEn = "Old", Unit = "kg", IsActive = false,
            ManufacturingRole = ManufacturingRole.RawMaterial, BaseUnitId = SystemUnits.Kilogram,
        });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        const string route = Mfg + "/materials/page";

        var first = await PageAsync(s.Db, admin, route, "?q=zq");
        Assert.Equal(13, Total(first));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"ZQ-{i:00}"), Column(first, "sku"));
        Assert.Equal(["ZQ-11", "ZQ-12", "ZQ-X"], Column(await PageAsync(s.Db, admin, route, "?q=zq&skip=10"), "sku"));

        // سقاله finds سقالة: the till's fold, on the server.
        Assert.Equal(["ZQ-07"], Column(await PageAsync(s.Db, admin, route, "?q=" + Uri.EscapeDataString("سقاله")), "sku"));
        Assert.Equal(12, Total(await PageAsync(s.Db, admin, route, "?q=zq&role=manufacturing")));
        Assert.Equal(["ZQ-X"], Column(await PageAsync(s.Db, admin, route, "?q=zq&role=None"), "sku"));
        Assert.Equal(["ZQ-X"], Column(await PageAsync(s.Db, admin, route, "?q=zq&baseUnit=missing"), "sku"));
        // The Till column's three words, in its order of precedence: sold as well, sold, not at the till.
        Assert.Equal(["ZQ-05"], Column(await PageAsync(s.Db, admin, route, "?q=zq&till=also"), "sku"));
        Assert.Equal(["ZQ-X"], Column(await PageAsync(s.Db, admin, route, "?q=zq&till=sold"), "sku"));
        Assert.Equal(11, Total(await PageAsync(s.Db, admin, route, "?q=zq&till=not")));
    }

    [Fact]
    public async Task Lines_filter_on_manager_and_recipe_and_the_tiles_count_every_line_the_state_keeps()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        for (var i = 1; i <= 10; i++)
        {
            var warehouse = new Branch { NameAr = $"ورشة {i}", NameFr = $"Atelier {i}", IsWarehouse = true, IsOpenForOperation = true };
            s.Db.Branches.Add(warehouse);
            await s.Db.SaveChangesAsync();
            s.Db.ProductionLines.Add(new ProductionLine
            {
                Code = $"L{i:00}", NameAr = $"خط {i}", NameFr = $"Ligne {i}", NameEn = $"Line {i}", LocationId = warehouse.Id,
                IsActive = i != 10, ManagerUserId = i == 1 ? w.Admin : null,
            });
        }
        await s.Db.SaveChangesAsync();
        var l02 = s.Db.ProductionLines.Single(l => l.Code == "L02").Id;
        s.Db.BillsOfMaterials.Add(new BillOfMaterials
        {
            ProductId = w.Nails, ProductionLineId = l02, Version = 1, Status = BomStatus.Active, BatchQuantity = 100m,
            BatchUnitId = SystemUnits.Kilogram, CreatedBy = "test", ActivatedAtUtc = DateTime.UtcNow, ActivatedBy = "test",
        });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        const string route = Mfg + "/lines/page";

        // Active lines only, by code: L01…L09, LA, LB. Karim and Samir work on LA and LB.
        var first = await PageAsync(s.Db, w.AdminPrincipal, route);
        Assert.Equal(11, Total(first));
        Assert.Equal(["L01", "L02", "L03", "L04", "L05", "L06", "L07", "L08", "L09", "LA"], Column(first, "code"));
        Assert.Equal(["LB"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?skip=10"), "code"));
        var tiles = Summary(first);
        Assert.Equal((11, 4, 1), (tiles.GetProperty("lines").GetInt32(), tiles.GetProperty("employees").GetInt32(), tiles.GetProperty("activeBoms").GetInt32()));

        Assert.Equal(["L01"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?manager=yes"), "code"));
        Assert.Equal(["L02"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?boms=yes"), "code"));
        var neither = await PageAsync(s.Db, w.AdminPrincipal, route, "?manager=no&boms=no");
        Assert.Equal(9, Total(neither));
        // The tiles are the list's, whatever the manager and recipe filters: as the screen always showed them.
        Assert.Equal(11, Summary(neither).GetProperty("lines").GetInt32());

        var every = await PageAsync(s.Db, w.AdminPrincipal, route, "?includeInactive=true");
        Assert.Equal((12, 12), (Total(every), Summary(every).GetProperty("lines").GetInt32()));
    }

    [Fact]
    public async Task A_line_s_stock_filters_on_the_role_and_a_line_out_of_reach_is_not_found()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        for (var i = 1; i <= 11; i++)
        {
            var product = await ProductAsync(s.Db, $"ZS-{i:00}", ManufacturingRole.Consumable, SystemUnits.Piece);
            s.Db.ProductStocks.Add(new ProductStock { ProductId = product.Id, BranchId = WarehouseA, StockQty = 5m });
        }
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        const string route = Mfg + "/lines/{id:guid}/stock/page";

        var first = await PageAsync(s.Db, w.AdminPrincipal, route, "", Of(w.LineA));
        Assert.Equal(12, Total(first));
        Assert.Equal(new[] { "WIRE-3" }.Concat(Enumerable.Range(1, 9).Select(i => $"ZS-{i:00}")), Column(first, "sku"));
        Assert.Equal(12, Summary(first).GetProperty("all").GetInt32());
        Assert.Equal(["RawMaterial", "Consumable"], Strings(Summary(first).GetProperty("choices")));

        var raw = await PageAsync(s.Db, w.AdminPrincipal, route, "?role=RawMaterial", Of(w.LineA));
        Assert.Equal(["WIRE-3"], Column(raw, "sku"));
        Assert.Equal(12, Summary(raw).GetProperty("all").GetInt32());
        Assert.Equal(11, Total(await PageAsync(s.Db, w.AdminPrincipal, route, "?role=Consumable", Of(w.LineA))));

        // Oran's manager is on no line and works at no line's warehouse: the line does not exist for them.
        var (_, oran) = await ManagerAsync(s.Db, TillHarness.Oran, PermissionCatalog.ManufacturingView);
        await RefusedAsync(s.Db, oran, 404, "not_found", "GET", route, "", Of(w.LineA));
    }

    [Fact]
    public async Task Recipes_filter_on_the_list_route_s_own_filters_the_scope_and_the_search()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        for (var i = 1; i <= 11; i++)
        {
            var product = await ProductAsync(s.Db, $"ZB-{i:00}", ManufacturingRole.Finished, SystemUnits.Piece);
            s.Db.BillsOfMaterials.Add(new BillOfMaterials
            {
                ProductId = product.Id, ProductionLineId = w.LineA, Version = 1, Status = BomStatus.Draft, BatchQuantity = 1m,
                BatchUnitId = SystemUnits.Piece, CreatedBy = "test",
            });
        }
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        const string route = Mfg + "/boms/page";

        // NAIL-70's recipe serves every line; the eleven drafts are line A's own.
        var first = await PageAsync(s.Db, w.AdminPrincipal, route);
        Assert.Equal(12, Total(first));
        Assert.Equal(new[] { "NAIL-70" }.Concat(Enumerable.Range(1, 9).Select(i => $"ZB-{i:00}")), Column(first, "productSku"));
        Assert.Equal(["NAIL-70"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?scope=every"), "productSku"));
        Assert.Equal(11, Total(await PageAsync(s.Db, w.AdminPrincipal, route, "?scope=one")));
        Assert.Equal(["NAIL-70"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?status=Active"), "productSku"));
        Assert.Equal(11, Total(await PageAsync(s.Db, w.AdminPrincipal, route, $"?lineId={w.LineA}")));
        Assert.Equal(["ZB-03"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?q=zb-03"), "productSku"));
        Assert.Equal(0, Total(await PageAsync(s.Db, w.AdminPrincipal, route, "?status=Draft&scope=every")));
    }

    // ---------- operations ----------

    [Fact]
    public async Task Orders_newest_first_filter_on_late_started_short_planned_end_and_the_search()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var ids = new List<Guid>();
        for (var i = 1; i <= 12; i++) ids.Add(OrderId(await w.CreateOrderAsync(s.Db, w.LineA, 1)));
        // The third is cancelled: no longer live, so never late.
        await OkAsync(s.Db, w.AdminPrincipal, "POST", Order + "/cancel", """{"reason":"Plus nécessaire"}""", Of(ids[2]));
        const string route = Orders + "/page";

        var first = await PageAsync(s.Db, w.AdminPrincipal, route);
        Assert.Equal(12, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        Assert.Equal("MO-000012", Column(first, "number")[0]);
        Assert.Equal(["MO-000002", "MO-000001"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?skip=10"), "number"));

        // Planned to end on 2026-10-21, a day long past: every live order is late.
        Assert.Equal(11, Total(await PageAsync(s.Db, w.AdminPrincipal, route, "?late=yes")));
        Assert.Equal(["MO-000003"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?late=no"), "number"));
        Assert.Equal(["MO-000003"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?status=Cancelled"), "number"));
        Assert.Equal(0, Total(await PageAsync(s.Db, w.AdminPrincipal, route, "?startedShort=yes")));
        Assert.Equal(12, Total(await PageAsync(s.Db, w.AdminPrincipal, route, "?endFrom=2026-10-21&endTo=2026-10-21")));
        Assert.Equal(0, Total(await PageAsync(s.Db, w.AdminPrincipal, route, "?endFrom=2026-10-22")));
        Assert.Equal(["MO-000005"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?q=mo-000005"), "number"));
        Assert.Equal(0, Total(await PageAsync(s.Db, w.AdminPrincipal, route, $"?lineId={w.LineB}")));
    }

    [Fact]
    public async Task Daily_records_newest_day_first_between_two_company_days()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        for (var day = 1; day <= 12; day++)
            await OkAsync(s.Db, w.AdminPrincipal, "POST", Mfg + "/logs", $$"""{"lineId":"{{w.LineA}}","day":"2026-10-{{day:00}}"}""");
        const string route = Mfg + "/logs/page";

        var first = await PageAsync(s.Db, w.AdminPrincipal, route);
        Assert.Equal(12, Total(first));
        Assert.Equal(Enumerable.Range(3, 10).Reverse().Select(d => $"2026-10-{d:00}"), Column(first, "day"));
        Assert.Equal(["2026-10-07", "2026-10-06", "2026-10-05"],
            Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?from=2026-10-05&to=2026-10-07"), "day"));
        Assert.Equal(12, Total(await PageAsync(s.Db, w.AdminPrincipal, route, "?status=Draft")));
        Assert.Equal(0, Total(await PageAsync(s.Db, w.AdminPrincipal, route, "?status=Approved")));
        Assert.Equal(0, Total(await PageAsync(s.Db, w.AdminPrincipal, route, $"?lineId={w.LineB}")));
    }

    // ---------- reports ----------

    [Fact]
    public async Task Pos_exceptions_filter_on_the_role_and_keep_the_report_s_tiles_within_the_caller_s_branch()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var sale = TillHarness.Sale("T-SALE-1", 1);
        Assert.Equal(1, (await TillHarness.NewService(s.Db).PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None)).Accepted);
        var now = DateTime.UtcNow;
        for (var i = 1; i <= 12; i++)
            s.Db.ManufacturingSaleExceptions.Add(new ManufacturingSaleException
            {
                SaleId = sale.Id, SaleLineId = Guid.NewGuid(), ProductId = TillHarness.Pipe, Sku = "PIPE-90", BranchId = TillHarness.Algiers,
                DeviceCode = "C01", Reference = $"T-{i:00}", Quantity = i, SoldAtUtc = now.AddHours(-i), ReceivedAtUtc = now.AddHours(-i),
                Role = i <= 9 ? ManufacturingRole.RawMaterial : ManufacturingRole.Consumable,
                // Two sold before the product was withdrawn, two after it, eight with no withdrawal on file.
                WithdrawnAtUtc = i <= 4 ? now.AddDays(-1) : null, SoldBeforeWithdrawal = i <= 2,
            });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        const string route = Mfg + "/reports/pos-exceptions/page";

        var first = await PageAsync(s.Db, admin, route);
        Assert.Equal(12, Total(first));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"T-{i:00}"), Column(first, "reference"));
        var tiles = Summary(first);
        Assert.Equal((12, 78m, 2, 2, 8), (tiles.GetProperty("sales").GetInt32(), tiles.GetProperty("totalQuantity").GetDecimal(),
            tiles.GetProperty("staleCount").GetInt32(), tiles.GetProperty("afterWithdrawalCount").GetInt32(), tiles.GetProperty("unknownCount").GetInt32()));
        Assert.Equal(["RawMaterial", "Consumable"], Strings(tiles.GetProperty("roles")));

        var consumables = await PageAsync(s.Db, admin, route, "?role=Consumable");
        Assert.Equal(["T-10", "T-11", "T-12"], Column(consumables, "reference"));
        // The tiles stay the report's, whatever role is chosen.
        Assert.Equal(12, Summary(consumables).GetProperty("sales").GetInt32());
        Assert.Equal(2, Total(await PageAsync(s.Db, admin, route, "?stale=stale")));

        // Oran's manager sees Oran's sales only, whatever branch they ask for.
        var (_, oran) = await ManagerAsync(s.Db, TillHarness.Oran, PermissionCatalog.ManufacturingReportsView);
        var own = await PageAsync(s.Db, oran, route, $"?branchId={TillHarness.Algiers}");
        Assert.Equal((0, 0), (Total(own), Summary(own).GetProperty("sales").GetInt32()));
    }

    [Fact]
    public async Task Pending_movements_filter_on_kind_and_company_day_and_keep_the_report_s_values()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var id = OrderId(order);
        for (var i = 0; i < 6; i++)
            await OkAsync(s.Db, w.AdminPrincipal, "POST", Order + "/movements", Movement("Issue", MaterialId(order), 10), Of(id));
        for (var i = 0; i < 6; i++)
            await OkAsync(s.Db, w.AdminPrincipal, "POST", Order + "/movements", Movement("Return", MaterialId(order), 1), Of(id));
        const string route = Mfg + "/reports/pending-movements/page";

        var first = await PageAsync(s.Db, w.AdminPrincipal, route);
        Assert.Equal(12, Total(first));
        Assert.Equal(["MI-000001", "MI-000002", "MI-000003", "MI-000004", "MI-000005", "MI-000006", "MR-000001", "MR-000002", "MR-000003", "MR-000004"],
            Column(first, "number"));
        Assert.Equal(["MR-000005", "MR-000006"], Column(await PageAsync(s.Db, w.AdminPrincipal, route, "?skip=10"), "number"));
        // 60 kg issued and 6 kg returned at the wire's 150: the report's tiles, over every movement of the line.
        var tiles = Summary(first);
        Assert.Equal((9000m, 900m, 8100m, 12), (tiles.GetProperty("issuedValue").GetDecimal(), tiles.GetProperty("returnedValue").GetDecimal(),
            tiles.GetProperty("workInProgressValue").GetDecimal(), tiles.GetProperty("all").GetInt32()));
        Assert.Equal(["Issue", "Return"], Strings(tiles.GetProperty("kinds")));

        var returns = await PageAsync(s.Db, w.AdminPrincipal, route, "?kind=Return");
        Assert.Equal(6, Total(returns));
        Assert.Equal(9000m, Summary(returns).GetProperty("issuedValue").GetDecimal());

        // The movement's day is the company's: today's movements are found around today, and not in 2000.
        var today = CompanyDays.Today(DateTime.UtcNow, CompanyDays.Zone(new Algeria().Get().TimeZone));
        Assert.Equal(12, Total(await PageAsync(s.Db, w.AdminPrincipal, route, $"?from={today.AddDays(-1):yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}")));
        Assert.Equal(0, Total(await PageAsync(s.Db, w.AdminPrincipal, route, "?from=2000-01-01&to=2000-01-02")));
        Assert.Equal(0, Total(await PageAsync(s.Db, w.AdminPrincipal, route, $"?lineId={w.LineB}")));
    }

    [Fact]
    public async Task The_costing_reports_and_the_practice_ledger_page_their_rows_and_keep_the_report_s_figures()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var id = OrderId(order);
        await OkAsync(s.Db, w.AdminPrincipal, "POST", Order + "/movements", Movement("Issue", MaterialId(order), 100), Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", Order + "/movements", Movement("Return", MaterialId(order), 10), Of(id));
        await w.CreateOrderAsync(s.Db, w.LineB, 10);

        // Work in progress: the live orders, and the report's own totals.
        var wipReport = await OkAsync(s.Db, w.AdminPrincipal, "GET", Mfg + "/reports/work-in-progress");
        var wip = await PageAsync(s.Db, w.AdminPrincipal, Mfg + "/reports/work-in-progress/page");
        Assert.Equal(wipReport.GetProperty("rows").GetArrayLength(), Total(wip));
        Assert.Equal(2, Total(wip));
        Assert.Equal(wipReport.GetProperty("atLineValue").GetDecimal(), Summary(wip).GetProperty("atLineValue").GetDecimal());
        Assert.Equal(wipReport.GetProperty("workInProgress").GetDecimal(), Summary(wip).GetProperty("workInProgress").GetDecimal());
        Assert.Equal(["Open"], Strings(Summary(wip).GetProperty("statuses")));
        Assert.Equal(1, Total(await PageAsync(s.Db, w.AdminPrincipal, Mfg + "/reports/work-in-progress/page", $"?lineId={w.LineB}")));
        Assert.Equal(0, Total(await PageAsync(s.Db, w.AdminPrincipal, Mfg + "/reports/work-in-progress/page", "?status=Cancelled")));
        var provisional = Total(await PageAsync(s.Db, w.AdminPrincipal, Mfg + "/reports/work-in-progress/page", "?provisional=yes"));
        Assert.Equal(2, provisional + Total(await PageAsync(s.Db, w.AdminPrincipal, Mfg + "/reports/work-in-progress/page", "?provisional=no")));

        // Material flow: the differences found, and what was checked.
        var flowReport = await OkAsync(s.Db, w.AdminPrincipal, "GET", Mfg + "/reports/material-flow");
        var flow = await PageAsync(s.Db, w.AdminPrincipal, Mfg + "/reports/material-flow/page");
        Assert.Equal(flowReport.GetProperty("issues").GetArrayLength(), Total(flow));
        Assert.Equal(flowReport.GetProperty("issues").GetArrayLength(), Summary(flow).GetProperty("issues").GetInt32());
        Assert.Equal(flowReport.GetProperty("movementsChecked").GetInt32(), Summary(flow).GetProperty("movementsChecked").GetInt32());

        // Labour: one month, the report's own hours and month.
        var labourReport = await OkAsync(s.Db, w.AdminPrincipal, "GET", Mfg + "/reports/labour-absorption", "?year=2026&month=10");
        var labour = await PageAsync(s.Db, w.AdminPrincipal, Mfg + "/reports/labour-absorption/page", "?year=2026&month=10");
        Assert.Equal(labourReport.GetProperty("rows").GetArrayLength(), Total(labour));
        Assert.Equal((2026, 10), (Summary(labour).GetProperty("year").GetInt32(), Summary(labour).GetProperty("month").GetInt32()));
        Assert.Equal(labourReport.GetProperty("orderHours").GetDecimal(), Summary(labour).GetProperty("orderHours").GetDecimal());

        // The practice ledger: the month's entries a page at a time, the month itself in the summary.
        const string ledger = Mfg + "/accounting/practice-ledger";
        var month = await OkAsync(s.Db, w.AdminPrincipal, "GET", ledger);
        var entries = month.GetProperty("entries").EnumerateArray().ToList();
        var page = await PageAsync(s.Db, w.AdminPrincipal, ledger + "/entries/page");
        Assert.NotEmpty(entries);
        Assert.Equal(entries.Count, Total(page));
        Assert.Equal(entries.Take(10).Select(e => e.GetProperty("kind").GetString()), Column(page, "kind"));
        var summary = Summary(page);
        Assert.Equal(entries.Count, summary.GetProperty("entryCount").GetInt32());
        Assert.Equal(month.GetProperty("totalDebit").GetDecimal(), summary.GetProperty("totalDebit").GetDecimal());
        Assert.Equal(month.GetProperty("checks").GetArrayLength(), summary.GetProperty("checks").GetArrayLength());
        Assert.Equal(month.GetProperty("lateCosts").GetArrayLength(), summary.GetProperty("lateCostCount").GetInt32());
        Assert.Equal(entries.Select(e => e.GetProperty("kind").GetString()!).Distinct(), Strings(summary.GetProperty("kinds")));
        var kind = entries[0].GetProperty("kind").GetString()!;
        Assert.Equal(entries.Count(e => e.GetProperty("kind").GetString() == kind),
            Total(await PageAsync(s.Db, w.AdminPrincipal, ledger + "/entries/page", $"?kind={kind}")));
        var branch = entries[0].GetProperty("branchId").GetGuid();
        Assert.Equal(entries.Count(e => e.GetProperty("branchId").GetGuid() == branch),
            Total(await PageAsync(s.Db, w.AdminPrincipal, ledger + "/entries/page", $"?entryBranchId={branch}")));
        Assert.Equal(month.GetProperty("lateCosts").GetArrayLength(), Total(await PageAsync(s.Db, w.AdminPrincipal, ledger + "/late-costs/page")));
        // The ledger's refusals are the page's too.
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "month_in_future", "GET", ledger + "/entries/page", "?year=2100&month=12");
    }
}
