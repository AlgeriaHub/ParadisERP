using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace AlgeriaPos.Infrastructure.Reports;

public enum XlsxKind { Text, Number, Date }

/// <param name="Header">The column header label.</param>
/// <param name="Kind">The semantic column kind.</param>
/// <param name="Decimals">For Number: places shown (0 = integer format "#,##0", 2 = "#,##0.00", 3 = "#,##0.000"; any 0..6).</param>
/// <param name="Width">Column width in Excel characters; 0 = automatic from the longest value (clamped 8..60).</param>
public sealed record XlsxColumn(string Header, XlsxKind Kind = XlsxKind.Text, int Decimals = 0, double Width = 0);

public sealed class XlsxSheet
{
    public required string Name { get; init; }                 // sanitised: []:*?/\ removed, cut to 31 chars, empty -> "Sheet1"
    public bool RightToLeft { get; init; }                      // <sheetView rightToLeft="1"> for Arabic
    public IReadOnlyList<string> Title { get; init; } = [];     // optional lines above the table (report name, filters, "as of"), first one bold
    public required IReadOnlyList<XlsxColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public IReadOnlyList<object?>? Totals { get; init; }        // optional last row, bold, with a top border
}

/// <summary>
/// Writes one or more worksheets of an .xlsx workbook into OpenXML with nothing but the
/// framework's zip and XML facilities.
///
/// <para>
/// No spreadsheet package is taken on for this. Report exports need clean tabular sheets —
/// styled headers, frozen panes, autofilters, numbers, dates, and totals — and the part of the
/// format that carries them is OpenXML written directly with System.IO.Compression.ZipArchive
/// and UTF-8 XML strings. A package would add a heavy dependency to every deployment for reports
/// that the framework can write in milliseconds.
/// </para>
/// <para>
/// Cell text is written strictly as inline strings and never as formulas (<c>&lt;f&gt;</c>),
/// even when text starts with '=', '+', '-', or '@', protecting against spreadsheet injection.
/// </para>
/// </summary>
public static class XlsxWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const int StyleGeneral = 0;
    private const int StyleTitleBold = 1;
    private const int StyleHeader = 2;
    private const int StyleNumberBase = 3;       // 3..9 for decimals 0..6
    private const int StyleDate = 10;
    private const int StyleDateTime = 11;
    private const int StyleTotalGeneral = 12;
    private const int StyleTotalNumberBase = 13;  // 13..19 for decimals 0..6
    private const int StyleTotalDate = 20;
    private const int StyleTotalDateTime = 21;

    private static readonly DateTime ExcelEpoch = new(1899, 12, 30);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private const string RelsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\n" +
        "  <Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>\n" +
        "</Relationships>";

    private const string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\n" +
        "  <numFmts count=\"9\">\n" +
        "    <numFmt numFmtId=\"164\" formatCode=\"#,##0\"/>\n" +
        "    <numFmt numFmtId=\"165\" formatCode=\"#,##0.0\"/>\n" +
        "    <numFmt numFmtId=\"166\" formatCode=\"#,##0.00\"/>\n" +
        "    <numFmt numFmtId=\"167\" formatCode=\"#,##0.000\"/>\n" +
        "    <numFmt numFmtId=\"168\" formatCode=\"#,##0.0000\"/>\n" +
        "    <numFmt numFmtId=\"169\" formatCode=\"#,##0.00000\"/>\n" +
        "    <numFmt numFmtId=\"170\" formatCode=\"#,##0.000000\"/>\n" +
        "    <numFmt numFmtId=\"171\" formatCode=\"yyyy-mm-dd\"/>\n" +
        "    <numFmt numFmtId=\"172\" formatCode=\"yyyy-mm-dd hh:mm\"/>\n" +
        "  </numFmts>\n" +
        "  <fonts count=\"2\">\n" +
        "    <font><sz val=\"11\"/><name val=\"Calibri\"/></font>\n" +
        "    <font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>\n" +
        "  </fonts>\n" +
        "  <fills count=\"3\">\n" +
        "    <fill><patternFill patternType=\"none\"/></fill>\n" +
        "    <fill><patternFill patternType=\"gray125\"/></fill>\n" +
        "    <fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/><bgColor indexed=\"64\"/></patternFill></fill>\n" +
        "  </fills>\n" +
        "  <borders count=\"3\">\n" +
        "    <border><left/><right/><top/><bottom/><diagonal/></border>\n" +
        "    <border><left/><right/><top/><bottom style=\"thin\"><color auto=\"1\"/></bottom><diagonal/></border>\n" +
        "    <border><left/><right/><top style=\"thin\"><color auto=\"1\"/></top><bottom/><diagonal/></border>\n" +
        "  </borders>\n" +
        "  <cellStyleXfs count=\"1\">\n" +
        "    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>\n" +
        "  </cellStyleXfs>\n" +
        "  <cellXfs count=\"22\">\n" +
        "    <xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>\n" +
        "    <xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>\n" +
        "    <xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>\n" +
        "    <xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"167\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"168\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"169\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"170\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"171\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"172\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\"/>\n" +
        "    <xf numFmtId=\"164\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"165\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"166\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"167\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"168\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"169\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"170\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"171\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"/>\n" +
        "    <xf numFmtId=\"172\" fontId=\"1\" fillId=\"0\" borderId=\"2\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyNumberFormat=\"1\"/>\n" +
        "  </cellXfs>\n" +
        "  <cellStyles count=\"1\">\n" +
        "    <cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/>\n" +
        "  </cellStyles>\n" +
        "</styleSheet>";

    public static byte[] Write(params XlsxSheet[] sheets) => Write((IReadOnlyList<XlsxSheet>)sheets);

    public static byte[] Write(IReadOnlyList<XlsxSheet> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        if (sheets.Count == 0)
            throw new ArgumentException("The workbook must contain at least one sheet.", nameof(sheets));

        var sheetNames = DeduplicateSheetNames(sheets);

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", BuildContentTypesXml(sheets.Count));
            WriteEntry(zip, "_rels/.rels", RelsXml);
            WriteEntry(zip, "xl/workbook.xml", BuildWorkbookXml(sheetNames));
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml(sheets.Count));
            WriteEntry(zip, "xl/styles.xml", StylesXml);

            for (var i = 0; i < sheets.Count; i++)
            {
                var sheetXml = BuildWorksheetXml(sheets[i]);
                WriteEntry(zip, $"xl/worksheets/sheet{i + 1}.xml", sheetXml);
            }
        }

        return memory.ToArray();
    }

    public static string ColumnName(int index1)
    {
        if (index1 <= 0) return string.Empty;
        var letters = string.Empty;
        for (var n = index1; n > 0; n = (n - 1) / 26)
            letters = (char)('A' + (n - 1) % 26) + letters;
        return letters;
    }

    private static string BuildContentTypesXml(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">\n");
        sb.Append("  <Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>\n");
        sb.Append("  <Default Extension=\"xml\" ContentType=\"application/xml\"/>\n");
        sb.Append("  <Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>\n");
        sb.Append("  <Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>\n");
        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append($"  <Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>\n");
        }
        sb.Append("</Types>");
        return sb.ToString();
    }

    private static string BuildWorkbookRelsXml(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">\n");
        for (var i = 1; i <= sheetCount; i++)
        {
            sb.Append($"  <Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>\n");
        }
        sb.Append($"  <Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>\n");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string BuildWorkbookXml(IReadOnlyList<string> sheetNames)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">\n");
        sb.Append("  <sheets>\n");
        for (var i = 0; i < sheetNames.Count; i++)
        {
            var escapedName = EscapeXml(sheetNames[i]);
            sb.Append($"    <sheet name=\"{escapedName}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>\n");
        }
        sb.Append("  </sheets>\n");
        sb.Append("</workbook>");
        return sb.ToString();
    }

    private static string BuildWorksheetXml(XlsxSheet sheet)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">\n");

        var titleCount = sheet.Title.Count;
        var headerRow = titleCount > 0 ? titleCount + 2 : 1;

        sb.Append("  <sheetViews>\n");
        sb.Append("    <sheetView workbookViewId=\"0\"");
        if (sheet.RightToLeft)
            sb.Append(" rightToLeft=\"1\"");
        sb.Append(">\n");
        sb.Append($"      <pane ySplit=\"{headerRow}\" topLeftCell=\"A{headerRow + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/>\n");
        sb.Append("    </sheetView>\n");
        sb.Append("  </sheetViews>\n");

        if (sheet.Columns.Count > 0)
        {
            sb.Append("  <cols>\n");
            for (var c = 0; c < sheet.Columns.Count; c++)
            {
                var col = sheet.Columns[c];
                var colIndex = c + 1;
                double width;
                if (col.Width > 0)
                {
                    width = col.Width;
                }
                else
                {
                    var maxLen = CleanText(col.Header ?? string.Empty).Length;
                    for (var r = 0; r < sheet.Rows.Count; r++)
                    {
                        var row = sheet.Rows[r];
                        if (c < row.Count)
                            maxLen = Math.Max(maxLen, GetDisplayLength(row[c], col));
                    }
                    if (sheet.Totals is not null && c < sheet.Totals.Count)
                    {
                        maxLen = Math.Max(maxLen, GetDisplayLength(sheet.Totals[c], col));
                    }
                    width = Math.Clamp(maxLen + 2, 8, 60);
                }
                sb.Append($"    <col min=\"{colIndex}\" max=\"{colIndex}\" width=\"{width.ToString("0.##", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>\n");
            }
            sb.Append("  </cols>\n");
        }

        sb.Append("  <sheetData>\n");

        if (titleCount > 0)
        {
            for (var t = 0; t < titleCount; t++)
            {
                var rowNum = t + 1;
                var titleLine = sheet.Title[t];
                var cleanTitle = CleanText(titleLine ?? string.Empty);
                if (cleanTitle.Length > 0)
                {
                    var style = t == 0 ? StyleTitleBold : StyleGeneral;
                    var styleAttr = style > 0 ? $" s=\"{style}\"" : "";
                    sb.Append($"    <row r=\"{rowNum}\"><c r=\"A{rowNum}\"{styleAttr} t=\"inlineStr\"><is><t xml:space=\"preserve\">{EscapeCleanedXml(cleanTitle)}</t></is></c></row>\n");
                }
                else
                {
                    sb.Append($"    <row r=\"{rowNum}\"/>\n");
                }
            }
            sb.Append($"    <row r=\"{titleCount + 1}\"/>\n");
        }

        sb.Append($"    <row r=\"{headerRow}\">\n");
        for (var c = 0; c < sheet.Columns.Count; c++)
        {
            var col = sheet.Columns[c];
            var cellRef = $"{ColumnName(c + 1)}{headerRow}";
            var cleanHeader = CleanText(col.Header ?? string.Empty);
            if (cleanHeader.Length > 0)
            {
                sb.Append($"      <c r=\"{cellRef}\" s=\"{StyleHeader}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{EscapeCleanedXml(cleanHeader)}</t></is></c>\n");
            }
            else
            {
                sb.Append($"      <c r=\"{cellRef}\" s=\"{StyleHeader}\"/>\n");
            }
        }
        sb.Append("    </row>\n");

        var currentRow = headerRow + 1;
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var rowNum = currentRow++;
            var rowCells = new StringBuilder();
            for (var c = 0; c < sheet.Columns.Count; c++)
            {
                var val = c < row.Count ? row[c] : null;
                var cellRef = $"{ColumnName(c + 1)}{rowNum}";
                if (FormatCell(cellRef, val, sheet.Columns[c], isTotal: false) is { } cellXml)
                {
                    rowCells.Append(cellXml);
                }
            }
            if (rowCells.Length > 0)
            {
                sb.Append($"    <row r=\"{rowNum}\">{rowCells}</row>\n");
            }
            else
            {
                sb.Append($"    <row r=\"{rowNum}\"/>\n");
            }
        }

        if (sheet.Totals is not null)
        {
            var rowNum = currentRow++;
            var rowCells = new StringBuilder();
            for (var c = 0; c < sheet.Columns.Count; c++)
            {
                var val = c < sheet.Totals.Count ? sheet.Totals[c] : null;
                var cellRef = $"{ColumnName(c + 1)}{rowNum}";
                if (FormatCell(cellRef, val, sheet.Columns[c], isTotal: true) is { } cellXml)
                {
                    rowCells.Append(cellXml);
                }
            }
            if (rowCells.Length > 0)
            {
                sb.Append($"    <row r=\"{rowNum}\">{rowCells}</row>\n");
            }
            else
            {
                sb.Append($"    <row r=\"{rowNum}\"/>\n");
            }
        }

        sb.Append("  </sheetData>\n");

        if (sheet.Rows.Count > 0 && sheet.Columns.Count > 0)
        {
            var lastCol = ColumnName(sheet.Columns.Count);
            var lastDataRow = headerRow + sheet.Rows.Count;
            sb.Append($"  <autoFilter ref=\"A{headerRow}:{lastCol}{lastDataRow}\"/>\n");
        }

        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static string? FormatCell(string cellRef, object? value, XlsxColumn col, bool isTotal)
    {
        if (value is null) return null;

        switch (value)
        {
            case string s:
            {
                if (s.Length == 0) return null;
                var clean = CleanText(s);
                if (clean.Length == 0) return null;
                var style = isTotal ? StyleTotalGeneral : StyleGeneral;
                var styleAttr = style > 0 ? $" s=\"{style}\"" : "";
                return $"<c r=\"{cellRef}\"{styleAttr} t=\"inlineStr\"><is><t xml:space=\"preserve\">{EscapeCleanedXml(clean)}</t></is></c>";
            }

            case decimal m:
            {
                var style = GetNumberStyle(col, isTotal);
                var styleAttr = style > 0 ? $" s=\"{style}\"" : "";
                return $"<c r=\"{cellRef}\"{styleAttr}><v>{m.ToString(CultureInfo.InvariantCulture)}</v></c>";
            }

            case double d:
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) return null;
                var style = GetNumberStyle(col, isTotal);
                var styleAttr = style > 0 ? $" s=\"{style}\"" : "";
                return $"<c r=\"{cellRef}\"{styleAttr}><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>";
            }

            case float f:
            {
                if (float.IsNaN(f) || float.IsInfinity(f)) return null;
                var style = GetNumberStyle(col, isTotal);
                var styleAttr = style > 0 ? $" s=\"{style}\"" : "";
                return $"<c r=\"{cellRef}\"{styleAttr}><v>{f.ToString("R", CultureInfo.InvariantCulture)}</v></c>";
            }

            case int or long or short or byte or sbyte or uint or ulong or ushort:
            {
                var style = GetNumberStyle(col, isTotal);
                var styleAttr = style > 0 ? $" s=\"{style}\"" : "";
                var valStr = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                return $"<c r=\"{cellRef}\"{styleAttr}><v>{valStr}</v></c>";
            }

            case DateOnly dateOnly:
            {
                var dt = dateOnly.ToDateTime(TimeOnly.MinValue);
                var serial = (dt - ExcelEpoch).TotalDays;
                var style = isTotal ? StyleTotalDate : StyleDate;
                return $"<c r=\"{cellRef}\" s=\"{style}\"><v>{serial.ToString("R", CultureInfo.InvariantCulture)}</v></c>";
            }

            case DateTime dt:
            {
                var serial = (dt - ExcelEpoch).TotalDays;
                var isZeroTime = dt.TimeOfDay == TimeSpan.Zero;
                var style = isTotal
                    ? (isZeroTime ? StyleTotalDate : StyleTotalDateTime)
                    : (isZeroTime ? StyleDate : StyleDateTime);
                return $"<c r=\"{cellRef}\" s=\"{style}\"><v>{serial.ToString("R", CultureInfo.InvariantCulture)}</v></c>";
            }

            case DateTimeOffset dto:
            {
                var wallClock = dto.DateTime;
                var serial = (wallClock - ExcelEpoch).TotalDays;
                var isZeroTime = wallClock.TimeOfDay == TimeSpan.Zero;
                var style = isTotal
                    ? (isZeroTime ? StyleTotalDate : StyleTotalDateTime)
                    : (isZeroTime ? StyleDate : StyleDateTime);
                return $"<c r=\"{cellRef}\" s=\"{style}\"><v>{serial.ToString("R", CultureInfo.InvariantCulture)}</v></c>";
            }

            case bool b:
            {
                var style = isTotal ? StyleTotalGeneral : StyleGeneral;
                var styleAttr = style > 0 ? $" s=\"{style}\"" : "";
                return $"<c r=\"{cellRef}\"{styleAttr} t=\"b\"><v>{(b ? "1" : "0")}</v></c>";
            }

            default:
            {
                var str = value.ToString();
                if (string.IsNullOrEmpty(str)) return null;
                var clean = CleanText(str);
                if (clean.Length == 0) return null;
                var style = isTotal ? StyleTotalGeneral : StyleGeneral;
                var styleAttr = style > 0 ? $" s=\"{style}\"" : "";
                return $"<c r=\"{cellRef}\"{styleAttr} t=\"inlineStr\"><is><t xml:space=\"preserve\">{EscapeCleanedXml(clean)}</t></is></c>";
            }
        }
    }

    private static int GetNumberStyle(XlsxColumn col, bool isTotal)
    {
        if (col.Kind == XlsxKind.Number)
        {
            var dec = Math.Clamp(col.Decimals, 0, 6);
            return isTotal ? StyleTotalNumberBase + dec : StyleNumberBase + dec;
        }
        return isTotal ? StyleTotalGeneral : StyleGeneral;
    }

    private static int GetDisplayLength(object? value, XlsxColumn col)
    {
        if (value is null) return 0;
        return value switch
        {
            string s => CleanText(s).Length,
            decimal m => col.Kind == XlsxKind.Number
                ? m.ToString("N" + Math.Clamp(col.Decimals, 0, 6), CultureInfo.InvariantCulture).Length
                : m.ToString(CultureInfo.InvariantCulture).Length,
            double d => double.IsNaN(d) || double.IsInfinity(d) ? 0 : (col.Kind == XlsxKind.Number
                ? d.ToString("N" + Math.Clamp(col.Decimals, 0, 6), CultureInfo.InvariantCulture).Length
                : d.ToString("R", CultureInfo.InvariantCulture).Length),
            float f => float.IsNaN(f) || float.IsInfinity(f) ? 0 : (col.Kind == XlsxKind.Number
                ? f.ToString("N" + Math.Clamp(col.Decimals, 0, 6), CultureInfo.InvariantCulture).Length
                : f.ToString("R", CultureInfo.InvariantCulture).Length),
            int or long or short or byte or sbyte or uint or ulong or ushort => col.Kind == XlsxKind.Number
                ? Convert.ToDecimal(value).ToString("N" + Math.Clamp(col.Decimals, 0, 6), CultureInfo.InvariantCulture).Length
                : value.ToString()!.Length,
            DateOnly => 10,
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? 10 : 16,
            DateTimeOffset dto => dto.DateTime.TimeOfDay == TimeSpan.Zero ? 10 : 16,
            bool => 5,
            _ => CleanText(value.ToString() ?? string.Empty).Length,
        };
    }

    private static List<string> DeduplicateSheetNames(IReadOnlyList<XlsxSheet> sheets)
    {
        var result = new List<string>(sheets.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sheets.Count; i++)
        {
            var baseName = SanitizeSheetName(sheets[i].Name);
            var uniqueName = baseName;
            var count = 2;
            while (used.Contains(uniqueName))
            {
                var suffix = $" ({count})";
                var maxBaseLen = 31 - suffix.Length;
                var trimmedBase = baseName.Length > maxBaseLen ? baseName[..maxBaseLen] : baseName;
                uniqueName = trimmedBase + suffix;
                count++;
            }
            used.Add(uniqueName);
            result.Add(uniqueName);
        }

        return result;
    }

    private static string SanitizeSheetName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "Sheet1";
        var cleaned = CleanText(raw);
        var sb = new StringBuilder(cleaned.Length);
        foreach (var ch in cleaned)
        {
            if (ch is '[' or ']' or ':' or '*' or '?' or '/' or '\\')
                continue;
            sb.Append(ch);
        }
        var result = sb.ToString();
        if (result.Length == 0) return "Sheet1";
        if (result.Length > 31) result = result[..31];
        return result;
    }

    private static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch < 0x20)
            {
                if (ch is '\t' or '\n' or '\r')
                    sb.Append(ch);
                continue;
            }

            if (char.IsSurrogate(ch))
            {
                if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(ch);
                    sb.Append(text[i + 1]);
                    i++;
                }
                continue;
            }

            if (ch is '\uFFFE' or '\uFFFF')
                continue;

            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string EscapeXml(string text) => EscapeCleanedXml(CleanText(text));

    private static string EscapeCleanedXml(string clean)
    {
        if (clean.Length == 0) return string.Empty;
        var sb = new StringBuilder(clean.Length);
        foreach (var ch in clean)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Utf8NoBom.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
