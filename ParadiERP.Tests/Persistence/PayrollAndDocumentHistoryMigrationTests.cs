using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Engine = ParadiERP.Application.Payroll;

namespace ParadiERP.Tests.Persistence;

/// <summary>
/// The upgrade an installation that is already trading performs for `PayrollAndDocumentHistory`,
/// with rows in it — the shape of <c>HardeningAndPrintFoundationMigrationTests</c>, for the same
/// reason: a default is right in the migration file and wrong in the database the moment anyone
/// hand-edits a scaffold, and the whole of part B is built on top of these tables.
///
/// <para>
/// The release adds tables and one column. Nothing that was trading changes: the company reads as
/// "no employees, no runs, no document changes yet", and its printed wording may still be
/// rewritten three times this year — not zero, which is what the scaffolder's default would have
/// meant.
/// </para>
/// </summary>
public class PayrollAndDocumentHistoryMigrationTests
{
    /// <summary>The release before this one.</summary>
    private const string Before = "CompanyFace";

    [Fact]
    public async Task A_trading_installation_upgrades_to_no_employees_no_runs_and_no_document_changes_yet()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);

        await db.Database.GetService<IMigrator>().MigrateAsync(Before);

        // Rows in the old shape, written with raw SQL because the current model would insist on
        // columns the old tables do not have. Parameters, never formatted text: SQLite compares
        // Guid and date columns as strings and only the provider knows the spelling it queries with.
        var now = DateTime.UtcNow;
        var storeId = Guid.NewGuid();
        var branchId = Guid.NewGuid();

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Branches (Id, Version, UpdatedAtUtc, IsDeleted, NameAr, NameFr, NameEn, Address, Phone, IsMain, AllowNegativeStock, IsActive, IsOpenForOperation) " +
            "VALUES ({0}, 1, {1}, 0, '', 'Oran', '', '', '', 1, 0, 1, 1)",
            branchId, now);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Stores (Id, Version, UpdatedAtUtc, IsDeleted, NameAr, NameFr, NameEn, Address, Phone, FiscalIds, ReceiptFooter, " +
            "RequireCustomerEmail, RequireCustomerBirthDate, RequireCustomerPhone, RequireCustomerAddress, RequireCustomerTaxId, " +
            "LoyaltyEnabled, LoyaltyPointsPerUnit, LoyaltyPointValue, LoyaltyMinRedeemPoints, RentalOverdueMultiplier, RentalDepositEnabled, RentalTaxRate, DocumentTexts) " +
            "VALUES ({0}, 7, {1}, 0, '', 'Ets Benali', '', '', '', '{{}}', '', 0, 0, 0, 0, 0, 0, {2}, {2}, 0, {3}, 0, {2}, {4})",
            storeId, now, 0m, 1.5m, "{\"rentalContract\":{\"fr\":\"Le locataire s'engage\\u2026\"}}");

        // The upgrade an existing installation performs. If a statement is not valid SQL on this
        // provider — the filtered unique index, the DateOnly columns — it stops here.
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        // Nobody is on the payroll yet, and nothing has been computed or paid.
        Assert.Empty(await db.Employees.AsNoTracking().ToListAsync());
        Assert.Empty(await db.PayrollRuns.AsNoTracking().ToListAsync());
        Assert.Empty(await db.PayrollRunLines.AsNoTracking().ToListAsync());
        Assert.Empty(await db.AttendanceDeclarations.AsNoTracking().ToListAsync());
        Assert.Empty(await db.AttendanceDays.AsNoTracking().ToListAsync());
        Assert.Empty(await db.ClockEvents.AsNoTracking().ToListAsync());
        Assert.Empty(await db.WageAdvances.AsNoTracking().ToListAsync());
        Assert.Empty(await db.WagePayments.AsNoTracking().ToListAsync());
        Assert.Empty(await db.PayrollStatutoryPayments.AsNoTracking().ToListAsync());

        // The wording that was configured is untouched and has no history yet: the allowance is
        // spent by changes made from here on, never backdated onto what the admin already wrote.
        Assert.Empty(await db.DocumentTextChanges.AsNoTracking().ToListAsync());

        var store = await db.Stores.AsNoTracking().SingleAsync(s => s.Id == storeId);
        Assert.Equal("Le locataire s'engage…", store.DocumentTexts!["rentalContract"]["fr"]);
        // Three, not the scaffolder's zero: an upgrade must not silently freeze the wording of a
        // company that never asked for a limit at all.
        Assert.Equal(3, store.DocumentTextChangesPerYear);
        Assert.Equal(Store.DefaultDocumentTextChangesPerYear, store.DocumentTextChangesPerYear);
        // And the replicated row did not move, so no till re-pulls a Store for a column it does
        // not carry.
        Assert.Equal(7, store.Version);

        // The new tables enforce what they were built to enforce. An employee needs a branch that
        // exists — SQLite checks foreign keys only when the pragma is on, which is how the
        // provider opens every connection.
        await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
        {
            db.Employees.Add(new Employee
            {
                Number = "EMP-000001",
                FullName = "Karim Haddad",
                BranchId = Guid.NewGuid(),
                HiredOn = new DateOnly(2026, 1, 5)
            });
            await db.SaveChangesAsync();
        });
        db.ChangeTracker.Clear();

        db.Employees.Add(new Employee
        {
            Number = "EMP-000001",
            FullName = "Karim Haddad",
            BranchId = branchId,
            HiredOn = new DateOnly(2026, 1, 5),
            PayBasis = PayBasis.Daily,
            BaseRate = 2_500m
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var hired = await db.Employees.AsNoTracking().SingleAsync();
        Assert.True(hired.IsActive);
        Assert.Null(hired.LeftOn);
        Assert.Null(hired.AttendanceModeOverride);
        Assert.Equal(PayoutMethod.Cash, hired.PayoutMethod);
        Assert.Equal(new DateOnly(2026, 1, 5), hired.HiredOn);
    }

    [Fact]
    public async Task One_live_run_per_branch_and_month_is_a_constraint_and_a_reversed_month_can_be_run_again()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();

        var branch = new Branch { NameFr = "Oran", IsActive = true };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();

        PayrollRun Run(string number, PayrollRunStatus status) => new()
        {
            Number = number,
            BranchId = branch.Id,
            PeriodYear = 2026,
            PeriodMonth = 2,
            Status = status
        };

        db.PayrollRuns.Add(Run("RUN-000001", PayrollRunStatus.Draft));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // A second live run for the same branch and month is refused by the database itself on
        // SQLite. The code check inside the write scope is the rule on both providers; this is
        // the second pair of hands MySQL does not get.
        await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
        {
            db.PayrollRuns.Add(Run("RUN-000002", PayrollRunStatus.Approved));
            await db.SaveChangesAsync();
        });
        db.ChangeTracker.Clear();

        // Reversed, the month is free again — and the reversed run stays, with its links to what
        // was posted and what undid it. That is the whole reason the index is filtered.
        var first = await db.PayrollRuns.SingleAsync();
        first.Status = PayrollRunStatus.Reversed;
        first.LedgerEntryId = Guid.NewGuid();
        first.ReversalEntryId = Guid.NewGuid();
        first.ReversalReason = "Les jours d'un chantier ont été saisis deux fois.";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.PayrollRuns.Add(Run("RUN-000003", PayrollRunStatus.Draft));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var runs = await db.PayrollRuns.AsNoTracking().OrderBy(r => r.Number).ToListAsync();
        Assert.Equal(2, runs.Count);
        Assert.NotNull(runs[0].LedgerEntryId);
        Assert.NotNull(runs[0].ReversalEntryId);
    }

    [Fact]
    public async Task A_document_text_change_keeps_the_wording_it_replaced_and_is_countable_by_kind_and_year()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        db.DocumentTextChanges.AddRange(
            new DocumentTextChange
            {
                Kind = "rentalContract",
                ChangeYear = 2026,
                PreviousTexts = "{}",
                ChangedBy = "admin"
            },
            new DocumentTextChange
            {
                Kind = "rentalContract",
                ChangeYear = 2026,
                PreviousTexts = "{\"fr\":\"v1\",\"ar\":\"نسخة 1\"}",
                ChangedBy = "admin"
            },
            // Another kind, and another year: neither spends the rental contract's 2026 allowance.
            new DocumentTextChange { Kind = "invoice", ChangeYear = 2026, PreviousTexts = "{}" },
            new DocumentTextChange { Kind = "rentalContract", ChangeYear = 2025, PreviousTexts = "{}" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var used = await db.DocumentTextChanges.AsNoTracking()
            .CountAsync(c => c.Kind == "rentalContract" && c.ChangeYear == 2026);
        Assert.Equal(2, used);

        // The predecessor is readable, which is the point of storing what was replaced rather
        // than what replaced it: the customer's signed copy stays legible.
        var second = await db.DocumentTextChanges.AsNoTracking()
            .SingleAsync(c => c.Kind == "rentalContract" && c.PreviousTexts.Contains("v1"));
        Assert.Contains("نسخة 1", second.PreviousTexts);
    }

    [Fact]
    public void No_payroll_or_document_history_entity_replicates_to_a_till()
    {
        // docs/07 §5.4: the base type is the mechanism. One word in one class declaration would
        // put every employee's salary, national id and advance on every cashier's device at the
        // next pull — silently, because the pull is driven by the base type.
        foreach (var type in new[]
                 {
                     typeof(Employee), typeof(EmployeeAllowance),
                     typeof(AttendanceDeclaration), typeof(AttendanceDeclarationOvertime),
                     typeof(AttendanceDay), typeof(AttendanceDayOvertime), typeof(ClockEvent),
                     typeof(WageAdvance),
                     typeof(PayrollRun), typeof(PayrollRunLine), typeof(PayrollRunLineElement),
                     typeof(PayrollAdjustment),
                     typeof(WagePayment), typeof(WagePaymentLine),
                     typeof(PayrollStatutoryPayment),
                     // The wording history, for the same reason the wording itself is off StoreDto.
                     typeof(DocumentTextChange)
                 })
            Assert.False(typeof(SyncEntity).IsAssignableFrom(type), type.Name);
    }

    [Fact]
    public void Only_the_catalog_replicates_and_nothing_new_has_joined_it()
    {
        // The list above names what exists today; this one catches what is written tomorrow. A
        // new entity that derives from SyncEntity by habit — the way every catalog entity does —
        // fails here whether or not anyone remembered to add it to a list.
        var options = new DbContextOptionsBuilder<PosDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var db = new PosDbContext(options);

        var replicated = db.Model.GetEntityTypes()
            .Select(t => t.ClrType)
            .Where(t => typeof(SyncEntity).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "Branch", "Category", "Customer", "GiftCard", "PaymentMethodDef", "Product", "Store", "User" },
            replicated);
    }

    [Fact]
    public void The_stored_pay_basis_and_attendance_mode_are_the_engines()
    {
        // The domain has no project references, so a stored column cannot use the engine's enum
        // and mirrors it instead — the RentalChargeKind precedent. This is the check that stops
        // the two drifting: a value added on one side and not the other would pay someone on the
        // wrong basis with nothing failing anywhere.
        Assert.Equal(Enum.GetNames<Engine.PayBasis>(), Enum.GetNames<PayBasis>());
        foreach (var name in Enum.GetNames<PayBasis>())
            Assert.Equal((int)Enum.Parse<Engine.PayBasis>(name), (int)Enum.Parse<PayBasis>(name));

        Assert.Equal(Enum.GetNames<Engine.AttendanceMode>(), Enum.GetNames<AttendanceMode>());
        foreach (var name in Enum.GetNames<AttendanceMode>())
            Assert.Equal((int)Enum.Parse<Engine.AttendanceMode>(name), (int)Enum.Parse<AttendanceMode>(name));

        // PayrollRunLine.AttendanceSource is stored with the domain copy and computed with the
        // engine's; a value on one side only would say the days came from somewhere they did not.
        Assert.Equal(Enum.GetNames<Engine.AttendanceSource>(), Enum.GetNames<AttendanceSource>());
        foreach (var name in Enum.GetNames<AttendanceSource>())
            Assert.Equal((int)Enum.Parse<Engine.AttendanceSource>(name), (int)Enum.Parse<AttendanceSource>(name));
    }

    [Fact]
    public void Reversed_is_four_because_the_sqlite_index_says_so()
    {
        // UX_PayrollRuns_LiveBranchPeriod is filtered on "Status" <> 4. Renumbering this enum
        // would leave the filter pointing at whatever state happened to take the number, and the
        // only symptom would be a month that can no longer be run after a reversal.
        Assert.Equal(4, (int)PayrollRunStatus.Reversed);
    }
}
