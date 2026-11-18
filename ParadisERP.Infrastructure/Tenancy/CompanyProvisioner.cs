using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ParadisERP.Application.Regional;
using ParadisERP.Application.Subscriptions;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Platform;
using ParadisERP.Infrastructure.Seeding;
using ParadisERP.Infrastructure.Subscriptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Tenancy;

/// <summary>Encrypts values stored at rest (company connection strings). Implemented with ASP.NET Data Protection.</summary>
public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public sealed class ProvisioningOptions
{
    /// <summary>Folder for SQLite databases of companies created in the portal.</summary>
    public string CompanyDataDirectory { get; init; } = "";
    /// <summary>The platform's own SQLite file, which a company database must never be.</summary>
    public string PlatformDatabasePath { get; init; } = "";
    /// <summary>The files folder's root; each company's files are in a folder named by its id under it.</summary>
    public string FilesRoot { get; init; } = Files.FileStoreOptions.DefaultRoot;
}

public sealed record CreateCompanyRequest(
    string Id, string[] Hosts, string DatabaseProvider, string? ConnectionString,
    string Country, string AdminUsername, SubscriptionOptions Subscription, string Reason);

/// <summary>The bootstrap password exists only in this response: it is never stored, logged or audited.</summary>
public sealed record CreatedCompany(string Id, string AdminUsername, string BootstrapPassword);

public static class CompanyStates
{
    public const string Active = "Active";
    public const string Archived = "Archived";
}

/// <summary>
/// Creates, re-hosts and archives companies from the operator portal, live. Every change is
/// validated against the whole registry before it is saved, and published to the registry only
/// after the platform database has committed it.
/// </summary>
public sealed partial class CompanyProvisioner(
    PlatformDbContext platformDb, TenantRegistry registry, ISecretProtector protector, ProvisioningOptions options)
{
    public const int MaxHosts = 10;

    // One provisioning change at a time: validation, database creation and publication must not interleave.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[A-Za-z0-9._@-]{3,64}$")] private static partial Regex UsernamePattern();

    /// <summary>Provisioned companies as registry entries. Fails loudly when a connection string can no longer be decrypted.</summary>
    public static async Task<List<TenantOptions>> LoadAsync(PlatformDbContext db, ISecretProtector protector, CancellationToken ct)
    {
        var rows = await db.Companies.AsNoTracking().OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
        return rows.Select(row =>
        {
            string connectionString;
            try { connectionString = protector.Unprotect(row.ProtectedConnectionString); }
            catch (CryptographicException exception)
            {
                throw new InvalidOperationException(
                    $"Cannot decrypt the database connection of company '{row.Id}'. Restore the Data Protection key ring " +
                    "(DataProtection:KeysPath) that was in use when the company was created.", exception);
            }
            return ToTenant(row, connectionString);
        }).ToList();
    }

    public async Task<Dictionary<string, ProvisionedCompany>> ListAsync(CancellationToken ct) =>
        await platformDb.Companies.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);

    public async Task<CreatedCompany> CreateAsync(CreateCompanyRequest request, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = (request.Id ?? "").Trim().ToLowerInvariant();
        var adminUsername = (request.AdminUsername ?? "").Trim();
        var country = (request.Country ?? "").Trim().ToUpperInvariant();
        var hosts = NormalizeHosts(request.Hosts);
        RequireReason(request.Reason);
        if (!IdPattern().IsMatch(id))
            throw new ArgumentException("Company IDs use 3-40 lower-case letters, digits and hyphens, and cannot start or end with a hyphen.");
        if (!UsernamePattern().IsMatch(adminUsername))
            throw new ArgumentException("The administrator username needs 3-64 letters, digits or . _ @ -.");
        if (RegionalPresets.Create(country) is null)
            throw new ArgumentException($"Choose a country preset: {string.Join(", ", RegionalPresets.Countries)}.");
        var subscription = request.Subscription ?? throw new ArgumentException("Subscription settings are required.");
        ValidateSubscription(subscription);
        var provider = PosDbProviders.Normalize(request.DatabaseProvider)
            ?? throw new ArgumentException("Choose SqlServer or Sqlite.");

        await Gate.WaitAsync(ct);
        try
        {
            if (registry.Tenants.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
                || await platformDb.Companies.AnyAsync(x => x.Id == id, ct))
                throw new InvalidOperationException($"A company with ID '{id}' already exists.");

            string connectionString;
            string? newSqliteFile = null;
            if (provider == PosDbProviders.Sqlite)
            {
                var path = string.IsNullOrWhiteSpace(request.ConnectionString)
                    ? Path.Combine(options.CompanyDataDirectory, id + ".db")
                    : new SqliteConnectionStringBuilder(request.ConnectionString).DataSource;
                if (string.IsNullOrWhiteSpace(path) || path == ":memory:")
                    throw new ArgumentException("A SQLite company needs a database file.");
                path = Path.GetFullPath(path);
                if (!string.IsNullOrEmpty(options.PlatformDatabasePath)
                    && string.Equals(path, Path.GetFullPath(options.PlatformDatabasePath), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("A company cannot use the platform database.");
                // Never adopt or overwrite a file we did not create.
                if (File.Exists(path)) throw new InvalidOperationException("That database file already exists.");
                connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
                newSqliteFile = path;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.ConnectionString))
                    throw new ArgumentException("SQL Server companies need a connection string with a database name.");
                connectionString = request.ConnectionString.Trim();
            }

            var tenant = new TenantOptions
            {
                Id = id, Hosts = hosts, DatabaseProvider = provider, ConnectionString = connectionString,
                Subscription = subscription, AdminUsername = adminUsername, Country = country, Source = TenantSource.Portal
            };
            var next = registry.Tenants.Append(tenant).ToList();
            registry.Validate(next);

            var password = GeneratePassword();
            try
            {
                await InitializeDatabaseAsync(tenant, password, newSqliteFile is not null, ct);

                platformDb.Companies.Add(new ProvisionedCompany
                {
                    Id = id, HostsJson = JsonSerializer.Serialize(hosts), DatabaseProvider = provider,
                    ProtectedConnectionString = protector.Protect(connectionString),
                    AdminUsername = adminUsername, Country = country, State = CompanyStates.Active, Revision = 1
                });
                // A stale override can exist for an ID that was once in configuration; start clean.
                var existing = await platformDb.Subscriptions.SingleOrDefaultAsync(x => x.CompanyId == id, ct);
                if (existing is null)
                    platformDb.Subscriptions.Add(new SubscriptionOverride { CompanyId = id, SettingsJson = JsonSerializer.Serialize(subscription), Revision = 1 });
                else
                {
                    existing.SettingsJson = JsonSerializer.Serialize(subscription);
                    existing.Revision++;
                }
                Audit(id, actor, "company.created", new
                {
                    reason = request.Reason.Trim(), hosts, provider, country, adminUsername,
                    subscription.Plan, subscription.Status, subscription.ExpiresAtUtc
                });
                await platformDb.SaveChangesAsync(ct);
            }
            catch
            {
                platformDb.ChangeTracker.Clear();
                if (newSqliteFile is not null) DeleteSqliteFiles(newSqliteFile);
                throw;
            }

            registry.Replace(next);
            return new CreatedCompany(id, adminUsername, password);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<bool> UpdateHostsAsync(string id, string[] hosts, long revision, string actor, string reason, CancellationToken ct)
    {
        RequireReason(reason);
        var normalized = NormalizeHosts(hosts);
        return await ChangeAsync(id, revision, actor, "company.hosts", new { reason = reason.Trim(), hosts = normalized },
            (row, tenant) =>
            {
                row.HostsJson = JsonSerializer.Serialize(normalized);
                tenant.Hosts = normalized;
            }, ct);
    }

    public async Task<bool> SetArchivedAsync(string id, bool archived, long revision, string actor, string reason, CancellationToken ct)
    {
        RequireReason(reason);
        return await ChangeAsync(id, revision, actor, archived ? "company.archived" : "company.restored", new { reason = reason.Trim() },
            (row, tenant) =>
            {
                row.State = archived ? CompanyStates.Archived : CompanyStates.Active;
                tenant.Archived = archived;
            }, ct,
            // Startup skips archived databases, so bring a restored one up to the current schema.
            beforeSave: archived ? null : (tenant, token) => MigrateAsync(tenant, options.FilesRoot, token));
    }

    /// <summary>Applies a change to a copy of the company, validates the whole registry, saves, then publishes.</summary>
    private async Task<bool> ChangeAsync(string id, long revision, string actor, string action, object detail,
        Action<ProvisionedCompany, TenantOptions> apply, CancellationToken ct,
        Func<TenantOptions, CancellationToken, Task>? beforeSave = null)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var row = await platformDb.Companies.SingleOrDefaultAsync(x => x.Id == id, ct);
            var current = registry.Tenants.FirstOrDefault(x => x.Id == id);
            if (row is null || current is null)
            {
                if (current?.Source == TenantSource.Configuration)
                    throw new ArgumentException("This company is defined in server configuration; change it there.");
                throw new KeyNotFoundException();
            }
            if (row.Revision != revision) return false;

            // Running requests hold the old instance, so the change goes into a copy.
            var changed = Copy(current);
            apply(row, changed);
            var next = registry.Tenants.Select(x => x.Id == id ? changed : x).ToList();
            try
            {
                registry.Validate(next);
                if (beforeSave is not null) await beforeSave(changed, ct);
            }
            catch
            {
                platformDb.ChangeTracker.Clear();
                throw;
            }

            row.Revision = revision + 1;
            Audit(id, actor, action, detail);
            try { await platformDb.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException)
            {
                platformDb.ChangeTracker.Clear();
                return false;
            }
            registry.Replace(next);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task MigrateAsync(TenantOptions tenant, string filesRoot, CancellationToken ct)
    {
        await using var db = Open(tenant);
        // A restored company may predate the files folder; its files move out before the drop.
        await Files.FileStoreUpgrade.MigrateAsync(db, Files.LocalFileStore.ForCompany(filesRoot, tenant.Id), ct);
        await Authorization.AuthorizationBootstrap.RunAsync(db, ct);
        await Seeding.CoreDataSeeder.EnsureAsync(db, ct);
        var context = new TenantContext();
        var regional = (await new PlatformStore(platformDb).ReadRegionalAsync(tenant.Id, ct)).Settings;
        context.Initialize(tenant, regional: regional);
        await new Rentals.RentalDailyPricingUpgrade(db, new TenantRegionalSettingsProvider(context)).ApplyAsync(ct);
    }

    private static PosDbContext Open(TenantOptions tenant)
    {
        var builder = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(builder, tenant.DatabaseProvider, tenant.ConnectionString);
        return new PosDbContext(builder.Options);
    }

    private static async Task InitializeDatabaseAsync(TenantOptions tenant, string password, bool isNewSqliteFile, CancellationToken ct)
    {
        if (isNewSqliteFile) Directory.CreateDirectory(Path.GetDirectoryName(new SqliteConnectionStringBuilder(tenant.ConnectionString).DataSource)!);
        await using var db = Open(tenant);
        if (!isNewSqliteFile && await db.Database.CanConnectAsync(ct)
            && (await db.Database.GetAppliedMigrationsAsync(ct)).Any() && await db.Users.AnyAsync(ct))
            throw new InvalidOperationException("That database already contains a company. Use an empty database.");
        await db.Database.MigrateAsync(ct);
        if (tenant.DatabaseProvider == PosDbProviders.Sqlite)
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", ct);
        }
        await Authorization.AuthorizationBootstrap.RunAsync(db, ct);
        tenant.BootstrapAdminPassword = password;
        try { await CompanySeeder.SeedAsync(db, tenant, ct); }
        finally { tenant.BootstrapAdminPassword = null; }
    }

    private void Audit(string id, string actor, string action, object detail) =>
        platformDb.Audit.Add(new PlatformAudit
        {
            CompanyId = id, ActorSubject = actor, Action = action, Detail = JsonSerializer.Serialize(detail)
        });

    private static TenantOptions ToTenant(ProvisionedCompany row, string connectionString) => new()
    {
        Id = row.Id,
        Hosts = JsonSerializer.Deserialize<string[]>(row.HostsJson) ?? [],
        DatabaseProvider = row.DatabaseProvider,
        ConnectionString = connectionString,
        AdminUsername = row.AdminUsername,
        Country = row.Country,
        Source = TenantSource.Portal,
        Archived = row.State == CompanyStates.Archived
    };

    private static TenantOptions Copy(TenantOptions source) => new()
    {
        Id = source.Id, Hosts = source.Hosts, DatabaseProvider = source.DatabaseProvider,
        ConnectionString = source.ConnectionString, Subscription = source.Subscription,
        AdminUsername = source.AdminUsername, Country = source.Country, Regional = source.Regional,
        Source = source.Source, Archived = source.Archived
    };

    public static string[] NormalizeHosts(string[]? hosts)
    {
        var normalized = (hosts ?? []).Select(x => (x ?? "").Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToArray();
        if (normalized.Length is 0 or > MaxHosts) throw new ArgumentException($"Give 1-{MaxHosts} hostnames.");
        if (normalized.Any(x => x.Length > 253 || Uri.CheckHostName(x) != UriHostNameType.Dns))
            throw new ArgumentException("Hostnames must be plain DNS names such as shop.example.com.");
        return normalized;
    }

    private static void ValidateSubscription(SubscriptionOptions settings)
    {
        _ = SubscriptionPolicy.Evaluate(settings.Plan, settings.Status, settings.ExpiresAtUtc, settings.GraceDays,
            settings.MaxUsers, settings.MaxBranches, settings.MaxDevices, DateTimeOffset.UtcNow);
        if (string.IsNullOrWhiteSpace(settings.Plan) || settings.Plan.Length > 100)
            throw new ArgumentException("A plan name of up to 100 characters is required.");
    }

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new ArgumentException("A reason (up to 500 characters) is required.");
    }

    public static string GeneratePassword() =>
        RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 20);

    private static void DeleteSqliteFiles(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try { File.Delete(file); }
            catch (IOException) { /* best effort: an orphaned empty file is harmless and blocks reuse of the ID */ }
        }
    }
}
