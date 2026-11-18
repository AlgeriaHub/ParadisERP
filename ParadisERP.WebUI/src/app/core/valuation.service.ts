import { Injectable, inject } from '@angular/core';
import { ApiService } from './api.service';

/**
 * The weighted-average shadow (P0.4b, `GET /api/valuation/shadow`): stock valued at a moving average
 * since the opening, beside today's cost price. Read only — nothing is posted, stored or priced from it.
 * The settings are the choices the accountant has not answered, set by whoever holds accounting.manage.
 */
export type ValuationScope = 'Company' | 'Location';
export type RefundValuation = 'SaleCost' | 'CurrentAverage';
/** How sales are costed (C-01): the cost price (today's rule, the default) or the weighted average. */
export type SalesCostMethod = 'CostPrice' | 'WeightedAverage';
/**
 * When goods are received, the product's cost price becomes (B-F01): the cost the note states (today's rule, the default),
 * the weighted average after the receipt, or nothing changes. In every mode a stated 0 never replaces a non-zero price.
 */
export type ReceiptCostRule = 'LastCost' | 'WeightedAverage' | 'Unchanged';
/** C-16: a covered negative-stock gap is posted by the accountant (today, the default) or with the inflow that covers it. */
export type GapPosting = 'Accountant' | 'Automatic';
/** B-F01: a change of the cost price leaves the stock on hand as it is (today, the default) or revalues it. */
export type CostPriceRevaluation = 'None' | 'StockOnHand';
/** A receiving line stated at 0 is valued at 0 (today, the default) or at the product's cost price. */
export type ZeroCostReceipt = 'Zero' | 'CostPrice';

export interface ValuationSettings {
  scope: ValuationScope;
  refundCost: RefundValuation;
  unitCostDecimals: number;
  startedAtUtc: string | null;
  updatedAtUtc: string | null;
  updatedBy: string | null;
  salesCost?: SalesCostMethod;
  salesCostChangedAtUtc?: string | null;
  salesCostChangedBy?: string | null;
  /** P0.3: an invoice typed in the back office takes its goods out of stock and posts their cost. Off by default. */
  directInvoiceStock?: boolean;
  directInvoiceStockChangedAtUtc?: string | null;
  directInvoiceStockChangedBy?: string | null;
  receiptCostRule?: ReceiptCostRule;
  gapPosting?: GapPosting;
  costPriceRevaluation?: CostPriceRevaluation;
  zeroCostReceipt?: ZeroCostReceipt;
}

/** A slot of the account map the switch posts to; unmapped, nothing is guessed. */
export interface ValuationAccount {
  slot: string;
  mapped: boolean;
  code: string | null;
  nameAr: string | null;
  nameFr: string | null;
  nameEn: string | null;
}

/** A negative-stock gap (C-16) settled since the switch, waiting to post. */
export interface ValuationGap {
  movementId: string;
  atUtc: string;
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  branchId: string;
  kind: string;
  documentNumber: string | null;
  amount: number;
}

export interface SalesCostChange {
  id: string;
  method: SalesCostMethod;
  atUtc: string;
  by: string | null;
  shadowValue: number;
  bookValue: number;
  adjustment: number;
  ledgerEntryId: string | null;
}

/**
 * How sales are costed and what switching would post (`GET /api/valuation/sales-cost`, accounting.manage): stock at the
 * average against the stock accounts, the difference (the opening adjustment, C-02), the gaps waiting (C-16).
 */
export interface SalesCosting {
  settings: ValuationSettings;
  shadowValue: number;
  bookValue: number;
  adjustment: number;
  stockAccounts: string[];
  openingAdjustmentAccount: ValuationAccount;
  valuationDifferenceAccount: ValuationAccount;
  pendingGaps: ValuationGap[];
  pendingGapTotal: number;
  fallbackLines: number;
  history: SalesCostChange[];
  /** Rows of the other stock documents (adjustments, write-offs, state changes, transfers, rental returns) valued at
   *  their reference cost for want of an average while sales were costed at it. Absent from an older server. */
  fallbackMovements?: number;
}

export interface ValuationRow {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  branchId: string | null;
  branchNameAr: string | null;
  branchNameFr: string | null;
  branchNameEn: string | null;
  openingQty: number;
  openingValue: number;
  inQty: number;
  inValue: number;
  outQty: number;
  outValue: number;
  movedInQty: number;
  movedInValue: number;
  movedOutQty: number;
  movedOutValue: number;
  closingQty: number;
  closingValue: number;
  averageCost: number | null;
  referenceCost: number;
  referenceValue: number;
  difference: number;
  negativeStockGap: number;
  postedCostOfSales: number;
  shadowCostOfSales: number;
  outputsAtReference: number;
  outflowsWithoutAverage: number;
  unclassified: number;
}

export interface ValuationTotals {
  openingValue: number;
  inValue: number;
  outValue: number;
  closingValue: number;
  referenceValue: number;
  difference: number;
  negativeStockGap: number;
  inTransitValue: number;
  postedCostOfSales: number;
  shadowCostOfSales: number;
}

export interface ValuationReport {
  settings: ValuationSettings;
  generatedAtUtc: string;
  rows: ValuationRow[];
  totals: ValuationTotals;
}

export interface ValuationLine {
  movementId: string;
  atUtc: string;
  kind: string;
  branchId: string;
  branchNameAr: string | null;
  branchNameFr: string | null;
  branchNameEn: string | null;
  documentNumber: string | null;
  qtyIn: number;
  qtyOut: number;
  referenceUnitCost: number | null;
  shadowUnitCost: number | null;
  shadowValue: number;
  poolQtyAfter: number;
  averageAfter: number | null;
  gap: number;
  notes: string[];
}

export interface ValuationProduct {
  settings: ValuationSettings;
  summary: ValuationRow | null;
  places: ValuationRow[];
  lines: ValuationLine[];
}

@Injectable({ providedIn: 'root' })
export class ValuationService {
  private readonly api = inject(ApiService);

  report(branchId: string | null): Promise<ValuationReport> {
    return this.api.getJson<ValuationReport>(`/api/valuation/shadow${branchId ? `?branchId=${encodeURIComponent(branchId)}` : ''}`);
  }

  product(productId: string): Promise<ValuationProduct> {
    return this.api.getJson<ValuationProduct>(`/api/valuation/shadow/products/${encodeURIComponent(productId)}`);
  }

  saveSettings(
    req: Pick<ValuationSettings, 'scope' | 'refundCost' | 'unitCostDecimals'> & {
      directInvoiceStock?: boolean;
      receiptCostRule?: ReceiptCostRule;
      gapPosting?: GapPosting;
      costPriceRevaluation?: CostPriceRevaluation;
      zeroCostReceipt?: ZeroCostReceipt;
    },
  ): Promise<ValuationSettings> {
    return this.api.putJson<ValuationSettings>('/api/valuation/shadow/settings', req);
  }

  salesCosting(): Promise<SalesCosting> {
    return this.api.getJson<SalesCosting>('/api/valuation/sales-cost');
  }

  /** Switching on states the adjustment the accountant was shown; the server refuses if it has moved since. */
  switchSalesCost(method: SalesCostMethod, expectedAdjustment: number | null): Promise<SalesCosting> {
    return this.api.postJson<SalesCosting>('/api/valuation/sales-cost/switch', { method, expectedAdjustment });
  }

  postGaps(): Promise<SalesCosting> {
    return this.api.postJson<SalesCosting>('/api/valuation/sales-cost/gaps/post', {});
  }
}
