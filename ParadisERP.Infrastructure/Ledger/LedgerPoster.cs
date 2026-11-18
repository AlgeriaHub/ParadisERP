using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Ledger;

/// <summary>
/// One side of a posting, expressed in account codes rather than ids so a module never has to
/// look an account up. Exactly one of <paramref name="Debit"/> and <paramref name="Credit"/>
/// is non-zero and both are positive.
/// </summary>
/// <param name="BranchId">
/// Which branch this side belongs to, when it is not the document's own branch. A remittance
/// is the case: the credit sits at the branch whose box emptied, the debit at the safe.
/// </param>
/// <param name="CustomerId">
/// Who this side is owed by, on the accounts that have a third party behind them — customers
/// (411) and uncleared paper (413). It is what makes "account 411 for that customer" answerable
/// and therefore what a customer statement is read from. Left null everywhere else.
/// </param>
/// <param name="SupplierId">Whom this side is owed to, on account 401 — what a supplier statement is read from.</param>
public readonly record struct PostingLine(
    string AccountCode, decimal Debit, decimal Credit, Guid? BranchId = null, string? Memo = null,
    Guid? CustomerId = null, Guid? SupplierId = null)
{
    public static PostingLine Dr(
        string accountCode, decimal amount, Guid? branchId = null, string? memo = null, Guid? customerId = null,
        Guid? supplierId = null) =>
        new(accountCode, amount, 0m, branchId, memo, customerId, supplierId);

    public static PostingLine Cr(
        string accountCode, decimal amount, Guid? branchId = null, string? memo = null, Guid? customerId = null,
        Guid? supplierId = null) =>
        new(accountCode, 0m, amount, branchId, memo, customerId, supplierId);

    /// <summary>
    /// A signed amount on one account: positive debits, negative credits. Lets a caller build a
    /// line from an arithmetic result without branching on its sign at every call site, which
    /// is where "a negative debit" creeps into a ledger.
    /// </summary>
    public static PostingLine Signed(
        string accountCode, decimal amount, Guid? branchId = null, string? memo = null, Guid? customerId = null,
        Guid? supplierId = null) =>
        amount >= 0
            ? Dr(accountCode, amount, branchId, memo, customerId, supplierId)
            : Cr(accountCode, -amount, branchId, memo, customerId, supplierId);
}

/// <summary>The document being posted, before it is checked and written.</summary>
public sealed class PostingDraft
{
    public required DateTime EntryDate { get; init; }
    public required Guid BranchId { get; init; }
    /// <summary>What kind of document caused this — <c>sale</c>, <c>receiving</c>, <c>remittance</c>.</summary>
    public required string SourceType { get; init; }
    /// <summary>The document's own id, so any figure in a report leads back to it.</summary>
    public required Guid SourceId { get; init; }
    public string? Description { get; init; }
    public string? CreatedBy { get; init; }

    /// <summary>Money or goods moving between two of the company's own branches. See <see cref="LedgerEntry.IsInterBranch"/>.</summary>
    public bool IsInterBranch { get; init; }
    public Guid? CounterpartBranchId { get; init; }

    public bool IsReversal { get; init; }
    public Guid? ReversalOfEntryId { get; init; }

    public List<PostingLine> Lines { get; } = [];

    /// <summary>
    /// The same entry on another day. Only for the till's late sale into closed books, which is never refused and is
    /// booked on the first open day instead (CLO-01, owner 2026-11-01).
    /// </summary>
    public PostingDraft DatedOn(DateTime entryDate, string? description)
    {
        var copy = new PostingDraft
        {
            EntryDate = entryDate, BranchId = BranchId, SourceType = SourceType, SourceId = SourceId,
            Description = description ?? Description, CreatedBy = CreatedBy, IsInterBranch = IsInterBranch,
            CounterpartBranchId = CounterpartBranchId, IsReversal = IsReversal, ReversalOfEntryId = ReversalOfEntryId
        };
        copy.Lines.AddRange(Lines);
        return copy;
    }

    public PostingDraft Add(PostingLine line)
    {
        Lines.Add(line);
        return this;
    }

    /// <summary>Adds a line only when there is an amount to post, so a zero never becomes an empty row.</summary>
    public PostingDraft AddIfAny(PostingLine line)
    {
        if (line.Debit != 0 || line.Credit != 0) Lines.Add(line);
        return this;
    }
}

/// <summary>
/// The only way anything reaches the ledger.
///
/// <para>
/// Every entry it writes balances, because it refuses to write one that does not. That check
/// is here rather than in a nightly report on purpose: an unbalanced entry is a bug in
/// whichever module produced it, and the only moment it can be attributed to that module is
/// the moment it is created.
/// </para>
/// <para>
/// It joins whatever transaction the caller already has open. Sales, receiving, remittances
/// and settlements all post inside the same unit of work as the document they come from, so a
/// document can never exist without its posting or the other way round.
/// </para>
/// </summary>
public sealed class LedgerPoster(PosDbContext db)
{
    /// <summary>The model stores money at scale 3, so balance is judged at the same precision.</summary>
    private const int Scale = 3;

    private Dictionary<string, LedgerAccount>? accounts;

    /// <summary>
    /// Writes one balanced entry, or nothing at all when the document has already been posted.
    ///
    /// <para>
    /// Returns null when the draft has no lines — a state change from available to maintenance
    /// moves no value, and an entry with nothing in it is noise in every report.
    /// </para>
    /// </summary>
    /// <exception cref="LedgerImbalanceException">The draft does not balance, or a line is malformed.</exception>
    public async Task<LedgerEntry?> PostAsync(PostingDraft draft, CancellationToken ct = default)
    {
        var lines = draft.Lines
            .Select(l => l with { Debit = Round(l.Debit), Credit = Round(l.Credit) })
            .Where(l => l.Debit != 0 || l.Credit != 0)
            .ToList();
        if (lines.Count == 0) return null;

        Validate(draft, lines);

        // A till re-pushes its outbox whenever it reconnects, and a retried batch is normal
        // rather than exceptional. The sale itself is idempotent on its terminal-generated id;
        // this is the same guarantee for its posting, so a document posts exactly once however
        // many times it arrives. Reversals are excluded because a reversal deliberately shares
        // the source of the entry it cancels.
        if (!draft.IsReversal &&
            await db.LedgerEntries.AnyAsync(
                e => e.SourceType == draft.SourceType && e.SourceId == draft.SourceId && !e.IsReversal, ct))
            return null;

        // CLO-01: nothing posts into closed books. Checked after the re-push test, so a document already posted
        // before the close answers "already done" rather than "closed". A reversal is dated today, which is open.
        if (await BookClosings.CurrentAsync(db, ct) is { OpenFromUtc: DateTime openFrom, ClosedThrough: DateOnly closedThrough }
            && draft.EntryDate < openFrom)
            throw new PeriodClosedException(closedThrough, draft.SourceType);

        var map = await AccountsAsync(ct);
        var entry = new LedgerEntry
        {
            EntryDate = draft.EntryDate,
            BranchId = draft.BranchId,
            SourceType = draft.SourceType,
            SourceId = draft.SourceId,
            Description = Trim(draft.Description, 400),
            CreatedBy = Trim(draft.CreatedBy, 200),
            IsReversal = draft.IsReversal,
            ReversalOfEntryId = draft.ReversalOfEntryId,
            IsInterBranch = draft.IsInterBranch,
            CounterpartBranchId = draft.CounterpartBranchId
        };

        foreach (var line in lines)
        {
            if (!map.TryGetValue(line.AccountCode, out var account))
                throw new LedgerImbalanceException(
                    $"{draft.SourceType}: no account '{line.AccountCode}' in the chart of accounts.");

            entry.Lines.Add(new LedgerEntryLine
            {
                EntryId = entry.Id,
                AccountId = account.Id,
                BranchId = line.BranchId ?? draft.BranchId,
                CustomerId = line.CustomerId,
                SupplierId = line.SupplierId,
                Debit = line.Debit,
                Credit = line.Credit,
                Memo = Trim(line.Memo, 300)
            });
        }

        db.LedgerEntries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Cancels an entry with its mirror image. Nothing is ever deleted or edited, so this is
    /// the only correction there is — and it leaves both the wrong figure and the correction
    /// visible, which is the point.
    /// </summary>
    public async Task<LedgerEntry> ReverseAsync(LedgerEntry original, string reason, string? by, CancellationToken ct = default)
    {
        // A reversal deliberately shares its source document with the entry it cancels, so the
        // post-once check below does not apply to it and cannot be the thing that stops a
        // replay. These are that thing, and they live here rather than only in the route that
        // calls it: reversing twice doubles a correction, and the second one looks exactly as
        // legitimate as the first in every report. Both checks run inside the caller's
        // transaction, which a write filter has already serialised on the version counter.
        if (original.IsReversal)
            throw new InvalidOperationException("A reversal cannot itself be reversed; post the original document again.");
        if (await db.LedgerEntries.AnyAsync(e => e.ReversalOfEntryId == original.Id, ct))
            throw new InvalidOperationException("This entry has already been reversed.");

        var draft = new PostingDraft
        {
            EntryDate = DateTime.UtcNow,
            BranchId = original.BranchId,
            SourceType = original.SourceType,
            SourceId = original.SourceId,
            Description = reason,
            CreatedBy = by,
            IsInterBranch = original.IsInterBranch,
            CounterpartBranchId = original.CounterpartBranchId,
            IsReversal = true,
            ReversalOfEntryId = original.Id
        };

        var codes = await CodesByIdAsync(ct);
        foreach (var line in original.Lines)
            // Debit and credit swap; the branch, the customer and the supplier do not. A remittance
            // reversal has to put the cash back in the box it actually left, not at the document's
            // branch, and a credit reversed has to come off the account of whoever was billed.
            draft.Add(new PostingLine(
                codes[line.AccountId], line.Credit, line.Debit, line.BranchId, "Reversal", line.CustomerId, line.SupplierId));

        // A reversal that balanced on the way in balances on the way out, but it goes through
        // the same check rather than around it: the day that stops being true is the day the
        // check is worth having.
        return await PostAsync(draft, ct)
               ?? throw new LedgerImbalanceException("An entry with no lines cannot be reversed.");
    }

    /// <summary>Has this document already been posted? Used by routes that want to say so rather than silently skip.</summary>
    public Task<bool> IsPostedAsync(string sourceType, Guid sourceId, CancellationToken ct = default) =>
        db.LedgerEntries.AnyAsync(e => e.SourceType == sourceType && e.SourceId == sourceId && !e.IsReversal, ct);

    private static void Validate(PostingDraft draft, List<PostingLine> lines)
    {
        foreach (var line in lines)
        {
            if (line.Debit < 0 || line.Credit < 0)
                throw new LedgerImbalanceException(
                    $"{draft.SourceType}: a negative amount on account {line.AccountCode}. A negative debit is a credit written badly.");
            if (line.Debit != 0 && line.Credit != 0)
                throw new LedgerImbalanceException(
                    $"{draft.SourceType}: account {line.AccountCode} was given both a debit and a credit.");
        }

        var debits = Round(lines.Sum(l => l.Debit));
        var credits = Round(lines.Sum(l => l.Credit));
        if (debits != credits)
            throw new LedgerImbalanceException(
                $"{draft.SourceType} {draft.SourceId} does not balance: debits {debits:0.###} != credits {credits:0.###}. " +
                "This is a bug in the module that raised it, not in the ledger.");

        if (lines.Count < 2)
            throw new LedgerImbalanceException(
                $"{draft.SourceType} {draft.SourceId} has a single line. Double entry has two sides.");
    }

    private async Task<Dictionary<string, LedgerAccount>> AccountsAsync(CancellationToken ct)
    {
        // Cached for the life of the scope: a push of 500 sales would otherwise read the whole
        // chart 500 times, and accounts do not change under a request.
        accounts ??= (await db.LedgerAccounts.AsNoTracking().ToListAsync(ct))
            .ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
        return accounts;
    }

    private async Task<Dictionary<Guid, string>> CodesByIdAsync(CancellationToken ct) =>
        (await AccountsAsync(ct)).Values.ToDictionary(a => a.Id, a => a.Code);

    private static decimal Round(decimal value) => Math.Round(value, Scale, MidpointRounding.AwayFromZero);

    private static string? Trim(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
