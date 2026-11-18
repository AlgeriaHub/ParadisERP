import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminService, problemCode } from '../../../core/admin.service';
import { adminErrorMessage } from '../../../core/error-message';
import { AuthService } from '../../../core/auth.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { localizedName } from '../../../core/multilingual';
import { formatNumber } from '../../../core/number-format';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest } from '../../../ui/confirm-requests';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PracticeLine } from '../manufacturing/costing-settings-api.service';
import { MonthInputComponent } from '../payroll/month-input.component';
import { parsePeriod } from '../payroll/payroll-run-view';
import { FinancePagesApi } from '../finance/finance-pages.api';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { FixedAssetSettingsComponent } from './fixed-asset-settings.component';
import { FixedAssetDepreciation, FixedAssetPool, FixedAssetsApi } from './fixed-assets-api.service';

type Tab = 'register' | 'depreciation' | 'settings';
const TABS: readonly Tab[] = ['register', 'depreciation', 'settings'];

/** The month poster's refusals (shared with the other practice ledgers) and the register's own, in the reader's language. */
const REFUSALS: Readonly<Record<string, string>> = {
  register_off: 'fa.error.registerOff',
  before_posting_start: 'fa.error.beforeStart',
  month_not_ended: 'maintLedger.error.monthNotEnded',
  checks_not_passed: 'maintLedger.error.checks',
  already_posted: 'maintLedger.error.alreadyPosted',
  previous_month_not_posted: 'maintLedger.error.previous',
  later_month_posted: 'maintLedger.error.later',
  not_posted: 'maintLedger.error.notPosted',
  post_needs_every_branch: 'maintLedger.error.everyBranch',
  books_closed: 'maintLedger.error.booksClosed',
  month_in_future: 'fa.error.future',
};

/**
 * Reports › Fixed assets (REN-12, Phase M7a, docs/accounting/fixed-assets.md): hire equipment as fixed assets. Three tabs —
 * the register (each pool, a hire type at a branch, with its units, cost, accumulated depreciation, net book value and life,
 * and its history); the month's depreciation as a practice ledger, posted by the accountant once the month has ended
 * (`accounting.manage`); and the settings with the account map and the switch-over. Everything is computed by the server on
 * read; until the accountant switches the treatment on and posts the switch-over, hire equipment stays in stock and the
 * register is empty. A branch user reads their own branch's pools (the server sets it).
 *
 * The register's pools, a pool's events and the month's entries are read a page at a time (owner, 2026-11-17: "paginate in
 * backend, not frontend"; FinancePagesApi): the filters run on the server, and the figures above each list come with it.
 */
@Component({
  selector: 'app-backoffice-fixed-assets',
  standalone: true,
  imports: [
    FormsModule,
    ...PAGE_CHROME,
    ListFooterComponent,
    MonthInputComponent,
    StatusComponent,
    TableStateComponent,
    FixedAssetSettingsComponent,
  ],
  template: `
    @if (tab() !== 'settings') {
      <ng-template appPageToolbar>
        <div class="ui-toolbar" role="search">
          <div class="ui-filter">
            <label for="fa-month">{{ i18n.t('mfgLedger.month') }}</label>
            <app-month-input id="fa-month" [ngModel]="month()" (ngModelChange)="setMonth($event)" />
          </div>
          @if (!lock.locked() && branches().length > 1) {
            <div class="ui-filter">
              <label for="fa-branch">{{ i18n.t('missingCost.branch') }}</label>
              <select id="fa-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
                <option [ngValue]="null">{{ i18n.t('reports.allBranches') }}</option>
                @for (b of branches(); track b.id) {
                  <option [ngValue]="b.id">{{ name(b) }}</option>
                }
              </select>
            </div>
          }
          <!-- The register's own filters (owner, 2026-11-17): they narrow the pools listed, never the figures above. -->
          @if (tab() === 'register' && register()?.poolCount) {
            <div class="ui-filter">
              <label for="fa-f-method">{{ i18n.t('fa.settings.method') }}</label>
              <select id="fa-f-method" [ngModel]="methodFilter()" (ngModelChange)="methodFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="StraightLine">{{ i18n.t('fa.method.short.StraightLine') }}</option>
                <option ngValue="DecliningBalance">{{ i18n.t('fa.method.DecliningBalance') }}</option>
              </select>
            </div>
            <div class="ui-filter">
              <label for="fa-f-done">{{ i18n.t('fa.filter.depreciated') }}</label>
              <select id="fa-f-done" [ngModel]="depreciatedFilter()" (ngModelChange)="depreciatedFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
                <option ngValue="no">{{ i18n.t('filters.no') }}</option>
              </select>
            </div>
          }
          <div class="ui-toolbar__end">
            @if (tab() === 'register' && poolsFiltered()) {
              <button type="button" class="btn-ghost" (click)="clearPoolFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
            }
            <button type="button" class="btn-ghost" [disabled]="working()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
          </div>
        </div>
      </ng-template>
    }

    <div class="bo-stack">
      <div class="ui-tabs" role="tablist" [attr.aria-label]="i18n.t('fa.navLabel')" (keydown)="onTabKey($event)">
        @for (t of tabs; track t) {
          <button type="button" class="ui-tab" role="tab" [id]="'fa-tab-' + t" [attr.aria-selected]="tab() === t"
            aria-controls="fa-panel" [attr.tabindex]="tab() === t ? 0 : -1" (click)="go(t)">
            {{ i18n.t('fa.tab.' + t) }}
          </button>
        }
      </div>

      <div id="fa-panel" role="tabpanel" [attr.aria-labelledby]="'fa-tab-' + tab()" class="bo-stack">
        @if (error() || listError()) {
          <p class="notice danger" role="alert">{{ error() || listError() }}</p>
        }

        @switch (tab()) {
          @case ('register') {
            @if (register(); as r) {
              @if (!r.active) {
                <p class="notice info" data-testid="fa-off">{{ i18n.t('fa.register.off') }}</p>
              } @else {
                <div class="ui-metrics">
                  <div class="ui-metric ui-metric--lead">
                    <span class="ui-metric__label">{{ i18n.t('fa.netBookValue') }}</span>
                    <strong class="ui-metric__value mono" data-testid="fa-nbv">{{ i18n.money(r.netBookValue) }}</strong>
                  </div>
                  <div class="ui-metric">
                    <span class="ui-metric__label">{{ i18n.t('fa.cost') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(r.cost) }}</strong>
                  </div>
                  <div class="ui-metric">
                    <span class="ui-metric__label">{{ i18n.t('fa.accumulated') }}</span>
                    <strong class="ui-metric__value mono">{{ i18n.money(r.accumulatedDepreciation) }}</strong>
                  </div>
                  <div class="ui-metric">
                    <span class="ui-metric__label">{{ i18n.t('fa.units') }}</span>
                    <strong class="ui-metric__value mono">{{ r.units }}</strong>
                  </div>
                </div>
                <p class="bo-hint">
                  {{ i18n.t(r.monthToDate ? 'fa.register.asAtToDate' : 'fa.register.asAt', { month: monthName(r.year, r.month) }) }}
                  · {{ i18n.t('fa.register.switchedIn', { month: r.switchMonth ? monthText(r.switchMonth) : '—' }) }}
                  · {{ r.postedThrough ? i18n.t('fa.register.postedThrough', { month: monthText(r.postedThrough) }) : i18n.t('fa.register.nothingPosted') }}
                </p>
              }
            }
            <section class="ui-surface" [attr.aria-busy]="working()" [attr.aria-label]="i18n.t('fa.tab.register')">
              @if (!poolPager.loaded() && !listError()) {
                <app-table-state kind="loading" />
              } @else if (!register()?.poolCount) {
                <app-table-state kind="empty" [message]="i18n.t('fa.register.empty')" />
              } @else if (!poolPager.total()) {
                <app-table-state kind="noResults" (clear)="clearPoolFilters()" />
              } @else {
                <div class="ui-scroll">
                  <table class="ui-table ui-table--cards">
                    <caption class="sr-only">{{ i18n.t('fa.tab.register') }}</caption>
                    <thead>
                      <tr>
                        <th>{{ i18n.t('fa.equipment') }}</th>
                        <th>{{ i18n.t('missingCost.branch') }}</th>
                        <th class="num">{{ i18n.t('fa.units') }}</th>
                        <th class="num">{{ i18n.t('fa.cost') }}</th>
                        <th class="num">{{ i18n.t('fa.accumulated') }}</th>
                        <th class="num">{{ i18n.t('fa.netBookValue') }}</th>
                        <th class="num">{{ i18n.t('fa.life') }}</th>
                        <th class="num">{{ i18n.t('fa.monthly') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (p of poolPager.rows(); track p.productId + p.branchId) {
                        <tr>
                          <td [attr.data-label]="i18n.t('fa.equipment')">
                            <button type="button" class="ui-link" (click)="open(p)">{{ poolName(p) }}</button>
                            <span class="ui-sub mono" dir="ltr">{{ p.sku }}</span>
                          </td>
                          <td [attr.data-label]="i18n.t('missingCost.branch')">{{ poolBranch(p) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('fa.units')">
                            {{ p.units }}
                            @if (p.units !== p.unitsInStock) {
                              <span class="ui-sub">{{ i18n.t('fa.register.inStock', { qty: p.unitsInStock }) }}</span>
                            }
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('fa.cost')">{{ i18n.money(p.cost) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('fa.accumulated')">{{ i18n.money(p.accumulatedDepreciation) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('fa.netBookValue')">{{ i18n.money(p.netBookValue) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('fa.life')">
                            {{ p.usefulLifeMonths === null ? '—' : i18n.t('fa.months', { n: p.usefulLifeMonths }) }}
                            <span class="ui-sub">{{ methodText(p) }}</span>
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('fa.monthly')">{{ i18n.money(p.monthlyCharge) }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </section>
            <app-list-footer [pager]="poolPager" [loading]="!poolPager.loaded()" />

            @if (history(); as h) {
              <section class="ui-surface fa-history" [attr.aria-label]="i18n.t('fa.history.title')" data-testid="fa-history">
                <div class="fa-history__head">
                  <h2 class="bo-panel__title">{{ i18n.t('fa.history.title') }} — {{ historyName(h) }} · {{ h.branchCode }}</h2>
                  <button type="button" class="btn-ghost" (click)="closeHistory()">{{ i18n.t('fa.history.close') }}</button>
                </div>
                @if (h.pool) {
                  <p class="bo-hint" data-testid="fa-method">{{ methodText(h.pool) }}</p>
                }
                @if (h.schedule?.length) {
                  <!-- The schedule year by year (declining balance, 2026-11-10): the switch to straight line said in words. -->
                  <h3 class="fa-history__sub">{{ i18n.t('fa.schedule.title') }}</h3>
                  <p class="bo-hint">{{ i18n.t('fa.schedule.lead') }}</p>
                  <div class="ui-scroll">
                    <table class="ui-table ui-table--cards" data-testid="fa-schedule">
                      <caption class="sr-only">{{ i18n.t('fa.schedule.title') }}</caption>
                      <thead>
                        <tr>
                          <th>{{ i18n.t('fa.schedule.year') }}</th>
                          <th class="num">{{ i18n.t('fa.schedule.opening') }}</th>
                          <th class="num">{{ i18n.t('fa.schedule.charge') }}</th>
                          @if (scheduleMoves()) {
                            <th class="num">{{ i18n.t('fa.schedule.movements') }}</th>
                          }
                          <th class="num">{{ i18n.t('fa.schedule.closing') }}</th>
                          <th>{{ i18n.t('fa.schedule.note') }}</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (y of h.schedule; track y.year) {
                          <tr [class.fa-switch-year]="y.switches">
                            <td class="mono" [attr.data-label]="i18n.t('fa.schedule.year')">{{ y.year }}</td>
                            <td class="num mono" [attr.data-label]="i18n.t('fa.schedule.opening')">{{ i18n.money(y.opening) }}</td>
                            <td class="num mono" [attr.data-label]="i18n.t('fa.schedule.charge')">{{ i18n.money(y.charge) }}</td>
                            @if (scheduleMoves()) {
                              <td class="num mono" [attr.data-label]="i18n.t('fa.schedule.movements')">{{ y.movements ? i18n.money(y.movements) : '' }}</td>
                            }
                            <td class="num mono" [attr.data-label]="i18n.t('fa.schedule.closing')">{{ i18n.money(y.closing) }}</td>
                            <td [attr.data-label]="i18n.t('fa.schedule.note')">
                              @if (y.switches) {
                                <app-status [tone]="toneOf('depreciationYear', 'switch')">{{ i18n.t('fa.schedule.switch') }}</app-status>
                              }
                              <span class="ui-sub">{{ i18n.t(y.projected ? 'fa.schedule.projected' : 'fa.schedule.computed') }}</span>
                            </td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                  <h3 class="fa-history__sub">{{ i18n.t('fa.history.events') }}</h3>
                }
                <!-- The events' own filters (owner, 2026-11-17): they narrow the rows below, never the schedule above. -->
                @if (h.lineCount) {
                  <div class="ui-toolbar" role="search">
                    @if (historyKinds().length > 1 || historyKind() !== 'all') {
                      <div class="ui-filter">
                        <label for="fa-h-kind">{{ i18n.t('fa.history.event') }}</label>
                        <select id="fa-h-kind" [ngModel]="historyKind()" (ngModelChange)="historyKind.set($event)">
                          <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                          @for (k of historyKinds(); track k) {
                            <option [ngValue]="k">{{ i18n.t('fa.kind.' + k) }}</option>
                          }
                        </select>
                      </div>
                    }
                    <div class="ui-filter">
                      <label for="fa-h-posted">{{ i18n.t('fa.history.ledger') }}</label>
                      <select id="fa-h-posted" [ngModel]="historyPosted()" (ngModelChange)="historyPosted.set($event)">
                        <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                        <option ngValue="yes">{{ i18n.t('fa.history.posted') }}</option>
                        <option ngValue="no">{{ i18n.t('fa.history.notPosted') }}</option>
                      </select>
                    </div>
                    @if (historyFiltered()) {
                      <div class="ui-toolbar__end">
                        <button type="button" class="btn-ghost" (click)="clearHistoryFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                      </div>
                    }
                  </div>
                }
                @if (h.lineCount && !historyPager.total()) {
                  <app-table-state kind="noResults" (clear)="clearHistoryFilters()" />
                } @else {
                  <div class="ui-scroll">
                    <table class="ui-table ui-table--cards">
                      <caption class="sr-only">{{ i18n.t('fa.history.title') }}</caption>
                      <thead>
                        <tr>
                          <th>{{ i18n.t('mfgLedger.day') }}</th>
                          <th>{{ i18n.t('fa.history.event') }}</th>
                          <th class="num">{{ i18n.t('fa.units') }}</th>
                          <th class="num">{{ i18n.t('fa.cost') }}</th>
                          <th class="num">{{ i18n.t('fa.history.depreciation') }}</th>
                          <th>{{ i18n.t('fa.history.ledger') }}</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (l of historyPager.rows(); track $index) {
                          <tr>
                            <td class="mono" [attr.data-label]="i18n.t('mfgLedger.day')">{{ i18n.date(l.atUtc) }}</td>
                            <td [attr.data-label]="i18n.t('fa.history.event')">
                              {{ i18n.t('fa.kind.' + l.kind) }}
                              @if (l.otherBranchCode || l.documentNumber) {
                                <span class="ui-sub">
                                  @if (l.otherBranchCode) { {{ l.otherBranchCode }} }
                                  @if (l.documentNumber) { <span class="mono" dir="ltr">{{ l.documentNumber }}</span> }
                                </span>
                              }
                              <!-- Phase M7d: a capitalised repair names the piece its job repaired. -->
                              @if (l.reference) {
                                <span class="ui-sub">{{ i18n.t('fa.history.piece', { reference: l.reference }) }}</span>
                              }
                            </td>
                            <td class="num mono" [attr.data-label]="i18n.t('fa.units')">{{ l.qty || '' }}</td>
                            <td class="num mono" [attr.data-label]="i18n.t('fa.cost')">{{ l.cost ? i18n.money(l.cost) : '' }}</td>
                            <td class="num mono" [attr.data-label]="i18n.t('fa.history.depreciation')">{{ l.depreciation ? i18n.money(l.depreciation) : '' }}</td>
                            <td [attr.data-label]="i18n.t('fa.history.ledger')">
                              <app-status [tone]="toneOf('costCheck', l.posted ? 'pass' : 'fail')">
                                {{ i18n.t(l.posted ? 'fa.history.posted' : 'fa.history.notPosted') }}
                              </app-status>
                            </td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                  <app-list-footer [pager]="historyPager" />
                }
              </section>
            }
          }

          @case ('depreciation') {
            @if (ledger(); as d) {
              @if (!d.active) {
                <p class="notice info">{{ i18n.t('fa.register.off') }}</p>
              } @else {
                <p class="notice">
                  {{ i18n.t('fa.depreciation.lead', { month: d.switchMonth ? monthText(d.switchMonth) : '—' }) }}
                  @if (d.monthToDate) {
                    · {{ i18n.t('mfgLedger.note.month_to_date') }}
                  }
                </p>
                <div class="ui-metrics">
                  <div class="ui-metric ui-metric--lead">
                    <span class="ui-metric__label">{{ i18n.t('fa.depreciation.total') }}</span>
                    <strong class="ui-metric__value mono" data-testid="fa-month-total">{{ i18n.money(d.totalDebit) }}</strong>
                  </div>
                  <div class="ui-metric">
                    <span class="ui-metric__label">{{ i18n.t('mfgLedger.entries') }}</span>
                    <strong class="ui-metric__value mono">{{ d.entryCount }}</strong>
                  </div>
                  <div class="ui-metric">
                    <span class="ui-metric__label">{{ i18n.t('mfgLedger.checksPassed') }}</span>
                    <strong class="ui-metric__value mono">{{ passed() }} / {{ d.checks.length }}</strong>
                  </div>
                </div>

                <section class="ui-surface fa-posting" [attr.aria-label]="i18n.t('mfgLedger.posting')">
                  <h2 class="bo-panel__title">{{ i18n.t('mfgLedger.posting') }}</h2>
                  @for (p of d.postings; track p.generation) {
                    <p>
                      {{ i18n.t('mfgLedger.posted', { who: p.postedBy ?? '—', date: i18n.dateTime(p.postedAtUtc), entries: p.entries, total: i18n.money(p.totalDebit) }) }}
                      @if (p.reversedAtUtc) {
                        · {{ i18n.t('mfgLedger.reversed', { who: p.reversedBy ?? '—', date: i18n.dateTime(p.reversedAtUtc) }) }}
                      }
                    </p>
                  } @empty {
                    <p class="bo-hint">{{ i18n.t('fa.depreciation.notPosted') }}</p>
                  }
                  @if (canManage()) {
                    <div class="fa-actions">
                      @if (live()) {
                        <button type="button" class="btn-secondary is-danger" [disabled]="busy()" (click)="reverse()">{{ i18n.t('mfgLedger.reverse') }}</button>
                      } @else {
                        <button type="button" class="btn-primary" data-testid="fa-post" [disabled]="busy() || d.monthToDate" (click)="post()">{{ i18n.t('mfgLedger.post') }}</button>
                      }
                    </div>
                  }
                </section>

                @if (d.unmappedSlots.length) {
                  <p class="notice warn" role="status">{{ i18n.t('mfgLedger.unmapped', { slots: unmappedNames() }) }}</p>
                }
              }
            }

            <section class="ui-surface" [attr.aria-busy]="working()" [attr.aria-label]="i18n.t('mfgLedger.entriesTitle')">
              @if (!ledgerPager.loaded() && !listError()) {
                <app-table-state kind="loading" />
              } @else if (!ledger()?.entryCount) {
                <app-table-state kind="empty" [message]="i18n.t(emptyLedgerKey())" />
              } @else {
                <div class="ui-scroll">
                  <table class="ui-table ui-table--cards">
                    <caption class="sr-only">{{ i18n.t('mfgLedger.entriesTitle') }}</caption>
                    <thead>
                      <tr>
                        <th>{{ i18n.t('mfgLedger.day') }}</th>
                        <th>{{ i18n.t('mfgLedger.entry') }}</th>
                        <th>{{ i18n.t('mfgLedger.account') }}</th>
                        <th class="num">{{ i18n.t('mfgLedger.debit') }}</th>
                        <th class="num">{{ i18n.t('mfgLedger.credit') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (e of ledgerPager.rows(); track $index) {
                        <tr>
                          <td class="mono" [attr.data-label]="i18n.t('mfgLedger.day')">{{ i18n.date(e.day) }}</td>
                          <td [attr.data-label]="i18n.t('mfgLedger.entry')">
                            {{ i18n.t('fa.kind.Depreciation') }}
                            <span class="ui-sub"><span class="mono" dir="ltr">{{ e.orderNumber }}</span> · {{ entryBranch(e) }}</span>
                          </td>
                          <td [attr.data-label]="i18n.t('mfgLedger.account')">
                            @for (l of e.lines; track $index) {
                              <div [class.fa-line--credit]="l.credit > 0">{{ accountLabel(l) }}</div>
                            }
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.debit')">
                            @for (l of e.lines; track $index) {
                              <div>{{ l.debit ? i18n.money(l.debit) : '' }}&nbsp;</div>
                            }
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfgLedger.credit')">
                            @for (l of e.lines; track $index) {
                              <div>{{ l.credit ? i18n.money(l.credit) : '' }}&nbsp;</div>
                            }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </section>
            <!-- Paged only (owner, 2026-11-17): the month is posted whole and its total is above; the branch is the toolbar's. -->
            <app-list-footer [pager]="ledgerPager" [loading]="!ledgerPager.loaded()" />

            @if (ledger()?.checks?.length) {
              <section class="ui-surface" [attr.aria-label]="i18n.t('mfgLedger.checks')">
                <div class="ui-scroll">
                  <table class="ui-table ui-table--cards">
                    <caption class="sr-only">{{ i18n.t('mfgLedger.checks') }}</caption>
                    <thead>
                      <tr>
                        <th>{{ i18n.t('mfgLedger.check') }}</th>
                        <th>{{ i18n.t('mfgLedger.result') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (c of ledger()!.checks; track c.code) {
                        <tr>
                          <td [attr.data-label]="i18n.t('mfgLedger.check')">{{ i18n.t('mfgLedger.checkName.' + c.code) }}</td>
                          <td [attr.data-label]="i18n.t('mfgLedger.result')">
                            <app-status [tone]="toneOf('costCheck', c.passed ? 'pass' : 'fail')">
                              {{ i18n.t(c.passed ? 'mfgLedger.pass' : 'mfgLedger.fail') }}
                            </app-status>
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              </section>
            }
          }

          @case ('settings') {
            <app-fixed-asset-settings />
          }
        }
      </div>
    </div>
  `,
  styles: [
    `
      .fa-line--credit {
        padding-inline-start: var(--sp-4);
      }
      .fa-posting,
      .fa-history {
        padding: var(--sp-4);
        display: grid;
        gap: var(--sp-2);
      }
      .fa-posting p {
        margin: 0;
      }
      .fa-actions,
      .fa-history__head {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: var(--sp-3);
        flex-wrap: wrap;
      }
      .fa-actions {
        justify-content: flex-end;
      }
      .fa-history__sub {
        margin: var(--sp-2) 0 0;
        font-size: var(--fs-card);
      }
      /* The year a declining-balance layer turns to straight line: a weight, beside the words that say it. */
      .fa-switch-year td {
        font-weight: 600;
      }
      td .ui-link {
        text-align: start;
      }
    `,
  ],
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class BackofficeFixedAssetsComponent implements OnInit {
  private readonly api = inject(FixedAssetsApi);
  private readonly admin = inject(AdminService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);
  private readonly chrome = inject(PageChrome, { optional: true });
  private readonly pages = inject(FinancePagesApi);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  readonly toneOf = toneOf;
  readonly tabs = TABS;

  readonly tab = signal<Tab>('register');
  readonly branches = signal<AdminBranch[]>([]);
  /** `YYYY-MM`. Empty until the first answer: the server says which month is the company's today. */
  readonly month = signal('');
  readonly branchId = signal<string | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  /** Posting and reversing a month are the accountant's: accounting.manage, checked again by the server. */
  readonly canManage = computed(() => mayDo(this.auth.currentUser(), 'accounting.manage'));
  readonly live = computed(() => (this.ledger()?.postings ?? []).some((p) => !p.reversedAtUtc));
  readonly passed = computed(() => this.ledger()?.checks.filter((c) => c.passed).length ?? 0);
  readonly unmappedNames = computed(() => (this.ledger()?.unmappedSlots ?? []).map((s) => this.i18n.t('fa.slot.' + s)).join(' · '));
  readonly emptyLedgerKey = computed(() => {
    const notes = this.ledger()?.notes ?? [];
    return notes.includes('before_switch_over') ? 'fa.depreciation.beforeSwitch' : 'fa.depreciation.empty';
  });
  /** Whether any year of the open schedule had events (purchases in, disposals out): the column shows only then. */
  readonly scheduleMoves = computed(() => (this.history()?.schedule ?? []).some((y) => y.movements !== 0));

  // ---------- pages and filters (owner, 2026-11-17) ----------
  // Ten rows to begin with; the footer offers 20, 50 or 100. Every page, size or filter change is a
  // request ("paginate in backend, not frontend"); the month and the branch are the lists' too.

  /** The month asked for; none until the first answer says which month is the company's today. */
  private readonly period = computed(() => {
    const p = parsePeriod(this.month());
    return { year: p?.year ?? null, month: p?.month ?? null };
  });

  readonly methodFilter = signal<'all' | 'StraightLine' | 'DecliningBalance'>('all');
  /** Fully depreciated: nothing left of the net book value. */
  readonly depreciatedFilter = signal<'all' | 'yes' | 'no'>('all');

  readonly poolsFiltered = computed(() => this.methodFilter() !== 'all' || this.depreciatedFilter() !== 'all');

  /** The register's pools, filtered by the server (the method as the life column says it; fully depreciated or not). */
  readonly poolPager = createServerPager(
    (page) => this.pages.fixedAssetRegisterPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'fixedAssets.register',
      filters: () => ({
        ...this.period(),
        branchId: this.branchId(),
        method: this.methodFilter(),
        depreciated: this.depreciatedFilter(),
      }),
      enabled: () => this.tab() === 'register',
    },
  );
  /** The register's figures: the whole register's, whatever the filters. */
  readonly register = computed(() => this.poolPager.summary());

  clearPoolFilters(): void {
    this.methodFilter.set('all');
    this.depreciatedFilter.set('all');
  }

  /** The month's depreciation entries: paged only, the month is posted whole. */
  readonly ledgerPager = createServerPager(
    (page) => this.pages.fixedAssetDepreciationPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'fixedAssets.depreciation',
      filters: () => ({ ...this.period(), branchId: this.branchId() }),
      enabled: () => this.tab() === 'depreciation',
    },
  );
  /** The month's totals, checks, accounts and postings. */
  readonly ledger = computed(() => this.ledgerPager.summary());

  /** A movement kind (`fa.kind.*`); `all` is any. */
  readonly historyKind = signal<string>('all');
  readonly historyPosted = signal<'all' | 'yes' | 'no'>('all');
  /** The pool whose history is open. */
  readonly historyTarget = signal<{ productId: string; branchId: string } | null>(null);

  readonly historyFiltered = computed(() => this.historyKind() !== 'all' || this.historyPosted() !== 'all');

  readonly historyPager = createServerPager(
    (page) => this.pages.fixedAssetHistoryPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'fixedAssets.history',
      filters: () => ({
        productId: this.historyTarget()?.productId ?? '',
        branchId: this.historyTarget()?.branchId ?? '',
        kind: this.historyKind(),
        posted: this.historyPosted(),
      }),
      enabled: () => this.historyTarget() !== null,
    },
  );
  /** The open pool's history, once the server has answered for that pool. */
  readonly history = computed(() => {
    const h = this.historyPager.summary();
    const target = this.historyTarget();
    const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
    return h && target && same(h.productId, target.productId) && same(h.branchId, target.branchId) ? h : null;
  });
  /** The kinds the open pool's history holds, so the filter offers only what is there. */
  readonly historyKinds = computed(() => this.history()?.kinds ?? []);

  clearHistoryFilters(): void {
    this.historyKind.set('all');
    this.historyPosted.set('all');
  }

  /** The lists the open tab shows. */
  private pagersOf(tab: Tab): { loading(): boolean; loaded(): boolean; error(): unknown; reload(): Promise<void> }[] {
    if (tab === 'register') return this.historyTarget() ? [this.poolPager, this.historyPager] : [this.poolPager];
    if (tab === 'depreciation') return [this.ledgerPager];
    return [];
  }

  /** Something of the open tab is on its way: an act, or a page of one of its lists. */
  readonly working = computed(() => this.busy() || this.pagersOf(this.tab()).some((p) => p.loading()));

  /** A list of the open tab could not be read, in the app's words. */
  readonly listError = computed(() => {
    const e = this.pagersOf(this.tab()).map((p) => p.error()).find((x) => !!x);
    return e ? this.message(e) : null;
  });

  constructor() {
    // The first answer says which month is the company's today; the toolbar takes it.
    effect(() => {
      const answered = this.tab() === 'depreciation' ? this.ledger() : this.register();
      if (!answered || this.month()) return;
      untracked(() => this.month.set(`${answered.year}-${String(answered.month).padStart(2, '0')}`));
    });
  }

  ngOnInit(): void {
    this.chrome?.description.set(this.i18n.t('fa.lead'));
    this.admin.listBranches().then((b) => this.branches.set(b), () => undefined);
    // The register loads through its pager, from its first page.
  }

  go(t: Tab): void {
    if (this.tab() === t) return;
    this.tab.set(t);
    this.error.set(null);
    // A list read before is asked again; one never read is asked by its pager.
    for (const pager of this.pagersOf(t)) if (pager.loaded()) void pager.reload();
  }

  /** Another month: the lists follow it, and an open history closes. */
  setMonth(value: string): void {
    this.month.set(value);
    this.historyTarget.set(null);
  }

  /** Another branch: the lists follow it, and an open history closes. */
  setBranch(value: string | null): void {
    this.branchId.set(value);
    this.historyTarget.set(null);
  }

  onTabKey(event: KeyboardEvent): void {
    const step = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    // The row mirrors under RTL, so the arrow follows what the reader sees.
    const rtl = document.documentElement.dir === 'rtl';
    const i = TABS.indexOf(this.tab());
    const next = TABS[(i + (rtl ? -step : step) + TABS.length) % TABS.length];
    this.go(next);
    queueMicrotask(() => document.getElementById('fa-tab-' + next)?.focus());
  }

  /** Refresh: the open tab's page asked again (the register's closes an open history, as before). */
  async reload(): Promise<void> {
    this.error.set(null);
    if (this.tab() === 'depreciation') await this.ledgerPager.reload();
    else if (this.tab() === 'register') {
      this.historyTarget.set(null);
      await this.poolPager.reload();
    }
  }

  open(p: FixedAssetPool): void {
    // Another pool's history starts unfiltered: its kinds are not the last one's.
    this.clearHistoryFilters();
    this.historyTarget.set({ productId: p.productId, branchId: p.branchId });
  }

  closeHistory(): void {
    this.historyTarget.set(null);
  }

  async post(): Promise<void> {
    await this.act('mfgLedger.post', (y, m) => this.api.post(y, m));
  }

  async reverse(): Promise<void> {
    await this.act('mfgLedger.reverse', (y, m) => this.api.reverse(y, m));
  }

  private async act(label: string, call: (year: number, month: number) => Promise<FixedAssetDepreciation>): Promise<void> {
    const d = this.ledger();
    if (!d || !(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t(label))))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await call(d.year, d.month);
      // The posting is the company's: the month read again, with the branch filter.
      await this.ledgerPager.reload();
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.busy.set(false);
    }
  }

  private message(e: unknown): string {
    const code = problemCode(e);
    return code && REFUSALS[code] ? this.i18n.t(REFUSALS[code]) : adminErrorMessage(e);
  }

  /** A month as mm/yyyy, the app's way. */
  monthName(year: number, month: number): string {
    return `${String(month).padStart(2, '0')}/${year}`;
  }

  /** `yyyy-MM` as mm/yyyy. */
  monthText(value: string): string {
    const [y, m] = value.split('-');
    return `${m}/${y}`;
  }

  name(b: AdminBranch): string {
    return localizedName(b, this.i18n.lang());
  }

  /** The method a pool is depreciated by, in words: straight line, or declining balance at its coefficient (and its switch). */
  methodText(p: FixedAssetPool): string {
    if (p.method !== 'DecliningBalance') return this.i18n.t('fa.method.short.StraightLine');
    const coefficient = formatNumber(p.decliningCoefficient ?? 0, this.i18n.lang(), { minimumFractionDigits: 1, maximumFractionDigits: 3 });
    return this.i18n.t(p.switchToStraightLine ? 'fa.method.short.decliningSwitch' : 'fa.method.short.declining', { coefficient });
  }

  poolName(p: FixedAssetPool): string {
    return localizedName(p, this.i18n.lang()) || p.sku;
  }

  historyName(h: { nameAr: string; nameFr: string; nameEn: string; sku: string }): string {
    return localizedName(h, this.i18n.lang()) || h.sku;
  }

  poolBranch(p: FixedAssetPool): string {
    return localizedName({ nameAr: p.branchNameAr, nameFr: p.branchNameFr, nameEn: p.branchNameEn }, this.i18n.lang()) || p.branchCode;
  }

  entryBranch(e: { branchNameAr: string; branchNameFr: string; branchNameEn: string; branchCode: string }): string {
    return localizedName({ nameAr: e.branchNameAr, nameFr: e.branchNameFr, nameEn: e.branchNameEn }, this.i18n.lang()) || e.branchCode;
  }

  /** The slot in words, and the account it names, or "not mapped". */
  accountLabel(l: PracticeLine): string {
    const slot = this.i18n.t('fa.slot.' + l.slot);
    if (!l.accountCode) return `${slot} — ${this.i18n.t('mfgChoices.map.unmapped')}`;
    const name = localizedName({ nameAr: l.accountNameAr ?? '', nameFr: l.accountNameFr ?? '', nameEn: l.accountNameEn ?? '' }, this.i18n.lang());
    return `${l.accountCode} ${name} (${slot})`;
  }
}
