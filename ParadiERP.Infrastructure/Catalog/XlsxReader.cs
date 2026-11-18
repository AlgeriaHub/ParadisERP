using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ParadiERP.Application.Catalog;

namespace ParadiERP.Infrastructure.Catalog;

/// <summary>
/// Reads one worksheet of an .xlsx workbook into text and numbers, with nothing but the
/// framework's zip and XML readers.
///
/// <para>
/// No spreadsheet package is taken on for this. What a stock sheet needs is the cell grid — no
/// styles, no formulas evaluated (Excel stores each formula's last result beside it, and that
/// result is what the person saw), no dates — and the part of the format that carries it is
/// three XML files in a zip. A package would add a dependency to every deployment for one
/// import screen.
/// </para>
/// <para>
/// The file is the user's, so it is read as untrusted: DTDs are refused (no entity expansion),
/// each part is read through a cap on its <i>uncompressed</i> size (a zip that inflates to
/// gigabytes stops at <see cref="MaxPartBytes"/>), and the row count is capped. Old binary .xls
/// files are not zips at all and are refused with the sentence that says what to do.
/// </para>
/// </summary>
public static class XlsxReader
{
    public const int MaxRows = 10_000;
    public const long MaxPartBytes = 32L * 1024 * 1024;

    public const string NotAWorkbook =
        "This file is not an Excel workbook (.xlsx). Open it in Excel and save it as \"Excel Workbook (*.xlsx)\".";

    private const string RelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string StrictRelationshipsNs = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    /// <param name="sheetName">The sheet to read; null or unknown reads the first visible one.</param>
    /// <param name="maxRows">
    /// The cap on rows with something in them. A stock sheet stays under <see cref="MaxRows"/>; a
    /// month of a terminal's punches is one row a punch and asks for more.
    /// </param>
    public static Worksheet Read(Stream stream, string? sheetName = null, int maxRows = MaxRows)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new SpreadsheetException(NotAWorkbook);
        }

        using (zip)
        {
            try
            {
                var sheets = Sheets(zip);
                if (sheets.Count == 0) throw new SpreadsheetException("The workbook has no worksheet.");
                var chosen = sheets.FirstOrDefault(s => s.Name == sheetName)
                             ?? sheets.FirstOrDefault(s => !s.Hidden)
                             ?? sheets[0];
                var part = zip.GetEntry(chosen.Path)
                           ?? throw new SpreadsheetException($"The sheet \"{chosen.Name}\" is missing from the workbook.");
                var rows = Rows(Load(part), SharedStrings(zip), maxRows);
                return new Worksheet(chosen.Name, sheets.Select(s => s.Name).ToList(), rows);
            }
            catch (XmlException)
            {
                throw new SpreadsheetException(NotAWorkbook);
            }
            catch (InvalidDataException)
            {
                throw new SpreadsheetException(NotAWorkbook);
            }
            catch (NotSupportedException)
            {
                // A part compressed with a method the framework does not read.
                throw new SpreadsheetException(NotAWorkbook);
            }
        }
    }

    private sealed record SheetEntry(string Name, string Path, bool Hidden);

    /// <summary>The sheets in workbook order, each resolved to its part through the relationships.</summary>
    private static List<SheetEntry> Sheets(ZipArchive zip)
    {
        var workbook = zip.GetEntry("xl/workbook.xml");
        if (workbook is null)
            throw new SpreadsheetException(NotAWorkbook);

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
            foreach (var r in Load(rels).Descendants().Where(e => e.Name.LocalName == "Relationship"))
                if ((string?)r.Attribute("Id") is { } id && (string?)r.Attribute("Target") is { } target)
                    targets[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

        var list = new List<SheetEntry>();
        var index = 0;
        foreach (var sheet in Load(workbook).Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            index++;
            var id = (string?)sheet.Attribute(XName.Get("id", RelationshipsNs))
                     ?? (string?)sheet.Attribute(XName.Get("id", StrictRelationshipsNs));
            var path = id is not null && targets.TryGetValue(id, out var t) ? t : $"xl/worksheets/sheet{index}.xml";
            var state = (string?)sheet.Attribute("state");
            list.Add(new SheetEntry((string?)sheet.Attribute("name") ?? $"Sheet{index}", path,
                state is "hidden" or "veryHidden"));
        }
        return list;
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        if (zip.GetEntry("xl/sharedStrings.xml") is not { } part) return [];
        return Load(part).Root!.Elements().Where(e => e.Name.LocalName == "si").Select(Text).ToList();
    }

    private static List<SheetRow> Rows(XDocument sheet, List<string> shared, int maxRows)
    {
        var data = sheet.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        if (data is null) return [];

        var rows = new List<SheetRow>();
        var rowNumber = 0;
        foreach (var row in data.Elements().Where(e => e.Name.LocalName == "row"))
        {
            // The reference is optional in the format; without it a row follows the previous one.
            rowNumber = int.TryParse((string?)row.Attribute("r"), out var r) ? r : rowNumber + 1;
            var cells = new Dictionary<int, SheetCell>();
            var column = 0;
            foreach (var c in row.Elements().Where(e => e.Name.LocalName == "c"))
            {
                var at = (string?)c.Attribute("r") is { } reference ? SheetColumns.Of(reference) : 0;
                column = at > 0 ? at : column + 1;
                if (Cell(c, shared) is { } cell) cells[column] = cell;
            }
            if (cells.Count == 0) continue;
            if (rows.Count == maxRows)
                throw new SpreadsheetException($"The sheet has more than {maxRows:N0} rows. Split it into smaller sheets.");
            rows.Add(new SheetRow(rowNumber, cells));
        }
        return rows;
    }

    private static SheetCell? Cell(XElement c, List<string> shared)
    {
        var value = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
        switch ((string?)c.Attribute("t"))
        {
            case "s":
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Count
                    ? Typed(shared[i])
                    : null;
            case "inlineStr":
                return c.Elements().FirstOrDefault(e => e.Name.LocalName == "is") is { } inline ? Typed(Text(inline)) : null;
            case "str":
                return value is null ? null : Typed(value);
            case "b":
                return value is null ? null : new SheetCell(value == "1" ? "TRUE" : "FALSE", null);
            case "e":
                return value is null ? null : new SheetCell(value, null);
            default:
                if (string.IsNullOrEmpty(value)) return null;
                return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? new SheetCell(Format(number), Round(number),
                        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var stored) ? stored : null)
                    : new SheetCell(value, null);
        }
    }

    /// <summary>A text cell, with its number when the text is one ("972", "1 452,5", "١٤").</summary>
    private static SheetCell? Typed(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return new SheetCell(text, LooseNumber(text));
    }

    private static decimal? LooseNumber(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if (ch is >= '٠' and <= '٩') builder.Append((char)('0' + (ch - '٠')));
            else if (ch is >= '۰' and <= '۹') builder.Append((char)('0' + (ch - '۰')));
            else if (ch is '٫' or ',') builder.Append('.');
            else if (ch is ' ' or ' ' or ' ' or '٬') continue; // digit grouping
            else builder.Append(ch);
        }
        var candidate = builder.ToString();
        return candidate.Count(ch => ch == '.') <= 1
               && decimal.TryParse(candidate, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                   CultureInfo.InvariantCulture, out var n)
            ? Round(n)
            : null;
    }

    /// <summary>
    /// Excel stores a double, so a typed 0.3 can arrive as 0.30000000000000004. Quantities are
    /// kept to three places in the database, which is where the noise is cut.
    /// </summary>
    private static decimal Round(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static string Format(decimal value) => Round(value).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A shared or inline string's text: every run's <c>t</c>, leaving out the phonetic guide.</summary>
    private static string Text(XElement holder) => string.Concat(
        holder.Descendants()
            .Where(e => e.Name.LocalName == "t" && e.Ancestors().All(a => a.Name.LocalName != "rPh"))
            .Select(e => e.Value));

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var raw = entry.Open();
        using var capped = new CappedStream(raw, MaxPartBytes);
        using var reader = XmlReader.Create(capped, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
        });
        return XDocument.Load(reader);
    }

    /// <summary>Stops reading a part once it inflates past the cap, whatever its header claimed.</summary>
    private sealed class CappedStream(Stream inner, long cap) : Stream
    {
        private long read;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            read += n;
            if (read > cap)
                throw new SpreadsheetException("The workbook is too large to import. Keep only the sheet with the stock on it.");
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
