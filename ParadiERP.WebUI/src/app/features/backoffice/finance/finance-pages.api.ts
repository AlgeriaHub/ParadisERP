import { Injectable, inject } from '@angular/core';
import type { AdminCashMovement, AdminSale, LowStockRow, MissingCostReport, MissingCostRow, PendingPayment } from '../../../core/admin.service';
import { ApiService } from '../../../core/api.service';
import type {
  AccountStatement,
  AccountStatementRow,
  BookClosingRow,
  LedgerEntry,
  TrialBalance,
  TrialBalanceRow,
} from '../../../core/ledger.service';
import { PageResult, pageQueryString } from '../../../core/paging';
import type { SalesCosting, ValuationGap, ValuationLine, ValuationProduct, ValuationReport, ValuationRow } from '../../../core/valuation.service';
import type {
  FixedAssetDepreciation,
  FixedAssetHistory,
  FixedAssetHistoryLine,
  FixedAssetPool,
  FixedAssetRegister,
} from '../fixed-assets/fixed-assets-api.service';
import type { PracticeEntry } from '../manufacturing/costing-settings-api.service';
import type { AwaitingRow } from './awaiting';

/**
 * The finance lists a page at a time (owner, 2026-11-17: "I need to paginate in backend, not frontend"): every page,
 * size or filter change is a request, the filters run on the server over every row, and the figures above a list
 * (tiles, a totals row, a running balance) come in the page's `summary`, computed over all the filtered rows
 * (`ParadiERP.API/Endpoints/Paging/FinancePageRoutes.cs`). Each route sits beside the list route the screen used to read
 * whole, which stays for the papers and exports. A filter left at "all", empty or null is not sent.
 */

/** Every page request: the offset and the size (10, 20, 50 or 100). */
export interface PageAt {
  skip: number;
  take: number;
}

type Query = Record<string, string | number | boolean | null | undefined>;

// ---------- the books ----------

/** The trial balance's figures, the whole balance's whatever page is shown. */
export type TrialBalanceSummary = Omit<TrialBalance, 'rows'>;

/**
 * The account and its figures over the whole window; `pageOpening` is the balance before the page's first row (the
 * opening on the first page).
 */
export type AccountStatementSummary = Omit<AccountStatement, 'rows'> & {
  totalDebit: number;
  totalCredit: number;
  pageOpening: number;
};

/** Who recorded the period's entries (the "by" filter's choices) and how many there are before the screen's filters. */
export interface JournalSummary {
  authors: string[];
  all: number;
}

/** The books' state and the trail's size, whatever the filters. */
export interface BookClosingSummary {
  closedThrough: string | null;
  last: BookClosingRow | null;
  all: number;
}

export interface PeriodPage extends PageAt {
  /** ISO instants, the server's `entryDate >= from` and `< to`. */
  from: string;
  to: string;
  branchId?: string | null;
}

// ---------- treasury ----------

/** The cheque register's tiles: count and amount per state over every instrument, whatever the filters. */
export interface ChequesSummary {
  pendingCount: number;
  pendingTotal: number;
  confirmedCount: number;
  confirmedTotal: number;
  bouncedCount: number;
  bouncedTotal: number;
  all: number;
}

/** Awaiting collection's figures over the filtered rows; the methods the filter offers are every row's. */
export interface AwaitingSummary {
  total: number;
  count: number;
  overdueTotal: number;
  overdueCount: number;
  methodKeys: string[];
  all: number;
}

/** A box's money in and out over every movement, its latest balance, and the page's balance before and after it. */
export interface CashMovementsSummary {
  cashBoxId: string;
  totalIn: number;
  totalOut: number;
  balance: number | null;
  pageOpening: number | null;
  pageClosing: number | null;
}

// ---------- sales and reports ----------

/** The cashiers of the period (the filter's choices) and its sales before the screen's filters. */
export interface SalesListSummary {
  cashiers: string[];
  all: number;
}

export type MissingCostSummary = Omit<MissingCostReport, 'rows'> & { all: number };

export interface LowStockSummary {
  all: number;
}

// ---------- fixed assets and valuation ----------

export type FixedAssetRegisterSummary = Omit<FixedAssetRegister, 'pools'> & { poolCount: number };
export type FixedAssetHistorySummary = Omit<FixedAssetHistory, 'lines'> & { kinds: string[]; lineCount: number };
export type FixedAssetDepreciationSummary = Omit<FixedAssetDepreciation, 'entries'> & { entryCount: number };
export type ValuationSummary = Omit<ValuationReport, 'rows'> & { all: number };
export type ValuationProductSummary = Omit<ValuationProduct, 'lines'> & { productId: string; lineCount: number };
/** The costing status itself, its pending gaps left out (they are the page); `pendingGapTotal` is every gap's. */
export type SalesCostSummary = SalesCosting;

@Injectable({ providedIn: 'root' })
export class FinancePagesApi {
  private readonly api = inject(ApiService);

  private page<T, S>(path: string, query: Query): Promise<PageResult<T, S>> {
    return this.api.getJson<PageResult<T, S>>(`${path}${pageQueryString(query)}`);
  }

  // ---------- the books ----------

  trialBalancePage(q: PeriodPage): Promise<PageResult<TrialBalanceRow, TrialBalanceSummary>> {
    return this.page('/api/ledger/reports/trial-balance/page', { ...q });
  }

  accountStatementPage(q: PeriodPage & { accountId: string }): Promise<PageResult<AccountStatementRow, AccountStatementSummary>> {
    return this.page('/api/ledger/reports/account-statement/page', { ...q });
  }

  journalPage(
    q: PeriodPage & { sourceType?: string | null; reversal?: string; interBranch?: string; by?: string },
  ): Promise<PageResult<LedgerEntry, JournalSummary>> {
    return this.page('/api/ledger/entries/page', { ...q });
  }

  bookClosingPage(q: PageAt & { action?: string; from?: string; to?: string }): Promise<PageResult<BookClosingRow, BookClosingSummary>> {
    return this.page('/api/ledger/closing/page', { ...q });
  }

  // ---------- treasury ----------

  chequesPage(q: PageAt & { status?: string; dueFrom?: string; dueTo?: string }): Promise<PageResult<PendingPayment, ChequesSummary>> {
    return this.page('/api/treasury/pending-payments/page', { ...q });
  }

  awaitingPage(
    q: PageAt & {
      source?: string;
      q?: string;
      due?: string;
      method?: string;
      dueFrom?: string;
      dueTo?: string;
      branchId?: string | null;
    },
  ): Promise<PageResult<AwaitingRow, AwaitingSummary>> {
    return this.page('/api/treasury/awaiting/page', { ...q });
  }

  cashMovementsPage(cashBoxId: string, q: PageAt): Promise<PageResult<AdminCashMovement, CashMovementsSummary>> {
    return this.page(`/api/treasury/cash-boxes/${encodeURIComponent(cashBoxId)}/movements/page`, { ...q });
  }

  // ---------- sales and reports ----------

  salesPage(
    q: PeriodPage & { status?: string; cashier?: string; tax?: string },
  ): Promise<PageResult<AdminSale, SalesListSummary>> {
    return this.page('/api/sales/page', { ...q });
  }

  /** Days as `yyyy-MM-dd` on the company's clock; none asks for the current month. */
  missingCostPage(
    q: PageAt & { from?: string | null; to?: string | null; branchId?: string | null; cost?: string },
  ): Promise<PageResult<MissingCostRow, MissingCostSummary>> {
    return this.page('/api/reports/missing-cost/page', { ...q });
  }

  lowStockPage(q: PageAt & { branchId?: string | null; stock?: string }): Promise<PageResult<LowStockRow, LowStockSummary>> {
    return this.page('/api/catalog/products/low-stock/page', { ...q });
  }

  // ---------- fixed assets ----------

  fixedAssetRegisterPage(
    q: PageAt & { year?: number | null; month?: number | null; branchId?: string | null; method?: string; depreciated?: string },
  ): Promise<PageResult<FixedAssetPool, FixedAssetRegisterSummary>> {
    return this.page('/api/fixed-assets/register/page', { ...q });
  }

  fixedAssetHistoryPage(
    q: PageAt & { productId: string; branchId: string; kind?: string; posted?: string },
  ): Promise<PageResult<FixedAssetHistoryLine, FixedAssetHistorySummary>> {
    return this.page('/api/fixed-assets/register/history/page', { ...q });
  }

  fixedAssetDepreciationPage(
    q: PageAt & { year?: number | null; month?: number | null; branchId?: string | null },
  ): Promise<PageResult<PracticeEntry, FixedAssetDepreciationSummary>> {
    return this.page('/api/fixed-assets/depreciation/page', { ...q });
  }

  // ---------- stock valuation ----------

  valuationPage(
    q: PageAt & { branchId?: string | null; qty?: string; difference?: string; notes?: string },
  ): Promise<PageResult<ValuationRow, ValuationSummary>> {
    return this.page('/api/valuation/shadow/page', { ...q });
  }

  valuationProductPage(productId: string, q: PageAt): Promise<PageResult<ValuationLine, ValuationProductSummary>> {
    return this.page(`/api/valuation/shadow/products/${encodeURIComponent(productId)}/page`, { ...q });
  }

  salesCostGapsPage(q: PageAt): Promise<PageResult<ValuationGap, SalesCostSummary>> {
    return this.page('/api/valuation/sales-cost/page', { ...q });
  }
}
