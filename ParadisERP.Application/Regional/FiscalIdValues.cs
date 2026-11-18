namespace ParadisERP.Application.Regional;

/// <summary>
/// The one reading of a set of fiscal identifier values typed into a form — the company's on the
/// store profile, a branch's on the branch. Keys must be ones the country profile defines; each
/// value is trimmed, a blank one is dropped rather than stored, and none may exceed
/// <see cref="RegionalSettings.MaxFiscalIdLength"/>.
///
/// <para>
/// Whether a key is <i>required</i> is not decided here: the company must carry the profile's
/// required identifiers, while a branch's blank field simply prints the company's.
/// </para>
/// </summary>
public static class FiscalIdValues
{
    /// <summary>
    /// Returns the one English sentence for the 400, or null and the cleaned values. Values the
    /// row still holds under keys the operator has since removed from the profile are carried
    /// over from <paramref name="stored"/>, so re-adding the identifier restores them.
    /// </summary>
    public static string? TryNormalize(
        RegionalSettings profile, IReadOnlyDictionary<string, string>? input,
        IReadOnlyDictionary<string, string>? stored, out Dictionary<string, string> clean)
    {
        clean = new Dictionary<string, string>();
        foreach (var (key, value) in input ?? new Dictionary<string, string>())
        {
            if (profile.FiscalIds.All(d => d.Key != key))
                return $"'{key}' is not a fiscal identifier of this company's country profile.";
            var text = (value ?? "").Trim();
            if (text.Length > RegionalSettings.MaxFiscalIdLength)
                return $"'{key}' is limited to {RegionalSettings.MaxFiscalIdLength} characters.";
            if (text.Length > 0) clean[key] = text;
        }
        foreach (var (key, value) in stored ?? new Dictionary<string, string>())
            if (profile.FiscalIds.All(d => d.Key != key)) clean[key] = value;
        return null;
    }
}
