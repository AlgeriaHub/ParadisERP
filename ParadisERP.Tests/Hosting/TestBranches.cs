using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ParadisERP.Tests.Hosting;

/// <summary>
/// A further branch that sells, for tests that need a cashier of their own. A test host's company
/// starts with its primary branch and its primary warehouse (2026-10-05); a cashier works at a
/// branch, never a warehouse, which hosts neither a till nor a cashier.
/// </summary>
public static class TestBranches
{
    public static async Task<Guid> SellingAsync(HttpClient admin, string name)
    {
        var created = await admin.PostAsJsonAsync("/api/catalog/branches", new
        {
            nameAr = name, nameFr = name, nameEn = name, address = "", phone = "",
            isMain = false, allowNegativeStock = false, isActive = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
