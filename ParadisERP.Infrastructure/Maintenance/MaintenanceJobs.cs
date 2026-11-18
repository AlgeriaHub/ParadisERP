using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Maintenance;

/// <summary>A job, or the status, code and reason it was refused.</summary>
public sealed record MaintenanceResult<T>(T? Value, int Status = 200, string? Code = null, string? Error = null)
{
    public bool Ok => Code is null;
    public static MaintenanceResult<T> Done(T value) => new(value);
    public static MaintenanceResult<T> Fail(int status, string code, string error) => new(default, status, code, error);
}

/// <summary>Who acts: the user id the token names and their name, as the movements and events keep them.</summary>
public readonly record struct MaintenanceActor(Guid? UserId, string? Name);

/// <summary>
/// Maintenance jobs, Phase M1 (docs/maintenance/plan.md §2.2): internal and recharged jobs over units already in the
/// maintenance state of the job's branch.
/// <list type="bullet">
/// <item><b>Open</b> (by hand, or a draft a return note opened under MS-03, then taken): moves nothing. The job holds its
/// units: a new job may take only what the branch has in maintenance of the product less what draft and open jobs
/// already hold, and a recharged job no more than its return-note line's repairable units less what other jobs cover.</item>
/// <item><b>Complete</b> (<c>maintenance.jobs.close</c>; under MS-05 "not the opener", someone other than whoever opened
/// it): repaired units move maintenance → available and units beyond repair maintenance → scrap, each through
/// <see cref="StockStateMoves"/> — the manual state route's own code — so the same movement is written and scrap posts the
/// same Dr 318 / Cr 310 at the product's cost price. Repaired back to available posts nothing, as today.</item>
/// <item><b>Cancel</b>: moves nothing; the units stay in maintenance and are free again.</item>
/// </list>
/// Every write takes the version lock first and reads the job and the figures it checks under it, so two completions of
/// one job, or two jobs taking the last free unit, cannot both pass. Branch scope is the API's (another branch's job
/// answers 404 before it reaches here). Whether the module is on is the API's too: every route but the settings
/// answers 409 <c>maintenance_off</c> while MS-01 is off.
/// </summary>
public sealed partial class MaintenanceJobs(PosDbContext db, LedgerPoster ledger, IRegionalSettingsProvider regional)
{
    public const decimal MaxQty = 1_000_000m;

    public const string OffCode = "maintenance_off";
    public const string NotYetCode = "maintenance_option_not_yet";
    public const string ExceedsMaintenanceCode = "exceeds_maintenance";
    public const string LineCoveredCode = "line_covered";
    public const string OwnJobCode = "own_job";

    /// <summary>The number of the next job: MJ- and six digits.</summary>
    public static string Number(int sequence) => $"MJ-{sequence:D6}";

    /// <summary>Draft and open jobs hold their units; completed and cancelled ones hold none.</summary>
    public static bool Holds(MaintenanceJobStatus status) => status is MaintenanceJobStatus.Draft or MaintenanceJobStatus.Open;

    // ---------- reading ----------

    public async Task<List<MaintenanceJobSummaryDto>> ListAsync(
        Guid? branchId, string? status, string? kind, Guid? productId, CancellationToken ct = default)
    {
        var query = db.MaintenanceJobs.AsNoTracking();
        if (branchId is Guid b) query = query.Where(j => j.BranchId == b);
        if (productId is Guid p) query = query.Where(j => j.ProductId == p);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<MaintenanceJobStatus>(status, true, out var s)) return [];
            query = query.Where(j => j.Status == s);
        }
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<MaintenanceJobKind>(kind, true, out var k)) return [];
            query = query.Where(j => j.Kind == k);
        }
        var jobs = await query.Include(j => j.Product).OrderByDescending(j => j.CreatedAtUtc).Take(500).ToListAsync(ct);
        var names = await NamesAsync(jobs, ct);
        return jobs.Select(j => Summary(j, names)).ToList();
    }

    public async Task<MaintenanceResult<MaintenanceJobDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var job = await db.MaintenanceJobs.AsNoTracking().Include(j => j.Product).FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return NotFound<MaintenanceJobDto>();
        var names = await NamesAsync([job], ct);
        var line = job.ReturnNoteLineId is Guid lineId
            ? await db.ReturnNoteLines.AsNoTracking().Where(l => l.Id == lineId)
                .Select(l => new { l.QtyPartialDamage, l.MaintenanceValue }).FirstOrDefaultAsync(ct)
            : null;
        var customer = job.ContractId is Guid contractId
            ? await db.RentalContracts.AsNoTracking().Where(c => c.Id == contractId)
                .Join(db.Customers, c => c.CustomerId, x => x.Id, (c, x) => x.FullName).FirstOrDefaultAsync(ct)
            : job.CustomerId is Guid customerId
                ? await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.FullName).FirstOrDefaultAsync(ct)
                : null;
        // A customer job's equipment is the customer's, never in the company's maintenance bucket.
        var inMaintenance = job.Kind == MaintenanceJobKind.Customer || job.ProductId is null ? 0m
            : await db.ProductStocks.AsNoTracking()
                .Where(s => s.ProductId == job.ProductId && s.BranchId == job.BranchId)
                .Select(s => (decimal?)s.MaintenanceQty).FirstOrDefaultAsync(ct) ?? 0m;
        var invoiceNumber = job.InvoiceId is Guid invoiceId
            ? await db.Invoices.AsNoTracking().Where(i => i.Id == invoiceId).Select(i => i.Number).FirstOrDefaultAsync(ct)
            : null;
        var events = await db.MaintenanceJobEvents.AsNoTracking().Where(e => e.JobId == id).OrderBy(e => e.AtUtc)
            .Select(e => new { e.Kind, e.Detail, e.UserName, e.AtUtc }).ToListAsync(ct);
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        // Phase M7c: the job a rework redoes, and the reworks opened against this one.
        var reworkOf = job.ReworkOfJobId is Guid originalId
            ? await db.MaintenanceJobs.AsNoTracking().Where(j => j.Id == originalId).Select(j => j.Number).FirstOrDefaultAsync(ct)
            : null;
        var reworks = await db.MaintenanceJobs.AsNoTracking().Where(j => j.ReworkOfJobId == id).OrderBy(j => j.CreatedAtUtc)
            .Select(j => new { j.Id, j.Number, j.Status, j.CreatedAtUtc }).ToListAsync(ct);
        var s = Summary(job, names);
        return MaintenanceResult<MaintenanceJobDto>.Done(new MaintenanceJobDto(
            s.Id, s.Number, s.BranchId, s.BranchName, s.Kind, s.Status,
            s.ProductId, s.ProductSku, s.ProductNameAr, s.ProductNameFr, s.ProductNameEn,
            s.Qty, s.RepairedQty, s.ScrappedQty, s.ItemReference,
            s.ContractId, s.ContractNumber, s.ReturnNoteId, s.ReturnNoteNumber, job.ReturnNoteLineId,
            customer, line?.QtyPartialDamage, line?.MaintenanceValue,
            job.Description, job.Diagnosis,
            job.CreatedBy, job.CreatedAtUtc, job.OpenedByUserId, job.OpenedBy, job.OpenedAtUtc,
            job.CompletedBy, job.CompletedAtUtc, job.CancelledBy, job.CancelledAtUtc, job.CancelReason,
            inMaintenance, settings.JobCloser.ToString(),
            events.Select(e => new MaintenanceJobEventDto(e.Kind.ToString(), e.Detail, e.UserName, e.AtUtc)).ToList(),
            job.CustomerId, job.Equipment, job.Price, job.TaxMode.ToString(), job.InvoiceId, invoiceNumber,
            job.ReworkOfJobId, reworkOf, job.GuaranteeUntil, job.ProvisionAmount,
            reworks.Select(x => new MaintenanceReworkRefDto(x.Id, x.Number, x.Status.ToString(), x.CreatedAtUtc)).ToList()));
    }

    /// <summary>
    /// Maintenance › Waiting for repair: per branch and product, what is in maintenance, what jobs hold and what is free;
    /// and every return-note line with repairable units that jobs do not yet cover in full. <paramref name="branchId"/> is
    /// the caller's own branch for everyone but the Admin (the API's CallersBranch); null reads every branch.
    /// </summary>
    public async Task<MaintenanceWaitingDto> WaitingAsync(Guid? branchId, CancellationToken ct = default)
    {
        var stocks = await db.ProductStocks.AsNoTracking()
            .Where(s => (branchId == null || s.BranchId == branchId) && s.MaintenanceQty > 0)
            .Select(s => new { s.BranchId, s.ProductId, s.MaintenanceQty }).ToListAsync(ct);
        var holding = await db.MaintenanceJobs.AsNoTracking()
            .Where(j => (branchId == null || j.BranchId == branchId) && j.Kind != MaintenanceJobKind.Customer && j.ProductId != null
                && (j.Status == MaintenanceJobStatus.Draft || j.Status == MaintenanceJobStatus.Open))
            .Select(j => new { j.BranchId, ProductId = j.ProductId!.Value, j.Qty }).ToListAsync(ct);
        var held = holding.GroupBy(j => (j.BranchId, j.ProductId)).ToDictionary(g => g.Key, g => g.Sum(j => j.Qty));

        // Return-note lines with repairable units, at the note's branch (where those units went into maintenance).
        var lines = await db.ReturnNoteLines.AsNoTracking()
            .Where(l => (branchId == null || l.ReturnNote!.BranchId == branchId) && l.QtyPartialDamage > 0)
            .Select(l => new
            {
                l.Id, l.ReturnNoteId, NoteNumber = l.ReturnNote!.Number, l.ReturnNote.ContractId, ContractNumber = l.ReturnNote.Contract!.Number,
                l.ReturnNote.Contract.CustomerId, l.ReturnNote.BranchId, l.ContractLine!.ProductId, l.ReturnNote.ReturnedAtUtc,
                l.QtyPartialDamage, l.MaintenanceValue,
            }).ToListAsync(ct);
        var lineIds = lines.Select(l => l.Id).ToList();
        var coverage = (await db.MaintenanceJobs.AsNoTracking()
                .Where(j => j.ReturnNoteLineId != null && lineIds.Contains(j.ReturnNoteLineId.Value) && j.Status != MaintenanceJobStatus.Cancelled)
                .Select(j => new { Line = j.ReturnNoteLineId!.Value, j.Qty }).ToListAsync(ct))
            .GroupBy(j => j.Line).ToDictionary(g => g.Key, g => g.Sum(j => j.Qty));
        var open = lines.Where(l => l.QtyPartialDamage > coverage.GetValueOrDefault(l.Id)).ToList();

        var productIds = stocks.Select(s => s.ProductId).Concat(open.Select(l => l.ProductId)).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn }).ToDictionaryAsync(p => p.Id, ct);
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);
        var customerIds = open.Select(l => l.CustomerId).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.FullName, ct);

        var buckets = stocks.Select(s =>
        {
            var p = products.GetValueOrDefault(s.ProductId);
            var h = held.GetValueOrDefault((s.BranchId, s.ProductId));
            return new MaintenanceBucketDto(s.BranchId, branches.GetValueOrDefault(s.BranchId) ?? "", s.ProductId,
                p?.Sku ?? "", p?.NameAr ?? "", p?.NameFr ?? "", p?.NameEn ?? "", s.MaintenanceQty, h, Math.Max(0m, s.MaintenanceQty - h));
        }).OrderBy(b => b.BranchName, StringComparer.Ordinal).ThenBy(b => b.Sku, StringComparer.Ordinal).ToList();

        var waiting = open.OrderBy(l => l.ReturnedAtUtc).Select(l =>
        {
            var p = products.GetValueOrDefault(l.ProductId);
            var covered = coverage.GetValueOrDefault(l.Id);
            return new MaintenanceWaitingLineDto(l.Id, l.ReturnNoteId, l.NoteNumber, l.ContractId, l.ContractNumber,
                customers.GetValueOrDefault(l.CustomerId), l.BranchId, branches.GetValueOrDefault(l.BranchId) ?? "", l.ProductId,
                p?.Sku ?? "", p?.NameAr ?? "", p?.NameFr ?? "", p?.NameEn ?? "", l.ReturnedAtUtc,
                l.QtyPartialDamage, covered, l.QtyPartialDamage - covered, l.MaintenanceValue);
        }).ToList();
        return new MaintenanceWaitingDto(buckets, waiting);
    }

    // ---------- writing ----------

    /// <summary>
    /// Opens a job by hand at <see cref="CreateMaintenanceJobRequest.BranchId"/> (the caller's own for a non-admin). A
    /// retried request answers the job it opened. Refused: a quantity the branch does not hold free in maintenance, more than
    /// a return-note line's repairable units not yet covered. A customer job (Phase M5a, MS-06) takes no units at all:
    /// <see cref="CreateCustomerJobAsync"/>.
    /// </summary>
    public async Task<MaintenanceResult<MaintenanceJobDto>> CreateAsync(
        CreateMaintenanceJobRequest r, MaintenanceActor actor, CancellationToken ct = default)
    {
        var requestId = Clean(r.ClientRequestId, 64);
        if (requestId is null || requestId.StartsWith("rn:", StringComparison.Ordinal))
            return Invalid<MaintenanceJobDto>("request_id_required", "Each request carries its own id.");
        if (!Enum.TryParse<MaintenanceJobKind>(r.Kind, true, out var kind) || !Enum.IsDefined(kind))
            return Invalid<MaintenanceJobDto>("invalid_kind", "A job is Internal, Recharged or Customer.");
        if (r.Qty <= 0 || r.Qty > MaxQty)
            return Invalid<MaintenanceJobDto>("invalid_quantity", "The quantity must be above zero.");
        if (r.BranchId is not Guid branchId)
            return Invalid<MaintenanceJobDto>("branch_required", "Name the branch where the units are in maintenance.");

        await using var scope = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);
        // Read under the lock, so a copy of the request sent alongside the first answers the job the first opened.
        if (await db.MaintenanceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.ClientRequestId == requestId, ct) is { } done)
            return await GetAsync(done.Id, ct);
        if (!await db.Branches.AnyAsync(b => b.Id == branchId && !b.IsDeleted, ct))
            return NotFound<MaintenanceJobDto>("Branch");

        if (kind == MaintenanceJobKind.Customer)
            return await CreateCustomerJobAsync(r, requestId, branchId, actor, scope, ct);

        Guid productId;
        Guid? contractId = null, noteId = null, lineId = null;
        if (kind == MaintenanceJobKind.Recharged)
        {
            if (r.ReturnNoteLineId is not Guid wanted)
                return Invalid<MaintenanceJobDto>("return_line_required", "A recharged job names the return-note line it repairs.");
            // Another branch's line is not found: its units went into maintenance at its own note's branch.
            var line = await db.ReturnNoteLines.AsNoTracking().Where(l => l.Id == wanted && l.ReturnNote!.BranchId == branchId)
                .Select(l => new { l.Id, l.ReturnNoteId, l.ReturnNote!.ContractId, l.ContractLine!.ProductId, l.QtyPartialDamage })
                .FirstOrDefaultAsync(ct);
            if (line is null) return NotFound<MaintenanceJobDto>("Return-note line");
            if (r.ProductId is Guid named && named != line.ProductId)
                return Invalid<MaintenanceJobDto>("product_mismatch", "The product is the return-note line's.");
            var covered = await CoveredAsync(line.Id, ct);
            if (r.Qty > line.QtyPartialDamage - covered)
                return MaintenanceResult<MaintenanceJobDto>.Fail(409, LineCoveredCode,
                    $"The line came back with {line.QtyPartialDamage:0.##} to repair and jobs already cover {covered:0.##}.");
            (productId, contractId, noteId, lineId) = (line.ProductId, line.ContractId, line.ReturnNoteId, line.Id);
        }
        else
        {
            if (r.ReturnNoteLineId is not null)
                return Invalid<MaintenanceJobDto>("return_line_not_internal", "An internal job names no return-note line; open a recharged job for one.");
            if (r.ProductId is not Guid named) return Invalid<MaintenanceJobDto>("product_required", "Name the product to repair.");
            if (!await db.Products.AnyAsync(p => p.Id == named, ct)) return NotFound<MaintenanceJobDto>("Product");
            productId = named;
        }

        // Phase M7d, MS-38 "per piece": a type tracked piece by piece is opened one named piece at a time.
        if (await PieceRefusalAsync(kind, productId, r.Qty, Clean(r.ItemReference, 120), await MaintenanceSettingsStore.CurrentAsync(db, ct), 400, ct)
            is { } piece) return piece;

        var (inMaintenance, held) = await BucketAsync(branchId, productId, ct);
        if (r.Qty > inMaintenance - held)
            return MaintenanceResult<MaintenanceJobDto>.Fail(409, ExceedsMaintenanceCode,
                $"{inMaintenance:0.##} is in maintenance at this branch and jobs already hold {held:0.##}, so {r.Qty:0.##} cannot be taken.");

        var now = DateTime.UtcNow;
        var job = new MaintenanceJob
        {
            Number = Number(await db.MaintenanceJobs.CountAsync(ct) + 1),
            BranchId = branchId, Kind = kind, Status = MaintenanceJobStatus.Open,
            ProductId = productId, Qty = r.Qty, ItemReference = Clean(r.ItemReference, 120), Description = Clean(r.Description, 1000),
            ContractId = contractId, ReturnNoteId = noteId, ReturnNoteLineId = lineId, ClientRequestId = requestId,
            CreatedByUserId = actor.UserId, CreatedBy = actor.Name, CreatedAtUtc = now,
            OpenedByUserId = actor.UserId, OpenedBy = actor.Name, OpenedAtUtc = now, UpdatedAtUtc = now,
        };
        db.MaintenanceJobs.Add(job);
        Event(job.Id, MaintenanceJobEventKind.Opened, $"{kind} {r.Qty:0.##}", actor);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return await GetAsync(job.Id, ct);
    }

    /// <summary>
    /// Phase M5a, MS-06: a job for an outside customer's equipment. Refused (400 <c>maintenance_option_not_yet</c>) while
    /// MS-06 is off. It names an existing customer and the equipment in words, optionally a catalogue product to say what
    /// it is, and how many pieces. <b>It takes nothing from the company's stock</b>: the equipment is the customer's, so no
    /// maintenance bucket is read or held and completing it moves no stock. Parts and hours go on it as on any job; its
    /// revenue is its own invoice (plan §3.2). Called under the caller's lock and scope.
    /// </summary>
    private async Task<MaintenanceResult<MaintenanceJobDto>> CreateCustomerJobAsync(
        CreateMaintenanceJobRequest r, string requestId, Guid branchId, MaintenanceActor actor, PosDbContext.WriteScope scope,
        CancellationToken ct)
    {
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        if (settings.CustomerJobs != MaintenanceSwitch.On)
            return MaintenanceResult<MaintenanceJobDto>.Fail(400, NotYetCode,
                "Jobs for outside customers are off (Maintenance › Settings, MS-06): open an internal or recharged job.");
        if (r.ReturnNoteLineId is not null)
            return Invalid<MaintenanceJobDto>("return_line_not_customer", "A customer job names no return-note line; its revenue is its own invoice.");
        if (r.CustomerId is not Guid customerId)
            return Invalid<MaintenanceJobDto>("customer_required", "Name the customer whose equipment is repaired.");
        if (!await db.Customers.AnyAsync(c => c.Id == customerId && !c.IsDeleted, ct))
            return NotFound<MaintenanceJobDto>("Customer");

        // Phase M7c (MS-27, MS-37): a free rework under guarantee names the completed customer job it redoes.
        MaintenanceJob? original = null;
        if (r.ReworkOfJobId is Guid originalId)
        {
            original = await db.MaintenanceJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == originalId && j.BranchId == branchId, ct);
            // Another branch's job is not found, as every job named by id.
            if (original is null) return NotFound<MaintenanceJobDto>("Original job");
            if (RefuseRework(original, customerId, settings) is { } refused) return refused;
        }

        if ((Clean(r.Equipment, 200) ?? original?.Equipment) is not string equipment)
            return Invalid<MaintenanceJobDto>("equipment_required", "Say in words what the customer's equipment is.");
        var productId = r.ProductId ?? original?.ProductId;
        if (r.ProductId is Guid named && !await db.Products.AnyAsync(p => p.Id == named && !p.IsDeleted, ct))
            return NotFound<MaintenanceJobDto>("Product");

        var now = DateTime.UtcNow;
        var job = new MaintenanceJob
        {
            Number = Number(await db.MaintenanceJobs.CountAsync(ct) + 1),
            BranchId = branchId, Kind = MaintenanceJobKind.Customer, Status = MaintenanceJobStatus.Open,
            ProductId = productId, Qty = r.Qty, CustomerId = customerId, Equipment = equipment,
            ItemReference = Clean(r.ItemReference, 120) ?? original?.ItemReference, Description = Clean(r.Description, 1000),
            ClientRequestId = requestId, TaxMode = TaxMode.Untaxed, ReworkOfJobId = original?.Id,
            CreatedByUserId = actor.UserId, CreatedBy = actor.Name, CreatedAtUtc = now,
            OpenedByUserId = actor.UserId, OpenedBy = actor.Name, OpenedAtUtc = now, UpdatedAtUtc = now,
        };
        db.MaintenanceJobs.Add(job);
        Event(job.Id, MaintenanceJobEventKind.Opened,
            $"Customer {r.Qty:0.##}" + (original is null ? "" : $", rework under guarantee of {original.Number}"), actor);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return await GetAsync(job.Id, ct);
    }

    /// <summary>A draft (opened by a return note, MS-03) is taken: it becomes open under whoever took it, who is then its opener for MS-05.</summary>
    public Task<MaintenanceResult<MaintenanceJobDto>> OpenAsync(Guid id, MaintenanceActor actor, CancellationToken ct = default) =>
        WriteAsync(id, ct, (job, _) =>
        {
            if (job.Status != MaintenanceJobStatus.Draft)
                return Task.FromResult<MaintenanceResult<MaintenanceJobDto>?>(Conflict("not_draft", "Only a draft job is opened."));
            job.Status = MaintenanceJobStatus.Open;
            (job.OpenedByUserId, job.OpenedBy, job.OpenedAtUtc) = (actor.UserId, actor.Name, DateTime.UtcNow);
            Event(job.Id, MaintenanceJobEventKind.Opened, null, actor);
            return Task.FromResult<MaintenanceResult<MaintenanceJobDto>?>(null);
        });

    /// <summary>The words of a draft or open job.</summary>
    public Task<MaintenanceResult<MaintenanceJobDto>> UpdateAsync(
        Guid id, UpdateMaintenanceJobRequest r, MaintenanceActor actor, CancellationToken ct = default) =>
        WriteAsync(id, ct, (job, _) =>
        {
            if (!Holds(job.Status))
                return Task.FromResult<MaintenanceResult<MaintenanceJobDto>?>(Conflict("not_live", "This job is already closed."));
            job.ItemReference = Clean(r.ItemReference, 120);
            job.Description = Clean(r.Description, 1000);
            job.Diagnosis = Clean(r.Diagnosis, 2000);
            // A customer job's equipment stays described: a blank leaves it as it was.
            if (job.Kind == MaintenanceJobKind.Customer && Clean(r.Equipment, 200) is string equipment) job.Equipment = equipment;
            Event(job.Id, MaintenanceJobEventKind.Edited, null, actor);
            return Task.FromResult<MaintenanceResult<MaintenanceJobDto>?>(null);
        });

    /// <summary>
    /// Completes an open job: <c>RepairedQty</c> maintenance → available and <c>ScrappedQty</c> maintenance → scrap, both
    /// through the manual state route's own code (<see cref="StockStateMoves"/>), in one transaction. Under MS-05 "not the
    /// opener", the person who opened the job cannot complete it. A second completion finds the job completed (409).
    /// </summary>
    public Task<MaintenanceResult<MaintenanceJobDto>> CompleteAsync(
        Guid id, CompleteMaintenanceJobRequest r, MaintenanceActor actor, CancellationToken ct = default) =>
        WriteAsync(id, ct, async (job, settings) =>
        {
            if (job.Status != MaintenanceJobStatus.Open)
                return Conflict(job.Status == MaintenanceJobStatus.Draft ? "not_open" : "not_live",
                    job.Status == MaintenanceJobStatus.Draft ? "Open the draft job before completing it." : "This job is already closed.");
            if (settings.JobCloser == MaintenanceJobCloser.NotTheOpener && job.OpenedByUserId is Guid opener && opener == actor.UserId)
                return MaintenanceResult<MaintenanceJobDto>.Fail(403, OwnJobCode,
                    "Someone other than the person who opened this job completes it (Maintenance › Settings, MS-05).");
            if (r.RepairedQty < 0 || r.ScrappedQty < 0 || r.RepairedQty + r.ScrappedQty != job.Qty)
                return Invalid<MaintenanceJobDto>("invalid_quantity",
                    $"Say what became of each of the job's {job.Qty:0.##} units: repaired and beyond repair together make {job.Qty:0.##}.");
            // Phase M7d, MS-38 "per piece": a type tracked piece by piece is completed one named piece at a time.
            if (await PieceRefusalAsync(job.Kind, job.ProductId, job.Qty, job.ItemReference, settings, 409, ct) is { } piece) return piece;

            var rows = new StockRows(db);
            // A customer job's pieces are the customer's (Phase M5a): repaired or beyond repair, they go back to the customer
            // and no company stock moves.
            var moves = job.Kind == MaintenanceJobKind.Customer || job.ProductId is null
                ? []
                : new[] { (To: StockState.Available, Qty: r.RepairedQty), (To: StockState.Scrap, Qty: r.ScrappedQty) };
            foreach (var (to, qty) in moves)
            {
                if (qty <= 0) continue;
                var moved = await StockStateMoves.MoveAsync(db, ledger, rows, job.ProductId!.Value, job.BranchId,
                    StockState.Maintenance, to, qty, reason: null, documentNumber: job.Number, actor.UserId, actor.Name, ct);
                if (!moved.Ok) return MaintenanceResult<MaintenanceJobDto>.Fail(moved.Status, "stock_state", moved.Error!);
            }
            job.Status = MaintenanceJobStatus.Completed;
            (job.RepairedQty, job.ScrappedQty) = (r.RepairedQty, r.ScrappedQty);
            if (Clean(r.Diagnosis, 2000) is string diagnosis) job.Diagnosis = diagnosis;
            var completedAt = DateTime.UtcNow;
            (job.CompletedByUserId, job.CompletedBy, job.CompletedAtUtc) = (actor.UserId, actor.Name, completedAt);
            // Phase M7c, MS-27: a customer job's guarantee is frozen now, from the day it is handed back (a rework carries none).
            if (job.Kind == MaintenanceJobKind.Customer && job.ReworkOfJobId is null)
                job.GuaranteeUntil = GuaranteeEnd(completedAt, settings.GuaranteeMonths, Zone);
            // Parts still at the job (M2) are what the repair used: completion is their consumption, and they move no more.
            var partsUsed = await MaintenanceJobParts.AtJobAsync(db, job.Id, ct);
            Event(job.Id, MaintenanceJobEventKind.Completed, $"repaired {r.RepairedQty:0.##}, beyond repair {r.ScrappedQty:0.##}"
                + (partsUsed > 0 ? $", parts used {partsUsed:0.######}" : "")
                + (job.GuaranteeUntil is DateOnly until
                    ? ", guaranteed until " + until.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : ""), actor);
            // Phase M7d, MS-30: a repair of own hire equipment above the amount joins the equipment's pool now, after the units
            // moved (so units beyond repair carry none of it). Off (the default) it reads the settings only and writes nothing.
            if (await new MaintenanceCapitalisation(db, regional).AtCompletionAsync(job, settings, completedAt, actor.Name, ct) is { } outcome)
                Event(job.Id, MaintenanceJobEventKind.Capitalisation, outcome.Detail, actor);
            return null;
        });

    /// <summary>
    /// Cancels a draft or open job, with the reason. Nothing moves: its units stay in maintenance, free for another job.
    /// Parts still at the job (M2) are returned first, or the request says they stay as used (plan §2.3); otherwise 409
    /// <c>parts_at_job</c>.
    /// </summary>
    public Task<MaintenanceResult<MaintenanceJobDto>> CancelAsync(
        Guid id, CancelMaintenanceJobRequest r, MaintenanceActor actor, CancellationToken ct = default) =>
        WriteAsync(id, ct, async (job, _) =>
        {
            if (!Holds(job.Status)) return Conflict("not_live", "This job is already closed.");
            if (Clean(r.Reason, 400) is not string reason)
                return Invalid<MaintenanceJobDto>("reason_required", "Say why the job is cancelled.");
            var atJob = await MaintenanceJobParts.AtJobAsync(db, job.Id, ct);
            if (atJob > 0 && !r.PartsUsed)
                return Conflict(MaintenanceJobParts.PartsAtJobCode,
                    $"{atJob:0.######} of the parts issued to this job are still at it: return them first, or say they stay as used.");
            job.Status = MaintenanceJobStatus.Cancelled;
            (job.CancelledByUserId, job.CancelledBy, job.CancelledAtUtc, job.CancelReason) = (actor.UserId, actor.Name, DateTime.UtcNow, reason);
            Event(job.Id, MaintenanceJobEventKind.Cancelled, atJob > 0 ? $"{reason} (parts kept as used: {atJob:0.######})" : reason, actor);
            return null;
        });

    // ---------- MS-03: draft jobs from a return note ----------

    /// <summary>
    /// MS-03 "a draft job for each damaged return line": called by the return-note writer inside its own transaction,
    /// after the note and its lines are saved and its units are in maintenance. With the module off or the default
    /// ("offered in Waiting for repair") it reads the settings row and does nothing else. Otherwise it adds one draft
    /// recharged job per line with repairable units, for all of them, at the note's branch; the caller saves. Never
    /// refuses: the return note is not held up by maintenance.
    /// </summary>
    public static async Task DraftForReturnNoteAsync(
        PosDbContext db, ReturnNote note, IReadOnlyList<RentalContractLine> contractLines, CancellationToken ct = default)
    {
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        if (settings is not { Module: MaintenanceSwitch.On, JobsFromReturnNotes: MaintenanceReturnNoteJobs.DraftJobPerDamagedLine }) return;

        var sequence = await db.MaintenanceJobs.CountAsync(ct);
        foreach (var line in note.Lines.Where(l => l.QtyPartialDamage > 0))
        {
            var contractLine = contractLines.FirstOrDefault(l => l.Id == line.ContractLineId);
            if (contractLine is null) continue;
            var job = new MaintenanceJob
            {
                Number = Number(++sequence), BranchId = note.BranchId, Kind = MaintenanceJobKind.Recharged,
                Status = MaintenanceJobStatus.Draft, ProductId = contractLine.ProductId, Qty = line.QtyPartialDamage,
                ContractId = note.ContractId, ReturnNoteId = note.Id, ReturnNoteLineId = line.Id,
                ClientRequestId = $"rn:{line.Id:N}", CreatedByUserId = note.UserId, CreatedBy = note.UserName,
            };
            db.MaintenanceJobs.Add(job);
            db.MaintenanceJobEvents.Add(new MaintenanceJobEvent
            {
                JobId = job.Id, Kind = MaintenanceJobEventKind.Drafted, Detail = note.Number, UserId = note.UserId, UserName = note.UserName,
            });
        }
    }

    // ---------- Phase M7c: rework under guarantee (MS-27, MS-37, MNT-09) ----------

    public const string ReworkNotCustomerJobCode = "rework_not_customer_job";
    public const string ReworkOfReworkCode = "rework_of_rework";
    public const string ReworkNotCompletedCode = "rework_not_completed";
    public const string ReworkOtherCustomerCode = "rework_other_customer";
    public const string OutsideGuaranteeCode = "outside_guarantee";

    /// <summary>
    /// The completed customer jobs of <paramref name="customerId"/> at <paramref name="branchId"/> (the caller's own for a
    /// non-admin) a free rework may still be opened against today: not reworks themselves, their guarantee running.
    /// </summary>
    public async Task<List<MaintenanceReworkCandidateDto>> ReworkCandidatesAsync(Guid? branchId, Guid customerId, CancellationToken ct = default)
    {
        var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
        var query = db.MaintenanceJobs.AsNoTracking().Where(j => j.Kind == MaintenanceJobKind.Customer && j.CustomerId == customerId
            && j.ReworkOfJobId == null && (j.Status == MaintenanceJobStatus.Completed || j.Status == MaintenanceJobStatus.Invoiced));
        if (branchId is Guid b) query = query.Where(j => j.BranchId == b);
        var today = Today();
        return (await query.OrderByDescending(j => j.CompletedAtUtc).Take(200).ToListAsync(ct))
            .Select(j => (Job: j, Until: GuaranteeOf(j, settings)))
            .Where(x => x.Until is DateOnly until && until >= today)
            .Select(x => new MaintenanceReworkCandidateDto(x.Job.Id, x.Job.Number, x.Job.Status.ToString(), x.Job.Equipment,
                x.Job.ItemReference, x.Job.ProductId, x.Job.CompletedAtUtc, x.Until!.Value))
            .ToList();
    }

    /// <summary>Why a rework cannot be opened against <paramref name="original"/>, or null when it can.</summary>
    private MaintenanceResult<MaintenanceJobDto>? RefuseRework(MaintenanceJob original, Guid customerId, MaintenanceSettings settings)
    {
        if (original.Kind != MaintenanceJobKind.Customer)
            return Conflict(ReworkNotCustomerJobCode, $"{original.Number} is not a customer job: a rework under guarantee redoes a customer's repair.");
        if (original.ReworkOfJobId is not null)
            return Conflict(ReworkOfReworkCode, $"{original.Number} is itself a rework: name the job it redoes, whose guarantee runs.");
        if (original.Status is not (MaintenanceJobStatus.Completed or MaintenanceJobStatus.Invoiced))
            return Conflict(ReworkNotCompletedCode, $"{original.Number} is not completed: a rework redoes a repair handed back.");
        if (original.CustomerId != customerId)
            return Conflict(ReworkOtherCustomerCode, $"{original.Number} is another customer's job: a rework is for the same customer.");
        if (GuaranteeOf(original, settings) is not DateOnly until || until < Today())
            return Conflict(OutsideGuaranteeCode, GuaranteeOf(original, settings) is DateOnly ended
                ? $"{original.Number}'s guarantee ended on {ended.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)}: open an ordinary customer job."
                : $"{original.Number} carries no guarantee (Maintenance › Settings, MS-27): open an ordinary customer job.");
        return null;
    }

    /// <summary>
    /// A customer job's guarantee: as frozen at its completion, or — a job completed before the guarantee was recorded —
    /// its completion day plus MS-27's months now. Null without a guarantee.
    /// </summary>
    public DateOnly? GuaranteeOf(MaintenanceJob job, MaintenanceSettings settings) =>
        job.GuaranteeUntil ?? (job.CompletedAtUtc is DateTime completed ? GuaranteeEnd(completed, settings.GuaranteeMonths, Zone) : null);

    /// <summary>MS-27: the last day of a guarantee given at <paramref name="completedAtUtc"/> for <paramref name="months"/> months (company days); none for 0.</summary>
    public static DateOnly? GuaranteeEnd(DateTime completedAtUtc, int months, TimeZoneInfo zone) => months <= 0 ? null
        : DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(completedAtUtc, DateTimeKind.Utc), zone)).AddMonths(months);

    /// <summary>The company's time zone (regional settings), never the server's or the browser's.</summary>
    private TimeZoneInfo Zone => StoreDocumentHistory.Zone(regional);

    /// <summary>The company's today, on the server's clock.</summary>
    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone));

    // ---------- Phase M7d: per-piece history (MS-38) ----------

    public const string PieceReferenceRequiredCode = "piece_reference_required";
    public const string OnePiecePerJobCode = "one_piece_per_job";

    /// <summary>
    /// Under MS-38 "per piece", an internal or recharged job on a type whose unit value is above the setting's value names the
    /// piece it repairs (its reference) and holds that one piece: refused (<paramref name="status"/>: 400 when opening, 409 when
    /// completing a draft a return note opened) otherwise. The default, and every other job, is never refused here.
    /// </summary>
    private async Task<MaintenanceResult<MaintenanceJobDto>?> PieceRefusalAsync(
        MaintenanceJobKind kind, Guid? productId, decimal qty, string? reference, MaintenanceSettings settings, int status, CancellationToken ct)
    {
        if (kind == MaintenanceJobKind.Customer || productId is not Guid id) return null;
        if (!(await MaintenancePieces.TrackedAsync(db, settings, [id], ct)).Contains(id)) return null;
        if (string.IsNullOrWhiteSpace(reference))
            return MaintenanceResult<MaintenanceJobDto>.Fail(status, PieceReferenceRequiredCode,
                "This equipment is tracked piece by piece (Maintenance › Settings, MS-38): give the piece's reference.");
        if (qty != 1m)
            return MaintenanceResult<MaintenanceJobDto>.Fail(status, OnePiecePerJobCode,
                "This equipment is tracked piece by piece (Maintenance › Settings, MS-38): a job repairs one piece; open one job per piece.");
        return null;
    }

    // ---------- helpers ----------

    /// <summary>
    /// The write's frame: the transaction, the version lock, the job read under it and the settings in force; the step;
    /// then the save and the commit. A step that answers a refusal leaves nothing written.
    /// </summary>
    private async Task<MaintenanceResult<MaintenanceJobDto>> WriteAsync(
        Guid id, CancellationToken ct, Func<MaintenanceJob, MaintenanceSettings, Task<MaintenanceResult<MaintenanceJobDto>?>> step)
    {
        await using (var scope = await db.BeginWriteScopeAsync(ct))
        {
            var job = await db.LockedAsync<MaintenanceJob>(id, ct);
            if (job is null) return NotFound<MaintenanceJobDto>();
            var settings = await MaintenanceSettingsStore.CurrentAsync(db, ct);
            if (await step(job, settings) is { } refusal)
            {
                // Nothing of a refused step is saved, here or by a later save in the same scope.
                db.ChangeTracker.Clear();
                return refusal;
            }
            job.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
        }
        return await GetAsync(id, ct);
    }

    /// <summary>What the branch has in maintenance of the product, and what draft and open jobs hold of it. Read under the caller's lock.</summary>
    private async Task<(decimal InMaintenance, decimal Held)> BucketAsync(Guid branchId, Guid productId, CancellationToken ct)
    {
        var inMaintenance = await db.ProductStocks.AsNoTracking()
            .Where(s => s.ProductId == productId && s.BranchId == branchId)
            .Select(s => (decimal?)s.MaintenanceQty).FirstOrDefaultAsync(ct) ?? 0m;
        // Summed in memory: SQLite stores decimal as text.
        var held = (await db.MaintenanceJobs.AsNoTracking()
                .Where(j => j.BranchId == branchId && j.ProductId == productId && j.Kind != MaintenanceJobKind.Customer
                    && (j.Status == MaintenanceJobStatus.Draft || j.Status == MaintenanceJobStatus.Open))
                .Select(j => j.Qty).ToListAsync(ct)).Sum();
        return (inMaintenance, held);
    }

    /// <summary>What jobs — any but cancelled ones — cover of a return-note line's repairable units.</summary>
    private async Task<decimal> CoveredAsync(Guid lineId, CancellationToken ct) =>
        (await db.MaintenanceJobs.AsNoTracking()
            .Where(j => j.ReturnNoteLineId == lineId && j.Status != MaintenanceJobStatus.Cancelled)
            .Select(j => j.Qty).ToListAsync(ct)).Sum();

    private sealed record Names(
        IReadOnlyDictionary<Guid, string> Branches, IReadOnlyDictionary<Guid, string> Contracts, IReadOnlyDictionary<Guid, string> Notes,
        IReadOnlyDictionary<Guid, string> Customers);

    private async Task<Names> NamesAsync(IReadOnlyCollection<MaintenanceJob> jobs, CancellationToken ct)
    {
        var branchIds = jobs.Select(j => j.BranchId).Distinct().ToList();
        var contractIds = jobs.Where(j => j.ContractId != null).Select(j => j.ContractId!.Value).Distinct().ToList();
        var noteIds = jobs.Where(j => j.ReturnNoteId != null).Select(j => j.ReturnNoteId!.Value).Distinct().ToList();
        var customerIds = jobs.Where(j => j.CustomerId != null).Select(j => j.CustomerId!.Value).Distinct().ToList();
        return new Names(
            await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.NameFr, ct),
            contractIds.Count == 0 ? new Dictionary<Guid, string>()
                : await db.RentalContracts.AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct),
            noteIds.Count == 0 ? new Dictionary<Guid, string>()
                : await db.ReturnNotes.AsNoTracking().Where(n => noteIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Number, ct),
            customerIds.Count == 0 ? new Dictionary<Guid, string>()
                : await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FullName, ct));
    }

    private static MaintenanceJobSummaryDto Summary(MaintenanceJob j, Names names) => new(
        j.Id, j.Number, j.BranchId, names.Branches.GetValueOrDefault(j.BranchId) ?? "", j.Kind.ToString(), j.Status.ToString(),
        j.ProductId, j.Product?.Sku ?? "", j.Product?.NameAr ?? "", j.Product?.NameFr ?? "", j.Product?.NameEn ?? "",
        j.Qty, j.RepairedQty, j.ScrappedQty, j.ItemReference,
        j.ContractId, j.ContractId is Guid c ? names.Contracts.GetValueOrDefault(c) : null,
        j.ReturnNoteId, j.ReturnNoteId is Guid n ? names.Notes.GetValueOrDefault(n) : null,
        j.CreatedAtUtc, j.CompletedAtUtc,
        j.Equipment, j.CustomerId, j.CustomerId is Guid x ? names.Customers.GetValueOrDefault(x) : null, j.InvoiceId, j.ReworkOfJobId);

    private void Event(Guid jobId, MaintenanceJobEventKind kind, string? detail, MaintenanceActor actor) =>
        db.MaintenanceJobEvents.Add(new MaintenanceJobEvent
        {
            JobId = jobId, Kind = kind, Detail = Clean(detail, 400), UserId = actor.UserId, UserName = actor.Name,
        });

    private static string? Clean(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length <= max ? t : t[..max];
    }

    private static MaintenanceResult<T> NotFound<T>(string what = "Maintenance job") =>
        MaintenanceResult<T>.Fail(404, "not_found", $"{what} not found.");

    private static MaintenanceResult<T> Invalid<T>(string code, string error) => MaintenanceResult<T>.Fail(400, code, error);

    private static MaintenanceResult<MaintenanceJobDto> Conflict(string code, string error) =>
        MaintenanceResult<MaintenanceJobDto>.Fail(409, code, error);
}
