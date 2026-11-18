using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ParadisERP.Tests.Persistence;

/// <summary>
/// The upgrade an installation that is already trading performs for `HardeningAndPrintFoundation`,
/// with rows in it — the same shape as <c>CustomerKindMigrationTests</c>, for the same reason:
/// a default is right in the migration file and wrong in the database if anyone hand-edits the
/// scaffold, and a backfill that silently matches nothing leaves every reprint with an empty
/// buyer block and nothing failing anywhere.
///
/// <para>
/// Three things carry the release for existing data: every account keeps "any terminal"
/// (<c>User.BranchId</c> NULL), every invoice is backfilled with its customer's details as they
/// stand at upgrade, and the store's printed-document fields are NULL and read as nothing
/// configured.
/// </para>
/// </summary>
public class HardeningAndPrintFoundationMigrationTests
{
    /// <summary>The release before this one.</summary>
    private const string Before = "CustomerKindAndSignatory";

    [Fact]
    public async Task The_release_leaves_every_account_on_any_terminal_and_freezes_the_customer_onto_every_existing_invoice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

        await db.Database.GetService<IMigrator>().MigrateAsync(Before);

        // Rows in the old shape, written with raw SQL because the current model would insist on
        // columns the old tables do not have. Parameters, never formatted text: SQLite compares
        // Guid and date columns as strings and only the provider knows the spelling it queries with.
        var now = DateTime.UtcNow;
        var cashierId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var storeId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var namelessCustomerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var sparseInvoiceId = Guid.NewGuid();

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Users (Id, Version, UpdatedAtUtc, IsDeleted, Username, FullName, PasswordHash, PinHash, Role, IsActive) " +
            "VALUES ({0}, 1, {1}, 0, 'c01', 'Caissier Oran', '', '', 0, 1)",
            cashierId, now);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Branches (Id, Version, UpdatedAtUtc, IsDeleted, NameAr, NameFr, NameEn, Address, Phone, IsMain, AllowNegativeStock, IsActive, IsOpenForOperation) " +
            "VALUES ({0}, 1, {1}, 0, '', 'Oran', '', '', '', 1, 0, 1, 1)",
            branchId, now);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Stores (Id, Version, UpdatedAtUtc, IsDeleted, NameAr, NameFr, NameEn, Address, Phone, FiscalIds, ReceiptFooter, " +
            "RequireCustomerEmail, RequireCustomerBirthDate, RequireCustomerPhone, RequireCustomerAddress, RequireCustomerTaxId, " +
            "LoyaltyEnabled, LoyaltyPointsPerUnit, LoyaltyPointValue, LoyaltyMinRedeemPoints, RentalOverdueMultiplier, RentalDepositEnabled, RentalTaxRate) " +
            "VALUES ({0}, 1, {1}, 0, '', 'Ets Benali', '', '', '', '{{}}', '', 0, 0, 0, 0, 0, 0, {2}, {2}, 0, {3}, 0, {2})",
            storeId, now, 0m, 1.5m);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Customers (Id, Version, UpdatedAtUtc, IsDeleted, Code, FullName, Address, TaxId, CreditLimit, Balance, LoyaltyPoints, Kind) " +
            "VALUES ({0}, 1, {1}, 0, 'C001', 'SARL Ben Ali BTP', '12 rue Didouche Mourad, Alger', '000116001234567', {2}, {2}, 0, 2)",
            customerId, now, 0m);
        // A customer with no address and no tax id: the backfill must copy the nulls, not skip the row.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Customers (Id, Version, UpdatedAtUtc, IsDeleted, Code, FullName, CreditLimit, Balance, LoyaltyPoints, Kind) " +
            "VALUES ({0}, 1, {1}, 0, 'C002', 'Karim Haddad', {2}, {2}, 0, 1)",
            namelessCustomerId, now, 0m);
        foreach (var (id, debtor, number) in new[] { (invoiceId, customerId, "INV-000001"), (sparseInvoiceId, namelessCustomerId, "INV-000002") })
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Invoices (Id, Number, CustomerId, BranchId, IssuedAtUtc, DueDateUtc, TaxMode, Origin, SubTotalHt, TaxTotal, GrandTotalTtc, PrepaidAmount, CreatedAtUtc) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {4}, 0, 0, {5}, {6}, {7}, {8}, {4})",
                id, number, debtor, branchId, now, 1000m, 190m, 1190m, 0m);
        }

        // The upgrade an existing installation performs. If a statement is not valid SQL on this
        // provider — the table rebuild the new foreign key forces on SQLite, the correlated
        // UPDATE — it stops here.
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        // Any terminal, for everyone who existed: nobody is locked out by the upgrade itself.
        var cashier = await db.Users.AsNoTracking().SingleAsync(u => u.Id == cashierId);
        Assert.Null(cashier.BranchId);
        Assert.Equal("c01", cashier.Username);
        Assert.True(cashier.IsActive);

        // And the rebuilt table still refuses what it always refused, and now refuses a branch
        // that does not exist. SQLite enforces foreign keys only when the pragma is on, which is
        // how the provider opens every connection.
        await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
        {
            db.Users.Add(new User { Username = "c02", FullName = "x", BranchId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        });
        db.ChangeTracker.Clear();
        await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
        {
            db.Users.Add(new User { Username = "c01", FullName = "duplicate" });
            await db.SaveChangesAsync();
        });
        db.ChangeTracker.Clear();

        // The invoice says what the customer's row said at upgrade, and keeps saying it.
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal("SARL Ben Ali BTP", invoice.BuyerName);
        Assert.Equal("12 rue Didouche Mourad, Alger", invoice.BuyerAddress);
        Assert.Equal("000116001234567", invoice.BuyerTaxId);

        var sparse = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == sparseInvoiceId);
        Assert.Equal("Karim Haddad", sparse.BuyerName);
        Assert.Null(sparse.BuyerAddress);
        Assert.Null(sparse.BuyerTaxId);

        var customer = await db.Customers.SingleAsync(c => c.Id == customerId);
        customer.FullName = "SARL Ben Ali Travaux Publics";
        customer.Address = "Zone industrielle, Rouiba";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reprint = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        Assert.Equal("SARL Ben Ali BTP", reprint.BuyerName);
        Assert.Equal("12 rue Didouche Mourad, Alger", reprint.BuyerAddress);

        // Nothing configured for printing, and the synced row did not move.
        var store = await db.Stores.AsNoTracking().SingleAsync(s => s.Id == storeId);
        Assert.Null(store.DocumentTexts);
        Assert.Null(store.Email);
        Assert.Null(store.Website);
        Assert.Null(store.BankDetails);
        Assert.Equal(1, store.Version);
    }

    [Fact]
    public async Task Document_texts_round_trip_as_json_and_an_empty_dictionary_is_not_null()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options;
        Guid id;
        await using (var db = new PosDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var store = new Store
            {
                NameFr = "Ets Benali",
                DocumentTexts = new()
                {
                    ["rentalContract"] = new() { ["fr"] = "Le locataire s'engage…", ["ar"] = "يلتزم المستأجر…" },
                    ["invoice"] = new() { ["fr"] = "Paiement à 30 jours." }
                },
                Email = "contact@example.dz",
                BankDetails = "RIB 007 99999 1234567890 12"
            };
            db.Stores.Add(store);
            await db.SaveChangesAsync();
            id = store.Id;
        }

        await using (var db = new PosDbContext(options))
        {
            var store = await db.Stores.SingleAsync(s => s.Id == id);
            Assert.NotNull(store.DocumentTexts);
            Assert.Equal("يلتزم المستأجر…", store.DocumentTexts!["rentalContract"]["ar"]);
            Assert.Equal("Paiement à 30 jours.", store.DocumentTexts["invoice"]["fr"]);
            Assert.Equal("contact@example.dz", store.Email);

            // A nested change is detected: the comparer works on the serialised form.
            store.DocumentTexts["invoice"]["ar"] = "الدفع خلال 30 يوماً.";
            Assert.Equal(EntityState.Modified, db.Entry(store).State);
            await db.SaveChangesAsync();
        }

        await using (var db = new PosDbContext(options))
        {
            var store = await db.Stores.AsNoTracking().SingleAsync(s => s.Id == id);
            Assert.Equal("الدفع خلال 30 يوماً.", store.DocumentTexts!["invoice"]["ar"]);
        }
    }
}
