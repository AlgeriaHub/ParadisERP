import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../backoffice-format';
import { monthNames } from './attendance-day-grid.component';
import { AttendanceReviewDay, AttendanceReviewPerson, hrProblem } from './hr-api.service';
import { PayrollPagesApi } from './payroll-pages.api';
import { moneyErrorMessage } from './wage-money-api.service';
import { StatusComponent } from '../../../ui/status.component';
import { StatusTone, toneOf } from '../../../ui/status-tones';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';

export type DayFlagKind = 'late' | 'earlyLeave' | 'missingOut' | 'possibleOvertime' | 'restDayWork';

export interface DayFlag {
  kind: DayFlagKind;
  text: string;
  tone: StatusTone;
}

@Component({
  selector: 'app-hr-attendance-review',
  standalone: true,
  imports: [FormsModule, StatusComponent, ListFooterComponent],
  template: `
    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <p class="bo-panel__note">{{ i18n.t('hr.review.note') }}</p>

          <div class="bo-actions bo-actions--start">
            @if (lock.locked()) {
              <p class="bo-scope-note" id="ar-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
            } @else {
              <div class="bo-field">
                <label for="ar-branch">{{ i18n.t('hr.employees.branch') }}</label>
                <select id="ar-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
                  @for (branch of branches(); track branch.id) {
                    <option [value]="branch.id">{{ branchName(branch) }}</option>
                  }
                </select>
              </div>
            }

            <div class="bo-field">
              <label for="ar-month">{{ i18n.t('hr.sheet.month') }}</label>
              <select id="ar-month" [ngModel]="month()" (ngModelChange)="setPeriod(year(), +$event)">
                @for (name of months(); track $index) {
                  <option [ngValue]="$index + 1">{{ name }}</option>
                }
              </select>
            </div>

            <div class="bo-field">
              <label for="ar-year">{{ i18n.t('hr.sheet.year') }}</label>
              <input
                id="ar-year"
                type="number"
                min="2000"
                max="2100"
                class="num mono bo-input-narrow"
                [ngModel]="year()"
                (ngModelChange)="setPeriod(+$event, month())"
              />
            </div>

            <div class="bo-check">
              <input
                id="ar-flagged"
                type="checkbox"
                [ngModel]="flaggedOnly()"
                (ngModelChange)="setFlaggedOnly($event)"
              />
              <label for="ar-flagged">{{ i18n.t('hr.review.flaggedOnly') }}</label>
            </div>

            @if (dayCount() || filtered()) {
              <!-- Both run on the server over the month's days (owner, 2026-11-17: "paginate in backend"). -->
              <div class="bo-field">
                <label for="ar-f-employee">{{ i18n.t('hr.review.colEmployee') }}</label>
                <select id="ar-f-employee" [ngModel]="employeeFilter()" (ngModelChange)="employeeFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  @for (person of people(); track person.employeeId) {
                    <option [ngValue]="person.employeeId">{{ person.employeeName }} ({{ person.employeeNumber }})</option>
                  }
                </select>
              </div>
              <div class="bo-field">
                <label for="ar-f-flag">{{ i18n.t('hr.review.colToCheck') }}</label>
                <select id="ar-f-flag" [ngModel]="flagFilter()" (ngModelChange)="flagFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="late">{{ i18n.t('hr.filter.flag.late') }}</option>
                  <option ngValue="earlyLeave">{{ i18n.t('hr.filter.flag.earlyLeave') }}</option>
                  <option ngValue="missingOut">{{ i18n.t('hr.review.flagMissingOut') }}</option>
                  <option ngValue="possibleOvertime">{{ i18n.t('hr.filter.flag.overtime') }}</option>
                  <option ngValue="restDayWork">{{ i18n.t('hr.review.flagRestDayWork') }}</option>
                  @if (!flaggedOnly()) {
                    <option ngValue="none">{{ i18n.t('hr.review.noFlags') }}</option>
                  }
                </select>
              </div>
              @if (filtered()) {
                <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
              }
            }
          </div>
        </div>

        <div class="bo-panel__body">
          @if (loading()) {
            <p class="notice">{{ i18n.t('common.loading') }}</p>
          } @else {
            @if (dayCount() === 0) {
              <p class="bo-empty">
                <strong>{{ i18n.t('hr.review.empty') }}</strong>
              </p>
            } @else if (pager.total() === 0) {
              <p class="bo-empty">
                <strong>{{ i18n.t('ui.state.noResults') }}</strong>
                <button type="button" class="btn-ghost bo-small" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
              </p>
            } @else {
              <div class="bo-scroll bo-scroll--tall">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('hr.review.colDate') }}</th>
                      <th>{{ i18n.t('hr.review.colEmployee') }}</th>
                      <th>{{ i18n.t('hr.review.colSchedule') }}</th>
                      <th class="num">{{ i18n.t('hr.review.colFirstIn') }}</th>
                      <th class="num">{{ i18n.t('hr.review.colLastOut') }}</th>
                      <th class="num">{{ i18n.t('hr.review.colWorked') }}</th>
                      <th>{{ i18n.t('hr.review.colToCheck') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (day of pager.rows(); track day.employeeId + '|' + day.workDate) {
                      <tr>
                        <td [attr.data-label]="i18n.t('hr.review.colDate')">
                          <span class="bo-cell-strong">{{ weekday(day.workDate) }}</span>
                          <span class="mono bo-soft">{{ day.workDate }}</span>
                        </td>
                        <td [attr.data-label]="i18n.t('hr.review.colEmployee')">
                          <span class="bo-cell-strong">{{ personOf(day.employeeId)?.employeeName || '—' }}</span>
                          <span class="bo-meta mono"><bdi dir="ltr">{{ personOf(day.employeeId)?.employeeNumber || '' }}</bdi></span>
                        </td>
                        <td [attr.data-label]="i18n.t('hr.review.colSchedule')">
                          @if (day.isRestDay) {
                            <span class="bo-soft">{{ i18n.t('hr.review.restDay') }}</span>
                          } @else {
                            <span class="mono">{{ day.scheduledStart || '—' }} – {{ day.scheduledEnd || '—' }}</span>
                            @if (day.periodName) {
                              <span class="bo-state">{{ day.periodName }}</span>
                            }
                          }
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('hr.review.colFirstIn')">
                          {{ day.firstIn || '—' }}
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('hr.review.colLastOut')">
                          {{ day.lastOut || '—' }}
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('hr.review.colWorked')">
                          {{ formatHours(day.workedHours) }}
                        </td>
                        <td [attr.data-label]="i18n.t('hr.review.colToCheck')">
                          <div class="bo-states">
                            @for (flag of rowFlags(day); track flag.text) {
                              <app-status [tone]="flag.tone">{{ flag.text }}</app-status>
                            }
                            @if (rowFlags(day).length === 0) {
                              <span class="bo-soft">{{ i18n.t('hr.review.noFlags') }}</span>
                            }
                          </div>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
            }
          }
        </div>
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class HrAttendanceReviewComponent implements OnInit {
  private readonly pages = inject(PayrollPagesApi);
  private readonly admin = inject(AdminService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);

  readonly branches = signal<AdminBranch[]>([]);
  readonly branchId = signal('');
  readonly year = signal(new Date().getFullYear());
  readonly month = signal(new Date().getMonth() + 1);
  readonly flaggedOnly = signal(true);

  /** True until the branch to show is known. */
  private readonly starting = signal(true);
  readonly error = signal<string | null>(null);

  readonly months = computed(() => monthNames(this.i18n.lang()));

  // The list's own filters (owner, 2026-11-17), applied on the server with the branch, the month and "flagged only".
  readonly employeeFilter = signal<string>('all');
  readonly flagFilter = signal<'all' | 'none' | DayFlagKind>('all');
  readonly filtered = computed(() => this.employeeFilter() !== 'all' || this.flagFilter() !== 'all');

  /**
   * The month's days a page at a time from the server (owner, 2026-11-17: "paginate in backend"). The "to check"
   * filter is read there from the same points the column shows (`rowFlags`, PeoplePageRoutes.DayFlags), so the filter
   * and the column never disagree. The month's people and its day count come in the summary.
   */
  readonly pager = createServerPager(
    (page) => this.pages.reviewPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'payroll.attendanceReview',
      filters: () => ({
        branchId: this.branchId(),
        year: this.year(),
        month: this.month(),
        flagged: this.flaggedOnly(),
        person: this.employeeFilter(),
        flag: this.flagFilter(),
      }),
      enabled: () => !!this.branchId(),
    },
  );

  /** The month's days before the filters (the server's count). */
  readonly dayCount = computed(() => this.pager.summary()?.days ?? 0);
  /** The first answer for the branch is on its way. */
  readonly loading = computed(
    () => this.starting() || (!!this.branchId() && !this.pager.loaded() && !this.pager.error()),
  );

  readonly peopleMap = computed(() => {
    const map = new Map<string, AttendanceReviewPerson>();
    for (const p of this.pager.summary()?.people ?? []) {
      map.set(p.employeeId, p);
    }
    return map;
  });

  /** The people the month lists, by name, for the employee filter. */
  readonly people = computed(() =>
    [...(this.pager.summary()?.people ?? [])].sort((a, b) => a.employeeName.localeCompare(b.employeeName)),
  );

  /** The month could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? this.message(e) : null;
  });

  clearFilters(): void {
    this.employeeFilter.set('all');
    this.flagFilter.set('all');
  }

  async ngOnInit(): Promise<void> {
    try {
      const branches = await this.admin.listBranches();
      this.branches.set(branches);
      if (this.lock.locked()) {
        this.branchId.set(this.lock.lockedBranchId()!);
      } else if (!this.branchId() && branches.length > 0) {
        this.branchId.set(this.lock.defaultBranch(branches) ?? branches[0].id);
      }
    } catch (e) {
      this.error.set(this.message(e));
    }
    // The month loads itself once a branch is chosen (the pager's filter).
    this.starting.set(false);
  }

  /** Asks again for the page on screen. */
  async load(): Promise<void> {
    if (!this.branchId()) return;
    this.error.set(null);
    await this.pager.reload();
  }

  // A change goes back to page one and asks the server again (the pager's own effect).
  setBranch(value: string): void {
    if (value === this.branchId()) return;
    this.branchId.set(value);
  }

  setPeriod(year: number, month: number): void {
    if (!Number.isFinite(year) || !Number.isFinite(month)) return;
    this.year.set(year);
    this.month.set(Math.min(Math.max(month, 1), 12));
  }

  setFlaggedOnly(flagged: boolean): void {
    this.flaggedOnly.set(!!flagged);
  }

  personOf(employeeId: string): AttendanceReviewPerson | undefined {
    return this.peopleMap().get(employeeId);
  }

  weekday(date: string): string {
    if (!date) return '';
    const [year, month, day] = date.split('-').map(Number);
    const at = new Date(Date.UTC(year, month - 1, day));
    try {
      return new Intl.DateTimeFormat(this.i18n.lang(), { weekday: 'short', timeZone: 'UTC' }).format(at);
    } catch {
      return new Intl.DateTimeFormat('fr', { weekday: 'short', timeZone: 'UTC' }).format(at);
    }
  }

  branchName(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  formatHours(hours: number): string {
    if (hours === null || hours === undefined) return '—';
    return Number(hours).toFixed(2);
  }

  rowFlags(day: AttendanceReviewDay): DayFlag[] {
    const flags: DayFlag[] = [];
    const add = (kind: DayFlagKind, text: string) => flags.push({ kind, text, tone: toneOf('attendance', kind) });
    if (day.lateMinutes > 0) add('late', this.i18n.t('hr.review.flagLate', { n: day.lateMinutes }));
    if (day.earlyLeaveMinutes > 0) add('earlyLeave', this.i18n.t('hr.review.flagEarlyLeave', { n: day.earlyLeaveMinutes }));
    if (day.missingCheckOut) add('missingOut', this.i18n.t('hr.review.flagMissingOut'));
    if (day.possibleOvertimeMinutes > 0) add('possibleOvertime', this.i18n.t('hr.review.flagOvertime', { n: day.possibleOvertimeMinutes }));
    if (day.workedOnRestDay) add('restDayWork', this.i18n.t('hr.review.flagRestDayWork'));
    return flags;
  }

  private message(error: unknown): string {
    const problem = hrProblem(error);
    if (!problem) return moneyErrorMessage(error, (key, params) => this.i18n.t(key, params));
    const key = `hr.problem.${problem.code}`;
    const text = this.i18n.t(key, {
      key: problem.key ?? '',
      run: problem.runNumber ?? '',
      status: problem.runStatus ? this.i18n.t(`hr.run.status.${problem.runStatus}`) : '',
    });
    return text === key ? problem.message || key : text;
  }
}
