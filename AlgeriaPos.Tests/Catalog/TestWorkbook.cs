using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using AlgeriaPos.Application.Catalog;

namespace AlgeriaPos.Tests.Catalog;

/// <summary>
/// Writes a minimal .xlsx the way Excel lays one out — text in the shared-strings table, numbers
/// in the cell — so the reader is tested against the format rather than against itself.
/// </summary>
internal static class TestWorkbook
{
    /// <summary>A row: its number, then its cells by column letter. A string is text, anything else a number.</summary>
    public sealed record Row(int Number, params (string Column, object Value)[] Cells);

    public sealed record Sheet(string Name, IReadOnlyList<Row> Rows, bool Hidden = false);

    public static byte[] Build(params Sheet[] sheets)
    {
        var shared = new List<string>();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            var workbook = new StringBuilder(
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var rels = new StringBuilder(
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 0; i < sheets.Length; i++)
            {
                var state = sheets[i].Hidden ? " state=\"hidden\"" : "";
                workbook.Append($"<sheet name=\"{Escape(sheets[i].Name)}\" sheetId=\"{i + 1}\"{state} r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                Write(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i], shared));
            }
            Write(zip, "xl/workbook.xml", workbook.Append("</sheets></workbook>").ToString());
            Write(zip, "xl/_rels/workbook.xml.rels", rels.Append("</Relationships>").ToString());
            Write(zip, "xl/sharedStrings.xml",
                "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                string.Concat(shared.Select(s => $"<si><t xml:space=\"preserve\">{Escape(s)}</t></si>")) + "</sst>");
        }
        return buffer.ToArray();
    }

    /// <summary>A workbook whose one sheet part is the given XML, for the cases a builder would tidy away.</summary>
    public static byte[] WithSheetXml(string sheetXml, string? sharedStringsXml = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets>" +
                "<sheet name=\"S\" sheetId=\"1\"/></sheets></workbook>");
            Write(zip, "xl/worksheets/sheet1.xml", sheetXml);
            if (sharedStringsXml is not null) Write(zip, "xl/sharedStrings.xml", sharedStringsXml);
        }
        return buffer.ToArray();
    }

    /// <summary>The usual shape of a branch's count: a title, the branch, a header, then the articles.</summary>
    public static byte[] StockSheet(params (string Name, object Shop, object Rented)[] articles)
    {
        var rows = new List<Row>
        {
            new(2, ("K", "الفرع "), ("L", "أولاد موسى")),
            new(4, ("K", "رصيد اول الفترة ")),
            new(5, ("I", "م"), ("J", "الصنف"), ("K", "محل "), ("L", "مؤجر "), ("M", "اجمالي الفرع ")),
        };
        for (var i = 0; i < articles.Length; i++)
        {
            var (name, shop, rented) = articles[i];
            var total = Convert.ToDecimal(shop, CultureInfo.InvariantCulture) + Convert.ToDecimal(rented, CultureInfo.InvariantCulture);
            rows.Add(new Row(6 + i, ("I", i + 1), ("J", name), ("K", shop), ("L", rented), ("M", total)));
        }
        return Build(new Sheet("29-09", rows));
    }

    public static Worksheet Read(byte[] workbook, string? sheet = null) =>
        AlgeriaPos.Infrastructure.Catalog.XlsxReader.Read(new MemoryStream(workbook), sheet);

    private static string SheetXml(Sheet sheet, List<string> shared)
    {
        var xml = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        foreach (var row in sheet.Rows)
        {
            xml.Append($"<row r=\"{row.Number}\">");
            foreach (var (column, value) in row.Cells)
            {
                var reference = column + row.Number;
                if (value is string text)
                {
                    var at = shared.IndexOf(text);
                    if (at < 0) { shared.Add(text); at = shared.Count - 1; }
                    xml.Append($"<c r=\"{reference}\" t=\"s\"><v>{at}</v></c>");
                }
                else
                {
                    var number = Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                    xml.Append($"<c r=\"{reference}\"><v>{number}</v></c>");
                }
            }
            xml.Append("</row>");
        }
        return xml.Append("</sheetData></worksheet>").ToString();
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        using var stream = zip.CreateEntry(path).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Escape(string value) => SecurityElement.Escape(value);
}
