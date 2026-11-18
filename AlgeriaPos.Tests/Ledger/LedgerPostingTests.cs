using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Seeding;
using AlgeriaPos.Infrastructure.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Ledger;

/// <summary>
/// The rules the ledger exists to enforce, and the three ways a system like this reliably
/// reports a profit nobody made: a remittance treated as income, an opening balance treated as
/// a purchase, and branches added up instead of consolidated.
/// </summary>
public class LedgerPostingTests
{
    private static readonly Guid Algiers = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Oran = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static async Task<(SqliteConnection, PosDbContext)> NewDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Branches.Add(new Branch { Id = Algiers, NameFr = "Alger-Centre", IsOpenForOperation = true });
        db.Branches.Add(new Branch { Id = Oran, NameFr = "Oran", IsOpenForOperation = true });
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);
        return (connection, db);
    }

    private static PostingDraft Draft(string source, Guid branchId) => new()
    {
        EntryDate = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc),
        BranchId = branchId,
        SourceType = source,
        SourceId = Guid.NewGuid()
    };

    // ---------- the chart ----------

    [Fact]
    public async Task The_default_chart_is_seeded_once_and_never_reshaped_on_restart()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;

        // Startup runs this every time, so it has to be idempotent — an upgrading company has
        // no chart at all and must get one without a fresh install.
        await CoreDataSeeder.EnsureAsync(db);

        var accounts = await db.LedgerAccounts.ToListAsync();
        Assert.Equal(LedgerAccounts.Default.Count, accounts.Count);
        Assert.All(accounts, a => Assert.True(a.IsSystem));

        // A firm's accountant may rename 530; a startup that quietly restores the seeded name
        // would undo a deliberate decision.
        var cash = accounts.Single(a => a.Code == LedgerAccounts.Cash);
        cash.NameFr = "Caisse magasin";
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);
        Assert.Equal("Caisse magasin", (await db.LedgerAccounts.SingleAsync(a => a.Code == LedgerAccounts.Cash)).NameFr);
    }

    [Fact]
    public async Task A_company_created_from_the_portal_gets_the_same_core_rows_as_any_other()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(
            new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        // CompanySeeder provisions the operator-supplied minimum and nothing else. Startup
        // then has to supply what the system cannot run without — which it did not, so a
        // portal-created company had no tenders, no cash box and no chart of accounts, and
        // its till could not take money at all.
        await CompanySeeder.SeedAsync(db, new TenantOptions
        {
            Id = "demo-sarl", AdminUsername = "admin", BootstrapAdminPassword = "bootstrap-pass-123"
        });
        await CoreDataSeeder.EnsureAsync(db);

        Assert.Equal(PaymentMethodDef.BuiltInKeys.Length, await db.PaymentMethodDefs.CountAsync());
        Assert.Equal(1, await db.CashBoxes.CountAsync());
        Assert.Equal(LedgerAccounts.Default.Count, await db.LedgerAccounts.CountAsync());
    }

    [Fact]
    public async Task A_new_company_has_no_name_until_the_owner_gives_one()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(
            new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        // The company id is an internal key — "default" on a single-company install — and it
        // was seeded as the name, so the live site's first receipt was headed "default".
        await CompanySeeder.SeedAsync(db, new TenantOptions
        {
            Id = "default", AdminUsername = "admin", BootstrapAdminPassword = "bootstrap-pass-123"
        });

        var store = await db.Stores.SingleAsync();
        Assert.Equal("", store.NameAr);
        Assert.Equal("", store.NameFr);
        Assert.Equal("", store.NameEn);
    }

    [Fact]
    public void Nothing_in_the_ledger_replicates_to_a_till()
    {
        // Design §5.9: the chart of accounts and its entries are server-only. Deriving any of
        // them from SyncEntity would put the company's books on every cashier's device at the
        // next pull, silently, because the pull is driven by the base type.
        Assert.False(typeof(SyncEntity).IsAssignableFrom(typeof(LedgerAccount)));
        Assert.False(typeof(SyncEntity).IsAssignableFrom(typeof(LedgerEntry)));
        Assert.False(typeof(SyncEntity).IsAssignableFrom(typeof(LedgerEntryLine)));
    }

    // ---------- balance ----------

    [Fact]
    public async Task An_entry_that_does_not_balance_is_refused_at_the_moment_it_is_made()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        var wrong = Draft(LedgerSources.Receiving, Algiers)
            .Add(PostingLine.Dr(LedgerAccounts.Inventory, 1000m))
            .Add(PostingLine.Cr(LedgerAccounts.Suppliers, 900m));

        // Loud, and at the point of creation: this is the only moment the difference can be
        // attributed to the module that caused it. Found later from a trial balance that does
        // not add up, it is a forensic exercise across every posting ever made.
        var error = await Assert.ThrowsAsync<LedgerImbalanceException>(() => poster.PostAsync(wrong));
        Assert.Contains("does not balance", error.Message);
        Assert.Empty(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task A_negative_amount_and_a_one_sided_entry_are_both_refused()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        // A negative debit is a credit written badly. Allowed once, every report that sums
        // debits is wrong and nothing says so.
        await Assert.ThrowsAsync<LedgerImbalanceException>(() => poster.PostAsync(
            Draft(LedgerSources.Receiving, Algiers)
                .Add(PostingLine.Dr(LedgerAccounts.Inventory, -50m))
                .Add(PostingLine.Cr(LedgerAccounts.Suppliers, -50m))));

        await Assert.ThrowsAsync<LedgerImbalanceException>(() => poster.PostAsync(
            Draft(LedgerSources.Receiving, Algiers)
                .Add(new PostingLine(LedgerAccounts.Inventory, 10m, 10m))
                .Add(PostingLine.Cr(LedgerAccounts.Suppliers, 0m))));
    }

    [Fact]
    public async Task A_document_posts_once_however_many_times_it_arrives()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        var sourceId = Guid.NewGuid();
        PostingDraft Same() => new PostingDraft
        {
            EntryDate = DateTime.UtcNow, BranchId = Algiers,
            SourceType = LedgerSources.Sale, SourceId = sourceId
        }
            .Add(PostingLine.Dr(LedgerAccounts.Cash, 1190m))
            .Add(PostingLine.Cr(LedgerAccounts.Sales, 1000m))
            .Add(PostingLine.Cr(LedgerAccounts.TaxCollected, 190m));

        Assert.NotNull(await poster.PostAsync(Same()));
        await db.SaveChangesAsync();

        // A till re-pushes its outbox whenever it reconnects. Recognising the same takings
        // twice would be a straight overstatement of revenue.
        Assert.Null(await poster.PostAsync(Same()));
        await db.SaveChangesAsync();
        Assert.Single(await db.LedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task A_correction_is_a_reversal_and_leaves_the_original_where_it_was()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        var original = await poster.PostAsync(Draft(LedgerSources.Receiving, Algiers)
            .Add(PostingLine.Dr(LedgerAccounts.Inventory, 4000m))
            .Add(PostingLine.Cr(LedgerAccounts.Suppliers, 4000m)));
        await db.SaveChangesAsync();

        var reversal = await poster.ReverseAsync(original!, "Delivery never arrived", "manager");
        await db.SaveChangesAsync();

        // Nothing is deleted: both the wrong figure and its correction stay visible, exactly
        // as a Sale is append-only.
        Assert.Equal(2, await db.LedgerEntries.CountAsync());
        Assert.True(reversal.IsReversal);
        Assert.Equal(original!.Id, reversal.ReversalOfEntryId);
        Assert.Equal(4000m, reversal.Lines.Single(l => l.Credit > 0).Credit);
        Assert.Equal(LedgerAccounts.Inventory, (await db.LedgerAccounts.FindAsync(reversal.Lines.Single(l => l.Credit > 0).AccountId))!.Code);

        var lines = await db.LedgerEntryLines.ToListAsync();
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    // ---------- trap 1: a remittance must not touch the P&L ----------

    [Fact]
    public async Task A_remittance_moves_an_asset_and_never_reaches_profit_or_loss()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        // The manager collects 50 000 from Oran for the central safe. Oran calls it صادر
        // because money left its box; that is direction at a location, not profit. This is the
        // production posting, not a replica of it — a test that builds its own entry would
        // still pass on the day the endpoint started posting to the P&L.
        await poster.PostAsync(TreasuryPosting.Remittance(
            Guid.NewGuid(), Oran, Algiers, "Oran", "Caisse centrale", 50_000m, "REM-1", "manager"));
        await db.SaveChangesAsync();

        var accounts = await db.LedgerAccounts.ToDictionaryAsync(a => a.Id);
        var lines = await db.LedgerEntryLines.ToListAsync();

        // Post it as an expense at the branch and income at the safe and the P&L inflates on
        // both sides while real profit never moved (design §5.8).
        Assert.DoesNotContain(lines, l => accounts[l.AccountId].Type is LedgerAccountType.Income or LedgerAccountType.Expense);
        Assert.All(lines, l => Assert.Equal(LedgerAccounts.Cash, accounts[l.AccountId].Code));

        // Each branch's cash position moved by exactly the amount, in opposite directions.
        Assert.Equal(50_000m, lines.Where(l => l.BranchId == Algiers).Sum(l => l.Debit - l.Credit));
        Assert.Equal(-50_000m, lines.Where(l => l.BranchId == Oran).Sum(l => l.Debit - l.Credit));

        var reports = new LedgerReports(db);
        var group = await reports.ProfitAndLossAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), null, default);
        Assert.Equal(0m, group.NetProfit);

        // And it is nothing at branch level either, where the entry is deliberately kept.
        var atOran = await reports.ProfitAndLossAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), Oran, default);
        Assert.Equal(0m, atOran.NetProfit);
    }

    // ---------- trap 2: an opening balance is not a purchase ----------

    [Fact]
    public async Task An_opening_balance_posts_to_equity_and_shows_no_cost_and_no_revenue()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        // Day one: Oran already holds 800 000 of stock bought years ago by a company that was
        // not using this system.
        await poster.PostAsync(StockPosting.Opening(
            Guid.NewGuid(), Oran, "Oran",
            [new ValuedLine("FER12", 500_000m), new ValuedLine("CIM50", 300_000m)]));
        await db.SaveChangesAsync();

        var accounts = await db.LedgerAccounts.ToDictionaryAsync(a => a.Id);
        var lines = await db.LedgerEntryLines.ToListAsync();

        // Post it as a purchase and the first period shows a cost that never occurred; post it
        // as income and it shows a profit that never happened (design §5.3).
        Assert.DoesNotContain(lines, l => accounts[l.AccountId].Code == LedgerAccounts.Suppliers);
        Assert.DoesNotContain(lines, l => accounts[l.AccountId].Code == LedgerAccounts.CostOfGoodsSold);
        Assert.DoesNotContain(lines, l => accounts[l.AccountId].Type is LedgerAccountType.Income or LedgerAccountType.Expense);
        Assert.Equal(LedgerAccountType.Equity, accounts[lines.Single(l => l.Credit > 0).AccountId].Type);

        var reports = new LedgerReports(db);
        var pl = await reports.ProfitAndLossAsync(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(1), null, default);
        Assert.Equal(0m, pl.TotalIncome);
        Assert.Equal(0m, pl.TotalExpenses);

        // The goods are on the balance sheet at their value, against opening-balance equity,
        // and the sheet balances — which is the whole point of not inventing a supplier.
        var sheet = await reports.BalanceSheetAsync(DateTime.UtcNow.AddDays(1), null, default);
        Assert.Equal(800_000m, sheet.TotalAssets);
        Assert.Equal(800_000m, sheet.TotalEquity);
        Assert.Equal(0m, sheet.TotalLiabilities);
        Assert.True(sheet.Balanced);
    }


    // ---------- what the other stock documents mean ----------

    [Fact]
    public async Task Receiving_raises_inventory_against_the_supplier_and_never_an_expense()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        await poster.PostAsync(StockPosting.Receiving(
            Guid.NewGuid(), Algiers, "BL-908",
            [new ValuedLine("CIM50", 42_500m), new ValuedLine("SAB", 7_500m)]));
        await db.SaveChangesAsync();

        var accounts = await db.LedgerAccounts.ToDictionaryAsync(a => a.Id);
        var lines = await db.LedgerEntryLines.ToListAsync();

        // Goods bought are an asset until they are sold; the cost belongs to the period they
        // leave in, not the period they arrive in.
        Assert.DoesNotContain(lines, l => accounts[l.AccountId].Type is LedgerAccountType.Income or LedgerAccountType.Expense);
        Assert.Equal(50_000m, lines.Where(l => accounts[l.AccountId].Code == LedgerAccounts.Inventory).Sum(l => l.Debit));
        Assert.Equal(50_000m, lines.Single(l => accounts[l.AccountId].Code == LedgerAccounts.Suppliers).Credit);
    }

    [Fact]
    public async Task Moving_stock_inside_the_states_it_is_owned_in_posts_nothing_at_all()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;

        // Available to maintenance is the same merchandise in a different place. Posting it
        // would show the company buying and selling from itself every time a pump goes in for
        // repair; the quantity buckets already carry the distinction.
        Assert.Null(StockPosting.StateChange(
            Guid.NewGuid(), Algiers, StockState.Available, StockState.Maintenance, 9_000m, "POMP", null));
        Assert.Null(StockPosting.StateChange(
            Guid.NewGuid(), Algiers, StockState.OnRent, StockState.Available, 9_000m, "POMP", null));
    }

    [Fact]
    public async Task Scrap_and_a_write_off_are_different_events_and_post_differently()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        // Damaged beyond use but still worth something: an asset at a different value.
        await poster.PostAsync(StockPosting.StateChange(
            Guid.NewGuid(), Algiers, StockState.Available, StockState.Scrap, 12_000m, "FER12", "crushed")!);
        // Gone: a straight loss, which is exactly what separates a write-off from a state.
        await poster.PostAsync(StockPosting.StateChange(
            Guid.NewGuid(), Algiers, StockState.Scrap, StockState.WrittenOff, 12_000m, "FER12", "unsellable")!);
        await db.SaveChangesAsync();

        var accounts = await db.LedgerAccounts.ToDictionaryAsync(a => a.Id);
        var byCode = (await db.LedgerEntryLines.ToListAsync())
            .GroupBy(l => accounts[l.AccountId].Code)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));

        Assert.Equal(-12_000m, byCode[LedgerAccounts.Inventory]);
        Assert.Equal(0m, byCode[LedgerAccounts.ScrapInventory]);   // in and straight back out
        Assert.Equal(12_000m, byCode[LedgerAccounts.StockLoss]);   // the loss, once
    }

    // ---------- a cheque clearing is not revenue ----------

    [Fact]
    public async Task Settling_a_cheque_moves_a_receivable_and_never_creates_revenue()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);

        var cleared = new SalePayment { Id = Guid.NewGuid(), MethodKey = "cheque", Amount = 250_000m, InstrumentNumber = "C-4471" };
        var bounced = new SalePayment { Id = Guid.NewGuid(), MethodKey = "cheque", Amount = 80_000m, InstrumentNumber = "C-4472" };

        await poster.PostAsync(TreasuryPosting.Settlement(cleared, PaymentStatus.Confirmed, Algiers, "manager"));
        await poster.PostAsync(TreasuryPosting.Settlement(bounced, PaymentStatus.Bounced, Algiers, "manager"));
        await db.SaveChangesAsync();

        var accounts = await db.LedgerAccounts.ToDictionaryAsync(a => a.Id);
        var lines = await db.LedgerEntryLines.ToListAsync();

        // The sale was recognised when it happened. Recognising it again on collection would
        // report the same money twice — the most expensive way to be wrong here.
        Assert.DoesNotContain(lines, l => accounts[l.AccountId].Type is LedgerAccountType.Income or LedgerAccountType.Expense);

        var byCode = lines.GroupBy(l => accounts[l.AccountId].Code).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
        Assert.Equal(250_000m, byCode[LedgerAccounts.Bank]);                  // paper became money
        Assert.Equal(80_000m, byCode[LedgerAccounts.Customers]);              // paper became plain debt
        Assert.Equal(-330_000m, byCode[LedgerAccounts.NotesReceivable]);      // and left the cheques

        var reports = new LedgerReports(db);
        var pl = await reports.ProfitAndLossAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), null, default);
        Assert.Equal(0m, pl.NetProfit);
    }

    // ---------- trap 3: consolidating is not adding the branches up ----------

    [Fact]
    public async Task Consolidation_eliminates_movements_between_the_companys_own_branches()
    {
        var (connection, db) = await NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var poster = new LedgerPoster(db);
        var day = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

        // Real trade: each branch sells to an outside customer.
        foreach (var (branch, amount) in new[] { (Algiers, 100_000m), (Oran, 60_000m) })
            await poster.PostAsync(new PostingDraft
            {
                EntryDate = day, BranchId = branch,
                SourceType = LedgerSources.Sale, SourceId = Guid.NewGuid()
            }
                .Add(PostingLine.Dr(LedgerAccounts.Cash, amount))
                .Add(PostingLine.Cr(LedgerAccounts.Sales, amount)));

        // Goods moving from the depot to Oran at cost. Booked by the receiving branch as a
        // purchase and by the sending one as a sale, this is counted twice and group profit
        // comes out larger than reality — the single most common error in multi-branch
        // reporting (design §5.10). Here it is one balanced asset move, marked.
        await poster.PostAsync(StockPosting.Transfer(
            Guid.NewGuid(), Algiers, Oran, "Alger-Centre", "Oran", 30_000m, "FER12"));

        // The shape that actually inflates a group: the depot charges Oran 5 000 for work on
        // its own asset, Oran capitalises it, and the depot books income. Every figure is
        // right at each branch and the group has earned 5 000 from itself. Nothing raises this
        // today — which is exactly why the elimination needs a test rather than an assumption,
        // because the first module that does will not come with one.
        await poster.PostAsync(new PostingDraft
        {
            EntryDate = day, BranchId = Algiers,
            SourceType = "internal-charge", SourceId = Guid.NewGuid(),
            IsInterBranch = true, CounterpartBranchId = Oran
        }
            .Add(PostingLine.Dr(LedgerAccounts.Inventory, 5_000m, Oran))
            .Add(PostingLine.Cr(LedgerAccounts.MaintenanceIncome, 5_000m, Algiers)));

        await db.SaveChangesAsync();

        var reports = new LedgerReports(db);
        var from = day.AddDays(-1);
        var to = day.AddDays(1);

        var atAlgiers = await reports.ProfitAndLossAsync(from, to, Algiers, default);
        var atOran = await reports.ProfitAndLossAsync(from, to, Oran, default);
        var group = await reports.ProfitAndLossAsync(from, to, null, default);

        // A branch sees what happened to it, the internal charge included: at branch level it
        // is real income, and dropping it there would understate the depot.
        Assert.Equal(105_000m, atAlgiers.TotalIncome);
        Assert.Equal(60_000m, atOran.TotalIncome);

        // The group sees only what it earned from outside. Adding the branches up gives
        // 165 000; the company sold 160 000 to customers and the other 5 000 it billed itself.
        Assert.Equal(160_000m, group.TotalIncome);
        Assert.Equal(160_000m, group.NetProfit);
        Assert.Equal(165_000m, atAlgiers.NetProfit + atOran.NetProfit);
        Assert.NotEqual(atAlgiers.NetProfit + atOran.NetProfit, group.NetProfit);

        // And the report says by how much, rather than leaving a reader to work out why the
        // branches do not add up to the group.
        Assert.Equal(5_000m, group.EliminatedInterBranch);
        Assert.True(group.Consolidated);

        // Within the consolidated report the branch breakdown does add up, because it is cut
        // from the same eliminated set rather than from three separate reports.
        Assert.Equal(group.NetProfit, group.ByBranch.Sum(b => b.NetProfit));

        // The stock transfer moved no value for the group: inventory is where it was, and the
        // 5 000 Oran capitalised never existed either.
        var sheet = await reports.BalanceSheetAsync(to, null, default);
        Assert.DoesNotContain(sheet.Assets, a => a.Code == LedgerAccounts.Inventory);
        Assert.Equal(160_000m, sheet.TotalAssets);
        Assert.True(sheet.Balanced);

        // A single branch's sheet balances through the inter-branch current account, which is
        // where the claim on the group sits. Without it a branch sheet is simply short by
        // whatever has moved in or out.
        var oranSheet = await reports.BalanceSheetAsync(to, Oran, default);
        Assert.True(oranSheet.Balanced);
        Assert.Contains(oranSheet.Equity, e => e.Code == LedgerAccounts.InterBranchCurrentAccount);
    }
}
