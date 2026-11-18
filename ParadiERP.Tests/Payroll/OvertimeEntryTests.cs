using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Hr;
using Microsoft.EntityFrameworkCore;
using Engine = ParadiERP.Application.Payroll;

namespace ParadiERP.Tests.Payroll;

/// <summary>
/// Overtime entries (owner, 2026-10-10): recorded by date and kind of work, corrected and deleted
/// until the month is held by a run that is locked, approved or paid, every change kept with its
/// previous values — and a draft run picks the corrected figure up when it is computed again.
/// </summary>
public class OvertimeEntryTests
{
    /// <summary>
    /// The harness profile — 20 working days of 10 hours — with two kinds of work: the workshop at
    /// the normal rate and loading at double, and a holiday band counted in days.
    /// </summary>
    private static void Kinds(Engine.PayrollSettings s) => s.OvertimeRules =
    [
        new() { Key = "atelier", Multiplier = 1m },
        new() { Key = "chargement", Multiplier = 2m },
        new() { Key = "ferie", AppliesTo = Engine.OvertimeBand.PublicHoliday, Multiplier = 2m, Unit = Engine.OvertimeUnit.Days }
    ];

    private static readonly DateOnly March10 = new(PayrollRunHarness.Year, PayrollRunHarness.Month, 10);

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    private static OvertimeEntries Entries(PayrollRunHarness harness)
    {
        var regional = new Algeria();
        var directory = new EmployeeDirectory(harness.Db, harness.Store, regional, TimeProvider.System);
        return new OvertimeEntries(harness.Db, new AttendanceSheet(harness.Db, directory, harness.Store, regional), harness.Store);
    }

    private static async Task<decimal> OvertimeAsync(PayrollRunHarness harness, Guid runId, string key) =>
        (await harness.LinesAsync(runId)).SelectMany(l => l.Elements).Where(e => e.Key == key).Sum(e => e.Amount);

    [Fact]
    public async Task An_entry_is_recorded_listed_and_paid_by_its_kind_in_the_month_s_run()
    {
        await using var harness = await PayrollRunHarness.NewAsync(Kinds);
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);
        var entries = Entries(harness);

        var created = await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10, "chargement", 3m), "chef");
        Assert.True(created.Ok, created.Message);
        await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10.AddDays(1), "atelier", 4m), "chef");

        var list = (await entries.ListAsync(PayrollRunHarness.Algiers, PayrollRunHarness.Year, PayrollRunHarness.Month, null)).Value!;
        Assert.Equal(2, list.Entries.Count);
        Assert.All(list.Entries, e => Assert.True(e.CanEdit));
        Assert.Contains(list.Kinds, k => k.Key == "ferie" && k.Unit == "Days");

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        // 20,000 ÷ 20 ÷ 10 = 100 an hour: loading 3 h × 200, workshop 4 h × 100.
        Assert.Equal(600m, await OvertimeAsync(harness, run.Id, "chargement"));
        Assert.Equal(400m, await OvertimeAsync(harness, run.Id, "atelier"));

        var history = (await entries.HistoryAsync(created.Value!.Id)).Value!;
        var row = Assert.Single(history);
        Assert.Equal(("Created", "chargement", 3m, "chef"), (row.Action, row.RuleKey, row.Hours!.Value, row.ChangedBy));
    }

    [Fact]
    public async Task Editing_an_entry_keeps_its_previous_values_and_the_draft_run_follows_on_recompute()
    {
        await using var harness = await PayrollRunHarness.NewAsync(Kinds);
        var employee = await harness.HireAsync("Karim T.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);
        var entries = Entries(harness);
        var entry = (await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10, "chargement", 3m), "chef")).Value!;

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        Assert.Equal(600m, await OvertimeAsync(harness, run.Id, "chargement"));

        // Computing locks the month, as it does for the sheet: correcting it means unlocking the
        // run first, so the lines that get approved are never older than the entries.
        Assert.Equal(HrProblems.PeriodLocked,
            (await entries.UpdateAsync(entry.Id, new UpdateOvertimeEntryRequest(March10, "atelier", 5m), "admin")).Code);
        Assert.True((await harness.Payroll.UnlockAsync(run.Id, "admin")).Ok);
        harness.Db.ChangeTracker.Clear();

        var edited = await entries.UpdateAsync(entry.Id, new UpdateOvertimeEntryRequest(March10.AddDays(2), "atelier", 5m, "corrigé"), "admin");
        Assert.True(edited.Ok, edited.Message);
        Assert.Equal((March10.AddDays(2), "atelier", 5m, "corrigé"), (edited.Value!.WorkDate, edited.Value.RuleKey, edited.Value.Hours, edited.Value.Note));

        var change = (await entries.HistoryAsync(entry.Id)).Value!.Last();
        Assert.Equal("Edited", change.Action);
        Assert.Equal((March10, "chargement", 3m), (change.PreviousWorkDate!.Value, change.PreviousRuleKey, change.PreviousHours!.Value));
        Assert.Equal((March10.AddDays(2), "atelier", 5m), (change.WorkDate!.Value, change.RuleKey, change.Hours!.Value));
        Assert.Equal("admin", change.ChangedBy);

        // A draft run holds nothing: computed again, it pays the corrected entry.
        await harness.ComputeAsync(run.Id);
        Assert.Equal(0m, await OvertimeAsync(harness, run.Id, "chargement"));
        Assert.Equal(500m, await OvertimeAsync(harness, run.Id, "atelier"));
    }

    [Fact]
    public async Task Deleting_an_entry_removes_it_and_keeps_what_it_was_in_the_history()
    {
        await using var harness = await PayrollRunHarness.NewAsync(Kinds);
        var employee = await harness.HireAsync("Yacine M.", 20_000m);
        var entries = Entries(harness);
        var entry = (await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10, "atelier", 2m), "chef")).Value!;

        var deleted = await entries.DeleteAsync(entry.Id, "admin");

        Assert.True(deleted.Ok, deleted.Message);
        Assert.False(await harness.Db.OvertimeEntries.AnyAsync());
        var last = (await entries.HistoryAsync(entry.Id)).Value!.Last();
        Assert.Equal(("Deleted", March10, "atelier", 2m), (last.Action, last.PreviousWorkDate!.Value, last.PreviousRuleKey, last.PreviousHours!.Value));
        Assert.Null(last.Hours);
        Assert.Equal(HrProblems.OvertimeEntryNotFound, (await entries.DeleteAsync(entry.Id, "admin")).Code);
    }

    [Fact]
    public async Task An_entry_inside_an_approved_run_cannot_be_edited_moved_into_or_deleted()
    {
        await using var harness = await PayrollRunHarness.NewAsync(Kinds);
        var employee = await harness.HireAsync("Amina B.", 20_000m);
        await harness.DeclareAsync(employee.Id, 20m);
        var entries = Entries(harness);
        var entry = (await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10, "chargement", 3m), "chef")).Value!;
        var april = (await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, new DateOnly(2026, 4, 2), "atelier", 1m), "chef")).Value!;

        var run = await harness.OpenAsync();
        await harness.ComputeAsync(run.Id);
        await harness.ApproveAsync(run.Id);

        var edit = await entries.UpdateAsync(entry.Id, new UpdateOvertimeEntryRequest(March10, "chargement", 9m), "admin");
        Assert.Equal(HrProblems.PeriodLocked, edit.Code);
        Assert.Equal(run.Number, edit.RunNumber);
        Assert.Equal(HrProblems.PeriodLocked, (await entries.DeleteAsync(entry.Id, "admin")).Code);
        // Nor can an open month's entry be moved into the paid one.
        Assert.Equal(HrProblems.PeriodLocked,
            (await entries.UpdateAsync(april.Id, new UpdateOvertimeEntryRequest(March10, "atelier", 1m), "admin")).Code);
        Assert.Equal(HrProblems.PeriodLocked,
            (await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10, "atelier", 1m), "chef")).Code);

        var stored = await harness.Db.OvertimeEntries.AsNoTracking().SingleAsync(e => e.Id == entry.Id);
        Assert.Equal(3m, stored.Hours);
        Assert.Single((await entries.HistoryAsync(entry.Id)).Value!);
        var list = (await entries.ListAsync(PayrollRunHarness.Algiers, PayrollRunHarness.Year, PayrollRunHarness.Month, null)).Value!;
        Assert.True(list.Locked);
        Assert.All(list.Entries, e => Assert.False(e.CanEdit));
    }

    [Fact]
    public async Task An_entry_names_a_kind_the_settings_define_and_a_day_counted_kind_takes_one_day_at_most()
    {
        await using var harness = await PayrollRunHarness.NewAsync(Kinds);
        var employee = await harness.HireAsync("Karim T.", 20_000m);
        var entries = Entries(harness);

        Assert.Equal(HrProblems.OvertimeRuleUnknown,
            (await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10, "inconnu", 2m), "chef")).Code);
        Assert.Equal(HrProblems.OvertimeHoursRequired,
            (await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10, "atelier", 0m), "chef")).Code);
        Assert.Equal(HrProblems.HoursExceedDay,
            (await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10, "atelier", 25m), "chef")).Code);
        Assert.Equal(HrProblems.DaysExceedMonth,
            (await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10, "ferie", 2m), "chef")).Code);
        Assert.True((await entries.CreateAsync(new CreateOvertimeEntryRequest(employee.Id, March10, "ferie", 1m), "chef")).Ok);
        Assert.False(await harness.Db.OvertimeEntryChanges.AnyAsync(c => c.RuleKey == "inconnu"));
    }
}
