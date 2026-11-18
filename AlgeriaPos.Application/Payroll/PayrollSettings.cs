using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AlgeriaPos.Application.Payroll;

// Every enum below crosses the wire to the payroll settings screen, and it crosses it by
// NAME. The API registers no global JsonStringEnumConverter — adding one would change the
// shape of every other endpoint — so without these attributes System.Text.Json writes the
// ordinal, the screen's <option value="Declared"> matches nothing, and the first save after
// the admin touches a select is refused by the model binder before a handler sees it.
// JsonStringEnumConverter reads names *and* numbers, so settings rows written as integers
// before this attribute existed still deserialise.
/// <summary>How an employee's gross pay is arrived at. Set per employee, not per company.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PayBasis
{
    /// <summary>Pay follows the days actually worked. Site labour, where the count moves weekly.</summary>
    Daily = 0,
    /// <summary>A fixed figure, against which absence is a deduction.</summary>
    Monthly = 1
}

/// <summary>Where the days a payslip is built from come from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttendanceMode
{
    /// <summary>Clock-in and clock-out entries.</summary>
    Recorded = 0,
    /// <summary>A period total entered by hand — a foreman submitting the days worked.</summary>
    Declared = 1
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AllowanceCalculation
{
    FixedAmount = 0,
    PercentOfBase = 1
}

/// <summary>
/// Whether income tax is withheld, and on what authority.
///
/// <para>
/// An empty scale used to mean "no tax is withheld". That is two different situations wearing
/// one face: a company genuinely below the threshold, and a company nobody has configured yet.
/// The second must not quietly pay everybody too much, so it is a mode of its own and the
/// engine refuses to compute against it.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncomeTaxMode
{
    /// <summary>Nobody has entered a scale. The engine refuses; it never withholds zero by default.</summary>
    NotConfigured = 0,
    /// <summary>Withhold per <see cref="PayrollSettings.IncomeTaxBrackets"/>.</summary>
    Scale = 1,
    /// <summary>Deliberately withhold nothing. A statement, not an absence of one.</summary>
    None = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeductionCalculation
{
    FixedAmount = 0,
    PercentOfBase = 1
}

/// <summary>What a deduction does to the company's books, which is not the same as what it does to net pay.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeductionTreatment
{
    /// <summary>The company pays less wage: the deduction reduces the wage expense.</summary>
    ReducesWageCost = 0,
    /// <summary>
    /// Money withheld for someone else — a fine, dues collected for a third party. It is not a
    /// wage saving, and the account it belongs in is the accountant's answer (docs/07 §5.9 posts
    /// it to a visible placeholder until they give it).
    /// </summary>
    Other = 1
}

/// <summary>
/// A standing deduction the company applies, as the admin defines it. Whether it leaves the
/// contribution base and whether it leaves the tax base are two separate answers, and both come
/// from the definition rather than from the engine.
/// </summary>
public class DeductionDef
{
    public string Key { get; set; } = "";
    public Dictionary<string, string> Labels { get; set; } = new();
    public DeductionCalculation Calculation { get; set; } = DeductionCalculation.FixedAmount;
    /// <summary>An amount, or a percentage of the base wage, per <see cref="Calculation"/>. Always positive; the engine signs it.</summary>
    public decimal Value { get; set; }

    public bool ReducesSocialBase { get; set; }
    public bool ReducesTaxBase { get; set; }

    public DeductionTreatment Treatment { get; set; } = DeductionTreatment.ReducesWageCost;

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>
/// An employer levy on the contribution base — a cost the company carries on top of wages and
/// never a deduction from anyone's pay. Which levies exist, and at what rate, is statutory.
/// </summary>
public class LevyDef
{
    public string Key { get; set; } = "";
    public Dictionary<string, string> Labels { get; set; } = new();
    /// <summary>Percentage of the social base.</summary>
    public decimal Rate { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>When an overtime rate applies.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OvertimeBand
{
    Weekday = 0,
    Night = 1,
    WeeklyRest = 2,
    PublicHoliday = 3
}

/// <summary>
/// What an overtime band counts (owner, 2026-10-10): hours, cut from the daily rate by
/// <see cref="PayrollSettings.HoursPerDay"/>, or whole days — a public holiday or a weekly rest day
/// worked is paid as days at the daily rate times the band's multiplier.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OvertimeUnit
{
    Hours = 0,
    Days = 1
}

/// <summary>
/// One allowance the company pays, as the admin defines it. Not a fixed set of columns: a
/// business adds and retires these without a release.
/// </summary>
public class AllowanceDef
{
    public string Key { get; set; } = "";
    public Dictionary<string, string> Labels { get; set; } = new();
    public AllowanceCalculation Calculation { get; set; } = AllowanceCalculation.FixedAmount;
    /// <summary>An amount, or a percentage of the base wage, per <see cref="Calculation"/>.</summary>
    public decimal Value { get; set; }

    /// <summary>
    /// Whether this allowance enters the social-contribution base. Not decoration: in Algerian
    /// payroll some allowances are in and some are out, and the wrong flag changes both the
    /// employee's net pay and what is declared to CNAS. Which is which comes from the
    /// company's accountant, never from a default.
    /// </summary>
    public bool SubjectToCnas { get; set; } = true;

    /// <summary>Whether it enters the income-tax base. Same caution as <see cref="SubjectToCnas"/>.</summary>
    public bool SubjectToIrg { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>An overtime rate for a band of hours, as a multiplier on the hourly rate.</summary>
public class OvertimeRuleDef
{
    public string Key { get; set; } = "";
    public Dictionary<string, string> Labels { get; set; } = new();
    public OvertimeBand AppliesTo { get; set; } = OvertimeBand.Weekday;
    public decimal Multiplier { get; set; } = 1m;

    /// <summary>
    /// Hours or days. Days pay the daily rate (the monthly wage ÷ <see cref="PayrollSettings.WorkingDaysPerMonth"/>)
    /// times <see cref="Multiplier"/> for each day entered. Hours by default, which is how every
    /// band computed before the setting existed.
    /// </summary>
    public OvertimeUnit Unit { get; set; } = OvertimeUnit.Hours;

    /// <summary>
    /// Whether these hours enter the social-contribution base. Same explicit flag as an
    /// allowance carries, for the same reason: the engine must not decide it.
    /// </summary>
    public bool SubjectToCnas { get; set; } = true;

    /// <summary>Whether these hours enter the income-tax base.</summary>
    public bool SubjectToIrg { get; set; } = true;

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One step of a progressive income-tax scale: everything above the previous step's ceiling
/// and up to <see cref="UpTo"/> is taxed at <see cref="Rate"/>. The last step carries no
/// ceiling.
/// </summary>
public class TaxBracket
{
    /// <summary>Upper bound of the step; null means "and above".</summary>
    public decimal? UpTo { get; set; }
    public decimal Rate { get; set; }
}

/// <summary>
/// Everything the payroll engine needs that varies by country or by company, held as data.
///
/// <para>
/// This follows the same rule the rest of the product follows: what changes between countries
/// is a setting, not code. Algeria works out of the box from its preset, another country is
/// configured from the screen, and a change in the finance law is an edit rather than a
/// release.
/// </para>
/// <para>
/// <see cref="NeedsReview"/> exists because the statutory figures below move with each finance
/// law. A preset is a starting point that saves the accountant typing, never an authority —
/// the flag stays set until someone with the current text confirms them. It is not a
/// stored field of its own: it is <see cref="Confirmed"/> read the other way round, so there is
/// one fact and not two that can disagree.
/// </para>
/// </summary>
public class PayrollSettings
{
    public string CountryCode { get; set; } = "";

    // ---------- social contributions ----------

    /// <summary>Withheld from the employee's pay.</summary>
    public decimal SocialEmployeeRate { get; set; }

    /// <summary>Paid by the company on top of the wage; a cost, not a deduction.</summary>
    public decimal SocialEmployerRate { get; set; }

    /// <summary>What the contribution is called on a payslip, by language.</summary>
    public Dictionary<string, string> SocialNames { get; set; } = new();

    /// <summary>
    /// Lower bound of the contribution base, when the law sets one; null means there is none.
    /// Applied only when set — an unset bound is not a bound of zero.
    /// </summary>
    public decimal? SocialBaseFloor { get; set; }

    /// <summary>Upper bound of the contribution base, when the law caps it; null means uncapped.</summary>
    public decimal? SocialBaseCeiling { get; set; }

    /// <summary>Levies the employer pays on the contribution base beyond the social contribution itself.</summary>
    public List<LevyDef> EmployerLevies { get; set; } = new();

    // ---------- income tax ----------

    public IncomeTaxMode IncomeTaxMode { get; set; } = IncomeTaxMode.NotConfigured;

    public List<TaxBracket> IncomeTaxBrackets { get; set; } = new();
    public Dictionary<string, string> IncomeTaxNames { get; set; } = new();

    /// <summary>
    /// Whether the employee's social contribution comes off the income-tax base before the scale
    /// is applied.
    ///
    /// <para>
    /// A flag, not an assumption. Most jurisdictions do deduct it, which is why the default is
    /// <c>true</c>, but the engine asserts nothing about it — it reads this and nothing else. A
    /// wrong answer here moves every payslip's tax, so it belongs with the figures the accountant
    /// confirms.
    /// </para>
    /// </summary>
    public bool EmployeeContributionReducesTaxBase { get; set; } = true;

    // ---------- workers' bonuses paid by voucher (owner, 2026-11-08) ----------

    /// <summary>A bonus paid by payment voucher counts in the contribution base. On by default; the accountant confirms.</summary>
    public bool VoucherBonusInSocialBase { get; set; } = true;

    /// <summary>A bonus paid by payment voucher counts in the income-tax base. On by default; the accountant confirms.</summary>
    public bool VoucherBonusInTaxBase { get; set; } = true;

    // ---------- floor ----------

    /// <summary>Statutory minimum monthly wage. 0 turns the check off.</summary>
    public decimal MinimumWage { get; set; }

    // ---------- company practice ----------

    public List<AllowanceDef> Allowances { get; set; } = new();
    public List<OvertimeRuleDef> OvertimeRules { get; set; } = new();
    public List<DeductionDef> Deductions { get; set; } = new();

    /// <summary>Company default; an employee may override it.</summary>
    public AttendanceMode DefaultAttendanceMode { get; set; } = AttendanceMode.Declared;

    /// <summary>Working days in a month, for turning a monthly wage into a daily rate.</summary>
    public decimal WorkingDaysPerMonth { get; set; } = 26m;

    /// <summary>Hours in a working day, for the hourly rate overtime multiplies.</summary>
    public decimal HoursPerDay { get; set; } = 8m;

    /// <summary>
    /// Whether an unpaid absent day comes off the gross the person would have received — the base
    /// and the allowances — rather than the base alone (owner, 2026-10-02, answer 40: "a day of
    /// absence is the gross ÷ 30, taken off what he would receive"). Off by default, which is how
    /// every run before the setting computed. With it on, a percentage allowance is a percentage of
    /// the whole base, and the absence line carries the gross day rate.
    /// </summary>
    public bool AbsenceOffGross { get; set; }

    /// <summary>
    /// Legacy net-only document choice retained to read payroll settings snapshots produced before
    /// 2026-10-21. New and draft runs use their own statutory-deduction policy instead, so this
    /// cannot make a newly computed slip drop allowances or employer levies.
    /// </summary>
    public bool NetPayOnly { get; set; }

    /// <summary>
    /// The working day and the dated periods that replace it — Ramadan (owner, 2026-10-10). What
    /// attendance is read against: late, early, a missing exit, time after the end. A day inside a
    /// period counts that period's paid hours as a full day; every other day keeps
    /// <see cref="HoursPerDay"/>. See <see cref="WorkScheduleRules"/>.
    /// </summary>
    public WorkSchedule Schedule { get; set; } = new();

    // ---------- has anyone checked these? ----------

    /// <summary>
    /// Set once someone with the current finance law in front of them has said these figures are
    /// right.
    ///
    /// <para>
    /// The owner asked for the module to ship running: the preset carries placeholder figures so
    /// a company can see a payslip on day one. The price of that is that a placeholder must never
    /// be able to pass itself off as law, so nothing is confirmed until a person says it is, and
    /// everything computed before then is marked provisional on screen and on paper.
    /// </para>
    /// </summary>
    public bool Confirmed { get; set; }

    /// <summary>When it was confirmed. Kept because "our accountant checked it" is worth nothing without a date.</summary>
    public DateTime? ConfirmedAtUtc { get; set; }

    /// <summary>Who said so.</summary>
    public string? ConfirmedBy { get; set; }

    /// <summary>
    /// Whether the statutory figures are still unchecked. Nothing refuses to run because of it —
    /// it is there so a payslip can be marked provisional and the settings screen can say plainly
    /// that these have not been checked against the current law.
    ///
    /// <para>
    /// Derived from <see cref="Confirmed"/> rather than stored beside it: two fields for one fact
    /// is two fields that can disagree, and the one that would win is the one nobody set.
    /// Which figures the mark is about is <see cref="PayrollOwnership"/>'s statutory half.
    /// </para>
    /// </summary>
    public bool NeedsReview => !Confirmed;
}

public static partial class PayrollSettingsValidator
{
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,29}$")] private static partial Regex KeyPattern();

    /// <summary>
    /// Cleans the settings up and rejects the ones that would silently produce wrong pay.
    /// Deliberately strict about the tax scale: a gap or an overlap in the brackets does not
    /// fail loudly at runtime, it just pays someone the wrong amount every month.
    /// </summary>
    public static void NormalizeAndValidate(PayrollSettings s)
    {
        s.CountryCode = (s.CountryCode ?? "").Trim().ToUpperInvariant();
        s.Allowances ??= [];
        s.OvertimeRules ??= [];
        s.Deductions ??= [];
        s.EmployerLevies ??= [];
        s.IncomeTaxBrackets ??= [];
        s.Schedule ??= new WorkSchedule();
        WorkScheduleRules.NormalizeAndValidate(s.Schedule);
        s.ConfirmedBy = string.IsNullOrWhiteSpace(s.ConfirmedBy) ? null : s.ConfirmedBy.Trim();

        // Nothing is confirmed by nobody on no date: an unconfirmed profile carries no stamp at
        // all, so a screen can never show a name beside figures that are still placeholders.
        if (!s.Confirmed) { s.ConfirmedAtUtc = null; s.ConfirmedBy = null; }
        else if (s.ConfirmedAtUtc is null) Fail("Confirmed figures must carry the date they were confirmed.");

        if (s.SocialEmployeeRate is < 0 or > 100) Fail("The employee contribution rate must be between 0 and 100.");
        if (s.SocialEmployerRate is < 0 or > 100) Fail("The employer contribution rate must be between 0 and 100.");
        if (s.MinimumWage < 0) Fail("The minimum wage cannot be negative.");
        if (s.WorkingDaysPerMonth is <= 0 or > 31) Fail("Working days per month must be between 1 and 31.");
        if (s.HoursPerDay is <= 0 or > 24) Fail("Hours per day must be between 1 and 24.");

        if (s.SocialBaseFloor is < 0) Fail("The contribution base floor cannot be negative.");
        if (s.SocialBaseCeiling is < 0) Fail("The contribution base ceiling cannot be negative.");
        if (s.SocialBaseFloor is decimal baseFloor && s.SocialBaseCeiling is decimal baseCeiling && baseFloor > baseCeiling)
            Fail("The contribution base floor cannot be above its ceiling.");

        // A scale nobody applies is a scale nobody checks, so the two modes that withhold by the
        // scale and by decision are kept apart from the mode that has no scale at all.
        switch (s.IncomeTaxMode)
        {
            case IncomeTaxMode.Scale when s.IncomeTaxBrackets.Count == 0:
                Fail("A scale mode needs at least one tax bracket.");
                break;
            case IncomeTaxMode.NotConfigured or IncomeTaxMode.None when s.IncomeTaxBrackets.Count > 0:
                Fail("Only the scale mode may carry tax brackets.");
                break;
        }

        // Ordered, non-overlapping, and open at the top. A scale whose last step has a ceiling
        // silently taxes nothing above it.
        decimal previous = 0;
        for (var i = 0; i < s.IncomeTaxBrackets.Count; i++)
        {
            var bracket = s.IncomeTaxBrackets[i];
            if (bracket.Rate is < 0 or > 100) Fail("Each tax rate must be between 0 and 100.");
            var isLast = i == s.IncomeTaxBrackets.Count - 1;
            if (isLast)
            {
                if (bracket.UpTo is not null) Fail("The top tax bracket must be open-ended.");
                continue;
            }
            if (bracket.UpTo is not decimal ceiling) Fail("Only the top tax bracket may be open-ended.");
            else if (ceiling <= previous) Fail("Tax brackets must be in ascending order with no overlap.");
            else previous = ceiling;
        }

        foreach (var allowance in s.Allowances)
        {
            allowance.Key = (allowance.Key ?? "").Trim().ToLowerInvariant();
            if (!KeyPattern().IsMatch(allowance.Key))
                Fail("Allowance keys use lower-case letters, digits, '-' and '_' (max 30).");
            if (allowance.Value < 0) Fail("An allowance cannot be negative.");
            if (allowance.Calculation == AllowanceCalculation.PercentOfBase && allowance.Value > 100)
                Fail("A percentage allowance cannot exceed 100% of the base wage.");
        }
        if (s.Allowances.Select(a => a.Key).Distinct().Count() != s.Allowances.Count)
            Fail("Allowance keys must be unique.");

        foreach (var rule in s.OvertimeRules)
        {
            rule.Key = (rule.Key ?? "").Trim().ToLowerInvariant();
            if (!KeyPattern().IsMatch(rule.Key))
                Fail("Overtime keys use lower-case letters, digits, '-' and '_' (max 30).");
            // Below 1 would pay overtime worse than ordinary time, which is never intended
            // and is the kind of typo nobody notices until a payslip is queried.
            if (rule.Multiplier is < 1m or > 5m) Fail("An overtime multiplier must be between 1 and 5.");
        }
        if (s.OvertimeRules.Select(r => r.Key).Distinct().Count() != s.OvertimeRules.Count)
            Fail("Overtime keys must be unique.");

        foreach (var deduction in s.Deductions)
        {
            deduction.Key = (deduction.Key ?? "").Trim().ToLowerInvariant();
            if (!KeyPattern().IsMatch(deduction.Key))
                Fail("Deduction keys use lower-case letters, digits, '-' and '_' (max 30).");
            // Held positive and signed by the engine. A negative deduction would be a pay rise
            // entered in the wrong screen, and nothing downstream would question it.
            if (deduction.Value < 0) Fail("A deduction cannot be negative.");
            if (deduction.Calculation == DeductionCalculation.PercentOfBase && deduction.Value > 100)
                Fail("A percentage deduction cannot exceed 100% of the base wage.");
        }
        if (s.Deductions.Select(d => d.Key).Distinct().Count() != s.Deductions.Count)
            Fail("Deduction keys must be unique.");

        foreach (var levy in s.EmployerLevies)
        {
            levy.Key = (levy.Key ?? "").Trim().ToLowerInvariant();
            if (!KeyPattern().IsMatch(levy.Key))
                Fail("Employer levy keys use lower-case letters, digits, '-' and '_' (max 30).");
            if (levy.Rate is < 0 or > 100) Fail("An employer levy rate must be between 0 and 100.");
        }
        if (s.EmployerLevies.Select(l => l.Key).Distinct().Count() != s.EmployerLevies.Count)
            Fail("Employer levy keys must be unique.");
    }

    private static void Fail(string message) => throw new ArgumentException(message);
}
