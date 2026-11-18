using System.Globalization;
using System.Security.Cryptography;

namespace ParadiERP.Application.Catalog;

/// <summary>
/// A unique three-digit location code (owner, 2026-10-19), chosen from 100–999.
/// Deleted locations keep their codes reserved. Codes are assigned once and never edited.
/// </summary>
public static class BranchCodes
{
    public const int First = 100;
    public const int Last = 999;
    public const int Length = 3;

    /// <summary>The stable error when all 900 codes have been assigned.</summary>
    public const string ExhaustedCode = "branch_codes_exhausted";
    public const string ExhaustedMessage = "All 900 branch codes (100–999) are in use.";

    /// <summary>Three ASCII digits, the first not 0.</summary>
    public static bool IsWellFormed(string? code) =>
        code is { Length: Length } && code[0] is >= '1' and <= '9' && code.All(char.IsAsciiDigit);

    /// <summary>Randomly choose an unused code; null only when the whole range is taken.</summary>
    public static string? Next(IEnumerable<string?> used)
    {
        var taken = used.Where(IsWellFormed).ToHashSet(StringComparer.Ordinal);
        var available = Enumerable.Range(First, Last - First + 1)
            .Select(n => n.ToString(CultureInfo.InvariantCulture)).Where(code => !taken.Contains(code)).ToArray();
        return available.Length == 0 ? null : available[RandomNumberGenerator.GetInt32(available.Length)];
    }
}
