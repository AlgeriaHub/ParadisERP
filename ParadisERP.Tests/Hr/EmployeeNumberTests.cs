using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ParadisERP.Tests.Hr;

/// <summary>
/// A new employee's number (owner, 2026-10-05): <c>Emp-</c> + the employee's branch code + the
/// company's YYMM + the sequence of that branch and month — Emp-3612610001 for the first person
/// hired at location 10 in October 2026. Drawn by the server, never changed (not even when the
/// person moves branch); people numbered EMP-000001… before keep theirs. In the harness Alger-Centre
/// is location 10 and Oran 11.
/// </summary>
public class EmployeeNumberTests
{
    private static readonly DateTimeOffset MidOctober = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Each_branch_numbers_its_own_people_and_a_move_keeps_the_number()
    {
        await using var h = await HrHarness.NewAsync(new TestClock(MidOctober));

        Assert.Equal("Emp-3612610001", (await h.HireAsync("Ali Benali")).Number);
        Assert.Equal("Emp-7852610001", (await h.HireAsync("Omar Ziane", branchId: HrHarness.Oran)).Number);
        var karim = await h.HireAsync("Karim Haddad");
        Assert.Equal("Emp-3612610002", karim.Number);

        var moved = await h.Employees.UpdateAsync(karim.Id, new UpsertEmployeeRequest(
            "Karim Haddad", HrHarness.Oran, "Monthly", 52_000m, "Cash", new DateOnly(2020, 1, 1)));
        Assert.True(moved.Ok, moved.Message);
        Assert.Equal("Emp-3612610002", moved.Value!.Number);
        // The move took no number from Oran's counter.
        Assert.Equal("Emp-7852610002", (await h.HireAsync("Samir Benali", branchId: HrHarness.Oran)).Number);
    }

    [Fact]
    public async Task Every_month_starts_again_and_the_company_s_time_zone_decides_it()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 31, 22, 30, 0, TimeSpan.Zero));
        await using var h = await HrHarness.NewAsync(clock);
        Assert.Equal("Emp-3612610001", (await h.HireAsync("Ali Benali")).Number);

        // 23:30 UTC is already 00:30 on 1 November in Algiers.
        clock.Now = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal("Emp-3612611001", (await h.HireAsync("Omar Ziane")).Number);
    }

    [Fact]
    public async Task People_numbered_before_keep_their_numbers_and_do_not_move_the_counter()
    {
        await using var h = await HrHarness.NewAsync(new TestClock(MidOctober));
        h.Db.Employees.Add(new Employee { Number = "EMP-000001", FullName = "Ancien", BranchId = HrHarness.Algiers, HiredOn = new DateOnly(2024, 1, 1) });
        h.Db.Employees.Add(new Employee { Number = "Emp-3612609007", FullName = "Septembre", BranchId = HrHarness.Algiers, HiredOn = new DateOnly(2026, 9, 1) });
        h.Db.Employees.Add(new Employee { Number = "Emp-3612610999", FullName = "Le 999e", BranchId = HrHarness.Algiers, HiredOn = new DateOnly(2026, 10, 1) });
        await h.Db.SaveChangesAsync();

        // The 999th of the month is followed by the 1000th.
        Assert.Equal("Emp-36126101000", (await h.HireAsync("Ali Benali")).Number);
        Assert.Equal("EMP-000001", (await h.Db.Employees.AsNoTracking().SingleAsync(e => e.FullName == "Ancien")).Number);
    }

    [Fact]
    public async Task A_drawn_number_already_taken_trips_the_index_and_is_drawn_again()
    {
        await using var h = await HrHarness.NewAsync(new TestClock(MidOctober));
        Assert.Equal("Emp-3612610001", (await h.HireAsync("Ali Benali")).Number);

        var forced = new System.Collections.Concurrent.ConcurrentQueue<int>([1]);
        var previous = CodeSeriesDraw.Draw;
        CodeSeriesDraw.Draw = next => forced.TryDequeue(out var taken) ? taken : next;
        try
        {
            Assert.Equal("Emp-3612610002", (await h.HireAsync("Omar Ziane")).Number);
            Assert.True(forced.IsEmpty);
        }
        finally
        {
            CodeSeriesDraw.Draw = previous;
        }
        Assert.Equal(2, await h.Db.Employees.CountAsync());
    }
}

/// <summary>Employees hired at the same moment through the real HTTP pipeline.</summary>
[Collection(HostCollection.Name)]
public class EmployeeNumberEndpointTests
{
    [Fact]
    public async Task Employees_saved_at_the_same_moment_each_get_their_own_consecutive_number()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        using var host = new PosHost(services: s => s.AddSingleton<TimeProvider>(clock));
        using var admin = host.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login",
            new { username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        var mainRow = (await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches"))
            .EnumerateArray().Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean());
        var main = mainRow.GetProperty("id").GetGuid();
        var code = mainRow.GetProperty("code").GetString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => admin.PostAsJsonAsync("/api/hr/employees", new
        {
            fullName = $"Simultané {i}", branchId = main, payBasis = "Monthly", baseRate = 30000m, payoutMethod = "Cash",
            hiredOn = "2026-10-01", isActive = true, number = "EMP-999999",
        })));

        var numbers = new List<string>();
        foreach (var response in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            numbers.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("number").GetString()!);
        }
        Assert.Equal(Enumerable.Range(1, 8).Select(n => $"Emp-{code}2610{n:D3}"), numbers.Order());
    }
}
