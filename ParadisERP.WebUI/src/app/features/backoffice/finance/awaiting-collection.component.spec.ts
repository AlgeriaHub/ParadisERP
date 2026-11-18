import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminService } from '../../../core/admin.service';
import { BranchScopeService } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ReceivablesApi } from '../receivables/receivables-api.service';
import { PageChrome } from '../shell/page-chrome';
import { ConfirmService } from '../../../ui/confirm.service';
import { AwaitingCollectionComponent } from './awaiting-collection.component';
import { AwaitingRow } from './awaiting';
import { FinancePagesApi } from './finance-pages.api';

/**
 * FR-2B (the owner, 2026-10-03) on the till's cheques and transfers: a bounce asks the bank's
 * reason and an explicit confirmation before anything is sent, the reason going into the note the
 * route always stored; collecting asks a confirmation, as the receipts always did.
 */
describe('awaiting collection — settling a till payment', () => {
  let admin: jasmine.SpyObj<AdminService>;
  let pages: jasmine.SpyObj<FinancePagesApi>;
  const row: AwaitingRow = {
    source: 'sale',
    id: 'pay-1',
    reference: 'C01-000123',
    customerId: null,
    customerName: 'Client',
    methodKey: 'cheque',
    amount: 25000,
    instrumentNumber: '0045123',
    instrumentBank: 'BNA',
    instrumentDueDate: '2026-09-28T00:00:00Z',
    takenAtUtc: '2026-09-20T10:00:00Z',
    branchId: null,
  };

  beforeEach(() => {
    admin = jasmine.createSpyObj<AdminService>('AdminService', ['listBranches', 'listPaymentMethods', 'settlePayment']);
    admin.listBranches.and.resolveTo([]);
    admin.listPaymentMethods.and.resolveTo([]);
    admin.settlePayment.and.resolveTo({});
    // The server's page (owner, 2026-11-17): the till's and the receipts' paper drawn together, figures beside it.
    pages = jasmine.createSpyObj<FinancePagesApi>('FinancePagesApi', ['awaitingPage']);
    pages.awaitingPage.and.callFake(async (q) => ({
      items: [row], total: 1, skip: 0, take: q.take,
      summary: { total: row.amount, count: 1, overdueTotal: row.amount, overdueCount: 1, methodKeys: ['cheque'], all: 1 },
    }));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        PageChrome,
        { provide: AdminService, useValue: admin },
        { provide: FinancePagesApi, useValue: pages },
        { provide: ReceivablesApi, useValue: { settleReceipt: () => Promise.resolve({}) } },
        { provide: BranchScopeService, useValue: { canChoose: () => false, resolve: () => ({ branchId: 'b1', label: 'branch' }), set: () => {} } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  async function render() {
    const fixture = TestBed.createComponent(AwaitingCollectionComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('opens the bounce dialog instead of bouncing, and sends nothing without a reason', async () => {
    const fixture = await render();
    const page = fixture.componentInstance;
    page.bounce(row);
    expect(page.bouncing()).toBe(row);
    expect(admin.settlePayment).not.toHaveBeenCalled();

    page.reason.set('   ');
    await page.confirmBounce();
    expect(admin.settlePayment).not.toHaveBeenCalled();
    expect(page.reasonMissing()).toBeTrue();
    expect(page.bouncing()).toBe(row);
  });

  it('bounces with the bank\'s reason as the note, once confirmed', async () => {
    const fixture = await render();
    const page = fixture.componentInstance;
    page.bounce(row);
    page.reason.set('  Provision insuffisante ');
    const ask = spyOn(TestBed.inject(ConfirmService), 'ask').and.resolveTo(true);
    await page.confirmBounce();
    expect(ask).toHaveBeenCalledTimes(1);
    expect(admin.settlePayment).toHaveBeenCalledOnceWith('pay-1', 'Bounced', 'Provision insuffisante');
    expect(page.bouncing()).toBeNull();
    // Settled, the page on screen is asked again.
    expect(pages.awaitingPage).toHaveBeenCalledTimes(2);
  });

  it('cancelling the dialog bounces nothing', async () => {
    const fixture = await render();
    const page = fixture.componentInstance;
    page.bounce(row);
    page.closeBounce();
    expect(page.bouncing()).toBeNull();
    expect(admin.settlePayment).not.toHaveBeenCalled();
  });

  it('asks the server for the page with the working branch and the filters (owner, 2026-11-17)', async () => {
    const fixture = await render();
    const page = fixture.componentInstance;
    expect(pages.awaitingPage).toHaveBeenCalledWith(
      jasmine.objectContaining({ skip: 0, take: 10, source: 'all', q: '', due: 'all', branchId: 'b1' }),
    );
    expect(page.figures()).toEqual({ total: 25000, count: 1, overdueTotal: 25000, overdueCount: 1 });
    page.setSource('receipt');
    page.dueFilter.set('overdue');
    page.setQuery(' societe ');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    expect(pages.awaitingPage).toHaveBeenCalledWith(
      jasmine.objectContaining({ skip: 0, take: 10, source: 'receipt', q: 'societe', due: 'overdue', branchId: 'b1' }),
    );
  });

  it('collects a till payment only once confirmed, with no note, as before', async () => {
    const fixture = await render();
    const page = fixture.componentInstance;
    // The app's own dialog now, not window.confirm (every confirmation, 2026-10-05).
    const ask = spyOn(TestBed.inject(ConfirmService), 'ask').and.resolveTo(false);
    await page.collect(row);
    expect(admin.settlePayment).not.toHaveBeenCalled();
    expect(ask.calls.mostRecent().args[0].message).toContain('C01-000123');
    ask.and.resolveTo(true);
    await page.collect(row);
    expect(admin.settlePayment).toHaveBeenCalledOnceWith('pay-1', 'Confirmed', null);
  });
});
