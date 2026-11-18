import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { MfgCostReconciliationComponent } from './cost-reconciliation.component';
import { ManufacturingApiService } from './manufacturing-api.service';
import { CostHistoryReport, LateCostReport, ProductionApiService } from './production-api.service';

describe('MfgCostReconciliationComponent', () => {
  let fixture: ComponentFixture<MfgCostReconciliationComponent>;
  let production: jasmine.SpyObj<ProductionApiService>;
  let router: jasmine.SpyObj<Router>;
  let params: BehaviorSubject<ReturnType<typeof convertToParamMap>>;

  const period = { from: '2026-10-01', to: '2026-10-29', basis: 'completed', asOf: '2026-10-29' };
  const figures = { original: 100, lateRecorded: 30, lateRecognized: 10, lateUnclassified: 5, lateUncertain: 15, adjustedProvisional: 110,
    financiallyFinalized: null, finalizationState: 'not_implemented', originalUnitCost: 1 };
  const history: CostHistoryReport = {
    period, page: { page: 1, pageSize: 50, total: 2 }, liveIncluded: false, limitations: ['provisional_only'],
    totals: { frozen: 1, notFrozen: 1, reconstructed: 0, incompleteLegacy: 0, integrityFailures: 0, original: 100, lateRecorded: 30,
      lateRecognized: 10, lateUnclassified: 5, lateUncertain: 15, adjustedProvisional: 110 },
    rows: [
      { orderId: 'o1', orderNumber: 'MO-1', lineCode: 'LA', productSku: 'NAIL', state: 'frozen', acceptedBaseQty: 98, rejectedBaseQty: 2, lateCount: 3,
        integrity: 'pass', gapCodes: [], live: null, figures,
        sheet: { sheetId: 's1', kind: 'created', sourceState: 'complete', calculationVersion: 'B1.0', status: 'provisional',
          frozenAtUtc: '2026-10-29T09:00:00Z', frozenBy: 'Admin', completedAtUtc: '2026-10-29T08:00:00Z', freezeLagDays: 0, sourceHash: 'AB' } },
      { orderId: 'o2', orderNumber: 'MO-2', lineCode: 'LA', productSku: 'NAIL', state: 'not_frozen', acceptedBaseQty: 0, rejectedBaseQty: 0, lateCount: 0,
        integrity: null, gapCodes: ['not_frozen'], live: null, figures: null, sheet: null },
    ],
  };
  const late: LateCostReport = {
    period, page: { page: 1, pageSize: 50, total: 1 }, byElement: [], recorded: 30, recognized: 10, unclassified: 5, uncertain: 15, limitations: [],
    rows: [{ id: 'x', orderId: 'o1', orderNumber: 'MO-1', lineCode: 'LA', kind: 'labour', element: 'labour', treatment: 'recognized', recordedAmount: 10,
      recognizedEffect: 10, unclassifiedEffect: 0, uncertainEffect: 0, sourceReference: null, reason: null, redacted: true,
      recordedAtUtc: '2026-10-29T10:00:00Z', flags: [] }],
  };

  async function render(query: Record<string, string>): Promise<HTMLElement> {
    production = jasmine.createSpyObj<ProductionApiService>('ProductionApiService',
      ['costHistory', 'lateCosts', 'costGaps', 'costVariances', 'scrapCosts', 'releaseReconciliation', 'exportCostRegister']);
    production.costHistory.and.resolveTo(history);
    production.lateCosts.and.resolveTo(late);
    production.costGaps.and.resolveTo({ period, rows: [], page: { page: 1, pageSize: 1, total: 0 }, countByCode: {}, countBySeverity: { missing: 1 }, limitations: [] });
    production.exportCostRegister.and.resolveTo(new Blob(['x']));
    const api = jasmine.createSpyObj<ManufacturingApiService>('ManufacturingApiService', ['lines']);
    api.lines.and.resolveTo([]);
    router = jasmine.createSpyObj<Router>('Router', ['navigate']);
    router.navigate.and.resolveTo(true);
    params = new BehaviorSubject(convertToParamMap(query));
    TestBed.configureTestingModule({
      imports: [MfgCostReconciliationComponent],
      providers: [
        { provide: ProductionApiService, useValue: production },
        { provide: ManufacturingApiService, useValue: api },
        { provide: Router, useValue: router },
        { provide: ActivatedRoute, useValue: { queryParamMap: params.asObservable(), snapshot: { queryParamMap: convertToParamMap(query) } } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(MfgCostReconciliationComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const t = (key: string, params?: Record<string, string | number>) => TestBed.inject(I18nService).t(key, params);

  it('opens on the overview with the four figures apart and finalized as words', async () => {
    const page = await render({});
    expect(page.querySelectorAll('[role="tab"]').length).toBe(6);
    const text = page.textContent!;
    expect(text).toContain(t('mfg.b2.fig.adjusted'));
    expect(text).toContain(t('mfg.b2.finalized.short'));
    expect(text).toContain(t('mfg.b2.finalized.why'));
    expect(text).toContain(t('mfg.b2.count.notFrozen'));
    expect(production.costHistory).toHaveBeenCalledWith(jasmine.objectContaining({ pageSize: 1 }));
  });

  it('lists a completed order with no snapshot as missing, never as a zero cost', async () => {
    const page = await render({ tab: 'history' });
    const rows = Array.from(page.querySelectorAll('tbody tr'));
    expect(rows.length).toBe(2);
    expect(rows[1].textContent).toContain(t('mfg.b2.state.not_frozen'));
    expect(rows[1].textContent).toContain(t('mfg.b2.noSnapshot'));
    expect(rows[1].textContent).not.toContain('0.000');
  });

  it('says a withheld late cost is withheld', async () => {
    const page = await render({ tab: 'late' });
    expect(page.textContent).toContain(t('mfg.b2.withheld'));
  });

  it('asks the live comparison only on the reconciliation tab, a small page at a time', async () => {
    await render({ tab: 'reconciliation' });
    // Ten rows to begin with (owner, 2026-11-17); the reader may pick up to the server's live cap of fifty.
    expect(production.costHistory).toHaveBeenCalledWith(jasmine.objectContaining({ live: true, state: 'frozen', pageSize: 10 }));
  });

  it('pushes a tab change and replaces a filter change, so Back returns to the previous tab', async () => {
    await render({ tab: 'history' });
    fixture.componentInstance.go('late');
    expect(router.navigate.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ replaceUrl: false, queryParamsHandling: 'merge' }));
    fixture.componentInstance.set({ line: 'l1' });
    expect(router.navigate.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ replaceUrl: true }));
  });

  it('exports the tab through its own route with the reader\'s language', async () => {
    await render({ tab: 'late' });
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    await fixture.componentInstance.export();
    expect(production.exportCostRegister).toHaveBeenCalledWith('late-costs', jasmine.any(Object), TestBed.inject(I18nService).lang());
  });
});
