using ParadisERP.Application.Contracts;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Rentals;

namespace ParadisERP.Tests.Rentals;

/// <summary>
/// The quantity statement's Excel file: the screen's statement, written through the shared
/// writer, with the summary as the items' totals row and no currency written anywhere.
/// </summary>
public class QuantityStatementWorkbookTests
{
    private static readonly Guid Branch = Guid.NewGuid();

    private static QuantityStatementDto Statement()
    {
        var contract = Guid.NewGuid();
        var moves = new List<QuantityStatementMovementDto>
        {
            new(new DateOnly(2026, 3, 1), null, "opening", null, null, null, null, 0, 0, 0, null, 0, null, null),
            new(new DateOnly(2026, 3, 1), DateTime.UtcNow, "issue", Guid.NewGuid(), "ISS-000001", contract, "RC-000001",
                4, 0, 0, 5, 4, Branch, "Chantier"),
        };
        return new QuantityStatementDto(
            Guid.NewGuid(), "C-001", "Entreprise Test", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), false,
            new DateOnly(2026, 1, 5),
            new QuantityStatementSummaryDto(2, 14, 4, 0, 10, 1234.5m, 300m),
            [
                new(Guid.NewGuid(), "ECH-1", "Échafaudage", "سقالة", null, 0, 4, 0, 0, 0, 4,
                    [new("rent", 5), new("rent", 6)], false, 682, 300, false, moves),
                new(Guid.NewGuid(), "COL-1", "Collier", null, null, 0, 10, 4, 0, 0, 6,
                    [new("rent", 2.5m)], false, 552.5m, 0, false, moves),
            ],
            [
                new(contract, "RC-000001", "open", Branch, null, "Chantier", new DateOnly(2026, 3, 1), false, 1234.5m, 300m,
                [
                    new(Guid.NewGuid(), Guid.NewGuid(), "ECH-1", "Échafaudage", "سقالة", new DateOnly(2026, 3, 1),
                        0, 4, 0, 0, 0, 0, 4, 5, false, [new("rent", 5)], 682, 300),
                ]),
            ],
            [new("pricingCorrectionPending", null, null, null, null, contract, "RC-000001", null, null)]);
    }

    private static byte[] Build(string lang) =>
        QuantityStatementWorkbook.Build(Statement(), lang, id => id == Branch ? "Alger" : "—", 2);

    [Fact]
    public void The_items_sheet_carries_the_summary_as_its_totals_row()
    {
        var sheet = XlsxReader.Read(new MemoryStream(Build("fr")));

        Assert.Equal(3, sheet.SheetNames.Count);
        var totals = sheet.Rows[^1];
        Assert.Equal("Total", totals[1]!.Text);
        Assert.Equal(14m, totals[4]!.Number);
        Assert.Equal(4m, totals[5]!.Number);
        Assert.Equal(10m, totals[8]!.Number);
        Assert.Equal(1234.5m, totals[10]!.Number);
        Assert.Equal(300m, totals[11]!.Number);
    }

    [Fact]
    public void Two_rates_read_multiple_rates_and_the_rate_and_value_are_named_apart()
    {
        var sheet = XlsxReader.Read(new MemoryStream(Build("fr")));

        var header = sheet.Rows.First(r => r[1]?.Text == "Référence");
        Assert.Equal("Prix unitaire / jour", header[9]!.Text);
        Assert.Equal("Valeur de location de la période", header[10]!.Text);
        var scaffold = sheet.Rows.First(r => r[1]?.Text == "ECH-1");
        Assert.Equal("Plusieurs tarifs", scaffold[9]!.Text);
        var clamp = sheet.Rows.First(r => r[1]?.Text == "COL-1");
        Assert.Equal("2.50", clamp[9]!.Text);
    }

    [Fact]
    public void Arabic_uses_the_arabic_names_and_words_and_no_currency_is_written()
    {
        var bytes = Build("ar");
        var sheet = XlsxReader.Read(new MemoryStream(bytes));

        Assert.Equal("حسب الصنف", sheet.Name);
        Assert.Contains(sheet.Rows, r => r[2]?.Text == "سقالة");
        Assert.Contains(sheet.Rows, r => r[1]?.Text == "كشف كميات العميل");
        var all = sheet.Rows.SelectMany(r => r.Cells.Values).Select(c => c.Text ?? "").ToList();
        Assert.DoesNotContain(all, t => t.Contains("DA") || t.Contains("DZD") || t.Contains("د.ج"));
        // The unfinished correction is said in words above the table.
        Assert.Contains(all, t => t.Contains("RC-000001"));

        var movements = XlsxReader.Read(new MemoryStream(bytes), "الحركات");
        Assert.Contains(movements.Rows, r => r[4]?.Text == "ISS-000001");
    }
}
