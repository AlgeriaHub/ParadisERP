import { InvoiceStatus, ReceiptStatus } from './receivables-api.service';

/**
 * Pure helpers for the receivables screens: navigation payloads, the state-word tone maps, and
 * the date and money arithmetic the screens would otherwise each solve a little differently.
 *
 * <p>No Angular in here. The rule that matters most is the one about words: every status the
 * server sends is PascalCase (`Open`, `PartlyPaid`, `Pending`, `Days31To60`) and the
 * translation keys are spelled after it, so an unknown value prints raw rather than as a
 * missing key.</p>
 */

export { today } from '../rentals/rental-view';
export { MAX_PERIOD_DAYS } from '../../../core/ledger.service';

// --- navigation payloads shared by the shell and every screen ---------------------------------

/** The four tables under a customer's account head. */
export type AccountPart = 'statement' | 'invoices' | 'receipts' | 'creditNotes';

/** A document opened from a list: the screen needs the customer too, to come back to the account. */
export interface DocRef {
  id: string;
  customerId: string;
}

/** Raising a receipt: from an account (invoiceId null) or from one invoice to be paid. */
export interface CollectRef {
  customerId: string;
  invoiceId: string | null;
}

// --- limits the server enforces, mirrored so the screens can say so before the 400 ------------

/** The server clamps `take` at 1000; asking for it is the only way to know whether a list was cut. */
export const INVOICE_TAKE = 1000;
export const RECEIPT_TAKE = 1000;
/** Default due date on a new invoice: issued + 30 days. */
export const DEFAULT_DUE_DAYS = 30;
/** How far back the invoice form looks for un-invoiced credit sales by default. */
export const SALES_WINDOW_DAYS = 90;

// --- state words ---------------------------------------------------------------------------------

/** Tone beside the word, never instead of it. `Credited` is quiet: the paper is void, not a problem. */
export const INVOICE_TONE: Record<InvoiceStatus, string> = {
  Open: 'bo-state--lead',
  PartlyPaid: 'bo-state--warn',
  Settled: 'bo-state--ok',
  Credited: '',
};

export const RECEIPT_TONE: Record<ReceiptStatus, string> = {
  Pending: 'bo-state--warn',
  Confirmed: 'bo-state--ok',
  Bounced: 'bo-state--danger',
};

/**
 * The translation key for a server enum value, spelled as the server spells it. The caller
 * prints the value raw when `i18n.t` hands the key back unchanged — the accounting tab's
 * `sourceLabel()` pattern.
 */
export function stateKey(prefix: string, value: string): string {
  return `${prefix}.${value}`;
}

/** The word for the income account an invoice or credit-note line sits on; null for a code nobody named. */
export function accountWordKey(code: string): string | null {
  switch (code) {
    case '700':
      return 'inv.view.account.700';
    case '706':
      return 'rent.kind.Rent';
    case '707':
      return 'rent.kind.Maintenance';
    case '708':
      return 'rent.kind.Overdue';
    case '757':
      return 'rent.kind.Damage';
    default:
      return null;
  }
}

/** Read as a word beside the date, keyed to `treasury.due.*`. */
export type DueWord = 'overdue' | 'today' | 'soon' | 'later' | 'none';

/** Within a week counts as soon: the treasury worklist draws the same line. */
const SOON_DAYS = 7;

export function dueWord(instrumentDueDate: string | null | undefined, timeZone: string): DueWord {
  if (!instrumentDueDate) return 'none';
  const due = new Date(withZ(instrumentDueDate));
  if (Number.isNaN(due.getTime())) return 'none';
  const days = daysBetween(localDayOf(new Date(), timeZone), localDayOf(due, timeZone));
  if (days < 0) return 'overdue';
  if (days === 0) return 'today';
  return days <= SOON_DAYS ? 'soon' : 'later';
}

// --- the credit-limit warning --------------------------------------------------------------------

/**
 * The server's warning is one English sentence with raw decimals: 'Customer credit limit
 * exceeded (1234567.89 > 1000000).' The two figures are all a screen needs to say it in the
 * reader's language and currency; null means the sentence changed shape and the raw fallback
 * text is the honest choice.
 */
export function parseCreditWarning(text: string | null | undefined): { balance: number; limit: number } | null {
  if (!text) return null;
  const match = /\(\s*(-?[\d.]+)\s*>\s*(-?[\d.]+)\s*\)/.exec(text);
  if (!match) return null;
  const balance = Number(match[1]);
  const limit = Number(match[2]);
  return Number.isFinite(balance) && Number.isFinite(limit) ? { balance, limit } : null;
}

// --- dates ---------------------------------------------------------------------------------------

/** SQL Server rows may come back without the trailing Z; every instant here is UTC regardless. */
export function withZ(iso: string): string {
  return /[zZ]$|[+-]\d{2}:\d{2}$/.test(iso) ? iso : `${iso}Z`;
}

/**
 * A date field means a day. Sent as midday, not midnight: at 00:00Z a day west of Greenwich is
 * still the day before, and the server's date-only comparison would move the due date.
 */
export function midday(day: string): string {
  return `${day}T12:00:00Z`;
}

export function startOfDay(date: Date): Date {
  const d = new Date(date);
  d.setHours(0, 0, 0, 0);
  return d;
}

export function startOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

export function toInputDate(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** Parses "yyyy-MM-dd" as a local date: `new Date(value)` would shift the day east of UTC. */
export function parseInputDate(value: string): Date {
  const [year, month, day] = value.split('-').map(Number);
  return new Date(year, month - 1, day);
}

/** The day after, as a local midnight: the server filters `< to`, the screen shows `to` inclusive. */
export function exclusiveEnd(day: string): Date {
  const end = parseInputDate(day);
  end.setDate(end.getDate() + 1);
  return end;
}

export function addDays(day: string, n: number): string {
  const d = parseInputDate(day);
  d.setDate(d.getDate() + n);
  return toInputDate(d);
}

/** Whole days from `a` to `b` (both "yyyy-MM-dd"); negative when `b` is earlier. */
export function daysBetween(a: string, b: string): number {
  const ms = Date.UTC(...ymd(b)) - Date.UTC(...ymd(a));
  return Math.round(ms / 86_400_000);
}

/** Inclusive length of a window in days — what the server's MaxPeriodDays counts. */
export function periodDays(from: string, to: string): number {
  return daysBetween(from, to) + 1;
}

function ymd(day: string): [number, number, number] {
  const [y, m, d] = day.split('-').map(Number);
  return [y, m - 1, d];
}

/** The calendar day an instant falls on in the company's zone, as "yyyy-MM-dd". */
function localDayOf(at: Date, timeZone: string): string {
  try {
    const parts = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    }).formatToParts(at);
    const get = (type: string) => parts.find((p) => p.type === type)?.value ?? '';
    return `${get('year')}-${get('month')}-${get('day')}`;
  } catch {
    return toInputDate(at);
  }
}

// --- money ---------------------------------------------------------------------------------------

/**
 * Tax the way the server charges it: per rate on the rate's total, not per line, so three lines
 * at 9.99 @ 19 % give 5.69 and not 5.70. `taxed` false is an Untaxed document — every rate reads
 * as zero whatever the lines say.
 */
export function taxByRate(lines: { lineNet: number; taxRate: number }[], taxed: boolean, decimals: number): number {
  if (!taxed) return 0;
  const byRate = new Map<number, number>();
  for (const line of lines) byRate.set(line.taxRate, (byRate.get(line.taxRate) ?? 0) + line.lineNet);
  let tax = 0;
  for (const [rate, net] of byRate) tax += roundTo((net * rate) / 100, decimals);
  return roundTo(tax, decimals);
}

export function roundTo(value: number, decimals: number): number {
  const factor = 10 ** decimals;
  return Math.round((value + Number.EPSILON) * factor) / factor;
}

/**
 * Oldest due first, never above a row's allocatable, until the amount runs out — the order a
 * clerk pays in. Rows come already sorted by due date; the result names only the rows that
 * received something, so an emptied allocation table reads as "all on account".
 */
export function spreadOldestFirst(
  amount: number | null,
  rows: { id: string; allocatable: number }[],
  decimals: number,
): Record<string, number> {
  let remaining = amount ?? 0;
  const next: Record<string, number> = {};
  for (const row of rows) {
    if (remaining <= 0) break;
    const take = roundTo(Math.min(remaining, row.allocatable), decimals);
    if (take > 0) next[row.id] = take;
    remaining = roundTo(remaining - take, decimals);
  }
  return next;
}
