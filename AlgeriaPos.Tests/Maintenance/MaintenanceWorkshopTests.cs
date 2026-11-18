using System.Text.Json;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Maintenance;
using AlgeriaPos.Tests.Manufacturing;
using AlgeriaPos.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Maintenance.MaintenanceWorld;

namespace AlgeriaPos.Tests.Maintenance;

/// <summary>
/// MS-04's workshop list (docs/maintenance/plan.md, "MS-04 as built"): which employees of a branch are workshop staff. Kept
/// per branch, read and kept by the Admin for every branch and by anyone else for their own; a row counts only while its
/// person still belongs to its branch. What booking does with it is in <see cref="MaintenanceLabourTests"/>.
/// </summary>
public class MaintenanceWorkshopTests
{
    private const string List = "/api/maintenance/workshop";
    private const string Row = "/api/maintenance/workshop/{id:guid}";

    private static readonly Employee Karim = new() { Number = "E-101", FullName = "Karim Alger", BranchId = Algiers, HiredOn = new DateOnly(2025, 1, 1) };
    private static readonly Employee Nadia = new() { Number = "E-201", FullName = "Nadia Oran", BranchId = Oran, HiredOn = new DateOnly(2025, 1, 1) };
    private static readonly Employee Gone = new() { Number = "E-102", FullName = "Parti Alger", BranchId = Algiers, HiredOn = new DateOnly(2024, 1, 1), LeftOn = new DateOnly(2025, 6, 30) };

    private static async Task<MaintenanceWorld> NewAsync(Action<MaintenanceSettings>? more = null)
    {
        var w = await MaintenanceWorld.NewAsync(On(more));
        foreach (var e in new[] { Karim, Nadia, Gone })
            w.Db.Employees.Add(new Employee { Id = e.Id, Number = e.Number, FullName = e.FullName, BranchId = e.BranchId, HiredOn = e.HiredOn, LeftOn = e.LeftOn });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        return w;
    }

    private static string Add(Guid employee, Guid? branch = null) => JsonSerializer.Serialize(new { employeeId = employee, branchId = branch });

    private static async Task<JsonElement> OkAsync(MaintenanceWorld w, System.Security.Claims.ClaimsPrincipal caller, string method, string pattern,
        string json = "", params (string Key, string Value)[] route)
    {
        var (status, body) = await w.CallAsync(caller, method, pattern, json, route);
        w.Db.ChangeTracker.Clear();
        Assert.True(status == 200, $"{method} {pattern}: {status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task RefusedAsync(MaintenanceWorld w, System.Security.Claims.ClaimsPrincipal caller, int status, string? code,
        string method, string pattern, string json = "", params (string Key, string Value)[] route)
    {
        var (actual, body) = await w.CallAsync(caller, method, pattern, json, route);
        w.Db.ChangeTracker.Clear();
        Assert.True(actual == status, $"{method} {pattern}: expected {status}, got {actual} {body}");
        if (code is not null) Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private static IEnumerable<string?> Names(JsonElement list, string array = "members") =>
        list.GetProperty(array).EnumerateArray().Select(m => m.GetProperty("fullName").GetString());

    private static JsonElement Branch(JsonElement list, Guid id) =>
        list.GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("branchId").GetGuid() == id);

    [Fact]
    public async Task The_Admin_keeps_every_branch_s_list_and_each_person_goes_on_their_own_branch_s_list_once()
    {
        await using var w = await NewAsync();
        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);

        // Empty at first: everyone employed today is offered, the one who left is not.
        var empty = await OkAsync(w, admin, "GET", List);
        Assert.Equal("AnyEmployeeOfBranch", empty.GetProperty("bookableEmployees").GetString());
        Assert.Empty(empty.GetProperty("members").EnumerateArray());
        Assert.Equal(["Karim Alger", "Nadia Oran"], Names(empty, "available").Order());
        Assert.Equal((1, 0), (Branch(empty, Algiers).GetProperty("employees").GetInt32(), Branch(empty, Algiers).GetProperty("onList").GetInt32()));

        // Karim on his own branch's list when none is named; Nadia on Oran's, named.
        await OkAsync(w, admin, "POST", List, Add(Karim.Id));
        var both = await OkAsync(w, admin, "POST", List, Add(Nadia.Id, Oran));
        Assert.Equal(["Karim Alger", "Nadia Oran"], Names(both));
        var karim = both.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("employeeId").GetGuid() == Karim.Id);
        Assert.Equal((Algiers, true, true, "chef"), (karim.GetProperty("branchId").GetGuid(), karim.GetProperty("employed").GetBoolean(),
            karim.GetProperty("effective").GetBoolean(), karim.GetProperty("addedBy").GetString()));
        Assert.Empty(both.GetProperty("available").EnumerateArray());
        Assert.Equal(1, Branch(both, Oran).GetProperty("onList").GetInt32());

        // Once per branch; another branch's list, a leaver and nobody are refused.
        await RefusedAsync(w, admin, 409, MaintenanceWorkshop.AlreadyOnListCode, "POST", List, Add(Karim.Id));
        await RefusedAsync(w, admin, 400, MaintenanceWorkshop.NotOfBranchCode, "POST", List, Add(Karim.Id, Oran));
        await RefusedAsync(w, admin, 400, MaintenanceWorkshop.NotEmployedCode, "POST", List, Add(Gone.Id));
        await RefusedAsync(w, admin, 404, "not_found", "POST", List, Add(Guid.NewGuid()));
        Assert.Equal(2, await w.Db.MaintenanceWorkshopMembers.CountAsync());

        // One branch named: only its list.
        var oran = await OkAsync(w, admin, "GET", List, "?branchId=" + Oran);
        Assert.Equal(["Nadia Oran"], Names(oran));
        Assert.Equal([Oran], oran.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("branchId").GetGuid()));

        // Removed: off the list, offered again.
        var id = karim.GetProperty("id").GetGuid().ToString();
        var after = await OkAsync(w, admin, "DELETE", Row, "", ("id", id));
        Assert.Equal(["Nadia Oran"], Names(after));
        Assert.Equal(["Karim Alger"], Names(after, "available"));
        await RefusedAsync(w, admin, 404, "not_found", "DELETE", Row, "", ("id", id));
    }

    [Fact]
    public async Task A_branch_manager_reads_and_keeps_only_their_own_branch_s_list()
    {
        await using var w = await NewAsync();
        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);
        var karimRow = (await OkAsync(w, admin, "POST", List, Add(Karim.Id))).GetProperty("members")[0].GetProperty("id").GetGuid();
        var (_, oran) = await MfgHarness.ManagerAsync(w.Db, Oran, PermissionCatalog.MaintenanceView, PermissionCatalog.MaintenanceSettings);
        w.Db.ChangeTracker.Clear();

        // Asking for Algiers reads Oran's: an empty list, Nadia offered, Algiers neither listed nor counted.
        var own = await OkAsync(w, oran, "GET", List, "?branchId=" + Algiers);
        Assert.Empty(own.GetProperty("members").EnumerateArray());
        Assert.Equal(["Nadia Oran"], Names(own, "available"));
        Assert.Equal([Oran], own.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("branchId").GetGuid()));

        // Naming Algiers lands on Oran's list, where Karim does not belong; Nadia does.
        await RefusedAsync(w, oran, 400, MaintenanceWorkshop.NotOfBranchCode, "POST", List, Add(Karim.Id, Algiers));
        var added = await OkAsync(w, oran, "POST", List, Add(Nadia.Id, Algiers));
        Assert.Equal(["Nadia Oran"], Names(added));
        Assert.Equal(Oran, (await w.Db.MaintenanceWorkshopMembers.AsNoTracking().SingleAsync(m => m.EmployeeId == Nadia.Id)).BranchId);

        // Algiers' row is not found from Oran, and stays.
        await RefusedAsync(w, oran, 404, null, "DELETE", Row, "", ("id", karimRow.ToString()));
        Assert.True(await w.Db.MaintenanceWorkshopMembers.AnyAsync(m => m.Id == karimRow));
        var nadiaRow = added.GetProperty("members")[0].GetProperty("id").GetGuid();
        Assert.Empty((await OkAsync(w, oran, "DELETE", Row, "", ("id", nadiaRow.ToString()))).GetProperty("members").EnumerateArray());
    }

    [Fact]
    public async Task A_person_who_moves_keeps_the_old_row_which_no_longer_counts_and_the_module_off_answers_maintenance_off()
    {
        await using var w = await NewAsync();
        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);
        await OkAsync(w, admin, "POST", List, Add(Nadia.Id, Oran));

        var moved = await w.Db.Employees.SingleAsync(e => e.Id == Nadia.Id);
        moved.BranchId = Algiers;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();

        var list = await OkAsync(w, admin, "GET", List);
        var row = list.GetProperty("members").EnumerateArray().Single();
        Assert.Equal((Oran, Algiers, "Alger-Centre", false), (row.GetProperty("branchId").GetGuid(), row.GetProperty("employeeBranchId").GetGuid(),
            row.GetProperty("employeeBranchName").GetString(), row.GetProperty("effective").GetBoolean()));
        Assert.Equal(0, Branch(list, Oran).GetProperty("onList").GetInt32());
        // Not copied to Algiers: she is offered there.
        Assert.Contains("Nadia Oran", Names(list, "available"));

        var settings = await w.Db.MaintenanceSettings.SingleAsync();
        settings.Module = MaintenanceSwitch.Off;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        await RefusedAsync(w, admin, 409, MaintenanceJobs.OffCode, "GET", List);
        await RefusedAsync(w, admin, 409, MaintenanceJobs.OffCode, "POST", List, Add(Karim.Id));
    }
}
