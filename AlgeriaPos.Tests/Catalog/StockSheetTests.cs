using AlgeriaPos.Application.Catalog;

namespace AlgeriaPos.Tests.Catalog;

public class StockSheetTests
{
    [Fact]
    public void The_owner_s_layout_finds_the_header_under_the_title_and_offers_every_count()
    {
        var layout = StockSheet.Read(TestWorkbook.Read(TestWorkbook.StockSheet(
            ("ارجل جالفا", 972, 1897), ("كولي ثابت", -192, 11))));

        Assert.Equal(5, layout.HeaderRow);
        Assert.Equal(10, layout.NameColumn);
        Assert.Equal("الصنف", layout.NameHeader);
        // The serial number, the shop, what is out on hire and the branch total — all numbers.
        Assert.Equal(["I", "K", "L", "M"], layout.Columns.Select(c => c.Letter));
        Assert.Equal(["م", "محل", "مؤجر", "اجمالي الفرع"], layout.Columns.Select(c => c.Header));
        // محل is the shop, the available count; the header says so, so it is chosen.
        Assert.Equal(11, layout.QuantityColumn);

        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal(new decimal?[] { 1, 972, 1897, 2869 }, layout.Lines[0].Values);
        Assert.Equal(-192m, layout.Lines[1].Values[1]);
    }

    [Fact]
    public void With_no_header_naming_the_count_none_is_chosen()
    {
        var sheet = TestWorkbook.Read(TestWorkbook.Build(new TestWorkbook.Sheet("S",
        [
            new(1, ("A", "Désignation"), ("B", "Ouled Moussa"), ("C", "Skikda")),
            new(2, ("A", "Tube 3 m"), ("B", 12), ("C", 4)),
        ])));

        var layout = StockSheet.Read(sheet);

        Assert.Null(layout.QuantityColumn);
        Assert.Equal(["Ouled Moussa", "Skikda"], layout.Columns.Select(c => c.Header));
    }

    [Fact]
    public void A_total_row_and_rows_with_no_article_name_are_not_articles()
    {
        var sheet = TestWorkbook.Read(TestWorkbook.Build(new TestWorkbook.Sheet("S",
        [
            new(1, ("A", "Article"), ("B", "Qté")),
            new(2, ("A", "Tube 3 m"), ("B", 12)),
            new(3, ("A", "   "), ("B", 5)),
            new(4, ("A", "17"), ("B", 5)),
            new(5, ("A", "المجموع"), ("B", 17)),
            new(6, ("A", "Total"), ("B", 17)),
        ])));

        var layout = StockSheet.Read(sheet);

        Assert.Equal(["Tube 3 m"], layout.Lines.Select(l => l.Name));
        Assert.Equal(2, layout.QuantityColumn);
    }

    [Fact]
    public void A_sheet_with_no_article_column_is_refused_in_words()
    {
        var sheet = TestWorkbook.Read(TestWorkbook.Build(new TestWorkbook.Sheet("S",
        [
            new(1, ("A", "Tube 3 m"), ("B", 12)),
        ])));

        var refused = Assert.Throws<SpreadsheetException>(() => StockSheet.Read(sheet));
        Assert.Contains("الصنف", refused.Message);
    }
}
