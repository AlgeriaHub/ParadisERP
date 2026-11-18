import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { PosUser } from '../../../core/models';
import { MfgOrderCostingComponent } from './order-costing.component';
import { ProductionApiService, ProductionCostSheet } from './production-api.service';

describe('MfgOrderCostingComponent', () => {
  let fixture: ComponentFixture<MfgOrderCostingComponent>;
  let production: jasmine.SpyObj<ProductionApiService>;

  const elements = (over: Partial<ProductionCostSheet['actual']> = {}) => ({
    materials: 15750, consumables: 0, accessories: 0, labour: 2000, overhead: 0, expenses: 0, recovery: 0, total: 17750, ...over,
  });

  const sheet = (over: Partial<ProductionCostSheet> = {}): ProductionCostSheet => ({
    orderId: 'o1', orderNumber: 'MO-000001', status: 'Open', baseUnitCode: 'kg', plannedBaseQty: 100, acceptedBaseQty: 40,
    rejectedBaseQty: 0, completed: false,
    estimate: elements({ labour: 600, total: 16350 }), estimatedUnitCost: 163.5,
    actual: elements(), actualUnitCost: 443.75,
    atLineValue: 15750, releasedValue: 8000, workInProgress: 9750, keptInCharges: 0,
    labourHours: 8, standardHours: 2.5, standardHoursForOutput: 1, standardRate: 240, overheadPerLabourHour: 0, perEmployeeMoneyShown: true,
    labourHoursWithheld: false, labourEstimateWithheld: false,
    materials: [{
      id: 'm1', sku: 'WIRE-3', role: 'RawMaterial', baseUnitCode: 'kg', requiredBaseQty: 105, estimatedUnitCost: 150, estimatedValue: 15750,
      netBaseQty: 105, netValue: 15750, issueAverage: 150, consumedValue: 0, scrapValue: 0, atLineBaseQty: 105, atLineValue: 15750,
      standardBaseQty: 42, usageVariance: 9450, priceVariance: 0,
    }],
    labour: [{ employeeId: 'e1', employeeNumber: 'E-001', employeeName: 'Karim Benali', year: 2026, month: 10, hours: 8,
      basis: 'Standard', reason: 'payroll_not_approved', rate: 250, cost: 2000, withoutEmployerSocial: true }],
    releases: [{ id: 'r1', number: 'OR-000001', baseQty: 40, value: 8000, unitValue: 200, carriesRemainder: false }],
    variances: {
      materialUsage: 9450, materialPrice: 0, labourEfficiency: 1680, labourRate: 80, labour: 1760, overhead: 0, expenses: 0, recovery: 0,
      total: 11210,
    },
    spoilage: 0,
    notes: [{ code: 'material_reference_cost', value: null }, { code: 'labour_standard_rate', value: 8 }, { code: 'labour_unvalued', value: 3 }],
    costStatus: 'provisional',
    statuses: {
      materials: 'reference_cost', consumables: 'none', accessories: 'none', labour: 'incomplete', overhead: 'not_set',
      expenses: 'none', recovery: 'none',
    },
    employerSocialMissing: true,
    ...over,
  });

  async function render(capabilities: string[], costingEnabled: boolean, s: ProductionCostSheet = sheet()): Promise<HTMLElement> {
    production = jasmine.createSpyObj<ProductionApiService>('ProductionApiService', ['operationsStatus', 'costing']);
    production.operationsStatus.and.resolveTo({ stockOperationsEnabled: true, costingEnabled });
    production.costing.and.resolveTo(s);
    TestBed.configureTestingModule({
      imports: [MfgOrderCostingComponent],
      providers: [{ provide: ProductionApiService, useValue: production }],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    TestBed.inject(AuthService).currentUser.set({
      id: 'u-1', username: 'chef', fullName: 'Chef', role: 'Manager', pinHash: '', isActive: true, version: 1, capabilities,
    } as unknown as PosUser);
    fixture = TestBed.createComponent(MfgOrderCostingComponent);
    fixture.componentRef.setInput('orderId', 'o1');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const t = (key: string, params?: Record<string, string | number>) => TestBed.inject(I18nService).t(key, params);

  it('shows nothing and asks nothing without manufacturing.costs.view', async () => {
    const page = await render(['manufacturing.view'], true);
    expect(production.costing).not.toHaveBeenCalled();
    expect(page.textContent!.trim()).toBe('');
  });

  it('shows nothing when this server does not compute costs', async () => {
    const page = await render(['manufacturing.costs.view'], false);
    expect(production.costing).not.toHaveBeenCalled();
    expect(page.querySelector('section')).toBeNull();
  });

  it('shows the sheet with each figure that is not final said in words, the unvalued hours as a warning', async () => {
    const page = await render(['manufacturing.costs.view'], true);
    const text = page.textContent!;
    expect(production.costing).toHaveBeenCalledWith('o1');
    expect(text).toContain(t('mfg.cost.title'));
    expect(text).toContain(t('mfg.cost.actualUnit.provisional', { unit: 'kg' }));
    expect(text).toContain(t('mfg.cost.toDate.provisional'));
    expect(text).toContain(t('mfg.cost.status.provisional'));
    expect(text).not.toContain(t('mfg.cost.status.approved'));
    expect(text).toContain('OR-000001');
    expect(text).toContain('Karim Benali');
    expect(text).toContain(t('mfg.cost.basisWord.Standard'));
    expect(text).toContain(t('mfg.cost.reason.payroll_not_approved'));
    expect(text).toContain(t('mfg.cost.note.material_reference_cost'));
    expect(text).toContain(t('mfg.cost.var.total'));
    expect(text).toContain(t('mfg.cost.unfavourable'));
    const warning = page.querySelector('.notice.warn')!;
    expect(warning.textContent).toContain(t('mfg.cost.note.labour_unvalued', { value: '3' }));
    // Rates and pay shown to this reader: the columns are there.
    expect(text).toContain(t('mfg.cost.rate'));
    expect(text).not.toContain(t('mfg.cost.moneyHidden'));
  });

  it('says what each element rests on, and words an overhead with no rate instead of a zero', async () => {
    const page = await render(['manufacturing.costs.view'], true);
    const rows = Array.from(page.querySelectorAll('tbody tr')).map((r) => r.textContent!.replace(/\s+/g, ' '));
    const overhead = rows.find((r) => r.includes(t('mfg.cost.el.overhead')))!;
    expect(overhead).toContain(t('mfg.cost.elementStatus.not_set'));
    expect(overhead).not.toMatch(/\d/);
    const materials = rows.find((r) => r.includes(t('mfg.cost.el.materials')))!;
    expect(materials).toContain(t('mfg.cost.elementStatus.reference_cost'));
    const labour = rows.find((r) => r.includes(t('mfg.cost.el.labour')))!;
    expect(labour).toContain(t('mfg.cost.elementStatus.incomplete'));
    expect(labour).toContain(t('mfg.cost.withoutEmployerSocial'));
  });

  it('never calls a completed order\'s cost approved while an input is provisional', async () => {
    const provisional = await render(['manufacturing.costs.view'], true, sheet({ completed: true, status: 'Completed' }));
    expect(provisional.textContent).toContain(t('mfg.cost.completed.provisional'));
    expect(provisional.textContent).not.toContain(t('mfg.cost.completed.approved'));
    TestBed.resetTestingModule();
    const approved = await render(['manufacturing.costs.view'], true, sheet({
      completed: true, status: 'Completed', costStatus: 'approved', employerSocialMissing: false,
      notes: [{ code: 'cost_not_frozen', value: null }],
      statuses: {
        materials: 'approved', consumables: 'none', accessories: 'none', labour: 'approved', overhead: 'approved',
        expenses: 'none', recovery: 'none',
      },
      labour: [{ ...sheet().labour[0], basis: 'Actual', reason: null, withoutEmployerSocial: false }],
    }));
    expect(approved.textContent).toContain(t('mfg.cost.completed.approved'));
    expect(approved.textContent).toContain(t('mfg.cost.note.cost_not_frozen'));
    expect(approved.textContent).not.toContain(t('mfg.cost.withoutEmployerSocial'));
  });

  it('leaves out each employee\'s rate and pay when the server did, and says why', async () => {
    const hidden = sheet({
      perEmployeeMoneyShown: false, standardRate: null,
      labour: [{ ...sheet().labour[0], rate: null, cost: null }],
    });
    const page = await render(['manufacturing.costs.view'], true, hidden);
    const text = page.textContent!;
    expect(text).toContain(t('mfg.cost.moneyHidden'));
    expect(text).not.toContain(t('mfg.cost.rate'));
    expect(text).toContain('Karim Benali');
  });

  it('says in words what divides back to one person\'s rate when the server withheld it', async () => {
    const withheld = sheet({
      perEmployeeMoneyShown: false, standardRate: null, labourHours: null, labourHoursWithheld: true, labourEstimateWithheld: true,
      estimate: { ...elements({ labour: 600, total: 16350 }), labour: null, total: null }, estimatedUnitCost: null,
      labour: [{ ...sheet().labour[0], hours: null, rate: null, cost: null }],
      variances: { ...sheet().variances!, labourEfficiency: null, labourRate: null, labour: null, total: null },
      notes: [{ code: 'labour_standard_rate', value: null }, { code: 'labour_unvalued', value: null }],
    });
    const page = await render(['manufacturing.costs.view'], true, withheld);
    const text = page.textContent!.replace(/\s+/g, ' ');
    expect(text).toContain(t('mfg.cost.hoursWithheld'));
    expect(text).toContain(t('mfg.cost.estimateWithheld'));
    const rows = Array.from(page.querySelectorAll('tbody tr')).map((r) => r.textContent!.replace(/\s+/g, ' '));
    const karim = rows.find((r) => r.includes('Karim Benali'))!;
    expect(karim).toContain(t('mfg.cost.withheld'));
    expect(karim).not.toMatch(/\b8\b/);
    const estimateLabour = rows.find((r) => r.includes(t('mfg.cost.el.labour')))!;
    expect(estimateLabour).toContain(t('mfg.cost.withheld'));
    // Labour's variance comes as one line, and that line and the total read withheld, never a zero.
    expect(text).toContain(t('mfg.cost.var.labour'));
    expect(text).not.toContain(t('mfg.cost.var.labourEfficiency'));
    const total = rows.find((r) => r.includes(t('mfg.cost.var.total')))!;
    expect(total).toContain(t('mfg.cost.withheld'));
    // The hour notes keep their word without the figure.
    expect(text).toContain(t('mfg.cost.note.labour_standard_rate_withheld'));
    expect(page.querySelector('.notice.warn')!.textContent).toContain(t('mfg.cost.note.labour_unvalued_withheld'));
  });

  it('without pay access says once why, and reads every figure that holds labour withheld, never a blank or a zero', async () => {
    const base = sheet();
    const withheld = sheet({
      perEmployeeMoneyShown: false, standardRate: null, labourEstimateWithheld: true, orderMoneyWithheld: true,
      estimate: { ...base.estimate, labour: null, total: null }, estimatedUnitCost: null,
      actual: { ...base.actual, labour: null, total: null }, actualUnitCost: null,
      releasedValue: null, workInProgress: null, keptInCharges: null, spoilage: null,
      releases: base.releases.map((r) => ({ ...r, value: null, unitValue: null })),
      labour: [{ ...base.labour[0], rate: null, cost: null }],
      variances: { ...base.variances!, labourEfficiency: null, labourRate: null, labour: null, total: null },
    });
    const page = await render(['manufacturing.costs.view'], true, withheld);
    const text = page.textContent!.replace(/\s+/g, ' ');
    // One notice says why; the narrower estimate hint does not repeat it.
    expect(text).toContain(t('mfg.cost.orderMoneyWithheld'));
    expect(text).not.toContain(t('mfg.cost.estimateWithheld'));
    const metrics = Array.from(page.querySelectorAll('.ui-metric__value')).map((m) => m.textContent!.trim());
    expect(metrics.filter((m) => m === t('mfg.cost.withheld')).length).toBeGreaterThanOrEqual(3);
    const rows = Array.from(page.querySelectorAll('tbody tr')).map((r) => r.textContent!.replace(/\s+/g, ' '));
    expect(rows.find((r) => r.includes(t('mfg.cost.total')))).toContain(t('mfg.cost.withheld'));
    // The materials stay, at their value.
    expect(rows.find((r) => r.includes(t('mfg.cost.el.materials')))).toContain(TestBed.inject(I18nService).money(base.actual.materials).replace(/\s+/g, ' '));
  });

  it('shows the labour variance split only when the server sent it', async () => {
    const together = sheet({ variances: { ...sheet().variances!, labourEfficiency: null, labourRate: null } });
    const page = await render(['manufacturing.costs.view'], true, together);
    // Raw text: the money format's spaces are no-break spaces a whitespace collapse would change.
    const rows = Array.from(page.querySelectorAll('tbody tr')).map((r) => r.textContent!);
    expect(rows.find((r) => r.includes(t('mfg.cost.var.labour')))).toContain(TestBed.inject(I18nService).money(1760));
    expect(page.textContent).not.toContain(t('mfg.cost.var.labourRate'));
  });

  it('reads the sheet again when the order page says something changed', async () => {
    await render(['manufacturing.costs.view'], true);
    fixture.componentRef.setInput('version', 1);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(production.costing).toHaveBeenCalledTimes(2);
    expect(production.operationsStatus).toHaveBeenCalledTimes(1);
  });
});
