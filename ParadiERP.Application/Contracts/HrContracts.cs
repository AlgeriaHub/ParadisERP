namespace ParadiERP.Application.Contracts;

// ---------------------------------------------------------------------------------------------
// Personnel and attendance — the shapes the employee and attendance routes need that the payroll
// block of Dtos.cs does not already carry (docs/07 §5.5, §5.11).
//
// **Nothing here redeclares a name Dtos.cs declares.** `EmployeeDto`, `UpsertEmployeeRequest`,
// `EmployeeAllowanceDto`, `AttendanceOvertimeDto`, `AttendanceDeclarationDto`,
// `AttendanceDeclarationRowRequest`, `UpsertAttendanceSheetRequest`, `AttendanceDayDto`,
// `UpsertAttendanceDaysRequest` and `ClockEventDto` are part A's, and these routes serve exactly
// those. What is added below is what a screen cannot work without and part A had no reason to
// foresee: the refusal envelope, the sheet's period lock and overtime columns, the paging around
// a list that grows with headcount, and the clock's badge number.
//
// Every enum crosses this wire as a NAME — "Monthly", "Declared", "PaidLeave" — never as an
// ordinal, exactly as the payroll block already does. Calendar dates are DateOnly ("2026-03-01");
// instants are UTC and say so in the name.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Why a personnel or attendance write was refused, in the one shape every HR route answers with.
///
/// <para>
/// <see cref="Code"/> is the stable half — the screen resolves <c>hr.problem.{code}</c> and shows
/// its own sentence — and <see cref="Message"/> is the English fallback for a caller with no
/// dictionary (curl, the smoke script, a log). The same split <c>RentalRules</c> and
/// <c>CustomerRules</c> use, moved onto the wire because these refusals are met by a clerk.
/// </para>
/// </summary>
/// <param name="RunNumber">The payroll run that locked the period, when that is the refusal.</param>
/// <param name="RunStatus">That run's status in words, so the screen can say what to unlock.</param>
/// <param name="Key">The allowance or overtime key the refusal is about, when it is about one.</param>
public record HrProblemDto(
    string Code, string Message, string? RunNumber = null, string? RunStatus = null, string? Key = null);

/// <param name="Total">Rows matching the filter, not rows in this page — the pager needs both.</param>
public record EmployeePageDto(int Total, int Skip, int Take, IReadOnlyList<EmployeeDto> Rows);

/// <summary>
/// Recording that somebody has left. A route of its own rather than a field on the edit form,
/// because it is the one employee change with a consequence: the run stops picking them up, and
/// every payslip, advance and posting that names them stays exactly where it is. There is no
/// delete — a person who has been paid is not un-personed.
/// </summary>
public record EmployeeLeaveRequest(DateOnly LeftOn, string? Note = null);

/// <summary>
/// One overtime band the sheet shows a column for, taken from the payroll settings rather than
/// from a fixed list: which bands exist is the company's configuration (CLAUDE.md — country is
/// configuration), and a screen that hard-coded four of them would be wrong in the first company
/// that defines a fifth.
/// </summary>
/// <param name="Labels">Language code to label, as the admin typed them into the settings.</param>
/// <param name="Unit">"Hours" or "Days" (owner, 2026-10-10): what the column's figure counts.</param>
public record OvertimeColumnDto(string Key, IReadOnlyDictionary<string, string> Labels, decimal Multiplier, string Unit = "Hours");

/// <summary>
/// One row of the attendance sheet: a person's month, in the figures the engine reads.
/// </summary>
/// <param name="Mode">"Declared" or "Recorded" — this person's effective capture mode.</param>
/// <param name="ReadOnly">
/// True for a Recorded row. Those figures were added up from day rows or clock punches, and
/// typing over them would make the sheet and the day grid two answers to one question.
/// </param>
/// <param name="Source">
/// "Declared", "Recorded" or "None". <c>None</c> is not zero: it says nothing was captured, which
/// the run warns about rather than paying as a month of absence.
/// </param>
/// <param name="UnpairedClockIns">
/// Punches that never found a partner. Reported, never guessed at (docs/07 §5.5): a missing
/// clock-out is a question for the foreman, not a shift the system invents.
/// </param>
public record AttendanceSheetRowDto(
    Guid EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    string PayBasis,
    string Mode,
    bool ReadOnly,
    decimal DaysPresent,
    decimal PaidLeaveDays,
    decimal UnpaidAbsenceDays,
    IReadOnlyList<AttendanceOvertimeDto> Overtime,
    string Source,
    int UnpairedClockIns,
    string? UpdatedBy = null,
    DateTime? UpdatedAtUtc = null);

/// <summary>
/// One branch's month. The period is two ints in the company's time zone, never an instant
/// (docs/07 §5.5): a UTC month boundary lands on the wrong day for half of Africa/Algiers' year.
/// </summary>
/// <param name="Locked">
/// The month's payroll run is Locked, Approved or Paid, so nothing here may be edited until
/// somebody unlocks it. Stated as a fact with the run named beside it, rather than left for the
/// screen to discover when a save comes back 409.
/// </param>
/// <param name="RunStatus">The run's status in words — "Draft", "Locked", "Approved", "Paid".</param>
/// <param name="CompanyDefaultMode">The mode an employee with no override is captured in.</param>
public record AttendanceSheetDto(
    Guid BranchId,
    int PeriodYear,
    int PeriodMonth,
    bool Locked,
    Guid? RunId,
    string? RunNumber,
    string? RunStatus,
    string CompanyDefaultMode,
    IReadOnlyList<OvertimeColumnDto> OvertimeRules,
    int Total,
    int Skip,
    int Take,
    IReadOnlyList<AttendanceSheetRowDto> Rows);

/// <summary>
/// A month reduced to what the payroll engine reads — the one shape both capture modes end in.
/// The day grid shows it as its footer, so a foreman sees what the run will see.
/// </summary>
public record AttendanceSummaryDto(
    decimal DaysPresent,
    decimal PaidLeaveDays,
    decimal UnpaidAbsenceDays,
    IReadOnlyList<AttendanceOvertimeDto> Overtime,
    string Source);

/// <summary>
/// One employee's month, day by day.
/// <para>
/// <see cref="Days"/> carries only the days somebody recorded. A date that is absent was not
/// recorded, which is not the same as a day of absence — the screen draws the month's calendar
/// and leaves those cells empty.
/// </para>
/// </summary>
/// <param name="UnpairedClockInsUtc">Punches with no partner, as instants, for the same reason.</param>
public record AttendanceDaysDto(
    Guid EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    Guid BranchId,
    int PeriodYear,
    int PeriodMonth,
    bool Locked,
    Guid? RunId,
    string? RunNumber,
    string? RunStatus,
    string Mode,
    IReadOnlyList<OvertimeColumnDto> OvertimeRules,
    IReadOnlyList<AttendanceDayDto> Days,
    AttendanceSummaryDto Summary,
    IReadOnlyList<DateTime> UnpairedClockInsUtc);

/// <summary>
/// A punch. Either identifier will do, and that is the point: the clock screen reads a badge
/// number through the document-wide scan catch, and the back office picks a person from a list.
/// <para>
/// This is what <c>POST /api/hr/attendance/clock</c> takes. Part A's
/// <c>RecordClockEventRequest</c> carries an id only, which no badge scanner has.
/// </para>
/// </summary>
/// <param name="AtUtc">Omitted means now. A stated instant is trusted — a late entry is ordinary.</param>
public record ClockPunchRequest(
    Guid? EmployeeId = null,
    string? EmployeeNumber = null,
    string Kind = "In",
    DateTime? AtUtc = null,
    string? Source = null);

/// <summary>Who a punch belongs to. The event itself carries an id; a clerk needs a name.</summary>
public record ClockPersonDto(Guid EmployeeId, string EmployeeNumber, string EmployeeName);

/// <summary>
/// The punch that was recorded, and anything odd about it.
/// <para>
/// <see cref="Warning"/> travels <b>beside</b> the event and never in place of it: a second
/// clock-in with no clock-out between them is a fact about the day the foreman has to see, not a
/// reason to refuse the punch and lose it. The screen resolves <c>hr.problem.{warning}</c>.
/// </para>
/// </summary>
public record ClockedDto(ClockEventDto Event, ClockPersonDto Person, string? Warning = null);

/// <param name="Unpaired">The punches in <see cref="Events"/> that never found a partner.</param>
/// <param name="People">One entry per employee named in <see cref="Events"/>, so the log reads as names.</param>
public record ClockLogDto(
    IReadOnlyList<ClockEventDto> Events,
    IReadOnlyList<ClockEventDto> Unpaired,
    IReadOnlyList<ClockPersonDto> People);

// ---------- the fingerprint terminal's export, previewed (FR-3B) ----------

/// <summary>One column of the sheet: its letter, its header text when a header row is set, and a few values.</summary>
public record PunchColumnDto(int Index, string Letter, string? Header, IReadOnlyList<string> Samples);

/// <summary>One of the sheet's top rows as written, for choosing which of them is the header.</summary>
public record PunchTopRowDto(int Row, IReadOnlyDictionary<int, string> Cells);

/// <summary>Which column holds what, as suggested or mapped.</summary>
public record PunchMappingDto(int? Number, int? Name, int? Date, int? Time, int? DateTime, int? Marker, int? TimeOut);

/// <summary>
/// One row of the file as it would be read.
/// <c>Status</c>: <c>matched</c> (an employee of this branch), <c>unknown</c> (no employee has
/// this number), <c>otherBranch</c> (an employee elsewhere — not named here), <c>invalid</c>
/// (<c>Problem</c> says why). <c>At</c> is wall-clock time in the company's zone.
/// <c>Exit</c>: the punch was read from the check-out column.
/// </summary>
public record PunchRowDto(
    int Row, string Status, string? Problem, string? Number, string? Name, string? At, string? Marker, string Source,
    Guid? EmployeeId = null, string? EmployeeNumber = null, string? EmployeeName = null, bool Exit = false,
    IReadOnlyList<string>? Flags = null);

/// <summary>A number nobody here answers to, how often it occurs and the names the file gives it.</summary>
public record PunchUnknownNumberDto(string Number, int Rows, IReadOnlyList<string> Names);

/// <summary>A number that belongs to an employee of another branch: said, not named.</summary>
public record PunchOtherBranchNumberDto(string Number, int Rows);

/// <summary>What the file holds for one matched employee. Counts only — no rule is applied to them.</summary>
public record PunchEmployeeDto(
    Guid EmployeeId, string EmployeeNumber, string EmployeeName, string FingerprintNumber,
    int Punches, int Dates, string First, string Last);

public record PunchSummaryDto(
    int Rows, int Matched, int Unknown, int OtherBranch, int Invalid, int TextDates, string? First, string? Last,
    int Flagged = 0);

/// <summary>
/// The whole preview. <c>MappingProblem</c> set means the columns are not yet mapped well
/// enough to read a row, and <c>Rows</c> is empty. Rows are cut at
/// <c>RowsShown</c>; <c>Summary</c> always counts every row.
/// </summary>
public record PunchImportPreviewDto(
    string FileName, string SheetName, IReadOnlyList<string> SheetNames,
    int? HeaderRow, int? SuggestedHeaderRow, string DateOrder,
    IReadOnlyList<PunchColumnDto> Columns, IReadOnlyList<PunchTopRowDto> Top,
    string? MappingProblem, PunchSummaryDto? Summary,
    IReadOnlyList<PunchRowDto> Rows, bool RowsCut,
    IReadOnlyList<PunchEmployeeDto> Employees,
    IReadOnlyList<PunchUnknownNumberDto> UnknownNumbers,
    IReadOnlyList<PunchOtherBranchNumberDto> OtherBranchNumbers,
    PunchMappingDto? SuggestedMapping = null);

/// <summary>
/// One person's day read against the working day (owner, 2026-10-10). Times are local <c>HH:mm</c>.
/// Every figure is for the supervisor to look at: nothing here deducts or pays by itself.
/// </summary>
/// <param name="PeriodName">The special period (Ramadan…) the day fell in; null on a normal day.</param>
/// <param name="PossibleOvertimeMinutes">After the end of the day, or all of a rest day — for the supervisor to confirm.</param>
public record AttendanceReviewDayDto(
    Guid EmployeeId, DateOnly WorkDate, bool IsRestDay, string? PeriodName,
    string ScheduledStart, string ScheduledEnd, decimal ScheduledHours,
    string? FirstIn, string? LastOut, decimal WorkedHours,
    int LateMinutes, int EarlyLeaveMinutes, bool MissingCheckOut, int PossibleOvertimeMinutes, bool WorkedOnRestDay);

/// <summary>A month of punched days against the schedule, with the people they belong to.</summary>
public record AttendanceReviewDto(
    int Year, int Month,
    IReadOnlyList<AttendanceReviewDayDto> Days,
    IReadOnlyList<ClockPersonDto> People);
