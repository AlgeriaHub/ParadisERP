using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Auth;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Security;

/// <summary>
/// Accounts are given to employees and sign in with a Login ID the server draws (owner, 2026-10-05).
///
/// <para>
/// The employee comes first and gets no Login ID; Users → New user picks the employee and the server
/// draws an 8-digit number, random, never starting with 0, unique company-wide, which the account
/// signs in with and which never changes. One active account per employee, enforced by the endpoint
/// and by a filtered unique index. An employee who leaves loses access, a cashier's own till with
/// them. The employee number (EMP-000001), the fingerprint number and the Login ID stay three
/// separate identifiers. Accounts from before the rule keep signing in as they did.
/// </para>
/// </summary>
[Collection(HostCollection.Name)]
public class EmployeeLoginIdTests
{
    private const string Password = "Essai@2026";

    private static async Task<HttpResponseMessage> LoginAsync(PosHost host, string username, string password, string? deviceCode = null) =>
        await host.CreateClient().PostAsJsonAsync("/api/auth/login", new { username, password, deviceCode });

    private static async Task<HttpClient> AdminAsync(PosHost host)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<JsonElement> UserAsync(HttpClient admin, Guid id) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/users")).EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == id).Clone();

    private static async Task<JsonElement> EmployeeAsync(HttpClient admin, Guid id) =>
        (await admin.GetFromJsonAsync<JsonElement>($"/api/hr/employees/{id}")).Clone();

    [Fact]
    public async Task An_account_is_given_to_an_employee_and_signs_in_with_the_8_digit_Login_ID_the_server_drew()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var branch = await TestBranches.SellingAsync(admin, "Alger");

        // No employee, no account — whatever username or name the body carries.
        var refused = await admin.PostAsJsonAsync("/api/users", new
        {
            username = "choisi", fullName = "Choisi", role = "Manager", password = Password, branchId = branch,
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(EmployeeAccounts.EmployeeRequiredCode, await CodeOfAsync(refused));

        // An employee added in Staff gets no Login ID.
        await using (var db = Db(host)) Assert.Equal(1, await db.Users.CountAsync());
        var employee = await TestUsers.EmployeeAsync(admin, "Ahmed Mohamed Ali", branch);
        await using (var db = Db(host)) Assert.Equal(1, await db.Users.CountAsync());
        var before = await EmployeeAsync(admin, employee);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("accountUserId").ValueKind);

        // The account: a username in the body is ignored, the server draws the Login ID.
        var response = await admin.PostAsJsonAsync("/api/users", new
        {
            employeeId = employee, username = "choisi", fullName = "Autre nom", role = "Manager", password = Password,
        });
        var (id, loginId) = await TestUsers.CreatedAsync(response);
        Assert.True(LoginIds.IsWellFormed(loginId), loginId);
        Assert.Equal(8, loginId.Length);
        Assert.NotEqual('0', loginId[0]);

        var user = await UserAsync(admin, id);
        Assert.Equal(loginId, user.GetProperty("username").GetString());
        Assert.Equal(loginId, user.GetProperty("loginId").GetString());
        Assert.Equal("Ahmed Mohamed Ali", user.GetProperty("fullName").GetString());
        Assert.Equal(employee, user.GetProperty("employeeId").GetGuid());
        // The employee number is its own identifier, untouched by the account and never its Login ID.
        Assert.Equal(before.GetProperty("number").GetString(), user.GetProperty("employeeNumber").GetString());
        // Emp- + branch code + YYMM + sequence since 2026-10-05 (CodeSeries): never eight bare digits.
        Assert.StartsWith("Emp-", user.GetProperty("employeeNumber").GetString());
        Assert.NotEqual(loginId, user.GetProperty("employeeNumber").GetString()!.Replace("Emp-", ""));
        // The account's branch is the employee's when the screen chose none.
        Assert.Equal(branch, user.GetProperty("branchId").GetGuid());
        Assert.Equal(id, (await EmployeeAsync(admin, employee)).GetProperty("accountUserId").GetGuid());

        // It signs in with the Login ID; the username it was never given does not exist.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, loginId, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(host, "choisi", Password)).StatusCode);
        // The bootstrap administrator, with no employee and no Login ID, signs in exactly as before.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, "C01")).StatusCode);
    }

    [Fact]
    public async Task An_employee_has_one_active_account_and_the_database_refuses_a_second_written_directly()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var branch = await TestBranches.SellingAsync(admin, "Oran");
        var employee = await TestUsers.EmployeeAsync(admin, "Karim", branch);
        var (first, firstLogin) = await TestUsers.CreatedAsync(await TestUsers.PostAsync(admin, employee, "Manager", Password));

        var second = await TestUsers.PostAsync(admin, employee, "Manager", Password);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(EmployeeAccounts.EmployeeHasAccountCode, body.GetProperty("code").GetString());
        Assert.Equal(firstLogin, body.GetProperty("signIn").GetString());

        // The backstop: a second active account written straight into the table is refused by the index.
        await using (var db = Db(host))
        {
            db.Users.Add(new User
            {
                Username = "contourne", FullName = "Karim", Role = UserRole.Manager, IsActive = true,
                EmployeeId = employee, BranchId = branch, PasswordHash = "x",
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Switched off, the first account no longer counts: the person may be given a new one.
        var off = await admin.PutAsJsonAsync($"/api/users/{first}", new { fullName = "ignored", role = "Manager", isActive = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var (_, againLogin) = await TestUsers.CreatedAsync(await TestUsers.PostAsync(admin, employee, "Manager", Password));
        Assert.NotEqual(firstLogin, againLogin);

        // And the old one cannot be switched back on while the new one is active.
        var back = await admin.PutAsJsonAsync($"/api/users/{first}", new { fullName = "ignored", role = "Manager", isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        Assert.Equal(EmployeeAccounts.EmployeeHasAccountCode, await CodeOfAsync(back));
    }

    [Fact]
    public async Task Login_IDs_are_unique_random_and_never_change_and_a_drawn_number_already_in_use_is_drawn_again()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var branch = await TestBranches.SellingAsync(admin, "Constantine");

        var ids = new List<string>();
        for (var i = 0; i < 25; i++)
            ids.Add((await TestUsers.CreateAsync(admin, $"Employé {i}", "Manager", branch, Password)).LoginId);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(LoginIds.IsWellFormed(id), id));
        // Not a sequence: consecutive accounts are not consecutive numbers.
        Assert.Contains(ids.Zip(ids.Skip(1)), pair => Math.Abs(long.Parse(pair.First) - long.Parse(pair.Second)) != 1);

        // Created at the same moment by several requests: every one gets its own number.
        var employees = new List<Guid>();
        for (var i = 0; i < 8; i++) employees.Add(await TestUsers.EmployeeAsync(admin, $"Simultané {i}", branch));
        var concurrent = await Task.WhenAll(employees.Select(e => TestUsers.PostAsync(admin, e, "Manager", Password)));
        var drawn = new List<string>();
        foreach (var response in concurrent) drawn.Add((await TestUsers.CreatedAsync(response)).LoginId);
        Assert.Equal(drawn.Count + ids.Count, drawn.Concat(ids).Distinct().Count());

        // A draw that lands on a number already in use — an account's, or an employee number's — is drawn again.
        var taken = ids[0];
        var forced = new System.Collections.Concurrent.ConcurrentQueue<int>([int.Parse(taken), 12345678]);
        var previous = LoginIds.Draw;
        LoginIds.Draw = () => forced.TryDequeue(out var next) ? next : LoginIds.Random();
        try
        {
            var (_, redrawn) = await TestUsers.CreateAsync(admin, "Tirage", "Manager", branch, Password);
            Assert.NotEqual(taken, redrawn);
            Assert.True(LoginIds.IsWellFormed(redrawn));
        }
        finally
        {
            LoginIds.Draw = previous;
        }

        // Immutable: editing the account — role, branch, name — never touches its Login ID.
        var (id, loginId) = await TestUsers.CreateAsync(admin, "Fixe", "Manager", branch, Password);
        var other = await TestBranches.SellingAsync(admin, "Annaba");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/users/{id}",
            new { fullName = "Autre", role = "Admin", isActive = true, branchId = other })).StatusCode);
        var user = await UserAsync(admin, id);
        Assert.Equal(loginId, user.GetProperty("loginId").GetString());
        Assert.Equal(loginId, user.GetProperty("username").GetString());
        Assert.Equal("Fixe", user.GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task An_employee_who_leaves_loses_access_with_their_till_and_a_rename_in_Staff_renames_the_account()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var branch = await TestBranches.SellingAsync(admin, "Blida");
        var employee = await TestUsers.EmployeeAsync(admin, "Samir", branch);
        var number = (await EmployeeAsync(admin, employee)).GetProperty("number").GetString();
        var (id, loginId) = await TestUsers.CreatedAsync(await TestUsers.PostAsync(admin, employee, "Cashier", Password, pin: "1234"));
        var user = await UserAsync(admin, id);
        Assert.True(user.GetProperty("tillActive").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, loginId, Password)).StatusCode);

        // Renamed and moved in Staff: the account takes the name; the employee number does not move.
        var record = await EmployeeAsync(admin, employee);
        var edit = await admin.PutAsJsonAsync($"/api/hr/employees/{employee}", new
        {
            fullName = "Samir Benali", branchId = branch, payBasis = "Monthly", baseRate = 30000m, payoutMethod = "Cash",
            hiredOn = "2026-01-01", isActive = true, jobTitle = "Caissier principal",
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("Samir Benali", (await UserAsync(admin, id)).GetProperty("fullName").GetString());
        Assert.Equal(number, (await EmployeeAsync(admin, employee)).GetProperty("number").GetString());
        Assert.Equal(record.GetProperty("number").GetString(), (await UserAsync(admin, id)).GetProperty("employeeNumber").GetString());

        // Leaving: the account is switched off, the cashier's till with it, and the sign-in refused.
        var left = await admin.PostAsJsonAsync($"/api/hr/employees/{employee}/leave", new { leftOn = "2026-09-30" });
        Assert.Equal(HttpStatusCode.OK, left.StatusCode);
        var after = await UserAsync(admin, id);
        Assert.False(after.GetProperty("isActive").GetBoolean());
        Assert.False(after.GetProperty("tillActive").GetBoolean());
        Assert.Equal(loginId, after.GetProperty("loginId").GetString());
        Assert.NotEqual(HttpStatusCode.OK, (await LoginAsync(host, loginId, Password)).StatusCode);

        // Someone who has left cannot be given an account, nor have theirs switched back on.
        Assert.Equal(EmployeeAccounts.EmployeeInactiveCode, await CodeOfAsync(await TestUsers.PostAsync(admin, employee, "Cashier", Password)));
        var back = await admin.PutAsJsonAsync($"/api/users/{id}", new { fullName = "x", role = "Cashier", isActive = true });
        Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);
        Assert.Equal(EmployeeAccounts.EmployeeInactiveCode, await CodeOfAsync(back));
    }

    [Fact]
    public async Task An_account_from_before_the_rule_is_linked_to_its_employee_and_keeps_signing_in_as_it_did()
    {
        using var host = new PosHost();
        using var admin = await AdminAsync(host);
        var branch = await TestBranches.SellingAsync(admin, "Tlemcen");
        Guid legacy;
        await using (var db = Db(host))
        {
            var user = new User
            {
                Username = "ancien.gerant", FullName = "Ancien", Role = UserRole.Manager, RoleId = SystemRoles.ManagerId,
                IsActive = true, BranchId = branch, PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            legacy = user.Id;
        }
        var listed = await UserAsync(admin, legacy);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("loginId").ValueKind);
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("employeeId").ValueKind);

        var employee = await TestUsers.EmployeeAsync(admin, "Nadia Ancienne", branch);
        var linked = await admin.PostAsJsonAsync($"/api/users/{legacy}/employee", new { employeeId = employee });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var after = await UserAsync(admin, legacy);
        Assert.Equal(employee, after.GetProperty("employeeId").GetGuid());
        Assert.Equal("Nadia Ancienne", after.GetProperty("fullName").GetString());
        // Its sign-in is unchanged: still the username, no Login ID drawn for it.
        Assert.Equal("ancien.gerant", after.GetProperty("username").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("loginId").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(host, "ancien.gerant", Password)).StatusCode);

        // Linked once only, and an employee with an account cannot be given a second through a link.
        Assert.Equal(EmployeeAccounts.AlreadyLinkedCode,
            await CodeOfAsync(await admin.PostAsJsonAsync($"/api/users/{legacy}/employee", new { employeeId = employee })));
        Assert.Equal(EmployeeAccounts.EmployeeHasAccountCode, await CodeOfAsync(await TestUsers.PostAsync(admin, employee, "Manager", Password)));
    }
}
