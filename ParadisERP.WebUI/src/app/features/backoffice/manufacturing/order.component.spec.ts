import { ComponentFixture, TestBed } from '@angular/core/testing';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { ManufacturingApiService } from './manufacturing-api.service';
import { MfgOrderComponent } from './order.component';
import { ProductionApiService, ProductionOrder } from './production-api.service';

describe('MfgOrderComponent', () => {
  let fixture: ComponentFixture<MfgOrderComponent>;
  let production: jasmine.SpyObj<ProductionApiService>;

  const order = (over: Partial<ProductionOrder> = {}, status: ProductionOrder['order']['status'] = 'Open'): ProductionOrder => ({
    order: {
      id: 'o1', number: 'MO-000001', lineId: 'l1', lineCode: 'LA', productId: 'p1', productSku: 'NAIL-70', productNameAr: 'مسامير',
      productNameFr: 'Pointes', productNameEn: 'Nails', status, plannedBaseQty: 100, baseUnitCode: 'kg', acceptedBaseQty: 60,
      rejectedBaseQty: 2, plannedStart: '2026-10-19', plannedEnd: '2026-10-21', actualEnd: null, overdueDays: 5, late: true,
      startedShort: false, createdAtUtc: '2026-10-19T08:00:00Z',
    },
    bomId: 'b1', bomVersion: 2, plannedQty: 100, plannedUnitCode: 'kg', batches: 1, shortageReason: null, notes: null,
    estimatedMaterialCost: 15750, producedBaseQty: 98, awaitingReleaseBaseQty: 38, remainingBaseQty: 40,
    createdBy: 'admin', completionRequestedBy: null, completionRequestedAtUtc: null, completedBy: null, completedAtUtc: null,
    materials: [{
      id: 'm1', sequence: 1, productId: 'w', sku: 'WIRE-3', nameAr: 'سلك', nameFr: 'Fil', nameEn: 'Wire', role: 'RawMaterial',
      baseUnitId: 'kg', baseUnitCode: 'kg', netBaseQty: 100, wasteBaseQty: 5, requiredBaseQty: 105, estimatedUnitCost: 150,
      specification: 'Ø 3 mm', issuedBaseQty: 105, returnedBaseQty: 0, consumedBaseQty: 90, scrappedBaseQty: 0,
      reservationReleasedBaseQty: 0, reservedBaseQty: 0, atLineBaseQty: 15,
    }],
    stages: [
      { id: 's1', sequence: 1, nameAr: 'القص', nameFr: 'Coupe', nameEn: 'Cut', plannedMinutes: 60, expectedWorkers: 2, isQualityCheckpoint: false, isOutput: false, goodBaseQty: 100, rejectedBaseQty: 0, bookedHours: 5 },
      { id: 's2', sequence: 2, nameAr: 'التشكيل', nameFr: 'Frappe', nameEn: 'Form', plannedMinutes: 30, expectedWorkers: 1, isQualityCheckpoint: false, isOutput: true, goodBaseQty: 98, rejectedBaseQty: 2, bookedHours: 3 },
    ],
    movements: [{ id: 'mv1', number: 'MI-000001', kind: 'Issue', postingState: 'Pending', note: null, userName: 'admin', atUtc: '2026-10-19T09:00:00Z',
      lines: [{ orderMaterialId: 'm1', productId: 'w', sku: 'WIRE-3', enteredQty: 105, unitCode: 'kg', baseQty: 105, unitCostSnapshot: 150 }] }],
    consumptions: [], releases: [], expenses: [], events: [{ kind: 'Created', detail: '2', userName: 'admin', atUtc: '2026-10-19T08:00:00Z' }],
    canManage: true, canMoveMaterials: true, canRelease: true, canRequestCompletion: true, canApproveCompletion: false,
    ...over,
  });

  async function render(o: ProductionOrder): Promise<void> {
    production = jasmine.createSpyObj<ProductionApiService>('ProductionApiService', ['order', 'costInputs', 'release']);
    production.order.and.resolveTo(o);
    production.costInputs.and.resolveTo({
      orderId: 'o1', number: 'MO-000001', plannedBaseQty: 100, acceptedBaseQty: 60, rejectedBaseQty: 2, estimatedMaterialCost: 15750,
      materials: [], labour: [], labourHours: 8, expenses: [], expensesTotal: 0,
    });
    const api = jasmine.createSpyObj<ManufacturingApiService>('ManufacturingApiService', ['units']);
    api.units.and.resolveTo([]);
    TestBed.configureTestingModule({
      imports: [MfgOrderComponent],
      providers: [
        { provide: ProductionApiService, useValue: production },
        { provide: ManufacturingApiService, useValue: api },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(MfgOrderComponent);
    fixture.componentRef.setInput('id', 'o1');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const t = (key: string) => TestBed.inject(I18nService).t(key);
  const buttons = () => [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].map((b) => b.textContent?.trim());

  it('offers what the server allows this reader, says the order is late, and that its movements are not posted', async () => {
    await render(order());
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(buttons()).toContain(t('mfg.orders.requestCompletion'));
    expect(buttons()).toContain(t('mfg.orders.release'));
    expect(buttons()).toContain(t('mfg.orders.hold'));
    expect(buttons()).not.toContain(t('mfg.orders.approveCompletion'));
    expect(text).toContain(t('mfg.orders.notPostedNote'));
    expect(text).toContain('MI-000001');
    expect(text).toContain('Ø 3 mm');
  });

  it('offers approval only when the server says this reader may approve, and nothing to a reader with no rights', async () => {
    await render(order({ canManage: false, canMoveMaterials: false, canRelease: false, canRequestCompletion: false, canApproveCompletion: true }, 'CompletionRequested'));
    expect(buttons()).toContain(t('mfg.orders.approveCompletion'));
    expect(buttons()).toContain(t('mfg.orders.refuseCompletion'));
    expect(buttons()).not.toContain(t('mfg.orders.cancel'));
    expect(buttons()).not.toContain(t('mfg.orders.release'));
  });

  it('proposes the awaiting quantity when releasing output', async () => {
    await render(order());
    fixture.componentInstance.askQty('Release', null);
    expect(fixture.componentInstance.qtyValue).toBe(38);
  });
});
