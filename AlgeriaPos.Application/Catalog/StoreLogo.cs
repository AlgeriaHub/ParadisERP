using System.Text;
using System.Text.RegularExpressions;
using AlgeriaPos.Application.Files;

namespace AlgeriaPos.Application.Catalog;

/// <summary>
/// The company logo printed at the head of every document (owner, 2026-09-30, answer 3) and the
/// branch logos shown in the top bar. The picture is kept in the files folder, named by its
/// content (<see cref="FilePaths.Logo"/>); the Store or Branch row keeps only that path (owner,
/// 2026-10-04). It reaches a screen through a signed-in endpoint, never a public URL.
///
/// <para>
/// A logo arrives as a <c>data:</c> URL on the same save as the rest of the row. Only four
/// raster/vector types are taken, base64-encoded, at most <see cref="MaxBytes"/> once decoded.
/// The bytes must be what the type says (a PNG that starts like a PNG), so a renamed file is
/// refused here rather than printing as a broken image.
/// </para>
/// <para>
/// An SVG is shown only through <c>&lt;img&gt;</c>, which never runs its scripts; it is still
/// refused when it carries a script, an event attribute, a <c>javascript:</c> link or a
/// <c>&lt;foreignObject&gt;</c>, so the stored copy is clean wherever it is opened later.
/// </para>
/// </summary>
public static partial class StoreLogo
{
    public const int MaxBytes = 512 * 1024;

    /// <summary>
    /// A branch's logo is capped at half the company's: every till fetches it and keeps a copy,
    /// so the top bar can draw it offline.
    /// </summary>
    public const int BranchMaxBytes = 256 * 1024;

    public static readonly IReadOnlyList<string> MediaTypes = ["image/png", "image/jpeg", "image/webp", "image/svg+xml"];

    /// <summary>A logo read from a save, ready to be written to the files folder.</summary>
    public sealed record LogoFile(string MediaType, byte[] Content)
    {
        public string Path => FilePaths.Logo(Content, MediaType);
    }

    public enum ChangeKind { Keep, Replace, Remove }

    /// <summary>What a save asks of the logo, and the new picture when it brings one.</summary>
    public sealed record Change(ChangeKind Kind, LogoFile? File = null);

    [GeneratedRegex(@"^data:(?<type>[a-z0-9.+/-]+);base64,(?<data>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DataUrl();

    /// <summary>The endpoint the company logo is read from, signed in.</summary>
    public const string StoreEndpoint = "/api/catalog/store/logo";

    /// <summary>The endpoint a branch's logo is read from, signed in.</summary>
    public static string BranchEndpoint(Guid branchId) => $"/api/catalog/branches/{branchId}/logo";

    /// <summary>
    /// The company logo's link, or null when there is none. The version changes with the file, so a
    /// copy kept under one link is never stale.
    /// </summary>
    public static string? StoreUrl(string? path) =>
        path is null ? null : $"{StoreEndpoint}?v={FilePaths.Version(path)}";

    /// <summary>A branch logo's link, or null when the branch has none.</summary>
    public static string? BranchUrl(Guid branchId, string? path) =>
        path is null ? null : $"{BranchEndpoint(branchId)}?v={FilePaths.Version(path)}";

    /// <summary>
    /// Reads the logo field of a save. The screen always sends the logo it shows: the link it was
    /// given (keep — whatever its version, so a screen opened before someone else replaced the logo
    /// does not undo them), a <c>data:</c> URL (a new picture), or nothing (remove). Any other link
    /// is refused. Answers the one English sentence for the 400, or null and the change.
    /// </summary>
    /// <param name="endpoint"><see cref="StoreEndpoint"/> or <see cref="BranchEndpoint"/>: the only link that keeps.</param>
    public static string? TryReadChange(string? requested, string endpoint, int maxBytes, out Change change)
    {
        change = new Change(ChangeKind.Remove);
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var value = requested.Trim();
        if (value == endpoint || value.StartsWith(endpoint + "?", StringComparison.Ordinal))
        {
            change = new Change(ChangeKind.Keep);
            return null;
        }
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return "The logo must be its current link, a new picture as a data URL, or nothing to remove it.";
        if (TryRead(value, maxBytes, out var file) is string problem) return problem;
        change = new Change(ChangeKind.Replace, file);
        return null;
    }

    /// <summary>
    /// Reads a <c>data:</c> URL into the picture it carries. Answers the one English sentence for
    /// the 400, or null and the file.
    /// </summary>
    public static string? TryRead(string dataUrl, int maxBytes, out LogoFile? file)
    {
        file = null;
        var match = DataUrl().Match(dataUrl.Trim());
        if (!match.Success)
            return "The logo must be a data URL: data:image/png, image/jpeg, image/webp or image/svg+xml, base64-encoded.";
        var type = match.Groups["type"].Value.ToLowerInvariant();
        if (!MediaTypes.Contains(type))
            return $"A logo of type '{type}' cannot be printed; use PNG, JPEG, WebP or SVG.";

        var base64 = Regex.Replace(match.Groups["data"].Value, @"\s+", "");
        // Bound the work before decoding: four base64 characters carry three bytes.
        if (base64.Length / 4L * 3 > maxBytes + 3)
            return $"The logo is limited to {maxBytes / 1024} KB.";
        var buffer = new byte[base64.Length / 4 * 3 + 3];
        if (base64.Length == 0 || !Convert.TryFromBase64String(base64, buffer, out var length))
            return "The logo's base64 data cannot be read.";
        if (length > maxBytes)
            return $"The logo is limited to {maxBytes / 1024} KB.";
        var bytes = buffer.AsSpan(0, length);

        if (ContentProblem(type, bytes) is string problem) return problem;

        file = new LogoFile(type, bytes.ToArray());
        return null;
    }

    private static string? ContentProblem(string type, ReadOnlySpan<byte> bytes)
    {
        switch (type)
        {
            case "image/png":
                return bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ? null : NotWhatItSays("PNG");
            case "image/jpeg":
                return bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) ? null : NotWhatItSays("JPEG");
            case "image/webp":
                return bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)
                    ? null
                    : NotWhatItSays("WebP");
            default:
                string svg;
                try { svg = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { return NotWhatItSays("SVG"); }
                if (svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) < 0) return NotWhatItSays("SVG");
                // Parsed and checked against what a drawing may contain (security review, 2026-10-10):
                // the text search it replaces missed a prefixed <svg:script> and encoded links.
                return SvgSafety.Problem(svg);
        }
    }

    private static string NotWhatItSays(string kind) => $"The logo's data is not a {kind} image.";
}
