import { ApiError } from '../../../core/api.service';
import { PunchColumn, PunchMapping, PunchRow, PunchRowStatus } from './hr-api.service';

/**
 * The rules of the punch-file preview that are not drawing (FR-3B), kept apart so they are tested
 * without a DOM. None of them reads meaning into a punch: which is an arrival, what a repeated
 * punch or a single punch on a day means — those wait for the terminal's real export.
 */

/** The seven things a column can be, in the order the form asks them. */
export const PUNCH_ROLES = ['number', 'name', 'date', 'time', 'timeOut', 'dateTime', 'marker'] as const;
export type PunchRole = (typeof PUNCH_ROLES)[number];

export function emptyMapping(): PunchMapping {
  return { number: null, name: null, date: null, time: null, timeOut: null, dateTime: null, marker: null };
}

/**
 * The mapping with one role set. A column is one thing: choosing it for a role takes it off any
 * other. And the date is said one way: a combined column clears the separate date and time, and
 * either of those clears the combined one.
 */
export function assign(mapping: PunchMapping, role: PunchRole, column: number | null): PunchMapping {
  const next: PunchMapping = { ...mapping, [role]: column };
  if (column !== null) {
    for (const other of PUNCH_ROLES) if (other !== role && next[other] === column) next[other] = null;
    if (role === 'dateTime') next.date = next.time = null;
    if (role === 'date' || role === 'time') next.dateTime = null;
  }
  return next;
}

/** What is still missing before a row can be read; the server says the same, with the same codes. */
export function mappingProblem(m: PunchMapping): 'mapNumber' | 'mapWhen' | null {
  if (m.number === null) return 'mapNumber';
  if (m.dateTime === null && (m.date === null || m.time === null)) return 'mapWhen';
  return null;
}

/** "B — Nom (Ali, Omar…)": the letter Excel shows, the header when there is one, and what is under it. */
export function columnLabel(c: PunchColumn): string {
  const head = c.header ? `${c.letter} — ${c.header}` : c.letter;
  return c.samples.length ? `${head} (${c.samples.join(', ')})` : head;
}

export type RowFilter = 'all' | PunchRowStatus;
export const ROW_FILTERS: readonly RowFilter[] = ['all', 'matched', 'unknown', 'otherBranch', 'invalid'];

export function filterRows(rows: PunchRow[], filter: RowFilter): PunchRow[] {
  return filter === 'all' ? rows : rows.filter((r) => r.status === filter);
}

/**
 * `2026-09-01T07:58:00` as `01/09/2026 07:58:00`: a wall-clock time from the terminal's file, shown
 * as written and never shifted by a zone, day first like every other date on screen.
 */
export function wallClock(at: string | null | undefined): string {
  if (!at) return '—';
  const m = at.match(/^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}:\d{2}(?::\d{2})?))?/);
  return m ? `${m[3]}/${m[2]}/${m[1]}${m[4] ? ' ' + m[4] : ''}` : at;
}

/**
 * The reader's refusals arrive as English sentences (`XlsxReader`, the route's size cap); the
 * screen says them in the reader's language. Anything else is left to the generic handler.
 */
export function fileProblemKey(error: unknown): string | null {
  if (!(error instanceof ApiError) || error.status !== 400) return null;
  const text = error.message ?? '';
  if (text.includes('not an Excel workbook')) return 'punch.err.notWorkbook';
  if (text.includes('larger than')) return 'punch.err.tooLarge';
  if (text.includes('more than') && text.includes('rows')) return 'punch.err.tooManyRows';
  if (text.includes('no worksheet')) return 'punch.err.noSheet';
  return null;
}
