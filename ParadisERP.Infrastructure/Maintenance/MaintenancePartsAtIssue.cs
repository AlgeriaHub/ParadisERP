using ParadisERP.Application.Maintenance;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Maintenance;

/// <summary>
/// MS-32 "at issue" (Phase M6, docs/maintenance/plan.md §5.2): a parts document of a job posts at once through
/// <see cref="LedgerPoster"/>, in the issue's own transaction — Dr <c>maintenance.own_parts</c> (or <c>maintenance.customer_parts</c>
/// for a customer job) / Cr the part's stock, per stock group, rounded once per group: exactly the entry the practice ledger would
/// have made for it (<see cref="MaintenancePracticeLedger.DocumentLines"/>), under source <c>maintenance-issue</c> and the
/// document's id. A document posted here is left out of the practice ledger, so it never posts twice.
/// <para>
/// Never guessed: when a slot the document uses names no account (the accountant cleared one after choosing "at issue"), nothing
/// posts and the document stays in the practice ledger like the default, which shows the slot "not mapped". The issue itself is
/// never refused for an account.
/// </para>
/// </summary>
public static class MaintenancePartsAtIssue
{
    public const string SourceType = "maintenance-issue";

    /// <summary>One line of the document: the part's role and the value it moved.</summary>
    public readonly record struct Line(ManufacturingRole Role, decimal Value);

    /// <returns>Whether the document posted (false: MS-32 is the default, or a slot it uses is not mapped).</returns>
    public static async Task<bool> PostAsync(
        PosDbContext db, MaintenanceSettings settings, MaintenanceJob job, Guid documentId, string number, bool isReturn,
        IEnumerable<Line> lines, string? by, DateTime atUtc, CancellationToken ct)
    {
        if (settings.PartsPosting != MaintenancePartsPosting.AtIssue) return false;
        var values = Values(lines);
        var practice = MaintenancePracticeLedger.DocumentLines(job.Kind == MaintenanceJobKind.Customer, isReturn, values);
        if (practice.Count == 0) return false;
        var accounts = await MaintenanceAccountMap.AccountsAsync(db, ct);
        if (practice.Any(l => !accounts.ContainsKey(l.Slot))) return false;

        var draft = new PostingDraft
        {
            EntryDate = atUtc, BranchId = job.BranchId, SourceType = SourceType, SourceId = documentId,
            Description = $"{(isReturn ? MaintenancePracticeLedger.KindReturn : MaintenancePracticeLedger.KindIssue)} {job.Number} {number}",
            CreatedBy = by,
        };
        foreach (var l in practice) draft.Add(new PostingLine(accounts[l.Slot].Code, l.Debit, l.Credit));
        return await new LedgerPoster(db).PostAsync(draft, ct) is not null;
    }

    /// <summary>The document's value per stock group, rounded once per group at the ledger's three decimals.</summary>
    public static IReadOnlyList<MaintenancePracticeValue> Values(IEnumerable<Line> lines) =>
        lines.GroupBy(l => MaintenancePracticeLedger.GroupOf(l.Role)).OrderBy(g => g.Key)
            .Select(g => new MaintenancePracticeValue(g.Key, Math.Round(g.Sum(l => l.Value), 3, MidpointRounding.AwayFromZero)))
            .ToList();

    /// <summary>The documents of these ids already posted at issue (an entry that stands, not a reversal).</summary>
    public static async Task<HashSet<Guid>> PostedAsync(PosDbContext db, IReadOnlyCollection<Guid> documentIds, CancellationToken ct) =>
        documentIds.Count == 0 ? [] : (await db.LedgerEntries.AsNoTracking()
            .Where(e => e.SourceType == SourceType && documentIds.Contains(e.SourceId) && !e.IsReversal)
            .Select(e => e.SourceId).ToListAsync(ct)).ToHashSet();
}
