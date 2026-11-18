using System.Globalization;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Maintenance;
using ParadiERP.Application.Manufacturing;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Manufacturing;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Maintenance;

/// <summary>
/// Maintenance › Practice ledger (Phase M6, docs/maintenance/plan.md §5): one month of the entries the accountant's choices
/// would make for maintenance, built by <see cref="MaintenancePracticeLedger"/> from the jobs' events. Computed on every read
/// and written nowhere: <c>LedgerEntries</c>, stock, payroll and the jobs are only read. Modelled on manufacturing's
/// <see cref="PracticeLedgerReport"/>.
/// <list type="bullet">
/// <item><b>Parts</b>: each MJI-/MJR- document of the month, valued per stock group at the values frozen on its lines (M2),
/// rounded once per group — but not a document already posted at issue (MS-32).</item>
/// <item><b>Labour and overhead</b> (MS-33 "moved"): each job's labour and overhead to date at the month's end less at its start,
/// as the job's cost values them (M4: payroll's rate of each employee-month known today, or MS-12's workshop rate).</item>
/// <item><b>Open customer jobs</b> (MS-36): each customer job's cost to date at the month's start and end while it was open.</item>
/// <item><b>Customer jobs' invoices</b> of the month (<c>SourceType = maintenance-job</c>): listed for the tie-out only.</item>
/// <item><b>Guarantee provisions</b> (MS-37 "provision", Phase M7c): each customer job whose invoice froze a provision, from the
/// invoice's month to its guarantee's end — what its reworks had booked at the month's start and end (parts; outside work once
/// its voucher was paid; labour and overhead when moved), for <see cref="MaintenanceGuaranteeProvision"/>. A provision invoiced
/// before the first month to post (MS-39) is left out whole: it never posted, so nothing of it is used or released.</item>
/// </list>
/// <para>
/// <b>Access</b> (plan §6.2): <c>reports.accounting</c> or <c>maintenance.costs.view</c>. When the entries carry labour valued at
/// payroll rates — MS-33 "moved", or MS-36 carrying work in progress, with labour in the cost (MS-10) at the payroll-derived rate
/// (MS-12) — the month is shown only to a reader who sees pay rates (<see cref="CostPrivacy"/>, <c>payroll_details_withheld</c>).
/// A reader held to a branch reads that branch's jobs only (the route's <c>branchId</c>).
/// </para>
/// </summary>
public sealed class MaintenancePracticeLedgerReport(
    PosDbContext db, MaintenanceCosting costing, LabourRates rates, IRegionalSettingsProvider regional, TimeProvider time)
{
    /// <summary>A customer job's invoice names it so (plan §3.2, Phase M5).</summary>
    public const string InvoiceSourceType = InvoiceSources.MaintenanceJob;

    private sealed record PartLine(Guid JobId, Guid DocumentId, string Number, MaintenancePartKind Kind, DateTime AtUtc,
        Guid ProductId, string Sku, ManufacturingRole Role, decimal Qty, decimal Value);

    private sealed record Booking(Guid JobId, Guid EmployeeId, DateOnly Day, int Year, int Month, decimal Hours, decimal OvertimeHours);

    private sealed record Expense(Guid Id, Guid JobId, decimal Amount, DateTime AtUtc, bool Settled);

    public async Task<MaintenanceResult<MaintenancePracticeLedgerDto>> MonthAsync(
        int? askedYear, int? askedMonth, Guid? branchId, Func<string, bool> has, CancellationToken ct = default)
    {
        if (!has(PermissionCatalog.ReportsAccounting) && !has(PermissionCatalog.MaintenanceCostsView))
            return MaintenanceResult<MaintenancePracticeLedgerDto>.Fail(403, "forbidden",
                "The maintenance practice ledger needs reports.accounting or maintenance.costs.view.");
        var (row, policy) = await costing.SettingsAsync(ct);
        var labourMoved = row.LabourInBooks == MaintenanceLabourBooks.MovedIntoJobs;
        var wipOn = row.OpenJobsAtPeriodEnd != MaintenanceOpenJobs.LeftInExpensesListed;
        // Labour at payroll rates in the entries gives a pay rate back (one or two workers on a job): rate access only.
        var labourAtPayRates = policy.LabourIncluded && policy.WorkshopRatePerHour is null;
        if ((labourMoved || wipOn) && labourAtPayRates && !CostPrivacy.RatesShown(has))
            return MaintenanceResult<MaintenancePracticeLedgerDto>.Fail(403, CostPrivacy.WithheldCode, CostPrivacy.WithheldMessage);

        // No month asked: the company's month today, on the server's clock in the company's zone, never the browser's.
        var zone = StoreDocumentHistory.Zone(regional);
        var nowUtc = time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        var (year, month) = askedYear is int y && askedMonth is int m ? (y, m) : (today.Year, today.Month);
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
            return MaintenanceResult<MaintenancePracticeLedgerDto>.Fail(400, "invalid_period", "Choose a month.");
        var first = new DateOnly(year, month, 1);
        if (first > today) return MaintenanceResult<MaintenancePracticeLedgerDto>.Fail(400, "month_in_future", "That month has not begun.");
        var last = first.AddMonths(1).AddDays(-1);
        var startUtc = StartUtc(first, zone);
        var endUtc = StartUtc(first.AddMonths(1), zone);
        var monthToDate = endUtc > nowUtc;
        var endCut = monthToDate ? nowUtc.AddTicks(1) : endUtc;
        DateOnly Day(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

        // ---------- the jobs the month touched ----------

        var jobsQuery = db.MaintenanceJobs.AsNoTracking().Where(j => j.Status != MaintenanceJobStatus.Draft);
        if (branchId is Guid b) jobsQuery = jobsQuery.Where(j => j.BranchId == b);
        var inScope = jobsQuery.Select(j => j.Id);
        var touched = (await db.MaintenanceJobParts.AsNoTracking()
                .Where(p => inScope.Contains(p.JobId) && p.AtUtc >= startUtc && p.AtUtc < endUtc).Select(p => p.JobId).Distinct().ToListAsync(ct))
            .ToHashSet();
        if (labourMoved)
            touched.UnionWith((await db.LaborBookings.AsNoTracking()
                .Where(x => x.OrderKind == LaborOrderKind.Maintenance && x.OrderId != null && inScope.Contains(x.OrderId.Value)
                    && x.Day >= first && x.Day <= last)
                .Select(x => x.OrderId!.Value).Distinct().ToListAsync(ct)));
        // Customer jobs open at the month's start or end (MS-36); also listed under the default.
        var customer = await jobsQuery.Where(j => j.Kind == MaintenanceJobKind.Customer && j.CreatedAtUtc < endUtc
                && (j.Status == MaintenanceJobStatus.Open || (j.CompletedAtUtc ?? j.CancelledAtUtc ?? j.UpdatedAtUtc) >= startUtc))
            .Select(j => j.Id).ToListAsync(ct);
        touched.UnionWith(customer);
        var jobs = await db.MaintenanceJobs.AsNoTracking().Where(j => touched.Contains(j.Id)).ToListAsync(ct);
        var ids = jobs.Select(j => j.Id).ToList();

        // ---------- MS-37 "provision" (Phase M7c): the provisions this month moves, and their reworks ----------

        // Every customer job whose invoice froze a provision, wherever MS-37 stands now: a provision once set aside is used and
        // released to the end. One invoiced before the first month to post (MS-39) never posted, so none of it is carried.
        var provisionQuery = db.MaintenanceJobs.AsNoTracking()
            .Where(j => j.ProvisionAmount != null && j.InvoiceId != null && j.GuaranteeUntil != null);
        if (branchId is Guid pb) provisionQuery = provisionQuery.Where(j => j.BranchId == pb);
        var provisioned = await provisionQuery
            .Join(db.Invoices, j => j.InvoiceId, i => (Guid?)i.Id, (j, i) => new { Job = j, i.Number, i.IssuedAtUtc })
            .ToListAsync(ct);
        var provisions = provisioned
            .Select(x =>
            {
                var issued = DateTime.SpecifyKind(x.IssuedAtUtc, DateTimeKind.Utc);
                var guaranteeEnd = StartUtc(x.Job.GuaranteeUntil!.Value.AddDays(1), zone);
                return new
                {
                    x.Job, x.Number, Issued = issued, InvoiceMonth = new DateOnly(Day(issued).Year, Day(issued).Month, 1),
                    ReleaseAt = guaranteeEnd > issued ? guaranteeEnd : issued,
                };
            })
            .Where(x => row.PostingFromMonth is not DateOnly fromMonth || x.InvoiceMonth >= fromMonth)
            // Booked by the month's end and not released by the time it began.
            .Where(x => x.Issued < endCut && x.ReleaseAt > startUtc)
            .ToList();
        var provisionIds = provisions.Select(p => p.Job.Id).ToList();
        var reworkJobs = provisionIds.Count == 0 ? []
            : await db.MaintenanceJobs.AsNoTracking()
                .Where(j => j.ReworkOfJobId != null && provisionIds.Contains(j.ReworkOfJobId.Value) && j.Status != MaintenanceJobStatus.Draft)
                .ToListAsync(ct);
        // What the month's jobs and the provisions' reworks recorded is read together.
        var loadIds = ids.Concat(reworkJobs.Select(j => j.Id)).Distinct().ToList();

        // ---------- what they recorded ----------

        var parts = await db.MaintenanceJobParts.AsNoTracking().Where(p => loadIds.Contains(p.JobId))
            .Select(p => new PartLine(p.JobId, p.DocumentId, p.DocumentNumber, p.Kind, p.AtUtc, p.ProductId, p.Product!.Sku,
                p.Product.ManufacturingRole, p.Qty, p.Value))
            .ToListAsync(ct);
        var monthDocIds = parts.Where(p => p.AtUtc >= startUtc && p.AtUtc < endUtc).Select(p => p.DocumentId).Distinct().ToList();
        var postedAtIssue = await MaintenancePartsAtIssue.PostedAsync(db, monthDocIds, ct);

        var orderIds = loadIds.Select(id => (Guid?)id).ToList();
        var bookings = await db.LaborBookings.AsNoTracking()
            .Where(x => x.OrderKind == LaborOrderKind.Maintenance && orderIds.Contains(x.OrderId))
            .Select(x => new Booking(x.OrderId!.Value, x.EmployeeId, x.Day, x.PayrollYear, x.PayrollMonth, x.Hours, x.OvertimeHours))
            .ToListAsync(ct);
        var needRates = labourAtPayRates && bookings.Count > 0;
        var labourRates = needRates
            ? await rates.ForAsync(bookings.Select(x => (x.EmployeeId, x.Year, x.Month)), ct)
            : new Dictionary<(Guid EmployeeId, int Year, int Month), LabourRate>();
        var recorded = await db.MaintenanceJobExpenses.AsNoTracking().Where(e => loadIds.Contains(e.JobId))
            .Select(e => new { e.Id, e.JobId, e.Amount, e.AtUtc, e.PaymentVoucherId }).ToListAsync(ct);
        var voucherIds = recorded.Where(e => e.PaymentVoucherId != null).Select(e => e.PaymentVoucherId!.Value).Distinct().ToList();
        var vouchers = voucherIds.Count == 0 ? []
            : await db.PaymentVouchers.AsNoTracking().Where(v => voucherIds.Contains(v.Id))
                .Select(v => new { v.Id, v.Status, v.PaidAtUtc, v.ExpenseAccountCode }).ToDictionaryAsync(v => v.Id, ct);
        bool Backed(Guid? voucher) => voucher is Guid v && vouchers.TryGetValue(v, out var found)
            && found.Status is PaymentVoucherStatus.Approved or PaymentVoucherStatus.Paid;
        var expenses = recorded.Select(e => new Expense(e.Id, e.JobId, e.Amount, e.AtUtc, Backed(e.PaymentVoucherId))).ToList();

        static DateTime? ClosedAt(MaintenanceJob job) => job.Status switch
        {
            MaintenanceJobStatus.Completed or MaintenanceJobStatus.Invoiced => job.CompletedAtUtc ?? job.UpdatedAtUtc,
            MaintenanceJobStatus.Cancelled => job.CancelledAtUtc ?? job.UpdatedAtUtc,
            _ => null,
        };

        // A job's cost to date at a moment, as its cost card values it (M4).
        MaintenanceCostSheet CostBeforeOf(MaintenanceJob job, DateTime cutoff)
        {
            var closedAt = ClosedAt(job);
            var lastDay = Day(cutoff.AddTicks(-1));
            var before = parts.Where(p => p.JobId == job.Id && p.AtUtc < cutoff).ToList();
            var costParts = before.GroupBy(p => p.ProductId).Select(g => new MaintenanceCostPart(g.Key, g.First().Sku, g.First().Role, "",
                g.Where(p => p.Kind == MaintenancePartKind.Issue).Sum(p => p.Qty), g.Where(p => p.Kind == MaintenancePartKind.Return).Sum(p => p.Qty),
                g.Where(p => p.Kind == MaintenancePartKind.Issue).Sum(p => p.Value), g.Where(p => p.Kind == MaintenancePartKind.Return).Sum(p => p.Value)))
                .OrderBy(p => p.Sku, StringComparer.Ordinal).ToList();
            var costBookings = bookings.Where(x => x.JobId == job.Id && x.Day <= lastDay)
                .Select(x => new CostingBooking(x.EmployeeId, job.Id, x.Day, x.Year, x.Month, x.Hours, x.OvertimeHours)).ToList();
            var costExpenses = expenses.Where(e => e.JobId == job.Id && e.AtUtc < cutoff)
                .Select(e => new CostingExpense(e.Id, e.Amount, e.AtUtc, e.Settled)).ToList();
            return MaintenanceCostAdapter.Compute(new MaintenanceCostInput(job.Qty, closedAt < cutoff && job.Status != MaintenanceJobStatus.Cancelled,
                costParts, costBookings, labourRates, costExpenses, policy));
        }

        // ---------- each job as the month saw it ----------

        var facts = new List<MaintenancePracticeJob>(jobs.Count);
        var openAtEnd = new List<(MaintenanceJob Job, decimal Cost, bool HasLabour)>();
        foreach (var job in jobs)
        {
            var closedAt = ClosedAt(job);
            var mine = parts.Where(p => p.JobId == job.Id).ToList();
            var myBookings = bookings.Where(x => x.JobId == job.Id).ToList();
            MaintenanceCostSheet CostBefore(DateTime cutoff) => CostBeforeOf(job, cutoff);

            var isCustomer = job.Kind == MaintenanceJobKind.Customer;
            // Phase M7c: a free rework is never invoiced, so it is no work in progress (MS-36) nor listed as an open job to bill:
            // its cost stays in expense, or is taken from the guarantee provision.
            var billable = job.ReworkOfJobId is null;
            var openAtStartFlag = billable && job.CreatedAtUtc < startUtc && (closedAt is null || closedAt >= startUtc);
            var openAtEndFlag = billable && job.CreatedAtUtc < endCut && (closedAt is null || closedAt >= endCut);
            MaintenanceCostSheet? start = null, end = null;
            if (labourMoved || (isCustomer && wipOn))
            {
                start = CostBefore(startUtc);
                end = CostBefore(endCut);
            }
            if (isCustomer && openAtEndFlag)
            {
                var atEnd = end ?? CostBefore(endCut);
                openAtEnd.Add((job, Money(atEnd.Total), policy.LabourIncluded && myBookings.Any(x => x.Day <= Day(endCut.AddTicks(-1)))));
            }

            var docs = mine.Where(p => p.AtUtc >= startUtc && p.AtUtc < endUtc && !postedAtIssue.Contains(p.DocumentId))
                .GroupBy(p => p.DocumentId)
                .Select(g =>
                {
                    var head = g.First();
                    return new MaintenancePracticeDoc(head.Number, Day(head.AtUtc), head.Kind == MaintenancePartKind.Return,
                        MaintenancePartsAtIssue.Values(g.Select(p => new MaintenancePartsAtIssue.Line(p.Role, p.Value))));
                }).ToList();

            facts.Add(new MaintenancePracticeJob(
                job.Id, job.Number, job.BranchId, isCustomer, docs,
                LabourMoved: labourMoved && start is not null ? Money(end!.Labour) - Money(start.Labour) : 0m,
                OverheadMoved: labourMoved && start is not null ? Money(end!.Overhead) - Money(start.Overhead) : 0m,
                ClosedDay: closedAt is DateTime at && at >= startUtc && at < endUtc ? Day(at) : null,
                OpenAtStart: openAtStartFlag, OpenAtEnd: openAtEndFlag,
                CostAtStart: start is not null && openAtStartFlag ? Money(start.Total) : 0m,
                CostAtEnd: end is not null && openAtEndFlag ? Money(end.Total) : 0m));
        }

        // ---------- customer jobs' invoices of the month (posted by the invoice; tie-out only) ----------

        var invoiceQuery = db.Invoices.AsNoTracking()
            .Where(i => i.SourceType == InvoiceSourceType && i.IssuedAtUtc >= startUtc && i.IssuedAtUtc < endUtc);
        if (branchId is Guid ib) invoiceQuery = invoiceQuery.Where(i => i.BranchId == ib);
        var invoices = await invoiceQuery.Select(i => new { i.Number, i.IssuedAtUtc, i.SourceId, i.SubTotalHt }).ToListAsync(ct);
        var invoicedJobIds = invoices.Where(i => i.SourceId != null).Select(i => i.SourceId!.Value).Distinct().ToList();
        var invoicedJobs = invoicedJobIds.Count == 0 ? []
            : await db.MaintenanceJobs.AsNoTracking().Where(j => invoicedJobIds.Contains(j.Id))
                .ToDictionaryAsync(j => j.Id, j => new { j.Number, j.Kind }, ct);
        var perJob = invoices.Where(i => i.SourceId != null).GroupBy(i => i.SourceId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var invoiceFacts = invoices.OrderBy(i => i.IssuedAtUtc).ThenBy(i => i.Number, StringComparer.Ordinal).Select(i =>
        {
            var job = i.SourceId is Guid id ? invoicedJobs.GetValueOrDefault(id) : null;
            var matched = job is not null && job.Kind == MaintenanceJobKind.Customer && perJob.GetValueOrDefault(i.SourceId!.Value) == 1;
            return new MaintenancePracticeInvoice(i.Number, Day(i.IssuedAtUtc), i.SourceId, job?.Number, matched, i.SubTotalHt);
        }).ToList();

        // ---------- MS-37 "provision": each provision's reworks, what they had booked at the month's start and end ----------

        // What a rework had booked to each account from the invoice month's first day to the cut: its parts (customer parts, as
        // the practice ledger or MS-32 charged them), its outside work once its voucher was paid (back to the expense account the
        // voucher debited, net of the recoverable VAT as its cost counts it), and labour and overhead only when moved into jobs
        // (MS-33; the service cost) — under "report only" they stay in the wage charges and are not taken.
        List<MaintenanceProvisionCost> ReworkCosts(MaintenanceJob rework, DateTime from, DateTime cut)
        {
            var costs = new List<MaintenanceProvisionCost>();
            if (cut <= from) return costs;
            var partsValue = parts.Where(p => p.JobId == rework.Id && p.AtUtc >= from && p.AtUtc < cut).GroupBy(p => p.DocumentId)
                .Sum(g => (g.First().Kind == MaintenancePartKind.Return ? -1m : 1m)
                    * MaintenancePartsAtIssue.Values(g.Select(p => new MaintenancePartsAtIssue.Line(p.Role, p.Value))).Sum(v => v.Value));
            costs.Add(new MaintenanceProvisionCost(MaintenanceAccountSlots.CustomerParts, partsValue));
            foreach (var e in recorded.Where(e => e.JobId == rework.Id && e.PaymentVoucherId is not null))
            {
                if (!vouchers.TryGetValue(e.PaymentVoucherId!.Value, out var v) || v.Status != PaymentVoucherStatus.Paid
                    || string.IsNullOrEmpty(v.ExpenseAccountCode)) continue;
                var at = v.PaidAtUtc is DateTime paid && paid > e.AtUtc ? paid : e.AtUtc;
                if (at < from || at >= cut) continue;
                costs.Add(new MaintenanceProvisionCost(MaintenanceAccountSlots.Account(v.ExpenseAccountCode!),
                    Money(policy.ExpenseVatPercent > 0m ? e.Amount * 100m / (100m + policy.ExpenseVatPercent) : e.Amount)));
            }
            if (labourMoved && policy.LabourIncluded)
            {
                var (a, b) = (CostBeforeOf(rework, from), CostBeforeOf(rework, cut));
                costs.Add(new MaintenanceProvisionCost(MaintenanceAccountSlots.ServiceCost,
                    Money(b.Labour) - Money(a.Labour) + Money(b.Overhead) - Money(a.Overhead)));
            }
            return costs;
        }

        static DateTime Earlier(DateTime x, DateTime y) => x < y ? x : y;
        var provisionFacts = provisions.OrderBy(p => p.Job.Number, StringComparer.Ordinal).Select(p =>
        {
            var from = StartUtc(p.InvoiceMonth, zone);
            var reworks = reworkJobs.Where(r => r.ReworkOfJobId == p.Job.Id).OrderBy(r => r.Number, StringComparer.Ordinal)
                .Select(r => new MaintenanceProvisionRework(r.Id, r.Number, r.BranchId,
                    ReworkCosts(r, from, Earlier(startUtc, p.ReleaseAt)), ReworkCosts(r, from, Earlier(endCut, p.ReleaseAt))))
                .ToList();
            return new MaintenanceProvisionJob(p.Job.Id, p.Job.Number, p.Job.BranchId, p.Number, p.Job.ProvisionAmount!.Value,
                Day(p.Issued), p.Issued < startUtc, p.Issued < endCut,
                Day(p.ReleaseAt.AddTicks(-1)), p.ReleaseAt <= startUtc, p.ReleaseAt <= endCut, reworks);
        }).ToList();

        // ---------- MS-30 "capitalised above an amount" (Phase M7d): the repairs capitalised at completion this month ----------

        // Their credit lines were frozen at completion, and the register's movement carries what the pool received. Kept whatever
        // MS-30 says now: a repair once capitalised is in the register.
        var capitalisedQuery = db.MaintenanceJobs.AsNoTracking()
            .Where(j => j.CapitalisedAtUtc != null && j.CapitalisedAtUtc >= startUtc && j.CapitalisedAtUtc < endCut);
        if (branchId is Guid cb) capitalisedQuery = capitalisedQuery.Where(j => j.BranchId == cb);
        var capitalisedJobs = await capitalisedQuery.Select(j => new { j.Id, j.Number, j.BranchId, j.CapitalisedAtUtc, j.CapitalisedMovementId })
            .ToListAsync(ct);
        var capitalisedIds = capitalisedJobs.Select(j => j.Id).ToList();
        var capitalisedLines = capitalisedIds.Count == 0 ? []
            : await db.MaintenanceJobCapitalisedLines.AsNoTracking().Where(l => capitalisedIds.Contains(l.JobId)).ToListAsync(ct);
        var movementIds = capitalisedJobs.Where(j => j.CapitalisedMovementId != null).Select(j => j.CapitalisedMovementId!.Value).ToList();
        var registerCosts = movementIds.Count == 0 ? new Dictionary<Guid, decimal>()
            : await db.FixedAssetMovements.AsNoTracking().Where(m => movementIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Cost, ct);
        // Labour in a capitalised repair, valued at payroll rates, gives a pay rate back as moved labour does: rate access only.
        if (labourAtPayRates && !CostPrivacy.RatesShown(has) && capitalisedLines.Any(l =>
                l.Element is MaintenanceCapitalisation.ElementLabour or MaintenanceCapitalisation.ElementOverhead))
            return MaintenanceResult<MaintenancePracticeLedgerDto>.Fail(403, CostPrivacy.WithheldCode, CostPrivacy.WithheldMessage);
        var capitalisations = capitalisedJobs.Select(j => new MaintenancePracticeCapitalisation(j.Id, j.Number, j.BranchId,
            Day(j.CapitalisedAtUtc!.Value), j.CapitalisedMovementId is Guid m ? registerCosts.GetValueOrDefault(m) : 0m,
            capitalisedLines.Where(l => l.JobId == j.Id).OrderBy(l => l.Element, StringComparer.Ordinal).ThenBy(l => l.Slot, StringComparer.Ordinal)
                .Select(l => new MaintenanceCapitalisedCredit(l.Slot, l.Amount)).ToList())).ToList();

        var result = MaintenancePracticeLedger.Build(
            new MaintenancePracticeChoices(row.LabourInBooks, row.OpenJobsAtPeriodEnd, policy.LabourIncluded),
            year, month, facts, invoiceFacts, monthToDate, provisionFacts, capitalisations);

        // ---------- slots to accounts ----------

        var accounts = await MaintenanceAccountMap.AccountsAsync(db, ct);
        // A rework's outside work goes back to the account its voucher debited (Phase M7c): that account, by its code.
        var named = result.Entries.SelectMany(e => e.Lines).Select(l => l.Slot)
            .Where(s => s.StartsWith(MaintenanceAccountSlots.AccountPrefix, StringComparison.Ordinal)).Distinct().ToList();
        if (named.Count > 0)
        {
            var codes = named.Select(s => s[MaintenanceAccountSlots.AccountPrefix.Length..]).ToList();
            foreach (var a in await db.LedgerAccounts.AsNoTracking().Where(a => codes.Contains(a.Code)).ToListAsync(ct))
                accounts[MaintenanceAccountSlots.Account(a.Code)] = a;
        }
        LedgerAccount? AccountOf(string slot) => accounts.GetValueOrDefault(slot);
        var branchIds = facts.Select(f => f.BranchId).Concat(openAtEnd.Select(o => o.Job.BranchId))
            .Concat(result.Entries.Select(e => e.BranchId)).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking().Where(x => branchIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        var entries = result.Entries.Select(e =>
        {
            var branch = branches.GetValueOrDefault(e.BranchId);
            return new PracticeLedgerEntryDto(e.Kind, e.Day, e.OrderId, e.OrderNumber, e.BranchId, branch?.Code ?? "",
                branch?.NameAr ?? "", branch?.NameFr ?? "", branch?.NameEn ?? "", e.Document,
                e.Lines.Select(l =>
                {
                    var a = AccountOf(l.Slot);
                    return new PracticeLedgerLineDto(l.Slot, a?.Id, a?.Code, a?.NameAr, a?.NameFr, a?.NameEn, l.Debit, l.Credit);
                }).ToList());
        }).OrderBy(e => e.Day).ThenBy(e => e.OrderNumber, StringComparer.Ordinal).ToList();

        var lines = result.Entries.SelectMany(e => e.Lines).ToList();
        var used = lines.Select(l => l.Slot).ToHashSet();
        var order = MaintenanceAccountSlots.All.Concat(MaintenanceAccountSlots.SharedStock).Append(MaintenanceAccountSlots.MerchandiseStock)
            .Append(MaintenancePracticeLedger.AssetSlot)
            .Concat(named.OrderBy(s => s, StringComparer.Ordinal)).ToList();
        var totals = order.Where(used.Contains).Select(slot =>
        {
            var a = AccountOf(slot);
            var slotLines = lines.Where(l => l.Slot == slot).ToList();
            return new PracticeLedgerAccountDto(slot, a?.Id, a?.Code, a?.NameAr, a?.NameFr, a?.NameEn, slotLines.Sum(l => l.Debit), slotLines.Sum(l => l.Credit));
        }).ToList();
        var unmapped = totals.Where(t => t.AccountId is null).Select(t => t.Slot).ToList();
        var checks = result.Checks.Select(c => new PracticeLedgerCheckDto(c.Code, c.Passed, c.Expected, c.Actual))
            // A month posts only to accounts the accountant named: an unmapped slot the month uses blocks it.
            .Append(new PracticeLedgerCheckDto("accounts_mapped", unmapped.Count == 0, used.Count, used.Count - unmapped.Count))
            .ToList();

        var notes = result.Notes.ToList();
        if (!row.AccountantApproved) notes.Add("policy_not_confirmed");
        // Phase M7c: under MS-33 "report only" a rework's hours stay in the wage charges, so the provision takes only its parts
        // and paid outside work.
        if (provisionFacts.Any(p => p.Reworks.Count > 0) && !labourMoved && policy.LabourIncluded) notes.Add(NoteProvisionLabourInWages);

        var atIssue = parts.Where(p => postedAtIssue.Contains(p.DocumentId)).GroupBy(p => p.DocumentId).Select(g =>
        {
            var head = g.First();
            var job = jobs.First(j => j.Id == head.JobId);
            return new MaintenancePostedAtIssueDto(head.Number, job.Id, job.Number, Day(head.AtUtc),
                Math.Round((head.Kind == MaintenancePartKind.Return ? -1m : 1m) * g.Sum(p => p.Value), 3, MidpointRounding.AwayFromZero));
        }).OrderBy(d => d.Day).ThenBy(d => d.Document, StringComparer.Ordinal).ToList();

        var moneyShown = MaintenanceCosting.MoneyShown(policy, has);
        var open = openAtEnd.OrderBy(o => o.Job.Number, StringComparer.Ordinal).Select(o => new MaintenanceOpenJobDto(
            o.Job.Id, o.Job.Number, o.Job.BranchId, branches.GetValueOrDefault(o.Job.BranchId)?.Code ?? "",
            moneyShown || !o.HasLabour ? o.Cost : null)).ToList();

        var postings = await db.MaintenanceMonthPostings.AsNoTracking().Where(p => p.Year == year && p.Month == month)
            .OrderBy(p => p.Generation)
            .Select(p => new PracticeLedgerPostingDto(p.Generation, p.Entries, p.TotalDebit, p.PostedAtUtc, p.PostedBy, p.ReversedAtUtc, p.ReversedBy))
            .ToListAsync(ct);

        return MaintenanceResult<MaintenancePracticeLedgerDto>.Done(new MaintenancePracticeLedgerDto(
            year, month, monthToDate, branchId,
            row.LabourInBooks.ToString(), row.OpenJobsAtPeriodEnd.ToString(), row.PartsPosting.ToString(), row.OwnRepairs.ToString(),
            row.AccountantApproved,
            entries, totals, checks, unmapped, notes, lines.Sum(l => l.Debit), lines.Sum(l => l.Credit),
            atIssue, open,
            invoiceFacts.Select(i => new MaintenanceLedgerInvoiceDto(i.Number, i.Day, i.JobId, i.JobNumber, i.JobMatched, i.NetAmount)).ToList(),
            row.PostingFromMonth?.ToString("yyyy-MM", CultureInfo.InvariantCulture), postings,
            row.Rework.ToString(),
            provisionFacts.Select(p =>
            {
                var state = MaintenanceGuaranteeProvision.StateOf(p);
                return new MaintenanceLedgerProvisionDto(p.JobId, p.Number, p.InvoiceNumber, p.Amount, state.Booked, state.Used,
                    state.Released, state.HeldAtEnd, provisions.First(x => x.Job.Id == p.JobId).Job.GuaranteeUntil!.Value);
            }).ToList()));
    }

    /// <summary>MS-37 "provision" with MS-33 "report only": a rework's labour stays a wage charge, not taken from the provision.</summary>
    public const string NoteProvisionLabourInWages = "provision_labour_in_wages";

    private static DateTime StartUtc(DateOnly day, TimeZoneInfo zone) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone);

    /// <summary>The ledger's precision: three decimals, as manufacturing's practice ledger.</summary>
    private static decimal Money(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
