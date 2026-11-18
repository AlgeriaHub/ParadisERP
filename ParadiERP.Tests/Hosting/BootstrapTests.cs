using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Tests.Hosting;

namespace ParadiERP.Tests.Hosting;

/// <summary>
/// The first real start (docs/07 §6.0 item 1). Two things must both be true: a company nobody
/// has named an administrator for must refuse to start rather than seed the demo pair, and the
/// upgrade that ships that rule must not lock out a deployment that already has its own admin.
/// </summary>
[Collection(HostCollection.Name)]
public class BootstrapTests
{
    private static readonly Dictionary<string, string?> NoBootstrap = new()
    {
        ["Bootstrap__AdminUsername"] = null,
        ["Bootstrap__AdminPassword"] = null,
    };

    [Fact]
    public void An_empty_database_outside_development_refuses_to_start_and_names_the_variable()
    {
        var refusal = Assert.ThrowsAny<Exception>(() => new PosHost(NoBootstrap));

        // The message is the whole point: an operator reading `docker compose logs pos` has to
        // learn what to set, not that something was null.
        Assert.Contains("Bootstrap__AdminPassword", Flatten(refusal));
    }

    [Fact]
    public void A_password_shorter_than_eight_characters_is_refused()
    {
        var refusal = Assert.ThrowsAny<Exception>(() => new PosHost(new Dictionary<string, string?>
        {
            ["Bootstrap__AdminPassword"] = "Adm@123",
        }));

        Assert.Contains("Bootstrap__AdminPassword", Flatten(refusal));
    }

    [Fact]
    public async Task A_bootstrapped_company_signs_its_own_administrator_in_and_carries_no_demo_data()
    {
        using var host = new PosHost();
        using var client = host.CreateClient();

        var ok = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("\"token\"", await ok.Content.ReadAsStringAsync());

        // The demo company's pair — the one every copy of this software would otherwise share.
        var demo = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "admin", password = Guid.NewGuid().ToString("N"), deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, demo.StatusCode);

        var demoCashier = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "caisse1", password = "Caisse@123", deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, demoCashier.StatusCode);
    }

    [Fact]
    public async Task A_database_that_already_has_users_starts_with_no_bootstrap_variables_at_all()
    {
        // Kept alive on purpose: its directory is the database the second host starts on, and
        // disposing it takes that directory away.
        using var first = new PosHost();
        using (var client = first.CreateClient())
        {
            var login = await client.PostAsJsonAsync("/api/auth/login", new
            {
                username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01",
            });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        // The upgrade: same database, and the operator has long since removed the password from
        // the environment exactly as the runbook told them to.
        using var upgraded = new PosHost(NoBootstrap, first.Root);
        using var after = upgraded.CreateClient();
        var again = await after.PostAsJsonAsync("/api/auth/login", new
        {
            username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public void Sqlite_outside_development_is_refused_unless_it_is_asked_for_by_name()
    {
        var refusal = Assert.ThrowsAny<Exception>(() => new PosHost(new Dictionary<string, string?>
        {
            ["Database__AllowSqliteInProduction"] = null,
        }));

        var message = Flatten(refusal);
        Assert.Contains("Database__AllowSqliteInProduction", message);
        Assert.Contains("SqlServer", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public void A_new_demo_database_requires_an_operator_password(string? password)
    {
        var refusal = Assert.ThrowsAny<Exception>(() => new PosHost(new Dictionary<string, string?>
        {
            ["Bootstrap__SeedDemoData"] = "true",
            ["Bootstrap__AdminPassword"] = password,
        }));
        Assert.Contains("Bootstrap__AdminPassword", Flatten(refusal));
    }

    [Fact]
    public async Task Demo_login_uses_the_supplied_password_and_an_upgrade_preserves_it()
    {
        var password = Guid.NewGuid().ToString("N");
        using var first = new PosHost(new Dictionary<string, string?>
        {
            ["Bootstrap__SeedDemoData"] = "true",
            ["Bootstrap__AdminPassword"] = password,
        });
        using (var client = first.CreateClient())
        {
            var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        using var upgraded = new PosHost(new Dictionary<string, string?>
        {
            ["Bootstrap__SeedDemoData"] = "true",
            ["Bootstrap__AdminPassword"] = null,
        }, first.Root);
        using var after = upgraded.CreateClient();
        var again = await after.PostAsJsonAsync("/api/auth/login", new { username = "admin", password });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    /// <summary>Startup failures arrive wrapped; the message may be on any inner exception.</summary>
    private static string Flatten(Exception exception)
    {
        var text = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException) text.Add(current.Message);
        return string.Join(" | ", text);
    }
}
