using ParadisERP.Application.Files;

namespace ParadisERP.API.Endpoints;

/// <summary>How a file from the files folder leaves the application.</summary>
internal static class FileResults
{
    /// <summary>
    /// A logo, or 404 when there is none. The link carries the file's version
    /// (<c>StoreLogo.StoreUrl</c>), so the caller may keep its copy as long as it likes — privately:
    /// never in a shared cache.
    /// </summary>
    public static async Task<IResult> LogoAsync(IFileStore files, string? path, HttpContext http, CancellationToken ct)
    {
        if (path is null) return Results.NotFound();
        var content = await files.OpenReadAsync(path, ct);
        if (content is null) return Results.NotFound();
        http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        Harden(http);
        return Results.File(content, FilePaths.MediaType(path));
    }

    /// <summary>
    /// Its own type, never guessed again by the browser; and should the file ever be opened on its
    /// own, an SVG stays a picture: no script, no plugin, no request of its own.
    /// </summary>
    public static void Harden(HttpContext http)
    {
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
    }
}
