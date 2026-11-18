using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadisERP.Domain;
using ParadisERP.Tests.Hosting;

namespace ParadisERP.Tests.Security;

/// <summary>
/// FR-2A (the owner, 2026-10-03): a cashier who holds the cash box reads and acts on their own
/// branch's payment vouchers only — the branch of the till they signed in at. Typing another
/// branch's voucher id reads, pays and cancels nothing: the server answers 404, as it does for
/// another branch's sale, so it does not even say the voucher exists. Since P0.1b (owner,
/// 2026-10-25) a branch manager is held to their own branch the same way; the admin reads every one.
/// </summary>
[Collection(HostCollection.Name)]
public class PaymentVoucherBranchScopeTests
{
    private const string Password = "Essai@2026";

    private static async Task<HttpClient> SignedInAsync(PosHost host, string username, string password, string device)
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
        var root = body.RootElement;
        return (root.TryGetProperty("voucher", out var voucher) ? voucher : root).GetProperty("id").GetGuid();
    }

    private static async Task CreateUserAsync(PosHost host, HttpClient admin, string username, UserRole role, Guid branchId, params string[] permissions)
    {
        // An employee first, then their account (2026-10-05); the host remembers its Login ID by name.
        await TestUsers.CreateAsync(host, admin, username, role.ToString(), branchId, Password, permissions: permissions);

    }

    /// <summary>An expense voucher raised by the admin at a branch; pending until approved.</summary>
    private static Task<Guid> RaiseAsync(HttpClient admin, Guid branchId, decimal amount) =>
        admin.PostAsJsonAsync("/api/payment-vouchers", new
        {
            branchId, kind = (int)PaymentVoucherKind.Expense, amount, methodKey = "cash",
            payeeName = "Naftal", expenseCategory = "fuel",
        }).ContinueWith(t => IdOfAsync(t.Result)).Unwrap();

    private static async Task<PaymentVoucherStatus> StatusOfAsync(HttpClient admin, Guid id)
    {
        var voucher = await admin.GetFromJsonAsync<JsonElement>($"/api/payment-vouchers/{id}");
        return (PaymentVoucherStatus)voucher.GetProperty("status").GetInt32();
    }

    [Fact]
    public async Task A_cash_box_cashier_reads_and_acts_on_their_own_branch_only_and_another_branch_s_voucher_is_not_found()
    {
        using var host = new PosHost();
        using var admin = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword, "C01");

        // Two branches: the main one (with till C01), and Oran with its own till, C02.
        var branches = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/branches");
        var main = branches.EnumerateArray().Single(b => b.GetProperty("isMain").GetBoolean() && !b.GetProperty("isWarehouse").GetBoolean()).GetProperty("id").GetGuid();
        var oranResponse = await admin.PostAsJsonAsync("/api/catalog/branches", new
        {
            nameAr = "وهران", nameFr = "Oran", nameEn = "Oran", address = "", phone = "",
            isMain = false, allowNegativeStock = false, isActive = true,
        });
        var oran = await IdOfAsync(oranResponse);
        var till = await admin.PostAsJsonAsync("/api/devices", new { code = "C02", name = "Oran 1", branchId = oran, isActive = true });
        Assert.True(till.IsSuccessStatusCode, await till.Content.ReadAsStringAsync());

        await CreateUserAsync(host, admin, "caisse-oran", UserRole.Cashier, oran, Permissions.CashBox);
        await CreateUserAsync(host, admin, "gerant-oran", UserRole.Manager, oran);

        // At each branch: one voucher left pending, one approved and waiting to be paid.
        var mainPending = await RaiseAsync(admin, main, 100m);
        var mainApproved = await RaiseAsync(admin, main, 200m);
        var oranPending = await RaiseAsync(admin, oran, 300m);
        var oranApproved = await RaiseAsync(admin, oran, 400m);
        foreach (var id in new[] { mainApproved, oranApproved })
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/payment-vouchers/{id}/approve", new { note = (string?)null })).StatusCode);

        using var cashier = await SignedInAsync(host, "caisse-oran", Password, "C02");

        // Another branch's voucher, by its id: not found — read, paid or cancelled — and untouched.
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.GetAsync($"/api/payment-vouchers/{mainPending}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.GetAsync($"/api/payment-vouchers/{mainApproved}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await cashier.PostAsJsonAsync($"/api/payment-vouchers/{mainApproved}/pay", new { note = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await cashier.PostAsJsonAsync($"/api/payment-vouchers/{mainPending}/cancel", new { note = "essai" })).StatusCode);
        Assert.Equal(PaymentVoucherStatus.Approved, await StatusOfAsync(admin, mainApproved));
        Assert.Equal(PaymentVoucherStatus.Pending, await StatusOfAsync(admin, mainPending));

        // Not found reads the same as an id that does not exist: nothing says the voucher is there.
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.GetAsync($"/api/payment-vouchers/{Guid.NewGuid()}")).StatusCode);

        // The list never named it either.
        var list = await cashier.GetFromJsonAsync<JsonElement>("/api/payment-vouchers?take=1000");
        var listed = list.EnumerateArray().Select(v => v.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(mainPending, listed);
        Assert.Contains(oranPending, listed);

        // Their own branch's: read, paid and cancelled exactly as before.
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync($"/api/payment-vouchers/{oranPending}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await cashier.PostAsJsonAsync($"/api/payment-vouchers/{oranApproved}/pay", new { note = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await cashier.PostAsJsonAsync($"/api/payment-vouchers/{oranPending}/cancel", new { note = "essai" })).StatusCode);
        Assert.Equal(PaymentVoucherStatus.Paid, await StatusOfAsync(admin, oranApproved));
        Assert.Equal(PaymentVoucherStatus.Cancelled, await StatusOfAsync(admin, oranPending));

        // Approving was never theirs, at any branch.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await cashier.PostAsJsonAsync($"/api/payment-vouchers/{mainPending}/approve", new { note = (string?)null })).StatusCode);

        // A branch manager is held to their branch too since P0.1b (owner, 2026-10-25): another
        // branch's voucher is not found for them, their own is read; the admin reads every branch.
        using var manager = await SignedInAsync(host, "gerant-oran", Password, "C02");
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/payment-vouchers/{mainPending}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/payment-vouchers/{oranPending}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/payment-vouchers/{oranPending}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/payment-vouchers/{mainPending}")).StatusCode);
    }
}
