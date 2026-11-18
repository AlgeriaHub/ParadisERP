using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Application.Catalog;
using ParadiERP.Application.Files;
using ParadiERP.Domain;
using ParadiERP.Tests.Files;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Catalog;

/// <summary>
/// A branch's own logo in the top bar (the owner, 2026-09-30: "the branch name appears with its
/// logo; if the branch has no logo, take the main branch's logo"). Read like the company's logo
/// but capped at 256 KB, because every till keeps a copy. Kept in the files folder, the row
/// holding its path and the screens its signed-in link (owner, 2026-10-04); the link sent back
/// keeps it, null removes it.
/// </summary>
public class BranchLogoTests
{
    private static readonly byte[] PngHead = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] PngBytes(int bytes) => PngHead.Concat(new byte[bytes - PngHead.Length]).ToArray();

    private static string Png(int bytes) => "data:image/png;base64," + Convert.ToBase64String(PngBytes(bytes));

    // A second location in one test takes a suffix: two locations never share a name (2026-10-06).
    private static string Body(string? logo, string suffix = "") => JsonSerializer.Serialize(new
    {
        nameAr = "سكيكدة" + suffix, nameFr = "Skikda" + suffix, nameEn = "Skikda" + suffix, address = "Skikda", phone = "",
        isMain = false, allowNegativeStock = false, isActive = true, logoUrl = logo,
    });

    [Fact]
    public void A_branch_logo_is_capped_at_256_KB_where_the_company_s_takes_512()
    {
        Assert.Null(StoreLogo.TryRead(Png(200 * 1024), StoreLogo.BranchMaxBytes, out var fits));
        Assert.NotNull(fits);

        var refusal = StoreLogo.TryRead(Png(300 * 1024), StoreLogo.BranchMaxBytes, out var none);
        Assert.Equal("The logo is limited to 256 KB.", refusal);
        Assert.Null(none);

        // The company's own cap is unchanged.
        Assert.Null(StoreLogo.TryRead(Png(300 * 1024), StoreLogo.MaxBytes, out _));
    }

    [Fact]
    public async Task A_branch_s_logo_is_kept_as_a_file_kept_by_its_link_removed_and_refused_in_words()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var files = TestFiles.Fresh();
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers);
        var logo = PngBytes(1024);

        var (created, answer) = await BranchLockTests.InvokeAsync(db, files, app => app.MapBranchEndpoints(), "POST",
            "/api/catalog/branches", admin, Body(Png(1024)));
        Assert.Equal(201, created);
        var row = JsonDocument.Parse(answer).RootElement;
        var id = row.GetProperty("id").GetGuid();
        var path = (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == id)).LogoPath;
        Assert.Equal(FilePaths.Logo(logo, "image/png"), path);
        Assert.Equal(logo, await files.ReadAllAsync(path!));
        // The screen gets the link, never the path or the picture.
        var link = row.GetProperty("logoUrl").GetString();
        Assert.Equal(StoreLogo.BranchUrl(id, path), link);
        Assert.False(row.TryGetProperty("logoPath", out JsonElement unexpected), "the path stays on the server: " + unexpected);

        var (kept, _) = await BranchLockTests.InvokeAsync(db, files, app => app.MapBranchEndpoints(), "PUT",
            "/api/catalog/branches/{id:guid}", admin, Body(link), ("id", id.ToString()));
        Assert.Equal(200, kept);
        Assert.Equal(path, (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == id)).LogoPath);

        var (tooBig, why) = await BranchLockTests.InvokeAsync(db, files, app => app.MapBranchEndpoints(), "PUT",
            "/api/catalog/branches/{id:guid}", admin, Body(Png(300 * 1024)), ("id", id.ToString()));
        Assert.Equal(400, tooBig);
        Assert.Contains("256 KB", why);
        Assert.Equal(path, (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == id)).LogoPath);

        var (renamed, _) = await BranchLockTests.InvokeAsync(db, files, app => app.MapBranchEndpoints(), "PUT",
            "/api/catalog/branches/{id:guid}", admin, Body("data:image/png;base64," + Convert.ToBase64String([0xFF, 0xD8, 0xFF])),
            ("id", id.ToString()));
        Assert.Equal(400, renamed);

        // Another branch's link is not this branch's logo.
        var (foreign, _) = await BranchLockTests.InvokeAsync(db, files, app => app.MapBranchEndpoints(), "PUT",
            "/api/catalog/branches/{id:guid}", admin, Body(StoreLogo.BranchUrl(TillHarness.Algiers, path)), ("id", id.ToString()));
        Assert.Equal(400, foreign);

        var (cleared, _) = await BranchLockTests.InvokeAsync(db, files, app => app.MapBranchEndpoints(), "PUT",
            "/api/catalog/branches/{id:guid}", admin, Body(null), ("id", id.ToString()));
        Assert.Equal(200, cleared);
        Assert.Null((await db.Branches.AsNoTracking().SingleAsync(b => b.Id == id)).LogoPath);
        Assert.Null(await files.ReadAllAsync(path!));
    }

    [Fact]
    public async Task Only_the_admin_changes_a_branch_s_logo_a_manager_s_save_leaves_it_as_it_is()
    {
        // The owner, 2026-09-30: "the branch logo is edited by the super admin".
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var files = TestFiles.Fresh();
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers);
        var (created, answer) = await BranchLockTests.InvokeAsync(db, files, app => app.MapBranchEndpoints(), "POST",
            "/api/catalog/branches", admin, Body(Png(1024)));
        Assert.Equal(201, created);
        var id = JsonDocument.Parse(answer).RootElement.GetProperty("id").GetGuid();
        var path = (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == id)).LogoPath;
        Assert.NotNull(path);
        // The branch's own manager: since the security review (2026-10-10) a manager edits only theirs.
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, id);

        // Another logo, then none: both saves go through for the rest of the row and leave the logo.
        foreach (var attempt in new[] { Png(2048), null })
        {
            var (saved, _) = await BranchLockTests.InvokeAsync(db, files, app => app.MapBranchEndpoints(), "PUT",
                "/api/catalog/branches/{id:guid}", manager, Body(attempt), ("id", id.ToString()));
            Assert.Equal(200, saved);
            Assert.Equal(path, (await db.Branches.AsNoTracking().SingleAsync(b => b.Id == id)).LogoPath);
        }
        Assert.NotNull(await files.ReadAllAsync(path!));

        // And opens no branch at all: that is the company's decision (security review, 2026-10-10).
        var (made, _) = await BranchLockTests.InvokeAsync(db, files, app => app.MapBranchEndpoints(), "POST",
            "/api/catalog/branches", manager, Body(Png(1024), " 2"));
        Assert.Equal(403, made);
    }

    [Fact]
    public async Task The_logo_s_link_reaches_the_till_with_the_branch_row()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var main = await db.Branches.SingleAsync(b => b.Id == TillHarness.Algiers);
        main.LogoPath = FilePaths.Logo(PngBytes(512), "image/png");
        await db.SaveWithVersionsAsync(default, main);

        var pull = await TillHarness.NewService(db).PullAsync(0, "C02", CancellationToken.None);

        Assert.Equal(StoreLogo.BranchUrl(TillHarness.Algiers, main.LogoPath),
            pull.Branches.Single(b => b.Id == TillHarness.Algiers).LogoUrl);
        Assert.DoesNotContain(pull.Branches, b => b.Id == TillHarness.Oran && b.LogoUrl != null);
    }
}
