using AlgeriaPos.Application.Contracts;

namespace AlgeriaPos.Application.Regional;

/// <summary>
/// The interface faces an operator may pick for a company, as keys. The till owns the actual
/// font stacks and the woff2 files; this is only the contract for which keys are legal, so a
/// new face is added here and in the till's catalogue and nothing else has to change.
/// </summary>
public static class UiFonts
{
    /// <summary>Whatever the device already has. No font file is fetched.</summary>
    public const string System = "system";

    /// <summary>
    /// What a company gets until it chooses is Readex Pro, a modern open-source
    /// face cut for Arabic and Latin interfaces whose files ship with the app.
    /// </summary>
    public const string Default = "readex-pro";

    /// <summary>Keys the operator may choose. Arabic-first faces that also carry a Latin cut,
    /// because an Algerian till shows Arabic and French side by side.</summary>
    public static readonly string[] All =
    [
        System,
        "readex-pro",
        "cairo",
        "noto-sans-arabic",
        "tajawal",
        "ibm-plex-sans-arabic"
    ];

    public static bool IsKnown(string? key) =>
        !string.IsNullOrWhiteSpace(key) && All.Contains(key.Trim().ToLowerInvariant());

    /// <summary>
    /// Falls back to <see cref="Default"/> rather than rejecting an unknown or empty key: a
    /// profile that never chose, or chose a face that no longer exists, gets the professional
    /// default. The device's own faces remain an explicit choice (<see cref="System"/>), never
    /// what a company lands on by accident.
    /// </summary>
    public static string Normalize(string? key)
    {
        var trimmed = (key ?? "").Trim().ToLowerInvariant();
        return IsKnown(trimmed) ? trimmed : Default;
    }

    /// <summary>
    /// What a company's own face becomes when a save carries one.
    /// </summary>
    /// <param name="current">What the company has now.</param>
    /// <param name="chosen">
    /// Unset: the body did not mention the face, so it is left alone — an older client cannot
    /// reset it. Null or blank: back to the country profile's face. Anything else: that face.
    /// </param>
    /// <param name="face">The value to store, null meaning "follow the profile".</param>
    /// <returns>
    /// Null when the choice stands, else the sentence refusing it. Unlike <see cref="Normalize"/>,
    /// which quietly falls back for a profile nobody is editing, an unknown key from a person
    /// choosing a face is refused: silently storing something else would leave the screen showing
    /// a face the operator did not pick and no way to tell why.
    /// </returns>
    public static string? TryChoose(string? current, Optional<string?> chosen, out string? face)
    {
        face = current;
        if (!chosen.IsSet) return null;

        var trimmed = (chosen.Value ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            face = null;
            return null;
        }
        if (!IsKnown(trimmed))
            return $"Unknown interface font '{chosen.Value}'. Use one of: {string.Join(", ", All)}.";

        face = trimmed;
        return null;
    }
}
