using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Hr;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// Overtime entries (owner, 2026-10-10): recorded by date and kind of work, corrected or deleted by
/// whoever may record attendance — <c>payroll.manage</c> or <c>hr.attendance</c>, as every
/// attendance route (RoutePermissions). A month held by a locked, approved or paid run refuses
/// every write with 409 and the run's number, and each entry says whether it can still be changed.
/// Like the rest of attendance, nothing here carries a wage.
/// </summary>
public static class HrOvertimeEndpoints
{
    public static void MapHrOvertimeEndpoints(this IEndpointRouteBuilder app)
    {
        var overtime = app.MapGroup("/api/hr/attendance/overtime").WithTags("HR")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        overtime.MapGet("", async (
            Guid branchId, int year, int month, Guid? employeeId, [FromServices] OvertimeEntries entries, CancellationToken ct) =>
        {
            var result = await entries.ListAsync(branchId, year, month, employeeId, ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        });
        PeoplePageRoutes.MapOvertimePage(overtime);

        overtime.MapPost("", async (
            CreateOvertimeEntryRequest body, [FromServices] OvertimeEntries entries, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("An entry is required.");
            if (await ForeignAsync(http, db, body.EmployeeId, ct)) return Results.NotFound();
            var result = await entries.CreateAsync(body, HrHttp.UserName(http), ct);
            return result.Ok
                ? Results.Created($"/api/hr/attendance/overtime/{result.Value!.Id}", result.Value)
                : HrHttp.Problem(result);
        });

        overtime.MapPut("/{id:guid}", async (
            Guid id, UpdateOvertimeEntryRequest body, [FromServices] OvertimeEntries entries, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("An entry is required.");
            if (await ForeignEntryAsync(http, db, entries, id, ct)) return Results.NotFound();
            var result = await entries.UpdateAsync(id, body, HrHttp.UserName(http), ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        });

        overtime.MapDelete("/{id:guid}", async (
            Guid id, [FromServices] OvertimeEntries entries, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            if (await ForeignEntryAsync(http, db, entries, id, ct)) return Results.NotFound();
            var result = await entries.DeleteAsync(id, HrHttp.UserName(http), ct);
            return result.Ok ? Results.NoContent() : HrHttp.Problem(result);
        });

        overtime.MapGet("/{id:guid}/history", async (
            Guid id, [FromServices] OvertimeEntries entries, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            if (await ForeignEntryAsync(http, db, entries, id, ct)) return Results.NotFound();
            var result = await entries.HistoryAsync(id, ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        });
    }

    /// <summary>
    /// True when a caller without company scope names another branch's employee: a branch's
    /// foreman records and corrects their own crew only.
    /// </summary>
    private static async Task<bool> ForeignAsync(HttpContext http, PosDbContext db, Guid employeeId, CancellationToken ct)
    {
        if (BranchAssignment.IsCompanyScoped(http.User)) return false;
        var own = await BranchAssignment.ForCallerAsync(http.User, db, ct);
        return await db.Employees.AsNoTracking().AnyAsync(e => e.Id == employeeId && e.BranchId != own, ct);
    }

    /// <summary>The same, for an entry named by id. An entry that no longer exists is left to the handler.</summary>
    private static async Task<bool> ForeignEntryAsync(
        HttpContext http, PosDbContext db, OvertimeEntries entries, Guid id, CancellationToken ct)
    {
        if (BranchAssignment.IsCompanyScoped(http.User)) return false;
        var employeeId = await entries.EmployeeOfAsync(id, ct)
            ?? await db.OvertimeEntryChanges.AsNoTracking().Where(c => c.EntryId == id)
                .Select(c => (Guid?)c.EmployeeId).FirstOrDefaultAsync(ct);
        return employeeId is Guid one && await ForeignAsync(http, db, one, ct);
    }
}
