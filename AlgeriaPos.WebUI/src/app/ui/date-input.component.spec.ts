import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DateInputComponent, isoToDisplayDate, parseDisplayDate } from './date-input.component';
import { I18nService } from '../core/i18n.service';

describe('DateInputComponent and date helpers', () => {
  describe('isoToDisplayDate', () => {
    it('formats ISO yyyy-MM-dd to dd/MM/yyyy', () => {
      expect(isoToDisplayDate('2026-10-15')).toBe('15/10/2026');
      expect(isoToDisplayDate('2026-05-04')).toBe('04/05/2026');
      expect(isoToDisplayDate('2026-01-01T12:00:00Z')).toBe('01/01/2026');
    });

    it('returns empty string for null, undefined or empty value', () => {
      expect(isoToDisplayDate(null)).toBe('');
      expect(isoToDisplayDate(undefined)).toBe('');
      expect(isoToDisplayDate('')).toBe('');
    });
  });

  describe('parseDisplayDate', () => {
    it('parses dd/MM/yyyy to ISO yyyy-MM-dd', () => {
      expect(parseDisplayDate('15/10/2026')).toBe('2026-10-15');
      expect(parseDisplayDate('04/05/2026')).toBe('2026-05-04');
    });

    it('parses single-digit day and month', () => {
      expect(parseDisplayDate('4/5/2026')).toBe('2026-05-04');
      expect(parseDisplayDate('9/12/2026')).toBe('2026-12-09');
    });

    it('parses dashed dd-MM-yyyy and ddmmyyyy', () => {
      expect(parseDisplayDate('15-10-2026')).toBe('2026-10-15');
      expect(parseDisplayDate('15102026')).toBe('2026-10-15');
    });

    it('preserves valid ISO string yyyy-MM-dd', () => {
      expect(parseDisplayDate('2026-10-15')).toBe('2026-10-15');
    });

    it('rejects invalid calendar dates', () => {
      expect(parseDisplayDate('31/02/2026')).toBeNull();
      expect(parseDisplayDate('32/01/2026')).toBeNull();
      expect(parseDisplayDate('15/13/2026')).toBeNull();
      expect(parseDisplayDate('invalid text')).toBeNull();
    });

    it('returns empty string for empty input', () => {
      expect(parseDisplayDate('')).toBe('');
      expect(parseDisplayDate('   ')).toBe('');
      expect(parseDisplayDate(null)).toBe('');
    });
  });

  describe('component behavior', () => {
    let component: DateInputComponent;
    let fixture: ComponentFixture<DateInputComponent>;

    beforeEach(async () => {
      await TestBed.configureTestingModule({ imports: [DateInputComponent] }).compileComponents();
      fixture = TestBed.createComponent(DateInputComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();
    });

    it('renders the formatted dd/MM/yyyy date when writeValue is called', () => {
      component.writeValue('2026-10-15');
      fixture.detectChanges();

      expect(component.isoValue).toBe('2026-10-15');
      expect(component.displayValue).toBe('15/10/2026');
      expect(component.textInput.nativeElement.value).toBe('15/10/2026');
    });

    it('updates ISO value when user edits the text input and blurs', () => {
      let emitted: string | undefined;
      component.registerOnChange((val) => (emitted = val));

      component.textInput.nativeElement.value = '4/5/2026';
      component.onTextBlur(new FocusEvent('blur'));

      expect(component.isoValue).toBe('2026-05-04');
      expect(component.textInput.nativeElement.value).toBe('04/05/2026');
      expect(emitted).toBe('2026-05-04');
    });

    it('takes a complete day while typing, without waiting for the blur', () => {
      let emitted: string | undefined;
      component.registerOnChange((val) => (emitted = val));

      const input = component.textInput.nativeElement;
      input.value = '15/10/202';
      input.dispatchEvent(new Event('input'));
      expect(emitted).toBeUndefined();
      input.value = '15/10/2026';
      input.dispatchEvent(new Event('input'));
      expect(emitted).toBe('2026-10-15');
    });

    it('puts the last good day back when the text is not a day', () => {
      component.writeValue('2026-10-15');
      let emitted: string | undefined;
      component.registerOnChange((val) => (emitted = val));

      component.textInput.nativeElement.value = '31/02/2026';
      component.onTextBlur(new FocusEvent('blur'));

      expect(emitted).toBeUndefined();
      expect(component.isoValue).toBe('2026-10-15');
      expect(component.textInput.nativeElement.value).toBe('15/10/2026');
    });

    it('updates ISO value when native picker emits a change', () => {
      let emitted: string | undefined;
      component.registerOnChange((val) => (emitted = val));

      const input = component.nativeInput.nativeElement;
      input.value = '2026-12-25';
      input.dispatchEvent(new Event('change'));

      expect(component.isoValue).toBe('2026-12-25');
      expect(component.textInput.nativeElement.value).toBe('25/12/2026');
      expect(emitted).toBe('2026-12-25');
    });

    it('gives its id to the text box, not the host, so a label names the field', () => {
      fixture.componentRef.setInput('id', 'from-day');
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      expect(host.getAttribute('id')).toBeNull();
      expect(component.textInput.nativeElement.id).toBe('from-day');
    });

    it('shows the placeholder in the language on screen', () => {
      const i18n = TestBed.inject(I18nService);
      expect(['jj/mm/aaaa', 'يوم/شهر/سنة', 'dd/mm/yyyy']).toContain(i18n.t('date.placeholder'));
      expect(component.textInput.nativeElement.placeholder).toBe(i18n.t('date.placeholder'));
    });
  });
});
