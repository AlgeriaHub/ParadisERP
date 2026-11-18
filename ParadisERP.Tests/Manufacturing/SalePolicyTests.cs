using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Sync;
using ParadisERP.Tests.Receivables;
using ParadisERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Manufacturing.MfgHarness;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// The sale policy for manufacturing items (hardening review, 2026-10-26; ManufacturingRoles.ForSale): the
/// till offers only what is for sale, a document typed for a customer refuses the rest, a till's sale —
/// online or queued offline — is never refused for it but is said and logged, and nothing already recorded
/// is re-judged.
/// </summary>
public class SalePolicyTests
{
    private const string Material = "/api/manufacturing/materials/{productId:guid}";
    private static (string, string) Of(Guid product) => ("productId", product.ToString());

    [Fact]
    public void Accessories_spare_parts_and_finished_goods_are_for_sale_and_a_material_only_when_marked()
    {
        foreach (var role in Enum.GetValues<ManufacturingRole>())
        {
            var counter = role is ManufacturingRole.None or ManufacturingRole.Accessory or ManufacturingRole.SparePart or ManufacturingRole.Finished;
            Assert.Equal(counter, ManufacturingRoles.ForSale(role, alsoForSale: false));
            // The product's own mark makes any role sellable; it never takes a counter role off sale.
            Assert.True(ManufacturingRoles.ForSale(role, alsoForSale: true));
            Assert.Equal(counter, ManufacturingRoles.ForSale(new Product { ManufacturingRole = role }));
        }
    }

    [Fact]
    public async Task A_material_marked_for_sale_comes_back_to_the_till_and_the_mark_is_dropped_for_a_counter_role()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var sync = TillHarness.NewService(s.Db);
        await s.Db.SaveWithVersionsAsync(CancellationToken.None, await s.Db.Products.SingleAsync(p => p.Id == TillHarness.Pipe));
        s.Db.ChangeTracker.Clear();
        var start = await sync.PullAsync(0, "C01", CancellationToken.None);

        var raw = await OkAsync(s.Db, admin, "PUT", Material, """{"role":"RawMaterial","baseUnitId":null,"units":[]}""", Of(TillHarness.Pipe));
        Assert.False(raw.GetProperty("soldAtTill").GetBoolean());
        var hidden = await sync.PullAsync(start.Cursor, "C01", CancellationToken.None);
        Assert.True(hidden.Products.Single(p => p.Id == TillHarness.Pipe).HiddenAtTill);

        // Surplus sold to another workshop: marked for sale, the till offers it again on its next pull.
        var marked = await OkAsync(s.Db, admin, "PUT", Material,
            """{"role":"RawMaterial","baseUnitId":null,"units":[],"alsoForSale":true}""", Of(TillHarness.Pipe));
        Assert.True(marked.GetProperty("alsoForSale").GetBoolean());
        Assert.True(marked.GetProperty("soldAtTill").GetBoolean());
        var shown = await sync.PullAsync(hidden.Cursor, "C01", CancellationToken.None);
        Assert.False(shown.Products.Single(p => p.Id == TillHarness.Pipe).HiddenAtTill);

        // A client that predates the mark sends none: the mark stays as it was.
        var kept = await OkAsync(s.Db, admin, "PUT", Material, """{"role":"RawMaterial","baseUnitId":null,"units":[]}""", Of(TillHarness.Pipe));
        Assert.True(kept.GetProperty("alsoForSale").GetBoolean());

        // For a role the counter sells anyway the mark means nothing, and it is not kept.
        var finished = await OkAsync(s.Db, admin, "PUT", Material,
            """{"role":"Finished","baseUnitId":null,"units":[],"alsoForSale":true}""", Of(TillHarness.Pipe));
        Assert.False(finished.GetProperty("alsoForSale").GetBoolean());
        Assert.True(finished.GetProperty("soldAtTill").GetBoolean());
    }

    [Fact]
    public async Task The_mark_can_change_while_a_recipe_uses_the_material_whose_role_cannot()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var wire = await ProductAsync(s.Db, "WIRE", ManufacturingRole.RawMaterial, SystemUnits.Kilogram);
        var nails = await ProductAsync(s.Db, "NAILS", ManufacturingRole.Finished, SystemUnits.Kilogram);
        await OkAsync(s.Db, admin, "POST", "/api/manufacturing/boms",
            $$"""{"productId":"{{nails.Id}}","batchQuantity":1,"batchUnitId":"{{SystemUnits.Kilogram}}","lines":[{"componentProductId":"{{wire.Id}}","quantity":1.05,"unitId":"{{SystemUnits.Kilogram}}"}]}""");

        var marked = await OkAsync(s.Db, admin, "PUT", Material,
            $$"""{"role":"RawMaterial","baseUnitId":"{{SystemUnits.Kilogram}}","units":[],"alsoForSale":true}""", Of(wire.Id));
        Assert.True(marked.GetProperty("soldAtTill").GetBoolean());
        Assert.Equal(ManufacturingRole.RawMaterial, (await s.Db.Products.AsNoTracking().SingleAsync(p => p.Id == wire.Id)).ManufacturingRole);
    }

    [Fact]
    public async Task A_till_sale_of_a_material_online_or_queued_offline_is_recorded_said_and_its_refund_is_not_judged()
    {
        await using var s = await NewAsync();
        var raw = await s.Db.Products.SingleAsync(p => p.Id == TillHarness.Pipe);
        raw.ManufacturingRole = ManufacturingRole.RawMaterial;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var sync = TillHarness.NewService(s.Db);

        // Two sales of the material: one made a minute ago, one queued offline two days ago and only now
        // sent. Both are the money taken and the receipt printed: neither is refused.
        var online = TillHarness.Sale("T-MAT-1", 1);
        var offline = TillHarness.Sale("T-MAT-0", 3) with { SoldAtUtc = DateTime.UtcNow.AddDays(-2) };
        var pushed = await sync.PushAsync(new PushRequest("C01", [online, offline]), "C01", CancellationToken.None);
        Assert.Equal(2, pushed.Accepted);
        Assert.All(pushed.Results, r =>
        {
            Assert.Equal("accepted", r.Status);
            Assert.Contains(SyncService.NotForSaleWarning, r.Message ?? "");
            Assert.Contains("PIPE-90", r.Message ?? "");
        });
        // Recorded exactly as made: the stock left the shop.
        Assert.Equal(8m, (await s.Db.ProductStocks.AsNoTracking().SingleAsync(x => x.ProductId == TillHarness.Pipe && x.BranchId == TillHarness.Algiers)).StockQty);
        Assert.Equal(2, await s.Db.Sales.CountAsync());

        // Giving one back is not a sale: accepted, with no word about the policy.
        var refund = TillHarness.Sale("T-MAT-R", 4) with
        {
            Id = Guid.NewGuid(), Status = nameof(SaleStatus.Refunded), RefundOfSaleId = online.Id,
        };
        var back = await sync.PushAsync(new PushRequest("C01", [refund]), "C01", CancellationToken.None);
        Assert.Equal(1, back.Accepted);
        Assert.DoesNotContain(SyncService.NotForSaleWarning, back.Results.Single().Message ?? "");
        Assert.Equal(9m, (await s.Db.ProductStocks.AsNoTracking().SingleAsync(x => x.ProductId == TillHarness.Pipe && x.BranchId == TillHarness.Algiers)).StockQty);
    }

    [Fact]
    public async Task Every_change_to_whether_a_product_is_for_sale_is_audited_with_who_and_what()
    {
        await using var s = await NewAsync();
        var (adminUser, admin) = await AdminAsync(s.Db);

        await OkAsync(s.Db, admin, "PUT", Material, """{"role":"RawMaterial","baseUnitId":null,"units":[]}""", Of(TillHarness.Pipe));
        await OkAsync(s.Db, admin, "PUT", Material, """{"role":"RawMaterial","baseUnitId":null,"units":[],"alsoForSale":true}""", Of(TillHarness.Pipe));
        // Saved again with nothing about the sale status changed: no row.
        await OkAsync(s.Db, admin, "PUT", Material, """{"role":"RawMaterial","baseUnitId":null,"units":[]}""", Of(TillHarness.Pipe));
        await OkAsync(s.Db, admin, "PUT", Material, """{"role":"RawMaterial","baseUnitId":null,"units":[],"alsoForSale":false}""", Of(TillHarness.Pipe));

        var changes = await s.Db.ProductSaleStatusChanges.AsNoTracking().Where(c => c.ProductId == TillHarness.Pipe)
            .OrderBy(c => c.ChangedAtUtc).ToListAsync();
        Assert.Equal(
            [
                (ManufacturingRole.None, ManufacturingRole.RawMaterial, false, false, true, false),
                (ManufacturingRole.RawMaterial, ManufacturingRole.RawMaterial, false, true, false, true),
                (ManufacturingRole.RawMaterial, ManufacturingRole.RawMaterial, true, false, true, false),
            ],
            changes.Select(c => (c.OldRole, c.NewRole, c.OldAlsoForSale, c.NewAlsoForSale, c.OldForSale, c.NewForSale)));
        Assert.All(changes, c =>
        {
            Assert.Equal(adminUser.Id, c.UserId);
            Assert.True(c.ChangedAtUtc > DateTime.UtcNow.AddMinutes(-5));
        });
    }

    [Fact]
    public async Task A_till_sale_of_a_material_is_recorded_as_an_exception_telling_stale_offline_data_from_a_sale_after_withdrawal()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var cashier = new User { Username = "caisse-1", FullName = "Amina Caisse", Role = UserRole.Cashier, BranchId = TillHarness.Algiers };
        s.Db.Users.Add(cashier);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        // Withdrawn from sale now, through the screen, so the history knows when.
        await OkAsync(s.Db, admin, "PUT", Material, """{"role":"RawMaterial","baseUnitId":null,"units":[]}""", Of(TillHarness.Pipe));
        var sync = TillHarness.NewService(s.Db);
        var pusher = Guid.NewGuid();
        var actor = new SyncActor(pusher, "Session", true, true);

        // Queued offline two days ago, while it was still for sale; and one sold after the withdrawal.
        var queued = TillHarness.Sale("T-Q-1", 1) with { SoldAtUtc = DateTime.UtcNow.AddDays(-2), CashierUserId = cashier.Id };
        var after = TillHarness.Sale("T-A-1", 2) with { CashierUserId = cashier.Id };
        var pushed = await sync.PushAsync(new PushRequest("C01", [queued, after]), "C01", CancellationToken.None, actor);
        Assert.Equal(2, pushed.Accepted);

        var rows = await s.Db.ManufacturingSaleExceptions.AsNoTracking().OrderBy(e => e.SoldAtUtc).ToListAsync();
        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.Equal((queued.Id, queued.Lines![0].Id, TillHarness.Pipe, "PIPE-90", TillHarness.Algiers, "C01", "T-Q-1", 1m),
            (first.SaleId, first.SaleLineId, first.ProductId, first.Sku, first.BranchId, first.DeviceCode, first.Reference, first.Quantity));
        Assert.Equal((cashier.Id, "Amina Caisse", (Guid?)pusher, ManufacturingRole.RawMaterial),
            (first.CashierUserId, first.CashierName, first.PushedByUserId, first.Role));
        Assert.True(first.ReceivedAtUtc - first.SoldAtUtc > TimeSpan.FromDays(1));
        Assert.NotNull(first.WithdrawnAtUtc);
        Assert.True(first.SoldBeforeWithdrawal);
        Assert.False(rows[1].SoldBeforeWithdrawal);
        Assert.Equal(rows[0].WithdrawnAtUtc, rows[1].WithdrawnAtUtc);

        // The sale itself is untouched: its money and its stock movement stand.
        Assert.Equal(8m, (await s.Db.ProductStocks.AsNoTracking().SingleAsync(x => x.ProductId == TillHarness.Pipe && x.BranchId == TillHarness.Algiers)).StockQty);
        Assert.Equal(2000m, (await s.Db.Sales.AsNoTracking().Select(x => x.GrandTotalTtc).ToListAsync()).Sum());

        // Sent again: a duplicate, with no second exception. A refund adds none.
        await sync.PushAsync(new PushRequest("C01", [after]), "C01", CancellationToken.None, actor);
        var refund = TillHarness.Sale("T-A-R", 3) with { Id = Guid.NewGuid(), Status = nameof(SaleStatus.Refunded), RefundOfSaleId = after.Id, CashierUserId = cashier.Id };
        Assert.Equal(1, (await sync.PushAsync(new PushRequest("C01", [refund]), "C01", CancellationToken.None, actor)).Accepted);
        Assert.Equal(2, await s.Db.ManufacturingSaleExceptions.CountAsync());
    }

    [Theory]
    [InlineData(ManufacturingRole.None, false)]
    [InlineData(ManufacturingRole.Accessory, false)]
    [InlineData(ManufacturingRole.SparePart, false)]
    [InlineData(ManufacturingRole.Finished, false)]
    [InlineData(ManufacturingRole.RawMaterial, true)]
    [InlineData(ManufacturingRole.SemiFinished, true)]
    public async Task A_sale_of_something_for_sale_carries_no_policy_word_and_no_exception(ManufacturingRole role, bool alsoForSale)
    {
        await using var s = await NewAsync();
        var product = await s.Db.Products.SingleAsync(p => p.Id == TillHarness.Pipe);
        product.ManufacturingRole = role;
        product.AlsoForSale = alsoForSale;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var pushed = await TillHarness.NewService(s.Db).PushAsync(new PushRequest("C01", [TillHarness.Sale("T-OK-1", 1)]), "C01", CancellationToken.None);
        Assert.Equal(1, pushed.Accepted);
        Assert.DoesNotContain(SyncService.NotForSaleWarning, pushed.Results.Single().Message ?? "");
        Assert.False(await s.Db.ManufacturingSaleExceptions.AnyAsync());
    }

    [Fact]
    public async Task A_document_typed_for_a_customer_refuses_a_material_and_an_invoice_of_a_recorded_sale_does_not()
    {
        await using var h = await ReceivableHarness.NewAsync();
        // Sold at the till while it was ordinary merchandise; then classified as a raw material. (Read after
        // the push: the push clears the tracker.)
        var saleId = await h.PushSaleAsync(total: 10_000m, cash: 0m, customerId: ReceivableHarness.Contractor);
        var concrete = await h.Db.Products.SingleAsync(p => p.Id == ReceivableHarness.Concrete);
        concrete.ManufacturingRole = ManufacturingRole.RawMaterial;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        // The recorded sale is history: it is invoiced as it was made.
        var fromSale = await h.Receivables.CreateInvoiceAsync(new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), SaleId: saleId), "admin");
        Assert.True(fromSale.Ok, fromSale.Message);

        // A new document typed for a customer is a new sale: refused.
        CreateInvoiceRequest Typed() => new(DateTime.UtcNow.AddDays(30), ReceivableHarness.Contractor, ReceivableHarness.Algiers,
            Lines: [new InvoiceLineRequest(ReceivableHarness.Concrete, null, null, 1m, 9_000m, 19m)]);
        var typed = await h.Receivables.CreateInvoiceAsync(Typed(), "admin");
        Assert.False(typed.Ok);
        Assert.Contains("not for sale", typed.Message ?? "");

        // Marked for sale, it is.
        concrete = await h.Db.Products.SingleAsync(p => p.Id == ReceivableHarness.Concrete);
        concrete.AlsoForSale = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        Assert.True((await h.Receivables.CreateInvoiceAsync(Typed(), "admin")).Ok);
    }
}
