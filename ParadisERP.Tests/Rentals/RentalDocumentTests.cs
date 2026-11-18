using System.Globalization;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Customers;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Rentals;
using ParadisERP.Infrastructure.Seeding;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Engine = ParadisERP.Application.Rentals;
using static ParadisERP.Tests.Rentals.RentalTestPaper;

namespace ParadisERP.Tests.Rentals;

/// <summary>
/// The document rules, as opposed to the arithmetic.
///
/// <para>
/// <see cref="RentalSettlementTests"/> already covers what a contract costs and
/// <see cref="RentalRulesTests"/> what may be raised. These are the things that only exist once
/// the engine is wired to a database: that a contract locks when it is acted on, that it closes
/// on the right condition, that issuing and returning move the same stock buckets the stock
/// module moves, that a return taken at the wrong branch does not leave two branches' balances
/// wrong, and that the rent invoiced when the goods went out is not billed a second time when
/// they come back.
/// </para>
/// </summary>
public partial class RentalDocumentTests
{
    private static readonly Guid Algiers = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Oran = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Scaffold = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Coupler = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Contractor = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Company = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OtherCompany = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid Legacy = Guid.Parse("88888888-8888-8888-8888-888888888888");

    // One site each (answer 9: every contract names one), and a second, closed one for the contractor.
    private static readonly Guid ContractorSite = Guid.Parse("99999999-0000-0000-0000-000000000001");
    private static readonly Guid CompanySite = Guid.Parse("99999999-0000-0000-0000-000000000002");
    private static readonly Guid OtherCompanySite = Guid.Parse("99999999-0000-0000-0000-000000000003");
    private static readonly Guid LegacySite = Guid.Parse("99999999-0000-0000-0000-000000000004");
    private static readonly Guid ClosedSite = Guid.Parse("99999999-0000-0000-0000-000000000005");

    private static Guid SiteFor(Guid customerId) =>
        customerId == Company ? CompanySite
        : customerId == OtherCompany ? OtherCompanySite
        : customerId == Legacy ? LegacySite
        : ContractorSite;

    /// <summary>A contract at its customer's site, unless the test named one.</summary>
    private static UpsertRentalContractRequest AtSite(UpsertRentalContractRequest r) => Sited(r, SiteFor(r.CustomerId));

    /// <summary>The five identity fields typed on a contract, as the clerk types them for a customer with nothing on file.</summary>
    private static UpsertRentalContractRequest Identity(UpsertRentalContractRequest r) => r with
    {
        SignatoryIdType = "drivingLicence", SignatoryIdNumber = r.SignatoryIdNumber ?? "0123456",
        SignatoryIdIssuer = "Wilaya d'Alger", SignatoryIdIssuedOn = new DateOnly(2020, 1, 15),
        SignatoryBirthDate = r.SignatoryBirthDate ?? new DateOnly(1985, 6, 1)
    };

    /// <summary>A company whose day is an hour ahead of UTC, so "the local day" is not "the UTC day".</summary>
    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    private sealed class Harness(SqliteConnection connection, PosDbContext db, RentalDocuments rentals) : IAsyncDisposable
    {
        public PosDbContext Db { get; } = db;
        public RentalDocuments Rentals { get; } = rentals;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }

        /// <summary>
        /// A second reader over the same database that counts every command it sends, for the
        /// tests that assert how many round trips a question costs rather than what it answers.
        /// </summary>
        public (PosDbContext Db, RentalDocuments Rentals, CommandCounter Counter) Counting()
        {
            var counter = new CommandCounter();
            var counted = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>()
                .UseSqlite(connection).AddInterceptors(counter).Options);
            return (counted, new RentalDocuments(counted, new LedgerPoster(counted), new Algeria()), counter);
        }

        public Task<ProductStock?> StockAsync(Guid productId, Guid branchId) =>
            Db.ProductStocks.AsNoTracking().FirstOrDefaultAsync(s => s.ProductId == productId && s.BranchId == branchId);

        /// <summary>Every ledger line of one document kind, by account code. Summed here: SQLite stores decimal as TEXT.</summary>
        public async Task<Dictionary<string, decimal>> PostedAsync(string sourceType)
        {
            var accounts = await Db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
            var entries = await Db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
                .Where(e => e.SourceType == sourceType).ToListAsync();
            return entries.SelectMany(e => e.Lines)
                .GroupBy(l => accounts[l.AccountId])
                .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
        }

        /// <summary>Whether any ledger line anywhere, of any document, touches one account code.</summary>
        public async Task<bool> AnyLineOnAsync(string accountCode)
        {
            var account = await Db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Code == accountCode);
            if (account is null) return false;
            var entries = await Db.LedgerEntries.AsNoTracking().Include(e => e.Lines).ToListAsync();
            return entries.SelectMany(e => e.Lines).Any(l => l.AccountId == account.Id);
        }
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Count++;
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// What a confirmed receipt naming the contract does (answers 15, 22): the money that pays it
    /// in full, then the closing rule. Written as the row it is, because this harness has no
    /// receivables service; ReceivableDocuments calls the same rule when a real receipt lands.
    /// </summary>
    private static async Task<bool> PayInFullAsync(Harness h, Guid contractId)
    {
        var standing = await RentalClosure.StandingAsync(h.Db, contractId);
        var contract = await h.Db.RentalContracts.AsNoTracking().FirstAsync(x => x.Id == contractId);
        h.Db.ReceiptVouchers.Add(new ReceiptVoucher
        {
            Number = "RCV-T" + Guid.NewGuid().ToString("N")[..6], CustomerId = contract.CustomerId, BranchId = Algiers,
            MethodKey = "cash", Behavior = PaymentBehavior.Cash, Status = PaymentStatus.Confirmed,
            Amount = standing.Remaining, ContractId = contractId, ConfirmedAtUtc = DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();
        var closed = await RentalClosure.TryCloseAsync(h.Db, contractId);
        await h.Db.SaveChangesAsync();
        return closed;
    }

    private static async Task<Harness> NewAsync(decimal availableAtAlgiers = 200, decimal availableAtOran = 0)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Stores.Add(new Store { NameFr = "Échafaudages SARL", RentalOverdueMultiplier = 1.5m });
        db.Branches.Add(new Branch { Id = Algiers, NameFr = "Alger-Centre", IsMain = true, IsOpenForOperation = true });
        db.Branches.Add(new Branch { Id = Oran, NameFr = "Oran", IsOpenForOperation = true });
        db.Products.Add(new Product
        {
            Id = Scaffold, Sku = "ECH-01", NameFr = "Échafaudage", IsRentable = true,
            CostPrice = 1200m, AssetUnitValue = 800m
        });
        db.Products.Add(new Product
        {
            Id = Coupler, Sku = "COL-01", NameFr = "Collier", IsRentable = true, CostPrice = 90m
        });
        db.ProductStocks.Add(new ProductStock { ProductId = Scaffold, BranchId = Algiers, StockQty = availableAtAlgiers });
        db.ProductStocks.Add(new ProductStock { ProductId = Scaffold, BranchId = Oran, StockQty = availableAtOran });
        db.ProductStocks.Add(new ProductStock { ProductId = Coupler, BranchId = Algiers, StockQty = 1000 });
        // A person, so a contract raised on him derives its signatory from him and every
        // create-then-issue test below meets the signatory rule by the rule working, not by it
        // being weakened. Beside him: a company with its authorised representative, a second
        // company with none, and a row as the migration leaves it — no kind, nobody guessed.
        db.Customers.Add(Identified(new Customer
        {
            Id = Contractor, Code = "C001", FullName = "Ben Ali BTP", CreditLimit = 5_000_000m,
            Kind = CustomerKind.Person, IdDocumentNumber = "109 876 543"
        }));
        db.Customers.Add(new Customer { Id = Company, Code = "C002", FullName = "SARL El Injaz Travaux", Kind = CustomerKind.Company });
        db.Customers.Add(new Customer { Id = OtherCompany, Code = "C003", FullName = "EURL Sahraoui", Kind = CustomerKind.Company });
        db.Customers.Add(new Customer { Id = Legacy, Code = "C004", FullName = "Client d'avant" });
        db.CustomerRepresentatives.Add(new CustomerRepresentative
        {
            CustomerId = Company, FullName = "Nadia Bouzid", IdDocumentNumber = "118 765 432",
            IdDocumentType = "nationalCard", IdDocumentIssuer = "Commune de Kouba", IdDocumentIssuedOn = new DateOnly(2021, 2, 3),
            FromUtc = DateTime.UtcNow.AddYears(-1), CreatedBy = "seed"
        });
        db.CustomerSites.Add(Site(ContractorSite, Contractor));
        db.CustomerSites.Add(Site(CompanySite, Company, "Chantier Bab Ezzouar"));
        db.CustomerSites.Add(Site(OtherCompanySite, OtherCompany, "Chantier Chéraga"));
        db.CustomerSites.Add(Site(LegacySite, Legacy, "Chantier Hydra"));
        var closed = Site(ClosedSite, Contractor, "Ancien chantier");
        closed.IsActive = false;
        db.CustomerSites.Add(closed);
        db.CustomerRepresentatives.Add(new CustomerRepresentative
        {
            CustomerId = OtherCompany, FullName = "Mourad Sahraoui", FromUtc = DateTime.UtcNow.AddYears(-1), CreatedBy = "seed"
        });
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);

        return new Harness(connection, db, new RentalDocuments(db, new LedgerPoster(db), new Algeria()));
    }

    private static DateTime Day(string iso) => DateTime.Parse(iso + "T09:00:00Z").ToUniversalTime();

    /// <summary>100 scaffolds for 30 days at 100 a day a unit, and 2,000 each if lost.</summary>
    /// <remarks>
    /// At the customer's site, with a birth date typed for a company's representative (whose row
    /// holds none); a person's own birth date on file wins over it.
    /// </remarks>
    private static UpsertRentalContractRequest Contract(decimal qty = 100, int days = 30, Guid? customerId = null) =>
        new(customerId ?? Contractor, Algiers,
            [new RentalContractLineRequest(Scaffold, qty, days, RentUnitPrice: 100, DamageUnitPrice: 2000)])
        {
            SiteId = SiteFor(customerId ?? Contractor),
            SignatoryBirthDate = new DateOnly(1979, 11, 20)
        };

    private static IssueNoteRequest Issue(Guid lineId, decimal qty = 40, string day = "2026-03-01") =>
        Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, qty)], Day(day)));

    private static Task<RentalContract> StoredAsync(Harness h, Guid contractId) =>
        h.Db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == contractId);

    // ---------- the signatory ----------

    [Fact]
    public async Task A_contract_raised_for_a_company_is_prefilled_with_the_current_representative()
    {
        await using var h = await NewAsync();

        // Nothing typed: the request says nothing about who signs.
        var created = await h.Rentals.CreateContractAsync(Contract(customerId: Company), "admin");
        Assert.True(created.Ok);

        var stored = await StoredAsync(h, created.Value!.Id);
        Assert.Equal("Nadia Bouzid", stored.SignatoryName);
        Assert.Equal("118 765 432", stored.SignatoryIdNumber);
        Assert.True(stored.SignatoryRequired);
    }

    [Fact]
    public async Task A_contract_raised_for_a_person_is_prefilled_with_the_person()
    {
        await using var h = await NewAsync();
        var created = await h.Rentals.CreateContractAsync(Contract(), "admin");

        var stored = await StoredAsync(h, created.Value!.Id);
        Assert.Equal("Ben Ali BTP", stored.SignatoryName);
        Assert.Equal("109 876 543", stored.SignatoryIdNumber);
        Assert.True(stored.SignatoryRequired);
    }

    [Fact]
    public async Task A_contract_for_an_unspecified_customer_with_no_signatory_is_a_valid_draft()
    {
        await using var h = await NewAsync();

        // Nothing to prefill from, and a blank signatory is not a validation error on a draft:
        // the refusal comes at the first issue note, which is where the paper matters.
        var created = await h.Rentals.CreateContractAsync(Contract(customerId: Legacy), "admin");
        Assert.True(created.Ok);

        var stored = await StoredAsync(h, created.Value!.Id);
        Assert.Null(stored.SignatoryName);
        Assert.Null(stored.SignatoryIdNumber);
        Assert.True(stored.SignatoryRequired);
    }

    [Fact]
    public async Task Nobody_signs_for_a_company_but_one_of_its_representatives()
    {
        await using var h = await NewAsync();

        // Answer 11: nobody signs on the customer's behalf. A delegate is refused, in words, and
        // nothing is written.
        var delegated = await h.Rentals.CreateContractAsync(
            Contract(customerId: Company) with { SignatoryName = "  Yacine Meziane ", SignatoryIdNumber = " 200 111 222 " }, "admin");
        Assert.Equal(RentalFailure.Invalid, delegated.Failure);
        Assert.Equal(
            "Yacine Meziane is not a representative of this company, current or past. Nobody signs on a customer's behalf: " +
            "choose the company's representative, or record the new one in Customers first.",
            delegated.Message);
        Assert.Empty(await h.Db.RentalContracts.AsNoTracking().ToListAsync());

        // The representative named by hand, in another case and spacing, is the representative —
        // and her identity comes off her row, not off what was typed.
        var named = await h.Rentals.CreateContractAsync(
            Contract(customerId: Company) with { SignatoryName = " nadia bouzid ", SignatoryIdNumber = "typed over" }, "admin");
        var stored = await StoredAsync(h, named.Value!.Id);
        Assert.Equal("Nadia Bouzid", stored.SignatoryName);
        Assert.Equal("118 765 432", stored.SignatoryIdNumber);
        Assert.Equal("nationalCard", stored.SignatoryIdType);
        Assert.Equal("Commune de Kouba", stored.SignatoryIdIssuer);
        Assert.Equal(new DateOnly(2021, 2, 3), stored.SignatoryIdIssuedOn);
        // Her row holds no birth date, so the one typed on the contract is kept.
        Assert.Equal(new DateOnly(1979, 11, 20), stored.SignatoryBirthDate);
    }

    [Fact]
    public async Task A_former_representative_may_still_sign_for_the_company()
    {
        await using var h = await NewAsync();
        h.Db.CustomerRepresentatives.Add(new CustomerRepresentative
        {
            CustomerId = Company, FullName = "Samir Haddad", IdDocumentNumber = "090 000 111",
            FromUtc = DateTime.UtcNow.AddYears(-3), ToUtc = DateTime.UtcNow.AddYears(-1), CreatedBy = "seed"
        });
        await h.Db.SaveChangesAsync();

        // In the company's history, so he is not "anyone": the owner's rule is about strangers.
        var created = await h.Rentals.CreateContractAsync(Contract(customerId: Company) with { SignatoryName = "Samir Haddad" }, "admin");
        Assert.True(created.Ok, created.Message);
        Assert.Equal("090 000 111", (await StoredAsync(h, created.Value!.Id)).SignatoryIdNumber);
    }

    [Fact]
    public async Task A_person_signs_for_himself_whatever_name_is_typed()
    {
        await using var h = await NewAsync();

        var created = await h.Rentals.CreateContractAsync(
            Contract() with
            {
                SignatoryName = "Son frère", SignatoryIdNumber = "P 000 111", SignatoryIdType = "passport",
                SignatoryIdIssuer = "Consulat", SignatoryIdIssuedOn = new DateOnly(2024, 1, 1), SignatoryBirthDate = new DateOnly(1990, 1, 1)
            }, "admin");
        Assert.True(created.Ok, created.Message);

        // The customer is the signatory, and his record on file is what the paper keeps.
        var stored = await StoredAsync(h, created.Value!.Id);
        Assert.Equal("Ben Ali BTP", stored.SignatoryName);
        Assert.Equal("109 876 543", stored.SignatoryIdNumber);
        Assert.Equal("nationalCard", stored.SignatoryIdType);
        Assert.Equal("Daïra de Bab El Oued", stored.SignatoryIdIssuer);
        Assert.Equal(new DateOnly(2019, 5, 12), stored.SignatoryIdIssuedOn);
        Assert.Equal(new DateOnly(1980, 3, 4), stored.SignatoryBirthDate);
    }

    [Fact]
    public async Task A_person_with_no_identity_on_file_takes_what_the_contract_says()
    {
        await using var h = await NewAsync();
        h.Db.Customers.Add(new Customer { Id = Guid.NewGuid(), Code = "C009", FullName = "Nouveau Client", Kind = CustomerKind.Person });
        await h.Db.SaveChangesAsync();
        var customerId = (await h.Db.Customers.AsNoTracking().FirstAsync(c => c.Code == "C009")).Id;
        h.Db.CustomerSites.Add(Site(Guid.NewGuid(), customerId));
        await h.Db.SaveChangesAsync();
        var siteId = (await h.Db.CustomerSites.AsNoTracking().FirstAsync(s => s.CustomerId == customerId)).Id;

        var created = await h.Rentals.CreateContractAsync(
            Identity(Contract(customerId: customerId) with { SiteId = siteId, SignatoryName = "Quelqu'un d'autre" }), "admin");
        var stored = await StoredAsync(h, created.Value!.Id);

        // The name is still his; only the fields his record lacks are filled from the paper.
        Assert.Equal("Nouveau Client", stored.SignatoryName);
        Assert.Equal("drivingLicence", stored.SignatoryIdType);
        Assert.Equal("0123456", stored.SignatoryIdNumber);
        Assert.Equal(new DateOnly(1979, 11, 20), stored.SignatoryBirthDate);
    }

    [Fact]
    public async Task An_unknown_identity_document_type_is_refused()
    {
        await using var h = await NewAsync();
        var refused = await h.Rentals.CreateContractAsync(Contract(customerId: Legacy) with { SignatoryIdType = "carteVitale" }, "admin");
        Assert.Equal(RentalFailure.Invalid, refused.Failure);
        Assert.Contains("passport", refused.Message);
    }

    [Fact]
    public async Task Moving_a_draft_to_another_company_recomputes_the_signatory()
    {
        await using var h = await NewAsync();
        var created = (await h.Rentals.CreateContractAsync(Contract(customerId: Company), "admin")).Value!;
        Assert.Equal("Nadia Bouzid", (await StoredAsync(h, created.Id)).SignatoryName);

        // Same draft, other company, nothing typed: A's representative must not follow it.
        var moved = await h.Rentals.UpdateContractAsync(created.Id, Contract(customerId: OtherCompany));
        Assert.True(moved.Ok);

        var stored = await StoredAsync(h, created.Id);
        Assert.Equal("Mourad Sahraoui", stored.SignatoryName);
        Assert.Null(stored.SignatoryIdNumber);
        Assert.True(stored.SignatoryRequired);
    }

    [Fact]
    public async Task The_first_issue_note_is_refused_while_the_signatory_is_blank_and_moves_nothing()
    {
        await using var h = await NewAsync(availableAtAlgiers: 200);
        var contract = (await h.Rentals.CreateContractAsync(Contract(customerId: Legacy), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        var refused = await h.Rentals.IssueAsync(contract.Id, Issue(lineId), null, "storeman");
        Assert.Equal(RentalFailure.Invalid, refused.Failure);
        Assert.Equal(
            "This contract has no signatory. Open the contract, edit it and enter who signs before the first issue note.",
            refused.Message);

        // A refused note is not a note: no stock moved, nothing written, the contract still a draft.
        Assert.Equal(200m, (await h.StockAsync(Scaffold, Algiers))!.StockQty);
        Assert.Equal(0m, (await h.StockAsync(Scaffold, Algiers))!.OnRentQty);
        Assert.Empty(await h.Db.IssueNotes.AsNoTracking().ToListAsync());
        Assert.Null((await StoredAsync(h, contract.Id)).LockedAtUtc);

        // The clerk opens the contract, edits it, enters who signs — and the same note goes through.
        var fixedUp = await h.Rentals.UpdateContractAsync(contract.Id,
            Identity(Contract(customerId: Legacy) with { SignatoryName = "Karim Ben Ali" }));
        Assert.True(fixedUp.Ok);
        var lineAfterEdit = (await h.Db.RentalContractLines.AsNoTracking().FirstAsync(l => l.ContractId == contract.Id)).Id;

        var issued = await h.Rentals.IssueAsync(contract.Id, Issue(lineAfterEdit), null, "storeman");
        Assert.True(issued.Ok);
        Assert.NotNull((await StoredAsync(h, contract.Id)).LockedAtUtc);
        Assert.Equal(40m, (await h.StockAsync(Scaffold, Algiers))!.OnRentQty);
    }

    [Fact]
    public async Task A_name_alone_is_not_enough_for_the_first_note_the_rest_of_the_identity_is_named()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(
            Contract(customerId: Legacy) with { SignatoryName = "Karim Ben Ali" }, "admin")).Value!;
        Assert.Null((await StoredAsync(h, contract.Id)).SignatoryIdNumber);

        // Answer 10 as revised on 2026-10-02: the document and its number are required; the
        // issuer, the issue date and the birth date are optional. Both missing parts are named.
        var refused = await h.Rentals.IssueAsync(contract.Id, Issue(contract.Lines.Single().Id), null, null);
        Assert.Equal(RentalFailure.Invalid, refused.Failure);
        Assert.Equal(
            "The signatory's identity is incomplete: the identity document type and its number are missing. " +
            "Open the contract, edit it and complete the signatory's identity before the first issue note.",
            refused.Message);
        Assert.Null((await StoredAsync(h, contract.Id)).LockedAtUtc);

        await h.Rentals.UpdateContractAsync(contract.Id, Identity(Contract(customerId: Legacy) with { SignatoryName = "Karim Ben Ali" }));
        var lineId = (await h.Db.RentalContractLines.AsNoTracking().FirstAsync(l => l.ContractId == contract.Id)).Id;
        var issued = await h.Rentals.IssueAsync(contract.Id, Issue(lineId), null, null);
        Assert.True(issued.Ok, issued.Message);

        // Once locked the rule is spent: a second note never asks again.
        var stored = await h.Db.RentalContracts.FirstAsync(c => c.Id == contract.Id);
        stored.SignatoryIdIssuer = null;
        await h.Db.SaveChangesAsync();
        Assert.True((await h.Rentals.IssueAsync(contract.Id, Issue(lineId, 10, "2026-03-02"), null, null)).Ok);
    }

    [Fact]
    public async Task A_contract_that_predates_the_rule_is_issued_without_a_signatory()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(customerId: Legacy), "admin")).Value!;

        // As the migration leaves every contract that existed before this release.
        var row = await h.Db.RentalContracts.FirstAsync(c => c.Id == contract.Id);
        row.SignatoryRequired = false;
        row.SignatoryName = null;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        // Valid under the old rules, and not refused now — even after an edit of the draft,
        // which leaves the flag exactly as stored.
        var edited = await h.Rentals.UpdateContractAsync(contract.Id, Contract(customerId: Legacy) with { Notes = "phone number fixed" });
        Assert.True(edited.Ok);
        Assert.False((await StoredAsync(h, contract.Id)).SignatoryRequired);

        var lineId = (await h.Db.RentalContractLines.AsNoTracking().FirstAsync(l => l.ContractId == contract.Id)).Id;
        var issued = await h.Rentals.IssueAsync(contract.Id, Issue(lineId), null, null);
        Assert.True(issued.Ok);
        Assert.Null((await StoredAsync(h, contract.Id)).SignatoryName);
    }

    [Fact]
    public async Task Changing_the_company_s_representative_does_not_touch_a_signed_contract()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(customerId: Company), "admin")).Value!;
        await h.Rentals.IssueAsync(contract.Id, Issue(contract.Lines.Single().Id), null, "storeman");
        Assert.Equal("Nadia Bouzid", (await StoredAsync(h, contract.Id)).SignatoryName);

        // The owner's sentence: the representative may leave the company, and whatever was
        // signed keeps the name of whoever signed it.
        var changed = await new CustomerRepresentatives(h.Db).ChangeAsync(
            Company, new ChangeRepresentativeRequest("Yacine Meziane", "200 111 222"), "admin");
        Assert.True(changed.Ok);
        Assert.Equal("Yacine Meziane", changed.Value!.Single(x => x.ToUtc is null).FullName);

        var stored = await StoredAsync(h, contract.Id);
        Assert.Equal("Nadia Bouzid", stored.SignatoryName);
        Assert.Equal("118 765 432", stored.SignatoryIdNumber);

        // And the next contract for the company is prefilled with the new person.
        var next = (await h.Rentals.CreateContractAsync(Contract(customerId: Company), "admin")).Value!;
        Assert.Equal("Yacine Meziane", (await StoredAsync(h, next.Id)).SignatoryName);
    }

    [Fact]
    public async Task The_second_issue_note_is_never_refused_for_the_signatory()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        Assert.True((await h.Rentals.IssueAsync(contract.Id, Issue(lineId, 40), null, null)).Ok);

        // Blanked behind the service's back: locked and required, and still not the second
        // note's business — the rule bites once, at the note that locks.
        var row = await h.Db.RentalContracts.FirstAsync(c => c.Id == contract.Id);
        row.SignatoryName = null;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var second = await h.Rentals.IssueAsync(contract.Id, Issue(lineId, 20, "2026-03-05"), null, null);
        Assert.True(second.Ok);
    }

    // ---------- the contract ----------

    [Fact]
    public async Task The_overdue_price_is_derived_from_the_multiplier_and_frozen_on_the_line()
    {
        await using var h = await NewAsync();

        var created = await h.Rentals.CreateContractAsync(Contract(), "admin");
        Assert.True(created.Ok);
        var line = created.Value!.Lines.Single();
        Assert.Equal(150m, line.OverdueUnitPrice);   // 3,000 × 1.5

        // Changing the company figure afterwards must not reach a contract already signed —
        // that is the whole reason the overdue price is a multiplier and not a second list.
        await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(3m, false));
        var again = await h.Db.RentalContractLines.AsNoTracking().FirstAsync(l => l.Id == line.Id);
        Assert.Equal(150m, again.OverdueUnitPrice);

        // The next contract signed gets the new figure.
        var later = await h.Rentals.CreateContractAsync(Contract(), "admin");
        Assert.Equal(300m, later.Value!.Lines.Single().OverdueUnitPrice);
    }

    [Fact]
    public async Task An_items_own_multiplier_beats_the_companys()
    {
        await using var h = await NewAsync();
        await h.Rentals.SetProductMultiplierAsync(Scaffold, 2m);

        var created = await h.Rentals.CreateContractAsync(Contract(), "admin");
        Assert.Equal(200m, created.Value!.Lines.Single().OverdueUnitPrice);

        // The read route agrees with what was frozen: the figure the settings screen shows for
        // the item is the figure the line was signed at, not the company's.
        var read = await h.Rentals.ProductMultiplierAsync(Scaffold);
        Assert.Equal(2m, read!.OverdueMultiplier);
        Assert.Equal(100m * read.OverdueMultiplier, created.Value.Lines.Single().OverdueUnitPrice);

        // Cleared, it falls back to the company's.
        await h.Rentals.SetProductMultiplierAsync(Scaffold, null);
        var next = await h.Rentals.CreateContractAsync(Contract(), "admin");
        Assert.Equal(150m, next.Value!.Lines.Single().OverdueUnitPrice);
    }

    [Fact]
    public async Task An_items_own_multiplier_can_be_read_back_and_listed()
    {
        await using var h = await NewAsync();

        // Nothing set: the item answers with a null override and the list is empty.
        Assert.Null((await h.Rentals.ProductMultiplierAsync(Scaffold))!.OverdueMultiplier);
        Assert.Empty(await h.Rentals.ProductMultipliersAsync());

        await h.Rentals.SetProductMultiplierAsync(Scaffold, 2m);
        Assert.Equal(2m, (await h.Rentals.ProductMultiplierAsync(Scaffold))!.OverdueMultiplier);
        var listed = Assert.Single(await h.Rentals.ProductMultipliersAsync());
        Assert.Equal(Scaffold, listed.ProductId);
        Assert.Equal("ECH-01", listed.Sku);
        Assert.Equal("Échafaudage", listed.NameFr);

        // Cleared: back to null, and off the list.
        await h.Rentals.SetProductMultiplierAsync(Scaffold, null);
        Assert.Null((await h.Rentals.ProductMultiplierAsync(Scaffold))!.OverdueMultiplier);
        Assert.Empty(await h.Rentals.ProductMultipliersAsync());

        // A deleted item is not listed and reads as unknown, whatever its column still holds.
        await h.Rentals.SetProductMultiplierAsync(Coupler, 3m);
        var coupler = await h.Db.Products.FirstAsync(p => p.Id == Coupler);
        coupler.IsDeleted = true;
        await h.Db.SaveChangesAsync();
        Assert.Empty(await h.Rentals.ProductMultipliersAsync());
        Assert.Null(await h.Rentals.ProductMultiplierAsync(Coupler));

        // An id nobody has is null, not an empty row.
        Assert.Null(await h.Rentals.ProductMultiplierAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task An_items_predefined_rental_price_is_stored_without_becoming_a_signed_price()
    {
        await using var h = await NewAsync();

        var saved = await h.Rentals.SetProductMultiplierAsync(Scaffold, null, 1250m);
        Assert.True(saved.Ok);
        Assert.Equal(1250m, saved.Value!.RentalUnitPrice);

        var read = await h.Rentals.ProductMultiplierAsync(Scaffold);
        Assert.Equal(1250m, read!.RentalUnitPrice);
        Assert.Null(read.OverdueMultiplier);
        Assert.Equal(1250m, Assert.Single(await h.Rentals.ProductMultipliersAsync()).RentalUnitPrice);

        // Updating only the multiplier is backwards-compatible and keeps the configured price.
        await h.Rentals.SetProductMultiplierAsync(Scaffold, 2m);
        Assert.Equal(1250m, (await h.Rentals.ProductMultiplierAsync(Scaffold))!.RentalUnitPrice);

        // The setting prefills clients; the contract remains the signed snapshot supplied to it.
        var contract = await h.Rentals.CreateContractAsync(Contract(), "admin");
        Assert.Equal(100m, contract.Value!.Lines.Single().RentUnitPrice);

        var refused = await h.Rentals.SetProductMultiplierAsync(Scaffold, 2m, -1m);
        Assert.False(refused.Ok);
        Assert.Equal(1250m, (await h.Rentals.ProductMultiplierAsync(Scaffold))!.RentalUnitPrice);
    }

    [Fact]
    public async Task An_overdue_price_below_the_admins_multiplier_is_refused()
    {
        await using var h = await NewAsync();

        static UpsertRentalContractRequest Signed(decimal? overdue) =>
            new(Contractor, Algiers,
                [new RentalContractLineRequest(Scaffold, 100, 30, RentUnitPrice: 3000, DamageUnitPrice: 2000, OverdueUnitPrice: overdue)])
            { SiteId = ContractorSite };

        // The company figure is 1.5×, so 4,500 is the least a late day may be signed at. A
        // manager signing 4,000 is signing away the admin's decision — refused, and the floor is
        // named so the screen can say what would be accepted.
        var below = await h.Rentals.CreateContractAsync(Signed(4000m), "manager");
        Assert.False(below.Ok);
        Assert.Equal(RentalFailure.Invalid, below.Failure);
        Assert.Contains("4500", below.Message);
        Assert.Contains("floor", below.Message);
        Assert.Empty(await h.Db.RentalContracts.AsNoTracking().ToListAsync());

        // At the floor and above it are both a negotiation the form was built for.
        Assert.Equal(4500m, (await h.Rentals.CreateContractAsync(Signed(4500m), "manager")).Value!.Lines.Single().OverdueUnitPrice);
        Assert.Equal(5000m, (await h.Rentals.CreateContractAsync(Signed(5000m), "manager")).Value!.Lines.Single().OverdueUnitPrice);

        // The item's own multiplier is the floor when it has one: 2× makes 6,000 the least.
        await h.Rentals.SetProductMultiplierAsync(Scaffold, 2m);
        var belowItem = await h.Rentals.CreateContractAsync(Signed(5000m), "manager");
        Assert.False(belowItem.Ok);
        Assert.Contains("6000", belowItem.Message);
        Assert.True((await h.Rentals.CreateContractAsync(Signed(6000m), "manager")).Ok);

        // Editing a draft cannot slip under it either.
        await h.Rentals.SetProductMultiplierAsync(Scaffold, null);
        var draft = (await h.Rentals.CreateContractAsync(Signed(null), "manager")).Value!;
        var edited = await h.Rentals.UpdateContractAsync(draft.Id, Signed(4499m));
        Assert.False(edited.Ok);
        Assert.Contains("4500", edited.Message);
        Assert.Equal(4500m, (await h.Db.RentalContractLines.AsNoTracking().SingleAsync(l => l.ContractId == draft.Id)).OverdueUnitPrice);
    }

    [Fact]
    public async Task The_overdue_floor_refusal_names_its_figures_the_same_way_under_a_french_culture()
    {
        // The API runs under whatever culture the host machine has; an Algerian server is very
        // often fr-FR. The refusal is a single English sentence the screen prints as it stands,
        // so its figures must not become "1,5×" and "4499,99" on one machine and dots on another.
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            await using var h = await NewAsync();

            var signed = AtSite(new UpsertRentalContractRequest(Contractor, Algiers,
                [new RentalContractLineRequest(Scaffold, 100, 30, RentUnitPrice: 3000, DamageUnitPrice: 2000, OverdueUnitPrice: 4499.99m)]));

            var below = await h.Rentals.CreateContractAsync(signed, "manager");
            Assert.False(below.Ok);
            Assert.Contains("4499.99", below.Message);
            Assert.Contains("1.5×", below.Message);
            Assert.DoesNotContain(",", below.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public async Task A_contract_is_editable_until_its_first_issue_note_and_locked_afterwards()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        Assert.Null(contract.LockedAtUtc);

        var edited = await h.Rentals.UpdateContractAsync(contract.Id,
            Contract(qty: 120) with { Notes = "quantity revised before delivery" });
        Assert.True(edited.Ok);
        Assert.Equal(120m, (await h.Db.RentalContractLines.AsNoTracking()
            .FirstAsync(l => l.ContractId == contract.Id)).Qty);

        var lineId = (await h.Db.RentalContractLines.AsNoTracking().FirstAsync(l => l.ContractId == contract.Id)).Id;
        var issued = await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 40)], Day("2026-03-01"))), null, "storeman");
        Assert.True(issued.Ok);

        // Goods have gone out against these prices. An edit now would silently re-price work
        // already done, so it is refused by the server and not merely hidden on a screen.
        var locked = await h.Rentals.UpdateContractAsync(contract.Id, Contract(qty: 200));
        Assert.False(locked.Ok);
        Assert.Equal(RentalFailure.Conflict, locked.Failure);
        Assert.NotNull((await h.Db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == contract.Id)).LockedAtUtc);

        // And the contract still says what it said.
        Assert.Equal(120m, (await h.Db.RentalContractLines.AsNoTracking()
            .FirstAsync(l => l.ContractId == contract.Id)).Qty);
    }

    [Fact]
    public async Task An_issue_note_can_never_exceed_the_contracted_quantity()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 100), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 60)], Day("2026-03-01"))), null, null)).Ok);

        var tooMuch = await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 50)], Day("2026-03-05"))), null, null);
        Assert.False(tooMuch.Ok);
        Assert.Contains("exceed", tooMuch.Message);

        // Two lines of one note against the same contract line are one batch to the
        // arithmetic, so they are checked together — separately, the pair would slip past.
        var split = await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            Algiers, [new IssueNoteLineRequest(lineId, 25), new IssueNoteLineRequest(lineId, 25)], Day("2026-03-05"))),
            null, null);
        Assert.False(split.Ok);
    }

    // ---------- stock ----------

    [Fact]
    public async Task Issuing_moves_available_to_on_rent_and_the_company_still_owns_it()
    {
        await using var h = await NewAsync(availableAtAlgiers: 200);
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(contract.Lines.Single().Id, 80)], Day("2026-03-01"))),
            null, "storeman");

        var stock = await h.StockAsync(Scaffold, Algiers);
        Assert.Equal(120m, stock!.StockQty);
        Assert.Equal(80m, stock.OnRentQty);
        // Out with a customer is not sold: the same merchandise in a different place.
        Assert.Equal(200m, stock.OwnedQty);

        // And nothing at all reaches the ledger for the move itself — available, on rent and in
        // maintenance are one asset account, which is why the buckets exist.
        var movement = await h.Db.StockMovements.AsNoTracking().FirstAsync(m => m.Reason == "rental-issue");
        Assert.Equal(0m, movement.QtyDelta);
        Assert.Empty(await h.Db.LedgerEntries.AsNoTracking()
            .Where(e => e.SourceType == LedgerSources.StockState).ToListAsync());
    }

    [Fact]
    public async Task Issuing_is_refused_when_the_branch_does_not_have_the_goods()
    {
        await using var h = await NewAsync(availableAtAlgiers: 30);
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;

        var short_ = await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(contract.Lines.Single().Id, 80)], Day("2026-03-01"))),
            null, null);

        Assert.Equal(RentalFailure.Conflict, short_.Failure);
        // Nothing moved, and the contract has not locked: a refused note is not a note.
        Assert.Equal(30m, (await h.StockAsync(Scaffold, Algiers))!.StockQty);
        Assert.Null((await h.Db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == contract.Id)).LockedAtUtc);
    }

    [Fact]
    public async Task A_return_sends_each_unit_to_the_state_its_condition_says()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);

        // 70 sound, 20 repairable, 6 wrecked, 4 never seen again.
        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers,
            [new ReturnNoteLineRequest(lineId, QtyOk: 70, QtyPartialDamage: 20, MaintenanceValue: 15_000,
                QtyTotalDamage: 6, QtyMissing: 4)],
            Day("2026-03-31"))), null, "storeman");
        Assert.True(back.Ok);

        var stock = await h.StockAsync(Scaffold, Algiers);
        Assert.Equal(170m, stock!.StockQty);       // 100 never issued + 70 sound
        Assert.Equal(20m, stock.MaintenanceQty);
        Assert.Equal(6m, stock.ScrapQty);
        Assert.Equal(0m, stock.OnRentQty);
        // The four that never came back left the business; the rest is still held.
        Assert.Equal(196m, stock.OwnedQty);

        var posted = await h.PostedAsync(LedgerSources.StockState);
        Assert.Equal(6 * 1200m, posted[LedgerAccounts.ScrapInventory]);   // the wrecks, at cost
        Assert.Equal(4 * 1200m, posted[LedgerAccounts.StockLoss]);        // the loss, once
        Assert.Equal(-10 * 1200m, posted[LedgerAccounts.Inventory]);
    }

    // ---------- the return comes back to the contract's branch ----------

    [Fact]
    public async Task A_return_is_taken_at_the_contracts_branch_and_another_is_refused_in_words()
    {
        // The owner, 2026-09-30: "the return is to the same branch, then it is transferred to
        // another branch". A lorry that unloads elsewhere is a stock transfer afterwards.
        await using var h = await NewAsync(availableAtAlgiers: 200, availableAtOran: 0);
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);

        var elsewhere = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Oran, [new ReturnNoteLineRequest(lineId, QtyOk: 100)], Day("2026-03-31"))), null, "storeman");
        Assert.Equal(RentalFailure.Invalid, elsewhere.Failure);
        Assert.Contains("contract's branch", elsewhere.Message);
        Assert.Contains("Alger", elsewhere.Message);

        // Refused before anything moved: the goods are still out, from Algiers, and no note exists.
        Assert.Equal(100m, (await h.StockAsync(Scaffold, Algiers))!.OnRentQty);
        Assert.Equal(0, await h.Db.ReturnNotes.CountAsync());

        var home = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 100)], Day("2026-03-31"))), null, "storeman");
        Assert.True(home.Ok);
        Assert.Empty(home.Value!.Transfers);

        var algiers = await h.StockAsync(Scaffold, Algiers);
        Assert.Equal(200m, algiers!.StockQty);
        Assert.Equal(0m, algiers.OnRentQty);
        Assert.Equal(0, await h.Db.StockMovements.CountAsync(m => m.Reason == "rental-return-transfer"));
    }

    [Fact]
    public async Task Goods_an_admin_issued_from_another_branch_come_back_to_the_contracts_with_the_transfer_they_made()
    {
        await using var h = await NewAsync(availableAtAlgiers: 200, availableAtOran: 100);
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        // An Algiers contract served from Oran's yard.
        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Oran, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);

        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 90, QtyMissing: 10)], Day("2026-03-31"))), null, null);
        Assert.True(back.Ok);

        // The 90 that came back left Oran and arrived at Algiers; the 10 that did not never
        // arrived anywhere, so they are written off at Oran, where they were issued.
        var transfer = Assert.Single(back.Value!.Transfers);
        Assert.Equal(Oran, transfer.FromBranchId);
        Assert.Equal(Algiers, transfer.ToBranchId);
        Assert.Equal(90m, transfer.Qty);

        var algiers = await h.StockAsync(Scaffold, Algiers);
        var oran = await h.StockAsync(Scaffold, Oran);
        Assert.Equal(290m, algiers!.StockQty);
        Assert.Equal(0m, oran!.OnRentQty);
        Assert.Equal(0m, oran.OwnedQty);

        // Paired movements, exactly as an explicit transfer writes them.
        var movements = await h.Db.StockMovements.AsNoTracking()
            .Where(m => m.Reason == "rental-return-transfer").ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(-90m, movements.Single(m => m.BranchId == Oran).QtyDelta);
        Assert.Equal(90m, movements.Single(m => m.BranchId == Algiers).QtyDelta);

        // And in the ledger it is one balanced asset move, marked so consolidation drops it.
        var entry = await h.Db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
            .SingleAsync(e => e.SourceType == LedgerSources.StockTransfer);
        Assert.True(entry.IsInterBranch);
        Assert.Equal(2, entry.Lines.Select(l => l.BranchId).Distinct().Count());
        Assert.Equal(90 * 1200m, entry.Lines.Sum(l => l.Debit));
    }

    [Fact]
    public async Task Fifo_decides_which_branch_a_returned_unit_came_from()
    {
        await using var h = await NewAsync(availableAtAlgiers: 60, availableAtOran: 40);
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        // 60 out of Algiers first, then 40 out of Oran a week later.
        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 60)], Day("2026-03-01"))), null, null);
        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Oran, [new IssueNoteLineRequest(lineId, 40)], Day("2026-03-08"))), null, null);

        // 70 come back to Algiers, the contract's branch. Oldest batch closes first, so 60 of them
        // are Algiers's own and 10 are Oran's — the same match the money is worked out with.
        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 70)], Day("2026-03-20"))), null, null);

        Assert.Equal(10m, Assert.Single(back.Value!.Transfers).Qty);
        var algiers = await h.StockAsync(Scaffold, Algiers);
        var oran = await h.StockAsync(Scaffold, Oran);
        Assert.Equal(0m, algiers!.OnRentQty);
        Assert.Equal(70m, algiers.StockQty);
        Assert.Equal(30m, oran!.OnRentQty);
        Assert.Equal(0m, oran.StockQty);
    }

    // ---------- closing ----------

    [Fact]
    public async Task A_contract_closes_when_everything_issued_is_settled_and_not_before()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 100), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        // Only 60 of the contracted 100 ever go out.
        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 60)], Day("2026-03-01"))), null, null);

        var partial = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 50)], Day("2026-03-20"))), null, null);
        Assert.False(partial.Value!.ContractClosed);
        Assert.Equal(RentalContractStatus.Open,
            (await h.Db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == contract.Id)).Status);

        // The last ten are written off rather than returned. A unit settles whether it walked
        // back in or was declared lost, so this clears the line.
        var last = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyMissing: 10)], Day("2026-03-25"))), null, null);

        // Everything issued is settled, so the goods are back — but the contract stays open until it
        // is paid (owner, 2026-10-02, answers 15 and 22).
        Assert.False(last.Value!.ContractClosed);
        var back = await h.Db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == contract.Id);
        Assert.Equal(RentalContractStatus.Open, back.Status);
        Assert.NotNull(back.GoodsBackAtUtc);
        Assert.True((await RentalClosure.StandingAsync(h.Db, contract.Id)).Owed > 0);

        // Paid in full, it closes.
        Assert.True(await PayInFullAsync(h, contract.Id));
        var closed = await h.Db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == contract.Id);
        Assert.Equal(RentalContractStatus.Closed, closed.Status);
        Assert.NotNull(closed.ClosedAtUtc);
        // The 40 never issued had no bearing on it.
        Assert.Equal(100m, contract.Lines.Single().Qty);
    }

    [Fact]
    public async Task A_contract_that_has_issued_nothing_is_not_closed_it_has_not_started()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;

        var settlement = await h.Rentals.SettleAsync(contract.Id);
        Assert.False(settlement!.IsClosed);
        Assert.Equal(RentalContractStatus.Open,
            (await h.Db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == contract.Id)).Status);
    }

    [Fact]
    public async Task A_closed_contract_takes_no_further_documents()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 10)], Day("2026-03-01"))), null, null);
        await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 10)], Day("2026-03-11"))), null, null);
        Assert.True(await PayInFullAsync(h, contract.Id));

        Assert.Equal(RentalFailure.Conflict, (await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 5)], Day("2026-03-12"))), null, null)).Failure);
        Assert.Equal(RentalFailure.Conflict, (await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 1)], Day("2026-03-13"))), null, null)).Failure);
    }

    // ---------- money ----------

    [Fact]
    public async Task The_full_period_is_invoiced_when_the_goods_go_out_and_never_billed_again()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 100, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        // 100 units × 30 days × 100 a day = 300,000, invoiced the moment the lorry leaves.
        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);

        var issue = await h.PostedAsync(LedgerSources.RentalIssue);
        Assert.Equal(300_000m, issue[LedgerAccounts.Customers]);
        Assert.Equal(-300_000m, issue[LedgerAccounts.RentalIncome]);
        Assert.Equal(300_000m, (await h.Db.Customers.AsNoTracking().FirstAsync(c => c.Id == Contractor)).Balance);

        // Back exactly on time. The hire was already recognised, so the return note posts
        // nothing at all — billing it again is the one way this module could double revenue.
        await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 100)], Day("2026-03-31"))), null, null);

        Assert.Empty(await h.PostedAsync(LedgerSources.RentalReturn));
        Assert.Equal(300_000m, (await h.Db.Customers.AsNoTracking().FirstAsync(c => c.Id == Contractor)).Balance);

        // The rent is recorded against the contract all the same — it is on the customer's
        // copy — and marked as already posted so nobody bills it twice from the charges.
        var rent = await h.Db.RentalCharges.AsNoTracking().Where(c => c.Kind == RentalChargeKind.Rent).ToListAsync();
        Assert.All(rent, c => Assert.False(c.IsPosted));
        Assert.Equal(300_000m, rent.Sum(c => c.Amount));
    }

    [Fact]
    public async Task Each_issue_note_reports_and_bills_only_what_it_put_out()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 100, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        var first = await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 60)], Day("2026-03-01"))), null, null);
        var second = await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 40)], Day("2026-03-10"))), null, null);

        // 60 × 30 × 100 and 40 × 30 × 100. The second delivery must not report the first one's
        // money beside its own, which is what a contract-wide running total would do.
        Assert.Equal(180_000m, first.Value!.PeriodRent);
        Assert.Equal(120_000m, second.Value!.PeriodRent);

        // And the two entries are separate documents, each leading back to its own note.
        var posted = await h.PostedAsync(LedgerSources.RentalIssue);
        Assert.Equal(300_000m, posted[LedgerAccounts.Customers]);
        Assert.Equal(2, await h.Db.LedgerEntries.CountAsync(e => e.SourceType == LedgerSources.RentalIssue));
    }

    [Fact]
    public async Task An_early_return_gives_back_the_days_the_customer_never_used()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 100, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);

        // Back after 10 days of a 30-day period: 20 days × 100 units × 100 a day = 200,000 back.
        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 100)], Day("2026-03-11"))), null, null);

        Assert.Equal(200_000m, back.Value!.Note.UnusedRent);
        // The customer owes 300,000 and has paid nothing, so it comes off the debt and no money
        // leaves the box. A voucher paid to someone who owes more just moves cash for nothing.
        Assert.Equal(200_000m, back.Value.Note.ReliefDebtReduction);
        Assert.Equal(0m, back.Value.Note.ReliefPaymentVoucher);

        var posted = await h.PostedAsync(LedgerSources.RentalReturn);
        // Revenue given back on the line it was recognised on, against the customer's account.
        Assert.Equal(200_000m, posted[LedgerAccounts.RentalIncome]);
        Assert.Equal(-200_000m, posted[LedgerAccounts.Customers]);
        Assert.Equal(100_000m, (await h.Db.Customers.AsNoTracking().FirstAsync(c => c.Id == Contractor)).Balance);
    }

    [Fact]
    public async Task Overdue_repairs_and_compensation_each_reach_their_own_account()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 100, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);

        // Ten days late, with 10 needing repair, 5 wrecked and 5 lost.
        await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers,
            [new ReturnNoteLineRequest(lineId, QtyOk: 80, QtyPartialDamage: 10, MaintenanceValue: 24_000,
                QtyTotalDamage: 5, QtyMissing: 5)],
            Day("2026-04-10"))), null, null);

        var posted = await h.PostedAsync(LedgerSources.RentalReturn);
        // 100 units × 10 overdue days × 150 a day (4,500 ÷ 30).
        Assert.Equal(-150_000m, posted[LedgerAccounts.LateIncome]);
        // Repair the customer pays for is income — the same work on the company's own asset
        // would be an expense, and which it is was decided at the moment of return.
        Assert.Equal(-24_000m, posted[LedgerAccounts.MaintenanceIncome]);
        // 10 units that will not come back, at the 2,000 frozen on the contract.
        Assert.Equal(-20_000m, posted[LedgerAccounts.DamageCompensation]);
        // Rent for the agreed period is not here: it was invoiced when the goods went out.
        Assert.False(posted.ContainsKey(LedgerAccounts.RentalIncome));
        Assert.Equal(194_000m, posted[LedgerAccounts.Customers]);

        var entries = await h.Db.LedgerEntries.AsNoTracking().Include(e => e.Lines).ToListAsync();
        Assert.All(entries, e => Assert.Equal(e.Lines.Sum(l => l.Debit), e.Lines.Sum(l => l.Credit)));
    }

    [Fact]
    public async Task A_unit_that_never_comes_back_is_charged_rent_as_well_as_its_price()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 10, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 10)], Day("2026-03-01"))), null, null);

        // Declared lost on 10 April, reported on 21 March. Rent stops on the report: the
        // company could write it off and stop waiting from that day.
        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyMissing: 10, ReportedOnUtc: Day("2026-03-21"))],
            Day("2026-04-10"))), null, null);

        var charges = back.Value!.Charges;
        Assert.Equal(20_000m, charges.Where(c => c.Kind == RentalChargeKind.Missing).Sum(c => c.Amount));
        // 20 days of the agreed period, not 40 — and no overdue, because the report landed
        // inside the period. The hire and the asset are two different things being paid for.
        Assert.Equal(20_000m, charges.Where(c => c.Kind == RentalChargeKind.Rent).Sum(c => c.Amount));
        Assert.DoesNotContain(charges, c => c.Kind == RentalChargeKind.Overdue);

        // The report date survives on the line, so the days can be explained months later.
        var line = await h.Db.ReturnNoteLines.AsNoTracking().FirstAsync(l => l.ReturnNoteId == back.Value.Note.Id);
        Assert.Equal(Day("2026-03-21"), line.ReportedOnUtc);
    }

    // ---------- extensions ----------

    [Fact]
    public async Task An_extension_records_the_day_it_was_raised_and_the_deadline_it_had_to_beat()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 100, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);

        // Signed on 20 March, inside a period that runs to 31 March: a decision, not a
        // consequence, and no manager needed.
        var early = await h.Rentals.ExtendAsync(contract.Id,
            new ContractExtensionRequest(ExtraDays: 10, ContractLineId: lineId, RaisedAtUtc: Day("2026-03-20")),
            isManager: false, null, "clerk");

        Assert.True(early.Ok);
        Assert.False(early.Value!.IsRetroactive);
        Assert.Equal(Day("2026-03-20"), early.Value.RaisedOnUtc);
        Assert.Equal(new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc), early.Value.PeriodEndsOnUtc);
        // At the rental price, not the overdue one — that is the whole difference.
        Assert.Equal(100m, early.Value.UnitPrice);

        // It moves no stock and posts nothing: the goods are already on site, and the days are
        // charged as they are consumed.
        Assert.Empty(await h.Db.LedgerEntries.AsNoTracking()
            .Where(e => e.SourceType == LedgerSources.RentalReturn).ToListAsync());
        Assert.Equal(0m, (await h.StockAsync(Scaffold, Algiers))!.OnRentQty - 100m);
    }

    [Fact]
    public async Task A_retroactive_extension_needs_a_manager_and_a_written_reason()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 100, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);

        var request = new ContractExtensionRequest(ExtraDays: 10, ContractLineId: lineId, RaisedAtUtc: Day("2026-04-05"));

        // Left open to everyone, every overdue quietly becomes an extension and the overdue
        // line stops meaning anything.
        var clerk = await h.Rentals.ExtendAsync(contract.Id, request, isManager: false, null, "clerk");
        Assert.Equal(RentalFailure.Forbidden, clerk.Failure);

        var unexplained = await h.Rentals.ExtendAsync(contract.Id, request, isManager: true, null, "manager");
        Assert.Equal(RentalFailure.Invalid, unexplained.Failure);

        var approved = await h.Rentals.ExtendAsync(contract.Id,
            request with { Reason = "Site flooded; agreed by telephone on 30 March." },
            isManager: true, Guid.NewGuid(), "manager");

        Assert.True(approved.Ok);
        Assert.True(approved.Value!.IsRetroactive);
        // Both dates are stored, so the approval can be re-verified once the batches it was
        // measured against are long since back.
        Assert.Equal(Day("2026-04-05"), approved.Value.RaisedOnUtc);
        Assert.Equal(new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc), approved.Value.PeriodEndsOnUtc);
        Assert.Equal("manager", approved.Value.ApprovedByName);
    }

    [Fact]
    public async Task Extension_days_are_charged_as_they_are_consumed_and_at_the_rental_price()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 100, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);
        await h.Rentals.ExtendAsync(contract.Id,
            new ContractExtensionRequest(ExtraDays: 10, ContractLineId: lineId, RaisedAtUtc: Day("2026-03-20")),
            isManager: false, null, "clerk");

        // Back on 5 April: 30 agreed days, then 5 of the 10 extension days. No overdue at all.
        await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 100)], Day("2026-04-05"))), null, null);

        var posted = await h.PostedAsync(LedgerSources.RentalReturn);
        // 100 units × 5 days × 100 a day, at the rental rate — not the overdue one.
        Assert.Equal(-50_000m, posted[LedgerAccounts.RentalIncome]);
        Assert.False(posted.ContainsKey(LedgerAccounts.LateIncome));

        // The five days the extension bought and nobody used cost nothing and refund nothing.
        var note = await h.Db.ReturnNotes.AsNoTracking().FirstAsync();
        Assert.Equal(0m, note.UnusedRent);

        var extensionCharges = await h.Db.RentalCharges.AsNoTracking()
            .Where(c => c.ExtensionId != null).ToListAsync();
        Assert.All(extensionCharges, c => Assert.True(c.IsPosted));
    }

    [Fact]
    public async Task An_extension_cannot_be_raised_against_a_contract_with_nothing_out()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;

        var nothing = await h.Rentals.ExtendAsync(contract.Id,
            new ContractExtensionRequest(ExtraDays: 10, ContractLineId: contract.Lines.Single().Id),
            isManager: true, null, "manager");

        Assert.Equal(RentalFailure.Conflict, nothing.Failure);
    }

    // ---------- accessories ----------

    [Fact]
    public async Task An_accessory_rents_at_zero_and_is_still_charged_when_it_goes_missing()
    {
        await using var h = await NewAsync();
        var created = await h.Rentals.CreateContractAsync(AtSite(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [
                new RentalContractLineRequest(Scaffold, 10, 30, RentUnitPrice: 100, DamageUnitPrice: 2000),
                new RentalContractLineRequest(Coupler, 200, 0, RentUnitPrice: 0, DamageUnitPrice: 500,
                    ParentLineIndex: 0)
            ])), "admin");
        Assert.True(created.Ok);

        var scaffolds = created.Value!.Lines.First(l => l.ProductId == Scaffold);
        var couplers = created.Value.Lines.First(l => l.ProductId == Coupler);
        Assert.Equal(scaffolds.Id, couplers.ParentLineId);

        await h.Rentals.IssueAsync(created.Value.Id, Driven(new IssueNoteRequest(
            Algiers,
            [new IssueNoteLineRequest(scaffolds.Id, 10), new IssueNoteLineRequest(couplers.Id, 200)],
            Day("2026-03-01"))), null, null);

        // Only the scaffolds were invoiced: 10 × 30 × 100.
        Assert.Equal(30_000m, (await h.PostedAsync(LedgerSources.RentalIssue))[LedgerAccounts.Customers]);

        // Forty couplers short. "No value" applied to the rent line only.
        var back = await h.Rentals.ReturnAsync(created.Value.Id, Driven(new ReturnNoteRequest(
            Algiers,
            [
                new ReturnNoteLineRequest(scaffolds.Id, QtyOk: 10),
                new ReturnNoteLineRequest(couplers.Id, QtyOk: 160, QtyMissing: 40)
            ],
            Day("2026-03-31"))), null, null);

        Assert.Equal(20_000m, back.Value!.Charges
            .Where(c => c.Kind == RentalChargeKind.Missing).Sum(c => c.Amount));
        // Every unit is settled, so the goods are back; closing waits for the money.
        Assert.NotNull((await StoredAsync(h, created.Value.Id)).GoodsBackAtUtc);
    }

    // ---------- the time zone ----------

    [Fact]
    public async Task Days_are_counted_in_the_companys_own_day_not_the_utc_one()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 10, days: 5), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        // Issued at 23:30 UTC on 1 March, which in Algiers is already 00:30 on the 2nd.
        await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            Algiers, [new IssueNoteLineRequest(lineId, 10)],
            new DateTime(2026, 3, 1, 23, 30, 0, DateTimeKind.Utc))), null, null);

        // Back at 10:00 UTC on 4 March, which is still the 4th in Algiers. Two local days —
        // counting off the UTC date would make it three and charge the customer a day they
        // never had, every time, on the early shift.
        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 10)],
            new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc))), null, null);

        // 5 agreed days, 2 used, 3 unused × 10 units × 100 a day. The UTC answer would be 2,000.
        Assert.Equal(3_000m, back.Value!.Note.UnusedRent);
    }

    // ---------- append-only ----------

    [Fact]
    public async Task A_return_note_cannot_be_slipped_in_behind_one_already_taken()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, null);
        await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 40)], Day("2026-03-20"))), null, null);

        // FIFO consumes the batches in date order, so an earlier note arriving later would
        // re-decide charges the customer has already been handed.
        var backdated = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 10)], Day("2026-03-10"))), null, null);

        Assert.False(backdated.Ok);
        Assert.Contains("before one already taken", backdated.Message);
    }

    [Fact]
    public async Task A_return_bigger_than_what_is_still_out_is_refused_at_the_document()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;

        await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 50)], Day("2026-03-01"))), null, null);

        // Once it is saved the FIFO match has nothing to charge the surplus against, so it has
        // to stop here.
        var tooMany = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 60)], Day("2026-03-20"))), null, null);

        Assert.False(tooMany.Ok);
        Assert.Contains("exceed", tooMany.Message);
        Assert.Equal(50m, (await h.StockAsync(Scaffold, Algiers))!.OnRentQty);
    }

    // ---------- the list's own figures ----------

    [Fact]
    public async Task The_list_totals_say_what_is_out_and_what_it_has_cost_without_settling_each_contract()
    {
        await using var h = await NewAsync();

        // The first contract: 100 out on 1 March for 30 days; 40 settled on 20 April — twenty
        // days late — 38 sound and 2 that never came back.
        var first = (await h.Rentals.CreateContractAsync(Contract(qty: 100, days: 30), "admin")).Value!;
        var lineId = first.Lines.Single().Id;
        await h.Rentals.IssueAsync(first.Id, Issue(lineId, 100, "2026-03-01"), null, null);
        var back = await h.Rentals.ReturnAsync(first.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 38, QtyMissing: 2)], Day("2026-04-20"))), null, null);
        Assert.True(back.Ok);

        // The second: signed and nothing else.
        var second = (await h.Rentals.CreateContractAsync(Contract(qty: 10), "admin")).Value!;

        var totals = await h.Rentals.TotalsAsync([first.Id, second.Id]);
        var settled = (await h.Rentals.SettleAsync(first.Id))!;

        // The row says what the engine says — read off the stored documents, not recomputed.
        var row = totals[first.Id];
        Assert.Equal(100m, row.IssuedQty);
        Assert.Equal(40m, row.SettledQty);
        Assert.Equal(settled.StillOutQty, row.StillOutQty);
        Assert.Equal(60m, row.StillOutQty);
        Assert.Equal(settled.ChargeTotal, row.ChargeTotal);
        Assert.Equal(settled.Total(Engine.RentalChargeKind.Overdue), row.OverdueTotal);
        // 40 units × 20 days × 150 a day: the late chip has something to stand on.
        Assert.Equal(120_000m, row.OverdueTotal);
        Assert.True(row.ChargeTotal > row.OverdueTotal);

        // Nothing leaks between contracts, and a contract with no documents is all zeros.
        Assert.Equal(ContractTotals.Zero, totals[second.Id]);
        Assert.Equal(2, totals.Count);
    }

    [Fact]
    public async Task Totals_are_asked_for_in_one_round_trip_per_table_not_per_contract()
    {
        await using var h = await NewAsync(availableAtAlgiers: 1000);

        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 4), "admin")).Value!;
            await h.Rentals.IssueAsync(contract.Id, Issue(contract.Lines.Single().Id, 4, "2026-03-01"), null, null);
            ids.Add(contract.Id);
        }

        // Issue lines, return lines, charges: three commands whatever the number of contracts.
        // The N+1 this replaces was one settlement per contract — a five-query load plus a full
        // engine run — sixty times over on a yard of sixty contracts.
        var (counted, rentals, counter) = h.Counting();
        await using (counted)
        {
            var totals = await rentals.TotalsAsync(ids);
            Assert.Equal(25, totals.Count);
            Assert.All(totals.Values, t => Assert.Equal(4m, t.StillOutQty));
            Assert.Equal(3, counter.Count);
        }
    }

    // ---------- the deposit, and the sync boundary ----------

    [Fact]
    public async Task The_deposit_is_stored_and_computes_nothing_while_it_is_switched_off()
    {
        await using var h = await NewAsync();

        var settings = await h.Rentals.SettingsAsync();
        Assert.False(settings.DepositEnabled);
        Assert.False(Engine.RentalDeposit.IsVisible(settings));

        var contract = (await h.Rentals.CreateContractAsync(
            Contract() with { DepositAmount = 50_000m }, "admin")).Value!;

        // Off is the default, and off means the request's figure is not stored: writing it in
        // would make it appear the day the switch is turned on, against a contract whose
        // customer never agreed one. Held() is the one place that decides this.
        Assert.Equal(0m, contract.DepositAmount);
        Assert.Equal(0m, Engine.RentalDeposit.Held(settings, 50_000m));
        Assert.NotNull(typeof(RentalContract).GetProperty(nameof(RentalContract.DepositAmount)));
    }

    private static Task<RentalResult<Store>> DepositSwitchAsync(Harness h, bool on) =>
        h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, on));

    [Fact]
    public async Task The_deposit_is_stored_and_shown_once_the_switch_is_on()
    {
        await using var h = await NewAsync();
        await DepositSwitchAsync(h, on: true);
        var settings = await h.Rentals.SettingsAsync();
        Assert.True(Engine.RentalDeposit.IsVisible(settings));

        var contract = (await h.Rentals.CreateContractAsync(Contract() with { DepositAmount = 50_000m }, "admin")).Value!;
        Assert.Equal(50_000m, (await StoredAsync(h, contract.Id)).DepositAmount);
        Assert.Equal(50_000m, Engine.RentalDeposit.Held(settings, contract.DepositAmount));

        // A draft's deposit moves with its prices…
        Assert.True((await h.Rentals.UpdateContractAsync(contract.Id, Contract() with { DepositAmount = 60_000m })).Ok);
        Assert.Equal(60_000m, (await StoredAsync(h, contract.Id)).DepositAmount);

        // …and the first issue note freezes it with them: a signed figure.
        var lineId = (await h.Db.RentalContractLines.AsNoTracking().SingleAsync(l => l.ContractId == contract.Id)).Id;
        Assert.True((await h.Rentals.IssueAsync(contract.Id, Issue(lineId), null, null)).Ok);
        var afterLock = await h.Rentals.UpdateContractAsync(contract.Id, Contract() with { DepositAmount = 70_000m });
        Assert.Equal(RentalFailure.Conflict, afterLock.Failure);
        Assert.Equal(60_000m, (await StoredAsync(h, contract.Id)).DepositAmount);

        // Not yet owed back: the goods are still out.
        var settlement = (await h.Rentals.SettleAsync(contract.Id))!;
        Assert.Equal(0m, Engine.RentalDeposit.ToReturn(settings, 60_000m, settlement.IsClosed));
    }

    [Fact]
    public async Task A_negative_deposit_is_refused()
    {
        await using var h = await NewAsync();
        await DepositSwitchAsync(h, on: true);

        var created = await h.Rentals.CreateContractAsync(Contract() with { DepositAmount = -1m }, "admin");
        Assert.Equal(RentalFailure.Invalid, created.Failure);
        Assert.Contains("negative", created.Message);
        Assert.Empty(await h.Db.RentalContracts.AsNoTracking().ToListAsync());

        var draft = (await h.Rentals.CreateContractAsync(Contract() with { DepositAmount = 1_000m }, "admin")).Value!;
        var edited = await h.Rentals.UpdateContractAsync(draft.Id, Contract() with { DepositAmount = -5m });
        Assert.Equal(RentalFailure.Invalid, edited.Failure);
        Assert.Equal(1_000m, (await StoredAsync(h, draft.Id)).DepositAmount);

        // Off, the figure is ignored entirely — a client still sending one must not start
        // failing the day the setting is turned back off.
        await DepositSwitchAsync(h, on: false);
        var ignored = await h.Rentals.CreateContractAsync(Contract() with { DepositAmount = -1m }, "admin");
        Assert.True(ignored.Ok);
        Assert.Equal(0m, ignored.Value!.DepositAmount);
    }

    [Fact]
    public async Task Switching_the_deposit_off_hides_it_without_erasing_it()
    {
        await using var h = await NewAsync();
        await DepositSwitchAsync(h, on: true);
        var contract = (await h.Rentals.CreateContractAsync(Contract() with { DepositAmount = 50_000m }, "admin")).Value!;

        await DepositSwitchAsync(h, on: false);
        var off = await h.Rentals.SettingsAsync();
        Assert.False(Engine.RentalDeposit.IsVisible(off));
        Assert.Equal(0m, Engine.RentalDeposit.Held(off, contract.DepositAmount));
        // The row keeps its figure: the switch decides what is shown, never what was signed.
        Assert.Equal(50_000m, (await StoredAsync(h, contract.Id)).DepositAmount);

        await DepositSwitchAsync(h, on: true);
        Assert.Equal(50_000m, Engine.RentalDeposit.Held(await h.Rentals.SettingsAsync(), (await StoredAsync(h, contract.Id)).DepositAmount));
    }

    [Fact]
    public async Task The_deposit_never_nets_against_what_the_contract_owes()
    {
        // The same hire run twice — once without a deposit, once with 50,000 held — must charge
        // the same money, move the customer's balance by the same amount, post the same entries,
        // and put nothing on 419 or any other account: a deposit is a promise on a contract, not
        // money, until a document collects it.
        static async Task<(decimal Charged, decimal BalanceDelta, Dictionary<string, decimal> Issue, Dictionary<string, decimal> Return)> RunAsync(bool deposit)
        {
            await using var h = await NewAsync();
            if (deposit) await DepositSwitchAsync(h, on: true);

            var before = (await h.Db.Customers.AsNoTracking().FirstAsync(c => c.Id == Contractor)).Balance;
            var request = Contract(qty: 10, days: 30) with { DepositAmount = deposit ? 50_000m : 0m };
            var contract = (await h.Rentals.CreateContractAsync(request, "admin")).Value!;
            Assert.Equal(deposit ? 50_000m : 0m, contract.DepositAmount);

            var lineId = contract.Lines.Single().Id;
            await h.Rentals.IssueAsync(contract.Id, Issue(lineId, 10, "2026-03-01"), null, null);
            // Ten days late, one lost: overdue and compensation both in play.
            var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
                Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 9, QtyMissing: 1)], Day("2026-04-10"))), null, null);
            Assert.True(back.Ok);
            Assert.NotNull((await StoredAsync(h, contract.Id)).GoodsBackAtUtc);

            Assert.False(await h.AnyLineOnAsync(LedgerAccounts.CustomerDeposits));
            var after = (await h.Db.Customers.AsNoTracking().FirstAsync(c => c.Id == Contractor)).Balance;
            return ((await h.Rentals.SettleAsync(contract.Id))!.ChargeTotal, after - before,
                await h.PostedAsync(LedgerSources.RentalIssue), await h.PostedAsync(LedgerSources.RentalReturn));
        }

        var without = await RunAsync(deposit: false);
        var with = await RunAsync(deposit: true);

        Assert.True(with.Charged > 0);
        Assert.Equal(without.Charged, with.Charged);
        Assert.Equal(without.BalanceDelta, with.BalanceDelta);
        Assert.Equal(without.Issue, with.Issue);
        Assert.Equal(without.Return, with.Return);
    }

    [Fact]
    public async Task The_deposit_is_owed_back_when_the_last_line_clears()
    {
        await using var h = await NewAsync();
        await DepositSwitchAsync(h, on: true);
        var settings = await h.Rentals.SettingsAsync();

        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 10, days: 30) with { DepositAmount = 50_000m }, "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        await h.Rentals.IssueAsync(contract.Id, Issue(lineId, 10, "2026-03-01"), null, null);

        // Half back: still out, nothing owed back yet.
        await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 5)], Day("2026-03-20"))), null, null);
        var open = (await h.Rentals.SettleAsync(contract.Id))!;
        Assert.False(open.IsClosed);
        Assert.Equal(0m, Engine.RentalDeposit.ToReturn(settings, 50_000m, open.IsClosed));

        // The last unit clears: the goods are back — stating the deposit to return, never waiting
        // for it — and the whole figure is owed back, untouched by what was charged. The contract
        // itself closes once it is paid (answers 15, 22).
        var last = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 5)], Day("2026-04-05"))), null, null);
        Assert.True(last.Ok);
        Assert.NotNull((await StoredAsync(h, contract.Id)).GoodsBackAtUtc);
        Assert.True(await PayInFullAsync(h, contract.Id));
        Assert.Equal(RentalContractStatus.Closed, (await StoredAsync(h, contract.Id)).Status);

        var closed = (await h.Rentals.SettleAsync(contract.Id))!;
        Assert.True(closed.ChargeTotal > 0);
        Assert.Equal(50_000m, Engine.RentalDeposit.Held(settings, 50_000m));
        Assert.Equal(50_000m, Engine.RentalDeposit.ToReturn(settings, 50_000m, closed.IsClosed));
        // No money moved for it: there is no payment voucher yet, and nothing reached 419.
        Assert.False(await h.AnyLineOnAsync(LedgerAccounts.CustomerDeposits));
    }

    [Fact]
    public void No_rental_document_replicates_to_a_till()
    {
        // Design §5.8: renting is back-office and server-only, and the largest new module in
        // the product therefore does not touch the sync engine at all. Deriving any of these
        // from SyncEntity is all it would take to put the company's rental book on every
        // cashier's device at the next pull — silently, because the pull is driven by the
        // base type.
        foreach (var type in new[]
                 {
                     typeof(RentalContract), typeof(RentalContractLine),
                     typeof(IssueNote), typeof(IssueNoteLine),
                     typeof(ReturnNote), typeof(ReturnNoteLine),
                     typeof(ContractExtension), typeof(RentalCharge),
                     // Who signs for a company is back-office business for the same reason.
                     typeof(CustomerRepresentative)
                 })
            Assert.False(typeof(SyncEntity).IsAssignableFrom(type), type.Name);
    }

    [Fact]
    public void The_stored_charge_kinds_are_the_engines_charge_kinds()
    {
        // The domain has no project references, so the column cannot use the engine's enum and
        // mirrors it instead. This is the check that stops the two drifting: a kind added on
        // one side and not the other would post to whatever account the gap happened to land on.
        Assert.Equal(
            Enum.GetNames<Engine.RentalChargeKind>(),
            Enum.GetNames<RentalChargeKind>());
        foreach (var name in Enum.GetNames<RentalChargeKind>())
            Assert.Equal(
                (int)Enum.Parse<Engine.RentalChargeKind>(name),
                (int)Enum.Parse<RentalChargeKind>(name));
    }
}
