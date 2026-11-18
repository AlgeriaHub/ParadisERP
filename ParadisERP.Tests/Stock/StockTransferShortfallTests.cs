using System.Security.Claims;
using System.Text.Json;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Authorization;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Stock.StockTransferTests;

namespace ParadisERP.Tests.Stock;

/// <summary>
/// A shortfall reported at receipt (owner, 2026-10-13), through the real routes: the destination
/// reports it with a reason, then the source branch — or a holder of the write-off key — confirms it
/// as lost or rejects it. Until it is answered it is in transit: the company still holds it, at every
/// step the rows plus the open transfers add up, and every ledger entry balances.
/// </summary>
public class StockTransferShortfallTests
{
    private static readonly Guid Algiers = TillHarness.Algiers;
    private static readonly Guid Oran = TillHarness.Oran;

    /// <summary>One receipt line: what arrived, what is reported short, and why.</summary>
    private static string Receipt(JsonElement t, decimal qty, decimal reportedShort, string? reason = "carton missing") =>
        $$"""{"lines":[{"lineId":"{{LineId(t)}}","qty":{{qty}},"short":{{reportedShort}}}],"note":null,"shortReason":{{Json(reason)}}}""";

    /// <summary>The answer to a reported shortfall on the one line.</summary>
    private static string Answer(JsonElement t, decimal qty, string? reason = null) =>
        $$"""{"lines":[{"lineId":"{{LineId(t)}}","qty":{{qty}}}],"reason":{{Json(reason)}}}""";

    private static string Json(string? text) => text is null ? "null" : JsonSerializer.Serialize(text);
    private static JsonElement Root(string body) => JsonDocument.Parse(body).RootElement.Clone();
    private static JsonElement Line(string body) => Root(body).GetProperty("lines")[0];
    private static decimal D(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    /// <summary>A company-scoped user of a custom role holding exactly <paramref name="keys"/>, as the server resolves them.</summary>
    private static async Task<ClaimsPrincipal> CompanyUserAsync(PosDbContext db, params string[] keys)
    {
        var user = new User { Username = $"co-{Guid.NewGuid():N}"[..20], FullName = "Contrôleur", Role = UserRole.Manager };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", user.Id.ToString()), new Claim("name", user.FullName)], "test", "name", "role"));
        principal.AddIdentity(ServerAuthorization.CreateIdentity(
            new ServerAuthorizationContext(Guid.NewGuid(), RoleScope.Company, keys.ToHashSet(StringComparer.Ordinal))));
        return principal;
    }

    [Fact]
    public async Task The_destination_reports_a_shortfall_at_receipt_and_it_stays_in_transit_at_the_source()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var (_, algiers) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Algiers);
        var (oranUser, oran) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);
        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);

        // A reason is required, only the destination reports, and never more than is left in transit.
        Assert.Equal(400, (await ActAsync(s.Db, oran, "receive", t, Receipt(t, 3, 1, reason: null))).Status);
        Assert.Equal(403, (await ActAsync(s.Db, algiers, "receive", t, Receipt(t, 3, 1))).Status);
        Assert.Equal(409, (await ActAsync(s.Db, oran, "receive", t, Receipt(t, 3, 2))).Status);
        Assert.Equal(10m, await QtyAsync(s.Db, Oran));

        var (status, body) = await ActAsync(s.Db, oran, "receive", t, Receipt(t, 3, 1));

        Assert.True(status == 200, body);
        var after = Root(body);
        Assert.Equal("PartlyReceived", after.GetProperty("status").GetString());
        Assert.Equal(1m, D(after, "inTransit"));
        Assert.Equal(1m, D(after, "qtyShortPending"));
        Assert.False(after.GetProperty("canDecideShortfall").GetBoolean()); // the destination never answers its own report
        var line = Line(body);
        Assert.Equal((3m, 1m, 1m, 0m), (D(line, "qtyReceived"), D(line, "qtyShortPending"), D(line, "qtyShortReported"), D(line, "qtyLost")));

        // Who, when and why, in the event table, after the receipt it came with.
        Assert.Equal(new[] { "Dispatch", "Receive", "ShortfallReported" },
            after.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()!).ToArray());
        var report = await s.Db.StockTransferEvents.AsNoTracking().SingleAsync(e => e.Kind == StockTransferEventKind.ShortfallReported);
        Assert.Equal((oranUser.Id, "carton missing"), (report.UserId, report.Reason));

        // Still the company's and on no shelf; only the three that arrived moved in the books.
        Assert.Equal(6m, await QtyAsync(s.Db, Algiers));
        Assert.Equal(13m, await QtyAsync(s.Db, Oran));
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));
        Assert.False(await s.Db.StockMovements.AsNoTracking().AnyAsync(m => m.Reason.StartsWith(StockTransferReasons.Loss)));
        Assert.Single(await EntriesAsync(s.Db, LedgerSources.StockTransfer));
        Assert.Empty(await EntriesAsync(s.Db, LedgerSources.StockTransferLoss));
        Assert.Equal(-1800m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Algiers));
        await AssertLedgerBalancedAsync(s.Db);

        // The source finds it under the shortfall filter, and the server says it may answer.
        var (listStatus, list) = await Call(s.Db, algiers, "GET", Route, "?status=shortfall");
        Assert.True(listStatus == 200, list);
        var listed = Assert.Single(JsonDocument.Parse(list).RootElement.EnumerateArray());
        Assert.True(listed.GetProperty("canDecideShortfall").GetBoolean());
        var other = await DispatchAsync(s.Db, admin, Algiers, Oran, 1); // open, nothing reported: not under the filter
        (_, list) = await Call(s.Db, admin, "GET", Route, "?status=shortfall");
        Assert.Equal(Id(t), Assert.Single(JsonDocument.Parse(list).RootElement.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.NotEqual(Id(t), Id(other));
    }

    [Fact]
    public async Task The_source_confirms_the_shortfall_and_it_is_written_off_as_a_declared_loss_is()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        // The source's manager holds no write-off key: being the source is enough to answer a report.
        var (algiersUser, algiers) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Algiers);
        var (_, oran) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);
        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);
        Assert.Equal(200, (await ActAsync(s.Db, oran, "receive", t, Receipt(t, 3, 1))).Status);

        // More than was reported, or nothing at all, is refused and changes nothing.
        Assert.Equal(409, (await ActAsync(s.Db, algiers, "shortfall/confirm", t, Answer(t, 2))).Status);
        Assert.Equal(400, (await ActAsync(s.Db, algiers, "shortfall/confirm", t, Answer(t, 0))).Status);
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));

        var (status, body) = await ActAsync(s.Db, algiers, "shortfall/confirm", t, Answer(t, 1));

        Assert.True(status == 200, body);
        var after = Root(body);
        Assert.Equal("Received", after.GetProperty("status").GetString());
        Assert.Equal(0m, D(after, "qtyShortPending"));
        Assert.False(after.GetProperty("canDecideShortfall").GetBoolean());
        var line = Line(body);
        Assert.Equal((1m, 1m, 0m, 0m), (D(line, "qtyLost"), D(line, "qtyShortConfirmed"), D(line, "qtyShortPending"), D(line, "inTransit")));
        Assert.Equal(19m, await CompanyTotalAsync(s.Db)); // the unit has left the company
        Assert.Equal(6m, await QtyAsync(s.Db, Algiers));

        // The same movement and the same entry a declared loss writes, under the confirmation's id.
        var confirm = await s.Db.StockTransferEvents.AsNoTracking().SingleAsync(e => e.Kind == StockTransferEventKind.ShortfallConfirmed);
        Assert.Equal(algiersUser.Id, confirm.UserId);
        var lost = await s.Db.StockMovements.AsNoTracking().SingleAsync(m => m.Reason.StartsWith(StockTransferReasons.Loss));
        Assert.Equal((Algiers, 0m, 1m, confirm.Id), (lost.BranchId, lost.QtyDelta, lost.MovedQty, lost.DocumentId));
        Assert.Contains("carton missing", lost.Reason); // the destination's words, when the source adds none
        var entry = Assert.Single(await EntriesAsync(s.Db, LedgerSources.StockTransferLoss));
        Assert.Equal(confirm.Id, entry.SourceId);
        Assert.False(entry.IsInterBranch);
        Assert.Equal(Algiers, entry.BranchId);
        Assert.Equal(600m, await BalanceAsync(s.Db, LedgerAccounts.StockLoss, Algiers));
        // Algiers: −1 800 for the three received, −600 written off.
        Assert.Equal(-2400m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Algiers));
        Assert.Equal(1800m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Oran));
        await AssertLedgerBalancedAsync(s.Db);

        // Answered: there is nothing left to confirm or reject.
        Assert.Equal(409, (await ActAsync(s.Db, algiers, "shortfall/reject", t, Answer(t, 1, "no"))).Status);
    }

    [Fact]
    public async Task A_write_off_holder_confirms_any_transfer_when_company_scoped_never_as_the_destination()
    {
        await using var s = await NewAsync();
        var constantine = Guid.NewGuid();
        s.Db.Branches.Add(new Branch { Id = constantine, NameFr = "Constantine", IsOpenForOperation = true });
        await s.Db.SaveChangesAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var (_, oranManager) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);
        var (_, thirdManager) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, constantine);
        var oranHolder = TillHarness.WithGrant(oranManager, PermissionCatalog.InventoryTransferWriteOff);
        var thirdHolder = TillHarness.WithGrant(thirdManager, PermissionCatalog.InventoryTransferWriteOff);
        var controller = await CompanyUserAsync(s.Db, PermissionCatalog.InventoryView, PermissionCatalog.InventoryTransferWriteOff);
        var clerk = await CompanyUserAsync(s.Db, PermissionCatalog.InventoryView, PermissionCatalog.InventoryTransfer);
        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);
        Assert.Equal(200, (await ActAsync(s.Db, oranManager, "receive", t, Receipt(t, 2, 2))).Status);

        // The destination never answers its own report, key or not; a branch-locked holder elsewhere does
        // not see the transfer; a company-wide user without the key may not answer for the source.
        Assert.Equal(403, (await ActAsync(s.Db, oranHolder, "shortfall/confirm", t, Answer(t, 1))).Status);
        Assert.Equal(404, (await ActAsync(s.Db, thirdHolder, "shortfall/confirm", t, Answer(t, 1))).Status);
        Assert.Equal(403, (await ActAsync(s.Db, clerk, "shortfall/confirm", t, Answer(t, 1))).Status);
        var (_, seen) = await Call(s.Db, clerk, "GET", Route + "/{id:guid}", "", Id(t));
        Assert.False(Root(seen).GetProperty("canDecideShortfall").GetBoolean());
        (_, seen) = await Call(s.Db, controller, "GET", Route + "/{id:guid}", "", Id(t));
        Assert.True(Root(seen).GetProperty("canDecideShortfall").GetBoolean());
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));

        // A company-scoped holder who is not the Admin confirms part of it, with a remark; then the Admin the rest.
        var (status, body) = await ActAsync(s.Db, controller, "shortfall/confirm", t, Answer(t, 1, "agreed with the driver"));
        Assert.True(status == 200, body);
        Assert.Equal(1m, D(Line(body), "qtyShortPending"));
        Assert.Equal("PartlyReceived", Root(body).GetProperty("status").GetString());
        Assert.Equal(19m, await CompanyTotalAsync(s.Db));
        await AssertLedgerBalancedAsync(s.Db);

        (status, body) = await ActAsync(s.Db, admin, "shortfall/confirm", t, Answer(t, 1));
        Assert.True(status == 200, body);
        Assert.Equal("Received", Root(body).GetProperty("status").GetString());
        Assert.Equal(18m, await CompanyTotalAsync(s.Db));
        Assert.Equal(2, (await EntriesAsync(s.Db, LedgerSources.StockTransferLoss)).Count);
        Assert.Equal(1200m, await BalanceAsync(s.Db, LedgerAccounts.StockLoss, Algiers));
        Assert.Equal(-2400m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Algiers));
        await AssertLedgerBalancedAsync(s.Db);
        var remarks = await s.Db.StockTransferEvents.AsNoTracking()
            .Where(e => e.Kind == StockTransferEventKind.ShortfallConfirmed).Select(e => e.Reason).ToListAsync();
        Assert.Contains("agreed with the driver", remarks);
    }

    [Fact]
    public async Task A_rejected_shortfall_stays_in_transit_and_the_destination_receives_it_later()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var (_, algiers) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Algiers);
        var (_, oran) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);
        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);
        Assert.Equal(200, (await ActAsync(s.Db, oran, "receive", t, Receipt(t, 3, 1))).Status);

        // A rejection says why.
        Assert.Equal(400, (await ActAsync(s.Db, algiers, "shortfall/reject", t, Answer(t, 1, "  "))).Status);

        var (status, body) = await ActAsync(s.Db, algiers, "shortfall/reject", t, Answer(t, 1, "four were loaded, look again"));

        Assert.True(status == 200, body);
        var after = Root(body);
        Assert.Equal("PartlyReceived", after.GetProperty("status").GetString());
        Assert.Equal(0m, D(after, "qtyShortPending"));
        Assert.Equal(1m, D(after, "inTransit"));
        var line = Line(body);
        Assert.Equal((1m, 1m, 0m), (D(line, "qtyShortReported"), D(line, "qtyShortRejected"), D(line, "qtyLost")));
        var rejected = after.GetProperty("events").EnumerateArray().Last();
        Assert.Equal(("ShortfallRejected", "four were loaded, look again"),
            (rejected.GetProperty("kind").GetString(), rejected.GetProperty("reason").GetString()));
        // Nothing moved, nothing posted: still in transit, still the source's.
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));
        Assert.False(await s.Db.StockMovements.AsNoTracking().AnyAsync(m => m.Reason.StartsWith(StockTransferReasons.Loss)));
        Assert.Empty(await EntriesAsync(s.Db, LedgerSources.StockTransferLoss));
        Assert.Equal(-1800m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Algiers));
        await AssertLedgerBalancedAsync(s.Db);

        // The destination finds the carton and receives it: the transfer closes and the books follow.
        (status, body) = await ActAsync(s.Db, oran, "receive", t, Receipt(t, 1, 0, reason: null));
        Assert.True(status == 200, body);
        Assert.Equal("Received", Root(body).GetProperty("status").GetString());
        Assert.Equal(14m, await QtyAsync(s.Db, Oran));
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));
        Assert.Equal(2, (await EntriesAsync(s.Db, LedgerSources.StockTransfer)).Count);
        Assert.Equal(2400m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Oran));
        Assert.Equal(-2400m, await BalanceAsync(s.Db, LedgerAccounts.Inventory, Algiers));
        await AssertLedgerBalancedAsync(s.Db);
    }

    [Fact]
    public async Task A_user_at_neither_end_gets_404_and_the_destination_cannot_answer()
    {
        await using var s = await NewAsync();
        var constantine = Guid.NewGuid();
        s.Db.Branches.Add(new Branch { Id = constantine, NameFr = "Constantine", IsOpenForOperation = true });
        await s.Db.SaveChangesAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var (_, oran) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);
        var (_, thirdManager) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, constantine);
        var third = TillHarness.WithGrant(thirdManager, PermissionCatalog.InventoryTransferWriteOff);
        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);

        Assert.Equal(404, (await ActAsync(s.Db, third, "receive", t, Receipt(t, 3, 1))).Status);
        Assert.Equal(200, (await ActAsync(s.Db, oran, "receive", t, Receipt(t, 3, 1))).Status);
        Assert.Equal(404, (await ActAsync(s.Db, third, "shortfall/confirm", t, Answer(t, 1))).Status);
        Assert.Equal(404, (await ActAsync(s.Db, third, "shortfall/reject", t, Answer(t, 1, "not ours"))).Status);
        Assert.Equal(404, (await Call(s.Db, third, "GET", Route + "/{id:guid}", "", Id(t))).Status);

        // The branch that reported it cannot settle it either way, and is told why.
        var (status, body) = await ActAsync(s.Db, oran, "shortfall/confirm", t, Answer(t, 1));
        Assert.Equal(403, status);
        Assert.Contains("dispatched", body);
        Assert.Equal(403, (await ActAsync(s.Db, oran, "shortfall/reject", t, Answer(t, 1, "found it"))).Status);

        // Nothing changed.
        var (_, seen) = await Call(s.Db, admin, "GET", Route + "/{id:guid}", "", Id(t));
        Assert.Equal(1m, D(Root(seen), "qtyShortPending"));
        Assert.True(Root(seen).GetProperty("canDecideShortfall").GetBoolean());
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));
        Assert.Empty(await EntriesAsync(s.Db, LedgerSources.StockTransferLoss));
        await AssertLedgerBalancedAsync(s.Db);
    }

    [Fact]
    public async Task Goods_reported_short_that_turn_up_clear_the_report_and_a_cancel_answers_it()
    {
        await using var s = await NewAsync();
        var (_, admin) = await BranchLockTests.UserAsync(s.Db, UserRole.Admin, Algiers);
        var (_, algiers) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Algiers);
        var (_, oran) = await BranchLockTests.UserAsync(s.Db, UserRole.Manager, Oran);

        // The carton reported short turns up: received after all, the report is cleared, nothing is lost.
        var t = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);
        Assert.Equal(200, (await ActAsync(s.Db, oran, "receive", t, Receipt(t, 3, 1))).Status);
        var (status, body) = await ActAsync(s.Db, oran, "receive", t, Receipt(t, 1, 0, reason: null));
        Assert.True(status == 200, body);
        Assert.Equal("Received", Root(body).GetProperty("status").GetString());
        Assert.Equal((0m, 1m, 0m), (D(Line(body), "qtyShortPending"), D(Line(body), "qtyShortReported"), D(Line(body), "qtyLost")));
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));

        // Nothing arrived at all and the source calls it off: the goods go back and the report is answered.
        var nothing = await DispatchAsync(s.Db, admin, Algiers, Oran, 4);
        (status, body) = await ActAsync(s.Db, oran, "receive", nothing, Receipt(nothing, 0, 4, "the truck never came"));
        Assert.True(status == 200, body);
        Assert.Equal("Dispatched", Root(body).GetProperty("status").GetString()); // a report settles nothing
        Assert.Equal(4m, D(Root(body), "qtyShortPending"));
        (status, body) = await ActAsync(s.Db, algiers, "cancel", nothing, """{"reason":"never loaded"}""");
        Assert.True(status == 200, body);
        Assert.Equal("Cancelled", Root(body).GetProperty("status").GetString());
        Assert.Equal(0m, D(Root(body), "qtyShortPending"));
        Assert.Equal(6m, await QtyAsync(s.Db, Algiers));
        Assert.Equal(20m, await CompanyTotalAsync(s.Db));
        Assert.Empty(await EntriesAsync(s.Db, LedgerSources.StockTransferLoss));
        await AssertLedgerBalancedAsync(s.Db);
    }
}
