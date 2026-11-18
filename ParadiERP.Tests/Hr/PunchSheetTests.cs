using ParadiERP.Application.Catalog;
using ParadiERP.Application.Payroll;
using ParadiERP.Tests.Catalog;
using static ParadiERP.Tests.Catalog.TestWorkbook;

namespace ParadiERP.Tests.Hr;

/// <summary>
/// FR-3B: the fingerprint terminal's export read as punches, with no device's layout assumed —
/// the person maps the columns, the header row is a suggestion, and a text date is read in the
/// order they choose. Every row comes back, readable or not, and says why when it is not.
/// </summary>
public class PunchSheetTests
{
    private static double Serial(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) =>
        new DateTime(y, mo, d, h, mi, s).ToOADate();

    private static Worksheet Sheet(params Row[] rows) => Read(Build(new TestWorkbook.Sheet("Pointage", rows)));

    private static readonly PunchMapping Split = new(Number: 1, Name: 2, Date: 3, Time: 4, DateTime: null, Marker: null);

    [Fact]
    public void A_header_row_is_only_suggested_and_a_title_above_it_is_skipped()
    {
        var sheet = Sheet(
            new Row(1, ("A", "Rapport de pointage")),
            new Row(3, ("A", "No."), ("B", "Nom"), ("C", "Date"), ("D", "Heure")),
            new Row(4, ("A", 7), ("B", "Ali"), ("C", Serial(2026, 9, 1)), ("D", 8.0 / 24)));

        Assert.Equal(3, PunchSheet.GuessHeaderRow(sheet));
        // No row of words above data: no suggestion, and the person says which, or none.
        Assert.Null(PunchSheet.GuessHeaderRow(Sheet(new Row(1, ("A", 7), ("B", "Ali")), new Row(2, ("A", 8), ("B", "Omar")))));
    }

    [Theory]
    [InlineData(null, null, 3, 4, null, PunchProblems.MapNumber)]
    [InlineData(1, null, 3, null, null, PunchProblems.MapWhen)]
    [InlineData(1, null, null, null, null, PunchProblems.MapWhen)]
    [InlineData(1, null, 3, 4, 5, PunchProblems.MapBoth)]
    [InlineData(1, 1, 3, 4, null, PunchProblems.MapSameColumn)]
    [InlineData(1, null, 3, 3, null, PunchProblems.MapSameColumn)]
    [InlineData(1, null, 3, 4, null, null)]
    [InlineData(1, 2, null, null, 3, null)]
    public void A_mapping_names_the_number_and_either_date_and_time_or_one_combined_column(
        int? number, int? name, int? date, int? time, int? both, string? problem) =>
        Assert.Equal(problem, PunchSheet.MappingProblem(new PunchMapping(number, name, date, time, both, null)));

    [Fact]
    public void Excel_dates_and_times_are_read_to_the_second_not_to_three_decimals()
    {
        // 08:29:46 is 0.35400… of a day; rounded to 0.354 it would read 08:29:46 ± 43 s.
        var sheet = Sheet(
            new Row(1, ("A", 7), ("B", "Ali"), ("C", Serial(2026, 9, 1)), ("D", (8 * 3600 + 29 * 60 + 46) / 86400.0)),
            new Row(2, ("A", 7), ("B", "Ali"), ("C", Serial(2026, 9, 1, 17, 0)), ("D", Serial(2026, 9, 1, 17, 31, 5))));

        var punches = PunchSheet.Read(sheet, null, Split);

        Assert.Equal(new DateTime(2026, 9, 1, 8, 29, 46), punches[0].At);
        // A date cell that carries a time keeps only its day; a time cell that carries a day keeps only its time.
        Assert.Equal(new DateTime(2026, 9, 1, 17, 31, 5), punches[1].At);
        Assert.All(punches, p => Assert.False(p.TextDate));
    }

    [Fact]
    public void A_combined_column_is_read_whole()
    {
        var sheet = Sheet(
            new Row(1, ("A", "0007"), ("C", Serial(2026, 9, 2, 7, 58, 30))),
            new Row(2, ("A", 7), ("C", "2026-09-02 17:04:00")),
            new Row(3, ("A", 7), ("C", "02/09/2026")));

        var punches = PunchSheet.Read(sheet, null, new PunchMapping(1, null, null, null, 3, null));

        Assert.Equal(new DateTime(2026, 9, 2, 7, 58, 30), punches[0].At);
        Assert.Equal("7", punches[0].Number);
        Assert.Equal(new DateTime(2026, 9, 2, 17, 4, 0), punches[1].At);
        // A date with no time in a combined column is a punch nobody can place.
        Assert.Equal(PunchProblems.TimeMissing, punches[2].Problem);
        Assert.Null(punches[2].At);
    }

    [Theory]
    [InlineData("03/09/2026", DateOrder.DayMonthYear, 2026, 9, 3)]
    [InlineData("03/09/2026", DateOrder.MonthDayYear, 2026, 3, 9)]
    [InlineData("2026-09-03", DateOrder.DayMonthYear, 2026, 9, 3)]
    [InlineData("26.09.03", DateOrder.YearMonthDay, 2026, 9, 3)]
    [InlineData("03-09-26", DateOrder.DayMonthYear, 2026, 9, 3)]
    [InlineData("٠٣/٠٩/٢٠٢٦", DateOrder.DayMonthYear, 2026, 9, 3)]
    public void A_text_date_is_read_in_the_order_chosen(string text, DateOrder order, int y, int mo, int d)
    {
        var date = PunchSheet.ReadDate(new SheetCell(text, null), order, out var problem);
        Assert.Null(problem);
        Assert.Equal(new DateOnly(y, mo, d), date);
    }

    [Theory]
    [InlineData("13/25/2026", DateOrder.DayMonthYear)]
    [InlineData("31/09/2026", DateOrder.DayMonthYear)]
    [InlineData("13/09/2026", DateOrder.MonthDayYear)]
    [InlineData("hier", DateOrder.DayMonthYear)]
    [InlineData("1/9", DateOrder.DayMonthYear)]
    public void A_text_date_that_is_not_one_in_that_order_is_refused_not_guessed(string text, DateOrder order)
    {
        Assert.Null(PunchSheet.ReadDate(new SheetCell(text, null), order, out var problem));
        Assert.Equal(PunchProblems.DateUnreadable, problem);
    }

    [Theory]
    [InlineData("8:03", 8, 3, 0)]
    [InlineData("08:03:15", 8, 3, 15)]
    [InlineData("8h03", 8, 3, 0)]
    [InlineData("8:03 PM", 20, 3, 0)]
    [InlineData("12:10 AM", 0, 10, 0)]
    [InlineData("12:10 PM", 12, 10, 0)]
    [InlineData("٠٨:٠٣ م", 20, 3, 0)]
    [InlineData("23:59:59", 23, 59, 59)]
    public void A_text_time_is_read_in_the_usual_spellings(string text, int h, int mi, int s)
    {
        var time = PunchSheet.ReadTime(new SheetCell(text, null), out var problem);
        Assert.Null(problem);
        Assert.Equal(new TimeOnly(h, mi, s), time);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("8:60")]
    [InlineData("13:00 PM")]
    [InlineData("8")]
    [InlineData("matin")]
    public void A_text_time_that_is_not_one_is_refused(string text)
    {
        Assert.Null(PunchSheet.ReadTime(new SheetCell(text, null), out var problem));
        Assert.Equal(PunchProblems.TimeUnreadable, problem);
    }

    [Fact]
    public void Every_row_below_the_header_comes_back_and_a_refused_one_says_why()
    {
        var sheet = Sheet(
            new Row(1, ("A", "No."), ("B", "Nom"), ("C", "Date"), ("D", "Heure"), ("E", "Etat")),
            new Row(2, ("A", 7), ("B", "Ali"), ("C", "01/09/2026"), ("D", "08:00"), ("E", "C/In")),
            new Row(3, ("B", "Sans numéro"), ("C", "01/09/2026"), ("D", "08:00")),
            new Row(4, ("A", 8), ("C", "01/09/2026")),
            new Row(5, ("A", 8), ("C", "le 1er"), ("D", "08:00")),
            new Row(6, ("A", 8), ("D", "08:00")),
            new Row(7, ("F", "une note hors des colonnes choisies")),
            new Row(8, ("A", new string('9', 21)), ("C", "01/09/2026"), ("D", "08:00")));

        var punches = PunchSheet.Read(sheet, 1, Split with { Marker = 5 });

        Assert.Equal([2, 3, 4, 5, 6, 8], punches.Select(p => p.Row));
        Assert.Null(punches[0].Problem);
        Assert.True(punches[0].TextDate);
        // The marker is carried as written; nothing here says what "C/In" means.
        Assert.Equal("C/In", punches[0].Marker);
        Assert.Equal(PunchProblems.NumberMissing, punches[1].Problem);
        Assert.Equal(PunchProblems.TimeMissing, punches[2].Problem);
        Assert.Equal(PunchProblems.DateUnreadable, punches[3].Problem);
        Assert.Equal("le 1er 08:00", punches[3].Source);
        Assert.Equal(PunchProblems.DateMissing, punches[4].Problem);
        Assert.Equal(PunchProblems.NumberTooLong, punches[5].Problem);
        Assert.All(punches.Skip(1), p => Assert.Null(p.At));
    }

    [Fact]
    public void Nothing_is_read_while_the_mapping_is_incomplete()
    {
        var sheet = Sheet(new Row(1, ("A", 7), ("C", "01/09/2026"), ("D", "08:00")));
        Assert.Empty(PunchSheet.Read(sheet, null, Split with { Number = null }));
    }

    private static readonly PunchMapping Split2 = new(Number: 1, Name: 2, Date: 3, Time: 4, DateTime: null, Marker: null, TimeOut: 5);

    [Fact]
    public void When_time_out_is_mapped_a_row_with_both_times_yields_entry_and_exit_punches()
    {
        var sheet = Sheet(new Row(2, ("A", 7), ("B", "Ali"), ("C", Serial(2026, 9, 1)), ("D", 8.0 / 24), ("E", 17.0 / 24)));
        var punches = PunchSheet.Read(sheet, null, Split2);

        Assert.Equal(2, punches.Count);
        Assert.Equal("7", punches[0].Number);
        Assert.Equal("Ali", punches[0].Name);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 0, 0), punches[0].At);
        Assert.False(punches[0].Exit);
        Assert.Equal(2, punches[0].Row);

        Assert.Equal("7", punches[1].Number);
        Assert.Equal("Ali", punches[1].Name);
        Assert.Equal(new DateTime(2026, 9, 1, 17, 0, 0), punches[1].At);
        Assert.True(punches[1].Exit);
        Assert.Equal(2, punches[1].Row);
    }

    [Fact]
    public void When_time_out_is_mapped_but_exit_cell_is_empty_only_entry_is_returned()
    {
        var sheet = Sheet(new Row(2, ("A", 7), ("B", "Ali"), ("C", Serial(2026, 9, 1)), ("D", 8.0 / 24), ("E", "")));
        var punches = PunchSheet.Read(sheet, null, Split2);

        var punch = Assert.Single(punches);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 0, 0), punch.At);
        Assert.False(punch.Exit);
    }

    [Fact]
    public void When_entry_time_is_empty_and_exit_time_is_present_only_exit_is_returned()
    {
        var sheet = Sheet(new Row(2, ("A", 7), ("B", "Ali"), ("C", Serial(2026, 9, 1)), ("D", ""), ("E", 17.0 / 24)));
        var punches = PunchSheet.Read(sheet, null, Split2);

        var punch = Assert.Single(punches);
        Assert.Equal(new DateTime(2026, 9, 1, 17, 0, 0), punch.At);
        Assert.True(punch.Exit);
        Assert.Null(punch.Problem);
    }

    [Fact]
    public void Night_shift_adds_one_day_to_exit_time_when_exit_is_earlier_than_entry()
    {
        var sheet = Sheet(new Row(2, ("A", 7), ("B", "Ali"), ("C", Serial(2026, 9, 1)), ("D", 22.0 / 24), ("E", 6.0 / 24)));
        var punches = PunchSheet.Read(sheet, null, Split2);

        Assert.Equal(2, punches.Count);
        Assert.Equal(new DateTime(2026, 9, 1, 22, 0, 0), punches[0].At);
        Assert.False(punches[0].Exit);
        Assert.Equal(new DateTime(2026, 9, 2, 6, 0, 0), punches[1].At);
        Assert.True(punches[1].Exit);
    }

    [Fact]
    public void Exit_text_formats_and_unreadable_exit_text()
    {
        var sheet = Sheet(
            new Row(2, ("A", 7), ("B", "Ali"), ("C", "01/09/2026"), ("D", "08:00"), ("E", "17h30")),
            new Row(3, ("A", 7), ("B", "Ali"), ("C", "01/09/2026"), ("D", "08:00"), ("E", "5:30 PM")),
            new Row(4, ("A", 7), ("B", "Ali"), ("C", "01/09/2026"), ("D", "08:00"), ("E", "abc")));
        var punches = PunchSheet.Read(sheet, null, Split2);

        Assert.Equal(6, punches.Count);
        Assert.Equal(new DateTime(2026, 9, 1, 17, 30, 0), punches[1].At);
        Assert.Equal(new DateTime(2026, 9, 1, 17, 30, 0), punches[3].At);

        Assert.Null(punches[4].Problem);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 0, 0), punches[4].At);
        Assert.Equal(PunchProblems.TimeUnreadable, punches[5].Problem);
        Assert.Null(punches[5].At);
        Assert.True(punches[5].Exit);
    }

    [Fact]
    public void Combined_mapping_with_time_out_yields_entry_and_exit()
    {
        var mapping = new PunchMapping(Number: 1, Name: null, Date: null, Time: null, DateTime: 3, Marker: null, TimeOut: 4);
        var sheet = Sheet(new Row(1, ("A", 7), ("C", Serial(2026, 9, 1, 8, 0)), ("D", 17.0 / 24)));
        var punches = PunchSheet.Read(sheet, null, mapping);

        Assert.Equal(2, punches.Count);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 0, 0), punches[0].At);
        Assert.False(punches[0].Exit);
        Assert.Equal(new DateTime(2026, 9, 1, 17, 0, 0), punches[1].At);
        Assert.True(punches[1].Exit);
    }

    [Fact]
    public void MappingProblem_validates_time_out_uniqueness()
    {
        var same = Split2 with { TimeOut = 4 };
        Assert.Equal(PunchProblems.MapSameColumn, PunchSheet.MappingProblem(same));
        Assert.Null(PunchSheet.MappingProblem(Split2));
    }

    [Fact]
    public void With_time_out_null_file_yields_one_punch_per_row_with_exit_false()
    {
        var sheet = Sheet(
            new Row(1, ("A", 7), ("B", "Ali"), ("C", Serial(2026, 9, 1)), ("D", 8.0 / 24)),
            new Row(2, ("A", 8), ("B", "Omar"), ("C", Serial(2026, 9, 1)), ("D", 9.0 / 24)));
        var punches = PunchSheet.Read(sheet, null, Split);

        Assert.Equal(2, punches.Count);
        Assert.All(punches, p => Assert.False(p.Exit));
    }

    [Fact]
    public void Exit_text_as_time_or_full_datetime()
    {
        var sheet = Sheet(
            new Row(1, ("A", 7), ("B", "Ali"), ("C", "01/09/2026"), ("D", "08:00"), ("E", "5:30 PM")),
            new Row(2, ("A", 7), ("B", "Ali"), ("C", "01/09/2026"), ("D", "08:00"), ("E", "2026-09-02 06:15")));
        var punches = PunchSheet.Read(sheet, null, Split2);

        Assert.Equal(4, punches.Count);
        Assert.Equal(new DateTime(2026, 9, 1, 17, 30, 0), punches[1].At);
        Assert.True(punches[1].Exit);

        Assert.Equal(new DateTime(2026, 9, 2, 6, 15, 0), punches[3].At);
        Assert.True(punches[3].Exit);
    }

    private static Worksheet RealDeviceSheet() => Sheet(
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
            ("E", "Mardi"), ("F", "08:10"), ("G", ""), ("H", ""))
    );

    [Fact]
    public void Real_device_export_identifies_header_row_and_guesses_mapping()
    {
        var sheet = RealDeviceSheet();
        Assert.Equal(2, PunchSheet.GuessHeaderRow(sheet));

        var mapping = PunchSheet.GuessMapping(sheet, 2);
        Assert.NotNull(mapping);
        Assert.Equal(1, mapping.Number);
        Assert.Equal(2, mapping.Name);
        Assert.Equal(4, mapping.Date);
        Assert.Equal(6, mapping.Time);
        Assert.Equal(7, mapping.TimeOut);
        Assert.Null(mapping.DateTime);
        Assert.Null(mapping.Marker);
    }

    [Fact]
    public void Read_with_guessed_mapping_handles_flags_and_night_entries()
    {
        var sheet = RealDeviceSheet();
        var mapping = PunchSheet.GuessMapping(sheet, 2)!;

        var punches = PunchSheet.Read(sheet, 2, mapping);

        // 09-01: entry 07:54 + exit 17:03, no flags
        var p0 = punches[0];
        Assert.Equal("184", p0.Number);
        Assert.Equal(new DateTime(2026, 9, 1, 7, 54, 0), p0.At);
        Assert.False(p0.Exit);
        Assert.Null(p0.Flags);

        var p1 = punches[1];
        Assert.Equal("184", p1.Number);
        Assert.Equal(new DateTime(2026, 9, 1, 17, 3, 0), p1.At);
        Assert.True(p1.Exit);
        Assert.Null(p1.Flags);

        // 09-09: ONE punch 15:26 with flags [missingOut]
        var p2 = punches[2];
        Assert.Equal("184", p2.Number);
        Assert.Equal(new DateTime(2026, 9, 9, 15, 26, 0), p2.At);
        Assert.False(p2.Exit);
        Assert.Equal([PunchFlags.MissingOut], p2.Flags);

        // 09-07: entry 00:31 flagged [earlyEntry] + exit 17:04 unflagged
        var p3 = punches[3];
        Assert.Equal("184", p3.Number);
        Assert.Equal(new DateTime(2026, 9, 7, 0, 31, 0), p3.At);
        Assert.False(p3.Exit);
        Assert.Equal([PunchFlags.EarlyEntry], p3.Flags);

        var p4 = punches[4];
        Assert.Equal("184", p4.Number);
        Assert.Equal(new DateTime(2026, 9, 7, 17, 4, 0), p4.At);
        Assert.True(p4.Exit);
        Assert.Null(p4.Flags);

        // 185: one entry 08:10 with [missingOut]
        var p5 = punches[5];
        Assert.Equal("185", p5.Number);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 10, 0), p5.At);
        Assert.False(p5.Exit);
        Assert.Equal([PunchFlags.MissingOut], p5.Flags);

        Assert.Equal(6, punches.Count);

        // With earlyHour: 0 the 00:31 entry has no flag.
        var punchesNoEarly = PunchSheet.Read(sheet, 2, mapping, earlyHour: 0);
        var p3NoEarly = punchesNoEarly[3];
        Assert.Null(p3NoEarly.Flags);
    }

    [Fact]
    public void Header_synonyms_and_mojibake_are_properly_recognised()
    {
        var arSheet = Sheet(new Row(1,
            ("A", "رقم الموظف"), ("B", "الاسم"), ("C", "التاريخ"), ("D", "وقت الدخول"), ("E", "وقت الخروج")));
        var arMapping = PunchSheet.GuessMapping(arSheet, 1);
        Assert.Equal(new PunchMapping(1, 2, 3, 4, null, null, TimeOut: 5), arMapping);

        var enSheet = Sheet(new Row(1,
            ("A", "Employee ID"), ("B", "Name"), ("C", "Date"), ("D", "Check In"), ("E", "Check Out")));
        var enMapping = PunchSheet.GuessMapping(enSheet, 1);
        Assert.Equal(new PunchMapping(1, 2, 3, 4, null, null, TimeOut: 5), enMapping);

        var dtSheet = Sheet(new Row(1,
            ("A", "AC-No."), ("B", "Name"), ("C", "Time"), ("D", "State")));
        var dtMapping = PunchSheet.GuessMapping(dtSheet, 1);
        Assert.Equal(new PunchMapping(1, 2, null, null, 3, 4), dtMapping);

        var splitSheet = Sheet(new Row(1,
            ("A", "No"), ("B", "Date"), ("C", "Time")));
        var splitMapping = PunchSheet.GuessMapping(splitSheet, 1);
        Assert.Equal(new PunchMapping(1, null, 2, 3, null, null), splitMapping);

        Assert.Equal(PunchSheet.NormalizeHeader("Numéro d'employé"), PunchSheet.NormalizeHeader("NumÃ©ro d'employÃ©"));
    }
}
