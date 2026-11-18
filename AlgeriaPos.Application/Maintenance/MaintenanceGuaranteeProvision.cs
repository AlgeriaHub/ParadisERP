using AlgeriaPos.Application.Manufacturing;
using AlgeriaPos.Domain;

namespace AlgeriaPos.Application.Maintenance;

/// <summary>
/// What a rework had booked to one account, counted from the first day of its original job's invoice month to a moment:
/// <see cref="MaintenanceAccountSlots.CustomerParts"/> for its parts, <see cref="MaintenanceAccountSlots.ServiceCost"/> for
/// labour and overhead moved into jobs (MS-33), or <c>account:&lt;code&gt;</c> for outside work its payment voucher debited.
/// </summary>
public sealed record MaintenanceProvisionCost(string Slot, decimal Amount);

/// <summary>
/// One rework of the original job, with what it had booked at the month's start and at its end — both counted from the
/// invoice month's first day and stopped at the guarantee's end (the gatherer clamps the moments).
/// </summary>
public sealed record MaintenanceProvisionRework(
    Guid JobId, string Number, Guid BranchId,
    IReadOnlyList<MaintenanceProvisionCost> AtStart, IReadOnlyList<MaintenanceProvisionCost> AtEnd);

/// <summary>
/// A customer job whose invoice set a provision aside (MS-37 "provision", Phase M7c), as one month sees it: the frozen amount,
/// the invoice's day and whether it was booked by the month's start and by its end; the guarantee's end (the release) and
/// whether it was reached by the start and by the end; and its reworks.
/// </summary>
public sealed record MaintenanceProvisionJob(
    Guid JobId, string Number, Guid BranchId, string? InvoiceNumber, decimal Amount,
    DateOnly BookedDay, bool BookedByStart, bool BookedByEnd,
    DateOnly ReleaseDay, bool ReleasedByStart, bool ReleasedByEnd,
    IReadOnlyList<MaintenanceProvisionRework> Reworks);

/// <summary>
/// MS-37 "provision" (Phase M7c, MNT-09 C): the guarantee provision's entries for one month, pure. For each customer job
/// invoiced under the provision:
/// <list type="bullet">
/// <item><b>Booked</b> in the invoice's month: Dr <c>maintenance.provision_expense</c> / Cr <c>maintenance.provision_liability</c>,
/// the frozen amount (rate × the invoice net before tax).</item>
/// <item><b>Used</b> by its reworks: the provision used to date is the smaller of the provision booked and what the reworks had
/// booked since the invoice month began (to the guarantee's end); each month moves the difference. Dr the liability / Cr the
/// accounts those costs debited — parts to customer parts, labour and overhead moved to the service cost, outside work back to
/// its voucher's expense account. What is used is shared over the reworks in their order (number), and within one over parts,
/// outside work, then labour and overhead; whatever exceeds the provision stays in expense.</item>
/// <item><b>Released</b> when the guarantee ends: what is left, Dr the liability / Cr the provision expense (the charge
/// reversed).</item>
/// </list>
/// Every amount is the state at the month's end less the state at its start, so the months add up to the whole and a month
/// read twice says the same.
/// </summary>
public static class MaintenanceGuaranteeProvision
{
    public const string KindBooked = "provision_booked";
    public const string KindUsed = "provision_used";
    public const string KindReleased = "provision_released";

    /// <summary>The order costs are taken from the provision within one rework: parts, outside work, then labour and overhead.</summary>
    private static int Rank(string slot) =>
        slot == MaintenanceAccountSlots.CustomerParts ? 0
        : slot.StartsWith(MaintenanceAccountSlots.AccountPrefix, StringComparison.Ordinal) ? 1
        : 2;

    /// <summary>The provision's state at the month's start and at its end: booked, used by reworks and released, each to date.</summary>
    public sealed record State(decimal BookedStart, decimal BookedEnd, decimal UsedStart, decimal UsedEnd, decimal ReleasedStart, decimal ReleasedEnd)
    {
        public decimal Booked => BookedEnd - BookedStart;
        public decimal Used => UsedEnd - UsedStart;
        public decimal Released => ReleasedEnd - ReleasedStart;
        /// <summary>What the liability holds at the month's end.</summary>
        public decimal HeldAtEnd => BookedEnd - UsedEnd - ReleasedEnd;
    }

    public static State StateOf(MaintenanceProvisionJob p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var bookedStart = p.BookedByStart ? p.Amount : 0m;
        var bookedEnd = p.BookedByEnd ? p.Amount : 0m;
        var usedStart = Allocate(bookedStart, p.Reworks, r => r.AtStart).Values.Sum();
        var usedEnd = Allocate(bookedEnd, p.Reworks, r => r.AtEnd).Values.Sum();
        return new State(bookedStart, bookedEnd, usedStart, usedEnd,
            p.ReleasedByStart ? bookedStart - usedStart : 0m, p.ReleasedByEnd ? bookedEnd - usedEnd : 0m);
    }

    /// <summary>The month's entries for one provision, and the liability's expected movement (credit positive).</summary>
    public static (IReadOnlyList<PracticeEntry> Entries, decimal LiabilityMovement) Month(MaintenanceProvisionJob p, DateOnly lastDay)
    {
        ArgumentNullException.ThrowIfNull(p);
        var entries = new List<PracticeEntry>();
        var state = StateOf(p);
        var (bookedStart, bookedEnd, releasedStart, releasedEnd) = (state.BookedStart, state.BookedEnd, state.ReleasedStart, state.ReleasedEnd);
        var usedStart = Allocate(bookedStart, p.Reworks, r => r.AtStart);
        var usedEnd = Allocate(bookedEnd, p.Reworks, r => r.AtEnd);

        if (bookedEnd != bookedStart)
            entries.Add(new PracticeEntry(KindBooked, p.BookedDay, p.JobId, p.Number, p.BranchId, p.InvoiceNumber,
                Pair(MaintenanceAccountSlots.ProvisionExpense, MaintenanceAccountSlots.ProvisionLiability, bookedEnd - bookedStart)));

        // The month's use is dated on the guarantee's last day when it ends this month, otherwise the month's last day.
        var usedDay = p.ReleasedByEnd && !p.ReleasedByStart && p.ReleaseDay <= lastDay ? p.ReleaseDay : lastDay;
        foreach (var rework in p.Reworks.OrderBy(r => r.Number, StringComparer.Ordinal))
        {
            var lines = new List<PracticeLine>();
            foreach (var slot in usedEnd.Keys.Concat(usedStart.Keys).Where(k => k.Rework == rework.JobId).Select(k => k.Slot)
                         .Distinct().OrderBy(Rank).ThenBy(s => s, StringComparer.Ordinal))
            {
                var delta = usedEnd.GetValueOrDefault((rework.JobId, slot)) - usedStart.GetValueOrDefault((rework.JobId, slot));
                if (delta > 0m) lines.Add(new PracticeLine(slot, 0m, delta));
                else if (delta < 0m) lines.Add(new PracticeLine(slot, -delta, 0m));
            }
            var net = lines.Sum(l => l.Credit - l.Debit);
            if (lines.Count == 0) continue;
            lines.Insert(0, net >= 0m
                ? new PracticeLine(MaintenanceAccountSlots.ProvisionLiability, net, 0m)
                : new PracticeLine(MaintenanceAccountSlots.ProvisionLiability, 0m, -net));
            entries.Add(new PracticeEntry(KindUsed, usedDay, rework.JobId, rework.Number, rework.BranchId, p.Number,
                lines.Where(l => l.Debit != 0m || l.Credit != 0m).ToList()));
        }

        if (releasedEnd != releasedStart)
            entries.Add(new PracticeEntry(KindReleased, p.ReleaseDay <= lastDay ? p.ReleaseDay : lastDay, p.JobId, p.Number, p.BranchId,
                p.InvoiceNumber, Pair(MaintenanceAccountSlots.ProvisionLiability, MaintenanceAccountSlots.ProvisionExpense, releasedEnd - releasedStart)));

        return (entries, state.Booked - state.Used - state.Released);
    }

    /// <summary>
    /// The provision used to date, per rework and account: the reworks' costs in their order, each account's cumulative amount
    /// (never below zero) taken until the provision is spent.
    /// </summary>
    public static Dictionary<(Guid Rework, string Slot), decimal> Allocate(
        decimal provision, IReadOnlyList<MaintenanceProvisionRework> reworks, Func<MaintenanceProvisionRework, IReadOnlyList<MaintenanceProvisionCost>> costs)
    {
        var left = Math.Max(0m, provision);
        var used = new Dictionary<(Guid, string), decimal>();
        foreach (var rework in reworks.OrderBy(r => r.Number, StringComparer.Ordinal))
            foreach (var cost in costs(rework).GroupBy(c => c.Slot).Select(g => new MaintenanceProvisionCost(g.Key, g.Sum(c => c.Amount)))
                         .OrderBy(c => Rank(c.Slot)).ThenBy(c => c.Slot, StringComparer.Ordinal))
            {
                var take = Math.Min(left, Math.Max(0m, cost.Amount));
                if (take <= 0m) continue;
                used[(rework.JobId, cost.Slot)] = take;
                left -= take;
            }
        return used;
    }

    private static PracticeLine[] Pair(string debit, string credit, decimal amount) => amount switch
    {
        > 0m => [new PracticeLine(debit, amount, 0m), new PracticeLine(credit, 0m, amount)],
        < 0m => [new PracticeLine(credit, -amount, 0m), new PracticeLine(debit, 0m, -amount)],
        _ => [],
    };
}
