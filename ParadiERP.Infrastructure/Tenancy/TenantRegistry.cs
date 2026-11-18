using System.Text.Json;
using ParadiERP.Application.Regional;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Subscriptions;

namespace ParadiERP.Infrastructure.Tenancy;

public enum TenantSource { Configuration, Portal }

public sealed class TenantOptions
{
    public string Id { get; set; } = "";
    public string[] Hosts { get; set; } = [];
    public string DatabaseProvider { get; set; } = PosDbProviders.Sqlite;
    public string ConnectionString { get; set; } = "";
    public SubscriptionOptions Subscription { get; set; } = new() { Status = "Suspended" };
    public string AdminUsername { get; set; } = "admin";
    public string? BootstrapAdminPassword { get; set; }
    /// <summary>Preset used when <see cref="Regional"/> is not configured. Defaults to Algeria.</summary>
    public string? Country { get; set; }
    /// <summary>Full country profile from configuration; the operator portal can override it.</summary>
    public RegionalSettings? Regional { get; set; }
    /// <summary>Where the company is defined. Only portal companies can be edited in the portal.</summary>
    public TenantSource Source { get; set; } = TenantSource.Configuration;
    /// <summary>An archived company keeps its database and hostnames but no longer resolves.</summary>
    public bool Archived { get; set; }

    private RegionalSettings? configuredRegional;

    /// <summary>The configured profile, validated once and shared; treat it as read-only.</summary>
    public RegionalSettings ConfiguredRegional()
    {
        if (configuredRegional is not null) return configuredRegional;
        var settings = Regional is null
            ? RegionalPresets.Create(Country ?? RegionalPresets.DefaultCountry)
              ?? throw new ArgumentException($"Company '{Id}' has an unknown country preset '{Country}'.")
            : JsonSerializer.Deserialize<RegionalSettings>(JsonSerializer.Serialize(Regional))!;
        RegionalSettingsValidator.NormalizeAndValidate(settings);
        return configuredRegional = settings;
    }
}

/// <summary>
/// Trusted platform configuration: companies from configuration plus those the operator
/// provisioned in the portal. Never populated from a tenant-supplied ID or URL. The company
/// set is an immutable snapshot swapped atomically, so a request always sees one consistent
/// set even while an operator adds or archives a company.
/// </summary>
public sealed class TenantRegistry
{
    private sealed record Snapshot(IReadOnlyList<TenantOptions> Tenants, IReadOnlyDictionary<string, TenantOptions> ByHost);

    private readonly bool singleCompany;
    private readonly string? reservedHost;
    private Snapshot current;

    /// <param name="reservedHost">A hostname no company may use (the operator portal's).</param>
    public TenantRegistry(IReadOnlyList<TenantOptions> tenants, bool singleCompany = false, string? reservedHost = null)
    {
        this.singleCompany = singleCompany;
        this.reservedHost = string.IsNullOrWhiteSpace(reservedHost) ? null : reservedHost.Trim();
        current = Build(tenants);
    }

    /// <summary>Every company, archived ones included.</summary>
    public IReadOnlyList<TenantOptions> Tenants => Volatile.Read(ref current).Tenants;

    public TenantOptions? Resolve(string host)
    {
        var snapshot = Volatile.Read(ref current);
        if (singleCompany) return snapshot.Tenants[0];
        return snapshot.ByHost.TryGetValue(host, out var tenant) && !tenant.Archived ? tenant : null;
    }

    /// <summary>Validates <paramref name="tenants"/> as a whole and makes it the live set; throws and keeps the old set on any problem.</summary>
    public void Replace(IReadOnlyList<TenantOptions> tenants)
    {
        if (singleCompany) throw new InvalidOperationException("A single-shop install has no company registry to change.");
        Volatile.Write(ref current, Build(tenants));
    }

    /// <summary>Throws the same errors <see cref="Replace"/> would, without changing anything.</summary>
    public void Validate(IReadOnlyList<TenantOptions> tenants) => _ = Build(tenants);

    private Snapshot Build(IReadOnlyList<TenantOptions> tenants)
    {
        if (singleCompany && tenants.Count != 1) throw new ArgumentException("A single-shop install has exactly one company.");
        if (tenants.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tenants.Count)
            throw new ArgumentException("Company IDs must be unique.");
        var byHost = new Dictionary<string, TenantOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var tenant in tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant.Id) || string.IsNullOrWhiteSpace(tenant.ConnectionString))
                throw new ArgumentException("Every company requires an ID and an isolated database connection.");
            // Named, because this aborts the whole startup: a leftover "MySql" entry in one
            // company's configuration must point the operator at that company, not at all of them.
            tenant.DatabaseProvider = PosDbProviders.Normalize(tenant.DatabaseProvider)
                ?? throw new ArgumentException(
                    $"Company '{tenant.Id}': unknown database provider '{tenant.DatabaseProvider}'. Use SqlServer or Sqlite.");
            if (!singleCompany && tenant.Hosts.Length == 0)
                throw new ArgumentException("Every hosted company requires at least one hostname.");
            _ = tenant.ConfiguredRegional();
            foreach (var host in tenant.Hosts)
            {
                if (Uri.CheckHostName(host) == UriHostNameType.Unknown || !byHost.TryAdd(host, tenant))
                    throw new ArgumentException("Company hostnames must be valid, explicit, and unique.");
                if (reservedHost is not null && string.Equals(host, reservedHost, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("A company cannot use the operator portal's hostname.");
            }
        }
        // Archived companies still own their database, so a new company can never reuse it.
        var databases = tenants.Select(DatabaseIdentity).ToList();
        if (databases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != databases.Count)
            throw new ArgumentException("Companies must not share a database.");
        return new Snapshot(tenants.ToList(), byHost);
    }

    public static string DatabaseIdentity(TenantOptions tenant)
    {
        if (tenant.DatabaseProvider == PosDbProviders.Sqlite)
        {
            var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(tenant.ConnectionString);
            if (connection.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory || connection.DataSource == ":memory:"
                || string.IsNullOrWhiteSpace(connection.DataSource))
                throw new ArgumentException("Hosted companies require persistent SQLite files.");
            return "Sqlite:" + Path.GetFullPath(connection.DataSource);
        }
        if (tenant.DatabaseProvider == PosDbProviders.SqlServer)
        {
            var sql = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(tenant.ConnectionString);
            if (string.IsNullOrWhiteSpace(sql.InitialCatalog)) throw new ArgumentException("Specify a database for each company.");
            return $"SqlServer:{sql.DataSource}:{sql.InitialCatalog}";
        }
        throw new ArgumentException($"Unknown company database provider '{tenant.DatabaseProvider}'. Use SqlServer or Sqlite.");
    }
}

public sealed class TenantContext
{
    private TenantOptions? current;
    public TenantOptions Current => current ?? throw new InvalidOperationException("Company has not been resolved.");
    public bool IsResolved => current is not null;
    public SubscriptionOptions Subscription { get; private set; } = new();
    public RegionalSettings Regional { get; private set; } = new();
    public void Initialize(TenantOptions tenant, SubscriptionOptions? subscription = null, RegionalSettings? regional = null)
    {
        if (current is not null) throw new InvalidOperationException("A request cannot switch companies.");
        current = tenant;
        Subscription = subscription ?? tenant.Subscription;
        // A portal profile is read as stored, not through the validator: one saved before phones
        // had rules takes its country preset's here (ConfiguredRegional did so for the other).
        if (regional is not null) PhoneNumbers.FillFromPreset(regional);
        Regional = regional ?? tenant.ConfiguredRegional();
    }
}

public sealed class TenantRegionalSettingsProvider(TenantContext tenant) : IRegionalSettingsProvider
{
    public RegionalSettings Get() => tenant.Regional;
}
