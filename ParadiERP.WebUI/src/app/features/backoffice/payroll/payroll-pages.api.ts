import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { PageResult, pageQueryString } from '../../../core/paging';
import { AttendanceReviewDay, AttendanceReviewPerson, ClockEvent, OvertimeEntry, OvertimeKind } from './hr-api.service';
import { PayrollRun } from './payroll-run-view';
import { AdvanceStatus, PayoutMethod, StatutoryPayment, WageAdvance, WagePaymentRow } from './wage-money-api.service';

/**
 * The payroll lists a page at a time (owner, 2026-11-17: "I need to paginate in backend, not frontend"). Each call is
 * `<the list's own route>/page` (ParadiERP.API/Endpoints/Paging/PeoplePageRoutes.cs): the filters run on the server over
 * every row, only the page comes back, and a list's figures come in `summary`. A filter left at "all", empty or null is
 * not sent. A day is an ISO day; the server reads it on the company's clock.
 */

// ---------- advances ----------

export interface AdvancesPageQuery {
  skip: number;
  take: number;
  branchId?: string | null;
  status?: AdvanceStatus | '' | null;
  method?: 'all' | PayoutMethod;
  interBranch?: 'all' | 'yes' | 'no';
  from?: string;
  to?: string;
}

/** One employee's open advances, as "who owes" shows them. */
export interface OwingRow {
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  branchName: string;
  count: number;
  outstanding: number;
}

/** The two tiles over every open advance the branch and state keep. */
export interface OwingSummary {
  outstandingTotal: number;
  peopleOwing: number;
}

export interface OwingPageQuery {
  skip: number;
  take: number;
  branchId?: string | null;
  status?: AdvanceStatus | '' | null;
}

// ---------- runs and wage payments ----------

export interface RunsPageQuery {
  skip: number;
  take: number;
  branchId?: string | null;
  year?: number | null;
  status?: string | null;
}

/** The runs list's totals row, over every run the filters keep. */
export interface RunsSummary {
  headCount: number;
  gross: number;
  net: number;
}

export interface WagePaymentsPageQuery {
  skip: number;
  take: number;
  method?: 'all' | PayoutMethod;
  state?: 'all' | 'standing' | 'reversed';
}

export interface StatutoryPageQuery {
  skip: number;
  take: number;
  branchId?: string | null;
  accountCode?: string | null;
  year?: number | null;
  method?: 'all' | PayoutMethod;
  from?: string;
  to?: string;
}

// ---------- attendance ----------

export interface OvertimePageQuery {
  skip: number;
  take: number;
  branchId: string;
  year: number;
  month: number;
  employeeId?: string | null;
}

/** The month's head, as the overtime list sends it beside its entries, and the hours per kind of work. */
export interface OvertimeSummary {
  branchId: string;
  year: number;
  month: number;
  locked: boolean;
  runNumber: string | null;
  runStatus: string | null;
  kinds: OvertimeKind[];
  totals: { key: string; hours: number }[];
}

export interface ClockPageQuery {
  skip: number;
  take: number;
  branchId: string;
  year: number;
  month: number;
  kind?: string;
  pair?: 'all' | 'paired' | 'unpaired';
  from?: string;
  to?: string;
}

/** A punch, its person, and whether it found a partner. */
export interface ClockRow extends ClockEvent {
  employeeNumber: string;
  employeeName: string;
  unpaired: boolean;
}

/** The month's punches before the filters, and how many never found a partner. */
export interface ClockSummary {
  events: number;
  unpaired: number;
}

export interface ReviewPageQuery {
  skip: number;
  take: number;
  branchId: string;
  year: number;
  month: number;
  flagged: boolean;
  person?: string;
  flag?: string;
}

/** The month read, its days before the filters, and the people it names. */
export interface ReviewSummary {
  year: number;
  month: number;
  days: number;
  people: AttendanceReviewPerson[];
}

@Injectable({ providedIn: 'root' })
export class PayrollPagesApi {
  private readonly api = inject(ApiService);

  advancesPage(query: AdvancesPageQuery): Promise<PageResult<WageAdvance>> {
    return this.api.getJson<PageResult<WageAdvance>>(`/api/hr/advances/page${pageQueryString({ ...query })}`);
  }

  owingPage(query: OwingPageQuery): Promise<PageResult<OwingRow, OwingSummary>> {
    return this.api.getJson<PageResult<OwingRow, OwingSummary>>(`/api/hr/advances/owing/page${pageQueryString({ ...query })}`);
  }

  runsPage(query: RunsPageQuery): Promise<PageResult<PayrollRun, RunsSummary>> {
    return this.api.getJson<PageResult<PayrollRun, RunsSummary>>(`/api/hr/payroll/runs/page${pageQueryString({ ...query })}`);
  }

  wagePaymentsPage(runId: string, query: WagePaymentsPageQuery): Promise<PageResult<WagePaymentRow>> {
    return this.api.getJson<PageResult<WagePaymentRow>>(
      `/api/hr/payroll/runs/${encodeURIComponent(runId)}/payments/page${pageQueryString({ ...query })}`,
    );
  }

  statutoryPage(query: StatutoryPageQuery): Promise<PageResult<StatutoryPayment>> {
    return this.api.getJson<PageResult<StatutoryPayment>>(
      `/api/hr/payroll/statutory-payments/page${pageQueryString({ ...query })}`,
    );
  }

  overtimePage(query: OvertimePageQuery): Promise<PageResult<OvertimeEntry, OvertimeSummary>> {
    return this.api.getJson<PageResult<OvertimeEntry, OvertimeSummary>>(
      `/api/hr/attendance/overtime/page${pageQueryString({ ...query })}`,
    );
  }

  clockPage(query: ClockPageQuery): Promise<PageResult<ClockRow, ClockSummary>> {
    return this.api.getJson<PageResult<ClockRow, ClockSummary>>(`/api/hr/attendance/clock/page${pageQueryString({ ...query })}`);
  }

  reviewPage(query: ReviewPageQuery): Promise<PageResult<AttendanceReviewDay, ReviewSummary>> {
    return this.api.getJson<PageResult<AttendanceReviewDay, ReviewSummary>>(
      `/api/hr/attendance/review/page${pageQueryString({ ...query })}`,
    );
  }
}
