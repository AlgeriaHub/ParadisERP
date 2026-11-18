using System.Security.Claims;
using System.Text.Json;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Maintenance;
using AlgeriaPos.Tests.Maintenance;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Maintenance.MaintenanceWorld;

namespace AlgeriaPos.Tests.Paging;

/// <summary>
/// "I need to paginate in backend, not frontend" (owner, 2026-11-17): every maintenance list is asked a page at a time — ten
/// rows to begin with, 10/20/50/100 — through <c>&lt;its list route&gt;/page</c>, with the screen's filters run on the server over
/// every row of the reader's scope, and the figures over the list (tiles, totals lines, account totals, the filters' choices)
/// in the page's summary, equal to what the list route answers.
/// </summary>
public class MaintenancePageTests
{
    private const string Jobs = "/api/maintenance/jobs/page";
    private const string WaitingLines = "/api/maintenance/waiting/lines/page";
    private const string WaitingBuckets = "/api/maintenance/waiting/buckets/page";
    private const string HistoryJobs = "/api/maintenance/history/jobs/page";
    private const string HistoryPieces = "/api/maintenance/history/pieces/page";
    private const string Workshop = "/api/maintenance/workshop/page";
    private const string RepairRates = "/api/maintenance/repair-rates/page";
    private const string Hours = "/api/maintenance/jobs/{id:guid}/hours/page";
    private const string Recharges = "/api/maintenance/reports/recharges/page";
    private const string Products = "/api/maintenance/reports/products/page";
    private const string Profit = "/api/maintenance/reports/profit/page";
    private const string Ledger = "/api/maintenance/practice-ledger";

    private static readonly DateTime March = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private static async Task<JsonElement> GetAsync(MaintenanceWorld w, ClaimsPrincipal caller, string pattern, string query = "",
        params (string Key, string Value)[] route)
    {
        var (status, body) = await w.CallAsync(caller, "GET", pattern, query, route);
        w.Db.ChangeTracker.Clear();
        Assert.True(status == 200, $"GET {pattern}{query}: {status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static int Total(JsonElement page) => page.GetProperty("total").GetInt32();

    private static string[] Texts(JsonElement page, string field) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(field).GetString()!).ToArray();

    private static decimal Num(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    private static JsonElement Summary(JsonElement page) => page.GetProperty("summary");

    private static string[] BranchNames(JsonElement summary, string field = "branches") =>
        summary.GetProperty(field).EnumerateArray().Select(b => b.GetProperty("name").GetString()!).ToArray();

    private static Task<ClaimsPrincipal> AdminAsync(MaintenanceWorld w) => w.CallerAsync(UserRole.Admin, Algiers, Chef);

    // ---------- Jobs ----------

    /// <summary>
    /// 25 jobs MJ-000001…MJ-000025, one a day from 1 March 2026 at 10:00 UTC — MJ-000015 at 23:30 UTC on 14 March, the 15th in
    /// Algiers. MJ-000001…3 completed, MJ-000004 cancelled, the rest open; every fifth a customer's "Bétonnière 350 L", the
    /// others a scaffold; MJ-000023…25 at Oran, the rest at Algiers.
    /// </summary>
    private static async Task SeedJobsAsync(MaintenanceWorld w)
    {
        for (var i = 1; i <= 25; i++)
        {
            var customer = i % 5 == 0;
            w.Db.MaintenanceJobs.Add(new MaintenanceJob
            {
                Number = MaintenanceJobs.Number(i), BranchId = i >= 23 ? Oran : Algiers,
                Kind = customer ? MaintenanceJobKind.Customer : MaintenanceJobKind.Internal,
                Status = i <= 3 ? MaintenanceJobStatus.Completed : i == 4 ? MaintenanceJobStatus.Cancelled : MaintenanceJobStatus.Open,
                ProductId = customer ? null : Scaffold, Qty = 1m,
                CustomerId = customer ? Contractor : null, Equipment = customer ? "Bétonnière 350 L" : null,
                ClientRequestId = $"page-{i}",
                CreatedAtUtc = i == 15 ? new DateTime(2026, 3, 14, 23, 30, 0, DateTimeKind.Utc) : March.AddDays(i - 1),
            });
        }
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Jobs_ten_rows_first_newest_first_with_the_total_of_every_job_and_a_page_past_the_end()
    {
        await using var w = await NewAsync(On());
        await SeedJobsAsync(w);
        var admin = await AdminAsync(w);

        var first = await GetAsync(w, admin, Jobs);
        Assert.Equal((25, 0, 10), (Total(first), first.GetProperty("skip").GetInt32(), first.GetProperty("take").GetInt32()));
        Assert.Equal(Enumerable.Range(16, 10).Reverse().Select(MaintenanceJobs.Number), Texts(first, "number"));
        // The branch filter offers every branch the jobs belong to, before any filter.
        Assert.Equal(["Alger-Centre", "Oran"], BranchNames(Summary(first)));

        var third = await GetAsync(w, admin, Jobs, "?skip=20&take=10");
        Assert.Equal(["MJ-000005", "MJ-000004", "MJ-000003", "MJ-000002", "MJ-000001"], Texts(third, "number"));

        // A page past the end, after a filter shrank the list, is answered with the last page.
        var past = await GetAsync(w, admin, Jobs, "?skip=90&take=20");
        Assert.Equal((20, 5), (past.GetProperty("skip").GetInt32(), past.GetProperty("items").GetArrayLength()));
        // Never more than a hundred rows at once; every job is reachable, not only the 500 newest the list route reads.
        Assert.Equal(100, (await GetAsync(w, admin, Jobs, "?take=5000")).GetProperty("take").GetInt32());
    }

    [Fact]
    public async Task Jobs_filters_run_on_the_server_over_every_job()
    {
        await using var w = await NewAsync(On());
        await SeedJobsAsync(w);
        var admin = await AdminAsync(w);

        Assert.Equal(["MJ-000003", "MJ-000002", "MJ-000001"], Texts(await GetAsync(w, admin, Jobs, "?status=Completed"), "number"));
        Assert.Equal(5, Total(await GetAsync(w, admin, Jobs, "?kind=Customer")));
        Assert.Equal(0, Total(await GetAsync(w, admin, Jobs, "?status=NotAStatus")));
        // The words, folded as the till folds them: no accent, any case.
        Assert.Equal(5, Total(await GetAsync(w, admin, Jobs, "?q=BETONNIERE")));
        Assert.Equal(20, Total(await GetAsync(w, admin, Jobs, "?q=echafaudage")));
        Assert.Equal(["MJ-000012"], Texts(await GetAsync(w, admin, Jobs, "?q=mj-000012"), "number"));
        // The day opened, on the company's clock.
        Assert.Equal(["MJ-000012", "MJ-000011", "MJ-000010"], Texts(await GetAsync(w, admin, Jobs, "?from=2026-03-10&to=2026-03-12"), "number"));
        Assert.Equal(["MJ-000015"], Texts(await GetAsync(w, admin, Jobs, "?from=2026-03-15&to=2026-03-15"), "number"));
        Assert.Equal(["MJ-000014"], Texts(await GetAsync(w, admin, Jobs, "?from=2026-03-14&to=2026-03-14"), "number"));
        // The Admin's branch, alone and with another filter.
        Assert.Equal(["MJ-000025", "MJ-000024", "MJ-000023"], Texts(await GetAsync(w, admin, Jobs, $"?branch={Oran}"), "number"));
        Assert.Equal(["MJ-000025"], Texts(await GetAsync(w, admin, Jobs, $"?branch={Oran}&kind=Customer"), "number"));
        // The branch choices stay every branch whatever is filtered.
        Assert.Equal(["Alger-Centre", "Oran"], BranchNames(Summary(await GetAsync(w, admin, Jobs, $"?branch={Oran}"))));
    }

    [Fact]
    public async Task A_branch_reader_s_jobs_page_is_their_own_branch_whatever_the_request_names()
    {
        await using var w = await NewAsync(On());
        await SeedJobsAsync(w);
        var oran = await w.CallerAsync(UserRole.Manager, Oran, Second);

        var mine = await GetAsync(w, oran, Jobs, $"?branchId={Algiers}");
        Assert.Equal(["MJ-000025", "MJ-000024", "MJ-000023"], Texts(mine, "number"));
        Assert.Equal(["Oran"], BranchNames(Summary(mine)));
        // The screen's branch filter only narrows the reader's own branch.
        Assert.Equal(0, Total(await GetAsync(w, oran, Jobs, $"?branch={Algiers}")));
    }

    // ---------- Waiting for repair ----------

    [Fact]
    public async Task Waiting_lines_and_products_in_maintenance_page_and_filter_on_the_server()
    {
        await using var w = await NewAsync(On());
        // Twelve parts in maintenance at Algiers (two units each, PRT-01's held by an open job) and one at Oran.
        var parts = Enumerable.Range(1, 13).Select(i => new Product { Sku = $"PRT-{i:00}", NameFr = $"Pièce {i:00}", TrackStock = true }).ToList();
        w.Db.Products.AddRange(parts);
        for (var i = 0; i < 13; i++)
            w.Db.ProductStocks.Add(new ProductStock { ProductId = parts[i].Id, BranchId = i == 12 ? Oran : Algiers, MaintenanceQty = i == 12 ? 3m : 2m });
        w.Db.MaintenanceJobs.Add(new MaintenanceJob
        {
            Number = MaintenanceJobs.Number(1), BranchId = Algiers, Kind = MaintenanceJobKind.Internal, Status = MaintenanceJobStatus.Open,
            ProductId = parts[0].Id, Qty = 2m, ClientRequestId = "page-hold",
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        // Four scaffolds back damaged on 31 March, 15,000 charged at return: one line no job covers yet.
        await w.ReturnDamagedAsync(4m, 15_000m);
        var admin = await AdminAsync(w);

        var lines = await GetAsync(w, admin, WaitingLines);
        var line = Assert.Single(lines.GetProperty("items").EnumerateArray());
        Assert.Equal((4m, 0m, 4m, 15_000m), (Num(line, "damagedQty"), Num(line, "coveredQty"), Num(line, "openQty"), Num(line, "recharge")));
        Assert.Equal(["Alger-Centre", "Oran"], BranchNames(Summary(lines)));
        Assert.Equal(1, Total(await GetAsync(w, admin, WaitingLines, "?recharge=yes")));
        Assert.Equal(0, Total(await GetAsync(w, admin, WaitingLines, "?recharge=no")));
        Assert.Equal(1, Total(await GetAsync(w, admin, WaitingLines, "?to=2026-03-31")));
        Assert.Equal(0, Total(await GetAsync(w, admin, WaitingLines, "?from=2026-04-01")));
        Assert.Equal(0, Total(await GetAsync(w, admin, WaitingLines, $"?branch={Oran}")));

        // By branch, then SKU: the scaffold and PRT-01…09 first, then PRT-10…12 and Oran's PRT-13.
        var first = await GetAsync(w, admin, WaitingBuckets);
        Assert.Equal(14, Total(first));
        Assert.Equal(new[] { "ECH-01" }.Concat(Enumerable.Range(1, 9).Select(i => $"PRT-{i:00}")), Texts(first, "sku"));
        Assert.Equal(["PRT-10", "PRT-11", "PRT-12", "PRT-13"], Texts(await GetAsync(w, admin, WaitingBuckets, "?skip=10"), "sku"));
        var held = Assert.Single((await GetAsync(w, admin, WaitingBuckets, "?free=none")).GetProperty("items").EnumerateArray());
        Assert.Equal(("PRT-01", 2m, 2m, 0m), (held.GetProperty("sku").GetString(), Num(held, "inMaintenance"), Num(held, "heldByJobs"), Num(held, "free")));
        Assert.Equal(13, Total(await GetAsync(w, admin, WaitingBuckets, "?free=some")));
        Assert.Equal(["PRT-13"], Texts(await GetAsync(w, admin, WaitingBuckets, $"?branch={Oran}"), "sku"));

        // An Oran manager reads Oran's units only.
        var oran = await w.CallerAsync(UserRole.Manager, Oran, Second);
        var theirs = await GetAsync(w, oran, WaitingBuckets, $"?branchId={Algiers}");
        Assert.Equal(["PRT-13"], Texts(theirs, "sku"));
        Assert.Equal(["Oran"], BranchNames(Summary(theirs)));
        Assert.Equal(0, Total(await GetAsync(w, oran, WaitingLines)));
    }

    // ---------- Repair history ----------

    [Fact]
    public async Task History_pages_its_jobs_and_pieces_and_its_tiles_add_up_every_job_of_the_type_or_the_piece()
    {
        await using var w = await NewAsync(On());
        // Twelve completed jobs on the scaffold, on pieces P-01, P-02 and P-03 in turn, each with outside work of 100 × its number.
        for (var i = 1; i <= 12; i++)
        {
            var job = new MaintenanceJob
            {
                Number = MaintenanceJobs.Number(i), BranchId = Algiers, Kind = MaintenanceJobKind.Internal, Status = MaintenanceJobStatus.Completed,
                ProductId = Scaffold, Qty = 1m, RepairedQty = 1m, ItemReference = $"P-0{(i - 1) % 3 + 1}", ClientRequestId = $"page-h{i}",
                CreatedAtUtc = March.AddDays(i), OpenedAtUtc = March.AddDays(i), CompletedAtUtc = March.AddDays(i + 1),
            };
            w.Db.MaintenanceJobs.Add(job);
            w.Db.MaintenanceJobExpenses.Add(new MaintenanceJobExpense
            {
                JobId = job.Id, Description = "Soudure", Amount = 100m * i, ClientRequestId = $"page-x{i}", UserName = "chef", AtUtc = March.AddDays(i),
            });
        }
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var admin = await AdminAsync(w);
        var product = $"?productId={Scaffold}";

        var first = await GetAsync(w, admin, HistoryJobs, product);
        Assert.Equal(12, Total(first));
        Assert.Equal(Enumerable.Range(1, 10).Select(MaintenanceJobs.Number), Texts(first, "number"));
        Assert.Equal(["MJ-000011", "MJ-000012"], Texts(await GetAsync(w, admin, HistoryJobs, product + "&skip=10"), "number"));
        var history = Summary(first).GetProperty("history");
        // 100 × (1 + … + 12) = 7,800, over every job, not the page's ten.
        Assert.Equal(7_800m, Num(history, "outsideWork"));
        Assert.Empty(history.GetProperty("jobs").EnumerateArray());
        Assert.Equal(["P-01", "P-02", "P-03"], Summary(first).GetProperty("references").EnumerateArray().Select(r => r.GetString()!).ToArray());
        // The same figures as the history route.
        var whole = await GetAsync(w, admin, "/api/maintenance/history", product);
        Assert.Equal((Num(whole, "outsideWork"), Num(whole, "parts"), Num(whole, "labourHours")),
            (Num(history, "outsideWork"), Num(history, "parts"), Num(history, "labourHours")));

        var pieces = await GetAsync(w, admin, HistoryPieces, product);
        Assert.Equal(["P-01", "P-02", "P-03"], Texts(pieces, "reference"));
        Assert.Equal(4, pieces.GetProperty("items")[1].GetProperty("jobs").GetInt32());

        // One piece, its reference in any case: its four jobs, and its tiles over them: 200 + 500 + 800 + 1,100.
        var piece = await GetAsync(w, admin, HistoryJobs, product + "&reference=p-02");
        Assert.Equal(["MJ-000002", "MJ-000005", "MJ-000008", "MJ-000011"], Texts(piece, "number"));
        Assert.Equal(2_600m, Num(Summary(piece).GetProperty("history"), "outsideWork"));
    }

    // ---------- Workshop ----------

    [Fact]
    public async Task Workshop_list_pages_by_branch_and_name_and_filters_by_state_on_the_server()
    {
        await using var w = await NewAsync(On());
        // Employé 01…12 on Algiers' list (11 has since moved to Oran, 12 has left), Employé 13 on Oran's.
        for (var i = 1; i <= 13; i++)
        {
            var employee = new Employee
            {
                Number = $"E-{i:00}", FullName = $"Employé {i:00}", BranchId = i >= 11 && i != 12 ? Oran : Algiers,
                HiredOn = new DateOnly(2025, 1, 1), LeftOn = i == 12 ? new DateOnly(2026, 1, 31) : null, BaseRate = 40_000m,
            };
            w.Db.Employees.Add(employee);
            w.Db.MaintenanceWorkshopMembers.Add(new MaintenanceWorkshopMember { EmployeeId = employee.Id, BranchId = i == 13 ? Oran : Algiers, AddedBy = "admin" });
        }
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var admin = await AdminAsync(w);

        var first = await GetAsync(w, admin, Workshop);
        Assert.Equal(13, Total(first));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"Employé {i:00}"), Texts(first, "fullName"));
        Assert.Equal(["Employé 11", "Employé 12", "Employé 13"], Texts(await GetAsync(w, admin, Workshop, "?skip=10"), "fullName"));
        var summary = Summary(first);
        Assert.Equal((13, "AnyEmployeeOfBranch"), (summary.GetProperty("members").GetInt32(), summary.GetProperty("bookableEmployees").GetString()));
        Assert.Equal(["Alger-Centre", "Oran"], BranchNames(summary, "memberBranches"));

        Assert.Equal(11, Total(await GetAsync(w, admin, Workshop, "?state=bookable")));
        Assert.Equal(["Employé 11"], Texts(await GetAsync(w, admin, Workshop, "?state=moved"), "fullName"));
        Assert.Equal(["Employé 12"], Texts(await GetAsync(w, admin, Workshop, "?state=left"), "fullName"));
        Assert.Equal(["Employé 13"], Texts(await GetAsync(w, admin, Workshop, $"?branch={Oran}"), "fullName"));
        // Filters leave the summary whole.
        Assert.Equal(13, Summary(await GetAsync(w, admin, Workshop, "?state=left")).GetProperty("members").GetInt32());
    }

    // ---------- Repair rates ----------

    [Fact]
    public async Task Repair_rates_page_by_SKU_and_filter_by_folded_words_day_set_and_product()
    {
        await using var w = await NewAsync(On());
        var products = Enumerable.Range(1, 12).Select(i => new Product
        {
            Sku = $"LOC-{i:00}", NameFr = $"Échafaudage {i:00}", NameAr = "سقالة", IsRentable = true,
        }).ToList();
        w.Db.Products.AddRange(products);
        for (var i = 0; i < 12; i++)
            w.Db.MaintenanceRepairRates.Add(new MaintenanceRepairRate
            {
                ProductId = products[i].Id, RatePerUnit = 100m * (i + 1), UpdatedBy = "tarif", UpdatedAtUtc = March.AddDays(i),
            });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var admin = await AdminAsync(w);

        var first = await GetAsync(w, admin, RepairRates);
        Assert.Equal(12, Total(first));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"LOC-{i:00}"), Texts(first, "sku"));
        // The summary: MS-23 as saved, the rentable products a rate may be set for (the scaffold too), every rate counted.
        var summary = Summary(first);
        Assert.Equal(("TypedAtReturn", 12, 13),
            (summary.GetProperty("returnRecharge").GetString(), summary.GetProperty("rates").GetInt32(), summary.GetProperty("products").GetArrayLength()));

        // سقاله finds سقالة, echafaudage 07 finds Échafaudage 07.
        Assert.Equal(12, Total(await GetAsync(w, admin, RepairRates, "?q=" + Uri.EscapeDataString("سقاله"))));
        Assert.Equal(["LOC-07"], Texts(await GetAsync(w, admin, RepairRates, "?q=" + Uri.EscapeDataString("echafaudage 07")), "sku"));
        Assert.Equal(["LOC-05", "LOC-06"], Texts(await GetAsync(w, admin, RepairRates, "?from=2026-03-05&to=2026-03-06"), "sku"));
        var one = await GetAsync(w, admin, RepairRates, $"?productId={products[8].Id}&take=1");
        Assert.Equal(900m, Num(one.GetProperty("items")[0], "ratePerUnit"));
    }

    // ---------- A job's hours ----------

    [Fact]
    public async Task A_job_s_hours_page_newest_day_first_with_their_total_and_another_branch_s_job_is_not_found()
    {
        await using var w = await NewAsync(On());
        var karim = new Employee { Number = "E-001", FullName = "Karim Benali", BranchId = Algiers, HiredOn = new DateOnly(2025, 1, 1), BaseRate = 52_000m };
        var job = new MaintenanceJob
        {
            Number = MaintenanceJobs.Number(1), BranchId = Algiers, Kind = MaintenanceJobKind.Internal, Status = MaintenanceJobStatus.Open,
            ProductId = Scaffold, Qty = 1m, ClientRequestId = "page-hours",
        };
        w.Db.Employees.Add(karim);
        w.Db.MaintenanceJobs.Add(job);
        // Twelve days of October, i hours on the i-th: 78 hours in all.
        for (var i = 1; i <= 12; i++)
            w.Db.LaborBookings.Add(new LaborBooking
            {
                EmployeeId = karim.Id, Day = new DateOnly(2026, 10, i), OrderKind = LaborOrderKind.Maintenance, OrderId = job.Id, Hours = i,
                PayrollBranchId = Algiers, PayrollYear = 2026, PayrollMonth = 10, AttendanceHours = 12m, UserName = "chef",
            });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var admin = await AdminAsync(w);
        var id = ("id", job.Id.ToString());

        var first = await GetAsync(w, admin, Hours, "", id);
        Assert.Equal(12, Total(first));
        Assert.Equal(Enumerable.Range(3, 10).Reverse().Select(d => $"2026-10-{d:00}"), Texts(first, "day"));
        Assert.Equal(["2026-10-02", "2026-10-01"], Texts(await GetAsync(w, admin, Hours, "?skip=10", id), "day"));
        Assert.Equal((78m, "Open"), (Num(Summary(first), "totalHours"), Summary(first).GetProperty("jobStatus").GetString()));
        Assert.Empty(Summary(first).GetProperty("bookings").EnumerateArray());

        var oran = await w.CallerAsync(UserRole.Manager, Oran, Second);
        Assert.Equal(404, (await w.CallAsync(oran, "GET", Hours, "", id)).Status);
    }

    // ---------- Reports ----------

    /// <summary>Twelve scaffolds back damaged with 24,000 charged at return, each repaired by its own recharged job for 1,000 of outside work.</summary>
    private static async Task TwelveRechargedJobsAsync(MaintenanceWorld w)
    {
        var (_, _, lineId, _) = await w.ReturnDamagedAsync(12m, 24_000m);
        var parts = new MaintenanceJobParts(w.Db, new DzRegional());
        for (var i = 0; i < 12; i++)
        {
            var job = await w.OpenAsync(Recharged(lineId, 1m));
            var paid = await parts.AddExpenseAsync(job.Id, new MaintenanceJobExpenseRequest(Guid.NewGuid().ToString("N"), "Soudure", 1_000m, null), Chef, true);
            Assert.True(paid.Ok, paid.Error);
            w.Db.ChangeTracker.Clear();
        }
    }

    [Fact]
    public async Task The_reports_page_their_rows_and_their_totals_lines_add_up_the_whole_period()
    {
        await using var w = await NewAsync(On());
        await TwelveRechargedJobsAsync(w);
        var admin = await AdminAsync(w);

        // Recharge against cost: each job's share is 24,000 × 1/12 = 2,000; 24,000 − 12,000 = 12,000 over the period.
        var recharges = await GetAsync(w, admin, Recharges);
        Assert.Equal(12, Total(recharges));
        Assert.Equal(Enumerable.Range(1, 10).Select(MaintenanceJobs.Number), Texts(recharges, "number"));
        Assert.All(recharges.GetProperty("items").EnumerateArray(), r => Assert.Equal((2_000m, 1_000m), (Num(r, "recharge"), Num(r, "total"))));
        var totals = Summary(recharges);
        Assert.Equal((24_000m, 12_000m, 12_000m, "ByUnits"),
            (Num(totals, "recharge"), Num(totals, "total"), Num(totals, "difference"), totals.GetProperty("rechargeSharing").GetString()));
        Assert.Empty(totals.GetProperty("rows").EnumerateArray());
        var whole = await GetAsync(w, admin, "/api/maintenance/reports/recharges");
        Assert.Equal((Num(whole, "recharge"), Num(whole, "total"), Num(whole, "difference")), (Num(totals, "recharge"), Num(totals, "total"), Num(totals, "difference")));
        Assert.Equal(2, (await GetAsync(w, admin, Recharges, "?skip=10")).GetProperty("items").GetArrayLength());

        // Cost per equipment type: one type, its twelve jobs.
        var products = await GetAsync(w, admin, Products);
        var scaffold = Assert.Single(products.GetProperty("items").EnumerateArray());
        Assert.Equal((12, 12_000m, 24_000m), (scaffold.GetProperty("jobs").GetInt32(), Num(scaffold, "outsideWork"), Num(scaffold, "recharge")));
        Assert.Equal((12, 12_000m, 24_000m), (Summary(products).GetProperty("jobs").GetInt32(), Num(Summary(products), "outsideWork"), Num(Summary(products), "recharge")));

        // Company profit: twelve lines, 24,000 earned against 12,000 of cost; the breakdowns whole in the summary.
        var profit = await GetAsync(w, admin, Profit);
        Assert.Equal((12, 10), (Total(profit), profit.GetProperty("items").GetArrayLength()));
        var sums = Summary(profit);
        Assert.Equal((24_000m, 12_000m, 12_000m), (Num(sums, "revenue"), Num(sums, "cost"), Num(sums, "profit")));
        var kind = Assert.Single(sums.GetProperty("byKind").EnumerateArray());
        Assert.Equal(("Recharged", 12), (kind.GetProperty("key").GetString(), kind.GetProperty("jobs").GetInt32()));

        // Another period holds nothing, and a reversed period is refused as the report route refuses it.
        var january = await GetAsync(w, admin, Recharges, "?from=2026-01-01&to=2026-01-31");
        Assert.Equal((0, 0m), (Total(january), Num(Summary(january), "recharge")));
        var (status, body) = await w.CallAsync(admin, "GET", Profit, "?from=2026-10-31&to=2026-10-01");
        Assert.Equal(400, status);
        Assert.Equal(MaintenanceCosting.InvalidPeriodCode, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    // ---------- Practice ledger ----------

    [Fact]
    public async Task The_practice_ledger_pages_its_entries_and_keeps_the_month_s_figures_over_every_entry()
    {
        await using var w = await NewAsync(On(), inMaintenance: 6);
        var rod = new Product { Sku = "ROD-25", NameFr = "Baguette", NameAr = "قضيب", NameEn = "Rod", CostPrice = 40m, TrackStock = true,
            ManufacturingRole = ManufacturingRole.SparePart };
        w.Db.Products.Add(rod);
        w.Db.ProductStocks.Add(new ProductStock { ProductId = rod.Id, BranchId = Algiers, StockQty = 50m });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        // Six September jobs, each issuing two rods at 40 and giving one back: twelve entries, 6 × (80 + 40) = 720 debited.
        var parts = new MaintenanceJobParts(w.Db, new DzRegional());
        var september = new DateTime(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 6; i++)
        {
            var job = await w.OpenAsync(Internal(1));
            foreach (var (kind, qty) in new[] { ("Issue", 2m), ("Return", 1m) })
            {
                var moved = await parts.MoveAsync(job.Id, new MaintenancePartsRequest(Guid.NewGuid().ToString("N"), kind,
                    [new MaintenancePartLineRequest(rod.Id, qty)], null), Chef);
                Assert.True(moved.Ok, moved.Error);
                w.Db.ChangeTracker.Clear();
            }
            await w.Db.MaintenanceJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u.SetProperty(j => j.CreatedAtUtc, september.AddDays(-14)));
            await w.Db.MaintenanceJobParts.Where(p => p.JobId == job.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.AtUtc, september));
        }
        var admin = await AdminAsync(w);
        const string month = "?year=2026&month=9";

        var first = await GetAsync(w, admin, Ledger + "/entries/page", month);
        Assert.Equal((12, 10), (Total(first), first.GetProperty("items").GetArrayLength()));
        Assert.Equal(2, (await GetAsync(w, admin, Ledger + "/entries/page", month + "&skip=10")).GetProperty("items").GetArrayLength());
        var summary = Summary(first);
        var head = summary.GetProperty("ledger");
        Assert.Equal((720m, 720m), (Num(head, "totalDebit"), Num(head, "totalCredit")));
        Assert.Empty(head.GetProperty("entries").EnumerateArray());
        Assert.Equal((0, 0, 0, 0), (summary.GetProperty("postedAtIssue").GetInt32(), summary.GetProperty("openCustomerJobs").GetInt32(),
            summary.GetProperty("invoices").GetInt32(), summary.GetProperty("provisions").GetInt32()));

        // The same month as the ledger route: its totals, every account total and every check.
        var whole = await GetAsync(w, admin, Ledger, month);
        Assert.Equal(whole.GetProperty("entries").GetArrayLength(), Total(first));
        Assert.Equal(Num(whole, "totalDebit"), Num(head, "totalDebit"));
        Assert.Equal(whole.GetProperty("accounts").GetRawText(), head.GetProperty("accounts").GetRawText());
        Assert.Equal(whole.GetProperty("checks").GetRawText(), head.GetProperty("checks").GetRawText());

        // The lists below the entries: none this month, and each answers an empty page.
        foreach (var list in new[] { "/posted-at-issue/page", "/open-jobs/page", "/invoices/page", "/provisions/page" })
            Assert.Equal(0, Total(await GetAsync(w, admin, Ledger + list, month)));
        Assert.Empty(Summary(await GetAsync(w, admin, Ledger + "/open-jobs/page", month)).GetProperty("branches").EnumerateArray());
    }
}

/// <summary>The Algeria preset, for the services these tests build by hand.</summary>
internal sealed class DzRegional : IRegionalSettingsProvider
{
    public RegionalSettings Get() => RegionalPresets.Create("DZ")!;
}
