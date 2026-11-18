using System.ComponentModel.DataAnnotations;

namespace AlgeriaPos.Domain;

/// <summary>
/// What a charge on a rental contract is for, as it is stored.
///
/// <para>
/// ⚠ This mirrors <c>AlgeriaPos.Application.Rentals.RentalChargeKind</c> name for name and
/// value for value, and <c>RentalChargeKindTests</c> fails the build if the two ever drift.
/// The duplication is deliberate: the domain has no project references at all (see
/// <c>LayeringTests</c>), so the stored column cannot reference the engine's enum, and a raw
/// <c>int</c> with no names would put the mapping in whichever call site was written last.
/// </para>
/// <para>
/// The kinds are not decoration: rent and overdue are two different income lines, maintenance
/// recovers a cost the company paid, and damage and missing are compensation for an asset that
/// is not coming back. They post to different accounts, so the split has to survive from the
/// return note all the way to the ledger.
/// </para>
/// </summary>
public enum RentalChargeKind
{
    Rent = 0,
    Overdue = 1,
    Maintenance = 2,
    Damage = 3,
    Missing = 4
}

/// <summary>
/// Open until everything issued has been settled, then closed. A contract that has issued nothing
/// has not started, which is not the same as being finished — so the one way out of it before an
/// exit note is <see cref="Cancelled"/>, which the owner asked for on 2026-10-06.
/// </summary>
public enum RentalContractStatus
{
    Open = 0,
    Closed = 1,

    /// <summary>
    /// Called off before any exit note: nothing left, nothing was charged. Kept, never deleted,
    /// and seen only by whoever holds <c>rentals.cancelled</c> — the Admin (owner, 2026-10-06:
    /// "hide it for cashier and only appear for admin").
    /// </summary>
    Cancelled = 2
}

/// <summary>
/// An agreement to hire goods out by quantity.
///
/// <para>
/// <b>Server-only, and deliberately not a <see cref="SyncEntity"/></b> (design §5.8/§5.9). A
/// contract is a signed back-office document; a till neither raises one nor needs to read one,
/// and deriving any of these types from <see cref="SyncEntity"/> is all it would take to put
/// the company's rental book on every cashier's device at the next pull — silently, because
/// the pull is driven by the base type.
/// </para>
/// <para>
/// <b>It has no end date.</b> Duration is a property of the line: scaffolding for a month and
/// formwork for six weeks sit on the same contract. And the period starts at the issue note,
/// not here — goods sitting in the warehouse because the site was not ready are not hired.
/// </para>
/// </summary>
public class RentalContract
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The number written on the customer's copy. Unique, and generated server-side.</summary>
    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>The branch that raised it. Goods may go out from, and come back to, others.</summary>
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }

    public RentalContractStatus Status { get; set; } = RentalContractStatus.Open;

    /// <summary>
    /// When the first issue note was raised. Non-null means the contract is locked: the prices
    /// and quantities on it have been signed and acted on, and anything that has to change
    /// afterwards is a new document — an extension, a return note — never an edit.
    /// </summary>
    public DateTime? LockedAtUtc { get; set; }

    public DateTime SignedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the contract closed: its goods all back and its value paid (answers 15, 22), or the
    /// Admin closing it short. Null while it is open.
    /// </summary>
    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>
    /// When the last line cleared — every unit back or settled as lost or damaged. Since the
    /// owner's answer of 2026-10-02 this is not the close: the contract stays open until it is
    /// paid. Null while anything is still out.
    /// </summary>
    public DateTime? GoodsBackAtUtc { get; set; }

    /// <summary>Why the Admin closed it before it was paid in full, and who did; null otherwise.</summary>
    [MaxLength(400)] public string? ClosedShortReason { get; set; }
    [MaxLength(200)] public string? ClosedShortBy { get; set; }

    /// <summary>When it was cancelled, who did it and why; null unless <see cref="RentalContractStatus.Cancelled"/>.</summary>
    public DateTime? CancelledAtUtc { get; set; }
    [MaxLength(200)] public string? CancelledBy { get; set; }
    [MaxLength(300)] public string? CancelReason { get; set; }

    /// <summary>
    /// The contract this one extends (owner, 2026-10-02, answer 8: "an extension is a new contract,
    /// and it says it is the customer's extension"). Its goods were carried over without leaving
    /// the site; null for a contract raised on its own.
    /// </summary>
    public Guid? ExtendsContractId { get; set; }

    /// <summary>
    /// Built and switched off (§5.6). While the company setting is off this stays zero,
    /// nothing reads it and nothing computes with it — a deposit is a liability held on the
    /// customer's behalf, never income and never a payment against what the contract owes.
    /// <para>The column exists now because adding it later is three migrations against
    /// databases holding live customer data.</para>
    /// </summary>
    public decimal DepositAmount { get; set; }

    [MaxLength(500)] public string? Notes { get; set; }

    [MaxLength(200)] public string? CreatedBy { get; set; }

    /// <summary>
    /// Which of the company's contract wordings this contract prints (owner, PinSay #302,
    /// 2026-10-05): one of <c>Store.ContractWordings</c>' keys, or null for the standard wording
    /// (every contract raised before the choice existed). Chosen while the contract is a draft and
    /// frozen with it at the first issue note; an extension carries its contract's. A wording a
    /// contract names cannot be removed (the documents PUT refuses), so the key always resolves.
    /// </summary>
    [MaxLength(40)] public string? WordingKey { get; set; }

    /// <summary>
    /// Whether this contract's bills carry VAT (owner, 2026-11-02: "VAT is optional when the bill is made", rental
    /// bills included). Chosen while the contract is a draft and frozen with it at the first issue note, because
    /// what it decides is the rate each line freezes: <see cref="TaxMode.Taxed"/> lines take the company's hire
    /// rate (<see cref="Store.RentalTaxRate"/>), <see cref="TaxMode.Untaxed"/> lines take 0, and every issue,
    /// return, extension, charge and invoice follows the lines. Making a contract untaxed needs the untaxed-invoice
    /// permission (<c>finance.invoices.untaxed</c>); taking it back to taxed does not. Taxed on every contract raised
    /// before the choice, whose lines keep the rate they were signed at.
    /// </summary>
    public TaxMode TaxMode { get; set; } = TaxMode.Taxed;

    /// <summary>
    /// The company's minimum hire (<see cref="Store.RentalMinimumDays"/>) as it stood when this
    /// contract was raised: the term the customer agreed to, so a later change of the setting never
    /// re-prices it. 0 on every contract raised before the setting, and on an extension, which
    /// continues a hire whose minimum the contract it extends already applied.
    /// </summary>
    public int MinimumRentalDays { get; set; }

    /// <summary>
    /// A snapshot of who signed the paper, taken when the contract is raised and never
    /// recomputed from the customer afterwards: the representative may leave the company, and
    /// this contract keeps the name of whoever signed it. Prefilled from the person, or from the
    /// company's current authorised representative, and editable as free text while the contract
    /// is a draft — the actual signer may be a delegate.
    /// </summary>
    [MaxLength(200)] public string? SignatoryName { get; set; }
    [MaxLength(40)] public string? SignatoryIdNumber { get; set; }

    /// <summary>
    /// The fact that this contract was raised under the signatory rule: its first issue note —
    /// the moment it locks — is refused while <see cref="SignatoryName"/> is blank.
    ///
    /// <para>
    /// The migration leaves it false on every existing row: those were valid under the old rules
    /// and are not refused. <c>RentalDocuments.CreateContractAsync</c> sets it true, and nothing
    /// else ever changes it — not <c>UpdateContractAsync</c> either, so a pre-release draft edited
    /// today stays exempt. A nullable column alone cannot tell an old contract from a new draft
    /// left blank; this bool can, and unlike a <see cref="CreatedAtUtc"/> gate it does not depend
    /// on knowing when each deployment migrated.
    /// </para>
    /// </summary>
    public bool SignatoryRequired { get; set; }

    /// <summary>
    /// A person customer's contract signed by an agent (وكيل) rather than the customer himself
    /// (owner, 2026-10-06, reversing answer 11 for persons). The customer stays the debtor; the
    /// snapshot above is the agent's name and identity, typed on the contract, and the first issue
    /// note asks for the agent's document and its number. False — the customer signs himself — is
    /// every row before it: his identity is taken from his record when it holds one, and is not
    /// asked for. Always false for a company, which signs through its representative.
    /// </summary>
    public bool SignedByAgent { get; set; }

    /// <summary>
    /// The customer's site the goods go to, and its name and address as they stood at signing.
    /// Snapshots, like the signatory: the site may be renamed or closed later and the signed
    /// paper keeps what was on it. Restrict on the site row for the same reason.
    /// </summary>
    public Guid? SiteId { get; set; }
    public CustomerSite? Site { get; set; }
    [MaxLength(120)] public string? SiteName { get; set; }
    [MaxLength(300)] public string? SiteAddress { get; set; }

    /// <summary>
    /// The rest of the signatory's identity as the owner's paper contract records it: which
    /// document, who issued it and when, and the date of birth. Snapshots taken at signing.
    /// </summary>
    [MaxLength(40)] public string? SignatoryIdType { get; set; }
    [MaxLength(120)] public string? SignatoryIdIssuer { get; set; }
    public DateOnly? SignatoryIdIssuedOn { get; set; }
    public DateOnly? SignatoryBirthDate { get; set; }

    /// <summary>The lorry the goods travel on, as written on the contract; the notes carry their own.</summary>
    [MaxLength(80)] public string? TruckType { get; set; }
    [MaxLength(40)] public string? TruckNumber { get; set; }
    [MaxLength(40)] public string? TruckOwnerPhone { get; set; }

    public List<RentalContractLine> Lines { get; set; } = new();
}

/// <summary>
/// One hired item, with the three prices signed on it.
///
/// <para>
/// Every price here is per unit <b>per day</b> except
/// <see cref="DamageUnitPrice"/>, which is per unit outright. All three are frozen when the
/// contract is signed; the point of signing them is to have something to stand on when the
/// customer refuses the difference months later. Re-pricing the product afterwards never
/// re-prices a signed line.
/// </para>
/// </summary>
public class RentalContractLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ContractId { get; set; }
    public RentalContract? Contract { get; set; }

    public Guid ProductId { get; set; }
    public Product? Product { get; set; }

    /// <summary>Product name and code captured at signing, so history survives a catalog rename.</summary>
    [MaxLength(64)] public string Sku { get; set; } = string.Empty;
    [MaxLength(200)] public string NameSnapshot { get; set; } = string.Empty;

    /// <summary>The product's Arabic name at signing, so the Arabic print says what the customer read. Null on lines signed before it existed.</summary>
    [MaxLength(200)] public string? NameArSnapshot { get; set; }

    /// <summary>The contracted quantity. Issues may total less, never more.</summary>
    public decimal Qty { get; set; }

    /// <summary>The agreed period, counted per issue batch from the day that batch went out.</summary>
    public int DurationDays { get; set; }

    public decimal RentUnitPrice { get; set; }

    /// <summary>What one unit costs the customer if it is lost or destroyed.</summary>
    public decimal DamageUnitPrice { get; set; }

    /// <summary>
    /// Per unit per day for days past the period. The schema sketch in §5.5 calls
    /// this <c>lateUnitPrice</c>; the owner calls it overdue, and so does the engine.
    /// <para>Derived once at signing from the rental price and the admin's multiplier, then
    /// frozen here: changing the multiplier afterwards never changes a signed contract.</para>
    /// </summary>
    public decimal OverdueUnitPrice { get; set; }

    /// <summary>
    /// Tax percentage for everything this line charges — hire, overdue, repairs and
    /// compensation alike. Taken from the company setting when the contract is signed and
    /// frozen here with the prices, for the same reason they are: the customer signed a
    /// figure, and a rate changed next quarter must not rewrite what they agreed to.
    /// <para>0 leaves the line untaxed, which is the default until a company sets a rate.</para>
    /// </summary>
    public decimal TaxRate { get; set; }

    /// <summary>
    /// Set on an accessory line — the couplers and posts that go out bolted to a scaffold.
    /// Nobody hires a coupler, so its rent is zero, but it is charged in full if it does not
    /// come back, and it inherits its parent's period: a coupler cannot expire before its
    /// scaffold.
    /// </summary>
    public Guid? ParentLineId { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>
/// Goods leaving for the site. <b>This is where the period starts and where the money starts:</b>
/// the full agreed period is invoiced the moment the goods go out (confirmed 2026-09-23), and
/// an early return refunds the unused part rather than producing a smaller bill.
/// </summary>
public class IssueNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    public Guid ContractId { get; set; }
    public RentalContract? Contract { get; set; }

    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The branch the goods physically left from, which need not be the contract's. It is what
    /// decides whose <see cref="ProductStock.OnRentQty"/> carries them, and therefore which
    /// branch a return somewhere else has to transfer them from.
    /// </summary>
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }

    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    [MaxLength(500)] public string? Note { get; set; }

    /// <summary>Who drove the goods out and on what — the printed note names them so the site can check the load.</summary>
    [MaxLength(120)] public string? DriverName { get; set; }
    [MaxLength(40)] public string? DriverPhone { get; set; }
    [MaxLength(40)] public string? TruckNumber { get; set; }

    /// <summary>
    /// The customer carried the goods himself (owner, 2026-10-02): the note says so in words and
    /// asks for no driver or truck, which are mandatory only when the branch transports.
    /// </summary>
    public bool CustomerTransport { get; set; }

    /// <summary>
    /// Goods carried over from the contract this one extends (answer 8): no truck took them
    /// anywhere and no stock moved — the note exists so the new period is counted and billed like
    /// any other batch. The owner's "no issue note is needed" is the paper; this is the record.
    /// </summary>
    public bool IsCarryOver { get; set; }

    /// <summary>
    /// The first day of this batch's period, frozen when the note is raised: the delivery day,
    /// or the next one when the goods left at or after the company's cutoff hour. Null only on
    /// notes raised before the column existed; those are frozen with the hour then in force
    /// the moment the hour is changed, so a new cutoff never moves a batch already out.
    /// </summary>
    public DateOnly? PeriodStartsOn { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<IssueNoteLine> Lines { get; set; } = new();
}

/// <summary>
/// A quantity of one contract line that went out on one day — the unit of calculation, not the
/// contract and not the line. Each batch gets its full duration counted from the day it left,
/// so one line can have a batch expiring today and another expiring in three weeks.
/// </summary>
public class IssueNoteLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IssueNoteId { get; set; }
    public IssueNote? IssueNote { get; set; }

    public Guid ContractLineId { get; set; }
    public RentalContractLine? ContractLine { get; set; }

    public decimal Qty { get; set; }
}

/// <summary>
/// Goods coming back, in whatever condition they come back in.
///
/// <para>
/// A return is taken at the contract's branch and nowhere else (the owner, 2026-09-30: "the
/// return is to the same branch, then it is transferred to another branch" — an ordinary stock
/// transfer afterwards). Goods an Admin issued from a different branch still come back to the
/// contract's, and for those the system records an <b>implicit transfer</b> (§7 decision 2), or
/// one branch ends up holding stock it never received.
/// </para>
/// </summary>
public class ReturnNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    public Guid ContractId { get; set; }
    public RentalContract? Contract { get; set; }

    public DateTime ReturnedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The branch that actually took the goods back.</summary>
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }

    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    [MaxLength(500)] public string? Note { get; set; }

    /// <summary>
    /// Days of the agreed period the customer paid for at issue and never used. Frozen here
    /// because the figure depends on the FIFO state at this moment, and re-deriving it after a
    /// later note would give a different answer to the customer holding the paperwork.
    /// </summary>
    public decimal UnusedRent { get; set; }

    /// <summary>
    /// Where the refund went. A customer who still owes more than the refund gets it off the
    /// debt and no money leaves; only the excess becomes a payment voucher.
    /// </summary>
    public decimal ReliefDebtReduction { get; set; }
    public decimal ReliefPaymentVoucher { get; set; }

    /// <summary>Who brought the goods back and on what.</summary>
    [MaxLength(120)] public string? DriverName { get; set; }
    [MaxLength(40)] public string? DriverPhone { get; set; }
    [MaxLength(40)] public string? TruckNumber { get; set; }

    /// <summary>
    /// The customer carried the goods himself (owner, 2026-10-02): the note says so in words and
    /// asks for no driver or truck, which are mandatory only when the branch transports.
    /// </summary>
    public bool CustomerTransport { get; set; }

    /// <summary>
    /// Goods carried over to the contract that extends this one (answer 8): they never left the
    /// site, so nothing moves and nothing is charged — the note settles this contract's batches at
    /// the end of their period, the day the extension's period starts.
    /// </summary>
    public bool IsCarryOver { get; set; }

    /// <summary>
    /// Whether the day the goods came back is charged as a rented day. False on every row that
    /// predates the column and under today's rule (the return day never counts). Once the
    /// company sets <see cref="Store.RentalDayCutoffHour"/>, a return at or after that hour
    /// defaults this to true, and the branch manager may decide otherwise on the note —
    /// <see cref="ReturnDayReason"/> says why and <see cref="ReturnDayDecidedBy"/> who.
    /// </summary>
    public bool CountReturnDay { get; set; }
    [MaxLength(300)] public string? ReturnDayReason { get; set; }
    [MaxLength(120)] public string? ReturnDayDecidedBy { get; set; }

    /// <summary>
    /// The answer to "charge the late days?", asked when goods come back after the end of their
    /// period (the owner, 2026-10-08; answers 7 and 55). False — on every row that predates the
    /// column, on a note that was not late, and when nobody answered — charges them as overdue, as
    /// the engine always has; true waives them, and <see cref="LateWaiverReason"/> says why.
    /// <see cref="LateDecidedBy"/> names whoever answered, either way; null means nobody was asked.
    /// </summary>
    public bool WaiveOverdue { get; set; }
    [MaxLength(300)] public string? LateWaiverReason { get; set; }
    [MaxLength(120)] public string? LateDecidedBy { get; set; }

    /// <summary>
    /// The third answer to the same question (the owner, 2026-10-22): charge the late days less a
    /// deduction the branch chooses, in days or as an amount, never more than the lateness itself.
    /// <see cref="LateWaiverReason"/> says why, as it does for a waiver. All null — every row that
    /// predates the columns, a note charged in full, a waived one — leaves the late days to
    /// <see cref="WaiveOverdue"/>; at most one of the two deductions is ever set.
    /// <list type="bullet">
    /// <item><see cref="LateDaysWaived"/>: this many late days are taken off each batch on the
    /// note, the last ones; the first are charged at the signed price.</item>
    /// <item><see cref="LateAmountWaived"/>: this much is taken off the note's late total,
    /// <see cref="LateAmountFull"/> — the late days at the signed prices, before tax, as the branch
    /// saw them when it answered. Every late day is charged at the same share of its price.</item>
    /// </list>
    /// </summary>
    public int? LateDaysWaived { get; set; }
    public decimal? LateAmountWaived { get; set; }
    public decimal? LateAmountFull { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<ReturnNoteLine> Lines { get; set; } = new();
}

/// <summary>
/// What came back of one contract line, and in what condition.
///
/// <para>
/// The four quantities are exclusive and together they settle units against what went out.
/// Each one also decides where the goods land: sound units are available again, damaged but
/// repairable ones go to maintenance, wrecks become scrap, and units that never came back
/// leave the business altogether.
/// </para>
/// </summary>
public enum SameDayUsage { LegacyUnknown = 0, NotApplicable = 1, Used = 2, NotUsed = 3 }

public enum RentalReturnCondition { Legacy = 0, Ok = 1, PartialDamage = 2, TotalDamage = 3, Missing = 4 }

/// <summary>Immutable allocation of returned quantities to the issue batch and its financial result.</summary>
public class ReturnNoteLineAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReturnNoteLineId { get; set; }
    public ReturnNoteLine? ReturnNoteLine { get; set; }
    public Guid IssueNoteId { get; set; }
    public Guid ContractLineId { get; set; }
    public decimal Qty { get; set; }
    public RentalReturnCondition Condition { get; set; }
    public int ChargeableDays { get; set; }
    public decimal UnusedRentNet { get; set; }
    public decimal ActualRentNet { get; set; }
    public decimal DailyUnitRate { get; set; }
    public decimal TaxRate { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ReturnedAtUtc { get; set; }
    public SameDayUsage SameDayUsage { get; set; }
}

public class ReturnNoteLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReturnNoteId { get; set; }
    public ReturnNote? ReturnNote { get; set; }

    public SameDayUsage SameDayUsage { get; set; }
    public Guid? UsageSelectedByUserId { get; set; }
    public DateTime? UsageSelectedAtUtc { get; set; }
    public List<ReturnNoteLineAllocation> Allocations { get; set; } = new();

    public Guid ContractLineId { get; set; }
    public RentalContractLine? ContractLine { get; set; }

    /// <summary>Came back sound: available again.</summary>
    public decimal QtyOk { get; set; }

    /// <summary>Came back damaged but usable: returned stock in need of repair.</summary>
    public decimal QtyPartialDamage { get; set; }

    /// <summary>What that repair is charged at. Income, because the customer is paying for it.</summary>
    public decimal MaintenanceValue { get; set; }

    /// <summary>Came back beyond repair: charged at the frozen damage price, and the wreck is scrap.</summary>
    public decimal QtyTotalDamage { get; set; }

    /// <summary>Did not come back at all: charged at the same price, and written off where it was issued.</summary>
    public decimal QtyMissing { get; set; }

    /// <summary>
    /// The day the customer reported the loss or the damage, when they reported one.
    ///
    /// <para>
    /// Rent runs to this day rather than to the settlement: reporting it is what lets the
    /// company write the unit off, charge for it and stop waiting. A customer who says nothing
    /// keeps paying to the day the loss is finally settled, so telling the company late is
    /// never the cheaper option.
    /// </para>
    /// <para>
    /// It only bites on a line where nothing came back, and the engine ignores a date after the
    /// settlement or before the goods went out — see
    /// <c>AlgeriaPos.Application.Rentals.ReturnEntry.RentStopsOn</c>, which owns the rule. This
    /// column exists so the date survives to be passed to it; nothing here decides anything.
    /// </para>
    /// <para>Null on every line that carries no report, which is most of them.</para>
    /// </summary>
    public DateTime? ReportedOnUtc { get; set; }

    /// <summary>Everything this line settles — a unit settles whether it walked back in or was written off.</summary>
    public decimal SettledQty => QtyOk + QtyPartialDamage + QtyTotalDamage + QtyMissing;
}

/// <summary>
/// Days added to a period at the rental price, signed as a document of its own.
///
/// <para>
/// <b>An extension is a decision; overdue is a consequence.</b> It is a separate document
/// rather than an edit, so the rule that a contract locks at its first issue note still holds,
/// and it moves no stock — the goods are already on site. Raising a fresh contract instead
/// would mean a fictitious return and a fictitious issue for goods that never moved, which
/// corrupts the one report the business runs on: what is out, and with whom.
/// </para>
/// </summary>
public class ContractExtension
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    public Guid ContractId { get; set; }
    public RentalContract? Contract { get; set; }

    /// <summary>The line it extends, or null for every line on the contract.</summary>
    public Guid? ContractLineId { get; set; }
    public RentalContractLine? ContractLine { get; set; }

    public int ExtraDays { get; set; }

    /// <summary>
    /// Per unit per day, at the rental rate. Held here rather than read back off
    /// the line, so re-pricing a line can never re-price a signed extension.
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// The day the document was signed, in the company's own time zone.
    ///
    /// <para>
    /// ⚠ Stored, not derived. <see cref="IsRetroactive"/> is <c>RaisedOn &gt; PeriodEndsOn</c>,
    /// and without both dates on the row a manager's approval cannot be re-verified later —
    /// the flag alone is an assertion with nothing behind it, and the FIFO state it was
    /// measured against has moved on by the time anyone asks.
    /// </para>
    /// </summary>
    public DateTime RaisedOnUtc { get; set; }

    /// <summary>
    /// The deadline the signature had to beat: the soonest period end among the batches still
    /// out when it was raised. Frozen for the same reason as <see cref="RaisedOnUtc"/>.
    /// </summary>
    public DateTime? PeriodEndsOnUtc { get; set; }

    /// <summary>
    /// Signed after the period had already run out. Allowed — sometimes the paperwork follows
    /// the handshake — but it turns days that were already overdue into rented days, so it is
    /// a manager's decision and it is written down. Left open to everyone, every overdue
    /// becomes an extension and the overdue line stops meaning anything.
    /// </summary>
    public bool IsRetroactive { get; set; }

    public Guid? ApprovedByUserId { get; set; }
    [MaxLength(200)] public string? ApprovedByName { get; set; }

    [MaxLength(500)] public string? Reason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One typed amount owed on a contract, frozen at the moment the document that created it was
/// raised.
///
/// <para>
/// The figures are all derivable from the notes, and they are stored anyway: the customer is
/// holding a piece of paper with these numbers on it, and a settlement re-run months later
/// against a contract that has since had three more returns will not reproduce them. A charge
/// carries the price it was worked out from and the note it came off, so a dispute can be
/// answered from the charge alone.
/// </para>
/// </summary>
public class RentalCharge
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ContractId { get; set; }
    public RentalContract? Contract { get; set; }

    public Guid ContractLineId { get; set; }

    public RentalChargeKind Kind { get; set; }
    public decimal Amount { get; set; }

    /// <summary>The return note that settled it. Every charge comes off one.</summary>
    public Guid ReturnNoteId { get; set; }

    /// <summary>The issue batch the units left on, where FIFO decided one. Null for compensation.</summary>
    public Guid? IssueNoteId { get; set; }

    /// <summary>Set when the days were bought by an extension, so the money leads back to it.</summary>
    public Guid? ExtensionId { get; set; }

    public decimal Qty { get; set; }
    public int Days { get; set; }

    /// <summary>
    /// The frozen price this was worked out from — per day for rent and overdue,
    /// per unit for damage and missing.
    /// </summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// Whether this amount reached the ledger when it was raised. Rent inside the agreed period
    /// was already invoiced at issue, so it is recorded on the contract and <b>not</b> posted
    /// again — posting it twice is the one way this module could overstate revenue.
    /// </summary>
    public bool IsPosted { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A file kept with a contract — today the scan or photo of the signatory's identity document
/// (<see cref="Kind"/> "idDocument"). The row keeps what the file is and where it is; the bytes
/// are in the files folder (owner, 2026-10-04), so a backup is the database and that folder
/// together. Cascade from the contract: the attachment is part of the document, not a record of
/// its own.
/// </summary>
public class RentalContractAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ContractId { get; set; }
    public RentalContract? Contract { get; set; }

    /// <summary>What the file is: "idDocument".</summary>
    [MaxLength(40)] public string Kind { get; set; } = string.Empty;
    [MaxLength(200)] public string FileName { get; set; } = string.Empty;
    [MaxLength(100)] public string ContentType { get; set; } = string.Empty;
    public long Length { get; set; }
    /// <summary>Where the file is kept in the files folder (<c>FilePaths.ContractAttachment</c>).</summary>
    [MaxLength(200)] public string StoragePath { get; set; } = string.Empty;

    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(100)] public string? UploadedBy { get; set; }
}
