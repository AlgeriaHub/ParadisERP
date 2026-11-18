using System.Text.Json;
using ParadisERP.API.Endpoints;
using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Maintenance;
using ParadisERP.Tests.Security;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Tests.Maintenance;

/// <summary>
/// Maintenance › Settings, Phase M0 (docs/maintenance/plan.md §4, §6.1): every decision a setting whose first option is
/// the default and today's behaviour; the operational half under maintenance.settings, the accounting half under
/// accounting.manage; the options kept for later refused; the accountant's confirmation as Costing settings keeps it.
/// </summary>
public class MaintenanceSettingsTests
{
    private static readonly Func<string, bool> Everything = _ => true;
    private static Func<string, bool> Only(params string[] keys) => keys.Contains;

    private static UpdateMaintenanceSettingsRequest Ask(MaintenanceSettingsDto d) => new(
        d.Module, d.UnitsLeaveMaintenance, d.JobsFromReturnNotes, d.BookableEmployees, d.JobCloser, d.CustomerJobs,
        d.CostIncludes, d.OverheadPerLabourHour, d.LabourRate, d.WorkshopRatePerHour, d.BurdenAndVat, d.EmployerBurdenPercent,
        d.ExpenseVatPercent, d.CustomerPrice, d.LabourChargePerHour, d.MarkupPercent, d.ReturnRecharge, d.PriceBelowCost,
        d.RechargeSharing, d.OwnRepairs, d.PartsPosting, d.LabourInBooks, d.CustomerIncome, d.Advances, d.OpenJobsAtPeriodEnd,
        d.Rework, d.RepairHistory, d.PostingFromMonth, d.AccountantApproved, d.GuaranteeMonths, d.ProvisionPercent,
        d.CapitaliseAboveAmount, d.PerPieceAboveValue);

    private static async Task<UpdateMaintenanceSettingsRequest> CurrentAsync(MaintenanceSettingsStore store) => Ask(await store.SettingsAsync());

    [Fact]
    public async Task By_default_the_module_is_off_every_choice_is_its_first_option_and_reading_writes_nothing()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _c = connection;
        await using var _d = db;
        var s = await new MaintenanceSettingsStore(db).SettingsAsync();

        Assert.Equal(
            ("Off", "ByHandOrByJob", "OfferedInWaitingList", "AnyEmployeeOfBranch", "AnyHolder", "Off"),
            (s.Module, s.UnitsLeaveMaintenance, s.JobsFromReturnNotes, s.BookableEmployees, s.JobCloser, s.CustomerJobs));
        Assert.Equal(("PartsLabourOutsideWork", "PayrollDerived", "FollowManufacturing"), (s.CostIncludes, s.LabourRate, s.BurdenAndVat));
        Assert.Equal(("Quoted", "TypedAtReturn", "ShownAsLoss", "ByUnits"), (s.CustomerPrice, s.ReturnRecharge, s.PriceBelowCost, s.RechargeSharing));
        Assert.Equal(
            ("AlwaysExpense", "PracticeLedgerByMonth", "ReportOnly", "SameAsRecharges", "NotTaken", "LeftInExpensesListed", "AddedToOriginalJob", "PerTypeWithReference"),
            (s.OwnRepairs, s.PartsPosting, s.LabourInBooks, s.CustomerIncome, s.Advances, s.OpenJobsAtPeriodEnd, s.Rework, s.RepairHistory));
        Assert.All(new[] { s.OverheadPerLabourHour, s.WorkshopRatePerHour, s.EmployerBurdenPercent, s.ExpenseVatPercent, s.LabourChargePerHour, s.MarkupPercent },
            v => Assert.Equal(0m, v));
        Assert.Equal((null, false, null, null, null), (s.PostingFromMonth, s.AccountantApproved, s.ApprovedBy, s.ApprovedAtUtc, s.UpdatedBy));
        // M7c: a guarantee of three months (MS-27), no provision rate (MS-37 adds its cost to the original job).
        Assert.Equal((3, 0m), (s.GuaranteeMonths, s.ProvisionPercent));
        // M7d: no amount above which repairs are capitalised (MS-30), no value above which pieces are tracked (MS-38).
        Assert.Equal((0m, 0m, false), (s.CapitaliseAboveAmount, s.PerPieceAboveValue, s.AssetRegisterActive));
        // Each choice's default is the enum's 0, so a row the migration never wrote reads the same as a fresh one.
        foreach (var choice in typeof(MaintenanceSettings).GetProperties().Where(p => p.PropertyType.IsEnum))
            Assert.Equal(0, Convert.ToInt32(choice.GetValue(new MaintenanceSettings())));
        Assert.Equal(0, await db.MaintenanceSettings.CountAsync());
    }

    [Fact]
    public async Task Settings_are_saved_once_and_read_back_with_their_amounts_kept_only_where_their_option_uses_them()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _c = connection;
        await using var _d = db;
        var store = new MaintenanceSettingsStore(db);
        var ask = await CurrentAsync(store) with
        {
            Module = "On", UnitsLeaveMaintenance = "OnlyByJob", JobCloser = "NotTheOpener", CustomerJobs = "On",
            CostIncludes = "PartsLabourOutsideWorkOverhead", OverheadPerLabourHour = 60m,
            LabourRate = "WorkshopRate", WorkshopRatePerHour = 450m,
            BurdenAndVat = "OwnValues", EmployerBurdenPercent = 26m, ExpenseVatPercent = 19m,
            CustomerPrice = "Itemised", LabourChargePerHour = 800m, MarkupPercent = 15m,
            ReturnRecharge = "SuggestedFromRate", PriceBelowCost = "NeedsConfirmation", RechargeSharing = "AllToFirstJob",
            LabourInBooks = "MovedIntoJobs",
            OpenJobsAtPeriodEnd = "WorkInProgressMonthly", Rework = "OwnJob", PostingFromMonth = "2026-12",
            // M7d: per piece above 500,000 (MS-38) needs no register; its value is kept.
            RepairHistory = "PerPiece", PerPieceAboveValue = 500_000m,
        };
        var saved = await store.UpdateAsync(ask, Everything, "admin");
        Assert.Null(saved.Error);

        var read = await new MaintenanceSettingsStore(db).SettingsAsync();
        Assert.Equal(Ask(read), ask);
        Assert.Equal("admin", read.UpdatedBy);
        Assert.Equal(1, await db.MaintenanceSettings.CountAsync());
        Assert.Equal(MaintenanceSettings.SingletonId, (await db.MaintenanceSettings.AsNoTracking().SingleAsync()).Id);

        // An amount its option does not use is not kept: back to quoted, payroll rates and Costing settings' burden.
        var plain = (await store.UpdateAsync(ask with
        {
            CostIncludes = "PartsAndOutsideWorkOnly", LabourRate = "PayrollDerived", BurdenAndVat = "FollowManufacturing", CustomerPrice = "Quoted",
        }, Everything, "admin")).Saved!;
        Assert.Equal((0m, 0m, 0m, 0m, 0m, 0m),
            (plain.OverheadPerLabourHour, plain.WorkshopRatePerHour, plain.EmployerBurdenPercent, plain.ExpenseVatPercent, plain.LabourChargePerHour, plain.MarkupPercent));
    }

    [Theory]
    [InlineData("Module", "Sometimes")]
    [InlineData("CostIncludes", "")]
    [InlineData("PostingFromMonth", "December")]
    [InlineData("OverheadPerLabourHour", "-1")]
    [InlineData("MarkupPercent", "1001")]
    [InlineData("WorkshopRateMissing", "")]
    [InlineData("GuaranteeMonths", "61")]
    [InlineData("ProvisionMissing", "")]
    [InlineData("OverheadMissing", "")]
    [InlineData("CapitaliseMissing", "")]
    [InlineData("PerPieceMissing", "")]
    [InlineData("PerPieceAboveValue", "-1")]
    public async Task A_choice_out_of_its_options_or_an_amount_out_of_range_is_refused_and_nothing_saved(string field, string value)
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _c = connection;
        await using var _d = db;
        var store = new MaintenanceSettingsStore(db);
        var ask = await CurrentAsync(store);
        ask = field switch
        {
            "Module" => ask with { Module = value },
            "CostIncludes" => ask with { CostIncludes = value },
            "PostingFromMonth" => ask with { PostingFromMonth = value },
            "OverheadPerLabourHour" => ask with { CostIncludes = "PartsLabourOutsideWorkOverhead", OverheadPerLabourHour = decimal.Parse(value) },
            "MarkupPercent" => ask with { CustomerPrice = "CostPlus", MarkupPercent = decimal.Parse(value) },
            "WorkshopRateMissing" => ask with { LabourRate = "WorkshopRate", WorkshopRatePerHour = 0m },
            "GuaranteeMonths" => ask with { GuaranteeMonths = int.Parse(value) },
            // M7c: the provision asks for its rate.
            "ProvisionMissing" => ask with { Rework = "Provision", ProvisionPercent = 0m },
            // M7d: capitalising asks for its amount, per piece for its value.
            "CapitaliseMissing" => ask with { OwnRepairs = "CapitalisedAboveAmount", CapitaliseAboveAmount = 0m },
            "PerPieceMissing" => ask with { RepairHistory = "PerPiece", PerPieceAboveValue = 0m },
            "PerPieceAboveValue" => ask with { RepairHistory = "PerPiece", PerPieceAboveValue = decimal.Parse(value) },
            _ => ask with { CostIncludes = "PartsLabourOutsideWorkOverhead", OverheadPerLabourHour = 0m },
        };
        var refused = await store.UpdateAsync(ask, Everything, "admin");
        Assert.Equal((400, MaintenanceSettingsStore.InvalidCode), (refused.Status, refused.Code));
        Assert.False(string.IsNullOrWhiteSpace(refused.Error));
        Assert.Equal(0, await db.MaintenanceSettings.CountAsync());
    }

    [Fact]
    public async Task Each_half_asks_for_its_own_key_only_when_it_would_change()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _c = connection;
        await using var _d = db;
        var store = new MaintenanceSettingsStore(db);
        var current = await CurrentAsync(store);
        var operations = Only(PermissionCatalog.MaintenanceSettings);
        var accountant = Only(PermissionCatalog.AccountingManage);

        // maintenance.settings: the module, jobs, cost and pricing, sent back with the accounting half as it stands.
        var on = await store.UpdateAsync(current with { Module = "On", MarkupPercent = 10m, CustomerPrice = "CostPlus" }, operations, "chef");
        Assert.Null(on.Error);
        foreach (var accounting in new[]
        {
            current with { Module = "On", MarkupPercent = 10m, CustomerPrice = "CostPlus", LabourInBooks = "MovedIntoJobs" },
            current with { Module = "On", MarkupPercent = 10m, CustomerPrice = "CostPlus", PostingFromMonth = "2026-12" },
            current with { Module = "On", MarkupPercent = 10m, CustomerPrice = "CostPlus", AccountantApproved = true },
        })
        {
            var refused = await store.UpdateAsync(accounting, operations, "chef");
            Assert.Equal((403, MaintenanceSettingsStore.AccountingForbiddenCode), (refused.Status, refused.Code));
        }

        // accounting.manage: the accounting half and the confirmation, the operational half sent back unchanged (10 = 10.00).
        var now = Ask(on.Saved!);
        var accounted = await store.UpdateAsync(now with { OpenJobsAtPeriodEnd = "WorkInProgressAtYearEnd", MarkupPercent = 10.00m, AccountantApproved = true }, accountant, "comptable");
        Assert.Null(accounted.Error);
        Assert.Equal(("WorkInProgressAtYearEnd", true, "comptable"), (accounted.Saved!.OpenJobsAtPeriodEnd, accounted.Saved.AccountantApproved, accounted.Saved.ApprovedBy));
        foreach (var operational in new[]
        {
            Ask(accounted.Saved) with { Module = "Off" },
            Ask(accounted.Saved) with { MarkupPercent = 12m },
            Ask(accounted.Saved) with { CostIncludes = "PartsAndOutsideWorkOnly" },
        })
        {
            var refused = await store.UpdateAsync(operational, accountant, "comptable");
            Assert.Equal((403, MaintenanceSettingsStore.OperationalForbiddenCode), (refused.Status, refused.Code));
        }

        // Neither key: nothing may change, though saving the panel unchanged is harmless.
        var neither = Only(PermissionCatalog.MaintenanceView);
        Assert.Null((await store.UpdateAsync(Ask(accounted.Saved), neither, "lecteur")).Error);
        Assert.Equal(403, (await store.UpdateAsync(Ask(accounted.Saved) with { JobCloser = "NotTheOpener" }, neither, "lecteur")).Status);
        var kept = await store.SettingsAsync();
        Assert.Equal(("On", "WorkInProgressAtYearEnd", "AnyHolder"), (kept.Module, kept.OpenJobsAtPeriodEnd, kept.JobCloser));
    }

    [Theory]
    // M7d lifted MS-30 "capitalised" (MaintenanceCapitalisationTests) and MS-38 "per piece".
    [InlineData("Advances", "Taken")]
    // MS-04 "workshop list only" (MaintenanceWorkshopTests) and MS-34's other options (MaintenanceCustomerIncomeTests) are built.
    public async Task The_options_kept_for_a_later_phase_are_known_but_refused_for_now(string field, string value)
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _c = connection;
        await using var _d = db;
        var store = new MaintenanceSettingsStore(db);
        var ask = await CurrentAsync(store);
        ask = field switch
        {
            "OwnRepairs" => ask with { OwnRepairs = value },
            "RepairHistory" => ask with { RepairHistory = value },
            "Advances" => ask with { Advances = value },
            "BookableEmployees" => ask with { BookableEmployees = value },
            "PartsPosting" => ask with { PartsPosting = value },
            "CustomerIncome" => ask with { CustomerIncome = value },
            _ => ask with { Rework = value },
        };
        var refused = await store.UpdateAsync(ask, Everything, "comptable");
        Assert.Equal((400, MaintenanceSettingsStore.NotYetCode), (refused.Status, refused.Code));
        Assert.Contains(field switch { "OwnRepairs" => "MS-30", "RepairHistory" => "MS-38", "Advances" => "MS-35", "BookableEmployees" => "MS-04", "PartsPosting" => "MS-32", "CustomerIncome" => "MS-34", _ => "MS-37" }, refused.Error);
        Assert.Equal(0, await db.MaintenanceSettings.CountAsync());
    }

    [Fact]
    public async Task MS_04_only_the_workshop_list_is_taken_under_maintenance_settings_even_while_every_list_is_empty()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _c = connection;
        await using var _d = db;
        var store = new MaintenanceSettingsStore(db);
        var workshop = await CurrentAsync(store) with { BookableEmployees = "WorkshopListOnly" };

        // The operational half: the accountant alone may not change it.
        var refused = await store.UpdateAsync(workshop, Only(PermissionCatalog.AccountingManage), "comptable");
        Assert.Equal((403, MaintenanceSettingsStore.OperationalForbiddenCode), (refused.Status, refused.Code));
        // No list holds anyone yet: taken all the same (the page warns which branches would book nobody).
        Assert.Equal(0, await db.MaintenanceWorkshopMembers.CountAsync());
        var saved = await store.UpdateAsync(workshop, Only(PermissionCatalog.MaintenanceSettings), "admin");
        Assert.Null(saved.Error);
        Assert.Equal("WorkshopListOnly", saved.Saved!.BookableEmployees);
        // And back to the default.
        var back = await store.UpdateAsync(Ask(saved.Saved) with { BookableEmployees = "AnyEmployeeOfBranch" }, Only(PermissionCatalog.MaintenanceSettings), "admin");
        Assert.Equal("AnyEmployeeOfBranch", back.Saved!.BookableEmployees);
    }

    [Fact]
    public async Task MS_35_advances_are_taken_once_customer_advances_are_held_on_419()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _c = connection;
        await using var _d = db;
        var store = new MaintenanceSettingsStore(db);
        var taken = await CurrentAsync(store) with { Advances = "Taken" };

        // On 411 (the default) still refused, pointing at the receivables setting.
        var refused = await store.UpdateAsync(taken, Everything, "comptable");
        Assert.Equal((400, MaintenanceSettingsStore.NotYetCode), (refused.Status, refused.Code));
        Assert.Contains("419", refused.Error);

        var on = await new ParadisERP.Infrastructure.Receivables.ReceivableSettingsStore(db)
            .UpdateAsync(new UpdateReceivableSettingsRequest("AdvancesAccount", "AtInvoice"), "comptable");
        Assert.Null(on.Error);
        var saved = await store.UpdateAsync(taken, Everything, "comptable");
        Assert.Null(saved.Error);
        Assert.Equal("Taken", saved.Saved!.Advances);
    }

    [Fact]
    public async Task The_accountant_confirms_a_covered_change_asks_again_and_unticking_withdraws_it()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _c = connection;
        await using var _d = db;
        var store = new MaintenanceSettingsStore(db);
        var ask = await CurrentAsync(store);

        var confirmed = (await store.UpdateAsync(ask with { AccountantApproved = true }, Everything, "comptable")).Saved!;
        Assert.Equal((true, "comptable"), (confirmed.AccountantApproved, confirmed.ApprovedBy));
        var at = confirmed.ApprovedAtUtc;
        Assert.NotNull(at);

        // A choice the confirmation does not cover (the module) keeps it as given.
        var moduleOn = (await store.UpdateAsync(Ask(confirmed) with { Module = "On" }, Everything, "admin")).Saved!;
        Assert.Equal((true, "comptable", at), (moduleOn.AccountantApproved, moduleOn.ApprovedBy, moduleOn.ApprovedAtUtc));

        // A covered choice saved confirmed is confirmed again by whoever saved it.
        var changed = (await store.UpdateAsync(Ask(moduleOn) with { OpenJobsAtPeriodEnd = "WorkInProgressAtYearEnd" }, Everything, "chef")).Saved!;
        Assert.Equal((true, "chef"), (changed.AccountantApproved, changed.ApprovedBy));

        // A covered cost choice changed by someone who may not confirm leaves the costs provisional.
        var byOperations = await store.UpdateAsync(Ask(changed) with { CostIncludes = "PartsAndOutsideWorkOnly" },
            Only(PermissionCatalog.MaintenanceSettings), "atelier");
        Assert.Null(byOperations.Error);
        Assert.Equal((false, null, null), (byOperations.Saved!.AccountantApproved, byOperations.Saved.ApprovedBy, byOperations.Saved.ApprovedAtUtc));

        // Unticked, the confirmation goes.
        var again = (await store.UpdateAsync(Ask(byOperations.Saved) with { AccountantApproved = true }, Everything, "comptable")).Saved!;
        Assert.True(again.AccountantApproved);
        var withdrawn = (await store.UpdateAsync(Ask(again) with { AccountantApproved = false }, Everything, "comptable")).Saved!;
        Assert.Equal((false, null, null), (withdrawn.AccountantApproved, withdrawn.ApprovedBy, withdrawn.ApprovedAtUtc));
    }

    [Fact]
    public async Task Over_the_route_a_manager_reads_and_sets_the_accounting_half_but_not_the_module_and_the_admin_sets_both()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _c = connection;
        await using var _d = db;
        var manager = TillHarness.Principal(UserRole.Manager, null);  // accounting.manage by role, no maintenance key
        var admin = TillHarness.Principal(UserRole.Admin, null);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var (status, body) = await BranchLockTests.InvokeAsync(db, a => a.MapMaintenanceEndpoints(), "GET", "/api/maintenance/settings", manager, "");
        Assert.Equal(200, status);
        var current = Ask(JsonSerializer.Deserialize<MaintenanceSettingsDto>(body, options)!);
        Assert.Equal("Off", current.Module);

        var put = (UpdateMaintenanceSettingsRequest r) => JsonSerializer.Serialize(r, options);
        var refused = await BranchLockTests.InvokeAsync(db, a => a.MapMaintenanceEndpoints(), "PUT", "/api/maintenance/settings",
            manager, put(current with { Module = "On" }));
        Assert.Equal(403, refused.Status);
        Assert.Equal(MaintenanceSettingsStore.OperationalForbiddenCode, JsonDocument.Parse(refused.Body).RootElement.GetProperty("code").GetString());

        var accounting = await BranchLockTests.InvokeAsync(db, a => a.MapMaintenanceEndpoints(), "PUT", "/api/maintenance/settings",
            manager, put(current with { LabourInBooks = "MovedIntoJobs" }));
        Assert.Equal(200, accounting.Status);

        var both = await BranchLockTests.InvokeAsync(db, a => a.MapMaintenanceEndpoints(), "PUT", "/api/maintenance/settings",
            admin, put(current with { Module = "On", LabourInBooks = "MovedIntoJobs" }));
        Assert.Equal(200, both.Status);
        var saved = JsonSerializer.Deserialize<MaintenanceSettingsDto>(both.Body, options)!;
        Assert.Equal(("On", "MovedIntoJobs"), (saved.Module, saved.LabourInBooks));
    }
}
