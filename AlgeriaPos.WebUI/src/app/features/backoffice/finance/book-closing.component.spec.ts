import { ComponentFixture, TestBed } from '@angular/core/testing';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { BookClosingState, LedgerService } from '../../../core/ledger.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { BookClosingComponent, monthEnd } from './book-closing.component';
import { FinancePagesApi } from './finance-pages.api';

describe('BookClosingComponent (CLO-01)', () => {
  let fixture: ComponentFixture<BookClosingComponent>;
  let ledger: jasmine.SpyObj<LedgerService>;
  let pages: jasmine.SpyObj<FinancePagesApi>;

  const closed: BookClosingState = {
    closedThrough: '2026-09-30',
    history: [
      { id: 'c2', closedThrough: '2026-09-30', isReopen: true, reason: 'Facture oubliée', userName: 'Admin', atUtc: '2026-10-20T09:00:00Z' },
      { id: 'c1', closedThrough: '2026-10-31', isReopen: false, reason: null, userName: 'Admin', atUtc: '2026-10-15T09:00:00Z' },
    ],
  };

  async function render(state: BookClosingState): Promise<BookClosingComponent> {
    ledger = jasmine.createSpyObj<LedgerService>('LedgerService', ['closeBooks', 'reopenBooks']);
    ledger.closeBooks.and.resolveTo(state.history[0]);
    ledger.reopenBooks.and.resolveTo(state.history[0]);
    // The server's page of the trail (owner, 2026-11-17), the books' state beside it.
    pages = jasmine.createSpyObj<FinancePagesApi>('FinancePagesApi', ['bookClosingPage']);
    pages.bookClosingPage.and.callFake(async (q) => ({
      items: state.history, total: state.history.length, skip: 0, take: q.take,
      summary: { closedThrough: state.closedThrough, last: state.history[0] ?? null, all: state.history.length },
    }));
    TestBed.configureTestingModule({
      imports: [BookClosingComponent],
      providers: [
        { provide: LedgerService, useValue: ledger },
        { provide: FinancePagesApi, useValue: pages },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(BookClosingComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  const t = (key: string) => TestBed.inject(I18nService).t(key);

  it('knows the last day of a month, February and leap years included', () => {
    expect(monthEnd('2026-10')).toBe('2026-10-31');
    expect(monthEnd('2026-02')).toBe('2026-02-28');
    expect(monthEnd('2028-02')).toBe('2028-02-29');
    expect(monthEnd('2026-12')).toBe('2026-12-31');
  });

  it('shows the closed day as dd/MM/yyyy and the trail in words, reopen first', async () => {
    await render(closed);
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('30/09/2026');
    expect(text).toContain(t('closing.action.reopened'));
    expect(text).toContain(t('closing.action.closed'));
    expect(text).toContain('Facture oubliée');
    // The month after what is closed is offered, never a month that has not ended.
    expect(fixture.componentInstance.closeMonth).toBe('2026-10');
  });

  it('closes through the last day of the chosen month and reopens to a month end, or to nothing, with the reason', async () => {
    const page = await render(closed);
    page.closeMonth = '2026-10';
    page.note = 'Revu';
    await page.close();
    expect(ledger.closeBooks).toHaveBeenCalledWith('2026-10-31', 'Revu');

    page.reopenMonth = '2026-08';
    page.reason = 'Correction';
    await page.reopen();
    expect(ledger.reopenBooks).toHaveBeenCalledWith('2026-08-31', 'Correction');

    page.reopenAll = true;
    page.reason = 'Tout rouvrir';
    await page.reopen();
    expect(ledger.reopenBooks).toHaveBeenCalledWith(null, 'Tout rouvrir');
    // After each act the trail and the state are asked again.
    expect(pages.bookClosingPage).toHaveBeenCalledTimes(4);
  });

  it('asks the server for the trail with its filters (owner, 2026-11-17)', async () => {
    const page = await render(closed);
    expect(pages.bookClosingPage).toHaveBeenCalledWith({ skip: 0, take: 10, action: 'all', from: '', to: '' });
    page.actionFilter.set('reopened');
    page.fromFilter.set('2026-10-01');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    expect(pages.bookClosingPage).toHaveBeenCalledWith({ skip: 0, take: 10, action: 'reopened', from: '2026-10-01', to: '' });
  });

  it('offers no reopen while nothing is closed', async () => {
    await render({ closedThrough: null, history: [] });
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain(t('closing.none'));
    expect(text).not.toContain(t('closing.reopen.button'));
    expect(text).toContain(t('closing.empty'));
  });
});
