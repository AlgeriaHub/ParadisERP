using System.Globalization;
using System.IO.Compression;
using System.Text;
using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Reports;
using Xunit;

namespace AlgeriaPos.Tests.Reports;

public class XlsxWriterTests
{
    [Fact]
    public void Arabic_text_numbers_and_totals_round_trip_with_expected_layout()
    {
        var sheet = new XlsxSheet
        {
            Name = "تقرير المبيعات",
            RightToLeft = true,
            Title = ["تقرير المبيعات", "فرع الجزائر"],
            Columns =
            [
                new("البيان", XlsxKind.Text),
                new("العدد", XlsxKind.Number, Decimals: 0),
                new("السعر", XlsxKind.Number, Decimals: 2),
                new("ملاحظات", XlsxKind.Text),
            ],
            Rows =
            [
                ["ارجل جالفا", 972, 1452.5m, null],
            ],
            Totals = ["المجموع", 972, 1452.5m, null],
        };

        var bytes = XlsxWriter.Write(sheet);
        var read = XlsxReader.Read(new MemoryStream(bytes));

        Assert.Equal("تقرير المبيعات", read.Name);
        Assert.Equal(5, read.Rows.Count);

        // Title lines above header row: rows 1 and 2
        var title1 = read.Rows.Single(r => r.Number == 1);
        Assert.Equal("تقرير المبيعات", title1[1]!.Text);

        var title2 = read.Rows.Single(r => r.Number == 2);
        Assert.Equal("فرع الجزائر", title2[1]!.Text);

        // Header row present at expected row number (row 4: 2 title lines + 1 empty row)
        var header = read.Rows.Single(r => r.Number == 4);
        Assert.Equal("البيان", header[1]!.Text);
        Assert.Equal("العدد", header[2]!.Text);
        Assert.Equal("السعر", header[3]!.Text);
        Assert.Equal("ملاحظات", header[4]!.Text);

        // Data row: row 5
        var data = read.Rows.Single(r => r.Number == 5);
        Assert.Equal("ارجل جالفا", data[1]!.Text);
        Assert.Null(data[1]!.Number);
        Assert.Equal(972m, data[2]!.Number);
        Assert.Equal(1452.5m, data[3]!.Number);
        Assert.Null(data[4]); // null cell absent

        // Totals row: row 6 (last)
        var totals = read.Rows.Single(r => r.Number == 6);
        Assert.Equal("المجموع", totals[1]!.Text);
        Assert.Equal(972m, totals[2]!.Number);
        Assert.Equal(1452.5m, totals[3]!.Number);
        Assert.Null(totals[4]); // null cell absent
    }

    [Fact]
    public void Text_starting_with_formula_operator_is_read_back_as_exact_text_without_formula_element()
    {
        var sheet = new XlsxSheet
        {
            Name = "FormulaGuard",
            Columns = [new("Calc", XlsxKind.Text)],
            Rows =
            [
                ["=SUM(A1)"],
            ],
        };

        var bytes = XlsxWriter.Write(sheet);
        var read = XlsxReader.Read(new MemoryStream(bytes));

        var row = read.Rows.Single(r => r.Number == 2);
        Assert.Equal("=SUM(A1)", row[1]!.Text);

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(sheetEntry);
        using var reader = new StreamReader(sheetEntry.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();

        Assert.DoesNotContain("<f>", xml);
        Assert.DoesNotContain("<f ", xml);
        Assert.Contains("=SUM(A1)", xml);
    }

    [Fact]
    public void Two_sheets_second_chosen_by_name_and_invalid_chars_sanitised_with_duplicate_suffix()
    {
        var rawName = "Reports/2026:Monthly-Sales-Summary-Detailed";
        var sheet1 = new XlsxSheet
        {
            Name = rawName,
            Columns = [new("Col1", XlsxKind.Text)],
            Rows = [["Row1"]],
        };
        var sheet2 = new XlsxSheet
        {
            Name = rawName,
            Columns = [new("Col1", XlsxKind.Text)],
            Rows = [["Row2"]],
        };

        var bytes = XlsxWriter.Write(sheet1, sheet2);

        // rawName (43 chars) sanitised of '/' and ':' -> "Reports2026Monthly-Sales-Summary-Detailed" (41 chars)
        // sheet1 cut to 31 chars -> "Reports2026Monthly-Sales-Summar"
        // sheet2 duplicate: base cut to 27 chars + " (2)" -> "Reports2026Monthly-Sales-Su (2)" (31 chars)
        var expectedSheet2Name = "Reports2026Monthly-Sales-Su (2)";

        var readSheet2 = XlsxReader.Read(new MemoryStream(bytes), expectedSheet2Name);
        Assert.Equal(expectedSheet2Name, readSheet2.Name);
        Assert.Equal("Row2", readSheet2.Rows.Single(r => r.Number == 2)[1]!.Text);

        Assert.Equal(2, readSheet2.SheetNames.Count);
        var name1 = readSheet2.SheetNames[0];
        var name2 = readSheet2.SheetNames[1];

        Assert.True(name1.Length <= 31);
        Assert.True(name2.Length <= 31);
        Assert.DoesNotContain("/", name1);
        Assert.DoesNotContain(":", name1);
        Assert.DoesNotContain("/", name2);
        Assert.DoesNotContain(":", name2);
        Assert.EndsWith(" (2)", name2);
    }

    [Fact]
    public void DateOnly_reads_back_with_expected_excel_serial()
    {
        var date = new DateOnly(2026, 10, 12);
        var expectedSerial = (new DateTime(2026, 10, 12) - new DateTime(1899, 12, 30)).TotalDays;

        var sheet = new XlsxSheet
        {
            Name = "Dates",
            Columns = [new("DateCol", XlsxKind.Date)],
            Rows =
            [
                [date],
            ],
        };

        var bytes = XlsxWriter.Write(sheet);
        var read = XlsxReader.Read(new MemoryStream(bytes));

        var dataRow = read.Rows.Single(r => r.Number == 2);
        var cell = dataRow[1];
        Assert.NotNull(cell);
        Assert.Equal(expectedSerial, cell.Serial);
        Assert.Equal(46307, cell.Serial);
    }

    [Fact]
    public void Column_28_header_lands_in_column_AB()
    {
        var columns = Enumerable.Range(1, 28)
            .Select(i => new XlsxColumn($"Col{i}", XlsxKind.Text))
            .ToList();

        var sheet = new XlsxSheet
        {
            Name = "ManyColumns",
            Columns = columns,
            Rows = [],
        };

        var bytes = XlsxWriter.Write(sheet);
        var read = XlsxReader.Read(new MemoryStream(bytes));

        var headerRow = read.Rows.Single(r => r.Number == 1);
        Assert.Equal("Col28", headerRow[28]!.Text);
        Assert.Null(headerRow[29]);

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(sheetEntry);
        using var reader = new StreamReader(sheetEntry.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        Assert.Contains("r=\"AB1\"", xml);
    }

    [Fact]
    public void Control_char_is_stripped_and_xml_special_characters_remain_intact()
    {
        var rawText = "Stock\u0001 & <Price> \"Special\"";
        var sheet = new XlsxSheet
        {
            Name = "XmlEscaping",
            Columns = [new("Name", XlsxKind.Text)],
            Rows =
            [
                [rawText],
            ],
        };

        var bytes = XlsxWriter.Write(sheet);
        var read = XlsxReader.Read(new MemoryStream(bytes));

        var cell = read.Rows.Single(r => r.Number == 2)[1];
        Assert.NotNull(cell);
        Assert.Equal("Stock & <Price> \"Special\"", cell.Text);
    }

    [Fact]
    public void Write_with_no_sheets_throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => XlsxWriter.Write());
        Assert.Throws<ArgumentException>(() => XlsxWriter.Write(Array.Empty<XlsxSheet>()));
        Assert.Throws<ArgumentException>(() => XlsxWriter.Write(new List<XlsxSheet>()));
    }

    [Fact]
    public void Zip_contains_styles_and_content_types_and_sheet_xml_has_rightToLeft_when_true()
    {
        var sheetRtl = new XlsxSheet
        {
            Name = "ArabicRtl",
            RightToLeft = true,
            Columns = [new("Col1", XlsxKind.Text)],
            Rows = [["Value"]],
        };
        var sheetLtr = new XlsxSheet
        {
            Name = "EnglishLtr",
            RightToLeft = false,
            Columns = [new("Col1", XlsxKind.Text)],
            Rows = [["Value"]],
        };

        var bytesRtl = XlsxWriter.Write(sheetRtl);
        using (var zip = new ZipArchive(new MemoryStream(bytesRtl), ZipArchiveMode.Read))
        {
            Assert.NotNull(zip.GetEntry("xl/styles.xml"));
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));

            var sheetPart = zip.GetEntry("xl/worksheets/sheet1.xml");
            Assert.NotNull(sheetPart);
            using var reader = new StreamReader(sheetPart.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd();
            Assert.Contains("rightToLeft=\"1\"", xml);
        }

        var bytesLtr = XlsxWriter.Write(sheetLtr);
        using (var zip = new ZipArchive(new MemoryStream(bytesLtr), ZipArchiveMode.Read))
        {
            var sheetPart = zip.GetEntry("xl/worksheets/sheet1.xml");
            Assert.NotNull(sheetPart);
            using var reader = new StreamReader(sheetPart.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd();
            Assert.DoesNotContain("rightToLeft=\"1\"", xml);
        }
    }

    [Fact]
    public void DateTime_and_DateTimeOffset_round_trip_with_serial()
    {
        var dtZero = new DateTime(2026, 10, 12, 0, 0, 0);
        var dtWithTime = new DateTime(2026, 10, 12, 12, 0, 0);
        var dto = new DateTimeOffset(2026, 10, 12, 18, 30, 0, TimeSpan.FromHours(1));

        var sheet = new XlsxSheet
        {
            Name = "DateTimes",
            Columns =
            [
                new("DtZero", XlsxKind.Date),
                new("DtTime", XlsxKind.Date),
                new("Dto", XlsxKind.Date),
            ],
            Rows =
            [
                [dtZero, dtWithTime, dto],
            ],
        };

        var bytes = XlsxWriter.Write(sheet);
        var read = XlsxReader.Read(new MemoryStream(bytes));

        var row = read.Rows.Single(r => r.Number == 2);
        var epoch = new DateTime(1899, 12, 30);
        Assert.Equal((dtZero - epoch).TotalDays, row[1]!.Serial);
        Assert.Equal((dtWithTime - epoch).TotalDays, row[2]!.Serial);
        Assert.Equal((dto.DateTime - epoch).TotalDays, row[3]!.Serial);
    }
}
