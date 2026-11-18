using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>
/// Where the money an invoice presents was created.
///
/// <para>
/// This is the field that stops revenue being reported twice. A sale and a rental note already
/// post their own revenue and their own receivable the moment they are raised; an invoice built
/// from one of them is a piece of paper with a number and a due date on it, not a second
/// economic event. Only <see cref="Direct"/> and <see cref="MaintenanceJob"/> (a job posts
/// nothing of its own) create the debt, and only they post.
/// </para>
/// </summary>
public enum InvoiceOrigin
{
    /// <summary>Typed in the back office. This document is what puts the money on 411.</summary>
    Direct = 0,
    /// <summary>Raised over a pushed sale. The sale posted; this adds the due date and the number.</summary>
    Sale = 1,
    /// <summary>Raised over rental charges. The issue and return notes posted; this bills them.</summary>
    Rental = 2,
    /// <summary>
    /// Raised from a completed customer maintenance job (Phase M5a). A job posts nothing of its own, so — like
    /// <see cref="Direct"/> — this invoice is the economic event and posts 411 / income / 4457 itself.
    /// <see cref="Invoice.SourceType"/> is <see cref="InvoiceSources.MaintenanceJob"/>, <see cref="Invoice.SourceId"/> the job.
    /// </summary>
    MaintenanceJob = 3
}

/// <summary>The <see cref="Invoice.SourceType"/> of invoices whose source is not a ledger document.</summary>
public static class InvoiceSources
{
    /// <summary>A customer maintenance job (Phase M5a); one invoice per job.</summary>
    public const string MaintenanceJob = "maintenance-job";
}

/// <summary>
/// What a customer owes, with a date by which they owe it.
///
/// <para>
/// <b>Server-only, and deliberately not a <see cref="SyncEntity"/></b> (design §5.9). A till
/// neither raises an invoice nor needs to read one, and deriving any of these types from
/// <see cref="SyncEntity"/> is all it would take to replicate the company's whole debtor book
/// to every cashier's device at the next pull — silently, because the pull is driven by the
/// base type.
/// </para>
/// <para>
/// <b>Append-only.</b> A raised invoice is never edited and never deleted; the correction is a
/// <see cref="CreditNote"/>, exactly as the only correction in the ledger is a reversal and the
/// only correction to a <see cref="Sale"/> is a refund. An edited invoice is a number the
/// customer has already been shown changing behind their back.
/// </para>
/// </summary>
public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The number on the customer's copy. Unique per company, generated server-side.</summary>
    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }

    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When payment falls due. The one field the whole ageing report is built on, so it is
    /// mandatory rather than nullable: an invoice with no due date can never be overdue, and a
    /// contractor whose debt can never be overdue is one nobody ever chases.
    /// </summary>
    public DateTime DueDateUtc { get; set; }

    /// <summary>
    /// Taxed or not, chosen when the document is raised — an untaxed invoice carries no tax and
    /// stays out of the tax report (§5.7).
    /// </summary>
    public TaxMode TaxMode { get; set; } = TaxMode.Taxed;

    public InvoiceOrigin Origin { get; set; }

    /// <summary>The <c>sourceType</c>/<c>sourceId</c> of the document this bills, when it bills one.</summary>
    [MaxLength(40)] public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }

    /// <summary>The sum of the lines, net of tax and of any discount on them.</summary>
    public decimal SubTotalHt { get; set; }

    /// <summary>
    /// Charged on each rate's own total rather than line by line, the rule sales and rentals
    /// already follow. Per-line tax is deliberately not stored: rounding each line and adding
    /// up drifts by centimes from the figure the customer checks and the business declares,
    /// and two stored figures that must agree are two figures that eventually do not.
    /// </summary>
    public decimal TaxTotal { get; set; }

    public decimal GrandTotalTtc { get; set; }

    /// <summary>
    /// What was already settled on the source document before this invoice existed — the cash
    /// part of a mixed sale, for instance.
    ///
    /// <para>
    /// It is not a payment against this invoice and it is never allocated: the money never
    /// passed through the customer's account. Without it an invoice raised over a sale that was
    /// half paid at the counter would chase the customer for money they have already handed
    /// over, and the ageing report would carry a debt the ledger does not have.
    /// </para>
    /// </summary>
    public decimal PrepaidAmount { get; set; }

    // The buyer block as it was on the paper first handed over (docs/07 §4.4, accepted in §8
    // Q12). Frozen at issue for the same reason SaleLine.NameSnapshot exists: a customer who
    // moves, renames or changes tax number must not make a reprint disagree with the original,
    // and the tax authority compares the two. Copied from the customer by ReceivableDocuments
    // when the invoice is raised and never recomputed; the migration backfilled every earlier
    // invoice from the customer row it names, so null here means only "not recorded".
    // CustomerId stays the debtor for the statement and the ledger whatever these say.

    /// <summary>The customer's name as printed at issue — the legal name for a company.</summary>
    [MaxLength(200)] public string? BuyerName { get; set; }
    [MaxLength(400)] public string? BuyerAddress { get; set; }
    /// <summary>The customer's tax number at issue; its label comes from the country profile.</summary>
    [MaxLength(40)] public string? BuyerTaxId { get; set; }
    /// <summary>
    /// The customer's other fiscal identifiers at issue (<see cref="Customer.FiscalIds"/>), frozen
    /// for the same reason as the tax number. Null on an invoice raised before the column: the
    /// customer had none recorded then, and the paper prints the row's current ones.
    /// </summary>
    public Dictionary<string, string>? BuyerFiscalIds { get; set; }

    /// <summary>
    /// How the customer is to pay, as the paper says it — "Mode de paiement : CHEQUE". The
    /// <see cref="PaymentMethodDef.Key"/>, stored not referenced, like a voucher's. Words only: it
    /// settles nothing and decides nothing — a voucher does that when the money comes.
    /// </summary>
    [MaxLength(40)] public string? PaymentMethodKey { get; set; }

    [MaxLength(500)] public string? Notes { get; set; }
    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<InvoiceLine> Lines { get; set; } = new();
}

/// <summary>One billed item, frozen at the moment the invoice was raised.</summary>
public class InvoiceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    /// <summary>
    /// Where the line stands on the paper, from 1, in the order it was entered — the N° column of
    /// the company's invoice. A database returns rows in no promised order, and an invoice whose
    /// lines move about between two prints is not the paper the customer holds. 0 on every line
    /// raised before 2026-10-10, which then keeps the order it is read in.
    /// </summary>
    public int Position { get; set; }

    /// <summary>Null for a line that is not a catalog item — a delivery charge, a rental charge.</summary>
    public Guid? ProductId { get; set; }

    [MaxLength(64)] public string Sku { get; set; } = string.Empty;
    /// <summary>Captured here so history survives a catalog rename, like <see cref="SaleLine.NameSnapshot"/>.</summary>
    [MaxLength(200)] public string NameSnapshot { get; set; } = string.Empty;

    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// The days a hire line is billed for — the JOURS column of the company's own invoice. Null on
    /// a line that is not priced by the day, which multiplies by one. With it the paper adds up:
    /// quantity × daily price × days is the line, with no detail hidden in the name.
    /// </summary>
    public int? Days { get; set; }

    public decimal TaxRate { get; set; }
    public decimal LineDiscount { get; set; }

    /// <summary>Quantity × price (× days) less the discount. Stored because the customer is holding it.</summary>
    public decimal LineNet { get; set; }

    /// <summary>
    /// Which income account this line belongs to.
    ///
    /// <para>
    /// Goods sold are 700, but a line billing a rental charge is rent (706), overdue (708),
    /// maintenance recharged (707) or compensation (757) — the owner asked for those apart, and
    /// a credit note has to give the money back on the line it was taken on. Carried on the
    /// line rather than inferred later, because by then the charge it came from may have been
    /// settled and the mapping would have to be guessed.
    /// </para>
    /// </summary>
    [MaxLength(16)] public string IncomeAccountCode { get; set; } = LedgerAccounts.Sales;

    /// <summary>
    /// The rental charge this line bills, where it bills one. Unique across invoice lines, so
    /// the same charge can never be put on two invoices.
    /// </summary>
    public Guid? RentalChargeId { get; set; }
    /// <summary>Exact rental source, frozen when issued; null on legacy and non-rental lines.</summary>
    public Guid? RentalContractLineId { get; set; }
    /// <summary>A returned fragment billed after its return; excluded from future return credits.</summary>
    public Guid? RentalReturnAllocationId { get; set; }

    /// <summary>
    /// The cost per unit at which this line's <see cref="Qty"/> left the invoice's branch (P0.3) — the figure its Dr 600 /
    /// Cr 310 was posted at, kept to 10 decimals — or null when the line took nothing out of stock: an invoice over a sale
    /// or a rental note, a direct invoice issued while <see cref="StockValuationSettings.DirectInvoiceStock"/> was off, a
    /// line with no product or whose product does not track stock. 0 when the goods left with no cost recorded. A credit
    /// note brings goods back into stock only on a line where this is set, and at this cost.
    /// </summary>
    public decimal? StockUnitCost { get; set; }
    /// <summary>What <see cref="StockUnitCost"/> was taken from; null when the line took nothing out.</summary>
    public SaleCostBasis? StockCostBasis { get; set; }
}

/// <summary>
/// Money received from a customer: <i>سند قبض</i>, the voucher §5.10 names.
///
/// <para>
/// One voucher, any number of invoices. A contractor settling three invoices with one transfer
/// is the ordinary case here, and so is paying a quarter of one — which is why the amount and
/// its allocation are separate things rather than a field on the invoice.
/// </para>
/// <para>
/// ⚠ <b>A pending voucher settles nothing.</b> It is recorded, it appears on the cheques-due
/// screen, and it moves neither the ledger, nor the invoice, nor the customer's balance until
/// somebody confirms the money arrived (§5.4). Paper is not payment.
/// </para>
/// </summary>
/// <summary>What a receipt is paid for. Stored as its number; never renumber.</summary>
public enum ReceiptKind
{
    Payment = 0,
    Repair = 1,
    Loss = 2,
    /// <summary>
    /// M7b (SAL-12, D45): money taken before anything is invoiced, held on account 419 (customer advances) until it is
    /// applied to an invoice — only while the accountant has chosen 419 (<see cref="ReceivableSettings.AdvancesHeldOn"/>).
    /// It names no invoice, no contract and no return note when taken; a customer maintenance job may be named (MS-35).
    /// </summary>
    Advance = 3
}

public class ReceiptVoucher
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>Where it was taken. A cash receipt lands in this branch's box.</summary>
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The <see cref="PaymentMethodDef.Key"/> it was taken under, stored not referenced.</summary>
    [MaxLength(40)] public string MethodKey { get; set; } = string.Empty;

    /// <summary>Frozen from the definition, so renaming or retiring a tender never rewrites history.</summary>
    public PaymentBehavior Behavior { get; set; }

    /// <summary>
    /// Cash is <see cref="PaymentStatus.Confirmed"/> the moment it is taken; a cheque is
    /// <see cref="PaymentStatus.Pending"/> until it clears and <see cref="PaymentStatus.Bounced"/>
    /// if it does not.
    /// </summary>
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public decimal Amount { get; set; }

    public DateTime? ConfirmedAtUtc { get; set; }
    [MaxLength(400)] public string? StatusNote { get; set; }

    [MaxLength(100)] public string? Reference { get; set; }

    // Columns rather than a blob, for the same reason as SalePayment: "which cheques are due
    // this week" has to be a query, not a table scan.
    [MaxLength(60)] public string? InstrumentNumber { get; set; }
    [MaxLength(120)] public string? InstrumentBank { get; set; }
    public DateTime? InstrumentDueDate { get; set; }

    /// <summary>The "in exchange for" line on the printed voucher — e.g. the rental contract number and period.</summary>
    [MaxLength(300)] public string? Purpose { get; set; }

    /// <summary>
    /// What the money is for (owner, 2026-10-02, answers 19 and 26): a payment on the account, or a
    /// repair or a loss charged on a return note. A repair or a loss always names its contract and
    /// the return note that recorded it; a payment names its contract when it is one's.
    /// </summary>
    public ReceiptKind Kind { get; set; } = ReceiptKind.Payment;

    /// <summary>The rental contract this money is for; null for a payment that belongs to none.</summary>
    public Guid? ContractId { get; set; }

    /// <summary>The return note that recorded the repair or the loss this receipt is paid for.</summary>
    public Guid? ReturnNoteId { get; set; }

    /// <summary>
    /// An advance (<see cref="ReceiptKind.Advance"/>) whose VAT is due when it is received (D45, the accountant's setting):
    /// the rate the seller ticked, as a percentage. 0 on every other receipt and on an untaxed advance (VAT only on request).
    /// Frozen when the receipt is taken, so a later change of the setting never re-prices it.
    /// </summary>
    public decimal AdvanceTaxRate { get; set; }

    /// <summary>
    /// The VAT inside the advance, posted to 4457 when the money is confirmed: Amount × rate / (100 + rate), rounded the
    /// company's way. Released back (Dr 4457) part by part as the advance is applied, so an invoice it pays does not carry
    /// the same tax twice. 0 whenever <see cref="AdvanceTaxRate"/> is.
    /// </summary>
    public decimal AdvanceTaxAmount { get; set; }

    /// <summary>MS-35: the customer maintenance job this advance is taken for; cleared at that job's invoice.</summary>
    public Guid? MaintenanceJobId { get; set; }

    [MaxLength(500)] public string? Notes { get; set; }
    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<ReceiptAllocation> Allocations { get; set; } = new();
}

/// <summary>
/// Part of an advance held on 419 moved to the customer's account 411 (M7b): against an invoice — which also records a
/// <see cref="ReceiptAllocation"/> on the advance, so the invoice reads paid — or, with no invoice, released to the account
/// (before a refund, or against a debt with no invoice). Posts Dr 419 / Dr 4457 (the advance's VAT released) / Cr 411,
/// all the customer's, under <see cref="LedgerSources.CustomerAdvanceApplication"/> with this row's id. Append-only.
/// <para>
/// SAL-13 (<see cref="Kind"/> = <see cref="AdvanceMoveKind.Kept"/>): part of a customer's money kept on a cancelled order
/// and moved to the "other income" account the accountant chose — from an advance on 419 (its VAT part stays due on 4457),
/// or from a receipt's unallocated credit on 411. Posts under <see cref="LedgerSources.CustomerAdvanceKept"/> with this
/// row's id. It counts against what the advance still holds exactly as an application does.
/// </para>
/// </summary>
public class CustomerAdvanceApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReceiptVoucherId { get; set; }

    /// <summary>The invoice it pays; null when the advance was released to the customer's account.</summary>
    public Guid? InvoiceId { get; set; }

    /// <summary>The allocation it recorded on the advance, when it paid an invoice.</summary>
    public Guid? ReceiptAllocationId { get; set; }

    /// <summary>How much of the advance moved, tax included.</summary>
    public decimal Amount { get; set; }

    /// <summary>The part of the advance's VAT released with it (Dr 4457); 0 for an advance whose VAT waits for the invoice.</summary>
    public decimal TaxAmount { get; set; }

    public DateTime AppliedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(200)] public string? CreatedBy { get; set; }

    /// <summary>SAL-13: an application or release (the default), or a part kept as other income.</summary>
    public AdvanceMoveKind Kind { get; set; } = AdvanceMoveKind.Application;

    /// <summary>SAL-13: why the money was kept, written by whoever kept it. Null on an application.</summary>
    [MaxLength(400)] public string? Reason { get; set; }

    /// <summary>SAL-13: the income account credited, frozen when it was kept. Null on an application.</summary>
    [MaxLength(16)] public string? IncomeAccountCode { get; set; }
}

/// <summary>What a <see cref="CustomerAdvanceApplication"/> row did. Stored as its number.</summary>
public enum AdvanceMoveKind
{
    /// <summary>Applied to an invoice, or released to the customer's account (M7b).</summary>
    Application = 0,
    /// <summary>Kept on a cancelled order and moved to other income (SAL-13). With VAT when received, its VAT stays due.</summary>
    Kept = 1
}

/// <summary>Whether part of a customer's advance may be kept on a cancelled order (SAL-13). Stored as its number.</summary>
public enum KeptAdvancePolicy
{
    /// <summary>Not allowed, as always: what the order does not use is refunded (release, then a refund voucher).</summary>
    NotAllowed = 0,
    /// <summary>The kept part moves from the customer's account to the income account the accountant chose; the rest is refunded.</summary>
    OtherIncome = 1
}

/// <summary>Where a customer advance is held (SAL-12, D45). Stored as its number.</summary>
public enum CustomerAdvanceAccount
{
    /// <summary>On the customer's account 411, as always: an unallocated receipt and a credit balance.</summary>
    CustomerAccount = 0,
    /// <summary>On 419 (customer advances) until it is applied to an invoice.</summary>
    AdvancesAccount = 1
}

/// <summary>When the VAT inside an advance is due (D45). Stored as its number.</summary>
public enum AdvanceVatTiming
{
    /// <summary>At the invoice, as always: an advance carries no VAT.</summary>
    AtInvoice = 0,
    /// <summary>When the advance is received, on the part the seller ticks as taxed; not again at the invoice for that part.</summary>
    WhenReceived = 1
}

/// <summary>
/// The debtor book's accounting choices (M7b; owner, 2026-11-09: every accounting decision is a setting the accountant
/// sets, the default today's behaviour). One row, changed under <c>accounting.manage</c>; no row reads as the defaults.
/// </summary>
public class ReceivableSettings
{
    public const int SingletonId = 1;
    public int Id { get; set; } = SingletonId;

    /// <summary>SAL-12 / D45 question 3: where an advance is held. 411 by default.</summary>
    public CustomerAdvanceAccount AdvancesHeldOn { get; set; } = CustomerAdvanceAccount.CustomerAccount;

    /// <summary>D45 question 1: when an advance's VAT is due. At the invoice by default; "when received" only with 419.</summary>
    public AdvanceVatTiming AdvanceVat { get; set; } = AdvanceVatTiming.AtInvoice;

    /// <summary>SAL-13: whether part of an advance may be kept on a cancelled order. Not allowed by default.</summary>
    public KeptAdvancePolicy KeptAdvances { get; set; } = KeptAdvancePolicy.NotAllowed;

    /// <summary>SAL-13: the income (class 7) account a kept advance is credited to. None by default; required to allow keeping.</summary>
    [MaxLength(16)] public string? KeptAdvanceIncomeAccount { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
    [MaxLength(200)] public string? UpdatedBy { get; set; }
}

/// <summary>
/// How much of one voucher was meant for one invoice.
///
/// <para>
/// Recorded when the voucher is taken, because the clerk knows then what the cheque is for and
/// nobody will know later. It reduces the invoice only once the voucher is confirmed; until
/// then it is a claim on that invoice, which is why an invoice reports what is outstanding and
/// what is spoken for as two different figures.
/// </para>
/// </summary>
public class ReceiptAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReceiptVoucherId { get; set; }
    public ReceiptVoucher? ReceiptVoucher { get; set; }

    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>
/// The correct way to reverse an invoice, in whole or in part.
///
/// <para>
/// A raised invoice is never edited and never deleted — the ledger has worked this way since it
/// existed and the customer's copy has a number on it. A credit note is the document that says
/// so: the wrong figure stays visible, the correction sits beside it, and both are on the
/// statement.
/// </para>
/// <para>
/// It always posts, whatever raised the invoice. The revenue it gives back is in the ledger
/// either way — put there by the sale, by the rental note, or by the invoice itself.
/// </para>
/// </summary>
public class CreditNote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(40)] public string Number { get; set; } = string.Empty;

    public Guid InvoiceId { get; set; }
    public Guid? ReturnNoteId { get; set; }
    public Guid? ContractId { get; set; }
    public Invoice? Invoice { get; set; }

    /// <summary>
    /// The movement that raised this credit note automatically (owner, 2026-10-10): "sale" (a till refund,
    /// SourceId = the refund sale's id) or "rental-return" (an early return, SourceId = the return note's id).
    /// Null for a credit note made by hand. An automatic one posts nothing and moves no balance: the
    /// refund or the return already did both.
    /// </summary>
    [MaxLength(40)] public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }

    public Guid CustomerId { get; set; }
    public Guid BranchId { get; set; }

    public DateTime IssuedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Why. Mandatory: a credit note is money leaving the debtor book, and an unexplained one is
    /// indistinguishable from a debt quietly written off by whoever could reach the screen.
    /// </summary>
    [MaxLength(400)] public string Reason { get; set; } = string.Empty;

    public decimal SubTotalHt { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal GrandTotalTtc { get; set; }

    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<CreditNoteLine> Lines { get; set; } = new();
}

/// <summary>One invoice line being given back, in whole or in part.</summary>
public class CreditNoteLine
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CreditNoteId { get; set; }
    public CreditNote? CreditNote { get; set; }

    /// <summary>The line being credited. Its price and rate are inherited, never re-entered.</summary>
    public Guid InvoiceLineId { get; set; }
    public Guid? ReturnAllocationId { get; set; }
    /// <summary>Rental only: the quantity an early return gave back. Not the goods-back-to-stock figure below.</summary>
    public decimal? ReturnedQty { get; set; }

    /// <summary>
    /// The goods of this line that came back into stock with this credit note (P0.3): 0 (the default) for a credit of
    /// money only. Only on a hand-made credit note, only on an invoice line that took goods out of stock
    /// (<see cref="InvoiceLine.StockUnitCost"/>), at most this note's own <see cref="Qty"/> and at most what that line
    /// took out less what earlier credit notes brought back. They come back at the invoice line's cost (Dr 310 / Cr 600).
    /// </summary>
    public decimal StockReturnedQty { get; set; }

    /// <summary>Where the line stands on the credit note, from 1, as on <see cref="InvoiceLine.Position"/>.</summary>
    public int Position { get; set; }

    [MaxLength(64)] public string Sku { get; set; } = string.Empty;
    [MaxLength(200)] public string NameSnapshot { get; set; } = string.Empty;

    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>Inherited from the invoice line with its price, so the credit note's line adds up the same way.</summary>
    public int? Days { get; set; }
    public decimal TaxRate { get; set; }
    public decimal LineNet { get; set; }

    /// <summary>Inherited from the invoice line, so the money goes back on the line it came off.</summary>
    [MaxLength(16)] public string IncomeAccountCode { get; set; } = LedgerAccounts.Sales;
}
