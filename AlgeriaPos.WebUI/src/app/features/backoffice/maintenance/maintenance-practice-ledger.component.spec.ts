import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ApiError } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { MaintenanceLedger, MaintenanceLedgerApi } from './maintenance-ledger-api.service';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { MaintenancePracticeLedgerComponent } from './maintenance-practice-ledger.component';
import { pageOf } from './maintenance-test-data';

describe('MaintenancePracticeLedgerComponent (maintenance, Phase M6)', () => {
  let fixture: ComponentFixture<MaintenancePracticeLedgerComponent>;
  let api: jasmine.SpyObj<MaintenanceLedgerApi>;
  let pages: jasmine.SpyObj<MaintenancePagesApi>;

  const line = (slot: string, debit: number, credit: number, code: string | null = null) => ({
    slot, accountId: code ? `a${code}` : null, accountCode: code, accountNameAr: code ? 'حساب' : null,
    accountNameFr: code ? 'Stocks de marchandises' : null, accountNameEn: code ? 'Merchandise' : null, debit, credit,
  });
  const entry = (kind: string, document: string | null, lines: ReturnType<typeof line>[]) => ({
    kind, day: '2026-09-15', orderId: 'j1', orderNumber: 'MJ-000001', branchId: 'b1', branchCode: '213',
    branchNameAr: 'الجزائر', branchNameFr: 'Alger-Centre', branchNameEn: 'Algiers', document, lines,
  });
  const ledger: MaintenanceLedger = {
    year: 2026, month: 9, monthToDate: false, branchId: null,
    labourInBooks: 'ReportOnly', openJobsAtPeriodEnd: 'LeftInExpensesListed', partsPosting: 'PracticeLedgerByMonth', ownRepairs: 'AlwaysExpense',
    accountantApproved: false,
    entries: [
      entry('parts_issue', 'MJI-000001', [
        line('maintenance.own_parts', 100, 0), line('maintenance.merchandise_stock', 0, 100, '310'),
        line('maintenance.own_parts', 200, 0), line('stock.supplies', 0, 200),
      ]),
      entry('parts_return', 'MJR-000001', [line('stock.supplies', 80, 0), line('maintenance.own_parts', 0, 80)]),
    ],
    accounts: [line('maintenance.own_parts', 300, 80), line('stock.supplies', 80, 200), line('maintenance.merchandise_stock', 0, 100, '310')],
    checks: [
      { code: 'entries_balance', passed: true, expected: 0, actual: 0 },
      { code: 'parts_tie', passed: true, expected: 220, actual: 220 },
      { code: 'wip_tie', passed: true, expected: 0, actual: 0 },
      { code: 'invoices_tie', passed: true, expected: 0, actual: 0 },
      { code: 'accounts_mapped', passed: false, expected: 3, actual: 1 },
    ],
    unmappedSlots: ['maintenance.own_parts', 'stock.supplies'],
    notes: ['practice_only', 'labour_report_only', 'open_jobs_listed', 'own_documents_post', 'policy_not_confirmed'],
    totalDebit: 380, totalCredit: 380,
    postedAtIssue: [],
    openCustomerJobs: [{ jobId: 'j9', jobNumber: 'MJ-900001', branchId: 'b1', branchCode: '213', costToDate: null }],
    invoices: [{ number: 'FA-1', day: '2026-09-20', jobId: null, jobNumber: null, jobMatched: false, netAmount: 5_000 }],
    postingFromMonth: null, postings: [],
  };

  /** The month as the server pages it (owner, 2026-11-17): each list one page, the rest of the month the entries' summary. */
  function serve(month: MaintenanceLedger): void {
    const provisions = month.provisions ?? [];
    pages.ledgerEntries.and.resolveTo(pageOf(month.entries, {
      ledger: { ...month, entries: [], postedAtIssue: [], openCustomerJobs: [], invoices: [], provisions: [] },
      postedAtIssue: month.postedAtIssue.length, openCustomerJobs: month.openCustomerJobs.length,
      invoices: month.invoices.length, provisions: provisions.length,
    }));
    pages.ledgerPostedAtIssue.and.resolveTo(pageOf(month.postedAtIssue));
    pages.ledgerOpenJobs.and.resolveTo(pageOf(month.openCustomerJobs, { branches: [...new Set(month.openCustomerJobs.map((o) => o.branchCode))] }));
    pages.ledgerInvoices.and.resolveTo(pageOf(month.invoices));
    pages.ledgerProvisions.and.resolveTo(pageOf(provisions));
  }

  async function render(answer: MaintenanceLedger = ledger, keys: string[] = ['reports.accounting']): Promise<HTMLElement> {
    api = jasmine.createSpyObj<MaintenanceLedgerApi>('MaintenanceLedgerApi', ['post', 'reverse']);
    pages = jasmine.createSpyObj<MaintenancePagesApi>('MaintenancePagesApi', [
      'ledgerEntries', 'ledgerPostedAtIssue', 'ledgerOpenJobs', 'ledgerInvoices', 'ledgerProvisions',
    ]);
    serve(answer);
    TestBed.configureTestingModule({
      imports: [MaintenancePracticeLedgerComponent],
      providers: [
        provideRouter([]),
        { provide: MaintenanceLedgerApi, useValue: api },
        { provide: MaintenancePagesApi, useValue: pages },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);
    i18n.setLang('fr');
    TestBed.inject(AuthService).currentUser.set({
      id: 'u1', username: 'acc', displayName: 'Accountant', role: 'Manager', permissions: [], capabilities: [],
      permissionKeys: keys, scope: 'Company', maintenanceOn: true,
    } as never);
    fixture = TestBed.createComponent(MaintenancePracticeLedgerComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    // The entries' summary says which lists below have rows; those are read next.
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const t = (key: string, p?: Record<string, string>) => TestBed.inject(I18nService).t(key, p);

  it('asks for the company month first and shows each entry in words, with the stock it left and the slots still unmapped', async () => {
    const page = await render();
    expect(pages.ledgerEntries).toHaveBeenCalledWith({ skip: 0, take: 10, year: null, month: null });
    expect(fixture.componentInstance.month()).toBe('2026-09');
    // The lists below are read for the month the server answered, and only those that have rows.
    expect(pages.ledgerOpenJobs).toHaveBeenCalledWith({ skip: 0, take: 10, year: 2026, month: 9, branchCode: null });
    expect(pages.ledgerInvoices).toHaveBeenCalledWith({ skip: 0, take: 10, year: 2026, month: 9, match: 'all' });
    expect(pages.ledgerPostedAtIssue).not.toHaveBeenCalled();
    expect(pages.ledgerProvisions).not.toHaveBeenCalled();
    const text = page.textContent ?? '';
    expect(text).toContain(t('maintLedger.kind.parts_issue'));
    expect(text).toContain('MJI-000001');
    expect(text).toContain('310 Stocks de marchandises');
    expect(text).toContain(`${t('maintLedger.slot.maintenance.own_parts')} — ${t('mfgChoices.map.unmapped')}`);
    expect(text).toContain(t('maintLedger.check.parts_tie'));
    expect(page.querySelector('.notice.warn')?.textContent).toContain(t('mfgChoices.slot.stock.supplies'));
    expect(text).toContain(t('maintLedger.note.labour_report_only'));
    // Never a raw key on the page.
    expect(text).not.toContain('maintLedger.');
  });

  it('lists open customer jobs without a figure that would give a pay rate back, and an invoice naming no job in words', async () => {
    const page = await render();
    const text = page.textContent ?? '';
    expect(text).toContain('MJ-900001');
    expect(text).toContain(t('maint.cost.notShown'));
    expect(text).toContain(t('maintLedger.invoiceUnmatched'));
  });

  it('lets the accountant post the closed month once posting is on, says when it is off, and says a refusal in words', async () => {
    let page = await render();
    expect(page.textContent).toContain(t('maintLedger.postingOff'));
    expect(page.querySelector('.ml-actions')).toBeNull();
    TestBed.resetTestingModule();

    const open = { ...ledger, postingFromMonth: '2026-09' };
    page = await render(open, ['reports.accounting', 'accounting.manage']);
    api.post.and.rejectWith(new ApiError(JSON.stringify({ detail: 'x', code: 'checks_not_passed' }), 409));
    await fixture.componentInstance.post();
    expect(fixture.componentInstance.error()).toBe(t('maintLedger.error.checks'));

    const posted = { ...open, postings: [{ generation: 1, entries: 2, totalDebit: 380, postedAtUtc: '2026-11-09T08:00:00Z', postedBy: 'acc', reversedAtUtc: null, reversedBy: null }] };
    api.post.and.resolveTo(posted);
    // The month is read again after the post.
    serve(posted);
    await fixture.componentInstance.post();
    expect(api.post).toHaveBeenCalledWith(2026, 9);
    fixture.detectChanges();
    expect((page.querySelector('.ml-actions button') as HTMLButtonElement).textContent).toContain(t('mfgLedger.reverse'));
  });

  it('says the guarantee provision (MS-37, Phase M7c) booked, used by a rework back to the accounts it debited, and released', async () => {
    const page = await render({
      ...ledger,
      rework: 'Provision',
      entries: [
        entry('provision_booked', 'FA-000009', [line('maintenance.provision_expense', 2000, 0, '685'), line('maintenance.provision_liability', 0, 2000, '158')]),
        entry('provision_used', 'MJ-000001', [
          line('maintenance.provision_liability', 1300, 0, '158'), line('maintenance.customer_parts', 0, 800, '600'), line('account:615', 0, 500, '615'),
        ]),
      ],
      checks: [...ledger.checks, { code: 'provision_tie', passed: true, expected: 700, actual: 700 }],
      notes: ['practice_only', 'provision_labour_in_wages'],
      provisions: [{ jobId: 'j4', jobNumber: 'MJ-000004', invoiceNumber: 'FA-000009', amount: 2000, booked: 2000, used: 1300, released: 0, heldAtEnd: 700, guaranteeUntil: '2026-10-15' }],
    });
    const text = page.textContent ?? '';
    expect(text).toContain(t('maintLedger.kind.provision_booked'));
    expect(text).toContain(t('maintLedger.kind.provision_used'));
    expect(text).toContain(t('maintLedger.slot.account', { code: '615' }));
    expect(text).toContain(t('maintLedger.check.provision_tie'));
    expect(text).toContain(t('maintLedger.note.provision_labour_in_wages'));
    expect(text).toContain(t('maintLedger.provisionsTitle'));
    expect(text).toContain('15/10/2026');
    expect(text).not.toContain('maintLedger.');
  });

  it('offers the production tab only to a reader of both practice ledgers', async () => {
    let page = await render();
    expect(page.querySelector('.ui-tabs')).toBeNull();
    TestBed.resetTestingModule();
    page = await render(ledger, ['reports.accounting', 'manufacturing.costs.view', 'manufacturing.costs.rates.view', 'payroll.manage']);
    const tabs = Array.from(page.querySelectorAll('.ui-tabs a'));
    expect(tabs.map((a) => a.textContent?.trim())).toEqual([t('maintLedger.tab.production'), t('maintLedger.tab.maintenance')]);
    expect(tabs[1].getAttribute('aria-current')).toBe('page');
  });
});
