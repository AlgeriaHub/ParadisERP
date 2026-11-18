using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ParadisERP.Tests.Paging;

/// <summary>
/// "I need to paginate in backend, not frontend" (owner, 2026-11-17), for the finance lists: each is asked a page at a
/// time — ten rows to begin with — its filters run on the server over every row, and every figure above it (a tile, a
/// totals row, a running balance) is computed over all the filtered rows. These are money screens, so each figure is
/// checked twice: against hand-computed amounts, and against the list route the screen read whole before, for the same
/// filters — the page route's rows, read page after page, are that route's rows in that order.
/// </summary>
[Collection(HostCollection.Name)]
public class FinancePageTests
{
    private const string March = "from=2025-03-01T00:00:00Z&to=2025-04-01T00:00:00Z";

    // ---------- plumbing ----------

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

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Join(string url, string query) => url + (url.Contains('?') ? "&" : "?") + query;

    /// <summary>Every row of a page route, read a hundred at a time — what turning every page shows.</summary>
    private static async Task<List<JsonElement>> EveryPageAsync(HttpClient client, string url)
    {
        var rows = new List<JsonElement>();
        for (var skip = 0; ; skip += 100)
        {
            var page = await GetAsync(client, Join(url, $"skip={skip}&take=100"));
            var items = page.GetProperty("items").EnumerateArray().ToList();
            rows.AddRange(items);
            if (skip + items.Count >= page.GetProperty("total").GetInt32() || items.Count == 0) return rows;
        }
    }

    private static List<JsonElement> Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToList();

    private static string[] Strings(JsonElement page, string property) =>
        Items(page).Select(x => x.GetProperty(property).GetString()!).ToArray();

    private static int Total(JsonElement page) => page.GetProperty("total").GetInt32();

    private static JsonElement Summary(JsonElement page) => page.GetProperty("summary");

    private static decimal Dec(JsonElement e, string property) => e.GetProperty(property).GetDecimal();

    /// <summary>Two lists of rows are the same rows, field for field, in the same order.</summary>
    private static void SameRows(IEnumerable<JsonElement> expected, IEnumerable<JsonElement> actual) =>
        Assert.Equal(expected.Select(x => x.GetRawText()), actual.Select(x => x.GetRawText()));

    private static async Task<Guid> MainBranchAsync(PosHost host)
    {
        await using var db = Db(host);
        return await db.Branches.Where(b => b.IsMain && !b.IsWarehouse && !b.IsDeleted).Select(b => b.Id).FirstAsync();
    }

    /// <summary>The company's clock, as the server reads it.</summary>
    /// <summary>
    /// The test company's zone. Its profile is the Algeria preset (the host's default country); the
    /// provider cannot be asked outside a request, where no company is resolved yet.
    /// </summary>
    private static TimeZoneInfo CompanyZone(PosHost host)
    {
        _ = host;
        return StoreDocumentHistory.Zone(new PresetRegional());
    }

    private sealed class PresetRegional : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => RegionalPresets.Create(RegionalPresets.DefaultCountry)!;
    }

    private static DateTime Utc(int year, int month, int day, int hour = 0) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    // ---------- the books ----------

    /// <summary>
    /// Cash (530) and sales (700) at the main branch: before March 2025, two sales of 1 000 and 500 (the opening); in
    /// March, 25 entries E01…E25 on day i, of i × 100 — a sale (cash debited) unless i is a multiple of five, which is a
    /// reversal (cash credited); every fourth inter-branch; recorded by Amina (i a multiple of 3), Karim (i ≡ 1) or nobody.
    /// </summary>
    private static async Task<(Guid Branch, Guid Cash)> SeedBooksAsync(PosHost host)
    {
        var branch = await MainBranchAsync(host);
        await using var db = Db(host);
        var cash = await db.LedgerAccounts.SingleAsync(a => a.Code == "530");
        var sales = await db.LedgerAccounts.SingleAsync(a => a.Code == "700");

        LedgerEntry Entry(DateTime at, decimal amount, bool reversal, string description, bool interBranch = false, string? by = null)
        {
            var entry = new LedgerEntry
            {
                EntryDate = at, CreatedAtUtc = at, BranchId = branch, SourceType = "test", SourceId = Guid.NewGuid(),
                Description = description, IsReversal = reversal, IsInterBranch = interBranch, CreatedBy = by,
            };
            entry.Lines.Add(new LedgerEntryLine { AccountId = reversal ? sales.Id : cash.Id, BranchId = branch, Debit = amount });
            entry.Lines.Add(new LedgerEntryLine { AccountId = reversal ? cash.Id : sales.Id, BranchId = branch, Credit = amount });
            return entry;
        }

        db.LedgerEntries.Add(Entry(Utc(2025, 2, 10, 10), 1000m, false, "Opening A"));
        db.LedgerEntries.Add(Entry(Utc(2025, 2, 20, 10), 500m, false, "Opening B"));
        for (var i = 1; i <= 25; i++)
            db.LedgerEntries.Add(Entry(Utc(2025, 3, i, 10), i * 100m, i % 5 == 0, $"E{i:00}",
                interBranch: i % 4 == 0, by: i % 3 == 0 ? "Amina" : i % 3 == 1 ? "Karim" : ""));
        await db.SaveChangesAsync();
        return (branch, cash.Id);
    }

    [Fact]
    public async Task The_journal_pages_newest_first_and_filters_on_the_server()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        await SeedBooksAsync(host);
        var url = $"/api/ledger/entries/page?{March}";

        var first = await GetAsync(client, url);
        Assert.Equal(25, Total(first));
        Assert.Equal(10, first.GetProperty("take").GetInt32());
        Assert.Equal(["E25", "E24", "E23", "E22", "E21", "E20", "E19", "E18", "E17", "E16"], Strings(first, "description"));
        // The "by" filter offers who recorded the period's entries; the count is before the screen's filters.
        Assert.Equal(["Amina", "Karim"], Summary(first).GetProperty("authors").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(25, Summary(first).GetProperty("all").GetInt32());

        Assert.Equal(["E25", "E20", "E15", "E10", "E05"], Strings(await GetAsync(client, Join(url, "reversal=yes")), "description"));
        Assert.Equal(20, Total(await GetAsync(client, Join(url, "reversal=no"))));
        Assert.Equal(["E24", "E20", "E16", "E12", "E08", "E04"], Strings(await GetAsync(client, Join(url, "interBranch=yes")), "description"));
        Assert.Equal(8, Total(await GetAsync(client, Join(url, "by=Amina"))));
        // Filters combine, and the summary stays the period's.
        var combined = await GetAsync(client, Join(url, "reversal=yes&by=Amina"));
        Assert.Equal(["E15"], Strings(combined, "description"));
        Assert.Equal(25, Summary(combined).GetProperty("all").GetInt32());

        // A page past the end is answered with the last page.
        var past = await GetAsync(client, Join(url, "skip=90&take=20"));
        Assert.Equal(20, past.GetProperty("skip").GetInt32());
        Assert.Equal(5, Items(past).Count);

        // Page after page, the rows the whole list showed, in its order (it stopped at 300; here there are 25).
        SameRows((await GetAsync(client, $"/api/ledger/entries?{March}&take=1000")).EnumerateArray(), await EveryPageAsync(client, url));
    }

    [Fact]
    public async Task The_trial_balance_pages_its_rows_and_keeps_the_whole_balance_s_totals()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var (branch, _) = await SeedBooksAsync(host);

        // One branch's balance keeps its inter-branch entries: cash debited 25 000 (the sales) and credited 7 500 (the
        // reversals 5+10+15+20+25), sales the other way round.
        var url = $"/api/ledger/reports/trial-balance/page?{March}&branchId={branch}";
        var first = await GetAsync(client, Join(url, "take=1"));
        Assert.Equal(2, Total(first));
        Assert.Equal(["530"], Strings(first, "code"));
        Assert.Equal(17500m, Dec(Items(first)[0], "balance"));
        var summary = Summary(first);
        Assert.Equal(32500m, Dec(summary, "totalDebit"));
        Assert.Equal(32500m, Dec(summary, "totalCredit"));
        Assert.True(summary.GetProperty("balanced").GetBoolean());
        Assert.False(summary.GetProperty("consolidated").GetBoolean());
        Assert.Equal(["700"], Strings(await GetAsync(client, Join(url, "skip=1&take=1")), "code"));

        // The group's balance (inter-branch entries dropped) and the branch's: the totals the whole report showed.
        foreach (var query in new[] { $"{March}", $"{March}&branchId={branch}" })
        {
            var whole = await GetAsync(client, $"/api/ledger/reports/trial-balance?{query}");
            var paged = await GetAsync(client, $"/api/ledger/reports/trial-balance/page?{query}");
            Assert.Equal(Dec(whole, "totalDebit"), Dec(Summary(paged), "totalDebit"));
            Assert.Equal(Dec(whole, "totalCredit"), Dec(Summary(paged), "totalCredit"));
            Assert.Equal(whole.GetProperty("balanced").GetBoolean(), Summary(paged).GetProperty("balanced").GetBoolean());
            SameRows(whole.GetProperty("rows").EnumerateArray(), await EveryPageAsync(client, $"/api/ledger/reports/trial-balance/page?{query}"));
        }
    }

    [Fact]
    public async Task The_account_statement_carries_its_running_balance_across_pages()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var (branch, cash) = await SeedBooksAsync(host);
        var query = $"accountId={cash}&{March}&branchId={branch}";
        var url = $"/api/ledger/reports/account-statement/page?{query}";

        var first = await GetAsync(client, url);
        Assert.Equal(25, Total(first));
        var summary = Summary(first);
        Assert.Equal("530", summary.GetProperty("code").GetString());
        Assert.Equal(1500m, Dec(summary, "opening"));            // 1 000 + 500 before March
        Assert.Equal(25000m, Dec(summary, "totalDebit"));
        Assert.Equal(7500m, Dec(summary, "totalCredit"));
        Assert.Equal(19000m, Dec(summary, "closing"));           // 1 500 + 25 000 − 7 500
        Assert.Equal(1500m, Dec(summary, "pageOpening"));        // the first page opens on the opening
        Assert.Equal(1600m, Dec(Items(first)[0], "runningBalance")); // E01: + 100

        // Page two opens on the balance after E01…E10 (+ 4 000 of sales, − 1 500 of reversals) and goes on from it.
        var second = await GetAsync(client, Join(url, "skip=10"));
        Assert.Equal(4000m, Dec(Summary(second), "pageOpening"));
        Assert.Equal(5100m, Dec(Items(second)[0], "runningBalance")); // E11: + 1 100

        // The whole statement (500 rows at most; here 25) showed the same opening, rows, balances and closing.
        var whole = await GetAsync(client, $"/api/ledger/reports/account-statement?{query}&take=2000");
        Assert.Equal(Dec(whole, "opening"), Dec(summary, "opening"));
        Assert.Equal(Dec(whole, "closing"), Dec(summary, "closing"));
        var wholeRows = whole.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(wholeRows.Sum(r => Dec(r, "debit")), Dec(summary, "totalDebit"));
        Assert.Equal(wholeRows.Sum(r => Dec(r, "credit")), Dec(summary, "totalCredit"));
        SameRows(wholeRows, await EveryPageAsync(client, url));

        // An unknown account is not found, as on the whole statement.
        var unknown = await client.GetAsync($"/api/ledger/reports/account-statement/page?accountId={Guid.NewGuid()}&{March}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task The_closing_trail_filters_on_the_company_s_days_and_leads_with_the_books_state()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        await using (var db = Db(host))
        {
            // One change a month in 2025, on the 15th; every fourth a reopen. One more close at 23:30 UTC on 30 June,
            // which is already 1 July on the company's clock (Africa/Algiers is an hour ahead).
            for (var i = 1; i <= 12; i++)
                db.BookClosings.Add(new BookClosing
                {
                    AtUtc = Utc(2025, i, 15, 10), IsReopen = i % 4 == 0, UserName = $"u{i:00}",
                    ClosedThrough = new DateOnly(2025, i, 1).AddDays(-1), Reason = $"r{i:00}",
                });
            db.BookClosings.Add(new BookClosing
            {
                AtUtc = new DateTime(2025, 6, 30, 23, 30, 0, DateTimeKind.Utc), UserName = "late", ClosedThrough = new DateOnly(2025, 5, 31),
            });
            await db.SaveChangesAsync();
        }

        var first = await GetAsync(client, "/api/ledger/closing/page");
        Assert.Equal(13, Total(first));
        Assert.Equal(["u12", "u11", "u10", "u09", "u08", "u07", "late", "u06", "u05", "u04"], Strings(first, "userName"));
        var summary = Summary(first);
        Assert.Equal("2025-11-30", summary.GetProperty("closedThrough").GetString());
        Assert.Equal("u12", summary.GetProperty("last").GetProperty("userName").GetString());
        Assert.Equal(13, summary.GetProperty("all").GetInt32());

        Assert.Equal(["u12", "u08", "u04"], Strings(await GetAsync(client, "/api/ledger/closing/page?action=reopened"), "userName"));
        Assert.Equal(10, Total(await GetAsync(client, "/api/ledger/closing/page?action=closed")));

        var zone = CompanyZone(host);
        var lateDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2025, 6, 30, 23, 30, 0, DateTimeKind.Utc), zone));
        var july = Strings(await GetAsync(client, "/api/ledger/closing/page?from=2025-07-01&to=2025-07-31"), "userName");
        var june = Strings(await GetAsync(client, "/api/ledger/closing/page?from=2025-06-01&to=2025-06-30"), "userName");
        Assert.Equal(lateDay.Month == 7 ? new[] { "u07", "late" } : new[] { "u07" }, july);
        Assert.Equal(lateDay.Month == 7 ? new[] { "u06" } : new[] { "late", "u06" }, june);
        // A filter narrows the trail, never the state above it.
        Assert.Equal("2025-11-30", Summary(await GetAsync(client, "/api/ledger/closing/page?action=reopened&from=2025-01-01&to=2025-01-31"))
            .GetProperty("closedThrough").GetString());

        // The whole trail (the newest 200) showed the same rows in the same order.
        SameRows((await GetAsync(client, "/api/ledger/closing")).GetProperty("history").EnumerateArray(),
            await EveryPageAsync(client, "/api/ledger/closing/page"));
    }

    // ---------- treasury ----------

    private static Sale NewSale(Guid branch, string reference, long seq, DateTime at, decimal amount, Guid? customer = null,
        string cashier = "Caisse", SaleStatus status = SaleStatus.Completed) => new()
    {
        Id = Guid.NewGuid(), Reference = reference, DeviceCode = "C01", LocalSeq = seq, BranchId = branch,
        SoldAtUtc = at, ReceivedAtUtc = at, CustomerId = customer, CashierName = cashier,
        SubTotalHt = amount, GrandTotalTtc = amount, PaidAmount = amount, Status = status, PaymentMethod = PaymentMethod.Card,
    };

    [Fact]
    public async Task The_cheque_register_pages_pending_first_and_its_tiles_count_every_state()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var branch = await MainBranchAsync(host);
        await using (var db = Db(host))
        {
            // CHQ-1…CHQ-12, of i × 1 000, due on 1…12 June 2025 (CHQ-10 undated): pending when i ≡ 1 (mod 3), collected
            // when i ≡ 2, bounced when i ≡ 0. Three cash payments beside them are not paper and stay out.
            for (var i = 1; i <= 12; i++)
            {
                var sale = NewSale(branch, $"CH-{i:00}", 7000 + i, Utc(2025, 5, i, 9), i * 1000m);
                db.Sales.Add(sale);
                db.SalePayments.Add(new SalePayment
                {
                    Id = Guid.NewGuid(), SaleId = sale.Id, Method = PaymentMethod.Card, MethodKey = "cheque", Amount = i * 1000m,
                    Status = (i % 3) switch { 1 => PaymentStatus.Pending, 2 => PaymentStatus.Confirmed, _ => PaymentStatus.Bounced },
                    InstrumentNumber = $"CHQ-{i}", InstrumentBank = "BNA", InstrumentDueDate = i == 10 ? null : Utc(2025, 6, i),
                });
            }
            for (var i = 1; i <= 3; i++)
            {
                var sale = NewSale(branch, $"CA-{i}", 7100 + i, Utc(2025, 5, i, 9), 500m);
                db.Sales.Add(sale);
                db.SalePayments.Add(new SalePayment
                {
                    Id = Guid.NewGuid(), SaleId = sale.Id, Method = PaymentMethod.Cash, MethodKey = "cash", Amount = 500m,
                    Status = PaymentStatus.Confirmed,
                });
            }
            await db.SaveChangesAsync();
        }

        const string url = "/api/treasury/pending-payments/page";
        var first = await GetAsync(client, url);
        Assert.Equal(12, Total(first));
        // Pending by due date (the undated last), then bounced, then collected.
        Assert.Equal(["CHQ-1", "CHQ-4", "CHQ-7", "CHQ-10", "CHQ-3", "CHQ-6", "CHQ-9", "CHQ-12", "CHQ-2", "CHQ-5"],
            Strings(first, "instrumentNumber"));
        var tiles = Summary(first);
        Assert.Equal(4, tiles.GetProperty("pendingCount").GetInt32());
        Assert.Equal(22000m, Dec(tiles, "pendingTotal"));     // 1 + 4 + 7 + 10
        Assert.Equal(4, tiles.GetProperty("confirmedCount").GetInt32());
        Assert.Equal(26000m, Dec(tiles, "confirmedTotal"));   // 2 + 5 + 8 + 11
        Assert.Equal(4, tiles.GetProperty("bouncedCount").GetInt32());
        Assert.Equal(30000m, Dec(tiles, "bouncedTotal"));     // 3 + 6 + 9 + 12
        Assert.Equal(12, tiles.GetProperty("all").GetInt32());

        Assert.Equal(["CHQ-1", "CHQ-4", "CHQ-7", "CHQ-10"], Strings(await GetAsync(client, $"{url}?status=Pending"), "instrumentNumber"));
        // Due between 4 and 8 June on the company's clock: an undated cheque has no day and stays out.
        var window = await GetAsync(client, $"{url}?dueFrom=2025-06-04&dueTo=2025-06-08");
        Assert.Equal(["CHQ-4", "CHQ-7", "CHQ-6", "CHQ-5", "CHQ-8"], Strings(window, "instrumentNumber"));
        // The tiles are the register's, never the filtered rows'.
        Assert.Equal(22000m, Dec(Summary(window), "pendingTotal"));

        var past = await GetAsync(client, $"{url}?skip=50&take=10");
        Assert.Equal(10, past.GetProperty("skip").GetInt32());
        Assert.Equal(2, Items(past).Count);

        // The three lists the screen read, kept to the paper as the screen kept them: the same tiles.
        var whole = new List<JsonElement>();
        foreach (var status in new[] { "Pending", "Confirmed", "Bounced" })
            whole.AddRange((await GetAsync(client, $"/api/treasury/pending-payments?status={status}")).EnumerateArray()
                .Where(p => p.GetProperty("methodKey").GetString() == "cheque" || p.GetProperty("instrumentNumber").ValueKind == JsonValueKind.String));
        foreach (var status in new[] { "Pending", "Confirmed", "Bounced" })
            Assert.Equal(whole.Where(p => p.GetProperty("status").GetString() == status).Sum(p => Dec(p, "amount")),
                Dec(tiles, $"{char.ToLowerInvariant(status[0])}{status[1..]}Total"));
        Assert.Equal(whole.Select(p => p.GetProperty("id").GetString()).Order(),
            (await EveryPageAsync(client, url)).Select(p => p.GetProperty("id").GetString()).Order());
    }

    [Fact]
    public async Task Awaiting_collection_merges_the_till_s_paper_and_the_receipts_and_sums_what_is_shown()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var branch = await MainBranchAsync(host);
        var zone = CompanyZone(host);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        // Noon UTC of a company day is still that day on the company's clock.
        DateTime? Due(int? days) => days is int d ? DateTime.SpecifyKind(today.AddDays(d).ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Utc) : null;

        await using (var db = Db(host))
        {
            var societe = new Customer { Code = "AW-C1", FullName = "Société Générale Algérie", Kind = CustomerKind.Company };
            var karim = new Customer { Code = "AW-C2", FullName = "Karim B.", Kind = CustomerKind.Person };
            db.Customers.AddRange(societe, karim);
            // The till's: AW-S1…AW-S6, cheques of i × 100, due −10, −1, 0, +3, +30 days and undated; S1 and S2 the company's.
            int?[] saleDue = [-10, -1, 0, 3, 30, null];
            for (var i = 1; i <= 6; i++)
            {
                var sale = NewSale(branch, $"AW-S{i}", 8000 + i, Utc(2025, 1, 1).AddHours(i), i * 100m, i <= 2 ? societe.Id : null);
                db.Sales.Add(sale);
                db.SalePayments.Add(new SalePayment
                {
                    Id = Guid.NewGuid(), SaleId = sale.Id, Method = PaymentMethod.Card, MethodKey = "cheque", Amount = i * 100m,
                    Status = PaymentStatus.Pending, InstrumentNumber = $"CHQ-A{i}", InstrumentBank = "BNA", InstrumentDueDate = Due(saleDue[i - 1]),
                });
            }
            // The receipts: AW-R1…AW-R5, transfers of i × 1 000, due −5, +2, +10 days, undated and today; odd ones the company's.
            int?[] receiptDue = [-5, 2, 10, null, 0];
            for (var i = 1; i <= 5; i++)
                db.ReceiptVouchers.Add(new ReceiptVoucher
                {
                    Number = $"AW-R{i}", CustomerId = i % 2 == 1 ? societe.Id : karim.Id, BranchId = branch,
                    ReceivedAtUtc = Utc(2025, 2, 1).AddHours(i), MethodKey = "transfer", Behavior = PaymentBehavior.Generic,
                    Status = PaymentStatus.Pending, Amount = i * 1000m, InstrumentDueDate = Due(receiptDue[i - 1]),
                });
            await db.SaveChangesAsync();
        }

        const string url = "/api/treasury/awaiting/page";
        var first = await GetAsync(client, url);
        Assert.Equal(11, Total(first));
        // Earliest due first, undated last; on the same day the earlier taken first.
        Assert.Equal(["AW-S1", "AW-R1", "AW-S2", "AW-S3", "AW-R5", "AW-R2", "AW-S4", "AW-R3", "AW-S5", "AW-S6"], Strings(first, "reference"));
        Assert.Equal(["AW-R4"], Strings(await GetAsync(client, $"{url}?skip=10"), "reference"));
        var figures = Summary(first);
        Assert.Equal(17100m, Dec(figures, "total"));        // 2 100 at the till, 15 000 in receipts
        Assert.Equal(11, figures.GetProperty("count").GetInt32());
        Assert.Equal(1300m, Dec(figures, "overdueTotal"));  // S1 100, S2 200, R1 1 000
        Assert.Equal(3, figures.GetProperty("overdueCount").GetInt32());
        Assert.Equal(["cheque", "transfer"], figures.GetProperty("methodKeys").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(11, figures.GetProperty("all").GetInt32());

        // Each filter narrows the rows and the figures with them, as on the screen.
        var receipts = await GetAsync(client, $"{url}?source=receipt");
        Assert.Equal(5, Total(receipts));
        Assert.Equal(15000m, Dec(Summary(receipts), "total"));
        Assert.Equal(1000m, Dec(Summary(receipts), "overdueTotal"));
        Assert.Equal(11, Summary(receipts).GetProperty("all").GetInt32());

        var bySociete = await GetAsync(client, $"{url}?q=societe");   // folded: Société finds societe
        Assert.Equal(["AW-S1", "AW-R1", "AW-S2", "AW-R5", "AW-R3"], Strings(bySociete, "reference"));
        Assert.Equal(9300m, Dec(Summary(bySociete), "total"));
        Assert.Equal(1300m, Dec(Summary(bySociete), "overdueTotal"));

        Assert.Equal(["AW-S1", "AW-R1", "AW-S2"], Strings(await GetAsync(client, $"{url}?due=overdue"), "reference"));
        Assert.Equal(["AW-S3", "AW-R5"], Strings(await GetAsync(client, $"{url}?due=today"), "reference"));
        Assert.Equal(["AW-S6", "AW-R4"], Strings(await GetAsync(client, $"{url}?due=none"), "reference"));
        Assert.Equal(6, Total(await GetAsync(client, $"{url}?method=cheque")));
        var window = await GetAsync(client, $"{url}?dueFrom={today:yyyy-MM-dd}&dueTo={today.AddDays(3):yyyy-MM-dd}");
        Assert.Equal(["AW-S3", "AW-R5", "AW-R2", "AW-S4"], Strings(window, "reference"));
        Assert.Equal(300m + 5000m + 2000m + 400m, Dec(Summary(window), "total"));
        Assert.Equal(0m, Dec(Summary(window), "overdueTotal"));

        // The two lists the screen read and drew together: the same rows and the same total.
        var tills = (await GetAsync(client, "/api/treasury/pending-payments?status=Pending")).EnumerateArray().ToList();
        var pendingReceipts = (await GetAsync(client, "/api/receivables/receipts?status=Pending&take=1000")).EnumerateArray().ToList();
        Assert.Equal(tills.Sum(p => Dec(p, "amount")) + pendingReceipts.Sum(r => Dec(r, "amount")), Dec(figures, "total"));
        Assert.Equal(tills.Select(p => p.GetProperty("id").GetString()).Concat(pendingReceipts.Select(r => r.GetProperty("id").GetString())).Order(),
            (await EveryPageAsync(client, url)).Select(r => r.GetProperty("id").GetString()).Order());
    }

    [Fact]
    public async Task A_box_s_movements_page_newest_first_with_the_balance_after_each()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var branch = await MainBranchAsync(host);
        CashBox box;
        // M01…M25 on 1 March 2025, hour i, of i × 10: in, except every fifth, out. The balance after each is written with it.
        var balances = new decimal[26];
        await using (var db = Db(host))
        {
            // One box per branch (unique on BranchId): the main branch's seeded one, empty in a fresh host.
            box = await db.CashBoxes.FirstOrDefaultAsync(b => b.BranchId == branch)
                ?? db.CashBoxes.Add(new CashBox { BranchId = branch, NameFr = "Caisse paging", NameAr = "صندوق", NameEn = "Paging box" }).Entity;
            for (var i = 1; i <= 25; i++)
            {
                var signed = i % 5 == 0 ? -i * 10m : i * 10m;
                balances[i] = balances[i - 1] + signed;
                db.CashMovements.Add(new CashMovement
                {
                    CashBoxId = box.Id, Direction = signed < 0 ? CashDirection.Out : CashDirection.In, Amount = i * 10m,
                    BalanceAfter = balances[i], Kind = CashMovementKind.Sale, Reference = $"M{i:00}", OccurredAtUtc = Utc(2025, 3, 1).AddHours(i - 1),
                });
            }
            await db.SaveChangesAsync();
        }

        var url = $"/api/treasury/cash-boxes/{box.Id}/movements/page";
        var first = await GetAsync(client, url);
        Assert.Equal(25, Total(first));
        Assert.Equal(["M25", "M24", "M23", "M22", "M21", "M20", "M19", "M18", "M17", "M16"], Strings(first, "reference"));
        var summary = Summary(first);
        Assert.Equal(2500m, Dec(summary, "totalIn"));     // (325 − 75) × 10
        Assert.Equal(750m, Dec(summary, "totalOut"));     // (5 + 10 + 15 + 20 + 25) × 10
        Assert.Equal(1750m, Dec(summary, "balance"));
        Assert.Equal(balances[25], Dec(summary, "balance"));
        Assert.Equal(balances[25], Dec(summary, "pageClosing"));
        Assert.Equal(balances[15], Dec(summary, "pageOpening")); // before M16, the page's earliest

        var second = await GetAsync(client, Join(url, "skip=10"));
        Assert.Equal(balances[5], Dec(Summary(second), "pageOpening"));
        Assert.Equal(balances[15], Dec(Summary(second), "pageClosing"));
        Assert.Equal(balances[15], Dec(Items(second)[0], "balanceAfter"));

        // The whole list (the latest 500) showed the same rows in the same order.
        SameRows((await GetAsync(client, $"/api/treasury/cash-boxes/{box.Id}/movements")).EnumerateArray(), await EveryPageAsync(client, url));
    }

    // ---------- sales and reports ----------

    [Fact]
    public async Task The_sales_list_pages_newest_first_and_filters_by_state_and_cashier()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var branch = await MainBranchAsync(host);
        await using (var db = Db(host))
        {
            // S-01…S-25 on 1 March 2025, hour i: Amina sells the even ones, Karim the odd; every sixth is a refund;
            // every fifth carries tax (the seller ticked it for the customer).
            for (var i = 1; i <= 25; i++)
            {
                var sale = NewSale(branch, $"S-{i:00}", 5000 + i, Utc(2025, 3, 1).AddHours(i - 1), i * 10m,
                    cashier: i % 2 == 0 ? "Amina" : "Karim", status: i % 6 == 0 ? SaleStatus.Refunded : SaleStatus.Completed);
                if (i % 5 == 0) sale.TaxTotal = i * 1.9m;
                db.Sales.Add(sale);
            }
            await db.SaveChangesAsync();
        }

        var url = $"/api/sales/page?{March}";
        var first = await GetAsync(client, url);
        Assert.Equal(25, Total(first));
        Assert.Equal(["S-25", "S-24", "S-23", "S-22", "S-21", "S-20", "S-19", "S-18", "S-17", "S-16"], Strings(first, "reference"));
        Assert.Equal(["Amina", "Karim"], Summary(first).GetProperty("cashiers").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(25, Summary(first).GetProperty("all").GetInt32());

        Assert.Equal(["S-24", "S-18", "S-12", "S-06"], Strings(await GetAsync(client, Join(url, "status=refunded")), "reference"));
        Assert.Equal(21, Total(await GetAsync(client, Join(url, "status=completed"))));
        var amina = await GetAsync(client, Join(url, "status=completed&cashier=Amina"));
        Assert.Equal(8, Total(amina));
        Assert.Equal(25, Summary(amina).GetProperty("all").GetInt32());

        // With or without tax (owner, 2026-11-18).
        Assert.Equal(["S-25", "S-20", "S-15", "S-10", "S-05"], Strings(await GetAsync(client, Join(url, "tax=with")), "reference"));
        Assert.Equal(20, Total(await GetAsync(client, Join(url, "tax=without"))));
        Assert.Equal(["S-20", "S-10"], Strings(await GetAsync(client, Join(url, "tax=with&cashier=Amina")), "reference"));

        // The whole list (200 at most; here 25) showed the same sales in the same order.
        SameRows((await GetAsync(client, $"/api/sales?{March}&take=1000")).EnumerateArray(), await EveryPageAsync(client, url));
    }

    [Fact]
    public async Task Sales_without_a_cost_page_their_products_and_keep_the_period_s_figures()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var branch = await MainBranchAsync(host);
        await using (var db = Db(host))
        {
            // M01…M12, each sold once on 10 May 2025 for i × 100 without a cost; a cost entered since on every third.
            for (var i = 1; i <= 12; i++)
            {
                var product = new Product { Sku = $"MC-{i:00}", NameFr = $"M{i:00}", NameAr = $"م{i}", NameEn = $"M{i:00}", Price = i * 100m, CostPrice = i % 3 == 0 ? 50m : 0m };
                db.Products.Add(product);
                var sale = NewSale(branch, $"MC-S{i:00}", 9000 + i, Utc(2025, 5, 10, 10), i * 100m);
                sale.Lines.Add(new SaleLine
                {
                    Id = Guid.NewGuid(), ProductId = product.Id, Sku = product.Sku, NameSnapshot = product.NameFr, Qty = 1m,
                    UnitPrice = i * 100m, LineTotalTtc = i * 100m, CostMissing = true,
                });
                db.Sales.Add(sale);
            }
            await db.SaveChangesAsync();
        }

        const string period = "from=2025-05-01&to=2025-05-31";
        var url = $"/api/reports/missing-cost/page?{period}";
        var first = await GetAsync(client, url);
        Assert.Equal(12, Total(first));
        Assert.Equal(["M12", "M11", "M10", "M09", "M08", "M07", "M06", "M05", "M04", "M03"], Strings(first, "nameFr"));
        var figures = Summary(first);
        Assert.Equal(7800m, Dec(figures, "uncostedHt"));
        Assert.Equal(12, figures.GetProperty("uncostedSales").GetInt32());
        Assert.Equal("2025-05-01", figures.GetProperty("from").GetString());

        var set = await GetAsync(client, Join(url, "cost=set"));
        Assert.Equal(["M12", "M09", "M06", "M03"], Strings(set, "nameFr"));
        Assert.Equal(7800m, Dec(Summary(set), "uncostedHt"));
        Assert.Equal(8, Total(await GetAsync(client, Join(url, "cost=missing"))));

        // The whole report showed the same figures and rows.
        var whole = await GetAsync(client, $"/api/reports/missing-cost?{period}");
        foreach (var name in new[] { "salesHt", "uncostedHt" }) Assert.Equal(Dec(whole, name), Dec(figures, name));
        foreach (var name in new[] { "uncostedSales", "productsWithoutCost" })
            Assert.Equal(whole.GetProperty(name).GetInt32(), figures.GetProperty(name).GetInt32());
        SameRows(whole.GetProperty("rows").EnumerateArray(), await EveryPageAsync(client, url));
    }

    [Fact]
    public async Task Low_stock_pages_the_largest_shortfall_first_and_filters_out_of_stock()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var branch = await MainBranchAsync(host);
        await using (var db = Db(host))
        {
            // L01…L12 at the main branch, holding i − 3 against a reorder level of 5: L01…L08 are low, L01…L03 out.
            // (Single figures: the list route compares the two in SQL, which SQLite does on their text.)
            for (var i = 1; i <= 12; i++)
            {
                var product = new Product { Sku = $"LS-{i:00}", NameFr = $"L{i:00}", Price = 10m };
                db.Products.Add(product);
                db.ProductStocks.Add(new ProductStock { ProductId = product.Id, BranchId = branch, StockQty = i - 3, ReorderLevel = 5m });
            }
            await db.SaveChangesAsync();
        }

        const string url = "/api/catalog/products/low-stock/page";
        var first = await GetAsync(client, url);
        var whole = (await GetAsync(client, "/api/catalog/products/low-stock")).EnumerateArray().ToList();
        Assert.Equal(whole.Count, Total(first));
        var mine = (await EveryPageAsync(client, url)).Where(r => r.GetProperty("sku").GetString()!.StartsWith("LS-")).ToList();
        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"LS-{i:00}").ToArray(), mine.Select(r => r.GetProperty("sku").GetString()!).ToArray());
        Assert.Equal(whole.Count, first.GetProperty("summary").GetProperty("all").GetInt32());

        var outOfStock = await EveryPageAsync(client, Join(url, "stock=out"));
        Assert.Equal(whole.Count(r => Dec(r, "stockQty") <= 0), outOfStock.Count);
        Assert.Equal(["LS-01", "LS-02", "LS-03"],
            outOfStock.Select(r => r.GetProperty("sku").GetString()!).Where(sku => sku.StartsWith("LS-")).ToArray());
        Assert.Equal(whole.Count(r => Dec(r, "stockQty") > 0), Total(await GetAsync(client, Join(url, "stock=low"))));

        // The whole list showed the same rows, largest shortfall first.
        Assert.Equal(whole.Select(r => Dec(r, "stockQty") - Dec(r, "reorderLevel")),
            (await EveryPageAsync(client, url)).Select(r => Dec(r, "stockQty") - Dec(r, "reorderLevel")));
    }

    // ---------- fixed assets and the stock valuation ----------

    [Fact]
    public async Task The_fixed_asset_pages_answer_what_their_reports_answer()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var branch = await MainBranchAsync(host);

        var register = await GetAsync(client, "/api/fixed-assets/register");
        var registerPage = await GetAsync(client, "/api/fixed-assets/register/page");
        var summary = Summary(registerPage);
        foreach (var name in new[] { "active", "monthToDate" })
            Assert.Equal(register.GetProperty(name).GetBoolean(), summary.GetProperty(name).GetBoolean());
        foreach (var name in new[] { "year", "month" })
            Assert.Equal(register.GetProperty(name).GetInt32(), summary.GetProperty(name).GetInt32());
        foreach (var name in new[] { "cost", "accumulatedDepreciation", "netBookValue", "units" })
            Assert.Equal(Dec(register, name), Dec(summary, name));
        Assert.Equal(register.GetProperty("pools").GetArrayLength(), Total(registerPage));
        Assert.Equal(register.GetProperty("pools").GetArrayLength(), summary.GetProperty("poolCount").GetInt32());
        SameRows(register.GetProperty("pools").EnumerateArray(), await EveryPageAsync(client, "/api/fixed-assets/register/page"));

        var month = await GetAsync(client, "/api/fixed-assets/depreciation");
        var monthPage = await GetAsync(client, "/api/fixed-assets/depreciation/page");
        Assert.Equal(Dec(month, "totalDebit"), Dec(Summary(monthPage), "totalDebit"));
        Assert.Equal(Dec(month, "totalCredit"), Dec(Summary(monthPage), "totalCredit"));
        Assert.Equal(month.GetProperty("entries").GetArrayLength(), Summary(monthPage).GetProperty("entryCount").GetInt32());
        Assert.Equal(month.GetProperty("checks").GetRawText(), Summary(monthPage).GetProperty("checks").GetRawText());
        SameRows(month.GetProperty("entries").EnumerateArray(), await EveryPageAsync(client, "/api/fixed-assets/depreciation/page"));

        // A pool's history: the same answer, whatever it is.
        var productId = Guid.NewGuid();
        var history = await client.GetAsync($"/api/fixed-assets/register/history?productId={productId}&branchId={branch}");
        var historyPage = await client.GetAsync($"/api/fixed-assets/register/history/page?productId={productId}&branchId={branch}");
        Assert.Equal(history.StatusCode, historyPage.StatusCode);
    }

    [Fact]
    public async Task The_stock_valuation_pages_its_rows_and_movements_and_keeps_the_report_s_totals()
    {
        using var host = new PosHost();
        var client = await SignedInAsync(host);
        var branch = await MainBranchAsync(host);
        // Twelve products received at the main branch, i units of each at a cost price of i × 100.
        var products = new List<Guid>();
        for (var i = 1; i <= 12; i++)
        {
            var created = await client.PostAsJsonAsync("/api/catalog/products", new
            {
                sku = $"SV-{i:00}", nameAr = $"ص{i}", nameFr = $"V{i:00}", nameEn = $"V{i:00}",
                price = i * 150m, costPrice = i * 100m, taxRate = 0m, unit = "u", trackStock = true, isActive = true,
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            products.Add(id);
            var stocked = await client.PostAsJsonAsync($"/api/catalog/products/{id}/stock",
                new { branchId = branch, qtyDelta = (decimal)i, reason = "opening", reorderLevel = (decimal?)null });
            Assert.Equal(HttpStatusCode.OK, stocked.StatusCode);
        }

        var whole = await GetAsync(client, "/api/valuation/shadow");
        var wholeRows = whole.GetProperty("rows").EnumerateArray().ToList();
        Assert.NotEmpty(wholeRows);
        var first = await GetAsync(client, "/api/valuation/shadow/page");
        Assert.Equal(wholeRows.Count, Total(first));
        Assert.Equal(Math.Min(10, wholeRows.Count), Items(first).Count);
        Assert.Equal(whole.GetProperty("totals").GetRawText(), Summary(first).GetProperty("totals").GetRawText());
        Assert.Equal(whole.GetProperty("settings").GetRawText(), Summary(first).GetProperty("settings").GetRawText());
        Assert.Equal(wholeRows.Count, Summary(first).GetProperty("all").GetInt32());
        SameRows(wholeRows, await EveryPageAsync(client, "/api/valuation/shadow/page"));

        // The screen's filters, on the server: the rows the whole report would have kept.
        SameRows(wholeRows.Where(r => Dec(r, "closingQty") > 0), await EveryPageAsync(client, "/api/valuation/shadow/page?qty=positive"));
        SameRows(wholeRows.Where(r => Dec(r, "difference") == 0), await EveryPageAsync(client, "/api/valuation/shadow/page?difference=none"));
        var filtered = await GetAsync(client, "/api/valuation/shadow/page?qty=negative");
        Assert.Equal(wholeRows.Count(r => Dec(r, "closingQty") < 0), Total(filtered));
        Assert.Equal(whole.GetProperty("totals").GetRawText(), Summary(filtered).GetProperty("totals").GetRawText());

        // One product's movements, each with the quantity and average after it.
        var detail = await GetAsync(client, $"/api/valuation/shadow/products/{products[^1]}");
        var detailPage = await GetAsync(client, $"/api/valuation/shadow/products/{products[^1]}/page");
        Assert.Equal(detail.GetProperty("lines").GetArrayLength(), Total(detailPage));
        Assert.Equal(detail.GetProperty("lines").GetArrayLength(), Summary(detailPage).GetProperty("lineCount").GetInt32());
        Assert.Equal(products[^1], Summary(detailPage).GetProperty("productId").GetGuid());
        SameRows(detail.GetProperty("lines").EnumerateArray(), await EveryPageAsync(client, $"/api/valuation/shadow/products/{products[^1]}/page"));
        var missing = await client.GetAsync($"/api/valuation/shadow/products/{Guid.NewGuid()}/page");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // The pending gaps: the costing status's figures, its list a page at a time.
        var costing = await GetAsync(client, "/api/valuation/sales-cost");
        var gaps = await GetAsync(client, "/api/valuation/sales-cost/page");
        Assert.Equal(costing.GetProperty("pendingGaps").GetArrayLength(), Total(gaps));
        foreach (var name in new[] { "shadowValue", "bookValue", "adjustment", "pendingGapTotal" })
            Assert.Equal(Dec(costing, name), Dec(Summary(gaps), name));
        SameRows(costing.GetProperty("pendingGaps").EnumerateArray(), await EveryPageAsync(client, "/api/valuation/sales-cost/page"));
    }
}
