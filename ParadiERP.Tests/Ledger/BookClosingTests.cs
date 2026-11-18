using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Tests.Hosting;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Ledger;

/// <summary>
/// CLO-01 (owner, 2026-10-31 and 2026-11-01): months are closed after review, nothing posts into a closed month,
/// and the way back is a reopen with its reason, kept as a trail. The Admin closes and reopens. The till is the one
/// exception: a sale is never refused, so one dated in closed books is booked on the first open day.
/// </summary>
public class BookClosingTests
{
    private static readonly TimeZoneInfo Algiers = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");
    // 15 November 2026 at noon in Algiers: October has ended, November has not.
    private static readonly DateTime Now = new(2026, 11, 15, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly EndOfSeptember = new(2026, 9, 30);
    private static readonly DateOnly EndOfOctober = new(2026, 10, 31);

    private static Task<BookClosingResult> CloseAsync(PosDbContext db, DateOnly through, DateTime? now = null) =>
        BookClosings.CloseAsync(db, through, null, null, "Admin", Algiers, now ?? Now, CancellationToken.None);

    private static Task<BookClosingResult> ReopenAsync(PosDbContext db, DateOnly? through, string? reason) =>
        BookClosings.ReopenAsync(db, through, reason, null, "Admin", Algiers, Now.AddMinutes(1), CancellationToken.None);

    [Fact]
    public async Task Months_close_whole_ended_and_forward_and_reopen_backward_with_a_reason()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;

        Assert.False((await CloseAsync(db, new DateOnly(2026, 10, 30))).Ok);           // not a month end
        Assert.False((await CloseAsync(db, new DateOnly(2026, 11, 30))).Ok);           // November has not ended
        Assert.True((await CloseAsync(db, EndOfSeptember)).Ok);
        Assert.False((await CloseAsync(db, EndOfSeptember, Now.AddMinutes(1))).Ok);   // already closed
        Assert.True((await CloseAsync(db, EndOfOctober, Now.AddMinutes(2))).Ok);

        Assert.False((await BookClosings.ReopenAsync(db, EndOfSeptember, "  ", null, "Admin", Algiers, Now.AddMinutes(3), default)).Ok);
        Assert.False((await BookClosings.ReopenAsync(db, EndOfOctober, "Erreur", null, "Admin", Algiers, Now.AddMinutes(3), default)).Ok);
        var reopened = await BookClosings.ReopenAsync(db, EndOfSeptember, "Facture d'octobre oubliée", null, "Admin", Algiers, Now.AddMinutes(3), default);
        Assert.True(reopened.Ok, reopened.Problem);

        var current = await BookClosings.CurrentAsync(db, default);
        Assert.Equal(EndOfSeptember, current!.ClosedThrough);
        // Midnight on 1 October in Algiers is 23:00 UTC on 30 September.
        Assert.Equal(new DateTime(2026, 9, 30, 23, 0, 0, DateTimeKind.Utc), current.OpenFromUtc);
        var trail = await BookClosings.HistoryAsync(db, 50, default);
        Assert.Equal([true, false, false], trail.Select(c => c.IsReopen));
        Assert.Equal("Facture d'octobre oubliée", trail[0].Reason);

        var all = await BookClosings.ReopenAsync(db, null, "Tout rouvrir", null, "Admin", Algiers, Now.AddMinutes(4), default);
        Assert.True(all.Ok, all.Problem);
        Assert.Null((await BookClosings.CurrentAsync(db, default))!.ClosedThrough);
        Assert.False((await BookClosings.ReopenAsync(db, null, "Encore", null, "Admin", Algiers, Now.AddMinutes(5), default)).Ok);
    }

    [Fact]
    public async Task Nothing_posts_into_closed_books_but_a_document_already_posted_is_still_already_posted()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var ledger = new LedgerPoster(db);
        PostingDraft Draft(Guid source, DateTime at) => new PostingDraft
        {
            EntryDate = at, BranchId = TillHarness.Algiers, SourceType = LedgerSources.Receiving, SourceId = source
        }.Add(PostingLine.Dr(LedgerAccounts.Inventory, 100m)).Add(PostingLine.Cr(LedgerAccounts.Suppliers, 100m));

        var earlier = Guid.NewGuid();
        await ledger.PostAsync(Draft(earlier, new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        Assert.True((await CloseAsync(db, EndOfSeptember)).Ok);

        // 30 September at 23:30 in Algiers is still September on the company's clock.
        var refused = await Assert.ThrowsAsync<PeriodClosedException>(() =>
            ledger.PostAsync(Draft(Guid.NewGuid(), new DateTime(2026, 9, 30, 22, 30, 0, DateTimeKind.Utc))));
        Assert.Equal(EndOfSeptember, refused.ClosedThrough);
        Assert.Null(await ledger.PostAsync(Draft(earlier, new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc))));
        Assert.NotNull(await ledger.PostAsync(Draft(Guid.NewGuid(), new DateTime(2026, 9, 30, 23, 0, 0, DateTimeKind.Utc))));
    }

    [Fact]
    public async Task A_till_sale_dated_in_closed_books_is_accepted_and_booked_on_the_first_open_day()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        // Closed through the month before this one, so the sale below, dated last month, falls inside.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Algiers));
        var endOfLastMonth = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        Assert.True((await BookClosings.CloseAsync(db, endOfLastMonth, null, null, "Admin", Algiers, DateTime.UtcNow, default)).Ok);
        var soldAt = TimeZoneInfo.ConvertTimeToUtc(endOfLastMonth.ToDateTime(new TimeOnly(10, 0)), Algiers);

        var sale = TillHarness.Sale("A-0001", 1) with { SoldAtUtc = soldAt };
        var result = await TillHarness.NewService(db).PushAsync(new PushRequest("C01", [sale]), "C01", CancellationToken.None);

        Assert.Equal(1, result.Accepted);
        db.ChangeTracker.Clear();
        Assert.Equal(soldAt, (await db.Sales.AsNoTracking().SingleAsync()).SoldAtUtc);   // the sale keeps its day
        var entry = await db.LedgerEntries.AsNoTracking().SingleAsync(e => e.SourceType == LedgerSources.Sale);
        Assert.Equal(BookClosings.OpenFrom(endOfLastMonth, Algiers), entry.EntryDate);   // its entry the first open day
        Assert.Contains("first open day", entry.Description);
    }

    // ---------- through the real API ----------

    [Collection(HostCollection.Name)]
    public class OverHttp
    {
        private static async Task<HttpClient> AdminAsync(PosHost host)
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

        [Fact]
        public async Task The_admin_closes_a_month_a_document_dated_in_it_is_refused_in_words_and_a_reopen_lets_it_through()
        {
            using var host = new PosHost();
            using var admin = await AdminAsync(host);
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
            var endOfLastMonth = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
            var inside = new DateOnly(endOfLastMonth.Year, endOfLastMonth.Month, 1);

            var close = await admin.PostAsJsonAsync("/api/ledger/closing/close", new { through = endOfLastMonth, note = "Revu" });
            Assert.True(close.IsSuccessStatusCode, await close.Content.ReadAsStringAsync());

            var created = await admin.PostAsJsonAsync("/api/catalog/customers", new
            {
                fullName = "Client clôture", phone = (string?)null, address = (string?)null, taxId = (string?)null,
                email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person",
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var customer = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var opening = new { amount = 5000m, direction = "owesUs", on = inside };

            var refused = await admin.PostAsJsonAsync($"/api/catalog/customers/{customer}/opening-balance", opening);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("periodClosed", problem.GetProperty("code").GetString());
            Assert.Equal(endOfLastMonth.ToString("yyyy-MM-dd"), problem.GetProperty("closedThrough").GetString());

            var noReason = await admin.PostAsJsonAsync("/api/ledger/closing/reopen", new { through = (DateOnly?)null, reason = "" });
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
            var reopen = await admin.PostAsJsonAsync("/api/ledger/closing/reopen", new { through = (DateOnly?)null, reason = "Solde d'ouverture oublié" });
            Assert.True(reopen.IsSuccessStatusCode, await reopen.Content.ReadAsStringAsync());

            var accepted = await admin.PostAsJsonAsync($"/api/catalog/customers/{customer}/opening-balance", opening);
            Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());

            var state = await admin.GetFromJsonAsync<JsonElement>("/api/ledger/closing");
            Assert.Equal(JsonValueKind.Null, state.GetProperty("closedThrough").ValueKind);
            Assert.Equal(2, state.GetProperty("history").GetArrayLength());
        }

        /// <summary>
        /// COR-E01 (owner with the accountant, 2026-11-05): a mistake in a closed month is corrected in the current
        /// month, naming the original. Reversing an entry of the closed month is accepted, dated in the open month; the
        /// closed month's entry is left as it was, and the reversal says which day it corrects.
        /// </summary>
        [Fact]
        public async Task A_closed_months_entry_is_corrected_in_the_open_month_and_the_correction_names_its_day()
        {
            using var host = new PosHost();
            using var admin = await AdminAsync(host);
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
            var endOfLastMonth = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
            var inside = new DateOnly(endOfLastMonth.Year, endOfLastMonth.Month, 10);

            // A customer's opening balance, dated in last month while it is open.
            var created = await admin.PostAsJsonAsync("/api/catalog/customers", new
            {
                fullName = "Client correction", phone = (string?)null, address = (string?)null, taxId = (string?)null,
                email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person",
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var customer = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var opening = await admin.PostAsJsonAsync($"/api/catalog/customers/{customer}/opening-balance",
                new { amount = 5000m, direction = "owesUs", on = inside });
            Assert.True(opening.IsSuccessStatusCode, await opening.Content.ReadAsStringAsync());
            var original = (await admin.GetFromJsonAsync<JsonElement>($"/api/ledger/entries?sourceType={LedgerSources.CustomerOpening}"))
                .EnumerateArray().Single();
            var originalDate = original.GetProperty("entryDate").GetDateTime();

            // The month is closed, then the mistake is found.
            var close = await admin.PostAsJsonAsync("/api/ledger/closing/close", new { through = endOfLastMonth, note = "Revu" });
            Assert.True(close.IsSuccessStatusCode, await close.Content.ReadAsStringAsync());
            var reverse = await admin.PostAsJsonAsync($"/api/ledger/entries/{original.GetProperty("id").GetGuid()}/reverse",
                new { reason = "Solde d'ouverture saisi deux fois" });
            Assert.True(reverse.IsSuccessStatusCode, await reverse.Content.ReadAsStringAsync());
            var reversalId = (await reverse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reversalId").GetGuid();

            var reversal = await admin.GetFromJsonAsync<JsonElement>($"/api/ledger/entries/{reversalId}");
            var reversalDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(reversal.GetProperty("entryDate").GetDateTime().ToUniversalTime(), zone));
            Assert.True(reversalDay > endOfLastMonth, $"the correction is dated {reversalDay}, inside the closed month");
            Assert.Equal(originalDate, reversal.GetProperty("reversalOfEntryDate").GetDateTime());

            // The closed month's entry is as it was, and the books stay closed through last month.
            var again = await admin.GetFromJsonAsync<JsonElement>($"/api/ledger/entries/{original.GetProperty("id").GetGuid()}");
            Assert.Equal(originalDate, again.GetProperty("entryDate").GetDateTime());
            Assert.Equal(original.GetProperty("total").GetDecimal(), again.GetProperty("total").GetDecimal());
            Assert.Equal(endOfLastMonth.ToString("yyyy-MM-dd"),
                (await admin.GetFromJsonAsync<JsonElement>("/api/ledger/closing")).GetProperty("closedThrough").GetString());
        }
    }
}
