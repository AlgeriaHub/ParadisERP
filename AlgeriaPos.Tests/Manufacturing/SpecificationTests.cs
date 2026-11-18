using System.Text.Json;
using AlgeriaPos.Domain;
using AlgeriaPos.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Manufacturing.MfgHarness;

namespace AlgeriaPos.Tests.Manufacturing;

/// <summary>
/// Structured specifications (hardening review, 2026-10-26): configurable fields — measured in a unit of
/// their dimension, or written as a word — on a product and on a recipe line, beside the free text and
/// never instead of it; optional throughout.
/// </summary>
public class SpecificationTests
{
    private const string Fields = "/api/manufacturing/spec-fields";
    private const string Field = "/api/manufacturing/spec-fields/{id:guid}";
    private const string Material = "/api/manufacturing/materials/{productId:guid}";
    private static (string, string) Of(Guid product) => ("productId", product.ToString());

    private static string Measure(Guid field, decimal value, Guid unit) =>
        $$"""{"attributeId":"{{field}}","value":{{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"unitId":"{{unit}}"}""";

    private static string Word(Guid field, string text) => $$"""{"attributeId":"{{field}}","text":"{{text}}"}""";

    private static List<(string Code, decimal? Value, string? Unit, string? Text)> SpecsOf(JsonElement owner) =>
        owner.GetProperty("specs").EnumerateArray().Select(x => (
            x.GetProperty("attributeCode").GetString()!,
            x.GetProperty("value").ValueKind == JsonValueKind.Null ? (decimal?)null : x.GetProperty("value").GetDecimal(),
            x.GetProperty("unitCode").GetString(),
            x.GetProperty("text").GetString())).ToList();

    [Fact]
    public async Task The_fields_come_seeded_and_a_company_adds_its_own()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);

        var seeded = (await OkAsync(s.Db, admin, "GET", Fields)).EnumerateArray().Select(f => f.GetProperty("code").GetString()).ToList();
        Assert.Equal(["LENGTH", "WIDTH", "THICKNESS", "DIAMETER", "GRADE"], seeded);
        // The millimetre comes with them, fixed like the other system units.
        Assert.True((await s.Db.UnitsOfMeasure.AsNoTracking().SingleAsync(u => u.Id == SystemUnits.Millimetre)).IsSystem);

        var weight = await OkAsync(s.Db, admin, "POST", Fields,
            """{"code":"weight","nameAr":"الوزن","nameFr":"Poids","kind":"Measure","dimension":"Mass","sortOrder":6}""");
        Assert.Equal(("WEIGHT", "Measure", "Mass"),
            (weight.GetProperty("code").GetString(), weight.GetProperty("kind").GetString(), weight.GetProperty("dimension").GetString()));
        var colour = await OkAsync(s.Db, admin, "POST", Fields,
            """{"code":"COLOUR","nameAr":"اللون","nameFr":"Couleur","kind":"Text","dimension":"Length"}""");
        Assert.Equal("Other", colour.GetProperty("dimension").GetString());

        await RefusedAsync(s.Db, admin, 409, "code_taken", "POST", Fields, """{"code":"Weight","nameAr":"و","nameFr":"P","kind":"Text"}""");
        await RefusedAsync(s.Db, admin, 400, "invalid_dimension", "POST", Fields, """{"code":"AREA","nameAr":"م","nameFr":"S","kind":"Measure"}""");
        await RefusedAsync(s.Db, admin, 400, "invalid_kind", "POST", Fields, """{"code":"X","nameAr":"س","nameFr":"X","kind":"Number"}""");

        // A system field keeps what it measures; its names and its place in the list are the company's.
        var renamed = await OkAsync(s.Db, admin, "PUT", Field,
            """{"code":"LEN","nameAr":"الطول الكلي","nameFr":"Longueur totale","kind":"Text","dimension":"Mass","sortOrder":9}""",
            Id(SystemSpecAttributes.Length));
        Assert.Equal(("LENGTH", "Measure", "Length", "Longueur totale", 9),
            (renamed.GetProperty("code").GetString(), renamed.GetProperty("kind").GetString(), renamed.GetProperty("dimension").GetString(),
             renamed.GetProperty("nameFr").GetString(), renamed.GetProperty("sortOrder").GetInt32()));
    }

    [Fact]
    public async Task A_product_carries_only_the_fields_that_mean_something_for_it_each_in_a_unit_that_converts()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var tube = await ProductAsync(s.Db, "TUBE-48", ManufacturingRole.RawMaterial, SystemUnits.Metre);
        var mortar = await ProductAsync(s.Db, "MORTIER", ManufacturingRole.Consumable, SystemUnits.Kilogram);
        string Classify(string role, Guid unit, string specs) => $$"""{"role":"{{role}}","baseUnitId":"{{unit}}","units":[],"specs":[{{specs}}]}""";

        var saved = await OkAsync(s.Db, admin, "PUT", Material, Classify("RawMaterial", SystemUnits.Metre, string.Join(",",
            Measure(SystemSpecAttributes.Length, 6m, SystemUnits.Metre),
            Measure(SystemSpecAttributes.Diameter, 48.3m, SystemUnits.Millimetre),
            Measure(SystemSpecAttributes.Thickness, 3.2m, SystemUnits.Millimetre),
            Word(SystemSpecAttributes.Grade, "S235"))), Of(tube.Id));
        Assert.Equal(
            [("LENGTH", 6m, "m", null), ("THICKNESS", 3.2m, "mm", null), ("DIAMETER", 48.3m, "mm", null), ("GRADE", (decimal?)null, (string?)null, "S235")],
            SpecsOf(saved));

        // Mortar has no physical dimensions: it carries none, and nothing asks it to.
        var plain = await OkAsync(s.Db, admin, "PUT", Material, Classify("Consumable", SystemUnits.Kilogram, ""), Of(mortar.Id));
        Assert.Empty(SpecsOf(plain));

        // What a later calculation could not read honestly is refused, and the stored values are untouched.
        await RefusedAsync(s.Db, admin, 400, "spec_unit_mismatch", "PUT", Material,
            Classify("RawMaterial", SystemUnits.Metre, Measure(SystemSpecAttributes.Thickness, 3m, SystemUnits.Kilogram)), Of(tube.Id));
        await RefusedAsync(s.Db, admin, 400, "spec_kind_mismatch", "PUT", Material,
            Classify("RawMaterial", SystemUnits.Metre, Measure(SystemSpecAttributes.Grade, 235m, SystemUnits.Millimetre)), Of(tube.Id));
        await RefusedAsync(s.Db, admin, 400, "spec_kind_mismatch", "PUT", Material,
            Classify("RawMaterial", SystemUnits.Metre, Word(SystemSpecAttributes.Length, "six metres")), Of(tube.Id));
        await RefusedAsync(s.Db, admin, 400, "spec_value_required", "PUT", Material,
            Classify("RawMaterial", SystemUnits.Metre, Measure(SystemSpecAttributes.Length, 0m, SystemUnits.Metre)), Of(tube.Id));
        await RefusedAsync(s.Db, admin, 400, "spec_unit_required", "PUT", Material,
            Classify("RawMaterial", SystemUnits.Metre, $$"""{"attributeId":"{{SystemSpecAttributes.Length}}","value":6}"""), Of(tube.Id));
        await RefusedAsync(s.Db, admin, 400, "duplicate_spec", "PUT", Material,
            Classify("RawMaterial", SystemUnits.Metre, Measure(SystemSpecAttributes.Length, 6m, SystemUnits.Metre) + "," + Measure(SystemSpecAttributes.Length, 600m, SystemUnits.Centimetre)), Of(tube.Id));
        await RefusedAsync(s.Db, admin, 400, "unknown_spec_field", "PUT", Material,
            Classify("RawMaterial", SystemUnits.Metre, Word(Guid.NewGuid(), "x")), Of(tube.Id));
        Assert.Equal(4, await s.Db.ProductSpecs.CountAsync(x => x.ProductId == tube.Id));

        // A client that predates the fields sends none: they stay. An empty list clears them.
        var kept = await OkAsync(s.Db, admin, "PUT", Material, $$"""{"role":"RawMaterial","baseUnitId":"{{SystemUnits.Metre}}","units":[]}""", Of(tube.Id));
        Assert.Equal(4, SpecsOf(kept).Count);
        Assert.Empty(SpecsOf(await OkAsync(s.Db, admin, "PUT", Material, Classify("RawMaterial", SystemUnits.Metre, ""), Of(tube.Id))));
    }

    [Fact]
    public async Task A_field_or_unit_a_value_uses_keeps_what_it_measures()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var sheet = await ProductAsync(s.Db, "TOLE", ManufacturingRole.RawMaterial, SystemUnits.Piece);
        var inch = await OkAsync(s.Db, admin, "POST", "/api/manufacturing/units",
            """{"code":"in","nameAr":"إنش","nameFr":"Pouce","dimension":"Length","factorToReference":0.0254}""");
        var gauge = await OkAsync(s.Db, admin, "POST", Fields, """{"code":"GAUGE","nameAr":"المقاس","nameFr":"Jauge","kind":"Measure","dimension":"Length"}""");

        await OkAsync(s.Db, admin, "PUT", Material,
            $$"""{"role":"RawMaterial","baseUnitId":"{{SystemUnits.Piece}}","units":[],"specs":[{{Measure(IdOf(gauge), 0.125m, IdOf(inch))}}]}""", Of(sheet.Id));

        await RefusedAsync(s.Db, admin, 409, "spec_field_in_use", "PUT", Field,
            """{"code":"GAUGE","nameAr":"المقاس","nameFr":"Jauge","kind":"Measure","dimension":"Mass"}""", Id(IdOf(gauge)));
        await RefusedAsync(s.Db, admin, 409, "unit_in_use", "PUT", "/api/manufacturing/units/{id:guid}",
            """{"code":"in","nameAr":"إنش","nameFr":"Pouce","dimension":"Length","factorToReference":0.025}""", Id(IdOf(inch)));

        // Switched off, it leaves the pickers; the value written with it stays valid and readable.
        var off = await OkAsync(s.Db, admin, "PUT", Field,
            """{"code":"GAUGE","nameAr":"المقاس","nameFr":"Jauge","kind":"Measure","dimension":"Length","isActive":false}""", Id(IdOf(gauge)));
        Assert.True(off.GetProperty("inUse").GetBoolean());
        var material = await OkAsync(s.Db, admin, "GET", Material, "", Of(sheet.Id));
        Assert.Equal([("GAUGE", 0.125m, "in", null)], SpecsOf(material));
    }

    [Fact]
    public async Task A_recipe_line_s_specification_is_checked_frozen_with_the_recipe_and_copied_to_its_next_version()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var tube = await ProductAsync(s.Db, "TUBE-48", ManufacturingRole.RawMaterial, SystemUnits.Metre);
        var frame = await ProductAsync(s.Db, "CADRE", ManufacturingRole.Finished, SystemUnits.Piece);
        string Draft(string specs) =>
            $$"""
            {"productId":"{{frame.Id}}","batchQuantity":1,"batchUnitId":"{{SystemUnits.Piece}}",
             "lines":[{"componentProductId":"{{tube.Id}}","quantity":4.8,"unitId":"{{SystemUnits.Metre}}","specification":"4 pieces of 1.20 m, S235","specs":[{{specs}}]}]}
            """;

        await RefusedAsync(s.Db, admin, 400, "spec_unit_mismatch", "POST", "/api/manufacturing/boms",
            Draft(Measure(SystemSpecAttributes.Length, 1.2m, SystemUnits.Kilogram)));
        var draft = await OkAsync(s.Db, admin, "POST", "/api/manufacturing/boms",
            Draft(Measure(SystemSpecAttributes.Length, 120m, SystemUnits.Centimetre) + "," + Word(SystemSpecAttributes.Grade, "S235")));
        var id = draft.GetProperty("bom").GetProperty("id").GetGuid();
        var line = draft.GetProperty("lines")[0];
        Assert.Equal("4 pieces of 1.20 m, S235", line.GetProperty("specification").GetString());
        Assert.Equal([("LENGTH", 120m, "cm", null), ("GRADE", (decimal?)null, (string?)null, "S235")], SpecsOf(line));

        // A draft is sent whole: its lines' figures are replaced with them.
        var edited = await OkAsync(s.Db, admin, "PUT", "/api/manufacturing/boms/{id:guid}",
            Draft(Measure(SystemSpecAttributes.Length, 1.2m, SystemUnits.Metre)), Id(id));
        Assert.Equal([("LENGTH", 1.2m, "m", null)], SpecsOf(edited.GetProperty("lines")[0]));
        Assert.Equal(1, await s.Db.BomLineSpecs.CountAsync());

        var active = await OkAsync(s.Db, admin, "POST", "/api/manufacturing/boms/{id:guid}/activate", "", Id(id));
        Assert.Equal("Active", active.GetProperty("bom").GetProperty("status").GetString());
        await RefusedAsync(s.Db, admin, 409, "bom_frozen", "PUT", "/api/manufacturing/boms/{id:guid}",
            Draft(Measure(SystemSpecAttributes.Length, 2m, SystemUnits.Metre)), Id(id));

        var next = await OkAsync(s.Db, admin, "POST", "/api/manufacturing/boms/{id:guid}/new-version", "", Id(id));
        Assert.Equal([("LENGTH", 1.2m, "m", null)], SpecsOf(next.GetProperty("lines")[0]));
        Assert.Equal(2, await s.Db.BomLineSpecs.CountAsync());

        // The requirements read quantities, not specifications: unchanged by them.
        var needs = await OkAsync(s.Db, admin, "GET", "/api/manufacturing/boms/{id:guid}/requirements",
            $"?quantity=10&unitId={SystemUnits.Piece}", Id(id));
        Assert.Equal(48m, needs.GetProperty("lines")[0].GetProperty("baseQuantity").GetDecimal());

        // Deleting the draft takes its figures with it.
        await OkAsync(s.Db, admin, "DELETE", "/api/manufacturing/boms/{id:guid}", "", Id(next.GetProperty("bom").GetProperty("id").GetGuid()));
        Assert.Equal(1, await s.Db.BomLineSpecs.CountAsync());
    }
}
