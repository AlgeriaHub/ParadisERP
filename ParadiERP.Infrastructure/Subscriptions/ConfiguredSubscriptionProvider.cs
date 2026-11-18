using ParadiERP.Application.Subscriptions;
using ParadiERP.Infrastructure.Tenancy;

namespace ParadiERP.Infrastructure.Subscriptions;

/// <summary>Operator-owned configuration for one company's isolated deployment.</summary>
public sealed class SubscriptionOptions
{
    public string Plan { get; set; } = "Standard";
    public string Status { get; set; } = "Unrestricted";
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public int GraceDays { get; set; } = 7;
    public int? MaxUsers { get; set; }
    public int? MaxBranches { get; set; }
    public int? MaxDevices { get; set; }
}

public sealed class ConfiguredSubscriptionProvider(TenantContext tenant, TimeProvider time)
    : ISubscriptionProvider
{
    public SubscriptionAccess GetAccess()
    {
        var value = tenant.Subscription;
        return SubscriptionPolicy.Evaluate(value.Plan, value.Status, value.ExpiresAtUtc,
            value.GraceDays, value.MaxUsers, value.MaxBranches, value.MaxDevices, time.GetUtcNow());
    }
}
