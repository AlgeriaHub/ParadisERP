import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

/**
 * The receivables module's wire types and its one client.
 *
 * <p>Written against `ParadisERP.API/Endpoints/ReceivableEndpoints.cs` and the DTOs in
 * `ParadisERP.Application/Contracts/Dtos.cs` — what the server actually returns — rather than
 * against the design document. Where the two differ the endpoint wins, and the difference is
 * noted here so the next person does not have to re-read the C#.</p>
 *
 * <p>It lives beside the screens rather than in `core/` on purpose: nothing outside this folder
 * imports it, so the whole module stays in the deferred chunk and never reaches a till's initial
 * download. Sales come from `AdminService.listSales` and rental notes from `RentalsApi`; nothing
 * of theirs is retyped here.</p>
 *
 * <p>Every date is an ISO 8601 UTC instant. Rows read back from SQL Server may arrive without the
 * trailing `Z`; see `withZ` in `receivable-view.ts` before handing one to `new Date()`.</p>
 */

/** Derived, never stored: a fully claimed pending cheque leaves an invoice `Open`. */
export type InvoiceStatus = 'Open' | 'PartlyPaid' | 'Settled' | 'Credited';
/** MaintenanceJob (Phase M5a): raised from a customer maintenance job; like Direct, it posts itself. */
export type InvoiceOrigin = 'Direct' | 'Sale' | 'Rental' | 'MaintenanceJob';
export type TaxMode = 'Taxed' | 'Untaxed';
export type AgeingBucket = 'Current' | 'Days1To30' | 'Days31To60' | 'Days61To90' | 'Over90';
export type ReceiptStatus = 'Pending' | 'Confirmed' | 'Bounced';
export type PaymentBehavior = 'Cash' | 'Card' | 'Credit' | 'GiftCard' | 'Loyalty' | 'Generic';
/** Ledger source keys that can appear on a statement row or as an invoice's sourceType. */
export type LedgerSourceType =
  | 'sale'
  | 'payment-settlement'
  | 'rental-issue'
  | 'rental-return'
  | 'invoice'
  | 'customer-receipt'
  // M7b: part of an advance held on 419 applied to an invoice or released to 411 (never a statement row: it nets to nothing).
  | 'customer-advance-application'
  // SAL-13: part of an advance (419) or of a receipt's unallocated credit (411) kept on a cancelled order — a debit row.
  | 'customer-advance-kept'
  | 'credit-note'
  | 'payment-voucher'
  // A customer's opening balance and its corrections (2026-10-10), on the customer's / correction's id.
  | 'customer-opening'
  | 'customer-opening-correction'
  // The daily-pricing restatement's adjustments (2026-10-06), on the id of the document each corrects.
  | 'correction.rental-issue-daily'
  | 'correction.rental-return-daily'
  | 'correction.rental-credit-daily';
/** 700 goods, 706 rent, 707 maintenance recharged, 708 overdue, 757 damage compensation. */
export type IncomeAccountCode = '700' | '706' | '707' | '708' | '757' | string;

// ---------- invoices ----------

export interface InvoiceLineRequest {
  /** When set the server takes the product's sku and, if `name` is blank, its French name. */
  productId?: string | null;
  sku?: string | null;
  /** The snapshot printed on the paper. Send it explicitly to control the language. */
  name?: string | null;
  qty: number;
  /** Net of tax, >= 0. */
  unitPrice: number;
  /** Percent, 0..100; stored as 0 when taxMode is 'Untaxed'. */
  taxRate: number;
  /** Absolute money off this line, not a percent; 0..qty*unitPrice*days. */
  discount?: number;
  /** The days a hire line is billed for, 1..3660; omitted or null multiplies by one. */
  days?: number | null;
}

/**
 * Exactly one of saleId / issueNoteId / returnNoteId, or none for a typed (direct) invoice.
 * Over a source document customerId, branchId, taxMode and lines are ignored — they come from
 * the document.
 */
export interface CreateInvoiceRequest {
  /** Required; on or after issuedAtUtc by date. */
  dueDateUtc: string;
  customerId?: string | null;
  branchId?: string | null;
  /** Default server now; may be back-dated, in which case the invoice is born overdue. */
  issuedAtUtc?: string | null;
  /** Anything but 'Untaxed' means Taxed. */
  taxMode?: TaxMode | null;
  /** <= 500; over a document defaults to 'Sale {ref}' / 'Rental {number}'. */
  notes?: string | null;
  lines?: InvoiceLineRequest[] | null;
  saleId?: string | null;
  issueNoteId?: string | null;
  returnNoteId?: string | null;
  /** How the customer is to pay, printed on the invoice: an active payment method's key. Words only. */
  paymentMethodKey?: string | null;
  /**
   * 'full' records and allocates payment on issue with settlementMethodKey;
   * 'deferred' stays open for later receipt vouchers.
   */
  settlement?: 'full' | 'deferred' | null;
  /** The payment method that paid the invoice in full on issue. */
  settlementMethodKey?: string | null;
}

export interface InvoiceLine {
  id: string;
  productId: string | null;
  sku: string;
  /**
   * Frozen at issue. A hire billed at issue has its quantity, daily price and days in their own
   * fields; a return-note charge, and a rental line raised before 2026-10-10, embeds French detail
   * like '60 × 30 j @ 100/jour'.
   */
  name: string;
  qty: number;
  unitPrice: number;
  taxRate: number;
  /** Absolute money. */
  discount: number;
  /** qty × unitPrice (× days) − discount, rounded: what the customer was charged on this line, net. */
  lineNet: number;
  /** The days a hire line is billed for — the JOURS column; null or absent on a line not priced by the day. */
  days?: number | null;
  incomeAccountCode: IncomeAccountCode;
  /** Already given back on credit notes; qty − creditedQty is what can still be credited. */
  creditedQty: number;
  /**
   * P0.3: what this line took out of stock — its whole qty when the invoice moved its goods, 0 otherwise (an invoice
   * over a sale or a rental note, or one issued while direct invoices moved no stock). Absent from older servers.
   */
  stockOutQty?: number;
  /** What credit notes have brought back into stock of it; stockOutQty − stockReturnedQty can still come back. */
  stockReturnedQty?: number;
}

export interface Invoice {
  id: string;
  /** 'INV-000001'. */
  number: string;
  customerId: string;
  customerName: string | null;
  branchId: string;
  issuedAtUtc: string;
  dueDateUtc: string;
  taxMode: TaxMode;
  origin: InvoiceOrigin;
  /** Set when origin != 'Direct': 'sale' | 'rental-issue' | 'rental-return'. */
  sourceType: LedgerSourceType | null;
  sourceId: string | null;
  notes: string | null;
  subTotalHt: number;
  taxTotal: number;
  grandTotalTtc: number;
  /**
   * Settled on the source document before this invoice existed — the cash half of a mixed
   * sale, the unused rental days. Never owed, never allocatable. 0 on a direct invoice.
   */
  prepaid: number;
  /** Σ grandTotalTtc of this invoice's credit notes. */
  credited: number;
  /** Σ allocations from confirmed vouchers — money that actually arrived. */
  received: number;
  /** Σ allocations from pending vouchers — paper spoken for this invoice, NOT taken off outstanding. */
  claimed: number;
  /** grandTotalTtc − prepaid − credited − received; negative when credited past what was owed. */
  outstanding: number;
  status: InvoiceStatus;
  /** Whole days past dueDateUtc by date, server clock; 0 once outstanding <= 0. */
  daysOverdue: number;
  ageingBucket: AgeingBucket;
  lines: InvoiceLine[];
  // For the printed invoice (docs/07 §4.4); absent from a server older than 2026-09-24.
  /** The paper this one bills — the sale's receipt reference or the rental note's number; null on a direct invoice. */
  sourceNumber?: string | null;
  /**
   * The buyer block frozen at issue (§8 Q12). `customerName` above is the customer's current
   * name; a printer uses these and falls back to the row only when one is null — after the
   * backfill that means "not recorded", never "unchanged".
   */
  buyerName?: string | null;
  buyerAddress?: string | null;
  buyerTaxId?: string | null;
  /** The buyer's other fiscal identifiers at issue (RC, AI, NIS…), by profile key; null before 2026-10-10. */
  buyerFiscalIds?: Record<string, string> | null;
  /** How the customer is to pay — a payment method's key, printed "Mode de paiement". Words only. */
  paymentMethodKey?: string | null;
}

/** Not on the wire: what a new voucher may still allocate to this invoice. */
export const allocatable = (invoice: Invoice): number => invoice.outstanding - invoice.claimed;

/** POST /invoices always answers with this envelope; `warning` sits beside the document, never instead of it. */
export interface CreateInvoiceResponse {
  invoice: Invoice;
  /** 'Customer credit limit exceeded (X > Y).' or null — English, unformatted decimals. */
  warning: string | null;
}

// ---------- credit notes ----------

export interface CreditNoteLineRequest {
  invoiceLineId: string;
  /** > 0, <= qty − creditedQty. */
  qty: number;
  /** P0.3: goods of the line back into stock with this note; 0 by default, <= qty and <= what is still out. */
  stockReturnedQty?: number;
}

export interface CreateCreditNoteRequest {
  /** Required, non-blank, <= 400. */
  reason: string;
  issuedAtUtc?: string | null;
  /** Omit or send empty to credit everything still left on every line. */
  lines?: CreditNoteLineRequest[] | null;
}

export interface CreditNoteLine {
  id: string;
  invoiceLineId: string;
  sku: string;
  name: string;
  qty: number;
  unitPrice: number;
  taxRate: number;
  /** Pro-rata on the invoice line's own net, so net of its discount. */
  lineNet: number;
  incomeAccountCode: IncomeAccountCode;
  /** Inherited from the invoice line with its price. */
  days?: number | null;
  /** P0.3: goods of the line this note brought back into stock. */
  stockReturnedQty?: number;
}

export interface CreditNote {
  id: string;
  /** 'CRN-000001'. */
  number: string;
  invoiceId: string;
  invoiceNumber: string | null;
  customerId: string;
  branchId: string;
  issuedAtUtc: string;
  reason: string;
  subTotalHt: number;
  taxTotal: number;
  grandTotalTtc: number;
  lines: CreditNoteLine[];
}

// ---------- receipts ----------

export interface ReceiptAllocationRequest {
  invoiceId: string;
  /** > 0, <= allocatable(invoice). */
  amount: number;
}

export interface CreateReceiptRequest {
  customerId: string;
  /** Where the money was taken; a cash receipt lands in this branch's cash box. */
  branchId: string;
  amount: number;
  /** PaymentMethodDto.key ('cash', 'card', 'cheque', operator keys). Not credit/giftcard/loyalty. */
  methodKey: string;
  receivedAtUtc?: string | null;
  /** <= 100 — the form enforces it when method.requiresReference; the server does not. */
  reference?: string | null;
  /** <= 60 — form-enforced when method.requiresInstrumentDetails. */
  instrumentNumber?: string | null;
  /** <= 120. */
  instrumentBank?: string | null;
  /** Drives the 'cheques due' ordering. */
  instrumentDueDate?: string | null;
  /** <= 500. */
  notes?: string | null;
  /** Σ amount <= receipt amount; the rest is money on account and cannot be allocated later. */
  allocations?: ReceiptAllocationRequest[] | null;
  /**
   * The "in exchange for" line (answer 23), <= 300. Blank lets the server write the rental
   * contract and period of the allocated invoices — `receiptPurpose` asks it for that line first.
   */
  purpose?: string | null;
  /** What the money is for (answers 19, 26). Absent = a payment. */
  kind?: ReceiptKind | null;
  /** The rental contract it is for; required for a repair or a loss. */
  contractId?: string | null;
  /** The return note that recorded the repair or the loss; only for those two. */
  returnNoteId?: string | null;
  /** M7b, an advance only: the VAT rate the seller ticked when VAT is due on receipt; null/0 = untaxed (the default). */
  advanceTaxRate?: number | null;
  /** M7b, an advance only (MS-35): the customer maintenance job it is taken for. */
  maintenanceJobId?: string | null;
}

/**
 * What a receipt is paid for: a payment on the account, a repair or a loss on a return note, or (M7b, only while
 * advances are held on 419) an advance applied to an invoice later.
 */
export type ReceiptKind = 'Payment' | 'Repair' | 'Loss' | 'Advance';

/** M7b: where a customer advance is held, and when its VAT is due (the accountant's settings). */
export type AdvancesHeldOn = 'CustomerAccount' | 'AdvancesAccount';
export type AdvanceVat = 'AtInvoice' | 'WhenReceived';

export interface ReceivableSettings {
  advancesHeldOn: AdvancesHeldOn;
  advanceVat: AdvanceVat;
  /** Account 419 is in the chart and active; 419 is refused otherwise. */
  advancesAccountReady: boolean;
  /** Confirmed advances still held on 419, all customers. */
  openAdvances: number;
  updatedAtUtc: string | null;
  updatedBy: string | null;
  /** SAL-13: whether part of an advance may be kept on a cancelled order; absent from a server before 2026-11-10. */
  keptAdvances?: KeptAdvances;
  /** SAL-13: the income (class 7) account a kept advance is credited to. */
  keptIncomeAccount?: string | null;
  /** SAL-13: the chart's active class 7 income accounts it may be chosen from. */
  incomeAccounts?: IncomeAccountChoice[] | null;
}

/** SAL-13: not allowed (the default, today) or kept as other income. */
export type KeptAdvances = 'NotAllowed' | 'OtherIncome';

export interface IncomeAccountChoice {
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
}

/** Left out, the SAL-13 fields keep what is saved. */
export interface SaveReceivableSettingsRequest {
  advancesHeldOn: AdvancesHeldOn;
  advanceVat: AdvanceVat;
  keptAdvances?: KeptAdvances;
  keptIncomeAccount?: string | null;
}

/**
 * One application of an advance to an invoice, or a release to the customer's account (no invoice) — or (SAL-13,
 * kind 'Kept') a part kept as other income, with its reason; its taxAmount is then the VAT that stays due.
 */
export interface AdvanceApplication {
  id: string;
  invoiceId: string | null;
  invoiceNumber: string | null;
  amount: number;
  taxAmount: number;
  appliedAtUtc: string;
  createdBy: string | null;
  kind?: 'Application' | 'Kept';
  reason?: string | null;
  incomeAccountCode?: string | null;
}

/** SAL-13: no amount = all the receipt still holds (the server caps it); the reason is required. */
export interface KeepAdvanceRequest {
  amount?: number | null;
  reason: string;
}

/** No invoice = released to the customer's account; no amount = as much as the advance holds and the invoice owes. */
export interface ApplyAdvanceRequest {
  invoiceId?: string | null;
  amount?: number | null;
}

/** A contract a voucher may name, with its return notes for a repair or a loss. */
export interface VoucherContractChoice {
  id: string;
  number: string;
  customerId: string;
  customerName: string | null;
  status: string;
  returnNotes: { id: string; number: string; returnedAtUtc: string }[];
}

export interface SettleReceiptRequest {
  status: 'Confirmed' | 'Bounced';
  /** <= 400, stored as statusNote. */
  note?: string | null;
}

export interface ReceiptAllocation {
  invoiceId: string;
  invoiceNumber: string | null;
  amount: number;
}

export interface ReceiptVoucher {
  id: string;
  /** 'RCV-000001'. */
  number: string;
  customerId: string;
  customerName: string | null;
  branchId: string;
  receivedAtUtc: string;
  methodKey: string;
  /** Frozen from the tender: 'Cash' reaches the drawer; anything else clears to Bank. */
  behavior: PaymentBehavior;
  /** 'Confirmed' at once when the tender needs no confirmation, else 'Pending' until settled. A Pending voucher settles nothing. */
  status: ReceiptStatus;
  amount: number;
  /** Σ allocations, whatever the status. */
  allocated: number;
  /** amount − allocated − kept: money on account. */
  unallocated: number;
  /** SAL-13: how much of it was kept as other income on a cancelled order. */
  kept?: number;
  /** Misleading name: the moment the voucher was SETTLED — set on Bounced too. Null while Pending. */
  confirmedAtUtc: string | null;
  statusNote: string | null;
  reference: string | null;
  instrumentNumber: string | null;
  instrumentBank: string | null;
  instrumentDueDate: string | null;
  notes: string | null;
  allocations: ReceiptAllocation[];
  /** The "in exchange for" line; absent from a server older than 2026-09-30. */
  purpose?: string | null;
  kind?: ReceiptKind;
  contractId?: string | null;
  contractNumber?: string | null;
  returnNoteId?: string | null;
  returnNoteNumber?: string | null;
  /** M7b, an advance: its VAT posted on receipt (0 untaxed or due at the invoice), what 419 still holds, its job, its applications. */
  advanceTaxRate?: number;
  advanceTaxAmount?: number;
  advanceOpen?: number;
  maintenanceJobId?: string | null;
  maintenanceJobNumber?: string | null;
  advanceApplications?: AdvanceApplication[] | null;
}

// ---------- statement (account 411 for one customer) ----------

export type StatementKind = 'sale' | 'rental' | 'receipt' | 'payment' | 'opening' | 'adjustment' | 'other';

export interface CustomerStatementRow {
  date: string;
  sourceType: LedgerSourceType;
  sourceId: string;
  /** The document's own number (T-0001, ISS-…, RET-…, INV-…, RCV-…, CRN-…); falls back to the ledger memo. */
  documentNumber: string | null;
  /** Only on a sale / rental-note row that has since been invoiced — the paper the customer holds. */
  invoiceNumber: string | null;
  description: string | null;
  isReversal: boolean;
  debit: number;
  credit: number;
  /** Running 411 balance after this row. */
  balance: number;
  kind: StatementKind;
  paidAtTill: number;
}

export type StatementFilter = 'all' | 'sales' | 'rentals';

export interface CustomerStatement {
  customerId: string;
  customerCode: string;
  customerName: string;
  from: string;
  /** Exclusive. */
  to: string;
  /** Σ 411 before `from`. */
  openingBalance: number;
  /** Trade debt (411) at `to`. */
  closingBalance: number;
  totalDebit: number;
  totalCredit: number;
  rows: CustomerStatementRow[];
  /** Uncleared TILL paper (account 413) — cheques taken on sales. Not pending receipt vouchers; those post nothing. */
  pendingNotes: number;
  /** closingBalance + pendingNotes. */
  totalOwed: number;
  /** The denormalised Customer.Balance the credit-limit warning reads. */
  recordedBalance: number;
  /** recordedBalance − totalOwed. Should be 0; anything else is drift worth showing in words. */
  balanceDifference: number;
  creditLimit: number;
  /** Σ outstanding over ALL this customer's invoices (all time, negatives included). */
  invoicedOutstanding: number;
  /** closingBalance − invoicedOutstanding: debt no invoice covers, which ageing cannot see. */
  uninvoicedBalance: number;
  filter: StatementFilter;
}

// ---------- ageing ----------

export interface CustomerAgeing {
  customerId: string;
  customerCode: string;
  customerName: string;
  creditLimit: number;
  /** Sum of the five buckets = positive outstanding of invoices issued <= asOf. */
  total: number;
  /** Owed, not yet late — not 'nothing owed'. */
  current: number;
  days1To30: number;
  days31To60: number;
  days61To90: number;
  over90: number;
  oldestDaysOverdue: number;
  /** Money the company owes this customer, kept out of the buckets, as a positive number. */
  creditBalance: number;
  /** 411 up to asOf. */
  ledgerBalance: number;
  /** ledgerBalance − (total − creditBalance): debt with no invoice behind it. */
  uninvoiced: number;
  /** creditLimit > 0 && ledgerBalance > creditLimit — reads 411 only, unlike the creation warning. */
  overCreditLimit: boolean;
}

export interface DebtAgeing {
  asOf: string;
  total: number;
  current: number;
  days1To30: number;
  days31To60: number;
  days61To90: number;
  over90: number;
  creditBalance: number;
  ledgerBalance: number;
  uninvoiced: number;
  /** Sorted by name; a customer with nothing owed, nothing credited and 411 = 0 is omitted. */
  customers: CustomerAgeing[];
}

// ---------- the client ----------

export interface InvoiceListQuery {
  customerId?: string | null;
  /** ISO; issuedAtUtc >= from. */
  from?: string | null;
  /** ISO; issuedAtUtc < to. */
  to?: string | null;
  /** Default 200, clamped 1..1000 server-side. */
  take?: number | null;
}

export interface CreditNoteListQuery {
  customerId?: string | null;
  invoiceId?: string | null;
  take?: number | null;
}

export interface ReceiptListQuery {
  customerId?: string | null;
  status?: ReceiptStatus | null;
  take?: number | null;
  /** One branch's receipts; absent or null, every branch the caller may see (the server decides). */
  branchId?: string | null;
}

function query(params: URLSearchParams): string {
  const text = params.toString();
  return text ? `?${text}` : '';
}

@Injectable({ providedIn: 'root' })
export class ReceivablesApi {
  private readonly api = inject(ApiService);

  // ---------- invoices ----------

  /**
   * Newest first. `status` and `overdue` are deliberately not sent: the server applies them in
   * memory AFTER `take`, so a filtered request answers "the Open ones among the N most recent",
   * not "the N most recent Open ones". Screens filter what they loaded and say when it was capped.
   */
  listInvoices(q: InvoiceListQuery = {}): Promise<Invoice[]> {
    const params = new URLSearchParams();
    if (q.customerId) params.set('customerId', q.customerId);
    if (q.from) params.set('from', q.from);
    if (q.to) params.set('to', q.to);
    if (q.take) params.set('take', String(q.take));
    return this.api.getJson<Invoice[]>(`/api/receivables/invoices${query(params)}`);
  }

  /** 404 with an EMPTY body when unknown — `adminErrorMessage` then prints "HTTP 404". */
  getInvoice(id: string): Promise<Invoice> {
    return this.api.getJson<Invoice>(`/api/receivables/invoices/${id}`);
  }

  /** The one POST that answers with an envelope rather than the DTO. */
  createInvoice(req: CreateInvoiceRequest): Promise<CreateInvoiceResponse> {
    return this.api.postJson<CreateInvoiceResponse>('/api/receivables/invoices', req);
  }

  // ---------- credit notes ----------

  createCreditNote(invoiceId: string, req: CreateCreditNoteRequest): Promise<CreditNote> {
    return this.api.postJson<CreditNote>(`/api/receivables/invoices/${invoiceId}/credit-notes`, req);
  }

  /** 404 with an EMPTY body when unknown. The printed credit note reads it here. */
  getCreditNote(id: string): Promise<CreditNote> {
    return this.api.getJson<CreditNote>(`/api/receivables/credit-notes/${id}`);
  }

  listCreditNotes(q: CreditNoteListQuery = {}): Promise<CreditNote[]> {
    const params = new URLSearchParams();
    if (q.customerId) params.set('customerId', q.customerId);
    if (q.invoiceId) params.set('invoiceId', q.invoiceId);
    if (q.take) params.set('take', String(q.take));
    return this.api.getJson<CreditNote[]>(`/api/receivables/credit-notes${query(params)}`);
  }

  // ---------- receipts ----------

  /** Ordered by (instrumentDueDate ?? receivedAtUtc) ascending: `?status=Pending` is already the list of paper to chase, oldest due first. */
  listReceipts(q: ReceiptListQuery = {}): Promise<ReceiptVoucher[]> {
    const params = new URLSearchParams();
    if (q.customerId) params.set('customerId', q.customerId);
    if (q.status) params.set('status', q.status);
    if (q.take) params.set('take', String(q.take));
    if (q.branchId) params.set('branchId', q.branchId);
    return this.api.getJson<ReceiptVoucher[]>(`/api/receivables/receipts${query(params)}`);
  }

  /** 404 with an EMPTY body when unknown. */
  getReceipt(id: string): Promise<ReceiptVoucher> {
    return this.api.getJson<ReceiptVoucher>(`/api/receivables/receipts/${id}`);
  }

  /** The contracts a voucher may name: one customer's, or the newest when none is given. */
  contractChoices(customerId?: string | null): Promise<VoucherContractChoice[]> {
    const params = new URLSearchParams();
    if (customerId) params.set('customerId', customerId);
    return this.api.getJson<VoucherContractChoice[]>(`/api/receivables/contracts${query(params)}`);
  }

  createReceipt(req: CreateReceiptRequest): Promise<ReceiptVoucher> {
    return this.api.postJson<ReceiptVoucher>('/api/receivables/receipts', req);
  }

  /** Only a Pending voucher can be settled, once, either way; there is no un-settle. */
  /** The line the server writes when a receipt against these invoices leaves its purpose blank; null when none is rental. */
  receiptPurpose(invoiceIds: readonly string[]): Promise<{ purpose: string | null }> {
    const params = new URLSearchParams({ invoiceIds: invoiceIds.join(',') });
    return this.api.getJson<{ purpose: string | null }>(`/api/receivables/receipts/purpose${query(params)}`);
  }

  settleReceipt(id: string, req: SettleReceiptRequest): Promise<ReceiptVoucher> {
    return this.api.postJson<ReceiptVoucher>(`/api/receivables/receipts/${id}/settle`, req);
  }

  /** M7b: part of an advance held on 419 applied to an invoice, or released to the customer's account. */
  applyAdvance(id: string, req: ApplyAdvanceRequest): Promise<ReceiptVoucher> {
    return this.api.postJson<ReceiptVoucher>(`/api/receivables/receipts/${id}/apply`, req);
  }

  /** SAL-13: part of an advance (or of a receipt's unallocated credit) kept as other income on a cancelled order. */
  keepAdvance(id: string, req: KeepAdvanceRequest): Promise<ReceiptVoucher> {
    return this.api.postJson<ReceiptVoucher>(`/api/receivables/receipts/${id}/keep`, req);
  }

  // ---------- settings (M7b) ----------

  settings(): Promise<ReceivableSettings> {
    return this.api.getJson<ReceivableSettings>('/api/receivables/settings');
  }

  saveSettings(req: SaveReceivableSettingsRequest): Promise<ReceivableSettings> {
    return this.api.putJson<ReceivableSettings>('/api/receivables/settings', req);
  }

  // ---------- reports ----------

  /** `to` is exclusive; the window may not exceed MAX_PERIOD_DAYS. Both omitted = the current UTC month. */
  statement(customerId: string, fromIso?: string | null, toIso?: string | null, kind?: StatementFilter | null): Promise<CustomerStatement> {
    const params = new URLSearchParams({ customerId });
    if (fromIso) params.set('from', fromIso);
    if (toIso) params.set('to', toIso);
    if (kind && kind !== 'all') params.set('kind', kind);
    return this.api.getJson<CustomerStatement>(`/api/receivables/statement${query(params)}`);
  }

  ageing(asOfIso?: string | null, customerId?: string | null): Promise<DebtAgeing> {
    const params = new URLSearchParams();
    if (asOfIso) params.set('asOf', asOfIso);
    if (customerId) params.set('customerId', customerId);
    return this.api.getJson<DebtAgeing>(`/api/receivables/ageing${query(params)}`);
  }
}
