using System.Text.Json;
using System.Text.Json.Serialization;
using AlgeriaPos.Application.Payroll;

namespace AlgeriaPos.Tests.Payroll;

/// <summary>
/// Payroll computes what real people are paid, so the tests here are mostly about the ways a
/// setting can be wrong without anything appearing to fail.
/// </summary>
public class PayrollSettingsTests
{
    [Fact]
    public void The_algeria_preset_is_valid_and_its_contribution_rates_are_placeholders_too()
    {
        var settings = PayrollPresets.Create("DZ")!;
        PayrollSettingsValidator.NormalizeAndValidate(settings);

        // These used to be the real CNAS split, on the grounds that it has been stable for years.
        // That made them the one pair of figures on the settings screen that looked already
        // checked, sitting beside brackets that obviously were not — so they were the pair an
        // admin would confirm without asking anyone. Whole tens instead, like the rest.
        Assert.Equal(0m, settings.SocialEmployeeRate % 10m);
        Assert.Equal(0m, settings.SocialEmployerRate % 10m);
        Assert.True(settings.SocialEmployeeRate > 0m);
        Assert.True(settings.SocialEmployerRate > 0m);
    }

    [Fact]
    public void The_preset_ships_figures_that_run_and_says_none_of_them_are_confirmed()
    {
        var settings = PayrollPresets.Create("DZ")!;
        PayrollSettingsValidator.NormalizeAndValidate(settings);

        // The owner asked for a module that runs on day one, so the scale is present and the
        // engine has no reason to refuse.
        Assert.Equal(IncomeTaxMode.Scale, settings.IncomeTaxMode);
        Assert.NotEmpty(settings.IncomeTaxBrackets);
        Assert.Null(PayrollEngine.Refusal(settings));

        // And the whole of it announces itself as unchecked. This is the only thing standing
        // between a placeholder and a payslip that looks authoritative.
        Assert.False(settings.Confirmed);
        Assert.True(settings.NeedsReview);
        Assert.Null(settings.ConfirmedBy);
        Assert.Null(settings.ConfirmedAtUtc);
    }

    [Fact]
    public void The_presets_placeholders_are_round_numbers_and_never_a_plausible_scale()
    {
        var settings = PayrollPresets.Create("DZ")!;

        // A figure written from memory looks right and is quietly wrong every month. Round
        // thousands and whole-ten percentages cannot be mistaken for a scale someone looked up.
        Assert.All(settings.IncomeTaxBrackets, b =>
        {
            Assert.Equal(0m, b.Rate % 10m);
            if (b.UpTo is decimal ceiling) Assert.Equal(0m, ceiling % 10000m);
        });
        Assert.Equal(0m, settings.MinimumWage % 10000m);
        // 20 000 is what a search returns for Algeria's SNMG. A placeholder that happens to be
        // the real figure passes every "is it round?" guard and still teaches the admin that
        // somebody looked it up, which is the one thing this file must never do.
        Assert.NotEqual(20_000m, settings.MinimumWage);

        // The contribution rates go through the same guard. A rate written to the nearest
        // percent is a rate somebody read somewhere; a whole ten is a rate nobody did.
        Assert.Equal(0m, settings.SocialEmployeeRate % 10m);
        Assert.Equal(0m, settings.SocialEmployerRate % 10m);

        // No invented cost and no invented bound: absent is cheaper to correct than wrong.
        Assert.Empty(settings.EmployerLevies);
        Assert.Empty(settings.Deductions);
        Assert.Null(settings.SocialBaseFloor);
        Assert.Null(settings.SocialBaseCeiling);

        // Nothing stands in for what one business pays its own people, so allowances pay zero
        // until someone types a figure. Overtime is the exception: a multiplier of 1 is not an
        // unset figure, it is the claim that overtime carries no premium.
        Assert.All(settings.Allowances, a => Assert.Equal(0m, a.Value));
        Assert.All(settings.OvertimeRules, r => Assert.True(r.Multiplier > 1m));
    }

    [Fact]
    public void An_unknown_country_has_no_preset_rather_than_a_guessed_one()
    {
        Assert.Null(PayrollPresets.Create("MA"));
        Assert.Null(PayrollPresets.Create(""));
        Assert.Null(PayrollPresets.Create(null));
    }

    [Fact]
    public void A_tax_scale_must_be_ordered_and_open_at_the_top()
    {
        // A ceiling on the last step taxes nothing above it, and nothing complains.
        var closed = Scaled();
        closed.IncomeTaxBrackets = [new() { UpTo = 30000, Rate = 0 }, new() { UpTo = 120000, Rate = 20 }];
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(closed));

        var overlapping = Scaled();
        overlapping.IncomeTaxBrackets =
            [new() { UpTo = 40000, Rate = 0 }, new() { UpTo = 30000, Rate = 20 }, new() { Rate = 35 }];
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(overlapping));

        var good = Scaled();
        good.IncomeTaxBrackets =
            [new() { UpTo = 30000, Rate = 0 }, new() { UpTo = 120000, Rate = 20 }, new() { Rate = 35 }];
        PayrollSettingsValidator.NormalizeAndValidate(good);
    }

    [Fact]
    public void Contribution_flags_survive_validation_untouched()
    {
        var settings = PayrollPresets.Create("DZ")!;
        PayrollSettingsValidator.NormalizeAndValidate(settings);

        // Whether an allowance enters the CNAS or IRG base changes both net pay and the
        // declaration. Nothing may quietly normalise these to a default.
        var transport = settings.Allowances.Single(a => a.Key == "transport");
        Assert.False(transport.SubjectToCnas);
        Assert.False(transport.SubjectToIrg);

        var poste = settings.Allowances.Single(a => a.Key == "poste");
        Assert.True(poste.SubjectToCnas);
        Assert.True(poste.SubjectToIrg);
    }

    [Fact]
    public void Overtime_below_ordinary_time_is_rejected()
    {
        var settings = Base();
        settings.OvertimeRules = [new() { Key = "hs", Multiplier = 0.5m }];
        // Paying overtime worse than ordinary time is never intended; it is a typo nobody
        // notices until someone queries a payslip.
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(settings));

        settings.OvertimeRules = [new() { Key = "hs", Multiplier = 1.5m }];
        PayrollSettingsValidator.NormalizeAndValidate(settings);
    }

    [Fact]
    public void Duplicate_and_malformed_keys_are_rejected()
    {
        var duplicate = Base();
        duplicate.Allowances = [new() { Key = "panier" }, new() { Key = "PANIER" }];
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(duplicate));

        var malformed = Base();
        malformed.Allowances = [new() { Key = "prime panier!" }];
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(malformed));
    }

    [Fact]
    public void Rates_outside_their_range_are_rejected()
    {
        var settings = Base();
        settings.SocialEmployeeRate = 140m;
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(settings));

        settings = Base();
        settings.WorkingDaysPerMonth = 0m;
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(settings));

        settings = Base();
        settings.Allowances = [new() { Key = "prime", Calculation = AllowanceCalculation.PercentOfBase, Value = 150m }];
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(settings));
    }

    [Fact]
    public void A_scale_mode_with_no_brackets_is_rejected()
    {
        var settings = Base();
        settings.IncomeTaxMode = IncomeTaxMode.Scale;
        settings.IncomeTaxBrackets = [];

        // "Withhold per the scale" with no scale is the state the tri-state exists to abolish.
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(settings));
    }

    [Fact]
    public void Not_configured_and_none_modes_must_carry_no_brackets()
    {
        // A scale nobody applies is a scale nobody checks, and it is the one that gets switched
        // on years later by a mode change nobody read.
        foreach (var mode in new[] { IncomeTaxMode.NotConfigured, IncomeTaxMode.None })
        {
            var settings = Base();
            settings.IncomeTaxMode = mode;
            settings.IncomeTaxBrackets = [new() { UpTo = 1000m, Rate = 10m }, new() { Rate = 20m }];
            Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(settings));
        }
    }

    [Fact]
    public void A_contribution_base_floor_above_its_ceiling_is_rejected()
    {
        var settings = Base();
        settings.SocialBaseFloor = 2000m;
        settings.SocialBaseCeiling = 1000m;

        // Nothing would fail at runtime; the clamp would simply pick one of them and pay everyone
        // on it every month.
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(settings));
    }

    [Fact]
    public void A_confirmation_without_a_date_is_rejected_and_an_unconfirmed_profile_carries_no_name()
    {
        var dated = Base();
        dated.Confirmed = true;
        // "Our accountant checked it" is worth nothing unless the record says when.
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(dated));

        var stale = Base();
        stale.ConfirmedBy = "someone";
        stale.ConfirmedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        PayrollSettingsValidator.NormalizeAndValidate(stale);

        // A name left beside figures nobody stands behind any more would read as approval.
        Assert.True(stale.NeedsReview);
        Assert.Null(stale.ConfirmedBy);
        Assert.Null(stale.ConfirmedAtUtc);
    }

    [Fact]
    public void An_employer_levy_and_a_deduction_are_validated_like_every_other_key()
    {
        var levy = Base();
        levy.EmployerLevies = [new() { Key = "taxe", Rate = 120m }];
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(levy));

        var deduction = Base();
        deduction.Deductions = [new() { Key = "amende", Value = -10m }];
        // Held positive and signed by the engine: a negative deduction is a pay rise typed into
        // the wrong screen, and nothing downstream would question it.
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(deduction));

        var duplicate = Base();
        duplicate.Deductions = [new() { Key = "amende" }, new() { Key = "AMENDE" }];
        Assert.Throws<ArgumentException>(() => PayrollSettingsValidator.NormalizeAndValidate(duplicate));
    }

    /// <summary>A minimal valid set, so each test changes exactly one thing.</summary>
    private static PayrollSettings Base() => new()
    {
        CountryCode = "DZ",
        SocialEmployeeRate = 9m,
        SocialEmployerRate = 26m,
        IncomeTaxMode = IncomeTaxMode.NotConfigured,
        IncomeTaxBrackets = [],
        WorkingDaysPerMonth = 26m,
        HoursPerDay = 8m
    };

    /// <summary>The same, in the mode that may carry a scale, for the tests that are about one.</summary>
    private static PayrollSettings Scaled()
    {
        var s = Base();
        s.IncomeTaxMode = IncomeTaxMode.Scale;
        return s;
    }
}

/// <summary>
/// The line between what a company may change about its own payroll and what only the platform
/// operator may change. A business that finds its contributions inconvenient must not be able
/// to quietly lower them.
/// </summary>
public class PayrollOwnershipTests
{
    private static PayrollSettings Current()
    {
        var s = PayrollPresets.Create("DZ")!;
        s.IncomeTaxBrackets = [new() { UpTo = 30000, Rate = 0 }, new() { Rate = 20 }];
        s.MinimumWage = 20000m;
        return s;
    }

    [Fact]
    public void A_company_may_set_its_own_allowances_and_working_day()
    {
        var current = Current();
        var edit = Current();
        edit.Allowances = [new() { Key = "panier", Value = 350m, Calculation = AllowanceCalculation.FixedAmount }];
        edit.OvertimeRules = [new() { Key = "hs-jour", Multiplier = 1.5m }];
        edit.WorkingDaysPerMonth = 22m;
        edit.DefaultAttendanceMode = AttendanceMode.Recorded;

        var merged = PayrollOwnership.ApplyCompanyEdit(current, edit);

        Assert.Equal(350m, merged.Allowances.Single().Value);
        Assert.Equal(1.5m, merged.OvertimeRules.Single().Multiplier);
        Assert.Equal(22m, merged.WorkingDaysPerMonth);
        Assert.Equal(AttendanceMode.Recorded, merged.DefaultAttendanceMode);
    }

    [Fact]
    public void A_company_cannot_lower_its_own_contribution_rate()
    {
        var current = Current();
        var edit = Current();
        edit.SocialEmployerRate = 10m;

        var refused = Assert.Throws<UnauthorizedAccessException>(
            () => PayrollOwnership.ApplyCompanyEdit(current, edit));
        Assert.Contains(nameof(PayrollSettings.SocialEmployerRate), refused.Message);
    }

    [Fact]
    public void A_company_cannot_rewrite_the_tax_scale_or_the_wage_floor()
    {
        var current = Current();

        var scale = Current();
        scale.IncomeTaxBrackets = [new() { Rate = 0 }];
        Assert.Throws<UnauthorizedAccessException>(() => PayrollOwnership.ApplyCompanyEdit(current, scale));

        var floor = Current();
        floor.MinimumWage = 1m;
        Assert.Throws<UnauthorizedAccessException>(() => PayrollOwnership.ApplyCompanyEdit(current, floor));

        var country = Current();
        country.CountryCode = "FR";
        Assert.Throws<UnauthorizedAccessException>(() => PayrollOwnership.ApplyCompanyEdit(current, country));
    }

    [Fact]
    public void A_refused_edit_is_refused_and_not_quietly_dropped()
    {
        var current = Current();
        var edit = Current();
        edit.SocialEmployeeRate = 1m;
        edit.WorkingDaysPerMonth = 22m;

        // Keeping the good half and discarding the rest would leave the administrator
        // believing they had lowered a rate, and the first sign otherwise would be a payslip
        // disagreeing with the screen they saved.
        Assert.Throws<UnauthorizedAccessException>(() => PayrollOwnership.ApplyCompanyEdit(current, edit));
        // Read from the preset rather than written here: what this test is about is that a
        // refusal leaves both the caller's object and the preset untouched, not what the
        // placeholder rate happens to be this month.
        var preset = PayrollPresets.Create("DZ")!;
        Assert.Equal(preset.SocialEmployerRate, current.SocialEmployerRate);
        Assert.Equal(preset.SocialEmployeeRate, current.SocialEmployeeRate);
    }

    [Fact]
    public void The_operator_may_set_the_statutory_figures()
    {
        var edit = Current();
        edit.SocialEmployeeRate = 9.5m;
        edit.IncomeTaxBrackets = [new() { UpTo = 40000, Rate = 0 }, new() { Rate = 23 }];

        var saved = PayrollOwnership.ApplyOperatorEdit(edit, reviewed: true);

        Assert.Equal(9.5m, saved.SocialEmployeeRate);
        Assert.Equal(23m, saved.IncomeTaxBrackets[^1].Rate);
        Assert.False(saved.NeedsReview);
    }

    [Fact]
    public void Correcting_a_figure_is_not_the_same_as_checking_it_against_the_law()
    {
        var saved = PayrollOwnership.ApplyOperatorEdit(Current(), reviewed: false);

        // An operator fixing a typo has not thereby verified the scale, so the warning stays up.
        Assert.True(saved.NeedsReview);
    }

    [Fact]
    public void An_operator_edit_is_still_validated()
    {
        var broken = Current();
        broken.IncomeTaxBrackets = [new() { UpTo = 30000, Rate = 0 }, new() { UpTo = 90000, Rate = 20 }];

        // Owning a figure does not mean being allowed to leave it in a state that pays people
        // wrongly — a closed top bracket taxes nothing above it.
        Assert.Throws<ArgumentException>(() => PayrollOwnership.ApplyOperatorEdit(broken, reviewed: true));
    }

    [Fact]
    public void Every_setting_has_a_declared_owner()
    {
        // A field nobody owns is a field that ends up editable by whoever asks first.
        var declared = PayrollOwnership.Fields.Keys.ToHashSet();
        var actual = typeof(PayrollSettings).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Equal(actual, declared);
    }

    [Fact]
    public void A_company_edit_does_not_alias_the_stored_settings()
    {
        var current = Current();
        var edit = Current();
        edit.Allowances = [new() { Key = "panier", Value = 350m }];

        var merged = PayrollOwnership.ApplyCompanyEdit(current, edit);
        merged.SocialNames["fr"] = "changed";
        merged.IncomeTaxBrackets[0].Rate = 99m;

        // The result is a copy: mutating it must not reach back into what is stored.
        Assert.NotEqual("changed", current.SocialNames["fr"]);
        Assert.Equal(0m, current.IncomeTaxBrackets[0].Rate);
    }

    [Fact]
    public void Income_tax_mode_and_the_social_base_bounds_are_owned_by_the_operator()
    {
        var current = Current();

        foreach (var edit in new[]
                 {
                     Mutate(s => s.IncomeTaxMode = IncomeTaxMode.None),
                     Mutate(s => s.SocialBaseFloor = 1000m),
                     Mutate(s => s.SocialBaseCeiling = 9000m),
                     Mutate(s => s.EmployerLevies = [new() { Key = "levy", Rate = 2m }]),
                     Mutate(s => s.EmployeeContributionReducesTaxBase = false)
                 })
            Assert.Throws<UnauthorizedAccessException>(() => PayrollOwnership.ApplyCompanyEdit(current, edit));

        static PayrollSettings Mutate(Action<PayrollSettings> change)
        {
            var s = Current();
            change(s);
            return s;
        }
    }

    [Fact]
    public void A_company_cannot_tick_the_box_that_says_the_accountant_checked_the_figures()
    {
        var current = Current();
        var edit = Current();
        edit.Confirmed = true;

        // Confirming is what takes the provisional mark off every payslip. It is a statement
        // about the law, so it cannot ride along on a screen that edits allowances.
        var refused = Assert.Throws<UnauthorizedAccessException>(
            () => PayrollOwnership.ApplyCompanyEdit(current, edit));
        Assert.Contains(nameof(PayrollSettings.Confirmed), refused.Message);
    }

    [Fact]
    public void Confirming_the_figures_records_who_said_so_and_when_and_unconfirming_clears_it()
    {
        var at = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

        var confirmed = PayrollOwnership.ApplyOperatorEdit(Current(), reviewed: true, by: "admin", atUtc: at);
        Assert.False(confirmed.NeedsReview);
        Assert.Equal("admin", confirmed.ConfirmedBy);
        Assert.Equal(at, confirmed.ConfirmedAtUtc);

        var withdrawn = PayrollOwnership.ApplyOperatorEdit(confirmed, reviewed: false, by: null, atUtc: null);
        Assert.True(withdrawn.NeedsReview);
        Assert.Null(withdrawn.ConfirmedBy);
        Assert.Null(withdrawn.ConfirmedAtUtc);
    }

    [Fact]
    public void The_statutory_half_is_what_the_unconfirmed_mark_is_about()
    {
        // The split is no longer only a gate — it is the list a settings screen shows beside its
        // banner, so it has to keep naming every figure that moves with the law.
        Assert.Contains(nameof(PayrollSettings.IncomeTaxBrackets), PayrollOwnership.Statutory);
        Assert.Contains(nameof(PayrollSettings.SocialEmployeeRate), PayrollOwnership.Statutory);
        Assert.Contains(nameof(PayrollSettings.EmployeeContributionReducesTaxBase), PayrollOwnership.Statutory);
        Assert.DoesNotContain(nameof(PayrollSettings.Allowances), PayrollOwnership.Statutory);
        Assert.DoesNotContain(nameof(PayrollSettings.Deductions), PayrollOwnership.Statutory);
    }

    // ---------- what the screen actually receives ----------

    /// <summary>
    /// Built exactly as <c>ConfigureHttpJsonOptions</c> builds it in Program.cs: web defaults,
    /// no <c>JsonStringEnumConverter</c> registered globally. If the payload only survives a
    /// converter the API does not have, the test proves nothing.
    /// </summary>
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    [Fact]
    public void Every_payroll_enum_crosses_the_wire_by_name_and_comes_back()
    {
        var settings = PayrollPresets.Create("DZ")!;
        settings.Deductions = [new() { Key = "avance", Value = 100m, Treatment = DeductionTreatment.Other }];

        var json = JsonSerializer.Serialize(settings, Wire);

        // The screen's <option value="Declared"> has to match what arrives. An ordinal here means
        // every enum-valued control renders blank, and the first save after the admin touches one
        // is refused by the model binder before a handler ever runs.
        Assert.Contains("\"defaultAttendanceMode\":\"Declared\"", json);
        Assert.Contains("\"incomeTaxMode\":\"Scale\"", json);
        Assert.Contains("\"calculation\":\"FixedAmount\"", json);
        Assert.Contains("\"appliesTo\":\"Weekday\"", json);
        Assert.Contains("\"treatment\":\"Other\"", json);

        var back = JsonSerializer.Deserialize<PayrollSettings>(json, Wire)!;
        Assert.Equal(AttendanceMode.Declared, back.DefaultAttendanceMode);
        Assert.Equal(IncomeTaxMode.Scale, back.IncomeTaxMode);
        Assert.Equal(OvertimeBand.WeeklyRest, back.OvertimeRules[2].AppliesTo);
        Assert.Equal(DeductionTreatment.Other, back.Deductions[0].Treatment);
    }

    [Fact]
    public void Settings_stored_as_numbers_before_the_names_existed_still_read_back()
    {
        // PayrollSettingsStore keeps the settings as a JSON blob, so rows written before the
        // enums carried a converter hold ordinals. JsonStringEnumConverter reads both; this is
        // what says so, because the alternative is a company's saved profile failing to load.
        const string stored = """
            {"countryCode":"DZ","defaultAttendanceMode":1,"incomeTaxMode":2,
             "allowances":[{"key":"panier","calculation":1,"value":5}],
             "overtimeRules":[{"key":"hs","appliesTo":2,"multiplier":2}]}
            """;

        var back = JsonSerializer.Deserialize<PayrollSettings>(stored, Wire)!;
        Assert.Equal(AttendanceMode.Declared, back.DefaultAttendanceMode);
        Assert.Equal(IncomeTaxMode.None, back.IncomeTaxMode);
        Assert.Equal(AllowanceCalculation.PercentOfBase, back.Allowances[0].Calculation);
        Assert.Equal(OvertimeBand.WeeklyRest, back.OvertimeRules[0].AppliesTo);
    }

    [Fact]
    public void The_ownership_table_goes_out_keyed_the_way_the_wire_names_the_fields()
    {
        // System.Text.Json renames properties and leaves dictionary keys alone, so the PascalCase
        // table went out beside camelCase properties and the screen's lookups all missed — the
        // income-tax mode, the tax-base flag and both contribution bounds simply never rendered,
        // although the server carried and owned all four.
        var json = JsonSerializer.Serialize(PayrollOwnership.WireFields.ToDictionary(
            pair => pair.Key, pair => pair.Value.ToString()), Wire);

        Assert.Contains("\"incomeTaxMode\":\"Operator\"", json);
        Assert.Contains("\"socialBaseFloor\":\"Operator\"", json);
        Assert.Contains("\"socialBaseCeiling\":\"Operator\"", json);
        Assert.Contains("\"employeeContributionReducesTaxBase\":\"Operator\"", json);
        Assert.Contains("\"allowances\":\"Company\"", json);

        // Same table, same size: the rename is a rename, not a filter.
        Assert.Equal(PayrollOwnership.Fields.Count, PayrollOwnership.WireFields.Count);
        Assert.All(PayrollOwnership.WireFields.Keys, key => Assert.True(char.IsLower(key[0])));
    }
}
