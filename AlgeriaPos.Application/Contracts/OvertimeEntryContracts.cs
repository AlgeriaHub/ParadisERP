namespace AlgeriaPos.Application.Contracts;

/// <summary>
/// One overtime entry as the list shows it (owner, 2026-10-10). Days, hours and the kind of work —
/// never a rate or an amount, like the rest of attendance: the foreman who records overtime does
/// not read what anybody earns.
/// </summary>
/// <param name="CanEdit">
/// False when the entry's month is held by a payroll run that is locked, approved or paid; the
/// screen hides edit and delete, and the server refuses them regardless.
/// </param>
public record OvertimeEntryDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    DateOnly WorkDate,
    string RuleKey,
    decimal Hours,
    string? Note,
    string? CreatedBy,
    DateTime CreatedAtUtc,
    string? UpdatedBy,
    DateTime UpdatedAtUtc,
    bool CanEdit,
    string? LockedByRun = null);

/// <summary>A branch's month of overtime entries, with the kinds of work the settings define.</summary>
public record OvertimeEntryListDto(
    Guid BranchId,
    int Year,
    int Month,
    bool Locked,
    string? RunNumber,
    string? RunStatus,
    IReadOnlyList<OvertimeColumnDto> Kinds,
    IReadOnlyList<OvertimeEntryDto> Entries);

/// <summary>Recording an entry.</summary>
public record CreateOvertimeEntryRequest(Guid EmployeeId, DateOnly WorkDate, string RuleKey, decimal Hours, string? Note = null);

/// <summary>Correcting one: the date, the kind and the hours. The person it belongs to does not change.</summary>
public record UpdateOvertimeEntryRequest(DateOnly WorkDate, string RuleKey, decimal Hours, string? Note = null);

/// <summary>One change to an entry, with the values before and after.</summary>
public record OvertimeEntryChangeDto(
    Guid Id,
    Guid EntryId,
    string Action,
    DateOnly? PreviousWorkDate,
    string? PreviousRuleKey,
    decimal? PreviousHours,
    DateOnly? WorkDate,
    string? RuleKey,
    decimal? Hours,
    string? ChangedBy,
    DateTime ChangedAtUtc);
