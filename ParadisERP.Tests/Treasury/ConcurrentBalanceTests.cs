using System.Net.Http.Json;
using ParadisERP.Domain;
using ParadisERP.Tests.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Treasury;

/// <summary>
/// The running figures besides stock — a customer's debt, a gift card's balance, a cash box's
/// running balance — with several tills pushing at the same moment, on SQL Server.
/// <para>
/// Each was read, changed in memory and written back, so two pushes that both read a debt of
/// 100 both wrote 110: one sale on credit was never owed. The cash box was summed from its whole
/// history on every write, so two pushes that summed at the same moment both recorded the same
/// "balance after". SQLite lets one writer in at a time and cannot show either; these run where
/// two writers can meet. Same cause and same cure as <see cref="Stock.ConcurrentStockTests"/>.
/// </para>
/// <para>
/// The sales here are of an article with no stock kept. A sale with a stocked line takes the
/// version lock on its way through the stock (StockRows) before it reaches the customer, the
/// card or the box, so it was protected by accident; a sale of services only took no lock at all.
/// </para>
/// </summary>
[Collection(HostCollection.Name)]
public class ConcurrentBalanceTests
{
    private const int Tills = 8;
    private const int SalesPerTill = 6;
    private const int Sales = Tills * SalesPerTill;

    [SqlServerFact]
    public async Task Every_sale_on_credit_is_owed_and_every_cash_sale_moves_the_box_once()
    {
        await using var fleet = await TillFleet.StartAsync(Tills, openingStock: 1000m);
        var customer = await TillFleet.CreatedIdAsync(await fleet.Admin.PostAsJsonAsync("/api/catalog/customers", new
        {
            fullName = "Entreprise Benali", phone = (string?)null, address = (string?)null, taxId = (string?)null,
            email = (string?)null, birthDate = (DateTime?)null, creditLimit = 0m, kind = "Person",
        }));

        // 10 a sale: 4 in cash, 6 on the customer's account.
        await fleet.PushAllAtOnceAsync(SalesPerTill,
            (till, seq) => fleet.Sale(till, seq, customerId: customer, product: fleet.Service,
                payments: [("Cash", 4m, null), ("Credit", 6m, null)]));

        await using var db = fleet.Open();
        var owed = (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == customer)).Balance;
        Assert.Equal(6m * Sales, owed);

        var box = await db.CashBoxes.AsNoTracking().SingleAsync(b => b.BranchId == fleet.Branch && b.IsActive);
        var after = await db.CashMovements.AsNoTracking()
            .Where(m => m.CashBoxId == box.Id && m.Direction == CashDirection.In)
            .Select(m => m.BalanceAfter)
            .ToListAsync();
        Assert.Equal(Sales, after.Count);
        // Each sale saw the box the one before it left: 4, 8, 12 … and no figure twice.
        Assert.Equal(Enumerable.Range(1, Sales).Select(n => 4m * n), after.OrderBy(x => x));
    }

    [SqlServerFact]
    public async Task Every_purchase_on_a_gift_card_comes_off_the_card()
    {
        await using var fleet = await TillFleet.StartAsync(Tills, openingStock: 1000m);
        const decimal loaded = 1000m;
        var card = await TillFleet.CreatedIdAsync(await fleet.Admin.PostAsJsonAsync("/api/catalog/gift-cards",
            new { code = "CARTE-2026", amount = loaded, expiresAtUtc = (DateTime?)null, customerId = (Guid?)null }));

        await fleet.PushAllAtOnceAsync(SalesPerTill,
            (till, seq) => fleet.Sale(till, seq, product: fleet.Service, payments: [("GiftCard", 10m, "CARTE-2026")]));

        await using var db = fleet.Open();
        Assert.Equal(loaded - 10m * Sales, (await db.GiftCards.AsNoTracking().SingleAsync(g => g.Id == card)).Balance);

        var after = await db.GiftCardMovements.AsNoTracking()
            .Where(m => m.GiftCardId == card && m.Reason == "redeem")
            .Select(m => m.BalanceAfter)
            .ToListAsync();
        Assert.Equal(Enumerable.Range(1, Sales).Select(n => loaded - 10m * n).OrderBy(x => x), after.OrderBy(x => x));
    }
}
