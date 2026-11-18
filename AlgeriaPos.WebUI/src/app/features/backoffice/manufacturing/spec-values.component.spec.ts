import { ComponentFixture, TestBed } from '@angular/core/testing';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { SpecFieldDto, UnitDto } from './manufacturing-api.service';
import { MfgSpecValuesComponent, SpecRow, describeSpecs, specRequest } from './spec-values.component';

describe('MfgSpecValuesComponent', () => {
  let fixture: ComponentFixture<MfgSpecValuesComponent>;

  const unit = (id: string, dimension: UnitDto['dimension'], isActive = true): UnitDto => ({
    id, code: id, nameAr: id, nameFr: id, nameEn: id, dimension, factorToReference: 1, isSystem: true, isActive,
  });
  const units = [unit('m', 'Length'), unit('mm', 'Length'), unit('kg', 'Mass'), unit('in', 'Length', false)];
  const field = (id: string, kind: SpecFieldDto['kind'], dimension: SpecFieldDto['dimension'], isActive = true): SpecFieldDto => ({
    id, code: id.toUpperCase(), nameAr: id, nameFr: id, nameEn: id, kind, dimension, isSystem: true, isActive, sortOrder: 0, inUse: false,
  });
  const fields = [field('length', 'Measure', 'Length'), field('grade', 'Text', 'Other'), field('old', 'Measure', 'Mass', false)];

  function render(rows: SpecRow[], disabled = false): void {
    TestBed.configureTestingModule({ imports: [MfgSpecValuesComponent] });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(MfgSpecValuesComponent);
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('fields', fields);
    fixture.componentRef.setInput('units', units);
    fixture.componentRef.setInput('disabled', disabled);
    fixture.detectChanges();
  }

  const el = () => fixture.nativeElement as HTMLElement;

  it('offers a measured field only the active units of its dimension, and a text field a text box', () => {
    render([
      { attributeId: 'length', value: 6, unitId: 'm', text: null },
      { attributeId: 'grade', value: null, unitId: null, text: 'S235' },
    ]);
    const unitOptions = [...el().querySelectorAll<HTMLSelectElement>('.pair select')[0].options].map((o) => o.textContent?.trim());
    // A thickness is never offered in kilograms; a switched-off unit is not offered anew.
    expect(unitOptions).toEqual(['m', 'mm']);
    expect(el().querySelectorAll('.pair').length).toBe(1);
    expect(el().querySelector<HTMLInputElement>('#spec-t-1')).not.toBeNull();
    // A field already used by another row and a switched-off field are not offered.
    const fieldOptions = [...el().querySelectorAll<HTMLSelectElement>('#spec-f-1')[0].options].map((o) => o.value);
    expect(fieldOptions.length).toBe(1);
  });

  it('adds the next free field with a unit that fits it', () => {
    const rows: SpecRow[] = [];
    render(rows);
    fixture.componentInstance.add();
    expect(rows).toEqual([{ attributeId: 'length', value: null, unitId: 'm', text: null }]);
    fixture.componentInstance.add();
    expect(rows[1]).toEqual({ attributeId: 'grade', value: null, unitId: null, text: null });
    // Nothing left that is active and unused.
    expect(fixture.componentInstance.nextField()).toBeUndefined();
  });

  it('sends a figure and unit for a measure, a word for a text field, and drops a row without its field', () => {
    const sent = specRequest(
      [
        { attributeId: 'length', value: '1.2' as unknown as number, unitId: 'm', text: 'stray' },
        { attributeId: 'grade', value: 3, unitId: 'm', text: '  S235 ' },
        { attributeId: '', value: 1, unitId: 'm', text: null },
      ],
      fields,
    );
    expect(sent).toEqual([
      { attributeId: 'length', value: 1.2, unitId: 'm', text: null },
      { attributeId: 'grade', value: null, unitId: null, text: 'S235' },
    ]);
  });

  it('describes the values in one line', () => {
    const line = describeSpecs(
      [
        { attributeId: 'length', attributeCode: 'LENGTH', kind: 'Measure', value: 6, unitId: 'm', unitCode: 'm', text: null },
        { attributeId: 'grade', attributeCode: 'GRADE', kind: 'Text', value: null, unitId: null, unitCode: null, text: 'S235' },
      ],
      fields,
      'fr',
    );
    expect(line).toBe('length 6 m · grade S235');
  });

  it('shows the values read only, with no add or remove', async () => {
    render([{ attributeId: 'length', value: 6, unitId: 'm', text: null }], true);
    // ngModel applies the disabled state after a turn.
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el().querySelector<HTMLInputElement>('#spec-v-0')!.disabled).toBeTrue();
    expect(el().querySelectorAll('button').length).toBe(0);
  });
});
