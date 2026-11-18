namespace ParadisERP.Application.Catalog;

/// <summary>
/// One cell as the sheet holds it: its text, and its number when it is one. A count typed as
/// text ("972") is still a count, so the reader fills <see cref="Number"/> for those too.
/// <see cref="Serial"/> is the number Excel stored, unrounded, and only for a cell that holds a
/// number rather than text: a date or a time is such a number (days since 1900, a fraction of a
/// day), and the three places <see cref="Number"/> keeps would move a time by up to 43 seconds.
/// </summary>
public sealed record SheetCell(string Text, decimal? Number, double? Serial = null);

/// <summary>A row by its number in the sheet (1-based, as Excel shows it), cells by column (1 = A).</summary>
public sealed record SheetRow(int Number, IReadOnlyDictionary<int, SheetCell> Cells)
{
    public SheetCell? this[int column] => Cells.TryGetValue(column, out var cell) ? cell : null;
}

/// <summary>A worksheet read out of a workbook, with the names of every sheet beside it.</summary>
public sealed record Worksheet(string Name, IReadOnlyList<string> SheetNames, IReadOnlyList<SheetRow> Rows);

/// <summary>A file that cannot be read as a workbook, with the sentence that says why.</summary>
public sealed class SpreadsheetException(string message) : Exception(message);

public static class SheetColumns
{
    /// <summary>1 → A, 26 → Z, 27 → AA.</summary>
    public static string Letter(int column)
    {
        var letters = string.Empty;
        for (var n = column; n > 0; n = (n - 1) / 26)
            letters = (char)('A' + (n - 1) % 26) + letters;
        return letters;
    }

    /// <summary>The column of a cell reference such as <c>J6</c>, or 0 when it has none.</summary>
    public static int Of(string reference)
    {
        var column = 0;
        foreach (var c in reference)
        {
            var upper = char.ToUpperInvariant(c);
            if (upper is < 'A' or > 'Z') break;
            column = column * 26 + (upper - 'A' + 1);
        }
        return column;
    }
}
