import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { PosUser } from '../../../core/models';
import { MfgOrderFrozenCostComponent } from './order-frozen-cost.component';
import { OrderCostReconciliation, ProductionApiService } from './production-api.service';

describe('MfgOrderFrozenCostComponent', () => {
  let fixture: ComponentFixture<MfgOrderFrozenCostComponent>;
  let production: jasmine.SpyObj<ProductionApiService>;

  const report = (over: Partial<OrderCostReconciliation> = {}): OrderCostReconciliation => ({
    orderId: 'o1', orderNumber: 'MO-000007', orderStatus: 'Completed', lineId: 'l1', lineCode: 'LA', productSku: 'NAIL-70', baseUnitCode: 'kg',
    state: 'frozen',
    sheet: { sheetId: 's1', kind: 'reconstructed', sourceState: 'complete', calculationVersion: 'B1.0', status: 'provisional',
      frozenAtUtc: '2026-10-29T09:00:00Z', frozenBy: 'Admin', completedAtUtc: '2026-10-20T09:00:00Z', freezeLagDays: 9, sourceHash: 'AB' },
    asOf: '2026-10-29', acceptedBaseQty: 98, rejectedBaseQty: 2,
    figures: { original: 19023.077, lateRecorded: 260, lateRecognized: -30, lateUnclassified: 50, lateUncertain: 240,
      adjustedProvisional: 18993.077, financiallyFinalized: null, finalizationState: 'not_implemented', originalUnitCost: 194.112 },
    elements: [
      { element: 'materials', status: 'reference_cost', original: 15600, lateRecognized: 0, adjusted: 15600 },
      { element: 'labour', status: 'provisional', original: 2923.077, lateRecognized: -30, adjusted: 2893.077 },
      { element: 'expenses', status: 'provisional', original: 500, lateRecognized: 0, adjusted: 500 },
    ],
    roundingResidual: 0,
    lateCosts: [
      { id: 'a', orderId: 'o1', orderNumber: 'MO-000007', lineCode: 'LA', kind: 'labour', element: 'labour', treatment: 'recognized', recordedAmount: -30,
        recognizedEffect: -30, unclassifiedEffect: 0, uncertainEffect: 0, sourceReference: null, reason: null, redacted: true,
        recordedAtUtc: '2026-10-29T10:00:00Z', flags: [] },
      { id: 'b', orderId: 'o1', orderNumber: 'MO-000007', lineCode: 'LA', kind: 'expense', element: 'expenses', treatment: 'uncertain', recordedAmount: 120,
        recognizedEffect: 0, unclassifiedEffect: 0, uncertainEffect: 120, sourceReference: 'PV-900', reason: 'Freight', redacted: false,
        recordedAtUtc: '2026-10-29T10:00:00Z', flags: ['duplicate_source'] },
    ],
    releases: { rows: [], acceptedQty: 98, releasedQty: 98, unreleasedAcceptedQty: 0, releasedValue: 19023.077, keptInCharges: 0, lateNotAllocated: -30, flags: [] },
    scrap: { acceptedQty: 98, rejectedQty: 2, rejectionRatePercent: 2, materialScrapValue: 450, recoveryValue: 0, recoveryStatus: 'not_recorded',
      spoilageAbsorbed: 380.462, materialWastage: 300, unavailable: null },
    variances: { available: false, reason: 'source_not_captured', lines: [] },
    labour: { perEmployeeMoneyShown: false, hoursWithheld: true, employees: 1,
      rows: [{ employeeNumber: 'E-001', employeeName: 'Karim Benali', year: 2026, month: 10, hours: null, basis: 'provisional', reason: null, rate: null, cost: null }] },
    checks: [{ code: 'source_hash', result: 'fail', expected: null, actual: null, detail: 'hash_mismatch' },
      { code: 'release_sum', result: 'pass', expected: 1, actual: 1, detail: null }],
    gaps: [{ code: 'reconstructed', severity: 'info', element: null, value: 9, detail: null }],
    limitations: ['provisional_only'],
    live: null,
    ...over,
  });

  /** A reader with these keys; with `pay` (the default) also the rates key and payroll.manage, which the history needs. */
  async function render(capabilities: string[], r: OrderCostReconciliation = report(), pay = true): Promise<HTMLElement> {
    production = jasmine.createSpyObj<ProductionApiService>('ProductionApiService', ['operationsStatus', 'costReconciliation', 'exportOrderCost']);
    production.operationsStatus.and.resolveTo({ stockOperationsEnabled: true, costingEnabled: true });
    production.costReconciliation.and.resolveTo(r);
    production.exportOrderCost.and.resolveTo(new Blob(['x']));
    TestBed.configureTestingModule({ imports: [MfgOrderFrozenCostComponent], providers: [{ provide: ProductionApiService, useValue: production }] });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    TestBed.inject(AuthService).currentUser.set({
      id: 'u-1', username: 'chef', fullName: 'Chef', role: 'Manager', pinHash: '', isActive: true, version: 1,
      capabilities: pay ? [...capabilities, 'manufacturing.costs.rates.view'] : capabilities,
      permissions: pay ? ['payroll.manage'] : [],
    } as unknown as PosUser);
    fixture = TestBed.createComponent(MfgOrderFrozenCostComponent);
    fixture.componentRef.setInput('orderId', 'o1');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const t = (key: string, params?: Record<string, string | number>) => TestBed.inject(I18nService).t(key, params);

  it('asks nothing without manufacturing.costs.view', async () => {
    const page = await render(['manufacturing.view']);
    expect(production.costReconciliation).not.toHaveBeenCalled();
    expect(page.textContent!.trim()).toBe('');
  });

  it('asks nothing of a reader who sees costs but not pay, and says the frozen cost is theirs who see pay rates', async () => {
    const page = await render(['manufacturing.costs.view'], report(), false);
    expect(production.costReconciliation).not.toHaveBeenCalled();
    expect(page.textContent).toContain(t('mfg.b2.payWithheld'));
    expect(page.querySelector('section')).toBeNull();
  });

  it('keeps the original, the late effects and the adjusted total apart, and finalized is words, never a number', async () => {
    const page = await render(['manufacturing.costs.view']);
    const text = page.textContent!;
    expect(text).toContain(t('mfg.b2.fig.original'));
    expect(text).toContain(t('mfg.b2.fig.adjusted'));
    expect(text).toContain(t('mfg.b2.fig.notIncluded'));
    expect(text).toContain(t('mfg.b2.finalized.notAvailable'));
    expect(text).toContain(t('mfg.b2.kind.reconstructed'));
    expect(text).toContain(t('mfg.b2.order.lag', { days: 9 }));
    expect(text).toContain(t('mfg.b2.check.result.fail'));
  });

  it('says what is withheld instead of showing payroll-derived details', async () => {
    const page = await render(['manufacturing.costs.view']);
    const text = page.textContent!;
    expect(text).toContain(t('mfg.b2.withheld'));
    expect(text).toContain(t('mfg.b2.labour.hoursWithheld'));
    expect(text).toContain('PV-900');
    expect(text).toContain(t('mfg.b2.flag.duplicate_source'));
  });

  it('says a completed order with no snapshot is missing, not zero', async () => {
    const page = await render(['manufacturing.costs.view'], report({ state: 'not_frozen', sheet: null, figures: null, elements: [], lateCosts: [],
      releases: null, scrap: null, variances: null, labour: null, checks: [], gaps: [{ code: 'not_frozen', severity: 'missing', element: null, value: null, detail: null }] }));
    expect(page.textContent).toContain(t('mfg.b2.order.notFrozen'));
    expect(page.textContent).toContain(t('mfg.b2.state.not_frozen'));
  });

  it('draws nothing for an order that is not completed', async () => {
    const page = await render(['manufacturing.costs.view'], report({ state: 'not_completed', sheet: null, figures: null }));
    expect(page.querySelector('section')).toBeNull();
  });

  it('asks the live comparison and the as-of date of the server, and exports through the same route', async () => {
    await render(['manufacturing.costs.view']);
    fixture.componentInstance.live.set(true);
    fixture.componentInstance.asOf.set('2026-10-25');
    await fixture.componentInstance.reload();
    expect(production.costReconciliation).toHaveBeenCalledWith('o1', '2026-10-25', true);
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    await fixture.componentInstance.export();
    expect(production.exportOrderCost).toHaveBeenCalledWith('o1', '2026-10-25', true, TestBed.inject(I18nService).lang());
  });
});
