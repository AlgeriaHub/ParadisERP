using System.Security.Claims;
using AlgeriaPos.API.Subscriptions;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Payroll;
using AlgeriaPos.Infrastructure.Persistence;

namespace AlgeriaPos.API.Endpoints;

/// <summary>
/// The money side of payroll under <c>/api/hr</c>: advances, wage payments and the transfer that
/// clears what was withheld (docs/07 §5.8, §5.10, §5.11).
///
/// <para>
/// A separate file from <see cref="HrEndpoints"/>, not a separate prefix: these routes sit in the
/// same <c>/api/hr</c> group behind the same write filter and the same back-office roles. What is
/// different is that every one of them either moves money or refuses to, so unlike the settings
/// GET there is <b>no read here without <c>payroll.manage</c></b> — a list of open advances is a
/// list of who in the company is short of money this month.
/// </para>
/// <para>
/// The refusal shape is the back office's: 404 for a document that is not there, 409 for a state
/// that says no — a run that is not approved, a payment already reversed — and 400 for a request
/// that is wrong in itself. A <b>warning never takes that channel</b>: it travels beside the
/// document in the same envelope whether there is one or not, so no screen has to discover that
/// the response changes shape on the day a box runs short.
/// </para>
/// </summary>
public static class HrMoneyEndpoints
{
    public static void MapHrMoneyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hr").WithTags("HR")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        MapAdvances(group);
        MapWagePayments(group);
        MapStatutoryPayments(group);
    }

    // ---------- advances ----------

    private static void MapAdvances(RouteGroupBuilder group)
    {
        group.MapGet("/advances", async (
                Guid? employeeId, Guid? branchId, string? status, int? take,
                WageMoney money, CancellationToken ct) =>
            Results.Ok(await money.ListAdvancesAsync(employeeId, branchId, status, take ?? 200, ct)));
        PeoplePageRoutes.MapAdvancesPage(group); PeoplePageRoutes.MapOwingAdvancesPage(group);

        group.MapGet("/advances/{id:guid}", async (Guid id, WageMoney money, CancellationToken ct) =>
        {
            var advance = await money.ReadAdvanceAsync(id, ct);
            return advance is null ? Results.NotFound() : Results.Ok(advance);
        }).ForBranchOf(DocumentBranches.WageAdvance);

        group.MapPost("/advances", async (
            GrantWageAdvanceRequest r, WageMoney money, HttpContext http, PosDbContext db, CancellationToken ct) =>
        {
            // The box is already held to the caller's branch (IPaidFromBox); the person is not, and
            // an advance to another branch's employee is money handed to someone this branch does
            // not pay (P0.1). Not found, as if they did not exist.
            if (await HrHttp.ForeignEmployeesAsync(http, db, [r.EmployeeId], ct)) return Results.NotFound();
            var result = await money.GrantAdvanceAsync(r, UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var dto = (await money.MapAdvancesAsync([result.Value!], ct)).Single();
            // "The box is short" rides beside the advance, never instead of it: by the time
            // anybody reads this the cash has already been handed over.
            return Results.Created($"/api/hr/advances/{dto.Id}", new { advance = dto, warning = result.Warning });
        });
    }

    // ---------- wage payments ----------

    private static void MapWagePayments(RouteGroupBuilder group)
    {
        group.MapGet("/payroll/runs/{runId:guid}/payments", async (
                Guid runId, int? take, WageMoney money, CancellationToken ct) =>
            Results.Ok(await money.ListPaymentsAsync(runId, null, take ?? 200, ct)))
            .ForBranchOf(DocumentBranches.PayrollRun, routeKey: "runId");
        PeoplePageRoutes.MapWagePaymentsPage(group);

        group.MapGet("/payroll/payments/{id:guid}", async (Guid id, WageMoney money, CancellationToken ct) =>
        {
            var payment = (await money.ListPaymentsAsync(null, id, 1, ct)).FirstOrDefault();
            return payment is null ? Results.NotFound() : Results.Ok(payment);
        }).ForBranchOf(DocumentBranches.WagePayment);

        group.MapPost("/payroll/runs/{runId:guid}/payments", async (
            Guid runId, PayWagesRequest r, WageMoney money, HttpContext http, CancellationToken ct) =>
        {
            var result = await money.PayAsync(runId, r, UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var dto = (await money.ListPaymentsAsync(null, result.Value!.Id, 1, ct)).Single();
            return Results.Created($"/api/hr/payroll/payments/{dto.Payment.Id}",
                new { payment = dto, warning = result.Warning });
        }).ForBranchOf(DocumentBranches.PayrollRun, routeKey: "runId");

        // A payment is never edited. This is the correction, and it leaves both the payment and
        // the reversal visible — the rule the ledger and the invoice already follow.
        group.MapPost("/payroll/payments/{id:guid}/reverse", async (
            Guid id, ReverseWagePaymentRequest? r, WageMoney money, HttpContext http, CancellationToken ct) =>
        {
            var result = await money.ReversePaymentAsync(id, r?.Reason, UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var dto = (await money.ListPaymentsAsync(null, result.Value!.Id, 1, ct)).Single();
            return Results.Ok(new { payment = dto, warning = result.Warning });
        }).ForBranchOf(DocumentBranches.WagePayment);
    }

    // ---------- statutory payments ----------

    private static void MapStatutoryPayments(RouteGroupBuilder group)
    {
        group.MapGet("/payroll/statutory-payments", async (
                Guid? branchId, string? accountCode, int? year, int? take,
                WageMoney money, CancellationToken ct) =>
            Results.Ok(await money.ListStatutoryPaymentsAsync(branchId, accountCode, year, take ?? 200, ct)));
        PeoplePageRoutes.MapStatutoryPaymentsPage(group);

        group.MapPost("/payroll/statutory-payments", async (
            RecordStatutoryPaymentRequest r, WageMoney money, HttpContext http, CancellationToken ct) =>
        {
            var result = await money.PayStatutoryAsync(r, UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var dto = (await money.ListStatutoryPaymentsAsync(null, null, null, 1000, ct))
                .First(p => p.Id == result.Value!.Id);
            return Results.Created($"/api/hr/payroll/statutory-payments/{dto.Id}",
                new { payment = dto, warning = result.Warning });
        });

        // What 431, 442 and 447 still hold. §5.14's risk read the other way round: a screen can
        // only say "this is settled from the bank outside the system" if it can first say how much
        // is standing.
        group.MapGet("/payroll/liabilities", async (
                Guid? branchId, WageMoney money, CancellationToken ct) =>
            Results.Ok(await money.LiabilitiesAsync(branchId, ct)));
    }

    // ---------- shaping ----------

    private static string? UserName(HttpContext http) =>
        http.User.FindFirstValue("name") ?? http.User.Identity?.Name;

    private static IResult Problem<T>(PayrollResult<T> result) => result.Failure switch
    {
        PayrollFailure.NotFound => Results.NotFound(result.Message),
        // A refusal that named a code is sent as one. An overpayment names a person and three
        // figures, so the screen has to rebuild the sentence rather than print this one.
        PayrollFailure.Conflict when result.Code is not null =>
            Results.Conflict(HrPayrollEndpoints.Coded(result)),
        PayrollFailure.Invalid when result.Code is not null =>
            Results.BadRequest(HrPayrollEndpoints.Coded(result)),
        PayrollFailure.Conflict => Results.Conflict(result.Message),
        // Nothing in this file computes, so this arm is here only because the shared result type
        // carries it; it stays a 409 with the missing figures named, the same as the run's.
        PayrollFailure.Refused => Results.Conflict(new
        {
            result.Message,
            Missing = result.Refusal?.MissingFields ?? [],
            FixableBy = result.Refusal?.FixableBy
        }),
        _ => Results.BadRequest(result.Message)
    };
}
