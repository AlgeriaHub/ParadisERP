/**
 * Dates on screen read day/month/year — `15/10/2026` — in every language (owner, 2026-10-15).
 *
 * <p>`dateStyle: 'short'` left the order to the locale, and the locale to the browser: an English
 * screen, or an Arabic one on a browser whose ICU resolves `ar` to a US order, showed
 * `10/15/2026`. The parts are taken from `Intl` (so the time zone still applies) and put in a
 * fixed order here, with Latin digits so receipts stay readable on thermal printers.</p>
 */

type Parts = Partial<Record<Intl.DateTimeFormatPartTypes, string>>;

function parts(date: Date, timeZone: string | undefined, withTime: boolean): Parts {
  const options: Intl.DateTimeFormatOptions = {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    numberingSystem: 'latn',
    timeZone,
    ...(withTime ? { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' } : {}),
  };
  let format: Intl.DateTimeFormat;
  try {
    format = new Intl.DateTimeFormat('en-GB', options);
  } catch {
    // A time zone this browser does not know: the device's own clock still beats a dash.
    format = new Intl.DateTimeFormat('en-GB', { ...options, timeZone: undefined });
  }
  const out: Parts = {};
  for (const part of format.formatToParts(date)) out[part.type] = part.value;
  return out;
}

/** `dd/MM/yyyy` for a moment, on the given zone's calendar. */
export function numericDate(date: Date, timeZone?: string): string {
  const p = parts(date, timeZone, false);
  return `${p.day}/${p.month}/${p.year}`;
}

/** `dd/MM/yyyy HH:mm` for a moment, on the given zone's clock (24-hour, as Algeria reads it). */
export function numericDateTime(date: Date, timeZone?: string): string {
  const p = parts(date, timeZone, true);
  return `${p.day}/${p.month}/${p.year} ${p.hour}:${p.minute}`;
}
