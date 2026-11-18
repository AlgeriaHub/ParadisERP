import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCashBox, AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { formatDay, localizedName } from '../backoffice-format';
import {
  PayoutMethod,
  PayrollLiability,
  RecordStatutoryPaymentRequest,
  StatutoryPayment,
  WageMoneyApi,
  moneyErrorMessage,
} from './wage-money-api.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { PayrollPagesApi } from './payroll-pages.api';

/** The three accounts payroll withholds into, in the order the chart lists them. */
const ACCOUNTS = ['431', '442', '447'] as const;

/**
 * What payroll withheld and has not yet handed over.
 *
 * <p>Approving a run credits three liabilities — the social fund, the tax withheld at source and
 * whatever other payroll tax the company owes — and nothing else in the system ever clears them.
 * Left alone they grow every month and the balance sheet reads as debt that was in fact paid from
 * the bank in the first week. This screen is the transfer that clears them, and the statement of
 * what is standing before it does.</p>
 *
 * <p>Every figure on the left is read from the ledger lines themselves, debit against credit,
 * rather than from a column that could drift away from them. That is the whole reason the
 * outstanding figure can be trusted enough to pay against.</p>
 *
 * <p>The period is optional on purpose. A fund is routinely paid a round figure covering several
 * months, or an arrears settlement covering none cleanly; naming a month when there is one makes
 * the reconciliation easy, and being forced to invent one would make it wrong. Both halves are
 * given together or not at all.</p>
 */
@Component({
  selector: 'app-statutory-payments',
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
          <h2 class="bo-panel__title">{{ i18n.t('hr.stat.held') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('hr.stat.heldHint') }}</p>
        </div>
        <div class="bo-panel__body">
          <div class="bo-fields">
            @if (lock.locked()) {
              <p class="bo-scope-note" id="st-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
            } @else {
              <div class="bo-field">
                <label for="st-branch">{{ i18n.t('hr.money.branch') }}</label>
                <select id="st-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
                  <option value="">{{ i18n.t('hr.money.branch.all') }}</option>
                  @for (b of branches(); track b.id) {
                    <option [value]="b.id">{{ branchName(b) }}</option>
                  }
                </select>
              </div>
            }
          </div>

          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('hr.stat.account') }}</th>
                  <th class="num">{{ i18n.t('hr.stat.accrued') }}</th>
                  <th class="num">{{ i18n.t('hr.stat.paid') }}</th>
                  <th class="num">{{ i18n.t('hr.stat.outstanding') }}</th>
                  <th>{{ i18n.t('hr.stat.state') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (l of liabilities(); track l.accountCode) {
                  <tr>
                    <td [attr.data-label]="i18n.t('hr.stat.account')">
                      <span class="mono">{{ l.accountCode }}</span>
                      {{ accountLabel(l.accountCode, l.accountName) }}
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.stat.accrued')">{{ i18n.money(l.accrued) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.stat.paid')">{{ i18n.money(l.paid) }}</td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('hr.stat.outstanding')">
                      {{ i18n.money(l.outstanding) }}
                    </td>
                    <td [attr.data-label]="i18n.t('hr.stat.state')">
                      <span class="bo-state" [class]="l.outstanding > 0 ? 'bo-state--warn' : 'bo-state--ok'">
                        {{ i18n.t(l.outstanding > 0 ? 'hr.stat.state.owing' : 'hr.stat.state.clear') }}
                      </span>
                    </td>
                  </tr>
                }
                @if (!liabilities().length) {
                  <tr>
                    <td class="bo-empty" colspan="5">
                      {{ busy() ? i18n.t('common.loading') : i18n.t('hr.stat.noLiabilities') }}
                    </td>
                  </tr>
                }
              </tbody>
              <tfoot>
                <tr>
                  <td [attr.data-label]="i18n.t('hr.stat.account')">{{ i18n.t('hr.stat.total') }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('hr.stat.accrued')">{{ i18n.money(totalAccrued()) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('hr.stat.paid')">{{ i18n.money(totalPaid()) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('hr.stat.outstanding')">
                    {{ i18n.money(totalOutstanding()) }}
                  </td>
                  <td></td>
                </tr>
              </tfoot>
            </table>
          </div>
        </div>
      </section>

      <!-- ---------- the transfer that clears one ---------- -->
      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('hr.stat.record') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('hr.stat.recordHint') }}</p>
        </div>
        <div class="bo-panel__body bo-form">
          <div class="bo-fields bo-fields--roomy">
            @if (lock.locked()) {
              <div class="bo-field">
                <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
                <p class="bo-fixed" id="stp-fixed">{{ lock.ownBranchName(branches()) }}</p>
                <span class="bo-hint">{{ i18n.t('branchLock.fixedHint') }}</span>
              </div>
            } @else {
              <div class="bo-field" [class.is-invalid]="!form.branchId">
                <label for="st-pay-branch">{{ i18n.t('hr.money.branch') }}</label>
                <select id="st-pay-branch" [(ngModel)]="form.branchId">
                  <option value="">{{ i18n.t('hr.stat.pickBranch') }}</option>
                  @for (b of branches(); track b.id) {
                    <option [value]="b.id">{{ branchName(b) }}</option>
                  }
                </select>
                @if (!form.branchId) {
                  <span class="bo-error" role="alert">{{ i18n.t('hr.stat.branchRequired') }}</span>
                }
              </div>
            }
            <div class="bo-field">
              <label for="st-account">{{ i18n.t('hr.stat.account') }}</label>
              <select id="st-account" [(ngModel)]="form.accountCode">
                @for (code of accounts; track code) {
                  <option [value]="code">{{ code }} — {{ accountLabel(code, code) }}</option>
                }
              </select>
              <p class="bo-hint">
                {{ i18n.t('hr.stat.standingOn', { amount: i18n.money(outstandingOf(form.accountCode)) }) }}
              </p>
            </div>
            <div class="bo-field">
              <label for="st-amount">{{ i18n.t('hr.stat.amount') }}</label>
              <input
                id="st-amount"
                type="number"
                min="0"
                [step]="regional.moneyStep()"
                class="num mono"
                [(ngModel)]="form.amount"
              />
            </div>
            <div class="bo-field">
              <label for="st-method">{{ i18n.t('hr.money.method') }}</label>
              <select id="st-method" [ngModel]="form.payoutMethod" (ngModelChange)="setMethod($event)">
                <option value="Bank">{{ i18n.t('hr.money.method.Bank') }}</option>
                <option value="Cash">{{ i18n.t('hr.money.method.Cash') }}</option>
              </select>
            </div>
            @if (form.payoutMethod === 'Cash') {
              <div class="bo-field" [class.is-invalid]="!form.cashBoxId">
                <label for="st-box">{{ i18n.t('hr.money.cashBox') }}</label>
                <select id="st-box" [(ngModel)]="form.cashBoxId">
                  <option value="">{{ i18n.t('hr.money.pickBox') }}</option>
                  @for (b of boxes(); track b.id) {
                    <option [value]="b.id">{{ boxName(b) }}</option>
                  }
                </select>
                @if (!form.cashBoxId) {
                  <span class="bo-error" role="alert">{{ i18n.t('hr.money.boxRequired') }}</span>
                }
                @if (chosenBox(); as b) {
                  <p class="bo-hint">{{ i18n.t('hr.money.boxBalance', { amount: i18n.money(b.balance) }) }}</p>
                }
              </div>
            } @else {
              <div class="bo-field">
                <p class="bo-hint">{{ i18n.t('hr.money.bankNoBox') }}</p>
              </div>
            }
            <div class="bo-field">
              <label for="st-year">{{ i18n.t('hr.stat.periodYear') }}</label>
              <input id="st-year" type="number" min="2000" max="2100" class="num mono" [(ngModel)]="form.periodYear" />
            </div>
            <div class="bo-field">
              <label for="st-month">{{ i18n.t('hr.stat.periodMonth') }}</label>
              <select id="st-month" [(ngModel)]="form.periodMonth">
                <option [ngValue]="null">{{ i18n.t('hr.stat.noPeriod') }}</option>
                @for (m of months; track m) {
                  <option [ngValue]="m">{{ monthName(m) }}</option>
                }
              </select>
              <p class="bo-hint">{{ i18n.t('hr.stat.periodHint') }}</p>
            </div>
            <div class="bo-field">
              <label for="st-ref">{{ i18n.t('hr.money.reference') }}</label>
              <input id="st-ref" type="text" maxlength="60" [(ngModel)]="form.reference" />
            </div>
            <div class="bo-field bo-field--wide">
              <label for="st-note">{{ i18n.t('hr.money.note') }}</label>
              <input id="st-note" type="text" maxlength="200" [(ngModel)]="form.note" />
            </div>
          </div>

          @if (periodHalf()) {
            <p class="notice warn" role="status">{{ i18n.t('hr.stat.periodBothOrNeither') }}</p>
          }
          @if (overPaying()) {
            <p class="notice warn" role="status">
              {{ i18n.t('hr.stat.overPaying', { amount: i18n.money(outstandingOf(form.accountCode)) }) }}
            </p>
          }
          @if (shortBox()) {
            <p class="notice warn" role="status">{{ i18n.t('hr.money.boxShortAhead') }}</p>
          }
        </div>
        <div class="bo-panel__foot bo-actions">
          <button type="button" class="btn-primary" [disabled]="busy() || !canRecord()" (click)="record()">
            {{ i18n.t('hr.stat.recordAction') }}
          </button>
        </div>
      </section>

      <!-- ---------- what has been handed over ---------- -->
      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('hr.stat.history') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('hr.stat.historyHint') }}</p>
        </div>
        <div class="bo-panel__body">
          <div class="bo-fields">
            <div class="bo-field">
              <label for="st-filter-account">{{ i18n.t('hr.stat.account') }}</label>
              <select id="st-filter-account" [ngModel]="filterAccount()" (ngModelChange)="setFilterAccount($event)">
                <option value="">{{ i18n.t('hr.stat.account.all') }}</option>
                @for (code of accounts; track code) {
                  <option [value]="code">{{ code }} — {{ accountLabel(code, code) }}</option>
                }
              </select>
            </div>
            <div class="bo-field">
              <label for="st-filter-year">{{ i18n.t('hr.money.year') }}</label>
              <input
                id="st-filter-year"
                type="number"
                min="2000"
                max="2100"
                class="num mono"
                [ngModel]="filterYear()"
                (ngModelChange)="setFilterYear($event)"
              />
            </div>
            <!-- Every filter here runs on the server over all payments (owner, 2026-11-17: "paginate in backend"). -->
            <div class="bo-field">
              <label for="st-f-method">{{ i18n.t('hr.money.method') }}</label>
              <select id="st-f-method" [ngModel]="methodFilter()" (ngModelChange)="methodFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="Bank">{{ i18n.t('hr.money.method.Bank') }}</option>
                <option ngValue="Cash">{{ i18n.t('hr.money.method.Cash') }}</option>
              </select>
            </div>
            <div class="bo-field">
              <label for="st-f-from">{{ i18n.t('filters.from') }}</label>
              <app-date-input id="st-f-from" [ngModel]="fromDay()" (ngModelChange)="fromDay.set($event || '')" />
            </div>
            <div class="bo-field">
              <label for="st-f-to">{{ i18n.t('filters.to') }}</label>
              <app-date-input id="st-f-to" [ngModel]="toDay()" (ngModelChange)="toDay.set($event || '')" />
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
                  <th>{{ i18n.t('hr.stat.paidOn') }}</th>
                  <th>{{ i18n.t('hr.money.branch') }}</th>
                  <th>{{ i18n.t('hr.stat.account') }}</th>
                  <th>{{ i18n.t('hr.money.period') }}</th>
                  <th>{{ i18n.t('hr.money.method') }}</th>
                  <th class="num">{{ i18n.t('hr.stat.amount') }}</th>
                  <th>{{ i18n.t('hr.money.reference') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (p of pager.rows(); track p.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('hr.stat.paidOn')">{{ day(p.paidAtUtc) }}</td>
                    <td [attr.data-label]="i18n.t('hr.money.branch')">{{ p.branchName }}</td>
                    <td [attr.data-label]="i18n.t('hr.stat.account')">
                      <span class="mono">{{ p.accountCode }}</span>
                      {{ accountLabel(p.accountCode, p.accountName) }}
                    </td>
                    <td [attr.data-label]="i18n.t('hr.money.period')">{{ periodOf(p) }}</td>
                    <td [attr.data-label]="i18n.t('hr.money.method')">
                      {{ i18n.t('hr.money.method.' + p.payoutMethod) }}
                    </td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('hr.stat.amount')">
                      {{ i18n.money(p.amount) }}
                    </td>
                    <td [attr.data-label]="i18n.t('hr.money.reference')">{{ p.reference || '—' }}</td>
                  </tr>
                }
                @if (!pager.total()) {
                  @if (!pager.loaded() || !filtered()) {
                    <tr>
                      <td class="bo-empty" colspan="7">
                        {{ !pager.loaded() ? i18n.t('common.loading') : i18n.t('hr.stat.noPayments') }}
                      </td>
                    </tr>
                  } @else {
                    <tr>
                      <td class="bo-empty" colspan="7">
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
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class StatutoryPaymentsComponent implements OnInit {
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  readonly regional = inject(RegionalService);
  private readonly money = inject(WageMoneyApi);
  private readonly pages = inject(PayrollPagesApi);
  private readonly admin = inject(AdminService);
  private readonly confirm = inject(ConfirmService);

  readonly accounts = ACCOUNTS;
  readonly months = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly warning = signal<string | null>(null);
  readonly done = signal<string | null>(null);

  readonly branches = signal<AdminBranch[]>([]);
  readonly boxes = signal<AdminCashBox[]>([]);
  readonly liabilities = signal<PayrollLiability[]>([]);
  /** The history asks the server once the branch a branch-held user is confined to is known. */
  private readonly ready = signal(false);

  readonly branchId = signal('');
  readonly filterAccount = signal('');
  readonly filterYear = signal(new Date().getFullYear());

  form: RecordStatutoryPaymentRequest & { reference: string; note: string } = this.blank();

  // The history's own filters (owner, 2026-11-17), applied on the server with the branch, the account and the year.
  readonly methodFilter = signal<'all' | PayoutMethod>('all');
  readonly fromDay = signal('');
  readonly toDay = signal('');
  readonly filtered = computed(() => this.methodFilter() !== 'all' || !!this.fromDay() || !!this.toDay());

  /** The history, a page at a time; a payment's day is its day in the company's zone, decided by the server. */
  readonly pager = createServerPager(
    (page) => this.pages.statutoryPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'payroll.statutory.history',
      filters: () => ({
        branchId: this.branchId() || null,
        accountCode: this.filterAccount() || null,
        year: this.filterYear(),
        method: this.methodFilter(),
        from: this.fromDay(),
        to: this.toDay(),
      }),
      enabled: () => this.ready(),
    },
  );

  /** The history could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? moneyErrorMessage(e, (key, params) => this.i18n.t(key, params)) : null;
  });

  clearFilters(): void {
    this.methodFilter.set('all');
    this.fromDay.set('');
    this.toDay.set('');
  }

  readonly totalAccrued = computed(() => this.liabilities().reduce((sum, l) => sum + l.accrued, 0));
  readonly totalPaid = computed(() => this.liabilities().reduce((sum, l) => sum + l.paid, 0));
  readonly totalOutstanding = computed(() => this.liabilities().reduce((sum, l) => sum + l.outstanding, 0));

  readonly chosenBox = computed(() => this.boxes().find((b) => b.id === this.form.cashBoxId) ?? null);

  /** One half of a period is not a period. Said before the server says it. */
  readonly periodHalf = computed(() => {
    const hasYear = !!this.form.periodYear;
    const hasMonth = !!this.form.periodMonth;
    return hasYear !== hasMonth;
  });

  /** Paying more than the ledger says is standing. A warning — arrears really do happen. */
  readonly overPaying = computed(() => this.amount() > this.outstandingOf(this.form.accountCode));

  readonly shortBox = computed(() => {
    const box = this.chosenBox();
    return this.form.payoutMethod === 'Cash' && !!box && this.amount() > box.balance;
  });

  async ngOnInit(): Promise<void> {
    await this.attempt(async () => {
      const [branches, boxes] = await Promise.all([this.admin.listBranches(), this.admin.listCashBoxes()]);
      this.branches.set(branches.filter((b) => b.isActive));
      this.branchId.set(this.lock.lockedBranchId() ?? '');
      // Paid from the employee's branch for a non-admin; the admin starts on his own and may change it.
      this.form.branchId ||= this.lock.defaultBranch(this.branches()) ?? '';
      this.boxes.set(this.lock.scopeRows(boxes));
    });
    // The history loads itself from here on, and again whenever a filter changes.
    this.ready.set(true);
    await this.loadLiabilities();
  }

  amount(): number {
    return Math.max(0, Number(this.form.amount) || 0);
  }

  canRecord(): boolean {
    if (!this.form.branchId || this.amount() <= 0 || this.periodHalf()) return false;
    return this.form.payoutMethod !== 'Cash' || !!this.form.cashBoxId;
  }

  /**
   * The account's name in the reader's language, falling back to the chart's own French name.
   * The chart is a company's to rename, so a code it has renamed still reads as the company
   * wrote it rather than as a name this screen invented.
   */
  accountLabel(code: string, fallback: string): string {
    const key = `hr.stat.account.${code}`;
    const said = this.i18n.t(key);
    return said === key ? fallback : said;
  }

  outstandingOf(code: string): number {
    return this.liabilities().find((l) => l.accountCode === code)?.outstanding ?? 0;
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

  monthName(month: number): string {
    try {
      return new Intl.DateTimeFormat(this.i18n.lang(), { month: 'long', timeZone: 'UTC' }).format(
        new Date(Date.UTC(2000, month - 1, 1)),
      );
    } catch {
      return String(month);
    }
  }

  /** A payment with no period says so in words rather than showing an empty cell. */
  periodOf(payment: StatutoryPayment): string {
    if (!payment.periodYear || !payment.periodMonth) return this.i18n.t('hr.stat.noPeriod');
    return `${this.monthName(payment.periodMonth)} ${payment.periodYear}`;
  }

  setMethod(method: PayoutMethod): void {
    this.form.payoutMethod = method;
    // A transfer leaves no drawer, so a box chosen a moment ago would mean nothing on the wire.
    if (method === 'Bank') this.form.cashBoxId = '';
  }

  /** Another branch: its liabilities, and the history asks the server again from page one. */
  async setBranch(id: string): Promise<void> {
    this.branchId.set(id);
    this.form.branchId ||= id;
    await this.loadLiabilities();
  }

  setFilterAccount(code: string): void {
    this.filterAccount.set(code);
  }

  setFilterYear(year: number): void {
    this.filterYear.set(Number(year) || new Date().getFullYear());
  }

  async record(): Promise<void> {
    if (!this.canRecord()) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('hr.stat.recordAction'))))) return;
    this.warning.set(null);
    this.done.set(null);
    await this.attempt(async () => {
      const result = await this.money.recordStatutoryPayment({
        branchId: this.form.branchId,
        accountCode: this.form.accountCode,
        amount: this.amount(),
        payoutMethod: this.form.payoutMethod,
        cashBoxId: this.form.payoutMethod === 'Cash' ? this.form.cashBoxId || null : null,
        periodYear: this.form.periodMonth ? this.form.periodYear : null,
        periodMonth: this.form.periodMonth ?? null,
        reference: this.form.reference.trim() || null,
        note: this.form.note.trim() || null,
      });
      // Written as sentences by the server rather than as codes, so shown as they came.
      this.warning.set(result.warning ?? null);
      this.done.set(
        this.i18n.t('hr.stat.recorded', {
          code: result.payment.accountCode,
          amount: this.i18n.money(result.payment.amount),
        }),
      );
      const branch = this.form.branchId;
      this.form = this.blank();
      this.form.branchId = branch;
      await Promise.all([this.loadLiabilities(), this.pager.reload(), this.refreshBoxes()]);
    });
  }

  private async loadLiabilities(): Promise<void> {
    await this.attempt(async () => this.liabilities.set(await this.money.liabilities(this.branchId() || null)));
  }

  private async refreshBoxes(): Promise<void> {
    await this.attempt(async () => this.boxes.set(this.lock.scopeRows(await this.admin.listCashBoxes())));
  }

  private async attempt(action: () => Promise<unknown>): Promise<void> {
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

  private blank(): RecordStatutoryPaymentRequest & { reference: string; note: string } {
    return {
      branchId: '',
      accountCode: ACCOUNTS[0],
      amount: 0,
      // A fund is paid by transfer; cash is the exception here, which is the reverse of an
      // advance handed to someone standing at the counter.
      payoutMethod: 'Bank',
      cashBoxId: '',
      periodYear: new Date().getFullYear(),
      periodMonth: null,
      reference: '',
      note: '',
    };
  }
}
