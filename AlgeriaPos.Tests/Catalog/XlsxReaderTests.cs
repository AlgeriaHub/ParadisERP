using System.IO.Compression;
using System.Text;
using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Infrastructure.Catalog;

namespace AlgeriaPos.Tests.Catalog;

public class XlsxReaderTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void Text_comes_from_the_shared_strings_and_numbers_from_the_cell()
    {
        var sheet = TestWorkbook.Read(TestWorkbook.StockSheet(("ارجل جالفا", 972, 1897), ("مقص 1,90 م", 1452.5, 2559.5)));

        Assert.Equal("29-09", sheet.Name);
        var first = sheet.Rows.Single(r => r.Number == 6);
        Assert.Equal("ارجل جالفا", first[10]!.Text);
        Assert.Null(first[10]!.Number);
        Assert.Equal(972m, first[11]!.Number);
        Assert.Equal(2869m, first[13]!.Number);
        Assert.Equal(1452.5m, sheet.Rows.Single(r => r.Number == 7)[11]!.Number);
    }

    [Fact]
    public void A_stored_double_is_cut_to_the_three_places_a_quantity_keeps()
    {
        var sheet = TestWorkbook.Read(TestWorkbook.WithSheetXml(
            $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\"><v>0.30000000000000004</v></c>" +
            "<c r=\"B1\"><v>1E3</v></c></row></sheetData></worksheet>"));

        Assert.Equal(0.3m, sheet.Rows[0][1]!.Number);
        Assert.Equal(1000m, sheet.Rows[0][2]!.Number);
    }

    [Fact]
    public void A_count_typed_as_text_is_still_a_count()
    {
        var sheet = TestWorkbook.Read(TestWorkbook.WithSheetXml(
            $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\">" +
            "<c r=\"A1\" t=\"inlineStr\"><is><t>972</t></is></c>" +
            "<c r=\"B1\" t=\"inlineStr\"><is><t>1 452,5</t></is></c>" +
            "<c r=\"C1\" t=\"inlineStr\"><is><t>١٤</t></is></c>" +
            "<c r=\"D1\" t=\"inlineStr\"><is><t>12 م</t></is></c>" +
            "<c r=\"E1\" t=\"inlineStr\"><is><t>1,200.5</t></is></c>" +
            "</row></sheetData></worksheet>"));

        var row = sheet.Rows[0];
        Assert.Equal(972m, row[1]!.Number);
        Assert.Equal(1452.5m, row[2]!.Number);
        Assert.Equal(14m, row[3]!.Number);
        Assert.Null(row[4]!.Number);
        // Grouping and decimal marks both present: refused rather than guessed.
        Assert.Null(row[5]!.Number);
    }

    [Fact]
    public void Rich_text_runs_are_joined_and_the_phonetic_guide_is_left_out()
    {
        var sheet = TestWorkbook.Read(TestWorkbook.WithSheetXml(
            $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"3\"><c r=\"B3\" t=\"s\"><v>0</v></c></row></sheetData></worksheet>",
            $"<sst xmlns=\"{Main}\"><si><r><t>بانو </t></r><r><t>0.50*3 م</t></r><rPh><t>guide</t></rPh></si></sst>"));

        Assert.Equal("بانو 0.50*3 م", sheet.Rows[0][2]!.Text);
        Assert.Equal(3, sheet.Rows[0].Number);
    }

    [Fact]
    public void A_cell_without_a_reference_follows_the_one_before_it()
    {
        var sheet = TestWorkbook.Read(TestWorkbook.WithSheetXml(
            $"<worksheet xmlns=\"{Main}\"><sheetData><row><c r=\"C1\"><v>1</v></c><c><v>2</v></c></row>" +
            "<row><c><v>3</v></c></row></sheetData></worksheet>"));

        Assert.Equal(2m, sheet.Rows[0][4]!.Number);
        Assert.Equal(2, sheet.Rows[1].Number);
        Assert.Equal(3m, sheet.Rows[1][1]!.Number);
    }

    [Fact]
    public void The_first_visible_sheet_is_read_unless_another_is_named()
    {
        var workbook = TestWorkbook.Build(
            new TestWorkbook.Sheet("old", [new(1, ("A", "hidden"))], Hidden: true),
            new TestWorkbook.Sheet("Skikda", [new(1, ("A", "first"))]),
            new TestWorkbook.Sheet("Oran", [new(1, ("A", "second"))]));

        var read = TestWorkbook.Read(workbook);
        Assert.Equal("Skikda", read.Name);
        Assert.Equal(["old", "Skikda", "Oran"], read.SheetNames);
        Assert.Equal("second", TestWorkbook.Read(workbook, "Oran").Rows[0][1]!.Text);
        Assert.Equal("Skikda", TestWorkbook.Read(workbook, "missing").Name);
    }

    [Fact]
    public void An_old_xls_or_any_other_file_is_refused_with_what_to_do()
    {
        // The first bytes of a binary .xls (an OLE compound file).
        var xls = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };
        var refused = Assert.Throws<SpreadsheetException>(() => TestWorkbook.Read(xls));
        Assert.Contains(".xlsx", refused.Message);

        Assert.Throws<SpreadsheetException>(() => TestWorkbook.Read(Zip(("readme.txt", "not a workbook"))));
    }

    [Fact]
    public void A_document_type_declaration_is_refused_rather_than_expanded()
    {
        var sheet = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"boom\">]>" +
                    $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>&e;</t></is></c></row></sheetData></worksheet>";

        Assert.Throws<SpreadsheetException>(() => TestWorkbook.Read(TestWorkbook.WithSheetXml(sheet)));
    }

    [Fact]
    public void A_part_that_inflates_past_the_cap_stops_there()
    {
        // Compresses to a few dozen kilobytes; inflates past the 32 MB cap. Written in chunks so
        // the test itself never holds the inflated part.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = zip.CreateEntry("xl/workbook.xml").Open())
                w.Write(Encoding.UTF8.GetBytes($"<workbook xmlns=\"{Main}\"><sheets><sheet name=\"S\" sheetId=\"1\"/></sheets></workbook>"));
            using var sheet = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open();
            sheet.Write(Encoding.UTF8.GetBytes($"<worksheet xmlns=\"{Main}\"><sheetData>"));
            var chunk = Encoding.UTF8.GetBytes(new string(' ', 1 << 20));
            for (long written = 0; written <= XlsxReader.MaxPartBytes; written += chunk.Length) sheet.Write(chunk);
            sheet.Write(Encoding.UTF8.GetBytes("</sheetData></worksheet>"));
        }

        var refused = Assert.Throws<SpreadsheetException>(() => TestWorkbook.Read(buffer.ToArray()));
        Assert.Contains("too large", refused.Message);
    }

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        return buffer.ToArray();
    }
}
