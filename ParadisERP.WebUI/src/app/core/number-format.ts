/**
 * How figures are written: which mark groups thousands and which marks decimals. A country setting
 * (`RegionalSettings.numberFormat`), never a language's habit: the owner asked for 5,300,256.25 in every place of the
 * system and every language (2026-11-09), where Arabic wrote 408.024,00 and French 408 024,00.
 *
 * <p>`locale` keeps each language's own marks (the behaviour before the setting). Digits are Latin in every case.</p>
 */
export type NumberFormatKey = '1,234.56' | '1.234,56' | '1 234,56' | 'locale';

export const NUMBER_FORMATS: readonly NumberFormatKey[] = ['1,234.56', '1.234,56', '1 234,56', 'locale'];

/** The system's own format when a company has not chosen one. */
export const DEFAULT_NUMBER_FORMAT: NumberFormatKey = '1,234.56';

const SEPARATORS: Record<Exclude<NumberFormatKey, 'locale'>, { group: string; decimal: string }> = {
  '1,234.56': { group: ',', decimal: '.' },
  '1.234,56': { group: '.', decimal: ',' },
  // A narrow no-break space: the group never breaks across a line.
  '1 234,56': { group: ' ', decimal: ',' },
};

let current: NumberFormatKey = DEFAULT_NUMBER_FORMAT;

export function isNumberFormat(value: unknown): value is NumberFormatKey {
  return typeof value === 'string' && (NUMBER_FORMATS as readonly string[]).includes(value);
}

/** The company's format, set by RegionalService whenever its settings arrive; an unknown value is the default. */
export function setNumberFormat(value: string | null | undefined): void {
  current = isNumberFormat(value) ? value : DEFAULT_NUMBER_FORMAT;
}

export function currentNumberFormat(): NumberFormatKey {
  return current;
}

/**
 * Writes a number with the company's marks. `localeTag` is the language (or language-country) the `locale` format
 * follows; `format` overrides the company's choice (a preview of another choice).
 */
export function formatNumber(
  value: number,
  localeTag: string,
  options: Intl.NumberFormatOptions = {},
  format: NumberFormatKey = current,
): string {
  const opts: Intl.NumberFormatOptions = { numberingSystem: 'latn', ...options };
  if (format === 'locale') {
    try {
      return new Intl.NumberFormat(localeTag, opts).format(value);
    } catch {
      // A language tag this browser cannot combine with the country.
      return new Intl.NumberFormat('fr', opts).format(value);
    }
  }
  const marks = SEPARATORS[format];
  return new Intl.NumberFormat('en-US', opts)
    .formatToParts(value)
    .map((part) => (part.type === 'group' ? marks.group : part.type === 'decimal' ? marks.decimal : part.value))
    .join('');
}
