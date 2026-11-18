using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Manufacturing;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Receivables;
using ParadisERP.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ParadisERP.Infrastructure.Maintenance;

/// <summary>
/// Parts and outside work on a maintenance job, Phase M2 (docs/maintenance/plan.md §2.3, §2.4).
/// <list type="bullet">
/// <item><b>Issue</b> (<c>MJI-000001</c>): parts leave the job's branch stock to an open job. Refused beyond what the
/// branch holds less what live production orders there still claim (the production issue's own rule). One
/// <see cref="StockMovement"/> per product, kind <see cref="StockMovementKind.MaintenanceIssue"/>, valued by the cost method
/// in force: the product's cost price, or — once sales are costed at the weighted average (C-01) — the average of the
/// moment through <see cref="LiveSaleCosting.MovementCostsOrFallbackAsync"/>, exactly as every other stock document;
/// never refused for a cost, the cost price is the fallback and <see cref="StockMovement.CostBasis"/> says so.</item>
/// <item><b>Return</b> (<c>MJR-000001</c>): unused parts back into the branch stock, never more than is at the job, at the
/// average the job's issues of the product went out at — goods come back at the cost they left at, as a production return
/// and a credit note's goods do. Kind <see cref="StockMovementKind.MaintenanceReturn"/>.</item>
/// <item><b>Outside work</b>: an amount with an optional expense payment voucher; the voucher's linked amounts, on
/// production orders and jobs together, never exceed it (<see cref="VoucherExpenseLinks"/>).</item>
/// </list>
/// <para>
/// <b>Posting.</b> MS-32's default leaves parts to the practice ledger, posted by month by the accountant (M6): these documents
/// write no <c>LedgerEntries</c> row. Under "at issue" (M6) each document posts at once through <see cref="MaintenancePartsAtIssue"/>,
/// in the same transaction, when every slot it uses names an account. Outside work never posts: the voucher already did.
/// </para>
/// <para>
/// Every write runs in one transaction that takes the version lock before it reads a job, a stock figure or a voucher's
/// links, and is retried safely by its request id. Branch scope and the module switch are the API's.
/// </para>
/// </summary>
public sealed class MaintenanceJobParts(PosDbContext db, IRegionalSettingsProvider regional, ILogger<MaintenanceJobParts>? log = null)
{
    /// <summary>Stock reasons, short as the column wants (StockMovement.ReasonMaxLength).</summary>
    public const string IssueReason = "maint-issue";
    public const string ReturnReason = "maint-return";

    public const string PartsAtJobCode = "parts_at_job";

    // ---------- reading ----------

    /// <summary>The job's parts (per product and per document line) and its outside work.</summary>
    public async Task<MaintenanceResult<MaintenanceJobWorkDto>> WorkAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.MaintenanceJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => new { j.Id, j.Status }).FirstOrDefaultAsync(ct);
        if (job is null) return NotFound<MaintenanceJobWorkDto>();
        var parts = await db.MaintenanceJobParts.AsNoTracking().Where(p => p.JobId == jobId).Include(p => p.Product)
            .OrderBy(p => p.AtUtc).ThenBy(p => p.DocumentNumber).ToListAsync(ct);
        var balances = parts.GroupBy(p => p.ProductId).Select(g =>
        {
            var product = g.First().Product;
            var issued = g.Where(p => p.Kind == MaintenancePartKind.Issue).ToList();
            var returned = g.Where(p => p.Kind == MaintenancePartKind.Return).ToList();
            var issuedQty = issued.Sum(p => p.Qty);
            var returnedQty = returned.Sum(p => p.Qty);
            return new MaintenancePartBalanceDto(g.Key, product?.Sku ?? "", product?.NameAr ?? "", product?.NameFr ?? "", product?.NameEn ?? "",
                issuedQty, returnedQty, issuedQty - returnedQty,
                issuedQty > 0 ? Math.Round(issued.Sum(p => p.Value) / issuedQty, 10, MidpointRounding.AwayFromZero) : 0m,
                Money(issued.Sum(p => p.Value) - returned.Sum(p => p.Value)));
        }).OrderBy(b => b.Sku, StringComparer.Ordinal).ToList();
        var movements = parts.Select(p => new MaintenancePartMovementDto(p.DocumentId, p.DocumentNumber, p.Kind.ToString(), p.ProductId,
            p.Product?.Sku ?? "", p.Qty, p.UnitCost, Money(p.Value), p.CostBasis.ToString(), p.Note, p.UserName, p.AtUtc)).ToList();

        var expenses = await db.MaintenanceJobExpenses.AsNoTracking().Where(e => e.JobId == jobId).OrderBy(e => e.AtUtc).ToListAsync(ct);
        var voucherIds = expenses.Where(e => e.PaymentVoucherId is not null).Select(e => e.PaymentVoucherId!.Value).Distinct().ToList();
        var vouchers = voucherIds.Count == 0 ? new Dictionary<Guid, (string Number, PaymentVoucherStatus Status)>()
            : (await db.PaymentVouchers.AsNoTracking().Where(v => voucherIds.Contains(v.Id))
                .Select(v => new { v.Id, v.Number, v.Status }).ToListAsync(ct)).ToDictionary(v => v.Id, v => (v.Number, v.Status));
        var expenseRows = expenses.Select(e =>
        {
            var found = e.PaymentVoucherId is Guid id && vouchers.TryGetValue(id, out var v) ? v : ((string Number, PaymentVoucherStatus Status)?)null;
            return new MaintenanceJobExpenseDto(e.Id, e.Description, e.Amount, e.PaymentVoucherId, found?.Number, found?.Status.ToString(), e.UserName, e.AtUtc);
        }).ToList();

        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        return MaintenanceResult<MaintenanceJobWorkDto>.Done(new MaintenanceJobWorkDto(
            job.Id, job.Status.ToString(), balances, movements, expenseRows,
            balances.Sum(b => b.Value), balances.Sum(b => b.AtJobQty), expenses.Sum(e => e.Amount), settings.PartsPosting.ToString()));
    }

    /// <summary>
    /// What the job's cards offer: the stocked products its branch can issue now (on hand less what live production orders
    /// there still claim), and the expense vouchers of its branch with something not yet linked.
    /// </summary>
    public async Task<MaintenanceResult<MaintenanceWorkOptionsDto>> OptionsAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await db.MaintenanceJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => new { j.BranchId }).FirstOrDefaultAsync(ct);
        if (job is null) return NotFound<MaintenanceWorkOptionsDto>();

        var stocks = await db.ProductStocks.AsNoTracking()
            .Where(s => s.BranchId == job.BranchId && s.Product!.TrackStock && !s.Product.IsDeleted)
            .Select(s => new { s.ProductId, s.StockQty, s.Product!.Sku, s.Product.Barcode, s.Product.NameAr, s.Product.NameFr, s.Product.NameEn })
            .ToListAsync(ct);
        var reserved = await Production.ReservedAtAsync(db, job.BranchId, null, null, ct);
        var products = stocks
            .Select(s => new MaintenancePartOptionDto(s.ProductId, s.Sku, s.Barcode, s.NameAr, s.NameFr, s.NameEn,
                s.StockQty - reserved.GetValueOrDefault(s.ProductId)))
            .Where(p => p.Available > 0).OrderBy(p => p.Sku, StringComparer.Ordinal).ToList();

        var candidates = await db.PaymentVouchers.AsNoTracking()
            .Where(v => v.BranchId == job.BranchId && v.Kind == PaymentVoucherKind.Expense && v.Status != PaymentVoucherStatus.Cancelled)
            .OrderByDescending(v => v.CreatedAtUtc).Take(500)
            .Select(v => new { v.Id, v.Number, v.Status, v.ExpenseCategory, v.PayeeName, v.Purpose, v.Amount, v.CreatedAtUtc })
            .ToListAsync(ct);
        var linked = await VoucherExpenseLinks.LinkedAsync(db, candidates.Select(v => v.Id).ToList(), ct);
        var vouchers = candidates
            .Select(v => new MaintenanceVoucherOptionDto(v.Id, v.Number, v.Status.ToString(), v.ExpenseCategory, v.PayeeName, v.Purpose,
                v.Amount, linked.GetValueOrDefault(v.Id), v.Amount - linked.GetValueOrDefault(v.Id), v.CreatedAtUtc))
            .Where(v => v.Free > 0).Take(200).ToList();
        return MaintenanceResult<MaintenanceWorkOptionsDto>.Done(new MaintenanceWorkOptionsDto(products, vouchers));
    }

    /// <summary>What is still at a job, all products together: issued less returned. Read under the caller's lock.</summary>
    public static async Task<decimal> AtJobAsync(PosDbContext db, Guid jobId, CancellationToken ct = default)
    {
        var lines = await db.MaintenanceJobParts.AsNoTracking().Where(p => p.JobId == jobId)
            .Select(p => new { p.Kind, p.Qty }).ToListAsync(ct);
        return lines.Sum(l => l.Kind == MaintenancePartKind.Issue ? l.Qty : -l.Qty);
    }

    // ---------- issue and return ----------

    public async Task<MaintenanceResult<MaintenanceJobWorkDto>> MoveAsync(
        Guid jobId, MaintenancePartsRequest r, MaintenanceActor actor, CancellationToken ct = default)
    {
        if (Clean(r.ClientRequestId, 64) is not string requestId)
            return Invalid<MaintenanceJobWorkDto>("request_id_required", "Each request carries its own id.");
        if (!Enum.TryParse<MaintenancePartKind>(r.Kind, true, out var kind) || !Enum.IsDefined(kind))
            return Invalid<MaintenanceJobWorkDto>("invalid_kind", "Parts are issued to the job or returned from it.");
        var wanted = r.Lines ?? [];
        if (wanted.Count == 0 || wanted.Count > 50)
            return Invalid<MaintenanceJobWorkDto>("lines_required", "Name at least one part.");
        if (wanted.Any(l => l.Qty <= 0 || l.Qty > MaintenanceJobs.MaxQty))
            return Invalid<MaintenanceJobWorkDto>("invalid_quantity", "Each quantity must be above zero.");
        // One line per product: the same product named twice in a request is one movement of the sum.
        var asked = wanted.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => Production.Round(g.Sum(l => l.Qty)));
        if (asked.Values.Any(q => q <= 0 || q > MaintenanceJobs.MaxQty))
            return Invalid<MaintenanceJobWorkDto>("invalid_quantity", "Each quantity must be above zero.");

        await using (var scope = await db.BeginWriteScopeAsync(ct))
        {
            await db.LockVersionsAsync(ct);
            // Read under the lock, so a copy of the request sent alongside the first finds the first's document.
            var usedBy = await db.MaintenanceJobParts.AsNoTracking().Where(p => p.ClientRequestId == requestId)
                .Select(p => (Guid?)p.JobId).FirstOrDefaultAsync(ct);
            if (usedBy is Guid owner)
                return owner == jobId ? await WorkAsync(jobId, ct)
                    : Conflict<MaintenanceJobWorkDto>("request_id_reused", "This request id belongs to another job.");

            var job = await db.MaintenanceJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null) return NotFound<MaintenanceJobWorkDto>();
            if (job.Status != MaintenanceJobStatus.Open)
                return job.Status == MaintenanceJobStatus.Draft
                    ? Conflict<MaintenanceJobWorkDto>("not_open", "Open the draft job before parts move on it.")
                    : Conflict<MaintenanceJobWorkDto>("not_live", "This job is already closed: its parts no longer move.");

            var ids = asked.Keys.ToList();
            var products = await db.Products.Where(p => ids.Contains(p.Id) && !p.IsDeleted).ToDictionaryAsync(p => p.Id, ct);
            if (ids.Any(id => !products.ContainsKey(id))) return NotFound<MaintenanceJobWorkDto>("Product");
            if (products.Values.FirstOrDefault(p => !p.TrackStock) is { } untracked)
                return Invalid<MaintenanceJobWorkDto>("not_stocked", $"{untracked.Sku}: this product keeps no stock, so it cannot be issued as a part.");

            var rows = new StockRows(db);
            var history = await db.MaintenanceJobParts.AsNoTracking().Where(p => p.JobId == jobId && ids.Contains(p.ProductId))
                .Select(p => new { p.ProductId, p.Kind, p.Qty, p.Value, p.CostBasis }).ToListAsync(ct);
            if (kind == MaintenancePartKind.Issue)
            {
                // Never fictitious stock: the branch must hold it, and a live production order's claim there is not taken.
                var reserved = await Production.ReservedAtAsync(db, job.BranchId, ids, null, ct);
                foreach (var (productId, qty) in asked)
                {
                    var product = products[productId];
                    var onHand = (await rows.FindAsync(productId, job.BranchId, ct))?.StockQty ?? 0m;
                    if (qty > onHand)
                        return Conflict<MaintenanceJobWorkDto>("insufficient_stock",
                            $"{product.Sku}: {onHand:0.######} at this branch, {qty:0.######} asked.");
                    var free = onHand - reserved.GetValueOrDefault(productId);
                    if (qty > free)
                        return Conflict<MaintenanceJobWorkDto>("reserved_by_production",
                            $"{product.Sku}: {Math.Max(0m, free):0.######} is free; the rest is reserved for production orders at this branch.");
                }
            }
            else
            {
                foreach (var (productId, qty) in asked)
                {
                    var mine = history.Where(h => h.ProductId == productId).ToList();
                    var atJob = mine.Sum(h => h.Kind == MaintenancePartKind.Issue ? h.Qty : -h.Qty);
                    if (qty > atJob)
                        return Conflict<MaintenanceJobWorkDto>("over_balance",
                            $"{products[productId].Sku}: only {Math.Max(0m, atJob):0.######} is at this job.");
                }
            }

            var prefix = kind == MaintenancePartKind.Issue ? "MJI" : "MJR";
            var documents = await db.MaintenanceJobParts.AsNoTracking().Where(p => p.Kind == kind)
                .Select(p => p.DocumentId).Distinct().CountAsync(ct);
            var number = $"{prefix}-{documents + 1:D6}";
            var documentId = Guid.NewGuid();
            var note = Clean(r.Note, 400);
            var now = DateTime.UtcNow;

            // The movements first, not saved, so the issue can be valued as every stock document is (C-01).
            var planned = new List<(Product Product, decimal Qty, StockMovement Movement)>();
            foreach (var (productId, qty) in asked)
            {
                var product = products[productId];
                var delta = kind == MaintenancePartKind.Issue ? -qty : qty;
                var stock = await rows.GetAsync(productId, job.BranchId, ct);
                stock.StockQty += delta;
                planned.Add((product, qty, new StockMovement
                {
                    ProductId = productId, BranchId = job.BranchId, QtyDelta = delta, BalanceAfter = stock.StockQty,
                    Reason = kind == MaintenancePartKind.Issue ? IssueReason : ReturnReason,
                    Kind = kind == MaintenancePartKind.Issue ? StockMovementKind.MaintenanceIssue : StockMovementKind.MaintenanceReturn,
                    DocumentId = documentId, DocumentNumber = number, MovedQty = qty, UnitCost = product.CostPrice,
                    UserId = actor.UserId, UserName = actor.Name, CreatedAtUtc = now,
                }));
            }

            IReadOnlyDictionary<Guid, LiveMovementCost>? live = null;
            if (kind == MaintenancePartKind.Issue)
                live = await new LiveSaleCosting(db).MovementCostsOrFallbackAsync(
                    planned.Select(p => new PlannedMovement(p.Movement, p.Product.CostPrice)).ToList(), log, ct);

            var posted = new List<MaintenancePartsAtIssue.Line>();
            foreach (var (product, qty, movement) in planned)
            {
                decimal unitCost, value;
                SaleCostBasis basis;
                if (kind == MaintenancePartKind.Issue)
                {
                    // Today's rule while sales are costed at the cost price; the average of the moment once they are not.
                    (unitCost, value, basis) = live?.GetValueOrDefault(movement.Id) is { } cost
                        ? (cost.UnitCost, cost.Value, cost.Basis)
                        : (product.CostPrice, qty * product.CostPrice, SaleCostBasis.CostPrice);
                }
                else
                {
                    // Back at what the job's issues of the product went out at; where that came from, its weakest basis.
                    var issues = history.Where(h => h.ProductId == product.Id && h.Kind == MaintenancePartKind.Issue).ToList();
                    var returns = history.Where(h => h.ProductId == product.Id && h.Kind == MaintenancePartKind.Return).ToList();
                    var (issuedQty, issuedValue) = (issues.Sum(h => h.Qty), issues.Sum(h => h.Value));
                    unitCost = Math.Round(issuedValue / issuedQty, 10, MidpointRounding.AwayFromZero);
                    // The last units back take exactly what is left of the issues' value, so nothing is stranded by rounding.
                    value = qty == issuedQty - returns.Sum(h => h.Qty) ? issuedValue - returns.Sum(h => h.Value) : qty * unitCost;
                    basis = Weakest(issues.Select(h => h.CostBasis));
                }
                movement.UnitCost = unitCost;
                movement.CostBasis = basis;
                db.StockMovements.Add(movement);
                db.MaintenanceJobParts.Add(new MaintenanceJobPart
                {
                    JobId = job.Id, Kind = kind, DocumentId = documentId, DocumentNumber = number, ProductId = product.Id,
                    Qty = qty, UnitCost = unitCost, Value = value, CostBasis = basis, StockMovementId = movement.Id,
                    ClientRequestId = requestId, Note = note, UserId = actor.UserId, UserName = actor.Name, AtUtc = now,
                });
                posted.Add(new MaintenancePartsAtIssue.Line(product.ManufacturingRole, value));
                // A till shows the branch's stock of the product: its pull watches the product's version.
                await db.TouchAsync(product, ct);
            }

            // MS-32 "at issue" (M6): the document's entry posts now, in this transaction; by default nothing posts here and the
            // practice ledger carries it to the month the accountant posts.
            var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
            await MaintenancePartsAtIssue.PostAsync(db, settings, job, documentId, number, kind == MaintenancePartKind.Return,
                posted, actor.Name, now, ct);

            job.UpdatedAtUtc = now;
            db.MaintenanceJobEvents.Add(new MaintenanceJobEvent
            {
                JobId = job.Id, Kind = kind == MaintenancePartKind.Issue ? MaintenanceJobEventKind.PartsIssued : MaintenanceJobEventKind.PartsReturned,
                Detail = Clean($"{number} " + string.Join(", ", planned.Select(p => $"{p.Product.Sku} {p.Qty:0.######}")), 400),
                UserId = actor.UserId, UserName = actor.Name, AtUtc = now,
            });
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
        }
        return await WorkAsync(jobId, ct);
    }

    /// <summary>Where a return's cost came from: a fallback among its issues says fallback; then the cost price; else the average.</summary>
    private static SaleCostBasis Weakest(IEnumerable<SaleCostBasis> bases)
    {
        var all = bases.ToList();
        if (all.Contains(SaleCostBasis.CostPriceFallback)) return SaleCostBasis.CostPriceFallback;
        if (all.Count == 0 || all.Contains(SaleCostBasis.CostPrice)) return SaleCostBasis.CostPrice;
        return all.All(b => b == SaleCostBasis.WeightedAverage) ? SaleCostBasis.WeightedAverage : SaleCostBasis.CostPrice;
    }

    // ---------- outside work ----------

    /// <summary>
    /// Outside work on an open or completed job. A voucher must be an expense voucher the caller reaches (any for the
    /// company's roles, the job's branch for a branch-held caller), not cancelled, with enough of it not yet linked to
    /// production orders' and jobs' expenses. Posts nothing; a retried request adds nothing.
    /// </summary>
    public async Task<MaintenanceResult<MaintenanceJobWorkDto>> AddExpenseAsync(
        Guid jobId, MaintenanceJobExpenseRequest r, MaintenanceActor actor, bool companyScoped, CancellationToken ct = default)
    {
        if (Clean(r.ClientRequestId, 64) is not string requestId)
            return Invalid<MaintenanceJobWorkDto>("request_id_required", "Each request carries its own id.");
        if (Clean(r.Description, 300) is not string description)
            return Invalid<MaintenanceJobWorkDto>("invalid_input", "Describe the outside work.");
        if (r.Amount <= 0 || r.Amount > 1_000_000_000m)
            return Invalid<MaintenanceJobWorkDto>("invalid_amount", "The amount must be above zero.");
        var amount = regional.Get().RoundMoney(r.Amount);
        if (amount <= 0) return Invalid<MaintenanceJobWorkDto>("invalid_amount", "The amount must be above zero.");

        await using (var scope = await db.BeginWriteScopeAsync(ct))
        {
            await db.LockVersionsAsync(ct);
            var usedBy = await db.MaintenanceJobExpenses.AsNoTracking().Where(e => e.ClientRequestId == requestId)
                .Select(e => (Guid?)e.JobId).FirstOrDefaultAsync(ct);
            if (usedBy is Guid owner)
                return owner == jobId ? await WorkAsync(jobId, ct)
                    : Conflict<MaintenanceJobWorkDto>("request_id_reused", "This request id belongs to another job.");

            var job = await db.MaintenanceJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null) return NotFound<MaintenanceJobWorkDto>();
            if (job.Status == MaintenanceJobStatus.Draft)
                return Conflict<MaintenanceJobWorkDto>("not_open", "Open the draft job before recording outside work on it.");
            if (job.Status == MaintenanceJobStatus.Cancelled)
                return Conflict<MaintenanceJobWorkDto>("not_live", "This job is cancelled.");

            if (r.PaymentVoucherId is Guid voucherId)
            {
                // Another branch's voucher is not found for a caller held to their branch (the job's), as on the voucher routes.
                var voucher = await db.PaymentVouchers.AsNoTracking()
                    .Where(v => v.Id == voucherId && (companyScoped || v.BranchId == job.BranchId))
                    .Select(v => new { v.Amount, v.Kind, v.Status }).FirstOrDefaultAsync(ct);
                if (voucher is null) return NotFound<MaintenanceJobWorkDto>("Payment voucher");
                if (voucher.Kind != PaymentVoucherKind.Expense)
                    return Conflict<MaintenanceJobWorkDto>("voucher_not_expense", "Only an expense payment voucher can pay for outside work.");
                if (voucher.Status == PaymentVoucherStatus.Cancelled)
                    return Conflict<MaintenanceJobWorkDto>("voucher_cancelled", "A cancelled payment voucher cannot pay for new outside work.");
                // One cap over production orders' and maintenance jobs' expenses, read under the lock (plan §2.4).
                var linked = await VoucherExpenseLinks.LinkedAsync(db, voucherId, ct);
                if (linked + amount > voucher.Amount)
                    return Conflict<MaintenanceJobWorkDto>("voucher_amount_exceeded",
                        $"The voucher's amount is {voucher.Amount:0.###} and {linked:0.###} of it is already linked to orders' and maintenance jobs' expenses.");
            }

            var now = DateTime.UtcNow;
            db.MaintenanceJobExpenses.Add(new MaintenanceJobExpense
            {
                JobId = job.Id, Description = description, Amount = amount, PaymentVoucherId = r.PaymentVoucherId,
                ClientRequestId = requestId, UserId = actor.UserId, UserName = actor.Name, AtUtc = now,
            });
            job.UpdatedAtUtc = now;
            db.MaintenanceJobEvents.Add(new MaintenanceJobEvent
            {
                JobId = job.Id, Kind = MaintenanceJobEventKind.ExpenseRecorded, Detail = Clean($"{amount:0.###} — {description}", 400),
                UserId = actor.UserId, UserName = actor.Name, AtUtc = now,
            });
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
        }
        return await WorkAsync(jobId, ct);
    }

    // ---------- helpers ----------

    private decimal Money(decimal value) => regional.Get().RoundMoney(value);

    private static string? Clean(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length <= max ? t : t[..max];
    }

    private static MaintenanceResult<T> NotFound<T>(string what = "Maintenance job") =>
        MaintenanceResult<T>.Fail(404, "not_found", $"{what} not found.");

    private static MaintenanceResult<T> Invalid<T>(string code, string error) => MaintenanceResult<T>.Fail(400, code, error);

    private static MaintenanceResult<T> Conflict<T>(string code, string error) => MaintenanceResult<T>.Fail(409, code, error);
}
