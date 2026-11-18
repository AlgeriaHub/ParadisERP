using System.Text.Json;

namespace ParadisERP.Application.Payroll;

/// <summary>Who is allowed to move a payroll figure.</summary>
public enum PayrollFieldOwner
{
    /// <summary>
    /// The platform operator. Statutory figures that come from law rather than from the
    /// business: contribution rates, the income-tax scale, the minimum wage.
    /// </summary>
    Operator = 0,

    /// <summary>
    /// The company administrator. What this particular business pays and how it counts time —
    /// its allowances, its overtime rates, its working day.
    /// </summary>
    Company = 1
}

/// <summary>
/// The split between what a company may change about its own payroll and what only the
/// platform operator may change.
///
/// <para>
/// A company deciding its own meal allowance is ordinary administration. A company editing the
/// CNAS rate is not: the figure comes from law, it is the same for every company in the
/// country, and a business that finds its contributions inconvenient must not be able to
/// quietly lower them. That is the same division the product already draws — the operator owns
/// the country profile and its tax rates, the company owns its store settings and its loyalty
/// programme — applied to payroll.
/// </para>
/// <para>
/// This is enforced here rather than left to the endpoint that happens to be in front of it,
/// so a second screen, an import or a future API cannot route around it by accident.
/// </para>
/// <para>
/// <b>2026-09-24, the owner's decision.</b> Who may edit the statutory half is now a named
/// permission on a back-office screen rather than deployment configuration: the company's admin
/// types the figures his own accountant gives him. This split stays exactly as it is, because it
/// is no longer only a gate — it is the <b>record of which figures are statutory</b>, and that
/// is what <see cref="PayrollSettings.NeedsReview"/> marks as unconfirmed until someone says
/// otherwise. <see cref="ApplyCompanyEdit"/> therefore still refuses a statutory change: an
/// admin holding the permission goes through <see cref="ApplyOperatorEdit"/>, where saying so is
/// deliberate and leaves a name and a date.
/// </para>
/// </summary>
public static class PayrollOwnership
{
    /// <summary>What each field is, for a screen that wants to show why something is read-only.</summary>
    public static IReadOnlyDictionary<string, PayrollFieldOwner> Fields { get; } =
        new Dictionary<string, PayrollFieldOwner>
        {
            [nameof(PayrollSettings.SocialEmployeeRate)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.SocialEmployerRate)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.SocialNames)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.SocialBaseFloor)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.SocialBaseCeiling)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.EmployerLevies)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.IncomeTaxMode)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.IncomeTaxBrackets)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.IncomeTaxNames)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.EmployeeContributionReducesTaxBase)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.MinimumWage)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.Confirmed)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.ConfirmedAtUtc)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.ConfirmedBy)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.NeedsReview)] = PayrollFieldOwner.Operator,
            [nameof(PayrollSettings.CountryCode)] = PayrollFieldOwner.Operator,

            [nameof(PayrollSettings.Allowances)] = PayrollFieldOwner.Company,
            [nameof(PayrollSettings.OvertimeRules)] = PayrollFieldOwner.Company,
            [nameof(PayrollSettings.Deductions)] = PayrollFieldOwner.Company,
            [nameof(PayrollSettings.DefaultAttendanceMode)] = PayrollFieldOwner.Company,
            [nameof(PayrollSettings.WorkingDaysPerMonth)] = PayrollFieldOwner.Company,
            [nameof(PayrollSettings.HoursPerDay)] = PayrollFieldOwner.Company,
            [nameof(PayrollSettings.AbsenceOffGross)] = PayrollFieldOwner.Company,
            // The company's choice of payslip (owner, 2026-10-10), not a statutory figure: it says
            // what this company computes through the system, not what the law requires.
            [nameof(PayrollSettings.NetPayOnly)] = PayrollFieldOwner.Company,
            [nameof(PayrollSettings.Schedule)] = PayrollFieldOwner.Company,
            // Whether a workers' bonus paid by voucher counts in the two bases (owner, 2026-11-08): the company's choice,
            // like an allowance's own flags, until the accountant answers.
            [nameof(PayrollSettings.VoucherBonusInSocialBase)] = PayrollFieldOwner.Company,
            [nameof(PayrollSettings.VoucherBonusInTaxBase)] = PayrollFieldOwner.Company
        };

    /// <summary>
    /// The same table keyed the way the JSON wire names the properties.
    ///
    /// <para>
    /// <b>This exists because System.Text.Json does not camel-case dictionary keys.</b> Its
    /// <c>PropertyNamingPolicy</c> renames properties only; a <c>Dictionary&lt;string,…&gt;</c>
    /// serialises its keys verbatim. So <see cref="Fields"/> went out as
    /// <c>{"IncomeTaxMode":"Operator"}</c> while the screen — which reads every other property of
    /// the same payload in camel case — looked up <c>incomeTaxMode</c>, found nothing, and hid
    /// four statutory controls the server was carrying and owned. That is the dead wiring this
    /// codebase has already paid for; the conversion belongs here, where a test can pin it,
    /// rather than in the endpoint that happens to be in front of it.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, PayrollFieldOwner> WireFields { get; } =
        Fields.ToDictionary(pair => JsonNamingPolicy.CamelCase.ConvertName(pair.Key), pair => pair.Value);

    /// <summary>
    /// The statutory figures, in declaration order — what "unconfirmed" is about, for a screen
    /// that wants to list beside its banner exactly which figures are still placeholders.
    /// </summary>
    public static IReadOnlyList<string> Statutory { get; } =
        Fields.Where(f => f.Value == PayrollFieldOwner.Operator).Select(f => f.Key).ToList();

    /// <summary>
    /// Applies a company administrator's edit: their own fields are taken, the statutory ones
    /// are kept from <paramref name="current"/> whatever the request said.
    ///
    /// <para>
    /// A changed statutory figure is <b>refused rather than ignored</b>. Silently discarding it
    /// would leave the administrator believing they had lowered a contribution rate, and the
    /// first sign otherwise would be a payslip that disagrees with the screen they saved.
    /// </para>
    /// </summary>
    public static PayrollSettings ApplyCompanyEdit(PayrollSettings current, PayrollSettings incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);

        var refused = StatutoryDifferences(current, incoming);
        if (refused.Count > 0)
            throw new UnauthorizedAccessException(
                "These are set by the platform operator and cannot be changed here: " +
                string.Join(", ", refused) + ".");

        var merged = Clone(current);
        merged.Allowances = incoming.Allowances ?? [];
        merged.OvertimeRules = incoming.OvertimeRules ?? [];
        merged.Deductions = incoming.Deductions ?? [];
        merged.DefaultAttendanceMode = incoming.DefaultAttendanceMode;
        merged.WorkingDaysPerMonth = incoming.WorkingDaysPerMonth;
        merged.HoursPerDay = incoming.HoursPerDay;
        merged.AbsenceOffGross = incoming.AbsenceOffGross;
        merged.NetPayOnly = incoming.NetPayOnly;
        // A screen built before the schedule existed sends none: that keeps the stored one rather
        // than putting the defaults back over the admin's hours.
        merged.Schedule = incoming.Schedule ?? merged.Schedule;
        merged.VoucherBonusInSocialBase = incoming.VoucherBonusInSocialBase;
        merged.VoucherBonusInTaxBase = incoming.VoucherBonusInTaxBase;

        PayrollSettingsValidator.NormalizeAndValidate(merged);
        return merged;
    }

    /// <summary>
    /// Applies a platform operator's edit. The operator may set anything, including the
    /// company's own fields, because they provision companies and answer for the figures.
    /// <para>
    /// Changing a statutory figure clears <see cref="PayrollSettings.NeedsReview"/> only when
    /// the caller says so: an operator correcting a typo has not thereby checked the scale
    /// against the finance law.
    /// </para>
    /// </summary>
    public static PayrollSettings ApplyOperatorEdit(PayrollSettings incoming, bool reviewed) =>
        ApplyOperatorEdit(incoming, reviewed, by: null, atUtc: null);

    /// <summary>
    /// The same edit, with the name and the date behind <paramref name="reviewed"/>.
    ///
    /// <para>
    /// "Our accountant checked these" is worth nothing unless the record says who and when, so
    /// confirming stamps both; un-confirming clears them rather than leaving a stale name beside
    /// figures nobody stands behind any more.
    /// </para>
    /// </summary>
    public static PayrollSettings ApplyOperatorEdit(
        PayrollSettings incoming, bool reviewed, string? by, DateTime? atUtc)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var next = Clone(incoming);
        next.Confirmed = reviewed;
        if (reviewed)
        {
            next.ConfirmedAtUtc = atUtc ?? incoming.ConfirmedAtUtc ?? DateTime.UtcNow;
            next.ConfirmedBy = string.IsNullOrWhiteSpace(by) ? incoming.ConfirmedBy : by;
        }
        else
        {
            next.ConfirmedAtUtc = null;
            next.ConfirmedBy = null;
        }

        PayrollSettingsValidator.NormalizeAndValidate(next);
        return next;
    }

    /// <summary>Names the statutory fields an edit tried to move, in the order they are declared.</summary>
    public static IReadOnlyList<string> StatutoryDifferences(PayrollSettings current, PayrollSettings incoming)
    {
        var changed = new List<string>();

        if (current.SocialEmployeeRate != incoming.SocialEmployeeRate) changed.Add(nameof(current.SocialEmployeeRate));
        if (current.SocialEmployerRate != incoming.SocialEmployerRate) changed.Add(nameof(current.SocialEmployerRate));
        if (current.SocialBaseFloor != incoming.SocialBaseFloor) changed.Add(nameof(current.SocialBaseFloor));
        if (current.SocialBaseCeiling != incoming.SocialBaseCeiling) changed.Add(nameof(current.SocialBaseCeiling));
        if (current.MinimumWage != incoming.MinimumWage) changed.Add(nameof(current.MinimumWage));
        if (current.IncomeTaxMode != incoming.IncomeTaxMode) changed.Add(nameof(current.IncomeTaxMode));
        if (current.EmployeeContributionReducesTaxBase != incoming.EmployeeContributionReducesTaxBase)
            changed.Add(nameof(current.EmployeeContributionReducesTaxBase));
        // Ticking "our accountant confirmed these" is itself a statutory act: it is what takes the
        // provisional mark off every payslip, so it may not ride along on a company edit.
        if (current.Confirmed != incoming.Confirmed) changed.Add(nameof(current.Confirmed));
        if (!string.Equals(current.CountryCode, incoming.CountryCode, StringComparison.OrdinalIgnoreCase))
            changed.Add(nameof(current.CountryCode));
        if (!SameText(current.SocialNames, incoming.SocialNames)) changed.Add(nameof(current.SocialNames));
        if (!SameText(current.IncomeTaxNames, incoming.IncomeTaxNames)) changed.Add(nameof(current.IncomeTaxNames));
        if (!SameBrackets(current.IncomeTaxBrackets, incoming.IncomeTaxBrackets)) changed.Add(nameof(current.IncomeTaxBrackets));
        if (!SameLevies(current.EmployerLevies, incoming.EmployerLevies)) changed.Add(nameof(current.EmployerLevies));

        return changed;
    }

    private static bool SameLevies(List<LevyDef>? a, List<LevyDef>? b)
    {
        a ??= []; b ??= [];
        return a.Count == b.Count && a.Zip(b).All(pair =>
            pair.First.Key == pair.Second.Key
            && pair.First.Rate == pair.Second.Rate
            && pair.First.IsActive == pair.Second.IsActive
            && SameText(pair.First.Labels, pair.Second.Labels));
    }

    private static bool SameBrackets(List<TaxBracket>? a, List<TaxBracket>? b)
    {
        a ??= []; b ??= [];
        return a.Count == b.Count && a.Zip(b).All(pair => pair.First.UpTo == pair.Second.UpTo && pair.First.Rate == pair.Second.Rate);
    }

    private static bool SameText(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        a ??= new(); b ??= new();
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static PayrollSettings Clone(PayrollSettings s) => new()
    {
        CountryCode = s.CountryCode,
        SocialEmployeeRate = s.SocialEmployeeRate,
        SocialEmployerRate = s.SocialEmployerRate,
        SocialNames = new Dictionary<string, string>(s.SocialNames ?? new()),
        SocialBaseFloor = s.SocialBaseFloor,
        SocialBaseCeiling = s.SocialBaseCeiling,
        EmployerLevies = (s.EmployerLevies ?? []).Select(l => new LevyDef
        {
            Key = l.Key,
            Labels = new Dictionary<string, string>(l.Labels ?? new()),
            Rate = l.Rate,
            IsActive = l.IsActive
        }).ToList(),
        IncomeTaxMode = s.IncomeTaxMode,
        IncomeTaxBrackets = (s.IncomeTaxBrackets ?? []).Select(b => new TaxBracket { UpTo = b.UpTo, Rate = b.Rate }).ToList(),
        IncomeTaxNames = new Dictionary<string, string>(s.IncomeTaxNames ?? new()),
        EmployeeContributionReducesTaxBase = s.EmployeeContributionReducesTaxBase,
        MinimumWage = s.MinimumWage,
        Allowances = (s.Allowances ?? []).Select(a => new AllowanceDef
        {
            Key = a.Key,
            Labels = new Dictionary<string, string>(a.Labels ?? new()),
            Calculation = a.Calculation,
            Value = a.Value,
            SubjectToCnas = a.SubjectToCnas,
            SubjectToIrg = a.SubjectToIrg,
            IsActive = a.IsActive,
            SortOrder = a.SortOrder
        }).ToList(),
        OvertimeRules = (s.OvertimeRules ?? []).Select(r => new OvertimeRuleDef
        {
            Key = r.Key,
            Labels = new Dictionary<string, string>(r.Labels ?? new()),
            AppliesTo = r.AppliesTo,
            Multiplier = r.Multiplier,
            Unit = r.Unit,
            SubjectToCnas = r.SubjectToCnas,
            SubjectToIrg = r.SubjectToIrg,
            IsActive = r.IsActive
        }).ToList(),
        Deductions = (s.Deductions ?? []).Select(d => new DeductionDef
        {
            Key = d.Key,
            Labels = new Dictionary<string, string>(d.Labels ?? new()),
            Calculation = d.Calculation,
            Value = d.Value,
            ReducesSocialBase = d.ReducesSocialBase,
            ReducesTaxBase = d.ReducesTaxBase,
            Treatment = d.Treatment,
            IsActive = d.IsActive,
            SortOrder = d.SortOrder
        }).ToList(),
        DefaultAttendanceMode = s.DefaultAttendanceMode,
        WorkingDaysPerMonth = s.WorkingDaysPerMonth,
        HoursPerDay = s.HoursPerDay,
        AbsenceOffGross = s.AbsenceOffGross,
        NetPayOnly = s.NetPayOnly,
        Schedule = CloneSchedule(s.Schedule),
        VoucherBonusInSocialBase = s.VoucherBonusInSocialBase,
        VoucherBonusInTaxBase = s.VoucherBonusInTaxBase,
        Confirmed = s.Confirmed,
        ConfirmedAtUtc = s.ConfirmedAtUtc,
        ConfirmedBy = s.ConfirmedBy
    };

    private static WorkSchedule CloneSchedule(WorkSchedule? w) => w is null ? new WorkSchedule() : new WorkSchedule
    {
        Start = w.Start,
        End = w.End,
        BreakMinutes = w.BreakMinutes,
        LateGraceMinutes = w.LateGraceMinutes,
        RestDays = [.. w.RestDays ?? []],
        Periods = (w.Periods ?? []).Select(p => new WorkSchedulePeriod
        {
            Name = p.Name, From = p.From, To = p.To, Start = p.Start, End = p.End, BreakMinutes = p.BreakMinutes
        }).ToList()
    };
}
