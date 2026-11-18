import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ApiError } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { LedgerAccount, LedgerService } from '../../../core/ledger.service';
import { canOpen, findPage, NavUser } from '../../../core/nav';
import { MaintenanceApi, MaintenanceSettings } from './maintenance-api.service';
import { MaintenanceAccountSlot, MaintenanceLedgerApi } from './maintenance-ledger-api.service';
import { MaintenanceSettingsComponent } from './maintenance-settings.component';
import { MaintenanceWorkshop, MaintenanceWorkshopApi } from './maintenance-workshop-api.service';
import { ReceivablesApi } from '../receivables/receivables-api.service';

describe('MaintenanceSettingsComponent (maintenance, Phase M0)', () => {
  let fixture: ComponentFixture<MaintenanceSettingsComponent>;
  let api: jasmine.SpyObj<MaintenanceApi>;
  let ledgerApi: jasmine.SpyObj<MaintenanceLedgerApi>;

  const slot = (s: string, shared = false, hintCode: string | null = null): MaintenanceAccountSlot => ({
    slot: s, ledgerAccountId: null, accountCode: null, accountNameAr: null, accountNameFr: null, accountNameEn: null,
    hintCode, shared, updatedAtUtc: null, updatedBy: null,
  });
  /** MS-31 seeded empty: the six maintenance slots, then the stock slots shared with manufacturing. */
  const map: MaintenanceAccountSlot[] = [
    slot('maintenance.own_parts', false, '615'), slot('maintenance.customer_parts', false, '600'), slot('maintenance.labour_absorbed'),
    slot('maintenance.overhead_absorbed'), slot('maintenance.service_cost'), slot('maintenance.wip'),
    slot('stock.raw_material', true), slot('stock.supplies', true), slot('stock.semi_finished', true), slot('stock.finished', true),
  ];
  const chart: LedgerAccount[] = [
    { id: 'a615', code: '615', nameAr: 'صيانة', nameFr: 'Entretien et réparations', nameEn: 'Repairs', type: 'Expense', isSystem: true, isActive: true, sortOrder: 1 },
  ];

  const defaults: MaintenanceSettings = {
    module: 'Off', unitsLeaveMaintenance: 'ByHandOrByJob', jobsFromReturnNotes: 'OfferedInWaitingList',
    bookableEmployees: 'AnyEmployeeOfBranch', jobCloser: 'AnyHolder', customerJobs: 'Off',
    costIncludes: 'PartsLabourOutsideWork', overheadPerLabourHour: 0, labourRate: 'PayrollDerived', workshopRatePerHour: 0,
    burdenAndVat: 'FollowManufacturing', employerBurdenPercent: 0, expenseVatPercent: 0,
    customerPrice: 'Quoted', labourChargePerHour: 0, markupPercent: 0, returnRecharge: 'TypedAtReturn', priceBelowCost: 'ShownAsLoss',
    rechargeSharing: 'ByUnits', guaranteeMonths: 3,
    ownRepairs: 'AlwaysExpense', partsPosting: 'PracticeLedgerByMonth', labourInBooks: 'ReportOnly', customerIncome: 'SameAsRecharges',
    advances: 'NotTaken', openJobsAtPeriodEnd: 'LeftInExpensesListed', rework: 'AddedToOriginalJob', provisionPercent: 0, repairHistory: 'PerTypeWithReference',
    capitaliseAboveAmount: 0, perPieceAboveValue: 0, assetRegisterActive: false,
    postingFromMonth: null, accountantApproved: false, approvedBy: null, approvedAtUtc: null, updatedAtUtc: null, updatedBy: null,
  };

  /** MS-04's list as the server answers it: Alger employs three, one on its list; Oran employs two, nobody on its list. */
  const workshop: MaintenanceWorkshop = {
    bookableEmployees: 'AnyEmployeeOfBranch', members: [], available: [],
    branches: [
      { branchId: 'b1', branchName: 'Alger-Centre', employees: 3, onList: 1 },
      { branchId: 'b2', branchName: 'Oran', employees: 2, onList: 0 },
      { branchId: 'w1', branchName: 'Dépôt', employees: 0, onList: 0 },
    ],
  };
  let workshopApi: jasmine.SpyObj<MaintenanceWorkshopApi>;

  async function render(keys: string[], settings: MaintenanceSettings = defaults, advancesOn419 = false): Promise<HTMLElement> {
    workshopApi = jasmine.createSpyObj<MaintenanceWorkshopApi>('MaintenanceWorkshopApi', ['list']);
    workshopApi.list.and.resolveTo(workshop);
    api = jasmine.createSpyObj<MaintenanceApi>('MaintenanceApi', ['settings', 'saveSettings']);
    api.settings.and.resolveTo(settings);
    api.saveSettings.and.callFake(async (d) => ({ ...settings, ...d, updatedAtUtc: '2026-11-09T10:00:00Z', updatedBy: 'admin' }));
    ledgerApi = jasmine.createSpyObj<MaintenanceLedgerApi>('MaintenanceLedgerApi', ['accountMap', 'saveAccountMap']);
    ledgerApi.accountMap.and.resolveTo(map);
    ledgerApi.saveAccountMap.and.callFake(async (lines) =>
      map.map((m) => {
        const l = lines.find((x) => x.slot === m.slot);
        return l ? { ...m, ledgerAccountId: l.ledgerAccountId, accountCode: '615', accountNameFr: 'Entretien et réparations' } : m;
      }),
    );
    // M7b: where customer advances are held decides whether MS-35 may take them.
    const receivables = jasmine.createSpyObj<ReceivablesApi>('ReceivablesApi', ['settings']);
    receivables.settings.and.resolveTo({
      advancesHeldOn: advancesOn419 ? 'AdvancesAccount' : 'CustomerAccount', advanceVat: 'AtInvoice', advancesAccountReady: true,
      openAdvances: 0, updatedAtUtc: null, updatedBy: null,
    });
    const ledger = jasmine.createSpyObj<LedgerService>('LedgerService', ['listAccounts']);
    ledger.listAccounts.and.resolveTo(chart);
    TestBed.configureTestingModule({
      imports: [MaintenanceSettingsComponent],
      providers: [
        { provide: MaintenanceApi, useValue: api },
        { provide: MaintenanceLedgerApi, useValue: ledgerApi },
        { provide: LedgerService, useValue: ledger },
        { provide: ReceivablesApi, useValue: receivables },
        { provide: MaintenanceWorkshopApi, useValue: workshopApi },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    TestBed.inject(AuthService).currentUser.set({
      id: 'u1', username: 'u', displayName: 'User', role: 'Manager', permissions: [], capabilities: [],
      permissionKeys: keys, scope: 'Company',
    } as never);
    fixture = TestBed.createComponent(MaintenanceSettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const t = (key: string) => TestBed.inject(I18nService).t(key);
  const select = (page: HTMLElement, field: string) => page.querySelector(`#ms-${field}`) as HTMLSelectElement;

  it('says the module is off in words and marks the suggested first option of every choice', async () => {
    const page = await render(['maintenance.settings', 'accounting.manage']);
    expect(page.textContent).toContain(t('maint.state.off'));
    const fields = [
      'module', 'unitsLeaveMaintenance', 'jobsFromReturnNotes', 'bookableEmployees', 'jobCloser', 'customerJobs',
      'costIncludes', 'labourRate', 'burdenAndVat', 'customerPrice', 'returnRecharge', 'priceBelowCost', 'rechargeSharing',
      'ownRepairs', 'partsPosting', 'labourInBooks', 'customerIncome', 'advances', 'openJobsAtPeriodEnd', 'rework', 'repairHistory',
    ];
    for (const field of fields) {
      const s = select(page, field);
      expect(s).withContext(field).not.toBeNull();
      expect(s.options[0].textContent).withContext(field).toContain(t('maint.suggested'));
      expect(s.options[1].textContent).withContext(field).not.toContain(t('maint.suggested'));
      // Every option is said in words, never as its raw key.
      for (const o of Array.from(s.options)) expect(o.textContent).withContext(field).not.toContain('maint.');
    }
    // The first section is open, the others folded until the work reaches them — the account map open while a slot is unmapped.
    const folds = Array.from(page.querySelectorAll('app-fold button[aria-expanded]')).map((b) => b.getAttribute('aria-expanded'));
    expect(folds).toEqual(['true', 'false', 'false', 'false', 'true']);
  });

  it('offers the options kept for a later phase but does not let them be chosen', async () => {
    const page = await render(['maintenance.settings', 'accounting.manage']);
    for (const [field, option] of [['advances', 1]] as const) {
      const o = select(page, field).options[option];
      expect(o.disabled).withContext(field).toBeTrue();
      expect(o.textContent).withContext(field).toContain(t('maint.later'));
    }
    expect(select(page, 'labourInBooks').options[1].disabled).toBeFalse();
    // MS-04: the workshop list exists, so "only the workshop list" is chosen like any option.
    expect(select(page, 'bookableEmployees').options[1].disabled).toBeFalse();
    expect(select(page, 'bookableEmployees').options[1].textContent).not.toContain(t('maint.later'));
    // M6: parts at each issue (MS-32) can be chosen; the server takes it once own repairs' account is mapped.
    expect(select(page, 'partsPosting').options[1].disabled).toBeFalse();
    // M7c: the guarantee provision (MS-37) can be chosen; the server takes it once its two accounts are mapped.
    expect(select(page, 'rework').options[2].disabled).toBeFalse();
    expect(select(page, 'rework').options[2].textContent).not.toContain(t('maint.later'));
    // MS-34: one service account, and parts and labour apart, can be chosen; the server takes them once repair service income is mapped.
    for (const option of [1, 2]) {
      expect(select(page, 'customerIncome').options[option].disabled).withContext(`customerIncome ${option}`).toBeFalse();
      expect(select(page, 'customerIncome').options[option].textContent).withContext(`customerIncome ${option}`).not.toContain(t('maint.later'));
    }
    // M7d: capitalised repairs (MS-30) and per-piece history (MS-38) can be chosen; the server checks the register and accounts.
    for (const field of ['ownRepairs', 'repairHistory']) {
      expect(select(page, field).options[1].disabled).withContext(field).toBeFalse();
      expect(select(page, field).options[1].textContent).withContext(field).not.toContain(t('maint.later'));
    }
  });

  it('says under MS-04’s workshop list which branches would book nobody, and reads the list only while the module is on', async () => {
    const page = await render(['maintenance.settings'], { ...defaults, module: 'On' });
    const c = fixture.componentInstance;
    expect(workshopApi.list).toHaveBeenCalled();
    // The default: nothing said.
    expect(page.textContent).not.toContain(t('maint.workshop.settingsHint'));
    c.draft.bookableEmployees = 'WorkshopListOnly';
    fixture.detectChanges();
    expect(page.textContent).toContain(t('maint.workshop.settingsHint'));
    const warning = page.querySelector('.notice.warn') as HTMLElement;
    // Oran employs people and has nobody on its list; Alger has someone; the depot employs nobody.
    expect(warning.textContent).toContain('Oran');
    expect(warning.textContent).not.toContain('Alger-Centre');
    expect(warning.textContent).not.toContain('Dépôt');
    // Saved all the same.
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(jasmine.objectContaining({ bookableEmployees: 'WorkshopListOnly' }));

    // The module off: the list's route would answer maintenance_off, so it is not asked and nothing is warned.
    TestBed.resetTestingModule();
    const off = await render(['maintenance.settings'], { ...defaults, bookableEmployees: 'WorkshopListOnly' });
    expect(workshopApi.list).not.toHaveBeenCalled();
    expect(off.textContent).toContain(t('maint.workshop.settingsHint'));
    expect(off.querySelector('.notice.warn')).toBeNull();
  });

  it('asks the accountant for MS-30’s amount and MS-38’s value only under their options, and says when the register is off (M7d)', async () => {
    const page = await render(['maintenance.settings', 'accounting.manage']);
    const c = fixture.componentInstance;
    expect(page.querySelector('#ms-capitalise')).toBeNull();
    expect(page.querySelector('#ms-per-piece')).toBeNull();
    c.draft.ownRepairs = 'CapitalisedAboveAmount';
    c.draft.capitaliseAboveAmount = 50000;
    c.draft.repairHistory = 'PerPiece';
    c.draft.perPieceAboveValue = 500000;
    fixture.detectChanges();
    expect(page.querySelector('#ms-capitalise')).not.toBeNull();
    expect(page.querySelector('#ms-per-piece')).not.toBeNull();
    expect(page.textContent).toContain(t('maint.capitalise.registerOff'));
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(jasmine.objectContaining({
      ownRepairs: 'CapitalisedAboveAmount', capitaliseAboveAmount: 50000, repairHistory: 'PerPiece', perPieceAboveValue: 500000,
    }));
    // Back to the defaults, neither figure is sent.
    c.draft.ownRepairs = 'AlwaysExpense';
    c.draft.repairHistory = 'PerTypeWithReference';
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(jasmine.objectContaining({ capitaliseAboveAmount: 0, perPieceAboveValue: 0 }));
    // The register switched over: no warning; the read-only flag never travels back.
    const active = await (async () => {
      TestBed.resetTestingModule();
      return render(['accounting.manage'], { ...defaults, ownRepairs: 'CapitalisedAboveAmount', capitaliseAboveAmount: 50000, assetRegisterActive: true });
    })();
    expect(active.textContent).not.toContain(t('maint.capitalise.registerOff'));
    expect('assetRegisterActive' in fixture.componentInstance.draft).toBeFalse();
  });

  it('keeps the guarantee (MS-27) with the workshop and asks the accountant for the provision rate only under the provision (MS-37)', async () => {
    const page = await render(['maintenance.settings', 'accounting.manage']);
    const c = fixture.componentInstance;
    const guarantee = page.querySelector('#ms-guarantee') as HTMLInputElement;
    expect(guarantee).not.toBeNull();
    expect(page.textContent).toContain(t('maint.guaranteeMonths'));
    expect(page.querySelector('#ms-provision')).toBeNull();
    c.draft.rework = 'Provision';
    c.draft.provisionPercent = 5;
    c.draft.guaranteeMonths = 6;
    fixture.detectChanges();
    expect(page.querySelector('#ms-provision')).not.toBeNull();
    expect(page.textContent).toContain(t('maint.provisionPercent'));
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(jasmine.objectContaining({ rework: 'Provision', provisionPercent: 5, guaranteeMonths: 6 }));
    // Another treatment sends no rate.
    c.draft.rework = 'OwnJob';
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(jasmine.objectContaining({ rework: 'OwnJob', provisionPercent: 0 }));
  });

  it('lets only maintenance.settings change the guarantee, and only accounting.manage the provision rate', async () => {
    const page = await render(['accounting.manage'], { ...defaults, rework: 'Provision', provisionPercent: 5 });
    expect((page.querySelector('#ms-guarantee') as HTMLInputElement).disabled).toBeTrue();
    expect((page.querySelector('#ms-provision') as HTMLInputElement).disabled).toBeFalse();
  });

  it('lets MS-35 take advances once customer advances are held on 419 (M7b)', async () => {
    const page = await render(['maintenance.settings', 'accounting.manage'], defaults, true);
    const taken = select(page, 'advances').options[1];
    expect(taken.disabled).toBeFalse();
    expect(taken.textContent).not.toContain(t('maint.later'));
    // The other options stay as they are (the workshop list, the provision, capitalised repairs and per-piece history are built).
    expect(select(page, 'bookableEmployees').options[1].disabled).toBeFalse();
    expect(select(page, 'rework').options[2].disabled).toBeFalse();
  });

  it('lets accounting.manage map the maintenance slots (MS-31), never the shared stock slots, and saves only what changed', async () => {
    const page = await render(['accounting.manage']);
    const own = page.querySelector('#ms-slot-maintenance\\.own_parts') as HTMLSelectElement;
    expect(own).not.toBeNull();
    // Mapped in Costing settings: shown here, never edited.
    expect(page.querySelector('#ms-slot-stock\\.supplies')).toBeNull();
    expect(page.textContent).toContain(t('maint.map.shared'));
    // The hint is today's account, said as a hint only.
    expect(page.textContent).toContain(TestBed.inject(I18nService).t('maint.map.hint', { code: '615' }));
    expect(page.textContent).toContain(t('maintLedger.slot.maintenance.own_parts'));

    const c = fixture.componentInstance;
    expect(c.mapDirty()).toBeFalse();
    c.mapDraft['maintenance.own_parts'] = 'a615';
    expect(c.mapDirty()).toBeTrue();
    await c.saveMap();
    expect(ledgerApi.saveAccountMap).toHaveBeenCalledWith([{ slot: 'maintenance.own_parts', ledgerAccountId: 'a615' }]);
    expect(c.mapDirty()).toBeFalse();
    expect(c.mapSaved()).toBeTrue();
  });

  it('offers repair service income (MS-34) only the income accounts of class 7, every other slot the whole chart', async () => {
    const page = await render(['accounting.manage']);
    const c = fixture.componentInstance;
    const income: LedgerAccount = { id: 'a7062', code: '7062', nameAr: 'خدمات الإصلاح', nameFr: 'Prestations de réparation', nameEn: 'Repair services', type: 'Income', isSystem: false, isActive: true, sortOrder: 2 };
    c.accounts.set([...chart, income]);
    c.map.set([...map, slot('maintenance.service_income')]);
    fixture.detectChanges();
    const options = (id: string) => Array.from((page.querySelector(`#ms-slot-maintenance\\.${id}`) as HTMLSelectElement).options).map((o) => o.textContent!.trim());
    const offered = options('service_income');
    expect(offered.length).toBe(2);
    expect(offered[0]).toBe(t('mfgChoices.map.unmapped'));
    expect(offered[1]).toMatch(/^7062 — /);
    expect(options('own_parts').length).toBe(3);
    expect(page.textContent).toContain(t('maintLedger.slot.maintenance.service_income'));
  });

  it('shows the account map read only to maintenance.settings', async () => {
    const page = await render(['maintenance.settings']);
    expect(page.querySelector('#ms-slot-maintenance\\.own_parts')).toBeNull();
    expect(page.textContent).toContain(t('mfgChoices.map.unmapped'));
  });

  it('lets maintenance.settings change the operational half only, and saves the module on', async () => {
    const page = await render(['maintenance.settings']);
    expect(select(page, 'module').disabled).toBeFalse();
    expect(select(page, 'customerPrice').disabled).toBeFalse();
    expect(select(page, 'partsPosting').disabled).toBeTrue();
    expect((page.querySelector('#ms-approved') as HTMLInputElement).disabled).toBeTrue();
    expect(page.textContent).toContain(t('maint.accountingReadOnly'));

    const c = fixture.componentInstance;
    expect(c.dirty()).toBeFalse();
    c.draft.module = 'On';
    expect(c.dirty()).toBeTrue();
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(jasmine.objectContaining({ module: 'On', partsPosting: 'PracticeLedgerByMonth' }));
    fixture.detectChanges();
    expect(page.textContent).toContain(t('maint.state.on'));
    expect(c.dirty()).toBeFalse();
  });

  it('lets accounting.manage change the accounting half and confirm, but not the module', async () => {
    const page = await render(['accounting.manage']);
    expect(select(page, 'module').disabled).toBeTrue();
    expect(select(page, 'labourInBooks').disabled).toBeFalse();
    const c = fixture.componentInstance;
    c.draft.labourInBooks = 'MovedIntoJobs';
    c.draft.accountantApproved = true;
    c.postingFrom = '2026-12';
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(
      jasmine.objectContaining({ labourInBooks: 'MovedIntoJobs', accountantApproved: true, postingFromMonth: '2026-12', module: 'Off' }),
    );
  });

  it('asks for an amount only once its option uses one, and sends none it does not use', async () => {
    const page = await render(['maintenance.settings']);
    const c = fixture.componentInstance;
    expect(page.querySelector('#ms-overhead')).toBeNull();
    expect(page.querySelector('#ms-workshop-rate')).toBeNull();
    expect(page.querySelector('#ms-markup')).toBeNull();
    c.draft.costIncludes = 'PartsLabourOutsideWorkOverhead';
    c.draft.overheadPerLabourHour = 60;
    c.draft.labourRate = 'WorkshopRate';
    c.draft.workshopRatePerHour = 450;
    c.draft.customerPrice = 'CostPlus';
    c.draft.markupPercent = 15;
    c.draft.labourChargePerHour = 800; // the itemised price's rate: not used by cost-plus
    fixture.detectChanges();
    expect(page.querySelector('#ms-overhead')).not.toBeNull();
    expect(page.querySelector('#ms-workshop-rate')).not.toBeNull();
    expect(page.querySelector('#ms-markup')).not.toBeNull();
    expect(page.querySelector('#ms-labour-charge')).toBeNull();
    await c.save();
    expect(api.saveSettings).toHaveBeenCalledWith(
      jasmine.objectContaining({ overheadPerLabourHour: 60, workshopRatePerHour: 450, markupPercent: 15, labourChargePerHour: 0 }),
    );
  });

  it('reads only, with no save button, for a reader of maintenance', async () => {
    const page = await render(['maintenance.view']);
    expect(select(page, 'module').disabled).toBeTrue();
    expect(page.querySelector('.ui-actionbar button')).toBeNull();
    expect(page.textContent).toContain(t('maint.readOnly'));
  });

  it('says a refusal of the server in the reader’s words', async () => {
    await render(['maintenance.settings']);
    api.saveSettings.and.rejectWith(new ApiError(JSON.stringify({ detail: 'x', code: 'accounting_manage_required' }), 403));
    const c = fixture.componentInstance;
    c.draft.module = 'On';
    await c.save();
    expect(c.error()).toBe(t('maint.error.accounting'));
  });

  it('is offered in the sidebar to whoever holds maintenance.settings or accounting.manage', () => {
    const user = (keys: string[]): NavUser => ({ role: 'Manager', permissions: [], scope: 'AssignedBranch', permissionKeys: keys });
    expect(findPage('maintenance.settings')?.path).toBe('maintenance/settings');
    expect(canOpen(user(['maintenance.settings']), 'maintenance.settings')).toBeTrue();
    expect(canOpen(user(['accounting.manage']), 'maintenance.settings')).toBeTrue();
    expect(canOpen(user(['maintenance.view']), 'maintenance.settings')).toBeFalse();
    expect(canOpen(user([]), 'maintenance.settings')).toBeFalse();
  });
});
