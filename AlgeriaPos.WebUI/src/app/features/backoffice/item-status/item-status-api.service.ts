import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

export type ItemTypeFilter = 'rental' | 'sale';
export type ItemStatusFilter = 'available' | 'rented' | 'maintenance' | 'scrap' | 'unexplained' | 'transit';

export interface ItemStatusQuery {
  branchId?: string | null;
  q?: string;
  productIds?: string | string[];
  itemType?: ItemTypeFilter | null;
  status?: ItemStatusFilter | null;
  customerId?: string | null;
  contractId?: string | null;
  includeInactive?: boolean | null;
}

export interface ItemStatusScope {
  kind: 'company' | 'location' | 'own';
  locationId?: string | null;
  othersAvailableShown: boolean;
}

export interface ItemStatusTotals {
  total: number;
  warehouses: number;
  branches: number;
  available: number;
  rented: number;
  maintenance: number;
  scrap: number;
  stillOut: number;
  unexplainedOnRent: number;
  leftToIssue: number;
  otherAvailable: number | null;
  /** Dispatched from the locations seen in full and not yet received; already in `total` (owner, 2026-10-12). */
  inTransit: number;
}

export interface ItemStatusRow {
  productId: string;
  sku: string;
  barcode: string | null;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  unit: string;
  isRentable: boolean;
  isActive: boolean;
  total: number;
  warehouses: number;
  branches: number;
  available: number;
  rented: number;
  maintenance: number;
  scrap: number;
  stillOut: number;
  unexplainedOnRent: number;
  leftToIssue: number;
  otherAvailable: number | null;
  /** Dispatched from the locations seen in full and not yet received; already in `total` (owner, 2026-10-12). */
  inTransit: number;
}

export interface ItemStatusReport {
  asOfUtc: string;
  scope: ItemStatusScope;
  rows: ItemStatusRow[];
  totals: ItemStatusTotals;
}

export interface ItemLocationRow {
  branchId: string;
  code: string | null;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  isWarehouse: boolean;
  isMain: boolean;
  isActive: boolean;
  availableOnly: boolean;
  available: number;
  onRent: number | null;
  maintenance: number | null;
  scrap: number | null;
  total: number | null;
  stillOut: number | null;
  unexplainedOnRent: number | null;
  /** Dispatched from this location and not yet received (in `total`); null when available only. */
  inTransit: number | null;
}

/**
 * One open transfer line of the article touching a location seen in full. `countedHere`: its source is
 * seen in full, so it is in the scope's total — goods in transit count where they were dispatched from.
 */
export interface ItemInTransitRow {
  transferId: string;
  number: string;
  status: 'Dispatched' | 'PartlyReceived' | 'Received' | 'Cancelled';
  fromBranchId: string;
  fromNameAr: string;
  fromNameFr: string;
  fromNameEn: string;
  toBranchId: string;
  toNameAr: string;
  toNameFr: string;
  toNameEn: string;
  dispatchedAtUtc: string;
  qty: number;
  countedHere: boolean;
}

export interface ItemRentedRow {
  contractId: string;
  contractNumber: string;
  contractStatus: string;
  customerId: string;
  customerCode: string | null;
  customerName: string;
  issueBranchId: string;
  issueBranchNameAr: string;
  issueBranchNameFr: string;
  issueBranchNameEn: string;
  siteName: string | null;
  issueNoteId: string | null;
  issueNoteNumber: string | null;
  issuedOn: string;
  expectedReturnOn: string | null;
  overdue: boolean;
  qty: number;
}

export interface ItemLeftToIssueRow {
  contractId: string;
  contractNumber: string;
  customerId: string;
  customerName: string;
  qty: number;
}

export type MovementType =
  | 'sale'
  | 'refund'
  | 'receiving'
  | 'opening'
  | 'transfer'
  | 'transfer-out'
  | 'transfer-in'
  | 'transfer-cancel'
  | 'transfer-lost'
  | 'rental-issue'
  | 'rental-return'
  | 'rental-return-transfer'
  | 'state'
  | 'adjustment';

export type MovementState = 'available' | 'onrent' | 'maintenance' | 'scrap' | 'writtenoff';

export interface ItemMovementRow {
  atUtc: string;
  type: MovementType;
  fromState?: MovementState | string | null;
  toState?: MovementState | string | null;
  detail?: string | null;
  documentNumber?: string | null;
  documentId?: string | null;
  saleId?: string | null;
  contractId?: string | null;
  fromBranchId?: string | null;
  toBranchId?: string | null;
  customerName?: string | null;
  supplierName?: string | null;
  moved: number | null;
  in: number | null;
  out: number | null;
  balance: number;
  userName?: string | null;
}

export interface ItemMovementsBreakdown {
  from: string | null;
  to: string | null;
  opening: number;
  closing: number;
  unrecordedDifference: number | null;
  truncated: boolean;
  rows: ItemMovementRow[];
}

export interface ItemStatusDetail {
  asOfUtc: string;
  scope: ItemStatusScope;
  item: ItemStatusRow;
  locations: ItemLocationRow[];
  rented: ItemRentedRow[];
  leftToIssue: ItemLeftToIssueRow[];
  movements: ItemMovementsBreakdown;
  inTransit: ItemInTransitRow[];
}

/** Pure query string builder that omits empty/null/undefined values and joins productIds. */
export function itemStatusQueryString(
  q: ItemStatusQuery,
  extra?: Record<string, string | number | boolean | null | undefined>,
): string {
  const params = new URLSearchParams();

  if (q.branchId) params.set('branchId', q.branchId);
  if (q.q && q.q.trim()) params.set('q', q.q.trim());
  if (q.productIds) {
    const ids = Array.isArray(q.productIds) ? q.productIds.join(',') : q.productIds;
    if (ids.trim()) params.set('productIds', ids.trim());
  }
  if (q.itemType) params.set('itemType', q.itemType);
  if (q.status) params.set('status', q.status);
  if (q.customerId) params.set('customerId', q.customerId);
  if (q.contractId) params.set('contractId', q.contractId);
  if (q.includeInactive) params.set('includeInactive', 'true');

  if (extra) {
    for (const [key, val] of Object.entries(extra)) {
      if (val !== null && val !== undefined && val !== '') {
        params.set(key, String(val));
      }
    }
  }

  return params.toString();
}

/** Downloads a Blob using an ephemeral object URL and an anchor tag. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

@Injectable({ providedIn: 'root' })
export class ItemStatusApiService {
  private readonly api = inject(ApiService);

  summary(q: ItemStatusQuery): Promise<ItemStatusReport> {
    const qs = itemStatusQueryString(q);
    return this.api.getJson<ItemStatusReport>(`/api/reports/inventory/items${qs ? `?${qs}` : ''}`);
  }

  detail(productId: string, q: ItemStatusQuery, from?: string | null, to?: string | null): Promise<ItemStatusDetail> {
    const qs = itemStatusQueryString(q, { from, to });
    return this.api.getJson<ItemStatusDetail>(
      `/api/reports/inventory/items/${encodeURIComponent(productId)}${qs ? `?${qs}` : ''}`,
    );
  }

  exportSummary(q: ItemStatusQuery, lang: string): Promise<Blob> {
    const qs = itemStatusQueryString(q, { lang });
    return this.api.getBlob(`/api/reports/inventory/items/export${qs ? `?${qs}` : ''}`);
  }

  exportDetail(productId: string, q: ItemStatusQuery, from?: string | null, to?: string | null, lang?: string): Promise<Blob> {
    const qs = itemStatusQueryString(q, { from, to, lang });
    return this.api.getBlob(
      `/api/reports/inventory/items/${encodeURIComponent(productId)}/export${qs ? `?${qs}` : ''}`,
    );
  }
}
