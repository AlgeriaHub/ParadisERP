using ParadiERP.Application.Regional;
using ParadiERP.Domain;

namespace ParadiERP.Application.Rentals;

/// <summary>
/// Days at one price, pinned to the calendar: <see cref="Days"/> days starting on <see cref="From"/>.
///
/// <para>
/// A <see cref="MinimumTopUp"/> stretch is days charged but never spent — the minimum hire on a
/// batch back sooner (<see cref="RentalPeriod.ChargedDays"/>). It has no calendar of its own, so
/// <see cref="From"/> is the day the batch was settled, and the whole stretch belongs to whichever
/// window holds that day.
/// </para>
/// </summary>
public sealed record RentalStretch(
    DateOnly From, int Days, decimal UnitPrice, RentalChargeKind Kind,
    Guid? ExtensionId = null, bool MinimumTopUp = false)
{
    /// <summary>The last day of the stretch.</summary>
    public DateOnly Through => From.AddDays(Math.Max(1, Days) - 1);
}

/// <summary>One stay, priced: the days it was out, the days it is charged for, and the dated stretches.</summary>
public sealed record RentalStayPrice(int DaysOut, int ChargedDays, IReadOnlyList<RentalStretch> Stretches)
{
    /// <summary>The stretches as the settlement prices them: undated, a top-up folded back into the stretch it extends.</summary>
    public IReadOnlyList<RentalSpan> Spans => RentalStay.Spans(Stretches);
}

/// <summary>
/// The one function that prices a stay — the contract settlement and the customer quantity
/// statement both go through it, so there is no second formula.
///
/// <para>
/// A stay is a quantity that went out on <c>startsOn</c> and stopped being the customer's to use on
/// <c>rentStopsOn</c> (<see cref="ReturnEntry.RentStopsOn"/>). Its days are poured into the
/// entitlement (<see cref="RentalPeriod.Apportion"/>) and each stretch is pinned to the days it
/// covers, so a report can cut any window out of it.
/// </para>
/// </summary>
public static class RentalStay
{
    /// <summary>
    /// Prices one stay.
    /// </summary>
    /// <param name="startsOn">The first day of the batch's period (<see cref="IssueBatch.IssuedOn"/>).</param>
    /// <param name="rentStopsOn">The first day no longer charged.</param>
    /// <param name="minimumDays">The minimum hire frozen on the contract; applied only to a settled stay.</param>
    /// <param name="waivesOverdue">Late days waived on the return note: no overdue stretch at all.</param>
    /// <param name="lateDaysWaived">
    /// Late days deducted on the return note: the overdue stretch loses its last this many days.
    /// </param>
    /// <param name="overdueShare">
    /// An amount deducted on the return note, as the share of each late day's price still charged.
    /// </param>
    /// <param name="settledOn">
    /// The day the return note settled the stay, or null for goods still out — priced provisionally,
    /// with no minimum top-up, since the minimum is charged when they come back.
    /// </param>
    public static RentalStayPrice Price(
        DateOnly startsOn, DateOnly rentStopsOn, IReadOnlyList<RentalSpan> entitlement,
        int durationDays, decimal overdueUnitPrice, int minimumDays = 0,
        bool waivesOverdue = false, DateOnly? settledOn = null,
        DateOnly? actualIssuedOn = null, SameDayUsage sameDayUsage = SameDayUsage.LegacyUnknown,
        bool physicalReturn = true, int? lateDaysWaived = null, decimal? overdueShare = null)
    {
        // Compare actual company-calendar days, never the cutoff-adjusted period start.
        // Historical rows deliberately retain the original minimum/cutoff arithmetic.
        if (physicalReturn && settledOn is DateOnly returned && sameDayUsage != SameDayUsage.LegacyUnknown
            && (actualIssuedOn ?? startsOn) == returned)
        {
            if (sameDayUsage is not (SameDayUsage.Used or SameDayUsage.NotUsed))
                throw new ArgumentException("sameDayUsageRequired", nameof(sameDayUsage));
            var days = sameDayUsage == SameDayUsage.Used ? 1 : 0;
            return new RentalStayPrice(days, days, days == 0 ? [] :
                [new RentalStretch(returned, 1, entitlement.FirstOrDefault()?.UnitPrice ?? 0m, RentalChargeKind.Rent)]);
        }
        var daysOut = RentalPricing.DaysOut(startsOn, rentStopsOn);
        var charged = settledOn is null ? daysOut : RentalPeriod.ChargedDays(daysOut, durationDays, minimumDays);

        var stretches = new List<RentalStretch>();
        var offset = 0;
        foreach (var apportioned in RentalPeriod.Apportion(charged, entitlement, overdueUnitPrice))
        {
            var span = apportioned;
            var first = offset;
            offset += span.Days;
            // Late days the branch waived on the note (RentalLateReturn): asked, answered no. The
            // rent before them stands, priced exactly as it was.
            if (span.Kind == RentalChargeKind.Overdue && waivesOverdue) continue;
            // Answered "yes, less a deduction" (RentalLateReturn): in days, the last late days go
            // and the first are charged as they were; as an amount, every late day is charged at
            // the same share of its price.
            if (span.Kind == RentalChargeKind.Overdue && lateDaysWaived is int waived)
            {
                span = span with { Days = Math.Max(0, span.Days - Math.Max(0, waived)) };
                if (span.Days == 0) continue;
            }
            if (span.Kind == RentalChargeKind.Overdue && overdueShare is decimal share)
                span = span with { UnitPrice = span.UnitPrice * share };

            var spent = Math.Clamp(daysOut - first, 0, span.Days);
            if (spent > 0)
                stretches.Add(new RentalStretch(startsOn.AddDays(first), spent, span.UnitPrice, span.Kind, span.ExtensionId));
            if (span.Days - spent > 0 && settledOn is DateOnly settled)
                stretches.Add(new RentalStretch(settled, span.Days - spent, span.UnitPrice, span.Kind, span.ExtensionId,
                    MinimumTopUp: true));
        }
        return new RentalStayPrice(daysOut, charged, stretches);
    }

    /// <summary>
    /// The stretches as undated spans, in order. A top-up is the only split <see cref="Price"/>
    /// makes, so folding it back into the stretch before it gives exactly what
    /// <see cref="RentalPeriod.Apportion"/> gave.
    /// </summary>
    public static IReadOnlyList<RentalSpan> Spans(IEnumerable<RentalStretch> stretches)
    {
        var spans = new List<RentalSpan>();
        foreach (var s in stretches)
        {
            if (s.MinimumTopUp && spans.Count > 0 && spans[^1].Kind == s.Kind
                && spans[^1].UnitPrice == s.UnitPrice && spans[^1].ExtensionId == s.ExtensionId)
                spans[^1] = spans[^1] with { Days = spans[^1].Days + s.Days };
            else
                spans.Add(new RentalSpan(s.Days, s.UnitPrice, s.Kind, s.ExtensionId));
        }
        return spans;
    }

    /// <summary>
    /// The part of the stretches inside a window of days, both ends included. A top-up goes whole
    /// to the window holding the day the stay was settled.
    /// </summary>
    public static IReadOnlyList<RentalStretch> Clip(IEnumerable<RentalStretch> stretches, DateOnly from, DateOnly to)
    {
        var inside = new List<RentalStretch>();
        foreach (var s in stretches)
        {
            if (s.Days <= 0) continue;
            if (s.MinimumTopUp)
            {
                if (s.From >= from && s.From <= to) inside.Add(s);
                continue;
            }
            var start = s.From > from ? s.From : from;
            var end = s.Through < to ? s.Through : to;
            if (end >= start) inside.Add(s with { From = start, Days = end.DayNumber - start.DayNumber + 1 });
        }
        return inside;
    }

    /// <summary>What a quantity costs over the stretches: each stretch rounded once, as a charge is.</summary>
    public static decimal Value(RegionalSettings regional, IEnumerable<RentalStretch> stretches, decimal qty) =>
        stretches.Sum(s => RentalPricing.Amount(regional, s.UnitPrice, s.Days, qty));
}
