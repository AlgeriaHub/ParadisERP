using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Tests.Hosting;

namespace AlgeriaPos.Tests.Customers;

/// <summary>
/// The copy of a customer's identity document (owner, 2026-10-06: attached when the customer is
/// added, optional, and added later from the edit when it was not), through the real HTTP
/// pipeline: multipart in, the file in the files folder and never in the database, streamed back
/// with its own sniffed type, and taken off again.
/// </summary>
[Collection(HostCollection.Name)]
public class CustomerAttachmentTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];
    private static readonly byte[] Pdf = "%PDF-1.4\n1 0 obj\n"u8.ToArray();

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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CustomerAsync(HttpClient client) =>
        (await JsonAsync(await client.PostAsJsonAsync("/api/catalog/customers", new
        {
            fullName = "", firstName = "Karim", familyName = "Haddad", creditLimit = 0m, kind = "Person",
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static MultipartFormDataContent Upload(byte[] content, string name) =>
        new() { { new ByteArrayContent(content), "file", name } };

    [Fact]
    public async Task A_scan_is_kept_in_the_files_folder_listed_read_back_and_taken_off()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var customer = await CustomerAsync(client);
        var route = $"/api/catalog/customers/{customer}/attachments";

        // A customer added without one: nothing kept, and that is not an error.
        Assert.Equal(0, (await JsonAsync(await client.GetAsync(route), HttpStatusCode.OK)).GetArrayLength());

        using var front = Upload(Jpeg, "carte-recto.jpg");
        var kept = await JsonAsync(await client.PostAsync(route, front), HttpStatusCode.Created);
        Assert.Equal("image/jpeg", kept.GetProperty("contentType").GetString());
        Assert.Equal("idDocument", kept.GetProperty("kind").GetString());
        Assert.Equal(Jpeg.Length, kept.GetProperty("length").GetInt64());
        var frontId = kept.GetProperty("id").GetGuid();

        using var back = Upload(Pdf, "carte-verso.pdf");
        await JsonAsync(await client.PostAsync(route, back), HttpStatusCode.Created);

        var listed = await JsonAsync(await client.GetAsync(route), HttpStatusCode.OK);
        Assert.Equal(["carte-recto.jpg", "carte-verso.pdf"],
            listed.EnumerateArray().Select(a => a.GetProperty("fileName").GetString()).ToArray());

        // The bytes are on disk, in the customer's own folder; the type is the one sniffed.
        var onDisk = Directory.GetFiles(host.FilesRoot, $"{frontId:N}.jpg", SearchOption.AllDirectories);
        Assert.Single(onDisk);
        Assert.Contains($"customers{Path.DirectorySeparatorChar}{customer:N}", onDisk[0]);
        var file = await client.GetAsync($"{route}/{frontId}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Jpeg, await file.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{route}/{frontId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{route}/{frontId}")).StatusCode);
        Assert.Empty(Directory.GetFiles(host.FilesRoot, $"{frontId:N}.jpg", SearchOption.AllDirectories));
        Assert.Equal(1, (await JsonAsync(await client.GetAsync(route), HttpStatusCode.OK)).GetArrayLength());
    }

    [Fact]
    public async Task Only_an_image_or_a_pdf_is_kept_whatever_its_name_says()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var customer = await CustomerAsync(client);
        var route = $"/api/catalog/customers/{customer}/attachments";

        using var text = Upload("hello"u8.ToArray(), "carte.jpg");
        var refused = await client.PostAsync(route, text);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("JPEG, PNG or WebP", await refused.Content.ReadAsStringAsync());

        using var none = new MultipartFormDataContent { { new StringContent("x"), "note" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(route, none)).StatusCode);

        Assert.Equal(0, (await JsonAsync(await client.GetAsync(route), HttpStatusCode.OK)).GetArrayLength());
    }

    [Fact]
    public async Task A_deleted_customer_keeps_nothing_new_and_shows_nothing()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var customer = await CustomerAsync(client);
        var route = $"/api/catalog/customers/{customer}/attachments";
        using var front = Upload(Jpeg, "carte.jpg");
        var id = (await JsonAsync(await client.PostAsync(route, front), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/catalog/customers/{customer}")).StatusCode);

        using var again = Upload(Jpeg, "carte.jpg");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(route, again)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{route}/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_customer_keeps_at_most_ten_files()
    {
        using var host = new PosHost();
        using var client = await SignedInAsync(host);
        var customer = await CustomerAsync(client);
        var route = $"/api/catalog/customers/{customer}/attachments";
        for (var i = 0; i < 10; i++)
        {
            using var each = Upload(Jpeg, $"page-{i}.jpg");
            await JsonAsync(await client.PostAsync(route, each), HttpStatusCode.Created);
        }

        using var eleventh = Upload(Jpeg, "page-10.jpg");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(route, eleventh)).StatusCode);
    }
}
