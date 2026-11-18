import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

/**
 * Manufacturing › Costing settings (Phase C1, `/api/manufacturing/accounting`): the accountant's open costing and
 * accounting choices, set by the admin meanwhile (owner, 2026-11-08), and the account map, seeded empty.
 */
export interface CostingSettings {
  model: 'ByNature' | 'WorkInProgress';
  overheadPerLabourHour: number;
  releaseRule: 'StandardMaterialPlusConversion' | 'EstimatedUnitCost' | 'CostToDate';
  employerBurdenUse: 'None' | 'ReportsOnly' | 'StockValue';
  employerBurdenPercent: number;
  monthEndWorkInProgress: 'Monthly' | 'YearEndOnly';
  lateCosts: 'StayInCharges' | 'VarianceAccount';
  variances: 'ManagementOnly' | 'VarianceAccount';
  /** C-07: expenses before the recoverable VAT at `expenseVatPercent`, or as recorded. */
  expenseBasis: 'BeforeRecoverableVat' | 'AsRecorded';
  expenseVatPercent: number;
  /** C-09: no recovery for rejects kept as scrap, or a share of their standard material value. */
  scrapRecovery: 'None' | 'PercentOfMaterial';
  scrapRecoveryPercent: number;
  /** C-12: manufacturing may move stock. `stockOperationsByServer`: the server's configuration switches it on anyway. */
  stockOperations: boolean;
  stockOperationsByServer: boolean;
  /** The accountant confirmed the choices: cost sheets with final inputs read approved. */
  accountantApproved: boolean;
  approvedBy: string | null;
  approvedAtUtc: string | null;
  /** Phase D: the first month the accountant may post (`yyyy-MM`); null, nothing posts. */
  postingFromMonth: string | null;
  /**
   * C-05: every worked hour at the blended actual rate (suggested), or payroll's overtime premium kept to the hours the
   * daily record marks as overtime.
   */
  labourAbsorption: 'BlendedActualRate' | 'PremiumToOvertime';
  updatedAtUtc: string | null;
  updatedBy: string | null;
}

export type CostingSettingsDraft = Omit<CostingSettings, 'updatedAtUtc' | 'updatedBy' | 'stockOperationsByServer' | 'approvedBy' | 'approvedAtUtc'>;

export interface AccountSlot {
  slot: string;
  ledgerAccountId: string | null;
  accountCode: string | null;
  accountNameAr: string | null;
  accountNameFr: string | null;
  accountNameEn: string | null;
  updatedAtUtc: string | null;
  updatedBy: string | null;
}

/** Phase C2, the practice ledger: one side of an entry, on an account-map slot and the account it names (none while unmapped). */
export interface PracticeLine {
  slot: string;
  accountId: string | null;
  accountCode: string | null;
  accountNameAr: string | null;
  accountNameFr: string | null;
  accountNameEn: string | null;
  debit: number;
  credit: number;
}

export interface PracticeEntry {
  kind: string;
  day: string;
  orderId: string;
  orderNumber: string;
  branchId: string;
  branchCode: string;
  branchNameAr: string;
  branchNameFr: string;
  branchNameEn: string;
  document: string | null;
  lines: PracticeLine[];
}

export type PracticeAccount = PracticeLine;

export interface PracticeLedger {
  year: number;
  month: number;
  monthToDate: boolean;
  model: CostingSettings['model'];
  monthEndWorkInProgress: CostingSettings['monthEndWorkInProgress'];
  lateCostTreatment: CostingSettings['lateCosts'];
  variances: CostingSettings['variances'];
  entries: PracticeEntry[];
  accounts: PracticeAccount[];
  lateCosts: { orderId: string; orderNumber: string; element: string; amount: number }[];
  checks: { code: string; passed: boolean; expected: number; actual: number }[];
  unmappedSlots: string[];
  notes: string[];
  totalDebit: number;
  totalCredit: number;
  /** Phase D: the first month that may post, and this month's postings (the last one live unless reversed). */
  postingFromMonth: string | null;
  postings: PracticePosting[] | null;
}

export interface PracticePosting {
  generation: number;
  entries: number;
  totalDebit: number;
  postedAtUtc: string;
  postedBy: string | null;
  reversedAtUtc: string | null;
  reversedBy: string | null;
}

@Injectable({ providedIn: 'root' })export class CostingSettingsApi {
  private readonly api = inject(ApiService);

  settings(): Promise<CostingSettings> {
    return this.api.getJson<CostingSettings>('/api/manufacturing/accounting/settings');
  }

  saveSettings(draft: CostingSettingsDraft): Promise<CostingSettings> {
    return this.api.putJson<CostingSettings>('/api/manufacturing/accounting/settings', draft);
  }

  accountMap(): Promise<AccountSlot[]> {
    return this.api.getJson<AccountSlot[]>('/api/manufacturing/accounting/account-map');
  }

  saveAccountMap(lines: { slot: string; ledgerAccountId: string | null }[]): Promise<AccountSlot[]> {
    return this.api.putJson<AccountSlot[]>('/api/manufacturing/accounting/account-map', { lines });
  }

  /** One month of the entries the chosen model would make (the company's month when none is asked); posted nowhere. */
  practiceLedger(year: number | null, month: number | null, lineId: string | null): Promise<PracticeLedger> {
    const q = new URLSearchParams();
    if (year !== null && month !== null) {
      q.set('year', String(year));
      q.set('month', String(month));
    }
    if (lineId) q.set('lineId', lineId);
    const s = q.toString();
    return this.api.getJson<PracticeLedger>(`/api/manufacturing/accounting/practice-ledger${s ? `?${s}` : ''}`);
  }

  /** Phase D: posts the closed month to the ledger, as the practice ledger shows it. */
  postMonth(year: number, month: number): Promise<PracticeLedger> {
    return this.api.postJson<PracticeLedger>('/api/manufacturing/accounting/practice-ledger/post', { year, month });
  }

  /** Phase D: takes the month's posting back with reversal entries (the last month posted only). */
  reverseMonth(year: number, month: number): Promise<PracticeLedger> {
    return this.api.postJson<PracticeLedger>('/api/manufacturing/accounting/practice-ledger/reverse', { year, month });
  }
}