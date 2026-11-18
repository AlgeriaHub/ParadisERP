import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { AdminBranch, AdminService } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { BranchLock } from '../../../core/branch-lock';
import { CatalogStore } from '../../../core/catalog-store';
import { ConfirmService } from '../../../ui/confirm.service';
import { PrintService } from '../../print/print.service';
import { BackofficeStockTransfersComponent } from './stock-transfers.component';
import { StockTransfer, StockTransfersApi } from './stock-transfers-api.service';
import { StockTransferPagesApi } from './stock-transfer-pages.api';

/** What the transfers page offers to whom (owner, 2026-10-12): the server decides, the page only hides what it would refuse. */
describe('BackofficeStockTransfersComponent', () => {
  const SOURCE = 'b-source';
  const DEST = 'b-dest';

  function transfer(status: StockTransfer['status'], inTransit = 4): StockTransfer {
    return {
      id: 't1', number: 'TRF-000001', status, fromBranchId: SOURCE, fromBranchName: 'Alger', toBranchId: DEST,
      toBranchName: 'Oran', note: null, dispatchedAtUtc: '2026-10-12T10:00:00Z', dispatchedByName: 'Admin',
      closedAtUtc: null, qtyDispatched: 4, inTransit, events: [], qtyShortPending: 0, canDecideShortfall: false,
      lines: [
        { id: 'l1', productId: 'p1', sku: 'A', nameAr: 'أ', nameFr: 'A', nameEn: 'A', qtyDispatched: 3, qtyReceived: 0, qtyLost: 0, qtyReturned: 0, inTransit: 3, unitCost: 0,
          qtyShortPending: 0, qtyShortReported: 0, qtyShortConfirmed: 0, qtyShortRejected: 0 },
        { id: 'l2', productId: 'p2', sku: 'B', nameAr: 'ب', nameFr: 'B', nameEn: 'B', qtyDispatched: 1, qtyReceived: 0, qtyLost: 0, qtyReturned: 0, inTransit: 1, unitCost: 0,
          qtyShortPending: 0, qtyShortReported: 0, qtyShortConfirmed: 0, qtyShortRejected: 0 },
      ],
    };
  }

  /**
   * Two of A arrived and the third was reported short (owner, 2026-10-13); B is still on the road.
   * `canDecide` is the server's answer for the signed-in user.
   */
  function shortfall(canDecide: boolean): StockTransfer {
    const t = transfer('PartlyReceived', 2);
    return {
      ...t, qtyShortPending: 1, canDecideShortfall: canDecide,
      lines: [{ ...t.lines[0], qtyReceived: 2, inTransit: 1, qtyShortPending: 1, qtyShortReported: 1 }, t.lines[1]],
      events: [{ id: 'e1', kind: 'ShortfallReported', atUtc: '2026-10-13T09:00:00Z', userName: 'Oran', reason: 'carton missing' }],
    };
  }

  function make(lockedAt: string | null, capabilities: string[] = []) {
    const api = jasmine.createSpyObj<StockTransfersApi>('StockTransfersApi', [
      'list', 'get', 'dispatch', 'receive', 'declareLost', 'cancel', 'confirmShortfall', 'rejectShortfall',
    ]);
    // The list is asked a page at a time from the server (owner, 2026-11-17).
    const pages = jasmine.createSpyObj<StockTransferPagesApi>('StockTransferPagesApi', ['page']);
    pages.page.and.callFake(async (q) => ({ items: [], total: 0, skip: 0, take: q.take }));
    api.receive.and.callFake(async () => transfer('PartlyReceived'));
    api.confirmShortfall.and.callFake(async () => transfer('Received', 0));
    api.rejectShortfall.and.callFake(async () => transfer('PartlyReceived', 2));
    const confirm = jasmine.createSpyObj<ConfirmService>('ConfirmService', ['ask']);
    confirm.ask.and.resolveTo(true);
    TestBed.configureTestingModule({
      providers: [
        { provide: StockTransfersApi, useValue: api },
        { provide: StockTransferPagesApi, useValue: pages },
        { provide: AdminService, useValue: { listBranches: () => Promise.resolve([]) } },
        { provide: CatalogStore, useValue: { ensure: () => Promise.resolve([]) } },
        { provide: AuthService, useValue: { currentUser: signal({ capabilities }) } },
        { provide: BranchLock, useValue: { locked: signal(lockedAt !== null), lockedBranchId: signal(lockedAt), ownBranchName: () => '' } },
        { provide: ConfirmService, useValue: confirm },
        { provide: PrintService, useValue: { open: jasmine.createSpy('open') } },
      ],
    });
    const page = TestBed.runInInjectionContext(() => new BackofficeStockTransfersComponent());
    return { page, api, pages, confirm };
  }

  it('names each end in the reader language from its id, and keeps the server name for an unknown branch', () => {
    const { page } = make(null);
    page.branches.set([{ id: SOURCE, nameAr: 'المستودع المركزي', nameFr: 'Entrepôt central', nameEn: 'Central warehouse' } as AdminBranch]);
    page.i18n.lang.set('ar');
    expect(page.branchName(SOURCE, 'Entrepôt central')).toBe('المستودع المركزي');
    page.i18n.lang.set('fr');
    expect(page.branchName(SOURCE, 'Entrepôt central')).toBe('Entrepôt central');
    expect(page.branchName(DEST, 'Oran')).toBe('Oran');
  });

  it('offers the destination receive but not cancel', () => {
    const { page } = make(DEST);
    expect(page.canReceive(transfer('Dispatched'))).toBeTrue();
    expect(page.canCancel(transfer('Dispatched'))).toBeFalse();
  });

  it('offers the source cancel while dispatched, never receive, and no cancel once partly received', () => {
    const { page } = make(SOURCE);
    expect(page.canReceive(transfer('Dispatched'))).toBeFalse();
    expect(page.canCancel(transfer('Dispatched'))).toBeTrue();
    expect(page.canCancel(transfer('PartlyReceived'))).toBeFalse();
  });

  it('offers declare lost only with the write-off capability, at the source', () => {
    expect(make(SOURCE).page.canDeclareLost(transfer('PartlyReceived'))).toBeFalse();
    TestBed.resetTestingModule();
    expect(make(SOURCE, ['inventory.transfer.write_off']).page.canDeclareLost(transfer('PartlyReceived'))).toBeTrue();
    TestBed.resetTestingModule();
    expect(make(DEST, ['inventory.transfer.write_off']).page.canDeclareLost(transfer('PartlyReceived'))).toBeFalse();
  });

  it('offers nothing on a closed transfer, and everything open to the unlocked admin', () => {
    const { page } = make(null, ['inventory.transfer.write_off']);
    expect(page.canReceive(transfer('Received', 0))).toBeFalse();
    expect(page.canCancel(transfer('Cancelled', 0))).toBeFalse();
    expect(page.canReceive(transfer('Dispatched'))).toBeTrue();
    expect(page.canCancel(transfer('Dispatched'))).toBeTrue();
    expect(page.canDeclareLost(transfer('Dispatched'))).toBeTrue();
  });

  it('receives only the lines given a quantity', async () => {
    const { page, api } = make(DEST);
    const t = transfer('Dispatched');
    page.receiveQtys.set({ l1: 2, l2: 0 });
    await page.submitReceive(t);
    expect(api.receive).toHaveBeenCalledOnceWith('t1', jasmine.objectContaining({ lines: [{ lineId: 'l1', qty: 2 }] }));
  });

  describe('a shortfall reported at receipt (owner, 2026-10-13)', () => {
    it('reports what is short with the receipt, and asks for the reason first', async () => {
      const { page, api } = make(DEST);
      const t = transfer('Dispatched');
      page.receiveQtys.set({ l1: 2, l2: 1 });
      page.shortQtys.set({ l1: 1, l2: 0 });

      await page.submitReceive(t);
      expect(api.receive).not.toHaveBeenCalled();
      expect(page.actionError()).toBe(page.i18n.t('transfer.shortReasonRequired'));

      page.shortReason.set('  carton missing ');
      await page.submitReceive(t);
      expect(api.receive).toHaveBeenCalledOnceWith('t1', jasmine.objectContaining({
        lines: [{ lineId: 'l1', qty: 2, short: 1 }, { lineId: 'l2', qty: 1 }],
        shortReason: 'carton missing',
      }));
    });

    it('may report a line short with nothing received on it', async () => {
      const { page, api } = make(DEST);
      page.receiveQtys.set({ l1: 0, l2: 0 });
      page.shortQtys.set({ l1: 3 });
      page.shortReason.set('the truck never came');
      expect(page.hasValidReceiveQtys()).toBeTrue();
      await page.submitReceive(transfer('Dispatched'));
      expect(api.receive).toHaveBeenCalledOnceWith('t1', jasmine.objectContaining({ lines: [{ lineId: 'l1', qty: 0, short: 3 }] }));
    });

    it('starts the receipt on what is not reported short, and caps what can be reported', async () => {
      const { page, api } = make(DEST);
      const t = shortfall(false);
      api.get.and.resolveTo(t);
      await page.selectTransfer(t);
      expect(page.receiveQtys()).toEqual({ l1: 0, l2: 1 });
      expect(page.reportableShort(t.lines[0])).toBe(0); // its one unit in transit is already reported
      page.setReceiveQty('l2', 0);
      expect(page.reportableShort(t.lines[1])).toBe(1);
    });

    it('tags the transfer and offers the answer only where the server allows it', () => {
      const { page } = make(DEST);
      expect(page.hasPendingShortfall(shortfall(false))).toBeTrue();
      expect(page.hasPendingShortfall(transfer('PartlyReceived'))).toBeFalse();
      // The page reads the server's per-transfer answer, not the branch lock.
      expect(page.canDecideShortfall(shortfall(false))).toBeFalse();
      expect(page.canDecideShortfall(shortfall(true))).toBeTrue();
      expect(page.canDecideShortfall({ ...transfer('PartlyReceived'), canDecideShortfall: true })).toBeFalse();
      expect(page.lastReport(shortfall(true))?.reason).toBe('carton missing');
    });

    it('confirms the shortfall shown, through the confirm dialog', async () => {
      const { page, api, confirm } = make(SOURCE);
      confirm.ask.and.resolveTo(false);
      await page.confirmShortfall(shortfall(true));
      expect(confirm.ask).toHaveBeenCalledTimes(1);
      expect(api.confirmShortfall).not.toHaveBeenCalled();

      confirm.ask.and.resolveTo(true);
      await page.confirmShortfall(shortfall(true));
      expect(confirm.ask.calls.mostRecent().args[0].danger).toBeTrue();
      expect(api.confirmShortfall).toHaveBeenCalledOnceWith('t1', { lines: [{ lineId: 'l1', qty: 1 }], reason: null });
      expect(page.done()).toBe(page.i18n.t('transfer.shortfall.confirmed'));
      expect(page.selectedTransfer()?.status).toBe('Received');
    });

    it('rejects only with a reason, through the confirm dialog', async () => {
      const { page, api, confirm } = make(SOURCE);
      await page.rejectShortfall(shortfall(true));
      expect(confirm.ask).not.toHaveBeenCalled();
      expect(api.rejectShortfall).not.toHaveBeenCalled();
      expect(page.actionError()).toBe(page.i18n.t('transfer.reasonRequired'));

      page.shortfallAnswer.set(' four were loaded ');
      await page.rejectShortfall(shortfall(true));
      expect(confirm.ask).toHaveBeenCalledTimes(1);
      expect(api.rejectShortfall).toHaveBeenCalledOnceWith('t1', { lines: [{ lineId: 'l1', qty: 1 }], reason: 'four were loaded' });
      expect(page.done()).toBe(page.i18n.t('transfer.shortfall.rejected'));
    });

    it('asks the server for the transfers with a shortfall waiting for an answer', async () => {
      const { page, pages } = make(SOURCE);
      page.filterStatus.set('shortfall');
      await page.loadTransfers();
      expect(pages.page).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, status: 'shortfall' }));
    });
  });

  describe('the list a page at a time from the server (owner, 2026-11-17)', () => {
    it('sends every filter, and the direction only once a branch is in view', async () => {
      const { page, pages } = make(null);
      page.filterFrom.set('2026-10-01');
      page.directionFilter.set('out');
      await page.loadTransfers();
      // No branch chosen by the admin: no direction to read, so none is sent.
      expect(pages.page).toHaveBeenCalledWith(jasmine.objectContaining({ from: '2026-10-01', direction: 'all', branchId: null }));

      page.setFilterBranch(SOURCE);
      await page.loadTransfers();
      expect(pages.page).toHaveBeenCalledWith(jasmine.objectContaining({ branchId: SOURCE, direction: 'out' }));
    });

    it('reads the direction from a locked user\'s own branch, and clearing keeps that branch', async () => {
      const { page, pages } = make(DEST);
      page.directionFilter.set('in');
      page.setFilterStatus('open');
      await page.loadTransfers();
      expect(pages.page).toHaveBeenCalledWith(jasmine.objectContaining({ direction: 'in', status: 'open' }));
      expect(page.listFiltered()).toBeTrue();

      page.clearFilters();
      expect(page.listFiltered()).toBeFalse();
      await page.loadTransfers();
      expect(pages.page).toHaveBeenCalledWith(jasmine.objectContaining({ direction: 'all', status: 'all' }));
    });
  });
});
