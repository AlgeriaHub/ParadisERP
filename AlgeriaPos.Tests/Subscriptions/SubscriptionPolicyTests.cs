using AlgeriaPos.Application.Subscriptions;

namespace AlgeriaPos.Tests.Subscriptions;

public class SubscriptionPolicyTests
{
    private static readonly DateTimeOffset Expiry = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

    [Theory]
    [InlineData(-1, "Active", true)]
    [InlineData(0, "Grace", true)]
    [InlineData(6, "Grace", true)]
    [InlineData(7, "Expired", false)]
    public void Expiration_has_an_exclusive_grace_deadline(int days, string status, bool allowed)
    {
        var result = SubscriptionPolicy.Evaluate("Standard", "Active", Expiry, 7, 5, 2, 3, Expiry.AddDays(days));
        Assert.Equal(status, result.Status);
        Assert.Equal(allowed, result.CanOperate);
        Assert.Equal(Expiry.AddDays(7), result.AccessUntilUtc);
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Cancelled")]
    public void Suspension_overrides_a_future_paid_period(string status)
    {
        Assert.False(SubscriptionPolicy.Evaluate("Standard", status, Expiry, 7, null, null, null,
            Expiry.AddDays(-10)).CanOperate);
    }

    [Fact]
    public void Active_plan_requires_an_expiry() => Assert.Throws<ArgumentException>(() =>
        SubscriptionPolicy.Evaluate("Standard", "Active", null, 7, null, null, null, Expiry));

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(31, 5)]
    [InlineData(7, 0)]
    public void Invalid_operator_limits_fail_closed(int grace, int users) => Assert.Throws<ArgumentException>(() =>
        SubscriptionPolicy.Evaluate("Standard", "Active", Expiry, grace, users, null, null, Expiry));
}
