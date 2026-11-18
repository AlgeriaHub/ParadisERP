import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

// ---------- Phase M4: job cost and the cost reports (docs/maintenance/plan.md §3.1, §6.2, §7) ----------

/** What each element rests on: the cost engine's words (`approved`, `reference_cost`, `provisional`…) or `excluded` (MS-10). */
export interface MaintenanceCostStatuses {
  parts: string;
  labour: string;
  overhead: string;
  outsideWork: string;
}

export interface MaintenanceCostPart {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  netQty: number;
  unitCost: number;
  value: number;
}

/** An employee-month of hours in the cost; `rate` and `cost` are null for a reader who may not see pay rates. */
export interface MaintenanceCostLabour {
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  year: number;
  month: number;
  hours: number;
  overtimeHours: number;
  /** `Actual`, `Standard`, `None` or `Workshop` (MS-12). */
  rateKind: string;
  reason: string | null;
  rate: number | null;
  cost: number | null;
  withoutEmployerSocial: boolean;
}

export interface MaintenanceCostOutside {
  id: string;
  description: string;
  recorded: number;
  /** Before recoverable VAT under C-07 / MS-13; the recorded amount otherwise. */
  counted: number;
  settled: boolean;
  voucherNumber: string | null;
}

export interface CostNote {
  code: string;
  value: number | null;
}

/**
 * A job's cost, computed on read (never stored). `status` is `approved` (final) or `provisional`. With `labourMoneyShown`
 * false the labour money, the total and the recharge less cost are null: the reader does not see pay rates (§6.2).
 */
export interface MaintenanceJobCost {
  jobId: string;
  number: string;
  kind: string;
  jobStatus: string;
  status: string;
  policyApproved: boolean;
  labourMoneyShown: boolean;
  costIncludes: string;
  labourRate: string;
  burdenAndVat: string;
  overheadPerLabourHour: number;
  workshopRatePerHour: number | null;
  employerBurdenPercent: number;
  expenseVatPercent: number;
  parts: number;
  labour: number | null;
  overhead: number;
  outsideWork: number;
  total: number | null;
  labourHours: number;
  recharge: number | null;
  rechargeLessCost: number | null;
  statuses: MaintenanceCostStatuses;
  partLines: MaintenanceCostPart[];
  labourLines: MaintenanceCostLabour[];
  outsideWorkLines: MaintenanceCostOutside[];
  notes: CostNote[];
  /** Phase M7d (MS-30): when the repair was capitalised into its register pool at completion, and how much (null: an expense, or withheld). */
  capitalisedAtUtc?: string | null;
  capitalised?: number | null;
}

export interface MaintenanceRechargeRow {
  jobId: string;
  number: string;
  status: string;
  branchId: string;
  branchName: string;
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  qty: number;
  contractNumber: string | null;
  returnNoteNumber: string | null;
  customerName: string | null;
  lineRecharge: number;
  recharge: number;
  parts: number;
  labour: number | null;
  overhead: number;
  outsideWork: number;
  total: number | null;
  difference: number | null;
  labourHours: number;
  costStatus: string;
  withheld: boolean;
  createdAtUtc: string;
}

export interface MaintenanceRechargeReport {
  from: string | null;
  to: string | null;
  rechargeSharing: string;
  labourMoneyShown: boolean;
  rows: MaintenanceRechargeRow[];
  recharge: number;
  parts: number;
  overhead: number;
  outsideWork: number;
  labourHours: number;
  labour: number | null;
  total: number | null;
  difference: number | null;
  totalsWithheld: boolean;
}

export interface MaintenanceProductCostRow {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  jobs: number;
  units: number;
  repairedQty: number;
  scrappedQty: number;
  parts: number;
  overhead: number;
  outsideWork: number;
  labourHours: number;
  labour: number | null;
  total: number | null;
  recharge: number;
  workers: number;
  withheld: boolean;
  provisional: boolean;
  /** Phase M7d (MS-30): of the total, what was capitalised and what stays an expense (null when the total is withheld). */
  capitalised?: number | null;
  expensed?: number | null;
}

export interface MaintenanceProductCostReport {
  from: string | null;
  to: string | null;
  labourMoneyShown: boolean;
  rows: MaintenanceProductCostRow[];
  jobs: number;
  parts: number;
  overhead: number;
  outsideWork: number;
  labourHours: number;
  recharge: number;
  labour: number | null;
  total: number | null;
  totalsWithheld: boolean;
  capitalised?: number | null;
  expensed?: number | null;
}

// ---------- Phase M7d (MS-38, MNT-10): the repair history of a type or a piece (`/api/maintenance/history`) ----------

export interface MaintenancePiece {
  reference: string;
  jobs: number;
  lastAtUtc: string;
}

export interface MaintenanceHistoryPart {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  qty: number;
  value: number;
}

export interface MaintenanceHistoryJob {
  id: string;
  number: string;
  kind: string;
  status: string;
  branchId: string;
  branchName: string;
  itemReference: string | null;
  qty: number;
  repairedQty: number;
  scrappedQty: number;
  openedAtUtc: string;
  completedAtUtc: string | null;
  labourHours: number;
  parts: number;
  partLines: MaintenanceHistoryPart[];
  outsideWork: number;
  /** Null when not shown: without maintenance.costs.view, or withheld (`costWithheld`) by the pay-rate rule. */
  cost: number | null;
  costWithheld: boolean;
  capitalised: number | null;
  costStatus: string;
}

export interface MaintenanceRepairHistory {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  mode: 'PerTypeWithReference' | 'PerPiece';
  trackedPerPiece: boolean;
  unitValue: number;
  perPieceAboveValue: number;
  reference: string | null;
  pieces: MaintenancePiece[];
  jobs: MaintenanceHistoryJob[];
  labourHours: number;
  parts: number;
  outsideWork: number;
  cost: number | null;
  capitalised: number | null;
  costsShown: boolean;
  totalsWithheld: boolean;
}

@Injectable({ providedIn: 'root' })
export class MaintenanceCostApi {
  private readonly api = inject(ApiService);

  jobCost(jobId: string): Promise<MaintenanceJobCost> {
    return this.api.getJson<MaintenanceJobCost>(`/api/maintenance/jobs/${encodeURIComponent(jobId)}/cost`);
  }

  /** Recharged jobs of the period (company days, `yyyy-MM-dd`, either end open) against their share of the recharge. */
  recharges(from: string | null, to: string | null): Promise<MaintenanceRechargeReport> {
    return this.api.getJson<MaintenanceRechargeReport>(`/api/maintenance/reports/recharges${period(from, to)}`);
  }

  /** Every equipment type's maintenance cost over the period. */
  products(from: string | null, to: string | null): Promise<MaintenanceProductCostReport> {
    return this.api.getJson<MaintenanceProductCostReport>(`/api/maintenance/reports/products${period(from, to)}`);
  }

  /** Phase M7d: every job on an equipment type, or on one piece of it (its reference, case ignored). */
  history(productId: string, reference: string | null): Promise<MaintenanceRepairHistory> {
    const query = new URLSearchParams({ productId });
    if (reference) query.set('reference', reference);
    return this.api.getJson<MaintenanceRepairHistory>(`/api/maintenance/history?${query.toString()}`);
  }
}

function period(from: string | null, to: string | null): string {
  const query = new URLSearchParams();
  if (from) query.set('from', from);
  if (to) query.set('to', to);
  const text = query.toString();
  return text ? `?${text}` : '';
}
