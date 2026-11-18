using ParadiERP.Application.Regional;

namespace ParadiERP.Tests.Regional;

/// <summary>
/// How figures are written is a company setting (owner, 2026-11-09: "5,300,256.25 … for all places in the system"):
/// 1,234.56 unless the operator chose another; the web app formats, the server keeps and checks the choice.
/// </summary>
public class NumberFormatTests
{
    [Fact]
    public void Every_preset_writes_figures_as_the_system_does_and_a_profile_saved_before_the_setting_gets_it_too()
    {
        foreach (var code in RegionalPresets.Countries)
        {
            var preset = RegionalPresets.Create(code)!;
            RegionalSettingsValidator.NormalizeAndValidate(preset);
            Assert.Equal("1,234.56", preset.NumberFormat);
        }

        var old = RegionalPresets.Create(RegionalPresets.Countries.First())!;
        old.NumberFormat = "";
        RegionalSettingsValidator.NormalizeAndValidate(old);
        Assert.Equal(NumberFormats.Default, old.NumberFormat);
    }

    [Fact]
    public void Only_the_known_formats_are_kept()
    {
        foreach (var format in NumberFormats.All)
        {
            var s = RegionalPresets.Create(RegionalPresets.Countries.First())!;
            s.NumberFormat = format;
            RegionalSettingsValidator.NormalizeAndValidate(s);
            Assert.Equal(format, s.NumberFormat);
        }

        var bad = RegionalPresets.Create(RegionalPresets.Countries.First())!;
        bad.NumberFormat = "1'234.56";
        Assert.ThrowsAny<Exception>(() => RegionalSettingsValidator.NormalizeAndValidate(bad));
    }
}
