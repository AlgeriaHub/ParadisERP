import { Component, OnInit, computed, effect, inject, output, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { I18nService } from '../../../core/i18n.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { DEFAULT_PAGE_SIZE, PAGE_SIZES } from '../../../ui/pager';
import { StatusComponent } from '../../../ui/status.component';
import { StatusFamily, toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { saveBlob } from '../item-status/item-status-api.service';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { costWords } from './cost-words';
import { ManufacturingApiService, ProductionLineSummary } from './manufacturing-api.service';
import { mfgError, qty } from './manufacturing-shared';
import {
  CostGapReport, CostHistoryReport, CostRegister, CostReportFilter, CostVarianceReport, LateCostReport, ProductionApiService,
  ReleaseReconciliationReport, ScrapCostReport,
} from './production-api.service';

export type CostTab = 'overview' | 'history' | 'late' | 'reconciliation' | 'releases' | 'integrity';
type ReconciliationView = 'comparison' | 'variances' | 'scrap';

const TABS: CostTab[] = ['overview', 'history', 'late', 'reconciliation', 'releases', 'integrity'];
const VIEWS: ReconciliationView[] = ['comparison', 'variances', 'scrap'];
/**
 * The server pages these registers (owner, 2026-11-17: ten rows to begin with, the reader picks more). It caps a
 * page at 200, and at 50 when the live comparison recomputes every order on it (`ProductionCostHistory.MaxLivePageSize`).
 */
const MAX_LIVE_PAGE_SIZE = 50;
const LIVE_PAGE_SIZES = PAGE_SIZES.filter((s) => s <= MAX_LIVE_PAGE_SIZE);

/**
 * Manufacturing › Costing (B2 — docs/manufacturing/costing-b2.md): B1's frozen cost sheets and everything recorded
 * after them, read only. Every figure keeps the original snapshot apart from the late effects, the adjusted
 * provisional total apart from both, and "financially finalized" reads "not available" — never a zero. A completed
 * order with no snapshot is listed as missing, never as a zero cost.
 *
 * The tab, the period and the filters live in the address, so a link, Back and Forward land on the same report.
 * Exports are the same routes asked for a workbook: the server applies the same permission, line scope and
 * redaction to both.
 */
@Component({
  selector: 'app-mfg-cost-reconciliation',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, DateInputComponent, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="cr-line">{{ i18n.t('mfg.boms.line') }}</label>
          <select id="cr-line" [ngModel]="q().line" (ngModelChange)="set({ line: $event })">
            <option [ngValue]="null">{{ i18n.t('mfg.orders.allLines') }}</option>
            @for (l of lines(); track l.id) {
              <option [ngValue]="l.id">{{ l.code }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="cr-from">{{ i18n.t('mfg.reports.from') }}</label>
          <app-date-input id="cr-from" [ngModel]="q().from" (ngModelChange)="set({ from: $event || null })" />
        </div>
        <div class="ui-filter">
          <label for="cr-to">{{ i18n.t('mfg.reports.to') }}</label>
          <app-date-input id="cr-to" [ngModel]="q().to" (ngModelChange)="set({ to: $event || null })" />
        </div>
        <div class="ui-filter">
          <label for="cr-basis">{{ i18n.t('mfg.b2.basis') }}</label>
          <select id="cr-basis" [ngModel]="q().basis" (ngModelChange)="set({ basis: $event })">
            <option [ngValue]="null">{{ i18n.t('mfg.b2.basis.default') }}</option>
            @for (b of bases(); track b) {
              <option [ngValue]="b">{{ i18n.t('mfg.b2.basis.' + b) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="cr-asof">{{ i18n.t('mfg.b2.asOf') }}</label>
          <app-date-input id="cr-asof" [ngModel]="q().asOf" (ngModelChange)="set({ asOf: $event || null })" />
        </div>
        <div class="ui-toolbar__end">
          <button type="button" class="btn-ghost" [disabled]="busy()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
          @if (register()) {
            <button type="button" class="btn-ghost" [disabled]="exporting()" (click)="export()">{{ i18n.t('mfg.b2.export') }}</button>
          }
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      <div class="ui-tabs" role="tablist" [attr.aria-label]="i18n.t('mfg.page.costing')" (keydown)="onTabKey($event)">
        @for (t of tabs; track t) {
          <button type="button" class="ui-tab" role="tab" [id]="'cr-tab-' + t" [attr.aria-selected]="tab() === t"
            [attr.aria-controls]="'cr-panel'" [attr.tabindex]="tab() === t ? 0 : -1" (click)="go(t)">
            {{ i18n.t('mfg.b2.tab.' + t) }}
          </button>
        }
      </div>

      <div id="cr-panel" role="tabpanel" [attr.aria-labelledby]="'cr-tab-' + tab()" class="bo-stack">
        <p class="bo-hint">{{ i18n.t('mfg.b2.lead.' + tab()) }}</p>
        @if (period(); as p) {
          <p class="ui-sub">
            {{ i18n.t('mfg.b2.periodLine', { from: i18n.date(p.from), to: i18n.date(p.to), basis: i18n.t('mfg.b2.basis.' + p.basis), asOf: i18n.date(p.asOf) }) }}
          </p>
        }
        @if (error()) {
          <p class="notice danger" role="alert">{{ error() }}</p>
        }

        @switch (tab()) {
          @case ('overview') {
            @if (history(); as h) {
              <div class="ui-metrics">
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.original') }}</span><strong class="ui-metric__value mono">{{ i18n.money(h.totals.original) }}</strong></div>
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.lateRecognized') }}</span><strong class="ui-metric__value mono">{{ i18n.money(h.totals.lateRecognized) }}</strong></div>
                <div class="ui-metric ui-metric--lead"><span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.adjusted') }}</span><strong class="ui-metric__value mono">{{ i18n.money(h.totals.adjustedProvisional) }}</strong></div>
                <div class="ui-metric" [class.ui-metric--warn]="h.totals.lateUnclassified || h.totals.lateUncertain">
                  <span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.notIncluded') }}</span>
                  <strong class="ui-metric__value mono">{{ i18n.money(h.totals.lateUnclassified + h.totals.lateUncertain) }}</strong>
                </div>
                <div class="ui-metric">
                  <span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.finalized') }}</span>
                  <strong class="ui-metric__value cr-word">{{ i18n.t('mfg.b2.finalized.short') }}</strong>
                  <span class="ui-sub">{{ i18n.t('mfg.b2.finalized.why') }}</span>
                </div>
              </div>
              <div class="ui-metrics">
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.b2.count.frozen') }}</span><strong class="ui-metric__value mono">{{ h.totals.frozen }}</strong></div>
                <div class="ui-metric" [class.ui-metric--warn]="h.totals.notFrozen"><span class="ui-metric__label">{{ i18n.t('mfg.b2.count.notFrozen') }}</span><strong class="ui-metric__value mono">{{ h.totals.notFrozen }}</strong></div>
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.b2.count.reconstructed') }}</span><strong class="ui-metric__value mono">{{ h.totals.reconstructed }}</strong></div>
                <div class="ui-metric" [class.ui-metric--warn]="h.totals.integrityFailures"><span class="ui-metric__label">{{ i18n.t('mfg.b2.count.integrityFailures') }}</span><strong class="ui-metric__value mono">{{ h.totals.integrityFailures }}</strong></div>
              </div>
            }
            @if (late(); as l) {
              @if (l.byElement.length) {
                <h3 class="cr-subtitle">{{ i18n.t('mfg.b2.overview.lateByElement') }}</h3>
                <section class="ui-surface">
                  <table class="ui-table ui-table--cards">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('mfg.cost.element') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.recordedAmount') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.fig.lateRecognized') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.treatmentWord.unclassified') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.treatmentWord.uncertain') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (e of l.byElement; track e.element) {
                        <tr>
                          <td [attr.data-label]="i18n.t('mfg.cost.element')">{{ elementWord(e.element) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.recordedAmount')">{{ i18n.money(e.recorded) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.lateRecognized')">{{ i18n.money(e.recognized) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.treatmentWord.unclassified')">{{ i18n.money(e.unclassified) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.treatmentWord.uncertain')">{{ i18n.money(e.uncertain) }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </section>
              }
            }
            @if (gaps(); as g) {
              <div class="ui-metrics">
                @for (s of severities; track s) {
                  <div class="ui-metric" [class.ui-metric--warn]="(g.countBySeverity[s] ?? 0) > 0 && s !== 'info'">
                    <span class="ui-metric__label">{{ i18n.t('mfg.b2.severity.' + s) }}</span>
                    <strong class="ui-metric__value mono">{{ g.countBySeverity[s] ?? 0 }}</strong>
                  </div>
                }
              </div>
            }
            @if (busy() && !history()) { <app-table-state kind="loading" /> }
          }

          @case ('history') {
            <div class="ui-toolbar">
              <div class="ui-filter">
                <label for="cr-state">{{ i18n.t('common.state') }}</label>
                <select id="cr-state" [ngModel]="q().state" (ngModelChange)="set({ state: $event })">
                  <option [ngValue]="null">{{ i18n.t('mfg.b2.all') }}</option>
                  <option ngValue="frozen">{{ i18n.t('mfg.b2.state.frozen') }}</option>
                  <option ngValue="not_frozen">{{ i18n.t('mfg.b2.state.not_frozen') }}</option>
                </select>
              </div>
              <div class="ui-filter">
                <label for="cr-kind">{{ i18n.t('mfg.b2.kind') }}</label>
                <select id="cr-kind" [ngModel]="q().kind" (ngModelChange)="set({ kind: $event })">
                  <option [ngValue]="null">{{ i18n.t('mfg.b2.all') }}</option>
                  <option ngValue="created">{{ i18n.t('mfg.b2.kind.created') }}</option>
                  <option ngValue="reconstructed">{{ i18n.t('mfg.b2.kind.reconstructed') }}</option>
                </select>
              </div>
              <div class="ui-filter">
                <label for="cr-source">{{ i18n.t('mfg.b2.sourceCapture') }}</label>
                <select id="cr-source" [ngModel]="q().source" (ngModelChange)="set({ source: $event })">
                  <option [ngValue]="null">{{ i18n.t('mfg.b2.all') }}</option>
                  <option ngValue="complete">{{ i18n.t('mfg.b2.source.complete') }}</option>
                  <option ngValue="incompletelegacy">{{ i18n.t('mfg.b2.source.incompletelegacy') }}</option>
                </select>
              </div>
            </div>
            <section class="ui-surface" [attr.aria-busy]="busy()">
              @if (busy() && !history()) {
                <app-table-state kind="loading" />
              } @else if (!history()?.rows?.length) {
                <app-table-state kind="empty" [message]="i18n.t('mfg.b2.empty.history')" />
              } @else {
                <div class="ui-scroll">
                  <table class="ui-table ui-table--cards">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('mfg.orders.number') }}</th>
                        <th>{{ i18n.t('common.state') }}</th>
                        <th>{{ i18n.t('mfg.b2.frozenAt') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.fig.original') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.fig.lateRecognized') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.fig.adjusted') }}</th>
                        <th>{{ i18n.t('mfg.b2.fig.finalized') }}</th>
                        <th>{{ i18n.t('mfg.b2.integrity') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (r of history()!.rows; track r.orderId) {
                        <tr>
                          <td [attr.data-label]="i18n.t('mfg.orders.number')">
                            <button type="button" class="ui-link" (click)="open.emit(r.orderId)"><span dir="ltr">{{ r.orderNumber }}</span></button>
                            <span class="ui-sub"><span dir="ltr">{{ r.lineCode }} · {{ r.productSku }}</span></span>
                          </td>
                          <td [attr.data-label]="i18n.t('common.state')">
                            <app-status [tone]="tone('costHistory', r.state)">{{ i18n.t('mfg.b2.state.' + r.state) }}</app-status>
                            @if (r.sheet; as s) {
                              <app-status [tone]="tone('costSnapshotKind', s.kind)">{{ i18n.t('mfg.b2.kind.' + s.kind) }}</app-status>
                              @if (s.sourceState !== 'complete') {
                                <app-status [tone]="tone('costSource', s.sourceState)">{{ i18n.t('mfg.b2.source.' + s.sourceState) }}</app-status>
                              }
                            }
                          </td>
                          <td [attr.data-label]="i18n.t('mfg.b2.frozenAt')">
                            @if (r.sheet; as s) {
                              {{ i18n.dateTime(s.frozenAtUtc) }}
                              @if (s.freezeLagDays) { <span class="ui-sub">{{ i18n.t('mfg.b2.order.lag', { days: s.freezeLagDays }) }}</span> }
                            } @else { <span class="ui-sub">{{ i18n.t('mfg.b2.noSnapshot') }}</span> }
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.original')">{{ r.figures ? i18n.money(r.figures.original) : '—' }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.lateRecognized')">{{ r.figures ? i18n.money(r.figures.lateRecognized) : '—' }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.adjusted')"><strong>{{ r.figures ? i18n.money(r.figures.adjustedProvisional) : '—' }}</strong></td>
                          <td [attr.data-label]="i18n.t('mfg.b2.fig.finalized')"><span class="ui-sub">{{ i18n.t('mfg.b2.finalized.short') }}</span></td>
                          <td [attr.data-label]="i18n.t('mfg.b2.integrity')">
                            @if (r.integrity) {
                              <app-status [tone]="tone('costCheck', r.integrity)">{{ i18n.t('mfg.b2.check.result.' + r.integrity) }}</app-status>
                            } @else { — }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </section>
            @if (history(); as h) { <app-list-footer [total]="h.page.total" [pageIndex]="h.page.page - 1" [pageSize]="h.page.pageSize" [loading]="busy()" (pageChange)="set({ page: $event + 1 }, true)" (pageSizeChange)="setSize($event)" /> }
          }

          @case ('late') {
            <div class="ui-toolbar">
              <div class="ui-filter">
                <label for="cr-element">{{ i18n.t('mfg.cost.element') }}</label>
                <select id="cr-element" [ngModel]="q().element" (ngModelChange)="set({ element: $event })">
                  <option [ngValue]="null">{{ i18n.t('mfg.b2.all') }}</option>
                  @for (e of elements; track e) { <option [ngValue]="e">{{ elementWord(e) }}</option> }
                </select>
              </div>
              <div class="ui-filter">
                <label for="cr-treatment">{{ i18n.t('mfg.b2.treatment') }}</label>
                <select id="cr-treatment" [ngModel]="q().treatment" (ngModelChange)="set({ treatment: $event })">
                  <option [ngValue]="null">{{ i18n.t('mfg.b2.all') }}</option>
                  @for (t of treatments; track t) { <option [ngValue]="t">{{ i18n.t('mfg.b2.treatmentWord.' + t) }}</option> }
                </select>
              </div>
            </div>
            @if (late(); as l) {
              <div class="ui-metrics">
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.b2.recordedAmount') }}</span><strong class="ui-metric__value mono">{{ i18n.money(l.recorded) }}</strong></div>
                <div class="ui-metric ui-metric--lead"><span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.lateRecognized') }}</span><strong class="ui-metric__value mono">{{ i18n.money(l.recognized) }}</strong></div>
                <div class="ui-metric" [class.ui-metric--warn]="l.unclassified"><span class="ui-metric__label">{{ i18n.t('mfg.b2.treatmentWord.unclassified') }}</span><strong class="ui-metric__value mono">{{ i18n.money(l.unclassified) }}</strong></div>
                <div class="ui-metric" [class.ui-metric--warn]="l.uncertain"><span class="ui-metric__label">{{ i18n.t('mfg.b2.treatmentWord.uncertain') }}</span><strong class="ui-metric__value mono">{{ i18n.money(l.uncertain) }}</strong></div>
              </div>
            }
            <section class="ui-surface" [attr.aria-busy]="busy()">
              @if (busy() && !late()) {
                <app-table-state kind="loading" />
              } @else if (!late()?.rows?.length) {
                <app-table-state kind="empty" [message]="i18n.t('mfg.b2.empty.late')" />
              } @else {
                <div class="ui-scroll">
                  <table class="ui-table ui-table--cards">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('mfg.b2.recordedAt') }}</th>
                        <th>{{ i18n.t('mfg.orders.number') }}</th>
                        <th>{{ i18n.t('mfg.b2.kind') }}</th>
                        <th>{{ i18n.t('mfg.b2.treatment') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.recordedAmount') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.fig.lateRecognized') }}</th>
                        <th>{{ i18n.t('mfg.b2.sourceReason') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (r of late()!.rows; track r.id) {
                        <tr>
                          <td [attr.data-label]="i18n.t('mfg.b2.recordedAt')">{{ i18n.dateTime(r.recordedAtUtc) }}</td>
                          <td [attr.data-label]="i18n.t('mfg.orders.number')">
                            <button type="button" class="ui-link" (click)="open.emit(r.orderId)"><span dir="ltr">{{ r.orderNumber }}</span></button>
                            <span class="ui-sub" dir="ltr">{{ r.lineCode }}</span>
                          </td>
                          <td [attr.data-label]="i18n.t('mfg.b2.kind')"><span dir="ltr">{{ r.kind }}</span><span class="ui-sub">{{ elementWord(r.element) }}</span></td>
                          <td [attr.data-label]="i18n.t('mfg.b2.treatment')">
                            <app-status [tone]="tone('lateTreatment', r.treatment)">{{ i18n.t('mfg.b2.treatmentWord.' + r.treatment) }}</app-status>
                            @for (f of r.flags; track f) { <span class="ui-sub">{{ i18n.t('mfg.b2.flag.' + f) }}</span> }
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.recordedAmount')">{{ i18n.money(r.recordedAmount) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.lateRecognized')">{{ i18n.money(r.recognizedEffect) }}</td>
                          <td [attr.data-label]="i18n.t('mfg.b2.sourceReason')">
                            @if (r.redacted) { <span class="ui-sub">{{ i18n.t('mfg.b2.withheld') }}</span> }
                            @else { <span dir="ltr">{{ r.sourceReference }}</span><span class="ui-sub">{{ r.reason }}</span> }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </section>
            @if (late(); as l) { <app-list-footer [total]="l.page.total" [pageIndex]="l.page.page - 1" [pageSize]="l.page.pageSize" [loading]="busy()" (pageChange)="set({ page: $event + 1 }, true)" (pageSizeChange)="setSize($event)" /> }
          }

          @case ('reconciliation') {
            <div class="ui-toolbar">
              <span class="ui-filter__label">{{ i18n.t('mfg.b2.view') }}</span>
              @for (v of views; track v) {
                <button type="button" class="ui-tab" [attr.aria-pressed]="view() === v" [class.is-active]="view() === v" (click)="set({ view: v })">{{ i18n.t('mfg.b2.view.' + v) }}</button>
              }
            </div>
            @switch (view()) {
              @case ('comparison') {
                <section class="ui-surface" [attr.aria-busy]="busy()">
                  @if (busy() && !history()) {
                    <app-table-state kind="loading" />
                  } @else if (!history()?.rows?.length) {
                    <app-table-state kind="empty" [message]="i18n.t('mfg.b2.empty.history')" />
                  } @else {
                    <div class="ui-scroll">
                      <table class="ui-table ui-table--cards">
                        <thead>
                          <tr>
                            <th>{{ i18n.t('mfg.orders.number') }}</th>
                            <th class="num">{{ i18n.t('mfg.b2.fig.original') }}</th>
                            <th class="num">{{ i18n.t('mfg.b2.fig.lateRecognized') }}</th>
                            <th class="num">{{ i18n.t('mfg.b2.fig.adjusted') }}</th>
                            <th class="num">{{ i18n.t('mfg.b2.fig.live') }}</th>
                            <th class="num">{{ i18n.t('mfg.b2.fig.unexplained') }}</th>
                          </tr>
                        </thead>
                        <tbody>
                          @for (r of history()!.rows; track r.orderId) {
                            <tr>
                              <td [attr.data-label]="i18n.t('mfg.orders.number')"><button type="button" class="ui-link" (click)="open.emit(r.orderId)"><span dir="ltr">{{ r.orderNumber }}</span></button></td>
                              <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.original')">{{ r.figures ? i18n.money(r.figures.original) : '—' }}</td>
                              <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.lateRecognized')">{{ r.figures ? i18n.money(r.figures.lateRecognized) : '—' }}</td>
                              <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.adjusted')">{{ r.figures ? i18n.money(r.figures.adjustedProvisional) : '—' }}</td>
                              <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.live')">
                                @if (r.live?.comparable) { {{ i18n.money(r.live!.live!) }} }
                                @else { <span class="ui-sub">{{ i18n.t('mfg.b2.live.notComparable.' + (r.live?.reason ?? 'unknown')) }}</span> }
                              </td>
                              <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.unexplained')">
                                @if (r.live?.comparable) {
                                  <strong>{{ i18n.money(r.live!.unexplained!) }}</strong>
                                  @if (r.live!.unexplained) { <span class="ui-sub">{{ i18n.t('mfg.b2.live.review') }}</span> }
                                } @else { — }
                              </td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  }
                </section>
                <p class="bo-hint">{{ i18n.t('mfg.b2.live.lead') }}</p>
                @if (history(); as h) { <app-list-footer [total]="h.page.total" [pageIndex]="h.page.page - 1" [pageSize]="h.page.pageSize" [sizes]="liveSizes" [loading]="busy()" (pageChange)="set({ page: $event + 1 }, true)" (pageSizeChange)="setSize($event)" /> }
              }
              @case ('variances') {
                <section class="ui-surface" [attr.aria-busy]="busy()">
                  @if (busy() && !variances()) {
                    <app-table-state kind="loading" />
                  } @else if (!variances()?.rows?.length) {
                    <app-table-state kind="empty" [message]="i18n.t('mfg.b2.empty.history')" />
                  } @else {
                    <div class="ui-scroll">
                      <table class="ui-table ui-table--cards">
                        <thead>
                          <tr>
                            <th>{{ i18n.t('mfg.orders.number') }}</th>
                            @for (c of varianceGroups; track c) { <th class="num">{{ i18n.t('mfg.b2.var.' + c) }}</th> }
                          </tr>
                        </thead>
                        <tbody>
                          @for (r of variances()!.rows; track r.orderId) {
                            <tr>
                              <td [attr.data-label]="i18n.t('mfg.orders.number')"><button type="button" class="ui-link" (click)="open.emit(r.orderId)"><span dir="ltr">{{ r.orderNumber }}</span></button></td>
                              @if (r.variance.available) {
                                @for (c of varianceGroups; track c) {
                                  <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.var.' + c)">{{ varianceCell(r.variance.lines, c) }}</td>
                                }
                              } @else {
                                <td [attr.colspan]="varianceGroups.length" class="ui-sub">{{ i18n.t('mfg.b2.unavailable.' + (r.variance.reason ?? 'unknown')) }}</td>
                              }
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  }
                </section>
                <p class="bo-hint">{{ i18n.t('mfg.b2.var.lead') }}</p>
                @if (variances(); as v) { <app-list-footer [total]="v.page.total" [pageIndex]="v.page.page - 1" [pageSize]="v.page.pageSize" [loading]="busy()" (pageChange)="set({ page: $event + 1 }, true)" (pageSizeChange)="setSize($event)" /> }
              }
              @case ('scrap') {
                <section class="ui-surface" [attr.aria-busy]="busy()">
                  @if (busy() && !scrap()) {
                    <app-table-state kind="loading" />
                  } @else if (!scrap()?.rows?.length) {
                    <app-table-state kind="empty" [message]="i18n.t('mfg.b2.empty.history')" />
                  } @else {
                    <div class="ui-scroll">
                      <table class="ui-table ui-table--cards">
                        <thead>
                          <tr>
                            <th>{{ i18n.t('mfg.orders.number') }}</th>
                            <th class="num">{{ i18n.t('mfg.b2.scrap.rejectedQty') }}</th>
                            <th class="num">{{ i18n.t('mfg.b2.scrap.rate') }}</th>
                            <th class="num">{{ i18n.t('mfg.b2.scrap.materialScrap') }}</th>
                            <th class="num">{{ i18n.t('mfg.b2.scrap.spoilageAbsorbed') }}</th>
                            <th>{{ i18n.t('mfg.b2.scrap.recovery') }}</th>
                          </tr>
                        </thead>
                        <tbody>
                          @for (r of scrap()!.rows; track r.orderId) {
                            <tr>
                              <td [attr.data-label]="i18n.t('mfg.orders.number')"><button type="button" class="ui-link" (click)="open.emit(r.orderId)"><span dir="ltr">{{ r.orderNumber }}</span></button></td>
                              <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.scrap.rejectedQty')">{{ qty(r.scrap.rejectedQty) }} / {{ qty(r.scrap.acceptedQty + r.scrap.rejectedQty) }}</td>
                              <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.scrap.rate')">{{ r.scrap.rejectionRatePercent === null ? '—' : r.scrap.rejectionRatePercent + ' %' }}</td>
                              <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.scrap.materialScrap')">{{ money(r.scrap.materialScrapValue) }}</td>
                              <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.scrap.spoilageAbsorbed')">{{ money(r.scrap.spoilageAbsorbed) }}</td>
                              <td [attr.data-label]="i18n.t('mfg.b2.scrap.recovery')">{{ i18n.t('mfg.cost.elementStatus.' + r.scrap.recoveryStatus) }}</td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  }
                </section>
                <p class="bo-hint">{{ i18n.t('mfg.b2.scrap.lead') }}</p>
                @if (scrap(); as sc) { <app-list-footer [total]="sc.page.total" [pageIndex]="sc.page.page - 1" [pageSize]="sc.page.pageSize" [loading]="busy()" (pageChange)="set({ page: $event + 1 }, true)" (pageSizeChange)="setSize($event)" /> }
              }
            }
          }

          @case ('releases') {
            @if (releases(); as rr) {
              <div class="ui-metrics">
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.b2.fig.original') }}</span><strong class="ui-metric__value mono">{{ i18n.money(rr.original) }}</strong></div>
                <div class="ui-metric ui-metric--lead"><span class="ui-metric__label">{{ i18n.t('mfg.b2.rel.releasedValue') }}</span><strong class="ui-metric__value mono">{{ i18n.money(rr.releasedValue) }}</strong></div>
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.b2.rel.keptInCharges') }}</span><strong class="ui-metric__value mono">{{ i18n.money(rr.keptInCharges) }}</strong></div>
                <div class="ui-metric"><span class="ui-metric__label">{{ i18n.t('mfg.b2.rel.lateNotAllocatedLabel') }}</span><strong class="ui-metric__value mono">{{ i18n.money(rr.lateNotAllocated) }}</strong></div>
              </div>
            }
            <section class="ui-surface" [attr.aria-busy]="busy()">
              @if (busy() && !releases()) {
                <app-table-state kind="loading" />
              } @else if (!releases()?.rows?.length) {
                <app-table-state kind="empty" [message]="i18n.t('mfg.b2.empty.history')" />
              } @else {
                <div class="ui-scroll">
                  <table class="ui-table ui-table--cards">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('mfg.orders.number') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.fig.original') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.rel.releasedQty') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.rel.releasedValue') }}</th>
                        <th class="num">{{ i18n.t('mfg.b2.rel.lastRelease') }}</th>
                        <th>{{ i18n.t('mfg.b2.rel.notes') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (r of releases()!.rows; track r.orderId) {
                        <tr>
                          <td [attr.data-label]="i18n.t('mfg.orders.number')"><button type="button" class="ui-link" (click)="open.emit(r.orderId)"><span dir="ltr">{{ r.orderNumber }}</span></button></td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.fig.original')">{{ i18n.money(r.original) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.rel.releasedQty')">{{ qty(r.reconciliation.releasedQty) }} / {{ qty(r.reconciliation.acceptedQty) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.rel.releasedValue')">
                            {{ i18n.money(r.reconciliation.releasedValue) }}
                            @if (r.reconciliation.keptInCharges) { <span class="ui-sub">{{ i18n.t('mfg.b2.rel.kept', { amount: i18n.money(r.reconciliation.keptInCharges) }) }}</span> }
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.b2.rel.lastRelease')">
                            @if (lastRelease(r.reconciliation); as x) { {{ i18n.money(x.unitValue) }} <span class="ui-sub" dir="ltr">{{ x.number }}</span> } @else { — }
                          </td>
                          <td [attr.data-label]="i18n.t('mfg.b2.rel.notes')">
                            @for (f of r.reconciliation.flags; track f) { <span class="ui-sub">{{ i18n.t('mfg.b2.flag.' + f, { qty: qty(r.reconciliation.unreleasedAcceptedQty) }) }}</span> }
                            @if (r.reconciliation.lateNotAllocated) { <span class="ui-sub">{{ i18n.t('mfg.b2.rel.lateNotAllocated', { amount: i18n.money(r.reconciliation.lateNotAllocated) }) }}</span> }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </section>
            <p class="bo-hint">{{ i18n.t('mfg.b2.rel.lead') }}</p>
            @if (releases(); as rr) { <app-list-footer [total]="rr.page.total" [pageIndex]="rr.page.page - 1" [pageSize]="rr.page.pageSize" [loading]="busy()" (pageChange)="set({ page: $event + 1 }, true)" (pageSizeChange)="setSize($event)" /> }
          }

          @case ('integrity') {
            <div class="ui-toolbar">
              <div class="ui-filter">
                <label for="cr-severity">{{ i18n.t('mfg.b2.severity') }}</label>
                <select id="cr-severity" [ngModel]="q().severity" (ngModelChange)="set({ severity: $event })">
                  <option [ngValue]="null">{{ i18n.t('mfg.b2.all') }}</option>
                  @for (s of severities; track s) { <option [ngValue]="s">{{ i18n.t('mfg.b2.severity.' + s) }}</option> }
                </select>
              </div>
            </div>
            <section class="ui-surface" [attr.aria-busy]="busy()">
              @if (busy() && !gaps()) {
                <app-table-state kind="loading" />
              } @else if (!gaps()?.rows?.length) {
                <app-table-state kind="empty" [message]="i18n.t('mfg.b2.empty.gaps')" />
              } @else {
                <div class="ui-scroll">
                  <table class="ui-table ui-table--cards">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('mfg.orders.number') }}</th>
                        <th>{{ i18n.t('common.state') }}</th>
                        <th>{{ i18n.t('mfg.b2.integrity') }}</th>
                        <th>{{ i18n.t('mfg.b2.gaps') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (r of gaps()!.rows; track r.orderId) {
                        <tr>
                          <td [attr.data-label]="i18n.t('mfg.orders.number')">
                            <button type="button" class="ui-link" (click)="open.emit(r.orderId)"><span dir="ltr">{{ r.orderNumber }}</span></button>
                            <span class="ui-sub" dir="ltr">{{ r.lineCode }}</span>
                          </td>
                          <td [attr.data-label]="i18n.t('common.state')"><app-status [tone]="tone('costHistory', r.state)">{{ i18n.t('mfg.b2.state.' + r.state) }}</app-status></td>
                          <td [attr.data-label]="i18n.t('mfg.b2.integrity')">
                            @if (r.integrity) {
                              <app-status [tone]="tone('costCheck', r.integrity)">{{ i18n.t('mfg.b2.check.result.' + r.integrity) }}</app-status>
                              @for (c of failed(r.checks); track c.code) { <span class="ui-sub">{{ i18n.t('mfg.b2.check.' + c.code) }}</span> }
                            } @else { — }
                          </td>
                          <td [attr.data-label]="i18n.t('mfg.b2.gaps')">
                            @for (g of r.gaps; track g.code + (g.element ?? '') + (g.detail ?? '')) {
                              <div class="gap-line">
                                <app-status [tone]="tone('costGap', g.severity)">{{ i18n.t('mfg.b2.severity.' + g.severity) }}</app-status>
                                <span>{{ words.gap(g) }}</span>
                              </div>
                            }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </section>
            @if (gaps(); as g) { <app-list-footer [total]="g.page.total" [pageIndex]="g.page.page - 1" [pageSize]="g.page.pageSize" [loading]="busy()" (pageChange)="set({ page: $event + 1 }, true)" (pageSizeChange)="setSize($event)" /> }
          }
        }

        @if (limitations().length) {
          <div class="cr-notes">
            @for (l of limitations(); track l) { <p class="bo-hint">{{ i18n.t('mfg.b2.limit.' + l) }}</p> }
          </div>
        }
      </div>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .gap-line {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
        margin-block-end: var(--sp-1);
      }
      .cr-word {
        font-size: var(--fs-section);
      }
      .cr-subtitle {
        margin: 0;
        font-size: var(--fs-section);
        font-weight: 600;
      }
      .cr-notes {
        display: grid;
        gap: var(--sp-1);
        padding-block-start: var(--sp-3);
        border-block-start: var(--line) solid var(--border);
      }
    `,
  ],
})
export class MfgCostReconciliationComponent implements OnInit {
  private readonly production = inject(ProductionApiService);
  private readonly api = inject(ManufacturingApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);
  readonly words = costWords(this.i18n);

  readonly open = output<string>();

  readonly qty = qty;
  readonly tabs = TABS;
  readonly views = VIEWS;
  readonly elements = ['materials', 'consumables', 'accessories', 'labour', 'overhead', 'expenses', 'recovery', 'unclassified'];
  readonly treatments = ['recognized', 'unclassified', 'uncertain'];
  readonly severities = ['missing', 'integrity', 'incomplete', 'provisional', 'review', 'info'];
  readonly varianceGroups = ['materials', 'labour', 'overhead', 'expenses', 'recovery', 'total'];
  /** What the live comparison's footer offers: the server answers at most fifty orders a page there. */
  readonly liveSizes = LIVE_PAGE_SIZES;

  private readonly params = toSignal(this.route.queryParamMap, { initialValue: this.route.snapshot.queryParamMap });
  /** Everything the report shows, read from the address. */
  readonly q = computed(() => {
    const p = this.params();
    const tab = p.get('tab') as CostTab | null;
    const view = p.get('view') as ReconciliationView | null;
    const page = Number(p.get('page'));
    const size = Number(p.get('size'));
    return {
      tab: tab && TABS.includes(tab) ? tab : 'overview',
      view: view && VIEWS.includes(view) ? view : 'comparison',
      line: p.get('line'), from: p.get('from'), to: p.get('to'), basis: p.get('basis'), asOf: p.get('asOf'),
      state: p.get('state'), kind: p.get('kind'), source: p.get('source'), element: p.get('element'),
      treatment: p.get('treatment'), severity: p.get('severity'),
      page: Number.isInteger(page) && page > 0 ? page : 1,
      size: PAGE_SIZES.includes(size) ? size : DEFAULT_PAGE_SIZE,
    };
  });
  readonly tab = computed(() => this.q().tab);
  readonly view = computed(() => this.q().view);

  readonly lines = signal<ProductionLineSummary[]>([]);
  readonly history = signal<CostHistoryReport | null>(null);
  readonly late = signal<LateCostReport | null>(null);
  readonly variances = signal<CostVarianceReport | null>(null);
  readonly scrap = signal<ScrapCostReport | null>(null);
  readonly releases = signal<ReleaseReconciliationReport | null>(null);
  readonly gaps = signal<CostGapReport | null>(null);
  readonly busy = signal(false);
  readonly exporting = signal(false);
  readonly error = signal<string | null>(null);
  private request = 0;

  /** The late-cost register may also filter on the day a cost was recorded. */
  readonly bases = computed(() => (this.tab() === 'late' ? ['completed', 'frozen', 'recorded'] : ['completed', 'frozen']));

  /** The register the current tab exports, or null (the overview combines several). */
  readonly register = computed<CostRegister | null>(() => {
    switch (this.tab()) {
      case 'history': return 'cost-history';
      case 'late': return 'late-costs';
      case 'reconciliation': return this.view() === 'variances' ? 'cost-variances' : this.view() === 'scrap' ? 'scrap-costs' : 'cost-history';
      case 'releases': return 'release-reconciliation';
      case 'integrity': return 'cost-gaps';
      default: return null;
    }
  });

  readonly period = computed(() => {
    switch (this.tab()) {
      case 'late': return this.late()?.period ?? null;
      case 'releases': return this.releases()?.period ?? null;
      case 'integrity': return this.gaps()?.period ?? null;
      case 'reconciliation':
        return (this.view() === 'variances' ? this.variances()?.period : this.view() === 'scrap' ? this.scrap()?.period : this.history()?.period) ?? null;
      default: return this.history()?.period ?? null;
    }
  });

  readonly limitations = computed(() => {
    const report = this.tab() === 'late' ? this.late() : this.tab() === 'integrity' ? this.gaps() : this.tab() === 'releases' ? this.releases() : this.history();
    return report?.limitations ?? [];
  });

  constructor() {
    effect(() => {
      this.q();
      untracked(() => void this.reload());
    });
  }

  ngOnInit(): void {
    this.chrome?.description.set(this.i18n.t('mfg.b2.pageLead'));
    this.api.lines(true).then((l) => this.lines.set(l), () => undefined);
  }

  /** The filters the current tab sends; an empty date leaves the server's default period (the company's month). */
  private filter(): CostReportFilter {
    const q = this.q();
    const live = q.tab === 'reconciliation' && q.view === 'comparison';
    return {
      lineId: q.line, from: q.from, to: q.to, basis: q.basis, asOf: q.asOf, page: q.page,
      pageSize: live ? Math.min(q.size, MAX_LIVE_PAGE_SIZE) : q.size,
      state: q.tab === 'history' ? q.state : live ? 'frozen' : null,
      kind: q.tab === 'history' ? q.kind : null,
      sourceState: q.tab === 'history' ? q.source : null,
      element: q.tab === 'late' ? q.element : null,
      treatment: q.tab === 'late' ? q.treatment : null,
      severity: q.tab === 'integrity' ? q.severity : null,
      live: live || null,
    };
  }

  async reload(): Promise<void> {
    const ticket = ++this.request;
    const q = this.q();
    const f = this.filter();
    this.busy.set(true);
    this.error.set(null);
    try {
      switch (q.tab) {
        case 'overview': {
          // Totals cover the whole period whatever the page: one row of each is enough.
          const common = { ...f, page: 1, pageSize: 1 };
          const [h, l, g] = await Promise.all([
            this.production.costHistory(common), this.production.lateCosts(common), this.production.costGaps(common),
          ]);
          if (ticket !== this.request) return;
          this.history.set(h);
          this.late.set(l);
          this.gaps.set(g);
          break;
        }
        case 'history': this.keep(ticket, this.history, await this.production.costHistory(f)); break;
        case 'late': this.keep(ticket, this.late, await this.production.lateCosts(f)); break;
        case 'reconciliation':
          if (q.view === 'variances') this.keep(ticket, this.variances, await this.production.costVariances(f));
          else if (q.view === 'scrap') this.keep(ticket, this.scrap, await this.production.scrapCosts(f));
          else this.keep(ticket, this.history, await this.production.costHistory(f));
          break;
        case 'releases': this.keep(ticket, this.releases, await this.production.releaseReconciliation(f)); break;
        case 'integrity': this.keep(ticket, this.gaps, await this.production.costGaps(f)); break;
      }
    } catch (e) {
      if (ticket === this.request) this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      if (ticket === this.request) this.busy.set(false);
    }
  }

  private keep<T>(ticket: number, target: { set(value: T): void }, value: T): void {
    if (ticket === this.request) target.set(value);
  }

  /** A tab is a place (pushed, so Back returns to the previous one); a filter refines it (replaced). */
  go(tab: CostTab): void {
    if (tab === this.tab()) return;
    this.history.set(null);
    this.navigate({ tab, page: null, view: null, state: null, kind: null, source: null, element: null, treatment: null, severity: null }, false);
  }

  set(change: Record<string, string | number | null>, keepPage = false): void {
    this.navigate(keepPage ? change : { ...change, page: null }, true);
  }

  /** The rows a page holds, kept in the address like the page itself; a new size starts at page one. */
  setSize(size: number): void {
    this.set({ size: size === DEFAULT_PAGE_SIZE ? null : size });
  }

  private navigate(change: Record<string, string | number | null>, replace: boolean): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: change, queryParamsHandling: 'merge', replaceUrl: replace });
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
    queueMicrotask(() => document.getElementById('cr-tab-' + next)?.focus());
  }

  async export(): Promise<void> {
    const register = this.register();
    if (!register) return;
    this.exporting.set(true);
    try {
      const blob = await this.production.exportCostRegister(register, this.filter(), this.i18n.lang());
      saveBlob(blob, `manufacturing-${register}.xlsx`);
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.exporting.set(false);
    }
  }

  tone(family: StatusFamily, status: string) {
    return toneOf(family, status);
  }

  elementWord(element: string | null): string {
    return !element || element === 'unclassified' ? this.i18n.t('mfg.b2.treatmentWord.unclassified') : this.i18n.t('mfg.cost.el.' + element);
  }

  money(value: number | null): string {
    return value === null ? '—' : this.i18n.money(value);
  }

  varianceCell(lines: { component: string; adjusted: number | null; original: number | null }[], component: string): string {
    const line = lines.find((l) => l.component === component);
    return line ? this.money(line.adjusted ?? line.original) : '—';
  }

  lastRelease(r: { rows: { carriesRemainder: boolean; unitValue: number; number: string }[] }) {
    return r.rows.find((x) => x.carriesRemainder) ?? null;
  }

  failed<T extends { result: string }>(checks: T[]): T[] {
    return checks.filter((c) => c.result === 'fail');
  }
}
