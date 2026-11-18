using ParadisERP.Application.Files;
using ParadisERP.Infrastructure.Files;

namespace ParadisERP.Tests.Files;

/// <summary>
/// The files folder (owner, 2026-10-04): files are kept on disk with only their path in the
/// database, and the folder cannot be reached from outside the application — so no path a
/// request could send may name a file outside the company's own folder.
/// </summary>
public class LocalFileStoreTests
{
    [Fact]
    public async Task A_file_is_written_read_back_replaced_and_removed()
    {
        var files = TestFiles.Fresh();
        await files.WriteAsync("contracts/abc/def.pdf", [1, 2, 3]);
        Assert.Equal([1, 2, 3], await files.ReadAllAsync("contracts/abc/def.pdf"));

        await files.WriteAsync("contracts/abc/def.pdf", [4]);
        Assert.Equal([4], await files.ReadAllAsync("contracts/abc/def.pdf"));

        await files.DeleteAsync("contracts/abc/def.pdf");
        Assert.Null(await files.ReadAllAsync("contracts/abc/def.pdf"));
        // Removing what is not there is not an error.
        await files.DeleteAsync("contracts/abc/def.pdf");
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("logos/../../other/x.png")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("logos\\x.png")]
    [InlineData("Logos/X.png")]
    [InlineData("logos/x")]
    [InlineData("")]
    public async Task A_path_the_server_did_not_make_is_refused(string path)
    {
        var files = TestFiles.Fresh();
        await Assert.ThrowsAsync<ArgumentException>(() => files.WriteAsync(path, [1]));
        await Assert.ThrowsAsync<ArgumentException>(() => files.OpenReadAsync(path));
        await Assert.ThrowsAsync<ArgumentException>(() => files.DeleteAsync(path));
    }

    [Fact]
    public async Task Each_company_has_its_own_folder()
    {
        var root = TestFiles.NewRoot();
        var acme = LocalFileStore.ForCompany(root, "acme");
        var other = LocalFileStore.ForCompany(root, "other");
        await acme.WriteAsync("logos/a.png", [9]);

        Assert.Equal([9], await acme.ReadAllAsync("logos/a.png"));
        Assert.Null(await other.ReadAllAsync("logos/a.png"));
        Assert.True(File.Exists(Path.Combine(root, "acme", "logos", "a.png")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public void A_company_id_that_cannot_be_a_folder_name_is_refused(string id) =>
        Assert.Throws<ArgumentException>(() => LocalFileStore.ForCompany(TestFiles.NewRoot(), id));

    [Fact]
    public void The_paths_made_here_are_the_ones_the_store_takes()
    {
        var logo = FilePaths.Logo([1, 2, 3], "image/png");
        Assert.Matches("^logos/[0-9a-f]{64}\\.png$", logo);
        Assert.True(FilePaths.IsValid(logo));
        // Named by content: the same picture is the same path, another picture another path.
        Assert.Equal(logo, FilePaths.Logo([1, 2, 3], "image/png"));
        Assert.NotEqual(logo, FilePaths.Logo([1, 2, 4], "image/png"));

        var contract = Guid.NewGuid();
        var attachment = Guid.NewGuid();
        var scan = FilePaths.ContractAttachment(contract, attachment, "application/pdf");
        Assert.Equal($"contracts/{contract:N}/{attachment:N}.pdf", scan);
        Assert.True(FilePaths.IsValid(scan));
        Assert.Equal("application/pdf", FilePaths.MediaType(scan));
    }
}
