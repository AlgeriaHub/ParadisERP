using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Customers;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Customers;

/// <summary>
/// The company's authorised representative (المخوَّل) as a history: appointed, changed when
/// they leave, never edited and never deleted. The owner's sentence — the representative may
/// leave, and what was signed keeps the name of whoever signed it — is the contract's business
/// and is tested in <c>RentalDocumentTests</c>; this file is about the history itself.
/// </summary>
public class CustomerRepresentativeTests
{
    private static readonly Guid Company = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Person = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Legacy = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private sealed class Harness(SqliteConnection connection, PosDbContext db, CustomerRepresentatives representatives) : IAsyncDisposable
    {
        public PosDbContext Db { get; } = db;
        public CustomerRepresentatives Representatives { get; } = representatives;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        public Task<List<CustomerRepresentative>> HistoryOfAsync(Guid customerId) =>
            Db.CustomerRepresentatives.AsNoTracking().Where(x => x.CustomerId == customerId)
                .OrderBy(x => x.FromUtc).ToListAsync();

        public Task<Customer> CustomerAsync(Guid id) => Db.Customers.AsNoTracking().FirstAsync(c => c.Id == id);
    }

    private static async Task<Harness> NewAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Stores.Add(new Store { NameFr = "Échafaudages SARL" });
        db.Customers.Add(new Customer { Id = Company, Code = "C001", FullName = "SARL El Injaz Travaux", Kind = CustomerKind.Company });
        db.Customers.Add(new Customer { Id = Person, Code = "C002", FullName = "Karim Ben Ali", Kind = CustomerKind.Person, IdDocumentNumber = "109 876 543" });
        // A row as the migration leaves it: no kind, because nobody guessed one.
        db.Customers.Add(new Customer { Id = Legacy, Code = "C003", FullName = "Sahraoui Travaux" });
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);

        return new Harness(connection, db, new CustomerRepresentatives(db));
    }

    private static ChangeRepresentativeRequest Change(string name, DateTime? from = null, string? id = null) =>
        new(name, id, null, null, from);

    // ---------- the history ----------

    [Fact]
    public async Task Appointing_the_first_representative_creates_one_current_row()
    {
        await using var h = await NewAsync();

        var result = await h.Representatives.ChangeAsync(Company, Change("Nadia Bouzid", id: "118 765 432"), "admin");
        Assert.True(result.Ok);

        var row = Assert.Single(await h.HistoryOfAsync(Company));
        Assert.Equal("Nadia Bouzid", row.FullName);
        Assert.Equal("118 765 432", row.IdDocumentNumber);
        Assert.Null(row.ToUtc);
        Assert.Equal("admin", row.CreatedBy);
        Assert.Equal(row.Id, (await h.Representatives.CurrentAsync(Company))!.Id);
    }

    [Fact]
    public async Task Changing_the_representative_ends_the_current_one_and_adds_the_new_one_in_one_write()
    {
        await using var h = await NewAsync();
        var start = DateTime.UtcNow.AddMonths(-6);
        await h.Representatives.ChangeAsync(Company, Change("Nadia Bouzid", start), "admin");

        var takeover = DateTime.UtcNow.AddDays(-1);
        var result = await h.Representatives.ChangeAsync(Company, Change("Yacine Meziane", takeover), "manager");
        Assert.True(result.Ok);

        var history = await h.HistoryOfAsync(Company);
        Assert.Equal(2, history.Count);
        // Nothing deleted; the old row is ended on the day the new one starts, and exactly one
        // row is open — the invariant the whole "who signs today" question rests on.
        Assert.Equal("Nadia Bouzid", history[0].FullName);
        Assert.Equal(takeover, history[0].ToUtc);
        Assert.Equal("Yacine Meziane", history[1].FullName);
        Assert.Equal(takeover, history[1].FromUtc);
        Assert.Single(history, x => x.ToUtc is null);
        Assert.Equal("Yacine Meziane", (await h.Representatives.CurrentAsync(Company))!.FullName);

        // The response is the whole history, newest first, so a screen redraws from one answer.
        Assert.Equal(["Yacine Meziane", "Nadia Bouzid"], result.Value!.Select(x => x.FullName));
    }

    [Fact]
    public async Task A_representative_row_is_never_edited_or_deleted()
    {
        await using var h = await NewAsync();
        var t0 = DateTime.UtcNow.AddYears(-2);
        await h.Representatives.ChangeAsync(Company, Change("Nadia Bouzid", t0), "admin");
        await h.Representatives.ChangeAsync(Company, Change("Yacine Meziane", t0.AddMonths(8)), "admin");
        await h.Representatives.ChangeAsync(Company, Change("Amel Cherif", t0.AddMonths(16)), "admin");

        var history = await h.HistoryOfAsync(Company);
        Assert.Equal(3, history.Count);
        // The first two rows keep what was written; only the count grows and only ToUtc was set.
        Assert.Equal("Nadia Bouzid", history[0].FullName);
        Assert.Equal(t0, history[0].FromUtc);
        Assert.Equal(t0.AddMonths(8), history[0].ToUtc);
        Assert.Equal("Yacine Meziane", history[1].FullName);
        Assert.Equal(t0.AddMonths(8), history[1].FromUtc);
        Assert.Equal(t0.AddMonths(16), history[1].ToUtc);
        Assert.Null(history[2].ToUtc);

        // And there is no method on the service that could do otherwise.
        Assert.DoesNotContain(typeof(CustomerRepresentatives).GetMethods(),
            m => m.Name.StartsWith("Update", StringComparison.Ordinal) || m.Name.StartsWith("Delete", StringComparison.Ordinal)
                 || m.Name.StartsWith("Remove", StringComparison.Ordinal));
    }

    // ---------- refusals ----------

    [Fact]
    public async Task Changing_the_representative_of_a_person_or_unspecified_customer_is_refused()
    {
        await using var h = await NewAsync();

        var person = await h.Representatives.ChangeAsync(Person, Change("Someone"), "admin");
        Assert.Equal(RentalFailure.Conflict, person.Failure);
        var legacy = await h.Representatives.ChangeAsync(Legacy, Change("Someone"), "admin");
        Assert.Equal(RentalFailure.Conflict, legacy.Failure);
        Assert.Empty(await h.Db.CustomerRepresentatives.ToListAsync());

        var unknown = await h.Representatives.ChangeAsync(Guid.NewGuid(), Change("Someone"), "admin");
        Assert.Equal(RentalFailure.NotFound, unknown.Failure);
    }

    [Fact]
    public async Task A_blank_name_is_refused()
    {
        await using var h = await NewAsync();
        var blank = await h.Representatives.ChangeAsync(Company, Change("   "), "admin");
        Assert.Equal(RentalFailure.Invalid, blank.Failure);
        Assert.Contains("full name", blank.Message);
    }

    [Fact]
    public async Task A_change_dated_before_the_current_took_over_is_refused()
    {
        await using var h = await NewAsync();
        var start = DateTime.UtcNow.AddMonths(-6);
        await h.Representatives.ChangeAsync(Company, Change("Nadia Bouzid", start), "admin");

        var earlier = await h.Representatives.ChangeAsync(Company, Change("Yacine Meziane", start.AddDays(-1)), "admin");
        Assert.Equal(RentalFailure.Invalid, earlier.Failure);
        Assert.Contains("before the current one", earlier.Message);
        // Refused means untouched: the current one is still current.
        Assert.Equal("Nadia Bouzid", (await h.Representatives.CurrentAsync(Company))!.FullName);
        Assert.Single(await h.HistoryOfAsync(Company));
    }

    [Fact]
    public async Task A_change_dated_in_the_future_is_refused()
    {
        await using var h = await NewAsync();
        var future = await h.Representatives.ChangeAsync(Company, Change("Nadia Bouzid", DateTime.UtcNow.AddDays(3)), "admin");
        Assert.Equal(RentalFailure.Invalid, future.Failure);
        Assert.Contains("future", future.Message);

        // Today in the company's time zone, sent as UTC midnight, may be a few hours ahead of
        // UTC now and is accepted.
        var today = await h.Representatives.ChangeAsync(Company, Change("Nadia Bouzid", DateTime.UtcNow.AddHours(12)), "admin");
        Assert.True(today.Ok);
    }

    // ---------- what the change does not touch ----------

    [Fact]
    public async Task Changing_the_representative_does_not_bump_the_customers_version()
    {
        await using var h = await NewAsync();
        var before = await h.CustomerAsync(Company);
        var counterBefore = (await h.Db.SyncCounters.AsNoTracking().FirstAsync()).Value;

        await h.Representatives.ChangeAsync(Company, Change("Nadia Bouzid"), "admin");
        await h.Representatives.ChangeAsync(Company, Change("Yacine Meziane"), "admin");

        // Nothing replicated changed, so no till has anything to re-pull: neither the row's
        // version nor the global counter moved.
        var after = await h.CustomerAsync(Company);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal(counterBefore, (await h.Db.SyncCounters.AsNoTracking().FirstAsync()).Value);
    }

    [Fact]
    public async Task Turning_a_company_into_a_person_ends_its_current_representative_and_keeps_the_history()
    {
        await using var h = await NewAsync();
        await h.Representatives.ChangeAsync(Company, Change("Nadia Bouzid", DateTime.UtcNow.AddMonths(-6)), "admin");

        // What PUT /customers does inside its scope when the chip moves to Person.
        var at = DateTime.UtcNow;
        await h.Representatives.EndCurrentAsync(Company, at);
        var customer = await h.Db.Customers.FirstAsync(c => c.Id == Company);
        customer.Kind = CustomerKind.Person;
        await h.Db.SaveChangesAsync();

        Assert.Null(await h.Representatives.CurrentAsync(Company));
        var row = Assert.Single(await h.HistoryOfAsync(Company));
        Assert.Equal(at, row.ToUtc);
        Assert.Equal("Nadia Bouzid", row.FullName);
        // And the DTO shows nobody, whatever the table holds for a person.
        Assert.Null(CustomerRepresentatives.ToDto(await h.CustomerAsync(Company), row).Representative);
    }

    [Fact]
    public void A_company_stores_no_identity_document_number()
    {
        // Apply(Customer, request, kind) in CatalogEndpoints nulls it for a company; the DTO
        // it builds is what the screen reads, so the rule is checked at the mapping.
        var company = new Customer { Kind = CustomerKind.Company, IdDocumentNumber = null };
        var dto = CustomerRepresentatives.ToDto(company, new CustomerRepresentative
        {
            CustomerId = company.Id, FullName = "Nadia Bouzid", IdDocumentNumber = "118 765 432", FromUtc = DateTime.UtcNow
        });
        Assert.Equal("Company", dto.Kind);
        Assert.Null(dto.IdDocumentNumber);
        Assert.Equal("118 765 432", dto.Representative!.IdDocumentNumber);
    }

    // ---------- entity facts and the sync boundary ----------

    [Fact]
    public void A_new_customer_is_of_unspecified_kind_and_that_is_enum_value_zero()
    {
        // Tests build the schema with EnsureCreated (CLR default) and production migrates
        // (column default 0); the two agree only while the enum's zero means "we do not know".
        var customer = new Customer();
        Assert.Equal(CustomerKind.Unspecified, customer.Kind);
        Assert.Equal(0, (int)CustomerKind.Unspecified);
        Assert.Null(customer.IdDocumentNumber);
    }

    [Fact]
    public void A_new_rental_contract_is_not_held_to_the_signatory_rule_until_the_service_raises_it()
    {
        // The CLR default is the migration default: a row that reaches the database without
        // going through CreateContractAsync — a raw insert, an old row — is exempt.
        var contract = new RentalContract();
        Assert.False(contract.SignatoryRequired);
        Assert.Null(contract.SignatoryName);
        Assert.Null(contract.SignatoryIdNumber);
    }

    [Fact]
    public void Neither_the_customer_kind_nor_the_representative_history_replicates_to_a_till()
    {
        // The pull is driven by the base type: one word in the class declaration would put the
        // history of who signs for every company on every cashier's device.
        Assert.False(typeof(SyncEntity).IsAssignableFrom(typeof(CustomerRepresentative)));

        // And the till's customer record carries none of it — by reflection, so a well-meaning
        // DTO edit fails here loudly instead of replicating a field nothing on a till reads.
        var members = typeof(CustomerDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(nameof(Customer.Kind), members);
        Assert.DoesNotContain(nameof(Customer.IdDocumentNumber), members);
        Assert.DoesNotContain("Representative", members);
        Assert.DoesNotContain(members, m => m.Contains("Representative", StringComparison.Ordinal));
    }
}
