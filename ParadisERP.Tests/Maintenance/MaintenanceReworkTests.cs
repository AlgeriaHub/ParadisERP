using System.Security.Claims;
using System.Text.Json;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Maintenance;
using ParadisERP.Application.Manufacturing;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Maintenance;
using ParadisERP.Infrastructure.Manufacturing;
using ParadisERP.Infrastructure.Payroll;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Receivables;
using ParadisERP.Infrastructure.Rentals;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Maintenance.MaintenanceWorld;
using static ParadisERP.Tests.Rentals.RentalTestPaper;
using P = ParadisERP.Application.Maintenance.MaintenanceGuaranteeProvision;
using S = ParadisERP.Domain.MaintenanceAccountSlots;

namespace ParadisERP.Tests.Maintenance;

/// <summary>
/// Phase M7c (docs/maintenance/plan.md, MS-27 and MS-37, MNT-09): a free rework under guarantee linked to the completed
/// customer job it redoes — same customer, same branch, within the guarantee — never priced nor invoiced; the company profit
/// report under each MS-37 treatment; and the guarantee provision — booked at the invoice, used by a rework's costs, released
/// when the guarantee ends — in the practice ledger and posted by month, every entry balanced, never on an unmapped account.
/// </summary>
public class MaintenanceReworkTests
{
    private const string Ledger = "/api/maintenance/practice-ledger";
    private static readonly Guid Bearing = Guid.Parse("a7a7a7a7-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("a7a7a7a7-0000-0000-0000-000000000002");
    private static readonly Func<string, bool> Everything = _ => true;

    private sealed class Dz : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => RegionalPresets.Create("DZ")!;
    }

    // ---------- a company ----------

    /// <summary>Customer jobs on, a bearing costed 800 (a spare part, 40 on the shelf) and a second customer.</summary>
    private static async Task<MaintenanceWorld> WorldAsync(Action<MaintenanceSettings>? more = null)
    {
        var w = await NewAsync(On(s => { s.CustomerJobs = MaintenanceSwitch.On; more?.Invoke(s); }));
        w.Db.Products.Add(new Product { Id = Bearing, Sku = "ROU-01", NameFr = "Roulement", NameAr = "محمل", NameEn = "Bearing",
            Price = 1_500m, TaxRate = 19m, CostPrice = 800m, TrackStock = true, ManufacturingRole = ManufacturingRole.SparePart });
        w.Db.ProductStocks.Add(new ProductStock { ProductId = Bearing, BranchId = Algiers, StockQty = 40m });
        w.Db.Customers.Add(Identified(new Customer { Id = Other, Code = "C002", FullName = "Sonatrach Sud", Kind = CustomerKind.Person }));
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

    private static CreateMaintenanceJobRequest CustomerJob(Guid? customer = null, Guid? reworkOf = null, Guid? branch = null) =>
        new(Guid.NewGuid().ToString("N"), branch ?? Algiers, "Customer", null, 1m, null, null, "Ne démarre plus",
            CustomerId: customer ?? Contractor, Equipment: reworkOf is null ? "Bétonnière 350 L du client" : null, ReworkOfJobId: reworkOf);

    private static async Task CompleteAsync(MaintenanceWorld w, Guid job)
    {
        var done = await w.Jobs.CompleteAsync(job, new CompleteMaintenanceJobRequest(1m, 0m, "Réparée"), Second);
        Assert.True(done.Ok, done.Error);
        w.Db.ChangeTracker.Clear();
    }

    private static async Task IssueAsync(MaintenanceWorld w, Guid job, decimal bearings)
    {
        var moved = await new MaintenanceJobParts(w.Db, new Dz()).MoveAsync(job, new MaintenancePartsRequest(Guid.NewGuid().ToString("N"), "Issue",
            [new MaintenancePartLineRequest(Bearing, bearings)], null), Chef);
        Assert.True(moved.Ok, moved.Error);
        w.Db.ChangeTracker.Clear();
    }

    /// <summary>A customer job with <paramref name="bearings"/> bearings, completed, priced 20,000 before tax and invoiced untaxed.</summary>
    private static async Task<(Guid Job, MaintenanceJobInvoiceDto Invoice)> InvoicedJobAsync(MaintenanceWorld w, decimal bearings = 0m)
    {
        var job = await w.OpenAsync(CustomerJob());
        if (bearings > 0) await IssueAsync(w, job.Id, bearings);
        await CompleteAsync(w, job.Id);
        Assert.True((await Service(w).SetPriceAsync(job.Id, new SetMaintenanceJobPriceRequest(20_000m, null, false), Chef, Everything)).Ok);
        w.Db.ChangeTracker.Clear();
        var raised = await Service(w).InvoiceAsync(job.Id, new RaiseMaintenanceJobInvoiceRequest(DateTime.UtcNow.AddDays(30)), Chef, Everything);
        Assert.True(raised.Ok, raised.Error);
        w.Db.ChangeTracker.Clear();
        return (job.Id, raised.Value!);
    }

    private static DateOnly CompanyToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, StoreDocumentHistory.Zone(new Dz())));

    // ---------- the link ----------

    [Fact]
    public async Task A_rework_links_to_a_completed_customer_job_of_the_same_customer_within_its_guarantee_and_is_never_priced_nor_invoiced()
    {
        await using var w = await WorldAsync();
        var original = await w.OpenAsync(CustomerJob());
        // Not yet handed back: no rework against it.
        Assert.Equal(MaintenanceJobs.ReworkNotCompletedCode, (await w.Jobs.CreateAsync(CustomerJob(reworkOf: original.Id), Chef)).Code);
        await CompleteAsync(w, original.Id);

        // MS-27, three months by default, frozen at completion on the company's calendar.
        var completed = (await w.Jobs.GetAsync(original.Id)).Value!;
        Assert.Equal(CompanyToday().AddMonths(3), completed.GuaranteeUntil);
        var candidates = await w.Jobs.ReworkCandidatesAsync(Algiers, Contractor);
        Assert.Equal(original.Id, candidates.Single().Id);
        Assert.Empty(await w.Jobs.ReworkCandidatesAsync(Algiers, Other));

        // The rework: the same equipment unless said otherwise, the link both ways.
        var rework = await w.OpenAsync(CustomerJob(reworkOf: original.Id));
        Assert.Equal((original.Id, original.Number, "Bétonnière 350 L du client", (DateOnly?)null),
            (rework.ReworkOfJobId!.Value, rework.ReworkOfNumber, rework.Equipment, rework.GuaranteeUntil));
        Assert.Equal(rework.Id, (await w.Jobs.GetAsync(original.Id)).Value!.Reworks!.Single().Id);
        Assert.Equal(original.Id, (await w.Jobs.ListAsync(Algiers, null, null, null)).Single(j => j.Id == rework.Id).ReworkOfJobId);

        // Free: never priced, never invoiced.
        Assert.Equal(MaintenanceCustomerJobs.ReworkFreeCode,
            (await Service(w).SetPriceAsync(rework.Id, new SetMaintenanceJobPriceRequest(5_000m, null, false), Chef, Everything)).Code);
        await CompleteAsync(w, rework.Id);
        Assert.Equal(MaintenanceCustomerJobs.ReworkFreeCode,
            (await Service(w).InvoiceAsync(rework.Id, new RaiseMaintenanceJobInvoiceRequest(DateTime.UtcNow.AddDays(30)), Chef, Everything)).Code);
        Assert.Null((await w.Jobs.GetAsync(rework.Id)).Value!.GuaranteeUntil);

        // Refused: a rework of a rework, another customer's job, an internal job, another branch's job.
        Assert.Equal(MaintenanceJobs.ReworkOfReworkCode, (await w.Jobs.CreateAsync(CustomerJob(reworkOf: rework.Id), Chef)).Code);
        Assert.Equal(MaintenanceJobs.ReworkOtherCustomerCode, (await w.Jobs.CreateAsync(CustomerJob(customer: Other, reworkOf: original.Id), Chef)).Code);
        await w.Db.ProductStocks.Where(s => s.ProductId == Scaffold && s.BranchId == Algiers)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.MaintenanceQty, 1m));
        var internalJob = await w.OpenAsync(Internal(1));
        await w.Jobs.CompleteAsync(internalJob.Id, new CompleteMaintenanceJobRequest(1m, 0m, null), Second);
        w.Db.ChangeTracker.Clear();
        Assert.Equal(MaintenanceJobs.ReworkNotCustomerJobCode, (await w.Jobs.CreateAsync(CustomerJob(reworkOf: internalJob.Id), Chef)).Code);
        var elsewhere = await w.Jobs.CreateAsync(CustomerJob(reworkOf: original.Id, branch: Oran), Chef);
        Assert.Equal((404, "not_found"), (elsewhere.Status, elsewhere.Code));

        // Outside the guarantee: its last day was yesterday.
        await w.Db.MaintenanceJobs.Where(j => j.Id == original.Id).ExecuteUpdateAsync(u => u.SetProperty(j => j.GuaranteeUntil, CompanyToday().AddDays(-1)));
        var late = await w.Jobs.CreateAsync(CustomerJob(reworkOf: original.Id), Chef);
        Assert.Equal((409, MaintenanceJobs.OutsideGuaranteeCode), (late.Status, late.Code));
        Assert.Empty(await w.Jobs.ReworkCandidatesAsync(Algiers, Contractor));
    }

    [Fact]
    public async Task With_no_guarantee_a_completed_job_carries_none_and_takes_no_rework()
    {
        await using var w = await WorldAsync(s => s.GuaranteeMonths = 0);
        var original = await w.OpenAsync(CustomerJob());
        await CompleteAsync(w, original.Id);
        Assert.Null((await w.Jobs.GetAsync(original.Id)).Value!.GuaranteeUntil);
        Assert.Equal(MaintenanceJobs.OutsideGuaranteeCode, (await w.Jobs.CreateAsync(CustomerJob(reworkOf: original.Id), Chef)).Code);
    }

    // ---------- MS-37 in the company profit report ----------

    [Theory]
    [InlineData(MaintenanceRework.AddedToOriginalJob)]
    [InlineData(MaintenanceRework.OwnJob)]
    [InlineData(MaintenanceRework.Provision)]
    public async Task The_company_profit_report_adds_a_rework_to_its_original_job_or_shows_it_on_its_own_line(MaintenanceRework treatment)
    {
        await using var w = await WorldAsync(s => s.Rework = treatment);
        var (original, invoice) = await InvoicedJobAsync(w, bearings: 1m);
        var rework = await w.OpenAsync(CustomerJob(reworkOf: original));
        await IssueAsync(w, rework.Id, 2m);

        var report = (await Costing(w).ProfitAsync(Algiers, null, null, Everything)).Value!;
        Assert.Equal(treatment.ToString(), report.Rework);
        // The whole is the same whichever way it is shown: 20,000 earned, 800 + 1,600 spent.
        Assert.Equal((20_000m, 2_400m, 17_600m), (report.Revenue, report.Cost, report.Profit));
        Assert.Equal(20_000m, invoice.SubTotalHt);
        var first = report.Rows.Single(r => r.JobId == original);
        if (treatment == MaintenanceRework.AddedToOriginalJob)
        {
            // MNT-09 A: the job's real result — its rework's cost is its own.
            Assert.Equal((1, 2_400m, 1_600m, 17_600m, 2_400m), (first.Reworks, first.Cost, first.ReworkCost, first.Profit, first.Parts));
            Assert.DoesNotContain(report.Rows, r => r.JobId == rework.Id);
        }
        else
        {
            // Its own line, earning nothing, naming the job it redoes.
            Assert.Equal((0, 800m, 19_200m), (first.Reworks, first.Cost, first.Profit));
            var own = report.Rows.Single(r => r.JobId == rework.Id);
            Assert.Equal((MaintenanceCosting.RevenueRework, 0m, 1_600m, -1_600m, first.Number), (own.RevenueSource, own.Revenue, own.Cost, own.Profit, own.ReworkOfNumber));
        }
    }

    // ---------- the provision ----------

    /// <summary>Four active accounts of the chart for customer parts, supplies' stock and the provision's two slots.</summary>
    private static async Task MapProvisionAsync(MaintenanceWorld w)
    {
        var free = await w.Db.LedgerAccounts.AsNoTracking()
            .Where(a => a.IsActive && a.Code != LedgerAccounts.MaintenanceExpense && a.Code != LedgerAccounts.Inventory)
            .OrderBy(a => a.Code).Take(4).ToListAsync();
        Assert.Null((await new MaintenanceAccountMap(w.Db).UpdateAsync(new([
            new(S.CustomerParts, free[0].Id), new(S.ProvisionExpense, free[1].Id), new(S.ProvisionLiability, free[2].Id)]), "comptable")).Error);
        Assert.Null((await new ManufacturingCostingChoices(w.Db).UpdateAccountMapAsync(
            new([new(ManufacturingAccountSlots.SuppliesStock, free[3].Id)]), "comptable")).Error);
        w.Db.ChangeTracker.Clear();
    }

    private static UpdateMaintenanceSettingsRequest Ask(MaintenanceSettingsDto d) => new(
        d.Module, d.UnitsLeaveMaintenance, d.JobsFromReturnNotes, d.BookableEmployees, d.JobCloser, d.CustomerJobs,
        d.CostIncludes, d.OverheadPerLabourHour, d.LabourRate, d.WorkshopRatePerHour, d.BurdenAndVat, d.EmployerBurdenPercent,
        d.ExpenseVatPercent, d.CustomerPrice, d.LabourChargePerHour, d.MarkupPercent, d.ReturnRecharge, d.PriceBelowCost,
        d.RechargeSharing, d.OwnRepairs, d.PartsPosting, d.LabourInBooks, d.CustomerIncome, d.Advances, d.OpenJobsAtPeriodEnd,
        d.Rework, d.RepairHistory, d.PostingFromMonth, d.AccountantApproved, d.GuaranteeMonths, d.ProvisionPercent);

    [Fact]
    public async Task The_provision_waits_for_its_two_accounts_a_rate_and_a_guarantee_and_by_default_nothing_is_set_aside()
    {
        await using var w = await WorldAsync();
        // The default (added to the original job): an invoice sets nothing aside and the practice ledger carries no provision.
        var (job, _) = await InvoicedJobAsync(w);
        var saved = await w.Db.MaintenanceJobs.AsNoTracking().SingleAsync(j => j.Id == job);
        Assert.Equal((null, null), (saved.ProvisionPercent, saved.ProvisionAmount));
        Assert.Equal(CompanyToday().AddMonths(3), saved.GuaranteeUntil);
        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);
        var (status, body) = await w.CallAsync(admin, "GET", Ledger, "");
        Assert.Equal(200, status);
        var month = JsonDocument.Parse(body).RootElement;
        Assert.DoesNotContain(month.GetProperty("entries").EnumerateArray(), e => e.GetProperty("kind").GetString()!.StartsWith("provision"));
        Assert.Empty(month.GetProperty("provisions").EnumerateArray());

        var store = new MaintenanceSettingsStore(w.Db);
        var provision = Ask(await store.SettingsAsync()) with { Rework = "Provision", ProvisionPercent = 5m };
        // Never guessed: refused while the provision expense or liability names no account.
        var refused = await store.UpdateAsync(provision, Everything, "comptable");
        Assert.Equal((409, MaintenanceSettingsStore.AccountsNotMappedCode), (refused.Status, refused.Code));
        await MapProvisionAsync(w);
        // A rate above 0 and a guarantee to run over.
        Assert.Equal(400, (await store.UpdateAsync(provision with { ProvisionPercent = 0m }, Everything, "comptable")).Status);
        Assert.Equal(400, (await store.UpdateAsync(provision with { GuaranteeMonths = 0 }, Everything, "comptable")).Status);
        // The rate is the accountant's (MS-37); the guarantee the workshop's (MS-27).
        Assert.Equal(MaintenanceSettingsStore.AccountingForbiddenCode,
            (await store.UpdateAsync(provision, k => k == PermissionCatalog.MaintenanceSettings, "chef")).Code);
        var ok = await store.UpdateAsync(provision, Everything, "comptable");
        Assert.Null(ok.Error);
        Assert.Equal(("Provision", 5m), (ok.Saved!.Rework, ok.Saved.ProvisionPercent));
        Assert.Equal(MaintenanceSettingsStore.OperationalForbiddenCode,
            (await store.UpdateAsync(Ask(ok.Saved) with { GuaranteeMonths = 6 }, k => k == PermissionCatalog.AccountingManage, "comptable")).Code);
        // While it is on, its accounts stay named.
        Assert.NotNull((await new MaintenanceAccountMap(w.Db).UpdateAsync(new([new(S.ProvisionLiability, null)]), "comptable")).Error);
        // Another treatment keeps no rate.
        Assert.Equal(0m, (await store.UpdateAsync(Ask(ok.Saved) with { Rework = "OwnJob" }, Everything, "comptable")).Saved!.ProvisionPercent);
    }

    [Fact]
    public async Task The_provision_is_booked_at_the_invoice_used_by_a_rework_s_costs_released_at_the_guarantee_s_end_and_posted_balanced()
    {
        await using var w = await WorldAsync(s =>
        {
            s.Rework = MaintenanceRework.Provision;
            s.ProvisionPercent = 10m;
            s.PostingFromMonth = new DateOnly(2026, 8, 1);
        });
        await MapProvisionAsync(w);
        var (original, invoice) = await InvoicedJobAsync(w);
        // 10 % of the invoice's 20,000 before tax, frozen on the job.
        var frozen = await w.Db.MaintenanceJobs.AsNoTracking().SingleAsync(j => j.Id == original);
        Assert.Equal((10m, 2_000m), (frozen.ProvisionPercent, frozen.ProvisionAmount));

        // The customer comes back: a bearing (800) and a welder's 500, paid by voucher on the repair account.
        var rework = await w.OpenAsync(CustomerJob(reworkOf: original));
        await IssueAsync(w, rework.Id, 1m);
        var voucher = new PaymentVoucher { Number = "PV-000701", BranchId = Algiers, Kind = PaymentVoucherKind.Expense, Amount = 500m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Paid, ExpenseCategory = "repair", ExpenseAccountCode = LedgerAccounts.MaintenanceExpense };
        w.Db.PaymentVouchers.Add(voucher);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        Assert.True((await new MaintenanceJobParts(w.Db, new Dz()).AddExpenseAsync(rework.Id,
            new MaintenanceJobExpenseRequest(Guid.NewGuid().ToString("N"), "Soudeur", 500m, voucher.Id), Chef, true)).Ok);
        w.Db.ChangeTracker.Clear();

        // The calendar: invoiced 10 August, the rework's costs in September, the guarantee ending 15 October; a last bearing
        // issued on 20 October, after the guarantee, is not taken from the provision.
        var issued = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        var september = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        await w.Db.Invoices.Where(i => i.Id == invoice.InvoiceId).ExecuteUpdateAsync(u => u.SetProperty(i => i.IssuedAtUtc, issued));
        await w.Db.MaintenanceJobs.Where(j => j.Id == original).ExecuteUpdateAsync(u => u.SetProperty(j => j.GuaranteeUntil, new DateOnly(2026, 10, 15)));
        await w.Db.MaintenanceJobParts.Where(p => p.JobId == rework.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.AtUtc, september));
        await w.Db.MaintenanceJobExpenses.Where(e => e.JobId == rework.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.AtUtc, september));
        await w.Db.PaymentVouchers.Where(v => v.Id == voucher.Id).ExecuteUpdateAsync(u => u.SetProperty(v => v.PaidAtUtc, september.AddDays(2)));
        await IssueAsync(w, rework.Id, 1m);
        var lastIssue = await w.Db.MaintenanceJobParts.AsNoTracking().Where(p => p.JobId == rework.Id).OrderByDescending(p => p.AtUtc).FirstAsync();
        await w.Db.MaintenanceJobParts.Where(p => p.Id == lastIssue.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.AtUtc, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc)));
        var admin = await w.CallerAsync(UserRole.Admin, Algiers, Chef);

        // August: booked at the invoice, Dr provision expense / Cr provision liability 2,000.
        var august = await MonthAsync(w, admin, 8);
        var booked = august.Entries.Single(e => e.Kind == P.KindBooked);
        Assert.Equal((new DateOnly(2026, 8, 10), 2_000m, 2_000m), (booked.Day, Debit(booked, S.ProvisionExpense), Credit(booked, S.ProvisionLiability)));
        Assert.Equal((2_000m, 0m, 0m, 2_000m), (august.Provision.Booked, august.Provision.Used, august.Provision.Released, august.Provision.HeldAtEnd));

        // September: the rework's parts and outside work taken from it, back to the accounts they debited.
        var sept = await MonthAsync(w, admin, 9);
        var used = sept.Entries.Single(e => e.Kind == P.KindUsed);
        Assert.Equal(rework.Number, used.OrderNumber);
        Assert.Equal((1_300m, 800m, 500m), (Debit(used, S.ProvisionLiability), Credit(used, S.CustomerParts), Credit(used, S.Account(LedgerAccounts.MaintenanceExpense))));
        Assert.Equal(LedgerAccounts.MaintenanceExpense, used.Lines.Single(l => l.Slot == S.Account(LedgerAccounts.MaintenanceExpense)).Code);
        Assert.Equal((0m, 1_300m, 700m), (sept.Provision.Booked, sept.Provision.Used, sept.Provision.HeldAtEnd));

        // October: what is left released on the guarantee's last day; the later bearing stays in expense.
        var oct = await MonthAsync(w, admin, 10);
        var released = oct.Entries.Single(e => e.Kind == P.KindReleased);
        Assert.Equal((new DateOnly(2026, 10, 15), 700m, 700m), (released.Day, Debit(released, S.ProvisionLiability), Credit(released, S.ProvisionExpense)));
        Assert.DoesNotContain(oct.Entries, e => e.Kind == P.KindUsed);
        Assert.Contains(oct.Entries, e => e.Kind == MaintenancePracticeLedger.KindIssue);
        Assert.Equal((0m, 700m, 0m), (oct.Provision.Used, oct.Provision.Released, oct.Provision.HeldAtEnd));
        // November: the provision is spent and released, so nothing of it moves.
        Assert.DoesNotContain((await MonthAsync(w, admin, 11, provision: false)).Entries, e => e.Kind.StartsWith("provision"));

        // Posted month by month through the shared poster: balanced, and the liability back to nothing.
        foreach (var m in new[] { 8, 9, 10 })
        {
            var (status, body) = await w.CallAsync(admin, "POST", Ledger + "/post", JsonSerializer.Serialize(new { year = 2026, month = m }));
            w.Db.ChangeTracker.Clear();
            Assert.True(status == 200, $"post 2026-{m}: {status} {body}");
        }
        var posted = await w.Db.LedgerEntries.Include(e => e.Lines).AsNoTracking()
            .Where(e => e.SourceType == MaintenanceLedgerPostings.SourceType).ToListAsync();
        Assert.All(posted, e => Assert.Equal(e.Lines.Sum(l => l.Debit), e.Lines.Sum(l => l.Credit)));
        var map = await MaintenanceAccountMap.AccountsAsync(w.Db);
        var lines = posted.SelectMany(e => e.Lines).ToList();
        decimal Net(Guid account) => lines.Where(l => l.AccountId == account).Sum(l => l.Debit - l.Credit);
        var repairs = await w.Db.LedgerAccounts.AsNoTracking().SingleAsync(a => a.Code == LedgerAccounts.MaintenanceExpense);
        Assert.Equal((0m, 1_300m, -500m), (Net(map[S.ProvisionLiability].Id), Net(map[S.ProvisionExpense].Id), Net(repairs.Id)));
    }

    private sealed record Line(string Slot, string? Code, decimal Debit, decimal Credit);
    private sealed record Entry(string Kind, DateOnly Day, string OrderNumber, IReadOnlyList<Line> Lines);
    private sealed record Provision(decimal Booked, decimal Used, decimal Released, decimal HeldAtEnd);
    private sealed record Month(IReadOnlyList<Entry> Entries, Provision Provision);

    /// <summary>A month of the practice ledger over the route: every check passes and every entry balances.</summary>
    private static async Task<Month> MonthAsync(MaintenanceWorld w, ClaimsPrincipal caller, int month, bool provision = true)
    {
        var (status, body) = await w.CallAsync(caller, "GET", Ledger, $"?year=2026&month={month}");
        w.Db.ChangeTracker.Clear();
        Assert.True(status == 200, $"{status} {body}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.All(root.GetProperty("checks").EnumerateArray(), c => Assert.True(c.GetProperty("passed").GetBoolean(), c.GetProperty("code").GetString()));
        if (provision) Assert.Contains(root.GetProperty("checks").EnumerateArray(), c => c.GetProperty("code").GetString() == MaintenancePracticeLedger.CheckProvision);
        var entries = root.GetProperty("entries").EnumerateArray().Select(e => new Entry(
            e.GetProperty("kind").GetString()!, DateOnly.Parse(e.GetProperty("day").GetString()!), e.GetProperty("orderNumber").GetString()!,
            e.GetProperty("lines").EnumerateArray().Select(l => new Line(l.GetProperty("slot").GetString()!,
                l.GetProperty("accountCode").ValueKind == JsonValueKind.Null ? null : l.GetProperty("accountCode").GetString(),
                l.GetProperty("debit").GetDecimal(), l.GetProperty("credit").GetDecimal())).ToList())).ToList();
        Assert.All(entries, e => Assert.Equal(e.Lines.Sum(l => l.Debit), e.Lines.Sum(l => l.Credit)));
        var p = root.GetProperty("provisions").EnumerateArray().FirstOrDefault();
        return new Month(entries, p.ValueKind == JsonValueKind.Undefined ? new Provision(0m, 0m, 0m, 0m)
            : new Provision(p.GetProperty("booked").GetDecimal(), p.GetProperty("used").GetDecimal(), p.GetProperty("released").GetDecimal(),
                p.GetProperty("heldAtEnd").GetDecimal()));
    }

    private static decimal Debit(Entry e, string slot) => e.Lines.Where(l => l.Slot == slot).Sum(l => l.Debit);
    private static decimal Credit(Entry e, string slot) => e.Lines.Where(l => l.Slot == slot).Sum(l => l.Credit);

    // ---------- the provision, pure ----------

    private static MaintenanceProvisionRework Rework(string number, IReadOnlyList<MaintenanceProvisionCost> start, IReadOnlyList<MaintenanceProvisionCost> end) =>
        new(Guid.NewGuid(), number, Algiers, start, end);

    [Fact]
    public void A_provision_is_used_up_to_its_amount_in_the_reworks_order_and_the_excess_stays_in_expense()
    {
        var outside = S.Account("615");
        var first = Rework("MJ-000010", [], [new(S.CustomerParts, 800m), new(outside, 500m), new(S.ServiceCost, 300m)]);
        var second = Rework("MJ-000011", [], [new(S.CustomerParts, 400m)]);
        var job = new MaintenanceProvisionJob(Guid.NewGuid(), "MJ-000001", Algiers, "FA-1", 1_000m,
            new DateOnly(2026, 8, 10), true, true, new DateOnly(2026, 11, 10), false, false, [second, first]);

        var (entries, movement) = P.Month(job, new DateOnly(2026, 9, 30));
        // 1,000 held: the first rework's parts (800) then 200 of its outside work; its labour and the second rework stay in expense.
        var used = Assert.Single(entries);
        Assert.Equal((P.KindUsed, "MJ-000010", "MJ-000001"), (used.Kind, used.OrderNumber, used.Document));
        Assert.Equal([(S.ProvisionLiability, 1_000m, 0m), (S.CustomerParts, 0m, 800m), (outside, 0m, 200m)],
            used.Lines.Select(l => (l.Slot, l.Debit, l.Credit)));
        Assert.Equal(-1_000m, movement);

        // Parts of the first rework returned next month (400 back): the provision takes back what it no longer covers of them
        // and gives it to what waited — the rest of the outside work and the labour, then the second rework.
        var later = job with
        {
            Reworks = [first with { AtStart = first.AtEnd, AtEnd = [new(S.CustomerParts, 400m), new(outside, 500m), new(S.ServiceCost, 300m)] },
                second with { AtStart = second.AtEnd }],
        };
        var (moved, net) = P.Month(later, new DateOnly(2026, 10, 31));
        var shift = Assert.Single(moved);
        Assert.Equal([(S.CustomerParts, 400m, 0m), (outside, 0m, 300m), (S.ServiceCost, 0m, 100m)],
            shift.Lines.Where(l => l.Slot != S.ProvisionLiability).Select(l => (l.Slot, l.Debit, l.Credit)));
        Assert.DoesNotContain(shift.Lines, l => l.Slot == S.ProvisionLiability);
        Assert.Equal(0m, net);
        Assert.All(moved.Concat(entries), e => Assert.Equal(e.Lines.Sum(l => l.Debit), e.Lines.Sum(l => l.Credit)));

        // Booked and released in one month (invoiced after the guarantee ended): out again whole, nothing used.
        var late = new MaintenanceProvisionJob(Guid.NewGuid(), "MJ-000002", Algiers, "FA-2", 600m,
            new DateOnly(2026, 9, 20), false, true, new DateOnly(2026, 9, 20), false, true, []);
        var (pair, zero) = P.Month(late, new DateOnly(2026, 9, 30));
        Assert.Equal([P.KindBooked, P.KindReleased], pair.Select(e => e.Kind));
        Assert.Equal(0m, zero);
    }
}
