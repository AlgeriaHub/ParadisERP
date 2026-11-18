using System.Globalization;
using System.Text;
using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Domain;

namespace AlgeriaPos.Application.Payroll;

/// <summary>How a text date is ordered. A number Excel stores as a date needs none of this.</summary>
public enum DateOrder { DayMonthYear, MonthDayYear, YearMonthDay }

/// <summary>
/// Which column of the sheet holds what, as the person mapped it (FR-3B). Columns are 1-based
/// (1 = A); null is "not in this file". Either <see cref="Date"/> and <see cref="Time"/>, or one
/// combined <see cref="DateTime"/>. <see cref="Marker"/> is read and shown as it is written —
/// nothing here decides what an "in" or an "out" looks like.
/// TimeOut: the check-out time when the file puts the entry and the exit of a day on one row, in two columns; null otherwise.
/// </summary>
public sealed record PunchMapping(
    int? Number, int? Name, int? Date, int? Time, int? DateTime, int? Marker,
    DateOrder Order = DateOrder.DayMonthYear, int? TimeOut = null);

/// <summary>Things to check on a punch, e.g. missing partner punch, early night entry.</summary>
public static class PunchFlags
{
    public const string MissingOut = "missingOut";
    public const string MissingIn = "missingIn";
    public const string EarlyEntry = "earlyEntry";
}

/// <summary>One data row of the sheet, read through the mapping.</summary>
/// <param name="Row">The row's number in the sheet, as Excel shows it.</param>
/// <param name="Number">The fingerprint number in its one spelling, or null when the cell is empty.</param>
/// <param name="At">The punch as a wall-clock time in the company's zone; null when it could not be read.</param>
/// <param name="Problem">Why the row cannot be used, as a code (<see cref="PunchProblems"/>); null when it can.</param>
/// <param name="Source">What the date and time cells held, as text, so a refused row shows what was refused.</param>
/// <param name="TextDate">The date was written as text, so the chosen <see cref="DateOrder"/> decided it.</param>
/// <param name="Exit">This punch came from the check-out column (<see cref="PunchMapping.TimeOut"/>).</param>
/// <param name="Flags">Things to check on the punch, e.g. missing out, missing in, early entry.</param>
public sealed record ParsedPunch(
    int Row, string? Number, string? Name, DateTime? At, string? Marker, string? Problem, string Source,
    bool TextDate = false, bool Exit = false, IReadOnlyList<string>? Flags = null);

/// <summary>Why a row, or the mapping itself, cannot be read. Codes; the screen words them.</summary>
public static class PunchProblems
{
    // ---------- the mapping ----------
    public const string MapNumber = "mapNumber";
    public const string MapWhen = "mapWhen";
    public const string MapBoth = "mapBoth";
    public const string MapSameColumn = "mapSameColumn";

    // ---------- a row ----------
    public const string NumberMissing = "numberMissing";
    public const string NumberTooLong = "numberTooLong";
    public const string DateMissing = "dateMissing";
    public const string DateUnreadable = "dateUnreadable";
    public const string TimeMissing = "timeMissing";
    public const string TimeUnreadable = "timeUnreadable";
}

/// <summary>
/// The fingerprint terminal's export read as a list of punches — and <b>only</b> that (FR-3B).
///
/// <para>
/// Format-neutral on purpose: no column name, no device and no date layout is assumed. The person
/// says which column is which, the header row is only ever a suggestion, and a text date is read
/// in the order they choose. When the file has a separate check-out column, that column is the
/// departure. What this does not decide, because it waits for one real monthly export: what a
/// repeated punch means, where a working day ends, what a single punch on a day means, and what
/// absence, lateness or overtime are. Nothing here writes attendance.
/// </para>
/// </summary>
public static class PunchSheet
{
    /// <summary>How far down a header is looked for. A title block is a few rows, not a page.</summary>
    public const int HeaderSearchRows = 20;

    /// <summary>
    /// A suggestion for the header row: the first of the top rows with two or more cells, all of
    /// them words rather than numbers, that has a row below it. Null when nothing looks like one;
    /// the person can always choose another, or none.
    /// </summary>
    public static int? GuessHeaderRow(Worksheet sheet)
    {
        var rows = sheet.Rows;
        for (var i = 0; i < rows.Count - 1 && i < HeaderSearchRows; i++)
        {
            var cells = rows[i].Cells.Values.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToList();
            if (cells.Count >= 2 && cells.All(c => c.Number is null && c.Serial is null)) return rows[i].Number;
        }
        return null;
    }

    /// <summary>The mapping's own problem, before any row is read; null when it can be read.</summary>
    public static string? MappingProblem(PunchMapping m)
    {
        if (m.Number is null) return PunchProblems.MapNumber;
        var split = m.Date is not null || m.Time is not null;
        if (split && m.DateTime is not null) return PunchProblems.MapBoth;
        if (m.DateTime is null && (m.Date is null || m.Time is null)) return PunchProblems.MapWhen;
        int?[] used = [m.Number, m.Name, m.Date, m.Time, m.DateTime, m.Marker, m.TimeOut];
        var chosen = used.Where(c => c is not null).Select(c => c!.Value).ToList();
        return chosen.Count != chosen.Distinct().Count() ? PunchProblems.MapSameColumn : null;
    }

    private static readonly HashSet<string> NumberSynonyms =
        NormalizeSynonyms(["Numéro d'employé", "Numéro", "N°", "No", "ID", "Matricule", "Badge", "Employee ID",
            "Employee No", "Employee Number", "User ID", "AC-No", "Enroll ID", "رقم الموظف",
            "رقم البصمة", "الرقم", "رقم"]);

    private static readonly HashSet<string> NameSynonyms =
        NormalizeSynonyms(["Prénom", "Nom", "Nom et prénom", "Nom prénom", "Name", "Employee Name", "Full Name",
            "First Name", "الاسم", "اسم الموظف", "الاسم الكامل"]);

    private static readonly HashSet<string> DateSynonyms =
        NormalizeSynonyms(["Date", "التاريخ", "تاريخ"]);

    private static readonly HashSet<string> TimeInSynonyms =
        NormalizeSynonyms(["Premier pointage", "Première pointage", "Entrée", "Heure d'entrée",
            "Check In", "Clock In", "In", "In Time", "Time In", "First Punch", "First Check In",
            "الدخول", "دخول", "وقت الدخول", "أول بصمة"]);

    private static readonly HashSet<string> TimeOutSynonyms =
        NormalizeSynonyms(["Dernier pointage", "Sortie", "Heure de sortie", "Check Out",
            "Clock Out", "Out", "Out Time", "Time Out", "Last Punch", "Last Check Out", "الخروج", "خروج",
            "وقت الخروج", "آخر بصمة"]);

    private static readonly HashSet<string> DateTimeSynonyms =
        NormalizeSynonyms(["Date/Heure", "Date et heure", "Date heure", "DateTime", "Date Time",
            "Heure de pointage", "Punch Time", "التاريخ والوقت"]);

    private static readonly HashSet<string> MarkerSynonyms =
        NormalizeSynonyms(["État", "Statut", "State", "Status", "Type", "Check Type", "Mode", "In/Out",
            "Entrée/Sortie", "الحالة", "النوع", "دخول/خروج"]);

    private static readonly HashSet<string> AmbiguousTimeSynonyms =
        NormalizeSynonyms(["Time", "Heure", "الوقت", "وقت"]);

    private static HashSet<string> NormalizeSynonyms(string[] words) =>
        words.Select(NormalizeHeader).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>Normalise a column header to letters, digits and spaces.</summary>
    public static string NormalizeHeader(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var s = text;
        if (s.Contains('Ã') || s.Contains('Â'))
        {
            try
            {
                s = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(s));
            }
            catch
            {
            }
        }

        s = Digits(s);

        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }
        s = sb.ToString();

        sb.Clear();
        foreach (var ch in s)
        {
            if (ch is 'أ' or 'إ' or 'آ') sb.Append('ا');
            else if (ch == 'ـ') { }
            else sb.Append(ch);
        }
        s = sb.ToString().ToLowerInvariant();

        sb.Clear();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else sb.Append(' ');
        }

        var words = sb.ToString().Split([' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words);
    }

    /// <summary>
    /// Suggest column roles by matching headers against known synonyms. Null when headerRow is null
    /// or has no cells.
    /// </summary>
    public static PunchMapping? GuessMapping(Worksheet sheet, int? headerRow)
    {
        if (headerRow is null) return null;
        var row = sheet.Rows.FirstOrDefault(r => r.Number == headerRow.Value);
        if (row is null || row.Cells.Count == 0) return null;

        int? number = null;
        int? name = null;
        int? date = null;
        int? time = null;
        int? timeOut = null;
        int? dateTime = null;
        int? marker = null;
        int? ambiguous = null;

        foreach (var (col, cell) in row.Cells.OrderBy(c => c.Key))
        {
            var norm = NormalizeHeader(cell.Text);
            if (string.IsNullOrEmpty(norm)) continue;

            if (number is null && NumberSynonyms.Contains(norm)) number = col;
            else if (name is null && NameSynonyms.Contains(norm)) name = col;
            else if (date is null && DateSynonyms.Contains(norm)) date = col;
            else if (time is null && TimeInSynonyms.Contains(norm)) time = col;
            else if (timeOut is null && TimeOutSynonyms.Contains(norm)) timeOut = col;
            else if (dateTime is null && DateTimeSynonyms.Contains(norm)) dateTime = col;
            else if (marker is null && MarkerSynonyms.Contains(norm)) marker = col;
            else if (ambiguous is null && AmbiguousTimeSynonyms.Contains(norm)) ambiguous = col;
        }

        if (ambiguous is not null)
        {
            if (date is not null)
            {
                if (time is null) time = ambiguous;
            }
            else
            {
                if (dateTime is null) dateTime = ambiguous;
            }
        }

        if (date is not null && dateTime is not null)
        {
            dateTime = null;
        }
        else if (dateTime is not null && time is not null && date is null)
        {
            time = null;
        }

        return new PunchMapping(number, name, date, time, dateTime, marker, TimeOut: timeOut);
    }

    public const int DefaultEarlyHour = 4;

    /// <summary>
    /// Every data row below the header, read through the mapping. A row whose mapped cells are
    /// all empty is a spacer and is left out; every other row comes back, readable or not.
    /// </summary>
    public static List<ParsedPunch> Read(Worksheet sheet, int? headerRow, PunchMapping m, int earlyHour = DefaultEarlyHour)
    {
        if (MappingProblem(m) is not null) return [];

        int[] mapped = new[] { m.Number, m.Name, m.Date, m.Time, m.DateTime, m.Marker, m.TimeOut }
            .Where(c => c is not null).Select(c => c!.Value).ToArray();
        var list = new List<ParsedPunch>();
        foreach (var row in sheet.Rows)
        {
            if (headerRow is int header && row.Number <= header) continue;
            if (mapped.All(c => string.IsNullOrWhiteSpace(row[c]?.Text))) continue;

            if (m.TimeOut is null)
            {
                list.Add(ReadRow(row, m));
            }
            else
            {
                var exitCell = row[m.TimeOut.Value];
                var exitEmpty = string.IsNullOrWhiteSpace(exitCell?.Text);
                var entryEmpty = m.DateTime is null && string.IsNullOrWhiteSpace(row[m.Time!.Value]?.Text);

                if (exitEmpty)
                {
                    var entry = ReadRow(row, m);
                    var flags = new List<string> { PunchFlags.MissingOut };
                    if (earlyHour > 0 && entry.At is not null && entry.At.Value.TimeOfDay < TimeSpan.FromHours(earlyHour))
                    {
                        flags.Add(PunchFlags.EarlyEntry);
                    }
                    list.Add(entry with { Flags = flags });
                }
                else if (entryEmpty)
                {
                    var exit = ReadExit(row, m, null);
                    var flags = new List<string> { PunchFlags.MissingIn };
                    list.Add(exit with { Flags = flags });
                }
                else
                {
                    var entry = ReadRow(row, m);
                    var exit = ReadExit(row, m, entry.At);

                    if (entry.At is not null && exit.At is not null && entry.At.Value == exit.At.Value)
                    {
                        var flags = new List<string> { PunchFlags.MissingOut };
                        if (earlyHour > 0 && entry.At.Value.TimeOfDay < TimeSpan.FromHours(earlyHour))
                        {
                            flags.Add(PunchFlags.EarlyEntry);
                        }
                        list.Add(entry with { Flags = flags });
                    }
                    else
                    {
                        if (earlyHour > 0 && entry.At is not null && entry.At.Value.TimeOfDay < TimeSpan.FromHours(earlyHour))
                        {
                            entry = entry with { Flags = [PunchFlags.EarlyEntry] };
                        }
                        list.Add(entry);
                        list.Add(exit);
                    }
                }
            }
        }
        return list;
    }

    private static ParsedPunch ReadExit(SheetRow row, PunchMapping m, DateTime? entryAt)
    {
        var rawNumber = row[m.Number!.Value]?.Text;
        var number = FingerprintNumbers.Normalize(rawNumber);
        var name = Clean(m.Name is int n ? row[n]?.Text : null);
        var marker = Clean(m.Marker is int k ? row[k]?.Text : null);

        DateTime? at = null;
        string? problem = null;
        string source;
        bool textDate = false;

        var exitCell = row[m.TimeOut!.Value];
        DateOnly? date = null;
        
        if (m.DateTime is int both)
        {
            var whenCell = row[both];
            source = $"{whenCell?.Text?.Trim()} {exitCell?.Text?.Trim()}".Trim();
            textDate = whenCell is { Serial: null } && !string.IsNullOrWhiteSpace(whenCell.Text);
            var (dt, p) = ReadDateTime(whenCell, m.Order);
            if (dt is DateTime d) date = DateOnly.FromDateTime(d);
            else problem = p;
        }
        else
        {
            var whenCell = row[m.Date!.Value];
            source = $"{whenCell?.Text?.Trim()} {exitCell?.Text?.Trim()}".Trim();
            textDate = whenCell is { Serial: null } && !string.IsNullOrWhiteSpace(whenCell.Text);
            date = ReadDate(whenCell, m.Order, out problem);
        }

        bool isExitDateTime = (exitCell?.Serial is double s && Math.Floor(s) >= 1) ||
            (exitCell?.Text is string exitText &&
             Digits(exitText).Split([' ', 'T'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is string firstToken &&
             (firstToken.Contains('/') || firstToken.Contains('-')));

        if (isExitDateTime)
        {
            var (exitDt, exitProb) = ReadDateTime(exitCell, m.Order);
            problem = exitProb;
            at = exitDt;
        }
        else
        {
            var time = ReadTime(exitCell, out var timeProb);
            if (timeProb is not null) problem = timeProb;
            else if (date is DateOnly d && time is TimeOnly t)
            {
                at = d.ToDateTime(t);
                if (entryAt is DateTime ea && at < ea)
                {
                    at = at.Value.AddDays(1);
                }
            }
        }

        if (number is null) problem = PunchProblems.NumberMissing;
        else if (number.Length > FingerprintNumbers.MaxLength) problem = PunchProblems.NumberTooLong;

        return new ParsedPunch(row.Number, number, name, problem is null ? at : null, marker, problem, source, textDate, true);
    }

    private static ParsedPunch ReadRow(SheetRow row, PunchMapping m)
    {
        var rawNumber = row[m.Number!.Value]?.Text;
        var number = FingerprintNumbers.Normalize(rawNumber);
        var name = Clean(m.Name is int n ? row[n]?.Text : null);
        var marker = Clean(m.Marker is int k ? row[k]?.Text : null);

        DateTime? at;
        string? problem;
        string source;
        if (m.DateTime is int both)
        {
            var cell = row[both];
            source = cell?.Text?.Trim() ?? "";
            (at, problem) = ReadDateTime(cell, m.Order);
        }
        else
        {
            var dateCell = row[m.Date!.Value];
            var timeCell = row[m.Time!.Value];
            source = $"{dateCell?.Text?.Trim()} {timeCell?.Text?.Trim()}".Trim();
            var date = ReadDate(dateCell, m.Order, out problem);
            var time = problem is null ? ReadTime(timeCell, out problem) : null;
            at = date is DateOnly d && time is TimeOnly t ? d.ToDateTime(t) : null;
        }

        // The number is said first: a row nobody can be named for is the reason that matters.
        if (number is null) problem = PunchProblems.NumberMissing;
        else if (number.Length > FingerprintNumbers.MaxLength) problem = PunchProblems.NumberTooLong;

        var whenCell = row[(m.DateTime ?? m.Date)!.Value];
        var textDate = whenCell is { Serial: null } && !string.IsNullOrWhiteSpace(whenCell.Text);
        return new ParsedPunch(row.Number, number, name, problem is null ? at : null, marker, problem, source, textDate);
    }

    // ---------- values ----------

    /// <summary>A date: an Excel date number (any time of day in it is dropped), or text in the chosen order.</summary>
    public static DateOnly? ReadDate(SheetCell? cell, DateOrder order, out string? problem)
    {
        problem = null;
        if (cell is null || string.IsNullOrWhiteSpace(cell.Text))
        {
            problem = PunchProblems.DateMissing;
            return null;
        }
        if (cell.Serial is double serial)
        {
            var day = FromSerial(Math.Floor(serial));
            if (day is null) problem = PunchProblems.DateUnreadable;
            return day is DateTime d ? DateOnly.FromDateTime(d) : null;
        }
        var text = Digits(cell.Text).Trim();
        var token = text.Split([' ', 'T'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var date = TextDate(token, order);
        if (date is null) problem = PunchProblems.DateUnreadable;
        return date;
    }

    /// <summary>A time of day: Excel's fraction of a day, or text such as 8:03, 08:03:15, 8h03, 8:03 PM.</summary>
    public static TimeOnly? ReadTime(SheetCell? cell, out string? problem)
    {
        problem = null;
        if (cell is null || string.IsNullOrWhiteSpace(cell.Text))
        {
            problem = PunchProblems.TimeMissing;
            return null;
        }
        if (cell.Serial is double serial)
        {
            var time = FromFraction(serial - Math.Floor(serial));
            if (time is null) problem = PunchProblems.TimeUnreadable;
            return time;
        }
        var parsed = TextTime(Digits(cell.Text).Trim());
        if (parsed is null) problem = PunchProblems.TimeUnreadable;
        return parsed;
    }

    /// <summary>One cell holding both: an Excel date-and-time number, or a date then a time in text.</summary>
    public static (DateTime? At, string? Problem) ReadDateTime(SheetCell? cell, DateOrder order)
    {
        if (cell is null || string.IsNullOrWhiteSpace(cell.Text)) return (null, PunchProblems.DateMissing);
        if (cell.Serial is double serial)
        {
            var day = FromSerial(Math.Floor(serial));
            var time = FromFraction(serial - Math.Floor(serial));
            return day is DateTime d && time is TimeOnly t
                ? (DateOnly.FromDateTime(d).ToDateTime(t), null)
                : (null, PunchProblems.DateUnreadable);
        }
        var text = Digits(cell.Text).Trim();
        var parts = text.Split([' ', 'T'], 2, StringSplitOptions.RemoveEmptyEntries);
        var date = TextDate(parts[0], order);
        if (date is null) return (null, PunchProblems.DateUnreadable);
        if (parts.Length < 2) return (null, PunchProblems.TimeMissing);
        var clock = TextTime(parts[1].Trim());
        return clock is TimeOnly c ? (date.Value.ToDateTime(c), null) : (null, PunchProblems.TimeUnreadable);
    }

    /// <summary>
    /// Excel's day number to a date, in the 1900 system every current Excel writes. A number far
    /// outside any payroll month — an employee number mapped as a date, say — is not read as one.
    /// </summary>
    private static DateTime? FromSerial(double serial)
    {
        if (serial < 1 || serial > 2_958_465) return null;
        var date = DateTime.FromOADate(serial);
        return date.Year is >= 1990 and <= 2100 ? date : null;
    }

    /// <summary>A fraction of a day to a time, to the nearest second (Excel stores a double).</summary>
    private static TimeOnly? FromFraction(double fraction)
    {
        var seconds = (int)Math.Round(fraction * 86_400, MidpointRounding.AwayFromZero);
        if (seconds < 0) return null;
        // 23:59:59.6 rounds up to the next midnight; it is the last second of this day.
        return TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(Math.Min(seconds, 86_399)));
    }

    private static DateOnly? TextDate(string token, DateOrder order)
    {
        var parts = token.Split(['/', '-', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts.Any(p => !p.All(char.IsAsciiDigit) || p.Length > 4)) return null;
        var n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();

        // A four-digit first part can only be a year, whatever order was chosen.
        int y, mo, d;
        if (parts[0].Length == 4 || order == DateOrder.YearMonthDay) (y, mo, d) = (n[0], n[1], n[2]);
        else if (order == DateOrder.MonthDayYear) (mo, d, y) = (n[0], n[1], n[2]);
        else (d, mo, y) = (n[0], n[1], n[2]);
        if (y < 100) y += 2000;
        if (y is < 1990 or > 2100 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return null;
        return new DateOnly(y, mo, d);
    }

    private static TimeOnly? TextTime(string text)
    {
        var t = text.Trim();
        int? shift = null;
        string[] am = ["AM", "A.M.", "ص"];
        string[] pm = ["PM", "P.M.", "م"];
        foreach (var s in am)
            if (t.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { shift = 0; t = t[..^s.Length].Trim(); break; }
        if (shift is null)
            foreach (var s in pm)
                if (t.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { shift = 12; t = t[..^s.Length].Trim(); break; }

        var parts = t.Split([':', 'h', 'H'], StringSplitOptions.None);
        if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length is 0 or > 2 || !p.All(char.IsAsciiDigit))) return null;
        var h = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var mi = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var se = parts.Length == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
        if (shift is int add)
        {
            if (h is < 1 or > 12) return null;
            h = h % 12 + add;
        }
        if (h > 23 || mi > 59 || se > 59) return null;
        return new TimeOnly(h, mi, se);
    }

    /// <summary>Arabic-Indic and Persian digits as 0–9; the rest untouched.</summary>
    private static string Digits(string text) => new(text.Select(ch => ch switch
    {
        >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
        >= '۰' and <= '۹' => (char)('0' + (ch - '۰')),
        _ => ch
    }).ToArray());

    private static string? Clean(string? text)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > 120 ? t[..120] : t;
    }
}
