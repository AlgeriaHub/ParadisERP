using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Hr;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// The employee record: who the company pays, and who used to be paid.
///
/// <para>
/// Behind <c>payroll.manage</c>, not merely behind the manager role, and for the reason the
/// permission exists: these rows carry a wage. The foreman who counts the crew each morning
/// reaches the attendance sheet (<see cref="HrAttendanceEndpoints"/>) and never this.
/// </para>
/// <para>
/// <b>There is no DELETE.</b> A person who has been paid is named by payslips, advances and
/// ledger entries; removing the row would orphan all of them. Leaving is
/// <c>POST /employees/{id}/leave</c>, which is a date and a consequence, not an erasure.
/// </para>
/// </summary>
public static class HrEmployeeEndpoints
{
    public static void MapHrEmployeeEndpoints(this IEndpointRouteBuilder app)
    {
        // The same shape as MapHrEndpoints' group: the subscription filter opens the write
        // transaction these handlers join, and the back office is Manager or Admin.
        var group = app.MapGroup("/api/hr").WithTags("HR")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        var employees = group.MapGroup("/employees");

        employees.MapGet("", async (
            Guid? branchId, bool? active, string? q, int? skip, int? take,
            EmployeeDirectory directory, CancellationToken ct) =>
            Results.Ok(await directory.ListAsync(branchId, active, q, skip ?? 0, take ?? 200, ct)));

        // A file carries a wage, a national id and a bank account: another branch's is not found (P0.1).
        employees.MapGet("/{id:guid}", async (Guid id, EmployeeDirectory directory, CancellationToken ct) =>
            await directory.ReadAsync(id, ct) is EmployeeDto employee ? Results.Ok(employee) : Results.NotFound())
            .ForBranchOf(DocumentBranches.Employee);

        employees.MapPost("", async (
            UpsertEmployeeRequest body, EmployeeDirectory directory, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("An employee is required.");
            var result = await directory.CreateAsync(body, ct, await HrHttp.ViewerBranchAsync(http, db, ct));
            return result.Ok
                ? Results.Created($"/api/hr/employees/{result.Value!.Id}", result.Value)
                : HrHttp.Problem(result);
        });

        employees.MapPut("/{id:guid}", async (
            Guid id, UpsertEmployeeRequest body, EmployeeDirectory directory, HttpContext http, PosDbContext db,
            CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("An employee is required.");
            var result = await directory.UpdateAsync(id, body, ct, await HrHttp.ViewerBranchAsync(http, db, ct));
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        }).ForBranchOf(DocumentBranches.Employee);

        employees.MapPost("/{id:guid}/leave", async (
            Guid id, EmployeeLeaveRequest body, EmployeeDirectory directory, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("A leaving date is required.");
            var result = await directory.SetLeftAsync(id, body, ct);
            return result.Ok ? Results.Ok(result.Value) : HrHttp.Problem(result);
        }).ForBranchOf(DocumentBranches.Employee);
    }
}

/// <summary>
/// What an <see cref="HrResult{T}"/> becomes on the wire.
///
/// <para>
/// Every refusal carries a <b>code</b> as well as a sentence, because the person who meets one
/// is a clerk reading Arabic or French: the screen resolves <c>hr.problem.{code}</c> and the
/// English message is the fallback for curl, the smoke script and the log. A locked period also
/// names the run, so the screen can say <i>which</i> run to unlock rather than "somewhere".
/// </para>
/// </summary>
internal static class HrHttp
{
    public static IResult Problem<T>(HrResult<T> result)
    {
        var body = new HrProblemDto(
            result.Code ?? "unknown", result.Message ?? "That could not be saved.",
            result.RunNumber, result.RunStatus, result.Key);

        return result.Failure switch
        {
            HrFailure.NotFound => Results.NotFound(body),
            HrFailure.Conflict => Results.Conflict(body),
            _ => Results.BadRequest(body)
        };
    }

    public static string? UserName(HttpContext http) =>
        http.User.FindFirst("name")?.Value ?? http.User.Identity?.Name;

    /// <summary>
    /// True when a caller without company scope names an employee of another branch (security
    /// review, 2026-10-10; shared with the money routes in P0.1): only the sheet checked the branch,
    /// so a guessed employee number let a branch's foreman read another branch's punches and mark
    /// days that change their pay. An unknown employee is left to the handler, which says so itself.
    /// </summary>
    public static async Task<bool> ForeignEmployeesAsync(
        HttpContext http, PosDbContext db, IEnumerable<Guid> employeeIds, CancellationToken ct)
    {
        if (BranchAssignment.IsCompanyScoped(http.User)) return false;
        var ids = employeeIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return false;
        var own = await BranchAssignment.ForCallerAsync(http.User, db, ct);
        return await db.Employees.AsNoTracking().AnyAsync(e => ids.Contains(e.Id) && e.BranchId != own, ct);
    }

    /// <summary>
    /// The branch a refusal may speak about: null for company scope, which sees every branch, else
    /// the caller's own. Another branch's employee is then refused without being named (P0.1).
    /// </summary>
    public static async Task<Guid?> ViewerBranchAsync(HttpContext http, PosDbContext db, CancellationToken ct) =>
        BranchAssignment.IsCompanyScoped(http.User) ? null : await BranchAssignment.ForCallerAsync(http.User, db, ct);
}
