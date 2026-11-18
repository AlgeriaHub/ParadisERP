import { Component, computed, inject, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { FoldComponent } from '../../../ui/fold.component';
import { DateInputComponent } from '../../../ui/date-input.component';
import { WorkSchedule, WorkSchedulePeriod } from './payroll-api.service';

interface WeekdayOption {
  day: number;
  label: string;
}

@Component({
  selector: 'app-work-schedule-settings',
  standalone: true,
  imports: [FormsModule, FoldComponent, DateInputComponent],
  template: `
    <app-fold class="bo-group bo-group--row" [heading]="i18n.t('hr.schedule.title')">
      <p class="bo-group__note" foldLead>{{ i18n.t('hr.schedule.lead') }}</p>

      <div class="bo-stack">
        <!-- Normal working hours -->
        <div class="bo-fields bo-fields--roomy">
          <div class="bo-field">
            <label for="ws-start">{{ i18n.t('hr.schedule.start') }}</label>
            <input
              id="ws-start"
              type="time"
              class="mono"
              [disabled]="disabled()"
              [ngModel]="schedule()?.start ?? '08:00'"
              (ngModelChange)="setStart($event)"
            />
          </div>

          <div class="bo-field">
            <label for="ws-end">{{ i18n.t('hr.schedule.end') }}</label>
            <input
              id="ws-end"
              type="time"
              class="mono"
              [disabled]="disabled()"
              [ngModel]="schedule()?.end ?? '17:00'"
              (ngModelChange)="setEnd($event)"
            />
          </div>

          <div class="bo-field">
            <label for="ws-break">{{ i18n.t('hr.schedule.breakMinutes') }}</label>
            <input
              id="ws-break"
              type="number"
              min="0"
              class="num mono"
              [disabled]="disabled()"
              [ngModel]="schedule()?.breakMinutes ?? 60"
              (ngModelChange)="setBreakMinutes($event)"
            />
          </div>

          <div class="bo-field">
            <label for="ws-grace">{{ i18n.t('hr.schedule.lateGraceMinutes') }}</label>
            <input
              id="ws-grace"
              type="number"
              min="0"
              class="num mono"
              [disabled]="disabled()"
              [ngModel]="schedule()?.lateGraceMinutes ?? 0"
              (ngModelChange)="setLateGraceMinutes($event)"
            />
          </div>
        </div>

        <!-- Paid hours text -->
        <p class="bo-callout">
          <span class="mono">{{ i18n.t('hr.schedule.paidHours', { hours: paidHours() }) }}</span>
        </p>

        <!-- Rest days -->
        <div class="bo-field bo-field--wide">
          <label id="ws-rest-days-label">{{ i18n.t('hr.schedule.restDays') }}</label>
          <div class="bo-fields bo-fields--roomy" [attr.aria-labelledby]="'ws-rest-days-label'">
            @for (w of weekdays(); track w.day) {
              <div class="bo-check">
                <input
                  [id]="'ws-day-' + w.day"
                  type="checkbox"
                  [disabled]="disabled() || isLastUnticked(w.day)"
                  [checked]="isRestDay(w.day)"
                  (change)="toggleRestDay(w.day, $any($event.target).checked)"
                />
                <label [for]="'ws-day-' + w.day">{{ w.label }}</label>
              </div>
            }
          </div>
        </div>

        <!-- Special periods table -->
        <div class="bo-field bo-field--wide">
          <label>{{ i18n.t('hr.schedule.periods') }}</label>
          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('hr.schedule.periodName') }}</th>
                  <th>{{ i18n.t('hr.schedule.fromDate') }}</th>
                  <th>{{ i18n.t('hr.schedule.toDate') }}</th>
                  <th>{{ i18n.t('hr.schedule.start') }}</th>
                  <th>{{ i18n.t('hr.schedule.end') }}</th>
                  <th class="num">{{ i18n.t('hr.schedule.breakMinutes') }}</th>
                  <th>{{ i18n.t('common.actions') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (period of schedule()?.periods ?? []; track $index) {
                  <tr>
                    <td [attr.data-label]="i18n.t('hr.schedule.periodName')">
                      <input
                        [disabled]="disabled()"
                        [ngModel]="period.name"
                        (ngModelChange)="updatePeriod($index, { name: $event })"
                      />
                    </td>
                    <td [attr.data-label]="i18n.t('hr.schedule.fromDate')">
                      <app-date-input
                        class="mono"
                        [disabled]="disabled()"
                        [ngModel]="period.from"
                        (ngModelChange)="updatePeriod($index, { from: $event })"
                      />
                    </td>
                    <td [attr.data-label]="i18n.t('hr.schedule.toDate')">
                      <app-date-input
                        class="mono"
                        [disabled]="disabled()"
                        [ngModel]="period.to"
                        (ngModelChange)="updatePeriod($index, { to: $event })"
                      />
                    </td>
                    <td [attr.data-label]="i18n.t('hr.schedule.start')">
                      <input
                        type="time"
                        class="mono"
                        [disabled]="disabled()"
                        [ngModel]="period.start"
                        (ngModelChange)="updatePeriod($index, { start: $event })"
                      />
                    </td>
                    <td [attr.data-label]="i18n.t('hr.schedule.end')">
                      <input
                        type="time"
                        class="mono"
                        [disabled]="disabled()"
                        [ngModel]="period.end"
                        (ngModelChange)="updatePeriod($index, { end: $event })"
                      />
                    </td>
                    <td class="num" [attr.data-label]="i18n.t('hr.schedule.breakMinutes')">
                      <input
                        type="number"
                        min="0"
                        class="num mono bo-input-narrow"
                        [disabled]="disabled()"
                        [ngModel]="period.breakMinutes"
                        (ngModelChange)="updatePeriod($index, { breakMinutes: numeric($event) })"
                      />
                    </td>
                    <td class="bo-cell-actions" [attr.data-label]="i18n.t('common.actions')">
                      <button
                        type="button"
                        class="btn-ghost bo-small"
                        [disabled]="disabled()"
                        [attr.aria-label]="i18n.t('hr.settings.remove')"
                        (click)="removePeriod($index)"
                      >
                        &times;
                      </button>
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="7" class="bo-empty">
                      <strong>{{ i18n.t('hr.schedule.noPeriods') }}</strong>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <div class="bo-actions bo-actions--start">
            <button
              type="button"
              class="btn-ghost"
              [disabled]="disabled()"
              (click)="addPeriod()"
            >
              {{ i18n.t('hr.schedule.addPeriod') }}
            </button>
          </div>

          @if (hasPeriodTimeOrderError()) {
            <span class="bo-error" role="alert">{{ i18n.t('hr.schedule.errEndBeforeStart') }}</span>
          }
          @if (hasPeriodDateOverlapError()) {
            <span class="bo-error" role="alert">{{ i18n.t('hr.schedule.errDateOverlap') }}</span>
          }
        </div>
      </div>
    </app-fold>
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class WorkScheduleSettingsComponent {
  readonly i18n = inject(I18nService);

  readonly schedule = input<WorkSchedule>();
  readonly disabled = input<boolean>(false);
  readonly scheduleChange = output<WorkSchedule>();

  readonly weekdays = computed<WeekdayOption[]>(() => {
    const lang = this.i18n.lang();
    const days: WeekdayOption[] = [];
    for (let i = 0; i < 7; i++) {
      // 2026-10-04 is a Sunday (day 0 in .NET DayOfWeek)
      const at = new Date(Date.UTC(2026, 9, 4 + i));
      let name: string;
      try {
        name = new Intl.DateTimeFormat(lang, { weekday: 'long', timeZone: 'UTC' }).format(at);
      } catch {
        name = new Intl.DateTimeFormat('fr', { weekday: 'long', timeZone: 'UTC' }).format(at);
      }
      const capitalized = name.charAt(0).toUpperCase() + name.slice(1);
      days.push({ day: i, label: capitalized });
    }
    return days;
  });

  readonly paidHours = computed(() => {
    const s = this.schedule();
    if (!s || !s.start || !s.end) return 0;
    const startMins = parseTime(s.start);
    const endMins = parseTime(s.end);
    if (startMins === null || endMins === null || endMins <= startMins) return 0;
    const breakMins = Number(s.breakMinutes) || 0;
    const paidMins = Math.max(0, endMins - startMins - breakMins);
    return Math.round((paidMins / 60) * 100) / 100;
  });

  readonly hasPeriodTimeOrderError = computed(() => {
    const periods = this.schedule()?.periods ?? [];
    return periods.some((p) => {
      if (!p.start || !p.end) return false;
      const s = parseTime(p.start);
      const e = parseTime(p.end);
      return s !== null && e !== null && e <= s;
    });
  });

  readonly hasPeriodDateOverlapError = computed(() => {
    const periods = this.schedule()?.periods ?? [];
    for (let i = 0; i < periods.length; i++) {
      for (let j = i + 1; j < periods.length; j++) {
        const p1 = periods[i];
        const p2 = periods[j];
        if (!p1.from || !p1.to || !p2.from || !p2.to) continue;
        if (p1.from <= p2.to && p2.from <= p1.to) return true;
      }
    }
    return false;
  });

  isRestDay(day: number): boolean {
    return (this.schedule()?.restDays ?? []).includes(day);
  }

  isLastUnticked(day: number): boolean {
    const restDays = this.schedule()?.restDays ?? [];
    return !restDays.includes(day) && restDays.length >= 6;
  }

  setStart(start: string): void {
    this.scheduleChange.emit({ ...this.current(), start });
  }

  setEnd(end: string): void {
    this.scheduleChange.emit({ ...this.current(), end });
  }

  setBreakMinutes(value: unknown): void {
    this.scheduleChange.emit({ ...this.current(), breakMinutes: numeric(value) });
  }

  setLateGraceMinutes(value: unknown): void {
    this.scheduleChange.emit({ ...this.current(), lateGraceMinutes: numeric(value) });
  }

  toggleRestDay(day: number, checked: boolean): void {
    const cur = this.current();
    let restDays = cur.restDays;
    if (checked) {
      if (!restDays.includes(day)) {
        if (restDays.length >= 6) return;
        restDays = [...restDays, day].sort((a, b) => a - b);
      }
    } else {
      restDays = restDays.filter((d) => d !== day);
    }
    this.scheduleChange.emit({ ...cur, restDays });
  }

  addPeriod(): void {
    const cur = this.current();
    const newPeriod: WorkSchedulePeriod = {
      name: '',
      from: '',
      to: '',
      start: cur.start,
      end: cur.end,
      breakMinutes: cur.breakMinutes,
    };
    this.scheduleChange.emit({
      ...cur,
      periods: [...cur.periods, newPeriod],
    });
  }

  removePeriod(index: number): void {
    const cur = this.current();
    this.scheduleChange.emit({
      ...cur,
      periods: cur.periods.filter((_, i) => i !== index),
    });
  }

  updatePeriod(index: number, patch: Partial<WorkSchedulePeriod>): void {
    const cur = this.current();
    this.scheduleChange.emit({
      ...cur,
      periods: cur.periods.map((p, i) => (i === index ? { ...p, ...patch } : p)),
    });
  }

  numeric(value: unknown): number {
    return numeric(value);
  }

  private current(): WorkSchedule {
    const s = this.schedule();
    return {
      start: s?.start ?? '08:00',
      end: s?.end ?? '17:00',
      breakMinutes: s?.breakMinutes ?? 60,
      lateGraceMinutes: s?.lateGraceMinutes ?? 0,
      restDays: [...(s?.restDays ?? [5])],
      periods: (s?.periods ?? []).map((p) => ({ ...p })),
    };
  }
}

function numeric(value: unknown): number {
  const parsed = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function parseTime(time: string): number | null {
  if (!time || !time.includes(':')) return null;
  const [h, m] = time.split(':').map(Number);
  if (isNaN(h) || isNaN(m)) return null;
  return h * 60 + m;
}
