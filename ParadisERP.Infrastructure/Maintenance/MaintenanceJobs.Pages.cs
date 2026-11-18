using ParadisERP.Application.Catalog;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Maintenance;

public sealed partial class MaintenanceJobs
{
    /// <summary>
    /// Maintenance › Jobs a page at a time (owner, 2026-11-17: "paginate in backend, not frontend"). The scope is
    /// <see cref="ListAsync"/>'s — <paramref name="branchId"/> is the caller's own branch for everyone but the Admin (the API's
    /// CallersBranch), <paramref name="status"/>, <paramref name="kind"/> and <paramref name="productId"/> as there — newest
    /// first, every job rather than the 500 newest. The screen's filters run here over all of them: <paramref name="branch"/>
    /// (the Admin's choice), the day each job was opened on the company's clock (<paramref name="from"/>, <paramref name="to"/>),
    /// and the words (<paramref name="q"/>, folded as the till folds: number, product, piece, equipment, customer, note,
    /// contract). Counted and cut in SQL; with words, filtered on the server in memory and paged there. The summary offers the
    /// branches the scope holds, before any filter, for the branch filter.
    /// </summary>
    public async Task<PageResult<MaintenanceJobSummaryDto>> PageAsync(
        Guid? branchId, Guid? branch, string? status, string? kind, Guid? productId, string? q, DateOnly? from, DateOnly? to,
        int? skip, int? take, CancellationToken ct = default)
    {
        var scope = db.MaintenanceJobs.AsNoTracking();
        if (branchId is Guid b) scope = scope.Where(j => j.BranchId == b);

        // The branch filter's choices: the branches the reader's jobs belong to, before any filter (one for a branch reader).
        var held = await scope.Select(j => j.BranchId).Distinct().ToListAsync(ct);
        var branchNames = await db.Branches.AsNoTracking().Where(x => held.Contains(x.Id)).Select(x => new { x.Id, x.NameFr }).ToListAsync(ct);
        var summary = new MaintenanceBranchesPageSummary(MaintenanceBranchOptions.Of(branchNames.Select(x => (x.Id, x.NameFr))));

        var query = scope;
        if (branch is Guid one) query = query.Where(j => j.BranchId == one);
        if (productId is Guid p) query = query.Where(j => j.ProductId == p);
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<MaintenanceJobStatus>(status, true, out var s)) return ListPaging.Of<MaintenanceJobSummaryDto>([], skip, take, summary);
            query = query.Where(j => j.Status == s);
        }
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<MaintenanceJobKind>(kind, true, out var k)) return ListPaging.Of<MaintenanceJobSummaryDto>([], skip, take, summary);
            query = query.Where(j => j.Kind == k);
        }
        // The day opened, on the company's calendar: from the start of `from` to the start of the day after `to`.
        var zone = CompanyDays.Zone(regional.Get().TimeZone);
        if (from is DateOnly f)
        {
            var start = CompanyDays.StartUtc(f, zone);
            query = query.Where(j => j.CreatedAtUtc >= start);
        }
        if (to is DateOnly t)
        {
            var end = CompanyDays.StartUtc(t.AddDays(1), zone);
            query = query.Where(j => j.CreatedAtUtc < end);
        }
        var ordered = query.Include(j => j.Product).OrderByDescending(j => j.CreatedAtUtc).ThenBy(j => j.Id);

        var needle = ListText.Fold(q);
        if (needle.Length > 0)
        {
            // The words are folded as the till folds them (سقاله finds سقالة): not SQL, so read and matched here.
            var all = await ordered.ToListAsync(ct);
            var names = await NamesAsync(all, ct);
            var matched = all.Select(j => Summary(j, names)).Where(s => ListText.Fold(
                    $"{s.Number} {s.ProductSku} {s.ProductNameFr} {s.ProductNameAr} {s.ProductNameEn} {s.ItemReference ?? ""} {s.Equipment ?? ""} " +
                    $"{s.JobCustomerName ?? ""} {s.ReturnNoteNumber ?? ""} {s.ContractNumber ?? ""}")
                .Contains(needle, StringComparison.Ordinal)).ToList();
            return ListPaging.Of(matched, skip, take, summary);
        }

        return await ordered.ToPageAsync(skip, take, async rows =>
        {
            var names = await NamesAsync(rows, ct);
            return (IReadOnlyList<MaintenanceJobSummaryDto>)rows.Select(j => Summary(j, names)).ToList();
        }, ct, summary);
    }
}
