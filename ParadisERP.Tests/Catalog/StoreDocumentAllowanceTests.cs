using System.Reflection;
using System.Text.Json;
using ParadisERP.Application.Catalog;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Catalog;

/// <summary>
/// The yearly wording allowance the owner asked for on 2026-09-24:
/// "اشتغل كله وحطه ستنج الادمن يحددها وتتعدل 3 مرة بالسنة" — every printed document kind, each
/// counted on its own, with the limit itself a setting the admin edits and three by default.
///
/// <para>
/// The decision is a pure function, so most of this needs no database: given what has already been
/// changed this year, the limit in force and the incoming wording against the stored wording, it
/// answers "nothing changed", "allowed" or the one refusal sentence. Only the last test opens
/// SQLite, and only to pin what the endpoint does with that answer — that the predecessor is kept
/// and that an idle save writes no history row.
/// </para>
/// </summary>
public class StoreDocumentAllowanceTests
{
    /// <summary>A fixed +01:00, so the year arithmetic is the same on any machine's zone database.</summary>
    private static readonly TimeZoneInfo Algiers =
        TimeZoneInfo.CreateCustomTimeZone("test/Algiers", TimeSpan.FromHours(1), "Algiers", "Algiers");

    private static Dictionary<string, Dictionary<string, string>> Texts(
        params (string Kind, string Lang, string Text)[] entries) =>
        entries.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.ToDictionary(e => e.Lang, e => e.Text));

    private static StoreDocumentTexts.ChangeAllowance Allowance(
        int limit = Store.DefaultDocumentTextChangesPerYear, int year = 2026, params (string Kind, int Used)[] used) =>
        new(limit, year, used.ToDictionary(u => u.Kind, u => u.Used));

    [Fact]
    public void A_save_that_changes_nothing_consumes_nothing()
    {
        // The clerk who opens the form and saves it back has not changed the contract. Key order
        // differs on the way back through JSON, so the comparison must not depend on it.
        var stored = Texts(("rentalContract", "fr", "Les présentes conditions…"), ("rentalContract", "ar", "الشروط التالية…"));
        var incoming = Texts(("rentalContract", "ar", "الشروط التالية…"), ("rentalContract", "fr", "Les présentes conditions…"));

        Assert.Empty(StoreDocumentTexts.Decide(stored, incoming, Allowance()));
        // Nothing configured, and still nothing configured, is also no change.
        Assert.Empty(StoreDocumentTexts.Decide(null, null, Allowance()));
        Assert.Empty(StoreDocumentTexts.Decide(null, new Dictionary<string, Dictionary<string, string>>(), Allowance()));
    }

    [Fact]
    public void One_save_that_rewrites_several_languages_of_one_kind_is_one_change()
    {
        // Both languages rewritten, and a third added: one change, because that is how the owner
        // thinks of it — he changed the contract.
        var stored = Texts(("rentalContract", "fr", "Ancien"), ("rentalContract", "ar", "القديم"));
        var incoming = Texts(("rentalContract", "fr", "Nouveau"), ("rentalContract", "ar", "الجديد"), ("rentalContract", "en", "New"));

        var decisions = StoreDocumentTexts.Decide(stored, incoming, Allowance());
        var one = Assert.Single(decisions);
        Assert.Equal(DocumentTextKinds.RentalContract, one.Kind);
        Assert.Equal(StoreDocumentTexts.ChangeVerdict.Allowed, one.Verdict);
        // The predecessor is what is kept — the wording as it stood BEFORE, not after.
        Assert.Equal("Ancien", one.PreviousTexts["fr"]);
        Assert.Equal("القديم", one.PreviousTexts["ar"]);
        Assert.Null(one.Refusal);
    }

    [Fact]
    public void Each_kind_is_counted_on_its_own()
    {
        // The invoice footer has had its three; the rental contract has had none. Rewriting the
        // invoice is refused and rewriting the contract is not, in the same save.
        var stored = Texts(("invoice", "fr", "A"), ("rentalContract", "fr", "B"));
        var incoming = Texts(("invoice", "fr", "A2"), ("rentalContract", "fr", "B2"));

        var decisions = StoreDocumentTexts.Decide(stored, incoming, Allowance(3, 2026, ("invoice", 3)));
        Assert.Equal(2, decisions.Count);
        Assert.Equal(StoreDocumentTexts.ChangeVerdict.Refused,
            decisions.Single(d => d.Kind == DocumentTextKinds.Invoice).Verdict);
        Assert.Equal(StoreDocumentTexts.ChangeVerdict.Allowed,
            decisions.Single(d => d.Kind == DocumentTextKinds.RentalContract).Verdict);

        // A kind nobody touched is not in the answer at all, however much of its allowance is spent.
        Assert.DoesNotContain(decisions, d => d.Kind == DocumentTextKinds.Payslip);
    }

    [Fact]
    public void The_change_after_the_limit_is_refused_saying_how_many_were_used_and_when_it_resets()
    {
        var decisions = StoreDocumentTexts.Decide(
            Texts(("rentalContract", "fr", "A")), Texts(("rentalContract", "fr", "B")),
            Allowance(3, 2026, ("rentalContract", 3)));

        var refusal = Assert.Single(decisions).Refusal;
        Assert.NotNull(refusal);
        Assert.Contains("rental contract", refusal);
        Assert.Contains("3 times in 2026", refusal);
        Assert.Contains("1 January 2027", refusal);

        // Zero is a legitimate setting — "frozen" — and says so rather than counting to nothing.
        var frozen = Assert.Single(StoreDocumentTexts.Decide(
            Texts(("invoice", "fr", "A")), Texts(("invoice", "fr", "B")), Allowance(0)));
        Assert.Equal(StoreDocumentTexts.ChangeVerdict.Refused, frozen.Verdict);
        Assert.Contains("frozen", frozen.Refusal!);
    }

    [Fact]
    public void The_limit_is_a_setting_the_admin_edits_not_a_constant()
    {
        var store = new Store { NameFr = "X" };
        Assert.Equal(3, store.DocumentTextChangesPerYear);

        // He raises it from the same screen that shows what is left of it.
        Assert.Null(StoreDocumentTexts.TryNormalize(
            new StoreDocumentsDto(new Dictionary<string, IReadOnlyDictionary<string, string>>(), ChangesPerYear: 12),
            out var raised));
        StoreDocumentTexts.Apply(store, raised);
        Assert.Equal(12, store.DocumentTextChangesPerYear);

        // Zero is storable and means frozen; a negative one is meaningless and is refused.
        Assert.Null(StoreDocumentTexts.TryNormalize(
            new StoreDocumentsDto(new Dictionary<string, IReadOnlyDictionary<string, string>>(), ChangesPerYear: 0), out var frozen));
        Assert.Equal(0, frozen.ChangesPerYear);
        Assert.Contains("cannot be negative", StoreDocumentTexts.TryNormalize(
            new StoreDocumentsDto(new Dictionary<string, IReadOnlyDictionary<string, string>>(), ChangesPerYear: -1), out _)!);

        // And it never reaches a till: it is on neither replicated contract.
        Assert.DoesNotContain("DocumentTextChangesPerYear", typeof(StoreDto).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain("DocumentTextChangesPerYear", typeof(UpsertStoreRequest).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void The_year_and_its_reset_are_the_companys_never_UTC()
    {
        // 23:30 UTC on 31 December is already 1 January in Algiers: the change belongs to the new
        // year, the one the owner would name looking at the calendar on the wall.
        Assert.Equal(2027, StoreDocumentTexts.CompanyYear(new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc), Algiers));
        Assert.Equal(2026, StoreDocumentTexts.CompanyYear(new DateTime(2026, 12, 31, 22, 30, 0, DateTimeKind.Utc), Algiers));
        Assert.Equal(2026, StoreDocumentTexts.CompanyYear(new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc), TimeZoneInfo.Utc));

        // The reset the screen states is the company's own midnight, expressed in UTC.
        Assert.Equal(new DateTime(2026, 12, 31, 23, 0, 0, DateTimeKind.Utc), StoreDocumentTexts.YearResetsUtc(2026, Algiers));
    }

    [Fact]
    public void What_is_left_is_stated_for_every_kind_before_the_admin_types()
    {
        var resets = StoreDocumentTexts.YearResetsUtc(2026, Algiers);
        var allowances = StoreDocumentTexts.Allowances(
            3, 2026, resets, new Dictionary<string, int> { ["invoice"] = 2, ["rentalContract"] = 5 });

        Assert.Equal(DocumentTextKinds.All.Count, allowances.Count);
        var invoice = allowances.Single(a => a.Kind == DocumentTextKinds.Invoice);
        Assert.Equal(2, invoice.Used);
        Assert.Equal(1, invoice.Remaining);
        Assert.Equal(resets, invoice.ResetsOnUtc);
        // Untouched kinds have their whole allowance.
        Assert.Equal(3, allowances.Single(a => a.Kind == DocumentTextKinds.Payslip).Remaining);
        // Lowering the limit under what a kind already spent leaves nothing, never a negative.
        Assert.Equal(0, allowances.Single(a => a.Kind == DocumentTextKinds.RentalContract).Remaining);
    }

    [Fact]
    public async Task Every_change_keeps_its_predecessor_and_an_idle_save_writes_no_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options;

        var storeId = Guid.NewGuid();
        await using (var db = new PosDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 41 });
            db.Stores.Add(new Store { Id = storeId, NameFr = "Béton SARL", Version = 7 });
            await db.SaveChangesAsync();
        }

        // The endpoint's own sequence, three times over: count, decide, record, apply, save.
        async Task<string?> Save(PosDbContext db, StoreDocumentsDto dto)
        {
            var store = await db.Stores.FirstAsync(s => s.Id == storeId);
            if (StoreDocumentTexts.TryNormalize(dto, out var values) is string problem) return problem;

            var used = await StoreDocumentHistory.UsedAsync(db, 2026);
            var decisions = StoreDocumentTexts.Decide(
                store.DocumentTexts, values.Texts,
                new StoreDocumentTexts.ChangeAllowance(store.DocumentTextChangesPerYear, 2026, used));
            if (decisions.FirstOrDefault(d => d.Verdict == StoreDocumentTexts.ChangeVerdict.Refused) is { } refused)
                return refused.Refusal;

            StoreDocumentHistory.Record(db, decisions, 2026, "Amina");
            StoreDocumentTexts.Apply(store, values);
            await db.SaveChangesAsync();
            return null;
        }

        static StoreDocumentsDto Dto(string contract, string? invoice = null)
        {
            var texts = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["rentalContract"] = new Dictionary<string, string> { ["fr"] = contract, ["ar"] = contract + " (ع)" }
            };
            if (invoice is not null) texts["invoice"] = new Dictionary<string, string> { ["fr"] = invoice };
            return new StoreDocumentsDto(texts);
        }

        await using (var db = new PosDbContext(options))
        {
            Assert.Null(await Save(db, Dto("v1")));                 // first wording — predecessor is "nothing"
            Assert.Null(await Save(db, Dto("v1")));                 // saved back untouched — consumes nothing
            Assert.Null(await Save(db, Dto("v2")));                 // second wording
            Assert.Null(await Save(db, Dto("v3", invoice: "F1")));  // third, plus a first invoice footer
        }

        await using (var db = new PosDbContext(options))
        {
            var changes = await db.DocumentTextChanges.AsNoTracking().OrderBy(c => c.ChangedAtUtc).ToListAsync();
            // Three contract changes, not four: the idle save left no trace. Plus the invoice's first.
            Assert.Equal(3, changes.Count(c => c.Kind == DocumentTextKinds.RentalContract));
            Assert.Equal(1, changes.Count(c => c.Kind == DocumentTextKinds.Invoice));
            Assert.All(changes, c => Assert.Equal("Amina", c.ChangedBy));
            Assert.All(changes, c => Assert.Equal(2026, c.ChangeYear));

            // The chain is readable: the wording a customer signed under is the predecessor of the
            // first change that came after their signature.
            var contract = changes.Where(c => c.Kind == DocumentTextKinds.RentalContract).ToList();
            Assert.Equal("{}", contract[0].PreviousTexts);
            Assert.Equal("v1", Parse(contract[1].PreviousTexts)["fr"]);
            Assert.Equal("v2 (ع)", Parse(contract[2].PreviousTexts)["ar"]);
            // …and the wording in force today is on the row itself, with no successor to carry it.
            var store = await db.Stores.AsNoTracking().FirstAsync(s => s.Id == storeId);
            Assert.Equal("v3", store.DocumentTexts!["rentalContract"]["fr"]);
            // The save still does not touch the replicated half of the row.
            Assert.Equal(7, store.Version);
            Assert.Equal(41, (await db.SyncCounters.SingleAsync()).Value);
        }

        // A fourth contract change is refused — three is what this company allows — while the
        // invoice, which has spent one, still has two left.
        await using (var db = new PosDbContext(options))
        {
            var refusal = await Save(db, Dto("v4"));
            Assert.NotNull(refusal);
            Assert.Contains("3 times in 2026", refusal);
            Assert.Null(await Save(db, Dto("v3", invoice: "F2")));

            // The refused save wrote nothing at all — the contract is still v3, not v4. A refusal
            // answers the whole save, so the screen never has to guess which kinds landed.
            var store = await db.Stores.AsNoTracking().FirstAsync(s => s.Id == storeId);
            Assert.Equal("v3", store.DocumentTexts!["rentalContract"]["fr"]);
            Assert.Equal("F2", store.DocumentTexts["invoice"]["fr"]);
        }

        await using (var db = new PosDbContext(options))
        {
            var history = await StoreDocumentHistory.ReadAsync(db, DocumentTextKinds.RentalContract, 100);
            Assert.Equal(3, history.Count);
            // Newest first, so the screen lists the most recent wording it replaced at the top.
            Assert.Equal("v2", history[0].PreviousTexts["fr"]);
            Assert.Empty(history[2].PreviousTexts);
            Assert.All(history, h => Assert.Equal(DocumentTextKinds.RentalContract, h.Kind));
        }
    }

    private static Dictionary<string, string> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, (JsonSerializerOptions?)null)!;

    [Fact]
    public void The_history_never_reaches_a_till()
    {
        // The texts are deliberately off StoreDto; their history has even less business on a
        // cashier's device. A SyncEntity base would put it in the pull payload by itself.
        Assert.False(typeof(DocumentTextChange).IsSubclassOf(typeof(SyncEntity)));
        var names = typeof(StoreDto).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("DocumentTextChanges", names);
    }
}
