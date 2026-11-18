using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Catalog;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Payroll;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Hr;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Attendance: the sheet a foreman fills, the day grid behind a recorded month, and the clock.
///
/// <para>
/// <b>The one group in this module that is not <c>payroll.manage</c> alone.</b> These routes take
/// <c>payroll.manage</c> <i>or</i> <c>hr.attendance</c>, which is the whole reason the second
/// permission exists (docs/07 §5.11): the man who counts the crew each morning must be able to
/// submit days, and must not be able to read what anybody earns. Nothing here returns a wage —
/// the sheet carries days, hours and a pay <i>basis</i> in words, never a rate.
/// </para>
/// <para>
/// A period whose payroll run is Locked, Approved or Paid refuses every write with 409 and the
/// run's number, so the screen can say which run to unlock. A Draft run locks nothing: entering
/// the last of the days after a first look at the figures is ordinary work, and the run
/// recomputes.
/// </para>
/// </summary>
public static class HrAttendanceEndpoints
{
    public static void MapHrAttendanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hr").WithTags("HR")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        // payroll.manage or hr.attendance opens every route here (RoutePermissions).
        var attendance = group.MapGroup("/attendance");

        MapSheet(attendance);
        MapDays(attendance);
        MapClock(attendance);
        MapImport(attendance);
        MapReview(attendance);
    }

    /// <summary>
    /// A month of punches read against the working day — late, early, a missing exit, possible
    /// overtime (owner, 2026-10-10). Reads only. <c>flagged</c> keeps the days with something to say.
    /// </summary>
    private static void MapReview(RouteGroupBuilder attendance)
    {
        attendance.MapGet("/review", async (
            Guid? employeeId, Guid? branchId, int year, int month, bool? flagged,
            AttendanceReview review, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            // A named employee takes precedence over the branch in the review's query, so the
            // branch lock alone let another branch's punches through (P0.1).
            if (employeeId is Guid one && await HrHttp.ForeignEmployeesAsync(http, db, [one], ct)) return Results.NotFound();
            var result = await review.ReadAsync(employeeId, branchId, year, month, flagged ?? false, ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        });
        PeoplePageRoutes.MapReviewPage(attendance);
    }

    /// <summary>A month of a terminal's punches is a few thousand rows of a few short cells.</summary>
    private const long MaxPunchFileBytes = 10L * 1024 * 1024;

    /// <summary>One punch a row: a large branch's month runs past the stock sheet's 10,000.</summary>
    private const int MaxPunchRows = 60_000;

    private const string PunchFileTooLarge =
        "The workbook is larger than 10 MB. Keep only the sheet with the punches on it.";

    /// <summary>
    /// FR-3B: the fingerprint terminal's export, read and shown — <b>nothing is written</b>. The
    /// file is sent again with every change of sheet, header row or column, and read afresh; no
    /// copy is kept on the server. Multipart read by hand, as the opening-stock preview does: a
    /// bound form file asks for antiforgery tokens this bearer-token API has no use for.
    /// <c>branchId</c> is the caller's own for anyone but the Admin (CallersBranch).
    /// </summary>
    private static void MapImport(RouteGroupBuilder attendance)
    {
        attendance.MapPost("/import/preview", async (
            Guid branchId, HttpRequest request, PunchImportPreview preview, CancellationToken ct) =>
        {
            if (request.ContentLength is long declared && declared > MaxPunchFileBytes + 64 * 1024)
                return Results.BadRequest(PunchFileTooLarge);
            if (!request.HasFormContentType)
                return Results.BadRequest("Send the workbook as multipart/form-data, one file per request.");

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count != 1) return Results.BadRequest("Send exactly one file.");
            var file = form.Files[0];
            if (file.Length > MaxPunchFileBytes) return Results.BadRequest(PunchFileTooLarge);

            int? Column(string key) =>
                int.TryParse(form[key].ToString(), out var n) && n > 0 ? n : null;
            var mapping = new PunchMapping(
                Column("number"), Column("name"), Column("date"), Column("time"), Column("dateTime"), Column("marker"),
                form["order"].ToString() switch
                {
                    "mdy" => DateOrder.MonthDayYear,
                    "ymd" => DateOrder.YearMonthDay,
                    _ => DateOrder.DayMonthYear
                },
                TimeOut: Column("timeOut"));
            int earlyHour = int.TryParse(form["earlyHour"].ToString(), out var eh) && eh is >= 0 and <= 12
                ? eh
                : PunchSheet.DefaultEarlyHour;
            // Absent: take the suggestion. "0": the file has no header row.
            int? header = int.TryParse(form["header"].ToString(), out var h) ? h : null;

            try
            {
                await using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                buffer.Position = 0;
                var sheet = XlsxReader.Read(
                    buffer, form["sheet"].ToString() is { Length: > 0 } named ? named : null, MaxPunchRows);
                var result = await preview.PreviewAsync(branchId, Path.GetFileName(file.FileName), sheet, header, mapping, earlyHour, ct);
                return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
            }
            catch (SpreadsheetException e)
            {
                return Results.BadRequest(e.Message);
            }
        });
    }

    private static void MapSheet(RouteGroupBuilder attendance)
    {
        attendance.MapGet("/sheet", async (
            Guid branchId, int year, int month, int? skip, int? take, string? q,
            AttendanceSheet sheet, CancellationToken ct) =>
        {
            var result = await sheet.ReadSheetAsync(branchId, year, month, skip ?? 0, take ?? 100, q, ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        });

        attendance.MapPut("/sheet", async (
            UpsertAttendanceSheetRequest body, AttendanceSheet sheet, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("A sheet is required.");
            // The sheet refuses another branch's employee in words of its own, which told a branch
            // user that the person exists (P0.1): not found first, as for a number nobody holds.
            if (await HrHttp.ForeignEmployeesAsync(http, db, (body.Rows ?? []).Select(r => r.EmployeeId), ct))
                return Results.NotFound();
            // One upsert per row in one scope, and one refusal for the whole request: a
            // half-written sheet would leave the clerk guessing which rows to retype.
            var result = await sheet.SaveSheetAsync(body, HrHttp.UserName(http), ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        });
    }

    private static void MapDays(RouteGroupBuilder attendance)
    {
        attendance.MapGet("/days", async (
            Guid employeeId, int year, int month, AttendanceSheet sheet, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            if (await HrHttp.ForeignEmployeesAsync(http, db, [employeeId], ct)) return Results.NotFound();
            var result = await sheet.ReadDaysAsync(employeeId, year, month, ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        });

        attendance.MapPut("/days", async (
            UpsertAttendanceDaysRequest body, AttendanceSheet sheet, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("A month is required.");
            if (await HrHttp.ForeignEmployeesAsync(http, db, (body.Days ?? []).Select(d => d.EmployeeId), ct)) return Results.NotFound();
            var result = await sheet.SaveDaysAsync(body, HrHttp.UserName(http), ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        });
    }

    private static void MapClock(RouteGroupBuilder attendance)
    {
        attendance.MapPost("/clock", async (
            ClockPunchRequest body, AttendanceSheet sheet, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("A punch is required.");
            var byNumber = body.EmployeeId is null && body.EmployeeNumber?.Trim() is { Length: > 0 } number
                ? await db.Employees.AsNoTracking().Where(e => e.Number == number).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct)
                : null;
            if (await HrHttp.ForeignEmployeesAsync(http, db, [body.EmployeeId ?? byNumber ?? Guid.Empty], ct)) return Results.NotFound();
            var result = await sheet.ClockAsync(body, HrHttp.UserName(http), ct);
            // The warning travels inside the created document, never in place of it: a punch that
            // does not pair is a fact for the foreman, not a reason to lose it.
            return result.Ok
                ? Results.Created($"/api/hr/attendance/clock?employeeId={result.Value!.Person.EmployeeId}", result.Value)
                : HrHttp.Problem(result);
        });

        // The name behind a badge number, so the clock can ask before it records (owner, 2026-10-10).
        attendance.MapGet("/clock/person", async (
            string? number, string? at, AttendanceSheet sheet, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            // Another branch's number is answered exactly as a number nobody holds (P0.1): the
            // lookup searched the whole company, and its answer named the person.
            var held = number?.Trim() is { Length: > 0 } typed
                ? await db.Employees.AsNoTracking().Where(e => e.Number == typed).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct)
                : null;
            if (held is Guid one && await HrHttp.ForeignEmployeesAsync(http, db, [one], ct))
                return HrHttp.Problem(HrResult<ClockPersonDto>.NotFound(HrProblems.EmployeeNotFound, "No such employee."));
            // A set time on the clock asks about that time; none asks about now. Read as UTC in the
            // invariant culture, whatever the host's own culture or zone.
            DateTime? stated = DateTime.TryParse(at, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed) ? parsed : null;
            var result = await sheet.FindClockPersonAsync(number, stated, ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        });

        attendance.MapGet("/clock", async (
            Guid? employeeId, Guid? branchId, int year, int month, int? take,
            AttendanceSheet sheet, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            if (employeeId is Guid one && await HrHttp.ForeignEmployeesAsync(http, db, [one], ct)) return Results.NotFound();
            var result = await sheet.ReadClockAsync(employeeId, branchId, year, month, take ?? 1000, ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        });
        PeoplePageRoutes.MapClockPage(attendance);
    }
}
