namespace AlgeriaPos.Application.Contracts;

// ---------------------------------------------------------------------------------------------
// The payroll run's own wire shapes — docs/07 §5.7, §5.9, §5.11.
//
// ⚠ The run itself, its lines, its elements and its adjustments are already declared in Dtos.cs
// (`PayrollRunDto`, `PayrollRunLineDto`, `PayrollElementDto`, `PayrollAdjustmentDto`,
// `CreatePayrollRunRequest`, `UpsertPayrollAdjustmentRequest`, `ReversePayrollRunRequest`), where
// part A put the whole payroll vocabulary. Nothing here repeats one of them: a second `PayrollRunDto`
// with one extra field is how two screens end up reading two different runs.
//
// What is here is only what the lifecycle needs and Dtos.cs does not carry: the compute refusal,
// the warning counts, the entry the run will post, and the two envelopes (a page of lines, a
// branch's month) that exist so nothing that scales with headcount is ever sent whole.
//
// Every enum crosses as a NAME, resolved on the server. The API registers no global
// JsonStringEnumConverter, so a raw enum would cross as an ordinal and every screen would carry a
// number-to-word table of its own — which is how "state in words" (CLAUDE.md) becomes a colour.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Why a run cannot be computed at all — the one refusal payroll has, and the shape the 409
/// carries.
///
/// <para>
/// It names the figures that are missing and the permission that can set them, because "it cannot
/// be computed" with nothing to act on is what makes an admin open a support ticket instead of
/// opening the settings screen.
/// </para>
/// </summary>
public record PayrollRefusalDto(string Code, IReadOnlyList<string> Missing, string FixableBy);

/// <summary>
/// How many payslips raised one warning. Counts rather than the warnings themselves: the run's
/// header has to be able to say "12 people have no attendance" without carrying nine hundred
/// lines, and the lines that raised it are one paged read away.
/// </summary>
public record PayrollWarningCountDto(string Code, int Lines);

/// <summary>
/// One side of the entry the run posts. Sent before approval as well as after, so the action bar
/// can say in words what it is about to do to the books — and, when a deduction with no confirmed
/// account is in play, show the ⚠ placeholder on 641 rather than hide it.
/// </summary>
public record PayrollPostingLineDto(string AccountCode, decimal Debit, decimal Credit);

/// <summary>
/// One run read on its own: the header, what it will post, its warning counts and its adjustments.
///
/// <para>
/// <c>Run.Lines</c> is deliberately left null here — a branch with nine hundred staff would send a
/// megabyte to draw six stat tiles. The lines are their own paged route. <c>Run.Warnings</c> is
/// left null too, because <see cref="Warnings"/> below is the same fact with its counts, and two
/// places for one fact is two places that can disagree.
/// </para>
/// </summary>
/// <param name="Refusal">
/// Non-null when a compute would be refused as the settings stand today. Carried on the read so
/// the screen can name the missing figure before the button is pressed rather than after.
/// </param>
/// <param name="RecomputeNeeded">
/// True when an adjustment has been typed since the figures were computed. The screen disables
/// Approve on it, because approving posts around the adjustment and then strands it for good.
/// </param>
/// <param name="SettingsSnapshot">
/// The settings this run actually computed against, as they were then — the wording every element
/// on its payslips is printed under. Null on a run that never computed, where the screen falls back
/// to the settings as they stand today. Read from the run rather than from
/// <c>GET /api/hr/settings</c> so that reprinting March's payslips in June prints March's words
/// (docs/07 §5.11).
/// </param>
public record PayrollRunDetailDto(
    PayrollRunDto Run,
    IReadOnlyList<PayrollWarningCountDto> Warnings,
    IReadOnlyList<PayrollPostingLineDto> Posting,
    PayrollRefusalDto? Refusal = null,
    bool RecomputeNeeded = false,
    AlgeriaPos.Application.Payroll.PayrollSettings? SettingsSnapshot = null);

/// <summary>A page of payslip lines. <paramref name="Total"/> is the whole run, not the page.</summary>
public record PayrollRunLinesPageDto(
    int Total, int Skip, int Take, IReadOnlyList<PayrollRunLineDto> Lines);

/// <summary>
/// One branch's month on the group summary. A branch that has not opened a run at all says so in
/// a status word of its own rather than borrowing "Draft", which would read as a run somebody
/// started.
/// </summary>
public record PayrollSummaryRowDto(
    Guid BranchId,
    string BranchName,
    Guid? RunId,
    string? RunNumber,
    string Status,
    bool IsProvisional,
    int HeadCount,
    decimal Gross,
    decimal SocialEmployee,
    decimal SocialEmployer,
    decimal IncomeTax,
    decimal EmployerLevies,
    decimal AdvancesRecovered,
    decimal Net);
