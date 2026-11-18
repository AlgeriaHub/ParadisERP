using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.FixedAssets;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Tests.Security;
using ParadiERP.Tests.Valuation;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Ledger.FixedAssetRegisterTests;

namespace ParadiERP.Tests.Ledger;

/// <summary>
/// Declining balance (2026-11-10, docs/accounting/fixed-assets.md "Declining balance"): the straight-line rate × a coefficient
/// on the net book value at the start of each calendar year, the first year prorated by its months, switching to straight
/// line when that is higher (an option, on by default). A choice the accountant makes per type with a company default; the
/// default stays straight line, so the straight-line suite (<see cref="FixedAssetRegisterTests"/>) runs as it was.
/// </summary>
public class FixedAssetDecliningBalanceTests
{
    private static readonly Guid Kit = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Yard = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static FixedAssetMovement Row(FixedAssetMovementKind kind, decimal qty, decimal cost, decimal depreciation, DateOnly month,
        DateOnly? start = null) => new()
    {
        ProductId = Kit, BranchId = Yard, Kind = kind, Qty = qty, Cost = cost, Depreciation = depreciation, StartMonth = start,
        AtUtc = month.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc).AddDays(9),
    };

    private static FixedAssetPolicy Declining(int life = 60, decimal residual = 0m, decimal? coefficient = null, bool? switchToStraightLine = null) =>
        new(life, new Dictionary<Guid, int>(), residual, DepreciationMethod.DecliningBalance, true,
            coefficient is null && switchToStraightLine is null ? null : new Dictionary<Guid, FixedAssetMethodChoice>
            {
                [Kit] = new() { ProductId = Kit, DecliningCoefficient = coefficient, SwitchToStraightLine = switchToStraightLine },
            });

    /// <summary>One asset of 1,000,000 opened in <paramref name="start"/>, depreciated through <paramref name="through"/>.</summary>
    private static FixedAssetEngine Engine(FixedAssetPolicy policy, DateOnly start, DateOnly through, decimal qty = 1m, decimal cost = 1_000_000m)
    {
        var engine = new FixedAssetEngine(start, policy);
        engine.Apply(Row(FixedAssetMovementKind.Opening, qty, cost, 0m, start, start), start);
        engine.DepreciateThrough(through);
        return engine;
    }

    private static Dictionary<int, decimal> ByYear(FixedAssetEngine engine) =>
        engine.Charges.GroupBy(c => c.Month.Year).ToDictionary(g => g.Key, g => g.Sum(c => c.Amount));

    // ---------- the arithmetic: worked examples ----------

    [Theory]
    [InlineData(36, 1.5)]
    [InlineData(48, 1.5)]
    [InlineData(60, 2.0)]
    [InlineData(72, 2.0)]
    [InlineData(84, 2.5)]
    [InlineData(120, 2.5)]
    public void The_suggested_coefficient_is_the_tax_code_s_for_the_life(int months, double expected) =>
        Assert.Equal((decimal)expected, FixedAssetDeclining.LegalCoefficient(months));

    [Fact]
    public void One_million_over_five_years_at_coefficient_2_is_40_percent_and_switches_to_straight_line_in_year_four()
    {
        // The schedule itself: 400,000 · 240,000 · 144,000, then 108,000 twice — in year 4 the straight line on 216,000 over two
        // years (108,000) exceeds 40 % of it (86,400).
        var years = FixedAssetDeclining.Schedule(1_000_000m, 0m, new DateOnly(2027, 1, 1), 60, 2m, true);
        Assert.Equal(new[]
            {
            (2027, 1_000_000m, 400_000m, 600_000m, false), (2028, 600_000m, 240_000m, 360_000m, false), (2029, 360_000m, 144_000m, 216_000m, false),
             (2030, 216_000m, 108_000m, 108_000m, true), (2031, 108_000m, 108_000m, 0m, false),
            },
            years.Select(y => (y.Year, y.Opening, y.Charge, y.Closing, y.Switches)).ToList());

        // The engine, with the legal coefficient suggested from the 60-month life: the same years, posted month by month.
        var engine = Engine(Declining(), new DateOnly(2027, 1, 1), new DateOnly(2031, 12, 1));
        Assert.Equal(new Dictionary<int, decimal> { [2027] = 400_000m, [2028] = 240_000m, [2029] = 144_000m, [2030] = 108_000m, [2031] = 108_000m },
            ByYear(engine));
        // 400,000 / 12 = 33,333.333…: the rounded cumulative figure less the one before, so no thousandth is lost.
        Assert.All(engine.Charges.Where(c => c.Month.Year == 2027), c => Assert.InRange(c.Amount, 33_333.333m, 33_333.334m));
        Assert.Equal(9_000m, engine.Charges.Single(c => c.Month == new DateOnly(2030, 1, 1)).Amount);
        Assert.Equal(0m, engine.Find(Kit, Yard)!.NetBookValue);
    }

    [Fact]
    public void Without_the_switch_the_last_year_takes_what_is_left()
    {
        var years = FixedAssetDeclining.Schedule(1_000_000m, 0m, new DateOnly(2027, 1, 1), 60, 2m, false);
        Assert.Equal(new[] { 400_000m, 240_000m, 144_000m, 86_400m, 129_600m }, years.Select(y => y.Charge).ToList());
        Assert.DoesNotContain(years, y => y.Switches);

        var engine = Engine(Declining(switchToStraightLine: false), new DateOnly(2027, 1, 1), new DateOnly(2031, 12, 1));
        Assert.Equal(129_600m, ByYear(engine)[2031]);
        Assert.Equal(0m, engine.Find(Kit, Yard)!.NetBookValue);
    }

    [Fact]
    public void A_mid_year_start_prorates_the_first_year_by_its_months_and_ends_sixty_months_later()
    {
        // Put in service in April 2027: 9 months in 2027 (300,000), the life ending in March 2032 (3 months).
        var years = FixedAssetDeclining.Schedule(1_000_000m, 0m, new DateOnly(2027, 4, 1), 60, 2m, true);
        Assert.Equal(new[]
            {
            (2027, 9, 300_000m, false), (2028, 12, 280_000m, false), (2029, 12, 168_000m, false), (2030, 12, 112_000m, true),
            (2031, 12, 112_000m, false), (2032, 3, 28_000m, false),
            },
            years.Select(y => (y.Year, y.Months, y.Charge, y.Switches)).ToList());

        var april = new DateOnly(2027, 4, 1);
        var engine = Engine(Declining(), april, new DateOnly(2032, 12, 1));
        Assert.Equal(60, engine.Charges.Count);
        Assert.Equal(april, engine.Charges[0].Month);
        Assert.Equal(new DateOnly(2032, 3, 1), engine.Charges[^1].Month);
        Assert.Equal(300_000m, ByYear(engine)[2027]);
        Assert.Equal(1_000_000m, engine.Charges.Sum(c => c.Amount));
    }

    [Fact]
    public void A_residual_value_is_never_depreciated_and_the_declining_rate_runs_on_the_whole_value()
    {
        // Residual 10 %: 40 % of the net book value each year (the residual included), the last year down to 100,000.
        var engine = Engine(Declining(residual: 10m), new DateOnly(2027, 1, 1), new DateOnly(2032, 6, 1));
        Assert.Equal(new Dictionary<int, decimal> { [2027] = 400_000m, [2028] = 240_000m, [2029] = 144_000m, [2030] = 86_400m, [2031] = 29_600m },
            ByYear(engine));
        Assert.Equal(100_000m, engine.Find(Kit, Yard)!.NetBookValue);
    }

    [Fact]
    public void A_type_s_own_coefficient_overrides_the_suggested_one()
    {
        // 1.5 on 60 months: 30 % a year — 300,000, 210,000; in year 3 the straight line on 490,000 over three years (163,333.33)
        // exceeds 30 % of it (147,000), and runs to the end: the thousandths fall where the rounded cumulative figure puts them.
        var engine = Engine(Declining(coefficient: 1.5m), new DateOnly(2027, 1, 1), new DateOnly(2031, 12, 1));
        var years = ByYear(engine);
        Assert.Equal((300_000m, 210_000m), (years[2027], years[2028]));
        Assert.Equal((163_333.333m, 163_333.334m, 163_333.333m), (years[2029], years[2030], years[2031]));
        Assert.Equal(1_000_000m, years.Values.Sum());
    }

    [Theory]
    [InlineData(2027, 1, 60, 2.0, 0.0, true)]
    [InlineData(2027, 5, 54, 1.5, 7.5, true)]
    [InlineData(2027, 11, 84, 2.5, 0.0, false)]
    [InlineData(2026, 8, 37, 1.5, 12.5, true)]
    [InlineData(2028, 12, 120, 2.5, 5.0, false)]
    public void Every_month_posted_over_the_whole_life_adds_up_to_cost_less_residual_exactly(
        int year, int month, int life, double coefficient, double residual, bool switchOn)
    {
        // Three units bought together for 1,000,000: a unit cost of 333,333.333… — the rounding is absorbed in the last month.
        var start = new DateOnly(year, month, 1);
        var engine = Engine(Declining(life, (decimal)residual, (decimal)coefficient, switchOn), start, start.AddMonths(life + 12), qty: 3m);
        Assert.Equal(life, engine.Charges.Count);
        Assert.Equal(FixedAssetEngine.Money(1_000_000m * (1m - (decimal)residual / 100m)), engine.Charges.Sum(c => c.Amount));
        Assert.All(engine.Charges, c => Assert.True(c.Amount > 0m));
    }

    [Fact]
    public void Units_disposed_of_leave_at_their_share_and_the_rest_keeps_its_declining_schedule()
    {
        // 2,000 frames at 6,500 over five years at 40 %: 5,200,000 in the first year.
        var january = new DateOnly(2027, 1, 1);
        var engine = Engine(Declining(), january, new DateOnly(2027, 12, 1), qty: 2_000m, cost: 13_000_000m);
        var next = new DateOnly(2028, 1, 1);
        var (cost, depreciation) = engine.Share(Kit, Yard, 200m);
        Assert.Equal((1_300_000m, 520_000m), (cost, depreciation));
        engine.Apply(Row(FixedAssetMovementKind.Disposal, 200m, cost, depreciation, next), next);
        var pool = engine.Find(Kit, Yard)!;
        // The same net book value per unit as before: 3,900 each.
        Assert.Equal(3_900m, pool.NetBookValue / pool.Qty);

        // The 1,800 left: 40 % of 7,020,000 in 2028, then the same schedule as before, scaled.
        engine.DepreciateThrough(new DateOnly(2031, 12, 1));
        var years = ByYear(engine);
        Assert.Equal((2_808_000m, 1_684_800m, 1_263_600m, 1_263_600m), (years[2028], years[2029], years[2030], years[2031]));
        Assert.Equal(0m, pool.NetBookValue);
    }

    [Fact]
    public void The_default_is_still_straight_line()
    {
        var policy = new FixedAssetPolicy(60, new Dictionary<Guid, int>(), 0m);
        Assert.Equal(FixedAssetMethodTerms.StraightLine, policy.TermsOf(Kit));
        Assert.Equal(DepreciationMethod.StraightLine, new FixedAssetSettings().Method);
        Assert.True(new FixedAssetSettings().SwitchToStraightLine);
        // 1,000,000 over 60 months: 200,000 a year on the straight line.
        Assert.All(ByYear(Engine(policy, new DateOnly(2027, 1, 1), new DateOnly(2031, 12, 1))).Values, v => Assert.Equal(200_000m, v));
    }

    // ---------- the shop: the setting, the lock, the postings ----------

    private static UpdateFixedAssetSettingsRequest Settings(DateOnly month, string method = "DecliningBalance", bool? switchOn = null,
        IReadOnlyList<FixedAssetMethodLine>? methods = null) =>
        new("FixedAssets", method, 60, 0m, "CostInForce", $"{month:yyyy-MM}", null, switchOn, methods);

    [Fact]
    public async Task The_method_changes_until_depreciation_is_posted_on_the_type_then_it_is_refused_until_reversed()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var month = await SwitchedAsync(db);

        // Nothing posted yet: the company's default and the type's own row may change, and back.
        var chosen = await Store(db).UpdateAsync(Settings(month), "accountant");
        Assert.True(chosen.Code is null, chosen.Error);
        Assert.Equal("DecliningBalance", chosen.Value!.Method);
        var pipe = chosen.Value.Lives.Single(l => l.ProductId == TillHarness.Pipe);
        Assert.Equal((2m, 2m, true, false), (pipe.SuggestedCoefficient!.Value, pipe.EffectiveCoefficient!.Value, pipe.EffectiveSwitchToStraightLine, pipe.MethodLocked));
        var own = await Store(db).UpdateAsync(Settings(month, methods: [new FixedAssetMethodLine(TillHarness.Pipe, null, 1.5m)]), "accountant");
        Assert.True(own.Code is null, own.Error);
        Assert.Equal(1.5m, own.Value!.Lives.Single(l => l.ProductId == TillHarness.Pipe).EffectiveCoefficient);
        var cleared = await Store(db).UpdateAsync(Settings(month, methods: [new FixedAssetMethodLine(TillHarness.Pipe, null)]), "accountant");
        Assert.Null(cleared.Code);
        Assert.Equal(0, await db.FixedAssetMethods.CountAsync());

        // The first month posts at 40 % a year: Algiers' 12,000 over the months left in its first calendar year.
        var expected = FixedAssetEngine.Money(12_000m * 0.4m / 12m) + FixedAssetEngine.Money(6_000m * 0.4m / 12m);
        var posted = await Postings(db).PostAsync(month.Year, month.Month, true, "accountant");
        Assert.True(posted.Code is null, posted.Error);
        Assert.Equal(expected, posted.Value!.TotalDebit);
        db.ChangeTracker.Clear();
        Assert.Equal(expected, (await PostedAsync(db, FixedAssetLedgerPostings.SourceType))[Charge]);

        // Posted: neither the company's default nor the type's own row may change how the pipes are depreciated.
        var back = await Store(db).UpdateAsync(Settings(month, "StraightLine"), "accountant");
        Assert.Equal((409, FixedAssetSettingsStore.MethodLockedCode), (back.Status, back.Code));
        var noSwitch = await Store(db).UpdateAsync(Settings(month, switchOn: false), "accountant");
        Assert.Equal((409, FixedAssetSettingsStore.MethodLockedCode), (noSwitch.Status, noSwitch.Code));
        var coefficient = await Store(db).UpdateAsync(Settings(month, methods: [new FixedAssetMethodLine(TillHarness.Pipe, null, 2.5m)]), "accountant");
        Assert.Equal((409, FixedAssetSettingsStore.MethodLockedCode), (coefficient.Status, coefficient.Code));
        // Saying the same thing in other words changes nothing, so it is not refused: the pipe's own declining at the legal 2.
        var same = await Store(db).UpdateAsync(Settings(month, methods: [new FixedAssetMethodLine(TillHarness.Pipe, "DecliningBalance", 2m, true)]), "accountant");
        Assert.True(same.Code is null, same.Error);
        var read = await Store(db).SettingsAsync();
        Assert.True(read.Lives.Single(l => l.ProductId == TillHarness.Pipe).MethodLocked);
        // The pipe has its own method now: the company's default no longer decides it.
        Assert.False(read.MethodLocked);
        Assert.Null((await Store(db).UpdateAsync(Settings(month, "StraightLine", methods: [new FixedAssetMethodLine(TillHarness.Pipe, "DecliningBalance", 2m, true)]), "accountant")).Code);

        // Reversed, nothing is posted on the type any more: its method may change again.
        Assert.Null((await Postings(db).ReverseAsync(month.Year, month.Month, true, "accountant")).Code);
        db.ChangeTracker.Clear();
        var reopened = await Store(db).UpdateAsync(Settings(month, "StraightLine", methods: [new FixedAssetMethodLine(TillHarness.Pipe, null)]), "accountant");
        Assert.True(reopened.Code is null, reopened.Error);
        Assert.Equal("StraightLine", reopened.Value!.Lives.Single(l => l.ProductId == TillHarness.Pipe).EffectiveMethod);
    }

    [Fact]
    public async Task A_coefficient_outside_one_to_five_or_on_a_product_not_hired_out_is_refused()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var month = ThisMonth();
        var high = await Store(db).UpdateAsync(Settings(month, methods: [new FixedAssetMethodLine(TillHarness.Pipe, "DecliningBalance", 6m)]), "accountant");
        Assert.Equal((400, FixedAssetSettingsStore.InvalidCode), (high.Status, high.Code));
        var odd = await Store(db).UpdateAsync(Settings(month, methods: [new FixedAssetMethodLine(TillHarness.Pipe, "Sideways")]), "accountant");
        Assert.Equal((400, FixedAssetSettingsStore.InvalidCode), (odd.Status, odd.Code));
        var notHired = Guid.NewGuid();
        var wrong = await Store(db).UpdateAsync(Settings(month, methods: [new FixedAssetMethodLine(notHired, "DecliningBalance")]), "accountant");
        Assert.Equal((400, FixedAssetSettingsStore.InvalidCode), (wrong.Status, wrong.Code));
    }

    [Fact]
    public async Task Lost_units_leave_at_their_declining_net_book_value_and_the_schedule_reaches_zero_with_its_switch_marked()
    {
        var (connection, db) = await ShopAsync();
        await using var _ = connection;
        await using var __ = db;
        var month = await SwitchedAsync(db);
        Assert.Null((await Store(db).UpdateAsync(Settings(month), "accountant")).Code);

        // Three months depreciated before the return: a pipe's share at the declining rate (more than the straight line's 30).
        var perPipe = FixedAssetDeclining.Cumulative(600m, 0m, month, 60, new FixedAssetMethodTerms(DepreciationMethod.DecliningBalance, 2m, true))[3];
        Assert.True(perPipe > 30m);
        await StockDocumentsCostSwitchTests.RentAndReturnAsync(db, issued: 4, ok: 1, wrecked: 1, lost: 2);
        var register = await PostedAsync(db, LedgerSources.FixedAsset);
        // Each event takes its share of the pool's rounded figure: within a thousandth of three pipes' worth.
        Assert.InRange(register[Depreciation], 3 * perPipe - 0.002m, 3 * perPipe + 0.002m);
        Assert.InRange(register[Disposal], 2 * (600m - perPipe) - 0.002m, 2 * (600m - perPipe) + 0.002m);
        Assert.Equal(-1_800m, register[Asset]);

        // The pool's schedule: opened at 12,000, the three units out at their net book value, to nothing at the end of the life.
        var history = (await new FixedAssetRegisterReport(db, TimeProvider.System).HistoryAsync(TillHarness.Pipe, TillHarness.Algiers)).Value!;
        Assert.Equal("DecliningBalance", history.Pool!.Method);
        Assert.Equal(2m, history.Pool.DecliningCoefficient);
        var schedule = history.Schedule!;
        Assert.Equal(month.Year, schedule[0].Year);
        Assert.Equal(12_000m, schedule[0].Opening);
        Assert.Equal(-(1_800m - register[Depreciation]), schedule.Sum(y => y.Movements));
        // Every month charged over the life, less what the three took out with them, is the 10,200 the pool still carries.
        Assert.InRange(schedule.Sum(y => y.Charge) - register[Depreciation], 10_199.998m, 10_200.002m);
        Assert.InRange(schedule[^1].Closing, -0.002m, 0.002m);
        Assert.Single(schedule, y => y.Switches);
        Assert.All(schedule.Zip(schedule.Skip(1)), p => Assert.Equal(p.First.Closing, p.Second.Opening));
        Assert.True(schedule[^1].Projected);
    }
}
