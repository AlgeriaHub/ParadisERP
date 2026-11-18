using ParadisERP.Application.Files;

namespace ParadisERP.Infrastructure.Files;

/// <summary>
/// Where the files folder is: <c>Files:Root</c>, absolute or relative to the content root. The
/// default, <c>App_Data/files</c>, is outside <c>wwwroot</c>, and IIS refuses to serve
/// <c>App_Data</c> even to a request that reaches it; the container points it at its volume
/// (<c>/data/files</c>) so the files outlive the image.
/// </summary>
public sealed class FileStoreOptions
{
    public const string DefaultRoot = "App_Data/files";

    public string Root { get; init; } = DefaultRoot;
}

/// <summary>
/// <see cref="IFileStore"/> on the local disk, one folder per company under the root, so one
/// company's paths can never reach another's files.
/// </summary>
public sealed class LocalFileStore : IFileStore
{
    private readonly string folder;

    public LocalFileStore(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("The files folder is not set.", nameof(folder));
        this.folder = Path.GetFullPath(folder);
    }

    public static LocalFileStore ForCompany(string root, string companyId)
    {
        // A company id is trusted configuration, but it becomes a folder name, so hold it to the
        // same shape as a path segment rather than trust it blindly.
        var segment = (companyId ?? "").Trim().ToLowerInvariant();
        if (segment.Length == 0 || segment.Contains('/') || !FilePaths.IsValid(segment + ".d"))
            throw new ArgumentException($"Company id '{companyId}' cannot name a files folder.", nameof(companyId));
        return new LocalFileStore(Path.Combine(root, segment));
    }

    public async Task WriteAsync(string path, byte[] content, CancellationToken ct = default)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Write beside it and move into place, so a reader never sees half a file and a failed
        // write never leaves one.
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temp, content, ct);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var full = Resolve(path);
        Stream? stream = File.Exists(full)
            ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var full = Resolve(path);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    private string Resolve(string path)
    {
        if (!FilePaths.IsValid(path)) throw new ArgumentException($"'{path}' is not a stored file path.", nameof(path));
        var full = Path.GetFullPath(Path.Combine(folder, path));
        // The shape check already rules out "..", a root or a drive; this is the belt to its braces.
        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"'{path}' is outside the files folder.", nameof(path));
        return full;
    }
}
