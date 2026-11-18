using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlgeriaPos.Domain;
using AlgeriaPos.Tests.Catalog;
using AlgeriaPos.Tests.Hosting;
using static AlgeriaPos.Tests.Catalog.TestWorkbook;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// FR-3B over HTTP: the preview of a terminal's export takes the attendance sheet's two
/// permissions and no other, a manager previews their own branch whatever branch they name, and
/// a file that is not a workbook is refused in words.
/// </summary>
[Collection(HostCollection.Name)]
public class PunchImportRouteTests
{
    private const string Password = "Essai@2026";

    private static async Task<HttpClient> SignedInAsync(PosHost host, string username, string password, string? device)
    {
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = host.SignInName(username), password, deviceCode = device });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task CreateUserAsync(PosHost host, HttpClient admin, string username, UserRole role, Guid branchId, params string[] permissions)
    {
        // An employee first, then their account (2026-10-05); the host remembers its Login ID by name.
        await TestUsers.CreateAsync(host, admin, username, role.ToString(), branchId, Password, permissions: permissions);

    }

    private static async Task HireAsync(HttpClient admin, string name, Guid branchId, string fingerprint) =>
        await IdOfAsync(await admin.PostAsJsonAsync("/api/hr/employees", new
        {
            fullName = name, branchId, payBasis = "Monthly", baseRate = 40_000m, payoutMethod = "Cash",
            hiredOn = "2024-01-01", fingerprintNumber = fingerprint,
        }));

    private static MultipartFormDataContent Upload(byte[] file, string fileName = "pointage.xlsx")
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(content, "file", fileName);
        foreach (var (key, value) in new[] { ("number", "1"), ("name", "2"), ("date", "3"), ("time", "4") })
            form.Add(new StringContent(value), key);
        return form;
    }

    private static readonly byte[] Punches = Build(new TestWorkbook.Sheet("Sept", [
        new Row(1, ("A", "No."), ("B", "Nom"), ("C", "Date"), ("D", "Heure")),
        new Row(2, ("A", 7), ("B", "A"), ("C", "01/09/2026"), ("D", "08:00")),
        new Row(3, ("A", 12), ("B", "K"), ("C", "01/09/2026"), ("D", "08:05")),
    ]));

    [Fact]
    public async Task The_attendance_permissions_open_it_a_manager_reads_their_own_branch_and_nothing_else_does()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, "C01");

        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var main = branches.EnumerateArray().Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean()).GetProperty("id").GetGuid();
        var oran = await IdOfAsync(await admin.PostAsJsonAsync("/api/catalog/branches", new
        {
            nameAr = "وهران", nameFr = "Oran", nameEn = "Oran", address = "", phone = "",
            isMain = false, allowNegativeStock = false, isActive = true,
        }));
        await HireAsync(admin, "Essai Alger", main, "7");
        await HireAsync(admin, "Essai Oran", oran, "12");

        await CreateUserAsync(host, admin, "pointage-oran", UserRole.Manager, oran, Permissions.EnterAttendance);
        await CreateUserAsync(host, admin, "gerant-sans", UserRole.Manager, oran);
        // A cashier works at a selling branch, not the main warehouse (2026-10-05).
        await CreateUserAsync(host, admin, "caisse-x", UserRole.Cashier, oran, Permissions.CashBox);

        // The Admin previews the branch named.
        var asAdmin = await admin.PostAsync($"/api/hr/attendance/import/preview?branchId={main}", Upload(Punches));
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
        var adminView = await asAdmin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Essai Alger", adminView.GetProperty("employees")[0].GetProperty("employeeName").GetString());

        // The foreman of Oran names Algiers and is shown Oran: their own branch, set on the server.
        // Oran's managers open no Algiers till (security review, 2026-10-10): office sessions.
        using var foreman = await SignedInAsync(host, "pointage-oran", Password, null);
        var asForeman = await foreman.PostAsync($"/api/hr/attendance/import/preview?branchId={main}", Upload(Punches));
        Assert.Equal(HttpStatusCode.OK, asForeman.StatusCode);
        var foremanView = await asForeman.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Essai Oran", foremanView.GetProperty("employees")[0].GetProperty("employeeName").GetString());
        // Algiers' employee is a number from elsewhere, not a name.
        Assert.Equal("7", foremanView.GetProperty("otherBranchNumbers")[0].GetProperty("number").GetString());
        Assert.DoesNotContain("Essai Alger", await asForeman.Content.ReadAsStringAsync());

        // Neither permission: refused. A cashier: refused.
        using var plain = await SignedInAsync(host, "gerant-sans", Password, null);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await plain.PostAsync($"/api/hr/attendance/import/preview?branchId={oran}", Upload(Punches))).StatusCode);
        using var cashier = await SignedInAsync(host, "caisse-x", Password, null);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await cashier.PostAsync($"/api/hr/attendance/import/preview?branchId={main}", Upload(Punches))).StatusCode);

        // A file that is not a workbook is said to be one, in words.
        var notExcel = await admin.PostAsync($"/api/hr/attendance/import/preview?branchId={main}",
            Upload("AC-No.;Date\n7;01/09/2026"u8.ToArray(), "pointage.csv"));
        Assert.Equal(HttpStatusCode.BadRequest, notExcel.StatusCode);
        Assert.Contains("not an Excel workbook", await notExcel.Content.ReadAsStringAsync());

        // Previewing wrote nothing: the month's clock log is still empty.
        var clock = await admin.GetFromJsonAsync<JsonElement>($"/api/hr/attendance/clock?branchId={main}&year=2026&month=9");
        Assert.Equal(0, clock.GetProperty("events").GetArrayLength());
    }
}
