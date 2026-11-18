using ParadiERP.Application.Regional;

namespace ParadiERP.Application.Payroll;

/// <summary>
/// What a payslip line is made of, in the order the engine produces it. Mirrors
/// <c>ParadiERP.Domain.PayrollElementKind</c>.
/// </summary>
public enum PayrollElementKind
{
    Base = 0,
    Absence = 1,
    Allowance = 2,
    Overtime = 3,
    Deduction = 4,
    Adjustment = 5
}

/// <summary>An employee's own figure for one allowance, which wins over the company definition.</summary>
/// <param name="IsEnabled">False pays nothing at all, which is not the same as a value of zero.</param>
public sealed record EmployeeAllowanceOverride(string Key, decimal Value, bool IsEnabled = true);

/// <summary>An advance still owed, as it stands before this run touches it.</summary>
public sealed record OpenAdvance(Guid AdvanceId, decimal Outstanding);

/// <summary>
/// A one-off figure for one person in one run. An input, re-read on every compute — never an
/// element, so a recompute keeps it. Signed: a bonus is positive, a fine negative.
/// </summary>
public sealed record PayrollAdjustmentInput(
    string Key, decimal Amount, bool InSocialBase = false, bool InTaxBase = false);

/// <summary>
/// A workers' bonus the person already received by payment voucher (owner, 2026-11-08). The payslip shows it as an
/// earning and takes it back as paid by voucher, so it is never paid twice.
/// </summary>
public sealed record VoucherBonusInput(Guid LineId, decimal Amount, bool InSocialBase, bool InTaxBase);

/// <summary>Everything the engine needs about one person for one month, and nothing else.</summary>
public sealed record EmployeeInput(
    Guid EmployeeId,
    PayBasis Basis,
    decimal BaseRate,
    AttendanceSummary Attendance,
    IReadOnlyList<EmployeeAllowanceOverride>? Allowances = null,
    IReadOnlyList<OpenAdvance>? OpenAdvances = null,
    IReadOnlyList<PayrollAdjustmentInput>? Adjustments = null,
    IReadOnlyList<VoucherBonusInput>? VoucherBonuses = null);

/// <summary>One printed line of a payslip. <paramref name="Amount"/> is signed.</summary>
public sealed record PayrollElement(
    string Key, PayrollElementKind Kind, decimal Amount,
    decimal? Qty, decimal? Rate, bool InSocialBase, bool InTaxBase, int SortOrder);

/// <summary>
/// Something the run should say out loud. Never a refusal: the run must always be able to state
/// what it would pay and why, so the accountant can fix the input (CLAUDE.md's first rule, read
/// for payroll).
/// </summary>
/// <param name="Code">A stable code; the screen and the payslip resolve <c>hr.warning.{code}</c>.</param>
/// <param name="Key">The allowance, overtime or deduction key it is about, when it is about one.</param>
/// <param name="Value">The figure that provoked it, for a sentence that names the amount.</param>
public sealed record PayrollWarning(string Code, string? Key = null, decimal? Value = null);

/// <summary>The warning codes the engine can raise. The API turns these into <c>hr.warning.*</c> keys.</summary>
public static class PayrollWarnings
{
    /// <summary>A daily worker with no days: paid zero, and said so rather than left to be noticed.</summary>
    public const string DailyWorkerWithoutAttendance = "daily_worker_without_attendance";
    /// <summary>A monthly employee nobody entered attendance for: paid in full, and flagged.</summary>
    public const string MonthlyWithoutAttendance = "monthly_without_attendance";
    /// <summary>Hours were declared against a rule the settings do not define; they were not paid.</summary>
    public const string OvertimeRuleUnknown = "overtime_rule_unknown";
    /// <summary>The contribution base hit its floor or its ceiling.</summary>
    public const string SocialBaseClamped = "social_base_clamped";
    /// <summary>Deductions and advances took the pay below zero; it was held at zero.</summary>
    public const string NetClampedToZero = "net_clamped_to_zero";
    /// <summary>A full month's <b>gross</b> came out under the minimum wage. <c>Value</c> is that gross, not the take-home.</summary>
    public const string BelowMinimumWage = "below_minimum_wage";
    /// <summary>An advance is still open because the net could not cover it.</summary>
    public const string AdvanceNotFullyRecovered = "advance_not_fully_recovered";
    /// <summary>Computed against figures nobody has confirmed yet. Run-level.</summary>
    public const string SettingsUnconfirmed = "settings_unconfirmed";
}

/// <summary>
/// Why a run cannot be computed at all. The one refusal payroll has (docs/07 §5.3): a scale
/// nobody has entered must not be read as a scale that withholds nothing.
/// </summary>
public sealed record RunRefusal(string Code, IReadOnlyList<string> MissingFields, string FixableBy);

/// <summary>Thrown when <see cref="PayrollEngine.Compute"/> is called anyway. Carries the refusal for the 409.</summary>
public sealed class PayrollRefusedException(RunRefusal refusal)
    : InvalidOperationException($"Payroll cannot be computed: {refusal.Code}.")
{
    public RunRefusal Refusal { get; } = refusal;
}

/// <summary>How much of which advance this run took back.</summary>
public sealed record AdvanceRecovery(Guid AdvanceId, decimal Amount);

/// <summary>One person's payslip, computed. Every figure is already rounded to the currency.</summary>
public sealed record EmployeePayroll(
    Guid EmployeeId,
    PayBasis Basis,
    decimal BaseRate,
    decimal DaysPresent,
    decimal PaidLeaveDays,
    decimal UnpaidAbsenceDays,
    AttendanceSource AttendanceSource,
    IReadOnlyList<PayrollElement> Elements,
    decimal Gross,
    decimal SocialBase,
    decimal SocialEmployee,
    decimal SocialEmployer,
    decimal TaxBase,
    decimal IncomeTax,
    decimal EmployerLevies,
    decimal OtherDeductions,
    decimal NetBeforeAdvances,
    decimal AdvanceRecovered,
    IReadOnlyList<AdvanceRecovery> AdvanceRecoveries,
    decimal Net,
    IReadOnlyList<PayrollWarning> Warnings,
    decimal PaidByVoucher = 0m);

/// <summary>The run's header figures, which are the sum of the rounded lines and nothing else.</summary>
public sealed record PayrollTotals(
    int HeadCount,
    decimal Gross,
    decimal SocialBase,
    decimal SocialEmployee,
    decimal SocialEmployer,
    decimal TaxBase,
    decimal IncomeTax,
    decimal EmployerLevies,
    decimal OtherDeductions,
    decimal AdvancesRecovered,
    decimal Net,
    decimal PaidByVoucher = 0m);

/// <summary>
/// A whole run, computed.
/// </summary>
/// <param name="IsProvisional">
/// The figures it was computed against had not been confirmed. Frozen here and stored on the run:
/// a payslip printed under placeholders goes on saying so after the admin confirms them.
/// </param>
public sealed record PayrollComputation(
    IReadOnlyList<EmployeePayroll> Lines,
    PayrollTotals Totals,
    bool IsProvisional,
    IReadOnlyList<PayrollWarning> Warnings);

/// <summary>
/// The arithmetic of a payslip: gross, contributions, taxable, tax, net — in that order, because
/// each step is the input of the next and a step taken out of turn changes what everybody is paid.
///
/// <para>
/// Pure, like <c>RentalFifo</c> and <c>RentalPricing</c>: no entities, no database, no clock. It is
/// handed settings, a regional profile and a list of people, and it hands back lines. That is what
/// makes a payslip checkable against a hand calculation without a server running.
/// </para>
/// <para>
/// <b>It decides nothing that belongs to the accountant.</b> Whether an allowance enters the
/// contribution base, whether overtime enters the tax base, whether the employee's contribution
/// comes off the taxable amount — each of those is a flag it reads. The engine's only opinions are
/// the ones that are true by arithmetic: a base wage is in both bases, an employer's contribution
/// is a cost and never a deduction, and nobody is paid a negative amount.
/// </para>
/// <para>
/// <b>Every element is rounded as it is produced</b>, through <see cref="RegionalSettings.RoundMoney"/>,
/// and every total is built by adding rounded elements. So the payslip's own lines add up to its
/// net, and the lines add up to the run — to the centime, in whatever number of decimals the
/// currency has. Nothing here rounds to two places.
/// </para>
/// </summary>
public static class PayrollEngine
{
    /// <summary>The key of the base-wage element; the absence line that offsets it shares its kind.</summary>
    public const string BaseKey = "base";
    public const string AbsenceKey = "absence";

    /// <summary>
    /// Whether this profile can compute at all, and what is missing if it cannot.
    ///
    /// <para>
    /// The only refusal in payroll. <see cref="IncomeTaxMode.NotConfigured"/> means nobody has
    /// entered a scale, and the engine must not answer that with zero tax: an unnoticed zero is a
    /// company that has not withheld anything all year. Deliberate zero withholding exists — it is
    /// <see cref="IncomeTaxMode.None"/>, which somebody had to choose.
    /// </para>
    /// <para>
    /// Unconfirmed figures are <b>not</b> a refusal. They mark the run provisional, in words.
    /// </para>
    /// </summary>
    public static RunRefusal? Refusal(PayrollSettings settings, bool calculateStatutoryDeductions = true)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // A run that excludes IRG has no scale to validate. The older net-pay-only option keeps
        // its historical meaning for legacy runs that still opt into statutory calculation.
        if (!calculateStatutoryDeductions || settings.NetPayOnly) return null;

        return settings.IncomeTaxMode == IncomeTaxMode.NotConfigured
            ? new RunRefusal(
                "income_tax_not_configured",
                [nameof(PayrollSettings.IncomeTaxMode), nameof(PayrollSettings.IncomeTaxBrackets)],
                "payroll.edit_statutory")
            : null;
    }

    /// <summary>Computes every line and the run totals from them.</summary>
    /// <exception cref="PayrollRefusedException">The profile cannot compute; see <see cref="Refusal"/>.</exception>
    public static PayrollComputation Compute(
        PayrollSettings settings, RegionalSettings regional, IEnumerable<EmployeeInput> inputs,
        bool calculateStatutoryDeductions = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(regional);
        ArgumentNullException.ThrowIfNull(inputs);

        if (Refusal(settings, calculateStatutoryDeductions) is { } refusal) throw new PayrollRefusedException(refusal);

        var lines = inputs.Select(input => ComputeOne(settings, regional, input, calculateStatutoryDeductions)).ToList();

        var totals = new PayrollTotals(
            HeadCount: lines.Count,
            Gross: lines.Sum(l => l.Gross),
            SocialBase: lines.Sum(l => l.SocialBase),
            SocialEmployee: lines.Sum(l => l.SocialEmployee),
            SocialEmployer: lines.Sum(l => l.SocialEmployer),
            TaxBase: lines.Sum(l => l.TaxBase),
            IncomeTax: lines.Sum(l => l.IncomeTax),
            EmployerLevies: lines.Sum(l => l.EmployerLevies),
            OtherDeductions: lines.Sum(l => l.OtherDeductions),
            AdvancesRecovered: lines.Sum(l => l.AdvanceRecovered),
            Net: lines.Sum(l => l.Net),
            PaidByVoucher: lines.Sum(l => l.PaidByVoucher));

        // Said once for the run rather than repeated on every line, and frozen onto the run so a
        // payslip printed today keeps saying it after the figures are confirmed tomorrow. What
        // is unconfirmed is the statutory half — contributions and the tax scale — which a
        // net-pay payslip does not use, so it is not provisional for them.
        var legacyNetPayOnly = settings.NetPayOnly && calculateStatutoryDeductions;
        var usesStatutoryDeductions = calculateStatutoryDeductions && !legacyNetPayOnly;
        var usesEmployerLevy = !legacyNetPayOnly && (settings.EmployerLevies ?? []).Any(l => l.IsActive);
        var provisional = settings.NeedsReview && (usesStatutoryDeductions || usesEmployerLevy);
        List<PayrollWarning> runWarnings = provisional
            ? [new PayrollWarning(PayrollWarnings.SettingsUnconfirmed)]
            : [];

        return new PayrollComputation(lines, totals, provisional, runWarnings);
    }

    /// <summary>One person's payslip, in the twelve steps of docs/07 §5.6.</summary>
    public static EmployeePayroll ComputeOne(
        PayrollSettings settings, RegionalSettings regional, EmployeeInput input,
        bool calculateStatutoryDeductions = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(regional);
        ArgumentNullException.ThrowIfNull(input);

        if (Refusal(settings, calculateStatutoryDeductions) is { } refusal) throw new PayrollRefusedException(refusal);

        // The retired net-pay-only setting remains meaningful only for historical runs that kept
        // statutory calculation. New and draft runs preserve allowances and employer levies.
        var netPayOnly = settings.NetPayOnly && calculateStatutoryDeductions;

        var attendance = input.Attendance ?? AttendanceSummary.Nothing;
        var warnings = new List<PayrollWarning>();
        var elements = new List<PayrollElement>();
        var order = 0;

        // ---------- 1. the base wage ----------

        var baseWage = Base(settings, regional, input, attendance, elements, ref order, warnings);

        // ---------- 2. allowances ----------

        // None on a net-pay payslip (owner, 2026-10-10), whatever the definitions or the
        // employee's own figures say; they are kept, just not paid.
        if (!netPayOnly)
            Allowances(settings, regional, input, baseWage, elements, ref order);

        // With the company's answer-40 setting the absent day is a day of the gross — the base and
        // the allowances — so it is taken once both are known. Overtime is pay for hours actually
        // worked and is never part of what an absence forfeits.
        if (settings.AbsenceOffGross && input.Basis != PayBasis.Daily)
            AbsenceOffGross(settings, regional, attendance, elements, ref order);

        // ---------- 3. overtime ----------

        Overtime(settings, regional, input, attendance, elements, ref order, warnings);

        // ---------- 4. deductions and adjustments ----------

        var otherDeductions = Deductions(settings, regional, baseWage, elements, ref order);
        Adjustments(regional, input, elements, ref order);
        var paidByVoucher = VoucherBonuses(regional, input, elements, ref order);

        // Gross is everything that is pay: the base net of absence, allowances, overtime and any
        // adjustment. Deductions are not in it — they are withheld from it, and the ledger posts
        // them to their own account (docs/07 §5.9), so keeping them out is what makes
        // Gross − contributions − tax − deductions − advances = Net hold on both sides.
        var gross = elements.Where(e => e.Kind != PayrollElementKind.Deduction).Sum(e => e.Amount);

        // CNAS and IRG are deliberately absent from new and draft salary slips. Their bases and
        // withholdings therefore stay zero, which also keeps the print free of either row. Other
        // payroll calculations keep their normal inputs: employer levies still use the same
        // capped social base internally, without exposing it as a CNAS figure.
        decimal socialBase = 0m, socialEmployee = 0m, socialEmployer = 0m;
        decimal taxBase = 0m, incomeTax = 0m, employerLevies = 0m;
        var rawSocialBase = elements.Where(e => e.InSocialBase).Sum(e => e.Amount);
        var levyBase = Clamp(settings, rawSocialBase);
        if (calculateStatutoryDeductions && !netPayOnly)
        {
            // ---------- 5. the contribution base ----------

            socialBase = levyBase;
            if (socialBase != rawSocialBase)
                warnings.Add(new PayrollWarning(PayrollWarnings.SocialBaseClamped, Value: socialBase));

            // ---------- 6. contributions ----------

            socialEmployee = regional.RoundMoney(socialBase * settings.SocialEmployeeRate / 100m);
            // A cost the company carries, never taken off anyone's pay; it is not in the net below.
            socialEmployer = regional.RoundMoney(socialBase * settings.SocialEmployerRate / 100m);

            // ---------- 7. the tax base ----------

            taxBase = elements.Where(e => e.InTaxBase).Sum(e => e.Amount);
            if (settings.EmployeeContributionReducesTaxBase) taxBase -= socialEmployee;
            taxBase = regional.RoundMoney(taxBase);

            // ---------- 8. income tax ----------

            incomeTax = settings.IncomeTaxMode switch
            {
                IncomeTaxMode.Scale => IncomeTax.Progressive(regional, taxBase, settings.IncomeTaxBrackets),
                // Somebody said, in a setting, that nothing is withheld. Not an empty scale read as zero.
                IncomeTaxMode.None => 0m,
                _ => throw new PayrollRefusedException(Refusal(settings)!)
            };

        }

        // Employer levies are not CNAS or IRG: keep their existing calculation for every run
        // except the legacy net-pay-only document shape.
        if (!netPayOnly)
            employerLevies = (settings.EmployerLevies ?? [])
                .Where(l => l.IsActive)
                .Sum(l => regional.RoundMoney(levyBase * l.Rate / 100m));

        // ---------- 10. net before advances ----------

        var netBeforeAdvances = gross - socialEmployee - incomeTax - otherDeductions;

        // ---------- 11. advances ----------

        // A bonus already handed over by voucher comes back first and whole: it is in the gross above, so the
        // month has it in hand, and it is the person's own money rather than a loan. Advances take what is left.
        var (advanceRecovered, recoveries) = RecoverAdvances(regional, input, netBeforeAdvances - paidByVoucher, warnings);

        // ---------- 12. net ----------

        var net = netBeforeAdvances - paidByVoucher - advanceRecovered;
        if (net < 0m)
        {
            // Never a negative payslip. What is left uncollected stays on the advance, or is the
            // next run's problem, but nobody is handed a bill on their own wage slip.
            warnings.Add(new PayrollWarning(PayrollWarnings.NetClampedToZero, Value: net));
            net = 0m;
        }

        // A full month whose *wage* comes out under the floor. Gross, not net: the floor is a
        // statement about what the month paid, and net is that figure after the man's own
        // contributions, standing deductions and — the case that made this wrong — the advance he
        // borrowed and is repaying. Comparing net raised the warning every month on well-paid
        // staff recovering an advance, and the noise is what makes a warning stop being read.
        // Monthly basis only and no unpaid absence, because a half-worked month is legitimately
        // below the floor; 0 turns the check off entirely.
        if (settings.MinimumWage > 0m
            && input.Basis == PayBasis.Monthly
            && attendance.UnpaidAbsenceDays <= 0m
            && gross < settings.MinimumWage)
            warnings.Add(new PayrollWarning(PayrollWarnings.BelowMinimumWage, Value: gross));

        return new EmployeePayroll(
            input.EmployeeId, input.Basis, input.BaseRate,
            attendance.DaysPresent, attendance.PaidLeaveDays, attendance.UnpaidAbsenceDays, attendance.Source,
            elements,
            gross, socialBase, socialEmployee, socialEmployer,
            taxBase, incomeTax, employerLevies,
            otherDeductions, netBeforeAdvances,
            advanceRecovered, recoveries,
            net, warnings, paidByVoucher);
    }

    // ---------- step 1 ----------

    /// <summary>
    /// The base wage, and the figure percentage allowances are a percentage of.
    ///
    /// <para>
    /// The two bases are genuinely different questions. A monthly wage is a figure absence is
    /// taken out of; a daily wage is a figure days are multiplied into. Writing one as a special
    /// case of the other is where a month with no attendance starts paying a monthly employee
    /// zero.
    /// </para>
    /// </summary>
    private static decimal Base(
        PayrollSettings settings, RegionalSettings regional, EmployeeInput input,
        AttendanceSummary attendance, List<PayrollElement> elements, ref int order,
        List<PayrollWarning> warnings)
    {
        if (input.Basis == PayBasis.Daily)
        {
            var days = attendance.PaidDays;
            var amount = regional.RoundMoney(input.BaseRate * days);
            elements.Add(new PayrollElement(
                BaseKey, PayrollElementKind.Base, amount, days, input.BaseRate, true, true, order++));

            // Paid zero, flagged, never refused: the run still says what it would pay and the
            // foreman's missing sheet is visible instead of being an empty page.
            if (days <= 0m)
                warnings.Add(new PayrollWarning(PayrollWarnings.DailyWorkerWithoutAttendance));

            return amount;
        }

        var monthly = regional.RoundMoney(input.BaseRate);
        elements.Add(new PayrollElement(
            BaseKey, PayrollElementKind.Base, monthly, null, input.BaseRate, true, true, order++));

        var unpaid = Math.Max(0m, attendance.UnpaidAbsenceDays);
        var dayRate = input.BaseRate / settings.WorkingDaysPerMonth;
        // Off the gross instead: taken after the allowances (AbsenceOffGross), not here.
        var absence = unpaid > 0m && !settings.AbsenceOffGross
            ? regional.RoundMoney(input.BaseRate * unpaid / settings.WorkingDaysPerMonth)
            : 0m;
        if (absence > 0m)
            elements.Add(new PayrollElement(
                AbsenceKey, PayrollElementKind.Absence, -absence, unpaid, dayRate, true, true, order++));

        // Nobody entered anything for a salaried person. They are paid in full — that is what a
        // salary is — and the run says the month was never checked.
        if (attendance.Source == AttendanceSource.None)
            warnings.Add(new PayrollWarning(PayrollWarnings.MonthlyWithoutAttendance));

        return monthly - absence;
    }

    /// <summary>
    /// The absence line when the company takes it off the gross: the base and the allowances on the
    /// sheet so far, divided by the working days in a month, times the unpaid days.
    /// </summary>
    private static void AbsenceOffGross(
        PayrollSettings settings, RegionalSettings regional, AttendanceSummary attendance,
        List<PayrollElement> elements, ref int order)
    {
        var unpaid = Math.Max(0m, attendance.UnpaidAbsenceDays);
        if (unpaid <= 0m) return;
        var gross = elements
            .Where(e => e.Kind is PayrollElementKind.Base or PayrollElementKind.Allowance)
            .Sum(e => e.Amount);
        var dayRate = gross / settings.WorkingDaysPerMonth;
        var absence = regional.RoundMoney(gross * unpaid / settings.WorkingDaysPerMonth);
        if (absence > 0m)
            elements.Add(new PayrollElement(
                AbsenceKey, PayrollElementKind.Absence, -absence, unpaid, dayRate, true, true, order++));
    }

    // ---------- step 2 ----------

    private static void Allowances(
        PayrollSettings settings, RegionalSettings regional, EmployeeInput input,
        decimal baseWage, List<PayrollElement> elements, ref int order)
    {
        var overrides = (input.Allowances ?? [])
            .GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var def in (settings.Allowances ?? []).Where(a => a.IsActive).OrderBy(a => a.SortOrder))
        {
            var value = def.Value;
            if (overrides.TryGetValue(def.Key, out var own))
            {
                // Switched off for this person pays nothing at all — which is a different
                // statement from an override of zero, and both have to survive the round trip.
                if (!own.IsEnabled) continue;
                value = own.Value;
            }

            var amount = def.Calculation == AllowanceCalculation.PercentOfBase
                ? regional.RoundMoney(baseWage * value / 100m)
                : regional.RoundMoney(value);
            if (amount == 0m) continue;

            elements.Add(new PayrollElement(
                def.Key, PayrollElementKind.Allowance, amount,
                // No quantity: an allowance is a flat figure or a percentage, never a count. The
                // percentage is kept as the rate, so the payslip can print "5 % of 30 000".
                null,
                def.Calculation == AllowanceCalculation.PercentOfBase ? value : null,
                // The definition answers both questions; the engine only carries the answers onto
                // the line, where they are frozen against a later change to the definition.
                def.SubjectToCnas, def.SubjectToIrg, order++));
        }
    }

    // ---------- step 3 ----------

    private static void Overtime(
        PayrollSettings settings, RegionalSettings regional, EmployeeInput input,
        AttendanceSummary attendance, List<PayrollElement> elements, ref int order,
        List<PayrollWarning> warnings)
    {
        var hoursByRule = attendance.OvertimeHoursByRule ?? new Dictionary<string, decimal>();
        if (hoursByRule.Count == 0) return;

        // A daily rate exists on both bases; it is what the hourly rate is cut from.
        var dailyRate = input.Basis == PayBasis.Daily
            ? input.BaseRate
            : input.BaseRate / settings.WorkingDaysPerMonth;
        var hourlyRate = dailyRate / settings.HoursPerDay;

        var rules = (settings.OvertimeRules ?? []).Where(r => r.IsActive).ToList();

        foreach (var rule in rules)
        {
            if (!hoursByRule.TryGetValue(rule.Key, out var hours) || hours <= 0m) continue;

            // A band counted in days (a holiday or rest day worked, owner 2026-10-10) is paid off
            // the daily rate; one counted in hours off the hourly rate cut from it.
            var rate = (rule.Unit == OvertimeUnit.Days ? dailyRate : hourlyRate) * rule.Multiplier;
            var amount = regional.RoundMoney(hours * rate);
            if (amount == 0m) continue;

            elements.Add(new PayrollElement(
                rule.Key, PayrollElementKind.Overtime, amount, hours, rate,
                rule.SubjectToCnas, rule.SubjectToIrg, order++));
        }

        // Hours entered against a band that has since been renamed or retired. Reported, not
        // quietly dropped and not paid at a guessed rate: somebody worked them.
        var known = rules.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, hours) in hoursByRule.Where(h => h.Value > 0m && !known.Contains(h.Key)))
            warnings.Add(new PayrollWarning(PayrollWarnings.OvertimeRuleUnknown, key, hours));
    }

    // ---------- step 4 ----------

    /// <summary>Standing deductions, as negative elements. Returns their total as a positive figure.</summary>
    private static decimal Deductions(
        PayrollSettings settings, RegionalSettings regional, decimal baseWage,
        List<PayrollElement> elements, ref int order)
    {
        decimal total = 0m;

        foreach (var def in (settings.Deductions ?? []).Where(d => d.IsActive).OrderBy(d => d.SortOrder))
        {
            var amount = def.Calculation == DeductionCalculation.PercentOfBase
                ? regional.RoundMoney(baseWage * def.Value / 100m)
                : regional.RoundMoney(def.Value);
            if (amount <= 0m) continue;

            elements.Add(new PayrollElement(
                def.Key, PayrollElementKind.Deduction, -amount, null,
                def.Calculation == DeductionCalculation.PercentOfBase ? def.Value : null,
                // Signed negative and flagged: a deduction that leaves the contribution base is
                // simply a negative element inside it, so step 5 needs no second rule.
                def.ReducesSocialBase, def.ReducesTaxBase, order++));
            total += amount;
        }

        return total;
    }

    private static void Adjustments(
        RegionalSettings regional, EmployeeInput input, List<PayrollElement> elements, ref int order)
    {
        foreach (var adjustment in input.Adjustments ?? [])
        {
            var amount = regional.RoundMoney(adjustment.Amount);
            if (amount == 0m) continue;

            elements.Add(new PayrollElement(
                adjustment.Key, PayrollElementKind.Adjustment, amount, null, null,
                adjustment.InSocialBase, adjustment.InTaxBase, order++));
        }
    }

    /// <summary>
    /// The bonuses already paid by voucher: one earning for their total (in the bases the payroll settings say), and the
    /// same total returned to be taken back off net pay.
    /// </summary>
    private static decimal VoucherBonuses(
        RegionalSettings regional, EmployeeInput input, List<PayrollElement> elements, ref int order)
    {
        var bonuses = (input.VoucherBonuses ?? []).Where(b => b.Amount != 0m).ToList();
        if (bonuses.Count == 0) return 0m;
        var total = regional.RoundMoney(bonuses.Sum(b => b.Amount));
        if (total == 0m) return 0m;
        elements.Add(new PayrollElement(
            VoucherBonusKey, PayrollElementKind.Adjustment, total, null, null,
            bonuses[0].InSocialBase, bonuses[0].InTaxBase, order++));
        return total;
    }

    /// <summary>The element key of a bonus paid by payment voucher; the payslip prints <c>hr.element.voucherBonus</c>.</summary>
    public const string VoucherBonusKey = "voucherBonus";

    // ---------- step 5 ----------

    private static decimal Clamp(PayrollSettings settings, decimal socialBase)
    {
        // Only when set. A null bound is the absence of a bound, not a bound of zero — clamping a
        // base up to a floor nobody configured would invent contributions out of nothing.
        if (settings.SocialBaseFloor is decimal floor && socialBase < floor) return floor;
        if (settings.SocialBaseCeiling is decimal ceiling && socialBase > ceiling) return ceiling;
        return socialBase;
    }

    // ---------- step 11 ----------

    private static (decimal Total, IReadOnlyList<AdvanceRecovery> Recoveries) RecoverAdvances(
        RegionalSettings regional, EmployeeInput input, decimal netBeforeAdvances,
        List<PayrollWarning> warnings)
    {
        var open = (input.OpenAdvances ?? []).Where(a => a.Outstanding > 0m).ToList();
        if (open.Count == 0) return (0m, []);

        // Never more than the pay in hand, and never a negative recovery out of a negative net.
        var available = Math.Max(0m, netBeforeAdvances);
        var recoveries = new List<AdvanceRecovery>();
        decimal total = 0m;

        foreach (var advance in open)
        {
            if (available <= 0m) break;
            var take = regional.RoundMoney(Math.Min(advance.Outstanding, available));
            if (take <= 0m) continue;
            recoveries.Add(new AdvanceRecovery(advance.AdvanceId, take));
            available -= take;
            total += take;
        }

        // What the month could not cover stays owed; the advance stays Open rather than being
        // written off by arithmetic nobody decided.
        var outstanding = open.Sum(a => a.Outstanding) - total;
        if (outstanding > 0m)
            warnings.Add(new PayrollWarning(PayrollWarnings.AdvanceNotFullyRecovered, Value: outstanding));

        return (total, recoveries);
    }
}
