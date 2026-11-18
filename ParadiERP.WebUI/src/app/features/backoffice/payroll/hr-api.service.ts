import { Injectable, inject } from '@angular/core';
import { ApiError, ApiService } from '../../../core/api.service';
import { AttendanceMode, PayBasis, TextByLang } from './payroll-api.service';

/**
 * The people half of the personnel module on the wire: the employee register, the attendance
 * sheet, the day grid and the clock.
 *
 * <p>Written against `ParadiERP.API/Endpoints/HrEmployeeEndpoints.cs` and
 * `HrAttendanceEndpoints.cs`, and the contracts in `ParadiERP.Application/Contracts/HrContracts.cs`
 * — what the server actually answers, not what a plan hoped for. It sits beside the screens
 * rather than in `core/` for the same reason `payroll-api.service.ts` does: nothing under
 * `/api/hr` has a reader on a cashier's device, and the whole module stays in the deferred
 * chunk.</p>
 *
 * <p>Enums cross as their names, calendar dates as `YYYY-MM-DD` and instants as UTC ISO with
 * `Utc` in the name. Nothing here parses a date into a `Date` on the way in: a period is two
 * integers in the company's time zone, and a day is the day somebody worked, neither of which
 * survives a round trip through the device's own clock.</p>
 */

export interface OvertimeKind { key: string; labels: TextByLang; multiplier: number; unit: 'Hours' | 'Days' }
export interface OvertimeEntry {
  id: string; employeeId: string; employeeNumber: string; employeeName: string;
  workDate: string /* yyyy-MM-dd */; ruleKey: string; hours: number; note: string | null;
  createdBy: string | null; createdAtUtc: string; updatedBy: string | null; updatedAtUtc: string;
  canEdit: boolean; lockedByRun: string | null;
}
export interface OvertimeEntryList {
  branchId: string; year: number; month: number; locked: boolean;
  runNumber: string | null; runStatus: string | null;
  kinds: OvertimeKind[]; entries: OvertimeEntry[];
}
export interface CreateOvertimeEntry { employeeId: string; workDate: string; ruleKey: string; hours: number; note?: string | null }
export interface UpdateOvertimeEntry { workDate: string; ruleKey: string; hours: number; note?: string | null }
export interface OvertimeEntryChange {
  id: string; entryId: string; action: 'Created' | 'Edited' | 'Deleted';
  previousWorkDate: string | null; previousRuleKey: string | null; previousHours: number | null;
  workDate: string | null; ruleKey: string | null; hours: number | null;
  changedBy: string | null; changedAtUtc: string;
}

export type PayoutMethod = 'Cash' | 'Bank';
export type AttendanceSource = 'Declared' | 'Recorded' | 'None';
export type PayrollRunStatus = 'Draft' | 'Locked' | 'Approved' | 'Paid' | 'Reversed';
export type ClockKind = 'In' | 'Out';
export type DayStatus = 'Present' | 'Absent' | 'PaidLeave' | 'UnpaidLeave' | 'WeeklyRest' | 'PublicHoliday';

/** The six a day may be in. `null` on a write means "nothing recorded", which deletes the day. */
export const DAY_STATUSES: readonly DayStatus[] = [
  'Present',
  'Absent',
  'PaidLeave',
  'UnpaidLeave',
  'WeeklyRest',
  'PublicHoliday',
];

// ---------- employees ----------

/** An employee's own figure for an allowance the settings define. */
export interface EmployeeAllowance {
  allowanceKey: string;
  value: number;
  isEnabled: boolean;
}

export interface Employee {
  id: string;
  number: string;
  /** The first name then the family name (PinSay #301), as every list, account and payslip reads it. */
  fullName: string;
  /** Null for someone filed before the two parts existed: their whole name is the family name. */
  firstName?: string | null;
  familyName?: string | null;
  branchId: string;
  branchName: string;
  payBasis: PayBasis;
  baseRate: number;
  payoutMethod: PayoutMethod;
  /** `YYYY-MM-DD`. */
  hiredOn: string;
  isActive: boolean;
  leftOn?: string | null;
  nationalId?: string | null;
  socialSecurityNumber?: string | null;
  /** The number they are enrolled under on the fingerprint terminal (FR-3A), in the server's one spelling. */
  fingerprintNumber?: string | null;
  birthDate?: string | null;
  phone?: string | null;
  /** Optional, stored only (owner, 2026-10-05): nothing is mailed to it. The server trims it, a blank is null. */
  email: string | null;
  address?: string | null;
  jobTitle?: string | null;
  bankAccount?: string | null;
  /** What this person was given instead of the company default; null means they follow it. */
  attendanceModeOverride?: AttendanceMode | null;
  /** The effective mode, resolved by the server. Never recomputed here. */
  attendanceMode?: AttendanceMode | null;
  notes?: string | null;
  allowances?: EmployeeAllowance[] | null;
  /** What this person still owes on advances. A figure, not a warning. */
  openAdvances: number;
  /**
   * Their active account, when they have one (2026-10-05): its id and what it signs in with (the
   * Login ID, or an older username). Null for someone with no access to the application — the case
   * the employee screen offers "give access" for.
   */
  accountUserId?: string | null;
  accountSignIn?: string | null;
}

/** `total` counts the rows matching the filter, not the rows in this page. */
export interface EmployeePage {
  total: number;
  skip: number;
  take: number;
  rows: Employee[];
}

/**
 * What the form sends. `number` is absent on purpose — the server mints it — and `allowances`
 * omitted leaves the stored overrides alone, while a list <em>is</em> the set.
 */
export interface UpsertEmployee {
  /** Composed for an older server; a current one composes it itself from the two parts. */
  fullName: string;
  /** Both required, both always sent (PinSay #301): the server composes the full name from them. */
  firstName: string;
  familyName: string;
  branchId: string;
  payBasis: PayBasis;
  baseRate: number;
  payoutMethod: PayoutMethod;
  hiredOn: string;
  isActive?: boolean;
  leftOn?: string | null;
  nationalId?: string | null;
  socialSecurityNumber?: string | null;
  fingerprintNumber?: string | null;
  birthDate?: string | null;
  phone?: string | null;
  /**
   * Always sent, on a create and on an update, as text: '' clears it, while null keeps the stored
   * one (how the server reads an older client that does not know the field). A malformed one is
   * refused (`emailInvalid`).
   */
  email: string;
  address?: string | null;
  jobTitle?: string | null;
  bankAccount?: string | null;
  attendanceModeOverride?: AttendanceMode | null;
  notes?: string | null;
  allowances?: EmployeeAllowance[] | null;
}

export interface EmployeeLeaveRequest {
  leftOn: string;
  note?: string | null;
}

// ---------- attendance ----------

/** One overtime column, from the settings. Which bands exist is configuration, never a list of four. */
export interface OvertimeColumn {
  key: string;
  labels: TextByLang;
  multiplier: number;
  unit?: 'Hours' | 'Days';
}

export interface AttendanceOvertime {
  ruleKey: string;
  hours: number;
}

export interface AttendanceSheetRow {
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  payBasis: PayBasis;
  mode: AttendanceMode;
  /** True for a Recorded row: those figures were added up, and typing over them would fork them. */
  readOnly: boolean;
  daysPresent: number;
  paidLeaveDays: number;
  unpaidAbsenceDays: number;
  overtime: AttendanceOvertime[];
  /** `None` means nothing was captured — not a month of zero days. */
  source: AttendanceSource;
  unpairedClockIns: number;
  updatedBy?: string | null;
  updatedAtUtc?: string | null;
}

export interface AttendanceSheet {
  branchId: string;
  periodYear: number;
  periodMonth: number;
  /** The month's run is Locked, Approved or Paid. The run is named beside it. */
  locked: boolean;
  runId?: string | null;
  runNumber?: string | null;
  runStatus?: PayrollRunStatus | null;
  companyDefaultMode: AttendanceMode;
  overtimeRules: OvertimeColumn[];
  total: number;
  skip: number;
  take: number;
  rows: AttendanceSheetRow[];
}

export interface UpsertSheetRow {
  employeeId: string;
  daysPresent: number;
  paidLeaveDays: number;
  unpaidAbsenceDays: number;
  overtime?: AttendanceOvertime[];
}

export interface UpsertSheet {
  branchId: string;
  periodYear: number;
  periodMonth: number;
  rows: UpsertSheetRow[];
}

/**
 * One day. `status: null` on a write deletes it — "nothing recorded" has to stay reachable, or a
 * day ticked by mistake could only ever be corrected to some other claim about that day.
 */
export interface AttendanceDay {
  employeeId: string;
  workDate: string;
  status: DayStatus | null;
  hoursWorked?: number | null;
  overtime?: AttendanceOvertime[] | null;
  updatedBy?: string | null;
  updatedAtUtc?: string | null;
}

export interface AttendanceSummary {
  daysPresent: number;
  paidLeaveDays: number;
  unpaidAbsenceDays: number;
  overtime: AttendanceOvertime[];
  source: AttendanceSource;
}

export interface AttendanceDays {
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  branchId: string;
  periodYear: number;
  periodMonth: number;
  locked: boolean;
  runId?: string | null;
  runNumber?: string | null;
  runStatus?: PayrollRunStatus | null;
  mode: AttendanceMode;
  overtimeRules: OvertimeColumn[];
  /** Only the days somebody recorded; a date that is absent was not recorded. */
  days: AttendanceDay[];
  /** Exactly what the run will read. */
  summary: AttendanceSummary;
  unpairedClockInsUtc: string[];
}

// ---------- the clock ----------

export interface ClockPunch {
  employeeId?: string | null;
  employeeNumber?: string | null;
  kind: ClockKind;
  atUtc?: string | null;
  source?: string | null;
}

export interface ClockEvent {
  id: string;
  employeeId: string;
  atUtc: string;
  kind: ClockKind;
  source?: string | null;
  createdBy?: string | null;
}

export interface ClockPerson {
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
}

/** The punch that was stored, and anything odd about it. The warning never replaces the punch. */
export interface Clocked {
  event: ClockEvent;
  person: ClockPerson;
  warning?: string | null;
}

export interface ClockLog {
  events: ClockEvent[];
  unpaired: ClockEvent[];
  people: ClockPerson[];
}

// ---------- attendance review ----------

export interface AttendanceReviewDay {
  employeeId: string;
  workDate: string;
  isRestDay: boolean;
  periodName?: string | null;
  scheduledStart?: string | null;
  scheduledEnd?: string | null;
  scheduledHours: number;
  firstIn?: string | null;
  lastOut?: string | null;
  workedHours: number;
  lateMinutes: number;
  earlyLeaveMinutes: number;
  missingCheckOut: boolean;
  possibleOvertimeMinutes: number;
  workedOnRestDay: boolean;
}

export interface AttendanceReviewPerson {
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
}

export interface AttendanceReviewResult {
  year: number;
  month: number;
  days: AttendanceReviewDay[];
  people: AttendanceReviewPerson[];
}

export type AttendanceReview = AttendanceReviewResult;

// ---------- refusals ----------

/**
 * Why a write did nothing. `code` is the half that can be translated — the screen resolves
 * `hr.problem.{code}` — and `message` is the English sentence for curl and the log.
 */
export interface HrProblem {
  code: string;
  message: string;
  runNumber?: string | null;
  runStatus?: string | null;
  key?: string | null;
}

/**
 * The refusal inside an `ApiError`, or null when the failure was not one of the personnel
 * routes' own.
 *
 * <p>`ApiService` hands the raw body over as the error's message, so the JSON has to be opened
 * here. A body that is not this shape — a proxy's HTML, a timeout with no body at all — comes
 * back null and the caller falls back to its own sentence rather than showing the reader a
 * fragment of someone else's error page.</p>
 */
export function hrProblem(error: unknown): HrProblem | null {
  if (!(error instanceof ApiError)) return null;
  const body = error.message?.trim();
  if (!body || !body.startsWith('{')) return null;
  try {
    const parsed = JSON.parse(body) as Partial<HrProblem>;
    return typeof parsed?.code === 'string' && parsed.code
      ? { ...parsed, code: parsed.code, message: parsed.message ?? '' }
      : null;
  } catch {
    return null;
  }
}

// ---------- the fingerprint terminal's export, previewed (FR-3B) ----------

/** How a text date is ordered; a date Excel stores as a number needs none. */
export type PunchDateOrder = 'dmy' | 'mdy' | 'ymd';

/** 1-based column numbers (1 = A); null is "not in this file". */
export interface PunchMapping {
  number: number | null;
  name: number | null;
  date: number | null;
  time: number | null;
  dateTime: number | null;
  marker: number | null;
  timeOut: number | null;
}

export interface PunchPreviewQuery {
  branchId: string;
  file: File;
  sheet: string | null;
  /** null: take the server's suggestion; 0: the file has no header row. */
  header: number | null;
  mapping: PunchMapping;
  order: PunchDateOrder;
}

export type PunchRowStatus = 'matched' | 'unknown' | 'otherBranch' | 'invalid';

export interface PunchColumn {
  index: number;
  letter: string;
  header: string | null;
  samples: string[];
}

export interface PunchRow {
  row: number;
  status: PunchRowStatus;
  problem: string | null;
  number: string | null;
  name: string | null;
  /** Wall-clock `YYYY-MM-DDTHH:mm:ss` in the company's zone — never an instant. */
  at: string | null;
  marker: string | null;
  source: string;
  employeeId?: string | null;
  employeeNumber?: string | null;
  employeeName?: string | null;
  /** Read from the check-out column. */
  exit?: boolean;
  flags?: string[] | null;
}

export interface PunchSummary {
  rows: number;
  matched: number;
  unknown: number;
  otherBranch: number;
  invalid: number;
  textDates: number;
  first: string | null;
  last: string | null;
  flagged?: number;
}

export interface PunchPreview {
  fileName: string;
  sheetName: string;
  sheetNames: string[];
  headerRow: number | null;
  suggestedHeaderRow: number | null;
  dateOrder: string;
  columns: PunchColumn[];
  top: { row: number; cells: Record<string, string> }[];
  mappingProblem: string | null;
  summary: PunchSummary | null;
  rows: PunchRow[];
  rowsCut: boolean;
  employees: {
    employeeId: string;
    employeeNumber: string;
    employeeName: string;
    fingerprintNumber: string;
    punches: number;
    dates: number;
    first: string;
    last: string;
  }[];
  unknownNumbers: { number: string; rows: number; names: string[] }[];
  otherBranchNumbers: { number: string; rows: number }[];
  suggestedMapping?: PunchMapping | null;
}

@Injectable({ providedIn: 'root' })
export class HrApi {
  private readonly api = inject(ApiService);

  // ---------- employees ----------

  /** `active: true` is "still with us"; `false` is everyone else — suspended and departed alike. */
  employees(query: {
    branchId?: string | null;
    active?: boolean | null;
    q?: string | null;
    skip?: number;
    take?: number;
  }): Promise<EmployeePage> {
    return this.api.getJson<EmployeePage>(`/api/hr/employees${params(query)}`);
  }

  employee(id: string): Promise<Employee> {
    return this.api.getJson<Employee>(`/api/hr/employees/${id}`);
  }

  createEmployee(body: UpsertEmployee): Promise<Employee> {
    return this.api.postJson<Employee>('/api/hr/employees', body);
  }

  updateEmployee(id: string, body: UpsertEmployee): Promise<Employee> {
    return this.api.putJson<Employee>(`/api/hr/employees/${id}`, body);
  }

  /** There is no delete. A person who has been paid is recorded as having left. */
  markLeft(id: string, body: EmployeeLeaveRequest): Promise<Employee> {
    return this.api.postJson<Employee>(`/api/hr/employees/${id}/leave`, body);
  }

  // ---------- attendance ----------

  sheet(query: {
    branchId: string;
    year: number;
    month: number;
    skip?: number;
    take?: number;
    q?: string | null;
  }): Promise<AttendanceSheet> {
    return this.api.getJson<AttendanceSheet>(`/api/hr/attendance/sheet${params(query)}`);
  }

  /** One PUT for the whole page of edits; the answer carries exactly the rows that were sent. */
  saveSheet(body: UpsertSheet): Promise<AttendanceSheet> {
    return this.api.putJson<AttendanceSheet>('/api/hr/attendance/sheet', body);
  }

  days(query: { employeeId: string; year: number; month: number }): Promise<AttendanceDays> {
    return this.api.getJson<AttendanceDays>(`/api/hr/attendance/days${params(query)}`);
  }

  /** Days not mentioned are untouched; a day sent with `status: null` is removed. */
  saveDays(days: AttendanceDay[]): Promise<AttendanceDays> {
    return this.api.putJson<AttendanceDays>('/api/hr/attendance/days', { days });
  }

  overtimeEntries(query: { branchId: string; year: number; month: number; employeeId?: string }): Promise<OvertimeEntryList> {
    return this.api.getJson<OvertimeEntryList>(`/api/hr/attendance/overtime${params(query)}`);
  }

  createOvertimeEntry(body: CreateOvertimeEntry): Promise<OvertimeEntry> {
    return this.api.postJson<OvertimeEntry>('/api/hr/attendance/overtime', body);
  }

  updateOvertimeEntry(id: string, body: UpdateOvertimeEntry): Promise<OvertimeEntry> {
    return this.api.putJson<OvertimeEntry>(`/api/hr/attendance/overtime/${id}`, body);
  }

  deleteOvertimeEntry(id: string): Promise<void> {
    return this.api.deleteJson<void>(`/api/hr/attendance/overtime/${id}`);
  }

  overtimeHistory(id: string): Promise<OvertimeEntryChange[]> {
    return this.api.getJson<OvertimeEntryChange[]>(`/api/hr/attendance/overtime/${id}/history`);
  }

  /** Who a badge number belongs to, before anything is recorded (the clock's confirmation). */
  /** Who a badge belongs to; `atUtc` checks their employment on a set time rather than now. */
  clockPerson(employeeNumber: string, atUtc?: string | null): Promise<ClockPerson> {
    return this.api.getJson<ClockPerson>(`/api/hr/attendance/clock/person${params({ number: employeeNumber, at: atUtc })}`);
  }

  clock(punch: ClockPunch): Promise<Clocked> {
    return this.api.postJson<Clocked>('/api/hr/attendance/clock', punch);
  }

  clockLog(query: {
    employeeId?: string | null;
    branchId?: string | null;
    year: number;
    month: number;
    take?: number;
  }): Promise<ClockLog> {
    return this.api.getJson<ClockLog>(`/api/hr/attendance/clock${params(query)}`);
  }

  /**
   * FR-3B: the terminal's export read as it would be — nothing is written. The file goes again
   * with every change of sheet, header row or column; the server keeps no copy.
   */
  previewPunches(q: PunchPreviewQuery): Promise<PunchPreview> {
    const form = new FormData();
    form.append('file', q.file, q.file.name);
    if (q.sheet) form.append('sheet', q.sheet);
    if (q.header !== null) form.append('header', String(q.header));
    for (const [key, column] of Object.entries(q.mapping)) {
      if (column) form.append(key, String(column));
    }
    form.append('order', q.order);
    return this.api.postForm<PunchPreview>(`/api/hr/attendance/import/preview${params({ branchId: q.branchId })}`, form);
  }

  attendanceReview(query: {
    branchId?: string | null;
    employeeId?: string | null;
    year: number;
    month: number;
    flagged?: boolean;
  }): Promise<AttendanceReviewResult> {
    return this.api.getJson<AttendanceReviewResult>(`/api/hr/attendance/review${params(query)}`);
  }
}

/** A query string with the empty values left out, so `?q=` never reaches a route that filters on it. */
function params(query: Record<string, unknown>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === null || value === undefined || value === '') continue;
    search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}
