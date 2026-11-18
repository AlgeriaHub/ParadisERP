using ParadisERP.Application.Files;
using ParadisERP.Infrastructure.Files;

namespace ParadisERP.Tests.Files;

/// <summary>Files folders for tests: each one new and empty, under the temp directory.</summary>
internal static class TestFiles
{
    public static string NewRoot() => Path.Combine(Path.GetTempPath(), "algeriapos-files", Guid.NewGuid().ToString("n"));

    public static LocalFileStore Fresh() => LocalFileStore.ForCompany(NewRoot(), "default");

    /// <summary>The whole file, or null when nothing is at that path.</summary>
    public static async Task<byte[]?> ReadAllAsync(this IFileStore files, string path)
    {
        await using var stream = await files.OpenReadAsync(path);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }
}
