import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { LedgerService } from '../../../core/ledger.service';
import { AccountSlot, CostingSettings, CostingSettingsApi } from './costing-settings-api.service';
import { MfgCostingSettingsComponent } from './costing-settings.component';

describe('MfgCostingSettingsComponent (Phase C1)', () => {
  let fixture: ComponentFixture<MfgCostingSettingsComponent>;
  let api: jasmine.SpyObj<CostingSettingsApi>;

  const settings: CostingSettings = {
    model: 'ByNature', overheadPerLabourHour: 0, releaseRule: 'StandardMaterialPlusConversion', employerBurdenUse: 'None',
    employerBurdenPercent: 0, monthEndWorkInProgress: 'Monthly', lateCosts: 'StayInCharges', variances: 'ManagementOnly',
    expenseBasis: 'BeforeRecoverableVat', expenseVatPercent: 0, scrapRecovery: 'None', scrapRecoveryPercent: 0,
    stockOperations: false, stockOperationsByServer: false, accountantApproved: false, approvedBy: null, approvedAtUtc: null,
    postingFromMonth: null, labourAbsorption: 'BlendedActualRate',
    updatedAtUtc: null, updatedBy: null,
  };
  const map: AccountSlot[] = [
    { slot: 'work_in_progress', ledgerAccountId: null, accountCode: null, accountNameAr: null, accountNameFr: null, accountNameEn: null, updatedAtUtc: null, updatedBy: null },
    { slot: 'production_stocked', ledgerAccountId: 'a72', accountCode: '72', accountNameAr: 'إنتاج مخزن', accountNameFr: 'Production stockée', accountNameEn: 'Production stocked', updatedAtUtc: null, updatedBy: null },
  ];

  async function render(keys: string[]): Promise<HTMLElement> {
    api = jasmine.createSpyObj<CostingSettingsApi>('CostingSettingsApi', ['settings', 'saveSettings', 'accountMap', 'saveAccountMap']);
    api.settings.and.resolveTo(settings);
    api.accountMap.and.resolveTo(map);
    api.saveSettings.and.callFake(async (d) => ({ ...settings, ...d, updatedAtUtc: '2026-11-08T10:00:00Z', updatedBy: 'admin' }));
    const ledger = jasmine.createSpyObj<LedgerService>('LedgerService', ['listAccounts']);
    ledger.listAccounts.and.resolveTo([]);
    TestBed.configureTestingModule({
      imports: [MfgCostingSettingsComponent],
      providers: [{ provide: CostingSettingsApi, useValue: api }, { provide: LedgerService, useValue: ledger }],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    TestBed.inject(AuthService).currentUser.set({
      id: 'u1', username: 'acc', displayName: 'Accountant', role: 'Manager', permissions: [], capabilities: [],
      permissionKeys: keys, scope: 'Company',
    } as never);
    fixture = TestBed.createComponent(MfgCostingSettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const t = (key: string) => TestBed.inject(I18nService).t(key);

  it('marks the suggested answer of each choice and says each slot of the map in words', async () => {
    const page = await render(['manufacturing.costs.view']);
    const model = page.querySelector('#mc-model') as HTMLSelectElement;
    expect(model.options[0].textContent).toContain(t('mfgChoices.suggested'));
    expect(page.textContent).toContain(t('mfgChoices.map.unmapped'));
    expect(page.textContent).toContain('Production stockée');
  });

  it('offers the labour absorption as a choice, the blended rate suggested, and saves the premium choice (C-05)', async () => {
    const page = await render(['accounting.manage', 'manufacturing.costs.view']);
    const c = fixture.componentInstance;
    const select = page.querySelector('#mc-labourAbsorption') as HTMLSelectElement;
    expect(select).not.toBeNull();
    expect(select.options[0].textContent).toContain(t('mfgChoices.labourAbsorption.BlendedActualRate'));
    expect(select.options[0].textContent).toContain(t('mfgChoices.suggested'));
    c.draft.labourAbsorption = 'PremiumToOvertime';
    expect(c.dirty()).toBeTrue();
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(jasmine.objectContaining({ labourAbsorption: 'PremiumToOvertime' }));
  });

  it('lets only a user who manages the accounts change anything', async () => {
    const page = await render(['manufacturing.costs.view']);
    expect((page.querySelector('#mc-model') as HTMLSelectElement).disabled).toBeTrue();
    expect(page.textContent).toContain(t('mfgChoices.readOnly'));
  });

  it('asks for the burden percentage only once a burden is used, and saves the choices', async () => {
    const page = await render(['accounting.manage', 'manufacturing.costs.view']);
    const c = fixture.componentInstance;
    expect(page.querySelector('#mc-burden')).toBeNull();
    c.draft.employerBurdenUse = 'ReportsOnly';
    c.draft.employerBurdenPercent = 12;
    c.draft.overheadPerLabourHour = 50;
    fixture.detectChanges();
    expect(page.querySelector('#mc-burden')).not.toBeNull();
    expect(c.dirty()).toBeTrue();
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(jasmine.objectContaining({ employerBurdenUse: 'ReportsOnly', employerBurdenPercent: 12, overheadPerLabourHour: 50 }));
    expect(c.saved()).toBeTrue();
    expect(c.dirty()).toBeFalse();
  });

  it('lets the accountant switch stock operations on and confirm the choices in the panel (owner, 2026-11-09)', async () => {
    const page = await render(['accounting.manage', 'manufacturing.costs.view']);
    const c = fixture.componentInstance;
    expect(page.textContent).toContain(t('mfgChoices.approval.not'));
    // The recoverable VAT is asked under the suggested basis; the scrap share only once a recovery is chosen.
    expect(page.querySelector('#mc-vat')).not.toBeNull();
    expect(page.querySelector('#mc-scrap')).toBeNull();
    c.draft.stockOperations = true;
    c.draft.accountantApproved = true;
    c.draft.expenseVatPercent = 19;
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(
      jasmine.objectContaining({ stockOperations: true, accountantApproved: true, expenseVatPercent: 19, scrapRecoveryPercent: 0 }),
    );
  });
});
