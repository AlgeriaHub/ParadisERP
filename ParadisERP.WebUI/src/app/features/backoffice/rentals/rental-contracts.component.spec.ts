import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { BranchLock } from '../../../core/branch-lock';
import { PageResult } from '../../../core/paging';
import { RentalContractsComponent } from './rental-contracts.component';
import { RentalContractsPageQuery, RentalContractsPageSummary, RentalPagesApi } from './rental-pages.api';
import { RentalContractRow } from './rentals-api.service';

/**
 * The contracts list a page at a time from the server (owner, 2026-11-17: "paginate in backend, not
 * frontend"): every filter is sent, and the tiles are the server's sums, never the page's.
 */
describe('RentalContractsComponent', () => {
  const row: RentalContractRow = {
    id: 'k1', number: 'LOC-000001', customerId: 'c1', customerName: 'Entreprise Benali', branchId: 'b1',
    status: 'Open', isLocked: true, lockedAtUtc: '2026-10-01T09:00:00Z', signedAtUtc: '2026-10-01T09:00:00Z',
    closedAtUtc: null, issuedQty: 10, settledQty: 0, stillOutQty: 10, chargeTotal: 0, overdueTotal: 0,
  };

  const summary: RentalContractsPageSummary = {
    totals: { stillOut: 42, charged: 15000, overdue: 1200, drafts: 3, openWithGoods: 7 },
    stages: ['draft', 'out'],
    depositVisible: false,
    count: 25,
  };

  function make(locked: boolean) {
    const pages = jasmine.createSpyObj<RentalPagesApi>('RentalPagesApi', ['contractsPage']);
    pages.contractsPage.and.callFake(
      async (q: RentalContractsPageQuery): Promise<PageResult<RentalContractRow, RentalContractsPageSummary>> => ({
        items: [row], total: 25, skip: q.skip, take: q.take, summary,
      }),
    );
    TestBed.configureTestingModule({
      providers: [
        { provide: RentalPagesApi, useValue: pages },
        { provide: AuthService, useValue: { currentUser: signal({ role: 'Admin', permissions: [] }) } },
        { provide: BranchLock, useValue: { locked: signal(locked), lockedBranchId: signal(locked ? 'b1' : null) } },
      ],
    });
    const list = TestBed.runInInjectionContext(() => new RentalContractsComponent());
    return { list, pages };
  }

  it('asks for the open contracts, ten first, and reads the tiles from the server', async () => {
    const { list, pages } = make(false);
    await list.reload();
    expect(pages.contractsPage).toHaveBeenCalledWith(
      jasmine.objectContaining({ skip: 0, take: 10, status: 'Open', q: '', stage: 'all', late: 'all', branch: 'all' }),
    );
    expect(list.totals()).toEqual(summary.totals);
    expect(list.stagesShown()).toEqual(['draft', 'out']);
    expect(list.pager.total()).toBe(25);
  });

  it('sends the search, the further filters and the state chip to the server', async () => {
    const { list, pages } = make(false);
    list.setFilter('all');
    list.setQuery(' benali ');
    list.stageFilter.set('partly');
    list.lateFilter.set('yes');
    list.branchFilter.set('b2');
    list.signedFrom.set('2026-10-01');
    list.signedTo.set('2026-10-31');
    await list.reload();
    expect(pages.contractsPage).toHaveBeenCalledWith({
      skip: 0, take: 10, status: undefined, customerId: null, q: 'benali', stage: 'partly', late: 'yes', branch: 'b2',
      from: '2026-10-01', to: '2026-10-31',
    });
    // The chosen word stays offered even when the contracts no longer carry it.
    expect(list.stagesShown()).toEqual(['draft', 'out', 'partly']);
  });

  it('never sends a branch filter for a branch-locked user (the server reads their own branch)', async () => {
    const { list, pages } = make(true);
    list.branchFilter.set('b2');
    await list.reload();
    expect(pages.contractsPage).toHaveBeenCalledWith(jasmine.objectContaining({ branch: 'all' }));
  });

  it('clears the search and the further filters, keeping the state chip and the customer', () => {
    const { list } = make(false);
    list.setFilter('closed');
    list.pickCustomer('c1');
    list.setQuery('x');
    list.lateFilter.set('no');
    expect(list.filtered()).toBeTrue();
    list.clearFilters();
    expect(list.filtered()).toBeFalse();
    expect(list.filter()).toBe('closed');
    expect(list.customerId()).toBe('c1');
  });
});
