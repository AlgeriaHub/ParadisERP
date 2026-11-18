import { Injectable, inject } from '@angular/core';
import { ApiError, ApiService } from '../../../core/api.service';

/**
 * The money half of the personnel module: advances, wage payments and the transfer that clears
 * what payroll withheld.
 *
 * <p>It sits beside `payroll-api.service.ts` rather than inside it because the two are read by
 * different screens and neither needs the other's types; both stay in the deferred back-office
 * chunk, so a cashier's terminal downloads neither.</p>
 *
 * <p>Every route here is written against `ParadisERP.API/Endpoints/HrMoneyEndpoints.cs`. Two
 * shapes of that file matter to every caller:</p>
 * <ul>
 *   <li><b>A warning never replaces the document.</b> Granting, paying and clearing all answer
 *   with `{ thing, warning }` whether the warning is there or not — so no screen has to notice
 *   that the response changed shape on the day a box ran short.</li>
 *   <li><b>A refusal is a sentence, not a code.</b> These routes answer 400/404/409 with a
 *   plain message; only the attendance group speaks in `{ code }`. `moneyErrorMessage` handles
 *   both so a screen that later reads an attendance route needs no second helper.</li>
 * </ul>
 */

export type PayoutMethod = 'Cash' | 'Bank';
export type AdvanceStatus = 'Open' | 'Recovered' | 'WrittenOff';
/** The three accounts payroll withholds into. Nothing else may be cleared from these screens. */
export type LiabilityAccountCode = '431' | '442' | '447';

/** Whatever a write answers with, beside the document it wrote. */
export interface WithWarning {
  warning: string | null;
}

// ---------- advances ----------

export interface WageAdvance {
  id: string;
  number: string;
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  /** The employee's branch, frozen at the moment the money was handed over. */
  branchId: string;
  branchName: string;
  amount: number;
  recovered: number;
  /** `amount − recovered`, sent so no row has to do the subtraction. */
  outstanding: number;
  grantedAtUtc: string;
  payoutMethod: PayoutMethod;
  status: AdvanceStatus;
  cashBoxId?: string | null;
  recoveredInRunId?: string | null;
  recoveredInRunNumber?: string | null;
  reference?: string | null;
  note?: string | null;
  createdBy?: string | null;
  /** True when the paying box belonged to a branch other than the employee's. */
  isInterBranch: boolean;
}

export interface GrantAdvanceRequest {
  employeeId: string;
  amount: number;
  payoutMethod: PayoutMethod;
  cashBoxId?: string | null;
  grantedAtUtc?: string | null;
  reference?: string | null;
  note?: string | null;
}

// ---------- wage payments ----------

export interface WagePaymentLine {
  runLineId: string;
  employeeId: string;
  employeeName: string;
  amount: number;
}

export interface WagePayment {
  id: string;
  number: string;
  runId: string;
  runNumber: string;
  branchId: string;
  branchName: string;
  paidAtUtc: string;
  payoutMethod: PayoutMethod;
  amount: number;
  cashBoxId?: string | null;
  reference?: string | null;
  note?: string | null;
  createdBy?: string | null;
  ledgerEntryId?: string | null;
  lines?: WagePaymentLine[] | null;
}

/**
 * A payment plus the one fact it cannot hold itself. There is no reversed column on the server —
 * the reversing ledger entry is the record — so this is read from the ledger and said beside the
 * payment rather than in place of it.
 */
export interface WagePaymentRow {
  payment: WagePayment;
  isReversed: boolean;
  reversedAtUtc?: string | null;
  reversalReason?: string | null;
}

export interface PayWagesLine {
  runLineId: string;
  amount: number;
}

export interface PayWagesRequest {
  runId: string;
  payoutMethod: PayoutMethod;
  cashBoxId?: string | null;
  paidAtUtc?: string | null;
  reference?: string | null;
  note?: string | null;
  /** Omitted means every line still owed whose employee's own method matches this payment. */
  lines?: PayWagesLine[] | null;
}

// ---------- statutory payments and what they clear ----------

export interface StatutoryPayment {
  id: string;
  branchId: string;
  branchName: string;
  accountCode: string;
  /** The chart's French name. The screen prints the code beside it, which is language-free. */
  accountName: string;
  amount: number;
  paidAtUtc: string;
  payoutMethod: PayoutMethod;
  cashBoxId?: string | null;
  periodYear?: number | null;
  periodMonth?: number | null;
  reference?: string | null;
  note?: string | null;
  createdBy?: string | null;
  ledgerEntryId?: string | null;
}

export interface RecordStatutoryPaymentRequest {
  branchId: string;
  accountCode: string;
  amount: number;
  payoutMethod: PayoutMethod;
  paidAtUtc?: string | null;
  cashBoxId?: string | null;
  periodYear?: number | null;
  periodMonth?: number | null;
  reference?: string | null;
  note?: string | null;
}

/** Read out of the ledger lines, never from a column that could drift from them. */
export interface PayrollLiability {
  accountCode: string;
  accountName: string;
  branchId?: string | null;
  accrued: number;
  paid: number;
  outstanding: number;
}

// ---------- the neighbours these screens read but never write ----------

/** Only the fields the pickers on these screens show. The employees screen owns the rest. */
export interface EmployeePick {
  id: string;
  number: string;
  fullName: string;
  branchId: string;
  branchName: string;
  payBasis: 'Daily' | 'Monthly';
  baseRate: number;
  payoutMethod: PayoutMethod;
  isActive: boolean;
  jobTitle?: string | null;
  openAdvances: number;
}

export interface EmployeePage {
  total: number;
  skip: number;
  take: number;
  rows: EmployeePick[];
}

export type PayrollRunStatus = 'Draft' | 'Locked' | 'Approved' | 'Paid' | 'Reversed';

/** The run fields paying needs. The run screen owns the other thirty. */
export interface PayrollRunHead {
  id: string;
  number: string;
  branchId: string;
  branchName: string;
  periodYear: number;
  periodMonth: number;
  status: PayrollRunStatus;
  headCount: number;
  isProvisional: boolean;
  net: number;
  /** Sent on the detail only; a list row leaves it at zero. */
  paidTotal: number;
}

export interface PayrollRunDetail {
  run: PayrollRunHead;
}

export interface PayrollRunLine {
  id: string;
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  branchId: string;
  net: number;
  paidAmount: number;
  wagePaymentId?: string | null;
}

export interface PayrollRunLinesPage {
  total: number;
  skip: number;
  take: number;
  lines: PayrollRunLine[];
}

/**
 * What the server said, in the reader's language where it named a code and in its own words
 * where it wrote a sentence.
 *
 * <p>The money routes answer with a sentence; the attendance group answers with
 * `{ code, message }` so the screen can translate it. A screen that shows the raw body in the
 * second case shows English to an Arabic manager, and one that only ever looks for a code shows
 * nothing at all in the first. Both are handled here, once.</p>
 */
export function moneyErrorMessage(
  error: unknown,
  translate: (key: string, params?: Record<string, string | number>) => string,
): string {
  if (!(error instanceof ApiError)) return (error as Error)?.message ?? '';
  if (error.status === 401) return translate('hr.money.error.session');
  if (error.status === 0) return translate('hr.money.error.unreachable');
  if (error.status === 403) return translate('hr.money.error.forbidden');

  const body = (error.message ?? '').trim();
  if (!body) return translate('hr.money.error.unknown');

  // `Results.BadRequest("…")` serialises the sentence with its quotation marks; `Results.Conflict`
  // of an object serialises a `{ code | message }`. Neither is something to show as it arrived.
  try {
    const parsed: unknown = JSON.parse(body);
    if (typeof parsed === 'string') return parsed;
    if (parsed && typeof parsed === 'object') {
      const shaped = parsed as { code?: string; message?: string; args?: Record<string, string> };
      if (shaped.code) {
        const key = `hr.problem.${shaped.code}`;
        // The figures travel as parameters, so the sentence is rebuilt in the reader's language
        // rather than translated around an English one with a run number inside it.
        const said = translate(key, shaped.args ?? {});
        if (said !== key) return said;
      }
      if (shaped.message) return shaped.message;
    }
  } catch {
    /* not JSON — the body is already a sentence */
  }
  return body;
}

@Injectable({ providedIn: 'root' })
export class WageMoneyApi {
  private readonly api = inject(ApiService);

  // ---------- advances ----------

  listAdvances(filter: {
    employeeId?: string | null;
    branchId?: string | null;
    status?: AdvanceStatus | null;
    take?: number;
  }): Promise<WageAdvance[]> {
    return this.api.getJson<WageAdvance[]>(`/api/hr/advances${query(filter)}`);
  }

  grantAdvance(request: GrantAdvanceRequest): Promise<{ advance: WageAdvance } & WithWarning> {
    return this.api.postJson<{ advance: WageAdvance } & WithWarning>('/api/hr/advances', request);
  }

  // ---------- wage payments ----------

  listRuns(filter: {
    branchId?: string | null;
    year?: number | null;
    status?: PayrollRunStatus | null;
    take?: number;
  }): Promise<PayrollRunHead[]> {
    return this.api.getJson<PayrollRunHead[]>(`/api/hr/payroll/runs${query(filter)}`);
  }

  /** The detail is the only route that carries `paidTotal`; a list row cannot say what is owed. */
  readRun(runId: string): Promise<PayrollRunDetail> {
    return this.api.getJson<PayrollRunDetail>(`/api/hr/payroll/runs/${runId}`);
  }

  runLines(runId: string, skip: number, take: number, q?: string | null): Promise<PayrollRunLinesPage> {
    return this.api.getJson<PayrollRunLinesPage>(`/api/hr/payroll/runs/${runId}/lines${query({ skip, take, q })}`);
  }

  listPayments(runId: string, take = 200): Promise<WagePaymentRow[]> {
    return this.api.getJson<WagePaymentRow[]>(`/api/hr/payroll/runs/${runId}/payments${query({ take })}`);
  }

  pay(runId: string, request: PayWagesRequest): Promise<{ payment: WagePaymentRow } & WithWarning> {
    return this.api.postJson<{ payment: WagePaymentRow } & WithWarning>(
      `/api/hr/payroll/runs/${runId}/payments`,
      request,
    );
  }

  reversePayment(paymentId: string, reason: string): Promise<{ payment: WagePaymentRow } & WithWarning> {
    return this.api.postJson<{ payment: WagePaymentRow } & WithWarning>(
      `/api/hr/payroll/payments/${paymentId}/reverse`,
      { reason },
    );
  }

  // ---------- statutory payments ----------

  listStatutoryPayments(filter: {
    branchId?: string | null;
    accountCode?: string | null;
    year?: number | null;
    take?: number;
  }): Promise<StatutoryPayment[]> {
    return this.api.getJson<StatutoryPayment[]>(`/api/hr/payroll/statutory-payments${query(filter)}`);
  }

  recordStatutoryPayment(
    request: RecordStatutoryPaymentRequest,
  ): Promise<{ payment: StatutoryPayment } & WithWarning> {
    return this.api.postJson<{ payment: StatutoryPayment } & WithWarning>(
      '/api/hr/payroll/statutory-payments',
      request,
    );
  }

  liabilities(branchId?: string | null): Promise<PayrollLiability[]> {
    return this.api.getJson<PayrollLiability[]>(`/api/hr/payroll/liabilities${query({ branchId })}`);
  }

  // ---------- the employee picker ----------

  employees(filter: { branchId?: string | null; active?: boolean; q?: string | null; take?: number }): Promise<EmployeePage> {
    return this.api.getJson<EmployeePage>(`/api/hr/employees${query(filter)}`);
  }
}

/** Drops empty values rather than sending `branchId=` and having the server parse a blank guid. */
function query(params: Record<string, unknown>): string {
  const parts: string[] = [];
  for (const [name, value] of Object.entries(params)) {
    if (value === null || value === undefined || value === '') continue;
    parts.push(`${encodeURIComponent(name)}=${encodeURIComponent(String(value))}`);
  }
  return parts.length ? `?${parts.join('&')}` : '';
}
