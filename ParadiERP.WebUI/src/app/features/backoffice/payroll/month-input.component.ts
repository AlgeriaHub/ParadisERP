import { Component, Input, booleanAttribute, computed, forwardRef, inject, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { monthNames } from './attendance-day-grid.component';

/**
 * A payroll month as two lists — the month by name, the year — holding the same `YYYY-MM` string
 * an `<input type="month">` held, so nothing that reads or sends it changes.
 *
 * <p>The native box writes its month and digits in the browser's language, not the app's: on an
 * Arabic browser a French screen read « أكتوبر ٢٠٢٦ » (2026-10-15). The lists are the app's own
 * text. `id` goes to the month list, so the field's `<label for>` names it.</p>
 */
@Component({
  selector: 'app-month-input',
  standalone: true,
  host: { '[attr.id]': 'null' },
  template: `
    <select
      [attr.id]="id || null"
      [disabled]="disabled"
      (change)="pick(year(), +$any($event.target).value)"
      (blur)="onTouched()"
    >
      @if (!month()) {
        <option value="0" selected>—</option>
      }
      @for (name of names(); track $index) {
        <option [value]="$index + 1" [selected]="month() === $index + 1">{{ name }}</option>
      }
    </select>
    <select
      class="mono"
      [attr.aria-label]="i18n.t('hr.sheet.year')"
      [disabled]="disabled"
      (change)="pick(+$any($event.target).value, month())"
      (blur)="onTouched()"
    >
      @for (y of years(); track y) {
        <option [value]="y" [selected]="year() === y">{{ y }}</option>
      }
    </select>
  `,
  styles: [
    `
      :host {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 6.5rem;
        gap: var(--sp-2);
        inline-size: 100%;
      }
    `,
  ],
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => MonthInputComponent), multi: true }],
})
export class MonthInputComponent implements ControlValueAccessor {
  readonly i18n = inject(I18nService);

  @Input() id = '';
  @Input({ transform: booleanAttribute }) disabled = false;

  readonly year = signal(new Date().getFullYear());
  /** 1–12, or 0 while no month is chosen. */
  readonly month = signal(0);

  readonly names = computed(() => monthNames(this.i18n.lang()));
  /** Next year back to five years ago, and the year on show if it lies outside that. */
  readonly years = computed(() => {
    const now = new Date().getFullYear();
    const list = Array.from({ length: 7 }, (_, i) => now + 1 - i);
    return list.includes(this.year()) ? list : [this.year(), ...list].sort((a, b) => b - a);
  });

  private onChange: (value: string) => void = () => {};
  onTouched: () => void = () => {};

  writeValue(value: string | null | undefined): void {
    const match = /^(\d{4})-(\d{2})$/.exec((value ?? '').trim());
    if (match) {
      this.year.set(Number(match[1]));
      this.month.set(Number(match[2]));
    } else {
      this.month.set(0);
    }
  }

  registerOnChange(fn: (value: string) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled = isDisabled;
  }

  pick(year: number, month: number): void {
    this.year.set(year);
    this.month.set(month);
    if (month >= 1 && month <= 12) this.onChange(`${year}-${String(month).padStart(2, '0')}`);
  }
}
