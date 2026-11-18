using System.Text.Json;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Manufacturing;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Manufacturing;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Manufacturing.MfgHarness;
using static ParadiERP.Tests.Manufacturing.ProductionWorld;
using S = ParadiERP.Domain.ManufacturingAccountSlots;

namespace ParadiERP.Tests.Manufacturing;

/// <summary>
/// The accountant's panel (owner, 2026-11-09: "make everything as settings and accountant set it in cpanel", then
/// "finish please as recommended"): the last costing choices (C-07, C-09, C-12), the accountant's confirmation, and
/// Phase D — posting a closed month of the practice ledger to the ledger, and reversing it.
/// </summary>
public class AccountantPanelTests
{
    private const string Ledger = "/api/manufacturing/accounting/practice-ledger";
    private static readonly Guid Karim = Guid.NewGuid(), Stage = Guid.NewGuid();

    private static CostingInput Order(CostingPolicy policy, bool completed = true) => new(
        PlannedBaseQty: 100m, AcceptedBaseQty: 96m, RejectedBaseQty: 4m, Completed: completed, StandardHours: 8m, StandardRate: 300m,
        Materials: [new(Guid.NewGuid(), "WIRE", ManufacturingRole.RawMaterial, "kg", 100m, 150m, 100m, 0m, 100m, 0m, 15_000m, 0m)],
        Bookings: [new(Karim, Stage, new DateOnly(2026, 10, 20), 2026, 10, 8m)],
        Rates: new Dictionary<(Guid, int, int), LabourRate> { [(Karim, 2026, 10)] = new(250m, LabourRateKind.Actual) },
        Expenses: [new CostingExpense(Guid.NewGuid(), 1_190m, new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), Settled: true)],
        Releases: [new CostingRelease(Guid.NewGuid(), "OR-000001", 96m, new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 10, 21), 96m)],
        Policy: policy);

    [Fact]
    public void Expenses_count_before_the_recoverable_vat_and_rejects_kept_recover_their_share_once_the_order_is_completed()
    {
        var plain = ProductionCostEngine.Compute(Order(CostingPolicy.Default));
        var chosen = ProductionCostEngine.Compute(Order(new CostingPolicy(0m, ExpenseVatPercent: 19m, ScrapRecoveryPercent: 10m)));

        // 1,190 recorded is 1,000 before 19 % of recoverable VAT.
        Assert.Equal((1_190m, 1_000m), (plain.Actual.Expenses, chosen.Actual.Expenses));
        // 4 kg rejected × 150 of standard material a kg × 10 %.
        Assert.Equal((0m, 60m), (plain.Actual.Recovery, chosen.Actual.Recovery));
        Assert.Equal(ProductionCostEngine.ElementRecorded, chosen.Statuses.Recovery);
        // The last release carries the cost less what the scrap recovered.
        Assert.Equal(15_000m + 2_000m + 1_000m - 60m, chosen.Releases.Single().Value);
        // Open, nothing is recovered yet.
        Assert.Equal(0m, ProductionCostEngine.Compute(Order(new CostingPolicy(0m, ScrapRecoveryPercent: 10m), completed: false)).Actual.Recovery);
    }

    [Fact]
    public void Rejects_kept_enter_scrap_stock_when_the_order_closes_under_either_model()
    {
        var closed = new PracticeOrder(Guid.NewGuid(), "MO-000001", Guid.NewGuid(), OpenAtStart: false, OpenAtEnd: false,
            ClosedDay: new DateOnly(2026, 10, 25), Start: PracticeCostPoint.Zero,
            End: new PracticeCostPoint(15_000m, 2_000m, 0m, 1_000m, 17_940m, Recovery: 60m),
            Documents: [new("MI-000001", new DateOnly(2026, 10, 20), false, [new(PracticeMaterialGroup.Raw, 15_000m)])],
            Releases: [new PracticeRelease("OR-000001", new DateOnly(2026, 10, 21), 17_940m)]);

        foreach (var model in new[] { ManufacturingAccountingModel.ByNature, ManufacturingAccountingModel.WorkInProgress })
        {
            var r = PracticeLedger.Build(new PracticeLedgerChoices(model, MonthEndWorkInProgress.Monthly, LateCostTreatment.StayInCharges,
                VarianceTreatment.ManagementOnly, ScrapRecovered: true), 2026, 10, [closed]);
            var scrap = Assert.Single(r.Entries, e => e.Kind == PracticeLedger.KindScrapKept);
            Assert.Equal(new PracticeLine(S.ScrapStock, 60m, 0m), scrap.Lines[0]);
            Assert.DoesNotContain(r.Entries, e => e.Kind == PracticeLedger.KindCloseResidual);
            Assert.All(r.Checks, c => Assert.True(c.Passed, c.Code));
            Assert.DoesNotContain(PracticeLedger.NoteScrapNotRecorded, r.Notes);
        }
    }

    [Fact]
    public async Task The_accountant_confirms_switches_stock_operations_and_chooses_the_first_month_to_post_in_the_panel()
    {
        await using var s = await NewAsync();
        var choices = new ManufacturingCostingChoices(s.Db);
        UpdateManufacturingCostingSettingsRequest Ask(bool approved, decimal overhead = 0m, string? from = "2026-10", bool stock = true) => new(
            "ByNature", overhead, "StandardMaterialPlusConversion", "None", 0m, "Monthly", "StayInCharges", "ManagementOnly",
            "BeforeRecoverableVat", 19m, "PercentOfMaterial", 10m, stock, approved, from);

        Assert.False(ManufacturingCostingChoices.StockOperations(s.Db));
        Assert.NotNull((await choices.UpdateSettingsAsync(Ask(true, from: "October"), "comptable")).Error);
        Assert.NotNull((await choices.UpdateSettingsAsync(Ask(true) with { ScrapRecoveryPercent = 0m }, "comptable")).Error);

        var (saved, error) = await choices.UpdateSettingsAsync(Ask(true), "comptable");
        Assert.Null(error);
        Assert.Equal((true, "comptable", true, "2026-10", 19m, 10m),
            (saved!.AccountantApproved, saved.ApprovedBy, saved.StockOperations, saved.PostingFromMonth, saved.ExpenseVatPercent, saved.ScrapRecoveryPercent));
        Assert.True(ManufacturingCostingChoices.StockOperations(s.Db));

        // A change saved confirmed is confirmed by whoever saved it; unticked, the confirmation goes.
        var changed = (await choices.UpdateSettingsAsync(Ask(true, overhead: 50m), "chef")).Saved!;
        Assert.Equal("chef", changed.ApprovedBy);
        var withdrawn = (await choices.UpdateSettingsAsync(Ask(false, overhead: 50m, from: null, stock: false), "chef")).Saved!;
        Assert.Equal((false, null, null, false), (withdrawn.AccountantApproved, withdrawn.ApprovedBy, withdrawn.PostingFromMonth, withdrawn.StockOperations));
    }

    [Fact]
    public async Task A_closed_month_posts_once_to_the_ledger_balanced_and_a_reversal_takes_it_back_so_it_can_post_again()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var (_, id) = await ProductionCostingTests.WorkedOrderAsync(s.Db, w);
        var october = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
        await s.Db.ProductionOrders.Where(o => o.Id == id).ExecuteUpdateAsync(u => u.SetProperty(o => o.CreatedAtUtc, october));
        await s.Db.MaterialMovements.Where(m => m.OrderId == id).ExecuteUpdateAsync(u => u.SetProperty(m => m.AtUtc, october));
        var body = JsonSerializer.Serialize(new { year = 2026, month = 10 });

        // Nothing posts until the accountant chooses the first month.
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "posting_off", "POST", Ledger + "/post", body);
        var choices = new ManufacturingCostingChoices(s.Db);
        await choices.UpdateSettingsAsync(new UpdateManufacturingCostingSettingsRequest(
            "ByNature", 0m, "StandardMaterialPlusConversion", "None", 0m, "Monthly", "StayInCharges", "ManagementOnly",
            PostingFromMonth: "2026-10"), "comptable");
        // Nor while a slot the month uses names no account.
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "checks_not_passed", "POST", Ledger + "/post", body);

        var accounts = await s.Db.LedgerAccounts.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Code).Take(4).ToListAsync();
        var slots = new[] { S.RawMaterialStock, S.RawMaterialConsumption, S.WorkInProgress, S.ProductionStocked };
        Assert.Null((await choices.UpdateAccountMapAsync(new UpdateManufacturingAccountMapRequest(
            slots.Select((slot, i) => new ManufacturingAccountMapLine(slot, accounts[i].Id)).ToList()), "comptable")).Error);

        var posted = await OkAsync(s.Db, w.AdminPrincipal, "POST", Ledger + "/post", body);
        var posting = posted.GetProperty("postings").EnumerateArray().Single();
        // The issue (15,750) and the month-end work in progress (17,750): two entries, as the practice ledger shows them.
        Assert.Equal((2, 33_500m), (posting.GetProperty("entries").GetInt32(), posting.GetProperty("totalDebit").GetDecimal()));
        var entries = await s.Db.LedgerEntries.Include(e => e.Lines).AsNoTracking()
            .Where(e => e.SourceType == ManufacturingLedgerPostings.SourceType).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(e.Lines.Sum(l => l.Debit), e.Lines.Sum(l => l.Credit)));
        Assert.Contains(entries.SelectMany(e => e.Lines), l => l.AccountId == accounts[2].Id && l.Debit == 17_750m);

        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "already_posted", "POST", Ledger + "/post", body);
        // A month before the first is never posted.
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "before_posting_start", "POST", Ledger + "/post", JsonSerializer.Serialize(new { year = 2026, month = 9 }));
        // A month posts whole: a reader of one line cannot post it.
        var (_, oneLine) = await ManagerAsync(s.Db, WarehouseA, [.. AllGrants, PermissionCatalog.PayrollManage]);
        await RefusedAsync(s.Db, oneLine, 403, "post_needs_every_line", "POST", Ledger + "/post", body);

        // Reversed by mirror entries, never deleted; then the month posts again under its next posting.
        var reversed = await OkAsync(s.Db, w.AdminPrincipal, "POST", Ledger + "/reverse", body);
        Assert.NotEqual(JsonValueKind.Null, reversed.GetProperty("postings")[0].GetProperty("reversedAtUtc").ValueKind);
        Assert.Equal(2, await s.Db.LedgerEntries.CountAsync(e => e.SourceType == ManufacturingLedgerPostings.SourceType && e.IsReversal));
        var again = await OkAsync(s.Db, w.AdminPrincipal, "POST", Ledger + "/post", body);
        Assert.Equal(2, again.GetProperty("postings")[1].GetProperty("generation").GetInt32());
        Assert.Equal(4, await s.Db.LedgerEntries.CountAsync(e => e.SourceType == ManufacturingLedgerPostings.SourceType && !e.IsReversal));
    }
}
