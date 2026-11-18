import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { PageResult } from '../../../core/paging';
import {
  ItemMovementRow,
  ItemRentedRow,
  ItemStatusDetail,
  ItemStatusQuery,
  ItemStatusRow,
  ItemStatusScope,
  ItemStatusTotals,
  itemStatusQueryString,
} from './item-status-api.service';

/**
 * The report's figures over every row (owner, 2026-11-17: "paginate in backend, not frontend"): the
 * totals row, the scope, when it was read, and every row's product — so "select all" still selects
 * the whole report, not the page on screen.
 */
export interface ItemStatusPageSummary {
  asOfUtc: string;
  scope: ItemStatusScope;
  totals: ItemStatusTotals;
  productIds: string[];
}

/** A branch goods went out on rent from. */
export interface ItemIssueBranch {
  id: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
}

/** An article's rentals out: how many before the state and branch filters, and the branches present. */
export interface ItemRentedPageSummary {
  count: number;
  issueBranches: ItemIssueBranch[];
}

/**
 * An article's movements over the period. `pageOpening` is the balance before the page's first row
 * (the period's opening on page one); `closing` is the period's.
 */
export interface ItemMovementsPageSummary {
  from: string | null;
  to: string | null;
  opening: number;
  closing: number;
  unrecordedDifference: number | null;
  truncated: boolean;
  pageOpening: number;
}

/** What the rented list is narrowed to; "all" is not sent. */
export interface ItemRentedFilters {
  state: 'all' | 'overdue' | 'onTime';
  issueBranchId: string;
}

/**
 * The item status report's server-paged reads, beside `ItemStatusApiService` (whose whole report and
 * breakdown the exports and the papers still use).
 */
@Injectable({ providedIn: 'root' })
export class ItemStatusPagesApi {
  private readonly api = inject(ApiService);

  /** One page of the report's rows, with the totals over every row. */
  page(q: ItemStatusQuery, skip: number, take: number): Promise<PageResult<ItemStatusRow, ItemStatusPageSummary>> {
    const qs = itemStatusQueryString(q, { skip, take });
    return this.api.getJson<PageResult<ItemStatusRow, ItemStatusPageSummary>>(`/api/reports/inventory/items/page?${qs}`);
  }

  /** One article's breakdown without its rentals out and its movements, which page below. */
  overview(productId: string, q: ItemStatusQuery, from?: string | null, to?: string | null): Promise<ItemStatusDetail> {
    const qs = itemStatusQueryString(q, { from, to });
    return this.api.getJson<ItemStatusDetail>(`/api/reports/inventory/items/${encodeURIComponent(productId)}/overview${qs ? `?${qs}` : ''}`);
  }

  rented(
    productId: string,
    q: ItemStatusQuery,
    filters: ItemRentedFilters,
    skip: number,
    take: number,
  ): Promise<PageResult<ItemRentedRow, ItemRentedPageSummary>> {
    const qs = itemStatusQueryString(q, {
      state: filters.state === 'all' ? null : filters.state,
      issueBranchId: filters.issueBranchId === 'all' ? null : filters.issueBranchId,
      skip,
      take,
    });
    return this.api.getJson<PageResult<ItemRentedRow, ItemRentedPageSummary>>(`/api/reports/inventory/items/${encodeURIComponent(productId)}/rented/page?${qs}`);
  }

  movements(
    productId: string,
    q: ItemStatusQuery,
    from: string | null | undefined,
    to: string | null | undefined,
    skip: number,
    take: number,
  ): Promise<PageResult<ItemMovementRow, ItemMovementsPageSummary>> {
    const qs = itemStatusQueryString(q, { from, to, skip, take });
    return this.api.getJson<PageResult<ItemMovementRow, ItemMovementsPageSummary>>(`/api/reports/inventory/items/${encodeURIComponent(productId)}/movements/page?${qs}`);
  }
}
