import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../core/api.service';
import type { AdminGiftCard, GiftCardMovement } from '../../core/admin.service';
import { PageResult, pageQueryString } from '../../core/paging';
import type { OpeningPartyCorrection } from './customer-opening-balance.component';
import {
  QuantityStatement,
  QuantityStatementContract,
  QuantityStatementItem,
  QuantityStatementQuery,
  quantityStatementParams,
} from './rentals/rentals-api.service';
import type { SupplierDto, SupplierOpeningCorrection, SupplierStatement, SupplierStatementRow } from './suppliers/suppliers-api.service';

/**
 * Gift cards, suppliers, opening corrections and the customer quantity statement a page at a time
 * (owner, 2026-11-17: "I need to paginate in backend, not frontend"). Each `…/page` route sits
 * beside the list's own route (left as it was for tills, pickers and papers), runs the screen's
 * filters on the server over every row and sends one page, with the list's figures over all its
 * rows in `summary`. A filter at "all", empty or null is not sent (`pageQueryString`).
 */

/** The gift cards list's server filters. */
export interface GiftCardsPageQuery {
  skip: number;
  take: number;
  /** The code, normalised as a till types it. */
  q?: string;
  state?: 'all' | 'valid' | 'expired' | 'inactive';
  balance?: 'all' | 'left' | 'spent';
  customer?: 'all' | 'linked' | 'none';
}

/** The suppliers list's server filters; `id` re-reads one supplier whatever page it is on. */
export interface SuppliersPageQuery {
  skip: number;
  take: number;
  q?: string;
  balance?: 'all' | 'weOwe' | 'owesUs' | 'settled';
  missing?: 'all' | 'phone' | 'fiscalIds';
  id?: string;
}

/** A supplier statement without its rows; `pageOpening` is the running balance before the page's first row. */
export type SupplierStatementHead = Omit<SupplierStatement, 'rows'> & { pageOpening: number };

/** A quantity statement without its two tables, and what their columns depend on. */
export type QuantityStatementHead = Omit<QuantityStatement, 'items' | 'contracts'> & {
  hasTransfers: boolean;
  hasContractTransfers: boolean;
  itemCount: number;
  contractCount: number;
};

@Injectable({ providedIn: 'root' })
export class SalesPagesApi {
  private readonly api = inject(ApiService);

  /** `GET /api/catalog/gift-cards/page`, by code. */
  giftCardsPage(query: GiftCardsPageQuery): Promise<PageResult<AdminGiftCard>> {
    return this.api.getJson<PageResult<AdminGiftCard>>(`/api/catalog/gift-cards/page${pageQueryString({ ...query })}`);
  }

  /** One card's history, newest first; each row carries the balance after it. */
  giftCardMovementsPage(id: string, skip: number, take: number): Promise<PageResult<GiftCardMovement>> {
    return this.api.getJson<PageResult<GiftCardMovement>>(
      `/api/catalog/gift-cards/${id}/movements/page${pageQueryString({ skip, take })}`,
    );
  }

  /** A customer's opening-balance corrections, oldest first. */
  customerOpeningCorrectionsPage(id: string, skip: number, take: number): Promise<PageResult<OpeningPartyCorrection>> {
    return this.api.getJson<PageResult<OpeningPartyCorrection>>(
      `/api/catalog/customers/${id}/opening-corrections/page${pageQueryString({ skip, take })}`,
    );
  }

  /** A supplier's opening-balance corrections, oldest first. */
  supplierOpeningCorrectionsPage(id: string, skip: number, take: number): Promise<PageResult<SupplierOpeningCorrection>> {
    return this.api.getJson<PageResult<SupplierOpeningCorrection>>(
      `/api/suppliers/${id}/opening-corrections/page${pageQueryString({ skip, take })}`,
    );
  }

  /** `GET /api/suppliers/page`, by name. */
  suppliersPage(query: SuppliersPageQuery): Promise<PageResult<SupplierDto>> {
    return this.api.getJson<PageResult<SupplierDto>>(`/api/suppliers/page${pageQueryString({ ...query })}`);
  }

  /**
   * A page of one supplier's statement. `from` and `to` are plain days (`yyyy-MM-dd`), both
   * included, read in the company's zone by the server.
   */
  supplierStatementPage(
    id: string,
    query: { skip: number; take: number; from: string; to: string },
  ): Promise<PageResult<SupplierStatementRow, SupplierStatementHead>> {
    return this.api.getJson<PageResult<SupplierStatementRow, SupplierStatementHead>>(
      `/api/suppliers/${id}/statement/page${pageQueryString({ ...query })}`,
    );
  }

  /** A page of the quantity statement's items; the summary is the statement's head. */
  quantityItemsPage(q: QuantityStatementQuery, skip: number, take: number): Promise<PageResult<QuantityStatementItem, QuantityStatementHead>> {
    return this.api.getJson<PageResult<QuantityStatementItem, QuantityStatementHead>>(
      `/api/rentals/quantity-statement/items/page?${withPage(quantityStatementParams(q), skip, take)}`,
    );
  }

  /** A page of the quantity statement's contracts, each whole with its lines and its total. */
  quantityContractsPage(
    q: QuantityStatementQuery,
    skip: number,
    take: number,
  ): Promise<PageResult<QuantityStatementContract, QuantityStatementHead>> {
    return this.api.getJson<PageResult<QuantityStatementContract, QuantityStatementHead>>(
      `/api/rentals/quantity-statement/contracts/page?${withPage(quantityStatementParams(q), skip, take)}`,
    );
  }
}

function withPage(params: URLSearchParams, skip: number, take: number): URLSearchParams {
  params.set('skip', String(skip));
  params.set('take', String(take));
  return params;
}
