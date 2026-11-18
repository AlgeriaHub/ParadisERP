namespace AlgeriaPos.Application.Catalog;

/// <summary>A column a quantity may be read from, with the words its header row gives it.</summary>
public sealed record StockSheetColumn(int Index, string Letter, string Header);

/// <summary>One article line: its row in the sheet, its name, and a value per quantity column.</summary>
public sealed record StockSheetLine(int Row, string Name, IReadOnlyList<decimal?> Values);

/// <summary>
/// Where the articles and their counts are on a branch's stock sheet.
/// <see cref="QuantityColumn"/> is null when no header says which count it is — see
/// <see cref="StockSheet.Read"/> for why that is left to a person.
/// </summary>
public sealed record StockSheetLayout(
    int HeaderRow, int NameColumn, string NameHeader,
    IReadOnlyList<StockSheetColumn> Columns, int? QuantityColumn,
    IReadOnlyList<StockSheetLine> Lines);

/// <summary>
/// Reads a stock count laid out the way a person lays one out in Excel: a title or two, a
/// header row, one article per row, perhaps a total at the bottom.
///
/// <para>
/// The header row is the first one, within the top <see cref="HeaderSearchRows"/>, holding a
/// cell that names the article column (<c>الصنف</c>, <c>désignation</c>, <c>article</c>…).
/// Every other column that has a number under it is offered as a quantity, because one sheet
/// often carries several — the owner's carries what is in the shop, what is out on hire and
/// their sum — and which one is the opening balance is not something to infer from position.
/// </para>
/// <para>
/// A quantity column is only <b>pre-selected</b> when its header says it is the available
/// count (<c>محل</c>, <c>الكمية</c>, <c>qté</c>, <c>stock</c>…). With no such header nothing is
/// chosen: a default that happens to be the "on hire" column would load the wrong figure into
/// every line without a single warning, which is worse than asking.
/// </para>
/// </summary>
public static class StockSheet
{
    public const int HeaderSearchRows = 30;

    private static readonly HashSet<string> NameHeaders = Keys(
        "الصنف", "صنف", "الاصناف", "اسم الصنف", "الاسم", "اسم", "المنتج", "المنتوج", "المنتجات",
        "السلعة", "السلع", "البيان", "المادة", "المواد", "البضاعة",
        "désignation", "designation", "article", "articles", "produit", "produits", "libellé", "nom",
        "name", "item", "items", "product", "products", "description");

    private static readonly HashSet<string> AvailableHeaders = Keys(
        "محل", "المحل", "في المحل", "الكمية", "كمية", "الكمية المتوفرة", "المتوفر", "متوفر", "العدد",
        "المخزون", "مخزون", "الرصيد", "رصيد",
        "qte", "qté", "quantité", "quantite", "stock", "disponible", "en stock",
        "qty", "quantity", "available", "on hand");

    private static readonly HashSet<string> TotalRows = Keys(
        "المجموع", "مجموع", "الاجمالي", "اجمالي", "المجموع الكلي", "total", "totaux", "sous-total", "sous total", "grand total");

    /// <summary>The layout, or a refusal in words when there is no article column to find.</summary>
    public static StockSheetLayout Read(Worksheet sheet)
    {
        var header = sheet.Rows.Take(HeaderSearchRows)
            .Select(row => (row, column: row.Cells.Where(c => NameHeaders.Contains(ProductNameKey.For(c.Value.Text)))
                .Select(c => c.Key).DefaultIfEmpty(0).Min()))
            .FirstOrDefault(x => x.column > 0);
        if (header.row is null)
            throw new SpreadsheetException(
                "No header row names the article column. Put a header such as \"الصنف\" or \"Désignation\" " +
                $"above the article names, within the first {HeaderSearchRows} rows.");

        var nameColumn = header.column;
        var body = sheet.Rows
            .Where(r => r.Number > header.row.Number)
            .Select(r => (row: r, name: ProductNameKey.Clean(r[nameColumn]?.Text)))
            .Where(x => x.name.Any(char.IsLetter) && !TotalRows.Contains(ProductNameKey.For(x.name)))
            .ToList();

        // Every column with a number under it, in sheet order, the article column aside.
        var columns = body
            .SelectMany(x => x.row.Cells.Where(c => c.Key != nameColumn && c.Value.Number is not null).Select(c => c.Key))
            .Distinct()
            .Order()
            .Select(index => new StockSheetColumn(index, SheetColumns.Letter(index), ProductNameKey.Clean(header.row[index]?.Text)))
            .ToList();

        int? quantity = columns.FirstOrDefault(c => AvailableHeaders.Contains(ProductNameKey.For(c.Header)))?.Index;

        var lines = body
            .Select(x => new StockSheetLine(x.row.Number, x.name, columns.Select(c => x.row[c.Index]?.Number).ToList()))
            .ToList();

        return new StockSheetLayout(
            header.row.Number, nameColumn, ProductNameKey.Clean(header.row[nameColumn]?.Text), columns, quantity, lines);
    }

    private static HashSet<string> Keys(params string[] words) =>
        words.Select(ProductNameKey.For).ToHashSet(StringComparer.Ordinal);
}
