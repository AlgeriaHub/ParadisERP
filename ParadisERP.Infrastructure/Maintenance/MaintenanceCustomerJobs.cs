using ParadisERP.Application.Contracts;
using ParadisERP.Application.Maintenance;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Receivables;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Maintenance;

/// <summary>
/// Customer jobs, Phase M5a (docs/maintenance/plan.md §2.1, §3.2, §4 MS-06, MS-20 to MS-25, MS-34): what the New job form
/// offers, a customer job's price and its one invoice.
/// <list type="bullet">
/// <item><b>Price</b> (<c>maintenance.pricing</c>): MS-20's basis proposes (<see cref="MaintenancePricing"/>) — Quoted: nothing,
/// the price is typed; Itemised: parts at their sale price before tax + hours × MS-21 + outside work at cost + MS-22 % of it;
/// Cost-plus: the job's cost C (Phase M4, <see cref="MaintenanceCosting"/>) + MS-22 % of it. The holder sets the price before
/// tax. A price below the cost is never refused (MS-25): shown as a loss, or saved only once a pricing holder confirms it.</item>
/// <item><b>Invoice</b> (<c>finance.invoices.create</c> + <c>maintenance.view</c>): one per completed customer job, through the
/// existing writer (<see cref="ReceivableDocuments.CreateMaintenanceJobInvoiceAsync"/>, <see cref="InvoiceOrigin.MaintenanceJob"/>),
/// which posts 411 / income / 4457 through its own poster. Income per MS-34: the account recharges use today (the return
/// note's <c>RentalChargeKind.Maintenance</c> account, 707, the default); one line on the service income slot's account; or
/// parts at sale price on 700 and the rest on that account. VAT
/// off unless the seller ticks it (owner, 2026-11-05), then at the country profile's default rate. The price is frozen on the
/// job; a second invoice is refused.</item>
/// </list>
/// Every write takes the version lock before it reads the job. Branch scope and the module switch are the API's.
/// </summary>
public sealed class MaintenanceCustomerJobs(
    PosDbContext db, MaintenanceJobs jobs, MaintenanceCosting costing, ReceivableDocuments receivables, IRegionalSettingsProvider regional)
{
    public const decimal MaxPrice = 1_000_000_000m;
    public const string NotCustomerJobCode = "not_customer_job";
    public const string BelowCostCode = "price_below_cost";
    public const string PriceRequiredCode = "price_required";
    public const string AlreadyInvoicedCode = "already_invoiced";
    public const string NotCompletedCode = "not_completed";

    // ---------- the New job form ----------

    public async Task<MaintenanceNewJobOptionsDto> NewJobOptionsAsync(CancellationToken ct = default)
    {
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        var customers = await db.Customers.AsNoTracking().Where(c => !c.IsDeleted).OrderBy(c => c.FullName).Take(2000)
            .Select(c => new MaintenanceCustomerOptionDto(c.Id, c.Code, c.FullName)).ToListAsync(ct);
        var products = await db.Products.AsNoTracking().Where(p => !p.IsDeleted).OrderBy(p => p.Sku).Take(2000)
            .Select(p => new MaintenanceProductOptionDto(p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn)).ToListAsync(ct);
        return new MaintenanceNewJobOptionsDto(settings.CustomerJobs == MaintenanceSwitch.On, customers, products);
    }

    // ---------- the price ----------

    /// <param name="has">Whether the caller holds a key: decides whether the cost, the loss and the cost-plus proposal are shown.</param>
    public async Task<MaintenanceResult<MaintenanceJobPriceDto>> PriceAsync(Guid jobId, Func<string, bool> has, CancellationToken ct = default)
    {
        var job = await db.MaintenanceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound<MaintenanceJobPriceDto>();
        if (job.Kind != MaintenanceJobKind.Customer) return NotCustomer<MaintenanceJobPriceDto>();
        return MaintenanceResult<MaintenanceJobPriceDto>.Done(await PriceDtoAsync(job, has, ct));
    }

    public async Task<MaintenanceResult<MaintenanceJobPriceDto>> SetPriceAsync(
        Guid jobId, SetMaintenanceJobPriceRequest r, MaintenanceActor actor, Func<string, bool> has, CancellationToken ct = default)
    {
        if (r.Price <= 0 || r.Price > MaxPrice)
            return MaintenanceResult<MaintenanceJobPriceDto>.Fail(400, "invalid_price", "The price before tax is above zero.");
        if (TaxModeOf(r.TaxMode) is not { } taxChoice)
            return MaintenanceResult<MaintenanceJobPriceDto>.Fail(400, "invalid_tax_mode", "The tax mode is Taxed or Untaxed.");
        var price = regional.Get().RoundMoney(r.Price);

        await using (var scope = await db.BeginWriteScopeAsync(ct))
        {
            var job = await db.LockedAsync<MaintenanceJob>(jobId, ct);
            if (job is null) return NotFound<MaintenanceJobPriceDto>();
            if (job.Kind != MaintenanceJobKind.Customer) return NotCustomer<MaintenanceJobPriceDto>();
            if (job.ReworkOfJobId is not null) return ReworkFree<MaintenanceJobPriceDto>();
            if (job.Status is MaintenanceJobStatus.Invoiced or MaintenanceJobStatus.Cancelled || job.InvoiceId is not null)
                return MaintenanceResult<MaintenanceJobPriceDto>.Fail(409, "not_priceable", "An invoiced or cancelled job's price no longer changes.");

            var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
            var cost = await costing.FigureAsync(job.Id, has, ct);
            var proposal = MaintenancePricing.Propose(settings.CustomerPrice, await InputsAsync(job, settings, cost?.Total, ct), regional.Get().RoundMoney);
            var loss = MaintenancePricing.Loss(price, cost?.Total);
            if (loss is not null && settings.PriceBelowCost == MaintenanceBelowCost.NeedsConfirmation && !r.ConfirmBelowCost)
            {
                db.ChangeTracker.Clear();
                return MaintenanceResult<MaintenanceJobPriceDto>.Fail(409, BelowCostCode,
                    "This price is below the job's cost: confirm it to keep it (Maintenance › Settings, MS-25).");
            }

            var now = DateTime.UtcNow;
            job.PriceBasis = settings.CustomerPrice;
            job.Price = price;
            job.ProposedPrice = proposal.Proposed;
            job.CostAtPricing = cost?.Total;
            (job.PricedByUserId, job.PricedBy, job.PricedAtUtc) = (actor.UserId, actor.Name, now);
            var confirmed = loss is not null && r.ConfirmBelowCost;
            job.BelowCostConfirmedBy = confirmed ? actor.Name : null;
            job.BelowCostConfirmedAtUtc = confirmed ? now : null;
            if (taxChoice.Mode is TaxMode mode) job.TaxMode = mode;
            job.UpdatedAtUtc = now;
            // The price is no secret (plan §6.2); the cost and the loss never go into the history.
            db.MaintenanceJobEvents.Add(new MaintenanceJobEvent
            {
                JobId = job.Id, Kind = MaintenanceJobEventKind.Priced, UserId = actor.UserId, UserName = actor.Name,
                Detail = $"{price:0.##} ({settings.CustomerPrice}, {job.TaxMode})"
                    + (job.BelowCostConfirmedAtUtc is not null ? ", below cost confirmed" : ""),
            });
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
        }
        var saved = await db.MaintenanceJobs.AsNoTracking().FirstAsync(j => j.Id == jobId, ct);
        return MaintenanceResult<MaintenanceJobPriceDto>.Done(await PriceDtoAsync(saved, has, ct));
    }

    // ---------- the invoice ----------

    /// <summary>
    /// Raises the one invoice of a completed customer job. Refused: a job that is not a customer job, not completed, or
    /// already invoiced (409); with no price and no proposal to take (409 <c>price_required</c>); MS-34 on its own account
    /// while the service income slot names no active income account (409 <c>accounts_not_mapped</c>); under MS-25 "needs confirmation", a price below the
    /// cost no pricing holder confirmed at that cost (409 <c>price_below_cost</c>). Under "shown as a loss" it is raised
    /// and the loss is said in the warning.
    /// </summary>
    public async Task<MaintenanceResult<MaintenanceJobInvoiceDto>> InvoiceAsync(
        Guid jobId, RaiseMaintenanceJobInvoiceRequest r, MaintenanceActor actor, Func<string, bool> has, CancellationToken ct = default)
    {
        if (!has(PermissionCatalog.MaintenanceView))
            return MaintenanceResult<MaintenanceJobInvoiceDto>.Fail(403, "maintenance_view_required", "Raising a job's invoice also needs maintenance.view.");
        if (TaxModeOf(r.TaxMode) is not { } taxChoice)
            return MaintenanceResult<MaintenanceJobInvoiceDto>.Fail(400, "invalid_tax_mode", "The tax mode is Taxed or Untaxed.");

        Invoice invoice;
        string? warning;
        string account;
        await using (var scope = await db.BeginWriteScopeAsync(ct))
        {
            var job = await db.LockedAsync<MaintenanceJob>(jobId, ct);
            if (job is null) return NotFound<MaintenanceJobInvoiceDto>();
            if (job.Kind != MaintenanceJobKind.Customer) return NotCustomer<MaintenanceJobInvoiceDto>();
            if (job.ReworkOfJobId is not null) return ReworkFree<MaintenanceJobInvoiceDto>();
            if (job.Status == MaintenanceJobStatus.Invoiced || job.InvoiceId is not null)
                return Conflict<MaintenanceJobInvoiceDto>(AlreadyInvoicedCode, "This job has already been invoiced: one invoice per job.");
            if (job.Status != MaintenanceJobStatus.Completed)
                return Conflict<MaintenanceJobInvoiceDto>(NotCompletedCode, "Complete the job before invoicing it.");

            var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
            if (await IncomeAccountAsync(settings, ct) is not string income)
                return Conflict<MaintenanceJobInvoiceDto>(MaintenanceSettingsStore.AccountsNotMappedCode,
                    "Customer repair income on its own account (MS-34) needs the repair service income account (maintenance.service_income, an active income account of class 7) in the account map: the accountant names it in Maintenance › Settings.");
            account = income;

            var cost = await costing.FigureAsync(job.Id, has, ct);
            var basis = job.PriceBasis ?? settings.CustomerPrice;
            var proposal = job.Price is null
                ? MaintenancePricing.Propose(settings.CustomerPrice, await InputsAsync(job, settings, cost?.Total, ct), regional.Get().RoundMoney)
                : null;
            if ((job.Price ?? proposal?.Proposed) is not decimal price)
                return Conflict<MaintenanceJobInvoiceDto>(PriceRequiredCode, "Price the job before invoicing it (its Price card).");
            var loss = MaintenancePricing.Loss(price, cost?.Total);
            if (loss is not null && settings.PriceBelowCost == MaintenanceBelowCost.NeedsConfirmation
                && (job.BelowCostConfirmedAtUtc is null || job.CostAtPricing is not decimal confirmedAt || confirmedAt < cost!.Total))
                return Conflict<MaintenanceJobInvoiceDto>(BelowCostCode,
                    "The price is below the job's cost: a maintenance.pricing holder confirms it on the Price card first (MS-25).");

            var taxMode = taxChoice.Mode ?? job.TaxMode;
            var name = string.Join(" — ", new[] { job.Number, job.Equipment, job.ItemReference }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var lines = settings.CustomerIncome == MaintenanceIncomeAccount.PartsAndLabourApart
                ? await PartsAndLabourLinesAsync(job, settings, cost?.Total, price, name, income, ct)
                : [new MaintenanceJobBillLine(job.Number, name, price, income)];
            var made = await receivables.CreateMaintenanceJobInvoiceAsync(new MaintenanceJobBill(
                job.Id, job.CustomerId!.Value, job.BranchId, r.DueDateUtc, r.IssuedAtUtc, taxMode,
                taxMode == TaxMode.Taxed ? regional.Get().DefaultTaxRate : 0m, r.Notes, r.PaymentMethodKey,
                lines), actor.Name, ct);
            if (!made.Ok)
            {
                db.ChangeTracker.Clear();
                return made.Failure switch
                {
                    ReceivableFailure.NotFound => MaintenanceResult<MaintenanceJobInvoiceDto>.Fail(404, "not_found", made.Message!),
                    ReceivableFailure.Conflict => Conflict<MaintenanceJobInvoiceDto>("invoice_refused", made.Message!),
                    ReceivableFailure.Forbidden => MaintenanceResult<MaintenanceJobInvoiceDto>.Fail(403, made.Code ?? "forbidden", made.Message!),
                    _ => MaintenanceResult<MaintenanceJobInvoiceDto>.Fail(400, "invalid_invoice", made.Message!),
                };
            }
            invoice = made.Value!;
            warning = made.Warning;
            // MS-35 (M7b): the advances taken on 419 for this job clear against its invoice, dated with it. None: nothing.
            await receivables.ApplyJobAdvancesAsync(job.Id, invoice, actor.Name, ct);
            if (loss is not null) warning = string.Join(" ", new[] { warning, "The price is below the job's cost: the job shows a loss." }.Where(w => !string.IsNullOrWhiteSpace(w)));

            var now = DateTime.UtcNow;
            if (job.Price is null)
            {
                // The proposal taken as the price, frozen now as a typed one would be.
                (job.PriceBasis, job.Price, job.ProposedPrice, job.CostAtPricing) = (basis, price, proposal?.Proposed, cost?.Total);
                (job.PricedByUserId, job.PricedBy, job.PricedAtUtc) = (actor.UserId, actor.Name, now);
            }
            job.TaxMode = taxMode;
            job.InvoiceId = invoice.Id;
            job.Status = MaintenanceJobStatus.Invoiced;
            (job.InvoicedBy, job.InvoicedAtUtc, job.UpdatedAtUtc) = (actor.Name, now, now);

            // Phase M7c (MS-27, MS-37): a job completed before its guarantee was recorded takes it now; under the provision,
            // the share of the invoice net set aside for rework is frozen on the job — the practice ledger books it in the
            // invoice's month, takes reworks' costs from it and releases what is left when the guarantee ends.
            job.GuaranteeUntil ??= job.CompletedAtUtc is DateTime completed
                ? MaintenanceJobs.GuaranteeEnd(completed, settings.GuaranteeMonths, StoreDocumentHistory.Zone(regional))
                : null;
            // Only while the guarantee still runs on the invoice's day: an invoice raised after it ended sets nothing aside.
            var invoiceDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(invoice.IssuedAtUtc, DateTimeKind.Utc), StoreDocumentHistory.Zone(regional)));
            if (settings.Rework == MaintenanceRework.Provision && settings.ProvisionPercent > 0m
                && job.GuaranteeUntil is DateOnly until && until >= invoiceDay)
            {
                job.ProvisionPercent = settings.ProvisionPercent;
                job.ProvisionAmount = regional.Get().RoundMoney(invoice.SubTotalHt * settings.ProvisionPercent / 100m);
            }
            db.MaintenanceJobEvents.Add(new MaintenanceJobEvent
            {
                JobId = job.Id, Kind = MaintenanceJobEventKind.Invoiced, UserId = actor.UserId, UserName = actor.Name,
                Detail = $"{invoice.Number} {invoice.SubTotalHt:0.##} ({taxMode})"
                    + (job.ProvisionAmount is decimal provision ? $", guarantee provision {provision:0.##} ({job.ProvisionPercent:0.##} %)" : ""),
            });
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
        }

        var view = await jobs.GetAsync(jobId, ct);
        return MaintenanceResult<MaintenanceJobInvoiceDto>.Done(new MaintenanceJobInvoiceDto(
            invoice.Id, invoice.Number, invoice.SubTotalHt, invoice.TaxTotal, invoice.GrandTotalTtc, invoice.TaxMode.ToString(),
            account, warning, view.Value!));
    }

    /// <summary>
    /// MS-34: the income account a customer job's service is credited to. "Same as recharges" is the account the return
    /// note's repair recharge posts to today (<see cref="RentalPosting.AccountFor(RentalChargeKind)"/>: 707), read from that
    /// rule so the two never drift. "One service account" and "parts and labour apart" take the account the service income
    /// slot names (<see cref="MaintenanceAccountSlots.ServiceIncome"/>), while it is an active income account of class 7:
    /// null otherwise, never guessed.
    /// </summary>
    public async Task<string?> IncomeAccountAsync(MaintenanceSettings settings, CancellationToken ct = default) =>
        settings.CustomerIncome == MaintenanceIncomeAccount.SameAsRecharges
            ? RentalPosting.AccountFor(RentalChargeKind.Maintenance)
            : (await MaintenanceAccountMap.ServiceIncomeAsync(db, ct))?.Code;

    /// <summary>MS-34 "parts and labour apart": the account the parts line is credited to — the one sales of goods use (700).</summary>
    public const string PartsIncomeAccount = LedgerAccounts.Sales;

    /// <summary>
    /// The words a split invoice's lines start with, followed by " — " and the job's designation. Stored in French, as the
    /// other words the server writes on a document; the printed invoice and credit note say them in the paper's language
    /// (keys <c>print.line.maintenanceParts</c> and <c>print.line.maintenanceLabour</c>).
    /// </summary>
    public const string PartsLineWords = "Pièces";
    public const string LabourLineWords = "Main-d'œuvre et service";

    /// <summary>
    /// MS-34 "parts and labour apart": the parts line — the parts the job used (its MJI issues less its MJR returns) at their
    /// sale price before tax, on the account sales of goods use — and the labour and service line, the price less the parts,
    /// on the service income account. Parts above the price are capped at it; a line of 0 is left out. The lines add up to
    /// the price exactly, so the invoice's totals and its tax (on the rate's own total) are those of one line. No stock moves:
    /// the parts left with the job's own issue documents.
    /// </summary>
    private async Task<IReadOnlyList<MaintenanceJobBillLine>> PartsAndLabourLinesAsync(
        MaintenanceJob job, MaintenanceSettings settings, decimal? cost, decimal price, string name, string serviceAccount, CancellationToken ct)
    {
        var inputs = await InputsAsync(job, settings, cost, ct);
        var (parts, labour) = MaintenancePricing.SplitPartsAndLabour(price, inputs.Parts, regional.Get().RoundMoney);
        var lines = new List<MaintenanceJobBillLine>();
        if (parts > 0m) lines.Add(new MaintenanceJobBillLine(job.Number, $"{PartsLineWords} — {name}", parts, PartsIncomeAccount));
        if (labour > 0m) lines.Add(new MaintenanceJobBillLine(job.Number, $"{LabourLineWords} — {name}", labour, serviceAccount));
        return lines;
    }

    // ---------- helpers ----------

    /// <summary>What the basis is built from: the parts the job used at their sale price before tax, its hours, its outside work at cost.</summary>
    private async Task<MaintenancePriceInputs> InputsAsync(MaintenanceJob job, MaintenanceSettings settings, decimal? cost, CancellationToken ct)
    {
        var pricesIncludeTax = regional.Get().PricesIncludeTax;
        var parts = (await db.MaintenanceJobParts.AsNoTracking().Where(p => p.JobId == job.Id)
                .Select(p => new { p.ProductId, p.Kind, p.Qty, p.Product!.Price, p.Product.TaxRate }).ToListAsync(ct))
            .GroupBy(p => p.ProductId)
            .Select(g => new MaintenancePricedPart(g.Key,
                g.Sum(p => p.Kind == MaintenancePartKind.Issue ? p.Qty : -p.Qty),
                MaintenancePricing.NetOfTax(g.First().Price, g.First().TaxRate, pricesIncludeTax)))
            .Where(p => p.Qty != 0m).ToList();
        var jobId = (Guid?)job.Id;
        var hours = (await db.LaborBookings.AsNoTracking()
            .Where(b => b.OrderKind == LaborOrderKind.Maintenance && b.OrderId == jobId).Select(b => b.Hours).ToListAsync(ct)).Sum();
        var outside = await costing.OutsideWorkAtCostAsync(job.Id, ct);
        return new MaintenancePriceInputs(parts, hours, settings.LabourChargePerHour, outside, settings.MarkupPercent, cost);
    }

    private async Task<MaintenanceJobPriceDto> PriceDtoAsync(MaintenanceJob job, Func<string, bool> has, CancellationToken ct)
    {
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        var cost = await costing.FigureAsync(job.Id, has, ct);
        var shown = cost?.Shown == true;
        var inputs = await InputsAsync(job, settings, shown ? cost!.Total : null, ct);
        var proposal = MaintenancePricing.Propose(settings.CustomerPrice, inputs, regional.Get().RoundMoney);
        var loss = job.Price is decimal p ? MaintenancePricing.Loss(p, cost?.Total) : null;
        var invoiceNumber = job.InvoiceId is Guid id
            ? await db.Invoices.AsNoTracking().Where(i => i.Id == id).Select(i => i.Number).FirstOrDefaultAsync(ct)
            : null;
        return new MaintenanceJobPriceDto(
            job.Id, job.Number, job.Kind.ToString(), job.Status.ToString(),
            settings.CustomerPrice.ToString(), settings.LabourChargePerHour, settings.MarkupPercent, inputs.Hours,
            inputs.Parts.Sum(x => x.Qty), inputs.OutsideWork,
            proposal.Parts, proposal.Labour, proposal.Markup, proposal.CostBase, proposal.Proposed,
            shown, cost is not null, shown ? cost!.Total : null, shown ? cost!.Provisional : null,
            job.Price, job.PriceBasis?.ToString(), shown || job.PriceBasis != MaintenancePriceBasis.CostPlus ? job.ProposedPrice : null,
            job.PricedBy, job.PricedAtUtc,
            job.TaxMode.ToString(), regional.Get().DefaultTaxRate,
            settings.PriceBelowCost.ToString(), loss is not null, shown ? loss : null, job.BelowCostConfirmedBy, job.BelowCostConfirmedAtUtc,
            settings.CustomerIncome.ToString(), await IncomeAccountAsync(settings, ct) ?? "",
            job.InvoiceId, invoiceNumber);
    }

    /// <summary>The request's tax mode: <c>Mode</c> null when it names none (left as it is); null itself when it names neither.</summary>
    private static (TaxMode? Mode, bool _)? TaxModeOf(string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? (null, true)
        : string.Equals(requested.Trim(), nameof(TaxMode.Taxed), StringComparison.OrdinalIgnoreCase) ? (TaxMode.Taxed, true)
        : string.Equals(requested.Trim(), nameof(TaxMode.Untaxed), StringComparison.OrdinalIgnoreCase) ? (TaxMode.Untaxed, true)
        : null;

    private static MaintenanceResult<T> NotFound<T>() => MaintenanceResult<T>.Fail(404, "not_found", "Maintenance job not found.");

    private static MaintenanceResult<T> NotCustomer<T>() =>
        MaintenanceResult<T>.Fail(409, NotCustomerJobCode, "Only a customer job is priced and invoiced; an internal or recharged job earns no invoice.");

    private static MaintenanceResult<T> Conflict<T>(string code, string error) => MaintenanceResult<T>.Fail(409, code, error);

    public const string ReworkFreeCode = "rework_free";

    /// <summary>Phase M7c: a rework under guarantee is free — never priced, never invoiced (MS-37, MNT-09).</summary>
    private static MaintenanceResult<T> ReworkFree<T>() =>
        MaintenanceResult<T>.Fail(409, ReworkFreeCode, "A rework under guarantee is free: it is neither priced nor invoiced.");
}
