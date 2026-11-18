import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ApiError } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { CustomerAdvanceSettingsComponent } from './customer-advance-settings.component';
import { ReceivableSettings, ReceivablesApi } from './receivables-api.service';

describe('CustomerAdvanceSettingsComponent (M7b, SAL-12 / D45)', () => {
  let fixture: ComponentFixture<CustomerAdvanceSettingsComponent>;
  let api: jasmine.SpyObj<ReceivablesApi>;

  const defaults: ReceivableSettings = {
    advancesHeldOn: 'CustomerAccount', advanceVat: 'AtInvoice', advancesAccountReady: true, openAdvances: 0,
    updatedAtUtc: null, updatedBy: null,
  };

  async function render(keys: string[], settings: ReceivableSettings = defaults): Promise<HTMLElement> {
    api = jasmine.createSpyObj<ReceivablesApi>('ReceivablesApi', ['settings', 'saveSettings']);
    api.settings.and.resolveTo(settings);
    api.saveSettings.and.callFake(async (r) => ({ ...settings, ...r, updatedAtUtc: '2026-11-09T10:00:00Z', updatedBy: 'comptable' }));
    TestBed.configureTestingModule({
      imports: [CustomerAdvanceSettingsComponent],
      providers: [{ provide: ReceivablesApi, useValue: api }],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    TestBed.inject(AuthService).currentUser.set({
      id: 'u1', username: 'u', displayName: 'User', role: 'Manager', permissions: [], capabilities: [],
      permissionKeys: keys, scope: 'Company',
    } as never);
    fixture = TestBed.createComponent(CustomerAdvanceSettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const select = (page: HTMLElement, id: string) => page.querySelector(`#${id}`) as HTMLSelectElement;
  const t = (key: string) => TestBed.inject(I18nService).t(key);

  it("shows today's behaviour as the marked default and keeps VAT at the invoice while advances are on 411", async () => {
    const page = await render(['accounting.manage']);
    expect(select(page, 'adv-held').value).toBe('CustomerAccount');
    expect(select(page, 'adv-held').options[0].textContent).toContain(t('adv.settings.default'));
    expect(select(page, 'adv-vat').value).toBe('AtInvoice');
    // VAT when received is 419's only.
    expect(select(page, 'adv-vat').disabled).toBeTrue();
    expect(page.textContent).not.toContain('{tax}');
  });

  it('saves 419 and VAT when received under accounting.manage', async () => {
    const page = await render(['accounting.manage']);
    const held = select(page, 'adv-held');
    held.value = 'AdvancesAccount';
    held.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const vat = select(page, 'adv-vat');
    expect(vat.disabled).toBeFalse();
    vat.value = 'WhenReceived';
    vat.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    (page.querySelector('.foot .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(api.saveSettings).toHaveBeenCalledWith({
      advancesHeldOn: 'AdvancesAccount', advanceVat: 'WhenReceived', keptAdvances: 'NotAllowed', keptIncomeAccount: '',
    });
    expect(page.textContent).toContain(t('adv.settings.saved'));
  });

  // ---------- SAL-13: kept advances ----------

  const withAccounts: ReceivableSettings = {
    ...defaults,
    keptAdvances: 'NotAllowed',
    keptIncomeAccount: null,
    incomeAccounts: [
      { code: '700', nameAr: 'مبيعات', nameFr: 'Ventes de marchandises', nameEn: 'Sales' },
      { code: '7585', nameAr: 'إيرادات أخرى', nameFr: 'Autres produits', nameEn: 'Other income' },
    ],
  };

  async function change(page: HTMLElement, id: string, value: string): Promise<void> {
    const s = select(page, id);
    s.value = value;
    s.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('keeps advances not allowed by default, the income account closed until "kept as other income" is chosen', async () => {
    const page = await render(['accounting.manage'], withAccounts);
    expect(select(page, 'adv-kept').value).toBe('NotAllowed');
    expect(select(page, 'adv-kept').options[0].textContent).toContain(t('adv.settings.default'));
    expect(select(page, 'adv-kept-account').disabled).toBeTrue();
    expect(page.textContent).toContain('SAL-13');

    await change(page, 'adv-kept', 'OtherIncome');
    const account = select(page, 'adv-kept-account');
    expect(account.disabled).toBeFalse();
    expect(Array.from(account.options).map((o) => o.textContent?.trim())).toContain('7585 — Autres produits');
  });

  it('saves "kept as other income" with the account chosen', async () => {
    const page = await render(['accounting.manage'], withAccounts);
    await change(page, 'adv-kept', 'OtherIncome');
    const account = select(page, 'adv-kept-account');
    account.selectedIndex = Array.from(account.options).findIndex((o) => o.textContent?.includes('7585'));
    account.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    (page.querySelector('.foot .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(api.saveSettings).toHaveBeenCalledWith({
      advancesHeldOn: 'CustomerAccount', advanceVat: 'AtInvoice', keptAdvances: 'OtherIncome', keptIncomeAccount: '7585',
    });
  });

  it('says the income account is missing when the server refuses "kept as other income" without one', async () => {
    const page = await render(['accounting.manage'], withAccounts);
    api.saveSettings.and.rejectWith(new ApiError(JSON.stringify({ detail: 'no account', code: 'accounts_not_mapped' }), 409));
    await change(page, 'adv-kept', 'OtherIncome');
    (page.querySelector('.foot .btn-primary') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(page.textContent).toContain(t('adv.settings.error.keptAccount'));
  });

  it('is read-only without accounting.manage and offers no 419 while the account is missing', async () => {
    const page = await render(['finance.settings'], { ...defaults, advancesAccountReady: false });
    expect(select(page, 'adv-held').disabled).toBeTrue();
    expect(page.querySelector('.foot')).toBeNull();
    expect(page.textContent).toContain(t('adv.settings.readOnly'));
    const option419 = Array.from(select(page, 'adv-held').options).find((o) => o.value === 'AdvancesAccount')!;
    expect(option419.disabled).toBeTrue();
    expect(page.textContent).toContain(t('adv.settings.accountMissing'));
  });
});
