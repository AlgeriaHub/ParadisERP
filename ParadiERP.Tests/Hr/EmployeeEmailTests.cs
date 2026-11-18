using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Application.Contracts;
using ParadiERP.Infrastructure.Hr;
using ParadiERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Hr;

/// <summary>
/// An employee's e-mail address (owner, 2026-10-05: "new employee — add a field for the email
/// address, optional"): a stored fact on the staff file, nothing mailed and nothing about the
/// account. Trimmed, blank is none, and an address has to look like one. Omitted on an edit it is
/// kept — the convention the allowance overrides already follow — so a form from before the field
/// cannot wipe it.
/// </summary>
public class EmployeeEmailTests
{
    private static UpsertEmployeeRequest Request(string name, string? email) =>
        new(name, HrHarness.Algiers, "Monthly", 52_000m, "Cash", new DateOnly(2020, 1, 1), Email: email);

    private static async Task<string?> StoredAsync(HrHarness h, Guid id) =>
        (await h.Db.Employees.AsNoTracking().SingleAsync(e => e.Id == id)).Email;

    [Fact]
    public async Task An_address_is_kept_trimmed_and_read_back()
    {
        await using var h = await HrHarness.NewAsync();

        var created = await h.Employees.CreateAsync(Request("Ali Benali", "  ali.benali@exemple.dz "));

        Assert.True(created.Ok, created.Message);
        Assert.Equal("ali.benali@exemple.dz", created.Value!.Email);
        Assert.Equal("ali.benali@exemple.dz", (await h.Employees.ReadAsync(created.Value.Id))!.Email);
        Assert.Equal("ali.benali@exemple.dz", await StoredAsync(h, created.Value.Id));

        // Not unique: two people may share a family address.
        var second = await h.Employees.CreateAsync(Request("Omar Benali", "ali.benali@exemple.dz"));
        Assert.True(second.Ok, second.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public async Task No_address_or_a_blank_one_is_none(string? email)
    {
        await using var h = await HrHarness.NewAsync();

        var created = await h.Employees.CreateAsync(Request("Ali Benali", email));

        Assert.True(created.Ok, created.Message);
        Assert.Null(created.Value!.Email);
        Assert.Null(await StoredAsync(h, created.Value.Id));
    }

    [Theory]
    [InlineData("pas-une-adresse")]
    [InlineData("ali@")]
    [InlineData("ali@exemple")]
    [InlineData("ali benali@exemple.dz")]
    public async Task Something_that_is_not_an_address_is_refused_in_words(string email)
    {
        await using var h = await HrHarness.NewAsync();

        var refused = await h.Employees.CreateAsync(Request("Ali Benali", email));

        Assert.Equal(HrFailure.Invalid, refused.Failure);
        Assert.Equal(HrProblems.EmailInvalid, refused.Code);
        Assert.Equal("Enter a valid email address, or leave it empty.", refused.Message);
        Assert.Equal(0, await h.Db.Employees.CountAsync());
    }

    [Fact]
    public async Task An_address_longer_than_the_column_is_refused_rather_than_cut()
    {
        await using var h = await HrHarness.NewAsync();
        var tooLong = new string('a', 190) + "@exemple.dz";

        Assert.Equal(HrProblems.EmailInvalid, (await h.Employees.CreateAsync(Request("Ali Benali", tooLong))).Code);
    }

    [Fact]
    public async Task An_edit_that_leaves_the_address_out_keeps_it_and_a_blank_one_clears_it()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = (await h.Employees.CreateAsync(Request("Ali Benali", "ali@exemple.dz"))).Value!;

        // A form from before the field: no Email at all.
        var older = await h.Employees.UpdateAsync(ali.Id, Request("Ali Benali Ali", null));
        Assert.True(older.Ok, older.Message);
        Assert.Equal("ali@exemple.dz", older.Value!.Email);
        Assert.Equal("ali@exemple.dz", await StoredAsync(h, ali.Id));

        var changed = await h.Employees.UpdateAsync(ali.Id, Request("Ali Benali", "ali.b@exemple.dz"));
        Assert.Equal("ali.b@exemple.dz", changed.Value!.Email);

        // An invalid one on an edit is refused and the stored one stays.
        Assert.Equal(HrProblems.EmailInvalid, (await h.Employees.UpdateAsync(ali.Id, Request("Ali Benali", "ali.b@"))).Code);
        Assert.Equal("ali.b@exemple.dz", await StoredAsync(h, ali.Id));

        var cleared = await h.Employees.UpdateAsync(ali.Id, Request("Ali Benali", ""));
        Assert.True(cleared.Ok, cleared.Message);
        Assert.Null(cleared.Value!.Email);
        Assert.Null(await StoredAsync(h, ali.Id));
    }
}

/// <summary>The same rules as the staff screen meets them: JSON <c>email</c>, and the refusals' codes on the wire.</summary>
[Collection(HostCollection.Name)]
public class EmployeeEmailEndpointTests
{
    [Fact]
    public async Task The_staff_routes_carry_email_and_refuse_a_bad_one_or_a_new_daily_worker_with_a_code()
    {
        using var host = new PosHost();
        using var admin = host.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login",
            new { username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = (string?)null });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var branchId = branches.EnumerateArray().First().GetProperty("id").GetGuid();

        object Body(string? payBasis, string? email, bool withEmail = true) => withEmail
            ? new { fullName = "Nadia Kaci", branchId, payBasis, baseRate = 40000m, payoutMethod = "Cash", hiredOn = "2026-01-01", isActive = true, email }
            : new { fullName = "Nadia Kaci", branchId, payBasis, baseRate = 40000m, payoutMethod = "Cash", hiredOn = "2026-01-01", isActive = true };

        var created = await admin.PostAsJsonAsync("/api/hr/employees", Body("Monthly", " nadia.kaci@exemple.dz "));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var employee = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("nadia.kaci@exemple.dz", employee.GetProperty("email").GetString());
        var id = employee.GetProperty("id").GetGuid();

        // An older client that does not know the field keeps the stored address.
        var older = await admin.PutAsJsonAsync($"/api/hr/employees/{id}", Body("Monthly", null, withEmail: false));
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);
        Assert.Equal("nadia.kaci@exemple.dz", (await older.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("email").GetString());

        var invalid = await admin.PostAsJsonAsync("/api/hr/employees", Body("Monthly", "nadia@"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HrProblems.EmailInvalid, (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var daily = await admin.PostAsJsonAsync("/api/hr/employees", Body("Daily", null));
        Assert.Equal(HttpStatusCode.BadRequest, daily.StatusCode);
        var problem = await daily.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HrProblems.PayBasisDailyRetired, problem.GetProperty("code").GetString());
        Assert.Equal(EmployeeDirectory.DailyRetiredMessage, problem.GetProperty("message").GetString());

        // No basis at all is Monthly.
        var unnamed = await admin.PostAsJsonAsync("/api/hr/employees", Body(null, null));
        Assert.Equal(HttpStatusCode.Created, unnamed.StatusCode);
        Assert.Equal("Monthly", (await unnamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("payBasis").GetString());
    }
}
