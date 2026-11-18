namespace AlgeriaPos.Infrastructure.Hr;

/// <summary>Why a personnel or attendance write did nothing, in the shape an HTTP layer needs.</summary>
public enum HrFailure { NotFound, Invalid, Conflict }

/// <summary>
/// The short reason codes these services refuse with. Codes rather than sentences, for the same
/// reason <c>RentalRules</c> and <c>CustomerRules</c> return them: the person who meets one is a
/// clerk reading Arabic or French, and the screen resolves <c>hr.problem.{code}</c>. The English
/// text that travels beside a code is for curl, the smoke script and the log, never for a user.
/// </summary>
public static class HrProblems
{
    // ---------- employees ----------
    public const string NameRequired = "nameRequired";
    public const string BranchRequired = "branchRequired";
    public const string BranchUnknown = "branchUnknown";
    public const string PayBasisUnknown = "payBasisUnknown";
    public const string PayoutMethodUnknown = "payoutMethodUnknown";
    public const string AttendanceModeUnknown = "attendanceModeUnknown";
    public const string BaseRateNegative = "baseRateNegative";
    public const string LeftBeforeHired = "leftBeforeHired";
    public const string EmployeeNotFound = "employeeNotFound";
    public const string AllowanceUnknown = "allowanceUnknown";
    public const string AllowanceDuplicate = "allowanceDuplicate";
    public const string AllowanceNegative = "allowanceNegative";
    /// <summary>Another employee is enrolled under that fingerprint number; their number is the refusal's key.</summary>
    public const string FingerprintTaken = "fingerprintTaken";
    /// <summary>
    /// The same, told to a branch-locked caller when the holder is at another branch: no key, because
    /// naming the person would open a file the caller may not read (P0.1).
    /// </summary>
    public const string FingerprintTakenElsewhere = "fingerprintTakenElsewhere";
    public const string FingerprintTooLong = "fingerprintTooLong";
    /// <summary>
    /// Daily asked for someone not already on it (owner, 2026-10-05: no daily workers on payroll; a
    /// day worker is paid by payment voucher under Daily labour). A legacy Daily row keeps its basis.
    /// </summary>
    public const string PayBasisDailyRetired = "payBasisDailyRetired";
    /// <summary>An e-mail address that does not look like one. Blank is not this: blank is no address.</summary>
    public const string EmailInvalid = "emailInvalid";

    // ---------- attendance ----------
    public const string PeriodInvalid = "periodInvalid";
    /// <summary>The month's run is Locked, Approved or Paid. The run is named in the refusal.</summary>
    public const string PeriodLocked = "periodLocked";
    public const string RowsRequired = "rowsRequired";
    public const string DuplicateRow = "duplicateRow";
    public const string EmployeeNotInBranch = "employeeNotInBranch";
    /// <summary>Hired after the month ended, or left before it began.</summary>
    public const string EmployeeOutsidePeriod = "employeeOutsidePeriod";
    /// <summary>A Recorded person's totals are added up from days and punches, never typed over.</summary>
    public const string RecordedRowNotEditable = "recordedRowNotEditable";
    /// <summary>A Declared person has no day grid; their month is the sheet's four figures.</summary>
    public const string DeclaredHasNoDayGrid = "declaredHasNoDayGrid";
    public const string NegativeDays = "negativeDays";
    public const string DaysExceedMonth = "daysExceedMonth";
    public const string NegativeHours = "negativeHours";
    public const string HoursExceedDay = "hoursExceedDay";
    public const string OvertimeRuleUnknown = "overtimeRuleUnknown";
    public const string DayOutsidePeriod = "dayOutsidePeriod";
    public const string DayStatusUnknown = "dayStatusUnknown";
    /// <summary>A day grid belongs to one person; a request naming two is a screen bug, not an edit.</summary>
    public const string MixedEmployees = "mixedEmployees";
    public const string ClockKindUnknown = "clockKindUnknown";
    public const string EmployeeRequired = "employeeRequired";
    /// <summary>An overtime entry that does not exist, or no longer does.</summary>
    public const string OvertimeEntryNotFound = "overtimeEntryNotFound";
    /// <summary>An overtime entry with no hours: delete it rather than record zero.</summary>
    public const string OvertimeHoursRequired = "overtimeHoursRequired";

    // ---------- warnings, which refuse nothing ----------
    /// <summary>A second clock-in with no clock-out between them. Recorded and said, never dropped.</summary>
    public const string ClockRepeatedIn = "clockRepeatedIn";
    /// <summary>A clock-out with no open clock-in.</summary>
    public const string ClockOutWithoutIn = "clockOutWithoutIn";
}

/// <summary>
/// What a personnel or attendance call produced, or why it produced nothing — and, separately,
/// anything the clerk should be told about a write that <i>did</i> happen.
///
/// <para>
/// <see cref="Warning"/> is kept apart from <see cref="Failure"/> on purpose, the way
/// <c>ReceivableResult</c> keeps them apart: a punch that does not pair is a fact the foreman
/// has to see, and folding it into a refusal is how a warning quietly becomes a block.
/// </para>
/// </summary>
public sealed record HrResult<T>(
    T? Value,
    HrFailure? Failure = null,
    string? Code = null,
    string? Message = null,
    string? Warning = null,
    string? RunNumber = null,
    string? RunStatus = null,
    string? Key = null)
{
    public bool Ok => Failure is null;

    public static HrResult<T> Success(T value, string? warning = null) => new(value, Warning: warning);

    public static HrResult<T> NotFound(string code, string message) =>
        new(default, HrFailure.NotFound, code, message);

    public static HrResult<T> Invalid(string code, string message, string? key = null) =>
        new(default, HrFailure.Invalid, code, message, Key: key);

    public static HrResult<T> Conflict(string code, string message, string? runNumber = null, string? runStatus = null) =>
        new(default, HrFailure.Conflict, code, message, RunNumber: runNumber, RunStatus: runStatus);

    /// <summary>A conflict with another record, named by <paramref name="key"/>.</summary>
    public static HrResult<T> Taken(string code, string message, string key) =>
        new(default, HrFailure.Conflict, code, message, Key: key);
}
