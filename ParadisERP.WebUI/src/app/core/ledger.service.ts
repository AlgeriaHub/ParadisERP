import { Injectable, inject } from '@angular/core';
import { ApiService } from './api.service';

/**
 * The books, read from the server's double-entry ledger (`/api/ledger`).
 *
 * <p>Kept apart from {@link AdminService} because it is a different kind of thing: every other
 * back-office screen edits a record, and nothing here does. The ledger has no write route for
 * an entry at all — entries are raised by the documents that cause them — so the only call
 * below that changes anything is a reversal, which is the single correction the design allows.</p>
 *
 * <p>Online-only, like the rest of the back office. A report is a question about the whole
 * company, and a till's copy of the world is by design only its own.</p>
 */

export type LedgerAccountType = 'Asset' | 'Liability' | 'Income' | 'Expense' | 'Equity';

/** The documents that raise entries. Mirrors `LedgerSources` on the server. */
export type LedgerSource =
  | 'sale'
  | 'receiving'
  | 'opening'
  | 'stock-state'
  | 'stock-transfer'
  | 'stock-adjustment'
  | 'remittance'
  | 'payment-settlement';

export interface LedgerAccount {
  id: string;
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  type: LedgerAccountType;
  isSystem: boolean;
  isActive: boolean;
  sortOrder: number;
}

export interface LedgerEntryLine {
  id: string;
  accountId: string;
  accountCode: string;
  accountNameFr: string;
  accountType: LedgerAccountType;
  branchId: string;
  debit: number;
  credit: number;
  memo: string | null;
  /**
   * The other branches' lines of an inter-branch entry, summed: what a branch holding only part of
   * the entry reads in their place (P0.1b). No account, branch or memo — the totals alone.
   */
  folded?: boolean;
}

export interface LedgerEntry {
  id: string;
  entryDate: string;
  createdAtUtc: string;
  branchId: string;
  branchName: string | null;
  sourceType: string;
  sourceId: string;
  description: string | null;
  isReversal: boolean;
  reversalOfEntryId: string | null;
  /** On a reversal, the date of the entry it corrects (COR-E01): a closed month is corrected in the current one. */
  reversalOfEntryDate?: string | null;
  isInterBranch: boolean;
  counterpartBranchId: string | null;
  createdBy: string | null;
  total: number;
  lines: LedgerEntryLine[];
}

export interface TrialBalanceRow {
  accountId: string;
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  type: LedgerAccountType;
  debit: number;
  credit: number;
  /** Already on the account's natural side: a liability reads positive when it is owed. */
  balance: number;
}

export interface TrialBalance {
  from: string;
  to: string;
  branchId: string | null;
  consolidated: boolean;
  rows: TrialBalanceRow[];
  totalDebit: number;
  totalCredit: number;
  balanced: boolean;
}

export interface ProfitAndLossLine {
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  amount: number;
}

export interface BranchResult {
  branchId: string;
  branchName: string;
  income: number;
  expenses: number;
  netProfit: number;
}

export interface ProfitAndLoss {
  from: string;
  to: string;
  branchId: string | null;
  consolidated: boolean;
  income: ProfitAndLossLine[];
  expenses: ProfitAndLossLine[];
  totalIncome: number;
  totalExpenses: number;
  netProfit: number;
  byBranch: BranchResult[];
  /**
   * What consolidation took out: the profit the group would have reported had movements
   * between its own branches been counted. Expected to stay zero, and reported rather than
   * assumed so that the day it is not, the screen can say so.
   */
  eliminatedInterBranch: number;
}

export interface BalanceSheetLine {
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  amount: number;
}

export interface BalanceSheet {
  asOf: string;
  branchId: string | null;
  consolidated: boolean;
  assets: BalanceSheetLine[];
  liabilities: BalanceSheetLine[];
  equity: BalanceSheetLine[];
  totalAssets: number;
  totalLiabilities: number;
  totalEquity: number;
  /** The period's result, computed at read time: there is no year-end close in this system. */
  netResult: number;
  balanced: boolean;
}

export interface AccountStatementRow {
  entryId: string;
  entryDate: string;
  branchId: string;
  sourceType: string;
  sourceId: string;
  description: string | null;
  isReversal: boolean;
  debit: number;
  credit: number;
  runningBalance: number;
}

export interface AccountStatement {
  accountId: string;
  code: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  type: LedgerAccountType;
  from: string;
  to: string;
  branchId: string | null;
  opening: number;
  closing: number;
  rows: AccountStatementRow[];
}

export interface PeriodQuery {
  /** Inclusive, ISO. */
  from: string;
  /** Exclusive, ISO — the server filters `entryDate < to`. */
  to: string;
  branchId?: string | null;
}

/** The server refuses a wider window than this; the screens check first and say so in words. */
export const MAX_PERIOD_DAYS = 400;

/** One close or reopen of the books (CLO-01), as the trail shows it. `closedThrough` is `yyyy-MM-dd`, or null when all is open. */
export interface BookClosingRow {
  id: string;
  closedThrough: string | null;
  isReopen: boolean;
  reason: string | null;
  userName: string | null;
  atUtc: string;
}

/** The books now and every close and reopen, newest first (`GET /api/ledger/closing`). */
export interface BookClosingState {
  closedThrough: string | null;
  history: BookClosingRow[];
}

@Injectable({ providedIn: 'root' })
export class LedgerService {
  private readonly api = inject(ApiService);

  listAccounts(includeInactive = false): Promise<LedgerAccount[]> {
    return this.api.getJson<LedgerAccount[]>(`/api/ledger/accounts?includeInactive=${includeInactive}`);
  }

  listEntries(q: {
    from?: string;
    to?: string;
    branchId?: string | null;
    sourceType?: string | null;
    sourceId?: string | null;
    take?: number;
  }): Promise<LedgerEntry[]> {
    return this.api.getJson<LedgerEntry[]>(`/api/ledger/entries${query(q)}`);
  }

  getEntry(id: string): Promise<LedgerEntry> {
    return this.api.getJson<LedgerEntry>(`/api/ledger/entries/${id}`);
  }

  /** CLO-01: the books' state and the trail of closes and reopens. */
  closing(): Promise<BookClosingState> {
    return this.api.getJson<BookClosingState>('/api/ledger/closing');
  }

  /** Close the books through `through` (`yyyy-MM-dd`, the last day of an ended month). The Admin's. */
  closeBooks(through: string, note: string | null): Promise<BookClosingRow> {
    return this.api.postJson<BookClosingRow>('/api/ledger/closing/close', { through, note });
  }

  /** Reopen back to `through` (an earlier month end), or every month when null. A reason is required. The Admin's. */
  reopenBooks(through: string | null, reason: string): Promise<BookClosingRow> {
    return this.api.postJson<BookClosingRow>('/api/ledger/closing/reopen', { through, reason });
  }

  /** The original stays and the mirror image goes beside it; nothing is ever deleted. */
  reverseEntry(id: string, reason: string): Promise<{ reversalId: string; reversedEntryId: string }> {
    return this.api.postJson(`/api/ledger/entries/${id}/reverse`, { reason });
  }

  trialBalance(q: PeriodQuery): Promise<TrialBalance> {
    return this.api.getJson<TrialBalance>(`/api/ledger/reports/trial-balance${query(q)}`);
  }

  profitAndLoss(q: PeriodQuery): Promise<ProfitAndLoss> {
    return this.api.getJson<ProfitAndLoss>(`/api/ledger/reports/profit-and-loss${query(q)}`);
  }

  balanceSheet(q: { asOf: string; branchId?: string | null }): Promise<BalanceSheet> {
    return this.api.getJson<BalanceSheet>(`/api/ledger/reports/balance-sheet${query(q)}`);
  }

  accountStatement(q: PeriodQuery & { accountId: string; take?: number }): Promise<AccountStatement> {
    return this.api.getJson<AccountStatement>(`/api/ledger/reports/account-statement${query(q)}`);
  }
}

/**
 * Drops empty parameters rather than sending them blank. `branchId=` is not "the group" to the
 * server — it is an unparseable Guid and a 400.
 */
type QueryValue = string | number | boolean | null | undefined;

function query(params: object): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params) as [string, QueryValue][]) {
    if (value === null || value === undefined || value === '') continue;
    search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}
