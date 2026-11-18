using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Paging;

/// <summary>
/// "I need to paginate in backend, not frontend" (owner, 2026-11-17) — the people lists, payroll and administration
/// (AlgeriaPos.API/Endpoints/Paging/PeoplePageRoutes.cs): ten rows to begin with, the screens' filters run on the
/// server over every row, a page past the end answers with the last page, and a list's figures (the owing tiles, the
/// runs' totals row, the overtime hours, the clock's counts) are the server's over all its filtered rows.
/// </summary>
[Collection(HostCollection.Name)]
public class PeoplePageTests
{
    private static async Task<HttpClient> SignedInAsync(PosHost host)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static PosDbContext Db(PosHost host) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);

    private static async Task<JsonElement> PageAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static int Total(JsonElement page) => page.GetProperty("total").GetInt32();

    private static string[] Strings(JsonElement page, string field) =>
        page.GetProperty("items").EnumerateArray().Select(r => r.GetProperty(field).GetString()!).ToArray();

    private static decimal Number(JsonElement element, string field) => element.GetProperty(field).GetDecimal();

    /// <summary>The main branch, a second branch "Oran PT", a box in each, and three employees at the main branch.</summary>
    private sealed record Company(Guid Main, Guid Oran, Guid MainBox, Guid OranBox, Guid E1, Guid E2, Guid E3);

    private static async Task<Company> CompanyAsync(PosHost host)
    {
        await using var db = Db(host);
        var main = (await db.Branches.FirstAsync(b => b.IsMain && !b.IsWarehouse && !b.IsDeleted)).Id;
        var oran = new Branch { Code = "792", NameFr = "Oran PT", NameAr = "Oran PT", NameEn = "Oran PT", IsActive = true, IsOpenForOperation = true };
        db.Branches.Add(oran);
        // One box per branch (unique on BranchId): the main branch's is the seeded one.
        var mainBox = await db.CashBoxes.FirstOrDefaultAsync(b => b.BranchId == main)
            ?? db.CashBoxes.Add(new CashBox { BranchId = main, NameFr = "Caisse PT", NameAr = "Caisse PT", NameEn = "Caisse PT" }).Entity;
        var oranBox = new CashBox { BranchId = oran.Id, NameFr = "Caisse Oran PT", NameAr = "Caisse Oran PT", NameEn = "Caisse Oran PT" };
        db.CashBoxes.Add(oranBox);
        Employee Hire(string number) => new()
        {
            Number = number, FullName = $"Worker {number}", BranchId = main, HiredOn = new DateOnly(2020, 1, 1), BaseRate = 30000m,
        };
        var e1 = Hire("EMP-PT-01");
        var e2 = Hire("EMP-PT-02");
        var e3 = Hire("EMP-PT-03");
        db.Employees.AddRange(e1, e2, e3);
        await db.SaveChangesAsync();
        return new Company(main, oran.Id, mainBox.Id, oranBox.Id, e1.Id, e2.Id, e3.Id);
    }

    private static DateTime Noon(int day) => new(2026, 3, day, 12, 0, 0, DateTimeKind.Utc);

    // ---------- payroll › advances ----------

    /// <summary>
    /// Twelve advances on 1–12 March: E1 has 1–6, E2 7–10, E3 11–12; the third recovered, the fourth written off; an
    /// even one paid by bank, an odd one in cash from the main branch's box, the fifth from Oran's (inter-branch).
    /// </summary>
    private static async Task SeedAdvancesAsync(PosHost host, Company c)
    {
        await using var db = Db(host);
        for (var i = 1; i <= 12; i++)
        {
            var amount = 1000m * i;
            db.WageAdvances.Add(new WageAdvance
            {
                Number = $"ADV-PT-{i:00}",
                EmployeeId = i <= 6 ? c.E1 : i <= 10 ? c.E2 : c.E3,
                BranchId = c.Main,
                Amount = amount,
                Recovered = i == 2 ? 500m : i == 3 ? amount : 0m,
                Status = i == 3 ? WageAdvanceStatus.Recovered : i == 4 ? WageAdvanceStatus.WrittenOff : WageAdvanceStatus.Open,
                PayoutMethod = i % 2 == 0 ? PayoutMethod.Bank : PayoutMethod.Cash,
                CashBoxId = i % 2 == 0 ? null : i == 5 ? c.OranBox : c.MainBox,
                GrantedAtUtc = Noon(i),
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Advances_ten_first_newest_first_and_the_screens_filters_on_the_server()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var c = await CompanyAsync(host);
        await SeedAdvancesAsync(host, c);

        var first = await PageAsync(client, "/api/hr/advances/page");
        Assert.Equal(12, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        Assert.Equal("ADV-PT-12", Strings(first, "number")[0]);

        var past = await PageAsync(client, "/api/hr/advances/page?skip=90&take=10");
        Assert.Equal(10, past.GetProperty("skip").GetInt32());
        Assert.Equal(["ADV-PT-02", "ADV-PT-01"], Strings(past, "number"));

        Assert.Equal(10, Total(await PageAsync(client, "/api/hr/advances/page?status=Open")));
        Assert.Equal(6, Total(await PageAsync(client, "/api/hr/advances/page?method=Bank")));
        Assert.Equal(["ADV-PT-05"], Strings(await PageAsync(client, "/api/hr/advances/page?interBranch=yes"), "number"));
        Assert.Equal(11, Total(await PageAsync(client, "/api/hr/advances/page?interBranch=no")));
        // Granted between the 3rd and the 5th, both included, on the company's day.
        Assert.Equal(["ADV-PT-05", "ADV-PT-04", "ADV-PT-03"],
            Strings(await PageAsync(client, "/api/hr/advances/page?from=2026-03-03&to=2026-03-05"), "number"));
        // Filters combine, and the branch is the base route's own.
        Assert.Equal(["ADV-PT-05"], Strings(await PageAsync(client, "/api/hr/advances/page?method=Cash&interBranch=yes&status=Open"), "number"));
        Assert.Equal(0, Total(await PageAsync(client, $"/api/hr/advances/page?branchId={c.Oran}")));
    }

    [Fact]
    public async Task Who_owes_is_grouped_per_person_largest_first_with_the_tiles_over_every_open_advance()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var c = await CompanyAsync(host);
        await SeedAdvancesAsync(host, c);

        var owing = await PageAsync(client, "/api/hr/advances/owing/page");
        // E1: 1000 + (2000 − 500) + 5000 + 6000 = 13 500 over 4; E2: 7…10 thousand = 34 000 over 4; E3: 23 000 over 2.
        Assert.Equal(["EMP-PT-02", "EMP-PT-03", "EMP-PT-01"], Strings(owing, "employeeNumber"));
        var rows = owing.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([34000m, 23000m, 13500m], rows.Select(r => Number(r, "outstanding")));
        Assert.Equal([4, 2, 4], rows.Select(r => r.GetProperty("count").GetInt32()));
        var summary = owing.GetProperty("summary");
        Assert.Equal(70500m, Number(summary, "outstandingTotal"));
        Assert.Equal(3, summary.GetProperty("peopleOwing").GetInt32());

        // A page of one still carries the tiles over everyone.
        var one = await PageAsync(client, "/api/hr/advances/owing/page?take=1&skip=2");
        Assert.Equal(["EMP-PT-01"], Strings(one, "employeeNumber"));
        Assert.Equal(70500m, Number(one.GetProperty("summary"), "outstandingTotal"));

        // Only Open is owed: asking for the recovered ones leaves nobody owing, as the screen's tiles did.
        var recovered = await PageAsync(client, "/api/hr/advances/owing/page?status=Recovered");
        Assert.Equal(0, Total(recovered));
        Assert.Equal(0m, Number(recovered.GetProperty("summary"), "outstandingTotal"));
    }

    // ---------- payroll › runs ----------

    [Fact]
    public async Task Runs_page_with_the_totals_row_summed_over_every_run_the_filters_keep()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var c = await CompanyAsync(host);
        await using (var db = Db(host))
        {
            for (var m = 1; m <= 12; m++)
                db.PayrollRuns.Add(new PayrollRun
                {
                    Number = $"RUN-PT-{m:00}", BranchId = c.Main, PeriodYear = 2026, PeriodMonth = m,
                    Status = m <= 3 ? PayrollRunStatus.Paid : PayrollRunStatus.Draft,
                    HeadCount = m, Gross = 100m * m, Net = 80m * m,
                });
            db.PayrollRuns.Add(new PayrollRun
            {
                Number = "RUN-PT-OR", BranchId = c.Oran, PeriodYear = 2026, PeriodMonth = 1, Status = PayrollRunStatus.Draft,
                HeadCount = 5, Gross = 500m, Net = 400m,
            });
            db.PayrollRuns.Add(new PayrollRun
            {
                Number = "RUN-PT-25", BranchId = c.Main, PeriodYear = 2025, PeriodMonth = 12, Status = PayrollRunStatus.Paid,
                HeadCount = 9, Gross = 900m, Net = 700m,
            });
            await db.SaveChangesAsync();
        }

        var year = await PageAsync(client, "/api/hr/payroll/runs/page?year=2026");
        Assert.Equal(13, Total(year));
        Assert.Equal(10, year.GetProperty("items").GetArrayLength());
        Assert.Equal("RUN-PT-12", Strings(year, "number")[0]);
        // 1 + … + 12 = 78 heads, plus Oran's 5; gross 7 800 + 500; net 6 240 + 400.
        var totals = year.GetProperty("summary");
        Assert.Equal(83, totals.GetProperty("headCount").GetInt32());
        Assert.Equal(8300m, Number(totals, "gross"));
        Assert.Equal(6640m, Number(totals, "net"));

        var paid = await PageAsync(client, "/api/hr/payroll/runs/page?year=2026&status=Paid");
        Assert.Equal(["RUN-PT-03", "RUN-PT-02", "RUN-PT-01"], Strings(paid, "number"));
        Assert.Equal(6, paid.GetProperty("summary").GetProperty("headCount").GetInt32());
        Assert.Equal(600m, Number(paid.GetProperty("summary"), "gross"));
        Assert.Equal(480m, Number(paid.GetProperty("summary"), "net"));

        Assert.Equal(["RUN-PT-OR"], Strings(await PageAsync(client, $"/api/hr/payroll/runs/page?year=2026&branchId={c.Oran}"), "number"));
        Assert.Equal(3, (await PageAsync(client, "/api/hr/payroll/runs/page?year=2026&skip=90")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_runs_payments_page_with_the_method_and_standing_filters()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var c = await CompanyAsync(host);
        Guid runId;
        await using (var db = Db(host))
        {
            var run = new PayrollRun { Number = "RUN-PT-WP", BranchId = c.Main, PeriodYear = 2026, PeriodMonth = 2, Status = PayrollRunStatus.Approved };
            db.PayrollRuns.Add(run);
            for (var i = 1; i <= 12; i++)
                db.WagePayments.Add(new WagePayment
                {
                    Number = $"WP-PT-{i:00}", RunId = run.Id, BranchId = c.Main, PaidAtUtc = Noon(i), Amount = 100m * i,
                    PayoutMethod = i % 3 == 0 ? PayoutMethod.Bank : PayoutMethod.Cash, CashBoxId = i % 3 == 0 ? null : c.MainBox,
                });
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        var first = await PageAsync(client, $"/api/hr/payroll/runs/{runId}/payments/page");
        Assert.Equal(12, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        Assert.Equal("WP-PT-12", first.GetProperty("items")[0].GetProperty("payment").GetProperty("number").GetString());
        Assert.Equal(4, Total(await PageAsync(client, $"/api/hr/payroll/runs/{runId}/payments/page?method=Bank")));
        Assert.Equal(12, Total(await PageAsync(client, $"/api/hr/payroll/runs/{runId}/payments/page?state=standing")));
        Assert.Equal(0, Total(await PageAsync(client, $"/api/hr/payroll/runs/{runId}/payments/page?state=reversed")));
    }

    // ---------- payroll › statutory payments ----------

    [Fact]
    public async Task Statutory_payments_page_with_account_year_method_and_day_filters()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var c = await CompanyAsync(host);
        string[] codes = ["431", "442", "447"];
        await using (var db = Db(host))
        {
            for (var i = 1; i <= 12; i++)
                db.PayrollStatutoryPayments.Add(new PayrollStatutoryPayment
                {
                    BranchId = c.Main, AccountCode = codes[(i - 1) % 3], Amount = 1000m * i, PaidAtUtc = Noon(i),
                    PayoutMethod = i % 4 == 0 ? PayoutMethod.Cash : PayoutMethod.Bank, CashBoxId = i % 4 == 0 ? c.MainBox : null,
                    PeriodYear = i <= 8 ? 2026 : 2025, PeriodMonth = 1, Reference = $"ST-PT-{i:00}",
                });
            await db.SaveChangesAsync();
        }

        var all = await PageAsync(client, "/api/hr/payroll/statutory-payments/page");
        Assert.Equal(12, Total(all));
        Assert.Equal("ST-PT-12", Strings(all, "reference")[0]);
        Assert.Equal(8, Total(await PageAsync(client, "/api/hr/payroll/statutory-payments/page?year=2026")));
        Assert.Equal(4, Total(await PageAsync(client, "/api/hr/payroll/statutory-payments/page?accountCode=431")));
        Assert.Equal(3, Total(await PageAsync(client, "/api/hr/payroll/statutory-payments/page?method=Cash")));
        Assert.Equal(["ST-PT-06", "ST-PT-05"],
            Strings(await PageAsync(client, "/api/hr/payroll/statutory-payments/page?from=2026-03-05&to=2026-03-06"), "reference"));
        Assert.Equal(["ST-PT-04"],
            Strings(await PageAsync(client, "/api/hr/payroll/statutory-payments/page?year=2026&method=Cash&accountCode=431"), "reference"));
    }

    // ---------- attendance › overtime ----------

    [Fact]
    public async Task Overtime_entries_page_with_the_hours_per_kind_over_every_entry_kept()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var c = await CompanyAsync(host);
        await using (var db = Db(host))
        {
            for (var day = 1; day <= 6; day++)
            {
                db.OvertimeEntries.Add(new OvertimeEntry { EmployeeId = c.E1, WorkDate = new DateOnly(2026, 3, day), RuleKey = "hs-jour", Hours = 1.5m });
                db.OvertimeEntries.Add(new OvertimeEntry { EmployeeId = c.E2, WorkDate = new DateOnly(2026, 3, day), RuleKey = "hs-nuit", Hours = 2m });
            }
            await db.SaveChangesAsync();
        }

        var month = await PageAsync(client, $"/api/hr/attendance/overtime/page?branchId={c.Main}&year=2026&month=3");
        Assert.Equal(12, Total(month));
        Assert.Equal(10, month.GetProperty("items").GetArrayLength());
        Assert.False(month.GetProperty("summary").GetProperty("locked").GetBoolean());
        AssertTotals(month, new() { ["hs-jour"] = 9m, ["hs-nuit"] = 12m });

        var one = await PageAsync(client, $"/api/hr/attendance/overtime/page?branchId={c.Main}&year=2026&month=3&employeeId={c.E1}");
        Assert.Equal(6, Total(one));
        AssertTotals(one, new() { ["hs-jour"] = 9m, ["hs-nuit"] = 0m });

        Assert.Equal(2, (await PageAsync(client, $"/api/hr/attendance/overtime/page?branchId={c.Main}&year=2026&month=3&skip=90")).GetProperty("items").GetArrayLength());
    }

    /// <summary>Every kind the settings define is totalled; those seeded here carry their hand-summed hours.</summary>
    private static void AssertTotals(JsonElement page, Dictionary<string, decimal> expected)
    {
        var summary = page.GetProperty("summary");
        var kinds = summary.GetProperty("kinds").EnumerateArray().Select(k => k.GetProperty("key").GetString()!).ToList();
        var totals = summary.GetProperty("totals").EnumerateArray()
            .ToDictionary(t => t.GetProperty("key").GetString()!, t => Number(t, "hours"));
        Assert.Equal(kinds, totals.Keys.ToList());
        foreach (var (key, hours) in totals)
            Assert.Equal(expected.GetValueOrDefault(key), hours);
    }

    // ---------- attendance › clock and review ----------

    /// <summary>
    /// March 2026: E1 in at 08:00 and out at 16:00 UTC on the 2nd to the 6th, and in alone on the 9th; E2 in and out on
    /// the 2nd and the 3rd. Fifteen punches, one without a partner.
    /// </summary>
    private static async Task SeedPunchesAsync(PosHost host, Company c)
    {
        await using var db = Db(host);
        void Pair(Guid who, int day)
        {
            db.ClockEvents.Add(new ClockEvent { EmployeeId = who, AtUtc = new DateTime(2026, 3, day, 8, 0, 0, DateTimeKind.Utc), Kind = ClockEventKind.In });
            db.ClockEvents.Add(new ClockEvent { EmployeeId = who, AtUtc = new DateTime(2026, 3, day, 16, 0, 0, DateTimeKind.Utc), Kind = ClockEventKind.Out });
        }
        for (var day = 2; day <= 6; day++) Pair(c.E1, day);
        db.ClockEvents.Add(new ClockEvent { EmployeeId = c.E1, AtUtc = new DateTime(2026, 3, 9, 8, 0, 0, DateTimeKind.Utc), Kind = ClockEventKind.In });
        Pair(c.E2, 2);
        Pair(c.E2, 3);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Clock_log_pages_with_its_person_and_partner_and_the_month_counts_in_the_summary()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var c = await CompanyAsync(host);
        await SeedPunchesAsync(host, c);
        var route = $"/api/hr/attendance/clock/page?branchId={c.Main}&year=2026&month=3";

        var first = await PageAsync(client, route);
        Assert.Equal(15, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        var summary = first.GetProperty("summary");
        Assert.Equal(15, summary.GetProperty("events").GetInt32());
        Assert.Equal(1, summary.GetProperty("unpaired").GetInt32());
        Assert.Contains("Worker EMP-PT-01", Strings(first, "employeeName"));

        Assert.Equal(8, Total(await PageAsync(client, route + "&kind=In")));
        var lone = await PageAsync(client, route + "&pair=unpaired");
        Assert.Equal(1, Total(lone));
        Assert.True(lone.GetProperty("items")[0].GetProperty("unpaired").GetBoolean());
        Assert.Equal(14, Total(await PageAsync(client, route + "&pair=paired")));
        // The 3rd and the 4th on the company's clock: E1's two pairs and E2's one.
        Assert.Equal(6, Total(await PageAsync(client, route + "&from=2026-03-03&to=2026-03-04")));
        // The counts stay the month's, whatever the filters.
        Assert.Equal(15, (await PageAsync(client, route + "&kind=Out")).GetProperty("summary").GetProperty("events").GetInt32());
        Assert.Equal(5, (await PageAsync(client, route + "&skip=10")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Review_filters_by_person_and_by_the_points_the_column_shows()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var c = await CompanyAsync(host);
        await SeedPunchesAsync(host, c);
        var route = $"/api/hr/attendance/review/page?branchId={c.Main}&year=2026&month=3&flagged=false";

        var all = await PageAsync(client, route + "&take=100");
        var days = all.GetProperty("items").EnumerateArray().ToList();
        // E1 on the 2nd–6th and the 9th, E2 on the 2nd and the 3rd.
        Assert.Equal(8, days.Count);
        Assert.Equal(8, all.GetProperty("summary").GetProperty("days").GetInt32());
        Assert.Equal(2, all.GetProperty("summary").GetProperty("people").GetArrayLength());

        Assert.Equal(2, Total(await PageAsync(client, route + $"&person={c.E2}")));
        var missing = await PageAsync(client, route + "&flag=missingOut");
        Assert.Equal(["2026-03-09"], Strings(missing, "workDate"));

        // "None" and the points split the month between them, exactly as the column reads each day.
        static bool Flagged(JsonElement d) =>
            d.GetProperty("lateMinutes").GetInt32() > 0 || d.GetProperty("earlyLeaveMinutes").GetInt32() > 0
            || d.GetProperty("missingCheckOut").GetBoolean() || d.GetProperty("possibleOvertimeMinutes").GetInt32() > 0
            || d.GetProperty("workedOnRestDay").GetBoolean();
        Assert.Equal(days.Count(d => !Flagged(d)), Total(await PageAsync(client, route + "&flag=none")));
        Assert.Equal(days.Count(d => d.GetProperty("lateMinutes").GetInt32() > 0), Total(await PageAsync(client, route + "&flag=late")));
        // The day count in the summary is the month's, before the filters.
        Assert.Equal(8, missing.GetProperty("summary").GetProperty("days").GetInt32());
    }

    // ---------- administration ----------

    [Fact]
    public async Task Users_page_by_username_with_role_branch_state_and_link_filters()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var c = await CompanyAsync(host);
        Guid foreman;
        int before, beforeCashiers, beforeInactive, beforeNoBranch, beforeLinked;
        before = Total(await PageAsync(client, "/api/users/page"));
        beforeCashiers = Total(await PageAsync(client, "/api/users/page?role=Cashier"));
        beforeInactive = Total(await PageAsync(client, "/api/users/page?state=inactive"));
        beforeNoBranch = Total(await PageAsync(client, "/api/users/page?branch=none"));
        beforeLinked = Total(await PageAsync(client, "/api/users/page?link=linked"));
        await using (var db = Db(host))
        {
            var role = new Role { Name = "Chef PT", NormalizedName = "CHEF PT", Scope = RoleScope.AssignedBranch, IsSystem = false, Version = 1 };
            db.Roles.Add(role);
            for (var i = 1; i <= 12; i++)
                db.Users.Add(new User
                {
                    Username = $"pt-user-{i:00}", FullName = $"PT {i:00}", Role = UserRole.Cashier, RoleId = SystemRoles.CashierId,
                    BranchId = i == 12 ? null : c.Main, IsActive = i % 4 != 0, EmployeeId = i == 1 ? c.E1 : null,
                });
            db.Users.Add(new User { Username = "pt-user-chef", FullName = "PT Chef", Role = UserRole.Manager, RoleId = role.Id, BranchId = c.Main });
            await db.SaveChangesAsync();
            foreman = role.Id;
        }

        var all = await PageAsync(client, "/api/users/page");
        Assert.Equal(before + 13, Total(all));
        Assert.True(all.GetProperty("items").GetArrayLength() <= 10);
        Assert.Equal(beforeCashiers + 12, Total(await PageAsync(client, "/api/users/page?role=Cashier")));
        Assert.Equal(["pt-user-chef"], Strings(await PageAsync(client, $"/api/users/page?role={foreman}"), "username"));
        // On a company role, the account is not counted under the role word its scope reads as.
        Assert.DoesNotContain("pt-user-chef", Strings(await PageAsync(client, "/api/users/page?role=Manager&take=100"), "username"));
        Assert.Equal(beforeInactive + 3, Total(await PageAsync(client, "/api/users/page?state=inactive")));
        Assert.Equal(beforeNoBranch + 1, Total(await PageAsync(client, "/api/users/page?branch=none")));
        Assert.Equal(beforeLinked + 1, Total(await PageAsync(client, "/api/users/page?link=linked")));
        Assert.Equal(["pt-user-04", "pt-user-08"],
            Strings(await PageAsync(client, $"/api/users/page?role=Cashier&state=inactive&branch={c.Main}"), "username")
                .Where(u => u.StartsWith("pt-user", StringComparison.Ordinal)).ToArray());

        // The role filter offers the roles the accounts are on, a company role by its id and name.
        var options = all.GetProperty("summary").GetProperty("roles").EnumerateArray()
            .Select(o => (o.GetProperty("value").GetString(), o.GetProperty("name").ValueKind == JsonValueKind.Null ? null : o.GetProperty("name").GetString()))
            .ToList();
        Assert.Contains((foreman.ToString(), "Chef PT"), options);
        Assert.Contains(("Cashier", (string?)null), options);
        Assert.Contains(c.Main.ToString(), all.GetProperty("summary").GetProperty("branches").EnumerateArray().Select(b => b.GetString()));
    }

    [Fact]
    public async Task Roles_page_with_scope_state_and_kind_filters_and_one_role_by_id()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var beforeCustom = Total(await PageAsync(client, "/api/roles/page?kind=custom"));
        var system = Total(await PageAsync(client, "/api/roles/page?kind=system"));
        Guid paused;
        await using (var db = Db(host))
        {
            for (var i = 1; i <= 11; i++)
                db.Roles.Add(new Role
                {
                    Name = $"PT role {i:00}", NormalizedName = $"PT ROLE {i:00}", Scope = i <= 3 ? RoleScope.TillBranch : RoleScope.AssignedBranch,
                    IsActive = i != 11, IsSystem = false, Version = 1,
                });
            await db.SaveChangesAsync();
            paused = (await db.Roles.SingleAsync(r => r.Name == "PT role 11")).Id;
        }

        var first = await PageAsync(client, "/api/roles/page");
        Assert.Equal(system + beforeCustom + 11, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        // The system roles first, as the list route orders them.
        Assert.True(first.GetProperty("items")[0].GetProperty("isSystem").GetBoolean());
        Assert.Equal(beforeCustom + 11, Total(await PageAsync(client, "/api/roles/page?kind=custom")));
        Assert.Contains("PT role 01", Strings(await PageAsync(client, "/api/roles/page?scope=TillBranch&kind=custom&take=100"), "name"));
        Assert.DoesNotContain("PT role 04", Strings(await PageAsync(client, "/api/roles/page?scope=TillBranch&take=100"), "name"));
        Assert.Contains("PT role 11", Strings(await PageAsync(client, "/api/roles/page?state=inactive&take=100"), "name"));
        Assert.Equal(["PT role 11"], Strings(await PageAsync(client, $"/api/roles/page?id={paused}&take=1"), "name"));
    }

    [Fact]
    public async Task Branches_page_with_kind_and_state_filters_and_the_mains_beside_any_page()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var before = await PageAsync(client, "/api/catalog/branches/page?take=100");
        var beforeSetup = Total(await PageAsync(client, "/api/catalog/branches/page?state=setup"));
        var beforeInactive = Total(await PageAsync(client, "/api/catalog/branches/page?state=inactive"));
        var beforeWarehouses = Total(await PageAsync(client, "/api/catalog/branches/page?kind=warehouse"));
        await using (var db = Db(host))
        {
            for (var i = 1; i <= 11; i++)
                db.Branches.Add(new Branch
                {
                    Code = $"8{i:00}", NameFr = $"PT site {i:00}", NameAr = $"PT site {i:00}", NameEn = $"PT site {i:00}",
                    IsWarehouse = i == 11, IsActive = i != 10, IsOpenForOperation = i > 2,
                });
            await db.SaveChangesAsync();
        }

        var first = await PageAsync(client, "/api/catalog/branches/page");
        Assert.Equal(Total(before) + 11, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        // The main ones first, as the list route orders them, and both mains beside every page.
        Assert.True(first.GetProperty("items")[0].GetProperty("isMain").GetBoolean());
        var mains = first.GetProperty("summary").GetProperty("mains");
        Assert.Equal(before.GetProperty("summary").GetProperty("mains").GetArrayLength(), mains.GetArrayLength());
        Assert.All(mains.EnumerateArray(), m => Assert.True(m.GetProperty("isMain").GetBoolean()));
        var last = await PageAsync(client, "/api/catalog/branches/page?skip=90&take=10");
        Assert.Equal(mains.GetArrayLength(), last.GetProperty("summary").GetProperty("mains").GetArrayLength());

        Assert.Equal(beforeWarehouses + 1, Total(await PageAsync(client, "/api/catalog/branches/page?kind=warehouse")));
        Assert.Equal(beforeInactive + 1, Total(await PageAsync(client, "/api/catalog/branches/page?state=inactive")));
        Assert.Equal(beforeSetup + 2, Total(await PageAsync(client, "/api/catalog/branches/page?state=setup")));
    }

    [Fact]
    public async Task Devices_page_by_code_with_branch_state_and_sync_filters()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var c = await CompanyAsync(host);
        var before = Total(await PageAsync(client, "/api/devices/page"));
        var beforeNever = Total(await PageAsync(client, "/api/devices/page?sync=never"));
        var beforeInactive = Total(await PageAsync(client, "/api/devices/page?state=inactive"));
        await using (var db = Db(host))
        {
            for (var i = 1; i <= 12; i++)
                db.Devices.Add(new Device
                {
                    Code = $"PT{i:00}", Name = $"Caisse PT {i:00}", BranchId = i <= 4 ? c.Oran : c.Main,
                    IsActive = i % 3 != 0, LastSyncAtUtc = i <= 5 ? Noon(i) : null,
                });
            await db.SaveChangesAsync();
        }

        var first = await PageAsync(client, "/api/devices/page");
        Assert.Equal(before + 12, Total(first));
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());
        Assert.Equal(["PT01", "PT02", "PT03", "PT04"], Strings(await PageAsync(client, $"/api/devices/page?branchId={c.Oran}"), "code"));
        Assert.Equal(beforeNever + 7, Total(await PageAsync(client, "/api/devices/page?sync=never")));
        Assert.Equal(beforeInactive + 4, Total(await PageAsync(client, "/api/devices/page?state=inactive")));
        Assert.Equal(["PT03"], Strings(await PageAsync(client, $"/api/devices/page?branchId={c.Oran}&state=inactive&sync=synced"), "code"));
    }
}
