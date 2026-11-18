using ParadisERP.Application.Regional;

namespace ParadisERP.Tests.Regional;

/// <summary>
/// The words an amount in words is built from (docs/07 §4.5) are country data on the profile,
/// never a constant in the converter. A language with no entry prints figures only.
/// </summary>
public class CurrencyUnitNamesTests
{
    [Fact]
    public void Every_preset_names_the_currency_in_each_language_it_enables()
    {
        foreach (var code in RegionalPresets.Countries)
        {
            var preset = RegionalPresets.Create(code)!;
            RegionalSettingsValidator.NormalizeAndValidate(preset);
            foreach (var lang in preset.Languages)
            {
                Assert.True(preset.CurrencyUnitNames.TryGetValue(lang, out var names), $"{code} has no unit names in {lang}");
                Assert.False(string.IsNullOrWhiteSpace(names!.Major), $"{code}/{lang} has no major unit");
            }
        }

        // The Algeria preset is what the client's DEFAULT_REGIONAL mirrors; a change here is a
        // change there.
        var dz = RegionalPresets.Create("DZ")!;
        Assert.Equal(("dinar", "centime"), (dz.CurrencyUnitNames["fr"].Major, dz.CurrencyUnitNames["fr"].Minor));
        Assert.Equal(("دينار", "سنتيم"), (dz.CurrencyUnitNames["ar"].Major, dz.CurrencyUnitNames["ar"].Minor));
    }

    [Fact]
    public void The_validator_trims_lowercases_and_drops_an_entry_with_no_major_word()
    {
        var s = RegionalPresets.Create("DZ")!;
        s.CurrencyUnitNames = new Dictionary<string, CurrencyUnitNames>
        {
            ["FR "] = new() { Major = " dinar ", Minor = " centime " },
            ["ar"] = new() { Major = "", Minor = "سنتيم" },
            ["en"] = new() { Major = "dinar", Minor = "" },
            ["not a code"] = new() { Major = "x", Minor = "y" },
        };
        RegionalSettingsValidator.NormalizeAndValidate(s);

        Assert.Equal(new[] { "en", "fr" }, s.CurrencyUnitNames.Keys.Order());
        Assert.Equal("dinar", s.CurrencyUnitNames["fr"].Major);
        Assert.Equal("centime", s.CurrencyUnitNames["fr"].Minor);
        // A currency spoken without a subunit keeps its major word and an empty minor one.
        Assert.Equal("", s.CurrencyUnitNames["en"].Minor);
    }

    [Fact]
    public void A_unit_name_over_sixty_characters_fails_the_profile_save()
    {
        var s = RegionalPresets.Create("DZ")!;
        s.CurrencyUnitNames["fr"] = new CurrencyUnitNames { Major = new string('d', 61), Minor = "c" };
        var error = Assert.Throws<ArgumentException>(() => RegionalSettingsValidator.NormalizeAndValidate(s));
        Assert.Contains("unit names", error.Message);
    }
}
