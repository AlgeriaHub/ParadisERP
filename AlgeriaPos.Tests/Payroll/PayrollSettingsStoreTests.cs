using AlgeriaPos.Application.Payroll;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Payroll;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Payroll;

/// <summary>
/// The one reader and writer of the payroll figures.
///
/// <para>
/// The split between the company's own practice and the figures law sets is enforced here rather
/// than at whichever endpoint happens to be in front of it, so these tests are the ones that
/// matter: a second screen, an import or a future route cannot route around what they pin.
/// </para>
/// </summary>
public class PayrollSettingsStoreTests
{
    private sealed class Preset : IRegionalSettingsProvider
    {
        private readonly RegionalSettings settings = RegionalPresets.Create(RegionalPresets.DefaultCountry)!;
        public RegionalSettings Get() => settings;
    }

    private static async Task<(SqliteConnection Connection, PosDbContext Db, PayrollSettingsStore Store)> NewAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db, new PayrollSettingsStore(db, new Preset()));
    }

    [Fact]
    public async Task Nothing_saved_yet_reads_the_country_preset_and_writes_no_row()
    {
        var (connection, db, store) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;

        var read = await store.ReadAsync();

        Assert.Equal(0, read.Revision);
        Assert.Equal("DZ", read.Settings.CountryCode);
        // A preset is a starting point, never an authority: it arrives needing review.
        Assert.True(read.Settings.NeedsReview);
        // And reading it has created nothing. A GET that writes makes the first person to open
        // the screen the author of the settings, and lets two readers race to create one row.
        Assert.Empty(await db.PayrollConfigurations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_company_edit_saves_its_own_half_and_refuses_a_statutory_one_by_name()
    {
        var (connection, db, store) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;

        var current = (await store.ReadAsync()).Settings;
        current.WorkingDaysPerMonth = 24m;
        current.Allowances[0].Value = 3000m;

        var saved = await store.ApplyCompanyEditAsync(current, revision: 0, by: "benali");
        Assert.True(saved.Ok);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(24m, saved.Settings.WorkingDaysPerMonth);
        Assert.Equal(3000m, saved.Settings.Allowances[0].Value);

        // The same screen, with a contribution rate nudged. Refused rather than ignored: an
        // administrator who believes they lowered it and did not would find out from a payslip.
        var withRate = (await store.ReadAsync()).Settings;
        withRate.SocialEmployeeRate += 1m;
        var refused = await store.ApplyCompanyEditAsync(withRate, revision: 1, by: "benali");

        Assert.Equal(PayrollSettingsOutcome.Refused, refused.Outcome);
        Assert.Contains(nameof(PayrollSettings.SocialEmployeeRate), refused.Message);
        // And nothing moved.
        Assert.Equal(1, (await store.ReadAsync()).Revision);
        Assert.Equal(saved.Settings.SocialEmployeeRate, (await store.ReadAsync()).Settings.SocialEmployeeRate);
    }

    [Fact]
    public async Task A_statutory_put_that_also_moves_an_allowance_leaves_the_allowance_alone()
    {
        var (connection, db, store) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;

        var start = (await store.ReadAsync()).Settings;
        start.Allowances[0].Value = 2500m;
        var stored = await store.ApplyCompanyEditAsync(start, revision: 0, by: "benali");
        Assert.True(stored.Ok);

        // A statutory save carrying a company change with it — which is exactly what
        // PayrollOwnership.ApplyOperatorEdit would let through, since it clones what it is given.
        var incoming = (await store.ReadAsync()).Settings;
        incoming.SocialEmployeeRate = 10m;
        incoming.Allowances[0].Value = 99_999m;
        incoming.WorkingDaysPerMonth = 1m;

        var result = await store.ApplyStatutoryEditAsync(
            incoming, stored.Revision, reviewed: false, confirmedBy: null, by: "benali");

        Assert.True(result.Ok);
        Assert.Equal(10m, result.Settings.SocialEmployeeRate);
        Assert.Equal(2500m, result.Settings.Allowances[0].Value);
        Assert.Equal(26m, result.Settings.WorkingDaysPerMonth);
    }

    [Fact]
    public async Task A_second_administrator_saving_over_an_open_screen_is_refused_not_silently_overwritten()
    {
        var (connection, db, store) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;

        var first = (await store.ReadAsync()).Settings;
        first.HoursPerDay = 7m;
        Assert.True((await store.ApplyCompanyEditAsync(first, revision: 0, by: "benali")).Ok);

        // The other screen still believes the row is at revision 0.
        var second = (await store.ReadAsync()).Settings;
        second.HoursPerDay = 10m;
        var stale = await store.ApplyCompanyEditAsync(second, revision: 0, by: "amina");

        Assert.Equal(PayrollSettingsOutcome.Stale, stale.Outcome);
        Assert.Equal(7m, (await store.ReadAsync()).Settings.HoursPerDay);
    }

    [Fact]
    public async Task Confirming_takes_the_mark_off_and_records_who_answered_for_the_figures()
    {
        var (connection, db, store) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;

        var start = (await store.ReadAsync()).Settings;
        start.SocialEmployeeRate = 9m;
        var saved = await store.ApplyStatutoryEditAsync(
            start, revision: 0, reviewed: false, confirmedBy: null, by: "benali");

        // An admin correcting a figure has not thereby checked it against the finance law.
        Assert.True(saved.Settings.NeedsReview);
        Assert.Null(saved.Settings.ConfirmedBy);

        var confirmed = await store.ConfirmStatutoryAsync(saved.Revision, confirmedBy: "Cabinet Kaci", by: "benali");

        Assert.True(confirmed.Ok);
        Assert.False(confirmed.Settings.NeedsReview);
        Assert.Equal("Cabinet Kaci", confirmed.Settings.ConfirmedBy);
        Assert.NotNull(confirmed.Settings.ConfirmedAtUtc);
        // No figure moved with it.
        Assert.Equal(9m, confirmed.Settings.SocialEmployeeRate);
    }

    [Fact]
    public async Task A_statutory_figure_that_moves_after_a_confirmation_is_unconfirmed_again()
    {
        var (connection, db, store) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;

        var confirmed = await store.ConfirmStatutoryAsync(revision: 0, confirmedBy: "Cabinet Kaci", by: "benali");
        Assert.False(confirmed.Settings.NeedsReview);

        var moved = confirmed.Settings;
        moved.MinimumWage = 20_000m;
        var after = await store.ApplyStatutoryEditAsync(
            moved, confirmed.Revision, reviewed: false, confirmedBy: null, by: "benali");

        // A figure that has just moved is not a figure anybody has checked, so the banner and
        // the provisional mark come back with it.
        Assert.True(after.Settings.NeedsReview);
        Assert.Null(after.Settings.ConfirmedBy);
        Assert.Null(after.Settings.ConfirmedAtUtc);
    }

    [Fact]
    public async Task A_company_edit_leaves_a_confirmation_where_it_was()
    {
        var (connection, db, store) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;

        var confirmed = await store.ConfirmStatutoryAsync(revision: 0, confirmedBy: "Cabinet Kaci", by: "benali");

        var company = confirmed.Settings;
        company.HoursPerDay = 9m;
        var after = await store.ApplyCompanyEditAsync(company, confirmed.Revision, by: "benali");

        // Changing what the company pays says nothing about the rates the law sets.
        Assert.True(after.Ok);
        Assert.False(after.Settings.NeedsReview);
        Assert.Equal("Cabinet Kaci", after.Settings.ConfirmedBy);
    }

    [Fact]
    public async Task An_impossible_figure_is_refused_with_the_sentence_that_explains_it()
    {
        var (connection, db, store) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;

        var wrong = (await store.ReadAsync()).Settings;
        wrong.SocialEmployeeRate = 150m;

        var result = await store.ApplyStatutoryEditAsync(
            wrong, revision: 0, reviewed: false, confirmedBy: null, by: "benali");

        Assert.Equal(PayrollSettingsOutcome.Invalid, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Empty(await db.PayrollConfigurations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void The_payroll_figures_never_replicate_to_a_till()
    {
        // One word in the class declaration would put the company's contribution rates and its
        // whole allowance table on every cashier's device at the next pull. Payroll has no
        // reader there, and this is the mechanism that keeps it that way.
        Assert.False(typeof(SyncEntity).IsAssignableFrom(typeof(PayrollConfiguration)));
    }

    [Fact]
    public async Task There_is_one_settings_row_however_many_saves_it_takes()
    {
        var (connection, db, store) = await NewAsync();
        await using var _ = connection;
        await using var __ = db;

        var revision = 0L;
        for (var i = 0; i < 3; i++)
        {
            var settings = (await store.ReadAsync()).Settings;
            settings.HoursPerDay = 8m - i * 0.5m;
            revision = (await store.ApplyCompanyEditAsync(settings, revision, by: "benali")).Revision;
        }

        var rows = await db.PayrollConfigurations.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(PayrollConfiguration.SingletonId, rows[0].Id);
        Assert.Equal(3, rows[0].Revision);
    }
}
