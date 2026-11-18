import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { PageResult, pageQueryString } from '../../../core/paging';
import { MaintenanceBucket, MaintenanceJobSummary, MaintenanceWaitingLine } from './maintenance-api.service';
import {
  MaintenanceHistoryJob,
  MaintenancePiece,
  MaintenanceProductCostReport,
  MaintenanceProductCostRow,
  MaintenanceRechargeReport,
  MaintenanceRechargeRow,
  MaintenanceRepairHistory,
} from './maintenance-cost-api.service';
import { MaintenanceProductOption, MaintenanceProfitReport, MaintenanceProfitRow, MaintenanceRepairRate } from './maintenance-customer-api.service';
import { MaintenanceBooking, MaintenanceJobHours } from './maintenance-hours-api.service';
import { MaintenanceLedger, MaintenanceLedgerProvision } from './maintenance-ledger-api.service';
import { MaintenanceWorkshop, MaintenanceWorkshopMember } from './maintenance-workshop-api.service';

/**
 * The maintenance lists a page at a time (owner, 2026-11-17: "I need to paginate in backend, not frontend"): each
 * `…/page` route sits beside the list's own route (left as it was), runs the screen's filters on the server over every row,
 * and sends one page with the list's figures over all its filtered rows in `summary`. A filter at "all", empty or null is
 * not sent (`pageQueryString`).
 */

type Filters = Record<string, string | number | boolean | null | undefined>;

/** A branch a list holds, offered by its branch filter (several only for the Admin). */
export interface MaintenanceBranchOption {
  id: string;
  name: string;
}

/** Jobs and Waiting for repair: the branches the list holds before any filter. */
export interface MaintenanceBranchesSummary {
  branches: MaintenanceBranchOption[];
}

/** Repair history: its head (tiles and totals over every job; `jobs` and `pieces` empty here) and the references offered. */
export interface MaintenanceHistorySummary {
  history: MaintenanceRepairHistory;
  references: string[];
}

/** The workshop list: everything `GET /workshop` answers but the members, and how many rows the lists hold before a filter. */
export interface MaintenanceWorkshopSummary extends Omit<MaintenanceWorkshop, 'members'> {
  memberBranches: MaintenanceBranchOption[];
  members: number;
}

/** Repair rates: MS-23, the products a rate may be set for, and how many rates exist before a filter. */
export interface MaintenanceRepairRatesSummary {
  returnRecharge: 'TypedAtReturn' | 'SuggestedFromRate';
  products: MaintenanceProductOption[];
  rates: number;
}

/** A job's hours without its bookings: their total and the company's today. */
export type MaintenanceJobHoursSummary = Omit<MaintenanceJobHours, 'bookings'>;

/** A report without its rows: its totals line and words. */
export type MaintenanceRechargeSummary = Omit<MaintenanceRechargeReport, 'rows'>;
export type MaintenanceProductCostSummary = Omit<MaintenanceProductCostReport, 'rows'>;
export type MaintenanceProfitSummary = Omit<MaintenanceProfitReport, 'rows'>;

/** The practice ledger's month beside its entries (its five lists empty), and how many rows each list below holds. */
export interface MaintenanceLedgerSummary {
  ledger: MaintenanceLedger;
  postedAtIssue: number;
  openCustomerJobs: number;
  invoices: number;
  provisions: number;
}

export type MaintenancePostedAtIssue = MaintenanceLedger['postedAtIssue'][number];
export type MaintenanceOpenCustomerJob = MaintenanceLedger['openCustomerJobs'][number];
export type MaintenanceLedgerInvoice = MaintenanceLedger['invoices'][number];
export type MaintenanceLedgerEntry = MaintenanceLedger['entries'][number];

@Injectable({ providedIn: 'root' })
export class MaintenancePagesApi {
  private readonly api = inject(ApiService);

  private page<T, S = unknown>(route: string, query: Filters): Promise<PageResult<T, S>> {
    return this.api.getJson<PageResult<T, S>>(`/api/maintenance/${route}${pageQueryString(query)}`);
  }

  /** Maintenance › Jobs: `status`, `kind`, `branch`, `q`, `from`/`to` (company days, `yyyy-MM-dd`). */
  jobs(query: Filters): Promise<PageResult<MaintenanceJobSummary, MaintenanceBranchesSummary>> {
    return this.page('jobs/page', query);
  }

  /** Waiting for repair, the return-note lines: `branch`, `from`/`to` (day returned), `recharge` (`yes`/`no`). */
  waitingLines(query: Filters): Promise<PageResult<MaintenanceWaitingLine, MaintenanceBranchesSummary>> {
    return this.page('waiting/lines/page', query);
  }

  /** Waiting for repair, the products in maintenance: `branch`, `free` (`some`/`none`). */
  waitingBuckets(query: Filters): Promise<PageResult<MaintenanceBucket, MaintenanceBranchesSummary>> {
    return this.page('waiting/buckets/page', query);
  }

  /** Repair history, the pieces of a type: `productId` (required), `reference`. */
  historyPieces(query: Filters): Promise<PageResult<MaintenancePiece>> {
    return this.page('history/pieces/page', query);
  }

  /** Repair history, the jobs of a type or a piece: `productId` (required), `reference`. */
  historyJobs(query: Filters): Promise<PageResult<MaintenanceHistoryJob, MaintenanceHistorySummary>> {
    return this.page('history/jobs/page', query);
  }

  /** The workshop list: `branch`, `state` (`bookable`/`moved`/`left`). */
  workshop(query: Filters): Promise<PageResult<MaintenanceWorkshopMember, MaintenanceWorkshopSummary>> {
    return this.page('workshop/page', query);
  }

  /** Repair rates: `q`, `from`/`to` (day last set), `productId` (one product's rate). */
  repairRates(query: Filters): Promise<PageResult<MaintenanceRepairRate, MaintenanceRepairRatesSummary>> {
    return this.page('repair-rates/page', query);
  }

  /** A job's hours. */
  jobHours(jobId: string, query: Filters): Promise<PageResult<MaintenanceBooking, MaintenanceJobHoursSummary>> {
    return this.page(`jobs/${encodeURIComponent(jobId)}/hours/page`, query);
  }

  /** The reports over a period (`from`/`to`, company days). */
  recharges(query: Filters): Promise<PageResult<MaintenanceRechargeRow, MaintenanceRechargeSummary>> {
    return this.page('reports/recharges/page', query);
  }

  products(query: Filters): Promise<PageResult<MaintenanceProductCostRow, MaintenanceProductCostSummary>> {
    return this.page('reports/products/page', query);
  }

  profit(query: Filters): Promise<PageResult<MaintenanceProfitRow, MaintenanceProfitSummary>> {
    return this.page('reports/profit/page', query);
  }

  /** The practice ledger of a month (`year`, `month`; none: the company's month today). */
  ledgerEntries(query: Filters): Promise<PageResult<MaintenanceLedgerEntry, MaintenanceLedgerSummary>> {
    return this.page('practice-ledger/entries/page', query);
  }

  ledgerPostedAtIssue(query: Filters): Promise<PageResult<MaintenancePostedAtIssue>> {
    return this.page('practice-ledger/posted-at-issue/page', query);
  }

  /** `branchCode`: the branch the open customer jobs belong to. */
  ledgerOpenJobs(query: Filters): Promise<PageResult<MaintenanceOpenCustomerJob, { branches: string[] }>> {
    return this.page('practice-ledger/open-jobs/page', query);
  }

  /** `match`: `matched` or `unmatched`. */
  ledgerInvoices(query: Filters): Promise<PageResult<MaintenanceLedgerInvoice>> {
    return this.page('practice-ledger/invoices/page', query);
  }

  /** `held`: `yes` or `no`. */
  ledgerProvisions(query: Filters): Promise<PageResult<MaintenanceLedgerProvision>> {
    return this.page('practice-ledger/provisions/page', query);
  }
}
