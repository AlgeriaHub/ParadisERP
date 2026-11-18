using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Auth;
using ParadisERP.Infrastructure.Authorization;
using ParadisERP.Infrastructure.Hr;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Payroll;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.API.Endpoints;

/// <summary>
/// The people lists a page at a time — payroll and administration (owner, 2026-11-17: "I need to paginate in backend, not
/// frontend"). Each route is <c>&lt;the list's own GET route&gt;/page</c>, mapped on the same group as that route, so it keeps
/// the group's filters: a parameter named <c>branchId</c> is the caller's own branch for everyone but the Admin
/// (CallersBranch), the subscription filter applies, a run read by id is 404 at another branch (ForBranchOf). Each reads
/// exactly what its base route reads — the same service call, or the same query copied from the base handler and named
/// below — and its permission is the base route's entry (RoutePermissions). The filters the screens applied in the browser
/// run here over every row; only the page is sent, and a list's figures (a totals row, the tiles, the options a filter
/// offers) come in <c>Summary</c>, computed over all its filtered rows.
/// <para>
/// SQL where the filters translate (<see cref="ListPagingQueries"/>); in memory with <see cref="ListPaging.Of{T}"/> where the
/// base route builds its list in memory (a clock log paired in C#, the attendance review, a month of overtime, the roles,
/// the language packs) or where the figures are <c>decimal</c> sums SQLite cannot take. A day a screen filters on is an ISO
/// day read on the company's clock (<see cref="CompanyDays"/>), never the browser's or the host's.
/// </para>
/// </summary>
public static class PeoplePageRoutes
{
    /// <summary>The clock log's ceiling, as <see cref="AttendanceSheet.ReadClockAsync"/> clamps it.</summary>
    private const int MaxClockRows = 5000;

    /// <summary>A run's payments, as <see cref="WageMoney.ListPaymentsAsync"/> clamps them.</summary>
    private const int MaxRunPayments = 1000;

    // =========================================================================================
    // Payroll › Advances (base: GET /api/hr/advances, HrMoneyEndpoints.MapAdvances)
    // =========================================================================================

    /// <summary>
    /// The advances register: the base route's filters (employee, branch, state), plus the screen's payout method,
    /// inter-branch and granted-between days. The query is <see cref="WageMoney.ListAdvancesAsync"/>'s, filter for filter,
    /// and the rows are shaped by its own <see cref="WageMoney.MapAdvancesAsync"/>.
    /// </summary>
    public static void MapAdvancesPage(RouteGroupBuilder group) =>
        group.MapGet("/advances/page", async (
            Guid? employeeId, Guid? branchId, string? status, string? method, string? interBranch, string? from, string? to,
            int? skip, int? take, [FromServices] WageMoney money, PosDbContext db, IRegionalSettingsProvider regional,
            CancellationToken ct) =>
        {
            // WageMoney.ListAdvancesAsync, filter for filter.
            var query = db.WageAdvances.AsNoTracking();
            if (employeeId is Guid eid) query = query.Where(a => a.EmployeeId == eid);
            if (branchId is Guid bid) query = query.Where(a => a.BranchId == bid);
            if (Enum.TryParse<WageAdvanceStatus>(status, true, out var wanted)) query = query.Where(a => a.Status == wanted);

            // The screen's own filters, as it applied them to the rows it had loaded.
            if (Method(method) is PayoutMethod paid) query = query.Where(a => a.PayoutMethod == paid);
            // Inter-branch as MapAdvancesAsync says it: cash out of a box that is not the employee's branch's — a box it
            // cannot find counts as another branch's there too.
            query = interBranch switch
            {
                "yes" => query.Where(a => a.CashBoxId != null
                    && !db.CashBoxes.Any(b => b.Id == a.CashBoxId && b.BranchId == a.BranchId)),
                "no" => query.Where(a => a.CashBoxId == null
                    || db.CashBoxes.Any(b => b.Id == a.CashBoxId && b.BranchId == a.BranchId)),
                _ => query,
            };
            var (fromUtc, toUtc) = Days(from, to, regional);
            if (fromUtc is DateTime start) query = query.Where(a => a.GrantedAtUtc >= start);
            if (toUtc is DateTime end) query = query.Where(a => a.GrantedAtUtc < end);

            return Results.Ok(await query
                .OrderByDescending(a => a.GrantedAtUtc).ThenBy(a => a.Id)
                .ToPageAsync(skip, take, rows => money.MapAdvancesAsync(rows, ct), ct));
        });

    /// <summary>
    /// "Who owes": the open advances among those the register's branch and state ask for, one row per employee with the
    /// count and what is outstanding, largest first — the table and the two tiles above the register. Grouped and summed
    /// here, over every advance (SQLite cannot sum a decimal); the method, inter-branch and day filters never moved them.
    /// </summary>
    public static void MapOwingAdvancesPage(RouteGroupBuilder group) =>
        group.MapGet("/advances/owing/page", async (
            Guid? branchId, string? status, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
        {
            var query = db.WageAdvances.AsNoTracking();
            if (branchId is Guid bid) query = query.Where(a => a.BranchId == bid);
            if (Enum.TryParse<WageAdvanceStatus>(status, true, out var wanted)) query = query.Where(a => a.Status == wanted);

            // Only Open is what the next run takes; recovered ones are history.
            var open = await query.Where(a => a.Status == WageAdvanceStatus.Open)
                .Select(a => new { a.EmployeeId, a.BranchId, a.Amount, a.Recovered, a.GrantedAtUtc })
                .ToListAsync(ct);

            var people = open
                .GroupBy(a => a.EmployeeId)
                .Select(g =>
                {
                    // The branch of the latest advance, as the screen read it off the first row it met.
                    var latest = g.OrderByDescending(a => a.GrantedAtUtc).First();
                    return new
                    {
                        EmployeeId = g.Key, latest.BranchId, latest.GrantedAtUtc,
                        Count = g.Count(), Outstanding = g.Sum(a => a.Amount - a.Recovered),
                    };
                })
                .OrderByDescending(r => r.Outstanding).ThenByDescending(r => r.GrantedAtUtc).ThenBy(r => r.EmployeeId)
                .ToList();

            var summary = new OwingSummary(open.Sum(a => a.Amount - a.Recovered), people.Count);
            var page = ListPaging.Of(people, skip, take, summary);

            var ids = page.Items.Select(r => r.EmployeeId).ToList();
            var staff = ids.Count == 0
                ? []
                : await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, e.Number, e.FullName })
                    .ToDictionaryAsync(e => e.Id, ct);
            var branches = await BranchNamesAsync(db, page.Items.Select(r => r.BranchId), ct);

            return Results.Ok(new PageResult<OwingRow>(
                page.Items.Select(r =>
                {
                    staff.TryGetValue(r.EmployeeId, out var person);
                    return new OwingRow(r.EmployeeId, person?.Number ?? "", person?.FullName ?? "",
                        branches.GetValueOrDefault(r.BranchId) ?? "", r.Count, r.Outstanding);
                }).ToList(),
                page.Total, page.Skip, page.Take, summary));
        });

    /// <summary>One employee's open advances, as the "who owes" table shows them.</summary>
    public sealed record OwingRow(
        Guid EmployeeId, string EmployeeNumber, string EmployeeName, string BranchName, int Count, decimal Outstanding);

    /// <summary>The two tiles: everything outstanding, and how many people owe it.</summary>
    public sealed record OwingSummary(decimal OutstandingTotal, int PeopleOwing);

    // =========================================================================================
    // Payroll › Runs (base: GET /api/hr/payroll/runs, HrPayrollEndpoints.MapRuns)
    // =========================================================================================

    /// <summary>
    /// The runs list — the payroll runs page and the wage payments' "which run". The base route's filters (branch, year,
    /// state) and its order; the query is copied from <c>GET /runs</c> in HrPayrollEndpoints. The totals row (head
    /// count, gross, net) sums every run the filters keep.
    /// </summary>
    public static void MapPayrollRunsPage(RouteGroupBuilder group) =>
        group.MapGet("/runs/page", async (
            Guid? branchId, int? year, string? status, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
        {
            // HrPayrollEndpoints GET /runs, filter for filter.
            var query = db.PayrollRuns.AsNoTracking();
            if (branchId is Guid id) query = query.Where(r => r.BranchId == id);
            if (year is int y) query = query.Where(r => r.PeriodYear == y);
            if (Enum.TryParse<PayrollRunStatus>(status, true, out var wanted)) query = query.Where(r => r.Status == wanted);

            // Summed here rather than in the database: SQLite stores decimal as TEXT.
            var figures = await query.Select(r => new { r.HeadCount, r.Gross, r.Net }).ToListAsync(ct);
            var summary = new RunsSummary(figures.Sum(f => f.HeadCount), figures.Sum(f => f.Gross), figures.Sum(f => f.Net));

            return Results.Ok(await query
                .OrderByDescending(r => r.PeriodYear).ThenByDescending(r => r.PeriodMonth)
                .ThenBy(r => r.Number).ThenBy(r => r.Id)
                .ToPageAsync(skip, take, async rows =>
                {
                    var names = await BranchNamesAsync(db, rows.Select(r => r.BranchId), ct);
                    return (IReadOnlyList<PayrollRunDto>)rows.Select(r => RunHeader(r, names.GetValueOrDefault(r.BranchId))).ToList();
                }, ct, summary));
        });

    /// <summary>The runs list's totals row.</summary>
    public sealed record RunsSummary(int HeadCount, decimal Gross, decimal Net);

    // =========================================================================================
    // Payroll › Wage payments of one run (base: GET /api/hr/payroll/runs/{runId}/payments, HrMoneyEndpoints)
    // =========================================================================================

    /// <summary>
    /// A run's payments, with the screen's payout-method and standing/reversed filters. Read through the base route's
    /// <see cref="WageMoney.ListPaymentsAsync"/> — a reversal is a ledger entry, not a column — and held to the run's
    /// branch exactly as the base route is.
    /// </summary>
    public static void MapWagePaymentsPage(RouteGroupBuilder group) =>
        group.MapGet("/payroll/runs/{runId:guid}/payments/page", async (
            Guid runId, string? method, string? state, int? skip, int? take, [FromServices] WageMoney money,
            CancellationToken ct) =>
        {
            var paid = Method(method);
            var rows = (await money.ListPaymentsAsync(runId, null, MaxRunPayments, ct))
                .Where(p => paid is null || p.Payment.PayoutMethod == paid.Value.ToString())
                .Where(p => state switch
                {
                    "standing" => !p.IsReversed,
                    "reversed" => p.IsReversed,
                    _ => true,
                })
                // The service's order, with a tie-break so two payments of one instant keep their place between pages.
                .OrderByDescending(p => p.Payment.PaidAtUtc).ThenBy(p => p.Payment.Id)
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        }).ForBranchOf(DocumentBranches.PayrollRun, routeKey: "runId");

    // =========================================================================================
    // Payroll › Statutory payments (base: GET /api/hr/payroll/statutory-payments, HrMoneyEndpoints)
    // =========================================================================================

    /// <summary>
    /// What was handed to the fund and the tax office: the base route's filters (branch, account, period year), plus the
    /// screen's payout method and paid-between days. The query and the row are <see cref="WageMoney.ListStatutoryPaymentsAsync"/>'s.
    /// </summary>
    public static void MapStatutoryPaymentsPage(RouteGroupBuilder group) =>
        group.MapGet("/payroll/statutory-payments/page", async (
            Guid? branchId, string? accountCode, int? year, string? method, string? from, string? to,
            int? skip, int? take, PosDbContext db, IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            // WageMoney.ListStatutoryPaymentsAsync, filter for filter.
            var query = db.PayrollStatutoryPayments.AsNoTracking();
            if (branchId is Guid bid) query = query.Where(p => p.BranchId == bid);
            if (!string.IsNullOrWhiteSpace(accountCode)) query = query.Where(p => p.AccountCode == accountCode);
            if (year is int y) query = query.Where(p => p.PeriodYear == y);

            if (Method(method) is PayoutMethod paid) query = query.Where(p => p.PayoutMethod == paid);
            var (fromUtc, toUtc) = Days(from, to, regional);
            if (fromUtc is DateTime start) query = query.Where(p => p.PaidAtUtc >= start);
            if (toUtc is DateTime end) query = query.Where(p => p.PaidAtUtc < end);

            return Results.Ok(await query
                .OrderByDescending(p => p.PaidAtUtc).ThenBy(p => p.Id)
                .ToPageAsync(skip, take, async rows =>
                {
                    // WageMoney.ListStatutoryPaymentsAsync's shaping: the branch's and the account's French names.
                    var branches = await BranchNamesAsync(db, rows.Select(p => p.BranchId), ct);
                    var accounts = await db.LedgerAccounts.AsNoTracking()
                        .Where(a => PayrollPosting.StatutoryLiabilities.Contains(a.Code))
                        .ToDictionaryAsync(a => a.Code, a => a.NameFr, ct);
                    return (IReadOnlyList<PayrollStatutoryPaymentDto>)rows.Select(p => new PayrollStatutoryPaymentDto(
                        p.Id, p.BranchId, branches.GetValueOrDefault(p.BranchId) ?? "",
                        p.AccountCode, accounts.GetValueOrDefault(p.AccountCode) ?? "",
                        p.Amount, p.PaidAtUtc, p.PayoutMethod.ToString(),
                        p.CashBoxId, p.PeriodYear, p.PeriodMonth,
                        p.Reference, p.Note, p.CreatedBy, p.LedgerEntryId)).ToList();
                }, ct));
        });

    // =========================================================================================
    // Attendance › Overtime (base: GET /api/hr/attendance/overtime, HrOvertimeEndpoints)
    // =========================================================================================

    /// <summary>
    /// A branch's month of overtime entries, a page at a time, for everyone or one employee — the base route's own
    /// <c>employeeId</c>, the screen's employee filter. The month's head (locked by which run, the kinds of work) and
    /// the hours per kind over every entry kept come in the summary.
    /// </summary>
    public static void MapOvertimePage(RouteGroupBuilder overtime) =>
        overtime.MapGet("/page", async (
            Guid branchId, int year, int month, Guid? employeeId, int? skip, int? take,
            [FromServices] OvertimeEntries entries, CancellationToken ct) =>
        {
            var result = await entries.ListAsync(branchId, year, month, employeeId, ct);
            if (!result.Ok) return HrHttp.Problem(result);
            var list = result.Value!;
            var totals = list.Kinds
                .Select(k => new OvertimeTotal(k.Key, list.Entries.Where(e => e.RuleKey == k.Key).Sum(e => e.Hours)))
                .ToList();
            var summary = new OvertimeSummary(
                list.BranchId, list.Year, list.Month, list.Locked, list.RunNumber, list.RunStatus, list.Kinds, totals);
            return Results.Ok(ListPaging.Of(list.Entries, skip, take, summary));
        });

    /// <summary>The month's head, as the base route sends it beside the entries, and the hours per kind of work.</summary>
    public sealed record OvertimeSummary(
        Guid BranchId, int Year, int Month, bool Locked, string? RunNumber, string? RunStatus,
        IReadOnlyList<OvertimeColumnDto> Kinds, IReadOnlyList<OvertimeTotal> Totals);

    /// <summary>Hours (or days) of one kind of work over every entry the filter keeps.</summary>
    public sealed record OvertimeTotal(string Key, decimal Hours);

    // =========================================================================================
    // Attendance › Clock (base: GET /api/hr/attendance/clock, HrAttendanceEndpoints.MapClock)
    // =========================================================================================

    /// <summary>
    /// A month of punches for a branch or one employee, through the base route's <see cref="AttendanceSheet.ReadClockAsync"/>
    /// — paired in C#, a day either side — with the screen's direction, paired/unpaired and day filters. Each row
    /// carries its person and whether it found a partner; the summary counts the month's punches and the unpaired ones.
    /// </summary>
    public static void MapClockPage(RouteGroupBuilder attendance) =>
        attendance.MapGet("/clock/page", async (
            Guid? employeeId, Guid? branchId, int year, int month, string? kind, string? pair, string? from, string? to,
            int? skip, int? take, [FromServices] AttendanceSheet sheet, HttpContext http, PosDbContext db,
            IRegionalSettingsProvider regional, CancellationToken ct) =>
        {
            // The base route's guard: a named employee of another branch is not found.
            if (employeeId is Guid one && await HrHttp.ForeignEmployeesAsync(http, db, [one], ct)) return Results.NotFound();
            var result = await sheet.ReadClockAsync(employeeId, branchId, year, month, MaxClockRows, ct);
            if (!result.Ok) return HrHttp.Problem(result);

            var log = result.Value!;
            var unpaired = log.Unpaired.Select(e => e.Id).ToHashSet();
            var people = log.People.ToDictionary(p => p.EmployeeId);
            var (fromUtc, toUtc) = Days(from, to, regional);
            var rows = log.Events
                .Where(e => string.IsNullOrEmpty(kind) || kind == "all" || string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .Where(e => pair switch
                {
                    "paired" => !unpaired.Contains(e.Id),
                    "unpaired" => unpaired.Contains(e.Id),
                    _ => true,
                })
                .Where(e => (fromUtc is not DateTime start || e.AtUtc >= start) && (toUtc is not DateTime end || e.AtUtc < end))
                .OrderBy(e => e.AtUtc).ThenBy(e => e.Id)
                .Select(e =>
                {
                    people.TryGetValue(e.EmployeeId, out var person);
                    return new ClockRow(e.Id, e.EmployeeId, e.AtUtc, e.Kind, e.Source, e.CreatedBy,
                        person?.EmployeeNumber ?? "", person?.EmployeeName ?? "", unpaired.Contains(e.Id));
                })
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take, new ClockSummary(log.Events.Count, unpaired.Count)));
        });

    /// <summary>A punch (<see cref="ClockEventDto"/>'s fields), its person, and whether it found a partner.</summary>
    public sealed record ClockRow(
        Guid Id, Guid EmployeeId, DateTime AtUtc, string Kind, string? Source, string? CreatedBy,
        string EmployeeNumber, string EmployeeName, bool Unpaired);

    /// <summary>The month's punches before the screen's filters, and how many of them never found a partner.</summary>
    public sealed record ClockSummary(int Events, int Unpaired);

    // =========================================================================================
    // Attendance › Review (base: GET /api/hr/attendance/review, HrAttendanceEndpoints.MapReview)
    // =========================================================================================

    /// <summary>
    /// A month of punched days read against the schedule, through the base route's <see cref="AttendanceReview.ReadAsync"/>,
    /// with the screen's person filter (<c>person</c>, inside the branch) and its "to check" filter (<c>flag</c>): the
    /// points the column shows — late, early leave, missing exit, possible overtime, rest-day work — or <c>none</c>.
    /// The summary carries the month's day count and its people, which name the rows and the person filter.
    /// </summary>
    public static void MapReviewPage(RouteGroupBuilder attendance) =>
        attendance.MapGet("/review/page", async (
            Guid? employeeId, Guid? branchId, int year, int month, bool? flagged, Guid? person, string? flag,
            int? skip, int? take, [FromServices] AttendanceReview review, HttpContext http, PosDbContext db,
            CancellationToken ct) =>
        {
            // The base route's guard: a named employee takes precedence over the branch in the review's query.
            if (employeeId is Guid one && await HrHttp.ForeignEmployeesAsync(http, db, [one], ct)) return Results.NotFound();
            var result = await review.ReadAsync(employeeId, branchId, year, month, flagged ?? false, ct);
            if (!result.Ok) return HrHttp.Problem(result);

            var read = result.Value!;
            var days = read.Days
                .Where(d => person is not Guid who || d.EmployeeId == who)
                .Where(d => FlagMatches(d, flag))
                .ToList();
            return Results.Ok(ListPaging.Of(days, skip, take, new ReviewSummary(read.Year, read.Month, read.Days.Count, read.People)));
        });

    /// <summary>The month read, how many days it holds before the screen's filters, and the people it names.</summary>
    public sealed record ReviewSummary(int Year, int Month, int Days, IReadOnlyList<ClockPersonDto> People);

    /// <summary>
    /// The points the review's "to check" column shows for a day (attendance-review.component.ts <c>rowFlags</c>), so
    /// the filter and the column never disagree.
    /// </summary>
    public static IReadOnlyList<string> DayFlags(AttendanceReviewDayDto day)
    {
        var flags = new List<string>(5);
        if (day.LateMinutes > 0) flags.Add("late");
        if (day.EarlyLeaveMinutes > 0) flags.Add("earlyLeave");
        if (day.MissingCheckOut) flags.Add("missingOut");
        if (day.PossibleOvertimeMinutes > 0) flags.Add("possibleOvertime");
        if (day.WorkedOnRestDay) flags.Add("restDayWork");
        return flags;
    }

    private static bool FlagMatches(AttendanceReviewDayDto day, string? flag)
    {
        if (string.IsNullOrEmpty(flag) || flag == "all") return true;
        var flags = DayFlags(day);
        return flag == "none" ? flags.Count == 0 : flags.Contains(flag);
    }

    // =========================================================================================
    // Administration › Users (base: GET /api/users, UsersEndpoints)
    // =========================================================================================

    /// <summary>
    /// The accounts, by username as the base route lists them, with the screen's role, branch (<c>none</c> for an
    /// account with none), state and linked-to-an-employee filters. A role the company made is asked for by its id;
    /// a system role, or none, by the role word (<c>Admin</c>, <c>Manager</c>, <c>Cashier</c>) — the column's words.
    /// The rows are shaped exactly as <c>GET /api/users</c> shapes them; the summary lists the roles the accounts are
    /// on, for the role filter.
    /// </summary>
    public static void MapUsersPage(RouteGroupBuilder group) =>
        group.MapGet("/page", async (
            string? role, string? branch, string? state, string? link, Guid? id, int? skip, int? take,
            PosDbContext db, CancellationToken ct) =>
        {
            // GET /api/users: every account not removed, by username.
            var query = db.Users.AsNoTracking().Where(u => !u.IsDeleted);
            var roles = await db.Roles.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);

            // The roles the accounts are on, as the column names them: a company role by its name, else the role word.
            var held = await query.Select(u => new { u.RoleId, u.Role }).Distinct().ToListAsync(ct);
            var options = held
                .Select(h => h.RoleId is Guid rid && roles.TryGetValue(rid, out var r) && r.Key is null
                    ? new UserRoleOption(rid.ToString(), r.Name)
                    : new UserRoleOption(h.Role.ToString(), null))
                .Distinct()
                .OrderBy(o => o.Name ?? o.Value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            // The branches the accounts work at: the screen offers these beside the active ones, as it did.
            var branchesHeld = await query.Where(u => u.BranchId != null).Select(u => u.BranchId!.Value).Distinct().ToListAsync(ct);

            if (id is Guid one) query = query.Where(u => u.Id == one);
            if (Guid.TryParse(role, out var roleId))
                query = query.Where(u => u.RoleId == roleId);
            else if (Enum.TryParse<UserRole>(role, true, out var word) && !string.IsNullOrWhiteSpace(role))
                query = query.Where(u => u.Role == word
                    && (u.RoleId == null || !db.Roles.Any(r => r.Id == u.RoleId && r.Key == null)));
            if (branch == "none") query = query.Where(u => u.BranchId == null);
            else if (Guid.TryParse(branch, out var branchOf)) query = query.Where(u => u.BranchId == branchOf);
            query = state switch
            {
                "active" => query.Where(u => u.IsActive),
                "inactive" => query.Where(u => !u.IsActive),
                _ => query,
            };
            query = link switch
            {
                "linked" => query.Where(u => u.EmployeeId != null),
                "unlinked" => query.Where(u => u.EmployeeId == null),
                _ => query,
            };

            return Results.Ok(await query.OrderBy(u => u.Username).ThenBy(u => u.Id)
                .ToPageAsync(skip, take, async users =>
                {
                    // UsersEndpoints GET /: the same reads, for the page's accounts only.
                    var effective = await UserRoleWriter.EffectiveAsync(db, users, ct);
                    var tills = await OwnTillsAsync(db, users.Select(u => u.Id).ToList(), ct);
                    var numbers = await EmployeeNumbersAsync(db, users, ct);
                    var untaxedRoles = await UntaxedSalesGrant.RolesGrantingAsync(db, ct);
                    var own = await UserGrantEditor.OwnAsync(db, users.Select(u => u.Id).ToList(), ct);
                    return (IReadOnlyList<UserAdminDto>)users.Select(u => UserRow(u, effective[u.Id], tills.GetValueOrDefault(u.Id),
                        numbers, untaxedRoles, roles, own.GetValueOrDefault(u.Id, []))).ToList();
                }, ct, new UsersSummary(options, branchesHeld)));
        });

    /// <summary>A role the role filter offers: a company role's id and name, or a role word and no name.</summary>
    public sealed record UserRoleOption(string Value, string? Name);

    /// <summary>The roles the accounts are on, and the branches they work at — over every account, not the page.</summary>
    public sealed record UsersSummary(IReadOnlyList<UserRoleOption> Roles, IReadOnlyList<Guid> Branches);

    // =========================================================================================
    // Administration › Roles (base: GET /api/roles, RolesEndpoints)
    // =========================================================================================

    /// <summary>
    /// The roles in <see cref="RoleEditor.ListAsync"/>'s order (the system roles first, then the company's by name),
    /// with the screen's scope, state and system/custom filters; <c>id</c> reads one for the editor.
    /// </summary>
    public static void MapRolesPage(RouteGroupBuilder group) =>
        group.MapGet("/page", async (
            string? scope, string? state, string? kind, Guid? id, int? skip, int? take,
            [FromServices] RoleEditor roles, CancellationToken ct) =>
        {
            var rows = (await roles.ListAsync(ct))
                .Where(r => id is not Guid one || r.Id == one)
                .Where(r => string.IsNullOrEmpty(scope) || scope == "all" || string.Equals(r.Scope, scope, StringComparison.OrdinalIgnoreCase))
                .Where(r => state switch
                {
                    "active" => r.IsActive,
                    "inactive" => !r.IsActive,
                    _ => true,
                })
                .Where(r => kind switch
                {
                    "system" => r.IsSystem,
                    "custom" => !r.IsSystem,
                    _ => true,
                })
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        });

    // =========================================================================================
    // Administration › Branches (base: GET /api/catalog/branches, BranchEndpoints)
    // =========================================================================================

    /// <summary>
    /// The branches and warehouses in the base route's order, with the screen's kind and state (<c>setup</c>: not yet
    /// open for operation) filters. A caller without company scope sees their own branch, which is what the screen
    /// showed them (BranchLock.scope), now decided here. The summary carries the main branch and the main warehouse of
    /// that list, whatever the page: the editor says which one loses the flag on a save.
    /// </summary>
    public static void MapBranchesPage(RouteGroupBuilder cash) =>
        cash.MapGet("/page", async (
            string? kind, string? state, Guid? id, int? skip, int? take, HttpContext http, PosDbContext db,
            CancellationToken ct) =>
        {
            // GET /api/catalog/branches: every branch not removed.
            var query = db.Branches.AsNoTracking().Where(b => !b.IsDeleted);
            if (!BranchAssignment.IsCompanyScoped(http.User) && await BranchAssignment.ForCallerAsync(http.User, db, ct) is Guid own)
                query = query.Where(b => b.Id == own);

            var mains = (await query.Where(b => b.IsMain).ToListAsync(ct)).Select(BranchResponse.Of).ToList();

            if (id is Guid one) query = query.Where(b => b.Id == one);
            query = kind switch
            {
                "branch" => query.Where(b => !b.IsWarehouse),
                "warehouse" => query.Where(b => b.IsWarehouse),
                _ => query,
            };
            query = state switch
            {
                "active" => query.Where(b => b.IsActive),
                "inactive" => query.Where(b => !b.IsActive),
                "setup" => query.Where(b => !b.IsOpenForOperation),
                _ => query,
            };

            return Results.Ok(await query
                .OrderByDescending(b => b.IsMain).ThenBy(b => b.IsWarehouse).ThenBy(b => b.NameFr).ThenBy(b => b.Id)
                .ToPageAsync(skip, take,
                    rows => Task.FromResult<IReadOnlyList<BranchResponse>>(rows.Select(BranchResponse.Of).ToList()),
                    ct, new BranchesSummary(mains)));
        });

    /// <summary>The main branch and the main warehouse among the branches the caller sees.</summary>
    public sealed record BranchesSummary(IReadOnlyList<BranchResponse> Mains);

    // =========================================================================================
    // Administration › Tills (base: GET /api/devices, DevicesEndpoints)
    // =========================================================================================

    /// <summary>
    /// The tills by code, the base route's query copied with its <c>branchId</c> (the screen's branch filter; the
    /// caller's own branch for everyone but the Admin), plus the screen's state and synced/never filters.
    /// </summary>
    public static void MapDevicesPage(RouteGroupBuilder group) =>
        group.MapGet("/page", async (
            Guid? branchId, string? state, string? sync, int? skip, int? take, PosDbContext db, CancellationToken ct) =>
        {
            // DevicesEndpoints GET /, filter for filter.
            var query = db.Devices.AsNoTracking().Where(d => branchId == null || d.BranchId == branchId);
            query = state switch
            {
                "active" => query.Where(d => d.IsActive),
                "inactive" => query.Where(d => !d.IsActive),
                _ => query,
            };
            query = sync switch
            {
                "synced" => query.Where(d => d.LastSyncAtUtc != null),
                "never" => query.Where(d => d.LastSyncAtUtc == null),
                _ => query,
            };

            return Results.Ok(await query
                .Join(db.Branches.AsNoTracking(), d => d.BranchId, b => b.Id, (d, b) => new { d, b })
                .OrderBy(x => x.d.Code).ThenBy(x => x.d.Id)
                .Select(x => new DeviceAdminDto(
                    x.d.Id, x.d.Code, x.d.Name, x.d.BranchId, x.b.NameFr, x.d.IsActive, x.d.LastSyncAtUtc, x.d.LastCursor,
                    x.b.IsWarehouse))
                .ToPageAsync(skip, take, ct));
        });

    // =========================================================================================
    // Platform › Language packs (base: GET /api/platform/language-packs, PlatformEndpoints — operator policy)
    // =========================================================================================

    /// <summary>
    /// The language packs, as the base route lists them, with the screen's direction filter. Mapped on the platform
    /// group: the operator policy and no-store, never a company permission.
    /// </summary>
    public static void MapLanguagePacksPage(RouteGroupBuilder group) =>
        group.MapGet("/language-packs/page", async (
            string? direction, int? skip, int? take, PlatformStore store, CancellationToken ct) =>
        {
            var rows = (await store.ListPacksAsync(ct))
                .Where(x => direction switch
                {
                    "rtl" => x.Pack.Rtl,
                    "ltr" => !x.Pack.Rtl,
                    _ => true,
                })
                .Select(x => new LanguagePackRow(
                    x.Pack.Code, x.Pack.Label, x.Pack.Rtl, x.Pack.FallbackLanguage, x.Pack.Strings.Count, x.Revision))
                .ToList();
            return Results.Ok(ListPaging.Of(rows, skip, take));
        });

    /// <summary>One pack as the base route lists it (the same fields, the strings counted).</summary>
    public sealed record LanguagePackRow(string Code, string Label, bool Rtl, string FallbackLanguage, int Strings, long Revision);

    // =========================================================================================
    // Shared
    // =========================================================================================

    /// <summary>A payout method off the query, or null for none (or "all").</summary>
    private static PayoutMethod? Method(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Enum.TryParse<PayoutMethod>(value, true, out var method)
            && Enum.IsDefined(method) ? method : null;

    /// <summary>
    /// From and to as the company's days, <c>to</c> included: the instant <paramref name="from"/> starts and the instant
    /// the day after <paramref name="to"/> starts. A value that is not an ISO day is no bound.
    /// </summary>
    private static (DateTime? From, DateTime? To) Days(string? from, string? to, IRegionalSettingsProvider regional)
    {
        var hasFrom = CompanyDays.TryParseDay(from, out var first);
        var hasTo = CompanyDays.TryParseDay(to, out var last);
        if (!hasFrom && !hasTo) return (null, null);
        var zone = CompanyDays.Zone(regional.Get().TimeZone);
        DateTime? start = hasFrom ? CompanyDays.StartUtc(first, zone) : null;
        DateTime? end = hasTo ? CompanyDays.StartUtc(last.AddDays(1), zone) : null;
        return (start, end);
    }

    private static async Task<Dictionary<Guid, string>> BranchNamesAsync(
        PosDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.Branches.AsNoTracking()
            .Where(b => wanted.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);
    }

    /// <summary>The run's header, as HrPayrollEndpoints maps a list row (no lines, no adjustments, nothing paid).</summary>
    private static PayrollRunDto RunHeader(PayrollRun run, string? branchName) =>
        new(run.Id, run.Number, run.BranchId, branchName ?? "",
            run.PeriodYear, run.PeriodMonth,
            run.Status.ToString(),
            run.HeadCount, run.CurrencyDecimals, run.IsProvisional,
            run.Gross, run.SocialBase, run.SocialEmployee, run.SocialEmployer,
            run.TaxBase, run.IncomeTax, run.EmployerLevies,
            run.AdvancesRecovered, run.OtherDeductions, run.Net,
            0m,
            Utc(run.ComputedAtUtc), run.LockedBy,
            Utc(run.ApprovedAtUtc), run.ApprovedBy, Utc(run.PaidAtUtc),
            Utc(run.ReversedAtUtc), run.ReversedBy, run.ReversalReason,
            run.LedgerEntryId, run.ReversalEntryId,
            run.CreatedBy, Utc(run.CreatedAtUtc),
            Lines: null, Adjustments: null, Warnings: null, PaidByVoucher: run.PaidByVoucher);

    /// <summary>A stamp stored as UTC, marked as UTC whatever the provider returned (as HrPayrollEndpoints does).</summary>
    private static DateTime? Utc(DateTime? value) =>
        value is null ? null
        : value.Value.Kind == DateTimeKind.Utc ? value
        : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    // ---------- copied from UsersEndpoints, which keeps them private ----------

    /// <summary>The HR number of each linked user's employee, by employee id (UsersEndpoints.EmployeeNumbersAsync).</summary>
    private static async Task<Dictionary<Guid, string>> EmployeeNumbersAsync(PosDbContext db, IEnumerable<User> users, CancellationToken ct)
    {
        var ids = users.Where(u => u.EmployeeId != null).Select(u => u.EmployeeId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
    }

    /// <summary>Each user's own till: its code, and whether it is switched on (UsersEndpoints.OwnTillsAsync).</summary>
    private static async Task<Dictionary<Guid, (string Code, bool IsActive)>> OwnTillsAsync(PosDbContext db, List<Guid> userIds, CancellationToken ct) =>
        (await db.Devices.AsNoTracking()
            .Where(d => d.UserId != null && userIds.Contains(d.UserId!.Value))
            .Select(d => new { UserId = d.UserId!.Value, d.Code, d.IsActive })
            .ToListAsync(ct))
        .GroupBy(d => d.UserId)
        .ToDictionary(g => g.Key, g => g.Select(d => (d.Code, d.IsActive)).OrderByDescending(d => d.IsActive).First());

    /// <summary>One account's row (UsersEndpoints.Map).</summary>
    private static UserAdminDto UserRow(User u, IReadOnlySet<string> effective, (string Code, bool IsActive) till,
        IReadOnlyDictionary<Guid, string> employeeNumbers, IReadOnlySet<Guid> untaxedRoles, IReadOnlyDictionary<Guid, Role> roles,
        IReadOnlyList<string> own) =>
        new(u.Id, u.Username, u.FullName, u.Role.ToString(), u.IsActive, u.Version, u.IsDeleted,
            Permissions.Parse(u.Permissions).ToList(),
            ServerAuthorization.LegacyPermissions(effective).ToList(),
            u.BranchId,
            till.Code, till.Code is not null && till.IsActive,
            u.LoginId, u.EmployeeId,
            u.EmployeeId is Guid employee ? employeeNumbers.GetValueOrDefault(employee) : null,
            effective.Contains(PermissionCatalog.SalesUntaxed),
            u.RoleId is Guid roleId && untaxedRoles.Contains(roleId),
            u.RoleId,
            u.RoleId is Guid named ? roles.GetValueOrDefault(named)?.Name : null,
            u.RoleId is Guid scoped ? roles.GetValueOrDefault(scoped)?.Scope.ToString() : null,
            own,
            u.AuthorizationVersion,
            TwoFactor.IsOn(u));
}
