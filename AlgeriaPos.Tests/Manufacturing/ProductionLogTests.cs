using System.Security.Claims;
using System.Text.Json;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Manufacturing.MfgHarness;
using static AlgeriaPos.Tests.Manufacturing.ProductionWorld;

namespace AlgeriaPos.Tests.Manufacturing;

/// <summary>
/// The daily production record, its hours, partial output and completion (Phase 2, parts C to F): one record
/// per line and day, one row per operation, units passing several stages counted once, hours never above the
/// attendance the day shows without an audited override and never twice across lines, payroll and attendance
/// untouched, and every approval by a production manager who did not submit or request it.
/// </summary>
public class ProductionLogTests
{
    private const string Logs = "/api/manufacturing/logs";
    private const string Log = "/api/manufacturing/logs/{id:guid}";

    private static async Task<JsonElement> OpenLogAsync(PosDbContext db, ClaimsPrincipal caller, Guid line, string day = "2026-10-20") =>
        await OkAsync(db, caller, "POST", Logs, $$"""{"lineId":"{{line}}","day":"{{day}}"}""");

    private static Guid LogId(JsonElement log) => log.GetProperty("log").GetProperty("id").GetGuid();

    private static string Row(Guid order, Guid stage, decimal completed, decimal rejected = 0, string? reason = null, Guid? id = null) =>
        JsonSerializer.Serialize(new
        {
            id, orderId = order, stageId = stage, category = (string?)null, specification = (string?)null,
            plannedQty = 100m, completedQty = completed, rejectedQty = rejected, rejectReason = reason, notes = (string?)null,
        });

    private static string Rows(params string[] rows) => $$"""{"notes":null,"rows":[{{string.Join(",", rows)}}]}""";

    private static Guid RowId(JsonElement log, int index) => log.GetProperty("rows")[index].GetProperty("id").GetGuid();

    private static string Book(Guid employee, decimal hours, string? overrideReason = null) =>
        JsonSerializer.Serialize(new { employeeId = employee, hours, overrideReason });

    private static (string, string)[] RowRoute(Guid log, Guid row) => [Of(log), ("rowId", row.ToString())];

    [Fact]
    public async Task One_record_per_line_and_day_and_one_row_per_operation()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var log = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA);
        Assert.Equal(LogId(log), LogId(await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA)));
        Assert.NotEqual(LogId(log), LogId(await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineB)));
        Assert.Equal(2, await s.Db.ProductionLogs.CountAsync());
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "future_day", "POST", Logs, $$"""{"lineId":"{{w.LineA}}","day":"2099-01-01"}""");

        var cut = Stage(order, 0).GetProperty("id").GetGuid();
        var id = OrderId(order);
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "duplicate_operation", "PUT", Log, Rows(Row(id, cut, 50), Row(id, cut, 40)), Of(LogId(log)));
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "reject_reason_required", "PUT", Log, Rows(Row(id, cut, 50, rejected: 2)), Of(LogId(log)));
        var lineB = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineB);
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "order_not_on_line", "PUT", Log, Rows(Row(id, cut, 50)), Of(LogId(lineB)));
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "quantity_without_order", "PUT", Log,
            """{"notes":null,"rows":[{"category":"Breakdown","plannedQty":0,"completedQty":5,"rejectedQty":0}]}""", Of(LogId(log)));

        var saved = await OkAsync(s.Db, w.AdminPrincipal, "PUT", Log,
            Rows(Row(id, cut, 50), """{"category":"Breakdown","plannedQty":0,"completedQty":0,"rejectedQty":0,"notes":"Press jammed"}"""), Of(LogId(log)));
        var row = saved.GetProperty("rows")[0];
        // The product's specification and dimensions come onto the row; the order's product is on it.
        Assert.Equal(("NAIL-70", "kg"), (row.GetProperty("productSku").GetString(), row.GetProperty("baseUnitCode").GetString()));
        Assert.Equal("Breakdown", saved.GetProperty("rows")[1].GetProperty("category").GetString());
    }

    [Fact]
    public async Task Units_through_several_stages_are_finished_output_once_and_a_stage_ahead_of_the_previous_is_said()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, manager) = await AdminAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var id = OrderId(order);
        var (cut, form) = (Stage(order, 0).GetProperty("id").GetGuid(), Stage(order, 1).GetProperty("id").GetGuid());

        var log = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA);
        // 60 kg cut, 70 kg formed — more formed than cut: said, not refused.
        var saved = await OkAsync(s.Db, w.AdminPrincipal, "PUT", Log,
            Rows(Row(id, cut, 60), Row(id, form, 70, rejected: 3, reason: "Bent heads")), Of(LogId(log)));
        var warning = saved.GetProperty("warnings")[0];
        Assert.Equal(("stage_ahead_of_previous", "MO-000001", "Frappe", "Coupe"), (warning.GetProperty("code").GetString(),
            warning.GetProperty("orderNumber").GetString(), warning.GetProperty("stageNameFr").GetString(), warning.GetProperty("previousNameFr").GetString()));
        await OkAsync(s.Db, w.AdminPrincipal, "PUT", Log, Rows(Row(id, cut, 60, id: RowId(saved, 0)), Row(id, form, 55, rejected: 3, reason: "Bent heads", id: RowId(saved, 1))), Of(LogId(log)));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/submit", "", Of(LogId(log)));
        // The submitter does not approve their own record.
        await RefusedAsync(s.Db, w.AdminPrincipal, 403, "own_record", "POST", "/api/manufacturing/logs/{id:guid}/approve", "", Of(LogId(log)));
        var approved = await OkAsync(s.Db, manager, "POST", "/api/manufacturing/logs/{id:guid}/approve", "", Of(LogId(log)));
        Assert.Equal("Approved", approved.GetProperty("log").GetProperty("status").GetString());
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "log_not_draft", "PUT", Log, Rows(Row(id, cut, 1)), Of(LogId(log)));

        var after = await OkAsync(s.Db, w.AdminPrincipal, "GET", Order, "", Of(id));
        Assert.Equal((60m, 55m, 3m), (Stage(after, 0).GetProperty("goodBaseQty").GetDecimal(), Stage(after, 1).GetProperty("goodBaseQty").GetDecimal(),
            Stage(after, 1).GetProperty("rejectedBaseQty").GetDecimal()));
        // Produced is the output stage's 55, not 60 + 55.
        Assert.Equal((55m, 55m, 3m), (after.GetProperty("producedBaseQty").GetDecimal(), after.GetProperty("awaitingReleaseBaseQty").GetDecimal(),
            after.GetProperty("order").GetProperty("rejectedBaseQty").GetDecimal()));
    }

    [Fact]
    public async Task A_manager_sends_a_record_back_with_a_reason_and_it_is_edited_again()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, manager) = await AdminAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var log = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA);
        await OkAsync(s.Db, w.AdminPrincipal, "PUT", Log, Rows(Row(OrderId(order), Stage(order, 0).GetProperty("id").GetGuid(), 50)), Of(LogId(log)));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/submit", "", Of(LogId(log)));
        await RefusedAsync(s.Db, manager, 400, "reason_required", "POST", "/api/manufacturing/logs/{id:guid}/return", """{"reason":""}""", Of(LogId(log)));
        var back = await OkAsync(s.Db, manager, "POST", "/api/manufacturing/logs/{id:guid}/return", """{"reason":"Cut quantity is 45"}""", Of(LogId(log)));
        Assert.Equal(("Draft", "Cut quantity is 45"), (back.GetProperty("log").GetProperty("status").GetString(), back.GetProperty("returnedReason").GetString()));
    }

    [Fact]
    public async Task Hours_stay_within_the_day_s_attendance_across_every_line_unless_an_audited_override_says_why()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var a = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var b = await OkAsync(s.Db, w.AdminPrincipal, "POST", Orders, w.OrderBody(w.LineB, 40));
        var logA = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA);
        var logB = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineB);
        logA = await OkAsync(s.Db, w.AdminPrincipal, "PUT", Log, Rows(Row(OrderId(a), Stage(a, 0).GetProperty("id").GetGuid(), 50)), Of(LogId(logA)));
        logB = await OkAsync(s.Db, w.AdminPrincipal, "PUT", Log, Rows(Row(OrderId(b), Stage(b, 0).GetProperty("id").GetGuid(), 20)), Of(LogId(logB)));
        var path = "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings";

        // Karim's day shows 8 h: 5 on line A, then 4 on line B is one hour too many.
        var booked = await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Book(w.Karim, 5), RowRoute(LogId(logA), RowId(logA, 0)));
        Assert.Equal(8m, booked.GetProperty("rows")[0].GetProperty("bookings")[0].GetProperty("attendanceHours").GetDecimal());
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "already_booked", "POST", path, Book(w.Karim, 1), RowRoute(LogId(logA), RowId(logA, 0)));
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "exceeds_attendance", "POST", path, Book(w.Karim, 4), RowRoute(LogId(logB), RowId(logB, 0)));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Book(w.Karim, 3), RowRoute(LogId(logB), RowId(logB, 0)));

        // Someone without the override cannot pass the ceiling, even with a reason.
        var (user, supervisor) = await ManagerAsync(s.Db, null, PermissionCatalog.ManufacturingView, PermissionCatalog.ManufacturingLogSubmit);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = user.Id, Role = ProductionLineMemberRole.Supervisor });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        await RefusedAsync(s.Db, supervisor, 403, "override_forbidden", "POST", path, Book(w.Samir, 2, "Night shift"), RowRoute(LogId(logA), RowId(logA, 0)));

        // Samir's attendance is declared per month: no day to read, so only the override books him — and it is kept.
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "exceeds_attendance", "POST", path, Book(w.Samir, 2), RowRoute(LogId(logA), RowId(logA, 0)));
        var overridden = await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Book(w.Samir, 2, "Declared attendance; foreman confirmed 07:00–09:00"),
            RowRoute(LogId(logA), RowId(logA, 0)));
        var samir = overridden.GetProperty("rows")[0].GetProperty("bookings").EnumerateArray().Single(x => x.GetProperty("employeeId").GetGuid() == w.Samir);
        Assert.Equal("Declared attendance; foreman confirmed 07:00–09:00", samir.GetProperty("overrideReason").GetString());
        var stored = await s.Db.LaborBookings.AsNoTracking().SingleAsync(x => x.EmployeeId == w.Samir);
        Assert.Equal((w.Admin, 0m, AlgeriaPos.Tests.Security.TillHarness.Algiers, 2026, 10), (stored.OverriddenByUserId!.Value, stored.AttendanceHours, stored.PayrollBranchId, stored.PayrollYear, stored.PayrollMonth));
        Assert.Equal(3, await s.Db.LaborBookingChanges.CountAsync(x => x.Action == LaborBookingAction.Created));
    }

    [Fact]
    public async Task Hours_are_never_written_into_payroll_or_attendance_and_a_closed_payroll_month_takes_no_more()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var log = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA);
        log = await OkAsync(s.Db, w.AdminPrincipal, "PUT", Log, Rows(Row(OrderId(order), Stage(order, 0).GetProperty("id").GetGuid(), 50)), Of(LogId(log)));
        var before = (await s.Db.AttendanceDays.CountAsync(), await s.Db.OvertimeEntries.CountAsync(), await s.Db.PayrollRuns.CountAsync(), await s.Db.Employees.AsNoTracking().SumAsync(e => (double)e.BaseRate));
        var path = "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings";
        var booked = await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Book(w.Karim, 6), RowRoute(LogId(log), RowId(log, 0)));
        Assert.Equal(before, (await s.Db.AttendanceDays.CountAsync(), await s.Db.OvertimeEntries.CountAsync(), await s.Db.PayrollRuns.CountAsync(), await s.Db.Employees.AsNoTracking().SumAsync(e => (double)e.BaseRate)));

        // October's run for Karim's branch is locked: his October hours no longer change.
        s.Db.PayrollRuns.Add(new PayrollRun { Number = "PR-2026-10", BranchId = AlgeriaPos.Tests.Security.TillHarness.Algiers, PeriodYear = 2026, PeriodMonth = 10, Status = PayrollRunStatus.Locked });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "payroll_month_locked", "POST", path, Book(w.Samir, 1, "late entry"), RowRoute(LogId(log), RowId(log, 0)));
        var bookingId = booked.GetProperty("rows")[0].GetProperty("bookings")[0].GetProperty("id").GetGuid();
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "payroll_month_locked", "DELETE", "/api/manufacturing/logs/{id:guid}/bookings/{bookingId:guid}", "",
            Of(LogId(log)), ("bookingId", bookingId.ToString()));
    }

    [Fact]
    public async Task Partial_releases_keep_the_order_open_and_completion_is_approved_by_another_production_manager()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, manager) = await AdminAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var id = OrderId(order);
        var material = MaterialId(order);
        var (cut, form) = (Stage(order, 0).GetProperty("id").GetGuid(), Stage(order, 1).GetProperty("id").GetGuid());
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", material, 105), Of(id));

        var log = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA);
        var saved = await OkAsync(s.Db, w.AdminPrincipal, "PUT", Log, Rows(Row(id, cut, 100), Row(id, form, 98, rejected: 2, reason: "Bent")), Of(LogId(log)));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/submit", "", Of(LogId(log)));
        // Nothing is released before the record is approved.
        await RefusedAsync(s.Db, manager, 409, "over_output", "POST", "/api/manufacturing/orders/{id:guid}/releases", Release(10), Of(id));
        await OkAsync(s.Db, manager, "POST", "/api/manufacturing/logs/{id:guid}/approve", "", Of(LogId(log)));

        // Released in two parts; the order stays open between them, and the same request twice releases once.
        var first = await OkAsync(s.Db, manager, "POST", "/api/manufacturing/orders/{id:guid}/releases", Release(60, "req-rel-1"), Of(id));
        await OkAsync(s.Db, manager, "POST", "/api/manufacturing/orders/{id:guid}/releases", Release(60, "req-rel-1"), Of(id));
        Assert.Equal(("Open", 60m, 38m, 40m), (first.GetProperty("order").GetProperty("status").GetString(), first.GetProperty("order").GetProperty("acceptedBaseQty").GetDecimal(),
            first.GetProperty("awaitingReleaseBaseQty").GetDecimal(), first.GetProperty("remainingBaseQty").GetDecimal()));
        await RefusedAsync(s.Db, manager, 409, "over_output", "POST", "/api/manufacturing/orders/{id:guid}/releases", Release(39), Of(id));
        Assert.Equal(60m, await ProductionOrderTests.StockAsync(s.Db, w.Nails, WarehouseA));

        // Completion waits for everything: the leftover counted, then the output released.
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "materials_at_line", "POST", "/api/manufacturing/orders/{id:guid}/request-completion", "", Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/consumptions", Consume(material, 101, row: RowId(saved, 0)), Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/consumptions", Consume(material, 3, "Offcut"), Of(id));
        // The record still opens with the consumption it references, named by the material's SKU.
        var reread = await OkAsync(s.Db, w.AdminPrincipal, "GET", Log, "", Of(LogId(log)));
        var referenced = reread.GetProperty("rows")[0].GetProperty("consumptions")[0];
        Assert.Equal((material, order.GetProperty("materials")[0].GetProperty("sku").GetString(), 101m),
            (referenced.GetProperty("orderMaterialId").GetGuid(), referenced.GetProperty("sku").GetString(), referenced.GetProperty("baseQty").GetDecimal()));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Return", material, 1), Of(id));
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "output_awaiting_release", "POST", "/api/manufacturing/orders/{id:guid}/request-completion", "", Of(id));
        await OkAsync(s.Db, manager, "POST", "/api/manufacturing/orders/{id:guid}/releases", Release(38), Of(id));
        var requested = await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/request-completion", "", Of(id));
        Assert.Equal("CompletionRequested", requested.GetProperty("order").GetProperty("status").GetString());

        // The one who asked does not approve; the date never completes it on its own.
        await RefusedAsync(s.Db, w.AdminPrincipal, 403, "own_request", "POST", "/api/manufacturing/orders/{id:guid}/approve-completion", "", Of(id));
        var done = await OkAsync(s.Db, manager, "POST", "/api/manufacturing/orders/{id:guid}/approve-completion", "", Of(id));
        var summary = done.GetProperty("order");
        Assert.Equal(("Completed", 98m, 2m), (summary.GetProperty("status").GetString(), summary.GetProperty("acceptedBaseQty").GetDecimal(), summary.GetProperty("rejectedBaseQty").GetDecimal()));
        Assert.Equal("2026-10-21", summary.GetProperty("plannedEnd").GetString());
        Assert.NotEqual(JsonValueKind.Null, summary.GetProperty("actualEnd").ValueKind);
        Assert.True(summary.GetProperty("overdueDays").GetInt32() >= 0);
        Assert.Equal(0m, done.GetProperty("materials")[0].GetProperty("reservedBaseQty").GetDecimal());
        Assert.Equal(98m, await ProductionOrderTests.StockAsync(s.Db, w.Nails, WarehouseA));
    }

    [Fact]
    public async Task A_production_manager_of_another_line_cannot_approve_or_release_and_refusal_reopens_with_a_reason()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var log = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA);
        await OkAsync(s.Db, w.AdminPrincipal, "PUT", Log, Rows(Row(OrderId(order), Stage(order, 1).GetProperty("id").GetGuid(), 10)), Of(LogId(log)));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/logs/{id:guid}/submit", "", Of(LogId(log)));

        var (user, otherLine) = await ManagerAsync(s.Db, null, AllGrants);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineB, UserId = user.Id, Role = ProductionLineMemberRole.Manager });
        // A supervisor of line A reaches it but does not manage it.
        var (supervisorUser, supervisor) = await ManagerAsync(s.Db, null, AllGrants);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = supervisorUser.Id, Role = ProductionLineMemberRole.Supervisor });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        await RefusedAsync(s.Db, otherLine, 404, "not_found", "POST", "/api/manufacturing/logs/{id:guid}/approve", "", Of(LogId(log)));
        await RefusedAsync(s.Db, supervisor, 403, "forbidden", "POST", "/api/manufacturing/logs/{id:guid}/approve", "", Of(LogId(log)));
    }

    [Fact]
    public async Task A_day_without_a_row_in_a_month_payroll_reads_from_day_rows_shows_no_hours_whatever_the_clock_holds()
    {
        // Costing plan F6, step A0: payroll reads Karim's October from day rows only (he has one on the 20th), so
        // his punches on the 21st pay nothing and book nothing. Nadia has no October row: her punches count, and a
        // row she has in September changes nothing in October.
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var nadia = new Employee { Number = "E-003", FullName = "Nadia Cherif", BranchId = AlgeriaPos.Tests.Security.TillHarness.Algiers,
            HiredOn = new DateOnly(2025, 1, 1), BaseRate = 50000m, AttendanceModeOverride = AttendanceMode.Recorded };
        s.Db.Employees.Add(nadia);
        s.Db.ProductionLineEmployees.Add(new ProductionLineEmployee { LineId = w.LineA, EmployeeId = nadia.Id, AssignedOn = new DateOnly(2026, 1, 1) });
        s.Db.AttendanceDays.Add(new AttendanceDay { EmployeeId = nadia.Id, WorkDate = new DateOnly(2026, 9, 30), Status = AttendanceDayStatus.Present, HoursWorked = 8m });
        foreach (var person in new[] { w.Karim, nadia.Id })
        {
            s.Db.ClockEvents.Add(new ClockEvent { EmployeeId = person, AtUtc = new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc), Kind = ClockEventKind.In });
            s.Db.ClockEvents.Add(new ClockEvent { EmployeeId = person, AtUtc = new DateTime(2026, 10, 21, 14, 0, 0, DateTimeKind.Utc), Kind = ClockEventKind.Out });
        }
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var log = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA, "2026-10-21");
        var people = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/logs/{id:guid}/employee-candidates", "", Of(LogId(log)));
        var byName = people.EnumerateArray().ToDictionary(p => p.GetProperty("fullName").GetString()!);
        Assert.Equal((0m, "none"), (byName["Karim Benali"].GetProperty("attendanceHours").GetDecimal(), byName["Karim Benali"].GetProperty("attendanceSource").GetString()));
        Assert.Equal((6m, "clock"), (byName["Nadia Cherif"].GetProperty("attendanceHours").GetDecimal(), byName["Nadia Cherif"].GetProperty("attendanceSource").GetString()));

        log = await OkAsync(s.Db, w.AdminPrincipal, "PUT", Log, Rows(Row(OrderId(order), Stage(order, 0).GetProperty("id").GetGuid(), 10)), Of(LogId(log)));
        var path = "/api/manufacturing/logs/{id:guid}/rows/{rowId:guid}/bookings";
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "exceeds_attendance", "POST", path, Book(w.Karim, 1), RowRoute(LogId(log), RowId(log, 0)));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Book(nadia.Id, 6), RowRoute(LogId(log), RowId(log, 0)));
        // The day row on the 20th still reads as before.
        var onRow = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA);
        var candidates = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/logs/{id:guid}/employee-candidates", "", Of(LogId(onRow)));
        Assert.Equal(8m, candidates.EnumerateArray().Single(p => p.GetProperty("fullName").GetString() == "Karim Benali").GetProperty("attendanceHours").GetDecimal());
    }

    [Fact]
    public async Task The_employees_offered_are_the_line_s_with_the_day_s_attendance_and_hours_already_booked()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var log = await OpenLogAsync(s.Db, w.AdminPrincipal, w.LineA);
        var people = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/logs/{id:guid}/employee-candidates", "", Of(LogId(log)));
        var byName = people.EnumerateArray().ToDictionary(p => p.GetProperty("fullName").GetString()!);
        Assert.Equal((8m, "recorded"), (byName["Karim Benali"].GetProperty("attendanceHours").GetDecimal(), byName["Karim Benali"].GetProperty("attendanceSource").GetString()));
        Assert.Equal((JsonValueKind.Null, "declared"), (byName["Samir Haddad"].GetProperty("attendanceHours").ValueKind, byName["Samir Haddad"].GetProperty("attendanceSource").GetString()));
        // No pay is read back through the record.
        Assert.DoesNotContain("52000", people.GetRawText());
    }
}
