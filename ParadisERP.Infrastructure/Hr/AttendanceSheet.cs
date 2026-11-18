using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Payroll;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = ParadisERP.Application.Payroll;

namespace ParadisERP.Infrastructure.Hr;

/// <summary>
/// Attendance: the foreman's sheet, the day grid, the clock — and the one summary shape the
/// payroll engine is fed, whichever of the three captured the month (docs/07 §5.5).
///
/// <para>
/// <b>The summary is the contract.</b> Everything else here is capture. A declared month, a
/// month of ticked days and a month of clock punches all leave this class as the same
/// <c>AttendanceSummary</c>, so the engine has one rule per figure instead of three, and the
/// daily basis — which cannot run without days — cannot take its number from whichever path
/// happened to be filled.
/// </para>
/// <para>
/// <b>The period lock.</b> Attendance for a month whose run is Locked, Approved or Paid is
/// refused with the run named, never silently ignored and never allowed to drift away from the
/// payslips already computed from it. A Draft run locks nothing: entering the last of the days
/// after a first look at the figures is ordinary work, and the run recomputes.
/// </para>
/// </summary>
public sealed class AttendanceSheet(
    PosDbContext db,
    EmployeeDirectory employees,
    PayrollSettingsStore settings,
    IRegionalSettingsProvider regional)
{
    /// <summary>The period's run, and whether it holds the month closed.</summary>
    /// <param name="Locked">Locked, Approved or Paid. Draft is open; Reversed is not a live run at all.</param>
    public sealed record PeriodLock(PayrollRun? Run, bool Locked)
    {
        public string? Number => Run?.Number;
        public string? Status => Run?.Status.ToString();
    }

    // ---------- the sheet ----------

    public async Task<HrResult<AttendanceSheetDto>> ReadSheetAsync(
        Guid branchId, int year, int month, int skip, int take, string? q, CancellationToken ct = default)
    {
        if (!AttendancePeriod.IsValid(year, month))
            return HrResult<AttendanceSheetDto>.Invalid(HrProblems.PeriodInvalid, "That is not a month.");
        if (!await db.Branches.AnyAsync(b => b.Id == branchId, ct))
            return HrResult<AttendanceSheetDto>.Invalid(HrProblems.BranchUnknown, "That branch does not exist.");

        var period = new AttendancePeriod(year, month);
        var profile = (await settings.ReadAsync(ct)).Settings;

        var query = employees.Payable(branchId, period);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(e => e.Number.Contains(term) || e.FullName.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var clampedSkip = Math.Max(0, skip);
        var clampedTake = Math.Clamp(take, 1, 1000);
        var page = await query.OrderBy(e => e.Number).Skip(clampedSkip).Take(clampedTake).ToListAsync(ct);

        var captured = await CaptureAsync(page, period, profile, ct);
        var lockState = await LockOfAsync(branchId, period, ct);

        return HrResult<AttendanceSheetDto>.Success(Sheet(
            branchId, period, lockState, profile, total, clampedSkip, clampedTake, page, captured));
    }

    /// <summary>
    /// One save of the sheet. Every row is checked before anything is written, and a single bad
    /// row refuses the lot: a half-saved sheet would leave the screen explaining which rows
    /// landed and the clerk retyping the rest.
    /// </summary>
    public async Task<HrResult<AttendanceSheetDto>> SaveSheetAsync(
        UpsertAttendanceSheetRequest r, string? by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(r);

        if (!AttendancePeriod.IsValid(r.PeriodYear, r.PeriodMonth))
            return HrResult<AttendanceSheetDto>.Invalid(HrProblems.PeriodInvalid, "That is not a month.");
        if (r.Rows is null || r.Rows.Count == 0)
            return HrResult<AttendanceSheetDto>.Invalid(HrProblems.RowsRequired, "There is nothing to save.");
        if (r.Rows.Select(row => row.EmployeeId).Distinct().Count() != r.Rows.Count)
            return HrResult<AttendanceSheetDto>.Invalid(HrProblems.DuplicateRow, "An employee appears twice.");

        var period = new AttendancePeriod(r.PeriodYear, r.PeriodMonth);
        var profile = (await settings.ReadAsync(ct)).Settings;
        var rules = profile.OvertimeRules.Where(rule => rule.IsActive)
            .Select(rule => rule.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dayBands = DayBands(profile);

        // One transaction around the lock check, the row checks and the upserts: a run locked
        // between the check and the write would otherwise have its figures moved underneath it.
        await using var scope = await db.BeginWriteScopeAsync(ct);

        var lockState = await LockOfAsync(r.BranchId, period, ct);
        if (lockState.Locked) return Locked<AttendanceSheetDto>(lockState);

        var ids = r.Rows.Select(row => row.EmployeeId).ToList();
        var people = await db.Employees.Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        var byId = people.ToDictionary(e => e.Id);

        foreach (var row in r.Rows)
        {
            if (!byId.TryGetValue(row.EmployeeId, out var employee))
                return HrResult<AttendanceSheetDto>.NotFound(HrProblems.EmployeeNotFound, "No such employee.");
            if (employee.BranchId != r.BranchId)
                return HrResult<AttendanceSheetDto>.Invalid(
                    HrProblems.EmployeeNotInBranch, "That employee is not on this branch's payroll.");
            if (!EmployeeDirectory.CoversPeriod(employee, period))
                return HrResult<AttendanceSheetDto>.Invalid(
                    HrProblems.EmployeeOutsidePeriod, "That employee was not employed in this month.");

            // A recorded month is added up from day rows and punches. Letting the sheet write over
            // it would give one month two answers, and the run would take whichever was written
            // last.
            if (AttendanceModes.Resolve(employee.AttendanceModeOverride, profile.DefaultAttendanceMode)
                == AttendanceMode.Recorded)
                return HrResult<AttendanceSheetDto>.Invalid(
                    HrProblems.RecordedRowNotEditable, "This person's days come from clock records.");

            if (row.DaysPresent < 0 || row.PaidLeaveDays < 0 || row.UnpaidAbsenceDays < 0)
                return HrResult<AttendanceSheetDto>.Invalid(HrProblems.NegativeDays, "Days cannot be negative.");
            if (row.DaysPresent + row.PaidLeaveDays + row.UnpaidAbsenceDays > period.DayCount)
                return HrResult<AttendanceSheetDto>.Invalid(
                    HrProblems.DaysExceedMonth, "That is more days than the month has.");

            foreach (var band in row.Overtime ?? [])
            {
                if (band.Hours < 0)
                    return HrResult<AttendanceSheetDto>.Invalid(
                        HrProblems.NegativeHours, "Hours cannot be negative.", band.RuleKey);
                // Refused rather than stored and ignored: hours entered against a band the
                // settings do not define are hours somebody worked and nobody paid.
                if (band.Hours > 0 && !rules.Contains(band.RuleKey ?? ""))
                    return HrResult<AttendanceSheetDto>.Invalid(
                        HrProblems.OvertimeRuleUnknown, "That overtime band is not defined.", band.RuleKey);
                // A band counted in days (a holiday or rest day worked) cannot hold more days than
                // the month has.
                if (dayBands.Contains(band.RuleKey ?? "") && band.Hours > period.DayCount)
                    return HrResult<AttendanceSheetDto>.Invalid(
                        HrProblems.DaysExceedMonth, "That is more days than the month has.", band.RuleKey);
            }
        }

        var stored = await db.AttendanceDeclarations
            .Include(d => d.Overtime)
            .Where(d => ids.Contains(d.EmployeeId)
                && d.PeriodYear == r.PeriodYear && d.PeriodMonth == r.PeriodMonth)
            .ToListAsync(ct);

        foreach (var row in r.Rows)
        {
            var declaration = stored.FirstOrDefault(d => d.EmployeeId == row.EmployeeId);
            if (declaration is null)
            {
                declaration = new AttendanceDeclaration
                {
                    EmployeeId = row.EmployeeId, PeriodYear = r.PeriodYear, PeriodMonth = r.PeriodMonth
                };
                db.AttendanceDeclarations.Add(declaration);
            }

            declaration.DaysPresent = row.DaysPresent;
            declaration.PaidLeaveDays = row.PaidLeaveDays;
            declaration.UnpaidAbsenceDays = row.UnpaidAbsenceDays;
            declaration.UpdatedBy = Trim(by, 200);
            declaration.UpdatedAtUtc = DateTime.UtcNow;

            // Replaced, never appended: the sheet is a statement of the month, so a band cleared
            // to zero has to disappear rather than go on paying last week's figure.
            db.AttendanceDeclarationOvertimes.RemoveRange(declaration.Overtime);
            declaration.Overtime.Clear();
            foreach (var (key, hours) in Bands(row.Overtime))
            {
                var band = new AttendanceDeclarationOvertime
                {
                    DeclarationId = declaration.Id, RuleKey = key, Hours = hours
                };
                // Added to the set by name, not only to the navigation. A child reached through
                // the navigation of an *existing* parent carries a key value already, which EF
                // reads as "this row exists" — the insert comes out as an UPDATE that matches
                // nothing and the save throws a concurrency exception the clerk cannot act on.
                db.AttendanceDeclarationOvertimes.Add(band);
                declaration.Overtime.Add(band);
            }
        }

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        // The rows just saved, read back through the same summariser the run uses, so the screen
        // shows what the payslip will use rather than what it typed.
        var saved = people.OrderBy(e => e.Number).ToList();
        var captured = await CaptureAsync(saved, period, profile, ct);
        return HrResult<AttendanceSheetDto>.Success(Sheet(
            r.BranchId, period, lockState, profile, saved.Count, 0, saved.Count, saved, captured));
    }

    // ---------- the day grid ----------

    public async Task<HrResult<AttendanceDaysDto>> ReadDaysAsync(
        Guid employeeId, int year, int month, CancellationToken ct = default)
    {
        if (!AttendancePeriod.IsValid(year, month))
            return HrResult<AttendanceDaysDto>.Invalid(HrProblems.PeriodInvalid, "That is not a month.");

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
            return HrResult<AttendanceDaysDto>.NotFound(HrProblems.EmployeeNotFound, "No such employee.");

        var period = new AttendancePeriod(year, month);
        var profile = (await settings.ReadAsync(ct)).Settings;
        var captured = await CaptureAsync([employee], period, profile, ct);
        var lockState = await LockOfAsync(employee.BranchId, period, ct);
        var state = captured[employee.Id];

        return HrResult<AttendanceDaysDto>.Success(new AttendanceDaysDto(
            employee.Id, employee.Number, employee.FullName, employee.BranchId, year, month,
            lockState.Locked, lockState.Run?.Id, lockState.Number, lockState.Status,
            state.Mode.ToString(), Columns(profile),
            state.Days.Select(Day).ToList(),
            Summary(state.Summary),
            state.Unpaired.Select(p => EmployeeDirectory.Utc(p.AtUtc)).ToList()));
    }

    /// <summary>
    /// Writing part of a month's days — one employee's, because that is what a day grid is.
    ///
    /// <para>
    /// A day carried with <b>no status</b> is deleted: "nothing was recorded" has to be a state
    /// the grid can go back to, or a mis-click becomes a permanent absence on somebody's payslip.
    /// Days the request does not mention are left alone, so the screen may send only what changed.
    /// </para>
    /// </summary>
    public async Task<HrResult<AttendanceDaysDto>> SaveDaysAsync(
        UpsertAttendanceDaysRequest r, string? by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(r);

        if (r.Days is null || r.Days.Count == 0)
            return HrResult<AttendanceDaysDto>.Invalid(HrProblems.RowsRequired, "There is nothing to save.");
        if (r.Days.Select(d => d.EmployeeId).Distinct().Count() > 1)
            return HrResult<AttendanceDaysDto>.Invalid(
                HrProblems.MixedEmployees, "A day grid belongs to one person.");
        if (r.Days.Select(d => d.WorkDate).Distinct().Count() != r.Days.Count)
            return HrResult<AttendanceDaysDto>.Invalid(HrProblems.DuplicateRow, "A day appears twice.");

        var employeeId = r.Days[0].EmployeeId;
        // The period is the days' own month rather than a field beside them: two places for one
        // fact is two places that can disagree, and the disagreement would be a locked month
        // edited through an unlocked one's envelope.
        var period = new AttendancePeriod(r.Days[0].WorkDate.Year, r.Days[0].WorkDate.Month);
        if (r.Days.Any(d => !period.Contains(d.WorkDate)))
            return HrResult<AttendanceDaysDto>.Invalid(
                HrProblems.DayOutsidePeriod, "Those days are not all in one month.");

        var profile = (await settings.ReadAsync(ct)).Settings;
        var rules = profile.OvertimeRules.Where(rule => rule.IsActive)
            .Select(rule => rule.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dayBands = DayBands(profile);

        await using var scope = await db.BeginWriteScopeAsync(ct);

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
            return HrResult<AttendanceDaysDto>.NotFound(HrProblems.EmployeeNotFound, "No such employee.");
        if (!EmployeeDirectory.CoversPeriod(employee, period))
            return HrResult<AttendanceDaysDto>.Invalid(
                HrProblems.EmployeeOutsidePeriod, "That employee was not employed in this month.");

        var mode = AttendanceModes.Resolve(employee.AttendanceModeOverride, profile.DefaultAttendanceMode);
        if (mode == AttendanceMode.Declared)
            return HrResult<AttendanceDaysDto>.Invalid(
                HrProblems.DeclaredHasNoDayGrid, "This person's month is declared as totals, not day by day.");

        var lockState = await LockOfAsync(employee.BranchId, period, ct);
        if (lockState.Locked) return Locked<AttendanceDaysDto>(lockState);

        foreach (var day in r.Days)
        {
            if (string.IsNullOrWhiteSpace(day.Status)) continue;
            if (ParseStatus(day.Status) is null)
                return HrResult<AttendanceDaysDto>.Invalid(HrProblems.DayStatusUnknown, "That is not a day status.");
            if (day.HoursWorked is decimal hours)
            {
                if (hours < 0)
                    return HrResult<AttendanceDaysDto>.Invalid(HrProblems.NegativeHours, "Hours cannot be negative.");
                if (hours > 24)
                    return HrResult<AttendanceDaysDto>.Invalid(HrProblems.HoursExceedDay, "A day has 24 hours.");
            }
            foreach (var band in day.Overtime ?? [])
            {
                if (band.Hours < 0)
                    return HrResult<AttendanceDaysDto>.Invalid(
                        HrProblems.NegativeHours, "Hours cannot be negative.", band.RuleKey);
                if (band.Hours > 0 && !rules.Contains(band.RuleKey ?? ""))
                    return HrResult<AttendanceDaysDto>.Invalid(
                        HrProblems.OvertimeRuleUnknown, "That overtime band is not defined.", band.RuleKey);
                // On one day's row a band counted in days is that day worked or not: at most one.
                if (dayBands.Contains(band.RuleKey ?? "") && band.Hours > 1)
                    return HrResult<AttendanceDaysDto>.Invalid(
                        HrProblems.DaysExceedMonth, "A day-counted band takes at most one day per day.", band.RuleKey);
            }
        }

        var dates = r.Days.Select(d => d.WorkDate).ToList();
        var stored = await db.AttendanceDays
            .Include(d => d.Overtime)
            .Where(d => d.EmployeeId == employeeId && dates.Contains(d.WorkDate))
            .ToListAsync(ct);

        foreach (var input in r.Days)
        {
            var row = stored.FirstOrDefault(d => d.WorkDate == input.WorkDate);

            if (string.IsNullOrWhiteSpace(input.Status))
            {
                if (row is not null)
                {
                    db.AttendanceDayOvertimes.RemoveRange(row.Overtime);
                    db.AttendanceDays.Remove(row);
                }
                continue;
            }

            if (row is null)
            {
                row = new AttendanceDay { EmployeeId = employeeId, WorkDate = input.WorkDate };
                db.AttendanceDays.Add(row);
            }

            row.Status = ParseStatus(input.Status)!.Value;
            row.HoursWorked = input.HoursWorked;
            row.UpdatedBy = Trim(by, 200);
            row.UpdatedAtUtc = DateTime.UtcNow;

            db.AttendanceDayOvertimes.RemoveRange(row.Overtime);
            row.Overtime.Clear();
            foreach (var (key, hours) in Bands(input.Overtime))
            {
                // Added to the set for the same reason the declaration's bands are.
                var band = new AttendanceDayOvertime { DayId = row.Id, RuleKey = key, Hours = hours };
                db.AttendanceDayOvertimes.Add(band);
                row.Overtime.Add(band);
            }
        }

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return await ReadDaysAsync(employeeId, period.Year, period.Month, ct);
    }

    // ---------- the clock ----------

    /// <summary>
    /// Records a punch. An In on top of an open In, or an Out with nothing open, is <b>recorded
    /// and reported</b> — never refused and never mended: the punch happened, and only a foreman
    /// with the sheet in front of him can say what the missing half was.
    /// </summary>
    public async Task<HrResult<ClockedDto>> ClockAsync(
        ClockPunchRequest r, string? by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(r);

        if (ParseKind(r.Kind) is not ClockEventKind kind)
            return HrResult<ClockedDto>.Invalid(HrProblems.ClockKindUnknown, "A punch is In or Out.");
        if (r.EmployeeId is null && string.IsNullOrWhiteSpace(r.EmployeeNumber))
            return HrResult<ClockedDto>.Invalid(HrProblems.EmployeeRequired, "Name the employee or their number.");

        var atUtc = EmployeeDirectory.Utc(r.AtUtc ?? DateTime.UtcNow);
        var zone = Zone();
        var period = AttendancePeriod.Of(atUtc, zone);

        await using var scope = await db.BeginWriteScopeAsync(ct);

        var number = r.EmployeeNumber?.Trim();
        var employee = r.EmployeeId is Guid id
            ? await db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct)
            : await db.Employees.FirstOrDefaultAsync(e => e.Number == number, ct);
        if (employee is null)
            return HrResult<ClockedDto>.NotFound(HrProblems.EmployeeNotFound, "No such employee.");
        if (!EmployeeDirectory.CoversPeriod(employee, period))
            return HrResult<ClockedDto>.Invalid(
                HrProblems.EmployeeOutsidePeriod, "That employee was not employed in this month.");

        var lockState = await LockOfAsync(employee.BranchId, period, ct);
        if (lockState.Locked) return Locked<ClockedDto>(lockState);

        // The punch before this one, whatever day it fell on: a night shift's Out is the next
        // morning, so "the last punch" is the only question worth asking.
        var previous = await db.ClockEvents.AsNoTracking()
            .Where(e => e.EmployeeId == employee.Id && e.AtUtc <= atUtc)
            .OrderByDescending(e => e.AtUtc)
            .FirstOrDefaultAsync(ct);

        var warning = (kind, previous?.Kind) switch
        {
            (ClockEventKind.In, ClockEventKind.In) => HrProblems.ClockRepeatedIn,
            (ClockEventKind.Out, ClockEventKind.Out) => HrProblems.ClockOutWithoutIn,
            (ClockEventKind.Out, null) => HrProblems.ClockOutWithoutIn,
            _ => null
        };

        var punch = new ClockEvent
        {
            EmployeeId = employee.Id,
            AtUtc = atUtc,
            Kind = kind,
            Source = Trim(r.Source, 40),
            CreatedBy = Trim(by, 200)
        };
        db.ClockEvents.Add(punch);

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return HrResult<ClockedDto>.Success(
            new ClockedDto(Event(punch), Person(employee), warning), warning);
    }

    /// <summary>
    /// Who a badge number belongs to, read before anything is recorded: the clock asks the person
    /// at the counter to confirm the name, the direction and the time first (owner, 2026-10-10).
    /// The same lookup and the same refusals as <see cref="ClockAsync"/>, so a number the clock
    /// would refuse is refused here, before the question is asked.
    /// </summary>
    public async Task<HrResult<ClockPersonDto>> FindClockPersonAsync(
        string? employeeNumber, DateTime? atUtc = null, CancellationToken ct = default)
    {
        var number = employeeNumber?.Trim();
        if (string.IsNullOrEmpty(number))
            return HrResult<ClockPersonDto>.Invalid(HrProblems.EmployeeRequired, "Name the employee or their number.");

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Number == number, ct);
        if (employee is null)
            return HrResult<ClockPersonDto>.NotFound(HrProblems.EmployeeNotFound, "No such employee.");
        var period = AttendancePeriod.Of(EmployeeDirectory.Utc(atUtc ?? DateTime.UtcNow), Zone());
        if (!EmployeeDirectory.CoversPeriod(employee, period))
            return HrResult<ClockPersonDto>.Invalid(
                HrProblems.EmployeeOutsidePeriod, "That employee was not employed in this month.");

        return HrResult<ClockPersonDto>.Success(Person(employee));
    }

    public async Task<HrResult<ClockLogDto>> ReadClockAsync(
        Guid? employeeId, Guid? branchId, int year, int month, int take, CancellationToken ct = default)
    {
        if (!AttendancePeriod.IsValid(year, month))
            return HrResult<ClockLogDto>.Invalid(HrProblems.PeriodInvalid, "That is not a month.");
        if (employeeId is null && branchId is null)
            return HrResult<ClockLogDto>.Invalid(HrProblems.EmployeeRequired, "Name an employee or a branch.");

        var period = new AttendancePeriod(year, month);
        var zone = Zone();
        var (from, to) = Window(period, zone);

        var query = db.ClockEvents.AsNoTracking().Where(e => e.AtUtc >= from && e.AtUtc < to);
        if (employeeId is Guid one) query = query.Where(e => e.EmployeeId == one);
        // The branch is narrowed in the query rather than after the page is cut, or another
        // branch's punches would fill the page and this one's log would come back short.
        else if (branchId is Guid branch)
            query = query.Where(e => db.Employees.Any(x => x.Id == e.EmployeeId && x.BranchId == branch));

        var rows = await query
            .OrderBy(e => e.AtUtc)
            .Take(Math.Clamp(take, 1, 5000))
            .ToListAsync(ct);

        var staff = await Staff(rows.Select(e => e.EmployeeId).Distinct().ToList(), ct);

        // Pairing runs over the whole window — a day either side — and only then is the month cut
        // out of it, which is the only order in which a night shift is not torn in half.
        var unpaired = rows
            .GroupBy(e => e.EmployeeId)
            .SelectMany(person => AttendanceSummarizer.Pair(person.Select(Punch), zone).Unpaired
                .Where(p => period.Contains(AttendancePeriod.LocalDay(p.AtUtc, zone)))
                .Select(p => person.First(e => e.Id == p.Id)))
            .OrderBy(e => e.AtUtc)
            .ToList();

        var inMonth = rows
            .Where(e => period.Contains(AttendancePeriod.LocalDay(e.AtUtc, zone)))
            .ToList();

        return HrResult<ClockLogDto>.Success(new ClockLogDto(
            inMonth.Select(Event).ToList(),
            unpaired.Select(Event).ToList(),
            inMonth.Select(e => e.EmployeeId).Concat(unpaired.Select(e => e.EmployeeId)).Distinct()
                .Select(id => Person(staff[id])).ToList()));
    }

    // ---------- what the run reads ----------

    /// <summary>
    /// One summary per employee, in the shape <c>PayrollEngine</c> takes — the whole point of this
    /// class, and the only thing the run should ask attendance for.
    ///
    /// <para>
    /// An employee with nothing captured comes back as <c>AttendanceSummary.Nothing</c>
    /// (<c>Source = None</c>), which is not a month of zero days: the engine pays a monthly
    /// employee in full and warns, and flags a daily one (project memory, 2026-09-24).
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, Engine.AttendanceSummary>> SummariesAsync(
        IReadOnlyList<Employee> staff, AttendancePeriod period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(staff);
        var profile = (await settings.ReadAsync(ct)).Settings;
        var captured = await CaptureAsync(staff, period, profile, ct);
        var entries = await OvertimeEntryHoursAsync(staff.Select(e => e.Id).ToList(), period, ct);
        return captured.ToDictionary(
            pair => pair.Key,
            pair => entries.TryGetValue(pair.Key, out var hours) ? WithEntries(pair.Value.Summary, hours) : pair.Value.Summary);
    }

    /// <summary>
    /// The month's overtime entries per person and kind (owner, 2026-10-10). Only the run reads
    /// them: the sheet's overtime columns and the day grid stay what was typed into them, so saving
    /// the sheet never writes an entry's hours back as declared hours.
    /// </summary>
    private async Task<Dictionary<Guid, Dictionary<string, decimal>>> OvertimeEntryHoursAsync(
        IReadOnlyCollection<Guid> employeeIds, AttendancePeriod period, CancellationToken ct)
    {
        if (employeeIds.Count == 0) return [];
        var first = period.FirstDay;
        var last = period.LastDay;
        var rows = await db.OvertimeEntries.AsNoTracking()
            .Where(e => employeeIds.Contains(e.EmployeeId) && e.WorkDate >= first && e.WorkDate <= last)
            .Select(e => new { e.EmployeeId, e.RuleKey, e.Hours })
            .ToListAsync(ct);
        // Summed here, not in the database: SQLite stores decimal as text.
        return rows.GroupBy(r => r.EmployeeId).ToDictionary(
            g => g.Key,
            g => g.GroupBy(r => r.RuleKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(k => k.Key, k => k.Sum(r => r.Hours), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>A summary with entries' hours added to its overtime, per kind. Days and source unchanged.</summary>
    private static Engine.AttendanceSummary WithEntries(Engine.AttendanceSummary summary, Dictionary<string, decimal> entries)
    {
        var merged = new Dictionary<string, decimal>(summary.OvertimeHoursByRule ?? new Dictionary<string, decimal>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (key, hours) in entries)
            merged[key] = merged.GetValueOrDefault(key) + hours;
        return summary with { OvertimeHoursByRule = merged };
    }

    /// <summary>
    /// The run holding a month closed, if one does. Public because the run's own transitions read
    /// the same fact, and two answers to "is this month locked" is one too many.
    /// </summary>
    public async Task<PeriodLock> LockOfAsync(Guid branchId, AttendancePeriod period, CancellationToken ct = default)
    {
        // Uniqueness is over non-reversed runs only (project memory, 2026-09-24): a reversed run
        // stays for its posting links and must never lock the month it no longer owns.
        var run = await db.PayrollRuns.AsNoTracking()
            .Where(x => x.BranchId == branchId
                && x.PeriodYear == period.Year && x.PeriodMonth == period.Month
                && x.Status != PayrollRunStatus.Reversed)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        return new PeriodLock(run, run is not null && run.Status != PayrollRunStatus.Draft);
    }

    // ---------- capture, reduced ----------

    /// <summary>One person's month as captured, and as the engine will read it.</summary>
    private sealed record Captured(
        AttendanceMode Mode,
        Engine.AttendanceSummary Summary,
        IReadOnlyList<AttendanceDay> Days,
        IReadOnlyList<ClockPunch> Unpaired,
        string? UpdatedBy,
        DateTime? UpdatedAtUtc);

    private async Task<Dictionary<Guid, Captured>> CaptureAsync(
        IReadOnlyList<Employee> staff, AttendancePeriod period, Engine.PayrollSettings profile, CancellationToken ct)
    {
        var captured = new Dictionary<Guid, Captured>();
        if (staff.Count == 0) return captured;

        var zone = Zone();
        var modes = staff.ToDictionary(
            e => e.Id, e => AttendanceModes.Resolve(e.AttendanceModeOverride, profile.DefaultAttendanceMode));

        var declaredIds = modes.Where(m => m.Value == AttendanceMode.Declared).Select(m => m.Key).ToList();
        var recordedIds = modes.Where(m => m.Value == AttendanceMode.Recorded).Select(m => m.Key).ToList();

        var declarations = declaredIds.Count == 0
            ? new List<AttendanceDeclaration>()
            : await db.AttendanceDeclarations.AsNoTracking().Include(d => d.Overtime)
                .Where(d => declaredIds.Contains(d.EmployeeId)
                    && d.PeriodYear == period.Year && d.PeriodMonth == period.Month)
                .ToListAsync(ct);

        var first = period.FirstDay;
        var last = period.LastDay;
        var days = recordedIds.Count == 0
            ? new List<AttendanceDay>()
            : await db.AttendanceDays.AsNoTracking().Include(d => d.Overtime)
                .Where(d => recordedIds.Contains(d.EmployeeId) && d.WorkDate >= first && d.WorkDate <= last)
                .OrderBy(d => d.WorkDate)
                .ToListAsync(ct);

        var (from, to) = Window(period, zone);
        var punches = recordedIds.Count == 0
            ? new List<ClockEvent>()
            : await db.ClockEvents.AsNoTracking()
                .Where(e => recordedIds.Contains(e.EmployeeId) && e.AtUtc >= from && e.AtUtc < to)
                .OrderBy(e => e.AtUtc)
                .ToListAsync(ct);

        decimal fullDay(DateOnly date) => Engine.WorkScheduleRules.FullDayHours(profile, date);

        foreach (var employee in staff)
        {
            var mode = modes[employee.Id];

            if (mode == AttendanceMode.Declared)
            {
                var declaration = declarations.FirstOrDefault(d => d.EmployeeId == employee.Id);
                captured[employee.Id] = new Captured(
                    mode,
                    declaration is null
                        ? Engine.AttendanceSummary.Nothing
                        : AttendanceSummarizer.FromDeclaration(
                            declaration.DaysPresent, declaration.PaidLeaveDays, declaration.UnpaidAbsenceDays,
                            declaration.Overtime.Select(o => new AttendanceOvertimeEntry(o.RuleKey, o.Hours))),
                    [], [],
                    declaration?.UpdatedBy,
                    EmployeeDirectory.Utc(declaration?.UpdatedAtUtc));
                continue;
            }

            var mine = days.Where(d => d.EmployeeId == employee.Id).ToList();
            var pairing = AttendanceSummarizer.Pair(
                punches.Where(p => p.EmployeeId == employee.Id).Select(Punch), zone);

            // Punches from the day either side are only here so a shift crossing the month
            // boundary pairs; they are not this month's unpaired punches.
            var unpaired = pairing.Unpaired
                .Where(p => period.Contains(AttendancePeriod.LocalDay(p.AtUtc, zone)))
                .ToList();
            var shifts = pairing.Shifts.Where(s => period.Contains(s.LocalDay)).ToList();

            // Ticked days win over punches when both exist: a day row is somebody's answer, and
            // the punches are the raw material it was read from.
            // A full day is the company's hours per day, or a special period's paid hours on a day
            // inside one (Ramadan, owner 2026-10-10).
            var summary = mine.Count > 0
                ? AttendanceSummarizer.FromDays(mine.Select(Entry), fullDay)
                : shifts.Count > 0
                    ? AttendanceSummarizer.FromShifts(shifts, fullDay)
                    : Engine.AttendanceSummary.Nothing;

            captured[employee.Id] = new Captured(
                mode, summary, mine, unpaired,
                mine.OrderByDescending(d => d.UpdatedAtUtc).FirstOrDefault()?.UpdatedBy,
                EmployeeDirectory.Utc(mine.Count == 0 ? null : mine.Max(d => d.UpdatedAtUtc)));
        }

        return captured;
    }

    // ---------- shaping ----------

    private static AttendanceSheetDto Sheet(
        Guid branchId, AttendancePeriod period, PeriodLock lockState, Engine.PayrollSettings profile,
        int total, int skip, int take, IReadOnlyList<Employee> page, Dictionary<Guid, Captured> captured) =>
        new(branchId, period.Year, period.Month,
            lockState.Locked, lockState.Run?.Id, lockState.Number, lockState.Status,
            profile.DefaultAttendanceMode.ToString(),
            Columns(profile),
            total, skip, take,
            page.Select(e => Row(e, captured[e.Id])).ToList());

    private static AttendanceSheetRowDto Row(Employee e, Captured captured) =>
        new(e.Id, e.Number, e.FullName, e.PayBasis.ToString(),
            captured.Mode.ToString(), captured.Mode == AttendanceMode.Recorded,
            captured.Summary.DaysPresent, captured.Summary.PaidLeaveDays, captured.Summary.UnpaidAbsenceDays,
            Overtime(captured.Summary),
            captured.Summary.Source.ToString(), captured.Unpaired.Count,
            captured.UpdatedBy, captured.UpdatedAtUtc);

    private static IReadOnlyList<OvertimeColumnDto> Columns(Engine.PayrollSettings profile) =>
        profile.OvertimeRules
            .Where(rule => rule.IsActive)
            .Select(rule => new OvertimeColumnDto(rule.Key, rule.Labels, rule.Multiplier, rule.Unit.ToString()))
            .ToList();

    /// <summary>The active bands counted in days rather than hours (owner, 2026-10-10).</summary>
    private static HashSet<string> DayBands(Engine.PayrollSettings profile) =>
        profile.OvertimeRules.Where(rule => rule.IsActive && rule.Unit == Engine.OvertimeUnit.Days)
            .Select(rule => rule.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<AttendanceOvertimeDto> Overtime(Engine.AttendanceSummary summary) =>
        summary.OvertimeHoursByRule
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new AttendanceOvertimeDto(pair.Key, pair.Value))
            .ToList();

    private static AttendanceDayDto Day(AttendanceDay d) =>
        new(d.EmployeeId, d.WorkDate, d.Status.ToString(), d.HoursWorked,
            d.Overtime.OrderBy(o => o.RuleKey, StringComparer.Ordinal)
                .Select(o => new AttendanceOvertimeDto(o.RuleKey, o.Hours)).ToList(),
            d.UpdatedBy, EmployeeDirectory.Utc(d.UpdatedAtUtc));

    private static AttendanceSummaryDto Summary(Engine.AttendanceSummary s) =>
        new(s.DaysPresent, s.PaidLeaveDays, s.UnpaidAbsenceDays, Overtime(s), s.Source.ToString());

    private static ClockEventDto Event(ClockEvent e) =>
        new(e.Id, e.EmployeeId, EmployeeDirectory.Utc(e.AtUtc), e.Kind.ToString(), e.Source, e.CreatedBy);

    private static ClockPersonDto Person(Employee e) => new(e.Id, e.Number, e.FullName);

    private static AttendanceDayEntry Entry(AttendanceDay d) =>
        new(d.WorkDate, d.Status, d.HoursWorked,
            d.Overtime.Select(o => new AttendanceOvertimeEntry(o.RuleKey, o.Hours)).ToList());

    private static ClockPunch Punch(ClockEvent e) => new(e.Id, EmployeeDirectory.Utc(e.AtUtc), e.Kind);

    // ---------- helpers ----------

    /// <summary>
    /// The bands actually worth storing: keys folded to lower case, hours summed when a request
    /// names a band twice, and anything at or below zero left out — a cleared band is an absent
    /// row, not a row of zero.
    /// </summary>
    private static IEnumerable<(string Key, decimal Hours)> Bands(IReadOnlyList<AttendanceOvertimeDto>? overtime) =>
        (overtime ?? [])
            .Where(band => band.Hours > 0 && !string.IsNullOrWhiteSpace(band.RuleKey))
            .GroupBy(band => band.RuleKey.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Select(g => (g.Key, g.Sum(band => band.Hours)));

    private async Task<Dictionary<Guid, Employee>> Staff(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? []
            : await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);

    /// <summary>
    /// The instants a month's punches can fall in, a day wide on each side: a shift that starts on
    /// the last evening ends after the month, and one that ends on the first morning started
    /// before it. Shifts and stray punches outside the month are dropped after pairing.
    /// </summary>
    private static (DateTime From, DateTime To) Window(AttendancePeriod period, TimeZoneInfo zone) =>
        (AttendancePeriod.StartUtc(period.FirstDay.AddDays(-1), zone),
         AttendancePeriod.StartUtc(period.LastDay.AddDays(2), zone));

    private TimeZoneInfo Zone() => AttendancePeriod.Zone(regional.Get().TimeZone);

    private static HrResult<T> Locked<T>(PeriodLock state) =>
        HrResult<T>.Conflict(
            HrProblems.PeriodLocked,
            $"Payroll run {state.Number} is {state.Status}; unlock it before changing attendance.",
            state.Number, state.Status);

    private static AttendanceDayStatus? ParseStatus(string? value) =>
        Enum.TryParse<AttendanceDayStatus>(value?.Trim(), ignoreCase: true, out var parsed) ? parsed : null;

    private static ClockEventKind? ParseKind(string? value) =>
        Enum.TryParse<ClockEventKind>(value?.Trim(), ignoreCase: true, out var parsed) ? parsed : null;

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
