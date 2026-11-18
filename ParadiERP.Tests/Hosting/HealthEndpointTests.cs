using System.Net;
using System.Text.Json;

namespace ParadiERP.Tests.Hosting;

/// <summary>
/// docs/07 §6.0 item 2. <c>AddHealthChecks()</c> with nothing registered answered Healthy with
/// the database down, which is worse than no monitor: it says the shop is fine while no till
/// can sell.
/// </summary>
[Collection(HostCollection.Name)]
public class HealthEndpointTests
{
    [Fact]
    public async Task Health_is_healthy_and_says_which_checks_it_ran()
    {
        using var host = new PosHost();
        using var client = host.CreateClient();

        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Healthy", body.RootElement.GetProperty("checks").GetProperty("database").GetString());
    }

    [Fact]
    public async Task Health_turns_unhealthy_when_the_database_is_no_longer_there()
    {
        using var host = new PosHost();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        // Take the database away the way a lost volume or an unmounted disk would. Clearing the
        // pool first is what releases the file handles the pool is holding open.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(host.DatabaseDirectory, recursive: true);

        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Unhealthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Unhealthy", body.RootElement.GetProperty("checks").GetProperty("database").GetString());
        // Anonymous route: it says something is wrong, never the connection string or a stack.
        Assert.DoesNotContain(host.ConnectionString, await response.Content.ReadAsStringAsync());
    }
}
