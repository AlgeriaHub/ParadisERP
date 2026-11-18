using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AlgeriaPos.Tests.Hosting;

/// <summary>
/// Accounts the way the back office makes them since 2026-10-05: an employee first, then an account
/// given to that employee. The server draws the account's Login ID, which is what it signs in with,
/// so a test keeps the returned ID rather than a username of its own choosing.
/// </summary>
public static class TestUsers
{
    /// <summary>An employee on a branch, the way Staff adds one. No Login ID comes with it.</summary>
    public static async Task<Guid> EmployeeAsync(HttpClient admin, string name, Guid branchId)
    {
        var created = await admin.PostAsJsonAsync("/api/hr/employees", new
        {
            fullName = name, branchId, payBasis = "Monthly", baseRate = 30000m, payoutMethod = "Cash",
            hiredOn = "2026-01-01", isActive = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>The account request for an existing employee, answered as it comes.</summary>
    public static Task<HttpResponseMessage> PostAsync(HttpClient admin, Guid employeeId, string role, string password,
        Guid? branchId = null, string? pin = null, IReadOnlyList<string>? permissions = null) =>
        admin.PostAsJsonAsync("/api/users", new
        {
            employeeId, role, password, pin, permissions = permissions ?? Array.Empty<string>(), branchId,
        });

    /// <summary>An employee and their account, in one step. Returns the account's id and its Login ID.</summary>
    public static async Task<(Guid Id, string LoginId)> CreateAsync(HttpClient admin, string name, string role, Guid branchId,
        string password, string? pin = null, IReadOnlyList<string>? permissions = null)
    {
        var employee = await EmployeeAsync(admin, name, branchId);
        return await CreatedAsync(await PostAsync(admin, employee, role, password, branchId, pin, permissions));
    }

    /// <summary>
    /// The same, remembered on the host under a readable name, so the test can go on signing in as
    /// "gerant-a" (<see cref="PosHost.SignInName"/>). The account's display name is <paramref name="name"/>.
    /// </summary>
    public static async Task<(Guid Id, string LoginId)> CreateAsync(PosHost host, HttpClient admin, string name, string role, Guid branchId,
        string password, string? pin = null, IReadOnlyList<string>? permissions = null)
    {
        var created = await CreateAsync(admin, name, role, branchId, password, pin, permissions);
        host.Remember(name, created.LoginId);
        return created;
    }

    /// <summary>The id and Login ID of a 201 from <c>POST /api/users</c>.</summary>
    public static async Task<(Guid Id, string LoginId)> CreatedAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("loginId").GetString()!);
    }
}
