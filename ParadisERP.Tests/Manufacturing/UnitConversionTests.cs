using ParadisERP.Domain;
using ParadisERP.Infrastructure.Manufacturing;
using static ParadisERP.Tests.Manufacturing.MfgHarness;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// Units and conversions (Phase 1 §3): explicit, validated, and never a silent conversion between units
/// that do not measure the same thing.
/// </summary>
public class UnitConversionTests
{
    private static readonly UnitConversions.UnitInfo Kg = new(SystemUnits.Kilogram, "kg", UnitDimension.Mass, 1m);
    private static readonly UnitConversions.UnitInfo G = new(SystemUnits.Gram, "g", UnitDimension.Mass, 0.001m);
    private static readonly UnitConversions.UnitInfo M = new(SystemUnits.Metre, "m", UnitDimension.Length, 1m);
    private static readonly UnitConversions.UnitInfo Cm = new(SystemUnits.Centimetre, "cm", UnitDimension.Length, 0.01m);
    private static readonly UnitConversions.UnitInfo Pcs = new(SystemUnits.Piece, "pcs", UnitDimension.Count, 1m);
    private static readonly UnitConversions.UnitInfo Carton = new(Guid.NewGuid(), "ctn", UnitDimension.Other, 1m);
    private static readonly UnitConversions.UnitInfo Coil = new(Guid.NewGuid(), "coil", UnitDimension.Other, 1m);

    [Fact]
    public void Within_a_dimension_the_factors_convert_both_ways_exactly()
    {
        Assert.Equal(1000m, UnitConversions.StandardFactor(Kg, G));
        Assert.Equal(0.001m, UnitConversions.StandardFactor(G, Kg));
        Assert.Equal(2500m, UnitConversions.Apply(2.5m, UnitConversions.StandardFactor(Kg, G)!.Value));
        Assert.Equal(1.25m, UnitConversions.Apply(125m, UnitConversions.StandardFactor(Cm, M)!.Value));
        Assert.Equal(1m, UnitConversions.StandardFactor(Kg, Kg));
    }

    [Fact]
    public void Units_of_different_dimensions_or_of_no_dimension_do_not_convert()
    {
        Assert.Null(UnitConversions.StandardFactor(Kg, M));
        Assert.Null(UnitConversions.StandardFactor(Pcs, Kg));
        Assert.Null(UnitConversions.StandardFactor(Carton, Coil));
        Assert.Null(UnitConversions.StandardFactor(Carton, Kg));
    }

    [Fact]
    public void A_product_factor_converts_its_own_packaging_and_onward_through_the_base_unit()
    {
        // A carton of these nails is 25 kg; nothing is written for pieces.
        var nails = new UnitConversions.ProductUnits(SystemUnits.Kilogram, new Dictionary<Guid, decimal> { [Carton.Id] = 25m });
        Assert.Equal(25m, UnitConversions.FactorToBase(Carton, Kg, nails));
        Assert.Equal(0.001m, UnitConversions.FactorToBase(G, Kg, nails));
        Assert.Equal(25_000m, UnitConversions.Factor(Carton, G, Kg, nails));
        Assert.Equal(0.04m, UnitConversions.Factor(Kg, Carton, Kg, nails));
        // A coil's weight varies: no factor, no conversion — it is weighed in its base unit instead.
        Assert.Null(UnitConversions.FactorToBase(Coil, Kg, nails));
        Assert.Null(UnitConversions.Factor(Pcs, Kg, Kg, nails));
    }

    [Fact]
    public async Task The_six_system_units_are_seeded_and_their_meaning_is_fixed()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);

        var units = await OkAsync(s.Db, admin, "GET", "/api/manufacturing/units");
        var codes = units.EnumerateArray().Select(u => u.GetProperty("code").GetString()).ToList();
        foreach (var code in new[] { "pcs", "kg", "g", "m", "cm", "l" }) Assert.Contains(code, codes);

        // 1 kg = 1,000 g, through the route the screens use.
        var converted = await OkAsync(s.Db, admin, "GET", "/api/manufacturing/units/convert",
            $"?quantity=2.5&fromUnitId={SystemUnits.Kilogram}&toUnitId={SystemUnits.Gram}");
        Assert.Equal(2500m, converted.GetProperty("result").GetDecimal());

        await RefusedAsync(s.Db, admin, 400, "no_conversion", "GET", "/api/manufacturing/units/convert",
            $"?quantity=1&fromUnitId={SystemUnits.Kilogram}&toUnitId={SystemUnits.Metre}");

        // A system unit's names may change; its factor and dimension stay as they are.
        await OkAsync(s.Db, admin, "PUT", "/api/manufacturing/units/{id:guid}",
            """{"code":"kgx","nameAr":"كلغ","nameFr":"Kilo","nameEn":"Kilo","dimension":"Length","factorToReference":5,"isActive":true}""",
            Id(SystemUnits.Kilogram));
        var kg = s.Db.UnitsOfMeasure.Single(u => u.Id == SystemUnits.Kilogram);
        Assert.Equal(("kg", UnitDimension.Mass, 1m, "Kilo"), (kg.Code, kg.Dimension, kg.FactorToReference, kg.NameFr));
    }

    [Fact]
    public async Task A_configurable_unit_is_validated_and_frozen_once_something_counts_in_it()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);

        await RefusedAsync(s.Db, admin, 400, "invalid_factor", "POST", "/api/manufacturing/units",
            """{"code":"t","nameAr":"طن","nameFr":"Tonne","dimension":"Mass","factorToReference":0}""");
        await RefusedAsync(s.Db, admin, 409, "code_taken", "POST", "/api/manufacturing/units",
            """{"code":"KG","nameAr":"x","nameFr":"x","dimension":"Mass","factorToReference":1}""");

        var tonne = await OkAsync(s.Db, admin, "POST", "/api/manufacturing/units",
            """{"code":"t","nameAr":"طن","nameFr":"Tonne","dimension":"Mass","factorToReference":1000}""");
        var converted = await OkAsync(s.Db, admin, "GET", "/api/manufacturing/units/convert",
            $"?quantity=1.2&fromUnitId={IdOf(tonne)}&toUnitId={SystemUnits.Gram}");
        Assert.Equal(1_200_000m, converted.GetProperty("result").GetDecimal());

        // Once a product counts in it, its factor can no longer move.
        await ProductAsync(s.Db, "STEEL-COIL", ManufacturingRole.RawMaterial, IdOf(tonne));
        await RefusedAsync(s.Db, admin, 409, "unit_in_use", "PUT", "/api/manufacturing/units/{id:guid}",
            """{"code":"t","nameAr":"طن","nameFr":"Tonne","dimension":"Mass","factorToReference":907}""", Id(IdOf(tonne)));
    }
}
