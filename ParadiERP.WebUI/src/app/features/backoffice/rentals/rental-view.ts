import { numericDate } from '../../../core/day-format';
import { Lang } from '../../../core/models';
import {
  IssueBatch,
  RentalContractInput,
  RentalDay,
  RentalExtension,
  RentalLine,
  ReturnEntry,
  dayNumber,
  durationOf,
  entitledDays,
  entitlement,
  extensionsFor,
  localDay,
  localTime,
  oldestFirst,
  periodEndsOn,
  settledQty,
} from '../../../core/rentals';
import { AdminProduct } from '../../../core/admin.service';
import { localizedName } from '../backoffice-format';
import {
  ContractDocuments,
  ContractExtension,
  IssueNote,
  RentalContractLine,
  ReturnNote,
} from './rentals-api.service';

/**
 * The join between what the endpoints return and what `core/rentals.ts` — the server engine's
 * TypeScript mirror — consumes, plus the one thing neither of them gives a screen directly:
 * **when each batch is due back**.
 *
 * <p>A contract has no end date. Each line carries its own duration and each issue batch starts
 * its own clock on the day it went out, so one contract can have ten scaffolds due tomorrow and
 * forty due in three weeks. Everything here works in batches for that reason; anything that
 * printed a single expiry per contract would be stating something the business does not have.</p>
 */

/** A day in the company's own time zone. Counting from the UTC date charges a day nobody had. */
export function today(timeZone: string): RentalDay {
  return localDay(new Date().toISOString(), timeZone);
}

/**
 * A `RentalDay` for the reader.
 *
 * <p>Not `formatDay`: that one takes an instant and renders it in the company's zone, which is
 * right for a timestamp and wrong here. A rental day has already been resolved to a calendar
 * date, and putting it through a zone a second time moves it — west of Greenwich, by a day.
 * Fixed to UTC, it renders the date it says, dd/MM/yyyy in Latin digits, matching every other
 * date column on the screen.</p>
 */
export function formatRentalDay(day: RentalDay | null | undefined, lang: Lang): string {
  if (!day) return '—';
  const at = new Date(`${day}T00:00:00Z`);
  if (Number.isNaN(at.getTime())) return day;
  return numericDate(at, 'UTC');
}

/** The wall-clock time now in the company's zone, as 'HH:mm' for a time field. */
export function nowTime(timeZone: string): string {
  const now = localTime(new Date().toISOString(), timeZone);
  return `${String(now.hour).padStart(2, '0')}:${String(now.minute).padStart(2, '0')}`;
}

export function toDay(utcIso: string | null | undefined, timeZone: string): RentalDay | null {
  return utcIso ? localDay(utcIso, timeZone) : null;
}

/** A contract line as the engine wants it. */
export function engineLine(line: RentalContractLine): RentalLine {
  return {
    id: line.id,
    qty: line.qty,
    durationDays: line.durationDays,
    rentUnitPrice: line.rentUnitPrice,
    damageUnitPrice: line.damageUnitPrice,
    overdueUnitPrice: line.overdueUnitPrice,
    parentLineId: line.parentLineId,
  };
}

/**
 * Each batch starts on the day its period starts. The server says which day that is on every note
 * (`periodStartsOn`: the delivery day, or the next one for goods out after the company's cutoff
 * hour); an older server does not, and then the delivery day is the rule it applied.
 */
export function engineBatches(notes: IssueNote[], timeZone: string): IssueBatch[] {
  return notes.flatMap((note) =>
    note.lines.map((line) => ({
      issueNoteId: note.id,
      lineId: line.contractLineId,
      issuedOn: note.periodStartsOn || localDay(note.issuedAtUtc, timeZone),
      qty: line.qty,
    })),
  );
}

export function engineReturns(notes: ReturnNote[], timeZone: string): ReturnEntry[] {
  return notes.flatMap((note) =>
    note.lines.map((line) => ({
      returnNoteId: note.id,
      lineId: line.contractLineId,
      returnedOn: localDay(note.returnedAtUtc, timeZone),
      qtyOk: line.qtyOk,
      qtyPartialDamage: line.qtyPartialDamage,
      maintenanceValue: line.maintenanceValue,
      qtyTotalDamage: line.qtyTotalDamage,
      qtyMissing: line.qtyMissing,
      reportedOn: toDay(line.reportedOnUtc, timeZone),
      // The decisions stored on the note, never recomputed from today's cutoff hour.
      countsReturnDay: !!note.countReturnDay,
      waivesOverdue: !!note.waiveOverdue,
      lateDaysWaived: note.lateDaysWaived ?? null,
      lateAmountWaived: note.lateAmountWaived ?? null,
      lateAmountFull: note.lateAmountFull ?? null,
    })),
  );
}

export function engineExtensions(extensions: ContractExtension[], timeZone: string): RentalExtension[] {
  return extensions.map((e) => ({
    id: e.id,
    lineId: e.contractLineId,
    extraDays: e.extraDays,
    unitPrice: e.unitPrice,
    raisedOn: localDay(e.raisedOnUtc, timeZone),
    reason: e.reason,
  }));
}

/** Everything the engine needs to price a contract, from the five calls that describe it. */
export function toEngineInput(docs: ContractDocuments, timeZone: string): RentalContractInput {
  return {
    lines: docs.contract.lines.map(engineLine),
    issues: engineBatches(docs.issueNotes, timeZone),
    returns: engineReturns(docs.returnNotes, timeZone),
    extensions: engineExtensions(docs.extensions, timeZone),
    minimumDays: docs.contract.minimumRentalDays ?? 0,
  };
}

// --- what is still out, and when it is due ------------------------------------------------------

/** One batch of one line still with the customer, and the day it is due back. */
export interface OpenBatch {
  lineId: string;
  issueNoteId: string;
  issuedOn: RentalDay;
  /** Of the quantity this batch put out, what FIFO says has not yet been settled. */
  qty: number;
  /** The line's own duration plus every extension signed over it. */
  entitledDays: number;
  dueOn: RentalDay;
  /** Negative until the day it is due, then the number of days it has run over. */
  daysOverdue: number;
}

/**
 * The batches of one line that are still out, oldest first.
 *
 * <p>Mirrors `RentalDocuments.OpenBatches`: returns close the oldest batch first, so the
 * quantity a note settles is taken off the front of the queue and what is left of each batch is
 * what is genuinely on site. Getting this wrong would date the remaining units from the wrong
 * issue note and misprice every day they are out.</p>
 */
export function openBatchesOf(
  line: RentalContractLine,
  allLines: RentalContractLine[],
  batches: IssueBatch[],
  returns: ReturnEntry[],
  extensions: RentalExtension[],
  todayDay: RentalDay,
): OpenBatch[] {
  const engineLines = allLines.map(engineLine);
  const self = engineLine(line);
  const days = entitledDays(
    entitlement(line.rentUnitPrice, durationOf(self, engineLines), extensionsFor(self, extensions)),
  );

  let consumed = returns
    .filter((r) => r.lineId === line.id)
    .reduce((sum, r) => sum + settledQty(r), 0);

  const open: OpenBatch[] = [];
  for (const batch of oldestFirst(batches.filter((b) => b.lineId === line.id))) {
    if (consumed >= batch.qty) {
      consumed -= batch.qty;
      continue;
    }
    const qty = batch.qty - Math.max(0, consumed);
    consumed = 0;
    const dueOn = periodEndsOn(batch.issuedOn, days);
    open.push({
      lineId: line.id,
      issueNoteId: batch.issueNoteId,
      issuedOn: batch.issuedOn,
      qty,
      entitledDays: days,
      dueOn,
      daysOverdue: dayNumber(todayDay) - dayNumber(dueOn),
    });
  }
  return open;
}

/** Every batch still out on a contract, soonest due first — the order a yard cares about. */
export function openBatches(docs: ContractDocuments, timeZone: string): OpenBatch[] {
  const batches = engineBatches(docs.issueNotes, timeZone);
  const returns = engineReturns(docs.returnNotes, timeZone);
  const extensions = engineExtensions(docs.extensions, timeZone);
  const now = today(timeZone);
  return docs.contract.lines
    .flatMap((line) => openBatchesOf(line, docs.contract.lines, batches, returns, extensions, now))
    .sort((a, b) => a.dueOn.localeCompare(b.dueOn));
}

/** Read as a word, never as a colour: overdue, due today, or due in so many days. */
export type DueState = 'overdue' | 'dueToday' | 'due';

export function dueState(batch: OpenBatch): DueState {
  if (batch.daysOverdue > 0) return 'overdue';
  return batch.daysOverdue === 0 ? 'dueToday' : 'due';
}

// --- naming -------------------------------------------------------------------------------------

/**
 * What to call a contract line on screen.
 *
 * <p>The line carries a name snapshot frozen at signing, and the server freezes the French one
 * whatever the reader speaks. So the catalogue's own name wins while the item still exists —
 * that is what `localizedName` is for — and the snapshot is the fallback for an item since
 * deleted, which is exactly the case the snapshot was taken for.</p>
 */
export function lineName(
  line: { productId: string; name: string },
  products: AdminProduct[],
  lang: Lang,
): string {
  const product = products.find((p) => p.id === line.productId);
  return (product && localizedName(product, lang)) || line.name;
}

/**
 * The two names a rental paper prints for a line (answer 28): the French one frozen at signing,
 * and beside it the Arabic one — frozen too on lines signed since, and read from the catalogue for
 * an older line that has none. Empty Arabic when the item has no Arabic name: nothing is invented.
 */
export function paperNames(
  line: { productId: string; name: string; nameAr?: string | null },
  products: AdminProduct[],
): { fr: string; ar: string } {
  const product = products.find((p) => p.id === line.productId);
  const fr = line.name || product?.nameFr || product?.nameEn || '';
  const ar = (line.nameAr ?? product?.nameAr ?? '').trim();
  return { fr, ar: ar && ar !== fr ? ar : '' };
}

/** One item's name as a rental paper prints it: the name, and what prints beneath in another language. */
export interface ItemPaperName {
  main: string;
  second: string;
  secondLang: string;
  secondDir: 'rtl' | 'ltr';
}

/**
 * The names the company's setting asks for (answer 28: "one name, Arabic or French, the admin
 * chooses — or both"). Unset, the paper keeps what it always printed: the French name signed and
 * the Arabic beneath. The French name is the one frozen at signing, the Arabic one is frozen too on
 * lines signed since; any other language is the catalogue's. A language the item has no name in
 * falls back to the French one for the name and prints nothing beneath — nothing is invented.
 */
export function itemPaperName(
  line: { productId: string; name: string; nameAr?: string | null },
  products: AdminProduct[],
  languages: readonly string[] | null | undefined,
  isRtl: (lang: string) => boolean,
): ItemPaperName {
  const product = products.find((p) => p.id === line.productId);
  const { fr, ar } = paperNames(line, products);
  const nameIn = (lang: string): string => {
    if (lang === 'fr') return fr;
    if (lang === 'ar') return (line.nameAr ?? product?.nameAr ?? '').trim();
    if (lang === 'en') return (product?.nameEn ?? '').trim();
    return '';
  };
  if (!languages?.length) {
    return { main: fr, second: ar, secondLang: 'ar', secondDir: 'rtl' };
  }
  const main = nameIn(languages[0]) || fr;
  const secondLang = languages[1] ?? '';
  const second = secondLang ? nameIn(secondLang) : '';
  return {
    main,
    second: second && second !== main ? second : '',
    secondLang,
    secondDir: secondLang && isRtl(secondLang) ? 'rtl' : 'ltr',
  };
}

/** An accessory hangs off a parent: zero rent, a mandatory damage price, its parent's period. */
export function isAccessory(line: RentalContractLine): boolean {
  return !!line.parentLineId;
}

// --- the state of a contract, as a word ---------------------------------------------------------

/**
 * Four words, in the order a contract lives through them. `draft` is not a server status — the
 * server has only Open and Closed — but a contract with nothing issued is still editable and has
 * started no clock, and calling that "open" hides the one moment the prices can still change.
 */
export type ContractState = 'draft' | 'out' | 'partly' | 'unpaid' | 'closed' | 'cancelled';

export function contractState(
  contract: { status: string; isLocked: boolean; goodsBackAtUtc?: string | null },
  stillOutQty: number | null,
  settledQty: number | null,
): ContractState {
  // Called off before any exit note (owner, 2026-10-06); only the Admin is shown one.
  if (contract.status === 'Cancelled') return 'cancelled';
  if (contract.status === 'Closed') return 'closed';
  // Every unit back or settled, and not yet paid (answers 15, 22): open, and said so.
  if (contract.goodsBackAtUtc) return 'unpaid';
  if (!contract.isLocked) return 'draft';
  // Until the settlement has answered, an issued contract is out; it is the safe half-truth.
  if (stillOutQty === null || settledQty === null) return 'out';
  return settledQty > 0 ? 'partly' : 'out';
}

/**
 * Whether this user still sees a contract once it is cancelled: the Admin, or whoever was granted
 * rentals.cancelled (owner, 2026-10-06: "hide it for cashier and only appear for admin"). The
 * server decides the same and hides the rows; this only keeps the screen from offering them.
 */
export function seesCancelled(user: { role?: string; permissions?: readonly string[] } | null | undefined): boolean {
  return user?.role === 'Admin' || (user?.permissions ?? []).includes('rentals.cancelled');
}

export const STATE_TONE: Record<ContractState, string> = {
  draft: '',
  out: 'bo-state--lead',
  partly: 'bo-state--lead',
  unpaid: 'bo-state--warn',
  closed: 'bo-state--ok',
  cancelled: '',
};
