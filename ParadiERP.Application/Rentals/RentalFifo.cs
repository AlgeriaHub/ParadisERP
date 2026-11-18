namespace ParadiERP.Application.Rentals;

/// <summary>Part of one issue batch closed by one return.</summary>
public sealed record BatchTake(IssueBatch Batch, decimal Qty);

/// <summary>
/// How one return note's quantity was spread over the batches still out.
/// </summary>
/// <param name="Unmatched">
/// Quantity the return claims that was never issued. It is reported rather than absorbed: a
/// return note bigger than what went out is a counting mistake somewhere, and silently charging
/// it to the oldest batch would hide it.
/// </param>
public sealed record FifoMatch(IReadOnlyList<BatchTake> Takes, decimal Unmatched)
{
    public decimal MatchedQty => Takes.Sum(t => t.Qty);
}

/// <summary>
/// Matching returns to issues, oldest batch first.
///
/// <para>
/// The items are quantities, not serial numbers: forty couplers come back and nothing on them
/// says which note they left on. FIFO is the only rule that can be applied consistently, and
/// consistency is the point — it decides how many days each returned unit is charged for, so
/// two people counting the same pile must arrive at the same money.
/// </para>
/// </summary>
public static class RentalFifo
{
    /// <summary>
    /// Batches in the order they close: oldest issue date first, and for two notes raised on
    /// the same day, the order they were raised in (the sort is stable, so the caller's order
    /// decides).
    /// </summary>
    public static IReadOnlyList<IssueBatch> Oldest(IEnumerable<IssueBatch> issues) =>
        issues.OrderBy(b => b.IssuedOn).ToList();

    /// <summary>
    /// Spreads each return's settled quantity over the batches, oldest first. Returns one match
    /// per quantity given, in the same order — the caller is expected to hand them over in
    /// return-date order.
    /// </summary>
    public static IReadOnlyList<FifoMatch> Match(
        IEnumerable<IssueBatch> issues, IEnumerable<decimal> settledQuantities)
    {
        var batches = Oldest(issues);
        var remaining = batches.Select(b => b.Qty).ToArray();
        var head = 0;

        var matches = new List<FifoMatch>();
        foreach (var quantity in settledQuantities)
        {
            var takes = new List<BatchTake>();
            var left = Math.Max(0, quantity);
            while (left > 0 && head < batches.Count)
            {
                if (remaining[head] <= 0) { head++; continue; }
                var take = Math.Min(left, remaining[head]);
                takes.Add(new BatchTake(batches[head], take));
                remaining[head] -= take;
                left -= take;
            }
            matches.Add(new FifoMatch(takes, left));
        }
        return matches;
    }

    /// <summary>What is still out of a set of batches once a total quantity has been settled.</summary>
    public static decimal StillOut(IEnumerable<IssueBatch> issues, decimal settledQty) =>
        Math.Max(0, issues.Sum(b => b.Qty) - settledQty);
}
