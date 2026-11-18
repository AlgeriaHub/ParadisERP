using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Hr;
using AlgeriaPos.Infrastructure.Payroll;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Hr;

/// <summary>
/// A company with two branches and a payroll profile, against a real database.
///
/// <para>
/// Real because almost everything attendance claims is a claim about what is stored afterwards:
/// that a second submission replaces a row rather than adding one, that a locked month refuses
/// the write, that a day grid can be cleared back to nothing. The arithmetic that needs no
/// database at all is in <see cref="AttendanceSummaryTests"/>, which touches none of this.
/// </para>
/// </summary>
public sealed class HrHarness(
    SqliteConnection connection,
    PosDbContext db,
    EmployeeDirectory employees,
    AttendanceSheet attendance,
    PayrollSettingsStore settings) : IAsyncDisposable
{
    public static readonly Guid Algiers = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Oran = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public PosDbContext Db { get; } = db;
    public EmployeeDirectory Employees { get; } = employees;
    public AttendanceSheet Attendance { get; } = attendance;
    public PayrollSettingsStore Settings { get; } = settings;

    /// <summary>Algeria, an hour ahead of UTC all year — which is the whole reason periods are ints.</summary>
    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    /// <param name="clock">The instant an employee is created at (the month in their number); now when not given.</param>
    public static async Task<HrHarness> NewAsync(TimeProvider? clock = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.SyncCounters.Add(new SyncCounter { Id = 1, Value = 0 });
        db.Stores.Add(new Store { NameFr = "Béton & Échafaudages SARL" });
        db.Branches.Add(new Branch { Id = Algiers, Code = "361", NameFr = "Alger-Centre", IsMain = true, IsOpenForOperation = true });
        db.Branches.Add(new Branch { Id = Oran, Code = "785", NameFr = "Oran", IsOpenForOperation = true });
        await db.SaveChangesAsync();

        var regional = new Algeria();
        var settings = new PayrollSettingsStore(db, regional);
        var directory = new EmployeeDirectory(db, settings, regional, clock ?? TimeProvider.System);
        return new HrHarness(
            connection, db, directory, new AttendanceSheet(db, directory, settings, regional), settings);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await connection.DisposeAsync();
    }

    // ---------- the company ----------

    /// <summary>Hires somebody. Monthly, in Algiers, declared by the company default, unless told otherwise.</summary>
    public async Task<EmployeeDto> HireAsync(
        string name,
        string basis = "Monthly",
        decimal rate = 52_000m,
        Guid? branchId = null,
        string? attendanceMode = null,
        DateOnly? hiredOn = null,
        IReadOnlyList<EmployeeAllowanceDto>? allowances = null)
    {
        var result = await Employees.CreateAsync(new UpsertEmployeeRequest(
            name, branchId ?? Algiers, basis, rate, "Cash",
            hiredOn ?? new DateOnly(2020, 1, 1),
            AttendanceModeOverride: attendanceMode,
            Allowances: allowances));

        Assert.True(result.Ok, result.Message);
        return result.Value!;
    }

    /// <summary>A payroll run over a month, in whatever state the test needs it to be in.</summary>
    public async Task<PayrollRun> RunAsync(
        int year, int month, PayrollRunStatus status, Guid? branchId = null, string number = "RUN-000001")
    {
        var run = new PayrollRun
        {
            Number = number,
            BranchId = branchId ?? Algiers,
            PeriodYear = year,
            PeriodMonth = month,
            Status = status
        };
        Db.PayrollRuns.Add(run);
        await Db.SaveChangesAsync();
        return run;
    }

    /// <summary>Cash drawn against wages, so the employee list has an open balance to report.</summary>
    public async Task<WageAdvance> AdvanceAsync(Guid employeeId, decimal amount, decimal recovered = 0m)
    {
        var advance = new WageAdvance
        {
            Number = $"ADV-{Guid.NewGuid().ToString()[..6]}",
            EmployeeId = employeeId,
            BranchId = Algiers,
            Amount = amount,
            Recovered = recovered,
            Status = WageAdvanceStatus.Open
        };
        Db.WageAdvances.Add(advance);
        await Db.SaveChangesAsync();
        return advance;
    }

    // ---------- shorthand ----------

    public Task<HrResult<AttendanceSheetDto>> SaveSheetAsync(
        int year, int month, params AttendanceDeclarationRowRequest[] rows) =>
        Attendance.SaveSheetAsync(new UpsertAttendanceSheetRequest(Algiers, year, month, rows), "foreman");

    public Task<HrResult<AttendanceSheetDto>> ReadSheetAsync(int year, int month, Guid? branchId = null) =>
        Attendance.ReadSheetAsync(branchId ?? Algiers, year, month, 0, 100, null);

    public static AttendanceDeclarationRowRequest Declared(
        Guid employeeId, decimal present, decimal paidLeave = 0m, decimal unpaid = 0m,
        IReadOnlyList<AttendanceOvertimeDto>? overtime = null) =>
        new(employeeId, present, paidLeave, unpaid, overtime);
}
