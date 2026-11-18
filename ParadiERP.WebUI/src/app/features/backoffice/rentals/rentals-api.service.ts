import { Injectable, inject } from '@angular/core';
import { ApiError, ApiService } from '../../../core/api.service';
import { CustomerKind } from '../../../core/admin.service';
import { getMeta } from '../../../core/db';
import { TaxMode } from '../../../core/models';

/**
 * The rental module's wire types and its one client.
 *
 * <p>Written against `ParadiERP.API/Endpoints/RentalEndpoints.cs` — what the server actually
 * returns — rather than against the design document. Where the two differ the endpoint wins,
 * and the difference is noted here so the next person does not have to re-read the C#.</p>
 *
 * <p>It lives beside the screens rather than in `core/` on purpose: nothing outside this folder
 * imports it, so the whole module — types, client and the arithmetic it drives — stays in the
 * deferred chunk and never reaches a till's initial download.</p>
 */

export interface RentalSettings {
  overdueMultiplier: number;
  /**
   * The deposit switch. On, a deposit is entered on the contract, frozen at the first issue note,
   * shown while it is held and stated as owed back at close. Nothing collects or refunds it yet.
   */
  depositEnabled: boolean;
  /** Percentage. 0 — the default — means hire is not taxed here; it is not a missing value. */
  taxRate: number;
  /**
   * The local hour (0–23) that splits a day for delivery and return (answers 5 and 6); null keeps
   * the old rule — the delivery day counts, the return day never does.
   */
  dayCutoffHour?: number | null;
  /** The minimum hire in days; 0 — the default — charges the days used. New contracts only. */
  minimumDays?: number;
}

/**
 * One item's own overdue multiplier, as `GET|PUT /settings/products[/{id}]` answer it.
 * `overdueMultiplier` null means the item is on the company figure.
 */
export interface ProductOverdueMultiplier {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  overdueMultiplier: number | null;
  /** Predefined ordinary hire price; zero means the administrator has not set one yet. */
  rentalUnitPrice: number;
  /** The fixed replacement value per unit (answer 13) a contract line starts from; null when none is set. */
  rentalDamageUnitPrice?: number | null;
}

/** The ten members a list row and the full contract share. */
export interface RentalContractHead {
  id: string;
  number: string;
  customerId: string;
  customerName: string | null;
  branchId: string;
  status: string;
  isLocked: boolean;
  lockedAtUtc: string | null;
  signedAtUtc: string;
  closedAtUtc: string | null;
}

/**
 * A row of `GET /contracts`, carrying its own answer to the list's question — what is out, and
 * what it has cost — so the screen no longer settles each contract to find out.
 *
 * <p>The money is **what was charged**: sums over the stored charges, which agree with the
 * contract screen's recomputed settlement by construction. A future engine correction would show
 * on the contract before it showed here.</p>
 */
export interface RentalContractRow extends RentalContractHead {
  issuedQty: number;
  settledQty: number;
  stillOutQty: number;
  chargeTotal: number;
  /** Charges of kind Overdue only — what the "returned late" chip stands on. */
  overdueTotal: number;
  /** Absent while the deposit is switched off (§5.6): no column, no zero. */
  depositAmount?: number | null;
}

export interface RentalContractLine {
  /** Authoritative quantity × duration × daily unit rate, calculated by the server. */
  rentalTotal?: number;
  id: string;
  productId: string;
  sku: string;
  /** The name frozen onto the line when it was signed — French, whatever the reader speaks. */
  name: string;
  qty: number;
  durationDays: number;
  rentUnitPrice: number;
  damageUnitPrice: number;
  overdueUnitPrice: number;
  parentLineId: string | null;
  issuedQty: number;
  leftToIssueQty: number;
  settledQty: number;
  stillOutQty: number;
  /** The rate frozen on the line at signing; 0 is untaxed hire. For the printed contract. */
  taxRate: number;
  /** The Arabic name frozen at signing, printed beside the French (answer 28); null on older lines. */
  nameAr?: string | null;
}

/** One file kept with a contract — today the scan of the signatory's identity document. Never the bytes. */
export interface RentalAttachment {
  id: string;
  kind: string;
  fileName: string;
  contentType: string;
  length: number;
  uploadedAtUtc: string;
}

/** A place a customer takes hired goods to (answer 9). Closed sites stay listed; a contract may name one. */
export interface CustomerSite {
  id: string;
  customerId: string;
  name: string;
  address: string | null;
  phone: string | null;
  isActive: boolean;
}

export interface UpsertCustomerSiteRequest {
  name: string;
  address: string | null;
  phone: string | null;
  isActive: boolean;
}

export interface RentalContract extends RentalContractHead {
  /** Sum of authoritative rental line totals, before tax or other charges. */
  rentalTotal?: number;
  notes: string | null;
  lines: RentalContractLine[];
  /**
   * Absent while the deposit is switched off (§5.6): no field, no zero line, no column. Present,
   * it is the figure signed on the paper — frozen at the first issue note with the prices.
   */
  depositAmount: number | null;
  /**
   * Who signed the paper, frozen onto the contract when it was raised. The company's
   * representative may change afterwards; this pair never does, so a screen reads the name from
   * here and never from the customer.
   */
  signatoryName: string | null;
  signatoryIdNumber: string | null;
  /**
   * False on a contract raised before the signatory rule existed: it is never refused for a blank
   * name, and a screen says it predates the rule rather than flagging it as incomplete.
   */
  signatoryRequired: boolean;
  /** The customer's kind at read time — it decides the wording ("for the company"), nothing else. */
  customerKind: CustomerKind;
  /** The site as snapshotted at signing; all three null on a contract raised before the site rule. */
  siteId?: string | null;
  siteName?: string | null;
  siteAddress?: string | null;
  /** The rest of the signatory's identity (answer 10), snapshotted with the name. Dates are 'YYYY-MM-DD'. */
  signatoryIdType?: string | null;
  signatoryIdIssuer?: string | null;
  signatoryIdIssuedOn?: string | null;
  signatoryBirthDate?: string | null;
  /** The lorry named on the contract (answer 12); each note carries its own. */
  truckType?: string | null;
  truckNumber?: string | null;
  truckOwnerPhone?: string | null;
  attachments?: RentalAttachment[];
  /**
   * Answers 15 and 22 (2026-10-02): when the goods were all back, what the contract is worth on
   * the customer's account, what confirmed receipts have paid against it, and a short close.
   */
  goodsBackAtUtc?: string | null;
  owed?: number;
  paid?: number;
  closedShortReason?: string | null;
  closedShortBy?: string | null;
  /** Called off before any exit note (owner, 2026-10-06): when, by whom, why. */
  cancelledAtUtc?: string | null;
  cancelledBy?: string | null;
  cancelReason?: string | null;
  /** The minimum hire frozen when the contract was raised; 0 for none. */
  minimumRentalDays?: number;
  /** Answer 8: the contract this one extends, and the one that extended it. */
  extendsContractId?: string | null;
  extendsContractNumber?: string | null;
  extendedByContractId?: string | null;
  extendedByContractNumber?: string | null;
  /** The named wording it prints (PinSay #302): a key of the company's contract wordings; null is the standard one. */
  wordingKey?: string | null;
  /** A person customer's contract signed by an agent rather than the customer himself (owner, 2026-10-06). */
  signedByAgent?: boolean;
  /**
   * Whether its bills carry VAT (owner, 2026-11-02: VAT is optional on every bill). Untaxed lines froze 0, taxed
   * ones the company's hire rate. Absent from an older server: taxed.
   */
  taxMode?: TaxMode;
}

export interface RentalContractLineRequest {
  productId: string;
  qty: number;
  durationDays: number;
  rentUnitPrice: number;
  damageUnitPrice: number;
  /** Left null the server derives it from the multiplier in force; sent when it was negotiated. */
  overdueUnitPrice: number | null;
  /** A position in this request's own line list — the parent has no id until the contract saves. */
  parentLineIndex: number | null;
}

export interface UpsertRentalContractRequest {
  customerId: string;
  branchId: string;
  lines: RentalContractLineRequest[];
  signedAtUtc: string | null;
  notes: string | null;
  /**
   * Blank means "prefill for me": the server derives the pair from the person or from the company's
   * current representative. Sent, the typed name wins — the actual signer may be a delegate.
   */
  signatoryName: string | null;
  signatoryIdNumber: string | null;
  /** Stored only while the deposit switch is on; ignored — and never negative-checked — while off. */
  depositAmount: number;
  /** One of the customer's open sites; mandatory on a new contract. */
  siteId: string | null;
  /** Filled only where the customer's record (or the representative's) does not hold them. */
  signatoryIdType: string | null;
  signatoryIdIssuer: string | null;
  signatoryIdIssuedOn: string | null;
  signatoryBirthDate: string | null;
  truckType: string | null;
  truckNumber: string | null;
  truckOwnerPhone: string | null;
  /**
   * The named wording it prints (PinSay #302). Always sent: '' is the standard wording, a key one of
   * the company's contract wordings; null would keep the stored one (how the server reads an older client).
   */
  wordingKey: string;
  /**
   * A person customer's agent signs (owner, 2026-10-06): the signatory fields are then the agent's,
   * as typed. False is the customer himself; ignored for a company.
   */
  signedByAgent: boolean;
  /**
   * Always sent. 'Untaxed' needs the untaxed-invoice permission (finance.invoices.untaxed) unless the draft already
   * is; the server answers 403 `rental_untaxed_forbidden` otherwise. Taking a contract back to 'Taxed' needs nothing.
   */
  taxMode: TaxMode;
}

export interface IssueNoteLine {
  contractLineId: string;
  productId: string;
  sku: string;
  qty: number;
}

export interface IssueNote {
  /** Carried over from the contract this one extends: no stock moved, no truck (answer 8). */
  isCarryOver?: boolean;
  id: string;
  number: string;
  contractId: string;
  branchId: string;
  issuedAtUtc: string;
  userName: string | null;
  note: string | null;
  /** What this note alone invoiced: the full agreed period on everything it put out. */
  periodRent: number;
  lines: IssueNoteLine[];
  driverName?: string | null;
  driverPhone?: string | null;
  truckNumber?: string | null;
  /** The customer carried the goods himself; no driver or truck is on the note. */
  customerTransport?: boolean;
  /**
   * The first day of this note's period as the server counts it — the delivery day, or the next
   * one for goods out after the company's cutoff hour. Absent from an older server.
   */
  periodStartsOn?: string | null;
}

export interface ReturnNoteLine {
  sameDayUsage?: SameDayUsage | 'Legacy' | 'NotApplicable';
  contractLineId: string;
  productId: string;
  sku: string;
  qtyOk: number;
  qtyPartialDamage: number;
  maintenanceValue: number;
  qtyTotalDamage: number;
  qtyMissing: number;
  reportedOnUtc: string | null;
}

export interface ImplicitTransfer {
  productId: string;
  fromBranchId: string;
  toBranchId: string;
  qty: number;
  state: string;
}

export interface ReturnNote {
  /** Carried over to the contract that extends this one: nothing came back to the yard. */
  isCarryOver?: boolean;
  id: string;
  number: string;
  contractId: string;
  branchId: string;
  returnedAtUtc: string;
  userName: string | null;
  note: string | null;
  unusedRent: number;
  reliefDebtReduction: number;
  reliefPaymentVoucher: number;
  chargeTotal: number;
  contractClosed: boolean;
  lines: ReturnNoteLine[];
  charges: RentalChargeRow[];
  /** Only ever populated on the note the server just created; the list route returns []. */
  transfers: ImplicitTransfer[];
  driverName?: string | null;
  driverPhone?: string | null;
  truckNumber?: string | null;
  /** The customer carried the goods himself; no driver or truck is on the note. */
  customerTransport?: boolean;
  /** Whether the return day was charged, why, and who decided when somebody did. */
  countReturnDay?: boolean;
  returnDayReason?: string | null;
  returnDayDecidedBy?: string | null;
  /** Back late: whether the late days were waived, why, and who answered (null: nobody was asked). */
  waiveOverdue?: boolean;
  lateWaiverReason?: string | null;
  lateDecidedBy?: string | null;
  /** Charged less a deduction: so many late days, or so much off the late total it names. */
  lateDaysWaived?: number | null;
  lateAmountWaived?: number | null;
  lateAmountFull?: number | null;
}

export interface RentalChargeRow {
  id: string;
  contractLineId: string;
  kind: string;
  amount: number;
  returnNoteId: string;
  issueNoteId: string | null;
  extensionId: string | null;
  qty: number;
  days: number;
  unitPrice: number;
  isPosted: boolean;
}

export interface ContractExtension {
  id: string;
  number: string;
  contractId: string;
  contractLineId: string | null;
  extraDays: number;
  unitPrice: number;
  raisedOnUtc: string;
  periodEndsOnUtc: string | null;
  isRetroactive: boolean;
  approvedByName: string | null;
  reason: string | null;
}

export interface RentalLineSettlement {
  lineId: string;
  issuedQty: number;
  settledQty: number;
  stillOutQty: number;
  overReturnedQty: number;
  issuedPeriodRent: number;
  unusedRent: number;
  isCleared: boolean;
  charges: RentalChargeRow[];
}

export interface RentalSettlement {
  contractId: string;
  status: string;
  isClosed: boolean;
  stillOutQty: number;
  chargeTotal: number;
  unusedRent: number;
  rent: number;
  overdue: number;
  maintenance: number;
  damage: number;
  missing: number;
  lines: RentalLineSettlement[];
  /**
   * The deposit, outside every figure above: held on the customer's behalf, never netted against
   * `chargeTotal`. Both null while the switch is off so no zero prints; `depositToReturn` is the
   * same figure once the contract is closed and 0 before — stated, never a payment.
   */
  depositHeld?: number | null;
  depositToReturn?: number | null;
}

export interface IssueNoteRequest {
  branchId: string;
  lines: { contractLineId: string; qty: number }[];
  issuedAtUtc: string | null;
  note: string | null;
  /** All three mandatory when the branch transports (answer 16); ignored when the customer does. */
  driverName: string;
  driverPhone: string;
  truckNumber: string;
  customerTransport: boolean;
}

export interface ReturnNoteLineRequest {
  sameDayUsage?: SameDayUsage | null;
  contractLineId: string;
  qtyOk: number;
  qtyPartialDamage: number;
  maintenanceValue: number;
  qtyTotalDamage: number;
  qtyMissing: number;
  reportedOnUtc: string | null;
}

export interface ReturnNoteRequest {
  branchId: string;
  lines: ReturnNoteLineRequest[];
  returnedAtUtc: string | null;
  note: string | null;
  driverName: string;
  driverPhone: string;
  truckNumber: string;
  customerTransport: boolean;
  /** Null takes the rule's answer for the hour; a value is the manager's decision. */
  countReturnDay: boolean | null;
  returnDayReason: string | null;
  /**
   * Back late: the answer to "charge the late days?" — true charges them, false waives them and
   * needs a reason. Null when the note is not late, and the server then keeps the rule: charged.
   * Only the answer travels; the server prices every day itself.
   */
  chargeOverdue: boolean | null;
  lateWaiverReason: string | null;
  /**
   * With `chargeOverdue` true, a deduction (owner, 2026-10-22): late days off each batch, or an
   * amount off the note's late total before tax. One or the other, never more than the lateness,
   * and either needs `lateWaiverReason`. The server still prices every day itself.
   */
  lateDaysWaived?: number | null;
  lateAmountWaived?: number | null;
}

export type SameDayUsage = 'Used' | 'NotUsed';
export interface ReturnPreviewRow {
  returnLineIndex: number;
  contractLineId: string;
  issueNoteId: string;
  issueNumber: string;
  issuedAtUtc: string;
  qty: number;
  physicalQty: number;
  sameDayUsageRequired: boolean;
  sameDayUsage: SameDayUsage | 'NotApplicable' | null;
  chargeableDays: number | null;
  dailyUnitRate: number;
  rentalCharge: number | null;
}
export interface ReturnPreview {
  rows: ReturnPreviewRow[];
  charges: RentalChargeRow[];
  chargeTotal: number | null;
  unusedRent: number | null;
  customerBalance: number;
  previouslyPaid: number;
  customerCredit: number | null;
  reliefDebtReduction: number;
  reliefPaymentVoucher: number;
  isClosed: boolean;
  late: { dueOn: string | null; days: number; amount: number; minutes: number } | null;
  canConfirm: boolean;
  problems: string[];
}

export interface ContractExtensionRequest {
  extraDays: number;
  contractLineId: string | null;
  unitPrice: number | null;
  reason: string | null;
  raisedAtUtc: string | null;
}

/** Everything one contract's screens read. Four calls, because the server has four routes. */
export interface ContractDocuments {
  contract: RentalContract;
  issueNotes: IssueNote[];
  returnNotes: ReturnNote[];
  extensions: ContractExtension[];
  settlement: RentalSettlement;
}

// ---------- customer quantity statement (GET /api/rentals/quantity-statement) ----------

/** A daily unit rate that applied in the window. */
export interface QuantityStatementRate {
  kind: 'rent' | 'extension' | 'overdue';
  unitPrice: number;
}

export interface QuantityStatementSummary {
  itemTypes: number;
  issued: number;
  returned: number;
  missingDamaged: number;
  withCustomer: number;
  value: number;
  /** The part of `value` for goods still out, priced through `to`: it moves when they come back. */
  provisionalValue: number;
}

export type QuantityMovementKind = 'opening' | 'issue' | 'return' | 'partialReturn' | 'transferIn' | 'transferOut';

export interface QuantityStatementMovement {
  /** The note's day in the company's zone, `yyyy-MM-dd`. */
  on: string;
  atUtc: string | null;
  kind: QuantityMovementKind;
  noteId: string | null;
  documentNumber: string | null;
  contractId: string | null;
  contractNumber: string | null;
  /** Signed change to what the customer holds. */
  qty: number;
  returned: number;
  missingDamaged: number;
  unitPrice: number | null;
  balance: number;
  branchId: string | null;
  siteName: string | null;
}

export interface QuantityStatementItem {
  productId: string;
  sku: string;
  name: string;
  nameAr: string | null;
  barcode: string | null;
  opening: number;
  issued: number;
  returned: number;
  missingDamaged: number;
  transferNet: number;
  remaining: number;
  rates: QuantityStatementRate[];
  noRent: boolean;
  value: number;
  provisionalValue: number;
  negativeBalance: boolean;
  movements: QuantityStatementMovement[];
}

export interface QuantityStatementContractLine {
  lineId: string;
  productId: string;
  sku: string;
  name: string;
  nameAr: string | null;
  firstIssueOn: string | null;
  opening: number;
  issued: number;
  returned: number;
  missingDamaged: number;
  transferredIn: number;
  transferredOut: number;
  remaining: number;
  rentUnitPrice: number;
  noRent: boolean;
  rates: QuantityStatementRate[];
  value: number;
  provisionalValue: number;
}

export interface QuantityStatementContract {
  contractId: string;
  number: string;
  /** The contract's state at the end of `to`. */
  status: 'open' | 'goodsBack' | 'closed';
  branchId: string;
  siteId: string | null;
  siteName: string | null;
  firstIssueOn: string | null;
  pricingCorrectionPending: boolean;
  value: number;
  provisionalValue: number;
  lines: QuantityStatementContractLine[];
}

export interface QuantityStatementWarning {
  code: 'negativeBalance' | 'overReturned' | 'pricingCorrectionPending';
  productId: string | null;
  sku: string | null;
  name: string | null;
  nameAr: string | null;
  contractId: string | null;
  contractNumber: string | null;
  on: string | null;
  qty: number | null;
}

/** كشف كميات العميل — no payment, debt or balance owed in it (that is the account statement). */
export interface QuantityStatement {
  customerId: string;
  customerCode: string;
  customerName: string;
  /** Plain dates, both included. */
  from: string;
  to: string;
  current: boolean;
  /** The customer's first issue note: quantities before it were not keyed in. */
  firstIssueOn: string | null;
  summary: QuantityStatementSummary;
  items: QuantityStatementItem[];
  contracts: QuantityStatementContract[];
  warnings: QuantityStatementWarning[];
}

export interface QuantityStatementQuery {
  customerId: string;
  /** Plain dates, `yyyy-MM-dd`, both included; omitted when `current`. */
  from?: string | null;
  to?: string | null;
  current?: boolean;
  productIds?: string[];
  contractId?: string | null;
  branchId?: string | null;
  siteId?: string | null;
  /** all | open | goodsBack | closed — the contract's state as of `to`. */
  status?: string | null;
  search?: string | null;
}

/** The statement's query string, shared by the screen, its print and its export. */
export function quantityStatementParams(q: QuantityStatementQuery): URLSearchParams {
  const params = new URLSearchParams({ customerId: q.customerId });
  if (q.current) params.set('current', 'true');
  else {
    if (q.from) params.set('from', q.from);
    if (q.to) params.set('to', q.to);
  }
  if (q.productIds?.length) params.set('productIds', q.productIds.join(','));
  if (q.contractId) params.set('contractId', q.contractId);
  if (q.branchId) params.set('branchId', q.branchId);
  if (q.siteId) params.set('siteId', q.siteId);
  if (q.status && q.status !== 'all') params.set('status', q.status);
  if (q.search?.trim()) params.set('search', q.search.trim());
  return params;
}

@Injectable({ providedIn: 'root' })
export class RentalsApi {
  private readonly api = inject(ApiService);

  // ---------- settings ----------

  getSettings(): Promise<RentalSettings> {
    return this.api.getJson<RentalSettings>('/api/rentals/settings');
  }

  updateSettings(req: RentalSettings): Promise<RentalSettings> {
    return this.api.putJson<RentalSettings>('/api/rentals/settings', req);
  }

  /** Every item carrying its own multiplier, by SKU. Empty when all are on the company figure. */
  listProductMultipliers(): Promise<ProductOverdueMultiplier[]> {
    return this.api.getJson<ProductOverdueMultiplier[]>('/api/rentals/settings/products');
  }

  /** One item's row, `overdueMultiplier` null when it is on the company figure. 404 when unknown. */
  getProductMultiplier(productId: string): Promise<ProductOverdueMultiplier> {
    return this.api.getJson<ProductOverdueMultiplier>(`/api/rentals/settings/products/${productId}`);
  }

  /** Sets or, with null, clears an item's own multiplier. Answers with the same row the GETs return. */
  setProductMultiplier(
    productId: string,
    overdueMultiplier: number | null,
    rentalUnitPrice?: number,
    rentalDamageUnitPrice: number | null = null,
  ): Promise<ProductOverdueMultiplier> {
    // The replacement value is always sent: null clears it, so the screen sends what it shows.
    return this.api.putJson<ProductOverdueMultiplier>(`/api/rentals/settings/products/${productId}`, {
      overdueMultiplier,
      rentalUnitPrice,
      rentalDamageUnitPrice,
    });
  }

  // ---------- a customer's sites ----------

  listSites(customerId: string): Promise<CustomerSite[]> {
    return this.api.getJson<CustomerSite[]>(`/api/customers/${customerId}/sites`);
  }

  createSite(customerId: string, req: UpsertCustomerSiteRequest): Promise<CustomerSite> {
    return this.api.postJson<CustomerSite>(`/api/customers/${customerId}/sites`, req);
  }

  updateSite(customerId: string, siteId: string, req: UpsertCustomerSiteRequest): Promise<CustomerSite> {
    return this.api.putJson<CustomerSite>(`/api/customers/${customerId}/sites/${siteId}`, req);
  }

  // ---------- the identity scan kept with a contract ----------

  /**
   * One file up, as multipart. Not through `ApiService`, which sends JSON only; the same base URL,
   * token and error shape, so a refusal reads like every other one.
   */
  async uploadAttachment(contractId: string, file: File, kind = 'idDocument'): Promise<RentalAttachment> {
    const form = new FormData();
    form.append('kind', kind);
    form.append('file', file, file.name);
    const response = await this.raw(`/api/rentals/contracts/${contractId}/attachments`, { method: 'POST', body: form });
    return (await response.json()) as RentalAttachment;
  }

  /** The file itself, for viewing: a blob the screen opens as an object URL. */
  async attachmentBlob(contractId: string, attachmentId: string): Promise<Blob> {
    const response = await this.raw(`/api/rentals/contracts/${contractId}/attachments/${attachmentId}`, { method: 'GET' });
    return response.blob();
  }

  removeAttachment(contractId: string, attachmentId: string): Promise<void> {
    return this.api.deleteJson<void>(`/api/rentals/contracts/${contractId}/attachments/${attachmentId}`);
  }

  private async raw(path: string, init: RequestInit): Promise<Response> {
    const meta = await getMeta();
    const base = (meta.apiBaseUrl || '').replace(/\/+$/, '');
    const headers: Record<string, string> = {};
    if (meta.token) headers['Authorization'] = `Bearer ${meta.token}`;
    let response: Response;
    try {
      response = await fetch(`${base}${path}`, { ...init, headers, cache: 'no-store' });
    } catch (error) {
      throw new ApiError((error as Error)?.message ?? 'Network error', 0);
    }
    if (!response.ok) {
      const body = await response.text().catch(() => '');
      throw new ApiError(body || `${response.status} ${response.statusText}`, response.status);
    }
    return response;
  }

  // ---------- contracts ----------

  /** The customer quantity statement; plain dates read by the server in the company's zone. */
  quantityStatement(q: QuantityStatementQuery): Promise<QuantityStatement> {
    return this.api.getJson<QuantityStatement>(`/api/rentals/quantity-statement?${quantityStatementParams(q)}`);
  }

  /** The same statement as an .xlsx (the server's shared writer), in the reader's language. */
  async quantityStatementExport(q: QuantityStatementQuery, lang: string): Promise<Blob> {
    const params = quantityStatementParams(q);
    params.set('lang', lang);
    const response = await this.raw(`/api/rentals/quantity-statement/export?${params}`, { method: 'GET' });
    return response.blob();
  }

  listContracts(status?: string, customerId?: string, branchId?: string | null): Promise<RentalContractRow[]> {
    const params = new URLSearchParams();
    if (status) params.set('status', status);
    if (customerId) params.set('customerId', customerId);
    // An Admin's narrowing to one branch (the dashboard); a locked user's branch is the server's.
    if (branchId) params.set('branchId', branchId);
    const query = params.toString();
    return this.api.getJson<RentalContractRow[]>(`/api/rentals/contracts${query ? `?${query}` : ''}`);
  }

  getContract(id: string): Promise<RentalContract> {
    return this.api.getJson<RentalContract>(`/api/rentals/contracts/${id}`);
  }

  createContract(req: UpsertRentalContractRequest): Promise<RentalContract> {
    return this.api.postJson<RentalContract>('/api/rentals/contracts', req);
  }

  updateContract(id: string, req: UpsertRentalContractRequest): Promise<RentalContract> {
    return this.api.putJson<RentalContract>(`/api/rentals/contracts/${id}`, req);
  }

  getSettlement(id: string): Promise<RentalSettlement> {
    return this.api.getJson<RentalSettlement>(`/api/rentals/contracts/${id}/settlement`);
  }

  // ---------- the chain of documents ----------

  listIssueNotes(id: string): Promise<IssueNote[]> {
    return this.api.getJson<IssueNote[]>(`/api/rentals/contracts/${id}/issue-notes`);
  }

  issue(id: string, req: IssueNoteRequest): Promise<IssueNote> {
    return this.api.postJson<IssueNote>(`/api/rentals/contracts/${id}/issue-notes`, req);
  }

  listReturnNotes(id: string): Promise<ReturnNote[]> {
    return this.api.getJson<ReturnNote[]>(`/api/rentals/contracts/${id}/return-notes`);
  }

  return(id: string, req: ReturnNoteRequest): Promise<ReturnNote> {
    return this.api.postJson<ReturnNote>(`/api/rentals/contracts/${id}/return-notes`, req);
  }

  previewReturn(id: string, req: ReturnNoteRequest): Promise<ReturnPreview> {
    return this.api.postJson<ReturnPreview>(`/api/rentals/contracts/${id}/return-notes/preview`, req);
  }

  listExtensions(id: string): Promise<ContractExtension[]> {
    return this.api.getJson<ContractExtension[]>(`/api/rentals/contracts/${id}/extensions`);
  }

  /** Answer 8: a new contract for the customer, every unit still out carried over to it. */
  extendAsNew(id: string, req: { durationDays: number; signedAtUtc: string | null; notes: string | null }): Promise<RentalContract> {
    return this.api.postJson<RentalContract>(`/api/rentals/contracts/${id}/extend-as-new`, req);
  }

  /**
   * Calls a contract off before its exit note, with the reason (owner, 2026-10-06). Refused once
   * anything has gone out; afterwards only the Admin sees it.
   */
  cancelContract(id: string, reason: string): Promise<RentalContract> {
    return this.api.postJson<RentalContract>(`/api/rentals/contracts/${id}/cancel`, { reason });
  }

  /** Admin only: closes a contract whose goods are back but which is not paid in full. */
  closeShort(id: string, reason: string): Promise<RentalContract> {
    return this.api.postJson<RentalContract>(`/api/rentals/contracts/${id}/close`, { reason });
  }

  extend(id: string, req: ContractExtensionRequest): Promise<ContractExtension> {
    return this.api.postJson<ContractExtension>(`/api/rentals/contracts/${id}/extensions`, req);
  }

  /**
   * One contract, whole. The four document routes are independent and none of them depends on
   * another's answer, so they go out together rather than one after the next.
   */
  async readContract(id: string): Promise<ContractDocuments> {
    const [contract, issueNotes, returnNotes, extensions, settlement] = await Promise.all([
      this.getContract(id),
      this.listIssueNotes(id),
      this.listReturnNotes(id),
      this.listExtensions(id),
      this.getSettlement(id),
    ]);
    return { contract, issueNotes, returnNotes, extensions, settlement };
  }
}
