import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminService, adminErrorMessage, problemCode } from '../../core/admin.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n.service';
import { mayDo } from '../../core/landing';
import { localizedName } from '../../core/multilingual';
import {
  CostPriceRevaluation, GapPosting, ReceiptCostRule, RefundValuation, SalesCostMethod, ValuationAccount, ValuationLine,
  ValuationRow, ZeroCostReceipt,
  ValuationScope, ValuationService,
} from '../../core/valuation.service';
import { DialogComponent } from '../../ui/dialog.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { TableStateComponent } from '../../ui/table-state.component';
import { FinancePagesApi } from './finance/finance-pages.api';
import { PAGE_CHROME, PageChrome } from './shell/page-chrome';

/**
 * Reports › Stock valuation, shadow (P0.4b, owner's choice of 2026-11-08). Stock valued at a moving weighted
 * average since the opening, beside the same quantity at today's cost price — the method the accountant is
 * asked about (C-01), shown before anything switches. Read only: nothing here is posted or changes a price.
 * The three choices the accountant has not answered are settings on the page (owner, 2026-11-08: "make it
 * settings and the admin can choose"); whoever holds accounting.manage changes them and the figures follow.
 * Beside them, the accountant's rule for what a receiving note makes the product's cost price (B-F01, 2026-11-09).
 * The rows, the pending gaps and a product's movements are read a page at a time (owner, 2026-11-17: "paginate in
 * backend, not frontend"; FinancePagesApi); the settings and totals come with every page, the report's whole.
 */
@Component({
  selector: 'app-backoffice-stock-valuation',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, DialogComponent, ListFooterComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        @if (scope() === 'Location' && branches().length > 1) {
          <div class="ui-filter">
            <label for="sv-branch">{{ i18n.t('valuation.branch') }}</label>
            <select id="sv-branch" [(ngModel)]="branchId">
              <option [ngValue]="null">{{ i18n.t('reports.allBranches') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ name(b) }}</option>
              }
            </select>
          </div>
        }
        <!-- The table's own filters (owner, 2026-11-17): they narrow the rows listed, never the figures above. -->
        @if (anyRows()) {
          <div class="ui-filter">
            <label for="sv-f-qty">{{ i18n.t('valuation.qty') }}</label>
            <select id="sv-f-qty" [ngModel]="qtyFilter()" (ngModelChange)="qtyFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="positive">{{ i18n.t('valuation.filter.inStock') }}</option>
              <option ngValue="zero">{{ i18n.t('valuation.filter.zero') }}</option>
              <option ngValue="negative">{{ i18n.t('valuation.filter.negative') }}</option>
            </select>
          </div>
          <div class="ui-filter">
            <label for="sv-f-diff">{{ i18n.t('valuation.difference') }}</label>
            <select id="sv-f-diff" [ngModel]="differenceFilter()" (ngModelChange)="differenceFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="above">{{ i18n.t('valuation.filter.above') }}</option>
              <option ngValue="below">{{ i18n.t('valuation.filter.below') }}</option>
              <option ngValue="none">{{ i18n.t('valuation.filter.none') }}</option>
            </select>
          </div>
          <div class="ui-filter">
            <label for="sv-f-notes">{{ i18n.t('valuation.notes') }}</label>
            <select id="sv-f-notes" [ngModel]="notesFilter()" (ngModelChange)="notesFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="yes">{{ i18n.t('valuation.filter.withNotes') }}</option>
              <option ngValue="no">{{ i18n.t('valuation.filter.noNotes') }}</option>
            </select>
          </div>
        }
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-primary" [disabled]="busy()" (click)="reload()">{{ i18n.t('valuation.show') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      <p class="notice info">{{ i18n.t(costingOn() ? 'valuation.shadowNoteOn' : 'valuation.shadowNote') }}</p>
      @if (problem() && report()) {
        <p class="notice danger" role="alert">{{ problem() }}</p>
      }

      @if (report(); as r) {
        @if (!r.settings.startedAtUtc) {
          <p class="notice warn">{{ i18n.t('valuation.notStarted') }}</p>
        } @else {
          <p class="ui-sub">{{ i18n.t('valuation.since', { date: i18n.dateTime(r.settings.startedAtUtc) }) }}</p>
        }
        <div class="ui-metrics">
          <div class="ui-metric ui-metric--lead">
            <span class="ui-metric__label">{{ i18n.t('valuation.atAverage') }}</span>
            <strong class="ui-metric__value mono">{{ i18n.money(r.totals.closingValue + r.totals.inTransitValue) }}</strong>
            @if (r.totals.inTransitValue) {
              <span class="ui-metric__note">{{ i18n.t('valuation.inTransit', { value: i18n.money(r.totals.inTransitValue) }) }}</span>
            }
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('valuation.atCostPrice') }}</span>
            <strong class="ui-metric__value mono">{{ i18n.money(r.totals.referenceValue) }}</strong>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('valuation.difference') }}</span>
            <strong class="ui-metric__value mono">{{ i18n.money(r.totals.difference) }}</strong>
            <span class="ui-metric__note">{{ i18n.t('valuation.differenceNote') }}</span>
          </div>
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('valuation.costOfSales') }}</span>
            <strong class="ui-metric__value mono">{{ i18n.money(r.totals.shadowCostOfSales) }}</strong>
            <span class="ui-metric__note">{{ i18n.t('valuation.costOfSalesPosted', { value: i18n.money(r.totals.postedCostOfSales) }) }}</span>
          </div>
          @if (r.totals.negativeStockGap) {
            <div class="ui-metric">
              <span class="ui-metric__label">{{ i18n.t('valuation.gap') }}</span>
              <strong class="ui-metric__value mono">{{ i18n.money(r.totals.negativeStockGap) }}</strong>
              <span class="ui-metric__note">{{ i18n.t('valuation.gapNote') }}</span>
            </div>
          }
        </div>

        <section class="ui-surface" aria-labelledby="sv-settings-title">
          <header class="bo-panel__head">
            <h2 id="sv-settings-title" class="bo-panel__title">{{ i18n.t('valuation.settings.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('valuation.settings.lead') }}</p>
          </header>
          <div class="settings">
          <div class="settings-grid">
            <div class="field">
              <label for="sv-scope">{{ i18n.t('valuation.settings.scope') }}</label>
              <select id="sv-scope" [(ngModel)]="draftScope" [disabled]="!canEdit() || saving() || costingOn()" aria-describedby="sv-scope-help">
                <option value="Company">{{ i18n.t('valuation.scope.Company') }}</option>
                <option value="Location">{{ i18n.t('valuation.scope.Location') }}</option>
              </select>
              <small id="sv-scope-help" class="help">{{ i18n.t('valuation.settings.scopeHelp') }}</small>
            </div>
            <div class="field">
              <label for="sv-refund">{{ i18n.t('valuation.settings.refund') }}</label>
              <select id="sv-refund" [(ngModel)]="draftRefund" [disabled]="!canEdit() || saving() || costingOn()" aria-describedby="sv-refund-help">
                <option value="SaleCost">{{ i18n.t('valuation.refund.SaleCost') }}</option>
                <option value="CurrentAverage">{{ i18n.t('valuation.refund.CurrentAverage') }}</option>
              </select>
              <small id="sv-refund-help" class="help">{{ i18n.t('valuation.settings.refundHelp') }}</small>
            </div>
            <div class="field">
              <label for="sv-decimals">{{ i18n.t('valuation.settings.decimals') }}</label>
              <select id="sv-decimals" [(ngModel)]="draftDecimals" [disabled]="!canEdit() || saving() || costingOn()" aria-describedby="sv-decimals-help">
                @for (d of decimalChoices; track d) {
                  <option [ngValue]="d">{{ d }}</option>
                }
              </select>
              <small id="sv-decimals-help" class="help">{{ i18n.t('valuation.settings.decimalsHelp') }}</small>
            </div>
            <div class="field">
              <label for="sv-direct-invoice">{{ i18n.t('valuation.settings.directInvoice') }}</label>
              <!-- P0.3: not locked by the sales-cost switch; it only changes what the next direct invoice does. -->
              <select id="sv-direct-invoice" [(ngModel)]="draftDirectInvoiceStock" [disabled]="!canEdit() || saving()" aria-describedby="sv-direct-invoice-help">
                <option [ngValue]="false">{{ i18n.t('valuation.directInvoice.off') }}</option>
                <option [ngValue]="true">{{ i18n.t('valuation.directInvoice.on') }}</option>
              </select>
              <small id="sv-direct-invoice-help" class="help">{{ i18n.t('valuation.settings.directInvoiceHelp') }}</small>
            </div>
          </div>
          @if (canEdit()) {
            <div class="settings-foot">
              @if (r.settings.updatedAtUtc) {
                <span class="ui-sub">{{ i18n.t('valuation.settings.updated', { date: i18n.dateTime(r.settings.updatedAtUtc), who: r.settings.updatedBy ?? '—' }) }}</span>
              }
              <button type="button" class="btn-primary" [disabled]="saving() || !dirty()" (click)="save()">{{ i18n.t('valuation.settings.save') }}</button>
            </div>
          } @else {
            <p class="ui-sub">{{ i18n.t('valuation.settings.readOnly') }}</p>
          }
          @if (costingOn()) {
            <p class="ui-sub">{{ i18n.t('valuation.cost.locked') }}</p>
          }
          </div>
        </section>

        <section class="ui-surface" aria-labelledby="sv-receipt-title">
          <header class="bo-panel__head">
            <h2 id="sv-receipt-title" class="bo-panel__title">{{ i18n.t('valuation.receipt.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('valuation.receipt.lead') }}</p>
          </header>
          <div class="settings">
            <div class="settings-grid">
              <div class="field">
                <label for="sv-receipt">{{ i18n.t('valuation.receipt.label') }}</label>
                <select id="sv-receipt" [(ngModel)]="draftReceipt" [disabled]="!canEdit() || saving()" aria-describedby="sv-receipt-help">
                  @for (rule of receiptRules; track rule) {
                    <option [value]="rule">{{ i18n.t('valuation.receipt.rule.' + rule) }}</option>
                  }
                </select>
                <small id="sv-receipt-help" class="help">{{ i18n.t('valuation.receipt.help.' + draftReceipt) }}</small>
              </div>
            </div>
            <p class="ui-sub">{{ i18n.t('valuation.receipt.zero') }}</p>
            @if (canEdit()) {
              <div class="settings-foot">
                <button type="button" class="btn-primary" [disabled]="saving() || !receiptDirty()" (click)="saveReceipt()">{{ i18n.t('valuation.receipt.save') }}</button>
              </div>
            } @else {
              <p class="ui-sub">{{ i18n.t('valuation.settings.readOnly') }}</p>
            }
          </div>
        </section>

        <section class="ui-surface" aria-labelledby="sv-postings-title">
          <header class="bo-panel__head">
            <h2 id="sv-postings-title" class="bo-panel__title">{{ i18n.t('valuation.postings.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('valuation.postings.lead') }}</p>
          </header>
          <div class="settings">
            <div class="settings-grid">
              <div class="field">
                <label for="sv-gap-posting">{{ i18n.t('valuation.postings.gaps.label') }}</label>
                <select id="sv-gap-posting" [(ngModel)]="draftGapPosting" [disabled]="!canEdit() || saving()" aria-describedby="sv-gap-posting-help">
                  @for (o of gapPostings; track o) {
                    <option [value]="o">{{ i18n.t('valuation.postings.gaps.' + o) }}</option>
                  }
                </select>
                <small id="sv-gap-posting-help" class="help">{{ i18n.t('valuation.postings.gapsHelp.' + draftGapPosting) }}</small>
              </div>
              <div class="field">
                <label for="sv-revaluation">{{ i18n.t('valuation.postings.revaluation.label') }}</label>
                <select id="sv-revaluation" [(ngModel)]="draftRevaluation" [disabled]="!canEdit() || saving()" aria-describedby="sv-revaluation-help">
                  @for (o of revaluations; track o) {
                    <option [value]="o">{{ i18n.t('valuation.postings.revaluation.' + o) }}</option>
                  }
                </select>
                <small id="sv-revaluation-help" class="help">{{ i18n.t('valuation.postings.revaluationHelp.' + draftRevaluation) }}</small>
              </div>
              <div class="field">
                <label for="sv-zero-cost">{{ i18n.t('valuation.postings.zero.label') }}</label>
                <select id="sv-zero-cost" [(ngModel)]="draftZeroCost" [disabled]="!canEdit() || saving()" aria-describedby="sv-zero-cost-help">
                  @for (o of zeroCosts; track o) {
                    <option [value]="o">{{ i18n.t('valuation.postings.zero.' + o) }}</option>
                  }
                </select>
                <small id="sv-zero-cost-help" class="help">{{ i18n.t('valuation.postings.zeroHelp.' + draftZeroCost) }}</small>
              </div>
            </div>
            @if (draftRevaluation === 'StockOnHand' && costingOn()) {
              <p class="ui-sub">{{ i18n.t('valuation.postings.revaluationAverage') }}</p>
            }
            @if (revaluationUnmapped()) {
              <p class="notice warn">{{ i18n.t('valuation.postings.differenceUnmapped') }}</p>
            }
            @if (canEdit()) {
              <div class="settings-foot">
                <button type="button" class="btn-primary" [disabled]="saving() || !postingsDirty() || revaluationUnmapped()" (click)="savePostings()">
                  {{ i18n.t('valuation.postings.save') }}
                </button>
              </div>
            } @else {
              <p class="ui-sub">{{ i18n.t('valuation.settings.readOnly') }}</p>
            }
          </div>
        </section>

        <section class="ui-surface" aria-labelledby="sv-cost-title">
          <header class="bo-panel__head">
            <h2 id="sv-cost-title" class="bo-panel__title">{{ i18n.t('valuation.cost.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('valuation.cost.lead') }}</p>
          </header>
          <div class="settings">
            <p class="cost-state">
              <span>{{ i18n.t('valuation.cost.current') }}</span>
              <strong data-testid="sales-cost-method">{{ i18n.t('valuation.cost.method.' + (r.settings.salesCost ?? 'CostPrice')) }}</strong>
              @if (r.settings.salesCostChangedAtUtc) {
                <span class="ui-sub">{{ i18n.t('valuation.cost.changed', { date: i18n.dateTime(r.settings.salesCostChangedAtUtc), who: r.settings.salesCostChangedBy ?? '—' }) }}</span>
              }
            </p>
            @if (!canEdit()) {
              <p class="ui-sub">{{ i18n.t('valuation.cost.readOnly') }}</p>
            } @else if (costProblem() && !confirming()) {
              <p class="notice danger" role="alert">{{ costProblem() }}</p>
            }
            @if (canEdit() && costing(); as c) {
              <dl class="cost-figures">
                <div>
                  <dt>{{ i18n.t('valuation.cost.shadowValue') }}</dt>
                  <dd class="mono">{{ i18n.money(c.shadowValue) }}</dd>
                </div>
                <div>
                  <dt>{{ i18n.t('valuation.cost.bookValue', { accounts: c.stockAccounts.join(', ') }) }}</dt>
                  <dd class="mono">{{ i18n.money(c.bookValue) }}</dd>
                </div>
                <div>
                  <dt>{{ i18n.t(costingOn() ? 'valuation.cost.drift' : 'valuation.cost.adjustment') }}</dt>
                  <dd class="mono">{{ i18n.money(c.adjustment) }}</dd>
                </div>
              </dl>
              @if (!costingOn()) {
                @if (c.openingAdjustmentAccount.mapped) {
                  <p class="ui-sub">{{ i18n.t('valuation.cost.postsTo', { account: accountLabel(c.openingAdjustmentAccount) }) }}</p>
                } @else {
                  <p class="notice warn">{{ i18n.t('valuation.cost.openingUnmapped') }}</p>
                }
                <div class="settings-foot">
                  <button type="button" class="btn-primary" [disabled]="costBusy() || !c.openingAdjustmentAccount.mapped" (click)="askSwitch('WeightedAverage')">
                    {{ i18n.t('valuation.cost.switchOn') }}
                  </button>
                </div>
              } @else {
                <p class="ui-sub">{{ i18n.t('valuation.cost.driftNote') }}</p>
                @if (c.fallbackLines) {
                  <p class="notice warn">{{ i18n.t('valuation.cost.fallbackLines', { n: c.fallbackLines }) }}</p>
                }
                @if (c.fallbackMovements) {
                  <p class="notice warn">{{ i18n.t('valuation.cost.fallbackMovements', { n: c.fallbackMovements }) }}</p>
                }
                <h3 class="cost-sub">{{ i18n.t('valuation.cost.gapsTitle') }}</h3>
                @if (!gapPager.total()) {
                  <p class="ui-sub">{{ i18n.t('valuation.cost.noGaps') }}</p>
                } @else {
                  <div class="ui-scroll">
                    <table class="ui-table ui-table--cards">
                      <thead>
                        <tr>
                          <th>{{ i18n.t('valuation.item') }}</th>
                          <th>{{ i18n.t('valuation.detail.at') }}</th>
                          <th>{{ i18n.t('valuation.detail.kind') }}</th>
                          <th class="num">{{ i18n.t('valuation.cost.gapAmount') }}</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (g of gapPager.rows(); track g.movementId) {
                          <tr>
                            <td [attr.data-label]="i18n.t('valuation.item')">
                              {{ name(g) || g.sku }}
                              <span class="ui-sub mono" dir="ltr">{{ g.sku }}</span>
                            </td>
                            <td class="mono" [attr.data-label]="i18n.t('valuation.detail.at')">{{ i18n.dateTime(g.atUtc) }}</td>
                            <td [attr.data-label]="i18n.t('valuation.detail.kind')">
                              {{ i18n.t('valuation.kind.' + g.kind) }}
                              @if (g.documentNumber) {
                                <span class="ui-sub mono" dir="ltr">{{ g.documentNumber }}</span>
                              }
                            </td>
                            <td class="num mono" [attr.data-label]="i18n.t('valuation.cost.gapAmount')">{{ i18n.money(g.amount) }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </div>
                  <!-- Paged only (owner, 2026-11-17): the line below totals every gap, so no filter narrows them. -->
                  <app-list-footer [pager]="gapPager" />
                  @if (c.valuationDifferenceAccount.mapped) {
                    <p class="ui-sub">{{ i18n.t('valuation.cost.gapsPostTo', { account: accountLabel(c.valuationDifferenceAccount), total: i18n.money(c.pendingGapTotal) }) }}</p>
                  } @else {
                    <p class="notice warn">{{ i18n.t('valuation.cost.differenceUnmapped') }}</p>
                  }
                }
                <div class="settings-foot">
                  @if (gapPager.total()) {
                    <button type="button" class="btn-primary" [disabled]="costBusy() || !c.valuationDifferenceAccount.mapped" (click)="postGaps()">
                      {{ i18n.t('valuation.cost.postGaps') }}
                    </button>
                  }
                  <button type="button" class="btn-ghost" [disabled]="costBusy()" (click)="askSwitch('CostPrice')">{{ i18n.t('valuation.cost.switchOff') }}</button>
                </div>
              }
            }
          </div>
        </section>
      }

      <section class="ui-surface" [attr.aria-busy]="busy()">
        @if (!pager.loaded() && !problem()) {
          <app-table-state kind="loading" />
        } @else if (problem() && !report()) {
          <app-table-state kind="error" [message]="problem()" (retry)="reload()" />
        } @else if (!anyRows()) {
          <app-table-state kind="empty" [message]="i18n.t('valuation.empty')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('valuation.item') }}</th>
                  @if (scope() === 'Location') {
                    <th>{{ i18n.t('valuation.branch') }}</th>
                  }
                  <th class="num">{{ i18n.t('valuation.qty') }}</th>
                  <th class="num">{{ i18n.t('valuation.average') }}</th>
                  <th class="num">{{ i18n.t('valuation.value') }}</th>
                  <th class="num">{{ i18n.t('valuation.costPrice') }}</th>
                  <th class="num">{{ i18n.t('valuation.valueAtCostPrice') }}</th>
                  <th class="num">{{ i18n.t('valuation.difference') }}</th>
                  <th>{{ i18n.t('valuation.notes') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (x of pager.rows(); track x.productId + (x.branchId ?? '')) {
                  <tr>
                    <td [attr.data-label]="i18n.t('valuation.item')">
                      <button type="button" class="ui-link" (click)="open(x)">{{ name(x) || x.sku }}</button>
                      <span class="ui-sub mono" dir="ltr">{{ x.sku }}</span>
                    </td>
                    @if (scope() === 'Location') {
                      <td [attr.data-label]="i18n.t('valuation.branch')">{{ branchOf(x) }}</td>
                    }
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.qty')">{{ x.closingQty }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.average')">{{ x.averageCost === null ? '—' : i18n.money(x.averageCost) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.value')">{{ i18n.money(x.closingValue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.costPrice')">{{ i18n.money(x.referenceCost) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.valueAtCostPrice')">{{ i18n.money(x.referenceValue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.difference')">{{ i18n.money(x.difference) }}</td>
                    <td [attr.data-label]="i18n.t('valuation.notes')">
                      @for (n of rowNotes(x); track n) {
                        <span class="ui-sub">{{ n }}</span>
                      } @empty {
                        —
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
    </div>

    <app-dialog [open]="!!confirming()" [heading]="i18n.t(confirming() === 'WeightedAverage' ? 'valuation.cost.switchOn' : 'valuation.cost.switchOff')" (dismiss)="closeConfirm()">
      @if (confirming() === 'WeightedAverage' && costing(); as c) {
        <p>{{ i18n.t('valuation.cost.confirmOn', { amount: i18n.money(c.adjustment), account: accountLabel(c.openingAdjustmentAccount) }) }}</p>
        <p class="ui-sub">{{ i18n.t('valuation.cost.confirmOnNote') }}</p>
      } @else {
        <p>{{ i18n.t('valuation.cost.confirmOff') }}</p>
      }
      @if (costError()) {
        <p class="notice danger" role="alert">{{ costError() }}</p>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="closeConfirm()">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="costBusy()" (click)="confirmSwitch()">{{ i18n.t('valuation.cost.confirm') }}</button>
      </div>
    </app-dialog>

    <app-dialog [open]="!!detail() || detailBusy()" size="lg" [heading]="detailHeading()" (dismiss)="closeDetail()">
      @if (detailBusy() && !detail()) {
        <app-table-state kind="loading" />
      } @else if (detailError()) {
        <p class="notice danger" role="alert">{{ detailError() }}</p>
      } @else if (detail(); as d) {
        @if (!d.lineCount) {
          <app-table-state kind="empty" [message]="i18n.t('valuation.detail.empty')" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('valuation.detail.at') }}</th>
                  <th>{{ i18n.t('valuation.detail.kind') }}</th>
                  <th>{{ i18n.t('valuation.branch') }}</th>
                  <th class="num">{{ i18n.t('valuation.detail.in') }}</th>
                  <th class="num">{{ i18n.t('valuation.detail.out') }}</th>
                  <th class="num">{{ i18n.t('valuation.detail.postedCost') }}</th>
                  <th class="num">{{ i18n.t('valuation.detail.shadowCost') }}</th>
                  <th class="num">{{ i18n.t('valuation.value') }}</th>
                  <th class="num">{{ i18n.t('valuation.detail.qtyAfter') }}</th>
                  <th class="num">{{ i18n.t('valuation.detail.averageAfter') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (l of detailPager.rows(); track l.movementId) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('valuation.detail.at')">{{ i18n.dateTime(l.atUtc) }}</td>
                    <td [attr.data-label]="i18n.t('valuation.detail.kind')">
                      {{ i18n.t('valuation.kind.' + l.kind) }}
                      @if (l.documentNumber) {
                        <span class="ui-sub mono" dir="ltr">{{ l.documentNumber }}</span>
                      }
                      @for (n of l.notes; track n) {
                        <span class="ui-sub">{{ i18n.t('valuation.note.' + n) }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('valuation.branch')">{{ lineBranch(l) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.detail.in')">{{ l.qtyIn || '—' }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.detail.out')">{{ l.qtyOut || '—' }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.detail.postedCost')">{{ l.referenceUnitCost === null ? '—' : i18n.money(l.referenceUnitCost) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.detail.shadowCost')">{{ l.shadowUnitCost === null ? '—' : i18n.money(l.shadowUnitCost) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.value')">{{ i18n.money(l.shadowValue) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.detail.qtyAfter')">{{ l.poolQtyAfter }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('valuation.detail.averageAfter')">{{ l.averageAfter === null ? '—' : i18n.money(l.averageAfter) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <!-- Paged only (owner, 2026-11-17): each line carries the quantity and average after it, which a filter would skip. -->
          <app-list-footer [pager]="detailPager" />
        }
      }
    </app-dialog>
  `,
  styleUrls: ['./backoffice-ui.scss', '../../ui/ui-patterns.scss'],
  styles: [
    `
      .settings {
        padding: var(--sp-4);
        display: grid;
        gap: var(--sp-3);
      }
      .settings-grid {
        display: grid;
        gap: var(--sp-4);
        grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr));
      }
      .field {
        display: grid;
        gap: var(--sp-1);
        align-content: start;
      }
      .help {
        color: var(--muted);
      }
      .settings-foot {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: flex-end;
        gap: var(--sp-3);
      }
      td .ui-sub {
        display: block;
      }
      .cost-state {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: var(--sp-2);
        margin: 0;
      }
      .cost-figures {
        display: grid;
        gap: var(--sp-3);
        grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr));
        margin: 0;
      }
      .cost-figures dt {
        color: var(--muted);
        font-size: var(--fs-sm);
      }
      .cost-figures dd {
        margin: 0;
        font-weight: 600;
      }
      .cost-sub {
        margin: 0;
        font-size: var(--fs-card);
        font-weight: 600;
      }
      @media (max-width: 640px) {
        .ui-metric--lead {
          grid-column: 1 / -1;
        }
      }
    `,
  ],
})
export class BackofficeStockValuationComponent implements OnInit {
  private readonly valuation = inject(ValuationService);
  private readonly admin = inject(AdminService);
  private readonly auth = inject(AuthService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  private readonly pages = inject(FinancePagesApi);

  readonly branches = signal<AdminBranch[]>([]);
  readonly saving = signal(false);
  /** What a save could not do, in words. */
  readonly error = signal<string | null>(null);
  /** The product whose movements are open. */
  readonly detailRow = signal<ValuationRow | null>(null);

  readonly costBusy = signal(false);
  /** What the switch or the gaps' posting answered, in the app's words. */
  readonly costError = signal<string | null>(null);
  readonly confirming = signal<SalesCostMethod | null>(null);

  readonly canEdit = computed(() => mayDo(this.auth.currentUser(), 'accounting.manage'));
  readonly decimalChoices = [0, 1, 2, 3, 4, 5, 6];

  // ---------- pages and filters (owner, 2026-11-17) ----------
  // Ten rows to begin with; the footer offers 20, 50 or 100. Every page, size or filter change is a
  // request ("paginate in backend, not frontend"); the branch is the one last shown. The pending gaps
  // (a total line below them) and a product's movements (a running quantity and average) page but
  // take no filter.

  readonly qtyFilter = signal<'all' | 'positive' | 'zero' | 'negative'>('all');
  /** The difference column's sign: the average cost above, below or level with the cost price. */
  readonly differenceFilter = signal<'all' | 'above' | 'below' | 'none'>('all');
  readonly notesFilter = signal<'all' | 'yes' | 'no'>('all');

  readonly filtered = computed(() => this.qtyFilter() !== 'all' || this.differenceFilter() !== 'all' || this.notesFilter() !== 'all');

  /** The branch last asked for with "Show": the select is a draft until then. */
  private readonly appliedBranch = signal<string | null>(null);
  /** Set by every read the user asked for (and at first): the next answer's settings go back into the drafts. */
  private draftsPending = true;

  readonly pager = createServerPager(
    (page) => this.pages.valuationPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'valuation.rows',
      filters: () => ({
        branchId: this.appliedBranch(),
        qty: this.qtyFilter(),
        difference: this.differenceFilter(),
        notes: this.notesFilter(),
      }),
    },
  );

  /** The report's settings and totals: the whole report's, whatever page or filter is shown. */
  readonly report = computed(() => this.pager.summary());
  /** Whether anything is valued at all, before the filters. */
  readonly anyRows = computed(() => (this.report()?.all ?? 0) > 0);
  readonly busy = computed(() => this.pager.loading());
  /** What went wrong, in words: a save, or reading the report. */
  readonly problem = computed(() => {
    const e = this.pager.error();
    return this.error() ?? (e ? this.message(e) : null);
  });
  readonly scope = computed<ValuationScope>(() => this.report()?.settings.scope ?? 'Company');
  readonly costingOn = computed(() => (this.report()?.settings.salesCost ?? 'CostPrice') === 'WeightedAverage');

  clearFilters(): void {
    this.qtyFilter.set('all');
    this.differenceFilter.set('all');
    this.notesFilter.set('all');
  }

  /** How sales are costed and the pending gaps (C-01), a page of gaps at a time: read only by whoever manages the accounts. */
  readonly gapPager = createServerPager(
    (page) => this.pages.salesCostGapsPage({ skip: page.skip, take: page.take }),
    { key: 'valuation.gaps', filters: () => ({}), enabled: () => this.canEdit() },
  );
  /** The costing status, its figures and accounts; its gaps are the pager's rows. */
  readonly costing = computed(() => this.gapPager.summary());
  readonly costProblem = computed(() => {
    const e = this.gapPager.error();
    return this.costError() ?? (e ? this.costMessage(e) : null);
  });

  readonly detailPager = createServerPager(
    (page) => this.pages.valuationProductPage(page.filters.productId, { skip: page.skip, take: page.take }),
    {
      key: 'valuation.movements',
      filters: () => ({ productId: this.detailRow()?.productId ?? '' }),
      enabled: () => this.detailRow() !== null,
    },
  );
  /** The open product's movements, once the server has answered for that product. */
  readonly detail = computed(() => {
    const d = this.detailPager.summary();
    const row = this.detailRow();
    return d && row && d.productId.toLowerCase() === row.productId.toLowerCase() ? d : null;
  });
  readonly detailBusy = computed(() => this.detailRow() !== null && this.detail() === null && !this.detailPager.error());
  readonly detailError = computed(() => {
    const e = this.detailRow() ? this.detailPager.error() : null;
    return e ? this.message(e) : null;
  });

  branchId: string | null = null;
  draftScope: ValuationScope = 'Company';
  draftRefund: RefundValuation = 'SaleCost';
  draftDecimals = 3;
  /** P0.3: whether a direct invoice takes its goods out of stock and posts their cost; off by default. */
  draftDirectInvoiceStock = false;
  draftReceipt: ReceiptCostRule = 'LastCost';
  readonly receiptRules: readonly ReceiptCostRule[] = ['LastCost', 'WeightedAverage', 'Unchanged'];
  /** Three of the accountant's choices (2026-11-09); each default keeps today's behaviour. */
  draftGapPosting: GapPosting = 'Accountant';
  draftRevaluation: CostPriceRevaluation = 'None';
  draftZeroCost: ZeroCostReceipt = 'Zero';
  readonly gapPostings: readonly GapPosting[] = ['Accountant', 'Automatic'];
  readonly revaluations: readonly CostPriceRevaluation[] = ['None', 'StockOnHand'];
  readonly zeroCosts: readonly ZeroCostReceipt[] = ['Zero', 'CostPrice'];

  readonly detailHeading = computed(() => {
    const r = this.detailRow();
    return r ? `${this.name(r) || r.sku} — ${this.i18n.t('valuation.detail.title')}` : this.i18n.t('valuation.detail.title');
  });

  constructor() {
    // The settings as saved go back into the drafts on the answer to every read the user asked for.
    effect(() => {
      const r = this.report();
      if (!r || !this.draftsPending) return;
      untracked(() => {
        this.draftsPending = false;
        this.draftScope = r.settings.scope;
        this.draftRefund = r.settings.refundCost;
        this.draftDecimals = r.settings.unitCostDecimals;
        this.draftDirectInvoiceStock = r.settings.directInvoiceStock ?? false;
        this.draftReceipt = r.settings.receiptCostRule ?? 'LastCost';
        this.draftGapPosting = r.settings.gapPosting ?? 'Accountant';
        this.draftRevaluation = r.settings.costPriceRevaluation ?? 'None';
        this.draftZeroCost = r.settings.zeroCostReceipt ?? 'Zero';
        if (r.settings.scope !== 'Location') this.branchId = null;
      });
    });
  }

  ngOnInit(): void {
    this.chrome?.description.set(this.i18n.t('valuation.lead'));
    this.admin.listBranches().then((b) => this.branches.set(b), () => undefined);
    // The report and the costing load through their pagers, from their first page.
  }

  /** "Show", and after every save: the branch in the select, the report's page and the costing asked again. */
  async reload(): Promise<void> {
    this.draftsPending = true;
    this.error.set(null);
    if (this.branchId !== this.appliedBranch()) this.appliedBranch.set(this.branchId);
    else await this.pager.reload();
    if (this.canEdit()) await this.loadCosting();
  }

  async loadCosting(): Promise<void> {
    this.costError.set(null);
    await this.gapPager.reload();
  }

  askSwitch(method: SalesCostMethod): void {
    this.costError.set(null);
    this.confirming.set(method);
  }

  closeConfirm(): void {
    this.confirming.set(null);
    this.costError.set(null);
  }

  /**
   * Switches with the figure the accountant was shown. If it moved meanwhile the server posts nothing and answers the new
   * figure: the dialog stays open on it, said in words, to be confirmed again.
   */
  async confirmSwitch(): Promise<void> {
    const method = this.confirming();
    if (!method) return;
    this.costBusy.set(true);
    this.costError.set(null);
    try {
      const expected = method === 'WeightedAverage' ? (this.costing()?.adjustment ?? null) : null;
      await this.valuation.switchSalesCost(method, expected);
      this.confirming.set(null);
      await this.reload();
    } catch (e) {
      if (problemCode(e) === 'adjustment_changed') await this.loadCosting();
      this.costError.set(this.costMessage(e));
    } finally {
      this.costBusy.set(false);
    }
  }

  async postGaps(): Promise<void> {
    this.costBusy.set(true);
    this.costError.set(null);
    try {
      await this.valuation.postGaps();
      await this.gapPager.reload();
    } catch (e) {
      this.costError.set(this.costMessage(e));
    } finally {
      this.costBusy.set(false);
    }
  }

  /** The switch's refusals, in the app's words; anything else as the server said it. */
  private costMessage(e: unknown): string {
    const code = problemCode(e);
    const known = ['adjustment_changed', 'opening_adjustment_unmapped', 'valuation_difference_unmapped', 'no_gaps', 'sales_cost_unchanged'];
    if (code && known.includes(code)) return this.i18n.t('valuation.cost.error.' + code);
    return this.message(e);
  }

  accountLabel(a: ValuationAccount): string {
    const name = localizedName({ nameAr: a.nameAr ?? '', nameFr: a.nameFr ?? '', nameEn: a.nameEn ?? '' }, this.i18n.lang());
    return [a.code, name].filter((x) => !!x).join(' — ');
  }

  dirty(): boolean {
    const s = this.report()?.settings;
    return (
      !!s &&
      (s.scope !== this.draftScope ||
        s.refundCost !== this.draftRefund ||
        s.unitCostDecimals !== this.draftDecimals ||
        (s.directInvoiceStock ?? false) !== this.draftDirectInvoiceStock)
    );
  }

  async save(): Promise<void> {
    await this.persist({
      scope: this.draftScope, refundCost: this.draftRefund, unitCostDecimals: this.draftDecimals,
      directInvoiceStock: this.draftDirectInvoiceStock,
    });
  }

  receiptDirty(): boolean {
    const s = this.report()?.settings;
    return !!s && (s.receiptCostRule ?? 'LastCost') !== this.draftReceipt;
  }

  /**
   * The cost price on receipt (B-F01) is saved on its own: the shadow's choices go back as they are saved, so a pending
   * edit there is not sent with it, and it is not locked while sales post the average (it changes no movement).
   */
  async saveReceipt(): Promise<void> {
    const s = this.report()?.settings;
    if (!s) return;
    await this.persist({ scope: s.scope, refundCost: s.refundCost, unitCostDecimals: s.unitCostDecimals, receiptCostRule: this.draftReceipt });
  }

  postingsDirty(): boolean {
    const s = this.report()?.settings;
    return (
      !!s &&
      ((s.gapPosting ?? 'Accountant') !== this.draftGapPosting ||
        (s.costPriceRevaluation ?? 'None') !== this.draftRevaluation ||
        (s.zeroCostReceipt ?? 'Zero') !== this.draftZeroCost)
    );
  }

  /**
   * Revaluing the stock on hand is chosen only while the valuation-difference account is named: the server refuses it
   * otherwise, and the page says why before anyone presses save.
   */
  revaluationUnmapped(): boolean {
    const saved = this.report()?.settings.costPriceRevaluation ?? 'None';
    return this.draftRevaluation === 'StockOnHand' && saved !== 'StockOnHand' && this.costing()?.valuationDifferenceAccount.mapped === false;
  }

  /**
   * The three valuation postings are saved on their own, like the cost price on receipt: the shadow's choices go back as
   * they are saved, and none of the three is locked while sales post the average (none changes a movement).
   */
  async savePostings(): Promise<void> {
    const s = this.report()?.settings;
    if (!s) return;
    await this.persist({
      scope: s.scope, refundCost: s.refundCost, unitCostDecimals: s.unitCostDecimals,
      gapPosting: this.draftGapPosting, costPriceRevaluation: this.draftRevaluation, zeroCostReceipt: this.draftZeroCost,
    });
  }

  private async persist(req: Parameters<ValuationService['saveSettings']>[0]): Promise<void> {
    this.saving.set(true);
    this.error.set(null);
    try {
      await this.valuation.saveSettings(req);
      await this.reload();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }

  /** A product's movements, read a page at a time through their pager. */
  open(row: ValuationRow): void {
    this.detailRow.set(row);
  }

  closeDetail(): void {
    this.detailRow.set(null);
  }

  /** A branch user under one company-wide average is refused by the server; said in the app's words. */
  private message(e: unknown): string {
    return problemCode(e) === 'valuation_company_wide' ? this.i18n.t('valuation.companyWide') : adminErrorMessage(e);
  }

  /** What the figures of a row rest on that the reader should know, in words. */
  rowNotes(x: ValuationRow): string[] {
    const notes: string[] = [];
    if (x.outputsAtReference) notes.push(this.i18n.t('valuation.flag.outputs', { n: x.outputsAtReference }));
    if (x.outflowsWithoutAverage) notes.push(this.i18n.t('valuation.flag.noAverage', { n: x.outflowsWithoutAverage }));
    if (x.negativeStockGap) notes.push(this.i18n.t('valuation.flag.gap', { value: this.i18n.money(x.negativeStockGap) }));
    if (x.unclassified) notes.push(this.i18n.t('valuation.flag.unclassified', { n: x.unclassified }));
    return notes;
  }

  name(x: { nameAr?: string; nameFr?: string; nameEn?: string }): string {
    return localizedName(x, this.i18n.lang());
  }

  branchOf(x: ValuationRow): string {
    return localizedName({ nameAr: x.branchNameAr ?? '', nameFr: x.branchNameFr ?? '', nameEn: x.branchNameEn ?? '' }, this.i18n.lang()) || '—';
  }

  lineBranch(l: ValuationLine): string {
    return localizedName({ nameAr: l.branchNameAr ?? '', nameFr: l.branchNameFr ?? '', nameEn: l.branchNameEn ?? '' }, this.i18n.lang()) || '—';
  }
}

/** Whether a row has anything under "good to know": the same four conditions as `rowNotes`. */
function hasNotes(x: ValuationRow): boolean {
  return !!(x.outputsAtReference || x.outflowsWithoutAverage || x.negativeStockGap || x.unclassified);
}
