using System.Security.Claims;
using System.Text.Json;
using ParadiERP.Domain;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Manufacturing.MfgHarness;

namespace ParadiERP.Tests.Manufacturing;

/// <summary>
/// Versioned bills of materials (Phase 1 §4): what a planned quantity needs, exactly; drafts that become
/// frozen versions; history that stays as it was defined; and recipes that cannot be written wrong.
/// </summary>
public class BillOfMaterialsTests
{
    private const string Boms = "/api/manufacturing/boms";
    private const string Bom = "/api/manufacturing/boms/{id:guid}";

    /// <summary>
    /// The nail workshop on line A: 100 kg of nails per batch from 105 kg of wire (2 % waste), 800 g of
    /// zinc (5 % waste, the zinc counted in kg) and 4 cartons. The nails are also sold by the 25 kg carton.
    /// </summary>
    private sealed record Workshop(MfgHarness.Setup S, ClaimsPrincipal Admin, Guid LineA, Product Nails, Product Wire, Product Zinc, Product Box, Guid Carton);

    private static async Task<Workshop> WorkshopAsync(MfgHarness.Setup s)
    {
        var (_, admin) = await AdminAsync(s.Db);
        var (a, _) = await ProductionLineTests.TwoLinesAsync(s, admin);
        var carton = await OkAsync(s.Db, admin, "POST", "/api/manufacturing/units",
            """{"code":"ctn","nameAr":"كرتونة","nameFr":"Carton","dimension":"Other","factorToReference":1}""");
        var nails = await ProductAsync(s.Db, "NAIL-70", ManufacturingRole.Finished, SystemUnits.Kilogram);
        await OkAsync(s.Db, admin, "PUT", "/api/manufacturing/materials/{productId:guid}",
            $$"""{"role":"Finished","baseUnitId":"{{SystemUnits.Kilogram}}","units":[{"unitId":"{{IdOf(carton)}}","factorToBase":25,"forSale":true}]}""",
            ("productId", nails.Id.ToString()));
        var wire = await ProductAsync(s.Db, "WIRE-3MM", ManufacturingRole.RawMaterial, SystemUnits.Kilogram);
        var zinc = await ProductAsync(s.Db, "ZINC", ManufacturingRole.Consumable, SystemUnits.Kilogram);
        var box = await ProductAsync(s.Db, "BOX-25", ManufacturingRole.Accessory, SystemUnits.Piece);
        await OkAsync(s.Db, admin, "PUT", "/api/manufacturing/lines/{id:guid}/products", $$"""{"productIds":["{{nails.Id}}"]}""", Id(a));
        return new Workshop(s, admin, a, nails, wire, zinc, box, IdOf(carton));
    }

    private static string RecipeJson(Workshop w, decimal wireKg = 105m, Guid? line = null, bool noLine = false, string stages = "null") =>
        $$"""
        {"productId":"{{w.Nails.Id}}","productionLineId":{{(noLine ? "null" : $"\"{line ?? w.LineA}\"")}},
         "batchQuantity":100,"batchUnitId":"{{SystemUnits.Kilogram}}","notes":"Clou 70 mm",
         "lines":[
           {"componentProductId":"{{w.Wire.Id}}","quantity":{{wireKg}},"unitId":"{{SystemUnits.Kilogram}}","wastePercent":2,"specification":"Ø 3 mm, S235"},
           {"componentProductId":"{{w.Zinc.Id}}","quantity":800,"unitId":"{{SystemUnits.Gram}}","wastePercent":5},
           {"componentProductId":"{{w.Box.Id}}","quantity":4,"unitId":"{{SystemUnits.Piece}}"}
         ],
         "stages":{{stages}}}
        """;

    private static Guid BomId(JsonElement bom) => bom.GetProperty("bom").GetProperty("id").GetGuid();

    [Fact]
    public async Task Requirements_for_a_planned_quantity_are_exact_with_waste_and_unit_conversions()
    {
        await using var s = await NewAsync();
        var w = await WorkshopAsync(s);
        var draft = await OkAsync(s.Db, w.Admin, "POST", Boms, RecipeJson(w));

        // Two cartons of nails = 50 kg = half a batch.
        var need = await OkAsync(s.Db, w.Admin, "GET", Bom + "/requirements", $"?quantity=2&unitId={w.Carton}", Id(BomId(draft)));
        Assert.Equal(0.5m, need.GetProperty("batches").GetDecimal());
        var lines = need.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("componentSku").GetString()!);
        Assert.Equal((52.5m, 1.05m, 53.55m, 53.55m), Figures(lines["WIRE-3MM"]));
        // Zinc is written in grams and stocked in kilograms: 400 g + 5 % = 420 g = 0.42 kg.
        Assert.Equal((400m, 20m, 420m, 0.42m), Figures(lines["ZINC"]));
        Assert.Equal("kg", lines["ZINC"].GetProperty("baseUnitCode").GetString());
        Assert.Equal((2m, 0m, 2m, 2m), Figures(lines["BOX-25"]));

        // 1,250 kg asked in kilograms: 12.5 batches.
        need = await OkAsync(s.Db, w.Admin, "GET", Bom + "/requirements", "?quantity=1250", Id(BomId(draft)));
        var totals = need.GetProperty("totals").EnumerateArray().ToDictionary(t => t.GetProperty("componentSku").GetString()!, t => t.GetProperty("baseQuantity").GetDecimal());
        Assert.Equal(1338.75m, totals["WIRE-3MM"]);
        Assert.Equal(10.5m, totals["ZINC"]);
        Assert.Equal(50m, totals["BOX-25"]);

        await RefusedAsync(s.Db, w.Admin, 400, "invalid_quantity", "GET", Bom + "/requirements", "?quantity=0", Id(BomId(draft)));
        await RefusedAsync(s.Db, w.Admin, 400, "no_conversion", "GET", Bom + "/requirements",
            $"?quantity=3&unitId={SystemUnits.Metre}", Id(BomId(draft)));

        static (decimal, decimal, decimal, decimal) Figures(JsonElement l) => (
            l.GetProperty("netQuantity").GetDecimal(), l.GetProperty("wasteQuantity").GetDecimal(),
            l.GetProperty("grossQuantity").GetDecimal(), l.GetProperty("baseQuantity").GetDecimal());
    }

    [Fact]
    public async Task An_active_version_is_frozen_and_a_new_one_retires_it_while_history_keeps_its_definition()
    {
        await using var s = await NewAsync();
        var w = await WorkshopAsync(s);
        var v1 = BomId(await OkAsync(s.Db, w.Admin, "POST", Boms, RecipeJson(w,
            stages: """[{"nameAr":"قطع","nameFr":"Coupe"},{"nameAr":"تشكيل","nameFr":"Frappe"},{"nameAr":"تعبئة","nameFr":"Emballage"}]""")));
        var active = await OkAsync(s.Db, w.Admin, "POST", Bom + "/activate", "", Id(v1));
        Assert.Equal("Active", active.GetProperty("bom").GetProperty("status").GetString());
        // With no stage marked, the last one is where the finished units come off.
        Assert.Equal([false, false, true], active.GetProperty("stages").EnumerateArray().Select(x => x.GetProperty("isOutput").GetBoolean()));

        // Frozen: neither edited nor deleted.
        await RefusedAsync(s.Db, w.Admin, 409, "bom_frozen", "PUT", Bom, RecipeJson(w, wireKg: 110m), Id(v1));
        await RefusedAsync(s.Db, w.Admin, 409, "bom_frozen", "DELETE", Bom, "", Id(v1));

        // A change is a new version, copied whole, then edited and activated in its turn.
        var v2Json = await OkAsync(s.Db, w.Admin, "POST", Bom + "/new-version", "", Id(v1));
        var v2 = BomId(v2Json);
        Assert.Equal((2, "Draft", 3, 3), (v2Json.GetProperty("bom").GetProperty("version").GetInt32(),
            v2Json.GetProperty("bom").GetProperty("status").GetString(), v2Json.GetProperty("lines").GetArrayLength(), v2Json.GetProperty("stages").GetArrayLength()));
        await RefusedAsync(s.Db, w.Admin, 409, "draft_exists", "POST", Bom + "/new-version", "", Id(v1));
        await OkAsync(s.Db, w.Admin, "PUT", Bom, RecipeJson(w, wireKg: 110m), Id(v2));
        await OkAsync(s.Db, w.Admin, "POST", Bom + "/activate", "", Id(v2));

        var old = await OkAsync(s.Db, w.Admin, "GET", Bom, "", Id(v1));
        Assert.Equal("Retired", old.GetProperty("bom").GetProperty("status").GetString());
        Assert.Equal(105m, old.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(3, old.GetProperty("stages").GetArrayLength());
        Assert.Equal(1, await s.Db.BillsOfMaterials.CountAsync(b => b.ProductId == w.Nails.Id && b.Status == BomStatus.Active));
        var current = await OkAsync(s.Db, w.Admin, "GET", Bom, "", Id(v2));
        Assert.Equal(110m, current.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());

        // And the old version still calculates exactly as it was defined.
        var need = await OkAsync(s.Db, w.Admin, "GET", Bom + "/requirements", "?quantity=100", Id(v1));
        Assert.Equal(107.1m, need.GetProperty("lines")[0].GetProperty("baseQuantity").GetDecimal());
    }

    [Fact]
    public async Task A_recipe_that_cannot_say_what_it_needs_is_refused_in_words()
    {
        await using var s = await NewAsync();
        var w = await WorkshopAsync(s);
        string One(Guid component, Guid unit, decimal qty = 1, decimal waste = 0) =>
            $$"""
            {"productId":"{{w.Nails.Id}}","productionLineId":"{{w.LineA}}","batchQuantity":100,"batchUnitId":"{{SystemUnits.Kilogram}}",
             "lines":[{"componentProductId":"{{component}}","quantity":{{qty}},"unitId":"{{unit}}","wastePercent":{{waste}}}]}
            """;

        // Wire counted in metres has no conversion to its kilograms.
        await RefusedAsync(s.Db, w.Admin, 400, "no_conversion", "POST", Boms, One(w.Wire.Id, SystemUnits.Metre));
        // A component without a base unit cannot say how much stock it takes.
        var loose = await ProductAsync(s.Db, "BOLT", ManufacturingRole.Accessory, null);
        await RefusedAsync(s.Db, w.Admin, 400, "component_no_base_unit", "POST", Boms, One(loose.Id, SystemUnits.Piece));
        // Ordinary merchandise and finished goods are not components; the product is not its own.
        await RefusedAsync(s.Db, w.Admin, 400, "not_component", "POST", Boms, One(TillHarness.Pipe, SystemUnits.Piece));
        await RefusedAsync(s.Db, w.Admin, 400, "bom_cycle", "POST", Boms, One(w.Nails.Id, SystemUnits.Kilogram));
        await RefusedAsync(s.Db, w.Admin, 400, "invalid_waste", "POST", Boms, One(w.Wire.Id, SystemUnits.Kilogram, waste: 100));
        await RefusedAsync(s.Db, w.Admin, 400, "invalid_quantity", "POST", Boms, One(w.Wire.Id, SystemUnits.Kilogram, qty: 0));
        // The line's recipe is for what the line makes.
        var other = await ProductAsync(s.Db, "NAIL-90", ManufacturingRole.Finished, SystemUnits.Kilogram);
        await RefusedAsync(s.Db, w.Admin, 400, "product_not_on_line", "POST", Boms,
            One(w.Wire.Id, SystemUnits.Kilogram).Replace(w.Nails.Id.ToString(), other.Id.ToString()));

        Assert.Equal(0, await s.Db.BillsOfMaterials.CountAsync());
    }

    [Fact]
    public async Task A_semi_finished_part_cannot_be_made_from_what_it_goes_into()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var wire = await ProductAsync(s.Db, "WIRE", ManufacturingRole.RawMaterial, SystemUnits.Kilogram);
        var frame = await ProductAsync(s.Db, "FRAME", ManufacturingRole.SemiFinished, SystemUnits.Piece);
        var panel = await ProductAsync(s.Db, "PANEL", ManufacturingRole.SemiFinished, SystemUnits.Piece);
        string Recipe(Guid product, Guid component, Guid unit) =>
            $$"""
            {"productId":"{{product}}","productionLineId":null,"batchQuantity":1,"batchUnitId":"{{SystemUnits.Piece}}",
             "lines":[{"componentProductId":"{{component}}","quantity":2,"unitId":"{{unit}}"}]}
            """;

        // A frame is made of wire; a panel of frames.
        var frameBom = await OkAsync(s.Db, admin, "POST", Boms, Recipe(frame.Id, wire.Id, SystemUnits.Kilogram));
        await OkAsync(s.Db, admin, "POST", Bom + "/activate", "", Id(BomId(frameBom)));
        var panelBom = await OkAsync(s.Db, admin, "POST", Boms, Recipe(panel.Id, frame.Id, SystemUnits.Piece));
        await OkAsync(s.Db, admin, "POST", Bom + "/activate", "", Id(BomId(panelBom)));

        // A new frame version that took panels would make a frame out of itself.
        var frameV2 = await OkAsync(s.Db, admin, "POST", Bom + "/new-version", "", Id(BomId(frameBom)));
        await RefusedAsync(s.Db, admin, 400, "bom_cycle", "PUT", Bom, Recipe(frame.Id, panel.Id, SystemUnits.Piece), Id(BomId(frameV2)));
        // The draft is untouched by the refusal: still the wire it was copied with.
        var stored = await OkAsync(s.Db, admin, "GET", Bom, "", Id(BomId(frameV2)));
        Assert.Equal("WIRE", stored.GetProperty("lines")[0].GetProperty("componentSku").GetString());
    }

    [Fact]
    public async Task Drafting_and_approving_are_separate_and_follow_the_line()
    {
        await using var s = await NewAsync();
        var w = await WorkshopAsync(s);
        var (writer, writerPrincipal) = await ManagerAsync(s.Db, TillHarness.Oran, PermissionCatalog.ManufacturingView, PermissionCatalog.ManufacturingBomManage);
        await OkAsync(s.Db, w.Admin, "PUT", "/api/manufacturing/lines/{id:guid}/members",
            $$"""{"members":[{"userId":"{{writer.Id}}","role":"Manager"}]}""", Id(w.LineA));

        // A manager of line A writes its recipe, but may not approve without the permission.
        var draft = await OkAsync(s.Db, writerPrincipal, "POST", Boms, RecipeJson(w));
        Assert.False(draft.GetProperty("canApprove").GetBoolean());
        await RefusedAsync(s.Db, writerPrincipal, 403, "forbidden", "POST", Bom + "/activate", "", Id(BomId(draft)));
        // A recipe for every line is the company's.
        await OkAsync(s.Db, w.Admin, "DELETE", Bom, "", Id(BomId(draft)));
        await RefusedAsync(s.Db, writerPrincipal, 403, "forbidden", "POST", Boms, RecipeJson(w, noLine: true));

        // With the approval, the line's manager activates it.
        var approver = TillHarness.WithGrant(writerPrincipal, PermissionCatalog.ManufacturingBomApprove);
        var again = await OkAsync(s.Db, writerPrincipal, "POST", Boms, RecipeJson(w));
        await OkAsync(s.Db, approver, "POST", Bom + "/activate", "", Id(BomId(again)));

        // Someone who does not reach line A does not see its recipes at all.
        var (_, stranger) = await ManagerAsync(s.Db, TillHarness.Algiers, AllGrants);
        await RefusedAsync(s.Db, stranger, 404, "not_found", "GET", Bom, "", Id(BomId(again)));
        Assert.Empty((await OkAsync(s.Db, stranger, "GET", Boms)).EnumerateArray());
    }
}
