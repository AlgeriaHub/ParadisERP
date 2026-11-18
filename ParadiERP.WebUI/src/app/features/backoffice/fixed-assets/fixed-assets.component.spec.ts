import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminService } from '../../../core/admin.service';
import { ApiError } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { LedgerService } from '../../../core/ledger.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { FinancePagesApi } from '../finance/finance-pages.api';
import { FixedAssetSettingsComponent, legalCoefficient, methodLine } from './fixed-asset-settings.component';
import { BackofficeFixedAssetsComponent } from './fixed-assets.component';
import {
  FixedAssetDepreciation,
  FixedAssetHistory,
  FixedAssetRegister,
  FixedAssetSettings,
  FixedAssetSwitch,
  FixedAssetsApi,
} from './fixed-assets-api.service';

describe('Fixed assets (REN-12, Phase M7a)', () => {
  let api: jasmine.SpyObj<FixedAssetsApi>;
  let pages: jasmine.SpyObj<FinancePagesApi>;

  // The lists come a page at a time from the server (owner, 2026-11-17): each report's rows as the page, its figures beside.
  function registerIs(r: FixedAssetRegister): void {
    const { pools, ...figures } = r;
    pages.fixedAssetRegisterPage.and.callFake(async (q) => ({
      items: pools, total: pools.length, skip: 0, take: q.take, summary: { ...figures, poolCount: pools.length },
    }));
  }
  function monthIs(read: () => FixedAssetDepreciation): void {
    pages.fixedAssetDepreciationPage.and.callFake(async (q) => {
      const { entries, ...figures } = read();
      return { items: entries, total: entries.length, skip: 0, take: q.take, summary: { ...figures, entryCount: entries.length } };
    });
  }
  function historyIs(h: FixedAssetHistory): void {
    const { lines, ...figures } = h;
    pages.fixedAssetHistoryPage.and.callFake(async (q) => ({
      items: lines, total: lines.length, skip: 0, take: q.take,
      summary: { ...figures, kinds: [...new Set(lines.map((l) => l.kind))], lineCount: lines.length },
    }));
  }
  /** Change detection, the answers, and change detection again: a pager asks when its view is checked. */
  async function settle<T>(fixture: ComponentFixture<T>): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const pool = {
    productId: 'p1', sku: 'CAD-01', nameAr: 'إطار', nameFr: 'Cadre', nameEn: 'Frame', branchId: 'b1', branchCode: '213',
    branchNameAr: 'الجزائر', branchNameFr: 'Alger-Centre', branchNameEn: 'Algiers',
    units: 2_000, unitsInStock: 1_998, cost: 13_000_000, accumulatedDepreciation: 7_800_000, netBookValue: 5_200_000,
    usefulLifeMonths: 60, monthlyCharge: 216_666.667, scrapQty: 0, scrapValue: 0,
  };
  const register: FixedAssetRegister = {
    active: true, switchMonth: '2023-11', year: 2026, month: 10, monthToDate: false, branchId: null, pools: [pool],
    cost: 13_000_000, accumulatedDepreciation: 7_800_000, netBookValue: 5_200_000, units: 2_000, postedThrough: '2026-09',
  };
  const off: FixedAssetRegister = { ...register, active: false, switchMonth: null, pools: [], cost: 0, accumulatedDepreciation: 0, netBookValue: 0, units: 0, postedThrough: null };
  const line = (slot: string, debit: number, credit: number) => ({
    slot, accountId: 'a' + slot, accountCode: slot.endsWith('expense') ? '681' : '2815', accountNameAr: 'حساب',
    accountNameFr: slot.endsWith('expense') ? 'Dotations' : 'Amortissements', accountNameEn: 'Account', debit, credit,
  });
  const month: FixedAssetDepreciation = {
    year: 2026, month: 10, monthToDate: false, branchId: null, active: true, switchMonth: '2023-11',
    entries: [{
      kind: 'depreciation', day: '2026-10-31', orderId: 'p1', orderNumber: 'CAD-01', branchId: 'b1', branchCode: '213',
      branchNameAr: 'الجزائر', branchNameFr: 'Alger-Centre', branchNameEn: 'Algiers', document: null,
      lines: [line('fixed_assets.depreciation_expense', 216_666.667, 0), line('fixed_assets.accumulated_depreciation', 0, 216_666.667)],
    }],
    accounts: [], checks: [{ code: 'entries_balance', passed: true, expected: 0, actual: 0 }, { code: 'accounts_mapped', passed: true, expected: 2, actual: 2 }],
    unmappedSlots: [], notes: [], totalDebit: 216_666.667, totalCredit: 216_666.667, postings: [],
  };

  function setUp(keys: string[]): void {
    api = jasmine.createSpyObj<FixedAssetsApi>('FixedAssetsApi', [
      'settings', 'saveSettings', 'saveAccountMap', 'switchOver', 'postSwitchOver', 'register', 'history', 'depreciation', 'post', 'reverse',
    ]);
    pages = jasmine.createSpyObj<FinancePagesApi>('FinancePagesApi', [
      'fixedAssetRegisterPage', 'fixedAssetHistoryPage', 'fixedAssetDepreciationPage',
    ]);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: FixedAssetsApi, useValue: api },
        { provide: FinancePagesApi, useValue: pages },
        { provide: AdminService, useValue: { listBranches: () => Promise.resolve([]) } },
        { provide: LedgerService, useValue: { listAccounts: () => Promise.resolve([]) } },
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
  }

  async function render<T>(type: Type<T>): Promise<{ fixture: ComponentFixture<T>; page: HTMLElement }> {
    const fixture = TestBed.createComponent(type);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    // ngModel writes its value a turn later.
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, page: fixture.nativeElement as HTMLElement };
  }

  const t = (key: string, p?: Record<string, string>) => TestBed.inject(I18nService).t(key, p);

  it('says in words that the register is off until the switch-over, and never shows a raw key', async () => {
    setUp(['reports.accounting']);
    registerIs(off);
    const { fixture, page } = await render(BackofficeFixedAssetsComponent);
    expect(pages.fixedAssetRegisterPage).toHaveBeenCalledWith(
      jasmine.objectContaining({ skip: 0, take: 10, year: null, month: null, branchId: null }),
    );
    expect(fixture.componentInstance.month()).toBe('2026-10');
    expect(page.querySelector('[data-testid="fa-off"]')?.textContent).toContain(t('fa.register.off'));
    expect(page.textContent).not.toContain('fa.');
  });

  it('lists each pool with its cost, depreciation and net book value, says when stock differs, and opens its history', async () => {
    setUp(['reports.accounting']);
    registerIs(register);
    historyIs({
      productId: 'p1', sku: 'CAD-01', nameAr: 'إطار', nameFr: 'Cadre', nameEn: 'Frame', branchId: 'b1', branchCode: '213', pool,
      lines: [
        { atUtc: '2023-11-05T10:00:00Z', kind: 'Opening', qty: 2_000, cost: 13_000_000, depreciation: 0, otherBranchId: null, otherBranchCode: null, documentNumber: null, reason: 'switch-over', by: 'acc', posted: true },
        { atUtc: '2023-11-30T11:00:00Z', kind: 'Depreciation', qty: 0, cost: 0, depreciation: 216_666.667, otherBranchId: null, otherBranchCode: null, documentNumber: null, reason: null, by: null, posted: false },
      ],
    });
    const { fixture, page } = await render(BackofficeFixedAssetsComponent);
    const text = page.textContent ?? '';
    expect(page.querySelector('[data-testid="fa-nbv"]')?.textContent).toContain(TestBed.inject(I18nService).money(5_200_000));
    expect(text).toContain('Cadre');
    expect(text).toContain(t('fa.register.inStock', { qty: '1998' }));
    expect(text).toContain(t('fa.register.postedThrough', { month: '09/2026' }));

    (page.querySelector('td .ui-link') as HTMLButtonElement).click();
    await settle(fixture);
    expect(pages.fixedAssetHistoryPage).toHaveBeenCalledWith(jasmine.objectContaining({ productId: 'p1', branchId: 'b1' }));
    const history = page.querySelector('[data-testid="fa-history"]')?.textContent ?? '';
    expect(history).toContain(t('fa.kind.Opening'));
    expect(history).toContain(t('fa.history.notPosted'));
    expect(page.textContent).not.toContain('fa.kind.');
  });

  it('lets the accountant post an ended month of depreciation and says a refusal in words', async () => {
    setUp(['reports.accounting', 'accounting.manage']);
    registerIs(register);
    let current = month;
    monthIs(() => current);
    const { fixture, page } = await render(BackofficeFixedAssetsComponent);
    fixture.componentInstance.go('depreciation');
    await settle(fixture);
    expect(pages.fixedAssetDepreciationPage).toHaveBeenCalledWith(jasmine.objectContaining({ year: 2026, month: 10, branchId: null }));
    expect(page.querySelector('[data-testid="fa-month-total"]')?.textContent).toContain(TestBed.inject(I18nService).money(216_666.667));
    expect(page.textContent).toContain('681 Dotations');

    api.post.and.rejectWith(new ApiError(JSON.stringify({ detail: 'x', code: 'previous_month_not_posted' }), 409));
    await fixture.componentInstance.post();
    expect(fixture.componentInstance.error()).toBe(t('maintLedger.error.previous'));

    const posted = { ...month, postings: [{ generation: 1, entries: 1, totalDebit: 216_666.667, postedAtUtc: '2026-11-09T08:00:00Z', postedBy: 'acc', reversedAtUtc: null, reversedBy: null }] };
    api.post.and.callFake(async () => {
      current = posted;
      return posted;
    });
    await fixture.componentInstance.post();
    expect(api.post).toHaveBeenCalledWith(2026, 10);
    fixture.detectChanges();
    expect(page.querySelector('.fa-actions button')?.textContent).toContain(t('mfgLedger.reverse'));
  });

  it('offers no posting to a reader without accounting.manage', async () => {
    setUp(['reports.accounting']);
    registerIs(register);
    monthIs(() => month);
    const { fixture, page } = await render(BackofficeFixedAssetsComponent);
    fixture.componentInstance.go('depreciation');
    await settle(fixture);
    expect(page.querySelector('[data-testid="fa-post"]')).toBeNull();
  });

  describe('settings and the switch-over', () => {
    const settings: FixedAssetSettings = {
      treatment: 'Stock', method: 'StraightLine', defaultUsefulLifeMonths: null, residualPercent: 0, openingValue: 'CostInForce',
      switchMonth: null, switchedAtUtc: null, switchedBy: null, active: false, depreciationLocked: false,
      lives: [{ productId: 'p1', sku: 'CAD-01', nameAr: 'إطار', nameFr: 'Cadre', nameEn: 'Frame', usefulLifeMonths: null, locked: false }],
      slots: [
        { slot: 'fixed_assets.asset', ledgerAccountId: null, accountCode: null, accountNameAr: null, accountNameFr: null, accountNameEn: null, hintCode: '215', required: true, updatedAtUtc: null, updatedBy: null },
      ],
      updatedAtUtc: null, updatedBy: null,
    };
    const blocked: FixedAssetSwitch = {
      switchMonth: null, costMethod: 'CostPrice', openingValue: 'CostInForce', posted: false, switchedAtUtc: null, switchedBy: null,
      lines: [], stockValue: 13_000_000, openingTotal: 13_000_000, difference: 0,
      blocking: [{ code: 'treatment_stock', message: 'x' }, { code: 'slot_unmapped', message: 'y' }],
    };

    it('shows today’s treatment as the default and says in words what stands in the way of the switch-over', async () => {
      setUp(['reports.accounting', 'accounting.manage']);
      api.settings.and.resolveTo(settings);
      api.switchOver.and.resolveTo(blocked);
      const { page } = await render(FixedAssetSettingsComponent);
      expect((page.querySelector('#fa-treatment') as HTMLSelectElement).value).toBe('Stock');
      const text = page.textContent ?? '';
      expect(text).toContain(t('fa.blocking.treatment_stock'));
      expect(text).toContain(t('fa.blocking.slot_unmapped'));
      expect((page.querySelector('[data-testid="fa-switch-post"]') as HTMLButtonElement).disabled).toBeTrue();
      expect(text).not.toContain('fa.');
    });

    it('saves the accountant’s choices and posts the switch-over with the figures shown', async () => {
      setUp(['reports.accounting', 'accounting.manage']);
      api.settings.and.resolveTo(settings);
      api.switchOver.and.resolveTo({ ...blocked, blocking: [] });
      api.saveSettings.and.resolveTo({ ...settings, treatment: 'FixedAssets', defaultUsefulLifeMonths: 60, switchMonth: '2026-11' });
      api.postSwitchOver.and.resolveTo({ ...blocked, blocking: [], posted: true, switchedBy: 'acc', switchedAtUtc: '2026-11-09T09:00:00Z' });
      const { fixture } = await render(FixedAssetSettingsComponent);
      const c = fixture.componentInstance;
      c.draft = { ...c.draft, treatment: 'FixedAssets', defaultUsefulLifeMonths: 60, switchMonth: '2026-11' };
      c.lifeDraft['p1'] = 48;
      await c.save();
      expect(api.saveSettings).toHaveBeenCalledWith(jasmine.objectContaining({
        treatment: 'FixedAssets', defaultUsefulLifeMonths: 60, switchMonth: '2026-11', lives: [{ productId: 'p1', usefulLifeMonths: 48 }],
      }));
      await c.switchOver();
      expect(api.postSwitchOver).toHaveBeenCalledWith(13_000_000, 13_000_000);
    });

    it('reads the settings without the switch-over for a reader who may not change them', async () => {
      setUp(['reports.accounting']);
      api.settings.and.resolveTo(settings);
      const { page } = await render(FixedAssetSettingsComponent);
      expect(api.switchOver).not.toHaveBeenCalled();
      expect(page.querySelector('[data-testid="fa-switch"]')).toBeNull();
      expect((page.querySelector('#fa-treatment') as HTMLSelectElement).disabled).toBeTrue();
    });

    // Declining balance (2026-11-10): a choice the accountant makes, the company's default and each type's own.
    describe('declining balance', () => {
      it('suggests the tax code’s coefficient from the life and sends only the fields a type sets', () => {
        expect([36, 48, 59, 60, 72, 73, 120].map((m) => legalCoefficient(m))).toEqual([1.5, 1.5, 1.5, 2, 2, 2.5, 2.5]);
        expect(legalCoefficient(null)).toBeNull();
        expect(methodLine('p1', { method: '', coefficient: null, switch: '' }))
          .toEqual({ productId: 'p1', method: null, decliningCoefficient: null, switchToStraightLine: null });
        expect(methodLine('p1', { method: 'DecliningBalance', coefficient: '1.5' as never, switch: 'off' }))
          .toEqual({ productId: 'p1', method: 'DecliningBalance', decliningCoefficient: 1.5, switchToStraightLine: false });
      });

      it('offers declining balance, saves a type’s own coefficient, and says a refusal once posted in words', async () => {
        setUp(['reports.accounting', 'accounting.manage']);
        const chosen: FixedAssetSettings = {
          ...settings, treatment: 'FixedAssets', defaultUsefulLifeMonths: 60, switchToStraightLine: true, methodLocked: false,
          lives: [{ ...settings.lives[0], suggestedCoefficient: 2, effectiveMethod: 'StraightLine', methodLocked: false }],
        };
        api.settings.and.resolveTo(chosen);
        api.switchOver.and.resolveTo({ ...blocked, blocking: [] });
        api.saveSettings.and.resolveTo(chosen);
        const { fixture, page } = await render(FixedAssetSettingsComponent);
        const option = page.querySelector('#fa-method option[value="DecliningBalance"]') as HTMLOptionElement;
        expect(option.disabled).toBeFalse();
        expect((page.querySelector('#fa-switch-sl') as HTMLInputElement).checked).toBeTrue();
        const c = fixture.componentInstance;

        // The type's own declining balance: its coefficient field appears, the legal one suggested.
        c.methodDraft['p1'] = { method: 'DecliningBalance', coefficient: 1.5, switch: '' };
        fixture.detectChanges();
        expect(page.textContent).toContain(t('fa.settings.coefficientSuggested', { value: '2.0' }));
        await c.save();
        expect(api.saveSettings).toHaveBeenCalledWith(jasmine.objectContaining({
          switchToStraightLine: true,
          methods: [{ productId: 'p1', method: 'DecliningBalance', decliningCoefficient: 1.5, switchToStraightLine: null }],
        }));

        api.saveSettings.and.rejectWith(new ApiError(JSON.stringify({ detail: 'x', code: 'method_locked' }), 409));
        c.draft = { ...c.draft, method: 'DecliningBalance' };
        await c.save();
        expect(c.error()).toBe(t('fa.error.methodLocked'));
        expect(page.textContent).not.toContain('fa.');
      });

      it('fixes a type’s method once depreciation is posted on it, and the company’s when a type follows it', async () => {
        setUp(['reports.accounting', 'accounting.manage']);
        api.settings.and.resolveTo({
          ...settings, treatment: 'FixedAssets', method: 'DecliningBalance', defaultUsefulLifeMonths: 60, active: true, depreciationLocked: true,
          switchToStraightLine: true, methodLocked: true,
          lives: [{ ...settings.lives[0], locked: true, methodLocked: true, effectiveMethod: 'DecliningBalance', effectiveCoefficient: 2, suggestedCoefficient: 2 }],
        });
        api.switchOver.and.resolveTo({ ...blocked, blocking: [], posted: true });
        const { page } = await render(FixedAssetSettingsComponent);
        expect((page.querySelector('#fa-method') as HTMLSelectElement).disabled).toBeTrue();
        expect((page.querySelector('#fa-switch-sl') as HTMLInputElement).disabled).toBeTrue();
        expect((page.querySelector('select[aria-label^="' + t('fa.settings.ownMethod') + '"]') as HTMLSelectElement).disabled).toBeTrue();
        const text = page.textContent ?? '';
        expect(text).toContain(t('fa.settings.methodFixed'));
        expect(text).toContain(t('fa.settings.defaultMethodFixed'));
      });

      it('shows a pool’s schedule year by year with the switch to straight line said in words', async () => {
        setUp(['reports.accounting']);
        const declining = { ...pool, method: 'DecliningBalance' as const, decliningCoefficient: 2, switchToStraightLine: true };
        registerIs({ ...register, pools: [declining] });
        historyIs({
          productId: 'p1', sku: 'CAD-01', nameAr: 'إطار', nameFr: 'Cadre', nameEn: 'Frame', branchId: 'b1', branchCode: '213', pool: declining,
          lines: [],
          schedule: [
            { year: 2027, opening: 1_000_000, charge: 400_000, movements: 0, closing: 600_000, switches: false, projected: false },
            { year: 2028, opening: 600_000, charge: 240_000, movements: 0, closing: 360_000, switches: false, projected: true },
            { year: 2029, opening: 360_000, charge: 144_000, movements: 0, closing: 216_000, switches: false, projected: true },
            { year: 2030, opening: 216_000, charge: 108_000, movements: 0, closing: 108_000, switches: true, projected: true },
            { year: 2031, opening: 108_000, charge: 108_000, movements: 0, closing: 0, switches: false, projected: true },
          ],
        });
        const { fixture, page } = await render(BackofficeFixedAssetsComponent);
        expect(page.textContent).toContain(t('fa.method.short.decliningSwitch', { coefficient: '2.0' }));
        (page.querySelector('td .ui-link') as HTMLButtonElement).click();
        await settle(fixture);
        const rows = Array.from(page.querySelectorAll('[data-testid="fa-schedule"] tbody tr'));
        expect(rows.length).toBe(5);
        const money = TestBed.inject(I18nService).money.bind(TestBed.inject(I18nService));
        expect(rows[0].textContent).toContain(money(400_000));
        expect(rows[3].textContent).toContain(t('fa.schedule.switch'));
        expect(rows[3].classList).toContain('fa-switch-year');
        expect(rows[1].textContent).not.toContain(t('fa.schedule.switch'));
        expect(rows[4].textContent).toContain(t('fa.schedule.projected'));
        // No year had events: the column is left out.
        expect(page.querySelector('[data-testid="fa-schedule"]')?.textContent).not.toContain(t('fa.schedule.movements'));
        expect(page.textContent).not.toContain('fa.');
      });
    });
  });
});
