using System.Security.Claims;
using ParadisERP.API.Subscriptions;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Payroll;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = ParadisERP.Application.Payroll;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// The payroll run — <c>/api/hr/payroll</c>, docs/07 §5.7 and §5.11.
///
/// <para>
/// Back office only, and behind <c>payroll.manage</c> as a whole: a foreman who may type an
/// attendance sheet has no business reading what anybody is paid. The attendance routes are the
/// deliberate exception and live with their own group.
/// </para>
/// <para>
/// Anything that scales with headcount is paged. The run's own read carries the header, the
/// warning <b>counts</b> and the entry it will post — never the lines, because a branch with nine
/// hundred staff would otherwise send a megabyte to draw six stat tiles.
/// </para>
/// </summary>
public static class HrPayrollEndpoints
{
    public static void MapHrPayrollEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/hr/payroll").WithTags("HR")
            .AddEndpointFilter<SubscriptionWriteFilter>()
            .AtCallersBranch();

        MapRuns(group);
        MapTransitions(group);
        MapSummary(group);
    }

    // ---------- reading ----------

    private static void MapRuns(RouteGroupBuilder group)
    {
        group.MapGet("/runs", async (
            Guid? branchId, int? year, string? status, int? take, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.PayrollRuns.AsNoTracking();
            if (branchId is Guid id) query = query.Where(r => r.BranchId == id);
            if (year is int y) query = query.Where(r => r.PeriodYear == y);
            if (Enum.TryParse<PayrollRunStatus>(status, true, out var wanted))
                query = query.Where(r => r.Status == wanted);

            var runs = await query
                .OrderByDescending(r => r.PeriodYear).ThenByDescending(r => r.PeriodMonth)
                .ThenBy(r => r.Number)
                .Take(Math.Clamp(take ?? 200, 1, 1000))
                .ToListAsync(ct);

            var branches = await BranchNamesAsync(db, runs.Select(r => r.BranchId), ct);
            return Results.Ok(runs.Select(r => Map(r, branches.GetValueOrDefault(r.BranchId))));
        });
        PeoplePageRoutes.MapPayrollRunsPage(group);

        group.MapGet("/runs/{id:guid}", async (
            Guid id, PayrollDocuments payroll, PayrollSettingsStore store, PosDbContext db, CancellationToken ct) =>
        {
            var run = await payroll.ReadAsync(id, ct);
            if (run is null) return Results.NotFound();

            var branches = await BranchNamesAsync(db, [run.BranchId], ct);

            // Summed here rather than in the database: SQLite stores decimal as TEXT, so a
            // server-side Sum would add strings up.
            var paid = (await db.PayrollRunLines.AsNoTracking()
                    .Where(l => l.RunId == id)
                    .Select(l => l.PaidAmount)
                    .ToListAsync(ct))
                .Sum();

            // What approving it will do to the books, in account codes, so the action bar can say
            // so in words before the button is pressed — and what it did, after.
            var accrual = await payroll.AccrualAsync(run, ct);
            var posting = PayrollPosting.Accrual(
                run.Id, run.BranchId, run.Number, accrual, null, PayrollDocuments.PeriodEndUtc(run));

            var adjustments = await db.PayrollAdjustments.AsNoTracking()
                .Where(a => a.RunId == id)
                .OrderBy(a => a.Key)
                .ToListAsync(ct);
            var staff = await EmployeeNamesAsync(db, adjustments.Select(a => a.EmployeeId), ct);

            // The refusal is carried on the read, not only on the compute: the screen has to be
            // able to say what is missing and who can set it before anybody presses anything.
            var refusal = Engine.PayrollEngine.Refusal((await store.ReadAsync(ct)).Settings);

            return Results.Ok(new PayrollRunDetailDto(
                Map(run, branches.GetValueOrDefault(run.BranchId), paid,
                    adjustments.Select(a => Map(a, staff.GetValueOrDefault(a.EmployeeId, ""))).ToList()),
                await payroll.WarningCountsAsync(id, ct),
                posting is null ? [] : Map(posting),
                refusal is null ? null : new PayrollRefusalDto(refusal.Code, refusal.MissingFields, refusal.FixableBy),
                // Said on the read as well as refused on the transition: the screen disables
                // Approve rather than letting it come back 409 after the manager has pressed it.
                // A base pay changed since the compute counts too.
                await payroll.AdjustmentsMovedAsync(run, ct) || await payroll.PayMovedAsync(run, ct),
                // The words this run's paper is read by, frozen with its figures. A payslip
                // reprinted a year later must not be relabelled by an allowance renamed since.
                PayrollDocuments.Snapshot(run)));
        }).ForBranchOf(DocumentBranches.PayrollRun);

        group.MapGet("/runs/{id:guid}/lines", async (
            Guid id, int? skip, int? take, string? q, PosDbContext db, CancellationToken ct) =>
        {
            if (!await db.PayrollRuns.AsNoTracking().AnyAsync(r => r.Id == id, ct)) return Results.NotFound();

            var query = db.PayrollRunLines.AsNoTracking().Where(l => l.RunId == id);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(l =>
                    l.EmployeeNameSnapshot.Contains(term) || l.EmployeeNumberSnapshot.Contains(term));
            }

            var total = await query.CountAsync(ct);
            var lines = await query
                .OrderBy(l => l.EmployeeNumberSnapshot)
                .Skip(Math.Max(0, skip ?? 0))
                .Take(Math.Clamp(take ?? 100, 1, 1000))
                .Include(l => l.Elements)
                .ToListAsync(ct);

            return Results.Ok(new PayrollRunLinesPageDto(
                total, Math.Max(0, skip ?? 0), lines.Count, lines.Select(Map).ToList()));
        }).ForBranchOf(DocumentBranches.PayrollRun);

        group.MapPut("/runs/{id:guid}/adjustments/{employeeId:guid}", async (
            Guid id, Guid employeeId, UpsertPayrollAdjustmentRequest body, PayrollDocuments payroll,
            PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("An adjustment is required.");
            // The route names the person. A body that names a different one is a screen bug, and
            // silently preferring either would put a bonus on the wrong payslip.
            if (body.EmployeeId != Guid.Empty && body.EmployeeId != employeeId)
                return Results.BadRequest("The adjustment names a different employee from the route.");

            var result = await payroll.UpsertAdjustmentAsync(id, employeeId, body, UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var name = result.Value is null
                ? ""
                : (await EmployeeNamesAsync(db, [employeeId], ct)).GetValueOrDefault(employeeId, "");
            return Results.Ok(new
            {
                adjustment = result.Value is null ? null : Map(result.Value, name),
                warning = result.Warning
            });
        }).ForBranchOf(DocumentBranches.PayrollRun);
    }

    // ---------- the lifecycle ----------

    private static void MapTransitions(RouteGroupBuilder group)
    {
        group.MapPost("/runs", async (
            CreatePayrollRunRequest body, PayrollDocuments payroll, PosDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            if (body is null) return Results.BadRequest("A branch and a period are required.");
            var result = await payroll.CreateAsync(
                body.BranchId, body.PeriodYear, body.PeriodMonth, UserName(http), ct);
            if (!result.Ok) return Problem(result);

            var run = result.Value!;
            var branches = await BranchNamesAsync(db, [run.BranchId], ct);
            return Results.Created($"/api/hr/payroll/runs/{run.Id}",
                Map(run, branches.GetValueOrDefault(run.BranchId)));
        });

        group.MapPost("/runs/{id:guid}/compute", Transition(
            (payroll, id, by, ct) => payroll.ComputeAsync(id, by, ct))).ForBranchOf(DocumentBranches.PayrollRun);

        group.MapPost("/runs/{id:guid}/unlock", Transition(
            (payroll, id, by, ct) => payroll.UnlockAsync(id, by, ct))).ForBranchOf(DocumentBranches.PayrollRun);

        group.MapPost("/runs/{id:guid}/approve", Transition(
            (payroll, id, by, ct) => payroll.ApproveAsync(id, by, ct))).ForBranchOf(DocumentBranches.PayrollRun);

        group.MapPost("/runs/{id:guid}/reverse", async (
            Guid id, ReversePayrollRunRequest body, PayrollDocuments payroll, PosDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var result = await payroll.ReverseRunAsync(id, body?.Reason ?? "", UserName(http), ct);
            return result.Ok ? await OkAsync(result, db, ct) : Problem(result);
        }).ForBranchOf(DocumentBranches.PayrollRun);
    }

    /// <summary>
    /// The three bodyless transitions, which differ only in the method they call. Written once so
    /// that the envelope — the run, and any warning beside it — cannot drift between them.
    /// </summary>
    private static Delegate Transition(
        Func<PayrollDocuments, Guid, string?, CancellationToken, Task<PayrollResult<PayrollRun>>> act) =>
        async (Guid id, PayrollDocuments payroll, PosDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var result = await act(payroll, id, UserName(http), ct);
            return result.Ok ? await OkAsync(result, db, ct) : Problem(result);
        };

    private static async Task<IResult> OkAsync(
        PayrollResult<PayrollRun> result, PosDbContext db, CancellationToken ct)
    {
        var run = result.Value!;
        var branches = await BranchNamesAsync(db, [run.BranchId], ct);
        // The warning travels beside the run, never instead of it: a provisional figure is
        // something to say, not a reason to refuse a month's wages.
        return Results.Ok(new { run = Map(run, branches.GetValueOrDefault(run.BranchId)), warning = result.Warning });
    }

    // ---------- the group's month ----------

    private static void MapSummary(RouteGroupBuilder group)
    {
        group.MapGet("/summary", async (int? year, int? month, Guid? branchId, PosDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var y = year ?? now.Year;
            var m = month ?? now.Month;
            if (m is < 1 or > 12) return Results.BadRequest("A period is a calendar month, 1 to 12.");

            var branches = await db.Branches.AsNoTracking()
                .Where(b => !b.IsDeleted && b.IsActive)
                // One row for everyone but the Admin: their own branch (CallersBranch).
                .Where(b => branchId == null || b.Id == branchId)
                .OrderBy(b => b.NameFr)
                .Select(b => new { b.Id, b.NameFr })
                .ToListAsync(ct);

            // Non-reversed only: a reversed run is history, and the month it belongs to is open.
            var runs = await db.PayrollRuns.AsNoTracking()
                .Where(r => r.PeriodYear == y && r.PeriodMonth == m && r.Status != PayrollRunStatus.Reversed)
                .ToListAsync(ct);
            var byBranch = runs.GroupBy(r => r.BranchId).ToDictionary(g => g.Key, g => g.First());

            return Results.Ok(branches.Select(branch =>
            {
                var run = byBranch.GetValueOrDefault(branch.Id);
                return new PayrollSummaryRowDto(
                    branch.Id, branch.NameFr ?? "", run?.Id, run?.Number,
                    // A branch with no run at all says so in a word of its own rather than
                    // borrowing "Draft", which would read as a run somebody has opened.
                    run is null ? "None" : run.Status.ToString(),
                    run?.IsProvisional ?? false,
                    run?.HeadCount ?? 0,
                    run?.Gross ?? 0m, run?.SocialEmployee ?? 0m, run?.SocialEmployer ?? 0m,
                    run?.IncomeTax ?? 0m, run?.EmployerLevies ?? 0m, run?.AdvancesRecovered ?? 0m,
                    run?.Net ?? 0m);
            }));
        });
    }

    // ---------- shaping ----------

    private static async Task<Dictionary<Guid, string>> BranchNamesAsync(
        PosDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        return await db.Branches.AsNoTracking()
            .Where(b => wanted.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);
    }

    private static async Task<Dictionary<Guid, string>> EmployeeNamesAsync(
        PosDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.Employees.AsNoTracking()
            .Where(e => wanted.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.FullName, ct);
    }

    /// <summary>
    /// The run's header. <c>Lines</c> and <c>Warnings</c> are deliberately left null: the lines
    /// are a paged route of their own, and the warnings are sent as counts beside the header.
    /// </summary>
    private static PayrollRunDto Map(
        PayrollRun run, string? branchName, decimal paidTotal = 0m,
        IReadOnlyList<PayrollAdjustmentDto>? adjustments = null) =>
        new(run.Id, run.Number, run.BranchId, branchName ?? "",
            run.PeriodYear, run.PeriodMonth,
            // In words, on the wire: the API registers no string-enum converter, so an ordinal
            // here would have every screen carrying its own number-to-word table.
            run.Status.ToString(),
            run.HeadCount, run.CurrencyDecimals, run.IsProvisional,
            run.Gross, run.SocialBase, run.SocialEmployee, run.SocialEmployer,
            run.TaxBase, run.IncomeTax, run.EmployerLevies,
            run.AdvancesRecovered, run.OtherDeductions, run.Net,
            paidTotal,
            Utc(run.ComputedAtUtc), run.LockedBy,
            Utc(run.ApprovedAtUtc), run.ApprovedBy, Utc(run.PaidAtUtc),
            Utc(run.ReversedAtUtc), run.ReversedBy, run.ReversalReason,
            run.LedgerEntryId, run.ReversalEntryId,
            run.CreatedBy, Utc(run.CreatedAtUtc),
            Lines: null, Adjustments: adjustments, Warnings: null, PaidByVoucher: run.PaidByVoucher);

    private static PayrollRunLineDto Map(PayrollRunLine line) =>
        new(line.Id, line.EmployeeId, line.EmployeeNumberSnapshot, line.EmployeeNameSnapshot,
            line.SocialNumberSnapshot, line.BranchIdSnapshot,
            line.PayBasis.ToString(), line.BaseRate,
            line.DaysPresent, line.PaidLeaveDays, line.UnpaidAbsenceDays, line.AttendanceSource.ToString(),
            line.Gross, line.SocialBase, line.SocialEmployee, line.SocialEmployer, line.TaxBase,
            line.IncomeTax, line.AdvanceRecovered, line.OtherDeductions, line.Net, line.PaidAmount,
            line.Elements.OrderBy(e => e.SortOrder).Select(Map).ToList(),
            PayrollDocuments.Warnings(line.WarningsJson),
            line.WagePaymentId,
            line.PaidByVoucher);

    private static PayrollElementDto Map(PayrollRunLineElement element) =>
        new(element.Key, element.Kind.ToString(), element.Amount, element.Qty, element.Rate,
            element.InSocialBase, element.InTaxBase, element.SortOrder);

    private static PayrollAdjustmentDto Map(PayrollAdjustment adjustment, string employeeName) =>
        new(adjustment.Id, adjustment.RunId, adjustment.EmployeeId, employeeName,
            adjustment.Key, adjustment.Amount, adjustment.Reason,
            adjustment.InSocialBase, adjustment.InTaxBase, adjustment.CreatedBy,
            Utc(adjustment.CreatedAtUtc));

    private static IReadOnlyList<PayrollPostingLineDto> Map(PostingDraft draft) =>
        draft.Lines.Select(l => new PayrollPostingLineDto(l.AccountCode, l.Debit, l.Credit)).ToList();

    /// <summary>A stamp this code stored as UTC, marked as UTC whatever the provider returned.</summary>
    private static DateTime? Utc(DateTime? value) =>
        value is null ? null
        : value.Value.Kind == DateTimeKind.Utc ? value
        : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static string? UserName(HttpContext http) =>
        http.User.FindFirstValue("name") ?? http.User.Identity?.Name;

    /// <summary>
    /// Every refusal carries the sentence the screen shows. The compute refusal carries more than
    /// a sentence — the missing figures and the permission that can set them — because "it cannot
    /// be computed" with nothing to act on is what makes a user open a support ticket.
    /// </summary>
    private static IResult Problem<T>(PayrollResult<T> result) => result.Failure switch
    {
        PayrollFailure.NotFound => Results.NotFound(result.Message),
        // A refusal that named a code is sent as one, with its figures beside it, so an Arabic
        // manager meets a sentence in Arabic rather than an English one with a run number in it.
        PayrollFailure.Conflict when result.Code is not null => Results.Conflict(Coded(result)),
        PayrollFailure.Conflict => Results.Conflict(result.Message),
        PayrollFailure.Invalid when result.Code is not null => Results.BadRequest(Coded(result)),
        PayrollFailure.Refused => Results.Json(
            new
            {
                code = result.Refusal!.Code,
                missing = result.Refusal.MissingFields,
                fixableBy = result.Refusal.FixableBy,
                message = result.Message
            },
            statusCode: StatusCodes.Status409Conflict),
        _ => Results.BadRequest(result.Message)
    };

    /// <summary>
    /// The coded envelope: the same <c>{ code, message, args }</c> the money routes answer with, so
    /// one helper on the client reads every payroll refusal.
    /// </summary>
    internal static object Coded<T>(PayrollResult<T> result) =>
        new { code = result.Code, message = result.Message, args = result.Args ?? new Dictionary<string, string>() };
}
