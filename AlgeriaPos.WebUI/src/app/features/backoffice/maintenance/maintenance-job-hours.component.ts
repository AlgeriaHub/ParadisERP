import { Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemCode } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { DateInputComponent } from '../../../ui/date-input.component';
import { DialogComponent } from '../../../ui/dialog.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatDay, formatQty } from '../backoffice-format';
import { MaintenanceJob } from './maintenance-api.service';
import { MaintenanceBooking, MaintenanceHoursApi, MaintenanceLabourCandidate } from './maintenance-hours-api.service';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { maintenanceError } from './maintenance-words';

/** The refusals of the hours routes (Phase M3, `MaintenanceLabour`) said in the reader's language; others go to `maintenanceError`. */
const HOURS_REFUSALS: Readonly<Record<string, string>> = {
  exceeds_attendance: 'maint.hours.error.exceeds',
  override_forbidden: 'maint.hours.error.overrideForbidden',
  payroll_month_locked: 'maint.hours.error.payrollLocked',
  already_booked: 'maint.hours.error.alreadyBooked',
  job_not_open: 'maint.hours.error.notOpen',
  employee_not_of_branch: 'maint.hours.error.notOfBranch',
  employee_not_employed: 'maint.hours.error.notEmployed',
  future_day: 'maint.hours.error.futureDay',
  invalid_hours: 'maint.hours.error.hours',
  invalid_overtime_hours: 'maint.hours.error.overtime',
  employee_not_on_workshop_list: 'maint.hours.error.notOnList',
};

/**
 * Maintenance › Job › Hours (Phase M3, docs/maintenance/plan.md §2.5 and §7): who worked on the job, which day and for how
 * long — hours only, their cost comes later. On an open job a `maintenance.jobs.manage` holder books a person's hours for a
 * day: the dialog says in words what the day's attendance shows and what is already booked on production records and
 * jobs, so what is left; past it only a `maintenance.labour.override` holder books, with the reason, which is kept. A
 * closed payroll month takes no more hours and gives none back. The bookings are paged by the server (owner, 2026-11-17:
 * "paginate in backend, not frontend"; ten rows to begin with) and take no filter: the card's head totals every one of
 * them, from the server's summary.
 */
@Component({
  selector: 'app-maintenance-job-hours',
  standalone: true,
  imports: [FormsModule, ...MENU, DateInputComponent, DialogComponent, ListFooterComponent, TableStateComponent],
  template: `
    <section class="ui-surface" aria-labelledby="mj-hours-title">
      <div class="ui-surface__head">
        <h2 id="mj-hours-title">{{ i18n.t('maint.hours.title') }}</h2>
        @if (hours(); as h) {
          <span class="ui-sub">{{ i18n.t('maint.hours.total', { n: qty(h.totalHours) }) }}</span>
        }
        @if (canBook()) {
          <button type="button" class="btn-ghost head-action" [disabled]="busy()" (click)="openBooking()">+ {{ i18n.t('maint.hours.book') }}</button>
        }
      </div>
      <div class="ui-surface__body hours">
        <p class="bo-hint">{{ i18n.t('maint.hours.lead') }}</p>
        @if (job().status === 'Draft' && canManage()) {
          <p class="notice info" role="status">{{ i18n.t('maint.hours.draftNote') }}</p>
        }
        @if ((error() || listError()) && pager.loaded()) {
          <p class="notice danger" role="alert">{{ error() || listError() }}</p>
        }
        @if (done()) {
          <p class="notice ok" role="status">{{ done() }}</p>
        }
        @if (!pager.loaded() && !listError()) {
          <app-table-state kind="loading" />
        } @else if (!pager.loaded()) {
          <app-table-state kind="error" [message]="listError()" (retry)="load()" />
        } @else if (!pager.total()) {
          <app-table-state kind="empty" [message]="i18n.t('maint.hours.empty')" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('maint.hours.day') }}</th>
                  <th>{{ i18n.t('maint.hours.employee') }}</th>
                  <th class="num">{{ i18n.t('maint.hours.hours') }}</th>
                  <th class="num">{{ i18n.t('maint.hours.overtime') }}</th>
                  <th class="num">{{ i18n.t('maint.hours.attendance') }}</th>
                  <th>{{ i18n.t('maint.hours.bookedBy') }}</th>
                  @if (editable()) {
                    <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (b of pager.rows(); track b.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('maint.hours.day')"><span dir="ltr">{{ day(b.day) }}</span></td>
                    <td [attr.data-label]="i18n.t('maint.hours.employee')">
                      {{ b.employeeName }} <span class="ui-sub mono" dir="ltr">{{ b.employeeNumber }}</span>
                      @if (b.overrideReason) {
                        <span class="ui-sub">{{ i18n.t('maint.hours.overridden', { by: b.overriddenBy ?? '—', reason: b.overrideReason }) }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.hours.hours')"><strong dir="ltr">{{ qty(b.hours) }}</strong></td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.hours.overtime')"><span dir="ltr">{{ qty(b.overtimeHours) }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.hours.attendance')"><span dir="ltr">{{ qty(b.attendanceHours) }}</span></td>
                    <td [attr.data-label]="i18n.t('maint.hours.bookedBy')">
                      {{ b.userName ?? '—' }} <span class="ui-sub mono">{{ i18n.dateTime(b.atUtc) }}</span>
                    </td>
                    @if (editable()) {
                      <td class="ui-cell-menu">
                        <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + b.employeeName + ', ' + day(b.day)">
                          <span menuTrigger aria-hidden="true">⋯</span>
                          <button type="button" appMenuItem class="is-danger" (click)="remove(b)">{{ i18n.t('maint.hours.remove') }}</button>
                        </app-menu>
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
        }
      </div>
    </section>

    <app-dialog [open]="booking()" size="md" [heading]="i18n.t('maint.hours.bookHeading', { n: job().number })" (dismiss)="booking.set(false)">
      @if (booking()) {
        <div class="bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          <div class="bo-field">
            <label for="mjh-day">{{ i18n.t('maint.hours.day') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <app-date-input id="mjh-day" [ngModel]="bookDay()" (ngModelChange)="changeDay($event)" />
          </div>
          <div class="bo-field">
            <label for="mjh-emp">{{ i18n.t('maint.hours.employee') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <select id="mjh-emp" [(ngModel)]="bookEmployee" [disabled]="loadingCandidates()">
              @for (c of candidates(); track c.employeeId) {
                <option [ngValue]="c.employeeId" [disabled]="c.payrollLocked || c.bookedOnJob">{{ c.fullName }} · {{ candidateText(c) }}</option>
              }
            </select>
            @if (!loadingCandidates() && !candidates().length) {
              <p class="bo-hint">{{ i18n.t('maint.hours.noCandidates') }}</p>
            }
          </div>
          @if (chosen(); as c) {
            <p class="notice info" role="status">{{ ceiling(c) }}</p>
          }
          <div class="bo-field">
            <label for="mjh-hours">{{ i18n.t('maint.hours.hours') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <input id="mjh-hours" type="number" min="0" max="24" step="0.25" class="num mono" [(ngModel)]="bookHours" />
          </div>
          <div class="bo-field">
            <label for="mjh-overtime">{{ i18n.t('maint.hours.overtime') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
            <input id="mjh-overtime" type="number" min="0" [max]="bookHours" step="0.25" class="num mono" [(ngModel)]="bookOvertime"
              aria-describedby="mjh-overtime-help" [attr.aria-invalid]="overtimeInvalid() ? 'true' : null" />
            <p id="mjh-overtime-help" class="bo-hint">{{ i18n.t('maint.hours.overtimeHint') }}</p>
            @if (overtimeInvalid()) {
              <p class="notice warn" role="alert">{{ i18n.t('maint.hours.error.overtime') }}</p>
            }
          </div>
          @if (overNeeded() && canOverride()) {
            <div class="bo-field">
              <label for="mjh-why">{{ i18n.t('maint.hours.overrideReason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="mjh-why" maxlength="400" [(ngModel)]="bookReason" />
              <p class="bo-hint">{{ i18n.t('maint.hours.overrideHint') }}</p>
            </div>
          } @else if (overNeeded()) {
            <p class="notice warn" role="status">{{ i18n.t('maint.hours.overAttendance') }}</p>
          }
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="booking.set(false)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="!canSubmit()" (click)="book()">{{ i18n.t('maint.hours.book') }}</button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .head-action {
        margin-inline-start: auto;
      }
      .hours {
        display: grid;
        gap: var(--sp-3);
      }
      .hours > .bo-hint {
        margin: 0;
      }
    `,
  ],
})
export class MaintenanceJobHoursComponent {
  private readonly api = inject(MaintenanceHoursApi);
  private readonly pages = inject(MaintenancePagesApi);
  private readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);

  readonly job = input.required<MaintenanceJob>();

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly booking = signal(false);
  readonly formError = signal<string | null>(null);
  readonly candidates = signal<MaintenanceLabourCandidate[]>([]);
  readonly loadingCandidates = signal(false);
  readonly bookDay = signal('');
  bookEmployee = '';
  bookHours: number | null = 1;
  bookOvertime: number | null = 0;
  bookReason = '';

  readonly canManage = computed(() => mayDo(this.auth.currentUser(), 'maintenance.jobs.manage'));
  readonly canOverride = computed(() => mayDo(this.auth.currentUser(), 'maintenance.labour.override'));
  /** Hours change only on an open job (the server refuses them on a draft or a closed one). */
  readonly editable = computed(() => this.canManage() && this.job().status === 'Open');
  readonly canBook = computed(() => this.editable() && !!this.hours());
  /**
   * Ten bookings to begin with, 20, 50 or 100 at the user's choice (owner, 2026-11-17), a page at a time from the server;
   * another job starts at page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.jobHours(page.filters.job, { skip: page.skip, take: page.take }),
    { key: 'maintenance.job.hours', filters: () => ({ job: this.job().id }), debounceMs: 0 },
  );
  /** The job's hours without the bookings: their total over every booking, and the company's today. */
  readonly hours = computed(() => this.pager.summary());
  /** The hours could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? this.say(e) : null;
  });

  /** Asks again for the page on screen. */
  async load(): Promise<void> {
    await this.pager.reload();
  }

  qty(value: number | null | undefined): string {
    return formatQty(value ?? 0, this.i18n.lang());
  }

  /** A day of the company's calendar (`yyyy-MM-dd`), as dd/MM/yyyy. */
  day(value: string): string {
    return formatDay(`${value.slice(0, 10)}T00:00:00Z`, this.i18n.lang(), 'UTC');
  }

  candidateText(c: MaintenanceLabourCandidate): string {
    if (c.payrollLocked) return this.i18n.t('maint.hours.payrollClosed');
    if (c.bookedOnJob) return this.i18n.t('maint.hours.onJob');
    if (c.attendanceHours === null) return this.i18n.t('maint.hours.declared');
    return this.i18n.t('maint.hours.attendanceOf', { n: this.qty(c.attendanceHours), booked: this.qty(c.bookedHours) });
  }

  /** The person picked in the dialog. */
  chosen(): MaintenanceLabourCandidate | undefined {
    return this.candidates().find((c) => c.employeeId === this.bookEmployee);
  }

  /** The ceiling in words: what the day's attendance shows, what is already booked on records and jobs, and what is left. */
  ceiling(c: MaintenanceLabourCandidate): string {
    const day = this.day(this.bookDay());
    if (c.attendanceHours === null) return this.i18n.t('maint.hours.ceilingDeclared', { who: c.fullName });
    const left = Math.max(0, c.attendanceHours - c.bookedHours);
    return this.i18n.t('maint.hours.ceiling', {
      who: c.fullName, day, attendance: this.qty(c.attendanceHours), booked: this.qty(c.bookedHours), left: this.qty(left),
    });
  }

  /** Whether the hours pass what the day's attendance leaves, so the override and its reason are needed. */
  overNeeded(): boolean {
    const c = this.chosen();
    if (!c) return false;
    return c.attendanceHours === null || c.bookedHours + (Number(this.bookHours) || 0) > c.attendanceHours;
  }

  overtimeInvalid(): boolean {
    const overtime = Number(this.bookOvertime) || 0;
    return overtime < 0 || overtime > (Number(this.bookHours) || 0);
  }

  canSubmit(): boolean {
    const hours = Number(this.bookHours);
    return !this.busy() && !!this.bookEmployee && !!this.bookDay() && Number.isFinite(hours) && hours > 0 && hours <= 24
      && !this.overtimeInvalid() && (!this.overNeeded() || (this.canOverride() && !!this.bookReason.trim()));
  }

  async openBooking(): Promise<void> {
    this.formError.set(null);
    this.bookHours = 1;
    this.bookOvertime = 0;
    this.bookReason = '';
    this.booking.set(true);
    await this.changeDay(this.bookDay() || this.hours()?.today || '');
  }

  async changeDay(day: string | null): Promise<void> {
    this.bookDay.set(day ?? '');
    this.candidates.set([]);
    this.bookEmployee = '';
    if (!day) return;
    this.loadingCandidates.set(true);
    try {
      const found = await this.api.candidates(this.job().id, day);
      this.candidates.set(found);
      this.bookEmployee = found.find((c) => !c.payrollLocked && !c.bookedOnJob)?.employeeId ?? '';
    } catch (e) {
      this.formError.set(this.say(e));
    } finally {
      this.loadingCandidates.set(false);
    }
  }

  async book(): Promise<void> {
    if (!this.canSubmit()) return;
    this.busy.set(true);
    this.formError.set(null);
    this.done.set(null);
    try {
      await this.api.book(this.job().id, {
        employeeId: this.bookEmployee,
        day: this.bookDay(),
        hours: Number(this.bookHours),
        overtimeHours: Number(this.bookOvertime) || 0,
        overrideReason: this.overNeeded() ? this.bookReason.trim() || null : null,
      });
      await this.pager.reload();
      this.booking.set(false);
      this.done.set(this.i18n.t('maint.hours.bookedNotice'));
    } catch (e) {
      this.formError.set(this.say(e));
    } finally {
      this.busy.set(false);
    }
  }

  async remove(b: MaintenanceBooking): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    try {
      await this.api.remove(this.job().id, b.id);
      await this.pager.reload();
      this.done.set(this.i18n.t('maint.hours.removedNotice'));
    } catch (e) {
      this.error.set(this.say(e));
    } finally {
      this.busy.set(false);
    }
  }

  private say(e: unknown): string {
    const code = problemCode(e);
    return code && HOURS_REFUSALS[code] ? this.i18n.t(HOURS_REFUSALS[code]) : maintenanceError(e, (k) => this.i18n.t(k));
  }
}
