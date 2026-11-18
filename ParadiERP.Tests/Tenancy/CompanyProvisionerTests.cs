using System.Security.Cryptography;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Platform;
using ParadiERP.Infrastructure.Subscriptions;
using ParadiERP.Infrastructure.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Tenancy;

public sealed class CompanyProvisionerTests : IAsyncLifetime
{
    private sealed class ReversingProtector : ISecretProtector
    {
        public string Protect(string value) => "enc:" + new string(value.Reverse().ToArray());
        public string Unprotect(string value) => value.StartsWith("enc:", StringComparison.Ordinal)
            ? new string(value[4..].Reverse().ToArray())
            : throw new CryptographicException("unknown key");
    }

    private readonly string folder = Path.Combine(Path.GetTempPath(), "pos-provisioning-" + Guid.NewGuid().ToString("N"));
    private readonly ReversingProtector protector = new();
    private PlatformDbContext platformDb = null!;
    private TenantRegistry registry = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        platformDb = OpenPlatform();
        await platformDb.Database.MigrateAsync();
        registry = new TenantRegistry(
            [new TenantOptions { Id = "configured", Hosts = ["configured.test"], ConnectionString = "Data Source=" + Path.Combine(folder, "configured.db") }],
            reservedHost: "ops.test");
    }

    public async Task DisposeAsync()
    {
        await platformDb.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    private PlatformDbContext OpenPlatform() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseSqlite("Data Source=" + Path.Combine(folder, "platform.db")).Options);

    private CompanyProvisioner Provisioner() => new(platformDb, registry, protector, new ProvisioningOptions
    {
        CompanyDataDirectory = Path.Combine(folder, "companies"),
        PlatformDatabasePath = Path.Combine(folder, "platform.db")
    });

    private static CreateCompanyRequest Request(string id = "casa-shop", params string[] hosts) => new(
        id, hosts.Length == 0 ? ["casa.test"] : hosts, "sqlite", null, "ma", "owner",
        new SubscriptionOptions { Plan = "Trial", Status = "Trial", ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30), GraceDays = 3 },
        "New customer");

    [Fact]
    public async Task Creates_a_live_company_with_a_one_time_password()
    {
        var created = await Provisioner().CreateAsync(Request(), "operator", default);

        var tenant = registry.Resolve("CASA.test");
        Assert.NotNull(tenant);
        Assert.Equal(TenantSource.Portal, tenant.Source);
        Assert.Equal("MAD", tenant.ConfiguredRegional().CurrencyCode);
        Assert.Null(tenant.BootstrapAdminPassword);
        Assert.Equal(20, created.BootstrapPassword.Length);

        var options = new DbContextOptionsBuilder<PosDbContext>();
        PosDbProviders.Configure(options, tenant.DatabaseProvider, tenant.ConnectionString);
        await using (var company = new PosDbContext(options.Options))
        {
            var admin = await company.Users.SingleAsync();
            Assert.Equal("owner", admin.Username);
            Assert.True(BCrypt.Net.BCrypt.Verify(created.BootstrapPassword, admin.PasswordHash));
        }

        var row = await platformDb.Companies.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(tenant.ConnectionString, row.ProtectedConnectionString);
        var audit = await platformDb.Audit.AsNoTracking().SingleAsync(x => x.Action == "company.created");
        Assert.DoesNotContain(created.BootstrapPassword, audit.Detail);
        Assert.DoesNotContain("Data Source", audit.Detail);
        var subscription = await platformDb.Subscriptions.AsNoTracking().SingleAsync(x => x.CompanyId == "casa-shop");
        Assert.Equal("Trial", System.Text.Json.JsonSerializer.Deserialize<SubscriptionOptions>(subscription.SettingsJson)!.Status);

        // What the next process start would load.
        var loaded = Assert.Single(await CompanyProvisioner.LoadAsync(platformDb, protector, default));
        Assert.Equal(tenant.ConnectionString, loaded.ConnectionString);
    }

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("-casa")]
    [InlineData("ab")]
    public async Task Rejects_invalid_ids(string id) =>
        await Assert.ThrowsAsync<ArgumentException>(() => Provisioner().CreateAsync(Request(id), "operator", default));

    [Fact]
    public async Task Rejects_host_and_id_collisions_without_leaving_files()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Provisioner().CreateAsync(Request("other", "configured.test"), "operator", default));
        await Assert.ThrowsAsync<ArgumentException>(() => Provisioner().CreateAsync(Request("other", "ops.test"), "operator", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provisioner().CreateAsync(Request("configured", "x.test"), "operator", default));
        Assert.False(File.Exists(Path.Combine(folder, "companies", "other.db")));
        Assert.Single(registry.Tenants);
        Assert.Empty(await platformDb.Companies.ToListAsync());
    }

    [Fact]
    public async Task Refuses_an_existing_database_file()
    {
        Directory.CreateDirectory(Path.Combine(folder, "companies"));
        await File.WriteAllTextAsync(Path.Combine(folder, "companies", "casa-shop.db"), "not ours");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provisioner().CreateAsync(Request(), "operator", default));
        Assert.Equal("not ours", await File.ReadAllTextAsync(Path.Combine(folder, "companies", "casa-shop.db")));
    }

    [Fact]
    public async Task Archives_restores_and_rehosts_with_revision_checks()
    {
        await Provisioner().CreateAsync(Request(), "operator", default);
        var original = registry.Resolve("casa.test")!;

        Assert.False(await Provisioner().SetArchivedAsync("casa-shop", true, revision: 0, "operator", "stale", default));
        Assert.True(await Provisioner().SetArchivedAsync("casa-shop", true, revision: 1, "operator", "Contract ended", default));
        Assert.Null(registry.Resolve("casa.test"));
        Assert.False(original.Archived);

        Assert.True(await Provisioner().SetArchivedAsync("casa-shop", false, revision: 2, "operator", "Renewed", default));
        Assert.NotNull(registry.Resolve("casa.test"));

        Assert.True(await Provisioner().UpdateHostsAsync("casa-shop", ["shop.casa.test", " CASA2.test "], 3, "operator", "New domain", default));
        Assert.Null(registry.Resolve("casa.test"));
        Assert.Equal("casa-shop", registry.Resolve("casa2.test")!.Id);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Provisioner().UpdateHostsAsync("casa-shop", ["configured.test"], 4, "operator", "Clash", default));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Provisioner().UpdateHostsAsync("configured", ["x.test"], 0, "operator", "Config", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Provisioner().SetArchivedAsync("missing", true, 0, "operator", "Nope", default));
        Assert.Equal(new[] { "company.archived", "company.created", "company.hosts", "company.restored" },
            (await platformDb.Audit.AsNoTracking().Select(x => x.Action).ToListAsync()).Order());
    }

    [Fact]
    public async Task Loading_with_a_lost_key_ring_fails_clearly()
    {
        platformDb.Companies.Add(new ProvisionedCompany { Id = "lost", HostsJson = "[\"lost.test\"]", DatabaseProvider = "Sqlite", ProtectedConnectionString = "garbage" });
        await platformDb.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CompanyProvisioner.LoadAsync(platformDb, protector, default));
        Assert.Contains("DataProtection:KeysPath", error.Message);
    }

    [Fact]
    public void Generated_passwords_are_long_and_distinct()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => CompanyProvisioner.GeneratePassword()).ToHashSet();
        Assert.Equal(50, passwords.Count);
        Assert.All(passwords, p => Assert.True(p.Length >= 12));
    }
}
