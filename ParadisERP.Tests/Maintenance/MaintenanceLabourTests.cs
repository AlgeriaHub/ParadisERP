using System.Security.Claims;
using System.Text.Json;
using ParadisERP.API.Endpoints;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Maintenance;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Manufacturing;
using ParadisERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Manufacturing.MfgHarness;
using static ParadisERP.Tests.Manufacturing.ProductionWorld;

namespace ParadisERP.Tests.Maintenance;

/// <summary>
/// Phase M3 (docs/maintenance/plan.md §2.5): hours on maintenance jobs go into production's <c>LaborBookings</c> with
/// <c>OrderKind = Maintenance</c>, under one attendance ceiling per person and day across production records and jobs,
/// the payroll-month lock, the override with a reason (<c>maintenance.labour.override</c>) and MS-04; and production's
/// readers never count them.
/// </summary>
public class MaintenanceLabourTests
{
    private const string Hours = "/api/maintenance/jobs/{id:guid}/hours";
    private const string Hour = "/api/maintenance/jobs/{id:guid}/hours/{bookingId:guid}";
    private const string Candidates = "/api/maintenance/jobs/{id:guid}/hours/candidates";
    private const string Workshop = "/api/maintenance/workshop";
    private const string ProductionBooking = "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings";

    /// <summary>
    /// The production world (Karim: 8 h on <see cref="ProductionWorld.Day"/>; Samir: attendance declared per month, both
    /// of Algiers), the maintenance module as <paramref name="settings"/> says (on by default), four pipes in maintenance in
    /// Algiers and an open internal job over two of them.
    /// </summary>
    private sealed record World(Setup S, ProductionWorld W, Guid Job)
    {
        public PosDbContext Db => S.Db;
    }

    private static async Task<World> NewAsync(Action<MaintenanceSettings>? settings = null)
    {
        var s = await MfgHarness.NewAsync();
        var w = await SetupAsync(s.Db);
        var row = new MaintenanceSettings { Module = MaintenanceSwitch.On };
        settings?.Invoke(row);
        s.Db.MaintenanceSettings.Add(row);
        var pipes = await s.Db.ProductStocks.SingleAsync(x => x.ProductId == TillHarness.Pipe && x.BranchId == TillHarness.Algiers);
        pipes.MaintenanceQty = 4m;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var jobs = new MaintenanceJobs(s.Db, new LedgerPoster(s.Db), MaintenanceWorld.Regional);
        // The job is opened with the module on, whatever the test then switches.
        var made = await jobs.CreateAsync(new CreateMaintenanceJobRequest(Guid.NewGuid().ToString("N"), TillHarness.Algiers, "Internal",
            TillHarness.Pipe, 2m, null, null, "Soudure"), new MaintenanceActor(w.Admin, "admin"));
        Assert.True(made.Ok, made.Error);
        s.Db.ChangeTracker.Clear();
        return new World(s, w, made.Value!.Id);
    }

    private static string Book(Guid employee, decimal hours, string? reason = null, string day = "2026-10-20", decimal overtime = 0m) =>
        JsonSerializer.Serialize(new { employeeId = employee, day, hours, overtimeHours = overtime, overrideReason = reason });

    private static async Task<(int Status, JsonElement Body)> CallAsync(World x, ClaimsPrincipal caller, string method, string pattern,
        string json = "", params (string Key, string Value)[] route)
    {
        var (status, body) = await BranchLockTests.InvokeAsync(x.Db, a => a.MapMaintenanceEndpoints(), method, pattern, caller, json, route);
        x.Db.ChangeTracker.Clear();
        return (status, string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone());
    }

    private static async Task<JsonElement> BookedAsync(World x, ClaimsPrincipal caller, string json)
    {
        var (status, body) = await CallAsync(x, caller, "POST", Hours, json, Of(x.Job));
        Assert.True(status == 200, $"{status} {body}");
        return body;
    }

    private static async Task RefusedAsync(World x, ClaimsPrincipal caller, int status, string code, string method, string pattern,
        string json = "", params (string Key, string Value)[] route)
    {
        var (actual, body) = await CallAsync(x, caller, method, pattern, json, route);
        Assert.True(actual == status, $"{method} {pattern}: expected {status}, got {actual} {body}");
        Assert.Equal(code, body.GetProperty("code").GetString());
    }

    /// <summary>A draft record of line A on <see cref="ProductionWorld.Day"/> with one row of a new order: its log and row ids.</summary>
    private static async Task<(Guid Log, Guid Row)> ProductionRowAsync(World x, Guid line)
    {
        var order = line == x.W.LineA ? await x.W.CreateOrderAsync(x.Db, line, 100m) : await OkAsync(x.Db, x.W.AdminPrincipal, "POST", Orders, x.W.OrderBody(line, 40m));
        var log = await OkAsync(x.Db, x.W.AdminPrincipal, "POST", "/api/manufacturing/logs", JsonSerializer.Serialize(new { lineId = line, day = "2026-10-20" }));
        var logId = log.GetProperty("log").GetProperty("id").GetGuid();
        var saved = await OkAsync(x.Db, x.W.AdminPrincipal, "PUT", "/api/manufacturing/logs/{id:guid}", JsonSerializer.Serialize(new
        {
            notes = (string?)null,
            rows = new[] { new { id = (Guid?)null, orderId = OrderId(order), stageId = Stage(order, 0).GetProperty("id").GetGuid(), category = (string?)null,
                specification = (string?)null, plannedQty = 100m, completedQty = 0m, rejectedQty = 0m, rejectReason = (string?)null, notes = (string?)null } },
        }), Of(logId));
        return (logId, saved.GetProperty("rows")[0].GetProperty("id").GetGuid());
    }

    private static (string, string)[] RowRoute(Guid log, Guid row) => [Of(log), ("rowId", row.ToString())];

    private static string ProductionHours(Guid employee, decimal hours, string? reason = null) =>
        JsonSerializer.Serialize(new { employeeId = employee, hours, overrideReason = reason });

    // ---------- one ceiling ----------

    [Fact]
    public async Task A_production_booking_and_a_maintenance_booking_on_one_day_stop_at_the_day_s_attendance()
    {
        var x = await NewAsync();
        await using var _ = x.S;
        var (logA, rowA) = await ProductionRowAsync(x, x.W.LineA);

        // Karim's day shows 8 h: 5 on line A's record, so 4 on the job is one too many and 3 fit.
        await OkAsync(x.Db, x.W.AdminPrincipal, "POST", ProductionBooking, ProductionHours(x.W.Karim, 5), RowRoute(logA, rowA));
        await RefusedAsync(x, x.W.AdminPrincipal, 409, "exceeds_attendance", "POST", Hours, Book(x.W.Karim, 4), Of(x.Job));
        var booked = await BookedAsync(x, x.W.AdminPrincipal, Book(x.W.Karim, 3, overtime: 1m));
        Assert.Equal(3m, booked.GetProperty("totalHours").GetDecimal());
        var line = booked.GetProperty("bookings")[0];
        Assert.Equal((8m, 1m, "Karim Benali"), (line.GetProperty("attendanceHours").GetDecimal(), line.GetProperty("overtimeHours").GetDecimal(),
            line.GetProperty("employeeName").GetString()));

        // And the other way round: the job's 3 h count against the next production booking — line B has no hour left.
        var (logB, rowB) = await ProductionRowAsync(x, x.W.LineB);
        await MfgHarness.RefusedAsync(x.Db, x.W.AdminPrincipal, 409, "exceeds_attendance", "POST", ProductionBooking, ProductionHours(x.W.Karim, 1), RowRoute(logB, rowB));

        var stored = await x.Db.LaborBookings.AsNoTracking().SingleAsync(b => b.OrderKind == LaborOrderKind.Maintenance);
        Assert.Equal((x.Job, (Guid?)null, (Guid?)null, (Guid?)null, TillHarness.Algiers, 2026, 10),
            (stored.OrderId!.Value, stored.LineId, stored.LogRowId, stored.StageId, stored.PayrollBranchId, stored.PayrollYear, stored.PayrollMonth));

        // The candidates say the same in figures: 8 h shown, 8 booked across both kinds, and Karim already on this job.
        var (status, candidates) = await CallAsync(x, x.W.AdminPrincipal, "GET", Candidates, "?day=2026-10-20", Of(x.Job));
        Assert.Equal(200, status);
        var karim = candidates.EnumerateArray().Single(c => c.GetProperty("employeeId").GetGuid() == x.W.Karim);
        Assert.Equal((8m, 8m, true, false), (karim.GetProperty("attendanceHours").GetDecimal(), karim.GetProperty("bookedHours").GetDecimal(),
            karim.GetProperty("bookedOnJob").GetBoolean(), karim.GetProperty("payrollLocked").GetBoolean()));
    }

    [Fact]
    public async Task Past_attendance_only_with_the_maintenance_override_and_a_reason_that_is_kept()
    {
        var x = await NewAsync();
        await using var _ = x.S;
        await BookedAsync(x, x.W.AdminPrincipal, Book(x.W.Karim, 6));

        // A maintenance hand without the override cannot pass the ceiling, even with a reason.
        var (_, hand) = await ManagerAsync(x.Db, TillHarness.Algiers, PermissionCatalog.MaintenanceView, PermissionCatalog.MaintenanceJobsManage);
        await RefusedAsync(x, hand, 403, MaintenanceLabour.OverrideForbiddenCode, "POST", Hours, Book(x.W.Samir, 3, "Urgent repair"), Of(x.Job));
        // The override holder still says why.
        await RefusedAsync(x, x.W.AdminPrincipal, 409, "exceeds_attendance", "POST", Hours, Book(x.W.Samir, 2), Of(x.Job));
        // The manufacturing override is not the maintenance one.
        var (_, mfg) = await ManagerAsync(x.Db, TillHarness.Algiers, PermissionCatalog.MaintenanceView, PermissionCatalog.MaintenanceJobsManage,
            PermissionCatalog.ManufacturingLabourOverride);
        await RefusedAsync(x, mfg, 403, MaintenanceLabour.OverrideForbiddenCode, "POST", Hours, Book(x.W.Samir, 2, "Declared"), Of(x.Job));

        var (user, overrider) = await ManagerAsync(x.Db, TillHarness.Algiers, PermissionCatalog.MaintenanceView, PermissionCatalog.MaintenanceJobsManage,
            PermissionCatalog.MaintenanceLabourOverride);
        var booked = await BookedAsync(x, overrider, Book(x.W.Samir, 2, "Declared attendance; foreman confirmed 07:00–09:00"));
        var samir = booked.GetProperty("bookings").EnumerateArray().Single(b => b.GetProperty("employeeId").GetGuid() == x.W.Samir);
        Assert.Equal("Declared attendance; foreman confirmed 07:00–09:00", samir.GetProperty("overrideReason").GetString());
        var stored = await x.Db.LaborBookings.AsNoTracking().SingleAsync(b => b.EmployeeId == x.W.Samir);
        Assert.Equal((user.Id, 0m), (stored.OverriddenByUserId!.Value, stored.AttendanceHours));
        // Within attendance, a reason given is not an override.
        Assert.Null((await x.Db.LaborBookings.AsNoTracking().SingleAsync(b => b.EmployeeId == x.W.Karim)).OverrideReason);
    }

    [Fact]
    public async Task A_closed_payroll_month_takes_no_more_hours_and_gives_none_back()
    {
        var x = await NewAsync();
        await using var _ = x.S;
        var booked = await BookedAsync(x, x.W.AdminPrincipal, Book(x.W.Karim, 4));
        var bookingId = booked.GetProperty("bookings")[0].GetProperty("id").GetGuid();
        var before = (await x.Db.AttendanceDays.CountAsync(), await x.Db.OvertimeEntries.CountAsync(), await x.Db.PayrollRuns.CountAsync());

        x.Db.PayrollRuns.Add(new PayrollRun { Number = "PR-2026-10", BranchId = TillHarness.Algiers, PeriodYear = 2026, PeriodMonth = 10, Status = PayrollRunStatus.Locked });
        await x.Db.SaveChangesAsync();
        x.Db.ChangeTracker.Clear();
        await RefusedAsync(x, x.W.AdminPrincipal, 409, "payroll_month_locked", "POST", Hours, Book(x.W.Samir, 1, "late entry"), Of(x.Job));
        await RefusedAsync(x, x.W.AdminPrincipal, 409, "payroll_month_locked", "DELETE", Hour, "", Of(x.Job), ("bookingId", bookingId.ToString()));
        Assert.Equal(1, await x.Db.LaborBookings.CountAsync());
        // Nothing was written into attendance or payroll by the booking.
        Assert.Equal(before, (await x.Db.AttendanceDays.CountAsync(), await x.Db.OvertimeEntries.CountAsync(), await x.Db.PayrollRuns.CountAsync() - 1));
    }

    // ---------- the booking's own rules ----------

    [Fact]
    public async Task One_booking_per_person_job_and_day_on_an_open_job_of_the_branch_and_each_change_is_kept()
    {
        var x = await NewAsync();
        await using var _ = x.S;
        var oran = new Employee { Number = "E-009", FullName = "Nadia Oran", BranchId = TillHarness.Oran, HiredOn = new DateOnly(2025, 1, 1) };
        var late = new Employee { Number = "E-010", FullName = "Yacine Tard", BranchId = TillHarness.Algiers, HiredOn = new DateOnly(2026, 11, 1) };
        x.Db.Employees.AddRange(oran, late);
        await x.Db.SaveChangesAsync();
        x.Db.ChangeTracker.Clear();

        await RefusedAsync(x, x.W.AdminPrincipal, 400, "invalid_hours", "POST", Hours, Book(x.W.Karim, 0), Of(x.Job));
        await RefusedAsync(x, x.W.AdminPrincipal, 400, "invalid_hours", "POST", Hours, Book(x.W.Karim, 25), Of(x.Job));
        await RefusedAsync(x, x.W.AdminPrincipal, 400, "invalid_overtime_hours", "POST", Hours, Book(x.W.Karim, 2, overtime: 3m), Of(x.Job));
        await RefusedAsync(x, x.W.AdminPrincipal, 400, "future_day", "POST", Hours, Book(x.W.Karim, 2, day: "2099-01-01"), Of(x.Job));
        await RefusedAsync(x, x.W.AdminPrincipal, 400, MaintenanceLabour.NotOfBranchCode, "POST", Hours, Book(oran.Id, 2, "x"), Of(x.Job));
        await RefusedAsync(x, x.W.AdminPrincipal, 400, "employee_not_employed", "POST", Hours, Book(late.Id, 2, "x"), Of(x.Job));
        await RefusedAsync(x, x.W.AdminPrincipal, 404, "not_found", "POST", Hours, Book(Guid.NewGuid(), 2), Of(x.Job));

        var first = await BookedAsync(x, x.W.AdminPrincipal, Book(x.W.Karim, 2));
        await RefusedAsync(x, x.W.AdminPrincipal, 409, MaintenanceLabour.AlreadyBookedCode, "POST", Hours, Book(x.W.Karim, 1), Of(x.Job));
        // Another day is another booking.
        await BookedAsync(x, x.W.AdminPrincipal, Book(x.W.Samir, 1, "Declared", day: "2026-10-21"));

        var bookingId = first.GetProperty("bookings").EnumerateArray().Single(b => b.GetProperty("employeeId").GetGuid() == x.W.Karim).GetProperty("id").GetGuid();
        var (status, after) = await CallAsync(x, x.W.AdminPrincipal, "DELETE", Hour, "", Of(x.Job), ("bookingId", bookingId.ToString()));
        Assert.Equal(200, status);
        Assert.Equal(1m, after.GetProperty("totalHours").GetDecimal());
        await RefusedAsync(x, x.W.AdminPrincipal, 404, "not_found", "DELETE", Hour, "", Of(x.Job), ("bookingId", bookingId.ToString()));

        // The log names the job, for the booking made and removed.
        var changes = await x.Db.LaborBookingChanges.AsNoTracking().Where(c => c.BookingId == bookingId).OrderBy(c => c.Action).ToListAsync();
        Assert.Equal([LaborBookingAction.Created, LaborBookingAction.Removed], changes.Select(c => c.Action));
        Assert.All(changes, c => Assert.Equal((LaborOrderKind.Maintenance, (Guid?)x.Job, (Guid?)null, 2m), (c.OrderKind, c.OrderId, c.LogRowId, c.Hours)));

        // A completed job takes no more hours and gives none back.
        var jobs = new MaintenanceJobs(x.Db, new LedgerPoster(x.Db), MaintenanceWorld.Regional);
        Assert.True((await jobs.CompleteAsync(x.Job, new CompleteMaintenanceJobRequest(2m, 0m, null), new MaintenanceActor(Guid.NewGuid(), "chef"))).Ok);
        x.Db.ChangeTracker.Clear();
        await RefusedAsync(x, x.W.AdminPrincipal, 409, MaintenanceLabour.NotOpenCode, "POST", Hours, Book(x.W.Karim, 1), Of(x.Job));
        var samirs = (await x.Db.LaborBookings.AsNoTracking().SingleAsync()).Id;
        await RefusedAsync(x, x.W.AdminPrincipal, 409, MaintenanceLabour.NotOpenCode, "DELETE", Hour, "", Of(x.Job), ("bookingId", samirs.ToString()));
        // Its hours are still read.
        var (read, hours) = await CallAsync(x, x.W.AdminPrincipal, "GET", Hours, "", Of(x.Job));
        Assert.Equal((200, 1m, "Completed"), (read, hours.GetProperty("totalHours").GetDecimal(), hours.GetProperty("jobStatus").GetString()));
    }

    [Fact]
    public async Task Another_branch_s_job_is_not_found_and_the_module_off_answers_maintenance_off()
    {
        var x = await NewAsync();
        await using var _ = x.S;
        var (_, oran) = await ManagerAsync(x.Db, TillHarness.Oran, PermissionCatalog.MaintenanceView, PermissionCatalog.MaintenanceJobsManage,
            PermissionCatalog.MaintenanceLabourOverride);
        // Answered as if the job did not exist, before the service is reached.
        Assert.Equal(404, (await CallAsync(x, oran, "GET", Hours, "", Of(x.Job))).Status);
        Assert.Equal(404, (await CallAsync(x, oran, "POST", Hours, Book(x.W.Karim, 1), Of(x.Job))).Status);
        Assert.Equal(404, (await CallAsync(x, oran, "GET", Candidates, "", Of(x.Job))).Status);
        Assert.Equal(0, await x.Db.LaborBookings.CountAsync());

        var settings = await x.Db.MaintenanceSettings.SingleAsync();
        settings.Module = MaintenanceSwitch.Off;
        await x.Db.SaveChangesAsync();
        x.Db.ChangeTracker.Clear();
        await RefusedAsync(x, x.W.AdminPrincipal, 409, MaintenanceJobs.OffCode, "POST", Hours, Book(x.W.Karim, 1), Of(x.Job));
        await RefusedAsync(x, x.W.AdminPrincipal, 409, MaintenanceJobs.OffCode, "GET", Hours, "", Of(x.Job));
    }

    [Fact]
    public async Task MS_04_any_employee_of_the_branch_is_offered_by_default_and_the_workshop_list_is_not_read()
    {
        var x = await NewAsync();
        await using var _ = x.S;
        x.Db.Employees.Add(new Employee { Number = "E-009", FullName = "Nadia Oran", BranchId = TillHarness.Oran, HiredOn = new DateOnly(2025, 1, 1) });
        // A list holding only Karim changes nothing while MS-04 says "any employee of the job's branch".
        x.Db.MaintenanceWorkshopMembers.Add(new MaintenanceWorkshopMember { EmployeeId = x.W.Karim, BranchId = TillHarness.Algiers });
        await x.Db.SaveChangesAsync();
        x.Db.ChangeTracker.Clear();
        var (_, candidates) = await CallAsync(x, x.W.AdminPrincipal, "GET", Candidates, "?day=2026-10-20", Of(x.Job));
        Assert.Equal(["Karim Benali", "Samir Haddad"], candidates.EnumerateArray().Select(c => c.GetProperty("fullName").GetString()));
        var samir = candidates.EnumerateArray().Single(c => c.GetProperty("employeeId").GetGuid() == x.W.Samir);
        Assert.Equal(JsonValueKind.Null, samir.GetProperty("attendanceHours").ValueKind);
        await BookedAsync(x, x.W.AdminPrincipal, Book(x.W.Samir, 1, "Declared"));
    }

    [Fact]
    public async Task MS_04_only_the_workshop_list_offers_and_books_only_those_on_the_job_s_branch_s_list()
    {
        var x = await NewAsync(s => s.BookableEmployees = MaintenanceBookable.WorkshopListOnly);
        await using var _ = x.S;
        var nadia = new Employee { Number = "E-009", FullName = "Nadia Oran", BranchId = TillHarness.Oran, HiredOn = new DateOnly(2025, 1, 1) };
        x.Db.Employees.Add(nadia);
        await x.Db.SaveChangesAsync();
        x.Db.ChangeTracker.Clear();
        var admin = x.W.AdminPrincipal;
        async Task<string?[]> OfferedAsync(string day = "2026-10-20") =>
            (await CallAsync(x, admin, "GET", Candidates, "?day=" + day, Of(x.Job))).Body.EnumerateArray().Select(c => c.GetProperty("fullName").GetString()).ToArray();

        // An empty list: nobody offered, everyone refused in words.
        Assert.Empty(await OfferedAsync());
        await RefusedAsync(x, admin, 400, MaintenanceWorkshop.NotOnListCode, "POST", Hours, Book(x.W.Karim, 1), Of(x.Job));

        // Karim on Algiers' list: offered and booked; Samir, of the branch but not on it, still refused.
        Assert.Equal(200, (await CallAsync(x, admin, "POST", Workshop, JsonSerializer.Serialize(new { employeeId = x.W.Karim }))).Status);
        Assert.Equal(["Karim Benali"], await OfferedAsync());
        await BookedAsync(x, admin, Book(x.W.Karim, 2));
        await RefusedAsync(x, admin, 400, MaintenanceWorkshop.NotOnListCode, "POST", Hours, Book(x.W.Samir, 1, "Declared"), Of(x.Job));

        // Someone on another branch's list: Oran's Nadia is not of the job's branch, whatever her list says.
        Assert.Equal(200, (await CallAsync(x, admin, "POST", Workshop, JsonSerializer.Serialize(new { employeeId = nadia.Id }))).Status);
        Assert.DoesNotContain("Nadia Oran", await OfferedAsync());
        await RefusedAsync(x, admin, 400, MaintenanceLabour.NotOfBranchCode, "POST", Hours, Book(nadia.Id, 1, "x"), Of(x.Job));
        // Moved to Algiers, her row is still Oran's: of the branch now, but not on its list.
        var moved = await x.Db.Employees.SingleAsync(e => e.Id == nadia.Id);
        moved.BranchId = TillHarness.Algiers;
        await x.Db.SaveChangesAsync();
        x.Db.ChangeTracker.Clear();
        Assert.Equal(["Karim Benali"], await OfferedAsync());
        await RefusedAsync(x, admin, 400, MaintenanceWorkshop.NotOnListCode, "POST", Hours, Book(nadia.Id, 1, "x"), Of(x.Job));

        // Removed from the list: no longer offered or booked; the hours booked before stay.
        var row = await x.Db.MaintenanceWorkshopMembers.AsNoTracking().SingleAsync(m => m.EmployeeId == x.W.Karim);
        Assert.Equal(200, (await CallAsync(x, admin, "DELETE", Workshop + "/{id:guid}", "", Of(row.Id))).Status);
        Assert.Empty(await OfferedAsync("2026-10-21"));
        await RefusedAsync(x, admin, 400, MaintenanceWorkshop.NotOnListCode, "POST", Hours, Book(x.W.Karim, 1, day: "2026-10-21"), Of(x.Job));
        Assert.Equal(2m, (await CallAsync(x, admin, "GET", Hours, "", Of(x.Job))).Body.GetProperty("totalHours").GetDecimal());

        // Back to the default: anyone of the branch again, the list kept but not read.
        var settings = await x.Db.MaintenanceSettings.SingleAsync();
        settings.BookableEmployees = MaintenanceBookable.AnyEmployeeOfBranch;
        await x.Db.SaveChangesAsync();
        x.Db.ChangeTracker.Clear();
        Assert.Equal(["Karim Benali", "Nadia Oran", "Samir Haddad"], await OfferedAsync("2026-10-21"));
    }

    // ---------- production's readers ----------

    [Fact]
    public async Task Maintenance_hours_stay_out_of_production_figures_labour_absorption_and_the_daily_record()
    {
        var x = await NewAsync();
        await using var _ = x.S;
        var (log, row) = await ProductionRowAsync(x, x.W.LineA);
        await OkAsync(x.Db, x.W.AdminPrincipal, "POST", ProductionBooking, ProductionHours(x.W.Karim, 5), RowRoute(log, row));
        var order = (await x.Db.ProductionLogRows.AsNoTracking().SingleAsync(r => r.Id == row)).OrderId!.Value;
        await BookedAsync(x, x.W.AdminPrincipal, Book(x.W.Karim, 3));

        var report = await OkAsync(x.Db, x.W.AdminPrincipal, "GET", "/api/manufacturing/reports/labour-absorption", "?year=2026&month=10");
        Assert.Equal(5m, report.GetProperty("orderHours").GetDecimal() + report.GetProperty("otherHours").GetDecimal());
        Assert.Equal(5m, report.GetProperty("rows")[0].GetProperty("orderHours").GetDecimal());

        var record = await OkAsync(x.Db, x.W.AdminPrincipal, "GET", "/api/manufacturing/logs/{id:guid}", "", Of(log));
        Assert.Equal(5m, record.GetProperty("log").GetProperty("hours").GetDecimal());
        Assert.Single(record.GetProperty("rows")[0].GetProperty("bookings").EnumerateArray());

        var cost = await OkAsync(x.Db, x.W.AdminPrincipal, "GET", "/api/manufacturing/orders/{id:guid}/cost-inputs", "", Of(order));
        Assert.Equal(5m, cost.GetProperty("labour").EnumerateArray().Sum(l => l.GetProperty("hours").GetDecimal()));
    }

    // ---------- the migration ----------

    [Fact]
    public async Task The_migration_keeps_every_booking_and_change_row_on_SQLite()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await MaintenanceLabourMigration.SeedBeforeAsync(db, MaintenanceLabourMigration.SqliteBefore);

        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await MaintenanceLabourMigration.AssertKeptAsync(db);
    }
}
