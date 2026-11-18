using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = AlgeriaPos.Application.Rentals;

namespace AlgeriaPos.Infrastructure.Rentals;

/// <summary>What the quantity statement is asked for. <see cref="To"/> is included; <see cref="Status"/> is open, goodsBack or closed, as of To.</summary>
public sealed record QuantityStatementQuery(
    Guid CustomerId, DateOnly From, DateOnly To, bool Current = false,
    IReadOnlyList<Guid>? ProductIds = null, Guid? ContractId = null, Guid? BranchId = null,
    Guid? SiteId = null, string? Status = null, string? Search = null);

/// <summary>
/// Loads one customer's contracts and hands them to <see cref="Engine.QuantityStatement"/>. Every
/// contract is read into the same engine input the settlement uses
/// (<see cref="RentalDocuments.EngineInput"/>), so the statement and the contract cannot price a
/// stay two ways.
/// </summary>
public sealed class CustomerQuantityStatements(
    PosDbContext db, RentalDocuments rentals, IRegionalSettingsProvider regionalSettings, TimeProvider time)
{
    public static readonly IReadOnlyList<string> Statuses = ["open", "goodsBack", "closed"];

    /// <summary>Today in the company's zone — the day the "current quantities" view is as of.</summary>
    public DateOnly Today() => CompanyDays.Today(time.GetUtcNow().UtcDateTime, rentals.CompanyZone());

    public async Task<QuantityStatementDto?> BuildAsync(QuantityStatementQuery q, CancellationToken ct = default)
    {
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == q.CustomerId, ct);
        if (customer is null) return null;
        var zone = rentals.CompanyZone();

        var query = db.RentalContracts.AsNoTracking()
            .Where(c => c.CustomerId == q.CustomerId && c.Status != RentalContractStatus.Cancelled);
        if (q.ContractId is Guid contractId) query = query.Where(c => c.Id == contractId);
        // Set to the caller's own branch for everyone but the Admin (CallersBranch).
        if (q.BranchId is Guid branchId) query = query.Where(c => c.BranchId == branchId);
        if (q.SiteId is Guid siteId) query = query.Where(c => c.SiteId == siteId);
        var contracts = await query.OrderBy(c => c.SignedAtUtc).ToListAsync(ct);
        if (q.Status is string status)
            contracts = contracts
                .Where(c => Engine.QuantityStatement.StatusAsOf(Day(c.GoodsBackAtUtc, zone), Day(c.ClosedAtUtc, zone), q.To) == status)
                .ToList();

        var ids = contracts.Select(c => c.Id).ToList();
        var lines = await db.RentalContractLines.AsNoTracking()
            .Where(l => ids.Contains(l.ContractId)).OrderBy(l => l.SortOrder).ToListAsync(ct);
        // Loaded in the order the engine consumes them, as RentalDocuments loads one contract.
        var issues = await db.IssueNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => ids.Contains(n.ContractId))
            .OrderBy(n => n.IssuedAtUtc).ThenBy(n => n.CreatedAtUtc).ToListAsync(ct);
        var returns = await db.ReturnNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => ids.Contains(n.ContractId))
            .OrderBy(n => n.ReturnedAtUtc).ThenBy(n => n.CreatedAtUtc).ToListAsync(ct);
        var extensions = await db.ContractExtensions.AsNoTracking()
            .Where(e => ids.Contains(e.ContractId))
            .OrderBy(e => e.RaisedOnUtc).ThenBy(e => e.CreatedAtUtc).ToListAsync(ct);
        var cutoff = await db.Stores.AsNoTracking().Where(s => !s.IsDeleted)
            .Select(s => s.RentalDayCutoffHour).FirstOrDefaultAsync(ct);
        var pending = (await db.RentalDailyPricingCorrections.AsNoTracking()
            .Where(x => x.CompletedAtUtc == null && ids.Contains(x.ContractId))
            .Select(x => x.ContractId).ToListAsync(ct)).ToHashSet();

        var productIds = lines.Select(l => l.ProductId).Distinct().ToList();
        // The barcode is the product's current one: a contract line keeps no barcode of its own.
        var barcodes = await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Barcode, ct);
        var siteIds = contracts.Where(c => c.SiteId != null).Select(c => c.SiteId!.Value).Distinct().ToList();
        var sites = await db.CustomerSites.AsNoTracking().Where(s => siteIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var built = new List<Engine.QuantityContract>(contracts.Count);
        foreach (var c in contracts)
        {
            var docs = new RentalDocuments.Documents(
                c,
                lines.Where(l => l.ContractId == c.Id).ToList(),
                issues.Where(n => n.ContractId == c.Id).ToList(),
                returns.Where(n => n.ContractId == c.Id).ToList(),
                extensions.Where(e => e.ContractId == c.Id).ToList(),
                cutoff);
            var meta = docs.Lines.Select(l => new Engine.QuantityLine(
                l.Id, l.ProductId, l.Sku, l.NameSnapshot, l.NameArSnapshot, barcodes.GetValueOrDefault(l.ProductId))).ToList();
            // Quantities count by each note's real moment in the company's zone — not by the
            // period start the pricing counts from (a delivery after the cutoff starts its period
            // the next day, but the goods left that day).
            var moves = docs.IssueNotes.SelectMany(n => n.Lines.Select(l => new Engine.QuantityMove(
                    n.Id, n.Number, l.ContractLineId, n.IssuedAtUtc, Engine.RentalPricing.LocalDay(n.IssuedAtUtc, zone),
                    n.BranchId, true, n.IsCarryOver, l.Qty, 0, 0)))
                .Concat(docs.ReturnNotes.SelectMany(n => n.Lines.Select(l => new Engine.QuantityMove(
                    n.Id, n.Number, l.ContractLineId, n.ReturnedAtUtc, Engine.RentalPricing.LocalDay(n.ReturnedAtUtc, zone),
                    n.BranchId, false, n.IsCarryOver, 0, l.QtyOk + l.QtyPartialDamage, l.QtyTotalDamage + l.QtyMissing))))
                .ToList();
            built.Add(new Engine.QuantityContract(
                c.Id, c.Number, c.BranchId, c.SiteId,
                c.SiteName ?? (c.SiteId is Guid site ? sites.GetValueOrDefault(site) : null),
                Day(c.GoodsBackAtUtc, zone), Day(c.ClosedAtUtc, zone), pending.Contains(c.Id),
                rentals.EngineInput(docs), meta, moves));
        }

        // The note about paper contracts keyed in as money only reads the customer's very first
        // issue note, whatever the filters.
        var firstIssueAt = await db.IssueNotes.AsNoTracking()
            .Where(n => !n.IsCarryOver && n.Contract!.CustomerId == q.CustomerId
                && n.Contract.Status != RentalContractStatus.Cancelled)
            .OrderBy(n => n.IssuedAtUtc).Select(n => (DateTime?)n.IssuedAtUtc).FirstOrDefaultAsync(ct);

        var wanted = q.ProductIds is { Count: > 0 } ? q.ProductIds.ToHashSet() : null;
        var search = string.IsNullOrWhiteSpace(q.Search) ? null : q.Search.Trim();
        Func<Engine.QuantityLine, bool>? filter = wanted is null && search is null
            ? null
            : l => (wanted is null || wanted.Contains(l.ProductId)) && (search is null || Matches(l, search));

        var header = new Engine.QuantityStatementHeader(
            customer.Id, customer.Code, customer.FullName, q.Current,
            firstIssueAt is DateTime at ? Engine.RentalPricing.LocalDay(at, zone) : null);
        return Engine.QuantityStatement.Build(header, built, q.From, q.To, regionalSettings.Get(), filter);
    }

    private static bool Matches(Engine.QuantityLine line, string search) =>
        new[] { line.Sku, line.Name, line.NameAr, line.Barcode }
            .Any(v => v is not null && v.Contains(search, StringComparison.OrdinalIgnoreCase));

    private static DateOnly? Day(DateTime? utc, TimeZoneInfo zone) =>
        utc is DateTime at ? Engine.RentalPricing.LocalDay(at, zone) : null;
}
