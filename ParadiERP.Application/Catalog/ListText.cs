using System.Globalization;
using System.Text;

namespace ParadiERP.Application.Catalog;

/// <summary>
/// Searching and ordering text in a list the server pages (owner, 2026-11-17), the way the screens
/// did it in the browser: <see cref="Fold"/> is the till's <c>fold</c> (core/product-search.ts), so
/// سقاله finds سقالة and echafaudage finds échafaudage; <see cref="Compare"/> is the browser's
/// <c>Intl.Collator(lang, { numeric: true, sensitivity: 'base' })</c>, so "Panneau 2" comes before
/// "Panneau 10".
/// </summary>
public static class ListText
{
    /// <summary>Lower case, without Latin accents or Arabic harakat, hamza seats, ta marbuta or alif maqsura variants, Latin digits.</summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            // Digits first: the Arabic-Indic ones (U+0660–0669) sit inside the harakat range below.
            if (c is >= '٠' and <= '٩') { builder.Append((char)('0' + (c - '٠'))); continue; }
            if (c is >= '̀' and <= 'ͯ') continue;                 // Latin combining marks
            if (c is >= 'ً' and <= 'ٰ' or 'ٕ' or 'ـ') continue; // harakat, hamza, tatweel
            var folded = c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' or 'ی' => 'ي',
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                _ => c,
            };
            builder.Append(char.ToLowerInvariant(folded));
        }
        return builder.ToString().Trim();
    }

    /// <summary>
    /// Orders two texts as the reader's language does, ignoring case and accents, with runs of
    /// digits compared as numbers.
    /// </summary>
    public static int Compare(string? a, string? b, string? lang)
    {
        var culture = CultureFor(lang);
        a ??= string.Empty;
        b ??= string.Empty;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var startA = i;
                var startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var numberA = a[startA..i].TrimStart('0');
                var numberB = b[startB..j].TrimStart('0');
                if (numberA.Length != numberB.Length) return numberA.Length.CompareTo(numberB.Length);
                var byDigits = string.CompareOrdinal(numberA, numberB);
                if (byDigits != 0) return byDigits;
                continue;
            }
            var endA = i;
            var endB = j;
            while (endA < a.Length && !char.IsAsciiDigit(a[endA])) endA++;
            while (endB < b.Length && !char.IsAsciiDigit(b[endB])) endB++;
            var byText = culture.CompareInfo.Compare(a[i..endA], b[j..endB],
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth);
            if (byText != 0) return byText;
            i = endA;
            j = endB;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static CultureInfo CultureFor(string? lang)
    {
        try
        {
            return string.IsNullOrWhiteSpace(lang) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(lang);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
