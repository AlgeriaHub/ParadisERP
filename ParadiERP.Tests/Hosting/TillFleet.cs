using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Hosting;

/// <summary>One till: a cashier signed in online on the till that came with their account.</summary>
public sealed record Till(HttpClient Client, Guid Cashier, string Code);

/// <summary>
/// Several tills at one selling branch, on a throwaway SQL Server database: the shape of a busy
/// shop pushing its sales at the same moment. Built for the <see cref="SqlServerFactAttribute"/>
/// tests about two writers meeting (see <see cref="SqlServerTestDatabase"/> for why SQL Server).
/// </summary>
public sealed class TillFleet : IAsyncDisposable
{
    public const string Password = "Essai@2026";

    private readonly SqlServerTestDatabase database;

    public PosHost Host { get; }
    public HttpClient Admin { get; }
    public Guid Branch { get; private set; }
    public Guid Product { get; private set; }

    /// <summary>
    /// An article with no stock kept (a fitting, a delivery). A sale of only such lines touches no
    /// stock row at all, so nothing about stock stands between two of them.
    /// </summary>
    public Guid Service { get; private set; }

    public IReadOnlyList<Till> Tills { get; private set; } = [];

    private TillFleet(SqlServerTestDatabase database, PosHost host, HttpClient admin)
    {
        this.database = database;
        Host = host;
        Admin = admin;
    }

    /// <summary>A company on SQL Server with a selling branch "Alger", one tracked article stocked there, and <paramref name="tills"/> cashiers.</summary>
    public static async Task<TillFleet> StartAsync(int tills, decimal openingStock)
    {
        var database = new SqlServerTestDatabase();
        var host = new PosHost(new Dictionary<string, string?>
        {
            ["Database__Provider"] = "SqlServer",
            ["ConnectionStrings__Default"] = database.ConnectionString,
        });
        var (admin, _) = await SignedInAsync(host, PosHost.BootstrapUsername, PosHost.BootstrapPassword);
        var fleet = new TillFleet(database, host, admin);

        fleet.Branch = await TestBranches.SellingAsync(admin, "Alger");
        fleet.Product = await CreatedIdAsync(await admin.PostAsJsonAsync("/api/catalog/products", new
        {
            sku = "TUBE-50", barcode = (string?)null, nameAr = "أنبوب", nameFr = "Tube", nameEn = "Tube",
            categoryId = (Guid?)null, price = 10m, costPrice = 6m, taxRate = 0m, unit = "u",
            trackStock = true, isActive = true,
        }));
        fleet.Service = await CreatedIdAsync(await admin.PostAsJsonAsync("/api/catalog/products", new
        {
            sku = "POSE", barcode = (string?)null, nameAr = "تركيب", nameFr = "Pose", nameEn = "Fitting",
            categoryId = (Guid?)null, price = 10m, costPrice = 0m, taxRate = 0m, unit = "u",
            trackStock = false, isActive = true,
        }));
        var stocked = await admin.PostAsJsonAsync($"/api/catalog/products/{fleet.Product}/stock",
            new { branchId = fleet.Branch, qtyDelta = openingStock, reason = "inventaire" });
        Assert.True(stocked.IsSuccessStatusCode, await stocked.Content.ReadAsStringAsync());

        // Boxes are made for the branches that exist at start-up; this one came after.
        await using (var db = fleet.Open())
        {
            if (!await db.CashBoxes.AnyAsync(b => b.BranchId == fleet.Branch && b.IsActive))
            {
                db.CashBoxes.Add(new CashBox { BranchId = fleet.Branch, NameAr = "صندوق", NameFr = "Caisse Alger", NameEn = "Alger" });
                await db.SaveChangesAsync();
            }
        }

        var signedIn = new List<Till>();
        for (var i = 0; i < tills; i++)
        {
            await TestUsers.CreateAsync(host, admin, $"caisse-{i}", "Cashier", fleet.Branch, Password, pin: "1234");
            var (client, body) = await SignedInAsync(host, host.SignInName($"caisse-{i}"), Password);
            signedIn.Add(new Till(client, body.GetProperty("user").GetProperty("id").GetGuid(), body.GetProperty("deviceCode").GetString()!));
        }
        fleet.Tills = signedIn;
        return fleet;
    }

    /// <summary>Every till pushes <paramref name="salesPerTill"/> sales, each in its own push, all at once. Returns each push's answer.</summary>
    public async Task<string[]> PushAllAtOnceAsync(int salesPerTill, Func<Till, long, object> sale)
    {
        var pushes = Tills.SelectMany(till => Enumerable.Range(1, salesPerTill).Select(async seq =>
        {
            var response = await till.Client.PostAsJsonAsync("/api/sync/push", new { deviceCode = till.Code, sales = new[] { sale(till, seq) } });
            return await response.Content.ReadAsStringAsync();
        }));
        var answers = await Task.WhenAll(pushes);
        foreach (var answer in answers)
            Assert.True(JsonDocument.Parse(answer).RootElement.GetProperty("accepted").GetInt32() == 1, answer);
        return answers;
    }

    /// <summary>
    /// One sale of <paramref name="qty"/> units of <paramref name="product"/> (the stocked article
    /// unless another is named) at 10 each, paid with <paramref name="payments"/> (method, amount,
    /// reference) — all cash when none are given.
    /// </summary>
    public object Sale(Till till, long seq, decimal qty = 1m, Guid? customerId = null, Guid? product = null,
        params (string Method, decimal Amount, string? Reference)[] payments)
    {
        var total = qty * 10m;
        var sold = product ?? Product;
        if (payments.Length == 0) payments = [("Cash", total, null)];
        return new
        {
            id = Guid.NewGuid(), reference = $"{till.Code}-{seq:000000}", localSeq = seq, soldAtUtc = DateTime.UtcNow, customerId,
            cashierUserId = till.Cashier, cashierName = "Essai", subTotalHt = total, taxTotal = 0m, discountTotal = 0m,
            grandTotalTtc = total, roundingAdjustment = 0m, paidAmount = total, changeAmount = 0m,
            paymentMethod = payments.Length == 1 ? payments[0].Method : "Mixed", status = "Completed",
            refundOfSaleId = (Guid?)null, note = (string?)null, taxMode = "Untaxed",
            lines = new[] { new { id = Guid.NewGuid(), productId = sold, sku = sold == Product ? "TUBE-50" : "POSE", nameSnapshot = "Article", qty,
                unitPrice = 10m, taxRate = 0m, lineDiscount = 0m, lineTotalTtc = total, lineTaxAmount = 0m } },
            payments = payments.Select(p => new { id = Guid.NewGuid(), method = p.Method, amount = p.Amount, reference = p.Reference }).ToArray(),
        };
    }

    public PosDbContext Open() =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseSqlServer(database.ConnectionString).Options);

    public static async Task<Guid> CreatedIdAsync(HttpResponseMessage created)
    {
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>How long one sign-in attempt may take, and how many are made, before the fleet gives up.</summary>
    private static readonly TimeSpan SignInAttempt = TimeSpan.FromSeconds(90);
    private const int SignInAttempts = 3;

    private static async Task<(HttpClient Client, JsonElement Body)> SignedInAsync(PosHost host, string username, string password)
    {
        var client = host.CreateClient();
        // A backstop for every later request of this client; the sign-in attempts below carry a tighter limit of their own.
        client.Timeout = TimeSpan.FromMinutes(5);
        // The fleet's first sign-in on a host just brought up on SQL Server gave no answer at all on CI — past 100 s,
        // then past 5 minutes (2026-11-02 and -03), always the first fleet of the run, never anywhere else. Each attempt
        // is bounded and a stalled one is tried again: what these tests measure is the pushes, not the start-up. If
        // every attempt stalls, the message says how the thread pool stood, which tells starvation from a deadlock.
        HttpResponseMessage? login = null;
        var stalls = new List<string>();
        for (var attempt = 1; login is null; attempt++)
        {
            using var limit = new CancellationTokenSource(SignInAttempt);
            try
            {
                login = await client.PostAsJsonAsync("/api/auth/login", new { username, password, deviceCode = (string?)null }, limit.Token);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                login = null;
                ThreadPool.GetAvailableThreads(out var workers, out var io);
                stalls.Add($"attempt {attempt}: no answer in {SignInAttempt.TotalSeconds:0} s (pool threads {ThreadPool.ThreadCount}, " +
                           $"queued items {ThreadPool.PendingWorkItemCount}, available workers {workers}, io {io})");
                if (attempt >= SignInAttempts)
                    Assert.Fail($"The fleet's sign-in as '{username}' never answered: {string.Join("; ", stalls)}.");
            }
        }
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.Clone();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return (client, body);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var till in Tills) till.Client.Dispose();
        Admin.Dispose();
        Host.Dispose();
        await database.DisposeAsync();
    }
}
