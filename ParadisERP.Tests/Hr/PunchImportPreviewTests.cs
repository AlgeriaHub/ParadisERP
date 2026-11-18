using ParadisERP.Application.Contracts;
using ParadisERP.Application.Payroll;
using ParadisERP.Infrastructure.Hr;
using ParadisERP.Tests.Catalog;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Catalog.TestWorkbook;

namespace ParadisERP.Tests.Hr;

/// <summary>
/// FR-3B: the preview of a terminal's export for one branch. A person is found by the number on
/// their card and by nothing else; a number nobody carries is reported with the names the file
/// gives it; another branch's employee is said, not named; and nothing is written.
/// </summary>
public class PunchImportPreviewTests
{
    private static readonly PunchMapping Map = new(Number: 1, Name: 2, Date: 3, Time: 4, DateTime: null, Marker: null);

    private static async Task<EmployeeDto> CardAsync(HrHarness h, string name, string fingerprint, Guid? branch = null)
    {
        var result = await h.Employees.CreateAsync(new UpsertEmployeeRequest(
            name, branch ?? HrHarness.Algiers, "Monthly", 52_000m, "Cash", new DateOnly(2020, 1, 1),
            FingerprintNumber: fingerprint));
        Assert.True(result.Ok, result.Message);
        return result.Value!;
    }

    private static Application.Catalog.Worksheet Month() => Read(Build(new TestWorkbook.Sheet("Sept", [
        new Row(1, ("A", "AC-No."), ("B", "Name"), ("C", "Date"), ("D", "Time")),
        new Row(2, ("A", 7), ("B", "Ali B."), ("C", "01/09/2026"), ("D", "07:58")),
        new Row(3, ("A", "007"), ("B", "Ali B."), ("C", "01/09/2026"), ("D", "17:02")),
        new Row(4, ("A", 7), ("B", "Ali B."), ("C", "02/09/2026"), ("D", "08:01")),
        // Omar's name is in the file and on a card, but his card carries no number: unknown.
        new Row(5, ("A", 9), ("B", "Omar Ziane"), ("C", "01/09/2026"), ("D", "08:10")),
        new Row(6, ("A", 9), ("B", "Omar Ziane"), ("C", "01/09/2026"), ("D", "16:55")),
        // An Oran employee's number in Algiers' file.
        new Row(7, ("A", 12), ("B", "Karim"), ("C", "01/09/2026"), ("D", "09:00")),
        new Row(8, ("A", 7), ("B", "Ali B."), ("C", "le 3"), ("D", "08:00")),
    ])));

    [Fact]
    public async Task People_are_found_by_their_card_s_number_and_never_by_the_name_in_the_file()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await CardAsync(h, "Ali Benali", "7");
        await h.HireAsync("Omar Ziane");
        await CardAsync(h, "Karim Haddad", "12", HrHarness.Oran);
        var preview = new PunchImportPreview(h.Db);

        var result = await preview.PreviewAsync(HrHarness.Algiers, "sept.xlsx", Month(), null, Map);

        Assert.True(result.Ok, result.Message);
        var p = result.Value!;
        Assert.Equal(1, p.HeaderRow);
        Assert.Null(p.MappingProblem);
        Assert.Equal(new PunchSummaryDto(7, 3, 2, 1, 1, 7, "2026-09-01T07:58:00", "2026-09-02T08:01:00"), p.Summary);

        var matched = p.Rows.Where(r => r.Status == "matched").ToList();
        Assert.All(matched, r => Assert.Equal(ali.Id, r.EmployeeId));
        Assert.Equal("Ali Benali", matched[0].EmployeeName);
        // The file's own spelling of the name travels beside the employee's, for the eye only.
        Assert.Equal("Ali B.", matched[0].Name);

        var employee = Assert.Single(p.Employees);
        Assert.Equal((ali.Number, 3, 2), (employee.EmployeeNumber, employee.Punches, employee.Dates));

        // "Omar Ziane" is both in the file and on the payroll, and still not matched.
        var unknown = Assert.Single(p.UnknownNumbers);
        Assert.Equal(("9", 2), (unknown.Number, unknown.Rows));
        Assert.Equal(["Omar Ziane"], unknown.Names);
        Assert.All(p.Rows.Where(r => r.Status == "unknown"), r => Assert.Null(r.EmployeeId));

        // Another branch's employee: the number is said, the person is not.
        var elsewhere = Assert.Single(p.OtherBranchNumbers);
        Assert.Equal(("12", 1), (elsewhere.Number, elsewhere.Rows));
        var row7 = p.Rows.Single(r => r.Row == 7);
        Assert.Equal("otherBranch", row7.Status);
        Assert.Null(row7.EmployeeId);
        Assert.Null(row7.EmployeeName);

        var bad = p.Rows.Single(r => r.Status == "invalid");
        Assert.Equal((8, PunchProblems.DateUnreadable), (bad.Row, bad.Problem));
    }

    [Fact]
    public async Task The_same_file_read_for_the_other_branch_turns_the_matches_round()
    {
        await using var h = await HrHarness.NewAsync();
        await CardAsync(h, "Ali Benali", "7");
        var karim = await CardAsync(h, "Karim Haddad", "12", HrHarness.Oran);

        var p = (await new PunchImportPreview(h.Db).PreviewAsync(HrHarness.Oran, "sept.xlsx", Month(), null, Map)).Value!;

        Assert.Equal(karim.Id, Assert.Single(p.Employees).EmployeeId);
        Assert.Equal("7", Assert.Single(p.OtherBranchNumbers).Number);
    }

    [Fact]
    public async Task Previewing_writes_nothing()
    {
        await using var h = await HrHarness.NewAsync();
        await CardAsync(h, "Ali Benali", "7");

        await new PunchImportPreview(h.Db).PreviewAsync(HrHarness.Algiers, "sept.xlsx", Month(), null, Map);

        Assert.Equal(0, await h.Db.ClockEvents.CountAsync());
        Assert.Equal(0, await h.Db.AttendanceDays.CountAsync());
        Assert.Equal(0, await h.Db.AttendanceDeclarations.CountAsync());
        Assert.False(h.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task An_unfinished_mapping_shows_the_columns_and_reads_no_row()
    {
        await using var h = await HrHarness.NewAsync();

        var p = (await new PunchImportPreview(h.Db).PreviewAsync(
            HrHarness.Algiers, "sept.xlsx", Month(), null, new PunchMapping(null, null, null, null, null, null))).Value!;

        Assert.Equal(PunchProblems.MapNumber, p.MappingProblem);
        Assert.Null(p.Summary);
        Assert.Empty(p.Rows);
        Assert.Equal(["A", "B", "C", "D"], p.Columns.Select(c => c.Letter));
        Assert.Equal("AC-No.", p.Columns[0].Header);
        Assert.Equal(["7", "007", "7"], p.Columns[0].Samples);
        Assert.Equal(8, p.Top.Count);
    }

    [Fact]
    public async Task Choosing_no_header_row_reads_the_first_row_as_data()
    {
        await using var h = await HrHarness.NewAsync();
        await CardAsync(h, "Ali Benali", "7");

        var p = (await new PunchImportPreview(h.Db).PreviewAsync(HrHarness.Algiers, "sept.xlsx", Month(), 0, Map)).Value!;

        Assert.Null(p.HeaderRow);
        Assert.Equal(1, p.SuggestedHeaderRow);
        // Row 1's words are now a row like any other, and an unreadable one.
        Assert.Equal(PunchProblems.DateUnreadable, p.Rows.Single(r => r.Row == 1).Problem);
        Assert.Null(p.Columns[0].Header);
    }

    [Fact]
    public async Task An_unknown_branch_is_refused()
    {
        await using var h = await HrHarness.NewAsync();
        var refused = await new PunchImportPreview(h.Db).PreviewAsync(Guid.NewGuid(), "x.xlsx", Month(), null, Map);
        Assert.Equal(HrProblems.BranchUnknown, refused.Code);
    }

    [Fact]
    public async Task A_matched_employee_with_both_times_comes_back_as_two_punch_rows_with_exit_flag()
    {
        await using var h = await HrHarness.NewAsync();
        var ali = await CardAsync(h, "Ali Benali", "7");
        var preview = new PunchImportPreview(h.Db);

        var sheet = Read(Build(new TestWorkbook.Sheet("Sept", [
            new Row(1, ("A", "No."), ("B", "Name"), ("C", "Date"), ("D", "In"), ("E", "Out")),
            new Row(2, ("A", 7), ("B", "Ali B."), ("C", "01/09/2026"), ("D", "08:00"), ("E", "17:00")),
        ])));

        var mapping = new PunchMapping(Number: 1, Name: 2, Date: 3, Time: 4, DateTime: null, Marker: null, TimeOut: 5);
        var result = await preview.PreviewAsync(HrHarness.Algiers, "sept.xlsx", sheet, null, mapping);

        Assert.True(result.Ok, result.Message);
        var p = result.Value!;
        var matched = p.Rows.Where(r => r.Status == "matched").ToList();
        Assert.Equal(2, matched.Count);

        Assert.Equal(ali.Id, matched[0].EmployeeId);
        Assert.Equal("2026-09-01T08:00:00", matched[0].At);
        Assert.False(matched[0].Exit);

        Assert.Equal(ali.Id, matched[1].EmployeeId);
        Assert.Equal("2026-09-01T17:00:00", matched[1].At);
        Assert.True(matched[1].Exit);
    }

    private static Application.Catalog.Worksheet RealDeviceSheet() => Read(Build(new TestWorkbook.Sheet("Feuille1", [
        new Row(1, ("A", "Première & Dernière")),
        new Row(2,
            ("A", "Numéro d'employé"),
            ("B", "Prénom"),
            ("C", "Département"),
            ("D", "Date"),
            ("E", "Jour de semaine"),
            ("F", "Premier pointage"),
            ("G", "Dernier pointage"),
            ("H", "Temps total")),
        new Row(3,
            ("A", "184"), ("B", "EMPLOYE A"), ("C", "DEPOT"), ("D", "2026-09-01"),
            ("E", "Mardi"), ("F", "07:54"), ("G", "17:03"), ("H", "09:09")),
        new Row(4,
            ("A", "184"), ("B", "EMPLOYE A"), ("C", "DEPOT"), ("D", "2026-09-09"),
            ("E", "Mercredi"), ("F", "15:26"), ("G", "15:26"), ("H", "00:00")),
        new Row(5,
            ("A", "184"), ("B", "EMPLOYE A"), ("C", "DEPOT"), ("D", "2026-09-07"),
            ("E", "Lundi"), ("F", "00:31"), ("G", "17:04"), ("H", "16:33")),
        new Row(6,
            ("A", "185"), ("B", "EMPLOYE B"), ("C", "DEPOT"), ("D", "2026-09-01"),
            ("E", "Mardi"), ("F", "08:10"), ("G", ""), ("H", "")),
    ])));

    [Fact]
    public async Task Previewing_with_empty_mapping_returns_problem_and_suggested_mapping()
    {
        await using var h = await HrHarness.NewAsync();
        var preview = new PunchImportPreview(h.Db);

        var emptyMapping = new PunchMapping(null, null, null, null, null, null);
        var result = await preview.PreviewAsync(HrHarness.Algiers, "device.xlsx", RealDeviceSheet(), null, emptyMapping);

        Assert.True(result.Ok, result.Message);
        var p = result.Value!;
        Assert.Equal(PunchProblems.MapNumber, p.MappingProblem);
        Assert.NotNull(p.SuggestedMapping);
        Assert.Equal(1, p.SuggestedMapping.Number);
        Assert.Equal(2, p.SuggestedMapping.Name);
        Assert.Equal(4, p.SuggestedMapping.Date);
        Assert.Equal(6, p.SuggestedMapping.Time);
        Assert.Equal(7, p.SuggestedMapping.TimeOut);
    }

    [Fact]
    public async Task Previewing_with_suggested_mapping_counts_flagged_punches()
    {
        await using var h = await HrHarness.NewAsync();
        await CardAsync(h, "Employe A", "184");
        await CardAsync(h, "Employe B", "185");
        var preview = new PunchImportPreview(h.Db);

        var mapping = new PunchMapping(1, 2, 4, 6, null, null, TimeOut: 7);
        var result = await preview.PreviewAsync(HrHarness.Algiers, "device.xlsx", RealDeviceSheet(), null, mapping);

        Assert.True(result.Ok, result.Message);
        var p = result.Value!;
        Assert.NotNull(p.Summary);
        Assert.Equal(3, p.Summary.Flagged);
    }
}
