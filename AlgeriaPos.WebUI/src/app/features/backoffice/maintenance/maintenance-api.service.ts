import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

/**
 * Maintenance › Settings (Phase M0, `/api/maintenance/settings`, docs/maintenance/plan.md §4): every maintenance
 * decision as a setting. Each choice travels by its option's name; the first option is the default and the suggested
 * answer. MS-01 to MS-27 are changed under `maintenance.settings`, MS-30 to MS-40 under `accounting.manage`.
 */
export interface MaintenanceSettings {
  // Module and jobs
  module: 'Off' | 'On';
  unitsLeaveMaintenance: 'ByHandOrByJob' | 'OnlyByJob';
  jobsFromReturnNotes: 'OfferedInWaitingList' | 'DraftJobPerDamagedLine';
  bookableEmployees: 'AnyEmployeeOfBranch' | 'WorkshopListOnly';
  jobCloser: 'AnyHolder' | 'NotTheOpener';
  customerJobs: 'Off' | 'On';
  // Cost
  costIncludes: 'PartsLabourOutsideWork' | 'PartsLabourOutsideWorkOverhead' | 'PartsAndOutsideWorkOnly';
  overheadPerLabourHour: number;
  labourRate: 'PayrollDerived' | 'WorkshopRate';
  workshopRatePerHour: number;
  burdenAndVat: 'FollowManufacturing' | 'OwnValues';
  employerBurdenPercent: number;
  expenseVatPercent: number;
  // Pricing
  customerPrice: 'Quoted' | 'Itemised' | 'CostPlus';
  labourChargePerHour: number;
  markupPercent: number;
  returnRecharge: 'TypedAtReturn' | 'SuggestedFromRate';
  priceBelowCost: 'ShownAsLoss' | 'NeedsConfirmation';
  rechargeSharing: 'ByUnits' | 'AllToFirstJob';
  /** MS-27 (Phase M7c): a customer job's guarantee in months from its completion; 0 gives none. */
  guaranteeMonths: number;
  // Accounting
  ownRepairs: 'AlwaysExpense' | 'CapitalisedAboveAmount';
  partsPosting: 'PracticeLedgerByMonth' | 'AtIssue';
  labourInBooks: 'ReportOnly' | 'MovedIntoJobs';
  customerIncome: 'SameAsRecharges' | 'OneServiceAccount' | 'PartsAndLabourApart';
  advances: 'NotTaken' | 'Taken';
  openJobsAtPeriodEnd: 'LeftInExpensesListed' | 'WorkInProgressAtYearEnd' | 'WorkInProgressMonthly';
  rework: 'AddedToOriginalJob' | 'OwnJob' | 'Provision';
  /** MS-37 "provision" (Phase M7c): the share of a customer job's invoice net set aside for rework; kept only under it. */
  provisionPercent: number;
  repairHistory: 'PerTypeWithReference' | 'PerPiece';
  /** MS-30 "capitalised" (Phase M7d): a repair of own hire equipment above this cost joins its register pool; kept only under it. */
  capitaliseAboveAmount: number;
  /** MS-38 "per piece" (Phase M7d): a type whose unit value is above this is tracked piece by piece; kept only under it. */
  perPieceAboveValue: number;
  /** MS-39: the first month the accountant may post (`yyyy-MM`); null, nothing posts. */
  postingFromMonth: string | null;
  /** MS-40: the accountant confirmed the cost and accounting choices. */
  accountantApproved: boolean;
  approvedBy: string | null;
  approvedAtUtc: string | null;
  updatedAtUtc: string | null;
  updatedBy: string | null;
  /** Phase M7d: whether the REN-12 register holds the hire equipment (MS-30 "capitalised" is chosen only then). Read only. */
  assetRegisterActive?: boolean;
}

export type MaintenanceSettingsDraft = Omit<MaintenanceSettings, 'approvedBy' | 'approvedAtUtc' | 'updatedAtUtc' | 'updatedBy' | 'assetRegisterActive'>;

// ---------- Phase M1: jobs and units (`/api/maintenance/jobs`, `/api/maintenance/waiting`, plan §2.2) ----------

export type MaintenanceJobKind = 'Internal' | 'Recharged' | 'Customer';
/** `MaintenanceJobStatus`, in the server's order. Invoiced is a customer job's (Phase M5). */
export const JOB_STATUSES = ['Draft', 'Open', 'Completed', 'Invoiced', 'Cancelled'] as const;
export type MaintenanceJobStatus = (typeof JOB_STATUSES)[number];
/** Every kind. A customer job (Phase M5a) is opened only while MS-06 is on; the New job form offers it then. */
export const JOB_KINDS: readonly MaintenanceJobKind[] = ['Internal', 'Recharged', 'Customer'];

export interface MaintenanceJobSummary {
  id: string;
  number: string;
  branchId: string;
  branchName: string;
  kind: MaintenanceJobKind;
  status: MaintenanceJobStatus;
  /** Null only on a customer job naming no product (Phase M5a). */
  productId: string | null;
  productSku: string;
  productNameAr: string;
  productNameFr: string;
  productNameEn: string;
  qty: number;
  repairedQty: number;
  scrappedQty: number;
  itemReference: string | null;
  contractId: string | null;
  contractNumber: string | null;
  returnNoteId: string | null;
  returnNoteNumber: string | null;
  createdAtUtc: string;
  completedAtUtc: string | null;
  /** Phase M5a, a customer job: the outside equipment in words, its customer, and its invoice once raised. */
  equipment?: string | null;
  customerId?: string | null;
  jobCustomerName?: string | null;
  invoiceId?: string | null;
  /** Phase M7c: a free rework under guarantee names the job it redoes (never priced nor invoiced). */
  reworkOfJobId?: string | null;
}

export interface MaintenanceJobEvent {
  kind: 'Drafted' | 'Opened' | 'Edited' | 'Completed' | 'Cancelled' | 'PartsIssued' | 'PartsReturned' | 'ExpenseRecorded' | 'Priced' | 'Invoiced';
  detail: string | null;
  userName: string | null;
  atUtc: string;
}

export interface MaintenanceJob extends MaintenanceJobSummary {
  returnNoteLineId: string | null;
  customerName: string | null;
  /** The return-note line's repairable units and the recharge typed at return (a recharged job). */
  lineDamagedQty: number | null;
  lineRecharge: number | null;
  description: string | null;
  diagnosis: string | null;
  createdBy: string | null;
  openedByUserId: string | null;
  openedBy: string | null;
  openedAtUtc: string | null;
  completedBy: string | null;
  cancelledBy: string | null;
  cancelledAtUtc: string | null;
  cancelReason: string | null;
  /** What the job's branch holds in maintenance of its product today. */
  inMaintenanceAtBranch: number;
  /** MS-05 in force: `NotTheOpener` keeps the opener from completing. */
  jobCloser: 'AnyHolder' | 'NotTheOpener';
  events: MaintenanceJobEvent[];
  /** Phase M5a: a customer job's price before tax once set, whether its invoice carries VAT, and the invoice once raised. */
  price?: number | null;
  taxMode?: 'Taxed' | 'Untaxed';
  invoiceNumber?: string | null;
  /** Phase M7c (MS-27, MS-37): the job a rework redoes; a customer job's guarantee (`yyyy-MM-dd`), the provision set aside at its invoice, and its reworks. */
  reworkOfNumber?: string | null;
  guaranteeUntil?: string | null;
  provisionAmount?: number | null;
  reworks?: MaintenanceReworkRef[] | null;
}

/** A rework under guarantee opened against a customer job. */
export interface MaintenanceReworkRef {
  id: string;
  number: string;
  status: MaintenanceJobStatus;
  createdAtUtc: string;
}

/** A completed customer job of the customer whose guarantee still runs: a free rework may be opened against it. */
export interface MaintenanceReworkCandidate {
  id: string;
  number: string;
  status: MaintenanceJobStatus;
  equipment: string | null;
  itemReference: string | null;
  productId: string | null;
  completedAtUtc: string | null;
  /** The guarantee's last day, `yyyy-MM-dd`. */
  guaranteeUntil: string;
}

export interface CreateMaintenanceJob {
  clientRequestId: string;
  /** Ignored for a non-admin: the job is opened at their own branch. */
  branchId: string | null;
  kind: MaintenanceJobKind;
  productId: string | null;
  qty: number;
  returnNoteLineId: string | null;
  itemReference: string | null;
  description: string | null;
  /** Phase M5a, a customer job: the existing customer and the equipment in words (`productId` optional). */
  customerId?: string | null;
  equipment?: string | null;
  /** Phase M7c: a free rework under guarantee names the completed customer job it redoes. */
  reworkOfJobId?: string | null;
}

export interface MaintenanceBucket {
  branchId: string;
  branchName: string;
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  inMaintenance: number;
  heldByJobs: number;
  free: number;
}

export interface MaintenanceWaitingLine {
  returnNoteLineId: string;
  returnNoteId: string;
  returnNoteNumber: string;
  contractId: string;
  contractNumber: string;
  customerName: string | null;
  branchId: string;
  branchName: string;
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  returnedAtUtc: string;
  damagedQty: number;
  coveredQty: number;
  openQty: number;
  recharge: number;
}

export interface MaintenanceWaiting {
  buckets: MaintenanceBucket[];
  lines: MaintenanceWaitingLine[];
}

// ---------- Phase M2: parts and outside work ----------

/** One product's parts on a job: issued, returned, still at the job, and their value at the frozen costs. */
export interface MaintenancePartBalance {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  issuedQty: number;
  returnedQty: number;
  atJobQty: number;
  /** What the issues went out at, on average: what a return comes back at. */
  issueUnitCost: number;
  value: number;
}

export type MaintenancePartKind = 'Issue' | 'Return';

/** One line of an issue (MJI-) or return (MJR-) document. */
export interface MaintenancePartMovement {
  documentId: string;
  documentNumber: string;
  kind: MaintenancePartKind;
  productId: string;
  sku: string;
  qty: number;
  unitCost: number;
  value: number;
  costBasis: 'CostPrice' | 'WeightedAverage' | 'CostPriceFallback' | 'SaleCost';
  note: string | null;
  userName: string | null;
  atUtc: string;
}

export interface MaintenanceJobExpense {
  id: string;
  description: string;
  amount: number;
  paymentVoucherId: string | null;
  voucherNumber: string | null;
  voucherStatus: 'Pending' | 'Approved' | 'Paid' | 'Cancelled' | null;
  userName: string | null;
  atUtc: string;
}

/** A job's parts and outside work. */
export interface MaintenanceJobWork {
  jobId: string;
  jobStatus: MaintenanceJobStatus;
  parts: MaintenancePartBalance[];
  movements: MaintenancePartMovement[];
  expenses: MaintenanceJobExpense[];
  partsValue: number;
  partsAtJobQty: number;
  expensesTotal: number;
  /** MS-32 as saved; parts post nothing in M2 whatever it says. */
  partsPosting: 'PracticeLedgerByMonth' | 'AtIssue';
}

/** A stocked product the job's branch can issue now (on hand less what production claims there). */
export interface MaintenancePartOption {
  id: string;
  sku: string;
  barcode: string | null;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  available: number;
}

/** An expense voucher of the job's branch with some of its amount not yet linked. */
export interface MaintenanceVoucherOption {
  id: string;
  number: string;
  status: 'Pending' | 'Approved' | 'Paid' | 'Cancelled';
  expenseCategory: string | null;
  payeeName: string | null;
  purpose: string | null;
  amount: number;
  linked: number;
  free: number;
  createdAtUtc: string;
}

export interface MaintenanceWorkOptions {
  products: MaintenancePartOption[];
  vouchers: MaintenanceVoucherOption[];
}

/** One id per attempt, so a retried request answers the job the first one opened. */
export function jobRequestId(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID().replace(/-/g, '')
    : `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 12)}`;
}

@Injectable({ providedIn: 'root' })
export class MaintenanceApi {
  private readonly api = inject(ApiService);

  settings(): Promise<MaintenanceSettings> {
    return this.api.getJson<MaintenanceSettings>('/api/maintenance/settings');
  }

  saveSettings(draft: MaintenanceSettingsDraft): Promise<MaintenanceSettings> {
    return this.api.putJson<MaintenanceSettings>('/api/maintenance/settings', draft);
  }

  /** The jobs of the caller's branch (every branch for the Admin), newest first, up to 500. */
  jobs(filter: { status?: string | null; kind?: string | null; productId?: string | null } = {}): Promise<MaintenanceJobSummary[]> {
    const q = new URLSearchParams();
    for (const [k, v] of Object.entries(filter)) if (v) q.set(k, v);
    const query = q.toString();
    return this.api.getJson<MaintenanceJobSummary[]>(`/api/maintenance/jobs${query ? `?${query}` : ''}`);
  }

  job(id: string): Promise<MaintenanceJob> {
    return this.api.getJson<MaintenanceJob>(`/api/maintenance/jobs/${encodeURIComponent(id)}`);
  }

  /** Phase M7c: the customer's completed jobs at the branch a free rework may still be opened against. */
  reworkCandidates(customerId: string, branchId: string | null = null): Promise<MaintenanceReworkCandidate[]> {
    const q = new URLSearchParams({ customerId });
    if (branchId) q.set('branchId', branchId);
    return this.api.getJson<MaintenanceReworkCandidate[]>(`/api/maintenance/rework-candidates?${q.toString()}`);
  }

  waiting(): Promise<MaintenanceWaiting> {
    return this.api.getJson<MaintenanceWaiting>('/api/maintenance/waiting');
  }

  create(request: CreateMaintenanceJob): Promise<MaintenanceJob> {
    return this.api.postJson<MaintenanceJob>('/api/maintenance/jobs', request);
  }

  update(id: string, words: { itemReference: string | null; description: string | null; diagnosis: string | null; equipment?: string | null }): Promise<MaintenanceJob> {
    return this.api.putJson<MaintenanceJob>(`/api/maintenance/jobs/${encodeURIComponent(id)}`, words);
  }

  /** A draft (opened by a return note, MS-03) taken: it becomes open, its taker the opener. */
  open(id: string): Promise<MaintenanceJob> {
    return this.api.postJson<MaintenanceJob>(`/api/maintenance/jobs/${encodeURIComponent(id)}/open`, {});
  }

  complete(id: string, repairedQty: number, scrappedQty: number, diagnosis: string | null): Promise<MaintenanceJob> {
    return this.api.postJson<MaintenanceJob>(`/api/maintenance/jobs/${encodeURIComponent(id)}/complete`, { repairedQty, scrappedQty, diagnosis });
  }

  /** `partsUsed`: parts still at the job stay as used (plan §2.3); without it the server refuses while any are there. */
  cancel(id: string, reason: string, partsUsed = false): Promise<MaintenanceJob> {
    return this.api.postJson<MaintenanceJob>(`/api/maintenance/jobs/${encodeURIComponent(id)}/cancel`, { reason, partsUsed });
  }

  /** The job's parts and outside work (Phase M2). */
  work(id: string): Promise<MaintenanceJobWork> {
    return this.api.getJson<MaintenanceJobWork>(`/api/maintenance/jobs/${encodeURIComponent(id)}/work`);
  }

  /** What the Parts and Outside work cards offer (maintenance.jobs.manage). */
  workOptions(id: string): Promise<MaintenanceWorkOptions> {
    return this.api.getJson<MaintenanceWorkOptions>(`/api/maintenance/jobs/${encodeURIComponent(id)}/work-options`);
  }

  /** Parts issued to the job or returned from it; one `requestId` per attempt, so a retry moves nothing twice. */
  moveParts(
    id: string,
    kind: MaintenancePartKind,
    lines: { productId: string; qty: number }[],
    requestId: string,
    note: string | null = null,
  ): Promise<MaintenanceJobWork> {
    return this.api.postJson<MaintenanceJobWork>(`/api/maintenance/jobs/${encodeURIComponent(id)}/parts`, {
      clientRequestId: requestId, kind, lines, note,
    });
  }

  /** Outside work on the job, optionally paid by an expense voucher. */
  addExpense(id: string, description: string, amount: number, paymentVoucherId: string | null, requestId: string): Promise<MaintenanceJobWork> {
    return this.api.postJson<MaintenanceJobWork>(`/api/maintenance/jobs/${encodeURIComponent(id)}/expenses`, {
      clientRequestId: requestId, description, amount, paymentVoucherId,
    });
  }
}
