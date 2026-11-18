using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using AlgeriaPos.API.Endpoints;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Tests.Stock;

/// <summary>
/// Stock transfers with dispatch and receive (owner, 2026-10-12), through the real routes. At every
/// step the company still holds what it held — on the branches' rows plus what is in transit — until
/// a loss takes some out, and every ledger entry balances.
/// </summary>
public class StockTransferTests
{
    private static readonly Guid Algiers = TillHarness.Algiers;
    private static readonly Guid Oran = TillHarness.Oran;
    internal static readonly Guid Pipe = TillHarness.Pipe;
    internal const decimal Cost = 600m;
    internal const string Route = "/api/catalog/stock/transfers";

    internal sealed record Setup(Microsoft.Data.Sqlite.SqliteConnection Connection, PosDbContext Db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    internal static async Task<Setup> NewAsync()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        (await db.Products.SingleAsync(p => p.Id == Pipe)).CostPrice = Cost;
        await db.SaveChangesAsync();
        return new Setup(connection, db);
    }

    internal static Task<(int Status, string Body)> Call(PosDbContext db, ClaimsPrincipal caller, string method, string pattern, string json, Guid? id = null) =>
        BranchLockTests.InvokeAsync(db, app => app.MapStockTransferEndpoints(), method, pattern, caller, json,
            id is Guid g ? [("id", g.ToString())] : []);

    internal static async Task<JsonElement> DispatchAsync(PosDbContext db, ClaimsPrincipal caller, Guid from, Guid to, decimal qty, int expected = 201)
    {
        var (status, body) = await Call(db, caller, "POST", Route,
            $$"""{"fromBranchId":"{{from}}","toBranchId":"{{to}}","note":"truck 12","lines":[{"productId":"{{Pipe}}","qty":{{qty}}}]}""");
        Assert.True(status == expected, $"{status}: {body}");
        return status < 300 ? JsonDocument.Parse(body).RootElement.Clone() : default;
    }

    internal static Guid Id(JsonElement t) => t.GetProperty("id").GetGuid();
    internal static Guid LineId(JsonElement t) => t.GetProperty("lines")[0].GetProperty("id").GetGuid();

    internal static Task<(int Status, string Body)> ActAsync(PosDbContext db, ClaimsPrincipal caller, string action, JsonElement t, string json) =>
        Call(db, caller, "POST", Route + "/{id:guid}/" + action, json, Id(t));

    private static string Lines(JsonElement t, decimal qty, string extra = "") =>
        $$"""{"lines":[{"lineId":"{{LineId(t)}}","qty":{{qty}}}]{{extra}}}""";

    internal static async Task<decimal> QtyAsync(PosDbContext db, Guid branch)
    {
        db.ChangeTracker.Clear();
        return (await db.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == Pipe && s.BranchId == branch)).StockQty;
    }

    /// <summary>On the rows (every bucket, every branch) plus the open remainder of the transfers.</summary>
    internal static async Task<decimal> CompanyTotalAsync(PosDbContext db)
    {
        db.ChangeTracker.Clear();
        var rows = await db.ProductStocks.AsNoTracking().Where(s => s.ProductId == Pipe).ToListAsync();
        var lines = await db.StockTransferLines.AsNoTracking().Include(l => l.Transfer).Where(l => l.ProductId == Pipe).ToListAsync();
        return rows.Sum(s => s.OwnedQty) + lines.Where(l => l.Transfer!.IsOpen).Sum(l => l.InTransitQty);
    }

    internal static async Task AssertLedgerBalancedAsync(PosDbContext db)
    {
        db.ChangeTracker.Clear();
        var lines = await db.LedgerEntryLines.AsNoTracking().ToListAsync();
        foreach (var entry in lines.GroupBy(l => l.EntryId))
            Assert.Equal(entry.Sum(l => l.Debit), entry.Sum(l => l.Credit));
        Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
    }

    internal static async Task<decimal> BalanceAsync(PosDbContext db, string code, Guid branch)
    {
        db.ChangeTracker.Clear();
        var account = await db.LedgerAccounts.AsNoTracking().SingleAsync(a => a.Code == code);
        var lines = await db.LedgerEntryLines.AsNoTracking().Where(l => l.AccountId == account.Id && l.BranchId == branch).ToListAsync();
        return lines.Sum(l => l.Debit - l.Credit);
    }

    internal static async Task<List<LedgerEntry>> EntriesAsync(PosDbContext db, string source)
    {
        db.ChangeTracker.Clear();
        return await db.LedgerEntries.AsNoTracking().Include(e => e.Lines).Where(e => e.SourceType == source).ToListAsync();
    }

    [Fact]
    public async Task Dispatch_takes_the_quantity_off_the_source_into_transit_and_posts_nothing()
    {
        await using var s = await NewAsync();
        var (admin, principal) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);

        var t = await DispatchAsync(s.Db, principal, Algiers, Oran, 4);

        Assert.Equal("TRF-000001", t.GetProperty("number").GetString());
        Assert.Equal("Dispatched", t.GetProperty("status").GetString());
        Assert.Equal(4m, t.GetProperty("inTransit").GetDecimal());
        Assert.Equal(6m, await QtyAsync(s.Db, Algiers));
        Assert.Equal(10m, await QtyAsync(s.Db, Oran));
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));

        var movement = await s.Db.StockMovements.AsNoTracking().SingleAsync(m => m.Reason.StartsWith(StockTransferReasons.Dispatch));
        Assert.Equal(Algiers, movement.BranchId);
        Assert.Equal(Oran, movement.RelatedBranchId);
        Assert.Equal(-4m, movement.QtyDelta);
        Assert.Equal(4m, movement.MovedQty);
        Assert.Equal(6m, movement.BalanceAfter);
        Assert.Equal("TRF-000001", movement.DocumentNumber);
        Assert.Equal(admin.Id, movement.UserId);
        Assert.Empty(await s.Db.LedgerEntries.AsNoTracking().ToListAsync());

        var second = await DispatchAsync(s.Db, principal, Algiers, Oran, 1);
        Assert.Equal("TRF-000002", second.GetProperty("number").GetString());
    }

    [Fact]
    public async Task Dispatching_more_than_the_source_holds_is_refused_and_writes_nothing()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);

        await DispatchAsync(s.Db, admin, Algiers, Oran, 11, expected: 409);

        Assert.Equal(10m, await QtyAsync(s.Db, Algiers));
        Assert.Empty(await s.Db.StockTransfers.AsNoTracking().ToListAsync());
        Assert.Empty(await s.Db.StockMovements.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_full_receipt_lands_at_the_destination_and_posts_the_inter_branch_move()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var (_, oran) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);
        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);

        var (status, body) = await ActAsync(s.Db, oran, "receive", t, Lines(t, 4));

        Assert.True(status == 200, body);
        var after = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Received", after.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("closedAtUtc").ValueKind);
        Assert.Equal(6m, await QtyAsync(s.Db, Algiers));
        Assert.Equal(14m, await QtyAsync(s.Db, Oran));
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));
        var incoming = await s.Db.StockMovements.AsNoTracking().SingleAsync(m => m.Reason.StartsWith(StockTransferReasons.Receive));
        Assert.Equal((Oran, 4m, 4m), (incoming.BranchId, incoming.QtyDelta, incoming.MovedQty));

        var entry = Assert.Single(await EntriesAsync(s.Db, LedgerSources.StockTransfer));
        Assert.True(entry.IsInterBranch);
        Assert.Equal(Oran, entry.CounterpartBranchId);
        Assert.Equal(2400m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Oran));
        Assert.Equal(-2400m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Algiers));
        await AssertLedgerBalancedAsync(s.Db);
    }

    [Fact]
    public async Task A_transfer_is_received_in_parts_and_never_beyond_what_is_in_transit()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var (_, oran) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);
        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);

        var (status, body) = await ActAsync(s.Db, oran, "receive", t, Lines(t, 1));
        Assert.True(status == 200, body);
        var partly = JsonDocument.Parse(body).RootElement;
        Assert.Equal("PartlyReceived", partly.GetProperty("status").GetString());
        Assert.Equal(3m, partly.GetProperty("inTransit").GetDecimal());
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));
        await AssertLedgerBalancedAsync(s.Db);

        Assert.Equal(409, (await ActAsync(s.Db, oran, "receive", t, Lines(t, 5))).Status);

        (status, body) = await ActAsync(s.Db, oran, "receive", t, Lines(t, 3));
        Assert.True(status == 200, body);
        Assert.Equal("Received", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());
        Assert.Equal(14m, await QtyAsync(s.Db, Oran));
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));
        Assert.Equal(2, (await EntriesAsync(s.Db, LedgerSources.StockTransfer)).Count);
        Assert.Equal(2, await s.Db.StockTransferEvents.CountAsync(e => e.Kind == StockTransferEventKind.Receive));
        Assert.Equal(2400m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Oran));
        await AssertLedgerBalancedAsync(s.Db);

        // Closed: nothing more is received.
        Assert.Equal(409, (await ActAsync(s.Db, oran, "receive", t, Lines(t, 1))).Status);
    }

    [Fact]
    public async Task Goods_lost_in_transit_are_written_off_at_the_source_with_a_reason()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var (_, algiersManager) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Algiers);
        var (_, oranManager) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);
        var algiers = TillHarness.WithGrant(algiersManager, PermissionCatalog.InventoryTransferWriteOff);
        var oran = TillHarness.WithGrant(oranManager, PermissionCatalog.InventoryTransferWriteOff);
        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);
        Assert.Equal(200, (await ActAsync(s.Db, oranManager, "receive", t, Lines(t, 3))).Status);

        Assert.Equal(403, (await ActAsync(s.Db, oran, "lost", t, Lines(t, 1, ""","reason":"fell off" """))).Status);
        Assert.Equal(400, (await ActAsync(s.Db, algiers, "lost", t, Lines(t, 1, ""","reason":"  " """))).Status);

        var (status, body) = await ActAsync(s.Db, algiers, "lost", t, Lines(t, 1, ""","reason":"fell off the truck" """));
        Assert.True(status == 200, body);
        var after = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Received", after.GetProperty("status").GetString());
        Assert.Equal(1m, after.GetProperty("lines")[0].GetProperty("qtyLost").GetDecimal());
        Assert.Equal(6m, await QtyAsync(s.Db, Algiers));
        Assert.Equal(19m, await CompanyTotalAsync(s.Db)); // one unit has left the company

        var lost = await s.Db.StockMovements.AsNoTracking().SingleAsync(m => m.Reason.StartsWith(StockTransferReasons.Loss));
        Assert.Equal((Algiers, 0m, 1m), (lost.BranchId, lost.QtyDelta, lost.MovedQty));

        var entry = Assert.Single(await EntriesAsync(s.Db, LedgerSources.StockTransferLoss));
        Assert.False(entry.IsInterBranch);
        Assert.Equal(Algiers, entry.BranchId);
        Assert.Equal(600m, await BalanceAsync(s.Db, LedgerAccounts.StockLoss, Algiers));
        // Algiers: −1 800 for the three received, −600 written off.
        Assert.Equal(-2400m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Algiers));
        await AssertLedgerBalancedAsync(s.Db);
    }

    [Fact]
    public async Task The_source_cancels_only_while_nothing_was_received_and_gets_everything_back()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var (_, algiers) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Algiers);
        var (_, oran) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);

        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);
        Assert.Equal(403, (await ActAsync(s.Db, oran, "cancel", t, "{}")).Status);
        var (status, body) = await ActAsync(s.Db, algiers, "cancel", t, """{"reason":"wrong truck"}""");
        Assert.True(status == 200, body);
        Assert.Equal("Cancelled", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());
        Assert.Equal(10m, await QtyAsync(s.Db, Algiers));
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));
        var back = await s.Db.StockMovements.AsNoTracking().SingleAsync(m => m.Reason.StartsWith(StockTransferReasons.Cancel));
        Assert.Equal(4m, back.QtyDelta);
        Assert.Empty(await s.Db.LedgerEntries.AsNoTracking().ToListAsync());

        var partly = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);
        Assert.Equal(200, (await ActAsync(s.Db, oran, "receive", partly, Lines(partly, 1))).Status);
        Assert.Equal(409, (await ActAsync(s.Db, algiers, "cancel", partly, "{}")).Status);
    }

    [Fact]
    public async Task A_branch_user_dispatches_from_their_own_branch_and_receives_only_at_it()
    {
        await using var s = await NewAsync();
        var constantine = Guid.NewGuid();
        s.Db.Branches.Add(new Branch { Id = constantine, NameFr = "Constantine", IsOpenForOperation = true });
        await s.Db.SaveChangesAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var (_, algiers) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Algiers);
        var (_, oran) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);
        var (_, third) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, constantine);

        // An Oran manager naming Algiers as the source still dispatches from Oran.
        var fromOran = await DispatchAsync(s.Db, oran, Algiers, constantine, 4);
        Assert.Equal(Oran, fromOran.GetProperty("fromBranchId").GetGuid());
        Assert.Equal(6m, await QtyAsync(s.Db, Oran));
        Assert.Equal(10m, await QtyAsync(s.Db, Algiers));

        // Algiers → Oran: the source cannot receive it, a third branch cannot see it.
        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 2);
        Assert.Equal(403, (await ActAsync(s.Db, algiers, "receive", t, Lines(t, 2))).Status);
        Assert.Equal(404, (await ActAsync(s.Db, third, "receive", t, Lines(t, 2))).Status);
        Assert.Equal(404, (await Call(s.Db, third, "GET", Route + "/{id:guid}", "", Id(t))).Status);
        Assert.Equal(200, (await Call(s.Db, algiers, "GET", Route + "/{id:guid}", "", Id(t))).Status);
        Assert.Equal(8m, await QtyAsync(s.Db, Algiers));
        Assert.Equal(6m, await QtyAsync(s.Db, Oran));

        // The Admin receives anywhere.
        Assert.Equal(200, (await ActAsync(s.Db, admin, "receive", t, Lines(t, 2))).Status);
        Assert.Equal(8m, await QtyAsync(s.Db, Oran));
        Assert.Equal(20m, await CompanyTotalAsync(s.Db)); // the 4 for Constantine are still in transit
        await AssertLedgerBalancedAsync(s.Db);
    }

    [Fact]
    public async Task A_dispatch_is_checked_and_the_same_product_twice_is_one_line()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);

        Assert.Equal(400, (await Call(s.Db, admin, "POST", Route,
            $$"""{"fromBranchId":"{{Algiers}}","toBranchId":"{{Algiers}}","note":null,"lines":[{"productId":"{{Pipe}}","qty":1}]}""")).Status);
        Assert.Equal(400, (await Call(s.Db, admin, "POST", Route,
            $$"""{"fromBranchId":"{{Algiers}}","toBranchId":"{{Oran}}","note":null,"lines":[]}""")).Status);
        Assert.Equal(400, (await Call(s.Db, admin, "POST", Route,
            $$"""{"fromBranchId":"{{Algiers}}","toBranchId":"{{Oran}}","note":null,"lines":[{"productId":"{{Pipe}}","qty":0}]}""")).Status);

        var (status, body) = await Call(s.Db, admin, "POST", Route,
            $$"""{"fromBranchId":"{{Algiers}}","toBranchId":"{{Oran}}","note":null,"lines":[{"productId":"{{Pipe}}","qty":2},{"productId":"{{Pipe}}","qty":3}]}""");
        Assert.True(status == 201, body);
        var line = Assert.Single(JsonDocument.Parse(body).RootElement.GetProperty("lines").EnumerateArray());
        Assert.Equal(5m, line.GetProperty("qtyDispatched").GetDecimal());
        Assert.Equal(5m, await QtyAsync(s.Db, Algiers));
    }

    [Fact]
    public void The_receipt_and_the_loss_post_as_the_design_says()
    {
        Guid from = Guid.NewGuid(), to = Guid.NewGuid(), step = Guid.NewGuid();
        var receipt = StockPosting.TransferReceipt(step, from, to, "Alger", "Oran", "TRF-000001",
            [new ValuedLine("A", 100m), new ValuedLine("B", 50m)]);
        Assert.True(receipt.IsInterBranch);
        Assert.Equal(to, receipt.CounterpartBranchId);
        Assert.Equal(LedgerSources.StockTransfer, receipt.SourceType);
        Assert.Equal(150m, receipt.Lines.Where(l => l.BranchId == to && l.AccountCode == LedgerAccounts.Inventory).Sum(l => l.Debit));
        Assert.Equal(150m, receipt.Lines.Where(l => l.BranchId == from && l.AccountCode == LedgerAccounts.Inventory).Sum(l => l.Credit));

        var loss = StockPosting.TransferLoss(step, from, "TRF-000001", [new ValuedLine("A", 100m)], "fell off");
        Assert.False(loss.IsInterBranch);
        Assert.Equal(from, loss.BranchId);
        Assert.Equal(LedgerSources.StockTransferLoss, loss.SourceType);
        Assert.Equal(100m, loss.Lines.Single(l => l.AccountCode == LedgerAccounts.StockLoss).Debit);
        Assert.Equal(100m, loss.Lines.Single(l => l.AccountCode == LedgerAccounts.Inventory).Credit);
    }

    [Fact]
    public async Task List_filters_by_branch_status_product_and_date_range()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var constantine = Guid.NewGuid();
        s.Db.Branches.Add(new Branch { Id = constantine, NameFr = "Constantine", IsOpenForOperation = true });
        var otherProduct = new Product { Sku = "BOLT-10", NameFr = "Boulon 10", TrackStock = true, CostPrice = 10m };
        s.Db.Products.Add(otherProduct);
        s.Db.ProductStocks.Add(new ProductStock { ProductId = otherProduct.Id, BranchId = Algiers, StockQty = 50m });
        await s.Db.SaveChangesAsync();

        var t1 = await DispatchAsync(s.Db, admin, Algiers, Oran, 2);
        var t2 = await DispatchAsync(s.Db, admin, Algiers, constantine, 1);
        await ActAsync(s.Db, admin, "cancel", t2, """{"reason":"mistake"}""");

        var (status, body) = await Call(s.Db, admin, "POST", Route,
            $$"""{"fromBranchId":"{{Algiers}}","toBranchId":"{{Oran}}","note":null,"lines":[{"productId":"{{otherProduct.Id}}","qty":5}]}""");
        Assert.True(status == 201, body);
        var t3 = JsonDocument.Parse(body).RootElement;

        // 1. Filter status=open (matches Dispatched and PartlyReceived, not Cancelled)
        var (s1, list1) = await Call(s.Db, admin, "GET", Route, "?status=open");
        Assert.Equal(200, s1);
        var openIds = JsonDocument.Parse(list1).RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(Id(t1), openIds);
        Assert.Contains(Id(t3), openIds);
        Assert.DoesNotContain(Id(t2), openIds);

        // 2. Filter status=Cancelled
        var (s2, list2) = await Call(s.Db, admin, "GET", Route, "?status=Cancelled");
        Assert.Equal(200, s2);
        var cancelledIds = JsonDocument.Parse(list2).RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(Id(t2), cancelledIds);
        Assert.DoesNotContain(Id(t1), cancelledIds);

        // 3. Filter productId
        var (s3, list3) = await Call(s.Db, admin, "GET", Route, $"?productId={otherProduct.Id}");
        Assert.Equal(200, s3);
        var productIds = JsonDocument.Parse(list3).RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(Id(t3), productIds);
        Assert.DoesNotContain(Id(t1), productIds);

        // 4. Filter branchId=Constantine (matches from or to)
        var (s4, list4) = await Call(s.Db, admin, "GET", Route, $"?branchId={constantine}");
        Assert.Equal(200, s4);
        var constIds = JsonDocument.Parse(list4).RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(Id(t2), constIds);
        Assert.DoesNotContain(Id(t1), constIds);

        // 5. Date filter: future from date returns empty
        var future = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (s5, list5) = await Call(s.Db, admin, "GET", Route, $"?from={future}");
        Assert.Equal(200, s5);
        Assert.Empty(JsonDocument.Parse(list5).RootElement.EnumerateArray());
    }
}
