using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlgeriaPos.Application.Catalog;

/// <summary>
/// When two written names are the same article.
///
/// <para>
/// A branch's stock sheet is typed by hand, and the same panel comes back as
/// <c>بانو 0,24*1,00 م</c>, <c>بانو 0.24* 1.00 م</c> and, from another branch,
/// <c>بانو 0.24*1م</c>. Matched on the raw text, every sheet after the first would create the
/// catalogue again under slightly different spellings, and the branches' stock would sit on
/// three products that are one. So the key forgets what a typist varies and keeps what tells
/// articles apart:
/// </para>
/// <list type="bullet">
/// <item>spacing, all of it — <c>اكس طويل</c> and <c>اكسطويل</c> are one key;</item>
/// <item>the written form of a number: a decimal comma, trailing zeros and Arabic-Indic digits
/// (<c>1,00</c>, <c>1.0</c>, <c>١</c> are all 1), and <c>×</c> or a Latin x between two numbers
/// read as <c>*</c>;</item>
/// <item>the Arabic letter variants the till's search already folds (hamza on alif, ta marbuta,
/// alif maqsura, harakat, tatweel), and case and accents in Latin.</item>
/// </list>
/// <para>
/// It never guesses past that: <c>076</c> is 76, not 0.76, and a word spelled differently is a
/// different article. A match it misses shows as "new" on the preview, where a person sees it;
/// a match it invents would put one article's count on another, where nobody would.
/// </para>
/// </summary>
public static partial class ProductNameKey
{
    public static string For(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var text = Fold(name);
        text = Times().Replace(text, "*");
        text = Figure().Replace(text, m =>
            decimal.TryParse(m.Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
                ? n.ToString("0.#########", CultureInfo.InvariantCulture)
                : m.Value);
        return Space().Replace(text, string.Empty);
    }

    /// <summary>
    /// The name as it will be stored: the typist's own words, with the edges trimmed and runs of
    /// spaces made one. Nothing else is changed — the key is for matching, not for rewriting.
    /// </summary>
    public static string Clean(string? name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : Space().Replace(name.Trim(), " ");

    /// <summary>
    /// The decomposed form splits أ إ آ (and ؤ ئ) into the bare letter and a combining hamza or
    /// madda, so dropping combining marks folds them along with the harakat — the same thing
    /// the till's <c>fold</c> does with <c>normalize('NFD')</c>.
    /// </summary>
    private static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            switch (c)
            {
                case 'ٱ': builder.Append('ا'); continue;
                case 'ة': builder.Append('ه'); continue;
                case 'ى' or 'ی': builder.Append('ي'); continue;
                case 'ک': builder.Append('ك'); continue;
                case 'ـ': continue; // tatweel
                case '٫': builder.Append('.'); continue; // Arabic decimal separator
                case '،': builder.Append(','); continue;
                case '×': builder.Append('*'); continue;
            }
            if (c is >= '٠' and <= '٩') { builder.Append((char)('0' + (c - '٠'))); continue; }
            if (c is >= '۰' and <= '۹') { builder.Append((char)('0' + (c - '۰'))); continue; }
            // Harakat and every other combining mark, Arabic or Latin.
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    [GeneratedRegex(@"(?<=\d)\s*[xX*]\s*(?=\d)")]
    private static partial Regex Times();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?")]
    private static partial Regex Figure();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();
}
