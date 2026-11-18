import { Injectable, inject } from '@angular/core';
import { ApiError, ApiService } from '../../../core/api.service';
import { PayBasis, PayrollSettings, TextByLang } from './payroll-api.service';

/**
 * The payroll run's wire shapes, its one client, and the arithmetic the run screens and the
 * payslip would otherwise each solve a little differently.
 *
 * <p>No Angular template and no component state in here, so every rule below is testable on its
 * own (`payroll-run-view.spec.ts`). The client is the exception and earns its place: the run
 * list, the run screen and the print tab all read the same routes, and three copies of a URL is
 * three places a `take` can drift.</p>
 *
 * <p>Two rules run through the whole file. <b>Every enum crosses as a name</b> — the API
 * registers no string-enum converter and shapes them explicitly — so the translation keys are
 * spelled exactly as the server spells them (`Draft`, `Allowance`, `Recorded`) and an unknown
 * value prints raw rather than as a missing key. And <b>a label belongs to the run</b>: an
 * allowance renamed in June must not rename May's payslips, so labels resolve from a label book
 * the caller builds, in the document's language, falling back to French and then to the key.</p>
 */

// ---------------------------------------------------------------------------------------------
// the wire
// ---------------------------------------------------------------------------------------------

/** The lifecycle, as the server spells it. */
export type PayrollRunStatus = 'Draft' | 'Locked' | 'Approved' | 'Paid' | 'Reversed';

/** A branch that has opened no run at all says so in a word of its own. */
export type SummaryStatus = PayrollRunStatus | 'None';

export type PayrollElementKind = 'Base' | 'Absence' | 'Allowance' | 'Overtime' | 'Deduction' | 'Adjustment';

/** Where the days on a payslip came from; `None` means nothing was captured, not zero days. */
export type AttendanceSource = 'Declared' | 'Recorded' | 'None';

/** One line of a payslip: what it is, how much, and — for time-based lines — how much of what. */
export interface PayrollElement {
  key: string;
  kind: PayrollElementKind;
  amount: number;
  /** Days or hours; null on a line that is a flat figure. */
  qty?: number | null;
  /** The day rate or the hourly rate the quantity was priced at. */
  rate?: number | null;
  inSocialBase: boolean;
  inTaxBase: boolean;
  sortOrder: number;
}

/**
 * One employee's payslip inside a run. Every name and number is the snapshot taken at compute
 * time: an employee edited in June does not rewrite May's paper.
 */
export interface PayrollRunLine {
  id: string;
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  socialNumber?: string | null;
  branchId: string;
  payBasis: PayBasis;
  baseRate: number;
  daysPresent: number;
  paidLeaveDays: number;
  unpaidAbsenceDays: number;
  attendanceSource: AttendanceSource;
  gross: number;
  socialBase: number;
  socialEmployee: number;
  socialEmployer: number;
  taxBase: number;
  incomeTax: number;
  advanceRecovered: number;
  /** Workers' bonuses already handed over by payment voucher, taken back off net pay (owner, 2026-11-08). */
  paidByVoucher?: number;
  otherDeductions: number;
  net: number;
  paidAmount: number;
  elements?: PayrollElement[] | null;
  /** Codes only; the screen says them in words through `hr.warning.{code}`. */
  warnings?: string[] | null;
  wagePaymentId?: string | null;
}

/** A one-off figure for one person in one run — an input re-read on every compute. */
export interface PayrollAdjustment {
  id: string;
  runId: string;
  employeeId: string;
  employeeName: string;
  key: string;
  amount: number;
  reason: string;
  inSocialBase: boolean;
  inTaxBase: boolean;
  createdBy?: string | null;
  createdAtUtc?: string | null;
}

/**
 * One month's wages for one branch. `lines` and `warnings` arrive null from every route this
 * file calls — the lines are a paged route of their own and the warnings come as counts.
 */
export interface PayrollRun {
  id: string;
  number: string;
  branchId: string;
  branchName: string;
  periodYear: number;
  periodMonth: number;
  status: PayrollRunStatus;
  headCount: number;
  currencyDecimals: number;
  /** Computed while the statutory figures were still unconfirmed. Frozen; it never clears itself. */
  isProvisional: boolean;
  gross: number;
  socialBase: number;
  socialEmployee: number;
  socialEmployer: number;
  taxBase: number;
  incomeTax: number;
  employerLevies: number;
  advancesRecovered: number;
  otherDeductions: number;
  net: number;
  /** Only on the detail read; a list sends zero. */
  paidTotal: number;
  computedAtUtc?: string | null;
  lockedBy?: string | null;
  approvedAtUtc?: string | null;
  approvedBy?: string | null;
  paidAtUtc?: string | null;
  reversedAtUtc?: string | null;
  reversedBy?: string | null;
  reversalReason?: string | null;
  ledgerEntryId?: string | null;
  reversalEntryId?: string | null;
  createdBy?: string | null;
  createdAtUtc?: string | null;
  lines?: PayrollRunLine[] | null;
  adjustments?: PayrollAdjustment[] | null;
  warnings?: string[] | null;
}

/** How many payslips raised one warning. Counts, so a header never carries nine hundred lines. */
export interface PayrollWarningCount {
  code: string;
  lines: number;
}

/** One side of the entry approving will write, or wrote. */
export interface PayrollPostingLine {
  accountCode: string;
  debit: number;
  credit: number;
}

/** Why a compute would be refused, what is missing, and who may set it. */
export interface PayrollRefusal {
  code: string;
  missing: string[];
  fixableBy: string;
}

export interface PayrollRunDetail {
  run: PayrollRun;
  warnings: PayrollWarningCount[];
  posting: PayrollPostingLine[];
  refusal: PayrollRefusal | null;
  /**
   * True when an adjustment has been typed since the figures were computed. Approve is disabled
   * on it, because approving posts around the adjustment and then strands it: the run leaves
   * Locked, the adjustment can no longer be edited, and the screen goes on listing a bonus the
   * payslip denies.
   */
  recomputeNeeded?: boolean;
  /**
   * The settings this run was computed against, frozen with it — the words its payslips are read
   * by. Null on a run that never computed. Never the settings as they stand today: an allowance
   * renamed in June must not relabel March's paper, and one deleted since would print as its key.
   */
  settingsSnapshot?: PayrollSettings | null;
}

/** `total` is the whole run, not the page. */
export interface PayrollRunLinesPage {
  total: number;
  skip: number;
  take: number;
  lines: PayrollRunLine[];
}

export interface PayrollSummaryRow {
  branchId: string;
  branchName: string;
  runId?: string | null;
  runNumber?: string | null;
  status: SummaryStatus;
  isProvisional: boolean;
  headCount: number;
  gross: number;
  socialEmployee: number;
  socialEmployer: number;
  incomeTax: number;
  employerLevies: number;
  advancesRecovered: number;
  net: number;
}

/** A warning rides beside the document, never instead of it. */
export interface PayrollRunEnvelope {
  run: PayrollRun;
  warning: string | null;
}

export interface PayrollAdjustmentEnvelope {
  /** Null when the amount was zero: that deletes the adjustment. */
  adjustment: PayrollAdjustment | null;
  warning: string | null;
}

export interface UpsertAdjustment {
  employeeId: string;
  key: string;
  amount: number;
  reason: string;
  inSocialBase: boolean;
  inTaxBase: boolean;
}

// ---------------------------------------------------------------------------------------------
// the client
// ---------------------------------------------------------------------------------------------

/** The server clamps a page of lines at 1000; a payslip batch is a hundred (docs/07 §5.12). */
export const LINES_TAKE = 100;
/** How many runs a list asks for. The server clamps at 1000 and defaults to 200. */
export const RUNS_TAKE = 200;

/**
 * `/api/hr/payroll`. Behind `payroll.manage` as a whole: a foreman who may type an attendance
 * sheet has no business reading what anybody is paid.
 */
@Injectable({ providedIn: 'root' })
export class PayrollRunsApi {
  private readonly api = inject(ApiService);

  runs(
    filter: { branchId?: string | null; year?: number | null; status?: PayrollRunStatus | null; take?: number } = {},
  ): Promise<PayrollRun[]> {
    const params = new URLSearchParams();
    if (filter.branchId) params.set('branchId', filter.branchId);
    if (filter.year) params.set('year', String(filter.year));
    if (filter.status) params.set('status', filter.status);
    params.set('take', String(filter.take ?? RUNS_TAKE));
    return this.api.getJson<PayrollRun[]>(`/api/hr/payroll/runs?${params.toString()}`);
  }

  create(branchId: string, periodYear: number, periodMonth: number): Promise<PayrollRun> {
    return this.api.postJson<PayrollRun>('/api/hr/payroll/runs', { branchId, periodYear, periodMonth });
  }

  read(id: string): Promise<PayrollRunDetail> {
    return this.api.getJson<PayrollRunDetail>(`/api/hr/payroll/runs/${id}`);
  }

  lines(id: string, skip = 0, take = LINES_TAKE, q = ''): Promise<PayrollRunLinesPage> {
    const params = new URLSearchParams({ skip: String(skip), take: String(take) });
    if (q.trim()) params.set('q', q.trim());
    return this.api.getJson<PayrollRunLinesPage>(`/api/hr/payroll/runs/${id}/lines?${params.toString()}`);
  }

  compute(id: string): Promise<PayrollRunEnvelope> {
    return this.api.postJson<PayrollRunEnvelope>(`/api/hr/payroll/runs/${id}/compute`, {});
  }

  unlock(id: string): Promise<PayrollRunEnvelope> {
    return this.api.postJson<PayrollRunEnvelope>(`/api/hr/payroll/runs/${id}/unlock`, {});
  }

  approve(id: string): Promise<PayrollRunEnvelope> {
    return this.api.postJson<PayrollRunEnvelope>(`/api/hr/payroll/runs/${id}/approve`, {});
  }

  reverse(id: string, reason: string): Promise<PayrollRunEnvelope> {
    return this.api.postJson<PayrollRunEnvelope>(`/api/hr/payroll/runs/${id}/reverse`, { reason });
  }

  upsertAdjustment(id: string, employeeId: string, body: UpsertAdjustment): Promise<PayrollAdjustmentEnvelope> {
    return this.api.putJson<PayrollAdjustmentEnvelope>(`/api/hr/payroll/runs/${id}/adjustments/${employeeId}`, body);
  }

  summary(year: number, month: number): Promise<PayrollSummaryRow[]> {
    return this.api.getJson<PayrollSummaryRow[]>(`/api/hr/payroll/summary?year=${year}&month=${month}`);
  }

  /** Every line of a run, a hundred at a time, so a branch prints its payslips in one go. */
  async allLines(id: string, take = LINES_TAKE): Promise<PayrollRunLine[]> {
    const all: PayrollRunLine[] = [];
    let skip = 0;
    for (;;) {
      const page = await this.lines(id, skip, take);
      all.push(...page.lines);
      skip += page.lines.length;
      // `lines.length` rather than `take`: a page that came back short has nothing left, and a
      // page that came back empty would otherwise loop for ever.
      if (page.lines.length === 0 || all.length >= page.total) return all;
    }
  }

  /**
   * What the print tab draws: every line of the run, or the one line asked for.
   *
   * <p>A single payslip is still looked up by paging, because the server pages by employee
   * number and has no route for one line — but it stops at the page that holds it rather than
   * reading the whole branch to print one sheet.</p>
   */
  async payslipLines(id: string, lineId: string | null, take = LINES_TAKE): Promise<PayrollRunLine[]> {
    if (!lineId) return this.allLines(id, take);
    let skip = 0;
    for (;;) {
      const page = await this.lines(id, skip, take);
      const found = page.lines.find((l) => l.id === lineId);
      if (found) return [found];
      skip += page.lines.length;
      if (page.lines.length === 0 || skip >= page.total) return [];
    }
  }
}

// ---------------------------------------------------------------------------------------------
// state words
// ---------------------------------------------------------------------------------------------

/** Tone beside the word, never instead of it (CLAUDE.md: colour never carries a state alone). */
export const RUN_TONE: Record<SummaryStatus, string> = {
  None: '',
  Draft: '',
  Locked: 'bo-state--warn',
  Approved: 'bo-state--lead',
  Paid: 'bo-state--ok',
  Reversed: 'bo-state--danger',
};

/** The key for a server enum value, spelled as the server spells it. */
export function stateKey(prefix: string, value: string): string {
  return `${prefix}.${value}`;
}

/** What may be done to a run in this state, in the order the action bar shows it. */
export type RunAction = 'compute' | 'unlock' | 'approve' | 'reverse';

/**
 * A Draft is computed; a Locked run may be recomputed, unlocked or approved; an Approved run may
 * be reversed. **A Paid run is final** and is corrected by an adjustment in the next run — the
 * nothing-is-edited rule the ledger and the invoices already follow. A Reversed run is history:
 * its month is open again, and what reopens it is a new run, not a button on this one.
 */
export function availableActions(status: PayrollRunStatus): RunAction[] {
  switch (status) {
    case 'Draft':
      return ['compute'];
    case 'Locked':
      return ['compute', 'unlock', 'approve'];
    case 'Approved':
      return ['reverse'];
    default:
      return [];
  }
}

/** True while attendance for the period may not be edited — the sheet says so in those words. */
export function locksAttendance(status: PayrollRunStatus): boolean {
  return status !== 'Draft' && status !== 'Reversed';
}

// ---------------------------------------------------------------------------------------------
// periods
// ---------------------------------------------------------------------------------------------

export interface Period {
  year: number;
  month: number;
}

/** `YYYY-MM`, what an `<input type="month">` reads and writes. */
export function periodInput(period: Period): string {
  return `${period.year}-${String(period.month).padStart(2, '0')}`;
}

/** `YYYY-MM` back to a period, or null when the box is empty or malformed. */
export function parsePeriod(value: string): Period | null {
  const match = /^(\d{4})-(\d{2})$/.exec((value ?? '').trim());
  if (!match) return null;
  const year = Number(match[1]);
  const month = Number(match[2]);
  if (month < 1 || month > 12 || year < 2000 || year > 2999) return null;
  return { year, month };
}

/** The month a date falls in; what a new run offers before anybody chooses. */
export function periodOf(date: Date): Period {
  return { year: date.getFullYear(), month: date.getMonth() + 1 };
}

/**
 * The period as a reader sees it — "mars 2026", "مارس 2026". Latin digits, like every other
 * figure this system prints, and no month names in the dictionaries to keep parity over.
 */
export function periodLabel(period: Period, lang: string): string {
  const date = new Date(Date.UTC(period.year, Math.max(0, Math.min(11, period.month - 1)), 1));
  const shape: Intl.DateTimeFormatOptions = {
    year: 'numeric',
    month: 'long',
    timeZone: 'UTC',
    numberingSystem: 'latn',
  };
  try {
    return new Intl.DateTimeFormat(lang, shape).format(date);
  } catch {
    try {
      // An uploaded language whose code this browser will not accept as a locale.
      return new Intl.DateTimeFormat('fr', shape).format(date);
    } catch {
      return periodInput(period);
    }
  }
}

// ---------------------------------------------------------------------------------------------
// labels — the run's own, not today's
// ---------------------------------------------------------------------------------------------

/**
 * Every label a payslip needs, keyed by the element key the engine emitted.
 *
 * <p>Built from a settings object. That object should be the run's own snapshot; the detail
 * route does not carry it yet, so the current settings stand in and an allowance renamed after
 * the run shows its new name on the reprint. The fallback chain is the same either way.</p>
 */
export interface LabelBook {
  elements: Map<string, TextByLang>;
  /** The contribution's own name, as this company's accountant writes it. */
  social: TextByLang;
  /** The income tax's own name, likewise. */
  incomeTax: TextByLang;
}

export function emptyLabelBook(): LabelBook {
  return { elements: new Map(), social: {}, incomeTax: {} };
}

/** Anything in the settings carrying a key and a set of labels: an allowance, a band, a deduction. */
interface KeyedLabels {
  key?: unknown;
  labels?: unknown;
}

export function labelBook(settings: PayrollSettings | null | undefined): LabelBook {
  const book = emptyLabelBook();
  if (!settings) return book;
  // Deductions are read through the index signature: the settings type names the two lists the
  // settings screen edits by hand, and everything else the server adds rides along untyped.
  const groups: unknown[] = [settings.allowances, settings.overtimeRules, settings['deductions']];
  for (const group of groups) {
    if (!Array.isArray(group)) continue;
    for (const item of group as KeyedLabels[]) {
      const key = typeof item?.key === 'string' ? item.key : '';
      if (!key) continue;
      book.elements.set(key, asLabels(item.labels));
    }
  }
  book.social = asLabels(settings.socialNames);
  book.incomeTax = asLabels(settings.incomeTaxNames);
  return book;
}

function asLabels(value: unknown): TextByLang {
  if (!value || typeof value !== 'object') return {};
  const out: TextByLang = {};
  for (const [lang, text] of Object.entries(value as Record<string, unknown>)) {
    if (typeof text === 'string' && text.trim()) out[lang] = text;
  }
  return out;
}

/**
 * A label in the document's language: the language asked for, then French — the language the
 * presets are written in and the one an Algerian payslip is legible in — then whatever the
 * company did fill in, then ''.
 */
export function labelIn(labels: TextByLang | undefined | null, lang: string): string {
  if (!labels) return '';
  const wanted = labels[lang];
  if (wanted && wanted.trim()) return wanted.trim();
  const french = labels['fr'];
  if (french && french.trim()) return french.trim();
  for (const text of Object.values(labels)) {
    if (text && text.trim()) return text.trim();
  }
  return '';
}

/**
 * What one element is called on the paper. The two the engine writes itself — the base wage and
 * the absence taken off it — are named by the dictionary; everything else carries the company's
 * own wording. A key nobody named prints as the key, which is ugly and true.
 */
export function elementLabel(
  element: Pick<PayrollElement, 'key' | 'kind'>,
  book: LabelBook,
  lang: string,
  t: (key: string) => string,
): string {
  if (element.kind === 'Base') return t('hr.element.base');
  if (element.kind === 'Absence') return t('hr.element.absence');
  // A workers' bonus paid by payment voucher (owner, 2026-11-08): named by the dictionary, not the company's list.
  if (element.key === 'voucherBonus') return t('hr.element.voucherBonus');
  return labelIn(book.elements.get(element.key), lang) || element.key;
}

// ---------------------------------------------------------------------------------------------
// the payslip's shape
// ---------------------------------------------------------------------------------------------

/**
 * The order a payslip reads in: what the time earned, what was taken off it, what the company
 * adds, what the extra hours earned, what is withheld, and last what somebody typed for this
 * month only.
 */
export const PAYSLIP_ORDER: readonly PayrollElementKind[] = [
  'Base',
  'Absence',
  'Allowance',
  'Overtime',
  'Deduction',
  'Adjustment',
];

export interface ElementGroup {
  kind: PayrollElementKind;
  elements: PayrollElement[];
  total: number;
}

/**
 * The elements grouped by kind in the payslip order, each group in the engine's own sort order.
 * An empty group is left out: a payslip with no overtime says nothing about overtime.
 */
export function groupElements(elements: readonly PayrollElement[] | null | undefined): ElementGroup[] {
  const groups: ElementGroup[] = [];
  for (const kind of PAYSLIP_ORDER) {
    const inKind = (elements ?? [])
      .filter((e) => e.kind === kind)
      .slice()
      .sort((a, b) => a.sortOrder - b.sortOrder || a.key.localeCompare(b.key));
    if (inKind.length) groups.push({ kind, elements: inKind, total: sum(inKind.map((e) => e.amount)) });
  }
  return groups;
}

/** Everything taken off the gross on one payslip, as one figure. */
export function totalWithheld(
  line: Pick<PayrollRunLine, 'socialEmployee' | 'incomeTax' | 'advanceRecovered' | 'otherDeductions' | 'paidByVoucher'>,
): number {
  return round(line.socialEmployee + line.incomeTax + line.advanceRecovered + (line.paidByVoucher ?? 0) + line.otherDeductions, 3);
}

/** What is still owed on a payslip after what has already been handed over. */
export function stillOwed(line: Pick<PayrollRunLine, 'net' | 'paidAmount'>): number {
  return round(line.net - line.paidAmount, 3);
}

// ---------------------------------------------------------------------------------------------
// the entry it will post
// ---------------------------------------------------------------------------------------------

export interface PostingPreview {
  debits: PayrollPostingLine[];
  credits: PayrollPostingLine[];
  debitTotal: number;
  creditTotal: number;
  /** One entry per branch per run, and it balances by construction — shown, not assumed. */
  balanced: boolean;
  /** The codes as one string for the action bar's sentence: "631, 635". */
  debitCodes: string;
  creditCodes: string;
}

/**
 * The accrual split into its two sides, so the action bar can say what approving will do to the
 * books before the button is pressed.
 *
 * <p>A line may carry a negative debit — that is the ⚠ placeholder on 641 for a deduction whose
 * account the accountant has not named yet (docs/07 §5.9), and it is meant to be visible. It
 * stays on the side the server put it on rather than being flipped into a tidy credit.</p>
 */
export function postingPreview(lines: readonly PayrollPostingLine[] | null | undefined): PostingPreview {
  const all = lines ?? [];
  const debits = all.filter((l) => l.debit !== 0);
  const credits = all.filter((l) => l.credit !== 0);
  const debitTotal = round(sum(debits.map((l) => l.debit)), 3);
  const creditTotal = round(sum(credits.map((l) => l.credit)), 3);
  return {
    debits,
    credits,
    debitTotal,
    creditTotal,
    balanced: all.length > 0 && Math.abs(debitTotal - creditTotal) < 0.0005,
    debitCodes: debits.map((l) => l.accountCode).join(', '),
    creditCodes: credits.map((l) => l.accountCode).join(', '),
  };
}

/** True when the entry carries the unconfirmed-account placeholder: a negative debit on 641. */
export function hasPostingPlaceholder(lines: readonly PayrollPostingLine[] | null | undefined): boolean {
  return (lines ?? []).some((l) => l.accountCode === '641' && l.debit < 0);
}

// ---------------------------------------------------------------------------------------------
// refusals
// ---------------------------------------------------------------------------------------------

/**
 * The compute refusal out of a 409 body. `ApiService` throws `ApiError` with the raw body as its
 * message, so the JSON is parsed here rather than in three screens.
 */
export function parseRefusal(error: unknown): PayrollRefusal | null {
  const body = error instanceof ApiError ? error.message : typeof error === 'string' ? error : '';
  if (!body.trim().startsWith('{')) return null;
  try {
    const parsed = JSON.parse(body) as Partial<PayrollRefusal>;
    if (typeof parsed?.code !== 'string' || !parsed.code) return null;
    return {
      code: parsed.code,
      missing: Array.isArray(parsed.missing) ? parsed.missing.filter((m): m is string => typeof m === 'string') : [],
      fixableBy: typeof parsed.fixableBy === 'string' ? parsed.fixableBy : '',
    };
  } catch {
    return null;
  }
}

// ---------------------------------------------------------------------------------------------
// arithmetic
// ---------------------------------------------------------------------------------------------

function sum(values: readonly number[]): number {
  return values.reduce((a, b) => a + b, 0);
}

/** Money read back from two providers adds up with a tail; a screen shows a figure, not a tail. */
export function round(value: number, decimals: number): number {
  const factor = 10 ** decimals;
  return Math.round((value + Number.EPSILON) * factor) / factor;
}
