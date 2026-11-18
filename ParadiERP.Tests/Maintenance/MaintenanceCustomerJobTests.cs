using System.Security.Claims;
using System.Text.Json;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Maintenance;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Maintenance;
using ParadiERP.Infrastructure.Manufacturing;
using ParadiERP.Infrastructure.Payroll;
using ParadiERP.Infrastructure.Receivables;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Tests.Manufacturing;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Maintenance.MaintenanceWorld;

namespace ParadiERP.Tests.Maintenance;

/// <summary>
/// Phase M5a (docs/maintenance/plan.md §2.1, §3.2, §4 MS-06, MS-20 to MS-25, MS-34) and the M5 company profit report
/// (§3.3): customer jobs over outside equipment, each price basis on a worked example, MS-25, the invoice through the
/// existing writer (411 / 707 / 4457, untaxed unless ticked, once per job), the MS-23 repair rates leaving rentals
/// untouched, and company profit = external revenue − cost under M4's privacy rule.
/// </summary>
public class MaintenanceCustomerJobTests
{
    private const string PriceRoute = "/api/maintenance/jobs/{id:guid}/price";
    private const string InvoiceRoute = "/api/maintenance/jobs/{id:guid}/invoice";
    private const string ProfitRoute = "/api/maintenance/reports/profit";

    private static readonly Guid Bearing = Guid.Parse("a5a5a5a5-0000-0000-0000-000000000001");
    private static readonly Guid Karim = Guid.Parse("a5a5a5a5-0000-0000-0000-0000000000e1");
    private static readonly Guid Samir = Guid.Parse("a5a5a5a5-0000-0000-0000-0000000000e2");

    /// <summary>The Algeria preset: prices before tax, 19 % the default rate, two decimals.</summary>
    private sealed class Dz : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => RegionalPresets.Create("DZ")!;
    }

    private static readonly Func<string, bool> Everything = _ => true;

    // ---------- a company ----------

    /// <summary>The maintenance world with customer jobs on, a bearing sold at 1,500 before tax (cost 800, 40 on the shelf) and two fitters.</summary>
    private static async Task<MaintenanceWorld> WorldAsync(Action<MaintenanceSettings>? more = null, decimal inMaintenance = 0m)
    {
        var w = await NewAsync(On(s => { s.CustomerJobs = MaintenanceSwitch.On; more?.Invoke(s); }), inMaintenance);
        w.Db.Products.Add(new Product { Id = Bearing, Sku = "ROU-01", NameFr = "Roulement", NameAr = "محمل", NameEn = "Bearing",
            Price = 1_500m, TaxRate = 19m, CostPrice = 800m, TrackStock = true, ManufacturingRole = ManufacturingRole.SparePart });
        w.Db.ProductStocks.Add(new ProductStock { ProductId = Bearing, BranchId = Algiers, StockQty = 40m });
        w.Db.Employees.AddRange(
            new Employee { Id = Karim, Number = "E-101", FullName = "Karim Benali", BranchId = Algiers, HiredOn = new DateOnly(2025, 1, 1), BaseRate = 52_000m },
            new Employee { Id = Samir, Number = "E-102", FullName = "Samir Haddad", BranchId = Algiers, HiredOn = new DateOnly(2025, 1, 1), BaseRate = 48_000m });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        return w;
    }

    private static MaintenanceCosting Costing(MaintenanceWorld w)
    {
        var regional = new Dz();
        return new MaintenanceCosting(w.Db, new LabourRates(w.Db, new PayrollSettingsStore(w.Db, regional), regional), regional);
    }

    private static MaintenanceCustomerJobs Service(MaintenanceWorld w)
    {
        var regional = new Dz();
        var ledger = new LedgerPoster(w.Db);
        return new MaintenanceCustomerJobs(w.Db, w.Jobs, Costing(w),
            new ReceivableDocuments(w.Db, ledger, new RentalDocuments(w.Db, ledger, regional), regional), regional);
    }

    private static CreateMaintenanceJobRequest CustomerJob(Guid? product = null, decimal qty = 1m) =>
        new(Guid.NewGuid().ToString("N"), Algiers, "Customer", product, qty, null, "N° 4471", "Ne démarre plus",
            CustomerId: Contractor, Equipment: "Bétonnière 350 L du client");

    /// <summary>A customer job with <paramref name="bearings"/> bearings issued, <paramref name="outside"/> of outside work, and Karim's hours.</summary>
    private static async Task<Guid> JobAsync(MaintenanceWorld w, decimal bearings = 0m, decimal outside = 0m, decimal hours = 0m,
        CreateMaintenanceJobRequest? request = null, Guid? worker = null)
    {
        var job = await w.OpenAsync(request ?? CustomerJob());
        var parts = new MaintenanceJobParts(w.Db, new Dz());
        if (bearings > 0)
            Assert.True((await parts.MoveAsync(job.Id, new MaintenancePartsRequest(Guid.NewGuid().ToString("N"), "Issue",
                [new MaintenancePartLineRequest(Bearing, bearings)], null), Chef)).Ok);
        if (outside > 0)
            Assert.True((await parts.AddExpenseAsync(job.Id, new MaintenanceJobExpenseRequest(Guid.NewGuid().ToString("N"), "Rebobinage", outside, null),
                Chef, true)).Ok);
        w.Db.ChangeTracker.Clear();
        if (hours > 0)
        {
            w.Db.LaborBookings.Add(new LaborBooking
            {
                EmployeeId = worker ?? Karim, Day = new DateOnly(2026, 10, 20), OrderKind = LaborOrderKind.Maintenance, OrderId = job.Id, Hours = hours,
                PayrollBranchId = Algiers, PayrollYear = 2026, PayrollMonth = 10, AttendanceHours = 8m, UserName = "chef",
            });
            await w.Db.SaveChangesAsync();
            w.Db.ChangeTracker.Clear();
        }
        return job.Id;
    }

    private static async Task CompleteAsync(MaintenanceWorld w, Guid job, decimal qty = 1m)
    {
        var done = await w.Jobs.CompleteAsync(job, new CompleteMaintenanceJobRequest(qty, 0m, "Réparée"), Second);
        Assert.True(done.Ok, done.Error);
        w.Db.ChangeTracker.Clear();
    }

    private static async Task<MaintenanceJobPriceDto> PriceAsync(MaintenanceWorld w, Guid job, Func<string, bool>? has = null)
    {
        var read = await Service(w).PriceAsync(job, has ?? Everything);
        Assert.True(read.Ok, read.Error);
        return read.Value!;
    }

    private static async Task<MaintenanceResult<MaintenanceJobPriceDto>> SetPriceAsync(MaintenanceWorld w, Guid job, decimal price,
        string? tax = null, bool confirm = false)
    {
        var set = await Service(w).SetPriceAsync(job, new SetMaintenanceJobPriceRequest(price, tax, confirm), Chef, Everything);
        w.Db.ChangeTracker.Clear();
        return set;
    }

    private static async Task<MaintenanceResult<MaintenanceJobInvoiceDto>> InvoiceAsync(MaintenanceWorld w, Guid job, string? tax = null)
    {
        var raised = await Service(w).InvoiceAsync(job, new RaiseMaintenanceJobInvoiceRequest(DateTime.UtcNow.AddDays(30), TaxMode: tax), Chef, Everything);
        w.Db.ChangeTracker.Clear();
        return raised;
    }

    /// <summary>What one source document posted, by account code (summed in memory: SQLite keeps decimals as text).</summary>
    private static async Task<Dictionary<string, decimal>> PostedAsync(MaintenanceWorld w, string sourceType, Guid? sourceId = null)
    {
        var accounts = await w.Db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code);
        var entries = await w.Db.LedgerEntries.AsNoTracking().Include(e => e.Lines)
            .Where(e => e.SourceType == sourceType && (sourceId == null || e.SourceId == sourceId)).ToListAsync();
        return entries.SelectMany(e => e.Lines).GroupBy(l => accounts[l.AccountId]).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
    }

    private static async Task<JsonElement> GetAsync(MaintenanceWorld w, ClaimsPrincipal caller, string pattern, string query = "", Guid? id = null)
    {
        var (status, body) = id is Guid job
            ? await w.CallAsync(caller, "GET", pattern, query, ("id", job.ToString()))
            : await w.CallAsync(caller, "GET", pattern, query);
        w.Db.ChangeTracker.Clear();
        Assert.True(status == 200, $"GET {pattern}: {status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static decimal? Num(JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetDecimal();

    // ---------- the customer job ----------

    [Fact]
    public async Task A_customer_job_waits_for_MS_06_and_takes_nothing_from_the_company_s_stock()
    {
        await using (var off = await NewAsync(On(), inMaintenance: 5))
        {
            var refused = await off.Jobs.CreateAsync(CustomerJob(), Chef);
            Assert.Equal((400, MaintenanceJobs.NotYetCode), (refused.Status, refused.Code));
            Assert.Contains("MS-06", refused.Error);
        }

        await using var w = await WorldAsync(inMaintenance: 5);
        // Named in words, a customer, optionally a product; refused without either word or customer.
        Assert.Equal("equipment_required", (await w.Jobs.CreateAsync(CustomerJob() with { Equipment = "  " }, Chef)).Code);
        Assert.Equal("customer_required", (await w.Jobs.CreateAsync(CustomerJob() with { CustomerId = null }, Chef)).Code);
        var stockBefore = await w.StockAsync(Algiers);

        // Even naming the scaffold and more pieces than the branch holds in maintenance: the equipment is the customer's.
        var job = await w.OpenAsync(CustomerJob(Scaffold, qty: 9m));
        Assert.Equal(("Customer", "Open", "Ben Ali BTP", "Bétonnière 350 L du client"), (job.Kind, job.Status, job.CustomerName, job.Equipment));
        Assert.Equal((Contractor, 0m, "Untaxed"), (job.CustomerId!.Value, job.InMaintenanceAtBranch, job.TaxMode));
        // It holds none of the five units in maintenance: an internal job still takes all five.
        var waiting = await w.Jobs.WaitingAsync(Algiers);
        Assert.Equal(5m, waiting.Buckets.Single().Free);
        await w.OpenAsync(Internal(5));

        // Completing moves no stock and posts nothing.
        await CompleteAsync(w, job.Id, 9m);
        var stockAfter = await w.StockAsync(Algiers);
        Assert.Equal((stockBefore.StockQty, stockBefore.MaintenanceQty, stockBefore.ScrapQty), (stockAfter.StockQty, stockAfter.MaintenanceQty, stockAfter.ScrapQty));
        Assert.Empty(await w.Db.StockMovements.Where(m => m.DocumentNumber == job.Number).ToListAsync());
        Assert.Empty(await w.Db.LedgerEntries.ToListAsync());

        // A product is optional.
        Assert.Null((await w.OpenAsync(CustomerJob())).ProductId);
    }

    // ---------- the price bases (MS-20 to MS-22) ----------

    [Fact]
    public async Task Quoted_proposes_nothing_and_keeps_the_typed_price()
    {
        await using var w = await WorldAsync();
        var job = await JobAsync(w, bearings: 2m, outside: 1_000m);

        var before = await PriceAsync(w, job);
        Assert.Equal(("Quoted", (decimal?)null, (decimal?)null, "Untaxed"), (before.Basis, before.Proposed, before.Price, before.TaxMode));

        var set = await SetPriceAsync(w, job, 25_000m);
        Assert.True(set.Ok, set.Error);
        Assert.Equal((25_000m, "Quoted", "chef", false), (set.Value!.Price, set.Value.PricedBasis, set.Value.PricedBy, set.Value.BelowCost));
        Assert.Equal(25_000m, (await w.Db.MaintenanceJobs.SingleAsync(j => j.Id == job)).Price);
    }

    [Fact]
    public async Task Itemised_charges_parts_at_their_sale_price_hours_at_MS_21_and_outside_work_with_MS_22()
    {
        // Four bearings at 1,500 = 6,000; 3 h at 1,200 = 3,600; rewinding 4,000 + 10 % = 4,400: 14,000 before tax.
        await using var w = await WorldAsync(s => { s.CustomerPrice = MaintenancePriceBasis.Itemised; s.LabourChargePerHour = 1_200m; s.MarkupPercent = 10m; });
        var job = await JobAsync(w, bearings: 4m, outside: 4_000m, hours: 3m);

        var price = await PriceAsync(w, job);
        Assert.Equal(("Itemised", 4m, 3m, 4_000m), (price.Basis, price.PartsUsedQty, price.Hours, price.OutsideWork));
        Assert.Equal((6_000m, 3_600m, 400m, 14_000m), (price.Parts!.Value, price.Labour!.Value, price.Markup!.Value, price.Proposed!.Value));

        // A shelf price that includes tax is netted first (CLAUDE.md: a stored price is before tax unless the profile says otherwise).
        Assert.Equal(1_000m, MaintenancePricing.NetOfTax(1_190m, 19m, pricesIncludeTax: true));
        Assert.Equal(1_190m, MaintenancePricing.NetOfTax(1_190m, 19m, pricesIncludeTax: false));
        var pure = MaintenancePricing.Propose(MaintenancePriceBasis.Itemised,
            new MaintenancePriceInputs([new MaintenancePricedPart(Bearing, 4m, MaintenancePricing.NetOfTax(1_785m, 19m, true))], 3m, 1_200m, 4_000m, 10m, null),
            x => Math.Round(x, 2, MidpointRounding.AwayFromZero));
        Assert.Equal(14_000m, pure.Proposed);

        // Accepted as is: the proposal becomes the price.
        var set = await SetPriceAsync(w, job, price.Proposed!.Value);
        Assert.Equal((14_000m, 14_000m), (set.Value!.Price!.Value, set.Value.ProposedAtPricing!.Value));
    }

    [Fact]
    public async Task Cost_plus_reads_the_job_s_cost_from_M4_and_adds_MS_22_shown_only_to_who_may_see_the_cost()
    {
        // M4's cost: four bearings at 800 = 3,200 + outside work 4,000 = 7,200; + 25 % = 9,000.
        await using var w = await WorldAsync(s => { s.CustomerPrice = MaintenancePriceBasis.CostPlus; s.MarkupPercent = 25m; });
        var job = await JobAsync(w, bearings: 4m, outside: 4_000m);
        Assert.Equal(7_200m, (await Costing(w).FigureAsync(job, Everything))!.Total);

        var price = await PriceAsync(w, job);
        Assert.Equal(("CostPlus", true, 7_200m), (price.Basis, price.CostShown, price.Cost!.Value));
        Assert.Equal((7_200m, 1_800m, 9_000m), (price.CostBase!.Value, price.Markup!.Value, price.Proposed!.Value));

        // A pricing holder without maintenance.costs.view sees no cost, no cost base and no proposal (it gives the cost back).
        var blind = await PriceAsync(w, job, k => k is PermissionCatalog.MaintenanceView or PermissionCatalog.MaintenancePricing);
        Assert.Equal((false, (decimal?)null, (decimal?)null, (decimal?)null, true), (blind.CostShown, blind.Cost, blind.CostBase, blind.Proposed, blind.CostAvailable));

        // With no price set, the invoice takes the cost-plus proposal and freezes it.
        await CompleteAsync(w, job);
        var raised = await InvoiceAsync(w, job);
        Assert.True(raised.Ok, raised.Error);
        Assert.Equal((9_000m, 9_000m), (raised.Value!.SubTotalHt, raised.Value.Job.Price!.Value));
    }

    // ---------- MS-25: a price below the cost ----------

    [Fact]
    public async Task A_price_below_cost_is_shown_as_a_loss_or_needs_a_confirmation_never_refused()
    {
        await using (var w = await WorldAsync())
        {
            var job = await JobAsync(w, bearings: 4m, outside: 4_000m); // cost 7,200
            var set = await SetPriceAsync(w, job, 5_000m);
            Assert.True(set.Ok, set.Error);
            Assert.Equal((true, 2_200m, "ShownAsLoss"), (set.Value!.BelowCost, set.Value.Loss!.Value, set.Value.BelowCostRule));
            await CompleteAsync(w, job);
            var raised = await InvoiceAsync(w, job);
            Assert.True(raised.Ok, raised.Error);
            Assert.Contains("loss", raised.Value!.Warning);
        }

        await using (var w = await WorldAsync(s => s.PriceBelowCost = MaintenanceBelowCost.NeedsConfirmation))
        {
            var job = await JobAsync(w, bearings: 4m, outside: 4_000m);
            var asked = await SetPriceAsync(w, job, 5_000m);
            Assert.Equal((409, MaintenanceCustomerJobs.BelowCostCode), (asked.Status, asked.Code));
            Assert.Null((await w.Db.MaintenanceJobs.SingleAsync(j => j.Id == job)).Price);

            var confirmed = await SetPriceAsync(w, job, 5_000m, confirm: true);
            Assert.True(confirmed.Ok, confirmed.Error);
            Assert.Equal("chef", confirmed.Value!.BelowCostConfirmedBy);

            // The cost grew after the confirmation: the invoice waits for it to be confirmed again at the new cost.
            await new MaintenanceJobParts(w.Db, new Dz()).AddExpenseAsync(job,
                new MaintenanceJobExpenseRequest(Guid.NewGuid().ToString("N"), "Peinture", 1_000m, null), Chef, true);
            w.Db.ChangeTracker.Clear();
            await CompleteAsync(w, job);
            Assert.Equal(MaintenanceCustomerJobs.BelowCostCode, (await InvoiceAsync(w, job)).Code);
            Assert.True((await SetPriceAsync(w, job, 5_000m, confirm: true)).Ok);
            Assert.True((await InvoiceAsync(w, job)).Ok);
        }
    }

    // ---------- the invoice ----------

    [Fact]
    public async Task The_invoice_posts_411_707_and_no_VAT_by_default_through_its_own_poster_once_per_job()
    {
        await using var w = await WorldAsync();
        var job = await JobAsync(w, bearings: 2m);
        Assert.True((await SetPriceAsync(w, job, 20_000m)).Ok);

        Assert.Equal(MaintenanceCustomerJobs.NotCompletedCode, (await InvoiceAsync(w, job)).Code);
        await CompleteAsync(w, job);
        var raised = await InvoiceAsync(w, job);
        Assert.True(raised.Ok, raised.Error);
        var dto = raised.Value!;
        Assert.Equal(("Untaxed", 20_000m, 0m, 20_000m, "707"), (dto.TaxMode, dto.SubTotalHt, dto.TaxTotal, dto.GrandTotalTtc, dto.IncomeAccountCode));
        Assert.Equal(("Invoiced", dto.InvoiceId, dto.InvoiceNumber), (dto.Job.Status, dto.Job.InvoiceId!.Value, dto.Job.InvoiceNumber));

        var invoice = await w.Db.Invoices.Include(i => i.Lines).SingleAsync();
        Assert.Equal((InvoiceOrigin.MaintenanceJob, InvoiceSources.MaintenanceJob, job, TaxMode.Untaxed),
            (invoice.Origin, invoice.SourceType, invoice.SourceId!.Value, invoice.TaxMode));
        var line = invoice.Lines.Single();
        Assert.Equal((LedgerAccounts.MaintenanceIncome, 0m, (Guid?)null), (line.IncomeAccountCode, line.TaxRate, line.ProductId));
        // The account recharges use today, read from the return note's own rule.
        Assert.Equal(RentalPosting.AccountFor(RentalChargeKind.Maintenance), line.IncomeAccountCode);

        // 411 / 707, no 4457 line at all, under the invoice's own source; no cost of goods (the parts left with the job).
        var posted = await PostedAsync(w, LedgerSources.Invoice, invoice.Id);
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.Customers] = 20_000m, [LedgerAccounts.MaintenanceIncome] = -20_000m }, posted);
        Assert.Equal(20_000m, (await w.Db.Customers.SingleAsync(c => c.Id == Contractor)).Balance);
        // The invoice names the job it bills, as it names a sale or a note.
        var numbers = await new ReceivableDocuments(w.Db, new LedgerPoster(w.Db), w.Rentals, new Dz()).SourceNumbersAsync([invoice]);
        Assert.Equal(dto.Job.Number, numbers[invoice.Id]);

        // M6's practice ledger finds it (Invoices.SourceType = maintenance-job, SourceId = the job), ties it to its customer job
        // and lists the job no longer open.
        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);
        var ledger = await GetAsync(w, admin, "/api/maintenance/practice-ledger");
        var listed = ledger.GetProperty("invoices").EnumerateArray().Single();
        Assert.Equal((dto.InvoiceNumber, job, true, 20_000m),
            (listed.GetProperty("number").GetString(), listed.GetProperty("jobId").GetGuid(), listed.GetProperty("jobMatched").GetBoolean(), Num(listed, "netAmount")!.Value));
        Assert.True(ledger.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "invoices_tie").GetProperty("passed").GetBoolean());
        Assert.DoesNotContain(ledger.GetProperty("openCustomerJobs").EnumerateArray(), o => o.GetProperty("jobId").GetGuid() == job);

        // Once: the job refuses a second invoice, and so does the writer itself.
        var second = await InvoiceAsync(w, job);
        Assert.Equal((409, MaintenanceCustomerJobs.AlreadyInvoicedCode), (second.Status, second.Code));
        var again = await new ReceivableDocuments(w.Db, new LedgerPoster(w.Db), w.Rentals, new Dz()).CreateMaintenanceJobInvoiceAsync(
            new MaintenanceJobBill(job, Contractor, Algiers, DateTime.UtcNow.AddDays(30), null, TaxMode.Untaxed, 0m, null, null,
                [new MaintenanceJobBillLine("MJ", "again", 1m, LedgerAccounts.MaintenanceIncome)]), "x");
        Assert.Equal(ReceivableFailure.Conflict, again.Failure);
        Assert.Equal(1, await w.Db.Invoices.CountAsync());
        // The price no longer moves once invoiced.
        Assert.Equal("not_priceable", (await SetPriceAsync(w, job, 1m)).Code);
    }

    [Fact]
    public async Task VAT_is_added_on_top_only_when_the_seller_ticks_it()
    {
        await using var w = await WorldAsync();
        var job = await JobAsync(w);
        Assert.True((await SetPriceAsync(w, job, 20_000m)).Ok);
        await CompleteAsync(w, job);

        var raised = await InvoiceAsync(w, job, tax: "Taxed");
        Assert.True(raised.Ok, raised.Error);
        // The stored price is before tax: 19 % (the profile's default rate) on top.
        Assert.Equal(("Taxed", 20_000m, 3_800m, 23_800m), (raised.Value!.TaxMode, raised.Value.SubTotalHt, raised.Value.TaxTotal, raised.Value.GrandTotalTtc));
        var posted = await PostedAsync(w, LedgerSources.Invoice, raised.Value.InvoiceId);
        Assert.Equal((23_800m, -20_000m, -3_800m),
            (posted[LedgerAccounts.Customers], posted[LedgerAccounts.MaintenanceIncome], posted[LedgerAccounts.TaxCollected]));
    }

    [Fact]
    public async Task MS_35_an_advance_taken_on_419_for_a_job_clears_against_the_job_s_invoice()
    {
        await using var w = await WorldAsync(s => s.Advances = MaintenanceAdvances.Taken);
        w.Db.ReceivableSettings.Add(new ReceivableSettings { AdvancesHeldOn = CustomerAdvanceAccount.AdvancesAccount });
        await w.Db.SaveChangesAsync();
        var job = await JobAsync(w);
        Assert.True((await SetPriceAsync(w, job, 20_000m)).Ok);
        var ledger = new LedgerPoster(w.Db);
        var receivables = new ReceivableDocuments(w.Db, ledger, new RentalDocuments(w.Db, ledger, new Dz()), new Dz());

        // A payment cannot name a job; an advance names this customer's open job.
        Assert.Contains("Only an advance", (await receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, 15_000m, "cash", MaintenanceJobId: job), "caissier")).Message);
        var taken = await receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, 15_000m, "cash", Kind: "advance", MaintenanceJobId: job), "caissier");
        Assert.True(taken.Ok, taken.Message);
        Assert.Equal(job, taken.Value!.MaintenanceJobId);
        Assert.StartsWith("Acompte — réparation MJ-", taken.Value.Purpose);
        w.Db.ChangeTracker.Clear();

        await CompleteAsync(w, job);
        var raised = await InvoiceAsync(w, job);
        Assert.True(raised.Ok, raised.Error);

        // The invoice posts as ever (411 / 707); the advance moves from 419 to 411 the same day: Dr 419 / Cr 411.
        var application = await w.Db.CustomerAdvanceApplications.AsNoTracking().SingleAsync();
        Assert.Equal((raised.Value!.InvoiceId, 15_000m), (application.InvoiceId!.Value, application.Amount));
        Assert.Equal(new Dictionary<string, decimal> { [LedgerAccounts.CustomerDeposits] = 15_000m, [LedgerAccounts.Customers] = -15_000m },
            await PostedAsync(w, LedgerSources.CustomerAdvanceApplication));
        var invoice = await w.Db.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(application.AppliedAtUtc, invoice.IssuedAtUtc);
        Assert.Equal(5_000m, (await receivables.StandingsAsync([invoice.Id]))[invoice.Id].Outstanding);
        Assert.Equal(5_000m, (await w.Db.Customers.AsNoTracking().SingleAsync(c => c.Id == Contractor)).Balance);

        // An invoiced job takes no more advances.
        Assert.Contains("already invoiced", (await receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, 1_000m, "cash", Kind: "advance", MaintenanceJobId: job), "caissier")).Message);
    }

    [Fact]
    public async Task MS_35_not_taken_refuses_an_advance_for_a_job()
    {
        await using var w = await WorldAsync();
        w.Db.ReceivableSettings.Add(new ReceivableSettings { AdvancesHeldOn = CustomerAdvanceAccount.AdvancesAccount });
        await w.Db.SaveChangesAsync();
        var job = await JobAsync(w);
        var ledger = new LedgerPoster(w.Db);
        var receivables = new ReceivableDocuments(w.Db, ledger, new RentalDocuments(w.Db, ledger, new Dz()), new Dz());
        var refused = await receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, 15_000m, "cash", Kind: "advance", MaintenanceJobId: job), "caissier");
        Assert.Contains("MS-35", refused.Message);
        Assert.Equal(0, await w.Db.ReceiptVouchers.CountAsync());
    }

    [Fact]
    public async Task Only_a_customer_job_is_invoiced_and_MS_34_s_other_options_wait_for_their_income_account()
    {
        await using var w = await WorldAsync(inMaintenance: 3);
        var internalJob = await w.OpenAsync(Internal(1));
        Assert.Equal(MaintenanceCustomerJobs.NotCustomerJobCode, (await InvoiceAsync(w, internalJob.Id)).Code);
        Assert.Equal(MaintenanceCustomerJobs.NotCustomerJobCode, (await SetPriceAsync(w, internalJob.Id, 1_000m)).Code);

        var job = await JobAsync(w);
        await CompleteAsync(w, job);
        Assert.Equal(MaintenanceCustomerJobs.PriceRequiredCode, (await InvoiceAsync(w, job)).Code);
        Assert.True((await SetPriceAsync(w, job, 3_000m)).Ok);

        // A row holding another income option while the service income slot names no account: the invoice is refused,
        // never booked on a guessed account (MS-34, MaintenanceCustomerIncomeTests).
        var row = await w.Db.MaintenanceSettings.SingleAsync();
        row.CustomerIncome = MaintenanceIncomeAccount.OneServiceAccount;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        Assert.Equal(MaintenanceSettingsStore.AccountsNotMappedCode, (await InvoiceAsync(w, job)).Code);
        Assert.Equal(0, await w.Db.Invoices.CountAsync());
    }

    // ---------- routes ----------

    [Fact]
    public async Task The_routes_answer_409_with_the_module_off_404_for_another_branch_and_ask_maintenance_view_for_the_invoice()
    {
        await using (var off = await NewAsync())
        {
            var admin = await off.CallerAsync(UserRole.Admin, Algiers, Chef);
            Assert.Equal(409, (await off.CallAsync(admin, "GET", PriceRoute, "", ("id", Guid.NewGuid().ToString()))).Status);
            Assert.Equal(409, (await off.CallAsync(admin, "GET", ProfitRoute)).Status);
            // The return-note screen's suggestions never error: "not enabled".
            var (status, body) = await off.CallAsync(admin, "GET", "/api/maintenance/repair-rates/suggestions");
            Assert.Equal(200, status);
            Assert.False(JsonDocument.Parse(body).RootElement.GetProperty("enabled").GetBoolean());
        }

        await using var w = await WorldAsync();
        var job = await JobAsync(w);
        await CompleteAsync(w, job);
        Assert.True((await SetPriceAsync(w, job, 4_000m)).Ok);
        var oran = (await MfgHarness.ManagerAsync(w.Db, Oran, [PermissionCatalog.MaintenanceView, PermissionCatalog.MaintenancePricing])).Principal;
        Assert.Equal(404, (await w.CallAsync(oran, "GET", PriceRoute, "", ("id", job.ToString()))).Status);

        // A manager holds finance.invoices.create by role, but not maintenance.view: refused, nothing raised.
        var clerk = (await MfgHarness.ManagerAsync(w.Db, Algiers)).Principal;
        var body1 = JsonSerializer.Serialize(new RaiseMaintenanceJobInvoiceRequest(DateTime.UtcNow.AddDays(30)));
        var refused = await w.CallAsync(clerk, "POST", InvoiceRoute, body1, ("id", job.ToString()));
        Assert.Equal(403, refused.Status);
        Assert.Contains("maintenance_view_required", refused.Body);
        Assert.Equal(0, await w.Db.Invoices.CountAsync());

        var reader = (await MfgHarness.ManagerAsync(w.Db, Algiers, [PermissionCatalog.MaintenanceView])).Principal;
        var raised = await w.CallAsync(reader, "POST", InvoiceRoute, body1, ("id", job.ToString()));
        Assert.True(raised.Status == 200, raised.Body);
        var price = await GetAsync(w, reader, PriceRoute, id: job);
        Assert.Equal("Invoiced", price.GetProperty("status").GetString());
    }

    // ---------- MS-23: repair rates, a suggestion only ----------

    [Fact]
    public async Task Repair_rates_are_a_suggestion_only_and_the_return_note_posts_exactly_as_before()
    {
        await using var plain = await NewAsync();
        await using var suggested = await NewAsync(On(s => s.ReturnRecharge = MaintenanceRechargeEntry.SuggestedFromRate));
        var rates = new MaintenanceRepairRates(suggested.Db, new Dz());
        Assert.Equal((true, 0), ((await rates.SuggestionsAsync()).Enabled, (await rates.SuggestionsAsync()).Rates.Count));
        Assert.True((await rates.SetAsync(Scaffold, new SetMaintenanceRepairRateRequest(2_000m), "tarif")).Ok);
        Assert.Equal("invalid_rate", (await rates.SetAsync(Scaffold, new SetMaintenanceRepairRateRequest(0m), "tarif")).Code);
        var suggestions = await rates.SuggestionsAsync();
        Assert.True(suggestions.Enabled);
        Assert.Equal((Scaffold, 2_000m), (suggestions.Rates.Single().ProductId, suggestions.Rates.Single().RatePerUnit));
        Assert.False((await new MaintenanceRepairRates(plain.Db, new Dz()).SuggestionsAsync()).Enabled);

        // The same return in both companies, the clerk typing 15,000 (not the 8 × 2,000 suggested): the same note, charges and entries.
        var a = await plain.ReturnDamagedAsync(8m, 15_000m);
        var b = await suggested.ReturnDamagedAsync(8m, 15_000m);
        Assert.Equal(Charges(a.Outcome), Charges(b.Outcome));
        Assert.Equal(await AllPostedAsync(plain), await AllPostedAsync(suggested));
        // And with nothing typed, nothing is charged: the rate never reaches the engine.
        await using var blank = await NewAsync(On(s => s.ReturnRecharge = MaintenanceRechargeEntry.SuggestedFromRate));
        Assert.True((await new MaintenanceRepairRates(blank.Db, new Dz()).SetAsync(Scaffold, new SetMaintenanceRepairRateRequest(2_000m), "tarif")).Ok);
        var c = await blank.ReturnDamagedAsync(2m, 0m);
        Assert.DoesNotContain(c.Outcome.Charges, x => x.Kind == RentalChargeKind.Maintenance && x.Amount != 0m);

        Assert.True((await rates.RemoveAsync(Scaffold)).Ok);
        Assert.Empty((await rates.SuggestionsAsync()).Rates);

        static string Charges(ReturnOutcome o) => string.Join(";", o.Charges.OrderBy(x => x.Kind).Select(x => $"{x.Kind}:{x.Amount:0.##}"));
        static async Task<string> AllPostedAsync(MaintenanceWorld w)
        {
            var accounts = await w.Db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code);
            var lines = await w.Db.LedgerEntryLines.AsNoTracking().ToListAsync();
            return string.Join(";", lines.GroupBy(l => accounts[l.AccountId]).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key}:{g.Sum(l => l.Debit):0.##}/{g.Sum(l => l.Credit):0.##}"));
        }
    }

    // ---------- M5: company profit ----------

    [Fact]
    public async Task Company_profit_is_external_revenue_less_cost_REN_11_gives_5_000()
    {
        await using var w = await WorldAsync(inMaintenance: 1);
        // REN-11: eight frames back damaged, 16,000 charged at return; the repair cost 11,000 (a welder's outside work).
        var (_, _, lineId, _) = await w.ReturnDamagedAsync(8m, 16_000m);
        var recharged = await JobAsync(w, outside: 11_000m, request: Recharged(lineId, 8m));
        // A customer job invoiced 20,000 before tax (ticked: the VAT is not revenue) against a cost of 7,200.
        var customer = await JobAsync(w, bearings: 4m, outside: 4_000m);
        Assert.True((await SetPriceAsync(w, customer, 20_000m)).Ok);
        await CompleteAsync(w, customer);
        Assert.True((await InvoiceAsync(w, customer, tax: "Taxed")).Ok);
        // An internal job: upkeep of own equipment, cost only.
        var upkeep = await JobAsync(w, outside: 1_000m, request: Internal(1));
        // A customer job not yet invoiced: no revenue yet.
        var pending = await JobAsync(w, outside: 500m);

        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);
        var report = await GetAsync(w, admin, ProfitRoute);
        var rows = report.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("jobId").GetGuid());
        Assert.Equal(("Recharge", 16_000m, 11_000m, 5_000m), Row(rows[recharged]));
        Assert.Equal(("Invoice", 20_000m, 7_200m, 12_800m), Row(rows[customer]));
        Assert.Equal(("None", 0m, 1_000m, -1_000m), Row(rows[upkeep]));
        Assert.Equal(("NotInvoiced", 0m, 500m, -500m), Row(rows[pending]));
        Assert.Equal((36_000m, 19_700m, 16_300m), (Num(report, "revenue")!.Value, Num(report, "cost")!.Value, Num(report, "profit")!.Value));
        var byKind = report.GetProperty("byKind").EnumerateArray().ToDictionary(g => g.GetProperty("key").GetString()!);
        Assert.Equal((2, 20_000m, 7_700m, 12_300m), Group(byKind["Customer"]));
        Assert.Equal((1, 16_000m, 11_000m, 5_000m), Group(byKind["Recharged"]));
        Assert.Equal((1, 0m, 1_000m, -1_000m), Group(byKind["Internal"]));
        Assert.Single(report.GetProperty("byBranch").EnumerateArray());
        Assert.Equal(4, report.GetProperty("byMonth").EnumerateArray().Sum(g => g.GetProperty("jobs").GetInt32()));
        var byProduct = report.GetProperty("byProduct").EnumerateArray().ToDictionary(g => g.GetProperty("key").GetString()!);
        Assert.Equal((2, 16_000m), (byProduct[Scaffold.ToString()].GetProperty("jobs").GetInt32(), Num(byProduct[Scaffold.ToString()], "revenue")!.Value));
        Assert.Equal(2, byProduct[""].GetProperty("jobs").GetInt32()); // the customer jobs, naming no product

        // A credit note gives revenue back.
        var invoiceId = (await w.Db.MaintenanceJobs.SingleAsync(j => j.Id == customer)).InvoiceId!.Value;
        Assert.True((await new ReceivableDocuments(w.Db, new LedgerPoster(w.Db), w.Rentals, new Dz())
            .CreateCreditNoteAsync(invoiceId, new CreateCreditNoteRequest("Remise", Lines: null), "admin")).Ok);
        w.Db.ChangeTracker.Clear();
        var credited = (await GetAsync(w, admin, ProfitRoute)).GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("jobId").GetGuid() == customer);
        Assert.Equal((0m, -7_200m), (Num(credited, "revenue")!.Value, Num(credited, "profit")!.Value));

        static (string, decimal, decimal, decimal) Row(JsonElement r) =>
            (r.GetProperty("revenueSource").GetString()!, Num(r, "revenue")!.Value, Num(r, "cost")!.Value, Num(r, "profit")!.Value);
        static (int, decimal, decimal, decimal) Group(JsonElement g) =>
            (g.GetProperty("jobs").GetInt32(), Num(g, "revenue")!.Value, Num(g, "cost")!.Value, Num(g, "profit")!.Value);
    }

    [Fact]
    public async Task Company_profit_follows_M4_s_privacy_rule()
    {
        await using var w = await WorldAsync();
        var a = await JobAsync(w, outside: 1_000m, hours: 2m);
        var b = await JobAsync(w, outside: 2_000m);
        var reader = (await MfgHarness.ManagerAsync(w.Db, Algiers, [PermissionCatalog.MaintenanceView, PermissionCatalog.MaintenanceCostsView])).Principal;

        // Karim's hours on A: without pay-rate access A's cost and profit go, and every sum holding them (one job, one worker).
        var hidden = await GetAsync(w, reader, ProfitRoute);
        var rows = hidden.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("jobId").GetGuid());
        Assert.True(rows[a].GetProperty("withheld").GetBoolean());
        Assert.Equal(((decimal?)null, (decimal?)null, (decimal?)null), (Num(rows[a], "labour"), Num(rows[a], "cost"), Num(rows[a], "profit")));
        Assert.Equal((2_000m, -2_000m), (Num(rows[b], "cost")!.Value, Num(rows[b], "profit")!.Value));
        Assert.True(hidden.GetProperty("totalsWithheld").GetBoolean());
        Assert.Null(Num(hidden, "cost"));
        Assert.All(hidden.GetProperty("byKind").EnumerateArray(), g => Assert.True(g.GetProperty("withheld").GetBoolean()));

        // A second job with labour by a second worker: the sums are shown again (two jobs, two workers), rows A and C still not.
        var c = await JobAsync(w, outside: 500m, hours: 1m, worker: Samir);
        var two = await GetAsync(w, reader, ProfitRoute);
        Assert.False(two.GetProperty("totalsWithheld").GetBoolean());
        Assert.NotNull(Num(two, "cost"));
        Assert.True(two.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("jobId").GetGuid() == c).GetProperty("withheld").GetBoolean());

        // MS-12 one workshop rate: nothing gives anyone's pay back, everything is shown.
        var row = await w.Db.MaintenanceSettings.SingleAsync();
        (row.LabourRate, row.WorkshopRatePerHour) = (MaintenanceLabourRate.WorkshopRate, 500m);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var open = await GetAsync(w, reader, ProfitRoute);
        var aRow = open.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("jobId").GetGuid() == a);
        Assert.Equal((false, 2_000m, -2_000m), (aRow.GetProperty("withheld").GetBoolean(), Num(aRow, "cost")!.Value, Num(aRow, "profit")!.Value));
    }
}
