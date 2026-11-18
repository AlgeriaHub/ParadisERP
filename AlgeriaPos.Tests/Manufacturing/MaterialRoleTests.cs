using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Manufacturing.MfgHarness;

namespace AlgeriaPos.Tests.Manufacturing;

/// <summary>
/// A product's manufacturing role and units (Phase 1 §2–3): existing products keep behaving exactly as
/// before, the till stops offering the workshop's materials, a sale is never refused for it, and a unit
/// cannot be changed under quantities already counted in it.
/// </summary>
public class MaterialRoleTests
{
    private const string Material = "/api/manufacturing/materials/{productId:guid}";
    private static (string, string) Of(Guid product) => ("productId", product.ToString());

    [Fact]
    public void Only_raw_materials_consumables_and_semi_finished_goods_are_kept_off_the_till()
    {
        Assert.Equal(default, (ManufacturingRole)0);
        Assert.Equal(ManufacturingRole.None, default(ManufacturingRole));
        var hidden = Enum.GetValues<ManufacturingRole>().Where(r => !ManufacturingRoles.SoldAtTill(r)).ToList();
        Assert.Equal([ManufacturingRole.RawMaterial, ManufacturingRole.Consumable, ManufacturingRole.SemiFinished], hidden);
    }

    [Fact]
    public async Task An_existing_product_is_untouched_and_a_role_change_reaches_the_till_on_its_next_pull()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var sync = TillHarness.NewService(s.Db);

        var before = await s.Db.Products.SingleAsync(p => p.Id == TillHarness.Pipe);
        Assert.Equal((ManufacturingRole.None, (Guid?)null, "pcs"), (before.ManufacturingRole, before.BaseUnitId, before.Unit));
        // Saved once through the catalogue, as every real product has been: it has a version to pull.
        await s.Db.SaveWithVersionsAsync(CancellationToken.None, before);
        s.Db.ChangeTracker.Clear();
        var first = await sync.PullAsync(0, "C01", CancellationToken.None);
        Assert.False(first.Products.Single(p => p.Id == TillHarness.Pipe).HiddenAtTill);

        var classified = await OkAsync(s.Db, admin, "PUT", Material, """{"role":"RawMaterial","baseUnitId":null,"units":[]}""", Of(TillHarness.Pipe));
        Assert.False(classified.GetProperty("soldAtTill").GetBoolean());

        // A new version: a till pulling from where it stopped gets the product again, now hidden.
        var next = await sync.PullAsync(first.Cursor, "C01", CancellationToken.None);
        var pipe = next.Products.Single(p => p.Id == TillHarness.Pipe);
        Assert.True(pipe.HiddenAtTill);
        Assert.Equal(10m, pipe.StockQty);

        // A sale that names it anyway — an older till, an offline basket — is accepted: a sale is never refused.
        var pushed = await sync.PushAsync(new PushRequest("C01", [TillHarness.Sale("T-RAW-1", 1)]), "C01", CancellationToken.None);
        Assert.Equal(1, pushed.Accepted);

        // Finished goods are sold at the till.
        await OkAsync(s.Db, admin, "PUT", Material, """{"role":"Finished","baseUnitId":null,"units":[]}""", Of(TillHarness.Pipe));
        Assert.False((await sync.PullAsync(next.Cursor, "C01", CancellationToken.None)).Products.Single(p => p.Id == TillHarness.Pipe).HiddenAtTill);
    }

    [Fact]
    public async Task A_base_unit_is_set_once_and_stays_once_stock_is_counted_in_it()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);

        // The pipes already on the shelves were counted in pieces: naming that unit changes nothing.
        var set = await OkAsync(s.Db, admin, "PUT", Material,
            $$"""{"role":"None","baseUnitId":"{{SystemUnits.Piece}}","units":[]}""", Of(TillHarness.Pipe));
        Assert.Equal("pcs", set.GetProperty("baseUnitCode").GetString());
        Assert.Equal(10m, (await s.Db.ProductStocks.AsNoTracking().SingleAsync(x => x.ProductId == TillHarness.Pipe && x.BranchId == TillHarness.Algiers)).StockQty);

        // Moving it to kilograms would turn ten pipes into ten kilograms: refused.
        await RefusedAsync(s.Db, admin, 409, "base_unit_locked", "PUT", Material,
            $$"""{"role":"None","baseUnitId":"{{SystemUnits.Kilogram}}","units":[]}""", Of(TillHarness.Pipe));
    }

    [Fact]
    public async Task A_product_s_own_unit_factors_are_checked_against_the_standard_ones()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var wire = await ProductAsync(s.Db, "WIRE", ManufacturingRole.RawMaterial, null);

        await RefusedAsync(s.Db, admin, 400, "base_unit_required", "PUT", Material,
            $$"""{"role":"RawMaterial","baseUnitId":null,"units":[{"unitId":"{{SystemUnits.Gram}}","factorToBase":0.001}]}""", Of(wire.Id));
        // 1 g is 0.001 kg by definition; a product cannot make it 0.002.
        await RefusedAsync(s.Db, admin, 400, "factor_contradicts_standard", "PUT", Material,
            $$"""{"role":"RawMaterial","baseUnitId":"{{SystemUnits.Kilogram}}","units":[{"unitId":"{{SystemUnits.Gram}}","factorToBase":0.002}]}""", Of(wire.Id));
        await RefusedAsync(s.Db, admin, 400, "invalid_factor", "PUT", Material,
            $$"""{"role":"RawMaterial","baseUnitId":"{{SystemUnits.Kilogram}}","units":[{"unitId":"{{SystemUnits.Piece}}","factorToBase":-1}]}""", Of(wire.Id));

        // A piece of this wire is a 1.5 kg coil end: a product factor across dimensions is the product's to state.
        var saved = await OkAsync(s.Db, admin, "PUT", Material,
            $$"""{"role":"RawMaterial","baseUnitId":"{{SystemUnits.Kilogram}}","units":[{"unitId":"{{SystemUnits.Piece}}","factorToBase":1.5,"forPurchase":true}]}""", Of(wire.Id));
        Assert.Equal(1.5m, saved.GetProperty("units")[0].GetProperty("factorToBase").GetDecimal());
        var converted = await OkAsync(s.Db, admin, "GET", "/api/manufacturing/units/convert",
            $"?quantity=4&fromUnitId={SystemUnits.Piece}&toUnitId={SystemUnits.Gram}&productId={wire.Id}");
        Assert.Equal(6000m, converted.GetProperty("result").GetDecimal());
    }

    [Fact]
    public async Task A_role_stays_what_the_recipes_and_lines_that_name_the_product_need()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var wire = await ProductAsync(s.Db, "WIRE", ManufacturingRole.RawMaterial, SystemUnits.Kilogram);
        var nails = await ProductAsync(s.Db, "NAILS", ManufacturingRole.Finished, SystemUnits.Kilogram);
        await OkAsync(s.Db, admin, "POST", "/api/manufacturing/boms",
            $$"""{"productId":"{{nails.Id}}","batchQuantity":1,"batchUnitId":"{{SystemUnits.Kilogram}}","lines":[{"componentProductId":"{{wire.Id}}","quantity":1.05,"unitId":"{{SystemUnits.Kilogram}}"}]}""");

        await RefusedAsync(s.Db, admin, 409, "role_in_use", "PUT", Material,
            $$"""{"role":"Finished","baseUnitId":"{{SystemUnits.Kilogram}}","units":[]}""", Of(wire.Id));
        await RefusedAsync(s.Db, admin, 409, "role_in_use", "PUT", Material,
            $$"""{"role":"RawMaterial","baseUnitId":"{{SystemUnits.Kilogram}}","units":[]}""", Of(nails.Id));
    }
}
