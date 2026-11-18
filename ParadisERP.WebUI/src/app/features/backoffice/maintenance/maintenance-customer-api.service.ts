import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { MaintenanceJob } from './maintenance-api.service';

/**
 * Phase M5a (docs/maintenance/plan.md §2.1, §3.2): customer jobs, their price and their one invoice, the MS-23 repair
 * rates; and Phase M5's company profit report (§3.3). Every amount is before tax unless it says otherwise.
 */

export interface MaintenanceCustomerOption {
  id: string;
  code: string;
  fullName: string;
}

export interface MaintenanceProductOption {
  id: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
}

export interface MaintenanceNewJobOptions {
  /** MS-06: jobs for outside customers are on. */
  customerJobs: boolean;
  customers: MaintenanceCustomerOption[];
  products: MaintenanceProductOption[];
}

export type MaintenancePriceBasis = 'Quoted' | 'Itemised' | 'CostPlus';

/**
 * A customer job's Price card. The cost, the loss and the cost-plus proposal are null for a reader who may not see the
 * job's cost (`costShown` false); `belowCost` is still said, never by how much.
 */
export interface MaintenanceJobPrice {
  jobId: string;
  number: string;
  kind: string;
  status: string;
  basis: MaintenancePriceBasis;
  labourChargePerHour: number;
  markupPercent: number;
  hours: number;
  partsUsedQty: number;
  outsideWork: number;
  parts: number | null;
  labour: number | null;
  markup: number | null;
  costBase: number | null;
  proposed: number | null;
  costShown: boolean;
  costAvailable: boolean;
  cost: number | null;
  costProvisional: boolean | null;
  price: number | null;
  pricedBasis: MaintenancePriceBasis | null;
  proposedAtPricing: number | null;
  pricedBy: string | null;
  pricedAtUtc: string | null;
  taxMode: 'Taxed' | 'Untaxed';
  /** The country profile's default rate, applied on top when VAT is ticked. */
  taxRateWhenTaxed: number;
  belowCostRule: 'ShownAsLoss' | 'NeedsConfirmation';
  belowCost: boolean;
  loss: number | null;
  belowCostConfirmedBy: string | null;
  belowCostConfirmedAtUtc: string | null;
  customerIncome: string;
  incomeAccountCode: string;
  invoiceId: string | null;
  invoiceNumber: string | null;
}

export interface MaintenanceJobInvoice {
  invoiceId: string;
  invoiceNumber: string;
  subTotalHt: number;
  taxTotal: number;
  grandTotalTtc: number;
  taxMode: 'Taxed' | 'Untaxed';
  incomeAccountCode: string;
  warning: string | null;
  job: MaintenanceJob;
}

export interface MaintenanceRepairRate {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  ratePerUnit: number;
  updatedBy: string | null;
  updatedAtUtc: string;
}

export interface MaintenanceRepairRates {
  /** MS-23 as saved: `SuggestedFromRate` makes the return-note screen suggest. */
  returnRecharge: 'TypedAtReturn' | 'SuggestedFromRate';
  rates: MaintenanceRepairRate[];
  products: MaintenanceProductOption[];
}

export interface MaintenanceRepairSuggestions {
  enabled: boolean;
  rates: { productId: string; ratePerUnit: number }[];
}

export type MaintenanceRevenueSource = 'Invoice' | 'NotInvoiced' | 'Recharge' | 'None' | 'Rework';

export interface MaintenanceProfitRow {
  jobId: string;
  number: string;
  kind: string;
  status: string;
  branchId: string;
  branchName: string;
  productId: string | null;
  productSku: string;
  productNameAr: string;
  productNameFr: string;
  productNameEn: string;
  equipment: string | null;
  customerName: string | null;
  month: string;
  qty: number;
  revenueSource: MaintenanceRevenueSource;
  revenue: number;
  invoiceNumber: string | null;
  parts: number;
  outsideWork: number;
  overhead: number;
  labourHours: number;
  labour: number | null;
  cost: number | null;
  profit: number | null;
  costStatus: string;
  withheld: boolean;
  openedAtUtc: string;
  /** Phase M7c (MS-37): a rework's own line names the job it redoes; under "added to the original job" the original counts its reworks and their cost (already in the cost). */
  reworkOfNumber?: string | null;
  reworks?: number;
  reworkCost?: number | null;
}

export interface MaintenanceProfitGroup {
  key: string;
  label: string;
  nameAr: string | null;
  nameFr: string | null;
  nameEn: string | null;
  jobs: number;
  revenue: number;
  cost: number | null;
  profit: number | null;
  withheld: boolean;
}

export interface MaintenanceProfitReport {
  from: string | null;
  to: string | null;
  moneyShown: boolean;
  rows: MaintenanceProfitRow[];
  byMonth: MaintenanceProfitGroup[];
  byBranch: MaintenanceProfitGroup[];
  byKind: MaintenanceProfitGroup[];
  byProduct: MaintenanceProfitGroup[];
  revenue: number;
  cost: number | null;
  profit: number | null;
  totalsWithheld: boolean;
  /** Phase M7c: MS-37 as saved — how reworks under guarantee are reported. */
  rework?: 'AddedToOriginalJob' | 'OwnJob' | 'Provision';
}

@Injectable({ providedIn: 'root' })
export class MaintenanceCustomerApi {
  private readonly api = inject(ApiService);

  newJobOptions(): Promise<MaintenanceNewJobOptions> {
    return this.api.getJson<MaintenanceNewJobOptions>('/api/maintenance/new-job-options');
  }

  price(jobId: string): Promise<MaintenanceJobPrice> {
    return this.api.getJson<MaintenanceJobPrice>(`/api/maintenance/jobs/${encodeURIComponent(jobId)}/price`);
  }

  /** The price before tax (maintenance.pricing); `taxMode` null leaves it; `confirmBelowCost` under MS-25 "needs confirmation". */
  setPrice(jobId: string, price: number, taxMode: 'Taxed' | 'Untaxed' | null, confirmBelowCost = false): Promise<MaintenanceJobPrice> {
    return this.api.putJson<MaintenanceJobPrice>(`/api/maintenance/jobs/${encodeURIComponent(jobId)}/price`, { price, taxMode, confirmBelowCost });
  }

  /** The job's one invoice. `dueDateUtc` is mandatory; `taxMode` is the seller's tick. */
  invoice(jobId: string, dueDateUtc: string, taxMode: 'Taxed' | 'Untaxed'): Promise<MaintenanceJobInvoice> {
    return this.api.postJson<MaintenanceJobInvoice>(`/api/maintenance/jobs/${encodeURIComponent(jobId)}/invoice`, { dueDateUtc, taxMode });
  }

  repairRates(): Promise<MaintenanceRepairRates> {
    return this.api.getJson<MaintenanceRepairRates>('/api/maintenance/repair-rates');
  }

  setRepairRate(productId: string, ratePerUnit: number): Promise<MaintenanceRepairRates> {
    return this.api.putJson<MaintenanceRepairRates>(`/api/maintenance/repair-rates/${encodeURIComponent(productId)}`, { ratePerUnit });
  }

  removeRepairRate(productId: string): Promise<MaintenanceRepairRates> {
    return this.api.deleteJson<MaintenanceRepairRates>(`/api/maintenance/repair-rates/${encodeURIComponent(productId)}`);
  }

  /** The return-note screen: whether to suggest a recharge, and each product's rate. Never an error while MS-01 or MS-23 is off. */
  repairSuggestions(): Promise<MaintenanceRepairSuggestions> {
    return this.api.getJson<MaintenanceRepairSuggestions>('/api/maintenance/repair-rates/suggestions');
  }

  profit(from: string | null, to: string | null): Promise<MaintenanceProfitReport> {
    const q = new URLSearchParams();
    if (from) q.set('from', from);
    if (to) q.set('to', to);
    const query = q.toString();
    return this.api.getJson<MaintenanceProfitReport>(`/api/maintenance/reports/profit${query ? `?${query}` : ''}`);
  }
}
