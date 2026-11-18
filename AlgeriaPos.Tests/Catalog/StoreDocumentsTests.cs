using System.Reflection;
using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Catalog;

/// <summary>
/// The company's printed-document texts (docs/07 §4.3): stored on the Store row, never on the
/// till's copy of it. The rule worth pinning is the version: these fields are not on
/// <see cref="StoreDto"/>, so saving them must not bump <c>Store.Version</c> — a bump would make
/// every terminal re-pull a row whose synced fields did not change.
/// </summary>
public class StoreDocumentsTests
{
    private static StoreDocumentsDto Dto(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? texts = null,
        string? email = null, string? website = null, string? bank = null) =>
        new(texts ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(), email, website, bank);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Texts(
        params (string Kind, string Lang, string Text)[] entries)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var group in entries.GroupBy(e => e.Kind))
            result[group.Key] = group.ToDictionary(e => e.Lang, e => e.Text);
        return result;
    }

    [Fact]
    public async Task Saving_document_texts_does_not_bump_the_replicated_version()
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

        await using (var db = new PosDbContext(options))
        {
            var store = await db.Stores.FirstAsync(s => s.Id == storeId);
            var problem = StoreDocumentTexts.TryNormalize(
                Dto(Texts(("invoice", "fr", "  Paiement à 30 jours. "), ("invoice", "ar", "الدفع خلال 30 يوماً"), ("payslip", "en", "")),
                    email: " compta@beton.dz ", website: "https://beton.dz", bank: "RIB 007 99999 0000123456 78"),
                out var values);
            Assert.Null(problem);

            // The way the endpoint saves: plain SaveChanges, never SaveWithVersions.
            StoreDocumentTexts.Apply(store, values);
            await db.SaveChangesAsync();
        }

        await using (var db = new PosDbContext(options))
        {
            var store = await db.Stores.AsNoTracking().FirstAsync(s => s.Id == storeId);
            Assert.Equal(7, store.Version);
            Assert.Equal(41, (await db.SyncCounters.SingleAsync()).Value);

            var dto = StoreDocumentTexts.ToDto(store);
            Assert.Equal("Paiement à 30 jours.", dto.DocumentTexts["invoice"]["fr"]);
            Assert.Equal("الدفع خلال 30 يوماً", dto.DocumentTexts["invoice"]["ar"]);
            // An empty text is dropped with its kind, not stored as "".
            Assert.False(dto.DocumentTexts.ContainsKey("payslip"));
            Assert.Equal("compta@beton.dz", dto.Email);
            Assert.Equal("https://beton.dz", dto.Website);
            Assert.Equal("RIB 007 99999 0000123456 78", dto.BankDetails);
        }
    }

    [Fact]
    public void Nothing_configured_reads_as_empty_and_is_stored_as_null()
    {
        var store = new Store { NameFr = "X" };
        Assert.Empty(StoreDocumentTexts.ToDto(store).DocumentTexts);

        Assert.Null(StoreDocumentTexts.TryNormalize(Dto(Texts(("invoice", "fr", "   ")), email: " "), out var values));
        StoreDocumentTexts.Apply(store, values);
        Assert.Null(store.DocumentTexts);
        Assert.Null(store.Email);
    }

    [Fact]
    public void A_text_over_the_cap_or_a_kind_nobody_defined_is_refused_not_stored()
    {
        var tooLong = new string('x', Store.DocumentTextMaxLength + 1);
        Assert.Contains("4000", StoreDocumentTexts.TryNormalize(Dto(Texts(("rentalContract", "fr", tooLong))), out _));
        Assert.Null(StoreDocumentTexts.TryNormalize(Dto(Texts(("rentalContract", "fr", tooLong[..^1]))), out _));

        Assert.Contains("not a printed document kind", StoreDocumentTexts.TryNormalize(Dto(Texts(("contract", "fr", "x"))), out _));
        Assert.Contains("E-mail", StoreDocumentTexts.TryNormalize(Dto(email: new string('a', 201)), out _));
        Assert.Contains("Bank", StoreDocumentTexts.TryNormalize(Dto(bank: new string('a', 401)), out _));

        // A language code that is not one is dropped silently, like the other localized dictionaries.
        Assert.Null(StoreDocumentTexts.TryNormalize(Dto(Texts(("invoice", "FR ", "ok"), ("invoice", "français", "no"))), out var values));
        Assert.Equal(new[] { "fr" }, values.Texts!["invoice"].Keys);
    }

    [Fact]
    public void The_till_replica_never_carries_document_texts()
    {
        // The sync pull is built from StoreDto; a member here would replicate paragraphs of
        // terms to every terminal that has no reader for them.
        var names = typeof(StoreDto).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("DocumentTexts", names);
        Assert.DoesNotContain("Email", names);
        Assert.DoesNotContain("Website", names);
        Assert.DoesNotContain("BankDetails", names);
        Assert.DoesNotContain("DocumentTexts", typeof(UpsertStoreRequest).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void Title_languages_keep_their_order_and_round_trip_through_the_row()
    {
        // The owner's return note: the French title with the Arabic beside it.
        var problem = StoreDocumentTexts.TryNormalize(
            Dto() with { TitleLanguages = new[] { " AR ", "fr", "ar", "" } }, out var values);
        Assert.Null(problem);
        Assert.Equal(new[] { "ar", "fr" }, values.TitleLanguages);

        var store = new Store();
        StoreDocumentTexts.Apply(store, values);
        Assert.Equal("ar,fr", store.DocumentTitleLanguages);
        Assert.Equal(new[] { "ar", "fr" }, StoreDocumentTexts.ToDto(store).TitleLanguages);
    }

    [Fact]
    public void No_title_languages_means_the_document_language_alone()
    {
        Assert.Null(StoreDocumentTexts.TryNormalize(Dto(), out var values));
        Assert.Null(values.TitleLanguages);

        var store = new Store { DocumentTitleLanguages = "ar,fr" };
        StoreDocumentTexts.Apply(store, values);
        Assert.Null(store.DocumentTitleLanguages);
        Assert.Empty(StoreDocumentTexts.ToDto(store).TitleLanguages!);
    }

    [Fact]
    public void A_title_language_that_is_not_a_code_or_one_too_many_is_refused()
    {
        Assert.NotNull(StoreDocumentTexts.TryNormalize(Dto() with { TitleLanguages = new[] { "arabic!" } }, out _));
        Assert.NotNull(StoreDocumentTexts.TryNormalize(
            Dto() with { TitleLanguages = new[] { "ar", "fr", "en", "es" } }, out _));
    }

    [Fact]
    public void Item_names_print_in_one_language_or_two_never_three()
    {
        // Answer 28: one name, Arabic or French, the admin chooses — or both.
        Assert.Null(StoreDocumentTexts.TryNormalize(Dto() with { ItemNameLanguages = new[] { "ar" } }, out var one));
        var store = new Store();
        StoreDocumentTexts.Apply(store, one);
        Assert.Equal("ar", store.DocumentItemNameLanguages);
        Assert.Equal(new[] { "ar" }, StoreDocumentTexts.ToDto(store).ItemNameLanguages);

        Assert.NotNull(StoreDocumentTexts.TryNormalize(
            Dto() with { ItemNameLanguages = new[] { "ar", "fr", "en" } }, out _));
    }
}
