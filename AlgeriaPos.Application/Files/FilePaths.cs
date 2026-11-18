using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AlgeriaPos.Application.Files;

/// <summary>
/// The relative paths files are kept at, made here and nowhere else. Lower-case letters, digits
/// and dashes, segments joined by <c>/</c>, one extension — so a stored path can never climb out
/// of the company's folder or name something the server did not write.
/// </summary>
public static partial class FilePaths
{
    public const int MaxLength = 200;

    [GeneratedRegex("^[a-z0-9-]+(/[a-z0-9-]+)*\\.[a-z0-9]+$")]
    private static partial Regex Shape();

    public static bool IsValid(string? path) =>
        path is { Length: > 0 and <= MaxLength } && Shape().IsMatch(path);

    /// <summary>
    /// A logo, named by its content: the same picture saved twice is one file, and a new picture
    /// is a new path, so its link changes and no copy cached on a till is ever stale.
    /// </summary>
    public static string Logo(byte[] content, string mediaType) =>
        $"logos/{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}{Extension(mediaType)}";

    /// <summary>A file kept with a rental contract, in the contract's own folder.</summary>
    public static string ContractAttachment(Guid contractId, Guid attachmentId, string mediaType) =>
        $"contracts/{contractId:N}/{attachmentId:N}{Extension(mediaType)}";

    /// <summary>A copy of a customer's identity document, in the customer's own folder.</summary>
    public static string CustomerAttachment(Guid customerId, Guid attachmentId, string mediaType) =>
        $"customers/{customerId:N}/{attachmentId:N}{Extension(mediaType)}";

    /// <summary>A user's own photo, in the user's folder, named by its content like a logo.</summary>
    public static string UserPhoto(Guid userId, byte[] content, string mediaType) =>
        $"users/{userId:N}/{Convert.ToHexString(SHA256.HashData(content))[..32].ToLowerInvariant()}{Extension(mediaType)}";

    /// <summary>
    /// The short tag a link carries, so a link changes whenever the path it reads does. Never the
    /// path itself: where the folder keeps a file is the server's business.
    /// </summary>
    public static string Version(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12].ToLowerInvariant();

    /// <summary>The media type a stored file is served as, from the extension its path was given.</summary>
    public static string MediaType(string path) => Path.GetExtension(path) switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };

    private static string Extension(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "application/pdf" => ".pdf",
        _ => throw new ArgumentException($"No file is kept of type '{mediaType}'.", nameof(mediaType))
    };
}
