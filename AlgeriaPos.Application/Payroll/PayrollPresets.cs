namespace AlgeriaPos.Application.Payroll;

/// <summary>
/// Starting points, not authorities.
///
/// <para>
/// Every figure below moves with a finance law, and this system computes what real people are
/// paid: a wrong rate underpays someone and files a wrong declaration. So a preset ships with
/// <see cref="PayrollSettings.NeedsReview"/> set, and the accountant's job is to confirm or
/// correct a handful of numbers rather than to start from an empty screen.
/// </para>
/// <para>
/// <b>2026-09-24, the owner's decision.</b> The module ships <b>running</b>: the figures the
/// engine cannot proceed without are present as <b>placeholders</b>, so a company sees a payslip
/// on its first day and the admin edits every one of them from the back office. Two rules keep
/// that from becoming a lie:
/// </para>
/// <list type="number">
/// <item>Nothing here is written from general knowledge dressed up as law. A placeholder is a
/// round, obviously arbitrary number — tens and thousands — that no one could mistake for a
/// scale someone looked up.</item>
/// <item>Nothing is confirmed. <see cref="PayrollSettings.Confirmed"/> is false, so every run
/// and every payslip computed against these says <i>provisional</i> in words until a person with
/// the current text ticks the box and leaves their name and the date.</item>
/// </list>
/// <para>
/// There is <b>no exception</b>. An earlier draft of this file carried the real CNAS split, on
/// the grounds that it has been stable for years — and that is precisely the figure an admin
/// scrolls past without asking his accountant, because it already looks right sitting beside the
/// arbitrary brackets. A placeholder that is indistinguishable from law defeats the mark that is
/// supposed to protect everyone, so the rates below are round tens like everything else here.
/// </para>
/// </summary>
public static class PayrollPresets
{
    public const string DefaultCountry = "DZ";

    public static IReadOnlyList<string> Countries { get; } = ["DZ"];

    /// <summary>Returns a fresh copy, so callers may mutate it.</summary>
    public static PayrollSettings? Create(string? countryCode) =>
        (countryCode ?? "").Trim().ToUpperInvariant() switch
        {
            "DZ" => Algeria(),
            _ => null
        };

    private static Dictionary<string, string> Text(string ar, string fr, string en) =>
        new() { ["ar"] = ar, ["fr"] = fr, ["en"] = en };

    /// <summary>
    /// Algeria.
    ///
    /// <para>
    /// The contribution split is a <b>placeholder</b> like everything else: round tens that pay
    /// something recognisable on the first run and announce themselves as unset. The real CNAS
    /// rates are the accountant's to type, and they are the figures most likely to be waved
    /// through if the screen shows them already looking plausible.
    /// </para>
    /// <para>
    /// The IRG scale is a <b>placeholder</b>, not the scale. It was rewritten by the 2022
    /// finance law and moves again; writing the real brackets from memory would look
    /// authoritative and be wrong, and a wrong scale is not a visible failure — it is a payslip
    /// that is quietly short every month. So the steps below are round arbitrary figures
    /// (20 000 and 40 000, at 0 %, 10 % and 20 %) that announce themselves as unset, and the
    /// whole profile is unconfirmed until the accountant replaces them. An empty scale is no
    /// longer an option: <see cref="IncomeTaxMode.NotConfigured"/> means the engine refuses, and
    /// zero withholding has to be said in words.
    /// </para>
    /// <para>
    /// The minimum wage is a round placeholder for the same reason, and deliberately not the
    /// figure a search returns for the SNMG: it only ever raises a warning, never a refusal, and
    /// 0 would turn the check off silently.
    /// </para>
    /// <para>
    /// The allowances and overtime bands below are the shapes Algerian payslips use. The
    /// <b>allowance amounts stay at zero</b>, because no placeholder can stand in for what a
    /// particular business pays its people — zero pays nothing and is visibly unset, while a
    /// plausible figure would be paid. The overtime multipliers do carry placeholders: a
    /// multiplier of 1 is not "unset", it is the statement that overtime carries no premium,
    /// which is a wrong answer rather than a missing one.
    /// </para>
    /// </summary>
    private static PayrollSettings Algeria() => new()
    {
        CountryCode = "DZ",

        // Round tens, deliberately not the real split. See the note above.
        SocialEmployeeRate = 10m,
        SocialEmployerRate = 30m,
        SocialNames = Text("الضمان الاجتماعي (CNAS)", "Sécurité sociale (CNAS)", "Social security (CNAS)"),

        // Left unset rather than guessed: "no bound" and "a bound of zero" are different
        // statements, and the engine applies these only when they are present.
        SocialBaseFloor = null,
        SocialBaseCeiling = null,

        // No invented levy. An employer levy is a real cost the company would start carrying the
        // day it appeared here; its absence costs nothing and is corrected by typing one in.
        EmployerLevies = [],

        IncomeTaxMode = IncomeTaxMode.Scale,
        IncomeTaxBrackets =
        [
            new() { UpTo = 20000m, Rate = 0m },
            new() { UpTo = 40000m, Rate = 10m },
            new() { Rate = 20m }
        ],
        IncomeTaxNames = Text("الضريبة على الدخل (IRG)", "Impôt sur le revenu (IRG)", "Income tax (IRG)"),

        // The common reading, and nothing more than that — the engine reads this flag and asserts
        // nothing of its own. It is unconfirmed like every other statutory figure here.
        EmployeeContributionReducesTaxBase = true,

        MinimumWage = 10000m,

        Allowances =
        [
            Allowance("transport", "منحة النقل", "Indemnité de transport", "Transport allowance", 1,
                subjectToCnas: false, subjectToIrg: false),
            Allowance("panier", "منحة السلة", "Prime de panier", "Meal allowance", 2,
                subjectToCnas: false, subjectToIrg: false),
            Allowance("poste", "منحة المنصب", "Indemnité de poste", "Post allowance", 3,
                subjectToCnas: true, subjectToIrg: true),
            Allowance("nuisance", "منحة الضرر", "Indemnité de nuisance", "Hardship allowance", 4,
                subjectToCnas: true, subjectToIrg: true),
            Allowance("rendement", "منحة المردودية", "Prime de rendement", "Performance bonus", 5,
                subjectToCnas: true, subjectToIrg: true)
        ],

        OvertimeRules =
        [
            Overtime("hs-jour", OvertimeBand.Weekday, "ساعات إضافية نهارية", "Heures supplémentaires", "Weekday overtime", 1.5m),
            Overtime("hs-nuit", OvertimeBand.Night, "ساعات إضافية ليلية", "Heures de nuit", "Night hours", 1.5m),
            // Counted in days worked, at twice the daily rate (owner, 2026-10-10).
            Overtime("hs-repos", OvertimeBand.WeeklyRest, "يوم الراحة الأسبوعية", "Jour de repos", "Weekly rest day", 2m, OvertimeUnit.Days),
            Overtime("hs-ferie", OvertimeBand.PublicHoliday, "يوم عطلة", "Jour férié", "Public holiday", 2m, OvertimeUnit.Days)
        ],

        // None shipped. A deduction is money taken off a named person's pay, and there is no such
        // thing as a placeholder for that — the company adds the ones it actually applies.
        Deductions = [],

        // Site labour is counted by crew on a sheet far more often than it is clocked in, and
        // a default that assumes otherwise just means nobody enters anything.
        DefaultAttendanceMode = AttendanceMode.Declared,
        WorkingDaysPerMonth = 26m,
        HoursPerDay = 8m,

        // Nobody has checked any of the above. Everything computed from it says so, in words, on
        // screen and on paper, until an admin ticks the box with a name and a date.
        Confirmed = false
    };

    private static AllowanceDef Allowance(
        string key, string ar, string fr, string en, int sortOrder,
        bool subjectToCnas, bool subjectToIrg) => new()
    {
        Key = key,
        Labels = Text(ar, fr, en),
        Calculation = AllowanceCalculation.FixedAmount,
        // Zero until the company says what it actually pays. A plausible-looking default here
        // would end up on a payslip unchallenged.
        Value = 0m,
        SubjectToCnas = subjectToCnas,
        SubjectToIrg = subjectToIrg,
        SortOrder = sortOrder
    };

    private static OvertimeRuleDef Overtime(
        string key, OvertimeBand band, string ar, string fr, string en, decimal multiplier,
        OvertimeUnit unit = OvertimeUnit.Hours) => new()
    {
        Key = key,
        Labels = Text(ar, fr, en),
        AppliesTo = band,
        Unit = unit,
        // A round placeholder premium, so a month with overtime in it pays something recognisable
        // on the first run. It is not the agreement and it is not the law — the company types its
        // own, and the profile says unconfirmed until it does.
        Multiplier = multiplier,
        // The common reading, carried explicitly so the engine never has to decide it.
        SubjectToCnas = true,
        SubjectToIrg = true
    };
}
