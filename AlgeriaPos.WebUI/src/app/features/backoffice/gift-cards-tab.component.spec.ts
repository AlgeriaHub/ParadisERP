import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminBranch, AdminGiftCard, AdminService } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../ui/confirm.service';
import { BackofficeGiftCardsComponent } from './gift-cards-tab.component';
import { SalesPagesApi } from './sales-pages.api';

describe('BackofficeGiftCardsComponent (SAL-17)', () => {
  let fixture: ComponentFixture<BackofficeGiftCardsComponent>;
  let admin: jasmine.SpyObj<AdminService>;
  let pages: jasmine.SpyObj<SalesPagesApi>;

  const card: AdminGiftCard = {
    id: 'g1', code: 'CARD-0001', initialAmount: 2000, balance: 2000, isActive: true, expiresAtUtc: null, customerId: null,
  } as AdminGiftCard;
  const branches = [
    { id: 'alg', nameFr: 'Alger', nameAr: 'الجزائر', nameEn: 'Algiers', isMain: true },
    { id: 'orn', nameFr: 'Oran', nameAr: 'وهران', nameEn: 'Oran' },
    { id: 'wh', nameFr: 'Dépôt', nameAr: 'المستودع', nameEn: 'Warehouse', isWarehouse: true },
  ] as AdminBranch[];

  async function render(): Promise<BackofficeGiftCardsComponent> {
    admin = jasmine.createSpyObj<AdminService>('AdminService',
      ['listCustomers', 'listBranches', 'issueGiftCard', 'adjustGiftCard', 'updateGiftCard']);
    admin.listCustomers.and.resolveTo([]);
    admin.listBranches.and.resolveTo(branches);
    admin.issueGiftCard.and.resolveTo(card);
    admin.adjustGiftCard.and.resolveTo(card);
    // The list and a card's history come a page at a time from the server (owner, 2026-11-17).
    pages = jasmine.createSpyObj<SalesPagesApi>('SalesPagesApi', ['giftCardsPage', 'giftCardMovementsPage']);
    pages.giftCardsPage.and.resolveTo({ items: [card], total: 1, skip: 0, take: 10 });
    pages.giftCardMovementsPage.and.resolveTo({ items: [], total: 0, skip: 0, take: 10 });
    TestBed.configureTestingModule({
      imports: [BackofficeGiftCardsComponent],
      providers: [
        { provide: AdminService, useValue: admin },
        { provide: SalesPagesApi, useValue: pages },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(BackofficeGiftCardsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  it('sells a card at a branch, never a warehouse, starting at the main branch, and says the cash goes into its box', async () => {
    const page = await render();
    expect(page.branches().map((b) => b.id)).toEqual(['alg', 'orn']);
    expect(page.branchId).toBe('alg');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(TestBed.inject(I18nService).t('giftCards.cash.issueNote'));

    page.amount = 5000;
    page.branchId = 'orn';
    await page.issue();
    expect(admin.issueGiftCard).toHaveBeenCalledWith(jasmine.objectContaining({ amount: 5000, branchId: 'orn' }));
  });

  it('asks the server for ten cards to begin with, the filters with them, and shows the page it answers', async () => {
    const page = await render();
    expect(pages.giftCardsPage).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, state: 'all' }));
    expect(page.pager.rows()).toEqual([card]);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('CARD-0001');

    page.stateFilter.set('expired');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    expect(pages.giftCardsPage).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, state: 'expired' }));
  });

  it('turns each kind of change into its sign, its server kind and, for cash only, its branch', async () => {
    const page = await render();
    const run = async (kind: 'topUp' | 'cashOut' | 'correctionAdd' | 'correctionRemove') => {
      page.adjustKind = kind;
      page.delta = 300;
      page.reason = 'Test';
      await page.adjust(card);
      return admin.adjustGiftCard.calls.mostRecent().args[1];
    };
    expect(await run('topUp')).toEqual({ delta: 300, reason: 'Test', kind: 'topUp', branchId: 'alg' });
    expect(await run('cashOut')).toEqual({ delta: -300, reason: 'Test', kind: 'cashOut', branchId: 'alg' });
    expect(await run('correctionAdd')).toEqual({ delta: 300, reason: 'Test', kind: 'correction', branchId: null });
    expect(await run('correctionRemove')).toEqual({ delta: -300, reason: 'Test', kind: 'correction', branchId: null });
    // No default: the next change is chosen again.
    expect(page.adjustKind).toBeNull();
  });
});
