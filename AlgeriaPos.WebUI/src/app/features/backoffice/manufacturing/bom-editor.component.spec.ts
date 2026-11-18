import { ComponentFixture, TestBed } from '@angular/core/testing';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { MfgBomEditorComponent } from './bom-editor.component';
import { BomDto, BomRequirements, ManufacturingApiService, MaterialDto, UnitDto } from './manufacturing-api.service';

describe('MfgBomEditorComponent', () => {
  let fixture: ComponentFixture<MfgBomEditorComponent>;
  let api: jasmine.SpyObj<ManufacturingApiService>;

  const unit = (id: string, code: string, dimension: UnitDto['dimension'], factor = 1): UnitDto => ({
    id, code, nameAr: code, nameFr: code, nameEn: code, dimension, factorToReference: factor, isSystem: true, isActive: true,
  });
  const units = [unit('kg', 'kg', 'Mass'), unit('g', 'g', 'Mass', 0.001), unit('m', 'm', 'Length'), unit('ctn', 'ctn', 'Other')];
  const material = (id: string, sku: string, role: MaterialDto['role'], base: string, extra: MaterialDto['units'] = []): MaterialDto => ({
    productId: id, sku, nameAr: sku, nameFr: sku, nameEn: sku, isActive: true, isRentable: true, role,
    soldAtTill: role !== 'RawMaterial', baseUnitId: base, baseUnitCode: base, unit: base, units: extra, alsoForSale: false, specs: [],
  });
  const materials = [
    material('nails', 'NAIL-70', 'Finished', 'kg', [{ unitId: 'ctn', unitCode: 'ctn', factorToBase: 25, forPurchase: false, forProduction: true, forSale: true }]),
    material('wire', 'WIRE-3MM', 'RawMaterial', 'kg'),
  ];

  const bom = (status: BomDto['bom']['status'], canEdit: boolean): BomDto => ({
    bom: {
      id: 'b1', productId: 'nails', productSku: 'NAIL-70', productNameAr: 'مسامير', productNameFr: 'Pointes', productNameEn: 'Nails',
      productionLineId: null, productionLineCode: null, version: 1, status, batchQuantity: 100, batchUnitId: 'kg', batchUnitCode: 'kg',
      lineCount: 1, stageCount: 0, createdAtUtc: '2026-10-25T08:00:00Z', activatedAtUtc: status === 'Draft' ? null : '2026-10-25T09:00:00Z',
      retiredAtUtc: null,
    },
    stageTemplateId: null, notes: null, createdBy: 'admin', activatedBy: status === 'Draft' ? null : 'admin', retiredBy: null,
    lines: [{
      id: 'l1', sequence: 1, componentProductId: 'wire', componentSku: 'WIRE-3MM', componentNameAr: 'سلك', componentNameFr: 'Fil',
      componentNameEn: 'Wire', componentRole: 'RawMaterial', quantity: 105, unitId: 'kg', unitCode: 'kg', wastePercent: 2,
      specification: 'Ø 3 mm', note: null, specs: [],
    }],
    stages: [], canEdit, canApprove: true,
  });

  beforeEach(() => {
    api = jasmine.createSpyObj<ManufacturingApiService>('ManufacturingApiService', [
      'materials', 'units', 'lines', 'stageTemplates', 'jobRoles', 'specFields', 'bom', 'requirements', 'newBomVersion', 'updateBom', 'activateBom',
    ]);
    api.materials.and.resolveTo(materials);
    api.units.and.resolveTo(units);
    api.lines.and.resolveTo([]);
    api.stageTemplates.and.resolveTo([]);
    api.jobRoles.and.resolveTo([]);
    api.specFields.and.resolveTo([]);
    TestBed.configureTestingModule({
      imports: [MfgBomEditorComponent],
      providers: [
        { provide: ManufacturingApiService, useValue: api },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  const el = () => fixture.nativeElement as HTMLElement;
  const t = (key: string) => TestBed.inject(I18nService).t(key);

  async function open(status: BomDto['bom']['status'], canEdit: boolean): Promise<void> {
    api.bom.and.resolveTo(bom(status, canEdit));
    fixture = TestBed.createComponent(MfgBomEditorComponent);
    fixture.componentRef.setInput('id', 'b1');
    fixture.componentRef.setInput('mayWrite', true);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('shows an active version frozen, offering a new version instead of a save', async () => {
    await open('Active', false);
    const buttons = [...el().querySelectorAll('button')].map((b) => b.textContent?.trim());
    expect(buttons).toContain(t('mfg.bom.newVersion'));
    expect(buttons).not.toContain(t('common.save'));
    const quantity = el().querySelector<HTMLInputElement>('#bc-q-0')!;
    expect(quantity.disabled).toBeTrue();
    expect(el().textContent).toContain(t('mfg.bomExplain.Active'));
  });

  it('offers only the units a component converts to', async () => {
    await open('Draft', true);
    const options = [...el().querySelectorAll<HTMLSelectElement>('.component select')[1].options].map((o) => o.textContent?.trim());
    // Wire is counted in kilograms: kg and g convert, metres and cartons do not.
    expect(options).toEqual(['kg', 'g']);
    // The nails come by the carton too, through the factor written on them.
    const batch = [...el().querySelectorAll<HTMLSelectElement>('.pair select')[0].options].map((o) => o.textContent?.trim());
    expect(batch).toEqual(['kg', 'g', 'ctn']);
  });

  it('shows the server\'s requirements for a planned quantity, in each component\'s stock unit', async () => {
    const answer: BomRequirements = {
      bomId: 'b1', plannedQuantity: 2, plannedUnitId: 'ctn', plannedUnitCode: 'ctn', batches: 0.5,
      lines: [{
        bomLineId: 'l1', componentProductId: 'wire', componentSku: 'WIRE-3MM', componentNameAr: 'سلك', componentNameFr: 'Fil',
        componentNameEn: 'Wire', unitId: 'kg', unitCode: 'kg', netQuantity: 52.5, wasteQuantity: 1.05, grossQuantity: 53.55,
        baseUnitId: 'kg', baseUnitCode: 'kg', baseQuantity: 53.55,
      }],
      totals: [{ componentProductId: 'wire', componentSku: 'WIRE-3MM', baseUnitId: 'kg', baseUnitCode: 'kg', baseQuantity: 53.55 }],
    };
    api.requirements.and.resolveTo(answer);
    await open('Active', false);
    const comp = fixture.componentInstance;
    comp.planned = 2;
    comp.plannedUnit = 'ctn';
    await comp.calculate();
    fixture.detectChanges();

    expect(api.requirements).toHaveBeenCalledWith('b1', 2, 'ctn');
    const row = el().querySelectorAll('tbody tr');
    expect(row.length).toBe(1);
    expect(row[0].textContent).toContain('52.5 kg');
    expect(row[0].textContent).toContain('1.05 kg');
    expect(row[0].textContent).toContain('53.55 kg');
  });
});
