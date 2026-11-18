using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlgeriaPos.Tests.Regional;

/// <summary>
/// The company's own interface face: chosen on the store screen, carried to every till with the
/// store row, and never guessed at.
///
/// <para>
/// Two rules are worth pinning. A body that does not mention the face leaves it alone — the
/// store screen is not the only client, and a client built before the field must not reset it.
/// And an unknown key is refused rather than quietly replaced: the profile's own fallback is
/// for a value nobody is editing, whereas this one is somebody making a choice, and silently
/// storing something else would leave them looking at a face they did not pick.
/// </para>
/// </summary>
public class CompanyFaceTests
{
    [Fact]
    public void A_save_that_does_not_mention_the_face_leaves_it_alone()
    {
        Assert.Null(UiFonts.TryChoose("cairo", Optional<string?>.Unset, out var face));
        Assert.Equal("cairo", face);
    }

    [Fact]
    public void An_explicit_nothing_goes_back_to_the_country_profile()
    {
        foreach (var cleared in new string?[] { null, "", "   " })
        {
            Assert.Null(UiFonts.TryChoose("cairo", Optional<string?>.Of(cleared), out var face));
            Assert.Null(face);
        }
    }

    [Fact]
    public void A_bundled_face_is_taken_however_it_was_typed()
    {
        Assert.Null(UiFonts.TryChoose(null, Optional<string?>.Of("  TAJAWAL  "), out var face));
        Assert.Equal("tajawal", face);
    }

    [Fact]
    public void An_unknown_face_is_refused_and_the_message_names_the_ones_that_exist()
    {
        var problem = UiFonts.TryChoose("cairo", Optional<string?>.Of("comic-sans"), out var face);

        Assert.NotNull(problem);
        Assert.Contains("comic-sans", problem);
        foreach (var known in UiFonts.All) Assert.Contains(known, problem);
        // The refusal leaves the company on the face it had, not on nothing.
        Assert.Equal("cairo", face);
    }

    [Fact]
    public void The_face_reaches_the_tills_with_the_store_row()
    {
        // Without this the screen would offer a choice that stops at the back office, which is
        // the "dead wiring" this codebase has paid for five times.
        var dto = SyncService.Map(new Store { NameFr = "Béton SARL", UiFont = "cairo" });

        Assert.Equal("cairo", dto.UiFont);
        Assert.Null(SyncService.Map(new Store { NameFr = "Béton SARL" }).UiFont);
    }

    [Fact]
    public async Task A_company_that_was_already_trading_upgrades_to_no_face_of_its_own()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(
            new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

        await db.Database.GetService<IMigrator>().MigrateAsync("HardeningAndPrintFoundation");
        var storeId = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Stores (Id, Version, UpdatedAtUtc, IsDeleted, NameAr, NameFr, NameEn, Address, Phone, FiscalIds, ReceiptFooter, " +
            "RequireCustomerEmail, RequireCustomerBirthDate, RequireCustomerPhone, RequireCustomerAddress, RequireCustomerTaxId, " +
            "LoyaltyEnabled, LoyaltyPointsPerUnit, LoyaltyPointValue, LoyaltyMinRedeemPoints) " +
            // The empty fiscal-id map travels as a parameter: braces in the text of an
            // ExecuteSqlRaw statement are read as placeholders and "{}" is not a number.
            "VALUES ({0}, 1, {1}, 0, '', 'Béton SARL', '', '', '', {2}, '', 0, 0, 0, 0, 0, 0, 0, 0, 0)",
            storeId, DateTime.UtcNow, "{}");

        await db.Database.GetService<IMigrator>().MigrateAsync();

        var store = await db.Stores.AsNoTracking().FirstAsync(s => s.Id == storeId);
        Assert.Null(store.UiFont);
        // And the version is untouched: the upgrade must not make every till re-pull the store.
        Assert.Equal(1, store.Version);
    }
}
