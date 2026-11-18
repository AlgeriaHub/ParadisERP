import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCashBox, AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { formatDay, localizedName } from '../backoffice-format';
import {
  PayWagesLine,
  PayoutMethod,
  PayrollRunHead,
  PayrollRunLine,
  WageMoneyApi,
  WagePaymentRow,
  moneyErrorMessage,
} from './wage-money-api.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { DEFAULT_PAGE_SIZE } from '../../../ui/pager';
import { createServerPager } from '../../../ui/server-pager';
import { PayrollPagesApi } from './payroll-pages.api';

/**
 * Payday: the largest regular cash outflow the business has.
 *
 * <p>A branch pays its cash crew on the 2nd and transfers the salaried staff on the 5th, and two
 * of the crew are away that week. So this screen is built around partial payment being the
 * ordinary case rather than the exception: several payments against one run are normal, the run
 * becomes Paid when no payslip has anything left on it, and nothing here is ever edited — a
 * mistake is reversed, with a reason, and both documents stay visible.</p>
 *
 * <p>The one hard gate in the whole module lives here, and it is stated in words rather than by
 * a greyed-out button with no explanation: <b>wages are paid against an approved run</b>. Money
 * paid against a draft clears a debt the ledger was never told about. Everything else that could
 * look wrong — a box counted short, staff whose own method does not match this payment — is a
 * warning beside the document, never a refusal.</p>
 *
 * <p>Paying "everyone still owed" is not everyone: the server takes only the staff whose own
 * payout method matches this payment, so one cash payment cannot hand a bank-paid salary over
 * the counter. The payslip line does not carry that method, so the rule is said in words above
 * the button rather than pre-counted here — a count this screen cannot verify would be worse
 * than a sentence that is exactly true.</p>
 */
@Component({
  selector: 'app-wage-payments',
  standalone: true,
  imports: [FormsModule, ListFooterComponent],
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

      <!-- ---------- which run ---------- -->
      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('hr.wagepay.pickRun') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('hr.wagepay.pickRunHint') }}</p>
        </div>
        <div class="bo-panel__body">
          <div class="bo-fields">
            @if (lock.locked()) {
              <p class="bo-scope-note" id="wp-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
            } @else {
              <div class="bo-field">
                <label for="wp-branch">{{ i18n.t('hr.money.branch') }}</label>
                <select id="wp-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
                  <option value="">{{ i18n.t('hr.money.branch.all') }}</option>
                  @for (b of branches(); track b.id) {
                    <option [value]="b.id">{{ branchName(b) }}</option>
                  }
                </select>
              </div>
            }
            <div class="bo-field">
              <label for="wp-year">{{ i18n.t('hr.money.year') }}</label>
              <input
                id="wp-year"
                type="number"
                min="2000"
                max="2100"
                class="num mono"
                [ngModel]="year()"
                (ngModelChange)="setYear($event)"
              />
            </div>
            <!-- Runs on the server with the branch and the year (owner, 2026-11-17: "paginate in backend"). -->
            <div class="bo-field">
              <label for="wp-f-state">{{ i18n.t('hr.wagepay.runState') }}</label>
              <select id="wp-f-state" [ngModel]="runStateFilter()" (ngModelChange)="runStateFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                @for (s of runStates; track s) {
                  <option [ngValue]="s">{{ i18n.t('hr.run.status.' + s) }}</option>
                }
              </select>
            </div>
          </div>
          @if (runStateFilter() !== 'all') {
            <div class="bo-actions">
              <button type="button" class="btn-ghost" (click)="runStateFilter.set('all')">{{ i18n.t('ui.state.clearFilters') }}</button>
            </div>
          }

          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('hr.wagepay.run') }}</th>
                  <th>{{ i18n.t('hr.money.branch') }}</th>
                  <th>{{ i18n.t('hr.money.period') }}</th>
                  <th>{{ i18n.t('hr.wagepay.runState') }}</th>
                  <th class="num">{{ i18n.t('hr.wagepay.headCount') }}</th>
                  <th class="num">{{ i18n.t('hr.wagepay.net') }}</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (r of runPager.rows(); track r.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('hr.wagepay.run')">{{ r.number }}</td>
                    <td [attr.data-label]="i18n.t('hr.money.branch')">{{ r.branchName }}</td>
                    <td [attr.data-label]="i18n.t('hr.money.period')">{{ period(r.periodYear, r.periodMonth) }}</td>
                    <td [attr.data-label]="i18n.t('hr.wagepay.runState')">
                      <span class="bo-state" [class]="runStateClass(r.status)">
                        {{ i18n.t('hr.run.status.' + r.status) }}
                      </span>
                      @if (r.isProvisional) {
                        <span class="bo-state bo-state--warn">{{ i18n.t('hr.run.provisional') }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('hr.wagepay.headCount')">{{ r.headCount }}</td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('hr.wagepay.net')">
                      {{ i18n.money(r.net) }}
                    </td>
                    <td class="bo-cell-actions">
                      <button type="button" class="btn-ghost bo-small" (click)="open(r.id)">
                        {{ i18n.t('hr.wagepay.open') }}
                      </button>
                    </td>
                  </tr>
                }
                @if (!runPager.total()) {
                  @if (!runPager.loaded() || runStateFilter() === 'all') {
                    <tr>
                      <td class="bo-empty" colspan="7">
                        {{ !runPager.loaded() ? i18n.t('common.loading') : i18n.t('hr.wagepay.noRuns') }}
                      </td>
                    </tr>
                  } @else {
                    <tr>
                      <td class="bo-empty" colspan="7">
                        <strong>{{ i18n.t('ui.state.noResults') }}</strong>
                        <button type="button" class="btn-ghost bo-small" (click)="runStateFilter.set('all')">
                          {{ i18n.t('ui.state.clearFilters') }}
                        </button>
                      </td>
                    </tr>
                  }
                }
              </tbody>
            </table>
          </div>
          <app-list-footer [pager]="runPager" [loading]="!runPager.loaded()" />
        </div>
      </section>

      @if (run(); as r) {
        <!-- ---------- what this run still owes ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">
              <span class="mono">{{ r.number }}</span> — {{ r.branchName }} — {{ period(r.periodYear, r.periodMonth) }}
            </h2>
            <p class="bo-panel__note">{{ i18n.t('hr.wagepay.runHint') }}</p>
          </div>
          <div class="bo-panel__body">
            @if (r.isProvisional) {
              <p class="notice warn" role="status">{{ i18n.t('hr.wagepay.provisional') }}</p>
            }
            @if (!payable()) {
              <p class="notice danger" role="alert">
                {{ i18n.t('hr.wagepay.notApproved', { state: i18n.t('hr.run.status.' + r.status) }) }}
              </p>
            }

            <div class="bo-metrics">
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('hr.wagepay.net') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(r.net) }}</strong>
              </div>
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('hr.wagepay.paid') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(r.paidTotal) }}</strong>
              </div>
              <div class="bo-metric bo-metric--lead">
                <span class="bo-metric__label">{{ i18n.t('hr.wagepay.owed') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(owedTotal()) }}</strong>
                <span class="bo-metric__note">
                  {{ i18n.t('hr.wagepay.owedNote', { count: owedLineCount() }) }}
                </span>
              </div>
            </div>
          </div>
        </section>

        <!-- ---------- the payment ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.wagepay.pay') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('hr.wagepay.payHint') }}</p>
          </div>
          <div class="bo-panel__body bo-form">
            <div class="bo-fields bo-fields--roomy">
              <div class="bo-field">
                <label for="wp-method">{{ i18n.t('hr.money.method') }}</label>
                <select id="wp-method" [ngModel]="method()" (ngModelChange)="setMethod($event)">
                  <option value="Cash">{{ i18n.t('hr.money.method.Cash') }}</option>
                  <option value="Bank">{{ i18n.t('hr.money.method.Bank') }}</option>
                </select>
              </div>
              @if (method() === 'Cash') {
                <div class="bo-field" [class.is-invalid]="!cashBoxId()">
                  <label for="wp-box">{{ i18n.t('hr.money.cashBox') }}</label>
                  <select id="wp-box" [ngModel]="cashBoxId()" (ngModelChange)="cashBoxId.set($event)">
                    <option value="">{{ i18n.t('hr.money.pickBox') }}</option>
                    @for (b of boxes(); track b.id) {
                      <option [value]="b.id">{{ boxName(b) }}</option>
                    }
                  </select>
                  @if (!cashBoxId()) {
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
                <label for="wp-ref">{{ i18n.t('hr.money.reference') }}</label>
                <input id="wp-ref" type="text" maxlength="60" [(ngModel)]="reference" />
              </div>
              <div class="bo-field bo-field--wide">
                <label for="wp-note">{{ i18n.t('hr.money.note') }}</label>
                <input id="wp-note" type="text" maxlength="200" [(ngModel)]="note" />
              </div>
            </div>

            <div class="bo-chips">
              <button
                type="button"
                [class.is-on]="mode() === 'all'"
                [attr.aria-current]="mode() === 'all' ? 'true' : null"
                (click)="setMode('all')"
              >
                {{ i18n.t('hr.wagepay.mode.all') }}
              </button>
              <button
                type="button"
                [class.is-on]="mode() === 'some'"
                [attr.aria-current]="mode() === 'some' ? 'true' : null"
                (click)="setMode('some')"
              >
                {{ i18n.t('hr.wagepay.mode.some') }}
              </button>
            </div>

            @if (mode() === 'all') {
              <p class="bo-hint">
                {{ i18n.t('hr.wagepay.allRule', { method: i18n.t('hr.money.method.' + method()) }) }}
              </p>
            } @else {
              <p class="bo-hint">{{ i18n.t('hr.wagepay.someRule') }}</p>
            }

            @if (shortBox()) {
              <p class="notice warn" role="status">{{ i18n.t('hr.money.boxShortAhead') }}</p>
            }
          </div>
          <div class="bo-panel__foot bo-actions">
            @if (mode() === 'some') {
              <span class="bo-meta">
                {{ i18n.t('hr.wagepay.chosen', { count: chosenCount(), amount: i18n.money(chosenTotal()) }) }}
              </span>
            }
            <button type="button" class="btn-primary" [disabled]="busy() || !canPay()" (click)="pay()">
              {{ i18n.t('hr.wagepay.payAction') }}
            </button>
          </div>
        </section>

        <!-- ---------- the payslip lines ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.wagepay.lines') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('hr.wagepay.linesHint') }}</p>
          </div>
          <div class="bo-panel__body">
            <div class="bo-scroll bo-scroll--tall">
              <table class="bo-data">
                <thead>
                  <tr>
                    @if (mode() === 'some') {
                      <th>{{ i18n.t('hr.wagepay.take') }}</th>
                    }
                    <th>{{ i18n.t('hr.wagepay.employee') }}</th>
                    <th class="num">{{ i18n.t('hr.wagepay.net') }}</th>
                    <th class="num">{{ i18n.t('hr.wagepay.paid') }}</th>
                    <th class="num">{{ i18n.t('hr.wagepay.owed') }}</th>
                    @if (mode() === 'some') {
                      <th class="num">{{ i18n.t('hr.wagepay.amountNow') }}</th>
                    }
                    <th>{{ i18n.t('hr.wagepay.lineState') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (l of lines(); track l.id) {
                    <tr [class.is-muted]="owed(l) <= 0">
                      @if (mode() === 'some') {
                        <td class="bo-cell-actions" [attr.data-label]="i18n.t('hr.wagepay.take')">
                          <input
                            type="checkbox"
                            [disabled]="owed(l) <= 0"
                            [checked]="picked().has(l.id)"
                            (change)="toggle(l)"
                            [attr.aria-label]="l.employeeName"
                          />
                        </td>
                      }
                      <td [attr.data-label]="i18n.t('hr.wagepay.employee')">
                        <span class="mono" dir="ltr">{{ l.employeeNumber }}</span> {{ l.employeeName }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('hr.wagepay.net')">{{ i18n.money(l.net) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('hr.wagepay.paid')">
                        {{ i18n.money(l.paidAmount) }}
                      </td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('hr.wagepay.owed')">
                        {{ i18n.money(owed(l)) }}
                      </td>
                      @if (mode() === 'some') {
                        <td class="num" [attr.data-label]="i18n.t('hr.wagepay.amountNow')">
                          @if (picked().has(l.id)) {
                            <input
                              type="number"
                              class="num mono bo-input-narrow"
                              min="0"
                              [max]="owed(l)"
                              [step]="regional.moneyStep()"
                              [ngModel]="picked().get(l.id)"
                              (ngModelChange)="setLineAmount(l, $event)"
                              [attr.aria-label]="i18n.t('hr.wagepay.amountNow')"
                            />
                            @if (trimmed().has(l.id)) {
                              <span class="bo-error" role="status">
                                {{ i18n.t('hr.wagepay.trimmed', { amount: i18n.money(owed(l)) }) }}
                              </span>
                            }
                          } @else {
                            <span class="bo-meta">—</span>
                          }
                        </td>
                      }
                      <td [attr.data-label]="i18n.t('hr.wagepay.lineState')">
                        <span class="bo-state" [class]="owed(l) <= 0 ? 'bo-state--ok' : 'bo-state--warn'">
                          {{ i18n.t(owed(l) <= 0 ? 'hr.wagepay.line.settled' : 'hr.wagepay.line.owed') }}
                        </span>
                      </td>
                    </tr>
                  }
                  @if (!lines().length) {
                    <tr>
                      <td class="bo-empty" [attr.colspan]="mode() === 'some' ? 7 : 5">
                        {{ busy() ? i18n.t('common.loading') : i18n.t('hr.wagepay.noLines') }}
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <!-- The server pages the payslips (skip/take); a line picked on one page stays picked on the next. -->
            <app-list-footer
              [total]="lineTotal()"
              [pageIndex]="linePageIndex()"
              [pageSize]="lineSize()"
              [loading]="busy() && !lines().length"
              (pageChange)="goToLines($event)"
              (pageSizeChange)="setLineSize($event)"
            />
          </div>
        </section>

        <!-- ---------- what has been handed over already ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.wagepay.history') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('hr.wagepay.historyHint') }}</p>
          </div>
          <div class="bo-panel__body">
            @if (paymentPager.total() || paymentsFiltered()) {
              <!-- Both run on the server over all the run's payments (owner, 2026-11-17: "paginate in backend"). -->
              <div class="bo-fields">
                <div class="bo-field">
                  <label for="wp-f-method">{{ i18n.t('hr.money.method') }}</label>
                  <select id="wp-f-method" [ngModel]="paymentMethodFilter()" (ngModelChange)="paymentMethodFilter.set($event)">
                    <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                    <option ngValue="Cash">{{ i18n.t('hr.money.method.Cash') }}</option>
                    <option ngValue="Bank">{{ i18n.t('hr.money.method.Bank') }}</option>
                  </select>
                </div>
                <div class="bo-field">
                  <label for="wp-f-pstate">{{ i18n.t('hr.wagepay.paymentState') }}</label>
                  <select id="wp-f-pstate" [ngModel]="paymentStateFilter()" (ngModelChange)="paymentStateFilter.set($event)">
                    <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                    <option ngValue="standing">{{ i18n.t('hr.wagepay.standing') }}</option>
                    <option ngValue="reversed">{{ i18n.t('hr.wagepay.reversed') }}</option>
                  </select>
                </div>
              </div>
              @if (paymentsFiltered()) {
                <div class="bo-actions">
                  <button type="button" class="btn-ghost" (click)="clearPaymentFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                </div>
              }
            }
            <div class="bo-scroll">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('hr.wagepay.payment') }}</th>
                    <th>{{ i18n.t('hr.wagepay.paidOn') }}</th>
                    <th>{{ i18n.t('hr.money.method') }}</th>
                    <th class="num">{{ i18n.t('hr.wagepay.amount') }}</th>
                    <th>{{ i18n.t('hr.money.reference') }}</th>
                    <th>{{ i18n.t('hr.wagepay.paymentState') }}</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  @for (p of paymentPager.rows(); track p.payment.id) {
                    <tr [class.is-muted]="p.isReversed">
                      <td class="mono" [attr.data-label]="i18n.t('hr.wagepay.payment')">{{ p.payment.number }}</td>
                      <td [attr.data-label]="i18n.t('hr.wagepay.paidOn')">{{ day(p.payment.paidAtUtc) }}</td>
                      <td [attr.data-label]="i18n.t('hr.money.method')">
                        {{ i18n.t('hr.money.method.' + p.payment.payoutMethod) }}
                      </td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('hr.wagepay.amount')">
                        {{ i18n.money(p.payment.amount) }}
                      </td>
                      <td [attr.data-label]="i18n.t('hr.money.reference')">{{ p.payment.reference || '—' }}</td>
                      <td [attr.data-label]="i18n.t('hr.wagepay.paymentState')">
                        @if (p.isReversed) {
                          <span class="bo-state bo-state--danger">{{ i18n.t('hr.wagepay.reversed') }}</span>
                          <span class="bo-meta">
                            {{ day(p.reversedAtUtc) }} — {{ p.reversalReason || i18n.t('hr.wagepay.noReason') }}
                          </span>
                        } @else {
                          <span class="bo-state bo-state--ok">{{ i18n.t('hr.wagepay.standing') }}</span>
                        }
                      </td>
                      <td class="bo-cell-actions">
                        @if (!p.isReversed) {
                          <button type="button" class="btn-ghost bo-small" (click)="askReverse(p)">
                            {{ i18n.t('hr.wagepay.reverse') }}
                          </button>
                        }
                      </td>
                    </tr>
                  }
                  @if (!paymentPager.total()) {
                    @if (!paymentsFiltered()) {
                      <tr>
                        <td class="bo-empty" colspan="7">{{ i18n.t('hr.wagepay.noPayments') }}</td>
                      </tr>
                    } @else {
                      <tr>
                        <td class="bo-empty" colspan="7">
                          <strong>{{ i18n.t('ui.state.noResults') }}</strong>
                          <button type="button" class="btn-ghost bo-small" (click)="clearPaymentFilters()">
                            {{ i18n.t('ui.state.clearFilters') }}
                          </button>
                        </td>
                      </tr>
                    }
                  }
                </tbody>
              </table>
            </div>
            <app-list-footer [pager]="paymentPager" [loading]="!paymentPager.loaded()" />
          </div>
        </section>
      }
    </div>

    <!-- ---------- reversing, which needs a reason before it needs a confirmation ---------- -->
    @if (reversing(); as p) {
      <div class="bo-backdrop" role="dialog" aria-modal="true" [attr.aria-label]="i18n.t('hr.wagepay.reverse')">
        <div class="bo-modal bo-modal--narrow">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('hr.wagepay.reverse') }}</h2>
            <p class="bo-panel__note">
              {{ i18n.t('hr.wagepay.reverseHint', { number: p.payment.number, amount: i18n.money(p.payment.amount) }) }}
            </p>
          </div>
          <div class="bo-panel__body bo-form">
            <div class="bo-field bo-field--wide" [class.is-invalid]="!reversalReason.trim()">
              <label for="wp-reason">{{ i18n.t('hr.wagepay.reason') }}</label>
              <input id="wp-reason" type="text" maxlength="200" [(ngModel)]="reversalReason" />
              @if (!reversalReason.trim()) {
                <span class="bo-error" role="alert">{{ i18n.t('hr.wagepay.reasonRequired') }}</span>
              }
            </div>
          </div>
          <div class="bo-panel__foot bo-actions">
            <button type="button" (click)="reversing.set(null)">{{ i18n.t('common.cancel') }}</button>
            <button type="button" class="btn-danger" [disabled]="busy() || !reversalReason.trim()" (click)="reverse()">
              {{ i18n.t('hr.wagepay.reverseAction') }}
            </button>
          </div>
        </div>
      </div>
    }
  `,
  styleUrls: ['../backoffice-ui.scss'],
})
export class WagePaymentsComponent implements OnInit {
  readonly i18n = inject(I18nService);
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
  /** The runs list asks the server once the branch a branch-held user is confined to is known. */
  private readonly ready = signal(false);
  readonly run = signal<PayrollRunHead | null>(null);
  readonly lines = signal<PayrollRunLine[]>([]);
  readonly lineTotal = signal(0);
  readonly skip = signal(0);
  /**
   * How many payslip lines one page of the settlement table asks for: ten to begin with, 20, 50 or
   * 100 at the user's choice (owner, 2026-11-17). The server caps at 1000.
   */
  readonly lineSize = signal(DEFAULT_PAGE_SIZE);
  readonly linePageIndex = computed(() => Math.floor(this.skip() / this.lineSize()));

  readonly branchId = signal('');
  readonly year = signal(new Date().getFullYear());

  readonly method = signal<PayoutMethod>('Cash');
  readonly cashBoxId = signal('');
  readonly mode = signal<'all' | 'some'>('all');
  /** Line id to the amount this payment takes off it. Kept across pages, like the sheet's buffer. */
  readonly picked = signal<Map<string, number>>(new Map());
  readonly reversing = signal<WagePaymentRow | null>(null);

  reference = '';
  note = '';
  reversalReason = '';

  // The lists' own filters (owner, 2026-11-17), applied on the server: every page, size or filter is a request.
  readonly runStates: readonly PayrollRunHead['status'][] = ['Draft', 'Locked', 'Approved', 'Paid', 'Reversed'];
  readonly runStateFilter = signal<'all' | PayrollRunHead['status']>('all');
  /** The runs of the branch and the year, in a state or all of them, ten to begin with. */
  readonly runPager = createServerPager(
    (page) => this.pages.runsPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'payroll.wagePayments.runs',
      filters: () => ({ branchId: this.branchId() || null, year: this.year(), status: this.runStateFilter() }),
      enabled: () => this.ready(),
    },
  );

  readonly paymentMethodFilter = signal<'all' | PayoutMethod>('all');
  readonly paymentStateFilter = signal<'all' | 'standing' | 'reversed'>('all');
  readonly paymentsFiltered = computed(() => this.paymentMethodFilter() !== 'all' || this.paymentStateFilter() !== 'all');
  /** What has been handed over against the open run; another run goes back to page one. */
  readonly paymentPager = createServerPager(
    (page) => {
      const { runId, ...filters } = page.filters;
      return this.pages.wagePaymentsPage(runId ?? '', { skip: page.skip, take: page.take, ...filters });
    },
    {
      key: 'payroll.wagePayments.history',
      filters: () => ({
        runId: this.run()?.id ?? null,
        method: this.paymentMethodFilter(),
        state: this.paymentStateFilter(),
      }),
      enabled: () => !!this.run(),
    },
  );

  /** A list the server could not read, in words. */
  readonly listError = computed(() => {
    const e = this.runPager.error() ?? this.paymentPager.error();
    return e ? moneyErrorMessage(e, (key, params) => this.i18n.t(key, params)) : null;
  });

  clearPaymentFilters(): void {
    this.paymentMethodFilter.set('all');
    this.paymentStateFilter.set('all');
  }

  readonly payable = computed(() => {
    const status = this.run()?.status;
    return status === 'Approved' || status === 'Paid';
  });

  readonly owedTotal = computed(() => {
    const run = this.run();
    return run ? Math.max(0, run.net - run.paidTotal) : 0;
  });
  /** Of this page. The run's own figure above is the whole run; this counts what is on screen. */
  readonly owedLineCount = computed(() => this.lines().filter((l) => this.owed(l) > 0).length);

  readonly chosenBox = computed(() => this.boxes().find((b) => b.id === this.cashBoxId()) ?? null);
  readonly chosenCount = computed(() => this.picked().size);
  readonly chosenTotal = computed(() => [...this.picked().values()].reduce((sum, a) => sum + a, 0));

  /**
   * What this click will actually take out of the box. In "everyone still owed" the screen cannot
   * know which staff match the method, so it compares against the whole outstanding figure — the
   * warning is then the widest true one rather than a precise one it cannot compute.
   */
  readonly shortBox = computed(() => {
    const box = this.chosenBox();
    if (this.method() !== 'Cash' || !box) return false;
    const amount = this.mode() === 'some' ? this.chosenTotal() : this.owedTotal();
    return amount > box.balance;
  });

  async ngOnInit(): Promise<void> {
    await this.attempt(async () => {
      const [branches, boxes] = await Promise.all([this.admin.listBranches(), this.admin.listCashBoxes()]);
      this.branches.set(branches.filter((b) => b.isActive));
      this.branchId.set(this.lock.lockedBranchId() ?? '');
      this.boxes.set(this.lock.scopeRows(boxes));
    });
    // The runs list loads itself from here on, and again whenever a filter changes.
    this.ready.set(true);
  }

  owed(line: PayrollRunLine): number {
    return Math.max(0, line.net - line.paidAmount);
  }

  canPay(): boolean {
    if (!this.run() || !this.payable()) return false;
    if (this.method() === 'Cash' && !this.cashBoxId()) return false;
    if (this.mode() === 'some') return this.chosenCount() > 0 && this.chosenTotal() > 0;
    return this.owedTotal() > 0;
  }

  day(value: string | null | undefined): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  /** The month named, not "3" — a period is a month of a year, and Arabic names it in Arabic. */
  period(year: number, month: number): string {
    const at = new Date(Date.UTC(year, Math.max(0, month - 1), 1));
    try {
      return new Intl.DateTimeFormat(this.i18n.lang(), {
        month: 'long',
        year: 'numeric',
        numberingSystem: 'latn',
        timeZone: 'UTC',
      }).format(at);
    } catch {
      return `${month}/${year}`;
    }
  }

  branchName(b: AdminBranch): string {
    return localizedName(b, this.i18n.lang());
  }

  boxName(b: AdminCashBox): string {
    return localizedName(b, this.i18n.lang());
  }

  runStateClass(status: string): string {
    if (status === 'Paid') return 'bo-state--ok';
    if (status === 'Approved') return 'bo-state--lead';
    if (status === 'Reversed') return 'bo-state--danger';
    return 'bo-state--warn';
  }

  /** Another branch or year: the runs list asks the server again, from page one. */
  setBranch(id: string): void {
    this.branchId.set(id);
  }

  setYear(year: number): void {
    this.year.set(Number(year) || new Date().getFullYear());
  }

  setMethod(method: PayoutMethod): void {
    this.method.set(method);
    // A transfer leaves no drawer; a box left selected would ride along meaning nothing.
    if (method === 'Bank') this.cashBoxId.set('');
    else this.preselectBox();
  }

  setMode(mode: 'all' | 'some'): void {
    this.mode.set(mode);
    if (mode === 'all') {
      this.picked.set(new Map());
      this.trimmed.set(new Set());
    }
  }

  toggle(line: PayrollRunLine): void {
    const owed = this.owed(line);
    if (owed <= 0) return;
    const next = new Map(this.picked());
    if (next.has(line.id)) next.delete(line.id);
    else next.set(line.id, owed);
    this.picked.set(next);
  }

  /**
   * Which lines had a typed figure brought down to what is owed. The correction is said beside
   * the field rather than left silent: clamping keeps a mistyped digit from losing the whole
   * payment, but a figure that changes under the cashier with nothing said is how a payout gets
   * signed for at a number nobody chose.
   */
  readonly trimmed = signal<Set<string>>(new Set());

  setLineAmount(line: PayrollRunLine, value: number): void {
    const owed = this.owed(line);
    const typed = Math.max(0, Number(value) || 0);
    // Clamped rather than refused: the server refuses anything above what is owed, and a form
    // that loses the whole payment over one mistyped figure is worse. The note says it happened.
    const amount = Math.min(owed, typed);
    const next = new Map(this.picked());
    next.set(line.id, amount);
    this.picked.set(next);

    this.trimmed.update((marked) => {
      const copy = new Set(marked);
      if (typed > owed) copy.add(line.id);
      else copy.delete(line.id);
      return copy;
    });
  }

  async open(runId: string): Promise<void> {
    this.warning.set(null);
    this.done.set(null);
    this.skip.set(0);
    this.picked.set(new Map());
    this.trimmed.set(new Set());
    // Another run's payments load themselves (the run is their filter); the same run is asked again.
    const same = this.run()?.id === runId;
    await this.attempt(async () => {
      const detail = await this.money.readRun(runId);
      this.run.set(detail.run);
      this.preselectBox();
    });
    await Promise.all([this.loadLines(), same ? this.paymentPager.reload() : Promise.resolve()]);
  }

  /** A page of payslips from the footer: the server is asked for it, as the old next/previous did. */
  async goToLines(index: number): Promise<void> {
    const next = Math.max(0, index * this.lineSize());
    if (next === this.skip()) return;
    this.skip.set(next);
    await this.loadLines();
  }

  /** Another page size starts again at the first page; the lines picked stay picked. */
  async setLineSize(size: number): Promise<void> {
    if (size === this.lineSize()) return;
    this.lineSize.set(size);
    this.skip.set(0);
    await this.loadLines();
  }

  async pay(): Promise<void> {
    const run = this.run();
    if (!run || !this.canPay()) return;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('hr.wagepay.payAction'))))) return;
    this.warning.set(null);
    this.done.set(null);

    const lines: PayWagesLine[] | null =
      this.mode() === 'some'
        ? [...this.picked().entries()].filter(([, amount]) => amount > 0).map(([runLineId, amount]) => ({ runLineId, amount }))
        : null;

    await this.attempt(async () => {
      const result = await this.money.pay(run.id, {
        runId: run.id,
        payoutMethod: this.method(),
        cashBoxId: this.method() === 'Cash' ? this.cashBoxId() || null : null,
        reference: this.reference.trim() || null,
        note: this.note.trim() || null,
        lines,
      });
      // The server writes its warnings as sentences rather than codes; shown as they came.
      this.warning.set(result.warning ?? null);
      this.done.set(
        this.i18n.t('hr.wagepay.paidDone', {
          number: result.payment.payment.number,
          amount: this.i18n.money(result.payment.payment.amount),
        }),
      );
      this.reference = '';
      this.note = '';
      this.picked.set(new Map());
      await this.refresh(run.id);
    });
  }

  askReverse(payment: WagePaymentRow): void {
    this.reversalReason = '';
    this.reversing.set(payment);
  }

  async reverse(): Promise<void> {
    const target = this.reversing();
    const run = this.run();
    if (!target || !run || !this.reversalReason.trim()) return;
    await this.attempt(async () => {
      const result = await this.money.reversePayment(target.payment.id, this.reversalReason.trim());
      this.warning.set(result.warning ?? null);
      this.done.set(this.i18n.t('hr.wagepay.reversedDone', { number: target.payment.number }));
      this.reversing.set(null);
      this.reversalReason = '';
      await this.refresh(run.id);
    });
  }

  private async refresh(runId: string): Promise<void> {
    await this.attempt(async () => {
      const detail = await this.money.readRun(runId);
      this.run.set(detail.run);
      this.boxes.set(this.lock.scopeRows(await this.admin.listCashBoxes()));
    });
    await Promise.all([this.loadLines(), this.paymentPager.reload(), this.runPager.reload()]);
  }

  private async loadLines(): Promise<void> {
    const run = this.run();
    if (!run) return;
    await this.attempt(async () => {
      const page = await this.money.runLines(run.id, this.skip(), this.lineSize());
      this.lines.set(page.lines);
      this.lineTotal.set(page.total);
    });
  }

  /** The run's own branch box: payday is paid where the crew is, not from head office. */
  private preselectBox(): void {
    if (this.method() !== 'Cash') return;
    const run = this.run();
    if (!run) return;
    const mine = this.boxes().filter((b) => b.branchId === run.branchId);
    const pick = mine.find((b) => !b.isMain) ?? mine[0];
    if (pick) this.cashBoxId.set(pick.id);
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
}
