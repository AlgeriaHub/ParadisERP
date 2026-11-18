import { AdminBranch, AdminCustomer, AdminPaymentMethod, AdminStore } from '../../core/admin.service';
import { amountInWords, currencyUnitNames } from '../../core/amount-words';
import { DocumentIssuer, documentIssuer } from '../../core/document-issuer';
import { I18nService } from '../../core/i18n.service';
import { Lang, LocalizedText, RegionalSettings } from '../../core/models';
import { localizedName } from '../../core/multilingual';
import { formatDay } from '../backoffice/backoffice-format';
import { DocumentTextKey, StoreDocumentsDto } from './store-documents-api.service';
import { formatNumber, isNumberFormat } from '../../core/number-format';
import { formatPhone } from '../../core/phone';

/**
 * Everything a printed document needs to say its figures and words in the document's
 * language, gathered once by the page and handed to the frame and the document.
 *
 * <p>The document language is not the interface language: a clerk working in French prints a
 * contractor's invoice in Arabic. So nothing here reads `I18nService.lang()`; every formatter
 * is the `…In(lang, …)` form, closed over the language chosen at print time.</p>
 *
 * <p>Latin digits throughout — `formatMoney`, `formatDay` and `dateTimeIn` all fix
 * `numberingSystem: 'latn'`, and `qty` does the same — because a figure on paper is read by
 * the tax office, the bank and the customer's own bookkeeper, not all of whom read Arabic-Indic
 * digits, and because it is what every receipt this system has ever printed did.</p>
 */
export interface PrintContext {
  lang: Lang;
  dir: 'rtl' | 'ltr';
  regional: RegionalSettings;
  store: AdminStore | null;
  documents: StoreDocumentsDto | null;
  branches: AdminBranch[];
  customers: AdminCustomer[];
  methods: AdminPaymentMethod[];
  /**
   * The branch the document was raised at, when it has one: the header prints that branch's name,
   * address, phone and fiscal identifiers, the main branch's where it has none (`paperIssuer`).
   * Null for a paper that belongs to no branch (a statement, the ageing board), which prints the
   * company's.
   */
  branchId: string | null;
  t(key: string, params?: Record<string, string | number>): string;
  text(value: LocalizedText | undefined): string;
  money(value: number): string;
  /** A quantity: up to two decimals, Latin digits, no unit. */
  qty(value: number): string;
  /** A date with no time; a voucher is dated to the day. */
  day(iso: string | null | undefined): string;
  dateTime(iso: string | null | undefined): string;
  rateLabel(rate: number): string;
  /** The amount in words, or '' when the profile names no units in this language. */
  words(value: number): string;
  /** The reader's name of a record with the three names. */
  name(value: { nameAr: string; nameFr: string; nameEn: string } | null | undefined): string;
  /**
   * A title key in the company's other title languages (`StoreDocumentsDto.titleLanguages`), in
   * the order the admin set: the owner's paper heads a note "BON DE RETOUR" with the Arabic beside
   * it. Never the document language itself, and only languages this company has enabled.
   */
  alsoTitled(key: string): AlsoTitled[];
  /** Whether a language is written right to left, for a fragment in a language not the sheet's. */
  isRtl(lang: string): boolean;
}

/** One extra rendering of a document title, with the direction its own script needs. */
export interface AlsoTitled {
  lang: Lang;
  dir: 'rtl' | 'ltr';
  text: string;
}

export interface PrintData {
  store: AdminStore | null;
  documents: StoreDocumentsDto | null;
  branches: AdminBranch[];
  customers: AdminCustomer[];
  methods: AdminPaymentMethod[];
  /** See `PrintContext.branchId`; the page reads it off the document with `documentBranchId`. */
  branchId?: string | null;
}

/** Rows read back from SQL Server may arrive without the trailing Z; `new Date()` then reads local time. */
export function utc(iso: string): string {
  return /(Z|[+-]\d\d:?\d\d)$/.test(iso) ? iso : `${iso}Z`;
}

export function buildPrintContext(i18n: I18nService, regional: RegionalSettings, lang: Lang, data: PrintData): PrintContext {
  const units = currencyUnitNames(regional, lang);
  // The paper writes figures as the screen does: the company's number format (core/number-format.ts).
  const qtyFormat = {
    format: (value: number) =>
      formatNumber(value, lang, { maximumFractionDigits: 2 }, isNumberFormat(regional.numberFormat) ? regional.numberFormat : undefined),
  };
  return {
    lang,
    dir: i18n.isRtlLang(lang) ? 'rtl' : 'ltr',
    regional,
    ...data,
    branchId: data.branchId ?? null,
    t: (key, params) => i18n.tIn(lang, key, params),
    text: (value) => i18n.textIn(lang, value),
    money: (value) => i18n.moneyIn(lang, value),
    qty: (value) => qtyFormat.format(value),
    day: (iso) => formatDay(iso ? utc(iso) : null, lang, regional.timeZone),
    dateTime: (iso) => (iso ? i18n.dateTimeIn(lang, utc(iso)) : '—'),
    rateLabel: (rate) => i18n.rateLabelIn(lang, rate),
    words: (value) => amountInWords(value, lang, units, regional.currencyDecimals),
    name: (value) => localizedName(value, lang),
    isRtl: (l) => i18n.isRtlLang(l),
    alsoTitled: (key) => {
      const wanted = data.documents?.titleLanguages ?? [];
      if (!wanted.length) return [];
      const enabled = new Set(i18n.langs().map((l) => l.code));
      return wanted
        .filter((l) => l !== lang && enabled.has(l))
        .map((l) => ({ lang: l, dir: i18n.isRtlLang(l) ? 'rtl' : 'ltr', text: i18n.tIn(l, key) }));
    },
  };
}

// ---------- the blocks every document shares ----------

export interface LabelledLine {
  label: string;
  value: string;
  /**
   * A phone, a code, a tax id, an address on the web: set left to right even on an Arabic page,
   * or "0550 12 34 56" prints as "56 34 12 0550". Names and addresses are left to the page.
   */
  ltr?: boolean;
}

/**
 * The identity a paper raised at `branchId` prints in its head: that branch's, then the main
 * trading branch's, then the company's, field by field (core/document-issuer.ts).
 */
export function paperIssuer(
  ctx: Pick<PrintContext, 'branches' | 'store' | 'lang'> & Partial<Pick<PrintContext, 'regional'>>,
  branchId: string | null | undefined,
): DocumentIssuer {
  return documentIssuer(branchId, ctx.branches, ctx.store, ctx.lang, ctx.regional?.fiscalIds);
}

/** A left-to-right number inside a sentence that may be Arabic: isolated so its digits keep their order. */
export function isolate(text: string): string {
  return `\u2066${text}\u2069`;
}

/** The company's name in the document language, with the receipt's fallback chain. */
export function storeName(store: AdminStore | null, lang: Lang): string {
  if (!store) return '';
  return localizedName(store, lang);
}

/**
 * Every fiscal identifier the profile defines that the company has a value for, with its code
 * — NIF 000…, RC 16/00-…. The profile's `showOnReceipt` flag governs the 80 mm slip only; an
 * invoice states every identifier the company has, because that is what the buyer's own
 * filing needs from it.
 *
 * <p>With a branch, each identifier is the branch's own when it has one and the company's
 * otherwise, key by key (owner, 2026-09-30: every branch has its own commercial register and NIF
 * and prints them). A branch that filled in only its register still prints the company's NIS.</p>
 */
export function fiscalIdLines(
  regional: RegionalSettings,
  store: AdminStore | null,
  branch?: Pick<AdminBranch, 'fiscalIds'> | null,
): LabelledLine[] {
  const company = store?.fiscalIds ?? {};
  const own = branch?.fiscalIds ?? {};
  return regional.fiscalIds
    .map((id) => ({ label: id.code, value: (own[id.key] ?? '').trim() || (company[id.key] ?? '').trim() }))
    .filter((line) => line.value);
}

/**
 * The branch a loaded document was raised at: the `branchId` of the first record on it that has
 * one — the invoice, the note, the voucher, the contract, the payroll run — or, for a stock
 * transfer, the sending branch (`fromBranchId`). Generic on purpose, so a document kind added to
 * the print page later reaches the header's branch identity without a case of its own. An issue or
 * return note travels with its contract's documents, which carry no branch at their top level, so
 * the note's own branch — where the goods moved — is the one read.
 */
export function documentBranchId(doc: object | null | undefined): string | null {
  if (!doc) return null;
  for (const value of Object.values(doc)) {
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      const record = value as { branchId?: unknown; fromBranchId?: unknown };
      const id = record.branchId ?? record.fromBranchId;
      if (typeof id === 'string' && id) return id;
    }
  }
  return null;
}

/**
 * The company logo as the header prints it, or '' when none is set. Only an inline image: the
 * print page turns the signed-in link into one before the sheet is drawn (PrintPageComponent).
 */
export function logoSource(documents: StoreDocumentsDto | null): string {
  const url = documents?.logoUrl ?? '';
  return url.startsWith('data:image/') ? url : '';
}

/**
 * The kind's footer text in the document language — the owner's own wording, or nothing. `kind` may
 * be a named rental-contract wording's key (PinSay #302), whose text is kept beside the kinds.
 */
export function documentText(documents: StoreDocumentsDto | null, kind: DocumentTextKey, ctx: Pick<PrintContext, 'text'>): string {
  const byLang = documents?.documentTexts?.[kind];
  return byLang ? ctx.text(byLang) : '';
}

/**
 * The party block of a document: name, code, address, phone, tax id, each line only when it
 * has a value. `frozen` are the fields an invoice snapshotted at issue; they win over the
 * customer row, which may have moved since, and the row fills only what was not recorded.
 */
export function partyLines(
  ctx: PrintContext,
  customer: AdminCustomer | null | undefined,
  frozen: { name?: string | null; address?: string | null; taxId?: string | null } = {},
): LabelledLine[] {
  const lines: LabelledLine[] = [];
  const name = frozen.name ?? customer?.fullName ?? '';
  if (name) lines.push({ label: '', value: name });
  if (customer?.code) lines.push({ label: ctx.t('backoffice.customers.code'), value: customer.code, ltr: true });
  const address = frozen.address ?? customer?.address ?? '';
  if (address) lines.push({ label: ctx.t('backoffice.customers.address'), value: address });
  if (customer?.phone) lines.push({ label: ctx.t('backoffice.customers.phone'), value: formatPhone(customer.phone, ctx.regional.phone), ltr: true });
  const taxId = frozen.taxId ?? customer?.taxId ?? '';
  if (taxId) lines.push({ label: ctx.text(ctx.regional.customerTaxIdLabels), value: taxId, ltr: true });
  return lines;
}

/** Address, phone, e-mail and site of the company, only the ones it has. */
export function companyLines(ctx: PrintContext, issuer?: DocumentIssuer | null): LabelledLine[] {
  const store = ctx.store;
  const lines: LabelledLine[] = [];
  const address = issuer ? issuer.address : store
    ? ctx.lang === 'ar'
      ? store.addressAr || store.addressFr || store.address
      : ctx.lang === 'en'
        ? store.addressEn || store.addressFr || store.address
        : store.addressFr || store.address
    : '';
  if (address) lines.push({ label: '', value: address });
  const phone = issuer ? issuer.phone : store?.phone;
  if (phone) lines.push({ label: ctx.t('receipt.phone'), value: phone, ltr: true });
  if (ctx.documents?.email) lines.push({ label: ctx.t('store.email'), value: ctx.documents.email, ltr: true });
  if (ctx.documents?.website) lines.push({ label: '', value: ctx.documents.website, ltr: true });
  return lines;
}

/** The branch a document was raised at, as one line, or '' when the branch is unknown to this session. */
export function branchLine(ctx: PrintContext, branchId: string): string {
  const branch = ctx.branches.find((b) => b.id === branchId);
  if (!branch) return '';
  const name = ctx.name(branch);
  const address = ctx.lang === 'ar'
    ? branch.addressAr || branch.addressFr || branch.address
    : ctx.lang === 'en'
      ? branch.addressEn || branch.addressFr || branch.address
      : branch.addressFr || branch.address;
  return address ? `${name} — ${address}` : name;
}

/** True when the text carries an Arabic-Indic or Extended Arabic-Indic digit — the print tests forbid them. */
export function hasNonLatinDigits(text: string): boolean {
  return /[٠-٩۰-۹]/.test(text);
}
