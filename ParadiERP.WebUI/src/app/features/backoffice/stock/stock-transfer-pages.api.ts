import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { PageResult, pageQueryString } from '../../../core/paging';
import { StockTransfer } from './stock-transfers-api.service';

/**
 * The transfers list's server filters (owner, 2026-11-17: "paginate in backend, not frontend"). A
 * filter at "all", empty or null is not sent. `direction` is read from `branchId` — the branch in view,
 * which the server sets to a branch user's own.
 */
export interface StockTransfersPageQuery {
  skip: number;
  take: number;
  branchId?: string | null;
  status?: string;
  from?: string;
  to?: string;
  direction?: 'all' | 'out' | 'in';
}

/** One page of `GET /api/catalog/stock/transfers/page`; the whole list stays on `StockTransfersApi.list`. */
@Injectable({ providedIn: 'root' })
export class StockTransferPagesApi {
  private readonly api = inject(ApiService);

  page(query: StockTransfersPageQuery): Promise<PageResult<StockTransfer>> {
    return this.api.getJson<PageResult<StockTransfer>>(`/api/catalog/stock/transfers/page${pageQueryString({ ...query })}`);
  }
}
