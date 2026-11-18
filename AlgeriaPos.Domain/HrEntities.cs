using System.ComponentModel.DataAnnotations;

namespace AlgeriaPos.Domain;

// ---------------------------------------------------------------------------------------------
// Personnel and payroll — docs/07 §5.4.
//
// NOTHING in this file derives from SyncEntity, and that is the whole mechanism keeping wages
// off the tills. A pull is driven by the base type: one word in one class declaration would put
// every employee's salary, national id and advance on every cashier's device at the next sync,
// silently, with no endpoint and no reviewer involved. `HrArchitectureTests` asserts it for every
// type declared here, the same guard the ledger, the rental book and the debtor book already have.
//
// The enums below mirror `AlgeriaPos.Application.Payroll` name for name and value for value. The
// duplication is deliberate and has a precedent in `RentalChargeKind`: the domain has no project
// references at all (`LayeringTests`), so a stored column cannot reference the engine's enum, and
// a raw int with no names would leave the mapping in whichever call site was written last.
// `HrEnumMirrorTests` fails the build if the two ever drift.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// How an employee's gross pay is arrived at. Set per employee, never per company: a site keeps
/// daily labour and salaried staff on the same payroll run.
/// <para>Mirrors <c>AlgeriaPos.Application.Payroll.PayBasis</c>.</para>
/// </summary>
public enum PayBasis
{
    Daily = 0,
    Monthly = 1
}

/// <summary>
/// Where the days a payslip is built from come from.
/// <para>Mirrors <c>AlgeriaPos.Application.Payroll.AttendanceMode</c>.</para>
/// </summary>
public enum AttendanceMode
{
    Recorded = 0,
    Declared = 1
}

/// <summary>
/// Which shape the run actually found for an employee's month, frozen onto the payslip line.
/// <c>None</c> is not an error: a monthly employee with no sheet is paid in full and flagged
/// (docs/07 §5.6), and the line has to say so years later.
/// </summary>
public enum AttendanceSource
{
    Declared = 0,
    Recorded = 1,
    None = 2
}

/// <summary>How money reaches a person. Cash moves a box; bank does not.</summary>
public enum PayoutMethod
{
    Cash = 0,
    Bank = 1
}

/// <summary>What one day of one employee's month was.</summary>
public enum AttendanceDayStatus
{
    Present = 0,
    Absent = 1,
    PaidLeave = 2,
    UnpaidLeave = 3,
    WeeklyRest = 4,
    PublicHoliday = 5
}

public enum ClockEventKind
{
    In = 0,
    Out = 1
}

/// <summary>
/// An advance is open until the runs have taken it all back. <c>WrittenOff</c> exists as a state
/// but has no route in this phase: the account that absorbs it is the accountant's answer
/// (docs/07 §5.8, Q24), and a builder must not pick one.
/// </summary>
public enum WageAdvanceStatus
{
    Open = 0,
    Recovered = 1,
    WrittenOff = 2
}

/// <summary>
/// The run's lifecycle, docs/07 §5.7. The order matters: <c>Reversed = 4</c> is what the SQLite
/// filtered unique index tests against, so renumbering these silently changes which runs collide.
/// </summary>
public enum PayrollRunStatus
{
    Draft = 0,
    Locked = 1,
    Approved = 2,
    Paid = 3,
    Reversed = 4
}

/// <summary>What a payslip line is made of, in the order the engine produces it (docs/07 §5.6).</summary>
public enum PayrollElementKind
{
    Base = 0,
    Absence = 1,
    Allowance = 2,
    Overtime = 3,
    Deduction = 4,
    Adjustment = 5
}

/// <summary>
/// A person the company pays. Not a <see cref="User"/>: a login is how someone operates the
/// system, an employee is someone the company owes money to, and most of a site's labour has
/// neither a password nor a till.
///
/// <para>
/// <b>Never deleted.</b> A person who has been paid is not un-personed — the payslips, the
/// advances and the postings all name them. Leaving is <see cref="LeftOn"/>, and every foreign
/// key pointing here is Restrict for the same reason.
/// </para>
/// </summary>
public class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Generated server-side and never changed. Since 2026-10-05 (owner) <c>Emp-</c> + the branch code
    /// + YYMM + the month's sequence, e.g. <c>Emp-112610001</c> (CodeSeries); people numbered before
    /// keep their <c>EMP-000001</c>, which their payslips carry. Unique.
    /// </summary>
    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    /// <summary>
    /// The name as everything prints and searches it: <see cref="FirstName"/> then
    /// <see cref="FamilyName"/> (owner, PinSay #301, 2026-10-05: "name and family name in separate
    /// inputs", printed first name first). Composed on every save; a payslip snapshots it.
    /// </summary>
    [MaxLength(200)] public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// The person's first name. Null on everyone filed before the two parts existed: their whole
    /// name was moved into <see cref="FamilyName"/>, so nothing printed changed, and an admin splits it
    /// the next time the person is edited.
    /// </summary>
    [MaxLength(100)] public string? FirstName { get; set; }

    /// <summary>The family name; for a person filed before the parts existed, the whole name as it was typed.</summary>
    [MaxLength(200)] public string? FamilyName { get; set; }

    /// <summary>
    /// The identity-document number, labelled by the country profile. Free text with no format
    /// check: a national card, a residence permit and a passport are all written here.
    /// </summary>
    [MaxLength(40)] public string? NationalId { get; set; }

    /// <summary>
    /// The social-security number, labelled from the settings' social names. Deliberately a free
    /// field with <b>no meaning attached</b>: nothing in this system parses it, derives a rate
    /// from it or validates its check digits. It is printed on the payslip and declared to the
    /// fund, and that is all.
    /// </summary>
    [MaxLength(40)] public string? SocialSecurityNumber { get; set; }

    /// <summary>
    /// The number this person is enrolled under on the fingerprint terminal (FR-3, "رقم البصمة"):
    /// what the terminal's monthly export names them by. Optional — the clock page and the sheet
    /// need none — and unique across the company when set, so one punch can never be read as two
    /// people's. Stored in one spelling (<see cref="FingerprintNumbers.Normalize"/>), the
    /// spelling it is matched in.
    /// </summary>
    [MaxLength(FingerprintNumbers.MaxLength)] public string? FingerprintNumber { get; set; }

    /// <summary>A calendar date, not an instant: a birthday does not move with a time zone.</summary>
    public DateOnly? BirthDate { get; set; }

    [MaxLength(60)] public string? Phone { get; set; }

    /// <summary>
    /// The person's e-mail address (owner, 2026-10-05: "new employee — add a field for the email
    /// address, optional"). A stored fact on the staff file and nothing more: nothing is mailed to it,
    /// and it has no part in the person's account, its Login ID or its password. Optional, not unique
    /// (a family business may share one), checked to look like an address when set
    /// (<c>Infrastructure/Hr/EmployeeDirectory</c>).
    /// </summary>
    [MaxLength(200)] public string? Email { get; set; }

    [MaxLength(400)] public string? Address { get; set; }

    /// <summary>
    /// The branch that carries this person's wage cost. Restrict: a branch with staff on it is in
    /// use. An employee who moves is edited here, and the run pays them whole from whichever
    /// branch holds them at compute time (docs/07 §5.4) — there is no dated branch history.
    /// </summary>
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }

    /// <summary>Free text. There is no grade table: a business names its own jobs.</summary>
    [MaxLength(120)] public string? JobTitle { get; set; }

    public DateOnly HiredOn { get; set; }

    /// <summary>Null while they are still with the company. Set, the run stops picking them up.</summary>
    public DateOnly? LeftOn { get; set; }

    public PayBasis PayBasis { get; set; } = PayBasis.Monthly;

    /// <summary>The monthly salary or the daily rate, per <see cref="PayBasis"/>.</summary>
    public decimal BaseRate { get; set; }

    public PayoutMethod PayoutMethod { get; set; } = PayoutMethod.Cash;

    /// <summary>Free text for the same reason <c>Store.BankDetails</c> is: RIB/IBAN labelling differs by country.</summary>
    [MaxLength(60)] public string? BankAccount { get; set; }

    /// <summary>
    /// Null means "whatever the company's default is". A foreman's crew can be declared while
    /// the office is clocked, without the company having to choose one mode for everyone.
    /// </summary>
    public AttendanceMode? AttendanceModeOverride { get; set; }

    /// <summary>
    /// Whether the payroll run should pick this person up. Distinct from <see cref="LeftOn"/>:
    /// someone suspended for a month is inactive and has not left.
    /// </summary>
    public bool IsActive { get; set; } = true;

    [MaxLength(500)] public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The one spelling of a fingerprint-terminal number, for storing and for matching alike.
///
/// <para>
/// Trimmed, Arabic-Indic and Persian digits read as 0–9, and an all-digit number without its
/// leading zeros: a terminal that pads user 7 to "0007" and a spreadsheet that reads the same
/// cell as the number 7 must land on the same person. Anything that is not all digits is kept
/// as typed, minus the outer spaces — no device's format is assumed.
/// </para>
/// </summary>
public static class FingerprintNumbers
{
    public const int MaxLength = 20;

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = new string(value.Trim().Select(ch => ch switch
        {
            >= '\u0660' and <= '\u0669' => (char)('0' + (ch - '\u0660')),
            >= '\u06F0' and <= '\u06F9' => (char)('0' + (ch - '\u06F0')),
            _ => ch
        }).ToArray());
        if (!text.All(char.IsAsciiDigit)) return text;
        text = text.TrimStart('0');
        return text.Length == 0 ? "0" : text;
    }
}

/// <summary>
/// One employee's value for one of the company's allowance definitions. The definition lives in
/// <c>PayrollSettings.Allowances</c> and carries the label and the base flags; this row only
/// overrides the figure, or switches the allowance off for this person.
/// </summary>
public class EmployeeAllowance
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Cascade: these rows are part of the employee's record, not history of their own.</summary>
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>The key of an <c>AllowanceDef</c>. No foreign key: the definitions are JSON settings, not rows.</summary>
    [MaxLength(30)] public string AllowanceKey { get; set; } = string.Empty;

    public decimal Value { get; set; }

    /// <summary>False pays nothing, which is not the same as a value of zero being absent.</summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// A foreman's period totals for one employee — the Declared capture mode (docs/07 §5.5).
///
/// <para>
/// The period is <b>a calendar month in the company's time zone</b>, stored as two ints. A UTC
/// timestamp would put the first of the month in the previous one for half of Africa/Algiers'
/// year, and the whole module's uniqueness rules hang off this pair.
/// </para>
/// </summary>
public class AttendanceDeclaration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }

    /// <summary>Decimal, not int: half days are ordinary on a site.</summary>
    public decimal DaysPresent { get; set; }
    public decimal PaidLeaveDays { get; set; }
    public decimal UnpaidAbsenceDays { get; set; }

    [MaxLength(200)] public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<AttendanceDeclarationOvertime> Overtime { get; set; } = [];
}

/// <summary>Declared overtime hours, kept per band so the multipliers stay separable.</summary>
public class AttendanceDeclarationOvertime
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DeclarationId { get; set; }
    public AttendanceDeclaration? Declaration { get; set; }

    /// <summary>The key of an <c>OvertimeRuleDef</c>. Settings again, so no foreign key.</summary>
    [MaxLength(30)] public string RuleKey { get; set; } = string.Empty;

    public decimal Hours { get; set; }
}

/// <summary>
/// One employee's one day — the Recorded capture mode, whether it was ticked in the back office
/// or derived from clock events.
/// </summary>
public class AttendanceDay
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>
    /// The local working day, as a date. <c>DateOnly</c> on purpose: EF 8 maps it to TEXT on
    /// SQLite and <c>date</c> on MySQL, and a <c>DateTime</c> at midnight would invite the
    /// time-zone arithmetic §5.5 exists to keep out. A shift crossing midnight belongs to the
    /// day it started in.
    /// </summary>
    public DateOnly WorkDate { get; set; }

    public AttendanceDayStatus Status { get; set; } = AttendanceDayStatus.Present;

    /// <summary>Null means "a normal day"; the engine uses the company's hours per day.</summary>
    public decimal? HoursWorked { get; set; }

    [MaxLength(200)] public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<AttendanceDayOvertime> Overtime { get; set; } = [];
}

/// <summary>Recorded overtime hours for one day, per band.</summary>
public class AttendanceDayOvertime
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DayId { get; set; }
    public AttendanceDay? Day { get; set; }

    [MaxLength(30)] public string RuleKey { get; set; } = string.Empty;
    public decimal Hours { get; set; }
}

/// <summary>
/// One overtime entry as a supervisor records it (owner, 2026-10-10): a person, a date, a kind of
/// work and the hours — editable and deletable until the month is held by a payroll run that is
/// locked, approved or paid. The run adds a month's entries to its overtime per kind; the sheet's
/// own overtime columns and the day grid are untouched by them.
/// </summary>
public class OvertimeEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>The local working day; the month it falls in is the payroll month it is paid in.</summary>
    public DateOnly WorkDate { get; set; }

    /// <summary>The key of an <c>OvertimeRuleDef</c> — the kind of work, which carries the rate.</summary>
    [MaxLength(30)] public string RuleKey { get; set; } = string.Empty;

    /// <summary>Hours, or days for a band the settings count in days.</summary>
    public decimal Hours { get; set; }

    [MaxLength(300)] public string? Note { get; set; }

    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(200)] public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum OvertimeEntryAction
{
    Created = 0,
    Edited = 1,
    Deleted = 2
}

/// <summary>
/// What happened to an overtime entry, by whom and when, with the values before and after. Kept
/// after the entry itself is deleted — that is when it matters most — so it carries no foreign key.
/// </summary>
public class OvertimeEntryChange
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EntryId { get; set; }
    public Guid EmployeeId { get; set; }
    public OvertimeEntryAction Action { get; set; }

    public DateOnly? PreviousWorkDate { get; set; }
    [MaxLength(30)] public string? PreviousRuleKey { get; set; }
    public decimal? PreviousHours { get; set; }

    public DateOnly? WorkDate { get; set; }
    [MaxLength(30)] public string? RuleKey { get; set; }
    public decimal? Hours { get; set; }

    [MaxLength(200)] public string? ChangedBy { get; set; }
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A raw clock punch. Kept as events rather than folded straight into <see cref="AttendanceDay"/>
/// so that an unpaired In can be <b>reported</b> rather than guessed at (docs/07 §5.5): a missing
/// clock-out is a question for the foreman, not a shift the system invents.
/// </summary>
public class ClockEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public ClockEventKind Kind { get; set; } = ClockEventKind.In;

    /// <summary>Where the punch came from — the back office, a device. Free text, not an enum: the list grows.</summary>
    [MaxLength(40)] public string? Source { get; set; }

    [MaxLength(200)] public string? CreatedBy { get; set; }
}

/// <summary>
/// Cash drawn against wages before the month closes — <c>سلفة</c>. A document in its own right:
/// money leaves a box today and the next run takes it back (docs/07 §5.8).
/// </summary>
public class WageAdvance
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Generated server-side as <c>ADV-000001</c>. Unique.</summary>
    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    /// <summary>Restrict: the person who took the money is never removed from under it.</summary>
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>
    /// The employee's branch at the moment of the grant, frozen. The cost belongs to the branch
    /// that had them then, whatever a later transfer says; the paying box may be another branch's,
    /// which is what makes the posting inter-branch.
    /// </summary>
    public Guid BranchId { get; set; }

    public decimal Amount { get; set; }
    public DateTime GrantedAtUtc { get; set; } = DateTime.UtcNow;

    public PayoutMethod PayoutMethod { get; set; } = PayoutMethod.Cash;

    /// <summary>The box the cash left, when it was cash. Null for a bank transfer.</summary>
    public Guid? CashBoxId { get; set; }

    /// <summary>Running total taken back by the runs. Starts at zero, never exceeds <see cref="Amount"/>.</summary>
    public decimal Recovered { get; set; }

    public WageAdvanceStatus Status { get; set; } = WageAdvanceStatus.Open;

    /// <summary>The run that closed it. Cleared when that run is reversed, which reopens the advance.</summary>
    public Guid? RecoveredInRunId { get; set; }

    [MaxLength(100)] public string? Reference { get; set; }
    [MaxLength(400)] public string? Note { get; set; }
    [MaxLength(200)] public string? CreatedBy { get; set; }
}

/// <summary>
/// One month's wages for one branch — the payroll document (docs/07 §5.7).
///
/// <para>
/// <b>One live run per branch and month.</b> Uniqueness is over non-<see cref="PayrollRunStatus.Reversed"/>
/// runs only: a reversed run has to stay, because it holds <see cref="LedgerEntryId"/> and
/// <see cref="ReversalEntryId"/> — the record of what was posted and what undid it — and a plain
/// unique index would let that row block the month from ever being run again. Enforced in code
/// inside the write scope on both providers, and backed by a filtered unique index on SQLite,
/// which is the only one of the two that has partial indexes.
/// </para>
/// </summary>
public class PayrollRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Generated server-side as <c>RUN-000001</c>. Unique.</summary>
    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    /// <summary>Restrict: a branch with a payroll history is not deletable.</summary>
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }

    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }

    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;

    /// <summary>
    /// Whether this run calculates CNAS contributions and IRG withholding. This belongs to the
    /// run rather than the company setting so a completed payslip always keeps the figures it was
    /// approved and paid with. New and reopened drafts opt out; historical locked, approved and
    /// paid runs keep their original calculation.
    /// </summary>
    public bool CalculateStatutoryDeductions { get; set; } = true;

    /// <summary>
    /// The settings this run computed with, serialised. Not a convenience: a rate changed in
    /// March must not move February's closed payslips, and a run that can be re-read years later
    /// is the only way an inspector can be shown why a figure is what it is. Recompute
    /// re-snapshots; nothing else rewrites it.
    /// </summary>
    public string? SettingsSnapshotJson { get; set; }

    /// <summary>The currency's decimals at compute time — elements are rounded to it before totals are built.</summary>
    public int CurrencyDecimals { get; set; }

    public int HeadCount { get; set; }

    public decimal Gross { get; set; }
    public decimal SocialBase { get; set; }
    public decimal SocialEmployee { get; set; }
    public decimal SocialEmployer { get; set; }
    public decimal TaxBase { get; set; }
    public decimal IncomeTax { get; set; }
    public decimal EmployerLevies { get; set; }
    public decimal AdvancesRecovered { get; set; }
    /// <summary>Workers' bonuses already handed over by payment voucher, taken back off net pay (owner, 2026-11-08). See <see cref="PaymentVoucherBonusLine"/>.</summary>
    public decimal PaidByVoucher { get; set; }
    public decimal OtherDeductions { get; set; }
    public decimal Net { get; set; }

    /// <summary>
    /// True when the snapshot's figures were still unconfirmed — the placeholder rates the admin
    /// edits from the back office before the accountant has signed them off (project memory,
    /// 2026-09-24). Frozen here rather than read live so that a run computed under placeholders
    /// keeps saying so on screen and on paper even after the figures are later confirmed.
    /// </summary>
    public bool IsProvisional { get; set; }

    public DateTime? ComputedAtUtc { get; set; }
    [MaxLength(200)] public string? LockedBy { get; set; }

    public DateTime? ApprovedAtUtc { get; set; }
    [MaxLength(200)] public string? ApprovedBy { get; set; }

    public DateTime? PaidAtUtc { get; set; }

    /// <summary>The accrual entry Approve posted.</summary>
    public Guid? LedgerEntryId { get; set; }

    /// <summary>The entry that undid it. Both links stay on a reversed run — that is its whole point.</summary>
    public Guid? ReversalEntryId { get; set; }

    public DateTime? ReversedAtUtc { get; set; }
    [MaxLength(200)] public string? ReversedBy { get; set; }

    /// <summary>
    /// Required by the Reverse transition (§5.7) and stored here because there is nowhere else a
    /// reversal's reason could live — the reversed run is the document that has to explain itself.
    /// </summary>
    [MaxLength(400)] public string? ReversalReason { get; set; }

    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<PayrollRunLine> Lines { get; set; } = [];
}

/// <summary>
/// One employee's payslip inside a run.
///
/// <para>
/// The snapshot columns follow the rule the invoice's buyer block and the contract's signatory
/// already follow: <b>an employee edit never rewrites a payslip.</b> A corrected spelling, a new
/// social-security number or a transfer next year must not change what last March's paper said.
/// </para>
/// </summary>
public class PayrollRunLine
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }
    public PayrollRun? Run { get; set; }

    /// <summary>Restrict, and no navigation back: the line names a person, it does not own them.</summary>
    public Guid EmployeeId { get; set; }

    [MaxLength(40)] public string EmployeeNumberSnapshot { get; set; } = string.Empty;
    [MaxLength(200)] public string EmployeeNameSnapshot { get; set; } = string.Empty;
    [MaxLength(40)] public string? SocialNumberSnapshot { get; set; }

    /// <summary>The branch that held them at compute time. Snapshotted with the name, for the same reason.</summary>
    public Guid BranchIdSnapshot { get; set; }

    public PayBasis PayBasis { get; set; }
    public decimal BaseRate { get; set; }

    public decimal DaysPresent { get; set; }
    public decimal PaidLeaveDays { get; set; }
    public decimal UnpaidAbsenceDays { get; set; }
    public AttendanceSource AttendanceSource { get; set; } = AttendanceSource.None;

    public decimal Gross { get; set; }
    public decimal SocialBase { get; set; }
    public decimal SocialEmployee { get; set; }
    public decimal SocialEmployer { get; set; }
    public decimal TaxBase { get; set; }
    public decimal IncomeTax { get; set; }
    public decimal AdvanceRecovered { get; set; }
    /// <summary>
    /// The bonuses this person already received by payment voucher (owner, 2026-11-08): in the gross as an earning
    /// (the <c>voucherBonus</c> element) and taken back here, so the payslip shows the bonus without paying it twice.
    /// </summary>
    public decimal PaidByVoucher { get; set; }
    public decimal OtherDeductions { get; set; }
    public decimal Net { get; set; }

    /// <summary>What has actually been handed over. Starts at zero; the run is Paid when every line matches its Net.</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>The payment that settled it, when one payment settled it whole.</summary>
    public Guid? WagePaymentId { get; set; }

    /// <summary>
    /// The engine's warnings for this person, serialised: zero days, a month with no sheet, a net
    /// clamped at zero, a wage under the minimum. Warnings, never refusals — CLAUDE.md's first
    /// rule read for payroll is that the run must always be able to say what it would pay and why.
    /// </summary>
    public string? WarningsJson { get; set; }

    public List<PayrollRunLineElement> Elements { get; set; } = [];
}

/// <summary>
/// One line of one payslip — the base, an absence, an allowance, an overtime band, a deduction,
/// an adjustment. This is what gets printed; the line's totals are the sum of these, rounded
/// first, so the paper adds up to the centime.
/// </summary>
public class PayrollRunLineElement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid LineId { get; set; }
    public PayrollRunLine? Line { get; set; }

    /// <summary>The allowance, overtime or deduction key it came from; a fixed key for the base and absence rows.</summary>
    [MaxLength(30)] public string Key { get; set; } = string.Empty;

    public PayrollElementKind Kind { get; set; }

    /// <summary>Days or hours, when the element has a quantity; null for a flat figure.</summary>
    public decimal? Qty { get; set; }
    public decimal? Rate { get; set; }

    /// <summary>Signed: a deduction is negative, so the elements sum to the gross without a second rule.</summary>
    public decimal Amount { get; set; }

    /// <summary>Frozen from the definition at compute time, because which allowance is in which base is the accountant's answer and it changes.</summary>
    public bool InSocialBase { get; set; }
    public bool InTaxBase { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// A one-off figure the admin adds to one person in one run — a bonus, a correction, a fine.
///
/// <para>
/// An <b>input</b>, not an element: it is re-read on every compute, so a recompute keeps it while
/// wiping and rebuilding the computed lines. Storing it as an element would make it the one thing
/// a recompute quietly deletes.
/// </para>
/// </summary>
public class PayrollAdjustment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }
    public PayrollRun? Run { get; set; }

    public Guid EmployeeId { get; set; }

    [MaxLength(30)] public string Key { get; set; } = string.Empty;

    /// <summary>Signed.</summary>
    public decimal Amount { get; set; }

    public bool InSocialBase { get; set; }
    public bool InTaxBase { get; set; }

    /// <summary>Required, never blank: a figure added to someone's pay by hand has to say why.</summary>
    [MaxLength(400)] public string Reason { get; set; } = string.Empty;

    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Money actually handed over against an approved run — the first money-out document in the
/// product (docs/07 §5.10). Several are possible for one run: a branch pays its cash crew on the
/// 2nd and transfers the salaried staff on the 5th.
/// </summary>
public class WagePayment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Generated server-side as <c>WP-000001</c>. Unique.</summary>
    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    /// <summary>Restrict: a run that has been paid against is not deletable.</summary>
    public Guid RunId { get; set; }
    public PayrollRun? Run { get; set; }

    /// <summary>The branch the money is charged to, which need not be the branch whose box paid.</summary>
    public Guid BranchId { get; set; }

    public DateTime PaidAtUtc { get; set; } = DateTime.UtcNow;
    public PayoutMethod PayoutMethod { get; set; } = PayoutMethod.Cash;
    public Guid? CashBoxId { get; set; }

    public decimal Amount { get; set; }

    [MaxLength(100)] public string? Reference { get; set; }
    [MaxLength(400)] public string? Note { get; set; }
    [MaxLength(200)] public string? CreatedBy { get; set; }

    public Guid? LedgerEntryId { get; set; }

    public List<WagePaymentLine> Lines { get; set; } = [];
}

/// <summary>
/// What one payment gave to one payslip line. At most one per run line per payment, enforced in
/// code rather than by a unique index: partial payment is legitimate and a second payment for the
/// same line on a later date is exactly what a part-paid crew looks like.
/// </summary>
public class WagePaymentLine
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WagePaymentId { get; set; }
    public WagePayment? WagePayment { get; set; }

    /// <summary>Restrict: a payslip line that has been paid is not removable, which also stops a recompute wiping it.</summary>
    public Guid RunLineId { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>
/// The accountant recording that the social fund or the tax office was actually paid, so the
/// 431/442/447 liabilities the run accrues do not read as debt forever (docs/07 §5.4, §5.9).
///
/// <para>
/// <see cref="AccountCode"/> is the ledger account's code rather than its id, matching how every
/// posting in this codebase names an account; <c>LedgerPoster</c> refuses an unknown one.
/// </para>
/// </summary>
public class PayrollStatutoryPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BranchId { get; set; }

    /// <summary>One of the payroll liability accounts. Validated against the chart at write time, not here.</summary>
    [MaxLength(20)] public string AccountCode { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public DateTime PaidAtUtc { get; set; } = DateTime.UtcNow;

    public PayoutMethod PayoutMethod { get; set; } = PayoutMethod.Bank;
    public Guid? CashBoxId { get; set; }

    /// <summary>
    /// The month being settled, when the payment is for one month. Nullable because a single
    /// transfer often clears several, and forcing a period onto it would have the accountant
    /// inventing one.
    /// </summary>
    public int? PeriodYear { get; set; }
    public int? PeriodMonth { get; set; }

    [MaxLength(100)] public string? Reference { get; set; }
    [MaxLength(400)] public string? Note { get; set; }
    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? LedgerEntryId { get; set; }
}
