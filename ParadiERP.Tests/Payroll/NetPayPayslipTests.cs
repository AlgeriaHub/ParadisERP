using ParadiERP.Application.Payroll;
using ParadiERP.Application.Regional;

namespace ParadiERP.Tests.Payroll;

/// <summary>
/// The owner's payslip (2026-10-10): net pay only — the base, plus overtime, plus holidays and rest
/// days worked, less absences, deductions and advances. No allowance, no contribution, no income
/// tax. And a holiday or rest day worked is counted in days, at a multiple of the daily rate.
///
/// <para>
/// The figures are invented, as in <see cref="PayrollEngineTests"/>: a 30-day month so the daily
/// rate is the owner's "base ÷ 30", 8-hour days, round wages.
/// </para>
/// </summary>
public class NetPayPayslipTests
{
    private static readonly RegionalSettings Money = new() { CurrencyDecimals = 2 };

    private static PayrollSettings Settings(Action<PayrollSettings>? tweak = null)
    {
        var s = new PayrollSettings
        {
            CountryCode = "ZZ",
            SocialEmployeeRate = 10m,
            SocialEmployerRate = 20m,
            IncomeTaxMode = IncomeTaxMode.Scale,
            IncomeTaxBrackets = [new() { UpTo = 10_000m, Rate = 0m }, new() { Rate = 10m }],
            MinimumWage = 0m,
            WorkingDaysPerMonth = 30m,
            HoursPerDay = 8m,
            Allowances = [new() { Key = "panier", Value = 5_000m, SubjectToCnas = true, SubjectToIrg = true }],
            OvertimeRules =
            [
                new() { Key = "hs", Multiplier = 2m },
                new() { Key = "repos", AppliesTo = OvertimeBand.WeeklyRest, Multiplier = 2m, Unit = OvertimeUnit.Days },
                new() { Key = "ferie", AppliesTo = OvertimeBand.PublicHoliday, Multiplier = 2m, Unit = OvertimeUnit.Days }
            ],
            Deductions = [new() { Key = "retenue", Value = 1_500m }],
            Confirmed = true,
            ConfirmedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        tweak?.Invoke(s);
        PayrollSettingsValidator.NormalizeAndValidate(s);
        return s;
    }

    private static EmployeeInput Monthly(
        decimal baseRate, decimal present = 30m, decimal unpaid = 0m,
        Dictionary<string, decimal>? overtime = null, decimal advance = 0m) =>
        new(Guid.NewGuid(), PayBasis.Monthly, baseRate,
            new AttendanceSummary(present, 0m, unpaid, overtime ?? new Dictionary<string, decimal>(), AttendanceSource.Declared),
            OpenAdvances: advance > 0m ? [new OpenAdvance(Guid.NewGuid(), advance)] : null);

    private static decimal Element(EmployeePayroll line, string key) =>
        line.Elements.Where(e => e.Key == key).Sum(e => e.Amount);

    [Fact]
    public void A_rest_day_or_a_holiday_worked_is_paid_in_days_at_the_multiple_of_the_daily_rate()
    {
        var line = PayrollEngine.ComputeOne(Settings(), Money, Monthly(30_000m,
            overtime: new() { ["repos"] = 2m, ["ferie"] = 1m }));

        // 30,000 ÷ 30 = 1,000 a day; two rest days at double is 4,000, one holiday 2,000.
        Assert.Equal(4_000m, Element(line, "repos"));
        Assert.Equal(2_000m, Element(line, "ferie"));
        var repos = line.Elements.Single(e => e.Key == "repos");
        Assert.Equal(2m, repos.Qty);
        Assert.Equal(2_000m, repos.Rate);
    }

    [Fact]
    public void The_day_multiple_is_a_setting()
    {
        var settings = Settings(s => s.OvertimeRules.Single(r => r.Key == "ferie").Multiplier = 3m);

        var line = PayrollEngine.ComputeOne(settings, Money, Monthly(30_000m, overtime: new() { ["ferie"] = 1m }));

        Assert.Equal(3_000m, Element(line, "ferie"));
    }

    [Fact]
    public void A_band_counted_in_hours_still_cuts_the_hourly_rate_from_the_daily_one()
    {
        var line = PayrollEngine.ComputeOne(Settings(), Money, Monthly(30_000m, overtime: new() { ["hs"] = 10m }));

        // 1,000 a day over 8 hours is 125 an hour; ten hours at double is 2,500.
        Assert.Equal(2_500m, Element(line, "hs"));
    }

    [Fact]
    public void Overtime_is_paid_by_the_kind_of_work_each_kind_at_its_own_rate_in_the_same_month()
    {
        // Owner, 2026-10-10: the nail workshop at the normal rate, loading goods at double. Kinds
        // and rates are the admin's list in the settings, not code.
        var settings = Settings(s =>
        {
            s.NetPayOnly = true;
            s.OvertimeRules.AddRange(
            [
                new() { Key = "atelier-clous", Labels = new() { ["ar"] = "ورشة المسمار", ["fr"] = "Atelier clous", ["en"] = "Nail workshop" }, Multiplier = 1m },
                new() { Key = "chargement", Labels = new() { ["ar"] = "تحميل السلع", ["fr"] = "Chargement", ["en"] = "Loading goods" }, Multiplier = 2m }
            ]);
        });

        var line = PayrollEngine.ComputeOne(settings, Money, Monthly(30_000m,
            overtime: new() { ["atelier-clous"] = 8m, ["chargement"] = 4m }));

        // 125 an hour: 8 h at ×1 is 1,000, 4 h at ×2 is 1,000 — each on its own line with its hours.
        var workshop = line.Elements.Single(e => e.Key == "atelier-clous");
        Assert.Equal((8m, 125m, 1_000m), (workshop.Qty!.Value, workshop.Rate!.Value, workshop.Amount));
        var loading = line.Elements.Single(e => e.Key == "chargement");
        Assert.Equal((4m, 250m, 1_000m), (loading.Qty!.Value, loading.Rate!.Value, loading.Amount));
        Assert.Equal(32_000m, line.Gross);
    }

    [Fact]
    public void A_daily_worker_s_day_band_multiplies_the_daily_wage()
    {
        var input = new EmployeeInput(Guid.NewGuid(), PayBasis.Daily, 1_200m,
            new AttendanceSummary(20m, 0m, 0m, new Dictionary<string, decimal> { ["repos"] = 1m }, AttendanceSource.Declared));

        var line = PayrollEngine.ComputeOne(Settings(), Money, input);

        Assert.Equal(2_400m, Element(line, "repos"));
    }

    [Fact]
    public void A_net_pay_payslip_pays_no_allowance_and_computes_no_contribution_tax_or_levy()
    {
        var settings = Settings(s =>
        {
            s.NetPayOnly = true;
            s.EmployerLevies = [new() { Key = "levy", Rate = 5m }];
        });

        var line = PayrollEngine.ComputeOne(settings, Money, Monthly(30_000m) with
        {
            Allowances = [new EmployeeAllowanceOverride("panier", 7_000m)]
        });

        Assert.DoesNotContain(line.Elements, e => e.Kind == PayrollElementKind.Allowance);
        Assert.Equal(30_000m, line.Gross);
        Assert.Equal(0m, line.SocialBase);
        Assert.Equal(0m, line.SocialEmployee);
        Assert.Equal(0m, line.SocialEmployer);
        Assert.Equal(0m, line.TaxBase);
        Assert.Equal(0m, line.IncomeTax);
        Assert.Equal(0m, line.EmployerLevies);
        Assert.Equal(28_500m, line.Net);
    }

    [Fact]
    public void A_worker_with_everything_on_the_month_is_paid_exactly_the_owner_s_formula()
    {
        var settings = Settings(s => s.NetPayOnly = true);

        var line = PayrollEngine.ComputeOne(settings, Money, Monthly(30_000m, present: 28m, unpaid: 2m,
            overtime: new() { ["hs"] = 10m, ["repos"] = 1m, ["ferie"] = 1m }, advance: 3_000m));

        // base 30,000 − absence 2 × 1,000 + overtime 10 h × 250 + rest day 2,000 + holiday 2,000
        Assert.Equal(30_000m, Element(line, PayrollEngine.BaseKey));
        Assert.Equal(-2_000m, Element(line, PayrollEngine.AbsenceKey));
        Assert.Equal(2_500m, Element(line, "hs"));
        Assert.Equal(2_000m, Element(line, "repos"));
        Assert.Equal(2_000m, Element(line, "ferie"));
        Assert.Equal(34_500m, line.Gross);
        // − the deduction 1,500 − the advance 3,000
        Assert.Equal(1_500m, line.OtherDeductions);
        Assert.Equal(3_000m, line.AdvanceRecovered);
        Assert.Equal(30_000m, line.Net);
        // The lines add up to the net: nothing is withheld that the payslip does not show.
        Assert.Equal(line.Net, line.Elements.Sum(e => e.Amount) - line.AdvanceRecovered);
    }

    [Fact]
    public void A_net_pay_run_needs_no_tax_scale_and_is_not_provisional_for_statutory_figures_it_does_not_use()
    {
        var settings = Settings(s =>
        {
            s.NetPayOnly = true;
            s.IncomeTaxMode = IncomeTaxMode.NotConfigured;
            s.IncomeTaxBrackets = [];
            s.Confirmed = false;
        });

        Assert.Null(PayrollEngine.Refusal(settings));
        var run = PayrollEngine.Compute(settings, Money, [Monthly(30_000m)]);

        Assert.False(run.IsProvisional);
        Assert.Empty(run.Warnings);
        Assert.Equal(28_500m, run.Totals.Net);
    }

    [Fact]
    public void Off_by_default_the_same_month_computes_allowances_contributions_and_tax_as_before()
    {
        var line = PayrollEngine.ComputeOne(Settings(), Money, Monthly(30_000m));

        Assert.Equal(5_000m, Element(line, "panier"));
        Assert.Equal(35_000m, line.SocialBase);
        Assert.Equal(3_500m, line.SocialEmployee);
        // (35,000 − 3,500) − 10,000 taxed at 10 %.
        Assert.Equal(2_150m, line.IncomeTax);
    }

    [Fact]
    public void A_statutory_exempt_payslip_keeps_allowances_deductions_and_other_levies()
    {
        var settings = Settings(s =>
        {
            // An old company setting must not erase allowances or levies from a new exempt run.
            s.NetPayOnly = true;
            s.EmployerLevies = [new() { Key = "levy", Rate = 5m }];
            s.IncomeTaxMode = IncomeTaxMode.NotConfigured;
            s.IncomeTaxBrackets = [];
        });

        var line = PayrollEngine.ComputeOne(settings, Money, Monthly(30_000m) with
        {
            Allowances = [new EmployeeAllowanceOverride("panier", 7_000m)]
        }, calculateStatutoryDeductions: false);

        Assert.Equal(7_000m, Element(line, "panier"));
        Assert.Equal(37_000m, line.Gross);
        Assert.Equal(1_500m, line.OtherDeductions);
        Assert.Equal(0m, line.SocialBase);
        Assert.Equal(0m, line.SocialEmployee);
        Assert.Equal(0m, line.SocialEmployer);
        Assert.Equal(0m, line.TaxBase);
        Assert.Equal(0m, line.IncomeTax);
        Assert.Equal(1_850m, line.EmployerLevies);
        Assert.Equal(35_500m, line.Net);
    }

    [Fact]
    public void A_statutory_exempt_run_still_marks_an_unconfirmed_employer_levy_as_provisional()
    {
        var settings = Settings(s =>
        {
            s.NetPayOnly = true;
            s.EmployerLevies = [new() { Key = "levy", Rate = 5m }];
            s.IncomeTaxMode = IncomeTaxMode.NotConfigured;
            s.IncomeTaxBrackets = [];
            s.Confirmed = false;
        });

        var run = PayrollEngine.Compute(settings, Money, [Monthly(30_000m)], calculateStatutoryDeductions: false);

        Assert.True(run.IsProvisional);
        Assert.Equal(1_750m, run.Totals.EmployerLevies);
    }

    [Fact]
    public void The_company_edit_keeps_the_payslip_choice_and_the_day_unit()
    {
        var current = Settings();
        var incoming = Settings(s =>
        {
            s.NetPayOnly = true;
            s.OvertimeRules.Single(r => r.Key == "hs").Unit = OvertimeUnit.Days;
        });

        var merged = PayrollOwnership.ApplyCompanyEdit(current, incoming);

        Assert.True(merged.NetPayOnly);
        Assert.Equal(OvertimeUnit.Days, merged.OvertimeRules.Single(r => r.Key == "hs").Unit);
        Assert.Equal(OvertimeUnit.Days, merged.OvertimeRules.Single(r => r.Key == "repos").Unit);
    }

    [Fact]
    public void The_preset_counts_rest_days_and_holidays_in_days_at_double()
    {
        var preset = PayrollPresets.Create("DZ")!;

        foreach (var band in new[] { OvertimeBand.WeeklyRest, OvertimeBand.PublicHoliday })
        {
            var rule = preset.OvertimeRules.Single(r => r.AppliesTo == band);
            Assert.Equal(OvertimeUnit.Days, rule.Unit);
            Assert.Equal(2m, rule.Multiplier);
        }
        Assert.All(preset.OvertimeRules.Where(r => r.AppliesTo is OvertimeBand.Weekday or OvertimeBand.Night),
            r => Assert.Equal(OvertimeUnit.Hours, r.Unit));
        Assert.False(preset.NetPayOnly);
    }
}
