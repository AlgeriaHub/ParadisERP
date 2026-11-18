using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ParadisERP.Application.Catalog;
using ParadisERP.Application.Files;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Catalog;

/// <summary>
/// The company logo on the printed documents (owner, 2026-09-30, answer 3). It arrives on the
/// store documents PUT as a data URL: four image types, base64, at most 512 KB decoded, bytes that
/// are what the type says, and an SVG with nothing in it that could run. It is kept in the files
/// folder with only its path in the database, and read back through a signed-in endpoint
/// (owner, 2026-10-04). Its link sent back keeps it, null removes it, and saving it spends none of
/// the yearly wording allowance.
/// </summary>
[Collection(HostCollection.Name)]
public class StoreLogoTests
{
    private static readonly byte[] PngHead = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] PngBytes(int extraBytes = 16) => PngHead.Concat(new byte[extraBytes]).ToArray();

    private static string Png(int extraBytes = 16) => "data:image/png;base64," + Convert.ToBase64String(PngBytes(extraBytes));

    private static string Svg(string body) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(body));

    private const string PlainSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><path d=\"M0 0 L10 10\" stroke=\"#000\"/></svg>";

    private static string? Change(string? value, out StoreLogo.Change change) =>
        StoreLogo.TryReadChange(value, StoreLogo.StoreEndpoint, StoreLogo.MaxBytes, out change);

    // ---------- the rule, pure ----------

    [Fact]
    public void Blank_removes_the_logo()
    {
        Assert.Null(Change(null, out var a));
        Assert.Equal(StoreLogo.ChangeKind.Remove, a.Kind);
        Assert.Null(Change("   ", out var b));
        Assert.Equal(StoreLogo.ChangeKind.Remove, b.Kind);
    }

    [Fact]
    public void Its_own_link_keeps_the_logo_whatever_the_version_and_any_other_link_is_refused()
    {
        Assert.Null(Change(StoreLogo.StoreUrl("logos/a.png"), out var current));
        Assert.Equal(StoreLogo.ChangeKind.Keep, current.Kind);
        // A screen opened before someone else replaced the logo carries the older version.
        Assert.Null(Change(StoreLogo.StoreEndpoint + "?v=000000000000", out var older));
        Assert.Equal(StoreLogo.ChangeKind.Keep, older.Kind);

        Assert.NotNull(Change(StoreLogo.BranchUrl(Guid.NewGuid(), "logos/a.png"), out _));
        Assert.NotNull(Change("/api/catalog/store/logo-other", out _));
        Assert.NotNull(Change("https://example.com/logo.png", out _));
    }

    [Fact]
    public void A_png_jpeg_webp_or_plain_svg_replaces_it_as_a_file_named_by_its_content()
    {
        Assert.Null(Change(Png(), out var png));
        Assert.Equal(StoreLogo.ChangeKind.Replace, png.Kind);
        Assert.Equal("image/png", png.File!.MediaType);
        Assert.Equal(PngBytes(), png.File.Content);
        Assert.Equal(FilePaths.Logo(PngBytes(), "image/png"), png.File.Path);

        Assert.Null(Change("DATA:IMAGE/JPEG;base64," + Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]), out var jpeg));
        Assert.Equal("image/jpeg", jpeg.File!.MediaType);
        Assert.EndsWith(".jpg", jpeg.File.Path);

        var webp = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");
        Assert.Null(Change("data:image/webp;base64," + Convert.ToBase64String(webp), out _));

        Assert.Null(Change(Svg(PlainSvg), out var svg));
        Assert.Equal(PlainSvg, Encoding.UTF8.GetString(svg.File!.Content));
        Assert.EndsWith(".svg", svg.File.Path);
    }

    [Theory]
    [InlineData("data:image/gif;base64,R0lGODlhAQABAAAAACw=")]
    [InlineData("data:text/html;base64,PGgxPmhpPC9oMT4=")]
    [InlineData("data:image/png,not-base64")]
    [InlineData("data:image/png;base64,@@@@")]
    public void Anything_but_a_base64_data_url_of_the_four_types_is_refused(string value)
    {
        Assert.NotNull(StoreLogo.TryRead(value, StoreLogo.MaxBytes, out var file));
        Assert.Null(file);
    }

    [Fact]
    public void Bytes_that_are_not_what_the_type_says_are_refused()
    {
        // A JPEG labelled PNG, and text labelled SVG.
        Assert.Contains("PNG", StoreLogo.TryRead("data:image/png;base64," + Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0]), StoreLogo.MaxBytes, out _));
        Assert.Contains("SVG", StoreLogo.TryRead(Svg("hello"), StoreLogo.MaxBytes, out _));
    }

    [Fact]
    public void Over_512_KB_decoded_is_refused_and_exactly_512_KB_is_taken()
    {
        Assert.Null(StoreLogo.TryRead(Png(StoreLogo.MaxBytes - PngHead.Length), StoreLogo.MaxBytes, out _));
        Assert.Contains("512 KB", StoreLogo.TryRead(Png(StoreLogo.MaxBytes - PngHead.Length + 1), StoreLogo.MaxBytes, out _));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect ONCLICK = \"x()\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"javascript:alert(1)\"><rect/></a></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject><div/></foreignObject></svg>")]
    // What the old text search let through (security review, 2026-10-10).
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><svg:script xmlns:svg=\"http://www.w3.org/2000/svg\">alert(1)</svg:script></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"java&#x73;cript:alert(1)\"><rect/></a></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect><set attributeName=\"fill\" to=\"red\"/></rect></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://example.com/beacon.png\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect style=\"fill: url( 'https://example.com/x' )\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>@import 'https://example.com/a.css';</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><html:p xmlns:html=\"http://www.w3.org/1999/xhtml\">x</html:p></svg>")]
    public void An_svg_that_could_run_anything_is_refused(string body)
    {
        Assert.Contains("script", StoreLogo.TryRead(Svg(body), StoreLogo.MaxBytes, out var file));
        Assert.Null(file);
    }

    [Fact]
    public void An_editor_s_export_with_its_own_metadata_gradients_and_inner_links_is_a_plain_drawing()
    {
        const string inkscape =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
            "xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" xmlns:sodipodi=\"http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd\" viewBox=\"0 0 10 10\">" +
            "<sodipodi:namedview inkscape:zoom=\"1\"/>" +
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#0f766e\"/></linearGradient></defs>" +
            "<g inkscape:label=\"Calque 1\"><rect width=\"10\" height=\"10\" style=\"fill:url(#g)\"/>" +
            "<use xlink:href=\"#g\"/><text x=\"1\" y=\"8\">SARL</text></g></svg>";
        Assert.Null(StoreLogo.TryRead(Svg(inkscape), StoreLogo.MaxBytes, out var file));
        Assert.NotNull(file);
    }

    // ---------- the endpoint ----------

    private static async Task<HttpClient> SignedInAsync(PosHost host)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            username = PosHost.BootstrapUsername, password = PosHost.BootstrapPassword, deviceCode = "C01",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static object Documents(string? logo) => new
    {
        documentTexts = new Dictionary<string, Dictionary<string, string>>(),
        email = (string?)null, website = (string?)null, bankDetails = (string?)null,
        changesPerYear = 3, logoUrl = logo,
    };

    private static async Task<string?> StoredLinkAsync(HttpClient client)
    {
        var dto = await client.GetFromJsonAsync<JsonElement>("/api/catalog/store/documents");
        var logo = dto.GetProperty("logoUrl");
        return logo.ValueKind == JsonValueKind.Null ? null : logo.GetString();
    }

    private static async Task<string?> StoredPathAsync(PosHost host)
    {
        await using var db = new PosDbContext(new DbContextOptionsBuilder<PosDbContext>().UseSqlite(host.ConnectionString).Options);
        return await db.Stores.Where(s => !s.IsDeleted).Select(s => s.LogoPath).SingleAsync();
    }

    private static string CompanyFile(PosHost host, string path) =>
        Path.Combine(host.FilesRoot, "default", path.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task The_logo_is_kept_in_the_files_folder_and_read_back_only_by_a_signed_in_user()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);

        var saved = await client.PutAsJsonAsync("/api/catalog/store/documents", Documents(Png()));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var link = (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("logoUrl").GetString();
        Assert.StartsWith(StoreLogo.StoreEndpoint + "?v=", link);
        Assert.Equal(link, await StoredLinkAsync(client));

        // The row holds the path, never the picture; the picture is on disk under the company's folder.
        var path = await StoredPathAsync(host);
        Assert.Equal(FilePaths.Logo(PngBytes(), "image/png"), path);
        Assert.Equal(PngBytes(), await File.ReadAllBytesAsync(CompanyFile(host, path!)));

        var read = await client.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("image/png", read.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PngBytes(), await read.Content.ReadAsByteArrayAsync());
        Assert.Contains("private", read.Headers.CacheControl?.ToString());

        using var stranger = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync(link)).StatusCode);
        // Nothing maps a URL onto the folder itself.
        var direct = await stranger.GetAsync("/files/default/" + path);
        Assert.NotEqual("image/png", direct.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Its_link_keeps_it_a_new_picture_replaces_it_and_null_removes_it_and_its_file()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/catalog/store/documents", Documents(Svg(PlainSvg)))).StatusCode);
        var link = await StoredLinkAsync(client);
        var first = await StoredPathAsync(host);

        var kept = await client.PutAsJsonAsync("/api/catalog/store/documents", Documents(link));
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(link, await StoredLinkAsync(client));

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/catalog/store/documents", Documents(Png()))).StatusCode);
        var replaced = await StoredLinkAsync(client);
        Assert.NotEqual(link, replaced);
        Assert.False(File.Exists(CompanyFile(host, first!)), "the replaced picture is no longer kept");

        var second = await StoredPathAsync(host);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/catalog/store/documents", Documents(null))).StatusCode);
        Assert.Null(await StoredLinkAsync(client));
        Assert.Null(await StoredPathAsync(host));
        Assert.False(File.Exists(CompanyFile(host, second!)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(StoreLogo.StoreEndpoint)).StatusCode);
    }

    [Fact]
    public async Task The_documents_route_refuses_a_bad_logo_in_words_and_keeps_the_stored_one()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/catalog/store/documents", Documents(Png()))).StatusCode);
        var link = await StoredLinkAsync(client);

        foreach (var bad in new[]
                 {
                     Png(StoreLogo.MaxBytes),
                     Svg("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>"),
                     "data:image/gif;base64,R0lGODlhAQABAAAAACw=",
                     "https://example.com/logo.png",
                 })
        {
            var refused = await client.PutAsJsonAsync("/api/catalog/store/documents", Documents(bad));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("logo", (await refused.Content.ReadAsStringAsync()).ToLowerInvariant());
        }
        Assert.Equal(link, await StoredLinkAsync(client));
    }
}
