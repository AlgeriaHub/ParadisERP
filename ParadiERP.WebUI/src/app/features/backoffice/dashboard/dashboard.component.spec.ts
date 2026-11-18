import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminBranch, AdminService, SalesSummary } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { BranchFace } from '../../../core/branch-face';
import { BranchLock } from '../../../core/branch-lock';
import { BranchScopeService } from '../../../core/branch-scope.service';
import { ConnectivityService } from '../../../core/connectivity.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { SyncService } from '../../../core/sync.service';
import { PaymentVouchersApi } from '../receivables/payment-vouchers-api.service';
import { ReceivablesApi } from '../receivables/receivables-api.service';
import { RentalsApi } from '../rentals/rentals-api.service';
import { PageChrome } from '../shell/page-chrome';
import { DashboardComponent } from './dashboard.component';

/**
 * The dashboard (Appendix A): each block loads on its own, offline the server blocks say so and
 * the till's own block still answers. The Admin's branch filter is the dashboard's own (owner,
 * 2026-10-03): every branch by default in a company of several, kept once chosen, and never
 * written to the working branch that every other page reads.
 */
describe('the dashboard', () => {
  const summary = (branches = 1): SalesSummary =>
    ({
      saleCount: 4,
      refundCount: 1,
      grossTtc: 12000,
      refundTotal: 500,
      branchBreakdown: Array.from({ length: branches }, (_, i) => ({ branchId: 'b' + i, branchName: 'B' + i, saleCount: 2, grossTtc: 6000 })),
    }) as SalesSummary;

  let admin: jasmine.SpyObj<AdminService>;
  let rentals: jasmine.SpyObj<RentalsApi>;
  let receivables: jasmine.SpyObj<ReceivablesApi>;
  let vouchers: jasmine.SpyObj<PaymentVouchersApi>;
  const online = signal(true);
  const locked = signal(false);
  const workingSet = jasmine.createSpy('working branch set');
  const branch = (id: string, nameFr: string) => ({ id, nameFr, nameAr: nameFr, nameEn: nameFr, isActive: true }) as AdminBranch;
  const TWO = [branch('b-alger', 'Alger'), branch('b-oran', 'Oran')];
  const clearStorage = () => {
    localStorage.removeItem('pos.dashboardBranch.u-admin');
    localStorage.removeItem('pos.workingBranch.u-admin');
  };

  beforeEach(() => {
    online.set(true);
    locked.set(false);
    workingSet.calls.reset();
    clearStorage();
    admin = jasmine.createSpyObj<AdminService>('AdminService', [
      'getSalesSummary', 'listBranches', 'getLowStock', 'listPendingPayments', 'listCashBoxes',
    ]);
    admin.getSalesSummary.and.resolveTo(summary());
    admin.listBranches.and.resolveTo(TWO);
    admin.getLowStock.and.resolveTo([]);
    admin.listPendingPayments.and.resolveTo([]);
    admin.listCashBoxes.and.resolveTo([]);
    rentals = jasmine.createSpyObj<RentalsApi>('RentalsApi', ['listContracts']);
    rentals.listContracts.and.resolveTo([]);
    receivables = jasmine.createSpyObj<ReceivablesApi>('ReceivablesApi', ['ageing']);
    receivables.ageing.and.resolveTo({ total: 900, current: 900, days1To30: 0, days31To60: 0, days61To90: 0, over90: 0 } as never);
    vouchers = jasmine.createSpyObj<PaymentVouchersApi>('PaymentVouchersApi', ['list']);
    vouchers.list.and.resolveTo([]);

    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        PageChrome,
        { provide: AdminService, useValue: admin },
        { provide: RentalsApi, useValue: rentals },
        { provide: ReceivablesApi, useValue: receivables },
        { provide: PaymentVouchersApi, useValue: vouchers },
        { provide: ConnectivityService, useValue: { online } },
        { provide: AuthService, useValue: { currentUser: signal({ id: 'u-admin' }) } },
        { provide: BranchScopeService, useValue: { set: workingSet } },
        {
          provide: BranchLock,
          useValue: { locked, lockedBranchId: () => (locked() ? 'b-oran' : null), ownBranchId: () => (locked() ? 'b-oran' : 'b-alger') },
        },
        { provide: BranchFace, useValue: { name: signal('Oran') } },
        {
          provide: SyncService,
          useValue: { stats: signal({ pending: 3, rejected: 1, synced: 0, products: 0, customers: 0, cursor: 0, lastSyncAtUtc: null, lastError: null }), refreshStats: () => Promise.resolve() },
        },
      ],
    });
  });

  afterEach(clearStorage);

  async function render() {
    // The shell brings the back-office words in the app; here the test does.
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    const fixture = TestBed.createComponent(DashboardComponent);
    // The Admin's first look waits for the branch list, then every block asks: two turns to settle.
    for (let turn = 0; turn < 2; turn++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
    return fixture;
  }

  it('keeps the other blocks when one endpoint fails', async () => {
    receivables.ageing.and.rejectWith(new Error('boom'));
    const fixture = await render();
    const dash = fixture.componentInstance;
    expect(dash.owe.state().status).toBe('error');
    expect(dash.sales.state().status).toBe('ready');
    expect(dash.rentals.state().status).toBe('ready');
    expect(dash.cash.state().status).toBe('ready');
    expect(fixture.nativeElement.textContent).toContain('boom');
  });

  it('offline, asks the server for nothing, says so on each server block, and still counts the till\'s outbox', async () => {
    online.set(false);
    const fixture = await render();
    const dash = fixture.componentInstance;
    for (const block of [dash.sales, dash.rentals, dash.owe, dash.vouchers, dash.awaiting, dash.cash, dash.low]) {
      expect(block.state().status).toBe('offline');
    }
    expect(admin.getSalesSummary).not.toHaveBeenCalled();
    const till = (fixture.nativeElement as HTMLElement).querySelector('[aria-labelledby="db-till"]')!;
    expect(till.textContent).toContain('3');
    expect(till.textContent).toContain('1');
  });

  // PinSay #300: each card on its state's own tint, beside the words that already say the state.
  it('colours each card by its state', async () => {
    receivables.ageing.and.rejectWith(new Error('boom'));
    const fixture = await render();
    const tone = (id: string) =>
      (fixture.nativeElement as HTMLElement).querySelector(`[aria-labelledby="${id}"]`)!.getAttribute('data-tone');
    expect(tone('db-sales')).toBe('money');
    expect(tone('db-vouchers')).toBe('success');
    expect(tone('db-awaiting')).toBe('success');
    expect(tone('db-low')).toBe('success');
    expect(tone('db-rentals')).toBe('neutral');
    expect(tone('db-cash')).toBe('neutral');
    expect(tone('db-owe')).toBe('critical');
    // 3 pending and 1 rejected in the outbox: the rejected sale is the one that matters.
    expect(tone('db-till')).toBe('critical');
  });

  it('opens the Admin of a company with several branches on every branch, asked once, and says so', async () => {
    const fixture = await render();
    expect(admin.getSalesSummary).toHaveBeenCalledTimes(1);
    expect(admin.getSalesSummary.calls.mostRecent().args[2]).toBeNull();
    expect(fixture.componentInstance.showPicker()).toBeTrue();
    expect(fixture.componentInstance.scopeWord(fixture.componentInstance.branchScope())).toBe(TestBed.inject(I18nService).t('scope.all'));
  });

  it('opens the Admin of a one-branch company on that branch, with nothing to choose', async () => {
    admin.listBranches.and.resolveTo([branch('b-alger', 'Alger')]);
    const fixture = await render();
    expect(admin.getSalesSummary).toHaveBeenCalledTimes(1);
    expect(admin.getSalesSummary.calls.mostRecent().args[2]).toBe('b-alger');
    expect(fixture.componentInstance.showPicker()).toBeFalse();
    expect(fixture.componentInstance.scopeWord('branch')).toContain('Alger');
  });

  it('without the branch list (offline at first), shows every branch and says so', async () => {
    admin.listBranches.and.rejectWith(new Error('offline'));
    const fixture = await render();
    expect(fixture.componentInstance.resolved()).toEqual({ branchId: null, label: 'all' });
  });

  it('asks every branch-aware block for the chosen branch, remembers it for the dashboard only', async () => {
    const fixture = await render();
    fixture.componentInstance.chooseBranch('b-oran');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(localStorage.getItem('pos.dashboardBranch.u-admin')).toBe('{"kind":"branch","id":"b-oran"}');
    expect(workingSet).not.toHaveBeenCalled();
    expect(localStorage.getItem('pos.workingBranch.u-admin')).toBeNull();
    expect(admin.getSalesSummary.calls.mostRecent().args[2]).toBe('b-oran');
    expect(rentals.listContracts.calls.mostRecent().args[2]).toBe('b-oran');
    expect(admin.listCashBoxes.calls.mostRecent().args[0]).toBe('b-oran');
    expect(admin.listPendingPayments.calls.mostRecent().args[1]).toBe('b-oran');
    expect(admin.getLowStock.calls.mostRecent().args[0]).toBe('b-oran');
    expect(vouchers.list.calls.mostRecent().args[0]?.branchId).toBe('b-oran');
    expect(fixture.componentInstance.scopeWord('branch')).toContain('Oran');

    // A reload opens on the same choice.
    fixture.destroy();
    admin.getSalesSummary.calls.reset();
    const again = await render();
    expect(again.componentInstance.branchChoice()).toBe('b-oran');
    expect(admin.getSalesSummary.calls.mostRecent().args[2]).toBe('b-oran');

    again.componentInstance.chooseBranch(again.componentInstance.ALL);
    expect(localStorage.getItem('pos.dashboardBranch.u-admin')).toBe('{"kind":"all"}');
    expect(workingSet).not.toHaveBeenCalled();
  });

  it('shows the sales-by-branch table only on every branch, with more than one branch selling', async () => {
    admin.getSalesSummary.and.resolveTo(summary(2));
    const fixture = await render();
    expect(fixture.componentInstance.showByBranch()).toBeTrue();
    fixture.componentInstance.chooseBranch('b-oran');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.showByBranch()).toBeFalse();
  });

  it('gives a locked Manager their own branch, no branch filter, and no sales-by-branch table', async () => {
    locked.set(true);
    admin.getSalesSummary.and.resolveTo(summary(2));
    const fixture = await render();
    expect(admin.getSalesSummary.calls.mostRecent().args[2]).toBe('b-oran');
    expect(admin.listBranches).not.toHaveBeenCalled();
    expect(fixture.componentInstance.showPicker()).toBeFalse();
    expect(fixture.componentInstance.showByBranch()).toBeFalse();
    expect(fixture.componentInstance.scopeWord('branch')).toContain('Oran');
    expect(fixture.componentInstance.scopeWord('company')).not.toContain('Oran');
  });
});
