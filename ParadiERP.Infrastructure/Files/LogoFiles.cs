using ParadiERP.Application.Files;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Files;

/// <summary>Housekeeping for logo files, which are named by their content and may be shared.</summary>
public static class LogoFiles
{
    /// <summary>
    /// Deletes a logo that was just replaced or removed, once no row points at it: the company and
    /// a branch may carry the same picture, and then it is one file. Called after the commit, so a
    /// rolled-back save never loses the file its row still names.
    /// </summary>
    public static async Task ReleaseAsync(PosDbContext db, IFileStore files, string? path, CancellationToken ct = default)
    {
        if (path is null) return;
        if (await db.Stores.AnyAsync(s => s.LogoPath == path, ct)) return;
        if (await db.Branches.AnyAsync(b => b.LogoPath == path, ct)) return;
        await files.DeleteAsync(path, ct);
    }
}
