import { numericDate } from '../../core/day-format';
import { parseInstant } from '../../core/instant';
import { Lang } from '../../core/models';
import { formatNumber } from '../../core/number-format';

/**
 * Two formatting jobs the back office kept solving a little differently in each tab.
 *
 * <p>Both matter more in Arabic than anywhere else, which is why they are here rather than
 * copied into eight components:</p>
 * <ul>
 *   <li><b>A record's name.</b> Every catalogue row carries three names, and a dropdown that
 *   always printed the French one left an Arabic manager choosing between branches he could
 *   not read. The tables that exist to <em>edit</em> all three names still show all three —
 *   that is what those columns are for — but anywhere a name is only being referred to, it is
 *   the reader's name that is shown.</li>
 *   <li><b>A date.</b> `toLocaleDateString(lang)` takes the browser's default numbering system
 *   and the device's own clock, so a due date could come out in Arabic-Indic digits and one
 *   day off the branch it belongs to, in the same column as a timestamp that came from
 *   `I18nService.dateTime` in Latin digits on the company's time zone.</li>
 * </ul>
 */

export { localizedName } from '../../core/multilingual';
export type { MultilingualName } from '../../core/multilingual';

/**
 * A date with no time: a cheque falls due on a day, not at a moment. dd/MM/yyyy in Latin digits
 * on the company's time zone (`core/day-format.ts`), matching `I18nService.dateTime` so two date
 * columns in one table cannot disagree about either.
 */
export function formatDay(value: string | Date | null | undefined, lang: Lang, timeZone?: string): string {
  if (!value) return '—';
  // The same reading as `I18nService.dateTime`: a moment from the API is UTC, Z or not.
  const date = parseInstant(value);
  if (Number.isNaN(date.getTime())) return '—';
  return numericDate(date, timeZone);
}

/**
 * Formats a quantity with up to 3 decimal places in Latin digits, without any currency symbol.
 * Null or undefined is returned as an em dash.
 */
export function formatQty(value: number | null | undefined, lang: Lang = 'fr'): string {
  if (value === null || value === undefined) return '—';
  // The company's number format (core/number-format.ts), like every other figure.
  return formatNumber(value, lang, { minimumFractionDigits: 0, maximumFractionDigits: 3 });
}
