import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { PageResult, pageQueryString } from '../../../core/paging';
import { RentalTotals } from './rental-totals';
import { ContractState } from './rental-view';
import { ProductOverdueMultiplier, RentalContractRow } from './rentals-api.service';

/**
 * The contract list's server filters (owner, 2026-11-17: "paginate in backend, not frontend"). A filter
 * at "all", empty or null is not sent. `status` and `customerId` are the list route's own; `branch` is the
 * Admin's narrowing to one branch (a branch user's branch is the server's); `from` and `to` are signing
 * days, `yyyy-MM-dd`, read on the company's calendar.
 */
export interface RentalContractsPageQuery {
  skip: number;
  take: number;
  status?: string;
  customerId?: string | null;
  q?: string;
  stage?: string;
  late?: string;
  branch?: string;
  from?: string;
  to?: string;
}

/**
 * The list's figures over the contracts of the state chip and the customer, before the screen's own
 * filters, as the screen summed them: the tiles (`rentalTotals`), the progress words those rows carry,
 * whether the deposit column shows, and how many there are.
 */
export interface RentalContractsPageSummary {
  totals: RentalTotals;
  stages: ContractState[];
  depositVisible: boolean;
  count: number;
}

/** The per-item figures list's server filters; "all" is not sent. */
export interface RentalItemFiguresPageQuery {
  skip: number;
  take: number;
  q?: string;
  price?: 'all' | 'set' | 'unset';
  damage?: 'all' | 'set' | 'unset';
}

/** The rentals lists a page at a time; `RentalsApi` keeps the whole lists for the dashboard and the pickers. */
@Injectable({ providedIn: 'root' })
export class RentalPagesApi {
  private readonly api = inject(ApiService);

  contractsPage(query: RentalContractsPageQuery): Promise<PageResult<RentalContractRow, RentalContractsPageSummary>> {
    return this.api.getJson<PageResult<RentalContractRow, RentalContractsPageSummary>>(
      `/api/rentals/contracts/page${pageQueryString({ ...query })}`,
    );
  }

  itemFiguresPage(query: RentalItemFiguresPageQuery): Promise<PageResult<ProductOverdueMultiplier>> {
    return this.api.getJson<PageResult<ProductOverdueMultiplier>>(`/api/rentals/settings/products/page${pageQueryString({ ...query })}`);
  }
}
