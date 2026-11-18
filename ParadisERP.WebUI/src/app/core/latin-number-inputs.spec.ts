import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormsModule } from '@angular/forms';
import { installLatinNumberInputs, latinNumberText, steppedValue, writesNonLatinDigits } from './latin-number-inputs';

describe('latin number inputs', () => {
  it('knows which browser languages write non-Latin digits', () => {
    expect(writesNonLatinDigits('ar-SA')).toBeTrue();
    expect(writesNonLatinDigits('ar-EG')).toBeTrue();
    expect(writesNonLatinDigits('fa-IR')).toBeTrue();
    expect(writesNonLatinDigits('ar-DZ')).toBeFalse();
    expect(writesNonLatinDigits('fr-FR')).toBeFalse();
    expect(writesNonLatinDigits('en-US')).toBeFalse();
    expect(writesNonLatinDigits(undefined)).toBeFalse();
  });

  it('puts what is typed in Latin digits with a dot', () => {
    expect(latinNumberText('٤٥٠٠٠')).toBe('45000');
    expect(latinNumberText('۲۲')).toBe('22');
    expect(latinNumberText('١٢٫٥')).toBe('12.5');
    expect(latinNumberText('12,5')).toBe('12.5');
    expect(latinNumberText('1 250,75')).toBe('1250.75');
    expect(latinNumberText('-3')).toBe('-3');
    expect(latinNumberText('3-')).toBe('3');
    expect(latinNumberText('1.2.3')).toBe('1.23');
    expect(latinNumberText('abc')).toBe('');
  });

  it('steps up and down within min and max, keeping the decimals in play', () => {
    expect(steppedValue('5', null, null, null, true)).toBe('6');
    expect(steppedValue('', '0.5', '0', null, false)).toBe('0.0');
    expect(steppedValue('9.5', '0.5', null, '10', true)).toBe('10.0');
    expect(steppedValue('10', '1', null, '10', true)).toBe('10');
    expect(steppedValue('2', 'any', '0', null, false)).toBe('1');
    expect(steppedValue('52000.5', null, null, null, true)).toBe('52001.5');
  });

  describe('on a page', () => {
    @Component({
      standalone: true,
      imports: [FormsModule],
      template: `<input id="salary" type="number" min="0" [(ngModel)]="salary" />`,
    })
    class Host {
      salary: number | null = 45000;
    }

    let stop: () => void = () => {};
    afterEach(() => stop());

    it('in an Arabic browser, makes number boxes text boxes and hands the model a number', async () => {
      stop = installLatinNumberInputs(document, 'ar-SA');
      const fixture = TestBed.createComponent(Host);
      document.body.appendChild(fixture.nativeElement);
      fixture.detectChanges();
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r)); // the observer runs after the insertion
      fixture.detectChanges();
      await fixture.whenStable();

      const input = fixture.nativeElement.querySelector('#salary') as HTMLInputElement;
      expect(input.type).toBe('text');
      expect(input.getAttribute('inputmode')).toBe('decimal');
      expect(input.value).toBe('45000');

      input.value = '٥٢٠٠٠٫٥';
      input.dispatchEvent(new Event('input', { bubbles: true }));
      expect(input.value).toBe('52000.5');
      expect(fixture.componentInstance.salary).toBe(52000.5);

      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }));
      expect(fixture.componentInstance.salary).toBe(52001.5);
      fixture.nativeElement.remove();
    });

    it('in a French or English browser, leaves the native number box alone', () => {
      stop = installLatinNumberInputs(document, 'fr-FR');
      const fixture = TestBed.createComponent(Host);
      fixture.detectChanges();
      const input = fixture.nativeElement.querySelector('#salary') as HTMLInputElement;
      expect(input.type).toBe('number');
      expect(input.hasAttribute('data-latin-number')).toBeFalse();
    });
  });
});
