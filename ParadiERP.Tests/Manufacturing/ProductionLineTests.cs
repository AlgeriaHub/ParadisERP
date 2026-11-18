using System.Text.Json;
using ParadiERP.Domain;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Manufacturing.MfgHarness;

namespace ParadiERP.Tests.Manufacturing;

/// <summary>
/// Production lines (Phase 1 §1): two independent lines, each on its own warehouse and with its own stock,
/// reached only by those who run them, with employees who stay on the branch that pays them.
/// </summary>
public class ProductionLineTests
{
    private const string Lines = "/api/manufacturing/lines";
    private const string Line = "/api/manufacturing/lines/{id:guid}";

    private static string LineJson(string code, Guid location, Guid? manager = null, bool active = true) =>
        $$"""{"code":"{{code}}","nameAr":"خط {{code}}","nameFr":"Ligne {{code}}","description":"Clous et pointes","locationId":"{{location}}","managerUserId":{{(manager is Guid m ? $"\"{m}\"" : "null")}},"isActive":{{(active ? "true" : "false")}}}""";

    internal static async Task<(Guid A, Guid B)> TwoLinesAsync(MfgHarness.Setup s, System.Security.Claims.ClaimsPrincipal admin, Guid? managerOfA = null)
    {
        var a = await OkAsync(s.Db, admin, "POST", Lines, LineJson("L1", WarehouseA, managerOfA));
        var b = await OkAsync(s.Db, admin, "POST", Lines, LineJson("L2", WarehouseB));
        return (a.GetProperty("line").GetProperty("id").GetGuid(), b.GetProperty("line").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Two_lines_are_configured_independently_each_on_its_own_warehouse()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var (a, b) = await TwoLinesAsync(s, admin);

        // The second line on the first one's location would share its stock: refused.
        await RefusedAsync(s.Db, admin, 409, "location_taken", "POST", Lines, LineJson("L3", WarehouseA));
        // A selling branch and the main warehouse are not line locations.
        await RefusedAsync(s.Db, admin, 400, "location_not_line_warehouse", "POST", Lines, LineJson("L3", TillHarness.Oran));
        var main = await s.Db.Branches.Where(x => x.IsMain && x.IsWarehouse).Select(x => x.Id).SingleAsync();
        await RefusedAsync(s.Db, admin, 400, "location_not_line_warehouse", "POST", Lines, LineJson("L3", main));
        await RefusedAsync(s.Db, admin, 409, "code_taken", "POST", Lines, LineJson("l1", main));

        // Editing one line leaves the other as it was; the location does not move.
        await OkAsync(s.Db, admin, "PUT", Line, LineJson("L1", WarehouseA, active: false), Id(a));
        await RefusedAsync(s.Db, admin, 409, "location_fixed", "PUT", Line, LineJson("L1", WarehouseB), Id(a));
        var lines = await s.Db.ProductionLines.AsNoTracking().OrderBy(l => l.Code).ToListAsync();
        Assert.Equal([("L1", WarehouseA, false), ("L2", WarehouseB, true)], lines.Select(l => (l.Code, l.LocationId, l.IsActive)));

        // An inactive line is out of the default list, still readable by id.
        var listed = await OkAsync(s.Db, admin, "GET", Lines);
        Assert.Equal(["L2"], listed.EnumerateArray().Select(l => l.GetProperty("code").GetString()));
        await OkAsync(s.Db, admin, "GET", Line, "", Id(a));
    }

    [Fact]
    public async Task Each_line_sees_only_its_own_location_s_stock()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var (a, b) = await TwoLinesAsync(s, admin);
        var wire = await ProductAsync(s.Db, "WIRE-3MM", ManufacturingRole.RawMaterial, SystemUnits.Kilogram);
        s.Db.ProductStocks.Add(new ProductStock { ProductId = wire.Id, BranchId = WarehouseA, StockQty = 420.5m });
        s.Db.ProductStocks.Add(new ProductStock { ProductId = wire.Id, BranchId = WarehouseB, StockQty = 75m });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var stockA = await OkAsync(s.Db, admin, "GET", Line + "/stock", "", Id(a));
        var stockB = await OkAsync(s.Db, admin, "GET", Line + "/stock", "", Id(b));
        Assert.Equal(420.5m, stockA.EnumerateArray().Single().GetProperty("available").GetDecimal());
        Assert.Equal(75m, stockB.EnumerateArray().Single().GetProperty("available").GetDecimal());
        // The pipes held at Algiers and Oran are no line's.
        Assert.DoesNotContain(stockA.EnumerateArray(), r => r.GetProperty("sku").GetString() == "PIPE-90");

        // Nothing was duplicated: the warehouse rows are the only rows.
        Assert.Equal(2, await s.Db.ProductStocks.CountAsync(x => x.ProductId == wire.Id));
    }

    [Fact]
    public async Task A_branch_user_reaches_only_the_lines_they_are_on_and_cannot_create_one()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var (manager, managerPrincipal) = await ManagerAsync(s.Db, TillHarness.Oran, AllGrants);
        var (a, b) = await TwoLinesAsync(s, admin, managerOfA: manager.Id);

        var listed = await OkAsync(s.Db, managerPrincipal, "GET", Lines);
        Assert.Equal(["L1"], listed.EnumerateArray().Select(l => l.GetProperty("code").GetString()));
        await OkAsync(s.Db, managerPrincipal, "GET", Line, "", Id(a));
        // The other line is not there for them: by id, its stock, its employees, its recipes.
        await RefusedAsync(s.Db, managerPrincipal, 404, "not_found", "GET", Line, "", Id(b));
        await RefusedAsync(s.Db, managerPrincipal, 404, "not_found", "GET", Line + "/stock", "", Id(b));
        await RefusedAsync(s.Db, managerPrincipal, 404, "not_found", "PUT", Line + "/products", """{"productIds":[]}""", Id(b));

        // Lines are company structure: a branch manager neither creates nor edits one, nor names members.
        await RefusedAsync(s.Db, managerPrincipal, 403, "forbidden", "POST", Lines, LineJson("L9", WarehouseB));
        await RefusedAsync(s.Db, managerPrincipal, 403, "forbidden", "PUT", Line, LineJson("L1", WarehouseA), Id(a));
        await RefusedAsync(s.Db, managerPrincipal, 403, "forbidden", "PUT", Line + "/members", """{"members":[]}""", Id(a));

        // A supervisor named on line B now reaches it — and only reads it.
        var (supervisor, supervisorPrincipal) = await ManagerAsync(s.Db, TillHarness.Algiers, AllGrants);
        await OkAsync(s.Db, admin, "PUT", Line + "/members", $$"""{"members":[{"userId":"{{supervisor.Id}}","role":"Supervisor"}]}""", Id(b));
        var seen = await OkAsync(s.Db, supervisorPrincipal, "GET", Line, "", Id(b));
        Assert.False(seen.GetProperty("line").GetProperty("canAssign").GetBoolean());
        await RefusedAsync(s.Db, supervisorPrincipal, 403, "forbidden", "PUT", Line + "/products", """{"productIds":[]}""", Id(b));
        await RefusedAsync(s.Db, supervisorPrincipal, 404, "not_found", "GET", Line, "", Id(a));
    }

    [Fact]
    public async Task An_employee_works_on_a_line_without_leaving_the_branch_that_pays_them()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var (a, b) = await TwoLinesAsync(s, admin);
        var welder = new Employee { Number = "EMP-000101", FullName = "Karim Soudeur", BranchId = TillHarness.Oran, HiredOn = new DateOnly(2025, 1, 1), BaseRate = 52000m };
        s.Db.Employees.Add(welder);
        await s.Db.SaveChangesAsync();
        var role = await OkAsync(s.Db, admin, "POST", "/api/manufacturing/job-roles", """{"code":"welder","nameAr":"لحام","nameFr":"Soudeur"}""");

        await OkAsync(s.Db, admin, "POST", Line + "/employees",
            $$"""{"employeeId":"{{welder.Id}}","jobRoleId":"{{IdOf(role)}}","assignedOn":"2026-10-25"}""", Id(a));
        await RefusedAsync(s.Db, admin, 409, "already_assigned", "POST", Line + "/employees",
            $$"""{"employeeId":"{{welder.Id}}"}""", Id(a));
        // The same person may also work on the other line.
        var onB = await OkAsync(s.Db, admin, "POST", Line + "/employees", $$"""{"employeeId":"{{welder.Id}}"}""", Id(b));

        var stored = await s.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == welder.Id);
        Assert.Equal((TillHarness.Oran, 52000m), (stored.BranchId, stored.BaseRate));
        var line = await OkAsync(s.Db, admin, "GET", Line, "", Id(a));
        var assigned = line.GetProperty("employees").EnumerateArray().Single();
        Assert.Equal(TillHarness.Oran, assigned.GetProperty("payrollBranchId").GetGuid());
        Assert.Equal("WELDER", assigned.GetProperty("jobRoleCode").GetString());
        // No pay is read back through a line.
        Assert.DoesNotContain("52000", line.GetRawText());

        var assignment = onB.GetProperty("employees").EnumerateArray().Single().GetProperty("id").GetGuid();
        await RefusedAsync(s.Db, admin, 400, "end_before_start", "POST", Line + "/employees/{assignmentId:guid}/end",
            """{"endedOn":"2000-01-01"}""", Id(b), ("assignmentId", assignment.ToString()));
        var ended = await OkAsync(s.Db, admin, "POST", Line + "/employees/{assignmentId:guid}/end", "{}", Id(b), ("assignmentId", assignment.ToString()));
        Assert.NotEqual(JsonValueKind.Null, ended.GetProperty("employees")[0].GetProperty("endedOn").ValueKind);
        Assert.Equal(0, ended.GetProperty("line").GetProperty("employeeCount").GetInt32());
    }

    [Fact]
    public async Task A_line_s_manager_assigns_only_the_staff_they_may_see_and_only_produced_products()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var (manager, managerPrincipal) = await ManagerAsync(s.Db, TillHarness.Oran, AllGrants);
        var (a, _) = await TwoLinesAsync(s, admin, managerOfA: manager.Id);
        var ownBranch = new Employee { Number = "EMP-1", FullName = "Oran Worker", BranchId = TillHarness.Oran, HiredOn = new DateOnly(2025, 1, 1) };
        var elsewhere = new Employee { Number = "EMP-2", FullName = "Algiers Worker", BranchId = TillHarness.Algiers, HiredOn = new DateOnly(2025, 1, 1) };
        s.Db.Employees.AddRange(ownBranch, elsewhere);
        await s.Db.SaveChangesAsync();

        var candidates = await OkAsync(s.Db, managerPrincipal, "GET", Line + "/employee-candidates", "", Id(a));
        Assert.Equal(["EMP-1"], candidates.EnumerateArray().Select(e => e.GetProperty("number").GetString()));
        await RefusedAsync(s.Db, managerPrincipal, 400, "unknown_employee", "POST", Line + "/employees",
            $$"""{"employeeId":"{{elsewhere.Id}}"}""", Id(a));
        await OkAsync(s.Db, managerPrincipal, "POST", Line + "/employees", $$"""{"employeeId":"{{ownBranch.Id}}"}""", Id(a));

        var nails = await ProductAsync(s.Db, "NAIL-70", ManufacturingRole.Finished, SystemUnits.Kilogram);
        var wire = await ProductAsync(s.Db, "WIRE-3MM", ManufacturingRole.RawMaterial, SystemUnits.Kilogram);
        await RefusedAsync(s.Db, managerPrincipal, 400, "not_produced", "PUT", Line + "/products",
            $$"""{"productIds":["{{nails.Id}}","{{wire.Id}}"]}""", Id(a));
        var saved = await OkAsync(s.Db, managerPrincipal, "PUT", Line + "/products", $$"""{"productIds":["{{nails.Id}}"]}""", Id(a));
        Assert.Equal("NAIL-70", saved.GetProperty("products")[0].GetProperty("sku").GetString());
    }
}
