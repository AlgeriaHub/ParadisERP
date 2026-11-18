import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminService } from '../../../core/admin.service';
import { listenForScans } from '../../../core/barcode-scan';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { isolate } from '../../../core/bidi';
import { RegionalService } from '../../../core/regional.service';
import { localTime, zonedTimeToUtcIso } from '../../../core/rentals';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { localizedName } from '../backoffice-format';
import { ConfirmService } from '../../../ui/confirm.service';
import { ClockKind, ClockPerson, Clocked, HrApi, hrProblem } from './hr-api.service';
import { ClockRow, PayrollPagesApi } from './payroll-pages.api';
import { moneyErrorMessage } from './wage-money-api.service';

/**
 * The clock: a badge, a number, a direction, and a punch that is never lost.
 *
 * <p>This screen sits on the counter by the door, so it is a box and two words rather than a
 * form. A badge read by the scanner lands in it through the document-wide scan catch; a number
 * typed by hand and followed by Enter does exactly the same thing.</p>
 *
 * <p>Every punch is confirmed first (owner, 2026-10-10), showing the name, the direction in words
 * and the time.</p>
 *
 * <p>The time is "now" by default: the request carries none and the server stamps the instant. A
 * set time (owner, 2026-10-22: "set the time in and the time out, or take now from the server —
 * keep both") is a day and an hour on the company's clock, sent as the instant it names; it can be
 * earlier than now, never later. Either way the time is read and shown in the company's zone
 * (owner: "Algeria time, not server time") — never the host's zone, never the browser's.</p>
 *
 * <p>An odd punch is <b>recorded and said</b>, never refused. A second clock-in with no clock-out
 * between them is a fact about somebody's day that the foreman has to see; a screen that rejected
 * it would leave the day with nothing recorded at all and the worker arguing from memory. The
 * warning is shown beside the punch that was stored, with the person's name on it.</p>
 */
@Component({
  selector: 'app-hr-clock',
  standalone: true,
  imports: [FormsModule, DateInputComponent, ListFooterComponent],
  template: `
    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <p class="bo-panel__note">{{ i18n.t('hr.clock.hint') }}</p>
        </div>

        <div class="bo-panel__body bo-form">
          <div class="bo-fields bo-fields--roomy">
            <div class="bo-field bo-field--wide">
              <label id="ck-kind-label">{{ i18n.t('hr.clock.direction') }}</label>
              <nav class="bo-chips" [attr.aria-labelledby]="'ck-kind-label'">
                <button
                  type="button"
                  [class.is-on]="kind() === 'In'"
                  [attr.aria-pressed]="kind() === 'In'"
                  (click)="kind.set('In')"
                >
                  {{ i18n.t('hr.clock.in') }}
                </button>
                <button
                  type="button"
                  [class.is-on]="kind() === 'Out'"
                  [attr.aria-pressed]="kind() === 'Out'"
                  (click)="kind.set('Out')"
                >
                  {{ i18n.t('hr.clock.out') }}
                </button>
              </nav>
              <p class="bo-hint">{{ i18n.t('hr.clock.directionHint') }}</p>
            </div>
            <div class="bo-field bo-field--wide">
              <label id="ck-when-label">{{ i18n.t('hr.clock.when') }}</label>
              <nav class="bo-chips" [attr.aria-labelledby]="'ck-when-label'">
                <button
                  id="ck-when-now"
                  type="button"
                  [class.is-on]="when() === 'now'"
                  [attr.aria-pressed]="when() === 'now'"
                  (click)="when.set('now')"
                >
                  {{ i18n.t('hr.clock.whenNow') }}
                </button>
                <button
                  id="ck-when-set"
                  type="button"
                  [class.is-on]="when() === 'set'"
                  [attr.aria-pressed]="when() === 'set'"
                  (click)="setTime()"
                >
                  {{ i18n.t('hr.clock.whenSet') }}
                </button>
              </nav>
              <p class="bo-hint" id="ck-when-hint">
                {{ when() === 'now'
                  ? i18n.t('hr.clock.whenNowHint', { zone: isolate(zone()) })
                  : i18n.t('hr.clock.whenSetHint', { zone: isolate(zone()) }) }}
              </p>
            </div>
            @if (when() === 'set') {
              <div class="bo-field">
                <label for="ck-day">{{ i18n.t('hr.clock.day') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <app-date-input id="ck-day" [ngModel]="day()" (ngModelChange)="day.set($event ?? '')"
                  [ariaInvalid]="!!timeProblem()" ariaDescribedby="ck-time-problem" />
              </div>
              <div class="bo-field">
                <label for="ck-time">{{ i18n.t('hr.clock.time') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <input id="ck-time" type="time" class="mono" [ngModel]="time()" (ngModelChange)="time.set($event ?? '')"
                  [attr.aria-invalid]="timeProblem() ? 'true' : null" aria-describedby="ck-time-problem" />
              </div>
              @if (timeProblem(); as problem) {
                <p class="bo-error bo-field--wide" id="ck-time-problem" role="alert">{{ problem }}</p>
              }
            }
            <div class="bo-field bo-field--wide">
              <label for="ck-number">{{ i18n.t('hr.clock.number') }}</label>
              <input
                id="ck-number"
                class="mono badge-number"
                autocomplete="off"
                [ngModel]="number()"
                (ngModelChange)="number.set($event)"
                (keydown.enter)="punch(number())"
              />
              <p class="bo-hint">{{ i18n.t('hr.clock.numberHint') }}</p>
            </div>
          </div>
        </div>

        <div class="bo-panel__foot">
          <button type="button" class="btn-primary" [disabled]="busy() || !number().trim() || !!timeProblem()" (click)="punch(number())">
            {{ i18n.t('hr.clock.record') }}
          </button>
        </div>
      </section>

      @if (last(); as done) {
        <section class="bo-panel">
          <div class="bo-panel__body">
            <p class="notice ok" role="status">
              {{
                i18n.t('hr.clock.recorded', {
                  name: done.person.employeeName,
                  kind: i18n.t('hr.clock.' + (done.event.kind === 'In' ? 'in' : 'out')),
                  at: i18n.dateTime(done.event.atUtc),
                })
              }}
            </p>
            @if (done.warning) {
              <!-- Beside the punch, never in place of it. -->
              <p class="notice warn" role="status">{{ i18n.t('hr.problem.' + done.warning) }}</p>
            }
          </div>
        </section>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('hr.clock.today') }}</h2>
          <div class="bo-actions bo-actions--start">
            @if (lock.locked()) {
              <p class="bo-scope-note" id="ck-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
            } @else {
              <div class="bo-field">
                <label for="ck-branch">{{ i18n.t('hr.employees.branch') }}</label>
                <select id="ck-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
                  @for (branch of branches(); track branch.id) {
                    <option [value]="branch.id">{{ branchName(branch) }}</option>
                  }
                </select>
              </div>
            }
            @if (eventCount() || filtered()) {
              <!-- Every filter here runs on the server over the month's punches (owner, 2026-11-17: "paginate in backend"). -->
              <div class="bo-field">
                <label for="ck-f-kind">{{ i18n.t('hr.clock.direction') }}</label>
                <select id="ck-f-kind" [ngModel]="kindFilter()" (ngModelChange)="kindFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="In">{{ i18n.t('hr.clock.in') }}</option>
                  <option ngValue="Out">{{ i18n.t('hr.clock.out') }}</option>
                </select>
              </div>
              <div class="bo-field">
                <label for="ck-f-state">{{ i18n.t('common.state') }}</label>
                <select id="ck-f-state" [ngModel]="pairFilter()" (ngModelChange)="pairFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="paired">{{ i18n.t('hr.clock.paired') }}</option>
                  <option ngValue="unpaired">{{ i18n.t('hr.clock.noPartner') }}</option>
                </select>
              </div>
              <div class="bo-field">
                <label for="ck-f-from">{{ i18n.t('filters.from') }}</label>
                <app-date-input id="ck-f-from" [ngModel]="fromDay()" (ngModelChange)="fromDay.set($event || '')" />
              </div>
              <div class="bo-field">
                <label for="ck-f-to">{{ i18n.t('filters.to') }}</label>
                <app-date-input id="ck-f-to" [ngModel]="toDay()" (ngModelChange)="toDay.set($event || '')" />
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
          } @else if (unpairedCount() > 0) {
            <p class="notice warn" role="status">{{ i18n.t('hr.clock.unpaired', { count: unpairedCount() }) }}</p>
          }

          @if (eventCount() === 0) {
            <p class="bo-empty">{{ i18n.t('hr.clock.none') }}</p>
          } @else if (pager.total() === 0) {
            <p class="bo-empty">
              <strong>{{ i18n.t('ui.state.noResults') }}</strong>
              <button type="button" class="btn-ghost bo-small" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
            </p>
          } @else {
            <div class="bo-scroll">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('hr.employees.name') }}</th>
                    <th>{{ i18n.t('hr.clock.direction') }}</th>
                    <th>{{ i18n.t('hr.clock.at') }}</th>
                    <th>{{ i18n.t('common.state') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (event of pager.rows(); track event.id) {
                    <tr>
                      <td [attr.data-label]="i18n.t('hr.employees.name')">
                        <span class="bo-cell-strong">{{ personName(event) }}</span>
                      </td>
                      <td [attr.data-label]="i18n.t('hr.clock.direction')">
                        {{ i18n.t('hr.clock.' + (event.kind === 'In' ? 'in' : 'out')) }}
                      </td>
                      <td class="mono" [attr.data-label]="i18n.t('hr.clock.at')">{{ i18n.dateTime(event.atUtc) }}</td>
                      <td [attr.data-label]="i18n.t('common.state')">
                        <span class="bo-state" [class.bo-state--warn]="isUnpaired(event)">
                          {{ isUnpaired(event) ? i18n.t('hr.clock.noPartner') : i18n.t('hr.clock.paired') }}
                        </span>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
          }
        </div>
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      /* A badge number read across a counter, not a form field. Not ".badge": that is the global
         status label, and its pill corners and small type took this input over. */
      .badge-number {
        font-size: 1.6rem;
        letter-spacing: 0.08em;
        padding-block: 0.5rem;
        background: var(--surface);
        border: var(--line) solid var(--border-strong);
        border-radius: var(--radius-field);
        color: var(--text);
      }
    `,
  ],
})
export class HrClockComponent implements OnInit {
  private readonly hr = inject(HrApi);
  private readonly pages = inject(PayrollPagesApi);
  private readonly admin = inject(AdminService);
  private readonly confirm = inject(ConfirmService);
  private readonly regional = inject(RegionalService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);

  readonly branches = signal<AdminBranch[]>([]);
  readonly branchId = signal('');
  readonly kind = signal<ClockKind>('In');
  readonly number = signal('');
  /** Now, stamped by the server; or a day and a time on the company's clock. */
  readonly when = signal<'now' | 'set'>('now');
  readonly day = signal('');
  readonly time = signal('');

  readonly last = signal<Clocked | null>(null);
  /** True until the branch to show is known. */
  private readonly starting = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  /** The company's zone (Africa/Algiers in the Algeria preset), never the browser's or the host's. */
  readonly zone = computed(() => this.regional.settings().timeZone);
  readonly isolate = isolate;

  // The log's own filters (owner, 2026-11-17), applied on the server over the month's punches.
  readonly kindFilter = signal<'all' | ClockKind>('all');
  readonly pairFilter = signal<'all' | 'paired' | 'unpaired'>('all');
  readonly fromDay = signal('');
  readonly toDay = signal('');
  readonly filtered = computed(
    () => this.kindFilter() !== 'all' || this.pairFilter() !== 'all' || !!this.fromDay() || !!this.toDay(),
  );

  /**
   * This month's punches at the branch, a page at a time from the server (owner, 2026-11-17: "paginate in backend").
   * Paired there as the run pairs them; a punch's day is its day on the company's clock. Each row carries its person
   * and whether it found a partner; the month's counts come in the summary.
   */
  readonly pager = createServerPager(
    (page) => {
      // This month on the company's calendar, read at each request: the browser's own date can be a day — and on
      // the 1st, a month — away from the one the punches are kept under.
      const [year, month] = localTime(new Date().toISOString(), this.zone()).day.split('-').map(Number);
      return this.pages.clockPage({ skip: page.skip, take: page.take, year, month, ...page.filters });
    },
    {
      key: 'payroll.clock',
      filters: () => ({
        branchId: this.branchId(),
        kind: this.kindFilter(),
        pair: this.pairFilter(),
        from: this.fromDay(),
        to: this.toDay(),
      }),
      enabled: () => !!this.branchId(),
    },
  );

  /** The month's punches before the filters, and those that never found a partner (the server's counts). */
  readonly eventCount = computed(() => this.pager.summary()?.events ?? 0);
  readonly unpairedCount = computed(() => this.pager.summary()?.unpaired ?? 0);
  /** The first answer for the branch is on its way. */
  readonly loading = computed(
    () => this.starting() || (!!this.branchId() && !this.pager.loaded() && !this.pager.error()),
  );

  /** The log could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? this.message(e) : null;
  });

  clearFilters(): void {
    this.kindFilter.set('all');
    this.pairFilter.set('all');
    this.fromDay.set('');
    this.toDay.set('');
  }

  constructor() {
    // The badge reader types into whatever has focus; the catch only picks up the keystrokes
    // that were not already landing in a box, which is exactly a scan made across the counter.
    const stop = listenForScans((code) => void this.punch(code));
    inject(DestroyRef).onDestroy(stop);
  }

  ngOnInit(): void {
    void this.start();
  }

  private async start(): Promise<void> {
    try {
      const branches = await this.admin.listBranches();
      this.branches.set(branches);
      // A non-admin's sheet is their own branch's; the admin starts on his.
      if (this.lock.locked()) this.branchId.set(this.lock.lockedBranchId()!);
      else if (!this.branchId() && branches.length > 0) this.branchId.set(this.lock.defaultBranch(branches) ?? branches[0].id);
    } catch (e) {
      this.error.set(this.message(e));
    }
    // The log loads itself once a branch is chosen (the pager's filter).
    this.starting.set(false);
  }

  /** A set time starts from the present minute, so the clerk only changes what differs. */
  setTime(): void {
    if (this.when() === 'set') return;
    const now = localTime(new Date().toISOString(), this.regional.settings().timeZone);
    this.day.set(now.day);
    this.time.set(`${String(now.hour).padStart(2, '0')}:${String(now.minute).padStart(2, '0')}`);
    this.when.set('set');
  }

  /** The instant a set time names, or null for now (the server's) or a time not yet complete. */
  private readonly statedUtc = computed(() => {
    if (this.when() !== 'set' || !/^\d{4}-\d{2}-\d{2}$/.test(this.day()) || !/^\d{2}:\d{2}/.test(this.time())) return null;
    return zonedTimeToUtcIso(this.day(), this.time().slice(0, 5), this.regional.settings().timeZone);
  });

  /** What is wrong with a set time, in words: missing, or later than now. */
  readonly timeProblem = computed(() => {
    if (this.when() !== 'set') return null;
    const at = this.statedUtc();
    if (!at) return this.i18n.t('hr.clock.timeRequired');
    return Date.parse(at) > Date.now() ? this.i18n.t('hr.clock.timeInFuture') : null;
  });

  setBranch(value: string): void {
    this.branchId.set(value ?? '');
  }

  async punch(code: string): Promise<void> {
    const employeeNumber = (code ?? '').trim();
    if (!employeeNumber || this.busy()) return;
    // A scan made while the set time is incomplete or ahead of now is not recorded at all.
    if (this.timeProblem()) {
      this.error.set(this.timeProblem());
      return;
    }
    const atUtc = this.statedUtc();
    this.busy.set(true);
    this.error.set(null);

    let person: ClockPerson;
    try {
      person = atUtc ? await this.hr.clockPerson(employeeNumber, atUtc) : await this.hr.clockPerson(employeeNumber);
    } catch (e) {
      this.last.set(null);
      this.error.set(this.message(e));
      this.busy.set(false);
      return;
    }

    const at = atUtc ? new Date(atUtc) : new Date();
    const yes = await this.confirm.ask({
      title: this.i18n.t('hr.clock.confirmTitle'),
      message: this.i18n.t('hr.clock.confirmMessage', {
        name: person.employeeName,
        number: isolate(person.employeeNumber),
        kind: this.i18n.t('hr.clock.' + (this.kind() === 'In' ? 'in' : 'out')),
        at: this.i18n.dateTime(at.toISOString()),
      }),
      confirmLabel: this.i18n.t('hr.clock.confirmOk'),
      focusConfirm: true,
    });

    if (!yes) {
      this.busy.set(false);
      document.getElementById('ck-number')?.focus();
      return;
    }

    try {
      // Now sends no time, so the server's clock stamps it; a set time sends the instant it names.
      const done = await this.hr.clock({ employeeNumber, kind: this.kind(), ...(atUtc ? { atUtc } : {}), source: 'clock-screen' });
      this.last.set(done);
      this.number.set('');
      if (this.branchId()) await this.pager.reload();
    } catch (e) {
      this.last.set(null);
      this.error.set(this.message(e));
    } finally {
      this.busy.set(false);
      document.getElementById('ck-number')?.focus();
    }
  }

  // ---------- words ----------

  branchName(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  personName(event: ClockRow): string {
    return event.employeeName ? `${event.employeeName} · ${isolate(event.employeeNumber)}` : event.employeeId;
  }

  isUnpaired(event: ClockRow): boolean {
    return event.unpaired;
  }

  private message(error: unknown): string {
    const problem = hrProblem(error);
    if (!problem) return moneyErrorMessage(error, (key, params) => this.i18n.t(key, params));
    const key = `hr.problem.${problem.code}`;
    const text = this.i18n.t(key, { key: problem.key ?? '', run: problem.runNumber ?? '' });
    return text === key ? problem.message || key : text;
  }
}
