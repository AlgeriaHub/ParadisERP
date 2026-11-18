using System.Net;
using System.Net.Http.Json;
using AlgeriaPos.API.Endpoints;
using AlgeriaPos.API.Identity;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Platform;
using AlgeriaPos.Infrastructure.Subscriptions;
using AlgeriaPos.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgeriaPos.Tests.Tenancy;

public sealed class TrialEndpointsTests : IAsyncLifetime
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "pos-trials-" + Guid.NewGuid().ToString("N"));
    private PlatformDbContext platformDb = null!;
    private SqliteConnection connection = null!;
    private TestServer server = null!;
    private HttpClient client = null!;

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection);
        platformDb = new PlatformDbContext(builder.Options);
        await platformDb.Database.EnsureCreatedAsync();

        var registry = new TenantRegistry([], reservedHost: "ops.test");
        var protector = new PlainProtector();
        var provOptions = new ProvisioningOptions
        {
            CompanyDataDirectory = folder,
            PlatformDatabasePath = ":memory:",
            FilesRoot = Path.Combine(folder, "files")
        };
        var provisioner = new CompanyProvisioner(platformDb, registry, protector, provOptions);

        var webHost = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddSingleton(platformDb);
                services.AddSingleton(registry);
                services.AddSingleton<ISecretProtector>(protector);
                services.AddSingleton(provOptions);
                services.AddSingleton(provisioner);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapTrialEndpoints(new PlatformOptions { Enabled = true });
                });
            });

        server = new TestServer(webHost);
        client = server.CreateClient();
    }

    public async Task DisposeAsync()
    {
        server?.Dispose();
        client?.Dispose();
        await connection.DisposeAsync();
        if (Directory.Exists(folder))
        {
            try { Directory.Delete(folder, true); }
            catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("-bad")]
    [InlineData("bad-")]
    [InlineData("bad_name")]
    [InlineData("api")]
    [InlineData("admin")]
    public async Task RejectsInvalidOrReservedSlugs(string slug)
    {
        var response = await client.PostAsJsonAsync("/api/trials", new TrialSignupRequest(slug, "My Store", "admin", "DZ"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProvisionsValidTrialTenant()
    {
        var slug = "test-store-" + Guid.NewGuid().ToString("N")[..6];
        var response = await client.PostAsJsonAsync("/api/trials", new TrialSignupRequest(slug, "Test Store", "owner@example.com", "DZ"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TrialSignupResponse>();
        Assert.NotNull(body);
        Assert.Equal(slug, body.Slug);
        Assert.Equal($"{slug}.paradierp.zena4cp.com", body.Host);
        Assert.Equal($"https://{slug}.paradierp.zena4cp.com/#/login", body.LoginUrl);
        Assert.Equal("owner@example.com", body.AdminUsername);
        Assert.False(string.IsNullOrWhiteSpace(body.BootstrapPassword));
        Assert.True(body.ExpiresAtUtc > DateTimeOffset.UtcNow);

        // Verify SQLite database was created
        var dbPath = Path.Combine(folder, slug + ".db");
        Assert.True(File.Exists(dbPath));
    }
}
