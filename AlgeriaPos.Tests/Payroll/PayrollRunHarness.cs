using System.Text.Json;
using Engine = AlgeriaPos.Application.Payroll;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Hr;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Payroll;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Seeding;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AlgeriaPos.Tests.Payroll;

/// <summary>
/// Two branches, a payroll profile and a real database.
///
/// <para>
/// Real, because everything §5.7 and §5.9 claim is a claim about what is in the tables and in
/// the ledger afterwards: that a reversed month can be run again, that a recompute leaves no
/// second set of lines behind, that one entry per branch balances. The arithmetic itself needs
/// no database and is checked in <see cref="PayrollEngineTests"/>.
/// </para>
/// <para>
/// <b>Every figure below is synthetic and says so</b> — contributions at 10 % and 20 %, a month
/// of 20 working days of 10 hours, brackets at 1,000 and 2,000. None of it is anybody's law. A
/// test is the easiest place for a plausible real figure to be written from memory and then
/// quoted back as authority.
/// </para>
/// </summary>
public sealed class PayrollRunHarness(
    SqliteConnection connection, PosDbContext db, PayrollDocuments payroll, PayrollSettingsStore store,
    PayrollRunHarness.QueryCounter queries)
    : IAsyncDisposable
{
    /// <summary>
    /// Counts the reads that reach the database, so a test can assert the thing §5.7 actually
    /// asks for: that a compute costs the same number of queries for nine hundred employees as
    /// for fifty. A count that merely looks small today is what an N+1 looks like before the
    /// branch grows.
    /// </summary>
    public sealed class QueryCounter : DbCommandInterceptor
    {
        public int Selects { get; private set; }

        public void Reset() => Selects = 0;

        private void Count(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Selects++;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken ct = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }

    public QueryCounter Queries { get; } = queries;

    public static readonly Guid Algiers = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Oran = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public const int Year = 2026;
    public const int Month = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PosDbContext Db { get; } = db;
    public PayrollDocuments Payroll { get; } = payroll;
    public PayrollSettingsStore Store { get; } = store;

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    /// <summary>A round, obviously invented profile. Each test changes exactly one thing about it.</summary>
    public static Engine.PayrollSettings Profile(Action<Engine.PayrollSettings>? tweak = null)
    {
        var settings = new Engine.PayrollSettings
        {
            CountryCode = "ZZ",
            SocialEmployeeRate = 10m,
            SocialEmployerRate = 20m,
            IncomeTaxMode = Engine.IncomeTaxMode.None,
            IncomeTaxBrackets = [],
            MinimumWage = 0m,
            WorkingDaysPerMonth = 20m,
            HoursPerDay = 10m,
            Confirmed = true,
            ConfirmedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        tweak?.Invoke(settings);
        Engine.PayrollSettingsValidator.NormalizeAndValidate(settings);
        return settings;
    }

    public static async Task<PayrollRunHarness> NewAsync(Action<Engine.PayrollSettings>? tweak = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var queries = new QueryCounter();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite(connection).AddInterceptors(queries).Options);
        await db.Database.EnsureCreatedAsync();

        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Stores.Add(new Store { NameFr = "Béton & Échafaudages SARL" });
        db.Branches.Add(new Branch { Id = Algiers, NameFr = "Alger-Centre", IsMain = true, IsOpenForOperation = true });
        db.Branches.Add(new Branch { Id = Oran, NameFr = "Oran", IsOpenForOperation = true });
        await db.SaveChangesAsync();
        await CoreDataSeeder.EnsureAsync(db);

        var regional = new Algeria();
        var store = new PayrollSettingsStore(db, regional);
        var directory = new EmployeeDirectory(db, store, regional, TimeProvider.System);
        // The run reads its staff and its attendance through the same two services the sheet does,
        // so a test that drives the run exercises the one answer to "who is paid" and "how many
        // days", rather than a second copy of both written for it.
        var harness = new PayrollRunHarness(
            connection, db,
            new PayrollDocuments(
                db, new LedgerPoster(db), store, regional,
                directory, new AttendanceSheet(db, directory, store, regional)),
            store,
            queries);

        await harness.SaveSettingsAsync(Profile(tweak));
        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ---------- arranging ----------

    /// <summary>
    /// Writes the settings row straight, rather than through the store's two halves: these tests
    /// are about the run, and driving the ownership split here would test that instead.
    /// </summary>
    public async Task SaveSettingsAsync(Engine.PayrollSettings settings)
    {
        var row = await Db.PayrollConfigurations
            .FirstOrDefaultAsync(x => x.Id == PayrollConfiguration.SingletonId);
        if (row is null)
        {
            row = new PayrollConfiguration();
            Db.PayrollConfigurations.Add(row);
        }
        row.SettingsJson = JsonSerializer.Serialize(settings, Json);
        row.Revision += 1;
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    public async Task<Employee> HireAsync(
        string name, decimal baseRate, PayBasis basis = PayBasis.Monthly, Guid? branchId = null,
        AttendanceMode? mode = null, DateOnly? hiredOn = null, DateOnly? leftOn = null)
    {
        var employee = new Employee
        {
            Number = $"EMP-{await Db.Employees.CountAsync() + 1:D6}",
            FullName = name,
            SocialSecurityNumber = "SS-0001",
            BranchId = branchId ?? Algiers,
            HiredOn = hiredOn ?? new DateOnly(2020, 1, 1),
            LeftOn = leftOn,
            PayBasis = basis,
            BaseRate = baseRate,
            AttendanceModeOverride = mode
        };
        Db.Employees.Add(employee);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return employee;
    }

    /// <summary>
    /// A whole crew with their month already declared, written in one save. Used by the query-count
    /// test, where the point is the size of the branch and not how it got there.
    /// </summary>
    public async Task HireCrewAsync(int count, Guid? branchId = null)
    {
        var start = await Db.Employees.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var employee = new Employee
            {
                Number = $"EMP-{start + i + 1:D6}",
                FullName = $"Ouvrier {start + i + 1}",
                BranchId = branchId ?? Algiers,
                HiredOn = new DateOnly(2020, 1, 1),
                PayBasis = PayBasis.Daily,
                BaseRate = 1_500m
            };
            Db.Employees.Add(employee);
            Db.AttendanceDeclarations.Add(new AttendanceDeclaration
            {
                EmployeeId = employee.Id, PeriodYear = Year, PeriodMonth = Month, DaysPresent = 20m
            });
        }
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>A foreman's period totals — the Declared capture mode.</summary>
    public async Task DeclareAsync(
        Guid employeeId, decimal daysPresent, decimal paidLeave = 0m, decimal unpaid = 0m,
        int year = Year, int month = Month, (string Key, decimal Hours)? overtime = null)
    {
        var declaration = new AttendanceDeclaration
        {
            EmployeeId = employeeId, PeriodYear = year, PeriodMonth = month,
            DaysPresent = daysPresent, PaidLeaveDays = paidLeave, UnpaidAbsenceDays = unpaid
        };
        if (overtime is { } hours)
            declaration.Overtime.Add(new AttendanceDeclarationOvertime
            {
                DeclarationId = declaration.Id, RuleKey = hours.Key, Hours = hours.Hours
            });

        Db.AttendanceDeclarations.Add(declaration);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>One recorded day — the other capture mode, which the run reads the same way.</summary>
    /// <param name="hours">
    /// Set for a part day. Four hours of a ten-hour day is 0.4 of a day on the sheet, and the run
    /// has to read the same fraction or the screen and the payslip disagree about what was worked.
    /// </param>
    public async Task RecordDayAsync(
        Guid employeeId, DateOnly date, AttendanceDayStatus status, decimal? hours = null)
    {
        Db.AttendanceDays.Add(new AttendanceDay
        {
            EmployeeId = employeeId, WorkDate = date, Status = status, HoursWorked = hours
        });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>
    /// A pair of clock punches and nothing else — the third way a month arrives, and the one that
    /// materialises no <see cref="AttendanceDay"/> at all.
    /// </summary>
    public async Task PunchAsync(Guid employeeId, DateTime inUtc, decimal hours)
    {
        Db.ClockEvents.Add(new ClockEvent { EmployeeId = employeeId, AtUtc = inUtc, Kind = ClockEventKind.In });
        Db.ClockEvents.Add(new ClockEvent
        {
            EmployeeId = employeeId, AtUtc = inUtc.AddHours((double)hours), Kind = ClockEventKind.Out
        });
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Somebody leaves, exactly as <c>POST /employees/{id}/leave</c> records it: a date <b>and</b> a
    /// cleared active flag. Both, because testing the date alone would not catch a run that filters
    /// on the flag.
    /// </summary>
    public async Task LeaveAsync(Guid employeeId, DateOnly on)
    {
        var employee = await Db.Employees.FirstAsync(e => e.Id == employeeId);
        employee.LeftOn = on;
        employee.IsActive = false;
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>
    /// An advance, written as a row rather than through the grant route: §5.8 belongs to another
    /// part, and what these tests are about is what the run does with one that is already open.
    /// </summary>
    public async Task<WageAdvance> AdvanceAsync(
        Guid employeeId, decimal amount, Guid? branchId = null, DateTime? grantedAtUtc = null)
    {
        var advance = new WageAdvance
        {
            Number = $"ADV-{await Db.WageAdvances.CountAsync() + 1:D6}",
            EmployeeId = employeeId,
            BranchId = branchId ?? Algiers,
            Amount = amount,
            GrantedAtUtc = grantedAtUtc ?? new DateTime(2026, 3, 5, 9, 0, 0, DateTimeKind.Utc)
        };
        Db.WageAdvances.Add(advance);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return advance;
    }

    /// <summary>
    /// A payment against one payslip line, written directly for the same reason: §5.10 is another
    /// part's, and the rule under test here is only that a paid line stops a recompute.
    /// </summary>
    public async Task<WagePayment> PayLineAsync(Guid runId, Guid runLineId, decimal amount)
    {
        var payment = new WagePayment
        {
            Number = $"WP-{await Db.WagePayments.CountAsync() + 1:D6}",
            RunId = runId, BranchId = Algiers, Amount = amount
        };
        payment.Lines.Add(new WagePaymentLine { WagePaymentId = payment.Id, RunLineId = runLineId, Amount = amount });
        Db.WagePayments.Add(payment);

        var line = await Db.PayrollRunLines.FirstAsync(l => l.Id == runLineId);
        line.PaidAmount = amount;
        line.WagePaymentId = payment.Id;

        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return payment;
    }

    // ---------- driving the run ----------

    public async Task<PayrollRun> OpenAsync(
        Guid? branchId = null, int year = Year, int month = Month, bool calculateStatutoryDeductions = true)
    {
        var result = await Payroll.CreateAsync(branchId ?? Algiers, year, month, "admin");
        Assert.True(result.Ok, result.Message);
        // Most pre-policy tests describe historical statutory runs. Production creates an exempt
        // run; a test of that new policy opts in explicitly so old arithmetic assertions stay
        // focused on the rule they were written to cover.
        if (calculateStatutoryDeductions)
        {
            result.Value!.CalculateStatutoryDeductions = true;
            await Db.SaveChangesAsync();
        }
        Db.ChangeTracker.Clear();
        return result.Value!;
    }

    public async Task<PayrollRun> ComputeAsync(Guid runId)
    {
        var result = await Payroll.ComputeAsync(runId, "admin");
        Assert.True(result.Ok, result.Message);
        Db.ChangeTracker.Clear();
        return result.Value!;
    }

    public async Task<PayrollRun> ApproveAsync(Guid runId)
    {
        var result = await Payroll.ApproveAsync(runId, "admin");
        Assert.True(result.Ok, result.Message);
        Db.ChangeTracker.Clear();
        return result.Value!;
    }

    public async Task<PayrollRun> ReverseAsync(Guid runId, string reason = "erreur de saisie")
    {
        var result = await Payroll.ReverseRunAsync(runId, reason, "admin");
        Assert.True(result.Ok, result.Message);
        Db.ChangeTracker.Clear();
        return result.Value!;
    }

    public Task<WageAdvance> AdvanceRowAsync(Guid advanceId) =>
        Db.WageAdvances.AsNoTracking().FirstAsync(a => a.Id == advanceId);

    public async Task<PayrollRun> ReadAsync(Guid runId)
    {
        Db.ChangeTracker.Clear();
        return await Db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
    }

    public Task<List<PayrollRunLine>> LinesAsync(Guid runId) =>
        Db.PayrollRunLines.AsNoTracking().Include(l => l.Elements)
            .Where(l => l.RunId == runId)
            .OrderBy(l => l.EmployeeNumberSnapshot)
            .ToListAsync();

    // ---------- reading the ledger back ----------

    /// <summary>
    /// Every line the company has posted, by account code. Summed here rather than in the
    /// database: SQLite stores decimal as TEXT, so a server-side Sum compares strings.
    /// </summary>
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

    public Task<List<LedgerEntry>> EntriesAsync(string sourceType = LedgerSources.PayrollRun) =>
        Db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.SourceType == sourceType)
            .ToListAsync();
}
