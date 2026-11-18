import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { PracticeAccount, PracticeEntry, PracticePosting } from '../manufacturing/costing-settings-api.service';

/**
 * One slot of the maintenance account map (MS-31, Phase M6): the account it names (null: not mapped, never guessed), today's
 * account for the same event as a hint, and whether it is one of manufacturing's stock slots (`shared`: mapped in
 * Manufacturing › Costing settings, shown here read only).
 */
export interface MaintenanceAccountSlot {
  slot: string;
  ledgerAccountId: string | null;
  accountCode: string | null;
  accountNameAr: string | null;
  accountNameFr: string | null;
  accountNameEn: string | null;
  hintCode: string | null;
  shared: boolean;
  updatedAtUtc: string | null;
  updatedBy: string | null;
}

/**
 * The maintenance section of the practice ledger for one month (Phase M6, docs/maintenance/plan.md §5): manufacturing's
 * entry shape (an entry's order is the job), plus what maintenance lists beside its entries.
 */
export interface MaintenanceLedger {
  year: number;
  month: number;
  monthToDate: boolean;
  branchId: string | null;
  labourInBooks: 'ReportOnly' | 'MovedIntoJobs';
  openJobsAtPeriodEnd: 'LeftInExpensesListed' | 'WorkInProgressAtYearEnd' | 'WorkInProgressMonthly';
  partsPosting: 'PracticeLedgerByMonth' | 'AtIssue';
  ownRepairs: string;
  accountantApproved: boolean;
  entries: PracticeEntry[];
  accounts: PracticeAccount[];
  checks: { code: string; passed: boolean; expected: number; actual: number }[];
  unmappedSlots: string[];
  notes: string[];
  totalDebit: number;
  totalCredit: number;
  postedAtIssue: { document: string; jobId: string; jobNumber: string; day: string; value: number }[];
  /** Open customer jobs at the month's end; `costToDate` null when it would give a pay rate back. */
  openCustomerJobs: { jobId: string; jobNumber: string; branchId: string; branchCode: string; costToDate: number | null }[];
  invoices: { number: string; day: string; jobId: string | null; jobNumber: string | null; jobMatched: boolean; netAmount: number }[];
  postingFromMonth: string | null;
  postings: PracticePosting[];
  /** Phase M7c: MS-37 as saved, and the guarantee provisions the month moves. */
  rework?: 'AddedToOriginalJob' | 'OwnJob' | 'Provision';
  provisions?: MaintenanceLedgerProvision[] | null;
}

/**
 * A guarantee provision the month moves (MS-37 "provision", Phase M7c): set aside at a customer job's invoice, used by its
 * reworks' costs, what is left released when its guarantee ends (`guaranteeUntil`, `yyyy-MM-dd`).
 */
export interface MaintenanceLedgerProvision {
  jobId: string;
  jobNumber: string;
  invoiceNumber: string | null;
  amount: number;
  booked: number;
  used: number;
  released: number;
  heldAtEnd: number;
  guaranteeUntil: string;
}

/** `/api/maintenance/account-map` and `/api/maintenance/practice-ledger` (Phase M6). */
@Injectable({ providedIn: 'root' })
export class MaintenanceLedgerApi {
  private readonly api = inject(ApiService);

  accountMap(): Promise<MaintenanceAccountSlot[]> {
    return this.api.getJson<MaintenanceAccountSlot[]>('/api/maintenance/account-map');
  }

  /** Maintenance's own slots only; the shared stock slots are mapped in Costing settings. */
  saveAccountMap(lines: { slot: string; ledgerAccountId: string | null }[]): Promise<MaintenanceAccountSlot[]> {
    return this.api.putJson<MaintenanceAccountSlot[]>('/api/maintenance/account-map', { lines });
  }

  /** One month (the company's month today when none is asked), at a branch or every branch the caller reads. */
  month(year: number | null, month: number | null, branchId: string | null): Promise<MaintenanceLedger> {
    const q = new URLSearchParams();
    if (year !== null && month !== null) {
      q.set('year', String(year));
      q.set('month', String(month));
    }
    if (branchId) q.set('branchId', branchId);
    const s = q.toString();
    return this.api.getJson<MaintenanceLedger>(`/api/maintenance/practice-ledger${s ? `?${s}` : ''}`);
  }

  post(year: number, month: number): Promise<MaintenanceLedger> {
    return this.api.postJson<MaintenanceLedger>('/api/maintenance/practice-ledger/post', { year, month });
  }

  reverse(year: number, month: number): Promise<MaintenanceLedger> {
    return this.api.postJson<MaintenanceLedger>('/api/maintenance/practice-ledger/reverse', { year, month });
  }
}
