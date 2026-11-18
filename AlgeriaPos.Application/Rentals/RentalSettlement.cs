using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;

namespace AlgeriaPos.Application.Rentals;

/// <summary>
/// One typed amount owed on a contract, traced back to the documents that created it: which
/// line, which return note settled it, and which issue batch it came off.
/// </summary>
/// <param name="UnitPrice">
/// The frozen price the amount was worked out from — per day for rent and
/// overdue, per unit for damage and missing. Kept on the charge so a dispute months later can
/// be answered from the charge alone.
/// </param>
public sealed record RentalCharge(
    RentalChargeKind Kind,
    decimal Amount,
    Guid LineId,
    Guid ReturnNoteId,
    Guid? IssueNoteId,
    decimal Qty,
    int Days,
    decimal UnitPrice,
    Guid? ExtensionId = null);

public sealed record RentalReturnAllocation(ReturnEntry Return, IssueBatch Batch, decimal Qty,
    RentalReturnCondition Condition, RentalStayPrice Stay, decimal UnusedRent, decimal ActualRent, int ReturnOrdinal);

/// <summary>Where an early return's money goes.</summary>
/// <param name="DebtReduction">Taken off what the customer still owes; no document leaves the till.</param>
/// <param name="PaymentVoucher">Money actually going back out, once the debt is clear.</param>
public sealed record RentalRelief(decimal DebtReduction, decimal PaymentVoucher)
{
    public decimal Total => DebtReduction + PaymentVoucher;
}

/// <summary>What one contract line owes, and whether anything of it is still out.</summary>
/// <param name="IssuedPeriodRent">
/// The full agreed period on everything issued so far — what the customer is invoiced when the
/// goods go out, before any of this settles.
/// </param>
/// <param name="UnusedRent">Days of that invoice the customer never used, given back on an early return.</param>
/// <param name="OverReturnedQty">Quantity returned that was never issued. Non-zero means someone counted wrong.</param>
public sealed record LineSettlement(
    Guid LineId,
    IReadOnlyList<RentalCharge> Charges,
    decimal IssuedQty,
    decimal SettledQty,
    decimal StillOutQty,
    decimal OverReturnedQty,
    decimal IssuedPeriodRent,
    decimal UnusedRent)
{
    /// <summary>A line is clear when nothing of it is out: issued = returned + settled as lost or damaged.</summary>
    public bool IsCleared => StillOutQty <= 0;

    public decimal Total(RentalChargeKind kind) => Charges.Where(c => c.Kind == kind).Sum(c => c.Amount);

    /// <summary>The sum of the rounded charges — exactly the figures on the customer's copy.</summary>
    public decimal ChargeTotal => Charges.Sum(c => c.Amount);
}

/// <summary>What a whole contract owes, and whether it can close.</summary>
public sealed record ContractSettlement(IReadOnlyList<LineSettlement> Lines)
{
    public IEnumerable<RentalCharge> Charges => Lines.SelectMany(l => l.Charges);

    public decimal Total(RentalChargeKind kind) => Lines.Sum(l => l.Total(kind));

    public decimal ChargeTotal => Lines.Sum(l => l.ChargeTotal);

    public decimal UnusedRent => Lines.Sum(l => l.UnusedRent);

    public decimal StillOutQty => Lines.Sum(l => l.StillOutQty);

    /// <summary>
    /// A contract closes when the last return note clears what is out — for every line,
    /// quantity issued = returned + settled as lost or damaged.
    ///
    /// <para>
    /// Quantities never issued have no bearing on it: a line contracted for 100 and issued at
    /// 60 closes when those 60 are settled. A contract that has issued nothing at all is not
    /// closed, it has not started.
    /// </para>
    /// </summary>
    public bool IsClosed => Lines.Any(l => l.IssuedQty > 0) && Lines.All(l => l.IsCleared);
}

/// <summary>
/// Works out what a contract owes from its documents: the issue notes that put goods out, the
/// return notes that brought them back, and the extensions signed in between.
///
/// <para>
/// <b>The rule everything else rests on</b> (confirmed by the owner, 2026-09-23): the full
/// agreed period is invoiced when the goods go out. That is what makes an early return a refund
/// rather than a smaller bill, and it is why <see cref="LineSettlement.IssuedPeriodRent"/> and
/// <see cref="LineSettlement.UnusedRent"/> are reported beside the charges.
/// </para>
/// <para>
/// <b>Units that never come back are still charged rent</b> (confirmed 2026-09-23). A unit declared missing or beyond
/// repair on a return note has been on the customer's site all that time, so it accrues rent —
/// and overdue, if it outlived its period — on top of the total-damage price. The hire and the
/// asset are two different things being paid for.
/// </para>
/// <para>
/// <b>Rent stops the day the customer reports it</b>, not the day it is settled. Reporting is
/// what lets the company write the unit off and stop waiting for it. A customer who says
/// nothing keeps paying to the settlement date, because the company did not know and could not
/// re-hire it — and because otherwise reporting late would be the cheaper option. See
/// <see cref="ReturnEntry.RentStopsOn"/>.
/// </para>
/// </summary>
public static class RentalSettlement
{
    /// <summary>FIFO allocation and pricing shared by settlement, financial preview and statements.</summary>
    public static IReadOnlyList<RentalReturnAllocation> AllocateLine(
        RentalLine line, int durationDays, IReadOnlyList<IssueBatch> issues,
        IReadOnlyList<ReturnEntry> returns, IReadOnlyList<RentalExtension> extensions,
        RegionalSettings regional, int minimumDays = 0, bool allowMissingUsage = false)
    {
        var batches = RentalFifo.Oldest(issues);
        var notes = returns.OrderBy(r => r.ReturnedOn).ToList();
        var entitlement = EntitlementFor(line, durationDays, extensions);
        var billedLeft = batches.GroupBy(b => b.IssueNoteId).ToDictionary(g => g.Key,
            g => g.Sum(b => RentalPricing.Amount(regional, line.RentUnitPrice, durationDays, b.Qty)));
        var batchQty = batches.GroupBy(b => b.IssueNoteId).ToDictionary(g => g.Key, g => g.Sum(b => b.Qty));
        var taken = new Dictionary<Guid, decimal>();
        var parts = notes.SelectMany((n, ordinal) => SplitConditions(n).Select(p => (Note: n, Ordinal: ordinal, p.Condition, p.Qty))).ToList();
        var matches = RentalFifo.Match(batches, parts.Select(p => p.Qty));
        var result = new List<RentalReturnAllocation>();
        var roundedQty = new Dictionary<(int Note, Guid Issue, int Days, decimal Price, RentalChargeKind Kind, Guid? Extension), decimal>();
        decimal RoundedPart(int ordinal, IssueBatch batch, RentalSpan span, decimal qty)
        {
            var key = (ordinal, batch.IssueNoteId, span.Days, span.UnitPrice, span.Kind, span.ExtensionId);
            var previous = roundedQty.GetValueOrDefault(key);
            roundedQty[key] = previous + qty;
            return RentalPricing.Amount(regional, span.UnitPrice, span.Days, previous + qty)
                - RentalPricing.Amount(regional, span.UnitPrice, span.Days, previous);
        }
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var note = part.Condition switch
            {
                RentalReturnCondition.Ok => part.Note with { QtyOk = part.Qty, QtyPartialDamage = 0, QtyTotalDamage = 0, QtyMissing = 0 },
                RentalReturnCondition.PartialDamage => part.Note with { QtyOk = 0, QtyPartialDamage = part.Qty, QtyTotalDamage = 0, QtyMissing = 0 },
                RentalReturnCondition.TotalDamage => part.Note with { QtyOk = 0, QtyPartialDamage = 0, QtyTotalDamage = part.Qty, QtyMissing = 0 },
                RentalReturnCondition.Missing => part.Note with { QtyOk = 0, QtyPartialDamage = 0, QtyTotalDamage = 0, QtyMissing = part.Qty },
                _ => part.Note
            };
            foreach (var take in matches[i].Takes)
            {
                var stay = RentalStay.Price(take.Batch.IssuedOn, note.RentStopsOn(take.Batch.IssuedOn),
                    entitlement, durationDays, line.OverdueUnitPrice, minimumDays, note.WaivesOverdue,
                    note.ReturnedOn, take.Batch.ActualIssuedOn,
                    allowMissingUsage && note.SameDayUsage == SameDayUsage.NotApplicable
                        ? SameDayUsage.LegacyUnknown : note.SameDayUsage,
                    physicalReturn: part.Condition != RentalReturnCondition.Missing,
                    lateDaysWaived: note.LateDaysWaived, overdueShare: note.OverdueShare);
                var baseDays = stay.Spans.Where(s => s.Kind == RentalChargeKind.Rent && s.ExtensionId is null)
                    .Sum(s => s.Days);
                var amounts = stay.Spans.Select(s => (Span: s, Amount: RoundedPart(part.Ordinal, take.Batch, s, take.Qty))).ToList();
                var earnedBase = amounts.Where(a => a.Span.Kind == RentalChargeKind.Rent && a.Span.ExtensionId is null)
                    .Sum(a => a.Amount);
                var key = take.Batch.IssueNoteId;
                taken[key] = taken.GetValueOrDefault(key) + take.Qty;
                var unused = taken[key] >= batchQty[key] ? billedLeft[key] - earnedBase
                    : RentalPricing.Amount(regional, line.RentUnitPrice,
                        RentalPeriod.UnusedDays(stay.ChargedDays, durationDays), take.Qty);
                unused = Math.Clamp(unused, 0m, Math.Max(0m, billedLeft[key] - earnedBase));
                billedLeft[key] -= earnedBase + unused;
                result.Add(new(note, take.Batch, take.Qty, part.Condition, stay, unused, amounts.Sum(a => a.Amount), part.Ordinal));
            }
        }
        return result;
    }

    /// <summary>Historic rows stay whole to preserve their exact rounding and mixed-loss rule.</summary>
    public static IReadOnlyList<(RentalReturnCondition Condition, decimal Qty)> SplitConditions(ReturnEntry note)
    {
        if (note.SameDayUsage == SameDayUsage.LegacyUnknown)
            return [(RentalReturnCondition.Legacy, note.SettledQty)];
        return new (RentalReturnCondition, decimal)[]
        {
            (RentalReturnCondition.Ok, note.QtyOk),
            (RentalReturnCondition.PartialDamage, note.QtyPartialDamage),
            (RentalReturnCondition.TotalDamage, note.QtyTotalDamage),
            (RentalReturnCondition.Missing, note.QtyMissing)
        }.Where(p => p.Item2 > 0).ToList();
    }

    /// <summary>Settles every line of a contract, resolving accessories against their parents.</summary>
    public static ContractSettlement Settle(RentalContractInput contract, RegionalSettings regional)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(regional);

        var lines = new List<LineSettlement>(contract.Lines.Count);
        foreach (var line in contract.Lines)
        {
            lines.Add(SettleLine(
                line,
                DurationOf(line, contract.Lines),
                contract.Issues.Where(i => i.LineId == line.Id).ToList(),
                contract.Returns.Where(r => r.LineId == line.Id).ToList(),
                ExtensionsFor(line, contract.Extensions),
                regional,
                contract.MinimumDays));
        }
        // An amount deducted from a late note is spread over lines, so its last pennies land here.
        return RentalLateReturn.LandDeductions(new ContractSettlement(lines), contract.Returns, regional);
    }

    /// <summary>
    /// An accessory inherits its parent's period. A coupler that expires before the scaffold it
    /// is bolted to makes no sense, and it comes back on the same return note anyway.
    /// </summary>
    public static int DurationOf(RentalLine line, IReadOnlyList<RentalLine> allLines)
    {
        if (!line.IsAccessory) return line.DurationDays;
        var parent = allLines.FirstOrDefault(l => l.Id == line.ParentLineId);
        return parent?.DurationDays ?? line.DurationDays;
    }

    /// <summary>
    /// The extensions that reach a line: one raised against the contract, one raised against the
    /// line, and — because the accessory follows its parent's dates — one raised against its
    /// parent.
    /// </summary>
    public static IReadOnlyList<RentalExtension> ExtensionsFor(
        RentalLine line, IEnumerable<RentalExtension> extensions) =>
        extensions
            .Where(e => e.LineId is null || e.LineId == line.Id || (line.IsAccessory && e.LineId == line.ParentLineId))
            .ToList();

    /// <summary>
    /// What a line is entitled to before overdue: its own period at its rent, then each extension
    /// that reaches it. An accessory inherits its parent's days, never its parent's price:
    /// extending the scaffold keeps the couplers out longer, and nobody hires a coupler.
    /// </summary>
    public static IReadOnlyList<RentalSpan> EntitlementFor(
        RentalLine line, int durationDays, IReadOnlyList<RentalExtension> extensions)
    {
        var priced = line.IsAccessory
            ? extensions.Select(e => e with { UnitPrice = 0m }).ToList()
            : extensions.ToList();
        return RentalPeriod.Entitlement(line.RentUnitPrice, durationDays, priced);
    }

    /// <summary>
    /// The heart of it: every unit that came back is matched to the batch it left on (oldest
    /// first), charged for the days that batch was out, and the days of its period it never used
    /// are given back.
    /// </summary>
    public static LineSettlement SettleLine(
        RentalLine line,
        int durationDays,
        IReadOnlyList<IssueBatch> issues,
        IReadOnlyList<ReturnEntry> returns,
        IReadOnlyList<RentalExtension> extensions,
        RegionalSettings regional,
        int minimumDays = 0)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(regional);

        var batches = RentalFifo.Oldest(issues);
        var settlements = returns.OrderBy(r => r.ReturnedOn).ToList();

        var entitlement = EntitlementFor(line, durationDays, extensions);

        var allocations = AllocateLine(line, durationDays, issues, returns, extensions, regional, minimumDays);
        var charges = new List<RentalCharge>();
        foreach (var group in allocations.SelectMany(a => a.Stay.Spans.Select(s => (Allocation: a, Span: s)))
            .GroupBy(x => (x.Allocation.ReturnOrdinal, x.Allocation.Batch.IssueNoteId, x.Span)))
        {
                var span = group.Key.Span;
                var qty = group.Sum(x => x.Allocation.Qty);
                var amount = RentalPricing.Amount(regional, span.UnitPrice, span.Days, qty);
                if (amount == 0) continue;
                charges.Add(new RentalCharge(span.Kind, amount, line.Id, group.First().Allocation.Return.ReturnNoteId,
                    group.Key.IssueNoteId, qty, span.Days, span.UnitPrice, span.ExtensionId));
        }
        foreach (var note in settlements) charges.AddRange(Compensation(line, note, regional));
        var settledQty = allocations.Sum(a => a.Qty);
        var overReturned = Math.Max(0m, settlements.Sum(n => n.SettledQty) - settledQty);
        var unusedRent = allocations.Sum(a => a.UnusedRent);

        var issuedQty = batches.Sum(b => b.Qty);
        return new LineSettlement(
            line.Id,
            charges,
            issuedQty,
            settledQty,
            Math.Max(0m, issuedQty - settledQty),
            overReturned,
            batches.Sum(b => RentalPricing.Amount(regional, line.RentUnitPrice, durationDays, b.Qty)),
            unusedRent);
    }

    /// <summary>
    /// What a return note charges for condition rather than for time: repair of what came back
    /// damaged, and the frozen total-damage price for what came back broken or not at all.
    ///
    /// <para>
    /// None of this depends on which batch the units left on, so none of it goes through FIFO.
    /// </para>
    /// </summary>
    private static IEnumerable<RentalCharge> Compensation(
        RentalLine line, ReturnEntry note, RegionalSettings regional)
    {
        if (note.MaintenanceValue != 0)
            yield return new RentalCharge(
                RentalChargeKind.Maintenance, regional.RoundMoney(note.MaintenanceValue),
                line.Id, note.ReturnNoteId, null, note.QtyPartialDamage, 0, note.MaintenanceValue);

        if (note.QtyTotalDamage > 0)
            yield return new RentalCharge(
                RentalChargeKind.Damage,
                RentalPricing.DamageAmount(regional, line.DamageUnitPrice, note.QtyTotalDamage),
                line.Id, note.ReturnNoteId, null, note.QtyTotalDamage, 0, line.DamageUnitPrice);

        // Forty couplers short is forty couplers charged, at the price signed on the contract —
        // not at today's price, and not at what they cost to buy.
        if (note.QtyMissing > 0)
            yield return new RentalCharge(
                RentalChargeKind.Missing,
                RentalPricing.DamageAmount(regional, line.DamageUnitPrice, note.QtyMissing),
                line.Id, note.ReturnNoteId, null, note.QtyMissing, 0, line.DamageUnitPrice);
    }

    /// <summary>
    /// Where an early return's unused period goes.
    ///
    /// <para>
    /// If the customer is on account and has not paid, it comes off the debt and no money
    /// leaves: raising a payment voucher to someone who still owes more than the refund just
    /// moves cash out of the box for nothing. Once the account is square the rest is a real
    /// payment voucher. A part-paid account does both, which is the same rule stated once.
    /// </para>
    /// </summary>
    public static RentalRelief Relieve(decimal unusedRent, decimal outstandingBalance)
    {
        var relief = Math.Max(0m, unusedRent);
        var offset = Math.Min(relief, Math.Max(0m, outstandingBalance));
        return new RentalRelief(offset, relief - offset);
    }
}
