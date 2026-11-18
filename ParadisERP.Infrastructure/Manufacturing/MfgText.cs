namespace ParadisERP.Infrastructure.Manufacturing;

/// <summary>The few checks every manufacturing form shares, written once.</summary>
internal static class MfgText
{
    /// <summary>Trimmed, or null when blank.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A code as it is stored: trimmed, inner spaces removed, upper case unless <paramref name="keepCase"/>.</summary>
    public static string Code(string? value, bool keepCase = false)
    {
        var code = string.Concat((value ?? "").Where(c => !char.IsWhiteSpace(c)));
        return keepCase ? code : code.ToUpperInvariant();
    }

    /// <summary>The first problem with a required name, or null.</summary>
    public static string? Required(string? value, int max, string field) =>
        Clean(value) is not string text ? $"{field} is required."
        : text.Length > max ? $"{field} is longer than {max} characters."
        : null;

    /// <summary>The first problem with an optional text, or null.</summary>
    public static string? Optional(string? value, int max, string field) =>
        Clean(value) is string text && text.Length > max ? $"{field} is longer than {max} characters." : null;

    /// <summary>The first non-null problem.</summary>
    public static string? First(params string?[] problems) => problems.FirstOrDefault(p => p is not null);
}
