// Small helpers only — the name, the day and the head's fiscal identifiers: the settings page imports
// the field list from here, and the rental paper itself stays in the chunk that previews or prints it.
import type { AdminCustomer } from '../../core/admin.service';
import type { RegionalSettings } from '../../core/models';
import type { RentalDay } from '../../core/rentals';
import type { RentalContract } from '../backoffice/rentals/rentals-api.service';
import { formatRentalDay } from '../backoffice/rentals/rental-view';
import { formatPhone } from '../../core/phone';
import { paperIssuer, type PrintContext } from './print-context';

/**
 * Fields inside a rental-contract wording (owner, 2026-10-08: "the admin can create the rental
 * contract template and preview it"). The admin writes `{customer}` in the text and the paper
 * prints the contract's own customer there.
 *
 * <p>The token is language-neutral — the same `{customer}` in the French, Arabic and English text —
 * and is stored in the wording as typed: the server keeps a wording as plain text and counts a
 * change to it like any other. Nothing is substituted until print time, so a wording reprinted
 * years later fills in that contract's figures, never today's.</p>
 *
 * <p>Every value comes from the contract and the context the sheet already prints from: money
 * through the context's `money()` (the regional service, the document language, Latin digits),
 * the date through `day()`, the company and the branch by their names in the document language.
 * This file writes no figure and no word of its own.</p>
 */

/**
 * The fields a contract wording may carry, in the order the editor offers them. The second group
 * (owner, 2026-10-10) is what the company's own contract 12547 says in its text: where the company
 * sits, the signatory's whole identity, the lorry, the site's address, what the goods are worth if
 * they do not come back, the period, and `{items}` — the place the items table goes.
 */
export const CONTRACT_FIELDS = [
  'company',
  'contractNumber',
  'signedOn',
  'customer',
  'customerCode',
  'signatory',
  'signatoryId',
  'branch',
  'site',
  'deposit',
  'rentTotal',
  'companyAddress',
  'customerAddress',
  'customerPhone',
  'signatoryBirthDate',
  'signatoryIdType',
  'signatoryIdIssuer',
  'signatoryIdIssuedOn',
  'truckType',
  'truckNumber',
  'truckOwnerPhone',
  'driverName',
  'driverPhone',
  'siteAddress',
  'damageTotal',
  'startDate',
  'endDate',
  'durationDays',
  'items',
] as const;

export type ContractField = (typeof CONTRACT_FIELDS)[number];

/**
 * The marker for the items table. Not a value: a wording that carries it is the contract's whole
 * body — the text before it, the table, the text after it — instead of terms at the foot.
 */
export const ITEMS_FIELD = 'items' as const;
export const ITEMS_TOKEN = `{${ITEMS_FIELD}}`;

/**
 * One of the company's fiscal identifiers as a field — `{companyNif}`, `{companyRc}` — named after
 * the country profile's key, so a profile with other identifiers offers its own. The value is the
 * branch's own identifier where it has one and the company's otherwise, as the head prints it.
 */
export function companyIdField(key: string): string {
  return 'company' + key.charAt(0).toUpperCase() + key.slice(1);
}

/** Every field the editor offers for this profile: the fixed ones, the company's identifiers after its address. */
export function contractFieldsFor(regional: Pick<RegionalSettings, 'fiscalIds'>): string[] {
  const fields: string[] = [...CONTRACT_FIELDS];
  const at = fields.indexOf('companyAddress') + 1;
  fields.splice(at, 0, ...(regional.fiscalIds ?? []).map((id) => companyIdField(id.key)));
  return fields;
}

/** Every value field by name, plus `company<Key>` for each of the profile's identifiers. */
export type ContractFieldValues = Record<Exclude<ContractField, typeof ITEMS_FIELD>, string> & Record<string, string>;

/**
 * The period a contract's lines run, as the paper prints it: from the first day to the last, and
 * the agreed days. Worked out by the sheet from its lines and issue notes; null where it cannot.
 */
export interface ContractPeriod {
  startDay: RentalDay | null;
  endDay: RentalDay | null;
  durationDays: number | null;
  /** Σ quantity × total-damage price: "the value of the goods if they are not returned". */
  damageTotal: number;
}

/**
 * The lorry as the first issue note recorded it (owner, 2026-10-10: the driver and the truck are
 * typed on إذن الإخراج, not on the contract). Null when no note carried the goods in a lorry.
 */
export interface ContractTransport {
  truckNumber: string | null;
  driverName: string | null;
  driverPhone: string | null;
}

/**
 * What a field prints when this contract has no value for it: a contract with no deposit, no site
 * or no signatory on record. A dash, as the place block already prints for a branch it cannot
 * name — never a blank, which would read as a sentence with a word missing, and never the token,
 * which would read as a fault in the paper. A zero deposit is no deposit: "0.00" would read as a
 * deposit taken and lost (the same rule as the deposit line).
 */
export const MISSING_FIELD = '—';

/** `{name}`: a letter, then letters or digits. Anything else between braces is not a field. */
const FIELD_TOKEN = /\{([A-Za-z][A-Za-z0-9]*)\}/g;

/** The token the editor inserts for a field. */
export function fieldToken(field: string): string {
  return `{${field}}`;
}

/**
 * A value set inside the sentence in its own direction (first-strong isolate): an Arabic name in a
 * French clause, a contract number or a date in an Arabic one, each keeps its own order, and the
 * punctuation around the field stays the sentence's.
 */
export function isolateField(value: string): string {
  return `\u2068${value}\u2069`;
}

/**
 * The wording with every field it names filled in.
 *
 * <p>A name the values know prints its value, isolated, or {@link MISSING_FIELD} when the value is
 * empty. A name they do not know — a typo, a field from a later version, a brace the owner meant
 * literally — prints exactly as typed: never blank and never a translation key.</p>
 */
export function fillFields(text: string, values: Readonly<Record<string, string | null | undefined>>): string {
  if (!text) return text;
  return text.replace(FIELD_TOKEN, (token, name: string) => {
    if (!Object.prototype.hasOwnProperty.call(values, name)) return token;
    const value = (values[name] ?? '').trim();
    return value ? isolateField(value) : MISSING_FIELD;
  });
}

/**
 * One contract's value for every field, in the document language; '' where the contract has none
 * (which {@link fillFields} prints as a dash).
 *
 * @param customer The customer row, for its code; the name is the one frozen on the contract.
 * @param rentTotal The period's rent as the sheet totals it, so the clause and the table agree.
 */
export function contractFieldValues(
  ctx: Pick<PrintContext, 'lang' | 'store' | 'branches' | 'money' | 'day' | 'name'> &
    Partial<Pick<PrintContext, 'regional' | 'branchId' | 't'>>,
  contract: RentalContract,
  customer: (Pick<AdminCustomer, 'fullName' | 'code'> & Partial<Pick<AdminCustomer, 'address' | 'phone'>>) | null,
  rentTotal: number,
  period: ContractPeriod | null = null,
  transport: ContractTransport | null = null,
): ContractFieldValues {
  const branch = ctx.branches.find((b) => b.id === contract.branchId);
  const issuer = paperIssuer(ctx, contract.branchId);
  const deposit = contract.depositAmount ?? 0;
  const values: ContractFieldValues = {
    company: issuer.name,
    contractNumber: contract.number,
    signedOn: contract.signedAtUtc ? ctx.day(contract.signedAtUtc) : '',
    customer: contract.customerName ?? customer?.fullName ?? '',
    customerCode: customer?.code ?? '',
    signatory: contract.signatoryName ?? '',
    signatoryId: contract.signatoryIdNumber ?? '',
    branch: branch ? ctx.name(branch) : '',
    site: contract.siteName ?? '',
    deposit: deposit > 0 ? ctx.money(deposit) : '',
    rentTotal: ctx.money(rentTotal),
    companyAddress: issuer.address,
    customerAddress: customer?.address ?? '',
    // As it reads, +213 658 41 77 30 (owner, 2026-11-17); a phone from before the rule as typed.
    customerPhone: customer?.phone ? formatPhone(customer.phone, ctx.regional?.phone) : '',
    signatoryBirthDate: day(contract.signatoryBirthDate, ctx.lang),
    signatoryIdType: contract.signatoryIdType && ctx.t ? ctx.t('rent.id.kind.' + contract.signatoryIdType) : '',
    signatoryIdIssuer: contract.signatoryIdIssuer ?? '',
    signatoryIdIssuedOn: day(contract.signatoryIdIssuedOn, ctx.lang),
    truckType: contract.truckType ?? '',
    // The contract's own lorry when it names one; otherwise the one the first issue note recorded.
    truckNumber: contract.truckNumber?.trim() || transport?.truckNumber || '',
    truckOwnerPhone: contract.truckOwnerPhone ?? '',
    driverName: transport?.driverName ?? '',
    driverPhone: transport?.driverPhone ?? '',
    siteAddress: contract.siteAddress ?? '',
    damageTotal: period && period.damageTotal > 0 ? ctx.money(period.damageTotal) : '',
    startDate: day(period?.startDay, ctx.lang),
    endDate: day(period?.endDay, ctx.lang),
    durationDays: period?.durationDays ? String(period.durationDays) : '',
  };
  if (ctx.regional) {
    const printed = new Map(issuer.fiscalIds.map((l) => [l.label, l.value]));
    for (const id of ctx.regional.fiscalIds) values[companyIdField(id.key)] = printed.get(id.code) ?? '';
  }
  return values;
}

/** A 'YYYY-MM-DD' day as the paper writes it, or '' (a dash in the text) when there is none. */
function day(value: RentalDay | null | undefined, lang: PrintContext['lang']): string {
  return value ? formatRentalDay(value, lang) : '';
}

/**
 * A wording split at the items table: the text before `{items}`, and after it. Null when the
 * wording carries no table, and then it prints as terms at the foot, as every wording did before.
 * Only the first marker places the table; a second is dropped rather than printed as braces.
 */
export function splitAtItems(text: string): { before: string; after: string } | null {
  const at = text.indexOf(ITEMS_TOKEN);
  if (at < 0) return null;
  return {
    before: text.slice(0, at).trimEnd(),
    after: text.slice(at + ITEMS_TOKEN.length).split(ITEMS_TOKEN).join('').trimStart(),
  };
}

/**
 * `token` typed over the selection `[start, end)` of `text`, and where the caret goes after it;
 * null when the text would then be longer than `max` (the server's cap on one wording).
 * Out-of-range positions are clamped, so a box that never had the focus takes it at the end.
 */
export function insertToken(
  text: string,
  start: number | null | undefined,
  end: number | null | undefined,
  token: string,
  max: number,
): { text: string; caret: number } | null {
  const from = clamp(start ?? text.length, text.length);
  const to = Math.max(from, clamp(end ?? from, text.length));
  const next = text.slice(0, from) + token + text.slice(to);
  if (next.length > max) return null;
  return { text: next, caret: from + token.length };
}

function clamp(at: number, length: number): number {
  return Number.isFinite(at) ? Math.min(Math.max(0, Math.trunc(at)), length) : length;
}
