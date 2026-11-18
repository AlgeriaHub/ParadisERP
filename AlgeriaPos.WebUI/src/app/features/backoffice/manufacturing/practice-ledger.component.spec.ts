import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { PageResult } from '../../../core/paging';
import { ConfirmService } from '../../../ui/confirm.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { CostingSettingsApi, PracticeEntry, PracticeLedger } from './costing-settings-api.service';
import { ManufacturingApiService } from './manufacturing-api.service';
import { ManufacturingPagesApi, PracticeLedgerPageSummary } from './manufacturing-pages.api';
import { MfgPracticeLedgerComponent } from './practice-ledger.component';

describe('MfgPracticeLedgerComponent (Phase C2)', () => {
  let fixture: ComponentFixture<MfgPracticeLedgerComponent>;
  let api: jasmine.SpyObj<CostingSettingsApi>;
  let pages: jasmine.SpyObj<ManufacturingPagesApi>;

  const line = (slot: string, debit: number, credit: number, code: string | null = null) => ({
    slot, accountId: code ? `a${code}` : null, accountCode: code, accountNameAr: code ? 'حساب' : null,
    accountNameFr: code ? 'Stock de matières' : null, accountNameEn: code ? 'Raw materials' : null, debit, credit,
  });
  const ledger: PracticeLedger = {
    year: 2026, month: 10, monthToDate: false, model: 'ByNature', monthEndWorkInProgress: 'Monthly',
    lateCostTreatment: 'StayInCharges', variances: 'ManagementOnly',
    entries: [
      {
        kind: 'material_issue', day: '2026-10-20', orderId: 'o1', orderNumber: 'MO-000001', branchId: 'b1', branchCode: '361',
        branchNameAr: 'الورشة', branchNameFr: 'Atelier', branchNameEn: 'Workshop', document: 'MI-000001',
        lines: [line('consumption.raw_material', 15_750, 0), line('stock.raw_material', 0, 15_750, '31')],
      },
      {
        kind: 'month_end_wip', day: '2026-10-31', orderId: 'o1', orderNumber: 'MO-000001', branchId: 'b1', branchCode: '361',
        branchNameAr: 'الورشة', branchNameFr: 'Atelier', branchNameEn: 'Workshop', document: null,
        lines: [line('work_in_progress', 17_750, 0), line('production_stocked', 0, 17_750)],
      },
    ],
    accounts: [line('stock.raw_material', 0, 15_750, '31'), line('work_in_progress', 17_750, 0)],
    lateCosts: [],
    checks: [
      { code: 'entries_balance', passed: true, expected: 0, actual: 0 },
      { code: 'accounts_mapped', passed: false, expected: 4, actual: 1 },
    ],
    unmappedSlots: ['consumption.raw_material', 'work_in_progress', 'production_stocked'],
    notes: ['practice_only', 'scrap_recovery_not_recorded'],
    totalDebit: 33_500, totalCredit: 33_500, postingFromMonth: null, postings: [],
  };

  /** The server's page of the month's entries: every entry on one page, the month itself in the summary. */
  function entriesPage(month: PracticeLedger): PageResult<PracticeEntry, PracticeLedgerPageSummary> {
    const { entries, lateCosts, ...rest } = month;
    const branches = [...new Map(entries.map((e) => [e.branchId, {
      id: e.branchId, code: e.branchCode, nameAr: e.branchNameAr, nameFr: e.branchNameFr, nameEn: e.branchNameEn,
    }])).values()];
    return {
      items: entries, total: entries.length, skip: 0, take: 10,
      summary: { ...rest, entryCount: entries.length, lateCostCount: lateCosts.length, kinds: [...new Set(entries.map((e) => e.kind))], branches },
    };
  }

  async function render(answer: PracticeLedger = ledger, keys: string[] = []): Promise<HTMLElement> {
    api = jasmine.createSpyObj<CostingSettingsApi>('CostingSettingsApi', ['practiceLedger', 'postMonth', 'reverseMonth']);
    pages = jasmine.createSpyObj<ManufacturingPagesApi>('ManufacturingPagesApi', ['ledgerEntries', 'ledgerLateCosts']);
    pages.ledgerEntries.and.resolveTo(entriesPage(answer));
    pages.ledgerLateCosts.and.resolveTo({ items: [], total: 0, skip: 0, take: 10, summary: { all: 0, choices: [] } });
    const mfg = jasmine.createSpyObj<ManufacturingApiService>('ManufacturingApiService', ['lines']);
    mfg.lines.and.resolveTo([]);
    TestBed.configureTestingModule({
      imports: [MfgPracticeLedgerComponent],
      providers: [
        { provide: CostingSettingsApi, useValue: api },
        { provide: ManufacturingPagesApi, useValue: pages },
        { provide: ManufacturingApiService, useValue: mfg },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);
    i18n.setLang('fr');
    TestBed.inject(AuthService).currentUser.set({
      id: 'u1', username: 'acc', displayName: 'Accountant', role: 'Manager', permissions: [], capabilities: [],
      permissionKeys: keys, scope: 'Company',
    } as never);
    fixture = TestBed.createComponent(MfgPracticeLedgerComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const t = (key: string, p?: Record<string, string>) => TestBed.inject(I18nService).t(key, p);

  it('asks the server for the company month first, then shows the month the server answered', async () => {
    await render();
    expect(pages.ledgerEntries).toHaveBeenCalledWith(
      jasmine.objectContaining({ skip: 0, take: 10 }),
      jasmine.objectContaining({ year: null, month: null, lineId: null }),
    );
    expect(fixture.componentInstance.shownMonth()).toBe('2026-10');
    // The month holds no late cost: its page is never asked.
    expect(pages.ledgerLateCosts).not.toHaveBeenCalled();
  });

  it('shows each entry in words with its accounts, the checks, and the slots still without an account', async () => {
    const page = await render();
    const text = page.textContent ?? '';
    expect(text).toContain(t('mfgLedger.kind.material_issue'));
    expect(text).toContain('MI-000001');
    expect(text).toContain('31 Stock de matières');
    expect(text).toContain(`${t('mfgChoices.slot.work_in_progress')} — ${t('mfgChoices.map.unmapped')}`);
    expect(text).toContain(t('mfgLedger.fail'));
    expect(page.querySelector('.notice.warn')?.textContent).toContain(t('mfgChoices.slot.production_stocked'));
    expect(text).toContain(t('mfgLedger.note.practice_only'));
  });

  it('lets the accountant post the closed month once posting is on, and says when it is off', async () => {
    let page = await render();
    expect(page.textContent).toContain(t('mfgLedger.postingOff'));
    expect(page.querySelector('.pl-actions')).toBeNull();
    TestBed.resetTestingModule();

    const open = { ...ledger, postingFromMonth: '2026-10' };
    page = await render(open, ['accounting.manage']);
    const posted: PracticeLedger = {
      ...open,
      postings: [{ generation: 1, entries: 2, totalDebit: 33_500, postedAtUtc: '2026-11-09T08:00:00Z', postedBy: 'acc', reversedAtUtc: null, reversedBy: null }],
    };
    api.postMonth.and.resolveTo(posted);
    // After posting, the month is read again from the server, which now holds the posting.
    pages.ledgerEntries.and.resolveTo(entriesPage(posted));
    const button = page.querySelector('.pl-actions button') as HTMLButtonElement;
    expect(button.textContent).toContain(t('mfgLedger.post'));
    await fixture.componentInstance.post();
    expect(api.postMonth).toHaveBeenCalledWith(2026, 10);
    fixture.detectChanges();
    expect((page.querySelector('.pl-actions button') as HTMLButtonElement).textContent).toContain(t('mfgLedger.reverse'));
  });
});
