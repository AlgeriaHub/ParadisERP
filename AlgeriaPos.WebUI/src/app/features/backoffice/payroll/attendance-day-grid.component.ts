import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { I18nService } from '../../../core/i18n.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { discardRequest, saveRequest } from '../../../ui/confirm-requests';
import { RegionalService } from '../../../core/regional.service';
import {
  AttendanceDay,
  AttendanceDays,
  AttendanceOvertime,
  DAY_STATUSES,
  DayStatus,
  HrApi,
  OvertimeColumn,
  hrProblem,
} from './hr-api.service';
import { moneyErrorMessage } from './wage-money-api.service';
import { TextByLang } from './payroll-api.service';
import { monthDays, parseFigure } from './sheet-keys';

/** One day as it is being edited. An empty status is "nothing recorded", which is not an absence. */
interface DayEdit {
  status: DayStatus | '';
  hours: number | null;
  overtime: Record<string, number | null>;
}

/**
 * One person's month, day by day — the Recorded side of attendance.
 *
 * <p>The sheet holds a month as four figures; this holds the days those figures were added up
 * from. It is where a foreman answers the questions the totals cannot: which day was the public
 * holiday, which was paid leave, and which clock-in never found its clock-out.</p>
 *
 * <p>A date that was never recorded shows an empty row, and clearing a status puts it back to
 * that state rather than to "absent" — the two are different claims about the same day, and only
 * one of them costs somebody a day's pay. That is why the status control has a blank option and
 * why the blank is what the server is sent.</p>
 */
@Component({
  selector: 'app-hr-day-grid',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      @if (loading()) {
        <p class="notice">{{ i18n.t('common.loading') }}</p>
      } @else {
      @if (grid(); as g) {
        <div class="grid-head">
          <div>
            <h3 class="bo-panel__title">{{ g.employeeName }}</h3>
            <p class="bo-hint mono"><bdi dir="ltr">{{ g.employeeNumber }}</bdi> · {{ periodLabel() }}</p>
          </div>
          <span class="bo-state" [class.bo-state--ok]="g.mode === 'Recorded'">
            {{ i18n.t('hr.attendance.mode.' + g.mode) }}
          </span>
        </div>

        @if (g.locked) {
          <p class="notice warn" role="status">
            {{
              i18n.t('hr.attendance.lockedBy', {
                run: g.runNumber ?? '—',
                status: i18n.t('hr.run.status.' + (g.runStatus ?? 'Locked')),
              })
            }}
          </p>
        }
        @if (g.mode === 'Declared') {
          <p class="notice warn" role="status">{{ i18n.t('hr.days.declaredHint') }}</p>
        }
        @if (g.unpairedClockInsUtc.length > 0) {
          <p class="notice warn" role="status">
            {{ i18n.t('hr.days.unpaired', { count: g.unpairedClockInsUtc.length }) }}
            <span class="mono">{{ unpairedText() }}</span>
          </p>
        }

        <div class="bo-scroll bo-scroll--tall">
          <table class="bo-data">
            <thead>
              <tr>
                <th>{{ i18n.t('hr.days.date') }}</th>
                <th>{{ i18n.t('hr.days.status') }}</th>
                <th class="num">{{ i18n.t('hr.days.hours') }}</th>
                @for (rule of g.overtimeRules; track rule.key) {
                  <th class="num">{{ label(rule.labels) || rule.key }}</th>
                }
              </tr>
            </thead>
            <tbody>
              @for (date of dates(); track date) {
                <tr [class.is-dirty]="dirty().has(date)">
                  <td [attr.data-label]="i18n.t('hr.days.date')">
                    <span class="bo-cell-strong mono">{{ dayNumber(date) }}</span>
                    <span class="bo-meta">{{ weekday(date) }}</span>
                  </td>
                  <td [attr.data-label]="i18n.t('hr.days.status')">
                    <select
                      [disabled]="g.locked"
                      [ngModel]="edit(date).status"
                      (ngModelChange)="setStatus(date, $event)"
                    >
                      <option value="">{{ i18n.t('hr.days.notRecorded') }}</option>
                      @for (status of statuses; track status) {
                        <option [value]="status">{{ i18n.t('hr.day.' + status) }}</option>
                      }
                    </select>
                  </td>
                  <td class="num" [attr.data-label]="i18n.t('hr.days.hours')">
                    <input
                      class="num mono bo-input-narrow"
                      inputmode="decimal"
                      [disabled]="g.locked || edit(date).status === ''"
                      [value]="text(date, 'hours')"
                      (input)="setHours(date, $any($event.target).value)"
                    />
                  </td>
                  @for (rule of g.overtimeRules; track rule.key) {
                    <td class="num" [attr.data-label]="label(rule.labels) || rule.key">
                      <input
                        class="num mono bo-input-narrow"
                        inputmode="decimal"
                        [disabled]="g.locked || edit(date).status === ''"
                        [value]="text(date, 'ot:' + rule.key)"
                        (input)="setOvertime(date, rule.key, $any($event.target).value)"
                      />
                    </td>
                  }
                </tr>
              }
            </tbody>
            <tfoot>
              <tr>
                <th>{{ i18n.t('hr.days.summary') }}</th>
                <td [attr.data-label]="i18n.t('hr.days.status')">
                  {{ i18n.t('hr.attendance.source.' + g.summary.source) }}
                </td>
                <td class="num mono" [attr.data-label]="i18n.t('hr.sheet.daysPresent')">
                  {{ g.summary.daysPresent }}
                </td>
                @for (rule of g.overtimeRules; track rule.key) {
                  <td class="num mono" [attr.data-label]="label(rule.labels) || rule.key">
                    {{ summaryHours(g.summary.overtime, rule.key) }}
                  </td>
                }
              </tr>
            </tfoot>
          </table>
        </div>

        <p class="bo-hint">
          {{
            i18n.t('hr.days.summaryLine', {
              present: g.summary.daysPresent,
              paid: g.summary.paidLeaveDays,
              unpaid: g.summary.unpaidAbsenceDays,
            })
          }}
        </p>

        <div class="bo-actions">
          <span class="bo-state" [class.bo-state--warn]="dirty().size > 0">
            {{ dirty().size > 0 ? i18n.t('hr.sheet.unsaved', { count: dirty().size }) : i18n.t('hr.sheet.allSaved') }}
          </span>
          <button type="button" class="btn-ghost" [disabled]="busy() || dirty().size === 0" (click)="discard()">
            {{ i18n.t('hr.sheet.discard') }}
          </button>
          <button
            type="button"
            class="btn-primary"
            [disabled]="busy() || g.locked || dirty().size === 0"
            (click)="save()"
          >
            {{ i18n.t('common.save') }}
          </button>
          <button type="button" class="btn-ghost" (click)="closed.emit()">{{ i18n.t('common.close') }}</button>
        </div>
      }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      .grid-head {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: 0.75rem;
        flex-wrap: wrap;
      }
      /* The one thing a colour says here is "not yet sent"; the counter says it in words too. */
      tr.is-dirty td {
        background: var(--warn-soft);
      }
    `,
  ],
})
export class HrDayGridComponent {
  private readonly hr = inject(HrApi);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly regional = inject(RegionalService);

  readonly employeeId = input.required<string>();
  readonly year = input.required<number>();
  readonly month = input.required<number>();

  /** The sheet reloads on this: a recorded day changes the totals it shows. */
  readonly saved = output<void>();
  readonly closed = output<void>();

  readonly statuses = DAY_STATUSES;

  readonly grid = signal<AttendanceDays | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  private readonly edits = signal(new Map<string, DayEdit>());
  private readonly texts = signal(new Map<string, string>());
  readonly dirty = signal(new Set<string>());

  readonly dates = computed(() => monthDays(this.year(), this.month()));

  readonly periodLabel = computed(() => monthLabel(this.i18n.lang(), this.year(), this.month()));

  readonly unpairedText = computed(() =>
    (this.grid()?.unpairedClockInsUtc ?? []).map((at) => this.i18n.dateTime(at)).join(' · '),
  );

  constructor() {
    // Reloads when the person or the month changes, which is what a grid opened from a sheet row
    // does for its whole life.
    effect(() => {
      const id = this.employeeId();
      const year = this.year();
      const month = this.month();
      void this.load(id, year, month);
    });
  }

  private async load(employeeId: string, year: number, month: number): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const grid = await this.hr.days({ employeeId, year, month });
      this.grid.set(grid);
      this.reset(grid);
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.loading.set(false);
    }
  }

  private reset(grid: AttendanceDays): void {
    const edits = new Map<string, DayEdit>();
    for (const day of grid.days ?? []) {
      const overtime: Record<string, number | null> = {};
      for (const entry of day.overtime ?? []) overtime[entry.ruleKey] = entry.hours;
      edits.set(day.workDate, {
        status: (day.status ?? '') as DayStatus | '',
        hours: day.hoursWorked ?? null,
        overtime,
      });
    }
    this.edits.set(edits);
    this.texts.set(new Map());
    this.dirty.set(new Set());
  }

  // ---------- reading a cell ----------

  edit(date: string): DayEdit {
    return this.edits().get(date) ?? { status: '', hours: null, overtime: {} };
  }

  /** What the box shows: what is being typed if anything, otherwise the stored figure. */
  text(date: string, field: string): string {
    const typed = this.texts().get(`${date}|${field}`);
    if (typed !== undefined) return typed;
    const edit = this.edit(date);
    const value = field === 'hours' ? edit.hours : (edit.overtime[field.slice(3)] ?? null);
    return value === null || value === undefined ? '' : String(value);
  }

  // ---------- writing ----------

  setStatus(date: string, status: string): void {
    this.apply(date, (edit) => ({ ...edit, status: (status ?? '') as DayStatus | '' }));
  }

  setHours(date: string, raw: string): void {
    const figure = parseFigure(raw, 24);
    this.texts.update((map) => new Map(map).set(`${date}|hours`, raw));
    if (figure.problem === 'notANumber' || figure.problem === 'negative') return;
    this.apply(date, (edit) => ({ ...edit, hours: figure.value }));
  }

  setOvertime(date: string, ruleKey: string, raw: string): void {
    const figure = parseFigure(raw, 24);
    this.texts.update((map) => new Map(map).set(`${date}|ot:${ruleKey}`, raw));
    if (figure.problem === 'notANumber' || figure.problem === 'negative') return;
    this.apply(date, (edit) => ({ ...edit, overtime: { ...edit.overtime, [ruleKey]: figure.value } }));
  }

  private apply(date: string, change: (edit: DayEdit) => DayEdit): void {
    this.edits.update((map) => {
      const next = new Map(map);
      next.set(date, change(this.edit(date)));
      return next;
    });
    this.dirty.update((set) => new Set(set).add(date));
  }

  // ---------- saving ----------

  /** Throws away the unsaved days, after asking: they were typed one cell at a time. */
  async discard(): Promise<void> {
    const grid = this.grid();
    if (!grid) return;
    if (this.dirty().size > 0 && !(await this.confirm.ask(discardRequest(this.i18n)))) return;
    this.reset(grid);
  }

  async save(): Promise<void> {
    const grid = this.grid();
    if (!grid || this.dirty().size === 0) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n)))) return;

    const days: AttendanceDay[] = [];
    for (const date of this.dirty()) {
      const edit = this.edit(date);
      const overtime: AttendanceOvertime[] = Object.entries(edit.overtime)
        .filter(([, hours]) => (hours ?? 0) > 0)
        .map(([ruleKey, hours]) => ({ ruleKey, hours: hours ?? 0 }));
      days.push({
        employeeId: grid.employeeId,
        workDate: date,
        // A cleared status deletes the day. "Nothing recorded" has to stay reachable, or a day
        // ticked by mistake could only ever be corrected into some other claim about that day.
        status: edit.status === '' ? null : edit.status,
        hoursWorked: edit.status === '' ? null : edit.hours,
        overtime: edit.status === '' ? [] : overtime,
      });
    }

    this.busy.set(true);
    this.error.set(null);
    try {
      const saved = await this.hr.saveDays(days);
      this.grid.set(saved);
      this.reset(saved);
      this.saved.emit();
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.busy.set(false);
    }
  }

  // ---------- words ----------

  label(labels: TextByLang | undefined): string {
    if (!labels) return '';
    return labels[this.i18n.lang()] ?? labels['fr'] ?? labels['en'] ?? '';
  }

  dayNumber(date: string): string {
    return date.slice(8);
  }

  /**
   * The weekday, read from the date's own parts rather than from a parsed instant: the row is a
   * calendar day in the company's month, and a device an hour behind must not rename it.
   */
  weekday(date: string): string {
    const [year, month, day] = date.split('-').map(Number);
    const at = new Date(Date.UTC(year, month - 1, day));
    try {
      return new Intl.DateTimeFormat(this.i18n.lang(), { weekday: 'short', timeZone: 'UTC' }).format(at);
    } catch {
      return new Intl.DateTimeFormat('fr', { weekday: 'short', timeZone: 'UTC' }).format(at);
    }
  }

  summaryHours(overtime: readonly AttendanceOvertime[], ruleKey: string): string {
    const found = overtime.find((entry) => entry.ruleKey === ruleKey);
    return found ? String(found.hours) : '—';
  }

  private message(error: unknown): string {
    const problem = hrProblem(error);
    if (!problem) return moneyErrorMessage(error, (key, params) => this.i18n.t(key, params));
    const key = `hr.problem.${problem.code}`;
    const text = this.i18n.t(key, { key: problem.key ?? '', run: problem.runNumber ?? '' });
    return text === key ? problem.message || key : text;
  }
}

/** The month in the reader's language, from the two integers — no instant, no time zone. */
export function monthLabel(lang: string, year: number, month: number): string {
  const at = new Date(Date.UTC(year, Math.max(month - 1, 0), 1));
  try {
    return new Intl.DateTimeFormat(lang, {
      month: 'long',
      year: 'numeric',
      numberingSystem: 'latn',
      timeZone: 'UTC',
    }).format(at);
  } catch {
    return `${year}-${String(month).padStart(2, '0')}`;
  }
}

/** The twelve month names, for a period picker that never needs twelve translation keys. */
export function monthNames(lang: string): string[] {
  const names: string[] = [];
  for (let month = 1; month <= 12; month++) {
    const at = new Date(Date.UTC(2000, month - 1, 1));
    try {
      names.push(new Intl.DateTimeFormat(lang, { month: 'long', timeZone: 'UTC' }).format(at));
    } catch {
      names.push(String(month));
    }
  }
  return names;
}
