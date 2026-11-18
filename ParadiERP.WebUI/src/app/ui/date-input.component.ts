import {
  Component,
  ElementRef,
  EventEmitter,
  Input,
  Output,
  ViewChild,
  booleanAttribute,
  forwardRef,
  inject,
} from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import { I18nService } from '../core/i18n.service';

/** `yyyy-MM-dd` (or the date part of an ISO moment) as `dd/MM/yyyy`; '' for nothing usable. */
export function isoToDisplayDate(iso: string | null | undefined): string {
  if (!iso) return '';
  const match = String(iso).trim().match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (!match) return '';
  const [, y, m, d] = match;
  return `${d}/${m}/${y}`;
}

/** The ISO day for a year, month and day, or null when that day does not exist (31/02). */
function isoDay(y: number, m: number, d: number): string | null {
  const date = new Date(Date.UTC(y, m - 1, d));
  if (date.getUTCFullYear() !== y || date.getUTCMonth() !== m - 1 || date.getUTCDate() !== d) return null;
  return `${String(y).padStart(4, '0')}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
}

/**
 * What a person typed, as the ISO `yyyy-MM-dd` the forms and the API carry: `dd/MM/yyyy`,
 * `d/M/yyyy`, `dd-MM-yyyy`, `dd.MM.yyyy`, eight digits `ddMMyyyy` (a phone's number pad has no
 * slash), or an ISO day pasted in. '' for an empty field, null for anything that is not a day.
 */
export function parseDisplayDate(text: string | null | undefined): string | null {
  const t = (text ?? '').trim();
  if (!t) return '';
  let m = t.match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (m) return isoDay(+m[1], +m[2], +m[3]);
  m = t.match(/^(\d{1,2})[/.-](\d{1,2})[/.-](\d{4})$/);
  if (m) return isoDay(+m[3], +m[2], +m[1]);
  m = t.match(/^(\d{2})(\d{2})(\d{4})$/);
  if (m) return isoDay(+m[3], +m[2], +m[1]);
  return null;
}

/**
 * A day field that reads dd/MM/yyyy in every language (owner, 2026-10-15).
 *
 * <p>A native `<input type="date">` draws its text in the browser's locale, not the app's, so an
 * Arabic or French screen on an English browser showed mm/dd/yyyy. This field shows the day as
 * text the app formats, and keeps the browser's own calendar behind the button (and Alt+↓ / F4).
 * The model stays the ISO `yyyy-MM-dd` the native input held, so nothing that reads or sends it
 * changes. `id` goes to the text box, so `<label for>` still names it.</p>
 */
@Component({
  selector: 'app-date-input',
  standalone: true,
  // The id belongs to the text box inside; left on the host as well, a label would point at the
  // host — not a form control — and the field would lose its name.
  host: { '[attr.id]': 'null' },
  template: `
    <span class="date-field">
      <input
        #textInput
        type="text"
        class="date-text"
        [attr.id]="id || null"
        [value]="displayValue"
        [placeholder]="placeholder || i18n.t('date.placeholder')"
        [disabled]="disabled"
        [required]="required"
        [attr.aria-label]="ariaLabel || null"
        [attr.aria-invalid]="ariaInvalid || null"
        [attr.aria-describedby]="ariaDescribedby || null"
        inputmode="numeric"
        size="11"
        autocomplete="off"
        (input)="onTextInput()"
        (blur)="onTextBlur($event)"
        (keydown)="onKeyDown($event)"
      />
      <button
        type="button"
        class="date-open"
        tabindex="-1"
        [disabled]="disabled"
        [attr.aria-label]="i18n.t('date.openCalendar')"
        [attr.title]="i18n.t('date.openCalendar')"
        (click)="openPicker()"
      >
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true">
          <rect x="3.5" y="5" width="17" height="15.5" rx="2" />
          <path d="M3.5 10h17M8 3v4M16 3v4" />
        </svg>
      </button>
      <input
        #nativeInput
        type="date"
        class="date-native"
        tabindex="-1"
        aria-hidden="true"
        [value]="isoValue"
        [attr.min]="min || null"
        [attr.max]="max || null"
        [disabled]="disabled"
        (change)="onPicked($event)"
      />
    </span>
  `,
  styles: [`
    :host {
      display: block;
      inline-size: 100%;
    }

    .date-field {
      position: relative;
      display: block;
    }

    /* The text box keeps the global input look (border, focus ring, themes, dark mode). Its
       natural width is a date's (size="11"), not the browser's twenty characters, so a toolbar of
       filters stays on one row; in a form grid it still fills its column. */
    .date-text {
      padding-inline-end: 2.5rem;
      font-variant-numeric: tabular-nums;
    }

    .date-open {
      position: absolute;
      inset-block: 0;
      inset-inline-end: 0;
      display: grid;
      place-items: center;
      inline-size: 2.4rem;
      min-height: 0;
      padding: 0;
      border: 0;
      border-radius: var(--radius-field);
      background: transparent;
      color: var(--muted);
      transition: color var(--t-fast) ease;
    }

    .date-open:hover:not(:disabled) {
      color: var(--text);
    }

    .date-open svg {
      inline-size: 1.1rem;
      block-size: 1.1rem;
    }

    /* The browser's calendar opens from here, under the field; it is never seen or reached. */
    .date-native {
      position: absolute;
      inset: 0;
      inline-size: 100%;
      block-size: 100%;
      min-height: 0;
      padding: 0;
      border: 0;
      opacity: 0;
      pointer-events: none;
    }

    :host-context(.is-invalid) .date-text {
      border-color: var(--danger);
    }
  `],
  providers: [
    { provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => DateInputComponent), multi: true },
  ],
})
export class DateInputComponent implements ControlValueAccessor {
  readonly i18n = inject(I18nService);

  @ViewChild('textInput', { static: true }) textInput!: ElementRef<HTMLInputElement>;
  @ViewChild('nativeInput', { static: true }) nativeInput!: ElementRef<HTMLInputElement>;

  @Input() id = '';
  @Input() min?: string | null;
  @Input() max?: string | null;
  @Input() placeholder?: string;
  @Input({ transform: booleanAttribute }) required = false;
  @Input({ transform: booleanAttribute }) disabled = false;
  @Input() ariaLabel?: string | null;
  @Input() ariaInvalid?: string | boolean | null;
  @Input() ariaDescribedby?: string | null;

  /** The text box lost focus — for "touched" bookkeeping, as `(blur)` on a native input. */
  @Output() readonly blur = new EventEmitter<FocusEvent>();

  isoValue = '';
  displayValue = '';

  private onChange: (value: string) => void = () => {};
  private onTouched: () => void = () => {};

  writeValue(value: string | null | undefined): void {
    this.isoValue = value ? String(value).trim().slice(0, 10) : '';
    this.show(this.isoValue);
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

  openPicker(): void {
    if (this.disabled) return;
    const native = this.nativeInput.nativeElement;
    try {
      native.showPicker();
    } catch {
      // No showPicker here (older Safari): the text box still takes a typed day.
      this.textInput.nativeElement.focus();
    }
  }

  /** A complete, real day typed in is taken at once, so a filter refreshes without a blur. */
  onTextInput(): void {
    const parsed = parseDisplayDate(this.textInput.nativeElement.value);
    if (parsed && parsed !== this.isoValue) this.commit(parsed, false);
  }

  /** On leaving: take what was typed, tidy it to dd/MM/yyyy, or put back the last good day. */
  onTextBlur(event: FocusEvent): void {
    this.onTouched();
    const parsed = parseDisplayDate(this.textInput.nativeElement.value);
    if (parsed !== null && parsed !== this.isoValue) this.commit(parsed, true);
    else this.show(this.isoValue);
    this.blur.emit(event);
  }

  onKeyDown(event: KeyboardEvent): void {
    if ((event.altKey && event.key === 'ArrowDown') || event.key === 'F4') {
      event.preventDefault();
      this.openPicker();
    }
  }

  onPicked(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.commit(value ? value.slice(0, 10) : '', true);
    this.onTouched();
  }

  private commit(iso: string, redraw: boolean): void {
    this.isoValue = iso;
    if (redraw) this.show(iso);
    this.onChange(iso);
  }

  /** Writes the text box directly: a value set through forms may arrive outside change detection. */
  private show(iso: string): void {
    this.displayValue = isoToDisplayDate(iso);
    this.textInput.nativeElement.value = this.displayValue;
  }
}
