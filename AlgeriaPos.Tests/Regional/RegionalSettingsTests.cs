using AlgeriaPos.Application.Regional;
using AlgeriaPos.Infrastructure.Platform;
using AlgeriaPos.Infrastructure.Tenancy;

namespace AlgeriaPos.Tests.Regional;

public class RegionalSettingsTests
{
    [Fact]
    public void Every_preset_is_valid()
    {
        foreach (var code in RegionalPresets.Countries)
        {
            var preset = RegionalPresets.Create(code)!;
            RegionalSettingsValidator.NormalizeAndValidate(preset);
            Assert.Equal(code, preset.CountryCode);
        }
    }

    [Fact]
    public void Ui_font_keeps_known_keys_and_falls_back_to_the_default_face()
    {
        var settings = RegionalPresets.Create("DZ")!;

        foreach (var key in UiFonts.All)
        {
            settings.UiFont = key;
            RegionalSettingsValidator.NormalizeAndValidate(settings);
            Assert.Equal(key, settings.UiFont);
        }

        // Case and padding are the operator's, not a different face.
        settings.UiFont = "  Cairo  ";
        RegionalSettingsValidator.NormalizeAndValidate(settings);
        Assert.Equal("cairo", settings.UiFont);

        // An unknown or empty key must not fail the whole profile save — the company simply
        // gets the default face. The device's own face is a choice, not a landing place.
        foreach (var bad in new[] { "comic-sans", "", "   ", null })
        {
            settings.UiFont = bad!;
            RegionalSettingsValidator.NormalizeAndValidate(settings);
            Assert.Equal(UiFonts.Default, settings.UiFont);
        }
    }

    [Fact]
    public void Default_ui_font_is_thmanyah_sans_and_stored_choices_stay_unchanged()
    {
        Assert.Equal("thmanyah-sans", UiFonts.Default);
        Assert.True(UiFonts.IsKnown("thmanyah-sans"));
        Assert.Equal("cairo", UiFonts.Normalize("cairo"));
    }

    [Fact]
    public void Presets_are_independent_copies()
    {
        var first = RegionalPresets.Create("DZ")!;
        first.TaxRates.Clear();
        Assert.NotEmpty(RegionalPresets.Create("DZ")!.TaxRates);
    }

    [Fact]
    public void Companies_default_to_algeria()
    {
        var company = new TenantOptions { Id = "a", ConnectionString = "Data Source=a.db" };
        var regional = company.ConfiguredRegional();
        Assert.Equal("DZD", regional.CurrencyCode);
        Assert.Equal(new[] { 19m, 9m, 0m }, regional.TaxRates);
    }

    [Fact]
    public void Country_preset_is_applied_and_unknown_presets_fail()
    {
        Assert.Equal("TND", new TenantOptions { Country = "tn" }.ConfiguredRegional().CurrencyCode);
        Assert.Throws<ArgumentException>(() => new TenantOptions { Country = "XX" }.ConfiguredRegional());
    }

    [Fact]
    public void Configured_profile_wins_over_preset_and_is_validated()
    {
        var custom = RegionalPresets.Create("FR")!;
        custom.CurrencyCode = "chf";
        var company = new TenantOptions { Country = "DZ", Regional = custom };
        Assert.Equal("CHF", company.ConfiguredRegional().CurrencyCode);

        var invalid = RegionalPresets.Create("FR")!;
        invalid.DefaultTaxRate = 7m;
        Assert.Throws<ArgumentException>(() => new TenantOptions { Regional = invalid }.ConfiguredRegional());
    }

    [Theory]
    [InlineData("country")]
    [InlineData("currency")]
    [InlineData("decimals")]
    [InlineData("language")]
    [InlineData("defaultLanguage")]
    [InlineData("taxRate")]
    [InlineData("fiscalKey")]
    [InlineData("duplicateKey")]
    [InlineData("payment")]
    [InlineData("timeZone")]
    [InlineData("rounding")]
    [InlineData("roundingPrecision")]
    [InlineData("roundingMode")]
    [InlineData("sections")]
    [InlineData("requiredSection")]
    [InlineData("rateLabel")]
    [InlineData("languageCode")]
    public void Invalid_profiles_are_rejected(string problem)
    {
        var s = RegionalPresets.Create("DZ")!;
        switch (problem)
        {
            case "country": s.CountryCode = "DZA"; break;
            case "currency": s.CurrencyCode = "DA"; break;
            case "decimals": s.CurrencyDecimals = 4; break;
            case "language": s.Languages.Add("de"); break;
            case "defaultLanguage": s.DefaultLanguage = "ar"; s.Languages = ["fr"]; break;
            case "taxRate": s.TaxRates.Add(120m); break;
            case "fiscalKey": s.FiscalIds[0].Key = "NIF number"; break;
            case "duplicateKey": s.FiscalIds[1].Key = "nif"; break;
            case "payment": s.PaymentMethods = ["Bitcoin"]; break;
            case "timeZone": s.TimeZone = "Algiers time; drop"; break;
            case "rounding": s.CashRoundingIncrement = -5m; break;
            case "roundingPrecision": s.CashRoundingIncrement = 0.005m; break;
            case "roundingMode": s.CashRoundingMode = "Banker"; break;
            case "sections": s.ReceiptSections = ["lines", "totals", "qrCode"]; break;
            case "requiredSection": s.ReceiptSections = ["header", "lines"]; break;
            case "rateLabel": s.TaxRateLabels.Add(new TaxRateLabel { Rate = 7m, Labels = new() { ["fr"] = "x" } }); break;
            case "languageCode": s.Languages.Add("German"); break;
        }
        Assert.Throws<ArgumentException>(() => RegionalSettingsValidator.NormalizeAndValidate(s, _ => false));
    }

    [Fact]
    public void Normalization_cleans_codes_and_unsupported_labels()
    {
        var s = RegionalPresets.Create("DZ")!;
        s.CountryCode = " dz ";
        s.PaymentMethods = ["cash", "CARD"];
        s.TaxNames["German!"] = "MwSt";
        s.CashRoundingMode = "up";
        s.ReceiptSections = [];
        RegionalSettingsValidator.NormalizeAndValidate(s);
        Assert.Equal("DZ", s.CountryCode);
        Assert.Equal(new[] { "Cash", "Card" }, s.PaymentMethods);
        Assert.False(s.TaxNames.ContainsKey("German!"));
        Assert.Equal(CashRoundingModes.Up, s.CashRoundingMode);
        Assert.Equal(ReceiptSections.Default, s.ReceiptSections);
    }

    [Fact]
    public void Extra_languages_need_a_pack()
    {
        var s = RegionalPresets.Create("DZ")!;
        s.Languages.Add("tzm");
        s.TaxNames["tzm"] = "TVA";
        Assert.Throws<ArgumentException>(() => RegionalSettingsValidator.NormalizeAndValidate(s, _ => false));
        RegionalSettingsValidator.NormalizeAndValidate(s, code => code == "tzm");
        Assert.Equal("TVA", s.TaxNames["tzm"]);
    }

    [Fact]
    public void Language_packs_are_validated()
    {
        LanguagePack Pack(string code) => new() { Code = code, Label = "Tamaziɣt", Strings = new() { ["login.title"] = "Tuqqna" } };
        var pack = Pack(" TZM ");
        LanguagePack.NormalizeAndValidate(pack);
        Assert.Equal("tzm", pack.Code);
        Assert.Throws<ArgumentException>(() => LanguagePack.NormalizeAndValidate(Pack("fr")));
        Assert.Throws<ArgumentException>(() => LanguagePack.NormalizeAndValidate(new LanguagePack { Code = "es", Label = "Español" }));
        var badFallback = Pack("es");
        badFallback.FallbackLanguage = "de";
        Assert.Throws<ArgumentException>(() => LanguagePack.NormalizeAndValidate(badFallback));
    }

    [Fact]
    public async Task Configured_packs_get_content_revisions()
    {
        LanguagePack Pack(string text) => new() { Code = "tzm", Label = "Tamaziɣt", Strings = new() { ["login.title"] = text } };
        var first = await new ConfiguredLanguagePackStore([Pack("Tuqqna")]).RevisionsAsync(default);
        var same = await new ConfiguredLanguagePackStore([Pack("Tuqqna")]).RevisionsAsync(default);
        var edited = await new ConfiguredLanguagePackStore([Pack("Anekcum")]).RevisionsAsync(default);
        Assert.Equal(first["tzm"], same["tzm"]);
        Assert.NotEqual(first["tzm"], edited["tzm"]);
        Assert.Throws<ArgumentException>(() => new ConfiguredLanguagePackStore([Pack("a"), Pack("b")]));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 0.01)]
    [InlineData(3, 0.001)]
    public void Tolerance_follows_currency_decimals(int decimals, double expected)
    {
        var s = new RegionalSettings { CurrencyDecimals = decimals };
        Assert.Equal((decimal)expected, s.MoneyTolerance());
        Assert.Equal(decimals == 0 ? 3m : decimals == 2 ? 2.51m : 2.505m, s.RoundMoney(2.505m));
    }
}
