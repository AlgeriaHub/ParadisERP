using ParadiERP.Application.Regional;

namespace ParadiERP.Tests.Regional;

/// <summary>
/// "Phone must be saved as mobile format" and "make the country key required" (owner,
/// 2026-11-17): a customer's phone is a mobile of the company's country, stored with its calling
/// code, from the country profile — never a constant.
/// </summary>
public class PhoneNumbersTests
{
    private static PhoneNumberRules Algeria => RegionalPresets.Create("DZ")!.Phone;

    [Theory]
    [InlineData("06.58.41.77.30")]
    [InlineData("0658417730")]
    [InlineData("06 58 41 77 30")]
    [InlineData("06-58-41-77-30")]
    [InlineData("658417730")]
    [InlineData("+213 658 41 77 30")]
    [InlineData("+213658417730")]
    [InlineData("00213658417730")]
    [InlineData("213658417730")]
    [InlineData("+213 0658417730")]
    [InlineData(" (0658) 41.77.30 ")]
    public void Every_way_of_typing_an_algerian_mobile_reads_the_same_number(string typed)
    {
        var reading = PhoneNumbers.Normalize(typed, Algeria);
        Assert.True(reading.IsMobile);
        Assert.Equal("+213658417730", reading.Value);
    }

    [Theory]
    [InlineData("0555123456", "+213555123456")]
    [InlineData("0770518208", "+213770518208")]
    public void The_three_mobile_ranges_are_mobiles(string typed, string stored) =>
        Assert.Equal(stored, PhoneNumbers.Normalize(typed, Algeria).Value);

    [Theory]
    [InlineData("021 23 45 67")]      // a landline
    [InlineData("041582207")]         // a landline, 9 digits
    [InlineData("065841773")]         // a digit short
    [InlineData("06584177301")]       // a digit long
    [InlineData("0555 11 22 33 / 0666 44 55 66")] // two numbers in one box
    [InlineData("tél. 0555123456")]   // words
    [InlineData("+21321234567")]      // an Algerian landline written internationally
    public void What_is_not_a_mobile_is_refused_and_kept_as_typed(string typed)
    {
        var reading = PhoneNumbers.Normalize(typed, Algeria);
        Assert.False(reading.IsMobile);
        Assert.Equal(typed.Trim(), reading.Value);
    }

    [Theory]
    [InlineData("+33 6 12 34 56 78", "+33612345678")]
    [InlineData("0033612345678", "+33612345678")]
    public void A_number_with_another_country_code_is_taken_as_given(string typed, string stored)
    {
        var reading = PhoneNumbers.Normalize(typed, Algeria);
        Assert.True(reading.IsMobile);
        Assert.Equal(stored, reading.Value);
    }

    [Fact]
    public void No_phone_is_none_and_never_refused()
    {
        Assert.Equal(new PhoneReading(null, true), PhoneNumbers.Normalize(null, Algeria));
        Assert.Equal(new PhoneReading(null, true), PhoneNumbers.Normalize("   ", Algeria));
    }

    [Fact]
    public void A_profile_without_phone_rules_keeps_what_was_typed()
    {
        Assert.Equal(new PhoneReading("021 23 45 67", true), PhoneNumbers.Normalize(" 021 23 45 67 ", new PhoneNumberRules()));
        Assert.Equal(new PhoneReading("021 23 45 67", true), PhoneNumbers.Normalize("021 23 45 67", null));
    }

    [Fact]
    public void The_rules_are_the_country_profiles()
    {
        // Tunisia has no trunk prefix: the eight digits are the number.
        var tunisia = RegionalPresets.Create("TN")!.Phone;
        Assert.Equal("+21620123456", PhoneNumbers.Normalize("20 123 456", tunisia).Value);
        var france = RegionalPresets.Create("FR")!.Phone;
        Assert.Equal("+33612345678", PhoneNumbers.Normalize("06 12 34 56 78", france).Value);
        Assert.False(PhoneNumbers.Normalize("01 23 45 67 89", france).IsMobile);
        foreach (var country in RegionalPresets.Countries)
        {
            var rules = RegionalPresets.Create(country)!.Phone;
            Assert.True(rules.IsSet, country);
            Assert.Null(PhoneNumbers.NormalizeAndCheck(rules));
            Assert.True(PhoneNumbers.Normalize(rules.Example, rules).IsMobile, country);
        }
    }

    [Fact]
    public void The_algeria_preset_is_the_one_the_browser_falls_back_to()
    {
        // core/regional.service.ts DEFAULT_REGIONAL.phone mirrors these (phone.spec.ts pins that side).
        Assert.Equal("213", Algeria.CallingCode);
        Assert.Equal("0", Algeria.TrunkPrefix);
        Assert.Equal("^[567][0-9]{8}$", Algeria.MobilePattern);
        Assert.Equal([3, 2, 2, 2], Algeria.DisplayGroups);
        Assert.Equal("0555123456", Algeria.Example);
    }

    [Fact]
    public void A_stored_phone_needs_fixing_when_a_save_would_write_it_otherwise()
    {
        Assert.False(PhoneNumbers.NeedsFixing("+213658417730", Algeria));
        Assert.False(PhoneNumbers.NeedsFixing("+33612345678", Algeria));
        Assert.False(PhoneNumbers.NeedsFixing(null, Algeria));
        Assert.True(PhoneNumbers.NeedsFixing("06.58.41.77.30", Algeria));
        Assert.True(PhoneNumbers.NeedsFixing("021 23 45 67", Algeria));
    }

    [Fact]
    public void A_profile_saved_before_phone_rules_takes_its_country_presets()
    {
        var stored = RegionalPresets.Create("DZ")!;
        stored.Phone = new PhoneNumberRules();
        PhoneNumbers.FillFromPreset(stored);
        Assert.Equal("213", stored.Phone.CallingCode);

        var custom = RegionalPresets.Create("DZ")!;
        custom.Phone.Example = "0661000000";
        PhoneNumbers.FillFromPreset(custom);
        Assert.Equal("0661000000", custom.Phone.Example); // an operator's own rules are kept
    }

    [Fact]
    public void The_validator_refuses_rules_that_cannot_work()
    {
        Assert.NotNull(PhoneNumbers.NormalizeAndCheck(new PhoneNumberRules { CallingCode = "2134", MobilePattern = "^5$" }));
        Assert.NotNull(PhoneNumbers.NormalizeAndCheck(new PhoneNumberRules { CallingCode = "213", MobilePattern = "([" }));
        Assert.NotNull(PhoneNumbers.NormalizeAndCheck(new PhoneNumberRules { CallingCode = "213" }));
        var plus = new PhoneNumberRules { CallingCode = " +213 ", MobilePattern = "^[567][0-9]{8}$" };
        Assert.Null(PhoneNumbers.NormalizeAndCheck(plus));
        Assert.Equal("213", plus.CallingCode);
    }
}
