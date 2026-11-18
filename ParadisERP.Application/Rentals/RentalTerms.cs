using ParadisERP.Domain;

namespace ParadisERP.Application.Rentals;

/// <summary>
/// What a charge on a rental contract is for.
///
/// <para>
/// The kinds are not decoration. Rent and overdue are two different income lines, maintenance
/// recovers a cost the company paid, and damage and missing are compensation for an asset that
/// is not coming back. They post to different accounts, so the split has to survive from the
/// return note all the way to the ledger.
/// </para>
/// </summary>
public enum RentalChargeKind
{
    /// <summary>Hire for days inside the agreed period, including days added by an extension.</summary>
    Rent = 0,

    /// <summary>
    /// Days past the agreed period, priced above the rental rate. The schema sketch in
    /// <c>docs/06-erp-redesign.md</c> §5.5 calls this kind <c>Late</c>; the owner calls it
    /// overdue, and so does this engine.
    /// </summary>
    Overdue = 1,

    /// <summary>Repair of an item that came back damaged but usable — a cost, recovered.</summary>
    Maintenance = 2,

    /// <summary>An item that came back beyond repair, at the frozen total-damage price.</summary>
    Damage = 3,

    /// <summary>An item that did not come back at all, at the same frozen total-damage price.</summary>
    Missing = 4
}

/// <summary>
/// The company-level rental settings an admin owns.
///
/// <para>
/// The overdue price is held as a multiplier rather than as a second price list: prices move,
/// and a second list kept by hand drifts out of step silently. The multiplier is applied once,
/// when the contract is signed, and the resulting figure is frozen on the line — see
/// <see cref="RentalPricing.OverdueUnitPrice"/>. Changing the multiplier afterwards never
/// changes a signed contract.
/// </para>
/// </summary>
/// <param name="OverdueMultiplier">
/// Applied to the rental price to get the overdue price, e.g. 1.5×. Never below 1: an overdue
/// day cheaper than a rented day pays the customer to be late.
/// </param>
/// <param name="DepositEnabled">
/// The deposit switch (§5.6, docs/07 §3.3). On, the deposit is recorded on the contract, shown,
/// and stated as owed back at close; off, it is invisible — no field, no zero line, no report
/// column. Either way nothing in this engine computes with it: see <see cref="RentalDeposit"/>.
/// </param>
/// <param name="DayCutoffHour">
/// The local hour (0–23) that splits a day for delivery and return, or null for the rule the engine
/// has always had — see <see cref="RentalDays"/>.
/// </param>
/// <param name="MinimumDays">
/// The minimum hire in days, 0 for none — see <see cref="RentalPeriod.ChargedDays"/>.
/// </param>
public sealed record RentalSettings(decimal OverdueMultiplier = 1.5m, bool DepositEnabled = false, int? DayCutoffHour = null,
    int MinimumDays = 0)
{
    /// <summary>The company multiplier, or the item's own when one is set.</summary>
    public decimal MultiplierFor(decimal? itemOverride) =>
        itemOverride is decimal m && m > 0 ? m : OverdueMultiplier;
}

/// <summary>
/// One line of a contract, with the three prices signed on it.
///
/// <para>
/// Every price here is per unit <b>per day</b> except
/// <see cref="DamageUnitPrice"/>, which is per unit outright. All three are frozen when the
/// contract is signed: the point of signing them is to have something to stand on when the
/// customer refuses the difference months later.
/// </para>
/// <para>
/// <see cref="DurationDays"/> lives here and not on the contract because scaffolding for a
/// month and formwork for six weeks sit on the same contract. The contract itself has no end
/// date at all.
/// </para>
/// </summary>
/// <param name="Qty">The contracted quantity. Issues may total less, never more.</param>
/// <param name="RentUnitPrice">Hire price per unit per day. Zero on an accessory.</param>
/// <param name="DamageUnitPrice">What one unit costs the customer if it is lost or destroyed.</param>
/// <param name="OverdueUnitPrice">Per unit per day for days past the period.</param>
/// <param name="ParentLineId">
/// Set on an accessory line — the couplers and posts that go out bolted to a scaffold. An
/// accessory is hired to nobody, so its rent is zero, but it is charged in full if it does not
/// come back, and it inherits its parent's period: a coupler cannot expire before its scaffold.
/// </param>
public sealed record RentalLine(
    Guid Id,
    decimal Qty,
    int DurationDays,
    decimal RentUnitPrice,
    decimal DamageUnitPrice,
    decimal OverdueUnitPrice,
    Guid? ParentLineId = null)
{
    public bool IsAccessory => ParentLineId is not null;
}

/// <summary>
/// One line of an issue note: a quantity of one contract line that went out on one day.
///
/// <para>
/// This is the unit of calculation — not the contract, and not the line. A contract is issued
/// in batches, and each batch gets its full duration counted from the day it went out, so the
/// same line can have one batch expiring today and another expiring in three weeks.
/// </para>
/// </summary>
public sealed record IssueBatch(Guid IssueNoteId, Guid LineId, DateOnly IssuedOn, decimal Qty,
    DateOnly? ActualIssuedOn = null);

/// <summary>
/// One line of a return note: what came back, in what condition, on one day.
///
/// <para>
/// The four quantities are exclusive and together they settle units against what went out.
/// <see cref="QtyPartialDamage"/> units did come back — they are returned stock in need of
/// repair, and <see cref="MaintenanceValue"/> is what that repair is charged at.
/// </para>
/// </summary>
public sealed record ReturnEntry(
    Guid ReturnNoteId,
    Guid LineId,
    DateOnly ReturnedOn,
    decimal QtyOk = 0,
    decimal QtyPartialDamage = 0,
    decimal MaintenanceValue = 0,
    decimal QtyTotalDamage = 0,
    decimal QtyMissing = 0,
    DateOnly? ReportedOn = null,
    bool CountsReturnDay = false,
    bool WaivesOverdue = false,
    Guid? ReturnLineId = null,
    SameDayUsage SameDayUsage = SameDayUsage.LegacyUnknown,
    int? LateDaysWaived = null,
    decimal? LateAmountWaived = null,
    decimal? LateAmountFull = null)
{
    /// <summary>
    /// The share of each late day's price still charged once <see cref="LateAmountWaived"/> is
    /// taken off <see cref="LateAmountFull"/>; null when the note deducts no amount.
    /// </summary>
    public decimal? OverdueShare =>
        LateAmountWaived is decimal waived && LateAmountFull is decimal full && full > 0
            ? Math.Clamp((full - waived) / full, 0m, 1m)
            : null;

    /// <summary>
    /// Everything this note settles. A unit settles whether it walked back in or was written
    /// off as lost: what closes a contract line is issued = returned + settled as lost/damaged.
    /// </summary>
    public decimal SettledQty => QtyOk + QtyPartialDamage + QtyTotalDamage + QtyMissing;

    /// <summary>
    /// The day rent stops running on this note.
    ///
    /// <para>
    /// A unit that never comes back is charged rent for the days it was on site, and the owner
    /// settled where those days end: <b>rent stops the day the customer reports the loss or the
    /// damage.</b> Reporting it is what lets the company write it off, charge for it and stop
    /// waiting; from that day the damage price takes over and the hire is finished.
    /// </para>
    /// <para>
    /// A customer who says nothing keeps paying. The company did not know, could not re-hire the
    /// unit and had no reason to treat it as gone, so rent runs to the day the loss is finally
    /// settled — <see cref="ReturnedOn"/>. That is also what stops "report it late" being the
    /// cheaper option.
    /// </para>
    /// <para>
    /// It applies only to a line where <b>no usable unit came back</b> — units missing, or units
    /// reported destroyed and later handed back as a wreck for scrap: a unit reported destroyed
    /// is no more usable than one reported lost, and the report is what let the company stop
    /// waiting for it either way (docs/07 §3.4, accepted 2026-09-24). A unit that walked back in
    /// sound or repairable was the customer's to use right up to the day they returned it,
    /// whatever was reported about it in between — so a line mixing returned units with lost ones
    /// keeps the return date for all of them, and a loss is declared on its own line to be dated
    /// from its report. The rule is per line: a report on one line never shortens another line's
    /// hire on the same note.
    /// </para>
    /// <para>
    /// A report dated after the settlement, or before the goods went out, is not a report the
    /// arithmetic can use; rent then runs to the settlement as though none had been made.
    /// </para>
    /// <para>
    /// <see cref="CountsReturnDay"/> is the return-day decision stored on the note
    /// (<see cref="RentalDays"/>): counted, rent runs through the return day as well. It never
    /// moves a report — a unit reported gone stopped being used on the report day, whatever hour
    /// the paperwork came back at — and it leaves <see cref="ReturnedOn"/> alone, so notes keep
    /// their order however the days were counted.
    /// </para>
    /// <para>
    /// <see cref="WaivesOverdue"/> is the answer stored on the note to "charge the late days?",
    /// asked when goods come back after the end of their period (the owner, 2026-10-08; answers 7
    /// and 55 — see <see cref="RentalLateReturn"/>). Waived, the days past the period are not
    /// charged at all. It never moves a day: the agreed period, and any refund of it, come out
    /// exactly as they would have. <see cref="LateDaysWaived"/> and <see cref="LateAmountWaived"/>
    /// are the partial answer: fewer late days charged, or each charged at a share of its price.
    /// </para>
    /// </summary>
    public DateOnly RentStopsOn(DateOnly issuedOn) =>
        ReportedOn is DateOnly reported
        && QtyOk == 0 && QtyPartialDamage == 0
        && reported < ReturnedOn && reported >= issuedOn
            ? reported
            : RentalDays.ChargedUntil(ReturnedOn, CountsReturnDay);
}

/// <summary>
/// A signed document adding days to a period <b>before</b> that period ends, at the rental
/// price.
///
/// <para>
/// An extension is a decision; overdue is a consequence. It is a separate document rather than
/// an edit, so the rule that a contract locks at its first issue note still holds, and it moves
/// no stock — the goods are already on site. Raising a fresh contract instead would mean a
/// fictitious return and a fictitious issue for goods that never moved, which corrupts the one
/// report the business runs on: what is out, and with whom.
/// </para>
/// </summary>
/// <param name="LineId">The line it extends, or null for every line on the contract.</param>
/// <param name="UnitPrice">
/// Per unit per day, at the rental rate. Held on the extension rather than read
/// back off the line, so that re-pricing a line can never re-price a signed extension.
/// </param>
/// <param name="RaisedOn">
/// The day the document was signed. Compared with the period end, this is what makes an
/// extension retroactive — see <see cref="RentalRules.ExtensionProblem"/>.
/// </param>
public sealed record RentalExtension(
    Guid Id,
    Guid? LineId,
    int ExtraDays,
    decimal UnitPrice,
    DateOnly RaisedOn,
    string? Reason = null);

/// <summary>Everything raised against one contract, flattened to what the arithmetic needs.</summary>
/// <param name="MinimumDays">
/// The minimum hire frozen on the contract (<see cref="RentalPeriod.ChargedDays"/>): a batch back
/// sooner is charged this many days, or its period if shorter. 0 charges the days used, as always.
/// </param>
public sealed record RentalContractInput(
    IReadOnlyList<RentalLine> Lines,
    IReadOnlyList<IssueBatch> Issues,
    IReadOnlyList<ReturnEntry> Returns,
    IReadOnlyList<RentalExtension> Extensions,
    int MinimumDays = 0)
{
    public RentalContractInput(IReadOnlyList<RentalLine> lines) : this(lines, [], [], []) { }
}
