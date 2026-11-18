import { Component, EventEmitter, OnInit, Output, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminSaleDetail, AdminService, PendingPayment, adminErrorMessage } from '../../core/admin.service';
import { BranchLock } from '../../core/branch-lock';
import { I18nService } from '../../core/i18n.service';
import { ConfirmService } from '../../ui/confirm.service';
import { RegionalService } from '../../core/regional.service';
import { formatDay } from './backoffice-format';
import { PrintService } from '../print/print.service';
import { DateInputComponent } from '../../ui/date-input.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { TableStateComponent } from '../../ui/table-state.component';
import { FinancePagesApi } from './finance/finance-pages.api';
import { PAGE_CHROME } from './shell/page-chrome';
import {
  BalanceSheet,
  LedgerAccount,
  LedgerAccountType,
  LedgerEntry,
  LedgerService,
  MAX_PERIOD_DAYS,
  ProfitAndLoss,
} from '../../core/ledger.service';

type Section = 'trial' | 'pl' | 'balance' | 'statement' | 'vouchers';
type Preset = 'month' | 'lastMonth' | 'quarter' | 'year';

const DAY = 86_400_000;

/**
 * Ledger source types known to the accounting tab. Grouped by module:
 * sales and receivables, rentals, treasury, stock, payroll, corrections.
 */
export const ACCOUNTING_SOURCES = [
  // sales and receivables
  'sale',
  'payment-settlement',
  'invoice',
  'customer-receipt',
  // an advance held on 419 applied to an invoice or released to 411 (M7b)
  'customer-advance-application',
  // part of an advance kept as other income on a cancelled order (SAL-13)
  'customer-advance-kept',
  'credit-note',
  'payment-voucher',
  'customer-opening',
  'customer-opening-correction',
  'supplier-opening',
  'supplier-opening-correction',
  // gift cards (SAL-17): cash taken for a card, and cash handed back from one
  'gift-card-sale',
  'gift-card-cash-out',
  // rentals
  'rental-issue',
  'rental-return',
  // treasury
  'remittance',
  // stock
  'receiving',
  'opening',
  'stock-transfer',
  'stock-transfer-loss',
  'stock-adjustment',
  'stock-state',
  // the switch of sales to the weighted average (C-01): its opening adjustment, and negative-stock gaps (C-16)
  'valuation-switch',
  'valuation-gap',
  // the stock on hand revalued to a changed cost price (B-F01, the accountant's setting)
  'cost-price-revaluation',
  // months of production and maintenance posted from the practice ledger (Phase D, M6), and maintenance parts posted at issue (MS-32)
  'manufacturing',
  'maintenance',
  'maintenance-issue',
  // hire equipment as fixed assets (REN-12, Phase M7a): the switch-over, the register's events and a month's depreciation
  'fixed-asset-switch',
  'fixed-asset',
  'fixed-assets',
  // payroll
  'payroll-run',
  'wage-payment',
  'wage-advance',
  'payroll-statutory-payment',
  // corrections
  'correction.rental-issue-daily',
  'correction.rental-return-daily',
  'correction.rental-credit-daily',
  'correction.change-double-counted',
] as const;

export type AccountingSource = (typeof ACCOUNTING_SOURCES)[number];

/**
 * The company's books.
 *
 * <p>Every figure here is read from the double-entry ledger, and nothing on this screen composes
 * an entry: entries are raised by the sale, receiving note or remittance that caused them. The
 * one thing that writes is a reversal, which is the single correction the design allows — the
 * original stays and the mirror image goes beside it.</p>
 *
 * <p>Two things this screen has to say out loud, because getting either wrong looks like a bug
 * in the figures rather than a misreading of them:</p>
 * <ul>
 *   <li><b>Consolidating is not adding the branches up.</b> Asked for the group, the server drops
 *   movements between the company's own branches; asked for one branch, it keeps them, because
 *   that branch really did lose the cash. So a branch's own report can differ from its line in
 *   the group's branch table, and the difference is named rather than left to be discovered.</li>
 *   <li><b>Debit and credit are not "in" and "out".</b> The owner is a contractor, not a
 *   bookkeeper, so every account also carries what it is in plain words — owned, owed, earned,
 *   spent, capital — and the two-sided rule is stated once above the trial balance.</li>
 * </ul>
 *
 * <p>The one mark that is not a number is the share bar beside the branch results, the same bar
 * the sales report uses. A loss draws it in the danger hue <em>and</em> says "loss" in the next
 * column: the colour is never the only carrier.</p>
 *
 * <p>The trial balance, the account statement, the cheque register and the journal are read a page
 * at a time (owner, 2026-11-17: "paginate in backend, not frontend"; FinancePagesApi). Their totals,
 * running balances and tiles come from the server, computed over every row; paper prints through
 * the report's own tab.</p>
 */
@Component({
  selector: 'app-backoffice-accounting',
  standalone: true,
  imports: [FormsModule, DateInputComponent, ListFooterComponent, TableStateComponent, ...PAGE_CHROME],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-ghost" [disabled]="!printable()" (click)="print()">{{ i18n.t('common.print') }}</button>
      <button type="button" class="btn-ghost" [disabled]="working()" (click)="refresh()">
        {{ working() ? i18n.t('common.loading') : i18n.t('subscription.refresh') }}
      </button>
    </ng-template>

    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        @if (section() !== 'balance' && section() !== 'vouchers') {
          <div class="ui-filter">
            <span class="ui-filter__label" id="ac-preset-label">{{ i18n.t('reports.period') }}</span>
            <div class="bo-chips" role="group" aria-labelledby="ac-preset-label">
              @for (p of presets; track p.key) {
                <button
                  type="button"
                  [class.is-on]="preset() === p.key"
                  [attr.aria-pressed]="preset() === p.key"
                  (click)="applyPreset(p.key)"
                >
                  {{ i18n.t(p.label) }}
                </button>
              }
            </div>
          </div>
          <div class="ui-filter">
            <label for="ac-from">{{ i18n.t('reports.from') }}</label>
            <app-date-input id="ac-from" [ngModel]="fromDate()" (ngModelChange)="setFrom($event)" />
          </div>
        }
        <div class="ui-filter">
          <label for="ac-to">{{ i18n.t(section() === 'balance' ? 'acc.bs.asOf' : 'reports.to') }}</label>
          <app-date-input id="ac-to" [ngModel]="toDate()" (ngModelChange)="setTo($event)" />
        </div>
        @if (lock.locked()) {
          <p class="bo-scope-note" id="ac-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
        } @else {
          <div class="ui-filter">
            <label for="ac-scope">{{ i18n.t('acc.scope') }}</label>
            <select id="ac-scope" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
              <option [ngValue]="null">{{ i18n.t('acc.group') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ branchLabel(b) }}</option>
              }
            </select>
          </div>
        }
        @if (section() === 'statement') {
          <div class="ui-filter ui-filter--search">
            <label for="ac-account">{{ i18n.t('acc.account') }}</label>
            <select id="ac-account" [ngModel]="accountId()" (ngModelChange)="setAccount($event)">
              <option [ngValue]="null">{{ i18n.t('acc.st.pick') }}</option>
              @for (a of accounts(); track a.id) {
                <option [ngValue]="a.id">{{ a.code }} — {{ accountName(a) }}</option>
              }
            </select>
          </div>
        }
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }
      @if (done()) {
        <p class="notice ok" role="status">{{ done() }}</p>
      }

      @if (!page()) {
        <nav class="bo-chips no-print" [attr.aria-label]="i18n.t('backoffice.tabs.accounting')">
          <button
            type="button"
            [class.is-on]="section() === 'trial'"
            [attr.aria-current]="section() === 'trial' ? 'true' : null"
            (click)="show('trial')"
          >
            {{ i18n.t('acc.section.trial') }}
          </button>
          <button
            type="button"
            [class.is-on]="section() === 'pl'"
            [attr.aria-current]="section() === 'pl' ? 'true' : null"
            (click)="show('pl')"
          >
            {{ i18n.t('acc.section.pl') }}
          </button>
          <button
            type="button"
            [class.is-on]="section() === 'balance'"
            [attr.aria-current]="section() === 'balance' ? 'true' : null"
            (click)="show('balance')"
          >
            {{ i18n.t('acc.section.balance') }}
          </button>
          <button
            type="button"
            [class.is-on]="section() === 'statement'"
            [attr.aria-current]="section() === 'statement' ? 'true' : null"
            (click)="show('statement')"
          >
            {{ i18n.t('acc.section.statement') }}
          </button>
          <button
            type="button"
            [class.is-on]="section() === 'vouchers'"
            [attr.aria-current]="section() === 'vouchers' ? 'true' : null"
            (click)="show('vouchers')"
          >
            {{ i18n.t('acc.section.vouchers') }}
          </button>
        </nav>
      }

      <!-- The period and scope are in the page header (appPageToolbar above); what they mean stays here. -->
      <p class="ui-sub no-print scope-note">{{ i18n.t(scopeNote()) }}</p>
      @if (tooWide()) {
        <p class="notice warn">{{ i18n.t('acc.tooWide', { days: maxDays }) }}</p>
      }

      @if (working() && !sectionReady()) {
        <section class="bo-panel" aria-busy="true">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('acc.section.' + section()) }}</h2>
          </div>
          <p class="bo-loading" role="status">
            <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
            <span class="bo-loading__rows" aria-hidden="true"></span>
          </p>
        </section>
      }

      <!-- ====================== trial balance ====================== -->
      @if (section() === 'trial') {
        @if (trial(); as t) {
          <div class="bo-metrics">
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('acc.totalDebit') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(t.totalDebit) }}</strong>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('acc.totalCredit') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(t.totalCredit) }}</strong>
            </div>
            <div class="bo-metric" [class.bo-metric--lead]="t.balanced" [class.bo-metric--alert]="!t.balanced">
              <span class="bo-metric__label">{{ i18n.t('common.state') }}</span>
              <strong class="bo-metric__value bo-metric__value--word">{{
                i18n.t(t.balanced ? 'acc.balanced' : 'acc.unbalanced')
              }}</strong>
              <span class="bo-metric__note"><span>{{ i18n.t(t.balanced ? 'acc.balancedHint' : 'acc.unbalancedHint') }}</span></span>
            </div>
          </div>

          <section class="bo-panel">
            <div class="bo-panel__head">
              <p class="bo-panel__note">{{ i18n.t('acc.doubleEntry') }} {{ i18n.t('acc.natureLegend') }}</p>
            </div>
            <div class="bo-scroll bo-scroll--tall">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('acc.account') }}</th>
                    <th>{{ i18n.t('common.meaning') }}</th>
                    <th class="num">{{ i18n.t('acc.debit') }}</th>
                    <th class="num">{{ i18n.t('acc.credit') }}</th>
                    <th class="num">{{ i18n.t('treasury.balance') }}</th>
                    <th>{{ i18n.t('acc.side') }}</th>
                    <th>{{ i18n.t('common.actions') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of trialPager.rows(); track row.accountId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('acc.account')">
                        <span class="mono">{{ row.code }}</span> {{ accountName(row) }}
                      </td>
                      <td [attr.data-label]="i18n.t('common.meaning')">
                        <span class="bo-state">{{ nature(row.type) }}</span>
                        @if (wrongSide(row)) {
                          <span class="bo-state bo-state--warn">{{ i18n.t('acc.check') }}</span>
                        }
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('acc.debit')">{{ i18n.money(row.debit) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('acc.credit')">{{ i18n.money(row.credit) }}</td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('treasury.balance')">
                        {{ i18n.money(abs(row.balance)) }}
                      </td>
                      <td [attr.data-label]="i18n.t('acc.side')">
                        <span class="bo-state">{{ balanceSide(row) }}</span>
                      </td>
                      <td class="bo-cell-actions no-print" [attr.data-label]="i18n.t('common.actions')">
                        <button type="button" class="btn-ghost bo-small" (click)="openStatement(row.accountId)">
                          {{ i18n.t('acc.openStatement') }}
                        </button>
                      </td>
                    </tr>
                  } @empty {
                    <tr>
                      <td colspan="7" class="bo-empty">
                        @if (trialPager.loading() || !trialPager.loaded()) {
                          <strong>{{ i18n.t('common.loading') }}</strong>
                          <span class="bo-loading__rows" aria-hidden="true"></span>
                        } @else {
                          <strong>{{ i18n.t('acc.empty') }}</strong>
                          {{ i18n.t('acc.emptyHint') }}
                        }
                      </td>
                    </tr>
                  }
                </tbody>
                @if (trialPager.total()) {
                  <tfoot>
                    <tr>
                      <td [attr.data-label]="i18n.t('acc.account')">{{ i18n.t('common.total') }}</td>
                      <td></td>
                      <td class="num mono" [attr.data-label]="i18n.t('acc.debit')">{{ i18n.money(t.totalDebit) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('acc.credit')">{{ i18n.money(t.totalCredit) }}</td>
                      <td></td>
                      <td></td>
                      <td></td>
                    </tr>
                  </tfoot>
                }
              </table>
            </div>
          </section>
          <!-- Paged only (owner, 2026-11-17): the totals row is the whole balance's, so no filter narrows it. -->
          <app-list-footer [pager]="trialPager" [loading]="!trialPager.loaded()" />
        }
      }

      <!-- ====================== profit and loss ====================== -->
      @if (section() === 'pl') {
        @if (pl(); as p) {
          <div class="bo-metrics">
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('acc.pl.income') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(p.totalIncome) }}</strong>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('acc.pl.expenses') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(p.totalExpenses) }}</strong>
            </div>
            <div class="bo-metric" [class.bo-metric--lead]="p.netProfit >= 0" [class.bo-metric--alert]="p.netProfit < 0">
              <span class="bo-metric__label">{{ i18n.t('acc.pl.net') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(p.netProfit) }}</strong>
              <span class="bo-metric__note">
                <span>{{ i18n.t(p.netProfit < 0 ? 'acc.pl.loss' : 'acc.pl.profit') }}</span>
                <span>{{ i18n.t(p.consolidated ? 'acc.group' : 'acc.oneBranch') }}</span>
              </span>
            </div>
          </div>

          @if (p.consolidated && p.byBranch.length > 0) {
            <section class="bo-panel">
              <div class="bo-panel__head">
                <h2 class="bo-panel__title">{{ i18n.t('acc.pl.byBranch') }}</h2>
                <p class="bo-panel__note">{{ i18n.t('acc.pl.consolidatedNote') }}</p>
              </div>
              <div class="bo-panel__body">
                <div class="bo-metrics">
                  <div class="bo-metric">
                    <span class="bo-metric__label">{{ i18n.t('acc.pl.branchesApart') }}</span>
                    <strong class="bo-metric__value mono">{{ i18n.money(p.netProfit + p.eliminatedInterBranch) }}</strong>
                  </div>
                  <div class="bo-metric">
                    <span class="bo-metric__label">{{ i18n.t('acc.pl.eliminated') }}</span>
                    <strong class="bo-metric__value mono">{{ i18n.money(p.eliminatedInterBranch) }}</strong>
                    <span class="bo-metric__note">
                      <span>{{ i18n.t(p.eliminatedInterBranch === 0 ? 'acc.pl.nothingEliminated' : 'acc.pl.somethingEliminated') }}</span>
                    </span>
                  </div>
                  <div class="bo-metric bo-metric--lead">
                    <span class="bo-metric__label">{{ i18n.t('acc.pl.consolidatedNet') }}</span>
                    <strong class="bo-metric__value mono">{{ i18n.money(p.netProfit) }}</strong>
                  </div>
                </div>
                <p class="bo-group__note">{{ i18n.t('acc.pl.branchOwnNote') }}</p>
              </div>
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('backoffice.tabs.branches') }}</th>
                      <th class="bar-col">{{ i18n.t('reports.share') }}</th>
                      <th class="num">{{ i18n.t('acc.pl.income') }}</th>
                      <th class="num">{{ i18n.t('acc.pl.expenses') }}</th>
                      <th class="num">{{ i18n.t('acc.pl.net') }}</th>
                      <th>{{ i18n.t('common.state') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (b of p.byBranch; track b.branchId) {
                      <tr>
                        <td [attr.data-label]="i18n.t('backoffice.tabs.branches')">{{ b.branchName }}</td>
                        <td class="bar-col" [attr.data-label]="i18n.t('reports.share')">
                          <span class="bo-bar" [class.bo-bar--loss]="b.netProfit < 0" aria-hidden="true">
                            <span [style.inline-size.%]="share(b.netProfit)"></span>
                          </span>
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('acc.pl.income')">{{ i18n.money(b.income) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('acc.pl.expenses')">{{ i18n.money(b.expenses) }}</td>
                        <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('acc.pl.net')">
                          {{ i18n.money(b.netProfit) }}
                        </td>
                        <td [attr.data-label]="i18n.t('common.state')">
                          <span class="bo-state" [class]="b.netProfit < 0 ? 'bo-state--danger' : 'bo-state--ok'">
                            {{ i18n.t(b.netProfit < 0 ? 'acc.pl.loss' : 'acc.pl.profit') }}
                          </span>
                        </td>
                      </tr>
                    }
                  </tbody>
                  <tfoot>
                    <tr>
                      <td [attr.data-label]="i18n.t('backoffice.tabs.branches')">{{ i18n.t('acc.pl.branchSum') }}</td>
                      <td></td>
                      <td class="num mono" [attr.data-label]="i18n.t('acc.pl.income')">{{ i18n.money(p.totalIncome) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('acc.pl.expenses')">{{ i18n.money(p.totalExpenses) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('acc.pl.net')">{{ i18n.money(p.netProfit) }}</td>
                      <td></td>
                    </tr>
                  </tfoot>
                </table>
              </div>
            </section>
          }

          <div class="bo-split">
            <section class="bo-panel">
              <div class="bo-panel__head">
                <h2 class="bo-panel__title">{{ i18n.t('acc.pl.income') }}</h2>
                <p class="bo-panel__note">{{ i18n.t('acc.pl.incomeHint') }}</p>
              </div>
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('acc.account') }}</th>
                      <th class="num">{{ i18n.t('pay.amount') }}</th>
                      <th>{{ i18n.t('common.actions') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (line of p.income; track line.code) {
                      <tr>
                        <td [attr.data-label]="i18n.t('acc.account')">
                          <span class="mono">{{ line.code }}</span> {{ accountName(line) }}
                        </td>
                        <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(line.amount) }}</td>
                        <td class="bo-cell-actions no-print" [attr.data-label]="i18n.t('common.actions')">
                          <button type="button" class="btn-ghost bo-small" (click)="openStatementByCode(line.code)">
                            {{ i18n.t('acc.openStatement') }}
                          </button>
                        </td>
                      </tr>
                    } @empty {
                      <tr>
                        <td colspan="3" class="bo-empty">
                          @if (busy()) {
                            <strong>{{ i18n.t('common.loading') }}</strong>
                            <span class="bo-loading__rows" aria-hidden="true"></span>
                          } @else {
                            <strong>{{ i18n.t('acc.empty') }}</strong>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                  <tfoot>
                    <tr>
                      <td [attr.data-label]="i18n.t('acc.account')">{{ i18n.t('common.total') }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(p.totalIncome) }}</td>
                      <td></td>
                    </tr>
                  </tfoot>
                </table>
              </div>
            </section>

            <section class="bo-panel">
              <div class="bo-panel__head">
                <h2 class="bo-panel__title">{{ i18n.t('acc.pl.expenses') }}</h2>
                <p class="bo-panel__note">{{ i18n.t('acc.pl.expensesHint') }}</p>
              </div>
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('acc.account') }}</th>
                      <th class="num">{{ i18n.t('pay.amount') }}</th>
                      <th>{{ i18n.t('common.actions') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (line of p.expenses; track line.code) {
                      <tr>
                        <td [attr.data-label]="i18n.t('acc.account')">
                          <span class="mono">{{ line.code }}</span> {{ accountName(line) }}
                        </td>
                        <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(line.amount) }}</td>
                        <td class="bo-cell-actions no-print" [attr.data-label]="i18n.t('common.actions')">
                          <button type="button" class="btn-ghost bo-small" (click)="openStatementByCode(line.code)">
                            {{ i18n.t('acc.openStatement') }}
                          </button>
                        </td>
                      </tr>
                    } @empty {
                      <tr>
                        <td colspan="3" class="bo-empty">
                          @if (busy()) {
                            <strong>{{ i18n.t('common.loading') }}</strong>
                            <span class="bo-loading__rows" aria-hidden="true"></span>
                          } @else {
                            <strong>{{ i18n.t('acc.empty') }}</strong>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                  <tfoot>
                    <tr>
                      <td [attr.data-label]="i18n.t('acc.account')">{{ i18n.t('common.total') }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(p.totalExpenses) }}</td>
                      <td></td>
                    </tr>
                  </tfoot>
                </table>
              </div>
            </section>
          </div>
        }
      }

      <!-- ====================== balance sheet ====================== -->
      @if (section() === 'balance') {
        @if (balance(); as b) {
          <div class="bo-metrics">
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('acc.bs.totalAssets') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(b.totalAssets) }}</strong>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('acc.bs.totalClaims') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(b.totalLiabilities + b.totalEquity + b.netResult) }}</strong>
            </div>
            <div class="bo-metric" [class.bo-metric--lead]="b.balanced" [class.bo-metric--alert]="!b.balanced">
              <span class="bo-metric__label">{{ i18n.t('common.state') }}</span>
              <strong class="bo-metric__value bo-metric__value--word">{{
                i18n.t(b.balanced ? 'acc.balanced' : 'acc.unbalanced')
              }}</strong>
              <span class="bo-metric__note"><span>{{ i18n.t('acc.bs.balancedHint') }}</span></span>
            </div>
          </div>

          <div class="bo-split">
            <section class="bo-panel">
              <div class="bo-panel__head">
                <h2 class="bo-panel__title">{{ i18n.t('acc.bs.assets') }}</h2>
                <p class="bo-panel__note">{{ i18n.t('acc.bs.assetsHint') }}</p>
              </div>
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('acc.account') }}</th>
                      <th class="num">{{ i18n.t('pay.amount') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (line of b.assets; track line.code) {
                      <tr>
                        <td [attr.data-label]="i18n.t('acc.account')">
                          <span class="mono">{{ line.code }}</span> {{ accountName(line) }}
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(line.amount) }}</td>
                      </tr>
                    } @empty {
                      <tr>
                        <td colspan="2" class="bo-empty">
                          @if (busy()) {
                            <strong>{{ i18n.t('common.loading') }}</strong>
                            <span class="bo-loading__rows" aria-hidden="true"></span>
                          } @else {
                            <strong>{{ i18n.t('acc.empty') }}</strong>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                  <tfoot>
                    <tr>
                      <td [attr.data-label]="i18n.t('acc.account')">{{ i18n.t('acc.bs.totalAssets') }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(b.totalAssets) }}</td>
                    </tr>
                  </tfoot>
                </table>
              </div>
            </section>

            <section class="bo-panel">
              <div class="bo-panel__head">
                <h2 class="bo-panel__title">{{ i18n.t('acc.bs.totalClaims') }}</h2>
                <p class="bo-panel__note">{{ i18n.t('acc.bs.claimsHint') }}</p>
              </div>
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('acc.account') }}</th>
                      <th class="num">{{ i18n.t('pay.amount') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (line of b.liabilities; track line.code) {
                      <tr>
                        <td [attr.data-label]="i18n.t('acc.account')">
                          <span class="mono">{{ line.code }}</span> {{ accountName(line) }}
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(line.amount) }}</td>
                      </tr>
                    }
                    @for (line of b.equity; track line.code) {
                      <tr>
                        <td [attr.data-label]="i18n.t('acc.account')">
                          <span class="mono">{{ line.code }}</span> {{ accountName(line) }}
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(line.amount) }}</td>
                      </tr>
                    }
                    <tr>
                      <td [attr.data-label]="i18n.t('acc.account')">
                        {{ i18n.t('acc.bs.netResult') }}
                        <span class="bo-state">{{ i18n.t(b.netResult < 0 ? 'acc.pl.loss' : 'acc.pl.profit') }}</span>
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(b.netResult) }}</td>
                    </tr>
                  </tbody>
                  <tfoot>
                    <tr>
                      <td [attr.data-label]="i18n.t('acc.account')">{{ i18n.t('acc.bs.totalClaims') }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('pay.amount')">
                        {{ i18n.money(b.totalLiabilities + b.totalEquity + b.netResult) }}
                      </td>
                    </tr>
                  </tfoot>
                </table>
              </div>
            </section>
          </div>

          <section class="bo-panel">
            <div class="bo-panel__head">
              <h2 class="bo-panel__title">{{ i18n.t('acc.bs.notes') }}</h2>
            </div>
            <div class="bo-panel__body">
              <p class="bo-group__note">{{ i18n.t('acc.bs.equation') }}</p>
              <p class="bo-group__note">{{ i18n.t('acc.bs.netResultNote') }}</p>
              @if (!b.consolidated) {
                <p class="bo-group__note">{{ i18n.t('acc.bs.interBranchNote') }}</p>
              }
              <p class="bo-group__note">{{ i18n.t('acc.openingNote') }}</p>
            </div>
          </section>
        }
      }

      <!-- ====================== account statement ====================== -->
      @if (section() === 'statement') {
        @if (statement(); as s) {
          <div class="bo-metrics">
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('acc.st.opening') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(s.opening) }}</strong>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('acc.debit') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(statementDebit()) }}</strong>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('acc.credit') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(statementCredit()) }}</strong>
            </div>
            <div class="bo-metric bo-metric--lead">
              <span class="bo-metric__label">{{ i18n.t('acc.st.closing') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(s.closing) }}</strong>
              <span class="bo-metric__note"><span>{{ nature(s.type) }}</span></span>
            </div>
          </div>

          <section class="bo-panel">
            <div class="bo-panel__head">
              <h2 class="bo-panel__title"><span class="mono">{{ s.code }}</span> {{ accountName(s) }}</h2>
              <p class="bo-panel__note">{{ i18n.t('acc.st.hint') }}</p>
            </div>
            <div class="bo-scroll bo-scroll--tall">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('receipt.date') }}</th>
                    <th>{{ i18n.t('acc.entry.document') }}</th>
                    <th>{{ i18n.t('backoffice.tabs.branches') }}</th>
                    <th class="num">{{ i18n.t('acc.debit') }}</th>
                    <th class="num">{{ i18n.t('acc.credit') }}</th>
                    <th class="num">{{ i18n.t('treasury.balance') }}</th>
                    <th>{{ i18n.t('common.actions') }}</th>
                  </tr>
                </thead>
                <tbody>
                  <!-- The opening balance comes before the first movement: on the first page (and on paper) only. -->
                  @if (statementAtStart()) {
                    <tr>
                      <td [attr.data-label]="i18n.t('receipt.date')">{{ i18n.t('acc.st.opening') }}</td>
                      <td></td>
                      <td></td>
                      <td></td>
                      <td></td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('treasury.balance')">{{ i18n.money(s.opening) }}</td>
                      <td></td>
                    </tr>
                  }
                  @for (row of statementPager.rows(); track $index) {
                    <tr>
                      <td class="mono" [attr.data-label]="i18n.t('receipt.date')">{{ i18n.dateTime(row.entryDate) }}</td>
                      <td [attr.data-label]="i18n.t('acc.entry.document')">
                        <span class="bo-state">{{ sourceLabel(row.sourceType) }}</span>
                        @if (row.isReversal) {
                          <span class="bo-state bo-state--warn">{{ i18n.t('acc.entry.reversal') }}</span>
                        }
                        @if (row.description) {
                          <span class="bo-soft">{{ row.description }}</span>
                        }
                      </td>
                      <td [attr.data-label]="i18n.t('backoffice.tabs.branches')">{{ branchName(row.branchId) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('acc.debit')">{{ row.debit ? i18n.money(row.debit) : '' }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('acc.credit')">{{ row.credit ? i18n.money(row.credit) : '' }}</td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('treasury.balance')">
                        {{ i18n.money(row.runningBalance) }}
                      </td>
                      <td class="bo-cell-actions no-print" [attr.data-label]="i18n.t('common.actions')">
                        <button type="button" class="btn-ghost bo-small" (click)="openEntry(row.entryId)">
                          {{ i18n.t('acc.drill') }}
                        </button>
                      </td>
                    </tr>
                  } @empty {
                    <tr>
                      <td colspan="7" class="bo-empty">
                        @if (statementPager.loading() || !statementPager.loaded()) {
                          <strong>{{ i18n.t('common.loading') }}</strong>
                          <span class="bo-loading__rows" aria-hidden="true"></span>
                        } @else {
                          <strong>{{ i18n.t('acc.empty') }}</strong>
                          {{ i18n.t('acc.emptyHint') }}
                        }
                      </td>
                    </tr>
                  }
                </tbody>
                <tfoot>
                  <tr>
                    <td [attr.data-label]="i18n.t('receipt.date')">{{ i18n.t('acc.st.closing') }}</td>
                    <td></td>
                    <td></td>
                    <td class="num mono" [attr.data-label]="i18n.t('acc.debit')">{{ i18n.money(statementDebit()) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('acc.credit')">{{ i18n.money(statementCredit()) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('treasury.balance')">{{ i18n.money(s.closing) }}</td>
                    <td></td>
                  </tr>
                </tfoot>
              </table>
            </div>
          </section>
          <!-- Paged only (owner, 2026-11-17): a filter would skip lines of the running balance. -->
          <app-list-footer [pager]="statementPager" [loading]="!statementPager.loaded()" />
        } @else {
          <section class="bo-panel">
            <p class="bo-empty">
              <strong>{{ i18n.t('acc.st.pick') }}</strong>
              {{ i18n.t('acc.st.pickHint') }}
            </p>
          </section>
        }
      }

      <!-- ====================== vouchers ====================== -->
      @if (section() === 'vouchers') {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('acc.vouchers.cheques') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('acc.vouchers.chequesHint') }}</p>
          </div>
          <div class="bo-panel__body">
            <div class="bo-metrics">
              <div class="bo-metric bo-metric--lead">
                <span class="bo-metric__label">{{ i18n.t('acc.vouchers.pending') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(chequeTotal('Pending')) }}</strong>
                <span class="bo-metric__note"><span>{{ i18n.t('acc.vouchers.count') }}</span><span class="mono">{{ chequeCount('Pending') }}</span></span>
              </div>
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('acc.vouchers.collected') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(chequeTotal('Confirmed')) }}</strong>
                <span class="bo-metric__note"><span>{{ i18n.t('acc.vouchers.count') }}</span><span class="mono">{{ chequeCount('Confirmed') }}</span></span>
              </div>
              <div class="bo-metric" [class.bo-metric--alert]="chequeTotal('Bounced') > 0">
                <span class="bo-metric__label">{{ i18n.t('acc.vouchers.bounced') }}</span>
                <strong class="bo-metric__value mono">{{ i18n.money(chequeTotal('Bounced')) }}</strong>
                <span class="bo-metric__note"><span>{{ i18n.t('acc.vouchers.count') }}</span><span class="mono">{{ chequeCount('Bounced') }}</span></span>
              </div>
            </div>
            <p class="bo-group__note">{{ i18n.t('acc.vouchers.treasuryNote') }}</p>
            <!-- The worklist's own filters (owner, 2026-11-17): they narrow the rows below, never the figures above. -->
            @if (anyCheques()) {
              <div class="ui-toolbar no-print" role="search">
                <div class="ui-filter">
                  <label for="ac-ch-status">{{ i18n.t('common.state') }}</label>
                  <select id="ac-ch-status" [ngModel]="chequeStatus()" (ngModelChange)="chequeStatus.set($event)">
                    <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                    <option ngValue="Pending">{{ i18n.t('acc.vouchers.pending') }}</option>
                    <option ngValue="Confirmed">{{ i18n.t('acc.vouchers.collected') }}</option>
                    <option ngValue="Bounced">{{ i18n.t('acc.vouchers.bounced') }}</option>
                  </select>
                </div>
                <div class="ui-filter">
                  <label for="ac-ch-from">{{ i18n.t('treasury.filter.dueFrom') }}</label>
                  <app-date-input id="ac-ch-from" [ngModel]="chequeFrom()" (ngModelChange)="chequeFrom.set($event)" />
                </div>
                <div class="ui-filter">
                  <label for="ac-ch-to">{{ i18n.t('treasury.filter.dueTo') }}</label>
                  <app-date-input id="ac-ch-to" [ngModel]="chequeTo()" (ngModelChange)="chequeTo.set($event)" />
                </div>
                @if (chequesFiltered()) {
                  <div class="ui-toolbar__end">
                    <button type="button" class="btn-ghost" (click)="clearChequeFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                  </div>
                }
              </div>
            }
          </div>
          <div class="bo-scroll bo-scroll--tall">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('treasury.dueDate') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th>{{ i18n.t('treasury.instrument') }}</th>
                  <th>{{ i18n.t('common.customer') }}</th>
                  <th>{{ i18n.t('receipt.no') }}</th>
                  <th class="num">{{ i18n.t('pay.amount') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (c of chequePager.rows(); track c.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('treasury.dueDate')">{{ day(c.instrumentDueDate) }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <span class="bo-state" [class]="chequeClass(c.status)">{{ i18n.t('acc.vouchers.' + statusKey(c.status)) }}</span>
                      <!-- FR-2B: a bounce says why; the reason is the settlement's note. -->
                      @if (c.statusNote) {
                        <span class="bo-soft">{{ c.statusNote }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('treasury.instrument')">
                      <span class="mono">{{ c.instrumentNumber || c.methodKey }}</span>
                      @if (c.instrumentBank) {
                        <span class="bo-soft">{{ c.instrumentBank }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('common.customer')">{{ c.customerName || '—' }}</td>
                    <td class="mono" [attr.data-label]="i18n.t('receipt.no')">{{ c.saleReference }}</td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(c.amount) }}</td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="6" class="bo-empty">
                      @if (chequePager.loading() || !chequePager.loaded()) {
                        <strong>{{ i18n.t('common.loading') }}</strong>
                        <span class="bo-loading__rows" aria-hidden="true"></span>
                      } @else if (anyCheques() && chequesFiltered()) {
                        <app-table-state kind="noResults" (clear)="clearChequeFilters()" />
                      } @else {
                        <strong>{{ i18n.t('treasury.nonePending') }}</strong>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <div class="bo-panel__foot no-print">
            <button type="button" class="btn-ghost" (click)="goTreasury.emit()">
              {{ i18n.t('acc.vouchers.openTreasury') }}
            </button>
          </div>
        </section>
        <app-list-footer [pager]="chequePager" [loading]="!chequePager.loaded()" />

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('acc.vouchers.register') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('acc.vouchers.registerHint') }}</p>
          </div>
          <div class="bo-panel__body">
            <nav class="bo-chips no-print" [attr.aria-label]="i18n.t('acc.vouchers.register')">
              <button
                type="button"
                [class.is-on]="source() === null"
                [attr.aria-current]="source() === null ? 'true' : null"
                (click)="setSource(null)"
              >
                {{ i18n.t('acc.vouchers.all') }}
              </button>
              @for (s of sources; track s) {
                <button
                  type="button"
                  [class.is-on]="source() === s"
                  [attr.aria-current]="source() === s ? 'true' : null"
                  (click)="setSource(s)"
                >
                  {{ i18n.t('acc.src.' + s) }}
                </button>
              }
            </nav>
            @if (source() === 'remittance') {
              <p class="bo-group__note">{{ i18n.t('acc.vouchers.remittanceNote') }}</p>
            }
            @if (source() === 'opening') {
              <p class="bo-group__note">{{ i18n.t('acc.openingNote') }}</p>
            }
            <!-- Beside the document chips: these narrow the entries too, on the server. -->
            @if (anyEntries()) {
              <div class="ui-toolbar no-print" role="search">
                <div class="ui-filter">
                  <label for="ac-en-rev">{{ i18n.t('acc.entry.reversal') }}</label>
                  <select id="ac-en-rev" [ngModel]="entryReversal()" (ngModelChange)="entryReversal.set($event)">
                    <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                    <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
                    <option ngValue="no">{{ i18n.t('filters.no') }}</option>
                  </select>
                </div>
                <div class="ui-filter">
                  <label for="ac-en-inter">{{ i18n.t('acc.entry.interBranch') }}</label>
                  <select id="ac-en-inter" [ngModel]="entryInterBranch()" (ngModelChange)="entryInterBranch.set($event)">
                    <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                    <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
                    <option ngValue="no">{{ i18n.t('filters.no') }}</option>
                  </select>
                </div>
                <div class="ui-filter">
                  <label for="ac-en-by">{{ i18n.t('treasury.by') }}</label>
                  <select id="ac-en-by" [ngModel]="entryBy()" (ngModelChange)="entryBy.set($event)">
                    <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                    @for (who of entryAuthors(); track who) {
                      <option [ngValue]="who">{{ who }}</option>
                    }
                  </select>
                </div>
                @if (entriesFiltered()) {
                  <div class="ui-toolbar__end">
                    <button type="button" class="btn-ghost" (click)="clearEntryFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                  </div>
                }
              </div>
            }
          </div>
          <div class="bo-scroll bo-scroll--tall">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('receipt.date') }}</th>
                  <th>{{ i18n.t('acc.entry.document') }}</th>
                  <th>{{ i18n.t('backoffice.tabs.branches') }}</th>
                  <th>{{ i18n.t('treasury.by') }}</th>
                  <th class="num">{{ i18n.t('pay.amount') }}</th>
                  <th>{{ i18n.t('common.actions') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (e of entryPager.rows(); track e.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('receipt.date')">{{ i18n.dateTime(e.entryDate) }}</td>
                    <td [attr.data-label]="i18n.t('acc.entry.document')">
                      <span class="bo-state">{{ sourceLabel(e.sourceType) }}</span>
                      @if (e.isReversal) {
                        <span class="bo-state bo-state--warn">{{ i18n.t('acc.entry.reversal') }}</span>
                      }
                      @if (e.isInterBranch) {
                        <span class="bo-state bo-state--lead">{{ i18n.t('acc.entry.interBranch') }}</span>
                      }
                      @if (e.description) {
                        <span class="bo-soft">{{ e.description }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('backoffice.tabs.branches')">{{ e.branchName || branchName(e.branchId) }}</td>
                    <td [attr.data-label]="i18n.t('treasury.by')">{{ e.createdBy || '—' }}</td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(e.total) }}</td>
                    <td class="bo-cell-actions no-print" [attr.data-label]="i18n.t('common.actions')">
                      <button type="button" class="btn-ghost bo-small" (click)="openEntry(e.id)">{{ i18n.t('acc.drill') }}</button>
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="6" class="bo-empty">
                      @if (entryPager.loading() || !entryPager.loaded()) {
                        <strong>{{ i18n.t('common.loading') }}</strong>
                        <span class="bo-loading__rows" aria-hidden="true"></span>
                      } @else if (anyEntries() && entriesFiltered()) {
                        <app-table-state kind="noResults" (clear)="clearEntryFilters()" />
                      } @else {
                        <strong>{{ i18n.t('acc.empty') }}</strong>
                        {{ i18n.t('acc.emptyHint') }}
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
        <app-list-footer [pager]="entryPager" [loading]="!entryPager.loaded()" />
      }
    </div>

    <!-- ====================== the drill-down: one journal entry ====================== -->
    @if (entry(); as e) {
      <div class="bo-backdrop no-print" (click)="closeEntry()">
        <div class="bo-modal" role="dialog" aria-modal="true" (click)="$event.stopPropagation()">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ sourceLabel(e.sourceType) }}</h2>
            <p class="bo-meta">
              <span>{{ i18n.dateTime(e.entryDate) }}</span>
              <span>{{ e.branchName || branchName(e.branchId) }}</span>
              @if (e.createdBy) {
                <span>{{ e.createdBy }}</span>
              }
            </p>
          </div>
          <div class="bo-panel__body">
            <div class="bo-states">
              @if (e.isReversal) {
                <span class="bo-state bo-state--warn">{{ i18n.t('acc.entry.reversal') }}</span>
              }
              @if (e.isInterBranch) {
                <span class="bo-state bo-state--lead">{{ i18n.t('acc.entry.interBranch') }}</span>
              }
              <span class="bo-state">{{ i18n.t('acc.entry.ref') }}<span class="mono">{{ shortId(e.sourceId) }}</span></span>
            </div>
            @if (e.description) {
              <p class="bo-group__note">{{ e.description }}</p>
            }
            @if (e.reversalOfEntryId) {
              <!-- A mistake in a closed month is corrected in the current one, naming the original (COR-E01,
                   owner 2026-11-05): the reversal says which day it corrects, in the company's dates. -->
              @if (e.reversalOfEntryDate) {
                <p class="bo-hint">{{ i18n.t('acc.entry.corrects', { date: i18n.date(e.reversalOfEntryDate) }) }}</p>
              }
              <div class="bo-actions bo-actions--start">
                <button type="button" class="btn-ghost bo-small" (click)="openEntry(e.reversalOfEntryId!)">
                  {{ i18n.t('acc.entry.openOriginal') }}
                </button>
              </div>
            }
          </div>
          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('acc.account') }}</th>
                  <th>{{ i18n.t('common.meaning') }}</th>
                  <th class="num">{{ i18n.t('acc.debit') }}</th>
                  <th class="num">{{ i18n.t('acc.credit') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (line of e.lines; track line.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('acc.account')">
                      @if (line.folded) {
                        <span class="bo-soft">{{ i18n.t('acc.entry.otherBranches') }}</span>
                      } @else {
                        <span class="mono">{{ line.accountCode }}</span> {{ lineAccountName(line.accountId, line.accountNameFr) }}
                        @if (line.memo) {
                          <span class="bo-soft">{{ line.memo }}</span>
                        }
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('common.meaning')">
                      @if (!line.folded) {
                        <span class="bo-state">{{ nature(line.accountType) }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('acc.debit')">{{ line.debit ? i18n.money(line.debit) : '' }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('acc.credit')">{{ line.credit ? i18n.money(line.credit) : '' }}</td>
                  </tr>
                }
              </tbody>
              <tfoot>
                <tr>
                  <td [attr.data-label]="i18n.t('acc.account')">{{ i18n.t('common.total') }}</td>
                  <td></td>
                  <td class="num mono" [attr.data-label]="i18n.t('acc.debit')">{{ i18n.money(e.total) }}</td>
                  <td class="num mono" [attr.data-label]="i18n.t('acc.credit')">{{ i18n.money(e.total) }}</td>
                </tr>
              </tfoot>
            </table>
          </div>

          @if (sale(); as d) {
            <div class="bo-panel__body">
              <h3 class="bo-group__title">{{ i18n.t('acc.entry.sourceSale') }}</h3>
              <div class="bo-states">
                <span class="bo-state bo-state--lead mono">{{ d.reference }}</span>
                <span class="bo-state">{{ i18n.dateTime(d.soldAtUtc) }}</span>
                <span class="bo-state">{{ d.cashierName }}</span>
                <span class="bo-state mono">{{ i18n.money(d.grandTotalTtc) }}</span>
              </div>
            </div>
          }

          <div class="bo-panel__body">
            @if (e.isReversal) {
              <p class="bo-group__note">{{ i18n.t('acc.entry.cannotReverse') }}</p>
            } @else if (partOnly(e)) {
              <!-- Booked at another branch: only that branch reverses it (P0.1b). -->
              <p class="bo-group__note">{{ i18n.t('acc.entry.otherBranchesNote') }}</p>
            } @else {
              <div class="bo-group">
                <h3 class="bo-group__title">{{ i18n.t('acc.entry.reverse') }}</h3>
                <p class="bo-group__note">{{ i18n.t('acc.entry.reverseHint') }}</p>
                <div class="bo-field" [class.is-invalid]="reasonTouched && !reason.trim()">
                  <label for="ac-reason">
                    {{ i18n.t('acc.entry.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="ac-reason"
                    [(ngModel)]="reason"
                    [attr.aria-invalid]="reasonTouched && !reason.trim() ? 'true' : null"
                    [attr.aria-describedby]="reasonTouched && !reason.trim() ? 'ac-reason-error' : null"
                    (blur)="reasonTouched = true"
                  />
                  @if (reasonTouched && !reason.trim()) {
                    <span id="ac-reason-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
              </div>
            }
          </div>

          <div class="bo-panel__foot">
            <button type="button" class="btn-ghost" (click)="closeEntry()">{{ i18n.t('common.close') }}</button>
            @if (e.sourceType === 'sale') {
              <button type="button" class="btn-ghost" [disabled]="busy()" (click)="loadSale(e.sourceId)">
                {{ i18n.t('acc.entry.openSale') }}
              </button>
            }
            @if (!e.isReversal && !partOnly(e)) {
              <button type="button" class="btn-danger" [disabled]="busy() || !reason.trim()" (click)="reverse(e)">
                {{ i18n.t('acc.entry.reverse') }}
              </button>
            }
          </div>
        </div>
      </div>
    }
  `,
  styleUrls: ['./backoffice-ui.scss', '../../ui/ui-patterns.scss'],
})
export class BackofficeAccountingComponent implements OnInit {
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  private readonly ledger = inject(LedgerService);
  private readonly confirm = inject(ConfirmService);
  private readonly admin = inject(AdminService);
  private readonly regional = inject(RegionalService);
  private readonly printer = inject(PrintService);
  private readonly pages = inject(FinancePagesApi);

  /** The cheque worklist and its actions live in the treasury tab; this asks the shell to go there. */
  @Output() readonly goTreasury = new EventEmitter<void>();

  readonly maxDays = MAX_PERIOD_DAYS;
  readonly sources = ACCOUNTING_SOURCES;

  private readonly knownSources = new Set<string>(this.sources);

  /**
   * The chip the route opened this section on (core/nav.ts). Set, the sidebar is the navigation
   * and this component's own chip strip is not drawn; unset, it behaves as it always did.
   */
  readonly page = input<Section | null>(null);
  /** With `page` = statement: the account to open it on (a trial-balance or P&L row's link). */
  readonly account = input<string | null>(null);
  /**
   * Asked of the route host when a row opens another report: the URL follows, so Back returns
   * to the report the row was on.
   */
  readonly pageChange = output<{ view: Section; account: string | null }>();
  readonly section = signal<Section>('trial');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);

  readonly branches = signal<AdminBranch[]>([]);
  readonly accounts = signal<LedgerAccount[]>([]);
  readonly pl = signal<ProfitAndLoss | null>(null);
  readonly balance = signal<BalanceSheet | null>(null);
  readonly entry = signal<LedgerEntry | null>(null);
  readonly sale = signal<AdminSaleDetail | null>(null);
  /** The branches and accounts are read, the route's section and account are set: the lists may be asked. */
  private readonly started = signal(false);

  /** The quick periods a statement is usually read over. */
  readonly presets: readonly { key: Preset; label: string }[] = [
    { key: 'month', label: 'reports.thisMonth' },
    { key: 'lastMonth', label: 'acc.lastMonth' },
    { key: 'quarter', label: 'acc.thisQuarter' },
    { key: 'year', label: 'acc.thisYear' },
  ];
  readonly preset = signal<Preset>('month');
  readonly branchId = signal<string | null>(null);
  readonly accountId = signal<string | null>(null);
  readonly source = signal<string | null>(null);
  reason = '';
  /** A required field only complains once it has been left, not while it is being filled. */
  reasonTouched = false;

  private readonly from = signal<Date>(startOfMonth(new Date()));
  /** The last day the report covers, inclusive on screen; sent to the server as an exclusive bound. */
  private readonly to = signal<Date>(startOfDay(new Date()));

  readonly fromDate = computed(() => toInputDate(this.from()));
  readonly toDate = computed(() => toInputDate(this.to()));
  readonly tooWide = computed(() => this.periodDays() > MAX_PERIOD_DAYS);

  private readonly branchNames = computed(() => new Map(this.branches().map((b) => [b.id, this.branchLabel(b)])));
  private readonly accountsById = computed(() => new Map(this.accounts().map((a) => [a.id, a])));
  private readonly accountsByCode = computed(() => new Map(this.accounts().map((a) => [a.code, a])));

  /** The bars read against the largest result in either direction, so a loss is as visible as a profit. */
  private readonly branchTop = computed(() => Math.max(0, ...(this.pl()?.byBranch ?? []).map((b) => Math.abs(b.netProfit))));

  // ---------- pages and filters (owner, 2026-11-17) ----------
  // Ten rows to begin with; the footer offers 20, 50 or 100. Every page, size or filter change is a
  // request (owner: "paginate in backend, not frontend"); the totals, the running balance and the
  // tiles come from the server, over every row. A table with a totals row or a running balance pages
  // but takes no filter: the trial balance and the account statement.

  /** The period as the server reads it: from the first day, to the day after the last (exclusive). */
  private readonly period = computed(() => ({
    from: this.from().toISOString(),
    to: this.exclusiveEnd().toISOString(),
    branchId: this.branchId(),
  }));

  private readonly trialOn = computed(() => this.started() && this.section() === 'trial' && !this.tooWide());
  private readonly statementOn = computed(
    () => this.started() && this.section() === 'statement' && this.accountId() !== null && !this.tooWide(),
  );
  private readonly vouchersOn = computed(() => this.started() && this.section() === 'vouchers');

  readonly trialPager = createServerPager(
    (page) => this.pages.trialBalancePage({ skip: page.skip, take: page.take, ...page.filters }),
    { key: 'accounting.trial', filters: () => this.period(), enabled: () => this.trialOn() },
  );
  /** The trial balance's totals and whether it balances: the whole balance's, whatever page is shown. */
  readonly trial = computed(() => this.trialPager.summary());

  readonly statementPager = createServerPager(
    (page) => this.pages.accountStatementPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'accounting.statement',
      filters: () => ({ ...this.period(), accountId: this.accountId() ?? '' }),
      enabled: () => this.statementOn(),
    },
  );
  /** The open account's statement figures, once the server has answered for that account. */
  readonly statement = computed(() => {
    const s = this.statementPager.summary();
    const id = this.accountId();
    return s && id && s.accountId.toLowerCase() === id.toLowerCase() ? s : null;
  });
  readonly statementDebit = computed(() => this.statement()?.totalDebit ?? 0);
  readonly statementCredit = computed(() => this.statement()?.totalCredit ?? 0);

  /** The opening balance comes before the first movement: on the first page only. */
  readonly statementAtStart = computed(() => this.statementPager.index() === 0);

  readonly chequeStatus = signal<'all' | PendingPayment['status']>('all');
  /** `yyyy-MM-dd`, the company's days; empty is no bound. */
  readonly chequeFrom = signal('');
  readonly chequeTo = signal('');

  readonly chequesFiltered = computed(() => this.chequeStatus() !== 'all' || !!this.chequeFrom() || !!this.chequeTo());

  /**
   * The instruments on paper (a tender that needs clearing, or instrument details), pending first, then bounced, then
   * collected, by due date: the server keeps the register's rule and computes the tiles over all of it.
   */
  readonly chequePager = createServerPager(
    (page) => this.pages.chequesPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'accounting.cheques',
      filters: () => ({ status: this.chequeStatus(), dueFrom: this.chequeFrom(), dueTo: this.chequeTo() }),
      enabled: () => this.vouchersOn(),
    },
  );
  /** Whether there is any instrument at all, before the worklist's filters. */
  readonly anyCheques = computed(() => (this.chequePager.summary()?.all ?? 0) > 0);

  clearChequeFilters(): void {
    this.chequeStatus.set('all');
    this.chequeFrom.set('');
    this.chequeTo.set('');
  }

  readonly entryReversal = signal<'all' | 'yes' | 'no'>('all');
  readonly entryInterBranch = signal<'all' | 'yes' | 'no'>('all');
  /** Who recorded the entry, as the column shows it; `all` is anyone. */
  readonly entryBy = signal<string>('all');

  readonly entriesFiltered = computed(
    () => this.entryReversal() !== 'all' || this.entryInterBranch() !== 'all' || this.entryBy() !== 'all',
  );

  readonly entryPager = createServerPager(
    (page) => this.pages.journalPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'accounting.journal',
      filters: () => ({
        ...this.period(),
        sourceType: this.source(),
        reversal: this.entryReversal(),
        interBranch: this.entryInterBranch(),
        by: this.entryBy(),
      }),
      enabled: () => this.vouchersOn(),
    },
  );
  /** Whether the period has any entry of the chosen document, before the register's own filters. */
  readonly anyEntries = computed(() => (this.entryPager.summary()?.all ?? 0) > 0);
  /** Who recorded the period's entries, so the filter offers only them. */
  readonly entryAuthors = computed(() => [...(this.entryPager.summary()?.authors ?? [])].sort((a, b) => a.localeCompare(b)));

  clearEntryFilters(): void {
    this.entryReversal.set('all');
    this.entryInterBranch.set('all');
    this.entryBy.set('all');
  }

  /** The lists of a section that are being shown, so their state reads as the section's. */
  private pagersOf(section: Section): { loading(): boolean; loaded(): boolean; error(): unknown; reload(): Promise<void> }[] {
    if (section === 'trial') return this.trialOn() ? [this.trialPager] : [];
    if (section === 'statement') return this.statementOn() ? [this.statementPager] : [];
    if (section === 'vouchers') return this.vouchersOn() ? [this.chequePager, this.entryPager] : [];
    return [];
  }

  /** Something of the open section is on its way: a report, or a page of one of its lists. */
  readonly working = computed(() => this.busy() || this.pagersOf(this.section()).some((p) => p.loading()));

  /** A list of the open section could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pagersOf(this.section()).map((p) => p.error()).find((x) => !!x);
    return e ? adminErrorMessage(e) : null;
  });

  readonly scopeNote = computed(() => (this.branchId() === null ? 'acc.groupNote' : 'acc.branchNote'));

  /**
   * Whether the open section already has something on screen. The vouchers section always
   * draws its two tables, and each says in its own empty row whether it is empty or waiting;
   * the four report sections draw nothing at all until their figures arrive, which is the one
   * case that needs a loading state of its own.
   */
  readonly sectionReady = computed(() => {
    switch (this.section()) {
      case 'trial':
        return this.trial() !== null;
      case 'pl':
        return this.pl() !== null;
      case 'balance':
        return this.balance() !== null;
      case 'statement':
        // With no account chosen the screen is showing its picker prompt, not an empty report.
        return this.statement() !== null || this.accountId() === null;
      default:
        return true;
    }
  });

  async ngOnInit(): Promise<void> {
    await this.guard(async () => {
      const [branches, accounts] = await Promise.all([this.admin.listBranches(), this.ledger.listAccounts()]);
      this.branches.set(branches);
      // A non-admin reads their branch's books, never the group's.
      this.branchId.set(this.lock.lockedBranchId());
      this.accounts.set(accounts);
    });
    const page = this.page();
    if (page) this.section.set(page);
    const account = this.account();
    if (page === 'statement' && account) this.accountId.set(account);
    this.started.set(true);
    await this.load();
  }

  // ---------- labels ----------

  branchLabel(b: AdminBranch): string {
    return this.localized(b) || b.nameFr;
  }

  branchName(id: string): string {
    return this.branchNames().get(id) ?? '—';
  }

  accountName(x: { nameAr: string; nameFr: string; nameEn: string; code?: string }): string {
    return this.localized(x) || x.code || '';
  }

  /** An entry line only carries the French name; the chart has all three. */
  lineAccountName(accountId: string, fallback: string): string {
    const account = this.accountsById().get(accountId);
    return account ? this.accountName(account) : fallback;
  }

  /**
   * What an account is, in a word the owner uses. The formal term is on the account's own code
   * for whoever wants it; this column is for the contractor reading his own books.
   */
  nature(type: LedgerAccountType): string {
    return this.i18n.t(`acc.nature.${type.toLowerCase()}`);
  }

  abs(value: number): number {
    return Math.abs(value);
  }

  /**
   * Whether this account's balance has landed on the side it does not belong on, which is one
   * of the two things a trial balance exists to show.
   *
   * <p>The report already returns each balance <b>signed in that account's own direction</b> —
   * debits less credits for an asset or an expense, credits less debits for a liability,
   * equity or income account. So a negative figure always means the wrong side, whatever the
   * account is, and nothing here needs to know which way round a type runs.</p>
   *
   * <p>This has been wrong twice in opposite directions. It first tested for a negative figure
   * on the assumption that the report returned debits minus credits throughout, which flagged
   * every liability and income account — four rows in ten, which teaches a reader to ignore
   * the mark. Correcting it by branching on the account type then flagged every one of those
   * same accounts that was <i>normal</i>, because the premise about the report was the part
   * that was wrong. A sales account carrying a debit balance is the row that wants looking
   * at, and it is the one this now marks.</p>
   */
  wrongSide(row: { type: LedgerAccountType; balance: number }): boolean {
    return row.balance < 0;
  }

  /**
   * Which side the balance sits on, in the reader's words.
   *
   * <p>A positive figure sits on the account's own side — debit for what is owned or spent,
   * credit for what is owed, earned or put in. A negative one sits on the other, which is
   * exactly what {@link wrongSide} marks.</p>
   */
  balanceSide(row: { type: LedgerAccountType; balance: number }): string {
    const naturallyDebit = row.type === 'Asset' || row.type === 'Expense';
    const onItsOwnSide = row.balance >= 0;
    return this.i18n.t(naturallyDebit === onItsOwnSide ? 'acc.debit' : 'acc.credit');
  }

  /**
   * A document type in words. An unknown one prints its raw key rather than a missing
   * translation key: modules are still being added, and `sourceType` is open-ended.
   */
  sourceLabel(type: string): string {
    return this.knownSources.has(type) ? this.i18n.t(`acc.src.${type}`) : type;
  }

  /** Enough of the document id to recognise it, without a 36-character string in a cell. */
  shortId(id: string): string {
    return id.slice(0, 8);
  }

  day(value: string | null): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  share(value: number): number {
    const top = this.branchTop();
    if (!(top > 0)) return 0;
    return Math.round((Math.abs(value) / top) * 100);
  }

  statusKey(status: PendingPayment['status']): string {
    return status === 'Pending' ? 'pending' : status === 'Confirmed' ? 'collected' : 'bounced';
  }

  chequeClass(status: PendingPayment['status']): string {
    return status === 'Bounced' ? 'bo-state--danger' : status === 'Confirmed' ? 'bo-state--ok' : 'bo-state--warn';
  }

  /** How many instruments are in a state, over the whole register (the server's count). */
  chequeCount(status: PendingPayment['status']): number {
    const s = this.chequePager.summary();
    if (!s) return 0;
    return status === 'Pending' ? s.pendingCount : status === 'Confirmed' ? s.confirmedCount : s.bouncedCount;
  }

  /** What the instruments in a state add up to, over the whole register (the server's sum). */
  chequeTotal(status: PendingPayment['status']): number {
    const s = this.chequePager.summary();
    if (!s) return 0;
    return status === 'Pending' ? s.pendingTotal : status === 'Confirmed' ? s.confirmedTotal : s.bouncedTotal;
  }

  // ---------- filters ----------

  async show(section: Section): Promise<void> {
    this.section.set(section);
    this.error.set(null);
    this.done.set(null);
    await this.refresh();
  }

  applyPreset(preset: Preset): void {
    this.preset.set(preset);
    const now = new Date();
    if (preset === 'month') {
      this.from.set(startOfMonth(now));
      this.to.set(startOfDay(now));
    } else if (preset === 'lastMonth') {
      const start = new Date(now.getFullYear(), now.getMonth() - 1, 1);
      this.from.set(start);
      this.to.set(new Date(now.getFullYear(), now.getMonth(), 0));
    } else if (preset === 'quarter') {
      this.from.set(new Date(now.getFullYear(), Math.floor(now.getMonth() / 3) * 3, 1));
      this.to.set(startOfDay(now));
    } else {
      this.from.set(new Date(now.getFullYear(), 0, 1));
      this.to.set(startOfDay(now));
    }
    void this.load();
  }

  setFrom(value: string): void {
    if (!value) return;
    this.from.set(startOfDay(parseInputDate(value)));
    void this.load();
  }

  setTo(value: string): void {
    if (!value) return;
    this.to.set(startOfDay(parseInputDate(value)));
    void this.load();
  }

  setBranch(value: string | null): void {
    this.branchId.set(value || null);
    void this.load();
  }

  setAccount(value: string | null): void {
    this.accountId.set(value || null);
    void this.load();
  }

  setSource(value: string | null): void {
    this.source.set(value);
    void this.load();
  }

  openStatement(accountId: string): void {
    if (this.page()) {
      this.pageChange.emit({ view: 'statement', account: accountId });
      return;
    }
    this.accountId.set(accountId);
    this.section.set('statement');
    void this.load();
  }

  /** A P&L line carries a code, not an id; the chart turns one into the other. */
  openStatementByCode(code: string): void {
    const account = this.accountsByCode().get(code);
    if (account) this.openStatement(account.id);
  }

  /**
   * Whether the open report can go to paper: the server refuses a report window wider than its
   * ceiling (the journal is a list and has none, as on screen), and a statement needs its account.
   */
  readonly printable = computed(() => {
    const section = this.section();
    if (section !== 'balance' && section !== 'vouchers' && this.tooWide()) return false;
    return section !== 'statement' || this.accountId() !== null;
  });

  /**
   * The open report on paper, in its own tab (print-reports.ts). It used to print this screen,
   * which put the sidebar, the filters' state and the screen's cards on the sheet and never said
   * which company, period or branch the figures were for. The tab reads the figures again with
   * exactly these filters, in the interface language unless the clerk picks another there.
   */
  print(): void {
    if (!this.printable()) return;
    const opts = {
      lang: this.i18n.lang(),
      from: this.from().toISOString(),
      to: this.exclusiveEnd().toISOString(),
      branch: this.branchId(),
    };
    switch (this.section()) {
      case 'trial':
        this.printer.open('report', 'trial-balance', opts);
        break;
      case 'pl':
        this.printer.open('report', 'profit-loss', opts);
        break;
      case 'balance':
        // At one day: the screen's "as of" date, sent as the exclusive end the server reads.
        this.printer.open('report', 'balance-sheet', { ...opts, from: undefined });
        break;
      case 'statement':
        this.printer.open('report', 'account-statement', { ...opts, account: this.accountId() });
        break;
      case 'vouchers':
        // The book itself; the cheque worklist above it is the treasury's, not the journal's.
        this.printer.open('report', 'journal', { ...opts, source: this.source() });
        break;
    }
  }

  // ---------- loading ----------

  /**
   * The open report. The profit and loss and the balance sheet are read whole; the trial balance, the statement and
   * the vouchers' two lists follow their period, branch and filters through their pagers, a page at a time.
   */
  async load(): Promise<void> {
    // The server refuses a window wider than its ceiling; saying so here beats a raw 400.
    if (this.tooWide() && this.section() !== 'balance' && this.section() !== 'vouchers') return;

    const period = this.period();
    await this.guard(async () => {
      if (this.section() === 'pl') {
        this.pl.set(await this.ledger.profitAndLoss(period));
      } else if (this.section() === 'balance') {
        this.balance.set(
          await this.ledger.balanceSheet({ asOf: this.exclusiveEnd().toISOString(), branchId: this.branchId() }),
        );
      }
    });
  }

  /** Refresh: the open report, and the page on screen of each of its lists, asked again. */
  async refresh(): Promise<void> {
    await Promise.all([this.load(), ...this.pagersOf(this.section()).filter((p) => p.loaded()).map((p) => p.reload())]);
  }

  // ---------- the drill-down ----------

  /** An entry this branch holds only part of: the other branches' lines come folded (P0.1b). */
  partOnly(e: LedgerEntry): boolean {
    return e.lines.some((l) => l.folded);
  }

  async openEntry(id: string): Promise<void> {
    this.sale.set(null);
    this.reason = '';
    this.reasonTouched = false;
    await this.guard(async () => this.entry.set(await this.ledger.getEntry(id)));
  }

  closeEntry(): void {
    this.entry.set(null);
    this.sale.set(null);
    this.reason = '';
    this.reasonTouched = false;
  }

  async loadSale(id: string): Promise<void> {
    await this.guard(async () => this.sale.set(await this.admin.getSale(id)));
  }

  async reverse(e: LedgerEntry): Promise<void> {
    const reason = this.reason.trim();
    // Nothing happens on a blank reason, so the field has to say why rather than leave the
    // button looking broken.
    this.reasonTouched = true;
    if (!reason) return;
    const yes = await this.confirm.ask({
      title: this.i18n.t('acc.entry.reverseTitle'),
      message: this.i18n.t('acc.entry.reverseMessage', { v: this.i18n.money(e.total), reason }),
      confirmLabel: this.i18n.t('acc.entry.reverse'),
      cancelLabel: this.i18n.t('confirm.keep'),
      danger: true,
    });
    if (!yes) return;
    await this.guard(async () => {
      await this.ledger.reverseEntry(e.id, reason);
      this.done.set(this.i18n.t('acc.entry.reversed'));
      this.closeEntry();
      await this.refresh();
    });
  }

  // ---------- plumbing ----------

  private periodDays(): number {
    return (this.exclusiveEnd().getTime() - this.from().getTime()) / DAY;
  }

  /** The server filters `entryDate < to`, so the day the user picked has to be included whole. */
  private exclusiveEnd(): Date {
    const end = new Date(this.to());
    end.setDate(end.getDate() + 1);
    return end;
  }

  private localized(x: { nameAr: string; nameFr: string; nameEn: string }): string {
    const lang = this.i18n.lang();
    const name = lang === 'ar' ? x.nameAr : lang === 'en' ? x.nameEn : x.nameFr;
    return name || x.nameFr || x.nameEn || x.nameAr || '';
  }

  private async guard(action: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    try {
      await action();
      this.error.set(null);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

function startOfDay(date: Date): Date {
  const d = new Date(date);
  d.setHours(0, 0, 0, 0);
  return d;
}

function startOfMonth(date: Date): Date {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

function toInputDate(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** Parses "yyyy-MM-dd" as a local date: `new Date(value)` would shift the day east of UTC. */
function parseInputDate(value: string): Date {
  const [year, month, day] = value.split('-').map(Number);
  return new Date(year, month - 1, day);
}
