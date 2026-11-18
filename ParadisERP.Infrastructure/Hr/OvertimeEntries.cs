using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Payroll;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = ParadisERP.Application.Payroll;

namespace ParadisERP.Infrastructure.Hr;

/// <summary>
/// Overtime entries (owner, 2026-10-10): a supervisor records a person's overtime by date and kind
/// of work, and may correct or delete an entry — the date, the kind, the hours — until the month
/// is held by a payroll run that is locked, approved or paid. Every change keeps who made it,
/// when, and the values before it (<see cref="OvertimeEntryChange"/>), and the history outlives
/// the entry.
///
/// <para>
/// The lock is the attendance lock (<see cref="AttendanceSheet.LockOfAsync"/>): a draft run holds
/// nothing, and picks a corrected entry up when it is computed again. A paid month is a historical
/// document; what was wrong in it is corrected in the next run.
/// </para>
/// </summary>
public sealed class OvertimeEntries(PosDbContext db, AttendanceSheet attendance, PayrollSettingsStore settings)
{
    /// <summary>The note travels as typed, cut to what the column holds.</summary>
    private const int NoteMax = 300;

    public async Task<HrResult<OvertimeEntryListDto>> ListAsync(
        Guid branchId, int year, int month, Guid? employeeId, CancellationToken ct = default)
    {
        if (!AttendancePeriod.IsValid(year, month))
            return HrResult<OvertimeEntryListDto>.Invalid(HrProblems.PeriodInvalid, "That is not a month.");
        if (!await db.Branches.AnyAsync(b => b.Id == branchId, ct))
            return HrResult<OvertimeEntryListDto>.Invalid(HrProblems.BranchUnknown, "That branch does not exist.");

        var period = new AttendancePeriod(year, month);
        var profile = (await settings.ReadAsync(ct)).Settings;
        var lockState = await attendance.LockOfAsync(branchId, period, ct);

        var first = period.FirstDay;
        var last = period.LastDay;
        var query = db.OvertimeEntries.AsNoTracking().Include(e => e.Employee)
            .Where(e => e.Employee!.BranchId == branchId && e.WorkDate >= first && e.WorkDate <= last);
        if (employeeId is Guid one) query = query.Where(e => e.EmployeeId == one);
        var rows = await query.ToListAsync(ct);

        var entries = rows
            .OrderBy(e => e.WorkDate).ThenBy(e => e.Employee!.Number).ThenBy(e => e.CreatedAtUtc)
            .Select(e => Dto(e, e.Employee!, lockState))
            .ToList();

        return HrResult<OvertimeEntryListDto>.Success(new OvertimeEntryListDto(
            branchId, year, month, lockState.Locked, lockState.Number, lockState.Status,
            Kinds(profile), entries));
    }

    public async Task<HrResult<OvertimeEntryDto>> CreateAsync(
        CreateOvertimeEntryRequest r, string? userName, CancellationToken ct = default)
    {
        var profile = (await settings.ReadAsync(ct)).Settings;

        await using var scope = await db.BeginWriteScopeAsync(ct);

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == r.EmployeeId, ct);
        if (employee is null)
            return HrResult<OvertimeEntryDto>.NotFound(HrProblems.EmployeeNotFound, "No such employee.");

        var problem = Check(profile, employee, r.WorkDate, r.RuleKey, r.Hours);
        if (problem is not null) return Fail<OvertimeEntryDto>(problem.Value);

        var lockState = await attendance.LockOfAsync(employee.BranchId, Period(r.WorkDate), ct);
        if (lockState.Locked) return Locked<OvertimeEntryDto>(lockState);

        var now = DateTime.UtcNow;
        var entry = new OvertimeEntry
        {
            EmployeeId = employee.Id,
            WorkDate = r.WorkDate,
            RuleKey = Key(r.RuleKey),
            Hours = r.Hours,
            Note = Note(r.Note),
            CreatedBy = userName,
            CreatedAtUtc = now,
            UpdatedBy = userName,
            UpdatedAtUtc = now
        };
        db.OvertimeEntries.Add(entry);
        db.OvertimeEntryChanges.Add(new OvertimeEntryChange
        {
            EntryId = entry.Id,
            EmployeeId = employee.Id,
            Action = OvertimeEntryAction.Created,
            WorkDate = entry.WorkDate,
            RuleKey = entry.RuleKey,
            Hours = entry.Hours,
            ChangedBy = userName,
            ChangedAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return HrResult<OvertimeEntryDto>.Success(Dto(entry, employee, lockState));
    }

    public async Task<HrResult<OvertimeEntryDto>> UpdateAsync(
        Guid id, UpdateOvertimeEntryRequest r, string? userName, CancellationToken ct = default)
    {
        var profile = (await settings.ReadAsync(ct)).Settings;

        await using var scope = await db.BeginWriteScopeAsync(ct);

        var entry = await db.OvertimeEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
            return HrResult<OvertimeEntryDto>.NotFound(HrProblems.OvertimeEntryNotFound, "No such overtime entry.");
        var employee = await db.Employees.AsNoTracking().FirstAsync(e => e.Id == entry.EmployeeId, ct);

        // The month it is in now, and the month it would move to: neither may be closed.
        var current = await attendance.LockOfAsync(employee.BranchId, Period(entry.WorkDate), ct);
        if (current.Locked) return Locked<OvertimeEntryDto>(current);

        var problem = Check(profile, employee, r.WorkDate, r.RuleKey, r.Hours);
        if (problem is not null) return Fail<OvertimeEntryDto>(problem.Value);

        var target = Period(r.WorkDate) == Period(entry.WorkDate)
            ? current
            : await attendance.LockOfAsync(employee.BranchId, Period(r.WorkDate), ct);
        if (target.Locked) return Locked<OvertimeEntryDto>(target);

        var key = Key(r.RuleKey);
        var note = Note(r.Note);
        if (entry.WorkDate == r.WorkDate && entry.RuleKey == key && entry.Hours == r.Hours && entry.Note == note)
            return HrResult<OvertimeEntryDto>.Success(Dto(entry, employee, target));

        var now = DateTime.UtcNow;
        db.OvertimeEntryChanges.Add(new OvertimeEntryChange
        {
            EntryId = entry.Id,
            EmployeeId = entry.EmployeeId,
            Action = OvertimeEntryAction.Edited,
            PreviousWorkDate = entry.WorkDate,
            PreviousRuleKey = entry.RuleKey,
            PreviousHours = entry.Hours,
            WorkDate = r.WorkDate,
            RuleKey = key,
            Hours = r.Hours,
            ChangedBy = userName,
            ChangedAtUtc = now
        });
        entry.WorkDate = r.WorkDate;
        entry.RuleKey = key;
        entry.Hours = r.Hours;
        entry.Note = note;
        entry.UpdatedBy = userName;
        entry.UpdatedAtUtc = now;

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return HrResult<OvertimeEntryDto>.Success(Dto(entry, employee, target));
    }

    public async Task<HrResult<bool>> DeleteAsync(Guid id, string? userName, CancellationToken ct = default)
    {
        await using var scope = await db.BeginWriteScopeAsync(ct);

        var entry = await db.OvertimeEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
            return HrResult<bool>.NotFound(HrProblems.OvertimeEntryNotFound, "No such overtime entry.");
        var branchId = await db.Employees.AsNoTracking().Where(e => e.Id == entry.EmployeeId).Select(e => e.BranchId).FirstAsync(ct);

        var lockState = await attendance.LockOfAsync(branchId, Period(entry.WorkDate), ct);
        if (lockState.Locked) return Locked<bool>(lockState);

        db.OvertimeEntryChanges.Add(new OvertimeEntryChange
        {
            EntryId = entry.Id,
            EmployeeId = entry.EmployeeId,
            Action = OvertimeEntryAction.Deleted,
            PreviousWorkDate = entry.WorkDate,
            PreviousRuleKey = entry.RuleKey,
            PreviousHours = entry.Hours,
            ChangedBy = userName,
            ChangedAtUtc = DateTime.UtcNow
        });
        db.OvertimeEntries.Remove(entry);

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return HrResult<bool>.Success(true);
    }

    public async Task<HrResult<IReadOnlyList<OvertimeEntryChangeDto>>> HistoryAsync(Guid id, CancellationToken ct = default)
    {
        var changes = await db.OvertimeEntryChanges.AsNoTracking()
            .Where(c => c.EntryId == id)
            .ToListAsync(ct);
        if (changes.Count == 0)
            return HrResult<IReadOnlyList<OvertimeEntryChangeDto>>.NotFound(
                HrProblems.OvertimeEntryNotFound, "No such overtime entry.");

        return HrResult<IReadOnlyList<OvertimeEntryChangeDto>>.Success(changes
            .OrderBy(c => c.ChangedAtUtc)
            .Select(c => new OvertimeEntryChangeDto(
                c.Id, c.EntryId, c.Action.ToString(),
                c.PreviousWorkDate, c.PreviousRuleKey, c.PreviousHours,
                c.WorkDate, c.RuleKey, c.Hours,
                c.ChangedBy, EmployeeDirectory.Utc(c.ChangedAtUtc)))
            .ToList());
    }

    /// <summary>The employee an entry belongs to, for the endpoint's branch check.</summary>
    public Task<Guid?> EmployeeOfAsync(Guid id, CancellationToken ct = default) =>
        db.OvertimeEntries.AsNoTracking().Where(e => e.Id == id).Select(e => (Guid?)e.EmployeeId).FirstOrDefaultAsync(ct);

    // ---------- rules ----------

    private readonly record struct Problem(string Code, string Message, string? Key = null);

    private static Problem? Check(Engine.PayrollSettings profile, Employee employee, DateOnly date, string? ruleKey, decimal hours)
    {
        var key = Key(ruleKey);
        var rule = profile.OvertimeRules.FirstOrDefault(r => r.IsActive && string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return new(HrProblems.OvertimeRuleUnknown, "That kind of overtime is not defined.", key);
        if (hours <= 0) return new(HrProblems.OvertimeHoursRequired, "Enter the hours worked.", key);
        if (rule.Unit == Engine.OvertimeUnit.Days ? hours > 1 : hours > 24)
            return new(rule.Unit == Engine.OvertimeUnit.Days ? HrProblems.DaysExceedMonth : HrProblems.HoursExceedDay,
                rule.Unit == Engine.OvertimeUnit.Days ? "An entry counts one day at most." : "A day has 24 hours.", key);
        if (!EmployeeDirectory.CoversPeriod(employee, Period(date)))
            return new(HrProblems.EmployeeOutsidePeriod, "That employee was not employed in this month.");
        return null;
    }

    private static AttendancePeriod Period(DateOnly date) => new(date.Year, date.Month);

    private static string Key(string? key) => (key ?? "").Trim().ToLowerInvariant();

    private static string? Note(string? note)
    {
        var trimmed = note?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > NoteMax ? trimmed[..NoteMax] : trimmed;
    }

    private static IReadOnlyList<OvertimeColumnDto> Kinds(Engine.PayrollSettings profile) =>
        profile.OvertimeRules.Where(r => r.IsActive)
            .Select(r => new OvertimeColumnDto(r.Key, r.Labels, r.Multiplier, r.Unit.ToString()))
            .ToList();

    private static OvertimeEntryDto Dto(OvertimeEntry e, Employee employee, AttendanceSheet.PeriodLock lockState) =>
        new(e.Id, e.EmployeeId, employee.Number, employee.FullName,
            e.WorkDate, e.RuleKey, e.Hours, e.Note,
            e.CreatedBy, EmployeeDirectory.Utc(e.CreatedAtUtc),
            e.UpdatedBy, EmployeeDirectory.Utc(e.UpdatedAtUtc),
            !lockState.Locked, lockState.Locked ? lockState.Number : null);

    private static HrResult<T> Fail<T>(Problem problem) =>
        HrResult<T>.Invalid(problem.Code, problem.Message, problem.Key);

    private static HrResult<T> Locked<T>(AttendanceSheet.PeriodLock state) =>
        HrResult<T>.Conflict(
            HrProblems.PeriodLocked,
            $"Payroll run {state.Number} is {state.Status}; this overtime is part of it and is corrected in the next run.",
            state.Number, state.Status);
}
