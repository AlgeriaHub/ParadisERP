using System.Text.Json;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Maintenance;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Maintenance;
using AlgeriaPos.Infrastructure.Manufacturing;
using AlgeriaPos.Infrastructure.Payroll;
using AlgeriaPos.Infrastructure.Receivables;
using AlgeriaPos.Infrastructure.Rentals;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Maintenance.MaintenanceWorld;
using S = AlgeriaPos.Domain.MaintenanceAccountSlots;

namespace AlgeriaPos.Tests.Maintenance;

/// <summary>
/// MS-34, customer repair income (docs/maintenance/plan.md "MS-34 — as built"): the service income slot (an active income
/// account of class 7, seeded empty, never guessed); "one service account" crediting it with the whole price; "parts and
/// labour apart" crediting the parts at their sale price to 700 and the rest to it, capped at the price, the totals and the
/// tax those of one line; both refused while the slot is unmapped; the credit note giving back per line; the practice
/// ledger and the company profit report reading the invoice as before. The default (707) is MaintenanceCustomerJobTests'.
/// </summary>
public class MaintenanceCustomerIncomeTests
{
    private static readonly Guid Bearing = Guid.Parse("a5a5a5a5-0000-0000-0000-000000000001");
    private static readonly Guid Seal = Guid.Parse("a5a5a5a5-0000-0000-0000-000000000002");
    private static readonly Guid RepairIncome = Guid.Parse("a5a5a5a5-0000-0000-0000-0000000000a7");
    private static readonly Guid OldIncome = Guid.Parse("a5a5a5a5-0000-0000-0000-0000000000a8");
    private const string RepairIncomeCode = "7062";

    private sealed class Dz : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => RegionalPresets.Create("DZ")!;
    }

    private static readonly Func<string, bool> Everything = _ => true;

    /// <summary>
    /// Customer jobs on, a bearing sold at 1,500 before tax and a seal at 1.40 (40 and 10 on the shelf), and two income
    /// accounts the accountant opened: 7062 active, 7069 inactive.
    /// </summary>
    private static async Task<MaintenanceWorld> WorldAsync(Action<MaintenanceSettings>? more = null)
    {
        var w = await NewAsync(On(s => { s.CustomerJobs = MaintenanceSwitch.On; more?.Invoke(s); }));
        w.Db.Products.AddRange(
            new Product { Id = Bearing, Sku = "ROU-01", NameFr = "Roulement", NameAr = "محمل", NameEn = "Bearing",
                Price = 1_500m, TaxRate = 19m, CostPrice = 800m, TrackStock = true, ManufacturingRole = ManufacturingRole.SparePart },
            new Product { Id = Seal, Sku = "JNT-01", NameFr = "Joint", NameAr = "حشية", NameEn = "Seal",
                Price = 1.40m, TaxRate = 19m, CostPrice = 0.50m, TrackStock = true, ManufacturingRole = ManufacturingRole.SparePart });
        w.Db.ProductStocks.AddRange(
            new ProductStock { ProductId = Bearing, BranchId = Algiers, StockQty = 40m },
            new ProductStock { ProductId = Seal, BranchId = Algiers, StockQty = 10m });
        w.Db.LedgerAccounts.AddRange(
            new LedgerAccount { Id = RepairIncome, Code = RepairIncomeCode, NameFr = "Prestations de réparation", NameAr = "خدمات الإصلاح",
                NameEn = "Repair services", Type = LedgerAccountType.Income, SortOrder = 900 },
            new LedgerAccount { Id = OldIncome, Code = "7069", NameFr = "Ancien compte", NameAr = "حساب قديم", NameEn = "Old account",
                Type = LedgerAccountType.Income, IsActive = false, SortOrder = 901 });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        return w;
    }

    private static MaintenanceCustomerJobs Service(MaintenanceWorld w)
    {
        var regional = new Dz();
        var ledger = new LedgerPoster(w.Db);
        var costing = new MaintenanceCosting(w.Db, new LabourRates(w.Db, new PayrollSettingsStore(w.Db, regional), regional), regional);
        return new MaintenanceCustomerJobs(w.Db, w.Jobs, costing,
            new ReceivableDocuments(w.Db, ledger, new RentalDocuments(w.Db, ledger, regional), regional), regional);
    }

    private static ReceivableDocuments Receivables(MaintenanceWorld w) => new(w.Db, new LedgerPoster(w.Db), w.Rentals, new Dz());

    private static CreateMaintenanceJobRequest CustomerJob() =>
        new(Guid.NewGuid().ToString("N"), Algiers, "Customer", null, 1m, null, "N° 4471", "Ne démarre plus",
            CustomerId: Contractor, Equipment: "Bétonnière 350 L du client");

    /// <summary>A completed customer job priced <paramref name="price"/> before tax, with parts issued (and some returned).</summary>
    private static async Task<Guid> PricedJobAsync(MaintenanceWorld w, decimal price, decimal bearings = 0m, decimal returned = 0m, decimal seals = 0m)
    {
        var job = (await w.OpenAsync(CustomerJob())).Id;
        var parts = new MaintenanceJobParts(w.Db, new Dz());
        var issue = new List<MaintenancePartLineRequest>();
        if (bearings > 0) issue.Add(new(Bearing, bearings));
        if (seals > 0) issue.Add(new(Seal, seals));
        if (issue.Count > 0)
            Assert.True((await parts.MoveAsync(job, new MaintenancePartsRequest(Guid.NewGuid().ToString("N"), "Issue", issue, null), Chef)).Ok);
        if (returned > 0)
            Assert.True((await parts.MoveAsync(job, new MaintenancePartsRequest(Guid.NewGuid().ToString("N"), "Return",
                [new MaintenancePartLineRequest(Bearing, returned)], null), Chef)).Ok);
        w.Db.ChangeTracker.Clear();
        var set = await Service(w).SetPriceAsync(job, new SetMaintenanceJobPriceRequest(price), Chef, Everything);
        Assert.True(set.Ok, set.Error);
        w.Db.ChangeTracker.Clear();
        var done = await w.Jobs.CompleteAsync(job, new CompleteMaintenanceJobRequest(1m, 0m, "Réparée"), Second);
        Assert.True(done.Ok, done.Error);
        w.Db.ChangeTracker.Clear();
        return job;
    }

    private static async Task<MaintenanceResult<MaintenanceJobInvoiceDto>> InvoiceAsync(MaintenanceWorld w, Guid job, string? tax = null)
    {
        var raised = await Service(w).InvoiceAsync(job, new RaiseMaintenanceJobInvoiceRequest(DateTime.UtcNow.AddDays(30), TaxMode: tax), Chef, Everything);
        w.Db.ChangeTracker.Clear();
        return raised;
    }

    private static async Task<string?> MapAsync(MaintenanceWorld w, Guid? account)
    {
        var (_, error) = await new MaintenanceAccountMap(w.Db).UpdateAsync(new([new(S.ServiceIncome, account)]), "comptable");
        w.Db.ChangeTracker.Clear();
        return error;
    }

    private static async Task ChooseAsync(MaintenanceWorld w, MaintenanceIncomeAccount income)
    {
        var row = await w.Db.MaintenanceSettings.SingleAsync();
        row.CustomerIncome = income;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
    }

    /// <summary>What one source document posted, by account code (summed in memory: SQLite keeps decimals as text).</summary>
    private static async Task<Dictionary<string, decimal>> PostedAsync(MaintenanceWorld w, string sourceType, Guid sourceId)
    {
        var accounts = await w.Db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
        var entries = await w.Db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.SourceType == sourceType && e.SourceId == sourceId).ToListAsync();
        return entries.SelectMany(e => e.Lines).GroupBy(l => accounts[l.AccountId]).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    private static async Task<List<(string Name, decimal Net, string Account, decimal Rate)>> LinesAsync(MaintenanceWorld w, Guid invoiceId) =>
        (await w.Db.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoiceId).ToListAsync())
            .OrderBy(l => l.Position).Select(l => (l.NameSnapshot, l.LineNet, l.IncomeAccountCode, l.TaxRate)).ToList();

    private static UpdateMaintenanceSettingsRequest Ask(MaintenanceSettingsDto d) => new(
        d.Module, d.UnitsLeaveMaintenance, d.JobsFromReturnNotes, d.BookableEmployees, d.JobCloser, d.CustomerJobs,
        d.CostIncludes, d.OverheadPerLabourHour, d.LabourRate, d.WorkshopRatePerHour, d.BurdenAndVat, d.EmployerBurdenPercent,
        d.ExpenseVatPercent, d.CustomerPrice, d.LabourChargePerHour, d.MarkupPercent, d.ReturnRecharge, d.PriceBelowCost,
        d.RechargeSharing, d.OwnRepairs, d.PartsPosting, d.LabourInBooks, d.CustomerIncome, d.Advances, d.OpenJobsAtPeriodEnd,
        d.Rework, d.RepairHistory, d.PostingFromMonth, d.AccountantApproved, d.GuaranteeMonths, d.ProvisionPercent,
        d.CapitaliseAboveAmount, d.PerPieceAboveValue);

    // ---------- the slot and the settings ----------

    [Fact]
    public async Task The_service_income_slot_is_seeded_empty_takes_only_an_active_income_account_of_class_7_and_MS_34_waits_for_it()
    {
        await using var w = await WorldAsync();
        // Listed in the account map, empty, with no hint: never guessed.
        var slot = (await new MaintenanceAccountMap(w.Db).MapAsync()).Single(m => m.Slot == S.ServiceIncome);
        Assert.Equal(((Guid?)null, (string?)null, false), (slot.LedgerAccountId, slot.HintCode, slot.Shared));

        var store = new MaintenanceSettingsStore(w.Db);
        foreach (var option in new[] { "OneServiceAccount", "PartsAndLabourApart" })
        {
            var refused = await store.UpdateAsync(Ask(await store.SettingsAsync()) with { CustomerIncome = option }, Everything, "comptable");
            Assert.Equal((409, MaintenanceSettingsStore.AccountsNotMappedCode), (refused.Status, refused.Code));
            Assert.Contains("MS-34", refused.Error);
        }

        // A cost account, a balance-sheet account, an inactive income account: refused. An active 7x income account: taken.
        var expense = await w.Db.LedgerAccounts.AsNoTracking().SingleAsync(a => a.Code == LedgerAccounts.MaintenanceExpense);
        var customers = await w.Db.LedgerAccounts.AsNoTracking().SingleAsync(a => a.Code == LedgerAccounts.Customers);
        Assert.Contains("class 7", await MapAsync(w, expense.Id));
        Assert.Contains("class 7", await MapAsync(w, customers.Id));
        Assert.NotNull(await MapAsync(w, OldIncome));
        Assert.Null(await MaintenanceAccountMap.ServiceIncomeAsync(w.Db));
        Assert.Null(await MapAsync(w, RepairIncome));
        Assert.Equal(RepairIncomeCode, (await MaintenanceAccountMap.ServiceIncomeAsync(w.Db))!.Code);

        // Now both options are saved (the accountant's: MS-34 is accounting.manage's half).
        var parts = Ask(await store.SettingsAsync()) with { CustomerIncome = "PartsAndLabourApart" };
        Assert.Equal(MaintenanceSettingsStore.AccountingForbiddenCode,
            (await store.UpdateAsync(parts, k => k == PermissionCatalog.MaintenanceSettings, "chef")).Code);
        var saved = await store.UpdateAsync(parts, Everything, "comptable");
        Assert.Null(saved.Error);
        Assert.Equal("PartsAndLabourApart", saved.Saved!.CustomerIncome);
        Assert.Equal("OneServiceAccount", (await store.UpdateAsync(Ask(saved.Saved) with { CustomerIncome = "OneServiceAccount" }, Everything, "comptable")).Saved!.CustomerIncome);

        // While it is in force, its account stays named; back on the default, it may be cleared.
        Assert.Contains("MS-34", await MapAsync(w, null));
        Assert.Null((await store.UpdateAsync(Ask(await store.SettingsAsync()) with { CustomerIncome = "SameAsRecharges" }, Everything, "comptable")).Error);
        Assert.Null(await MapAsync(w, null));
    }

    [Fact]
    public async Task An_invoice_under_its_own_account_is_refused_while_the_slot_names_no_active_income_account()
    {
        await using var w = await WorldAsync(s => s.CustomerIncome = MaintenanceIncomeAccount.OneServiceAccount);
        var job = await PricedJobAsync(w, 20_000m);

        // A row saved before the slot existed (or edited behind the store): refused, never booked on a guessed account.
        var refused = await InvoiceAsync(w, job);
        Assert.Equal((409, MaintenanceSettingsStore.AccountsNotMappedCode), (refused.Status, refused.Code));
        Assert.Equal(0, await w.Db.Invoices.CountAsync());
        Assert.Equal("", (await Service(w).PriceAsync(job, Everything)).Value!.IncomeAccountCode);

        // Mapped, then made inactive in the chart: refused again.
        Assert.Null(await MapAsync(w, RepairIncome));
        var account = await w.Db.LedgerAccounts.SingleAsync(a => a.Id == RepairIncome);
        account.IsActive = false;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        Assert.Equal(MaintenanceSettingsStore.AccountsNotMappedCode, (await InvoiceAsync(w, job)).Code);
        Assert.Equal(0, await w.Db.Invoices.CountAsync());
        Assert.Equal("Completed", (await w.Db.MaintenanceJobs.AsNoTracking().SingleAsync(j => j.Id == job)).Status.ToString());
    }

    // ---------- one service account ----------

    [Fact]
    public async Task One_service_account_credits_the_whole_price_to_the_slot_untaxed_and_taxed()
    {
        await using var w = await WorldAsync();
        Assert.Null(await MapAsync(w, RepairIncome));
        await ChooseAsync(w, MaintenanceIncomeAccount.OneServiceAccount);
        Assert.Equal(RepairIncomeCode, (await Service(w).PriceAsync(await PricedJobAsync(w, 1m), Everything)).Value!.IncomeAccountCode);

        // 20,000 untaxed: Dr 411 20,000 / Cr 7062 20,000, one line, no 4457, nothing to 707 or 700.
        var plain = await PricedJobAsync(w, 20_000m, bearings: 4m);
        var raised = await InvoiceAsync(w, plain);
        Assert.True(raised.Ok, raised.Error);
        Assert.Equal((20_000m, 0m, 20_000m, RepairIncomeCode), (raised.Value!.SubTotalHt, raised.Value.TaxTotal, raised.Value.GrandTotalTtc, raised.Value.IncomeAccountCode));
        var line = (await LinesAsync(w, raised.Value.InvoiceId)).Single();
        Assert.Equal((20_000m, RepairIncomeCode, 0m), (line.Net, line.Account, line.Rate));
        Assert.StartsWith("MJ-", line.Name);
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.Customers] = 20_000m, [RepairIncomeCode] = -20_000m },
            await PostedAsync(w, LedgerSources.Invoice, raised.Value.InvoiceId));

        // 20,000 + 19 %: Dr 411 23,800 / Cr 7062 20,000 / Cr 4457 3,800.
        var taxed = await InvoiceAsync(w, await PricedJobAsync(w, 20_000m), tax: "Taxed");
        Assert.True(taxed.Ok, taxed.Error);
        Assert.Equal((20_000m, 3_800m, 23_800m), (taxed.Value!.SubTotalHt, taxed.Value.TaxTotal, taxed.Value.GrandTotalTtc));
        Assert.Equal(new Dictionary<string, decimal>
            {
                [LedgerAccounts.Customers] = 23_800m, [RepairIncomeCode] = -20_000m, [LedgerAccounts.TaxCollected] = -3_800m,
            },
            await PostedAsync(w, LedgerSources.Invoice, taxed.Value.InvoiceId));
    }

    // ---------- parts and labour apart ----------

    [Fact]
    public async Task Parts_and_labour_apart_credits_parts_at_sale_price_to_700_and_the_rest_to_the_slot_moving_no_stock()
    {
        await using var w = await WorldAsync();
        Assert.Null(await MapAsync(w, RepairIncome));
        await ChooseAsync(w, MaintenanceIncomeAccount.PartsAndLabourApart);

        // Five bearings issued, one returned: four used at 1,500 before tax = 6,000 of parts; the price is 20,000.
        var job = await PricedJobAsync(w, 20_000m, bearings: 5m, returned: 1m);
        var stock = await w.Db.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == Bearing && s.BranchId == Algiers);
        var movements = await w.Db.StockMovements.CountAsync();

        var raised = await InvoiceAsync(w, job);
        Assert.True(raised.Ok, raised.Error);
        Assert.Equal((20_000m, 0m, 20_000m, RepairIncomeCode), (raised.Value!.SubTotalHt, raised.Value.TaxTotal, raised.Value.GrandTotalTtc, raised.Value.IncomeAccountCode));
        var number = raised.Value.Job.Number;
        var lines = await LinesAsync(w, raised.Value.InvoiceId);
        Assert.Equal(2, lines.Count);
        Assert.Equal((6_000m, LedgerAccounts.Sales), (lines[0].Net, lines[0].Account));
        Assert.StartsWith($"Pièces — {number} — Bétonnière", lines[0].Name);
        Assert.Equal((14_000m, RepairIncomeCode), (lines[1].Net, lines[1].Account));
        Assert.StartsWith($"Main-d'œuvre et service — {number} — Bétonnière", lines[1].Name);
        Assert.Equal(new Dictionary<string, decimal>
            {
                [LedgerAccounts.Customers] = 20_000m, [LedgerAccounts.Sales] = -6_000m, [RepairIncomeCode] = -14_000m,
            },
            await PostedAsync(w, LedgerSources.Invoice, raised.Value.InvoiceId));
        // No stock moves and no cost of goods: the parts left with the job's own MJI documents.
        Assert.Equal(movements, await w.Db.StockMovements.CountAsync());
        Assert.Equal(stock.StockQty, (await w.Db.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == Bearing && s.BranchId == Algiers)).StockQty);

        // 20,000 + 19 %: the same totals as one line — 23,800 / 3,800 — the tax on the rate's own total.
        var taxed = await InvoiceAsync(w, await PricedJobAsync(w, 20_000m, bearings: 4m), tax: "Taxed");
        Assert.True(taxed.Ok, taxed.Error);
        Assert.Equal((20_000m, 3_800m, 23_800m), (taxed.Value!.SubTotalHt, taxed.Value.TaxTotal, taxed.Value.GrandTotalTtc));
        Assert.All(await LinesAsync(w, taxed.Value.InvoiceId), l => Assert.Equal(19m, l.Rate));
        Assert.Equal(new Dictionary<string, decimal>
            {
                [LedgerAccounts.Customers] = 23_800m, [LedgerAccounts.Sales] = -6_000m, [RepairIncomeCode] = -14_000m,
                [LedgerAccounts.TaxCollected] = -3_800m,
            },
            await PostedAsync(w, LedgerSources.Invoice, taxed.Value.InvoiceId));
    }

    [Fact]
    public async Task Parts_and_labour_apart_rounds_the_tax_once_on_the_total_as_one_line_would()
    {
        await using var w = await WorldAsync();
        Assert.Null(await MapAsync(w, RepairIncome));
        // One seal at 1.40 and a price of 99.80: per line 0.266 → 0.27 and 18.696 → 18.70 would make 18.97; the invoice
        // rounds 19 % of 99.80 once, 18.962 → 18.96, exactly as the single line of "one service account".
        await ChooseAsync(w, MaintenanceIncomeAccount.OneServiceAccount);
        var single = await InvoiceAsync(w, await PricedJobAsync(w, 99.80m, seals: 1m), tax: "Taxed");
        Assert.True(single.Ok, single.Error);
        await ChooseAsync(w, MaintenanceIncomeAccount.PartsAndLabourApart);
        var split = await InvoiceAsync(w, await PricedJobAsync(w, 99.80m, seals: 1m), tax: "Taxed");
        Assert.True(split.Ok, split.Error);

        Assert.Equal((99.80m, 18.96m, 118.76m), (single.Value!.SubTotalHt, single.Value.TaxTotal, single.Value.GrandTotalTtc));
        Assert.Equal((single.Value.SubTotalHt, single.Value.TaxTotal, single.Value.GrandTotalTtc),
            (split.Value!.SubTotalHt, split.Value.TaxTotal, split.Value.GrandTotalTtc));
        Assert.Equal(new[] { 1.40m, 98.40m }, (await LinesAsync(w, split.Value.InvoiceId)).Select(l => l.Net));
        var posted = await PostedAsync(w, LedgerSources.Invoice, split.Value.InvoiceId);
        Assert.Equal((118.76m, -1.40m, -98.40m, -18.96m),
            (posted[LedgerAccounts.Customers], posted[LedgerAccounts.Sales], posted[RepairIncomeCode], posted[LedgerAccounts.TaxCollected]));
    }

    [Fact]
    public async Task Parts_above_the_price_are_capped_at_it_and_a_line_of_zero_is_left_out()
    {
        await using var w = await WorldAsync();
        Assert.Null(await MapAsync(w, RepairIncome));
        await ChooseAsync(w, MaintenanceIncomeAccount.PartsAndLabourApart);

        // Four bearings (6,000 at sale price) on a job priced 5,000: parts 5,000 on 700, no labour line.
        var capped = await InvoiceAsync(w, await PricedJobAsync(w, 5_000m, bearings: 4m));
        Assert.True(capped.Ok, capped.Error);
        var line = (await LinesAsync(w, capped.Value!.InvoiceId)).Single();
        Assert.Equal((5_000m, LedgerAccounts.Sales), (line.Net, line.Account));
        Assert.StartsWith("Pièces — MJ-", line.Name);
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.Customers] = 5_000m, [LedgerAccounts.Sales] = -5_000m },
            await PostedAsync(w, LedgerSources.Invoice, capped.Value.InvoiceId));

        // No parts at all: one labour and service line, on the slot.
        var labour = await InvoiceAsync(w, await PricedJobAsync(w, 3_000m));
        Assert.True(labour.Ok, labour.Error);
        line = (await LinesAsync(w, labour.Value!.InvoiceId)).Single();
        Assert.Equal((3_000m, RepairIncomeCode), (line.Net, line.Account));
        Assert.StartsWith("Main-d'œuvre et service — MJ-", line.Name);

        // The pure rule: the two always add up to the price.
        Func<decimal, decimal> round = x => Math.Round(x, 2, MidpointRounding.AwayFromZero);
        Assert.Equal((5_000m, 0m), MaintenancePricing.SplitPartsAndLabour(5_000m, [new MaintenancePricedPart(Bearing, 4m, 1_500m)], round));
        Assert.Equal((6_000m, 14_000m), MaintenancePricing.SplitPartsAndLabour(20_000m, [new MaintenancePricedPart(Bearing, 4m, 1_500m)], round));
        Assert.Equal((0m, 300m), MaintenancePricing.SplitPartsAndLabour(300m, [], round));
    }

    // ---------- the credit note, the practice ledger and company profit ----------

    [Fact]
    public async Task A_credit_note_gives_back_on_each_line_s_account_and_the_practice_ledger_and_profit_still_tie()
    {
        await using var w = await WorldAsync();
        Assert.Null(await MapAsync(w, RepairIncome));
        await ChooseAsync(w, MaintenanceIncomeAccount.PartsAndLabourApart);
        var job = await PricedJobAsync(w, 20_000m, bearings: 4m);
        var raised = await InvoiceAsync(w, job, tax: "Taxed");
        Assert.True(raised.Ok, raised.Error);
        var invoiceId = raised.Value!.InvoiceId;

        // M6's practice ledger reads the invoice's net, whatever its lines: listed, matched, invoices_tie passing.
        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);
        var (status, body) = await w.CallAsync(admin, "GET", "/api/maintenance/practice-ledger", "");
        Assert.True(status == 200, body);
        var ledger = JsonDocument.Parse(body).RootElement;
        var listed = ledger.GetProperty("invoices").EnumerateArray().Single();
        Assert.Equal((job, true, 20_000m), (listed.GetProperty("jobId").GetGuid(), listed.GetProperty("jobMatched").GetBoolean(), listed.GetProperty("netAmount").GetDecimal()));
        Assert.True(ledger.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "invoices_tie").GetProperty("passed").GetBoolean());

        // Company profit: revenue is the invoice's net before tax, 20,000 (both lines; the VAT is not revenue).
        (status, body) = await w.CallAsync(admin, "GET", "/api/maintenance/reports/profit", "");
        Assert.True(status == 200, body);
        var row = JsonDocument.Parse(body).RootElement.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("jobId").GetGuid() == job);
        Assert.Equal(("Invoice", 20_000m), (row.GetProperty("revenueSource").GetString(), row.GetProperty("revenue").GetDecimal()));

        // The labour line alone credited: back on the slot, with its own tax (14,000 × 19 % = 2,660).
        var labourLine = await w.Db.InvoiceLines.AsNoTracking().SingleAsync(l => l.InvoiceId == invoiceId && l.IncomeAccountCode == RepairIncomeCode);
        var partial = await Receivables(w).CreateCreditNoteAsync(invoiceId,
            new CreateCreditNoteRequest("Main-d'œuvre offerte", Lines: [new CreditNoteLineRequest(labourLine.Id, 1m)]), "admin");
        Assert.True(partial.Ok, partial.Message);
        w.Db.ChangeTracker.Clear();
        Assert.Equal(new Dictionary<string, decimal>
            {
                [RepairIncomeCode] = 14_000m, [LedgerAccounts.TaxCollected] = 2_660m, [LedgerAccounts.Customers] = -16_660m,
            },
            await PostedAsync(w, LedgerSources.CreditNote, partial.Value!.Id));

        // The rest: the parts back on 700.
        var rest = await Receivables(w).CreateCreditNoteAsync(invoiceId, new CreateCreditNoteRequest("Annulée", Lines: null), "admin");
        Assert.True(rest.Ok, rest.Message);
        w.Db.ChangeTracker.Clear();
        Assert.Equal(new Dictionary<string, decimal>
            {
                [LedgerAccounts.Sales] = 6_000m, [LedgerAccounts.TaxCollected] = 1_140m, [LedgerAccounts.Customers] = -7_140m,
            },
            await PostedAsync(w, LedgerSources.CreditNote, rest.Value!.Id));
        Assert.Equal(0m, (await w.Db.Customers.AsNoTracking().SingleAsync(c => c.Id == Contractor)).Balance);

        // Fully credited: no revenue left in company profit.
        (_, body) = await w.CallAsync(admin, "GET", "/api/maintenance/reports/profit", "");
        row = JsonDocument.Parse(body).RootElement.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("jobId").GetGuid() == job);
        Assert.Equal(0m, row.GetProperty("revenue").GetDecimal());
    }
}
