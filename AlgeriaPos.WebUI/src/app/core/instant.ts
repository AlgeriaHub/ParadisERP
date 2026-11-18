/** A date and time with no zone after it: `2026-10-13T22:12`, with or without seconds and fractions. */
const ZONELESS = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$/;

/**
 * A date and time the API sent, spelled as the UTC moment it is.
 *
 * <p>Every moment the server stores is UTC, and it says so with a trailing Z. A value without a
 * zone is UTC all the same — one a till cached before the server said so, or one worked out on the
 * server without a zone — so it gets its Z here. A date alone (`2026-10-13`) is already read as UTC
 * by the language, and a value carrying a Z or an offset is returned exactly as it is, so nothing
 * that was right moves.</p>
 */
export function utcIso(value: string): string {
  return ZONELESS.test(value) ? `${value}Z` : value;
}

/**
 * {@link utcIso}, read into a `Date`. Without it `new Date()` takes a zone-less value as this
 * browser's own clock: an hour out in Algiers, three in Riyadh, and a day out near midnight.
 */
export function parseInstant(value: string | Date): Date {
  return typeof value === 'string' ? new Date(utcIso(value)) : value;
}
