using System.Security.Claims;
using System.Text.Json;
using ParadisERP.API.Endpoints;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.FixedAssets;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Security;
using ParadisERP.Tests.Valuation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Ledger;

/// <summary>
/// REN-12, Phase M7a (docs/accounting/fixed-assets.md): hire equipment as fixed assets. The arithmetic on its own (the
/// REN-12 worked example), then the shop of the stock-document tests — pipes hired out, 20 at Algiers and 10 at Oran, cost
/// price 600 — before and after the switch-over: off, every document posts exactly as before; on, the register takes them.
/// </summary>
public class FixedAssetRegisterTests
{
    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new() { CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers" };
    }

    private sealed class At(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    internal const string Asset = "215";
    internal const string Depreciation = "2815";
    internal const string Charge = "681";
    internal const string Disposal = "652";
    private const string Adjustment = "1028";

    private static readonly Guid Frames = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Yard = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static FixedAssetMovement Row(FixedAssetMovementKind kind, decimal qty, decimal cost, decimal depreciation, DateOnly month,
        DateOnly? start = null) => new()
    {
        ProductId = Frames, BranchId = Yard, Kind = kind, Qty = qty, Cost = cost, Depreciation = depreciation, StartMonth = start,
        AtUtc = month.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc).AddDays(9),
    };

    // ---------- the arithmetic: REN-12's worked example ----------

    [Fact]
    public void Two_thousand_frames_at_6500_over_five_years_lose_2_6_million_a_year_and_are_worth_5_2_million_after_three()
    {
        var january = new DateOnly(2027, 1, 1);
        var engine = new FixedAssetEngine(january, new FixedAssetPolicy(60, new Dictionary<Guid, int>(), 0m));
        engine.Apply(Row(FixedAssetMovementKind.Opening, 2_000m, 13_000_000m, 0m, january, january), january);

        engine.DepreciateThrough(new DateOnly(2027, 12, 1));
        var pool = engine.Find(Frames, Yard)!;
        Assert.Equal(13_000_000m, pool.Cost);
        Assert.Equal(2_600_000m, pool.Depreciation);
        Assert.Equal(12, engine.Charges.Count);
        // 13,000,000 / 60 = 216,666.666…: each month the rounded cumulative figure less the one before, so no thousandth is lost.
        Assert.All(engine.Charges, c => Assert.InRange(c.Amount, 216_666.666m, 216_666.667m));

        engine.DepreciateThrough(new DateOnly(2029, 12, 1));
        Assert.Equal(5_200_000m, pool.NetBookValue);
        engine.DepreciateThrough(new DateOnly(2031, 12, 1));
        Assert.Equal(0m, pool.NetBookValue);
        Assert.Equal(60, engine.Charges.Count);
    }

    [Fact]
    public void A_residual_value_is_never_depreciated()
    {
        var january = new DateOnly(2027, 1, 1);
        var engine = new FixedAssetEngine(january, new FixedAssetPolicy(60, new Dictionary<Guid, int>(), 10m));
        engine.Apply(Row(FixedAssetMovementKind.Opening, 2_000m, 13_000_000m, 0m, january, january), january);
        engine.DepreciateThrough(new DateOnly(2032, 6, 1));
        Assert.Equal(1_300_000m, engine.Find(Frames, Yard)!.NetBookValue);
    }

    [Fact]
    public void Units_disposed_of_leave_at_their_share_of_cost_and_depreciation_and_a_purchase_starts_the_month_after()
    {
        var january = new DateOnly(2027, 1, 1);
        var engine = new FixedAssetEngine(january, new FixedAssetPolicy(60, new Dictionary<Guid, int> { [Frames] = 60 }, 0m));
        engine.Apply(Row(FixedAssetMovementKind.Opening, 2_000m, 13_000_000m, 0m, january, january), january);

        // A year later, 200 frames are lost: a tenth of the cost and of the year's depreciation.
        var next = new DateOnly(2028, 1, 1);
        engine.DepreciateThrough(next.AddMonths(-1));
        var (cost, depreciation) = engine.Share(Frames, Yard, 200m);
        Assert.Equal((1_300_000m, 260_000m), (cost, depreciation));
        engine.Apply(Row(FixedAssetMovementKind.Disposal, 200m, cost, depreciation, next), next);
        var pool = engine.Find(Frames, Yard)!;
        Assert.Equal((1_800m, 11_700_000m, 2_340_000m), (pool.Qty, pool.Cost, pool.Depreciation));
        // The same net book value per unit as before: 5,200 each.
        Assert.Equal(5_200m, pool.NetBookValue / pool.Qty);

        // 100 bought in January at 6,500: nothing in January, 1,800 × 6,500 / 60 + 100 × 6,500 / 60 from February.
        engine.Apply(Row(FixedAssetMovementKind.Acquisition, 100m, 650_000m, 0m, next, next.AddMonths(1)), next);
        engine.DepreciateThrough(next);
        Assert.Equal(195_000m, engine.Charges[^1].Amount);
        engine.DepreciateThrough(next.AddMonths(1));
        Assert.Equal(205_833.333m, engine.Charges[^1].Amount);
    }

    // ---------- the shop ----------

    internal static DateOnly ThisMonth()
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers"));
        return new DateOnly(local.Year, local.Month, 1);
    }

    /// <summary>The stock-document shop, with the accounts the accountant adds to the chart for the register.</summary>
    internal static async Task<(SqliteConnection Connection, PosDbContext Db)> ShopAsync()
    {
        var (connection, db) = await StockDocumentsCostSwitchTests.ShopAsync();
        db.LedgerAccounts.AddRange(
            new LedgerAccount { Code = Asset, Type = LedgerAccountType.Asset, NameFr = "Matériel de location", NameAr = "معدات", NameEn = "Hire equipment" },
            new LedgerAccount { Code = Depreciation, Type = LedgerAccountType.Asset, NameFr = "Amortissements", NameAr = "اهتلاكات", NameEn = "Accumulated depreciation" },
            new LedgerAccount { Code = Charge, Type = LedgerAccountType.Expense, NameFr = "Dotations", NameAr = "مخصصات", NameEn = "Depreciation" },
            new LedgerAccount { Code = Disposal, Type = LedgerAccountType.Expense, NameFr = "Moins-values", NameAr = "خسائر", NameEn = "Disposals" },
            new LedgerAccount { Code = Adjustment, Type = LedgerAccountType.Equity, NameFr = "Ajustement", NameAr = "تسوية", NameEn = "Adjustment" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (connection, db);
    }

    internal static FixedAssetSettingsStore Store(PosDbContext db) => new(db, new Algeria());

    private static async Task MapAsync(PosDbContext db, bool openingAdjustment = false)
    {
        var codes = new Dictionary<string, string>
        {
            [FixedAssetAccountSlots.Asset] = Asset, [FixedAssetAccountSlots.AccumulatedDepreciation] = Depreciation,
            [FixedAssetAccountSlots.DepreciationExpense] = Charge, [FixedAssetAccountSlots.Disposal] = Disposal,
        };
        if (openingAdjustment) codes[FixedAssetAccountSlots.OpeningAdjustment] = Adjustment;
        var accounts = await db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Code, a => a.Id);
        var saved = await Store(db).UpdateMapAsync(new UpdateManufacturingAccountMapRequest(
            codes.Select(x => new ManufacturingAccountMapLine(x.Key, accounts[x.Value])).ToList()), "accountant");
        Assert.Null(saved.Code);
    }

    private static async Task ChooseAsync(PosDbContext db, string treatment, DateOnly month, string opening = "CostInForce")
    {
        var saved = await Store(db).UpdateAsync(new UpdateFixedAssetSettingsRequest(
            treatment, "StraightLine", 60, 0m, opening, $"{month:yyyy-MM}"), "accountant");
        Assert.True(saved.Code is null, saved.Error);
    }

    private static FixedAssetSwitchOver SwitchOver(PosDbContext db, DateOnly month) =>
        new(db, new LedgerPoster(db), new Algeria(), new At(month.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc).AddDays(9)));

    /// <summary>Fixed assets from three months ago: chosen, mapped and switched over during that month.</summary>
    internal static async Task<DateOnly> SwitchedAsync(PosDbContext db)
    {
        var month = ThisMonth().AddMonths(-3);
        await MapAsync(db);
        await ChooseAsync(db, "FixedAssets", month);
        var preview = await SwitchOver(db, month).PreviewAsync();
        Assert.Empty(preview.Blocking);
        var posted = await SwitchOver(db, month).PostAsync(new PostFixedAssetSwitchRequest(preview.StockValue, preview.OpeningTotal), "accountant");
        Assert.True(posted.Code is null, posted.Error);
        db.ChangeTracker.Clear();
        return month;
    }

    internal static FixedAssetDepreciationReport Report(PosDbContext db) => new(db, TimeProvider.System);

    internal static FixedAssetLedgerPostings Postings(PosDbContext db) => new(db, Report(db), new LedgerPoster(db), new Algeria());

    /// <summary>Debit less credit per account code and branch, over every entry of one source.</summary>
    internal static async Task<Dictionary<(string Code, Guid Branch), decimal>> ByBranchAsync(PosDbContext db, string sourceType)
    {
        var lines = await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == sourceType).ToListAsync();
        return lines.GroupBy(l => (l.Account!.Code, l.BranchId)).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    internal static Task<Dictionary<string, decimal>> PostedAsync(PosDbContext db, string sourceType) =>
        StockDocumentsCostSwitchTests.PostedAsync(db, sourceType);

    [Fact]
    public async Task Until_the_switch_over_is_posted_every_hire_document_posts_as_before_and_the_register_records_nothing()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var admin = await StockDocumentsCostSwitchTests.AdminAsync(db);
        // Everything chosen and mapped, fixed assets selected — but not switched over: still stock.
        await MapAsync(db);
        await ChooseAsync(db, "FixedAssets", ThisMonth());

        await StockDocumentsCostSwitchTests.RentAndReturnAsync(db, issued: 4, ok: 1, wrecked: 1, lost: 2);
        var counted = await StockDocumentsCostSwitchTests.AdjustAsync(db, admin, -1);

        var states = await PostedAsync(db, LedgerSources.StockState);
        Assert.Equal((600m, 1_200m, -1_800m), (states[LedgerAccounts.ScrapInventory], states[LedgerAccounts.StockLoss], states[LedgerAccounts.Inventory]));
        Assert.Equal(-600m, (await StockDocumentsCostSwitchTests.PostedAsync(db, LedgerSources.StockAdjustment, counted.DocumentId))[LedgerAccounts.Inventory]);
        Assert.Equal(0, await db.FixedAssetMovements.CountAsync());
        Assert.False(await db.LedgerEntries.AnyAsync(e => e.SourceType == LedgerSources.FixedAsset || e.SourceType == LedgerSources.FixedAssetSwitch));
        Assert.False((await new FixedAssetRegisterReport(db, TimeProvider.System).RegisterAsync(null, null, null)).Value!.Active);
    }

    [Fact]
    public async Task The_switch_over_is_refused_while_a_slot_is_unmapped_or_the_figures_moved_and_posts_once()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var month = ThisMonth();

        // Nothing chosen: the treatment is stock, and no month.
        var blank = await SwitchOver(db, month).PreviewAsync();
        Assert.Contains(blank.Blocking, b => b.Code == "treatment_stock");
        Assert.Contains(blank.Blocking, b => b.Code == "month_not_chosen");

        await ChooseAsync(db, "FixedAssets", month);
        var unmapped = await SwitchOver(db, month).PreviewAsync();
        Assert.Equal(4, unmapped.Blocking.Count(b => b.Code == "slot_unmapped"));
        var refused = await SwitchOver(db, month).PostAsync(new PostFixedAssetSwitchRequest(unmapped.StockValue, unmapped.OpeningTotal), "accountant");
        Assert.Equal((409, "slot_unmapped"), (refused.Status, refused.Code));
        Assert.Equal(0, await db.FixedAssetMovements.CountAsync());

        await MapAsync(db);
        var preview = await SwitchOver(db, month).PreviewAsync();
        Assert.Empty(preview.Blocking);
        // 20 pipes at Algiers and 10 at Oran, at the cost price, 600.
        Assert.Equal(18_000m, preview.StockValue);
        Assert.Equal((20m, 12_000m), preview.Lines.Where(l => l.BranchId == TillHarness.Algiers).Select(l => (l.Qty, l.StockValue)).Single());
        var moved = await SwitchOver(db, month).PostAsync(new PostFixedAssetSwitchRequest(17_000m, 17_000m), "accountant");
        Assert.Equal((409, FixedAssetSwitchOver.ChangedCode), (moved.Status, moved.Code));

        var posted = await SwitchOver(db, month).PostAsync(new PostFixedAssetSwitchRequest(18_000m, 18_000m), "accountant");
        Assert.True(posted.Code is null, posted.Error);
        Assert.True(posted.Value!.Posted);
        db.ChangeTracker.Clear();
        var entry = await ByBranchAsync(db, LedgerSources.FixedAssetSwitch);
        Assert.Equal(12_000m, entry[(Asset, TillHarness.Algiers)]);
        Assert.Equal(-12_000m, entry[(LedgerAccounts.Inventory, TillHarness.Algiers)]);
        Assert.Equal(6_000m, entry[(Asset, TillHarness.Oran)]);
        Assert.Equal(-6_000m, entry[(LedgerAccounts.Inventory, TillHarness.Oran)]);
        Assert.Equal(2, await db.FixedAssetMovements.CountAsync(m => m.Kind == FixedAssetMovementKind.Opening));

        var again = await SwitchOver(db, month).PostAsync(new PostFixedAssetSwitchRequest(18_000m, 18_000m), "accountant");
        Assert.Equal((409, "already_posted"), (again.Status, again.Code));
        Assert.Equal(1, await db.LedgerEntries.CountAsync(e => e.SourceType == LedgerSources.FixedAssetSwitch));

        // The figures it was valued with are fixed now, and the slots it posts to cannot be emptied.
        var relife = await Store(db).UpdateAsync(new UpdateFixedAssetSettingsRequest("FixedAssets", "StraightLine", 48, 0m, "CostInForce", $"{month:yyyy-MM}"), "accountant");
        Assert.Equal((409, FixedAssetSettingsStore.LockedCode), (relife.Status, relife.Code));
        var back = await Store(db).UpdateAsync(new UpdateFixedAssetSettingsRequest("Stock", "StraightLine", 60, 0m, "CostInForce", $"{month:yyyy-MM}"), "accountant");
        Assert.Equal(409, back.Status);
        var emptied = await Store(db).UpdateMapAsync(new UpdateManufacturingAccountMapRequest([new ManufacturingAccountMapLine(FixedAssetAccountSlots.Asset, null)]), "accountant");
        Assert.Equal((409, FixedAssetSettingsStore.SlotInUseCode), (emptied.Status, emptied.Code));
    }

    [Fact]
    public async Task Opening_at_the_asset_value_posts_the_difference_to_the_opening_adjustment_once_it_is_mapped()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var month = ThisMonth();
        var pipe = await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe);
        pipe.AssetUnitValue = 450m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await MapAsync(db);
        await ChooseAsync(db, "FixedAssets", month, opening: "AssetUnitValue");

        var preview = await SwitchOver(db, month).PreviewAsync();
        Assert.Equal((18_000m, 13_500m, 4_500m), (preview.StockValue, preview.OpeningTotal, preview.Difference));
        Assert.Contains(preview.Blocking, b => b.Code == "slot_unmapped");
        await MapAsync(db, openingAdjustment: true);
        var posted = await SwitchOver(db, month).PostAsync(new PostFixedAssetSwitchRequest(18_000m, 13_500m), "accountant");
        Assert.True(posted.Code is null, posted.Error);
        var entry = await PostedAsync(db, LedgerSources.FixedAssetSwitch);
        Assert.Equal((13_500m, -18_000m, 4_500m), (entry[Asset], entry[LedgerAccounts.Inventory], entry[Adjustment]));
    }

    [Fact]
    public async Task A_month_of_depreciation_posts_once_in_order_reverses_and_posts_again()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var month = await SwitchedAsync(db);

        // 12,000 at Algiers and 6,000 at Oran over 60 months: 200 and 100 a month.
        var read = (await Report(db).MonthAsync(month.Year, month.Month, null)).Value!;
        Assert.Equal(300m, read.TotalDebit);
        Assert.Equal(200m, read.Entries.Single(e => e.BranchId == TillHarness.Algiers).Lines[0].Debit);
        Assert.All(read.Checks, c => Assert.True(c.Passed, c.Code));
        // A branch reader sees its own pools.
        Assert.Single((await Report(db).MonthAsync(month.Year, month.Month, TillHarness.Oran)).Value!.Entries);

        var second = month.AddMonths(1);
        var early = await Postings(db).PostAsync(second.Year, second.Month, companyScoped: true, "accountant");
        Assert.Equal("previous_month_not_posted", early.Code);
        Assert.Equal("post_needs_every_branch", (await Postings(db).PostAsync(month.Year, month.Month, companyScoped: false, "manager")).Code);
        var current = ThisMonth();
        Assert.Equal("month_not_ended", (await Postings(db).PostAsync(current.Year, current.Month, true, "accountant")).Code);

        Assert.Null((await Postings(db).PostAsync(month.Year, month.Month, true, "accountant")).Code);
        Assert.Equal("already_posted", (await Postings(db).PostAsync(month.Year, month.Month, true, "accountant")).Code);
        db.ChangeTracker.Clear();
        var books = await ByBranchAsync(db, FixedAssetLedgerPostings.SourceType);
        Assert.Equal((200m, -200m, 100m), (books[(Charge, TillHarness.Algiers)], books[(Depreciation, TillHarness.Algiers)], books[(Charge, TillHarness.Oran)]));
        Assert.Equal(2, await db.LedgerEntries.CountAsync(e => e.SourceType == FixedAssetLedgerPostings.SourceType));

        Assert.Null((await Postings(db).PostAsync(second.Year, second.Month, true, "accountant")).Code);
        Assert.Equal("later_month_posted", (await Postings(db).ReverseAsync(month.Year, month.Month, true, "accountant")).Code);
        Assert.Null((await Postings(db).ReverseAsync(second.Year, second.Month, true, "accountant")).Code);
        Assert.Null((await Postings(db).ReverseAsync(month.Year, month.Month, true, "accountant")).Code);
        db.ChangeTracker.Clear();
        Assert.Equal(0m, (await PostedAsync(db, FixedAssetLedgerPostings.SourceType)).Values.Sum(Math.Abs));
        Assert.Equal(4, await db.LedgerEntries.CountAsync(e => e.SourceType == FixedAssetLedgerPostings.SourceType && e.IsReversal));

        var again = await Postings(db).PostAsync(month.Year, month.Month, true, "accountant");
        Assert.Null(again.Code);
        Assert.Equal(2, again.Value!.Postings.Count);
        db.ChangeTracker.Clear();
        Assert.Equal(300m, (await PostedAsync(db, FixedAssetLedgerPostings.SourceType))[Charge]);

        // A month whose books are closed is refused.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Algiers");
        var closed = await BookClosings.CloseAsync(db, second.AddMonths(1).AddDays(-1), "year end", null, "admin", zone, DateTime.UtcNow, default);
        Assert.Null(closed.Problem);
        db.ChangeTracker.Clear();
        Assert.Equal("books_closed", (await Postings(db).PostAsync(second.Year, second.Month, true, "accountant")).Code);
    }

    [Fact]
    public async Task On_rent_lost_and_wrecked_units_leave_their_pool_at_net_book_value_and_nothing_posts_to_310()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var admin = await StockDocumentsCostSwitchTests.AdminAsync(db);
        await SwitchedAsync(db);

        // Three months depreciated at Algiers: 600 of 12,000 — 30 a pipe, each worth 570.
        await StockDocumentsCostSwitchTests.RentAndReturnAsync(db, issued: 4, ok: 1, wrecked: 1, lost: 2);

        Assert.Empty(await PostedAsync(db, LedgerSources.StockState));
        var register = await PostedAsync(db, LedgerSources.FixedAsset);
        // The wreck: 30 of depreciation and 570 into scrap for 600 of cost. The two lost: 60 and 1,140 to disposals for 1,200.
        Assert.Equal(90m, register[Depreciation]);
        Assert.Equal(570m, register[LedgerAccounts.ScrapInventory]);
        Assert.Equal(1_140m, register[Disposal]);
        Assert.Equal(-1_800m, register[Asset]);
        Assert.False(register.ContainsKey(LedgerAccounts.Inventory));

        // The scrap leaves at the 570 it is carried at; one written off from the yard leaves at its net book value.
        var scrapOut = await StockDocumentsCostSwitchTests.StateAsync(db, admin, 1, "Scrap", "WrittenOff");
        var writeOff = await StockDocumentsCostSwitchTests.StateAsync(db, admin, 1, "Available", "WrittenOff");
        var after = await PostedAsync(db, LedgerSources.FixedAsset);
        Assert.Equal(570m, after[LedgerAccounts.StockLoss]);
        Assert.Equal(0m, after[LedgerAccounts.ScrapInventory]);
        Assert.Equal(1_140m + 570m, after[Disposal]);
        Assert.Empty(await PostedAsync(db, LedgerSources.StockState));
        Assert.NotEqual(scrapOut.DocumentId, writeOff.DocumentId);

        // A count finds one: it joins the pool at today's value, 600, against the disposal account.
        var found = await StockDocumentsCostSwitchTests.AdjustAsync(db, admin, 1);
        Assert.Empty(await StockDocumentsCostSwitchTests.PostedAsync(db, LedgerSources.StockAdjustment, found.DocumentId));
        var pool = (await new FixedAssetRegisterReport(db, TimeProvider.System).RegisterAsync(null, null, TillHarness.Algiers)).Value!.Pools.Single();
        // 20 − 1 wrecked − 2 lost − 1 written off + 1 found.
        Assert.Equal(17m, pool.Units);
        Assert.Equal(12_000m - 600m - 1_200m - 600m + 600m, pool.Cost);
        Assert.Equal(600m - 30m - 60m - 30m, pool.AccumulatedDepreciation);
        Assert.Equal(pool.UnitsInStock, pool.Units);
    }

    [Fact]
    public async Task A_purchase_after_the_switch_over_is_debited_to_the_asset_account_and_depreciated_from_the_month_after()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var admin = await StockDocumentsCostSwitchTests.AdminAsync(db);
        await SwitchedAsync(db);

        var json = JsonSerializer.Serialize(new
        {
            lines = new[] { new { productId = TillHarness.Pipe, qty = 6m, unitCost = (decimal?)800m, assetUnitValue = (decimal?)null } },
            reference = "BL-77", note = (string?)null, supplierId = (Guid?)null, branchId = TillHarness.Oran,
        });
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapStockEndpoints(), "POST", "/api/catalog/stock/receiving", admin, json);
        Assert.True(status == 200, body);
        db.ChangeTracker.Clear();

        var receipt = await db.LedgerEntryLines.AsNoTracking().Include(l => l.Account).Include(l => l.Entry)
            .Where(l => l.Entry!.SourceType == LedgerSources.Receiving && l.Entry.Description!.Contains("BL-77")).ToListAsync();
        Assert.Equal(4_800m, receipt.Single(l => l.Account!.Code == Asset).Debit);
        Assert.DoesNotContain(receipt, l => l.Account!.Code == LedgerAccounts.Inventory);
        var acquired = await db.FixedAssetMovements.AsNoTracking().SingleAsync(m => m.Kind == FixedAssetMovementKind.Acquisition);
        Assert.Equal((6m, 4_800m, ThisMonth().AddMonths(1)), (acquired.Qty, acquired.Cost, acquired.StartMonth!.Value));

        // This month: Oran's opening layer only (100); from the next, 4,800 / 60 = 80 more.
        var now = ThisMonth();
        var month = (await Report(db).MonthAsync(now.Year, now.Month, TillHarness.Oran)).Value!;
        Assert.Equal(100m, month.TotalDebit);
        Assert.Contains("month_to_date", month.Notes);
    }

    [Fact]
    public async Task A_transfer_moves_cost_and_depreciation_between_the_branches_pools_and_a_loss_in_transit_is_a_disposal()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        await SwitchedAsync(db);

        var transfer = await StockDocumentsCostSwitchTests.DispatchAsync(db, 3);
        await StockDocumentsCostSwitchTests.ReceiveTransferAsync(db, transfer, 2);
        await StockDocumentsCostSwitchTests.LoseInTransitAsync(db, transfer, 1);

        // Nothing through 310: the receipt and the loss post through the register.
        Assert.Empty(await PostedAsync(db, LedgerSources.StockTransfer));
        Assert.Empty(await PostedAsync(db, LedgerSources.StockTransferLoss));
        var moved = await ByBranchAsync(db, LedgerSources.FixedAsset);
        Assert.Equal(1_200m, moved[(Asset, TillHarness.Oran)]);
        Assert.Equal(-60m, moved[(Depreciation, TillHarness.Oran)]);
        // Algiers: 1,200 moved and 600 lost (cost); 60 and 30 of depreciation with them; 570 to disposals.
        Assert.Equal(-1_800m, moved[(Asset, TillHarness.Algiers)]);
        Assert.Equal(90m, moved[(Depreciation, TillHarness.Algiers)]);
        Assert.Equal(570m, moved[(Disposal, TillHarness.Algiers)]);
        Assert.True(await db.LedgerEntries.AnyAsync(e => e.SourceType == LedgerSources.FixedAsset && e.IsInterBranch));

        var pools = (await new FixedAssetRegisterReport(db, TimeProvider.System).RegisterAsync(null, null, null)).Value!.Pools;
        Assert.Equal((17m, 10_200m), pools.Where(p => p.BranchId == TillHarness.Algiers).Select(p => (p.Units, p.Cost)).Single());
        Assert.Equal((12m, 7_200m, 360m), pools.Where(p => p.BranchId == TillHarness.Oran).Select(p => (p.Units, p.Cost, p.AccumulatedDepreciation)).Single());
    }

    [Fact]
    public async Task The_register_routes_read_with_the_accounting_reports_key_and_the_switch_over_is_the_company_s()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var admin = await StockDocumentsCostSwitchTests.AdminAsync(db);
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, TillHarness.Oran);

        var (status, body) = await BranchLockTests.InvokeAsync(db, a => a.MapFixedAssetEndpoints(), "GET", "/api/fixed-assets/settings", admin, "");
        Assert.True(status == 200, body);
        Assert.Contains("\"treatment\":\"Stock\"", body);
        var preview = await BranchLockTests.InvokeAsync(db, a => a.MapFixedAssetEndpoints(), "GET", "/api/fixed-assets/switch-over", manager, "");
        Assert.Equal(403, preview.Status);
        var declining = await BranchLockTests.InvokeAsync(db, a => a.MapFixedAssetEndpoints(), "PUT", "/api/fixed-assets/settings", admin,
            """{"treatment":"FixedAssets","method":"DecliningBalance","defaultUsefulLifeMonths":60,"residualPercent":0,"openingValue":"CostInForce","switchMonth":null}""");
        // Declining balance is built (2026-11-10): the company's default is the accountant's choice, the switch on by default.
        Assert.True(declining.Status == 200, declining.Body);
        Assert.Contains("\"method\":\"DecliningBalance\"", declining.Body);
        Assert.Contains("\"switchToStraightLine\":true", declining.Body);
    }
}
