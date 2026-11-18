using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Payroll;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Seeding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Payroll;

/// <summary>
/// Two branches with a box each, a crew, and an approved run to pay them from — against a real
/// database, because everything §5.8 and §5.10 claim is a claim about what the box and the ledger
/// hold afterwards.
///
/// <para>
/// The accrual is posted through <see cref="PayrollPosting.Accrual"/> rather than hand-written, so
/// "the payment clears 421" is checked against the liability the run really raises and not against
/// a figure this file invented.
/// </para>
/// </summary>
public sealed class WageMoneyHarness(
    SqliteConnection connection, PosDbContext db, WageMoney money, PayrollDocuments payroll)
    : IAsyncDisposable
{
    public static readonly Guid Algiers = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Oran = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public PosDbContext Db { get; } = db;
    public WageMoney Money { get; } = money;

    /// <summary>
    /// The run's own lifecycle, on the same database — so "reverse the payment, then reverse the
    /// run" can be walked here rather than asserted about in two harnesses that never meet.
    /// </summary>
    public PayrollDocuments Payroll { get; } = payroll;

    /// <summary>The box at Algiers — the one a crew is paid out of.</summary>
    public CashBox AlgiersBox { get; private set; } = null!;

    /// <summary>The box at Oran, so an inter-branch payout is a real pair of branches.</summary>
    public CashBox OranBox { get; private set; } = null!;

    private sealed class Algeria : IRegionalSettingsProvider
    {
        private readonly RegionalSettings settings = RegionalPresets.Create("DZ")!;
        public RegionalSettings Get() => settings;
    }

    public static async Task<WageMoneyHarness> NewAsync(decimal algiersFloat = 5_000_000m)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Stores.Add(new Store { NameFr = "Béton & Échafaudages SARL" });
        db.Branches.Add(new Branch { Id = Algiers, NameFr = "Alger-Centre", IsMain = true, IsOpenForOperation = true });
        db.Branches.Add(new Branch { Id = Oran, NameFr = "Oran", IsOpenForOperation = true });
        await db.SaveChangesAsync();

        // The chart, the tenders and one box per branch — the same seeder production runs, so the
        // payroll accounts under test are the ones a real company gets.
        await CoreDataSeeder.EnsureAsync(db);

        var regional = new Algeria();
        var store = new PayrollSettingsStore(db, regional);
        var directory = new AlgeriaPos.Infrastructure.Hr.EmployeeDirectory(db, store, regional, TimeProvider.System);
        var harness = new WageMoneyHarness(
            connection, db, new WageMoney(db, new LedgerPoster(db), regional),
            new PayrollDocuments(
                db, new LedgerPoster(db), store, regional, directory,
                new AlgeriaPos.Infrastructure.Hr.AttendanceSheet(db, directory, store, regional)));

        harness.AlgiersBox = await db.CashBoxes.FirstAsync(x => x.BranchId == Algiers);
        harness.OranBox = await db.CashBoxes.FirstAsync(x => x.BranchId == Oran);

        // A float, so "the box is short" is a case the tests choose rather than the only case there
        // is. Payday out of an empty drawer would warn on every single assertion.
        if (algiersFloat > 0) await harness.FundAsync(harness.AlgiersBox, algiersFloat);
        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ---------- building a company ----------

    /// <summary>The float a box starts life with, as its own counted movement.</summary>
    public async Task FundAsync(CashBox box, decimal amount)
    {
        Db.CashMovements.Add(new CashMovement
        {
            CashBoxId = box.Id,
            Direction = CashDirection.In,
            Amount = amount,
            BalanceAfter = await BoxBalanceAsync(box.Id) + amount,
            Kind = CashMovementKind.Opening,
            UserName = "admin"
        });
        await Db.SaveChangesAsync();
    }

    public async Task<Employee> HireAsync(
        string name, Guid? branchId = null, PayoutMethod payout = PayoutMethod.Cash,
        decimal baseRate = 60_000m, DateOnly? leftOn = null)
    {
        var employee = new Employee
        {
            Number = $"EMP-{await Db.Employees.CountAsync() + 1:D6}",
            FullName = name,
            BranchId = branchId ?? Algiers,
            HiredOn = new DateOnly(2025, 1, 1),
            PayBasis = PayBasis.Monthly,
            BaseRate = baseRate,
            PayoutMethod = payout,
            LeftOn = leftOn,
            IsActive = leftOn is null
        };
        Db.Employees.Add(employee);
        await Db.SaveChangesAsync();
        return employee;
    }

    /// <summary>
    /// An approved run for a branch, with one line per person, and the accrual it posted.
    /// </summary>
    /// <remarks>
    /// The figures are deliberately plain — gross is net, nothing withheld — because what these
    /// tests are about is the money going out, not the arithmetic that decided how much. The
    /// engine's own tests own that.
    /// </remarks>
    public async Task<PayrollRun> ApprovedRunAsync(
        params (Employee Employee, decimal Net)[] people)
    {
        var run = await RunAsync(PayrollRunStatus.Approved, people);

        var total = people.Sum(p => p.Net);
        var accrual = new PayrollAccrual(
            Gross: total, WageCostDeductions: 0m, OtherDeductions: 0m,
            SocialEmployee: 0m, SocialEmployer: 0m, IncomeTax: 0m,
            EmployerLevies: 0m, AdvancesRecovered: 0m, Net: total);

        var draft = PayrollPosting.Accrual(run.Id, run.BranchId, run.Number, accrual, "admin", PayrollDocuments.PeriodEndUtc(run));
        if (draft is not null) await new LedgerPoster(Db).PostAsync(draft);
        await Db.SaveChangesAsync();
        return run;
    }

    /// <summary>A run in whatever state a test needs, with no accrual behind it.</summary>
    public async Task<PayrollRun> RunAsync(
        PayrollRunStatus status, params (Employee Employee, decimal Net)[] people)
    {
        var branchId = people.Length > 0 ? people[0].Employee.BranchId : Algiers;
        var run = new PayrollRun
        {
            Number = $"RUN-{await Db.PayrollRuns.CountAsync() + 1:D6}",
            BranchId = branchId,
            PeriodYear = 2026,
            PeriodMonth = 9,
            Status = status,
            CurrencyDecimals = 2,
            HeadCount = people.Length,
            Gross = people.Sum(p => p.Net),
            Net = people.Sum(p => p.Net),
            ApprovedAtUtc = status >= PayrollRunStatus.Approved ? DateTime.UtcNow : null,
            CreatedBy = "admin"
        };

        foreach (var (employee, net) in people)
            run.Lines.Add(new PayrollRunLine
            {
                RunId = run.Id,
                EmployeeId = employee.Id,
                EmployeeNumberSnapshot = employee.Number,
                EmployeeNameSnapshot = employee.FullName,
                BranchIdSnapshot = employee.BranchId,
                PayBasis = employee.PayBasis,
                BaseRate = employee.BaseRate,
                Gross = net,
                Net = net
            });

        Db.PayrollRuns.Add(run);
        await Db.SaveChangesAsync();
        return run;
    }

    // ---------- reading it back ----------

    public async Task<decimal> BoxBalanceAsync(Guid cashBoxId)
    {
        var rows = await Db.CashMovements.AsNoTracking()
            .Where(m => m.CashBoxId == cashBoxId)
            .Select(m => new { m.Direction, m.Amount })
            .ToListAsync();
        // Summed here rather than in SQL: SQLite stores decimal as TEXT.
        return rows.Sum(m => m.Direction == CashDirection.In ? m.Amount : -m.Amount);
    }

    /// <summary>Every posted line by account code, debit-positive.</summary>
    public async Task<Dictionary<string, decimal>> PostedAsync(string? sourceType = null)
    {
        var accounts = await Db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
        var query = Db.LedgerEntries.AsNoTracking().Include(e => e.Lines).AsQueryable();
        if (sourceType is not null) query = query.Where(e => e.SourceType == sourceType);

        var entries = await query.ToListAsync();
        return entries.SelectMany(e => e.Lines)
            .GroupBy(l => accounts[l.AccountId])
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    /// <summary>One account's balance, debit-positive, optionally at one branch only.</summary>
    public async Task<decimal> AccountAsync(string code, Guid? branchId = null)
    {
        var account = await Db.LedgerAccounts.AsNoTracking().FirstAsync(a => a.Code == code);
        var query = Db.LedgerEntryLines.AsNoTracking().Where(l => l.AccountId == account.Id);
        if (branchId is Guid id) query = query.Where(l => l.BranchId == id);

        var lines = await query.Select(l => new { l.Debit, l.Credit }).ToListAsync();
        return lines.Sum(l => l.Debit - l.Credit);
    }

    public Task<List<CashMovement>> MovementsAsync(string sourceType) =>
        Db.CashMovements.AsNoTracking().Where(m => m.SourceType == sourceType).ToListAsync();

    public Task<PayrollRun> ReloadAsync(PayrollRun run) =>
        Db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id);

    public Task<List<PayrollRunLine>> LinesAsync(PayrollRun run) =>
        Db.PayrollRunLines.AsNoTracking().Where(l => l.RunId == run.Id).ToListAsync();
}
