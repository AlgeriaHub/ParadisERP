namespace ParadiERP.Application.Files;

/// <summary>
/// The company's uploaded files — logos and the papers kept with a contract — held in a folder
/// outside the database (the owner, 2026-10-04: "files and images must be uploaded in a folder
/// and only the path stored in the database"). A row keeps the relative path
/// <see cref="FilePaths"/> made for it, never the bytes.
///
/// <para>
/// The folder is never served. It sits outside <c>wwwroot</c>, nothing maps a URL onto it, and a
/// file leaves only through an endpoint that has checked who is asking. Paths are made by the
/// server and refused here unless they have the shape <see cref="FilePaths"/> gives them, so
/// nothing a request sends can name a file outside the company's own folder.
/// </para>
/// </summary>
public interface IFileStore
{
    /// <summary>Writes the file, replacing one already at that path.</summary>
    Task WriteAsync(string path, byte[] content, CancellationToken ct = default);

    /// <summary>The file for reading, or null when nothing is at that path. The caller disposes it.</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>Removes the file. A path with nothing at it is not an error.</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);
}
