import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCashBox, AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { isolate } from '../../../core/bidi';
import { RegionalService } from '../../../core/regional.service';
import { formatDay, localizedName } from '../backoffice-format';
import {
  AdvanceStatus,
  EmployeePick,
  GrantAdvanceRequest,
  PayoutMethod,
  WageAdvance,
  WageMoneyApi,
  moneyErrorMessage,
} from './wage-money-api.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { PayrollPagesApi } from './payroll-pages.api';

/**
 * Cash drawn against wages before payday.
 *
 * <p>Site labour asks for money in the middle of the month and it leaves the box whether this
 * screen exists or not. Without it the Z-report is short with no name against it and the next
 * payslip has nothing to deduct; with it the money leaves under a numbered document, the box is
 * credited, and the next run recovers what it can.</p>
 *
 * <p>Two things this screen has to say out loud, because both look like bugs otherwise:</p>
 * <ul>
 *   <li><b>A short box warns, it never refuses.</b> The manager is standing at the counter with
 *   the money already counted. The warning arrives beside the document that was written, not
 *   instead of it, and it is shown that way.</li>
 *   <li><b>Recovery is the run's, not this screen's.</b> What is outstanding here is what the
 *   next approved run for that branch will take off the net — up to the net, with the remainder
 *   staying open. Nothing on this screen deducts anything.</li>
 * </ul>
 *
 * <p>Writing an advance off is deliberately absent rather than disabled: docs/07 §5.8 leaves the
 * route unbuilt until the accountant names the account that absorbs the loss, and a button that
 * cannot be honoured is worse than a sentence that explains why. The status itself is rendered
 * wherever it arrives, so an advance written off elsewhere still reads correctly here.</p>
 */
@Component({
  selector: 'app-payroll-advances',
  standalone: true,
  imports: [FormsModule, DateInputComponent, ListFooterComponent],
  template: `
    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }
      @if (warning()) {
        <p class="notice warn" role="status">
          <strong>{{ i18n.t('hr.money.warning') }}</strong> {{ warning() }}
        </p>
      }
      @if (done()) {
        <p class="notice ok" role="status">{{ done() }}</p>
      }

      <!-- ---------- what is standing ---------- -->
      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('hr.adv.title') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('hr.adv.hint') }}</p>
        </div>
        <div class="bo-panel__body">
          <div class="bo-metrics">
            <div class="bo-metric bo-metric--lead">
              <span class="bo-metric__label">{{ i18n.t('hr.adv.outstandingTotal') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(outstandingTotal()) }}</strong>
              <span class="bo-metric__note">{{ i18n.t('hr.adv.nextRunRecoversNote') }}</span>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('hr.adv.peopleOwing') }}</span>
              <strong class="bo-metric__value mono">{{ peopleOwing() }}</strong>
              <span class="bo-metric__note">{{ i18n.t('hr.adv.peopleOwingNote') }}</span>
            </div>
          </div>

          @if (owingPager.total()) {
            <div class="bo-scroll">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('hr.adv.employee') }}</th>
                    <th>{{ i18n.t('hr.money.branch') }}</th>
                    <th class="num">{{ i18n.t('hr.adv.openCount') }}</th>
                    <th class="num">{{ i18n.t('hr.adv.outstanding') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of owingPager.rows(); track row.employeeId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('hr.adv.employee')">
                        <span class="mono" dir="ltr">{{ row.employeeNumber }}</span> {{ row.employeeName }}
                      </td>
                      <td [attr.data-label]="i18n.t('hr.money.branch')">{{ row.branchName }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('hr.adv.openCount')">{{ row.count }}</td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('hr.adv.outstanding')">
                        {{ i18n.money(row.outstanding) }}
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <app-list-footer [pager]="owingPager" [loading]="!owingPager.loaded()" />
          }
        </div>
      </section>

      <!-- ---------- granting one ---------- -->
      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('hr.adv.grant') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('hr.adv.grantHint') }}</p>
        </div>
        <div class="bo-panel__body bo-form">
          <div class="bo-fields bo-fields--roomy">
            <!-- The branch first (owner, 2026-11-09): its employees, and only its cash boxes. -->
            @if (lock.locked()) {
              <p class="bo-scope-note bo-field--wide">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
            } @else {
              <div class="bo-field bo-field--wide">
                <label for="adv-grant-branch">
                  {{ i18n.t('hr.money.branch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <select id="adv-grant-branch" [ngModel]="grantBranchId()" (ngModelChange)="setGrantBranch($event)">
                  <option value="">{{ i18n.t('hr.adv.pickBranch') }}</option>
                  @for (b of branches(); track b.id) {
                    <option [value]="b.id">{{ branchName(b) }}</option>
                  }
                </select>
              </div>
            }
            <div class="bo-field bo-field--wide">
              <label for="adv-search">{{ i18n.t('hr.adv.searchEmployee') }}</label>
              <input
                id="adv-search"
                type="search"
                [ngModel]="employeeQuery()"
                (ngModelChange)="searchEmployees($event)"
                [disabled]="!grantBranchId()"
                [attr.placeholder]="i18n.t('hr.adv.searchEmployeeHint')"
              />
            </div>
            <div class="bo-field bo-field--wide">
              <label for="adv-employee">{{ i18n.t('hr.adv.employee') }}</label>
              <select id="adv-employee" [ngModel]="form.employeeId" (ngModelChange)="pickEmployee($event)" [disabled]="!grantBranchId()">
                <option value="">{{ i18n.t(grantBranchId() ? 'hr.adv.pickEmployee' : 'hr.adv.pickBranchFirst') }}</option>
                @for (e of employees(); track e.id) {
                  <option [value]="e.id">{{ isolate(e.number) }} — {{ e.fullName }}</option>
                }
              </select>
              @if (chosen(); as e) {
                <p class="bo-hint">
                  {{ i18n.t('hr.adv.employeeMethod', { method: i18n.t('hr.money.method.' + e.payoutMethod) }) }}
                  @if (e.openAdvances > 0) {
                    — {{ i18n.t('hr.adv.alreadyOpen', { count: e.openAdvances }) }}
                  }
                </p>
              }
            </div>
            <div class="bo-field">
              <label for="adv-amount">{{ i18n.t('hr.adv.amount') }}</label>
              <input
                id="adv-amount"
                type="number"
                min="0"
                [step]="regional.moneyStep()"
                class="num mono"
                [(ngModel)]="form.amount"
              />
            </div>
            <div class="bo-field">
              <label for="adv-method">{{ i18n.t('hr.money.method') }}</label>
              <select id="adv-method" [ngModel]="form.payoutMethod" (ngModelChange)="setMethod($event)">
                <option value="Cash">{{ i18n.t('hr.money.method.Cash') }}</option>
                <option value="Bank">{{ i18n.t('hr.money.method.Bank') }}</option>
              </select>
            </div>
            @if (form.payoutMethod === 'Cash') {
              <div class="bo-field" [class.is-invalid]="boxMissing()">
                <label for="adv-box">{{ i18n.t('hr.money.cashBox') }}</label>
                <select id="adv-box" [(ngModel)]="form.cashBoxId" [disabled]="!grantBranchId()">
                  <option value="">{{ i18n.t('hr.money.pickBox') }}</option>
                  @for (b of branchBoxes(); track b.id) {
                    <option [value]="b.id">{{ boxName(b) }}</option>
                  }
                </select>
                @if (boxMissing()) {
                  <span class="bo-error" role="alert">{{ i18n.t('hr.money.boxRequired') }}</span>
                }
                @if (chosenBox(); as b) {
                  <p class="bo-hint">
                    {{ i18n.t('hr.money.boxBalance', { amount: i18n.money(b.balance) }) }}
                    @if (interBranch()) {
                      — {{ i18n.t('hr.adv.interBranchHint') }}
                    }
                  </p>
                }
              </div>
            } @else {
              <div class="bo-field">
                <p class="bo-hint">{{ i18n.t('hr.money.bankNoBox') }}</p>
              </div>
            }
            <div class="bo-field">
              <label for="adv-ref">{{ i18n.t('hr.money.reference') }}</label>
              <input id="adv-ref" type="text" maxlength="60" [(ngModel)]="form.reference" />
            </div>
            <div class="bo-field bo-field--wide">
              <label for="adv-note">{{ i18n.t('hr.money.note') }}</label>
              <input id="adv-note" type="text" maxlength="200" [(ngModel)]="form.note" />
            </div>
          </div>

          @if (shortBox()) {
            <p class="notice warn" role="status">{{ i18n.t('hr.money.boxShortAhead') }}</p>
          }
        </div>
        <div class="bo-panel__foot bo-actions">
          <button type="button" class="btn-primary" [disabled]="busy() || !canGrant()" (click)="grant()">
            {{ i18n.t('hr.adv.grantAction') }}
          </button>
        </div>
      </section>

      <!-- ---------- the register ---------- -->
      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('hr.adv.list') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('hr.adv.listHint') }}</p>
        </div>
        <div class="bo-panel__body">
          <div class="bo-fields">
            @if (lock.locked()) {
              <p class="bo-scope-note" id="adv-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
            } @else {
              <div class="bo-field">
                <label for="adv-branch">{{ i18n.t('hr.money.branch') }}</label>
                <select id="adv-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
                  <option value="">{{ i18n.t('hr.money.branch.all') }}</option>
                  @for (b of branches(); track b.id) {
                    <option [value]="b.id">{{ branchName(b) }}</option>
                  }
                </select>
              </div>
            }
            <div class="bo-field">
              <label for="adv-status">{{ i18n.t('hr.adv.filterStatus') }}</label>
              <select id="adv-status" [ngModel]="status()" (ngModelChange)="setStatus($event)">
                <option value="">{{ i18n.t('hr.adv.status.all') }}</option>
                <option value="Open">{{ i18n.t('hr.adv.status.Open') }}</option>
                <option value="Recovered">{{ i18n.t('hr.adv.status.Recovered') }}</option>
                <option value="WrittenOff">{{ i18n.t('hr.adv.status.WrittenOff') }}</option>
              </select>
            </div>
            <!-- Every filter here runs on the server over all advances (owner, 2026-11-17: "paginate in backend"). -->
            <div class="bo-field">
              <label for="adv-f-method">{{ i18n.t('hr.money.method') }}</label>
              <select id="adv-f-method" [ngModel]="methodFilter()" (ngModelChange)="methodFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="Cash">{{ i18n.t('hr.money.method.Cash') }}</option>
                <option ngValue="Bank">{{ i18n.t('hr.money.method.Bank') }}</option>
              </select>
            </div>
            <div class="bo-field">
              <label for="adv-f-inter">{{ i18n.t('hr.adv.interBranch') }}</label>
              <select id="adv-f-inter" [ngModel]="interFilter()" (ngModelChange)="interFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
                <option ngValue="no">{{ i18n.t('filters.no') }}</option>
              </select>
            </div>
            <div class="bo-field">
              <label for="adv-f-from">{{ i18n.t('filters.from') }}</label>
              <app-date-input id="adv-f-from" [ngModel]="fromDay()" (ngModelChange)="fromDay.set($event || '')" />
            </div>
            <div class="bo-field">
              <label for="adv-f-to">{{ i18n.t('filters.to') }}</label>
              <app-date-input id="adv-f-to" [ngModel]="toDay()" (ngModelChange)="toDay.set($event || '')" />
            </div>
          </div>
          @if (filtered()) {
            <div class="bo-actions">
              <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
            </div>
          }

          <div class="bo-scroll bo-scroll--tall">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('hr.adv.number') }}</th>
                  <th>{{ i18n.t('hr.adv.employee') }}</th>
                  <th>{{ i18n.t('hr.money.branch') }}</th>
                  <th>{{ i18n.t('hr.adv.grantedOn') }}</th>
                  <th>{{ i18n.t('hr.money.method') }}</th>
                  <th class="num">{{ i18n.t('hr.adv.amount') }}</th>
                  <th class="num">{{ i18n.t('hr.adv.recovered') }}</th>
                  <th class="num">{{ i18n.t('hr.adv.outstanding') }}</th>
                  <th>{{ i18n.t('hr.adv.state') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (a of pager.rows(); track a.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('hr.adv.number')">{{ a.number }}</td>
                    <td [attr.data-label]="i18n.t('hr.adv.employee')">
                      <span class="mono" dir="ltr">{{ a.employeeNumber }}</span> {{ a.employeeName }}
                    </td>
                    <td [attr.data-label]="i18n.t('hr.money.branch')">
                      {{ a.branchName }}
                      @if (a.isInterBranch) {
                        <span class="bo-state bo-state--warn">{{ i18n.t('hr.adv.interBranch') }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('hr.adv.grantedOn')">{{ day(a.grantedAtUtc) }}</td>
                    <td [attr.data-label]="i18n.t('hr.money.method')">
                      {{ i18n.t('hr.money.method.' + a.payoutMethod) }}
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.adv.amount')">{{ i18n.money(a.amount) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.adv.recovered')">
                      {{ i18n.money(a.recovered) }}
                      @if (a.recoveredInRunNumber) {
                        <span class="bo-meta mono">{{ a.recoveredInRunNumber }}</span>
                      }
                    </td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('hr.adv.outstanding')">
                      {{ i18n.money(a.outstanding) }}
                    </td>
                    <td [attr.data-label]="i18n.t('hr.adv.state')">
                      <span class="bo-state" [class]="stateClass(a)">{{ i18n.t('hr.adv.status.' + a.status) }}</span>
                    </td>
                  </tr>
                }
                @if (!pager.total()) {
                  @if (!pager.loaded() || !filtered()) {
                    <tr>
                      <td class="bo-empty" colspan="9">
                        {{ !pager.loaded() ? i18n.t('common.loading') : i18n.t('hr.adv.none') }}
                      </td>
                    </tr>
                  } @else {
                    <tr>
                      <td class="bo-empty" colspan="9">
                        <strong>{{ i18n.t('ui.state.noResults') }}</strong>
                        <button type="button" class="btn-ghost bo-small" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                      </td>
                    </tr>
                  }
                }
              </tbody>
            </table>
          </div>
          <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
        </div>
      </section>

      <!-- ---------- the one act this screen cannot do yet, said rather than greyed out ---------- -->
      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('hr.adv.writeOff.title') }}</h2>
        </div>
        <div class="bo-panel__body">
          <p class="notice warn" role="status">{{ i18n.t('hr.adv.writeOff.body') }}</p>
        </div>
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class PayrollAdvancesComponent implements OnInit {
  readonly i18n = inject(I18nService);
  /** An employee number in an option's text, kept one left-to-right run beside Arabic words (core/bidi.ts). */
  readonly isolate = isolate;
  readonly lock = inject(BranchLock);
  readonly regional = inject(RegionalService);
  private readonly money = inject(WageMoneyApi);
  private readonly pages = inject(PayrollPagesApi);
  private readonly admin = inject(AdminService);
  private readonly confirm = inject(ConfirmService);

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly warning = signal<string | null>(null);
  readonly done = signal<string | null>(null);

  readonly branches = signal<AdminBranch[]>([]);
  readonly boxes = signal<AdminCashBox[]>([]);
  readonly employees = signal<EmployeePick[]>([]);
  /** The lists ask the server once the branch a branch-held user is confined to is known. */
  private readonly ready = signal(false);

  readonly branchId = signal('');
  readonly status = signal<AdvanceStatus | ''>('');
  readonly employeeQuery = signal('');
  /** The branch the advance is granted at: its employees are offered, and only its cash boxes. */
  readonly grantBranchId = signal('');
  readonly branchBoxes = computed(() => this.boxes().filter((b) => b.branchId === this.grantBranchId()));

  form: GrantAdvanceRequest & { reference: string; note: string } = this.blank();

  /** Debounces the employee search: one keystroke is not one request to a table of 900. */
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  readonly chosen = computed(() => this.employees().find((e) => e.id === this.form.employeeId) ?? null);
  readonly chosenBox = computed(() => this.boxes().find((b) => b.id === this.form.cashBoxId) ?? null);

  /**
   * Who owes, a page at a time from the server (owner, 2026-11-17: "paginate in backend"): the open advances among
   * those the branch and state ask for, one row per person, largest first. The two tiles are the server's sum over
   * all of them, never over the page. Only `Open` advances are what the next run will take; recovered ones are history.
   */
  readonly owingPager = createServerPager(
    (page) => this.pages.owingPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'payroll.advances.owing',
      filters: () => ({ branchId: this.branchId() || null, status: this.status() || null }),
      enabled: () => this.ready(),
    },
  );
  readonly outstandingTotal = computed(() => this.owingPager.summary()?.outstandingTotal ?? 0);
  readonly peopleOwing = computed(() => this.owingPager.summary()?.peopleOwing ?? 0);

  // The register's own filters (owner, 2026-11-17), applied on the server with the branch and the state.
  readonly methodFilter = signal<'all' | PayoutMethod>('all');
  readonly interFilter = signal<'all' | 'yes' | 'no'>('all');
  readonly fromDay = signal('');
  readonly toDay = signal('');

  readonly filtered = computed(
    () => this.methodFilter() !== 'all' || this.interFilter() !== 'all' || !!this.fromDay() || !!this.toDay(),
  );

  /** The register, a page at a time; a day is the advance's day in the company's zone, decided by the server. */
  readonly pager = createServerPager(
    (page) => this.pages.advancesPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'payroll.advances',
      filters: () => ({
        branchId: this.branchId() || null,
        status: this.status() || null,
        method: this.methodFilter(),
        interBranch: this.interFilter(),
        from: this.fromDay(),
        to: this.toDay(),
      }),
      enabled: () => this.ready(),
    },
  );

  /** A list the server could not read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error() ?? this.owingPager.error();
    return e ? moneyErrorMessage(e, (key, params) => this.i18n.t(key, params)) : null;
  });

  clearFilters(): void {
    this.methodFilter.set('all');
    this.interFilter.set('all');
    this.fromDay.set('');
    this.toDay.set('');
  }

  /** The box is about to go short. Said before the click, and it still does not block it. */
  readonly shortBox = computed(() => {
    const box = this.chosenBox();
    return this.form.payoutMethod === 'Cash' && !!box && this.amount() > box.balance;
  });

  /** Cash from another branch's box is a real movement between two branches, and it is marked. */
  readonly interBranch = computed(() => {
    const box = this.chosenBox();
    const employee = this.chosen();
    return !!box && !!employee && box.branchId !== employee.branchId;
  });

  async ngOnInit(): Promise<void> {
    await this.run(async () => {
      const [branches, boxes] = await Promise.all([this.admin.listBranches(), this.admin.listCashBoxes()]);
      const active = branches.filter((b) => b.isActive);
      this.branches.set(active);
      this.branchId.set(this.lock.lockedBranchId() ?? '');
      this.boxes.set(this.lock.scopeRows(boxes));
      // A branch-held user grants at their own branch; a company with one branch needs no choice.
      this.grantBranchId.set(this.lock.lockedBranchId() ?? (active.length === 1 ? active[0].id : ''));
    });
    // The two lists load themselves from here on, and again whenever a filter changes.
    this.ready.set(true);
    await this.loadEmployees();
  }

  amount(): number {
    return Math.max(0, Number(this.form.amount) || 0);
  }

  boxMissing(): boolean {
    return this.form.payoutMethod === 'Cash' && !this.form.cashBoxId && this.amount() > 0;
  }

  canGrant(): boolean {
    if (!this.grantBranchId() || !this.form.employeeId || this.amount() <= 0) return false;
    return this.form.payoutMethod !== 'Cash' || !!this.form.cashBoxId;
  }

  day(value: string | null | undefined): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  branchName(b: AdminBranch): string {
    return localizedName(b, this.i18n.lang());
  }

  boxName(b: AdminCashBox): string {
    return localizedName(b, this.i18n.lang());
  }

  stateClass(a: WageAdvance): string {
    if (a.status === 'Open') return 'bo-state--warn';
    if (a.status === 'Recovered') return 'bo-state--ok';
    return 'bo-state--danger';
  }

  setMethod(method: PayoutMethod): void {
    this.form.payoutMethod = method;
    // A bank transfer leaves no drawer, so a box chosen a moment ago would travel with the
    // request and mean nothing. Clearing it keeps the form honest about what it will send.
    if (method === 'Bank') this.form.cashBoxId = '';
    else this.preselectBox();
  }

  pickEmployee(id: string): void {
    this.form.employeeId = id;
    this.preselectBox();
  }

  /** Another branch: its employees and its boxes; what was picked at the other one is cleared. */
  async setGrantBranch(id: string): Promise<void> {
    this.grantBranchId.set(id);
    this.form.employeeId = '';
    this.form.cashBoxId = '';
    await this.loadEmployees();
  }

  searchEmployees(text: string): void {
    this.employeeQuery.set(text);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => void this.loadEmployees(), 250);
  }

  /** Another branch or state: both lists ask the server again, from page one. */
  setBranch(id: string): void {
    this.branchId.set(id);
  }

  setStatus(status: AdvanceStatus | ''): void {
    this.status.set(status);
  }

  async grant(): Promise<void> {
    if (!this.canGrant()) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('hr.adv.grantAction'))))) return;
    this.warning.set(null);
    this.done.set(null);
    await this.run(async () => {
      const result = await this.money.grantAdvance({
        employeeId: this.form.employeeId,
        amount: this.amount(),
        payoutMethod: this.form.payoutMethod,
        cashBoxId: this.form.payoutMethod === 'Cash' ? this.form.cashBoxId || null : null,
        reference: this.form.reference.trim() || null,
        note: this.form.note.trim() || null,
      });
      // The document exists either way; a short box is a fact recorded beside it. The server
      // writes these as sentences rather than codes, so they are shown as they came — see the
      // note left for the API stage about giving them keys.
      this.warning.set(result.warning ?? null);
      this.done.set(this.i18n.t('hr.adv.granted', { number: result.advance.number }));
      this.form = this.blank();
      // The branch stays chosen: the next advance is usually the same site's.
      await Promise.all([this.pager.reload(), this.owingPager.reload(), this.refreshBoxes(), this.loadEmployees()]);
    });
  }

  private async loadEmployees(): Promise<void> {
    const branchId = this.grantBranchId();
    if (!branchId) {
      this.employees.set([]);
      return;
    }
    await this.run(async () => {
      const page = await this.money.employees({ branchId, active: true, q: this.employeeQuery().trim() || null, take: 200 });
      // An answer for a branch no longer chosen is dropped.
      if (this.grantBranchId() === branchId) this.employees.set(page.rows);
    });
  }

  private async refreshBoxes(): Promise<void> {
    await this.run(async () => this.boxes.set(this.lock.scopeRows(await this.admin.listCashBoxes())));
  }

  /** The branch's own box, because that is where site labour is actually paid from. */
  private preselectBox(): void {
    if (this.form.payoutMethod !== 'Cash') return;
    if (!this.chosen()) return;
    const mine = this.branchBoxes();
    const pick = mine.find((b) => !b.isMain) ?? mine[0];
    if (pick) this.form.cashBoxId = pick.id;
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    try {
      await action();
      this.error.set(null);
    } catch (e) {
      this.error.set(moneyErrorMessage(e, (key, params) => this.i18n.t(key, params)));
    } finally {
      this.busy.set(false);
    }
  }

  private blank(): GrantAdvanceRequest & { reference: string; note: string } {
    return { employeeId: '', amount: 0, payoutMethod: 'Cash', cashBoxId: '', reference: '', note: '' };
  }
}
