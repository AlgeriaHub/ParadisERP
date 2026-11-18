namespace AlgeriaPos.Application.Subscriptions;

public record SubscriptionAccess(
    string Plan, string Status, bool CanOperate, DateTimeOffset? AccessUntilUtc,
    int? MaxUsers, int? MaxBranches, int? MaxDevices, DateTimeOffset CheckedAtUtc);

/// <summary>Billing and licensing adapters supply entitlements, independently of identity.</summary>
public interface ISubscriptionProvider
{
    SubscriptionAccess GetAccess();
}

public static class SubscriptionPolicy
{
    public static SubscriptionAccess Evaluate(string plan, string status, DateTimeOffset? expiresAtUtc,
        int graceDays, int? maxUsers, int? maxBranches, int? maxDevices, DateTimeOffset now)
    {
        if (graceDays is < 0 or > 30 || maxUsers is <= 0 || maxBranches is <= 0 || maxDevices is <= 0)
            throw new ArgumentException("Grace must be 0–30 days and limits must be positive or null.");
        if (status is not ("Unrestricted" or "Trial" or "Active" or "Suspended" or "Cancelled"))
            throw new ArgumentException("Unknown subscription status.");
        if (status is "Trial" or "Active" && expiresAtUtc is null)
            throw new ArgumentException("An active subscription or trial requires an expiry date.");

        var until = status == "Unrestricted" ? null : expiresAtUtc?.AddDays(graceDays);
        var canOperate = status == "Unrestricted"
            || (status is "Trial" or "Active" && now < until);
        var effectiveStatus = status;
        if (status is "Trial" or "Active" && now >= expiresAtUtc)
            effectiveStatus = canOperate ? "Grace" : "Expired";
        return new(plan, effectiveStatus, canOperate, until, maxUsers, maxBranches, maxDevices, now);
    }
}
