using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Security;

/// <summary>
/// A till is a semi-trusted device: it sits on a counter, it holds a 30-day token, and one of
/// them will eventually be in the wrong hands. What it must not be able to do is act as a
/// <i>different</i> till.
///
/// <para>
/// Both sync routes used to take the terminal code from the caller — a query parameter on pull,
/// a body field on push — while the token already carried the terminal the session was opened
/// for. That one substitution let a till at one branch read another branch's stock position,
/// move that branch's stock, cash box and ledger, and occupy the receipt numbers that branch's
/// real till was about to use, which would have had its genuine sales bounce off the unique
/// index as duplicates. These pin the binding to the token.
/// </para>
/// </summary>
public class SyncDeviceBindingTests
{
    private static SalePush CashSale(string reference, long localSeq) =>
        TillHarness.Sale(reference, localSeq, payments: TillHarness.Tender("cash", 1000m));

    [Fact]
    public async Task A_push_is_recorded_against_the_token_s_terminal_not_the_one_in_the_body()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);

        // The Algiers till, holding a valid C01 token, claims to be Oran's C02.
        var response = await sync.PushAsync(new PushRequest("C02", [CashSale("T-0001", 1)]), "C01", CancellationToken.None);

        Assert.Equal(1, response.Accepted);
        var sale = await db.Sales.AsNoTracking().SingleAsync();
        Assert.Equal("C01", sale.DeviceCode);
        Assert.Equal(TillHarness.Algiers, sale.BranchId);

        // Oran's shelf, cash box and ledger are untouched; Algiers took the sale.
        var stock = await db.ProductStocks.AsNoTracking().ToDictionaryAsync(s => s.BranchId, s => s.StockQty);
        Assert.Equal(9m, stock[TillHarness.Algiers]);
        Assert.Equal(10m, stock[TillHarness.Oran]);
        Assert.Equal(0m, await TillHarness.CashInBoxAsync(db, TillHarness.Oran));
        Assert.Equal(1000m, await TillHarness.CashInBoxAsync(db, TillHarness.Algiers));
        Assert.Equal(TillHarness.Algiers,
            (await db.LedgerEntries.AsNoTracking().SingleAsync(e => e.SourceType == LedgerSources.Sale)).BranchId);
    }

    [Fact]
    public async Task A_till_cannot_occupy_another_terminal_s_receipt_numbers()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);

        // Sales.(DeviceCode, Reference) and (DeviceCode, LocalSeq) are unique. Squatting on
        // C02's sequence would make C02's own sales collide and come back rejected — the
        // other branch's day of takings never reaching the server at all.
        await sync.PushAsync(new PushRequest("C02", [CashSale("T-0001", 1)]), "C01", CancellationToken.None);
        var victim = await sync.PushAsync(new PushRequest("C02", [CashSale("T-0001", 1)]), "C02", CancellationToken.None);

        Assert.Equal(1, victim.Accepted);
        Assert.Equal(0, victim.Rejected);
        Assert.Equal(2, await db.Sales.CountAsync());
    }

    [Fact]
    public async Task A_pull_returns_the_stock_of_the_token_s_own_branch()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var sync = TillHarness.NewService(db);

        // Oran sells two pipes, so the two branches now hold different quantities.
        await sync.PushAsync(new PushRequest("C02", [CashSale("O-0001", 1)]), "C02", CancellationToken.None);
        await sync.PushAsync(new PushRequest("C02", [CashSale("O-0002", 2)]), "C02", CancellationToken.None);

        // Seeing another branch's position is exactly what Permissions.ViewOtherBranchStock
        // gates on /api/catalog/stock/availability; a pull must not be a way round it.
        Assert.Equal(10m, (await sync.PullAsync(0, "C01", CancellationToken.None)).Products.Single(p => p.Id == TillHarness.Pipe).StockQty);
        Assert.Equal(8m, (await sync.PullAsync(0, "C02", CancellationToken.None)).Products.Single(p => p.Id == TillHarness.Pipe).StockQty);

        // A pull also stamps the terminal's last-seen cursor. Only its own.
        var devices = await db.Devices.AsNoTracking().ToDictionaryAsync(d => d.Code, d => d.LastSyncAtUtc);
        Assert.NotNull(devices["C01"]);
        Assert.NotNull(devices["C02"]);
    }
}
