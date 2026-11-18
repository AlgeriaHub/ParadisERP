using AlgeriaPos.Domain;
using static AlgeriaPos.Tests.Manufacturing.MfgHarness;

namespace AlgeriaPos.Tests.Manufacturing;

/// <summary>
/// Stage templates and job roles (Phase 1 §5–6): a template is edited freely, and a recipe that copied its
/// stages keeps its own copy — the snapshot a production order will copy again.
/// </summary>
public class StageTemplateTests
{
    private const string Templates = "/api/manufacturing/stage-templates";

    [Fact]
    public async Task A_recipe_keeps_the_stages_it_copied_when_the_template_changes()
    {
        await using var s = await NewAsync();
        var (_, admin) = await AdminAsync(s.Db);
        var cutter = IdOf(await OkAsync(s.Db, admin, "POST", "/api/manufacturing/job-roles", """{"code":"cutter","nameAr":"قاطع","nameFr":"Coupeur"}"""));
        var welder = IdOf(await OkAsync(s.Db, admin, "POST", "/api/manufacturing/job-roles", """{"code":"welder","nameAr":"لحام","nameFr":"Soudeur"}"""));
        await RefusedAsync(s.Db, admin, 409, "code_taken", "POST", "/api/manufacturing/job-roles", """{"code":"Welder","nameAr":"x","nameFr":"x"}""");

        string Template(string steps) => $$"""{"code":"frame","nameAr":"إطار","nameFr":"Cadre","isActive":true,"steps":{{steps}}}""";
        var twoSteps = $$"""
            [{"nameAr":"قطع","nameFr":"Coupe","estimatedMinutes":30,"expectedWorkers":2,"roles":[{"jobRoleId":"{{cutter}}","count":2}]},
             {"nameAr":"لحام","nameFr":"Soudure","estimatedMinutes":45,"expectedWorkers":1,"isQualityCheckpoint":true,"qualityNote":"Contrôle visuel","roles":[{"jobRoleId":"{{welder}}"}]}]
            """;
        var template = IdOf(await OkAsync(s.Db, admin, "POST", Templates, Template(twoSteps)));
        await RefusedAsync(s.Db, admin, 400, "stages_required", "POST", Templates, Template("[]").Replace("frame", "empty"));
        await RefusedAsync(s.Db, admin, 400, "unknown_job_role", "POST", Templates,
            Template($$"""[{"nameAr":"x","nameFr":"x","roles":[{"jobRoleId":"{{Guid.NewGuid()}}"}]}]""").Replace("frame", "bad"));

        var frame = await ProductAsync(s.Db, "FRAME", ManufacturingRole.SemiFinished, SystemUnits.Piece);
        var tube = await ProductAsync(s.Db, "TUBE", ManufacturingRole.RawMaterial, SystemUnits.Metre);
        var bom = await OkAsync(s.Db, admin, "POST", "/api/manufacturing/boms",
            $$"""{"productId":"{{frame.Id}}","batchQuantity":10,"batchUnitId":"{{SystemUnits.Piece}}","stageTemplateId":"{{template}}","lines":[{"componentProductId":"{{tube.Id}}","quantity":2400,"unitId":"{{SystemUnits.Centimetre}}","wastePercent":3}]}""");
        var stages = bom.GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal(["Coupe", "Soudure"], stages.Select(x => x.GetProperty("nameFr").GetString()));
        Assert.Equal(2, stages[0].GetProperty("roles")[0].GetProperty("count").GetInt32());
        Assert.True(stages[1].GetProperty("isQualityCheckpoint").GetBoolean());
        Assert.True(stages[1].GetProperty("isOutput").GetBoolean());

        // The template gains a stage; the recipe keeps the two it copied.
        var threeSteps = twoSteps.TrimEnd().TrimEnd(']') + """,{"nameAr":"دهان","nameFr":"Peinture"}]""";
        var edited = await OkAsync(s.Db, admin, "PUT", Templates + "/{id:guid}", Template(threeSteps), Id(template));
        Assert.Equal(3, edited.GetProperty("steps").GetArrayLength());
        var reread = await OkAsync(s.Db, admin, "GET", "/api/manufacturing/boms/{id:guid}", "", Id(bom.GetProperty("bom").GetProperty("id").GetGuid()));
        Assert.Equal(2, reread.GetProperty("stages").GetArrayLength());

        // 24 m of tube per 10 frames, plus 3 %: 2.472 m for one frame.
        var need = await OkAsync(s.Db, admin, "GET", "/api/manufacturing/boms/{id:guid}/requirements", "?quantity=1",
            Id(bom.GetProperty("bom").GetProperty("id").GetGuid()));
        Assert.Equal(2.472m, need.GetProperty("lines")[0].GetProperty("baseQuantity").GetDecimal());
    }
}
