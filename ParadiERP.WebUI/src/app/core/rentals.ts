import { roundMoney } from './money';

/**
 * Rental arithmetic, mirroring ParadiERP.Application.Rentals on the server, so the back office
 * can price a return note before it is saved and while the line is down. The server recomputes
 * everything on receipt; if the two ever disagree, the server is right and this file is the bug.
 *
 * Every rule is named after what the owner called it, and the reasoning lives beside the
 * server's copy in ParadiERP.Application/Rentals.
 */

/** A day, as 'YYYY-MM-DD'. The whole calculation counts days, never hours. */
export type RentalDay = string;

export type RentalChargeKind = 'Rent' | 'Overdue' | 'Maintenance' | 'Damage' | 'Missing';

export const RENTAL_CHARGE_KINDS: RentalChargeKind[] = ['Rent', 'Overdue', 'Maintenance', 'Damage', 'Missing'];

export interface RentalSettings {
  /** Applied to the rental price to get the overdue price, e.g. 1.5x. Never below 1. */
  overdueMultiplier: number;
  /** Built and switched off. While false the deposit is invisible and nothing computes with it. */
  depositEnabled: boolean;
  /**
   * The local hour (0–23) that splits a day for delivery and return, or null/absent for the rule
   * the engine has always had: the delivery day counts, the return day never does. See `periodStartsOn`.
   */
  dayCutoffHour?: number | null;
}

/** One contract line, with the three prices signed on it. Rent and overdue are per day. */
export interface RentalLine {
  id: string;
  qty: number;
  durationDays: number;
  rentUnitPrice: number;
  damageUnitPrice: number;
  overdueUnitPrice: number;
  /** Set on an accessory: zero rent, a mandatory damage price, and its parent's period. */
  parentLineId?: string | null;
}

/** A quantity of one line that went out on one day — the unit of calculation. */
export interface IssueBatch {
  issueNoteId: string;
  lineId: string;
  issuedOn: RentalDay;
  qty: number;
}

/** What came back on one return note, and in what condition. */
export interface ReturnEntry {
  returnNoteId: string;
  lineId: string;
  returnedOn: RentalDay;
  qtyOk: number;
  qtyPartialDamage: number;
  /** What repairing the partially damaged units is charged at. */
  maintenanceValue: number;
  qtyTotalDamage: number;
  qtyMissing: number;
  /**
   * The day the customer reported the loss, when nothing on this note came back.
   *
   * Rent stops here rather than at the settlement: reporting is what lets the company write
   * the unit off and stop waiting for it. A customer who says nothing keeps paying, because
   * the company did not know and could not re-hire it — and so reporting late is never the
   * cheaper option. See `rentStopsOn`.
   */
  reportedOn?: RentalDay | null;
  /**
   * Whether the return day itself is charged — the decision stored on the note (see
   * `countsReturnDayByDefault`). Absent is false, the rule the engine has always had.
   */
  countsReturnDay?: boolean;
  /**
   * The answer stored on the note to "charge the late days?" (owner, 2026-10-08): waived, the days
   * past the period are not charged at all. Absent is false — charged, as they always were. See
   * `RentalLateReturn` on the server.
   */
  waivesOverdue?: boolean;
  /**
   * Yes, less a deduction (owner, 2026-10-22), as the note stores it: so many late days taken off
   * each batch (the last ones), or so much taken off the note's late total `lateAmountFull`, every
   * late day then charged at the same share of its price. At most one of the two is set.
   */
  lateDaysWaived?: number | null;
  lateAmountWaived?: number | null;
  lateAmountFull?: number | null;
}

/**
 * The day rent stops running on a note, exclusive.
 *
 * Only a note where **nothing came back** is dated from the report: a unit that walked back in
 * was the customer's to use until they returned it, whatever was said about it in between. A
 * report outside the hire — after the settlement, or before the goods went out — is a data
 * entry error and is ignored rather than trusted. Otherwise it is the return day, or the day
 * after it when the return day is counted; the report is never moved by that decision.
 */
export function rentStopsOn(note: ReturnEntry, issuedOn: RentalDay): RentalDay {
  const reported = note.reportedOn;
  const isPureLoss = note.qtyOk === 0 && note.qtyPartialDamage === 0;
  return reported && isPureLoss && reported < note.returnedOn && reported >= issuedOn
    ? reported
    : chargedUntil(note.returnedOn, !!note.countsReturnDay);
}

// --- the delivery hour and the return day (answers 5 and 6, 2026-09-30) ---------------------------

/**
 * The wall-clock time of an instant in the company's zone: its day, and the hour and minute on it.
 * Mirrors `RentalDays.LocalTime` on the server.
 */
export interface LocalTime {
  day: RentalDay;
  hour: number;
  minute: number;
}

export function localTime(utcIso: string, timeZone: string): LocalTime {
  const at = new Date(utcIso);
  try {
    const parts = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hourCycle: 'h23',
    }).formatToParts(at);
    const get = (type: string) => parts.find((p) => p.type === type)?.value ?? '';
    return {
      day: `${get('year')}-${get('month')}-${get('day')}`,
      hour: Number(get('hour')) % 24,
      minute: Number(get('minute')),
    };
  } catch {
    // An unknown zone must not stop a note being taken; UTC is at worst a few hours out.
    const iso = at.toISOString();
    return { day: iso.slice(0, 10), hour: at.getUTCHours(), minute: at.getUTCMinutes() };
  }
}

/**
 * The UTC instant of a wall-clock time in the company's zone — what a note typed as "the 26th at
 * 15:00" is stored as. Found by correcting a first guess by the zone's own offset at that moment,
 * twice, so it holds across a change of summer time as well as in Algiers, which has none.
 */
export function zonedTimeToUtcIso(day: RentalDay, time: string, timeZone: string): string {
  const [y, m, d] = day.split('-').map(Number);
  const [hh, mm] = (time || '00:00').split(':').map(Number);
  const wanted = Date.UTC(y, (m || 1) - 1, d || 1, hh || 0, mm || 0);
  let guess = wanted;
  for (let i = 0; i < 2; i++) {
    const seen = localTime(new Date(guess).toISOString(), timeZone);
    const [sy, sm, sd] = seen.day.split('-').map(Number);
    const shown = Date.UTC(sy, sm - 1, sd, seen.hour, seen.minute);
    guess += wanted - shown;
  }
  return new Date(guess).toISOString();
}

/** Whether a local time is at or after the cutoff hour. Always false with no cutoff. */
export function isAfterCutoff(hour: number, cutoffHour: number | null | undefined): boolean {
  return cutoffHour !== null && cutoffHour !== undefined && hour >= cutoffHour;
}

/**
 * The first day of a batch's period: the delivery day, or the next day for goods that left at or
 * after the cutoff hour — "the contract starts according to the delivery hour" (answer 5).
 */
export function periodStartsOn(delivered: LocalTime, cutoffHour: number | null | undefined): RentalDay {
  return isAfterCutoff(delivered.hour, cutoffHour) ? addDays(delivered.day, 1) : delivered.day;
}

/**
 * Whether the return day is charged when nobody decides otherwise: yes at or after the cutoff hour,
 * no before it, never with no cutoff — "the return day is not counted if the goods come back in
 * the morning, and as the branch manager decides" (answer 6).
 */
export function countsReturnDayByDefault(returned: LocalTime, cutoffHour: number | null | undefined): boolean {
  return isAfterCutoff(returned.hour, cutoffHour);
}

/** The day charged up to, exclusive: the return day when it is not counted, the next one when it is. */
export function chargedUntil(returnedOn: RentalDay, countReturnDay: boolean): RentalDay {
  return countReturnDay ? addDays(returnedOn, 1) : returnedOn;
}

/** A decision that departs from the rule is written down; one that follows it needs nothing. */
export function returnDayProblem(
  decision: boolean | null,
  byDefault: boolean,
  reason: string | null | undefined,
): 'returnDayNeedsReason' | null {
  return decision !== null && decision !== byDefault && !(reason ?? '').trim() ? 'returnDayNeedsReason' : null;
}

// --- back after the due time (the owner, 2026-10-08; answers 7 and 55) ----------------------------

/**
 * When goods are due back, on the company's clock: the last day of their period at the cutoff hour
 * — back at or after it, the return day is counted and is a late day — or, with no cutoff, the end
 * of that day, because without one the return day never counts. Only the words on the return note
 * use it; whether a note is late at all is the engine's answer (it charges overdue), never this one.
 */
export function dueBack(dueOn: RentalDay, cutoffHour: number | null | undefined): LocalTime {
  return cutoffHour === null || cutoffHour === undefined
    ? { day: addDays(dueOn, 1), hour: 0, minute: 0 }
    : { day: dueOn, hour: cutoffHour, minute: 0 };
}

/** Minutes from one wall-clock time to another on the same clock; negative when the second is earlier. */
export function minutesBetween(from: LocalTime, to: LocalTime): number {
  return (dayNumber(to.day) - dayNumber(from.day)) * 1440 + (to.hour - from.hour) * 60 + (to.minute - from.minute);
}

/**
 * Charging the late days is the rule and needs nothing; waiving them is the branch manager's
 * exception and is written down (answer 55). Mirrors `RentalLateReturn.WaiverProblem`.
 */
export function lateWaiverProblem(
  charge: boolean | null,
  reason: string | null | undefined,
  deducts = false,
): 'lateWaiverNeedsReason' | null {
  return (charge === false || (charge === true && deducts)) && !(reason ?? '').trim() ? 'lateWaiverNeedsReason' : null;
}

/**
 * A deduction from the late days (owner, 2026-10-22): in days, at least one and at most the note's
 * late days; as an amount, more than nothing, in whole units of money, and at most its late total.
 * One kind or the other, and only with a yes. Mirrors `RentalLateReturn.DeductionProblem`.
 */
export function lateDeductionProblem(
  charge: boolean | null,
  daysWaived: number | null,
  amountWaived: number | null,
  lateDays: number,
  lateAmount: number,
  decimals = 2,
): 'lateDeductionInvalid' | 'lateDeductionDaysInvalid' | 'lateDeductionAmountInvalid' | null {
  if (daysWaived === null && amountWaived === null) return null;
  if (charge !== true || (daysWaived !== null && amountWaived !== null)) return 'lateDeductionInvalid';
  if (daysWaived !== null && (!Number.isInteger(daysWaived) || daysWaived < 1 || daysWaived > lateDays)) {
    return 'lateDeductionDaysInvalid';
  }
  if (amountWaived !== null && (!(amountWaived > 0) || amountWaived > lateAmount || amountWaived !== roundMoney(amountWaived, decimals))) {
    return 'lateDeductionAmountInvalid';
  }
  return null;
}

/** The share of each late day's price a note still charges after an amount deduction; null without one. */
export function overdueShare(note: ReturnEntry): number | null {
  const waived = note.lateAmountWaived;
  const full = note.lateAmountFull;
  if (waived == null || full == null || full <= 0) return null;
  return Math.min(1, Math.max(0, (full - waived) / full));
}

// --- the paperwork a note or a first issue cannot go without (answers 10 and 16) -----------------

export type IdentityField = 'idType' | 'idNumber' | 'idIssuer' | 'idIssuedOn' | 'birthDate';

export interface SignatoryIdentity {
  idType?: string | null;
  idNumber?: string | null;
  idIssuer?: string | null;
  idIssuedOn?: string | null;
  birthDate?: string | null;
}

/** The identity papers the owner's contract lists; the values the server accepts. */
export const ID_DOCUMENT_TYPES = ['nationalCard', 'drivingLicence', 'passport'] as const;

/**
 * What the first issue note still lacks of the signatory's identity, in the order the paper prints
 * it; empty once locked or on a contract raised before the rule. `RentalRules.SignatoryIdentityMissing`.
 */
export function signatoryIdentityMissing(locked: boolean, required: boolean, identity: SignatoryIdentity): IdentityField[] {
  if (locked || !required) return [];
  const blank = (v: string | null | undefined) => !(v ?? '').trim();
  const missing: IdentityField[] = [];
  if (blank(identity.idType)) missing.push('idType');
  if (blank(identity.idNumber)) missing.push('idNumber');
  // The issuer, the issue date and the birth date are optional since 2026-10-02; filled in, they
  // are checked by `identityProblems` (core/identity.ts) instead.
  return missing;
}

export type TransportField = 'driverName' | 'driverPhone' | 'truckNumber';

/** What an issue or return note lacks of the driver and the truck. `RentalRules.TransportMissing`. */
export function transportMissing(driverName: string, driverPhone: string, truckNumber: string): TransportField[] {
  const missing: TransportField[] = [];
  if (!driverName.trim()) missing.push('driverName');
  if (!driverPhone.trim()) missing.push('driverPhone');
  if (!truckNumber.trim()) missing.push('truckNumber');
  return missing;
}

/** A line naming no damage price (zero) starts from the item's fixed value; a typed one wins. */
export function damagePriceFor(typedOnLine: number, itemValue: number | null | undefined): number {
  return typedOnLine > 0 ? typedOnLine : itemValue && itemValue > 0 ? itemValue : typedOnLine;
}

/** Days added to a period by a signed document, at the rental price. Causes no stock movement. */
export interface RentalExtension {
  id: string;
  /** The line it extends, or null for every line on the contract. */
  lineId: string | null;
  extraDays: number;
  unitPrice: number;
  raisedOn: RentalDay;
  reason?: string | null;
}

export interface RentalContractInput {
  lines: RentalLine[];
  issues: IssueBatch[];
  returns: ReturnEntry[];
  extensions: RentalExtension[];
  /** The minimum hire frozen on the contract (`chargedDays`); absent or 0 charges the days used. */
  minimumDays?: number;
}

/** A stretch of days at one price: the agreed period, an extension, or the overdue tail. */
export interface RentalSpan {
  days: number;
  unitPrice: number;
  kind: RentalChargeKind;
  extensionId: string | null;
}

export interface RentalCharge {
  kind: RentalChargeKind;
  amount: number;
  lineId: string;
  returnNoteId: string;
  issueNoteId: string | null;
  qty: number;
  days: number;
  unitPrice: number;
  extensionId: string | null;
}

export interface LineSettlement {
  lineId: string;
  charges: RentalCharge[];
  issuedQty: number;
  settledQty: number;
  stillOutQty: number;
  /** Quantity returned that was never issued: someone counted wrong. */
  overReturnedQty: number;
  /** The full agreed period on everything issued — what the customer is invoiced at issue. */
  issuedPeriodRent: number;
  /** Days of that invoice never used, given back on an early return. */
  unusedRent: number;
  isCleared: boolean;
}

export interface ContractSettlement {
  lines: LineSettlement[];
  charges: RentalCharge[];
  totals: Record<RentalChargeKind, number>;
  chargeTotal: number;
  unusedRent: number;
  stillOutQty: number;
  isClosed: boolean;
}

/** Where an early return's money goes. */
export interface RentalRelief {
  debtReduction: number;
  paymentVoucher: number;
}

export interface BatchTake {
  batch: IssueBatch;
  qty: number;
}

export interface FifoMatch {
  takes: BatchTake[];
  /** Quantity claimed that was never issued. Reported, never absorbed. */
  unmatched: number;
}

// --- days -------------------------------------------------------------------------------------

export function dayNumber(day: RentalDay): number {
  const [y, m, d] = day.split('-').map(Number);
  return Date.UTC(y, (m || 1) - 1, d || 1) / 86_400_000;
}

export function addDays(day: RentalDay, days: number): RentalDay {
  const at = new Date((dayNumber(day) + days) * 86_400_000);
  return at.toISOString().slice(0, 10);
}

/**
 * The date a note falls on in the company's own time zone. A return taken at 00:30 in Algiers is
 * stored on the previous UTC day, and counting from the UTC date charges a day nobody had.
 */
export function localDay(utcIso: string, timeZone: string): RentalDay {
  const at = new Date(utcIso);
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
    // An unknown zone must not stop a return being taken; UTC is at worst a day out.
    return at.toISOString().slice(0, 10);
  }
}

/** Return date minus issue date, minimum one: out and back the same afternoon is a day's hire. */
export function daysOut(issuedOn: RentalDay, returnedOn: RentalDay): number {
  return Math.max(1, dayNumber(returnedOn) - dayNumber(issuedOn));
}

/** The last day of a period. Back on this date is exactly the period, with nothing overdue. */
export function periodEndsOn(issuedOn: RentalDay, entitledDays: number): RentalDay {
  return addDays(issuedOn, Math.max(0, entitledDays));
}

// --- money ------------------------------------------------------------------------------------

/** The entered rate is for one unit for one day, without intermediate rounding. */
export function dailyRate(dailyUnitRate: number): number {
  return dailyUnitRate;
}

/** Quantity × rental days × daily unit rate, rounded once to currency precision. */
export function rentalAmount(dailyUnitRate: number, days: number, qty: number, decimals = 2): number {
  return roundMoney(qty * days * dailyUnitRate, decimals);
}

/** Compensation at the frozen total-damage price — the same whether it broke or vanished. */
export function damageAmount(damageUnitPrice: number, qty: number, decimals = 2): number {
  return roundMoney(damageUnitPrice * qty, decimals);
}

/** The overdue price frozen onto the line at signing: the rental price times the multiplier. */
export function overdueUnitPrice(rentUnitPrice: number, multiplier: number, decimals = 2): number {
  return roundMoney(rentUnitPrice * Math.max(0, multiplier), decimals);
}

export function multiplierFor(settings: RentalSettings, itemOverride?: number | null): number {
  return itemOverride && itemOverride > 0 ? itemOverride : settings.overdueMultiplier;
}

// --- the period ---------------------------------------------------------------------------------

/**
 * The days a batch is entitled to, in the order they are consumed: the line's own duration at the
 * rental price, then each extension at its own signed price. Whatever will not fit is overdue.
 */
export function entitlement(rentUnitPrice: number, durationDays: number, extensions: RentalExtension[]): RentalSpan[] {
  const spans: RentalSpan[] = [
    { days: Math.max(0, durationDays), unitPrice: rentUnitPrice, kind: 'Rent', extensionId: null },
  ];
  for (const extension of [...extensions].sort((a, b) => a.raisedOn.localeCompare(b.raisedOn))) {
    if (extension.extraDays > 0) {
      spans.push({ days: extension.extraDays, unitPrice: extension.unitPrice, kind: 'Rent', extensionId: extension.id });
    }
  }
  return spans;
}

export function entitledDays(spans: RentalSpan[]): number {
  return spans.reduce((sum, s) => sum + s.days, 0);
}

/**
 * Pours the days a batch was out into the entitlement in order, and prices the tail as overdue.
 * This is what makes "an extension is a decision, overdue is a consequence" arithmetic: the same
 * ten days cost the rental rate when they were agreed in advance and the multiplied rate when not.
 */
export function apportion(days: number, spans: RentalSpan[], overduePrice: number): RentalSpan[] {
  const used: RentalSpan[] = [];
  let left = Math.max(0, days);
  for (const span of spans) {
    if (left <= 0) break;
    const take = Math.min(left, span.days);
    if (take > 0) used.push({ ...span, days: take });
    left -= take;
  }
  if (left > 0) used.push({ days: left, unitPrice: overduePrice, kind: 'Overdue', extensionId: null });
  return used;
}

/** Days of the agreed period never used. The period is invoiced at issue, so this is given back. */
export function unusedDays(out: number, durationDays: number): number {
  return Math.max(0, durationDays - Math.max(0, out));
}

/**
 * The days a batch is charged for when it comes back: the days out, never fewer than the minimum
 * hire — or the agreed period when that is shorter. Mirrors `RentalPeriod.ChargedDays`.
 */
export function chargedDays(out: number, durationDays: number, minimumDays = 0): number {
  return Math.max(Math.max(0, out), Math.min(Math.max(0, minimumDays), Math.max(0, durationDays)));
}

// --- matching returns to issues -------------------------------------------------------------------

/** Oldest issue date first; two notes on the same day keep the order they were given in. */
export function oldestFirst(issues: IssueBatch[]): IssueBatch[] {
  return [...issues].sort((a, b) => a.issuedOn.localeCompare(b.issuedOn));
}

/**
 * Spreads each return's settled quantity over the batches, oldest first. Nothing on a returned
 * pipe says which note it left on, so FIFO is the only rule two people counting the same pile can
 * both arrive at — and it decides how many days each unit is charged for.
 */
export function fifoMatch(issues: IssueBatch[], settledQuantities: number[]): FifoMatch[] {
  const batches = oldestFirst(issues);
  const remaining = batches.map((b) => b.qty);
  let head = 0;

  return settledQuantities.map((quantity) => {
    const takes: BatchTake[] = [];
    let left = Math.max(0, quantity);
    while (left > 0 && head < batches.length) {
      if (remaining[head] <= 0) {
        head++;
        continue;
      }
      const take = Math.min(left, remaining[head]);
      takes.push({ batch: batches[head], qty: take });
      remaining[head] -= take;
      left -= take;
    }
    return { takes, unmatched: left };
  });
}

// --- settlement -------------------------------------------------------------------------------

/** An accessory inherits its parent's period: a coupler cannot expire before its scaffold. */
export function durationOf(line: RentalLine, allLines: RentalLine[]): number {
  if (!line.parentLineId) return line.durationDays;
  return allLines.find((l) => l.id === line.parentLineId)?.durationDays ?? line.durationDays;
}

/** Extensions raised on the contract, on the line, or — for an accessory — on its parent. */
export function extensionsFor(line: RentalLine, extensions: RentalExtension[]): RentalExtension[] {
  return extensions.filter(
    (e) => !e.lineId || e.lineId === line.id || (!!line.parentLineId && e.lineId === line.parentLineId),
  );
}

/**
 * What one line owes.
 *
 * Two things this assumes, both of them the server's rules: the full agreed period is invoiced
 * when the goods go out, which is what makes an early return a refund rather than a smaller bill;
 * and a unit declared missing or beyond repair was on site all that time, so it is charged rent up
 * to the day it was settled as well as the total-damage price.
 */
export function settleLine(
  line: RentalLine,
  durationDays: number,
  issues: IssueBatch[],
  returns: ReturnEntry[],
  extensions: RentalExtension[],
  decimals = 2,
  minimumDays = 0,
): LineSettlement {
  const batches = oldestFirst(issues);
  const notes = [...returns].sort((a, b) => a.returnedOn.localeCompare(b.returnedOn));

  // An accessory inherits its parent's days, never its parent's price.
  const priced = line.parentLineId ? extensions.map((e) => ({ ...e, unitPrice: 0 })) : extensions;
  const spans = entitlement(line.rentUnitPrice, durationDays, priced);

  // What each batch was invoiced for, and what of that invoice is still unaccounted. The return
  // that closes a batch takes whatever is left rather than a freshly rounded figure, so charges
  // and refund always add back up to the invoice instead of drifting a centime.
  const billedLeft = new Map<string, number>();
  const batchQty = new Map<string, number>();
  const takenFrom = new Map<string, number>();
  for (const batch of batches) {
    const key = batch.issueNoteId;
    billedLeft.set(key, (billedLeft.get(key) ?? 0) + rentalAmount(line.rentUnitPrice, durationDays, batch.qty, decimals));
    batchQty.set(key, (batchQty.get(key) ?? 0) + batch.qty);
    takenFrom.set(key, 0);
  }

  const matches = fifoMatch(batches, notes.map(settledQty));
  const charges: RentalCharge[] = [];
  let unusedRent = 0;
  let settled = 0;
  let overReturned = 0;

  notes.forEach((note, i) => {
    const match = matches[i];
    settled += match.takes.reduce((sum, t) => sum + t.qty, 0);
    overReturned += match.unmatched;

    for (const take of match.takes) {
      // Back before the minimum hire: charged the minimum, so only the days beyond it come back.
      const out = chargedDays(daysOut(take.batch.issuedOn, rentStopsOn(note, take.batch.issuedOn)), durationDays, minimumDays);
      const used = apportion(out, spans, line.overdueUnitPrice);

      for (const apportioned of used) {
        let span = apportioned;
        // Late days the branch waived on this note: asked, answered no. The rent before them stands.
        if (span.kind === 'Overdue' && note.waivesOverdue) continue;
        // Yes, less a deduction: the last late days go, or every late day costs a share of its price.
        if (span.kind === 'Overdue' && note.lateDaysWaived != null) {
          span = { ...span, days: Math.max(0, span.days - Math.max(0, note.lateDaysWaived)) };
          if (span.days === 0) continue;
        }
        const share = span.kind === 'Overdue' ? overdueShare(note) : null;
        if (share !== null) span = { ...span, unitPrice: span.unitPrice * share };
        const amount = rentalAmount(span.unitPrice, span.days, take.qty, decimals);
        // An accessory rents at zero, so it produces no rent line at all.
        if (amount === 0) continue;
        charges.push({
          kind: span.kind,
          amount,
          lineId: line.id,
          returnNoteId: note.returnNoteId,
          issueNoteId: take.batch.issueNoteId,
          qty: take.qty,
          days: span.days,
          unitPrice: span.unitPrice,
          extensionId: span.extensionId,
        });
      }

      // Only the line's own period was invoiced in advance; extension days are charged as they
      // are consumed, so an extension signed and not used costs nothing and refunds nothing.
      const baseDays = used.filter((s) => s.kind === 'Rent' && !s.extensionId).reduce((sum, s) => sum + s.days, 0);
      const earnedBase = rentalAmount(line.rentUnitPrice, baseDays, take.qty, decimals);

      const key = take.batch.issueNoteId;
      takenFrom.set(key, (takenFrom.get(key) ?? 0) + take.qty);
      const closesBatch = (takenFrom.get(key) ?? 0) >= (batchQty.get(key) ?? 0);
      const room = Math.max(0, roundMoney((billedLeft.get(key) ?? 0) - earnedBase, decimals));
      const unused = closesBatch ? room : Math.min(room, rentalAmount(line.rentUnitPrice, unusedDays(out, durationDays), take.qty, decimals));
      billedLeft.set(key, roundMoney((billedLeft.get(key) ?? 0) - earnedBase - unused, decimals));
      unusedRent = roundMoney(unusedRent + unused, decimals);
    }

    charges.push(...compensation(line, note, decimals));
  });

  const issuedQty = batches.reduce((sum, b) => sum + b.qty, 0);
  return {
    lineId: line.id,
    charges,
    issuedQty,
    settledQty: settled,
    stillOutQty: Math.max(0, issuedQty - settled),
    overReturnedQty: overReturned,
    issuedPeriodRent: batches.reduce(
      (sum, b) => roundMoney(sum + rentalAmount(line.rentUnitPrice, durationDays, b.qty, decimals), decimals),
      0,
    ),
    unusedRent,
    // A line is clear when nothing of it is out: issued = returned + settled as lost or damaged.
    isCleared: issuedQty - settled <= 0,
  };
}

/** Everything one return note settles, whether it walked back in or was written off as lost. */
export function settledQty(note: ReturnEntry): number {
  return (note.qtyOk || 0) + (note.qtyPartialDamage || 0) + (note.qtyTotalDamage || 0) + (note.qtyMissing || 0);
}

/** What a return charges for condition rather than for time. None of it depends on FIFO. */
function compensation(line: RentalLine, note: ReturnEntry, decimals: number): RentalCharge[] {
  const charges: RentalCharge[] = [];
  const base = { lineId: line.id, returnNoteId: note.returnNoteId, issueNoteId: null, days: 0, extensionId: null };

  if (note.maintenanceValue) {
    charges.push({
      ...base,
      kind: 'Maintenance',
      amount: roundMoney(note.maintenanceValue, decimals),
      qty: note.qtyPartialDamage,
      unitPrice: note.maintenanceValue,
    });
  }
  if (note.qtyTotalDamage > 0) {
    charges.push({
      ...base,
      kind: 'Damage',
      amount: damageAmount(line.damageUnitPrice, note.qtyTotalDamage, decimals),
      qty: note.qtyTotalDamage,
      unitPrice: line.damageUnitPrice,
    });
  }
  // Forty couplers short is forty charged, at the price signed on the contract.
  if (note.qtyMissing > 0) {
    charges.push({
      ...base,
      kind: 'Missing',
      amount: damageAmount(line.damageUnitPrice, note.qtyMissing, decimals),
      qty: note.qtyMissing,
      unitPrice: line.damageUnitPrice,
    });
  }
  return charges;
}

/**
 * The pennies an amount deduction leaves: each late charge is rounded on its own, so the note's
 * largest one takes the difference and the note charges exactly its late total less the deduction.
 * A gap wider than the rounding means the late days changed since the answer, and is left alone.
 * Mirrors `RentalLateReturn.LandDeductions`.
 */
function landDeductions(lines: LineSettlement[], returns: ReturnEntry[], decimals: number): void {
  const unit = 1 / 10 ** decimals;
  const targets = new Map<string, number>();
  for (const r of returns) {
    if (r.lateAmountWaived != null && r.lateAmountFull != null) {
      targets.set(r.returnNoteId, roundMoney(r.lateAmountFull - r.lateAmountWaived, decimals));
    }
  }
  for (const [noteId, target] of targets) {
    const late = lines.flatMap((l) => l.charges).filter((c) => c.returnNoteId === noteId && c.kind === 'Overdue');
    if (late.length === 0) continue;
    const rest = roundMoney(target - late.reduce((sum, c) => sum + c.amount, 0), decimals);
    if (rest === 0 || Math.abs(rest) > unit * late.length + 1e-9) continue;
    const largest = late.reduce((a, b) => (b.amount > a.amount ? b : a));
    largest.amount = roundMoney(largest.amount + rest, decimals);
  }
}

export function settleContract(contract: RentalContractInput, decimals = 2): ContractSettlement {
  const lines = contract.lines.map((line) =>
    settleLine(
      line,
      durationOf(line, contract.lines),
      contract.issues.filter((i) => i.lineId === line.id),
      contract.returns.filter((r) => r.lineId === line.id),
      extensionsFor(line, contract.extensions),
      decimals,
      contract.minimumDays ?? 0,
    ),
  );
  landDeductions(lines, contract.returns, decimals);

  const charges = lines.flatMap((l) => l.charges);
  const totals = RENTAL_CHARGE_KINDS.reduce(
    (acc, kind) => {
      acc[kind] = roundMoney(
        charges.filter((c) => c.kind === kind).reduce((sum, c) => sum + c.amount, 0),
        decimals,
      );
      return acc;
    },
    {} as Record<RentalChargeKind, number>,
  );

  return {
    lines,
    charges,
    totals,
    chargeTotal: roundMoney(charges.reduce((sum, c) => sum + c.amount, 0), decimals),
    unusedRent: roundMoney(lines.reduce((sum, l) => sum + l.unusedRent, 0), decimals),
    stillOutQty: lines.reduce((sum, l) => sum + l.stillOutQty, 0),
    // Quantities never issued have no bearing; a contract that issued nothing has not started.
    isClosed: lines.some((l) => l.issuedQty > 0) && lines.every((l) => l.isCleared),
  };
}

/**
 * Where an early return's unused period goes. On account and unpaid, it comes off the debt and no
 * cash moves; once the account is square the rest is a real payment voucher.
 */
export function relieve(unusedRent: number, outstandingBalance: number, decimals = 2): RentalRelief {
  const relief = Math.max(0, unusedRent);
  const offset = Math.min(relief, Math.max(0, outstandingBalance));
  return { debtReduction: roundMoney(offset, decimals), paymentVoucher: roundMoney(relief - offset, decimals) };
}

// --- what may be raised at all ------------------------------------------------------------------

export type RentalProblem =
  | 'quantityRequired'
  | 'exceedsContracted'
  | 'exceedsIssued'
  | 'negativeQuantity'
  | 'negativeMaintenance'
  | 'maintenanceNeedsDamagedUnits'
  | 'daysRequired'
  | 'negativePrice'
  | 'retroactiveNeedsManager'
  | 'retroactiveNeedsReason';

/** A contract locks as soon as its first issue note is raised. After that, change is a document. */
export function isLocked(issues: IssueBatch[]): boolean {
  return issues.length > 0;
}

export function issueProblem(line: RentalLine, alreadyIssued: number, qty: number): RentalProblem | null {
  if (qty <= 0) return 'quantityRequired';
  if (alreadyIssued + qty > line.qty) return 'exceedsContracted';
  return null;
}

/**
 * A return bigger than what is still out is a counting mistake, and it has to stop here: once it is
 * saved the FIFO match has nothing to charge it against.
 */
export function returnProblem(note: ReturnEntry, issuedQty: number, alreadySettled: number): RentalProblem | null {
  if (note.qtyOk < 0 || note.qtyPartialDamage < 0 || note.qtyTotalDamage < 0 || note.qtyMissing < 0) {
    return 'negativeQuantity';
  }
  if (settledQty(note) <= 0) return 'quantityRequired';
  if (note.maintenanceValue < 0) return 'negativeMaintenance';
  if (note.maintenanceValue > 0 && note.qtyPartialDamage <= 0) return 'maintenanceNeedsDamagedUnits';
  if (alreadySettled + settledQty(note) > issuedQty) return 'exceedsIssued';
  return null;
}

/** Signed after the period ran out: allowed, but it turns overdue days into rented ones. */
export function isRetroactive(extension: RentalExtension, periodEnds: RentalDay): boolean {
  return extension.raisedOn > periodEnds;
}

/**
 * Left open to everyone, every overdue quietly becomes an extension and the overdue line stops
 * meaning anything — which is the whole reason the two are separate documents.
 */
export function extensionProblem(
  extension: RentalExtension,
  periodEnds: RentalDay,
  isManager: boolean,
): RentalProblem | null {
  if (extension.extraDays < 1) return 'daysRequired';
  if (extension.unitPrice < 0) return 'negativePrice';
  if (!isRetroactive(extension, periodEnds)) return null;
  if (!isManager) return 'retroactiveNeedsManager';
  if (!extension.reason?.trim()) return 'retroactiveNeedsReason';
  return null;
}

/** The first batch out is the one about to expire, so that is the deadline a signature must beat. */
export function soonestPeriodEnd(openBatches: IssueBatch[], days: number): RentalDay | null {
  let soonest: RentalDay | null = null;
  for (const batch of openBatches) {
    const end = periodEndsOn(batch.issuedOn, days);
    if (!soonest || end < soonest) soonest = end;
  }
  return soonest;
}

/**
 * The deposit is built and switched off. While off it is invisible — no field, no zero line, no
 * report column — and it is never netted against what a contract owes: it is a liability held on
 * the customer's behalf, not a payment.
 */
export function depositHeld(settings: RentalSettings, contractDeposit: number): number {
  return settings.depositEnabled ? Math.max(0, contractDeposit) : 0;
}

export function depositVisible(settings: RentalSettings): boolean {
  return settings.depositEnabled;
}
