using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Domain;
using ParadiERP.Tests.Hosting;

namespace ParadiERP.Tests.Security;

/// <summary>
/// P0.1 (owner, 2026-10-25): a branch-locked user holding <c>payroll.manage</c> or <c>hr.attendance</c>
/// reads and acts on their own branch's payroll and staff only. Another branch's run, payslips,
/// payments, advances, employees and punches — named by id in the route, or by an id or a number in
/// the body or the query — are not found, as if they did not exist, and nothing is written. The Admin
/// is answered exactly as before. Before the fix the by-id check covered writes on a route value
/// called <c>id</c> and nothing else.
/// </summary>
[Collection(HostCollection.Name)]
public class PayrollBranchScopeTests
{
    private const string Password = "Essai@2026";

    /// <summary>
    /// Two branches and their September: the main branch (Alger) and Oran, one employee each, both
    /// months computed and approved by the Admin; Oran's wages paid and an advance granted by bank; an
    /// August draft at Alger for adjustments; and two Alger logins — a payroll manager and a foreman.
    /// </summary>
    private sealed record Scenario(
        HttpClient Admin, HttpClient Payroll, HttpClient Foreman,
        Guid Main, Guid Oran,
        Guid EmployeeA, string NumberA, Guid EmployeeB, string NumberB,
        Guid RunA, Guid RunB, Guid DraftA, Guid PaymentB, Guid AdvanceB);

    private static async Task<Scenario> SetUpAsync(PosHost host)
    {
        var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var main = branches.EnumerateArray()
            .Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean())
            .GetProperty("id").GetGuid();
        var oran = await TestBranches.SellingAsync(admin, "Oran");

        var (employeeA, numberA) = await EmployeeAsync(admin, "Amel Benali", main, fingerprint: "7002");
        var (employeeB, numberB) = await EmployeeAsync(admin, "Karim Haddad", oran, fingerprint: "7001");

        var runA = await ApprovedRunAsync(admin, main);
        var runB = await ApprovedRunAsync(admin, oran);
        var draftA = await IdOfAsync(await admin.PostAsJsonAsync("/api/hr/payroll/runs",
            new { branchId = main, periodYear = 2026, periodMonth = 8 }));

        var paymentB = await LocationIdAsync(await admin.PostAsJsonAsync($"/api/hr/payroll/runs/{runB}/payments",
            new { runId = runB, payoutMethod = "Bank" }));
        var advanceB = await LocationIdAsync(await admin.PostAsJsonAsync("/api/hr/advances",
            new { employeeId = employeeB, amount = 5000m, payoutMethod = "Bank" }));

        // Created after the runs were computed, so their own employee records sit on no payslip.
        await TestUsers.CreateAsync(host, admin, "paie-alger", "Manager", main, Password,
            permissions: [Permissions.ManagePayroll]);
        await TestUsers.CreateAsync(host, admin, "pointage-alger", "Manager", main, Password,
            permissions: [Permissions.EnterAttendance]);

        return new Scenario(
            admin,
            await SignedInAsync(host, "paie-alger", Password),
            await SignedInAsync(host, "pointage-alger", Password),
            main, oran, employeeA, numberA, employeeB, numberB, runA, runB, draftA, paymentB, advanceB);
    }

    [Fact]
    public async Task A_branch_payroll_manager_reads_another_branch_s_runs_payslips_payments_advances_and_staff_as_not_found()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);

        string[] oran =
        [
            $"/api/hr/payroll/runs/{s.RunB}",
            $"/api/hr/payroll/runs/{s.RunB}/lines",
            $"/api/hr/payroll/runs/{s.RunB}/payments",
            $"/api/hr/payroll/payments/{s.PaymentB}",
            $"/api/hr/advances/{s.AdvanceB}",
            $"/api/hr/employees/{s.EmployeeB}",
        ];
        foreach (var url in oran)
        {
            Assert.True(HttpStatusCode.NotFound == (await s.Payroll.GetAsync(url)).StatusCode, $"branch manager: {url}");
            // The Admin is company-scoped and reads every branch, exactly as before.
            Assert.True(HttpStatusCode.OK == (await s.Admin.GetAsync(url)).StatusCode, $"admin: {url}");
        }

        string[] alger =
        [
            $"/api/hr/payroll/runs/{s.RunA}",
            $"/api/hr/payroll/runs/{s.RunA}/lines",
            $"/api/hr/payroll/runs/{s.RunA}/payments",
            $"/api/hr/employees/{s.EmployeeA}",
        ];
        foreach (var url in alger)
            Assert.True(HttpStatusCode.OK == (await s.Payroll.GetAsync(url)).StatusCode, $"own branch: {url}");

        // Not found reads the same as an id nobody holds: nothing says the record is there.
        Assert.Equal(HttpStatusCode.NotFound, (await s.Payroll.GetAsync($"/api/hr/payroll/runs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Payroll.GetAsync($"/api/hr/employees/{Guid.NewGuid()}")).StatusCode);

        // The lists never named them either, whatever branch was asked for.
        var runs = await s.Payroll.GetFromJsonAsync<JsonElement>($"/api/hr/payroll/runs?branchId={s.Oran}");
        Assert.NotEmpty(runs.EnumerateArray());
        Assert.All(runs.EnumerateArray(), r => Assert.Equal(s.Main, r.GetProperty("branchId").GetGuid()));
        var staff = await s.Payroll.GetFromJsonAsync<JsonElement>($"/api/hr/employees?branchId={s.Oran}");
        Assert.DoesNotContain(s.EmployeeB, staff.GetProperty("rows").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
        var advances = await s.Payroll.GetFromJsonAsync<JsonElement>($"/api/hr/advances?employeeId={s.EmployeeB}");
        Assert.Empty(advances.EnumerateArray());
    }

    [Fact]
    public async Task A_branch_payroll_manager_can_neither_pay_another_branch_s_run_nor_advance_its_employee_and_nothing_is_written()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);

        // An approved Oran run nobody has paid yet, by its id, by bank so no cash box stands in the
        // way: before P0.1 this paid Oran's wages; now it is not found and nothing is paid.
        var unpaid = await ApprovedRunAsync(s.Admin, s.Oran, month: 7);
        var pay = await s.Payroll.PostAsJsonAsync($"/api/hr/payroll/runs/{unpaid}/payments",
            new { runId = unpaid, payoutMethod = "Bank" });
        Assert.Equal(HttpStatusCode.NotFound, pay.StatusCode);
        var payments = await s.Admin.GetFromJsonAsync<JsonElement>($"/api/hr/payroll/runs/{unpaid}/payments");
        Assert.Empty(payments.EnumerateArray());
        var run = await s.Admin.GetFromJsonAsync<JsonElement>($"/api/hr/payroll/runs/{unpaid}");
        Assert.Equal("Approved", run.GetProperty("run").GetProperty("status").GetString());

        // Oran's employee, named in the body: not found, and no advance exists for them but the Admin's.
        var advance = await s.Payroll.PostAsJsonAsync("/api/hr/advances",
            new { employeeId = s.EmployeeB, amount = 1000m, payoutMethod = "Bank" });
        Assert.Equal(HttpStatusCode.NotFound, advance.StatusCode);
        var advances = await s.Admin.GetFromJsonAsync<JsonElement>($"/api/hr/advances?branchId={s.Oran}");
        Assert.Single(advances.EnumerateArray());

        // The same acts on their own branch are untouched.
        var ownPay = await s.Payroll.PostAsJsonAsync($"/api/hr/payroll/runs/{s.RunA}/payments",
            new { runId = s.RunA, payoutMethod = "Bank" });
        Assert.True(ownPay.StatusCode == HttpStatusCode.Created, await ownPay.Content.ReadAsStringAsync());
        var ownAdvance = await s.Payroll.PostAsJsonAsync("/api/hr/advances",
            new { employeeId = s.EmployeeA, amount = 1000m, payoutMethod = "Bank" });
        Assert.True(ownAdvance.StatusCode == HttpStatusCode.Created, await ownAdvance.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_adjustment_names_someone_the_run_pays_for_every_caller_the_Admin_included()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);
        object Bonus(Guid employeeId) => new { employeeId, key = "prime", amount = 1000m, reason = "Essai" };

        // The run is Alger's own, so the route's branch check lets it through; the person is Oran's.
        foreach (var caller in new[] { s.Payroll, s.Admin })
            Assert.Equal(HttpStatusCode.NotFound,
                (await caller.PutAsJsonAsync($"/api/hr/payroll/runs/{s.DraftA}/adjustments/{s.EmployeeB}", Bonus(s.EmployeeB))).StatusCode);

        var detail = await s.Admin.GetFromJsonAsync<JsonElement>($"/api/hr/payroll/runs/{s.DraftA}");
        var adjusted = detail.GetProperty("run").GetProperty("adjustments").EnumerateArray()
            .Select(a => a.GetProperty("employeeId").GetGuid());
        Assert.DoesNotContain(s.EmployeeB, adjusted);

        // Someone of the run's branch, not yet computed, is adjusted as before.
        var own = await s.Payroll.PutAsJsonAsync($"/api/hr/payroll/runs/{s.DraftA}/adjustments/{s.EmployeeA}", Bonus(s.EmployeeA));
        Assert.True(own.StatusCode == HttpStatusCode.OK, await own.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_branch_foreman_cannot_read_another_branch_s_punches_or_find_its_people_by_number()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);

        // The review: a named employee used to win over the branch the lock had set.
        Assert.Equal(HttpStatusCode.NotFound,
            (await s.Foreman.GetAsync($"/api/hr/attendance/review?employeeId={s.EmployeeB}&year=2026&month=9")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await s.Foreman.GetAsync($"/api/hr/attendance/review?employeeId={s.EmployeeA}&year=2026&month=9")).StatusCode);

        // The clock's lookup by badge number: Oran's number reads exactly as a number nobody holds.
        var foreign = await s.Foreman.GetAsync($"/api/hr/attendance/clock/person?number={s.NumberB}");
        var unknown = await s.Foreman.GetAsync("/api/hr/attendance/clock/person?number=Emp-0000000");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await CodeAsync(unknown), await CodeAsync(foreign));
        Assert.DoesNotContain("Karim", await foreign.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await s.Foreman.GetAsync($"/api/hr/attendance/clock/person?number={s.NumberA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.GetAsync($"/api/hr/attendance/clock/person?number={s.NumberB}")).StatusCode);
    }

    [Fact]
    public async Task A_branch_foreman_writes_nothing_for_another_branch_s_people_and_is_told_nothing_about_them()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);
        object Sheet(Guid employeeId) => new
        {
            branchId = s.Main, periodYear = 2026, periodMonth = 10,
            rows = new[] { new { employeeId, daysPresent = 20m, paidLeaveDays = 0m, unpaidAbsenceDays = 0m } },
        };

        // The sheet used to answer "not on this branch's payroll" — which says the person exists —
        // where an unknown id gets "not found". Both are now not found.
        var foreignSheet = await s.Foreman.PutAsJsonAsync("/api/hr/attendance/sheet", Sheet(s.EmployeeB));
        var unknownSheet = await s.Foreman.PutAsJsonAsync("/api/hr/attendance/sheet", Sheet(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, foreignSheet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownSheet.StatusCode);

        // The day grid, a punch by id or by badge number, and an overtime entry: not found, nothing written.
        Assert.Equal(HttpStatusCode.NotFound,
            (await s.Foreman.GetAsync($"/api/hr/attendance/days?employeeId={s.EmployeeB}&year=2026&month=10")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await s.Foreman.PostAsJsonAsync("/api/hr/attendance/clock", new { employeeId = s.EmployeeB, kind = "In" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await s.Foreman.PostAsJsonAsync("/api/hr/attendance/clock", new { employeeNumber = s.NumberB, kind = "In" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await s.Foreman.PostAsJsonAsync("/api/hr/attendance/overtime",
                new { employeeId = s.EmployeeB, workDate = "2026-10-05", ruleKey = "weekday", hours = 2m })).StatusCode);
        var punches = await s.Admin.GetFromJsonAsync<JsonElement>(
            $"/api/hr/attendance/clock?employeeId={s.EmployeeB}&year=2026&month=10");
        Assert.Empty(punches.GetProperty("events").EnumerateArray());

        // Their own crew's sheet is saved as before.
        var own = await s.Foreman.PutAsJsonAsync("/api/hr/attendance/sheet", Sheet(s.EmployeeA));
        Assert.True(own.StatusCode == HttpStatusCode.OK, await own.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_fingerprint_number_held_at_another_branch_is_refused_without_naming_its_holder()
    {
        using var host = new PosHost();
        var s = await SetUpAsync(host);
        object Hire(string fingerprint) => new
        {
            fullName = "Nadia Saidi", branchId = s.Main, payBasis = "Monthly", baseRate = 30000m,
            payoutMethod = "Bank", bankAccount = "RIB 0001", hiredOn = "2026-01-01", isActive = true,
            fingerprintNumber = fingerprint,
        };

        // Oran's number: refused — the number is unique company-wide — but nobody is named.
        var elsewhere = await s.Payroll.PostAsJsonAsync("/api/hr/employees", Hire("7001"));
        Assert.Equal(HttpStatusCode.Conflict, elsewhere.StatusCode);
        var body = await elsewhere.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fingerprintTakenElsewhere", body.GetProperty("code").GetString());
        Assert.True(!body.TryGetProperty("key", out var key) || key.ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain(s.NumberB, await elsewhere.Content.ReadAsStringAsync());

        // Their own branch's number still names the holder, so the clerk knows whose card to correct.
        var here = await s.Payroll.PostAsJsonAsync("/api/hr/employees", Hire("7002"));
        Assert.Equal(HttpStatusCode.Conflict, here.StatusCode);
        Assert.Equal(s.NumberA, (await here.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString());

        // And the Admin is told whose it is, as before.
        var admin = await s.Admin.PostAsJsonAsync("/api/hr/employees", Hire("7001"));
        Assert.Equal(HttpStatusCode.Conflict, admin.StatusCode);
        var named = await admin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fingerprintTaken", named.GetProperty("code").GetString());
        Assert.Equal(s.NumberB, named.GetProperty("key").GetString());
    }

    // ---------- helpers ----------

    private static async Task<HttpClient> SignedInAsync(PosHost host, string username, string password)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = host.SignInName(username), password, deviceCode = "C01" });
        Assert.True(login.StatusCode == HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    /// <summary>An employee paid by bank, so wages and advances need no cash box.</summary>
    private static async Task<(Guid Id, string Number)> EmployeeAsync(HttpClient admin, string name, Guid branchId, string fingerprint)
    {
        var created = await admin.PostAsJsonAsync("/api/hr/employees", new
        {
            fullName = name, branchId, payBasis = "Monthly", baseRate = 30000m, payoutMethod = "Bank",
            bankAccount = "RIB 0001", hiredOn = "2026-01-01", isActive = true, fingerprintNumber = fingerprint,
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), body.GetProperty("number").GetString()!);
    }

    /// <summary>A month at a branch (September unless said), computed and approved by the Admin.</summary>
    private static async Task<Guid> ApprovedRunAsync(HttpClient admin, Guid branchId, int month = 9)
    {
        var id = await IdOfAsync(await admin.PostAsJsonAsync("/api/hr/payroll/runs",
            new { branchId, periodYear = 2026, periodMonth = month }));
        foreach (var step in new[] { "compute", "approve" })
        {
            var response = await admin.PostAsync($"/api/hr/payroll/runs/{id}/{step}", null);
            Assert.True(response.IsSuccessStatusCode, $"{step}: {await response.Content.ReadAsStringAsync()}");
        }
        return id;
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>The id at the end of a 201's Location, for the routes whose body nests the document.</summary>
    private static async Task<Guid> LocationIdAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return Guid.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
}
