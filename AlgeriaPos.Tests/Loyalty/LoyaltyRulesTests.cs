using AlgeriaPos.Application.Loyalty;
using AlgeriaPos.Application.Regional;

namespace AlgeriaPos.Tests.Loyalty;

public class LoyaltyRulesTests
{
    private static readonly LoyaltyProgram Program = new(true, PointsPerUnit: 0.1m, PointValue: 0.5m, MinRedeemPoints: 20);

    [Theory]
    [InlineData(1000, 0, 0, 100)]
    [InlineData(1000, 200, 300, 50)]
    [InlineData(99.9, 0, 0, 9)]
    [InlineData(100, 150, 0, 0)]
    public void Points_are_earned_on_the_amount_not_paid_with_rewards(decimal total, decimal loyalty, decimal giftCard, int expected) =>
        Assert.Equal(expected, LoyaltyRules.EarnedPoints(Program, LoyaltyRules.EligibleAmount(total, loyalty, giftCard)));

    [Fact]
    public void A_disabled_programme_earns_nothing() =>
        Assert.Equal(0, LoyaltyRules.EarnedPoints(Program with { Enabled = false }, 1000));

    [Theory]
    [InlineData(10, 20)]
    [InlineData(10.2, 21)]
    [InlineData(0, 0)]
    public void Redemption_rounds_points_up(decimal amount, int expected) =>
        Assert.Equal(expected, LoyaltyRules.PointsForAmount(Program, amount));

    [Theory]
    [InlineData(19, 0)]
    [InlineData(20, 10)]
    [InlineData(101, 50.5)]
    public void Redeemable_value_respects_the_threshold(int points, decimal expected) =>
        Assert.Equal(expected, LoyaltyRules.MaxRedeemValue(Program, points));

    [Fact]
    public void Programme_settings_are_validated()
    {
        LoyaltyRules.Validate(Program);
        LoyaltyRules.Validate(new LoyaltyProgram(false, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => LoyaltyRules.Validate(Program with { PointValue = 0 }));
        Assert.Throws<ArgumentException>(() => LoyaltyRules.Validate(Program with { PointsPerUnit = -1 }));
        Assert.Throws<ArgumentException>(() => LoyaltyRules.Validate(Program with { MinRedeemPoints = -1 }));
    }

    [Fact]
    public void Gift_card_problems_are_detected()
    {
        var now = new DateTime(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(GiftCardRules.Problem(true, 100, null, 100, now));
        Assert.Equal("inactive", GiftCardRules.Problem(false, 100, null, 10, now));
        Assert.Equal("expired", GiftCardRules.Problem(true, 100, now, 10, now));
        Assert.Equal("insufficient", GiftCardRules.Problem(true, 5, now.AddDays(1), 10, now));
        Assert.Equal("ABC-12", GiftCardRules.NormalizeCode("  abc-12 "));
    }

    [Fact]
    public void New_tenders_are_optional_in_country_profiles()
    {
        var profile = RegionalPresets.Create("DZ")!;
        Assert.DoesNotContain("GiftCard", profile.PaymentMethods);
        profile.PaymentMethods.AddRange(["giftcard", "Loyalty"]);
        RegionalSettingsValidator.NormalizeAndValidate(profile);
        Assert.Contains("GiftCard", profile.PaymentMethods);
        Assert.Contains("Loyalty", profile.PaymentMethods);
    }
}
