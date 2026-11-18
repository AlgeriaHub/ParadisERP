using ParadisERP.Application.Regional;

namespace ParadisERP.Application.Rentals;

/// <summary>
/// Goods back after the end of their period, and the question that asks whether to charge for it.
///
/// <para>
/// The owner, 2026-10-08: "if the client returns the rented items after the time they were due,
/// the system must ask whether to take fees for a day or not." It stands on two earlier answers:
/// a late day is always counted, with exceptions (7), and an exception is the branch manager's,
/// with the reason written and his name recorded (55).
/// </para>
/// <para>
/// <b>When it is asked.</b> A note is late when the engine would charge it overdue: the days a
/// batch was out run past the line's own days and every extension signed over it. Whether the
/// return day is one of those days is <see cref="RentalDays"/>' rule — the company's cutoff hour,
/// or the manager's word on the note — so the due time is the period's last day at the cutoff
/// hour, or the end of that day when no cutoff is set. An on-time return asks nothing and goes
/// through exactly as before.
/// </para>
/// <para>
/// <b>What the answer does.</b> Yes charges the late days as overdue, at the price signed on the
/// line, through the same settlement, posting and tax as every overdue day before it. No waives
/// them — none is charged — and is written down with the reason and the name. No answer at all
/// (an older screen, a direct call) is the rule as it always was: charged. The answer is a
/// decision, never a price: what the late days come to is always the server's own figure.
/// </para>
/// <para>
/// <b>Yes, less a deduction</b> (the owner, 2026-10-22: "the branch can charge some of the days,
/// not all of them", "the deduction in days or as an amount, the branch decides", "never more than
/// the late days, nor more than what they are worth"). In days, that many late days are taken off
/// each batch on the note — the last ones; the first are charged at the signed price. As an amount,
/// it comes off the note's late total and every late day is charged at the same share of its price,
/// so the money still sits on the lines and batches that ran late. Either is an exception like the
/// waiver: the reason is written and the name recorded. A deduction is at least one day or one
/// unit of money, and never more than the note's late days or its late total.
/// </para>
/// </summary>
public static class RentalLateReturn
{
    /// <summary>Whether a note's own charges include late days — the only time the question is asked.</summary>
    public static bool IsLate(IEnumerable<RentalCharge> noteCharges) =>
        noteCharges.Any(c => c.Kind == RentalChargeKind.Overdue);

    /// <summary>The note's late days: the most any batch on it ran past its period, before any answer.</summary>
    public static int LateDays(IEnumerable<RentalCharge> noteCharges) =>
        noteCharges.Where(c => c.Kind == RentalChargeKind.Overdue).Select(c => c.Days).DefaultIfEmpty(0).Max();

    /// <summary>The note's late total, before tax and before any answer: what a deduction comes off.</summary>
    public static decimal LateAmount(IEnumerable<RentalCharge> noteCharges) =>
        noteCharges.Where(c => c.Kind == RentalChargeKind.Overdue).Sum(c => c.Amount);

    /// <summary>
    /// Why the answer cannot be saved, or null. Charging in full is the rule and needs nothing;
    /// waiving (answer 55) and deducting are exceptions and say why.
    /// </summary>
    public static string? WaiverProblem(bool? chargeOverdue, string? reason, bool deducts = false) =>
        (chargeOverdue == false || (chargeOverdue == true && deducts))
        && string.IsNullOrWhiteSpace(reason) ? "lateWaiverNeedsReason" : null;

    /// <summary>
    /// Why a deduction cannot be saved, or null. It belongs to a yes and is one kind or the other;
    /// in days it is at least one and at most <paramref name="lateDays"/>, as an amount more than
    /// nothing, in whole units of money, and at most <paramref name="lateAmount"/>.
    /// </summary>
    public static string? DeductionProblem(bool? chargeOverdue, int? daysWaived, decimal? amountWaived,
        int lateDays, decimal lateAmount, RegionalSettings regional)
    {
        if (daysWaived is null && amountWaived is null) return null;
        if (chargeOverdue != true || (daysWaived is not null && amountWaived is not null)) return "lateDeductionInvalid";
        if (daysWaived is int days && (days < 1 || days > lateDays)) return "lateDeductionDaysInvalid";
        if (amountWaived is decimal amount && (amount <= 0 || amount > lateAmount || amount != regional.RoundMoney(amount)))
            return "lateDeductionAmountInvalid";
        return null;
    }

    /// <summary>
    /// The pennies an amount deduction leaves. Each late charge is rounded on its own, so their
    /// sum can miss the note's late total less the deduction by a unit of money or two; that
    /// difference goes to the note's largest late charge, so the note charges exactly what the
    /// branch decided. A larger difference means the late days themselves have changed since the
    /// answer, and is left alone.
    /// </summary>
    public static ContractSettlement LandDeductions(
        ContractSettlement settlement, IEnumerable<ReturnEntry> returns, RegionalSettings regional)
    {
        var targets = returns
            .Where(r => r.LateAmountWaived is not null && r.LateAmountFull is not null)
            .GroupBy(r => r.ReturnNoteId)
            .Select(g => (NoteId: g.Key, Target: regional.RoundMoney(g.First().LateAmountFull!.Value - g.First().LateAmountWaived!.Value)))
            .ToList();
        if (targets.Count == 0) return settlement;

        var unit = 1m;
        for (var i = 0; i < regional.CurrencyDecimals; i++) unit /= 10;
        var lines = settlement.Lines.ToList();
        foreach (var (noteId, target) in targets)
        {
            var late = lines.SelectMany((l, index) => l.Charges.Select(c => (Index: index, Charge: c)))
                .Where(x => x.Charge.ReturnNoteId == noteId && x.Charge.Kind == RentalChargeKind.Overdue)
                .ToList();
            if (late.Count == 0) continue;
            var rest = target - late.Sum(x => x.Charge.Amount);
            if (rest == 0 || Math.Abs(rest) > unit * late.Count) continue;
            var largest = late.MaxBy(x => x.Charge.Amount);
            var line = lines[largest.Index];
            lines[largest.Index] = line with
            {
                Charges = line.Charges
                    .Select(c => ReferenceEquals(c, largest.Charge) ? c with { Amount = c.Amount + rest } : c)
                    .ToList()
            };
        }
        return new ContractSettlement(lines);
    }
}
