using ParadiERP.Application.Payroll;
using ParadiERP.Application.Regional;

namespace ParadiERP.Tests.Payroll;

/// <summary>
/// The arithmetic of a payslip.
///
/// <para>
/// Every figure below is synthetic and says so: contributions at 10 % and 20 %, a month of 20
/// working days of 10 hours, tax brackets at 1,000 and 2,000. None of it is anybody's law. A
/// test is the easiest place for a plausible real figure to be written from memory and then
/// quoted back as authority, so no real scale, rate or minimum wage appears here — those are
/// settings an accountant types.
/// </para>
/// </summary>
public class PayrollEngineTests
{
    private static readonly RegionalSettings Money = new() { CurrencyDecimals = 2 };

    /// <summary>A round, obviously invented profile. Each test changes exactly one thing about it.</summary>
    private static PayrollSettings Settings(Action<PayrollSettings>? tweak = null)
    {
        var s = new PayrollSettings
        {
            CountryCode = "ZZ",
            SocialEmployeeRate = 10m,
            SocialEmployerRate = 20m,
            // Said, not absent: these tests are not about the refusal, which has its own.
            IncomeTaxMode = IncomeTaxMode.None,
            IncomeTaxBrackets = [],
            MinimumWage = 0m,
            WorkingDaysPerMonth = 20m,
            HoursPerDay = 10m,
            Confirmed = true,
            ConfirmedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        tweak?.Invoke(s);
        PayrollSettingsValidator.NormalizeAndValidate(s);
        return s;
    }

    private static AttendanceSummary Declared(
        decimal present, decimal paidLeave = 0m, decimal unpaid = 0m,
        Dictionary<string, decimal>? overtime = null) =>
        new(present, paidLeave, unpaid, overtime ?? new Dictionary<string, decimal>(), AttendanceSource.Declared);

    private static EmployeeInput Monthly(decimal baseRate, AttendanceSummary? attendance = null) =>
        new(Guid.NewGuid(), PayBasis.Monthly, baseRate, attendance ?? Declared(20m));

    private static EmployeePayroll One(PayrollSettings settings, EmployeeInput input) =>
        PayrollEngine.ComputeOne(settings, Money, input);

    private static decimal Element(EmployeePayroll line, string key) =>
        line.Elements.Where(e => e.Key == key).Sum(e => e.Amount);

    private static bool Warned(EmployeePayroll line, string code) =>
        line.Warnings.Any(w => w.Code == code);

    // ---------- step 1: the base ----------

    [Fact]
    public void A_monthly_employee_is_paid_the_base_less_unpaid_absence_at_the_working_day_rate()
    {
        var line = One(Settings(), Monthly(20_000m, Declared(present: 18m, unpaid: 2m)));

        // 20,000 over a 20-day month is 1,000 a day; two days away is 2,000 off.
        Assert.Equal(20_000m, Element(line, PayrollEngine.BaseKey));
        Assert.Equal(-2_000m, Element(line, PayrollEngine.AbsenceKey));
        Assert.Equal(18_000m, line.Gross);
        Assert.Equal(16_200m, line.Net);
    }

    [Fact]
    public void With_the_company_setting_an_absent_day_is_a_day_of_the_gross()
    {
        // Answer 40: a day is the gross ÷ 30, taken off what he would receive.
        var settings = Settings(s =>
        {
            s.WorkingDaysPerMonth = 30m;
            s.AbsenceOffGross = true;
            s.Allowances = [new() { Key = "panier", Value = 3_000m }];
        });
        var line = One(settings, Monthly(27_000m, Declared(present: 28m, unpaid: 2m)));

        // (27,000 + 3,000) / 30 = 1,000 a day; two days is 2,000 off the gross, not 1,800 off the base.
        Assert.Equal(27_000m, Element(line, PayrollEngine.BaseKey));
        Assert.Equal(3_000m, Element(line, "panier"));
        Assert.Equal(-2_000m, Element(line, PayrollEngine.AbsenceKey));
        Assert.Equal(28_000m, line.Gross);

        // Off, the same month takes the absence off the base alone, as before the setting.
        var before = One(Settings(s =>
        {
            s.WorkingDaysPerMonth = 30m;
            s.Allowances = [new() { Key = "panier", Value = 3_000m }];
        }), Monthly(27_000m, Declared(present: 28m, unpaid: 2m)));
        Assert.Equal(-1_800m, Element(before, PayrollEngine.AbsenceKey));
    }

    [Fact]
    public void A_daily_worker_is_paid_days_present_times_the_daily_rate_and_nothing_else()
    {
        var input = new EmployeeInput(
            Guid.NewGuid(), PayBasis.Daily, 1_000m, Declared(present: 18m, paidLeave: 2m, unpaid: 3m));

        var line = One(Settings(), input);

        // Days present plus paid leave, at the daily rate. Absence is not a deduction from a
        // daily wage — there was never a whole month to take it out of, and taking it twice is
        // exactly what writing one basis as a special case of the other produces.
        Assert.Equal(20_000m, line.Gross);
        Assert.DoesNotContain(line.Elements, e => e.Kind == PayrollElementKind.Absence);
    }

    [Fact]
    public void A_daily_worker_with_no_attendance_is_paid_zero_and_flagged_not_refused()
    {
        var input = new EmployeeInput(Guid.NewGuid(), PayBasis.Daily, 1_000m, Declared(present: 0m));

        var line = One(Settings(), input);

        // The run still says what it would pay. A missing sheet becomes a visible warning on a
        // line, instead of a person quietly absent from the payroll.
        Assert.Equal(0m, line.Gross);
        Assert.Equal(0m, line.Net);
        Assert.True(Warned(line, PayrollWarnings.DailyWorkerWithoutAttendance));
    }

    [Fact]
    public void A_monthly_employee_nobody_entered_a_month_for_is_paid_in_full_and_flagged()
    {
        var line = One(Settings(), Monthly(20_000m, AttendanceSummary.Nothing));

        // That is what a salary is. But nobody checked the month, and the run says so.
        Assert.Equal(20_000m, line.Gross);
        Assert.Equal(AttendanceSource.None, line.AttendanceSource);
        Assert.True(Warned(line, PayrollWarnings.MonthlyWithoutAttendance));
    }

    // ---------- step 2: allowances ----------

    [Fact]
    public void Allowances_enter_the_social_and_tax_bases_only_when_flagged_and_a_percentage_follows_the_base()
    {
        var settings = Settings(s => s.Allowances =
        [
            new() { Key = "transport", Value = 1_000m, SubjectToCnas = false, SubjectToIrg = false, SortOrder = 1 },
            new()
            {
                Key = "prime", Calculation = AllowanceCalculation.PercentOfBase, Value = 10m,
                SubjectToCnas = true, SubjectToIrg = true, SortOrder = 2
            }
        ]);

        var full = One(settings, Monthly(20_000m));

        Assert.Equal(1_000m, Element(full, "transport"));
        Assert.Equal(2_000m, Element(full, "prime"));
        Assert.Equal(23_000m, full.Gross);

        // The transport allowance is paid and is in neither base; the flag on the definition is
        // the only thing that decides it, and the line carries the answer frozen onto it.
        Assert.Equal(22_000m, full.SocialBase);
        Assert.False(full.Elements.Single(e => e.Key == "transport").InSocialBase);

        // A percentage is a percentage of the base after absence, not of the contract figure.
        var reduced = One(settings, Monthly(20_000m, Declared(present: 15m, unpaid: 5m)));
        Assert.Equal(1_500m, Element(reduced, "prime"));
    }

    [Fact]
    public void An_employees_own_figure_wins_and_switching_an_allowance_off_pays_nothing()
    {
        var settings = Settings(s => s.Allowances = [new() { Key = "prime", Value = 1_000m }]);

        var raised = One(settings, Monthly(20_000m) with
        {
            Allowances = [new EmployeeAllowanceOverride("prime", 2_500m)]
        });
        Assert.Equal(2_500m, Element(raised, "prime"));

        var switchedOff = One(settings, Monthly(20_000m) with
        {
            Allowances = [new EmployeeAllowanceOverride("prime", 1_000m, IsEnabled: false)]
        });
        // Not the same statement as an override of zero, and both have to survive the round trip.
        Assert.DoesNotContain(switchedOff.Elements, e => e.Key == "prime");
    }

    // ---------- step 3: overtime ----------

    [Fact]
    public void Overtime_multiplies_the_hourly_rate_by_the_band()
    {
        var settings = Settings(s => s.OvertimeRules =
            [new() { Key = "hs", Multiplier = 2m, SubjectToCnas = true, SubjectToIrg = true }]);
        var hours = new Dictionary<string, decimal> { ["hs"] = 10m };

        // A 20,000 month over 20 days of 10 hours is 100 an hour; ten hours at double is 2,000.
        var monthly = One(settings, Monthly(20_000m, Declared(20m, overtime: hours)));
        Assert.Equal(2_000m, Element(monthly, "hs"));

        // A daily worker reaches the same hourly rate from the other direction.
        var daily = One(settings, new EmployeeInput(
            Guid.NewGuid(), PayBasis.Daily, 1_000m, Declared(20m, overtime: hours)));
        Assert.Equal(2_000m, Element(daily, "hs"));
    }

    [Fact]
    public void Hours_declared_against_a_band_that_no_longer_exists_are_reported_and_not_paid_at_a_guess()
    {
        var settings = Settings(s => s.OvertimeRules = [new() { Key = "hs", Multiplier = 2m }]);
        var line = One(settings, Monthly(20_000m, Declared(20m,
            overtime: new Dictionary<string, decimal> { ["hs-retired"] = 5m })));

        Assert.Equal(20_000m, line.Gross);
        var warning = line.Warnings.Single(w => w.Code == PayrollWarnings.OvertimeRuleUnknown);
        // Somebody worked them. Paying them at a rate nobody configured would be a guess; dropping
        // them silently would lose the hours altogether.
        Assert.Equal("hs-retired", warning.Key);
        Assert.Equal(5m, warning.Value);
    }

    // ---------- step 4: deductions and adjustments ----------

    [Fact]
    public void A_deduction_reduces_the_bases_only_when_flagged()
    {
        var reducing = Settings(s => s.Deductions =
            [new() { Key = "retenue", Value = 2_000m, ReducesSocialBase = true, ReducesTaxBase = false }]);
        var line = One(reducing, Monthly(20_000m));

        Assert.Equal(2_000m, line.OtherDeductions);
        Assert.Equal(-2_000m, Element(line, "retenue"));
        // The deduction is not pay, so it is outside the gross and posted on its own.
        Assert.Equal(20_000m, line.Gross);
        Assert.Equal(18_000m, line.SocialBase);
        // Out of the contribution base, still inside the tax base: two separate answers.
        Assert.Equal(18_200m, line.TaxBase);
        Assert.Equal(16_200m, line.Net);

        var plain = Settings(s => s.Deductions = [new() { Key = "retenue", Value = 2_000m }]);
        Assert.Equal(20_000m, One(plain, Monthly(20_000m)).SocialBase);
    }

    [Fact]
    public void An_adjustment_is_pay_and_enters_the_bases_only_when_its_own_flags_say_so()
    {
        var inBase = One(Settings(), Monthly(20_000m) with
        {
            Adjustments = [new PayrollAdjustmentInput("bonus", 1_000m, InSocialBase: true, InTaxBase: true)]
        });
        Assert.Equal(21_000m, inBase.Gross);
        Assert.Equal(21_000m, inBase.SocialBase);

        var outside = One(Settings(), Monthly(20_000m) with
        {
            Adjustments = [new PayrollAdjustmentInput("bonus", 1_000m)]
        });
        Assert.Equal(21_000m, outside.Gross);
        Assert.Equal(20_000m, outside.SocialBase);
    }

    // ---------- steps 5 and 6: the base and the contributions ----------

    [Fact]
    public void The_social_base_respects_a_floor_and_ceiling_only_when_they_are_set()
    {
        // Unset is not a bound of zero: a small wage is not raised to anything.
        var none = One(Settings(), Monthly(100m));
        Assert.Equal(100m, none.SocialBase);
        Assert.False(Warned(none, PayrollWarnings.SocialBaseClamped));

        var capped = One(Settings(s => s.SocialBaseCeiling = 15_000m), Monthly(20_000m));
        Assert.Equal(15_000m, capped.SocialBase);
        Assert.Equal(1_500m, capped.SocialEmployee);
        Assert.True(Warned(capped, PayrollWarnings.SocialBaseClamped));

        var floored = One(Settings(s => s.SocialBaseFloor = 25_000m), Monthly(20_000m));
        Assert.Equal(25_000m, floored.SocialBase);
    }

    [Fact]
    public void The_employee_share_is_withheld_and_the_employer_share_is_a_cost_not_a_deduction()
    {
        var line = One(Settings(), Monthly(20_000m));

        Assert.Equal(2_000m, line.SocialEmployee);
        Assert.Equal(4_000m, line.SocialEmployer);
        // The employer's share never appears in what the person is handed. It is the company's
        // cost, and a net short by 4,000 is the mistake this test exists to catch.
        Assert.Equal(18_000m, line.Net);
    }

    // ---------- steps 7 and 8: the tax base and the tax ----------

    [Fact]
    public void The_employee_contribution_leaves_the_tax_base_only_when_the_flag_says_so()
    {
        // 10 % to 1,000 and 50 % above. Invented, and permanently so.
        void Scale(PayrollSettings s)
        {
            s.IncomeTaxMode = IncomeTaxMode.Scale;
            s.IncomeTaxBrackets = [new() { UpTo = 1_000m, Rate = 10m }, new() { Rate = 50m }];
        }

        var deducting = One(Settings(s => { Scale(s); s.EmployeeContributionReducesTaxBase = true; }), Monthly(20_000m));
        Assert.Equal(18_000m, deducting.TaxBase);
        Assert.Equal(8_600m, deducting.IncomeTax);

        var not = One(Settings(s => { Scale(s); s.EmployeeContributionReducesTaxBase = false; }), Monthly(20_000m));
        Assert.Equal(20_000m, not.TaxBase);
        Assert.Equal(9_600m, not.IncomeTax);

        // The engine asserts nothing about which is right. It reads the flag, and the flag is the
        // accountant's — which is the whole of the correction this test is named for.
    }

    [Fact]
    public void Income_tax_is_progressive_over_the_configured_brackets()
    {
        var settings = Settings(s =>
        {
            s.IncomeTaxMode = IncomeTaxMode.Scale;
            s.IncomeTaxBrackets =
                [new() { UpTo = 1_000m, Rate = 10m }, new() { UpTo = 2_000m, Rate = 20m }, new() { Rate = 50m }];
            s.EmployeeContributionReducesTaxBase = false;
        });

        // 1,000 at 10 % plus 1,000 at 20 %. Each slice at its own rate, never the whole at the top.
        Assert.Equal(300m, One(settings, Monthly(2_000m)).IncomeTax);
    }

    [Fact]
    public void The_run_refuses_to_compute_while_the_tax_scale_is_not_configured_and_says_what_is_missing()
    {
        var settings = Settings(s => { s.IncomeTaxMode = IncomeTaxMode.NotConfigured; s.IncomeTaxBrackets = []; });

        var refusal = PayrollEngine.Refusal(settings);
        Assert.NotNull(refusal);
        Assert.Equal("income_tax_not_configured", refusal!.Code);
        Assert.Contains(nameof(PayrollSettings.IncomeTaxBrackets), refusal.MissingFields);
        Assert.Equal("payroll.edit_statutory", refusal.FixableBy);

        // And it does not quietly answer zero. An unnoticed zero is a company that has withheld
        // nothing all year and does not know it.
        var thrown = Assert.Throws<PayrollRefusedException>(
            () => PayrollEngine.Compute(settings, Money, [Monthly(20_000m)]));
        Assert.Equal(refusal.Code, thrown.Refusal.Code);
        Assert.Throws<PayrollRefusedException>(() => PayrollEngine.ComputeOne(settings, Money, Monthly(20_000m)));
    }

    [Fact]
    public void An_explicit_no_withholding_mode_withholds_nothing_and_is_not_a_refusal()
    {
        var settings = Settings(s => s.IncomeTaxMode = IncomeTaxMode.None);

        // Somebody chose this. That is the difference between it and the mode above, and the
        // difference is the entire point of the tri-state.
        Assert.Null(PayrollEngine.Refusal(settings));
        Assert.Equal(0m, One(settings, Monthly(20_000m)).IncomeTax);
    }

    // ---------- step 9: employer levies ----------

    [Fact]
    public void Employer_levies_are_a_cost_and_never_touch_the_employees_net()
    {
        var withLevy = One(Settings(s => s.EmployerLevies = [new() { Key = "levy", Rate = 5m }]), Monthly(20_000m));
        var without = One(Settings(), Monthly(20_000m));

        Assert.Equal(1_000m, withLevy.EmployerLevies);
        Assert.Equal(0m, without.EmployerLevies);
        Assert.Equal(without.Net, withLevy.Net);
    }

    // ---------- steps 11 and 12: advances and the net ----------

    [Fact]
    public void An_advance_is_recovered_up_to_the_net_and_the_remainder_stays_open()
    {
        var advance = Guid.NewGuid();

        var partial = One(Settings(), Monthly(20_000m) with
        {
            OpenAdvances = [new OpenAdvance(advance, 25_000m)]
        });
        Assert.Equal(18_000m, partial.AdvanceRecovered);
        Assert.Equal(advance, partial.AdvanceRecoveries.Single().AdvanceId);
        Assert.Equal(0m, partial.Net);
        // What the month could not cover stays owed rather than being written off by arithmetic
        // nobody decided.
        Assert.Equal(7_000m, partial.Warnings.Single(w => w.Code == PayrollWarnings.AdvanceNotFullyRecovered).Value);

        var covered = One(Settings(), Monthly(20_000m) with
        {
            OpenAdvances = [new OpenAdvance(advance, 5_000m)]
        });
        Assert.Equal(5_000m, covered.AdvanceRecovered);
        Assert.Equal(13_000m, covered.Net);
        Assert.False(Warned(covered, PayrollWarnings.AdvanceNotFullyRecovered));
    }

    [Fact]
    public void Net_pay_never_goes_below_zero_and_says_so()
    {
        var settings = Settings(s => s.Deductions = [new() { Key = "retenue", Value = 12_000m }]);
        var line = One(settings, Monthly(10_000m));

        // 10,000 less 1,000 of contribution less a 12,000 deduction is below nothing. Nobody is
        // handed a bill on their own wage slip; the shortfall is a warning the run carries.
        Assert.Equal(-3_000m, line.NetBeforeAdvances);
        Assert.Equal(0m, line.Net);
        Assert.Equal(-3_000m, line.Warnings.Single(w => w.Code == PayrollWarnings.NetClampedToZero).Value);
    }

    [Fact]
    public void A_wage_below_the_minimum_is_a_warning_not_a_refusal_and_zero_turns_the_check_off()
    {
        var floor = Settings(s => s.MinimumWage = 20_000m);

        Assert.True(Warned(One(floor, Monthly(10_000m)), PayrollWarnings.BelowMinimumWage));

        // A half-worked month is legitimately below the floor; flagging it would train everyone
        // to ignore the warning.
        Assert.False(Warned(One(floor, Monthly(10_000m, Declared(15m, unpaid: 5m))), PayrollWarnings.BelowMinimumWage));

        // Zero turns the check off entirely.
        Assert.False(Warned(One(Settings(), Monthly(10_000m)), PayrollWarnings.BelowMinimumWage));
    }

    [Fact]
    public void The_floor_is_about_the_wage_not_about_what_the_man_borrowed_against_it()
    {
        var advance = Guid.NewGuid();
        var floor = Settings(s => s.MinimumWage = 20_000m);

        // 60,000 a month, with a 45,000 advance being recovered: the take-home is well under the
        // floor and the wage is nowhere near it. Comparing net raised this every month on every
        // well-paid employee repaying an advance, which is how a warning stops being read.
        var line = One(floor, Monthly(60_000m) with { OpenAdvances = [new OpenAdvance(advance, 45_000m)] });
        Assert.True(line.Net < floor.MinimumWage);
        Assert.False(Warned(line, PayrollWarnings.BelowMinimumWage));

        // And the figure the warning carries is the gross it judged, not the take-home.
        var under = One(floor, Monthly(12_000m));
        Assert.Equal(12_000m, under.Warnings.Single(w => w.Code == PayrollWarnings.BelowMinimumWage).Value);
    }

    // ---------- rounding and totals ----------

    [Fact]
    public void Elements_round_to_the_currency_decimals_and_sum_to_the_totals()
    {
        // A month of three days makes one day's absence a recurring third — the case where a
        // figure rounded twice, or rounded to "two places" by habit, stops adding up.
        var settings = Settings(s => s.WorkingDaysPerMonth = 3m);
        var people = new[] { Monthly(1_000m, Declared(2m, unpaid: 1m)), Monthly(1_000m, Declared(2m, unpaid: 1m)) };

        var run = PayrollEngine.Compute(settings, Money, people);

        var line = run.Lines[0];
        Assert.Equal(-333.33m, Element(line, PayrollEngine.AbsenceKey));
        Assert.Equal(666.67m, line.Gross);
        Assert.Equal(66.67m, line.SocialEmployee);
        Assert.Equal(600.00m, line.Net);

        // The payslip's own lines add up to its gross, and the payslips add up to the run — to the
        // centime, with no reconciling difference anywhere.
        Assert.Equal(line.Gross, line.Elements.Where(e => e.Kind != PayrollElementKind.Deduction).Sum(e => e.Amount));
        Assert.Equal(1_333.34m, run.Totals.Gross);
        Assert.Equal(1_200.00m, run.Totals.Net);
        Assert.Equal(2, run.Totals.HeadCount);
    }

    [Fact]
    public void The_currencys_own_decimals_decide_the_rounding_and_never_a_hardcoded_two()
    {
        var settings = Settings(s => s.WorkingDaysPerMonth = 3m);
        var whole = new RegionalSettings { CurrencyDecimals = 0 };

        var line = PayrollEngine.ComputeOne(settings, whole, Monthly(1_000m, Declared(2m, unpaid: 1m)));

        Assert.Equal(-333m, Element(line, PayrollEngine.AbsenceKey));
        Assert.Equal(667m, line.Gross);
        Assert.Equal(600m, line.Net);
    }

    // ---------- the owner's placeholders ----------

    [Fact]
    public void An_unconfirmed_profile_computes_end_to_end_and_every_line_of_it_says_it_is_unconfirmed()
    {
        // The shipped preset, untouched: the owner asked for a module that runs on day one.
        var settings = PayrollPresets.Create("DZ")!;
        PayrollSettingsValidator.NormalizeAndValidate(settings);
        Assert.True(settings.NeedsReview);

        var run = PayrollEngine.Compute(settings, Money, [Monthly(50_000m, Declared(26m))]);

        // It pays. Placeholders that refuse to compute would just mean nobody ever sees a payslip.
        Assert.Equal(50_000m, run.Totals.Gross);
        Assert.True(run.Totals.Net > 0m);

        // And nothing pretends the figures were checked: the mark is frozen onto the run, so a
        // payslip printed today goes on saying so after the admin confirms them tomorrow.
        Assert.True(run.IsProvisional);
        Assert.Contains(run.Warnings, w => w.Code == PayrollWarnings.SettingsUnconfirmed);

        var confirmed = PayrollOwnership.ApplyOperatorEdit(
            settings, reviewed: true, by: "admin", atUtc: new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc));
        var after = PayrollEngine.Compute(confirmed, Money, [Monthly(50_000m, Declared(26m))]);
        Assert.False(after.IsProvisional);
        Assert.Empty(after.Warnings);
    }

    [Fact]
    public void The_element_kinds_and_attendance_sources_mirror_the_domain()
    {
        // Two copies of an enum that drift are a payslip whose lines are filed under the wrong
        // kind, which nothing else in the system would notice.
        Assert.Equal(
            Enum.GetNames<Domain.PayrollElementKind>().Zip(
                Enum.GetValues<Domain.PayrollElementKind>().Select(v => (int)v)).ToArray(),
            Enum.GetNames<PayrollElementKind>().Zip(
                Enum.GetValues<PayrollElementKind>().Select(v => (int)v)).ToArray());

        Assert.Equal(
            Enum.GetNames<Domain.AttendanceSource>().Zip(
                Enum.GetValues<Domain.AttendanceSource>().Select(v => (int)v)).ToArray(),
            Enum.GetNames<AttendanceSource>().Zip(
                Enum.GetValues<AttendanceSource>().Select(v => (int)v)).ToArray());
    }
}
