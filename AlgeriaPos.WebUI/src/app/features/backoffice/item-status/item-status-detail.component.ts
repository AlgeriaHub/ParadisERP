import { Component, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminBranch } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { StatusComponent } from '../../../ui/status.component';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { MultilingualName, formatQty, localizedName } from '../backoffice-format';
import {
  ItemInTransitRow,
  ItemMovementRow,
  ItemStatusDetail,
  ItemStatusQuery,
} from './item-status-api.service';
import { ItemStatusPagesApi } from './item-status-pages.api';

@Component({
  selector: 'app-item-status-detail',
  standalone: true,
  imports: [FormsModule, RouterLink, StatusComponent, DateInputComponent, ListFooterComponent, TableStateComponent],
  template: `
    @if (detail(); as d) {
      <section class="bo-panel bo-detail-panel">
        <div class="bo-panel__head bo-detail-head">
          <div>
            <div class="bo-detail-titles">
              <h3 class="bo-panel__title">{{ itemName() }}</h3>
              <span class="mono bo-detail-sku">{{ d.item.sku }}</span>
              <span class="bo-detail-unit">({{ d.item.unit }})</span>
            </div>
            @if (d.item.barcode) {
              <p class="bo-panel__note mono">{{ d.item.barcode }}</p>
            }
          </div>
          <button type="button" class="btn-ghost" (click)="close.emit()">
            {{ i18n.t('itemStatus.detail.close') }}
          </button>
        </div>

        <div class="bo-panel__body">
          <!-- Metric tiles -->
          <div class="bo-metrics-grid">
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('itemStatus.detail.metrics.total') }}</span>
              <span class="bo-metric__value num mono">{{ qty(d.item.total) }}</span>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('itemStatus.detail.metrics.available') }}</span>
              <span class="bo-metric__value num mono">{{ qty(d.item.available) }}</span>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('itemStatus.detail.metrics.rented') }}</span>
              <span class="bo-metric__value num mono">{{ qty(d.item.rented) }}</span>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('itemStatus.detail.metrics.maintenance') }}</span>
              <span class="bo-metric__value num mono">{{ qty(d.item.maintenance) }}</span>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('itemStatus.detail.metrics.scrap') }}</span>
              <span class="bo-metric__value num mono">{{ qty(d.item.scrap) }}</span>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('itemStatus.col.inTransit') }}</span>
              <span class="bo-metric__value num mono">{{ qty(d.item.inTransit) }}</span>
            </div>
          </div>

          <!-- Reconciliation notice if unexplained on rent -->
          @if (d.item.unexplainedOnRent !== 0) {
            <p class="notice warn" role="alert">
              {{
                i18n.t('itemStatus.detail.reconciliationWarn', {
                  rented: qty(d.item.rented),
                  stillOut: qty(d.item.stillOut),
                  diff: qty(abs(d.item.unexplainedOnRent))
                })
              }}
            </p>
          }

          <!-- Left to issue informational note -->
          @if (d.leftToIssue.length > 0) {
            <div class="notice info">
              <span>{{ i18n.t('itemStatus.detail.leftToIssueLine') }}</span>
              <div class="contract-chips">
                @for (c of d.leftToIssue; track c.contractId) {
                  <span class="contract-chip">
                    <a [routerLink]="['/backoffice/rentals/contracts', c.contractId]">
                      {{ c.contractNumber }}
                    </a>
                    ({{ c.customerName }}: <span class="mono">{{ qty(c.qty) }}</span>)
                  </span>
                }
              </div>
            </div>
          }

          <!-- Locations table -->
          <div class="bo-section-block">
            <h4 class="bo-section-title">{{ i18n.t('itemStatus.detail.locations.title') }}</h4>
            <div class="bo-scroll">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('itemStatus.detail.locations.location') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.col.available') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.col.rented') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.detail.metrics.maintenance') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.detail.metrics.scrap') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.col.inTransit') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.col.total') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.detail.locations.contractsStillOut') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.detail.locations.unexplained') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (loc of d.locations; track loc.branchId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('itemStatus.detail.locations.location')">
                        {{ name(loc) }}
                        <span class="bo-kind-tag">
                          {{ loc.isWarehouse ? i18n.t('itemStatus.detail.locations.warehouse') : i18n.t('itemStatus.detail.locations.branch') }}
                        </span>
                        @if (!loc.isActive) {
                          <span class="bo-inactive-tag">{{ i18n.t('itemStatus.inactive') }}</span>
                        }
                      </td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('itemStatus.col.available')">
                        {{ qty(loc.available) }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.rented')">
                        {{ loc.availableOnly ? '—' : qty(loc.onRent) }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.detail.metrics.maintenance')">
                        {{ loc.availableOnly ? '—' : qty(loc.maintenance) }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.detail.metrics.scrap')">
                        {{ loc.availableOnly ? '—' : qty(loc.scrap) }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.inTransit')">
                        {{ loc.availableOnly ? '—' : qty(loc.inTransit) }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.total')">
                        {{ loc.availableOnly ? '—' : qty(loc.total) }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.detail.locations.contractsStillOut')">
                        {{ loc.availableOnly ? '—' : qty(loc.stillOut) }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.detail.locations.unexplained')">
                        {{ loc.availableOnly ? '—' : qty(loc.unexplainedOnRent) }}
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            @if (hasAvailableOnly()) {
              <p class="bo-scope-note">{{ i18n.t('itemStatus.detail.locations.availableOnlyCaption') }}</p>
            }
          </div>

          <!-- In transit: open transfers touching the locations seen (owner, 2026-10-12) -->
          @if (d.inTransit.length > 0) {
            <div class="bo-section-block">
              <h4 class="bo-section-title">{{ i18n.t('itemStatus.transit.title') }}</h4>
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('transfer.col.number') }}</th>
                      <th>{{ i18n.t('print.transfer.status') }}</th>
                      <th>{{ i18n.t('transfer.col.from') }}</th>
                      <th>{{ i18n.t('transfer.col.to') }}</th>
                      <th>{{ i18n.t('transfer.col.dispatchedAt') }}</th>
                      <th class="num">{{ i18n.t('transfer.col.inTransit') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (t of d.inTransit; track t.transferId) {
                      <tr>
                        <td class="mono" [attr.data-label]="i18n.t('transfer.col.number')">
                          <a routerLink="/backoffice/inventory/transfers">{{ t.number }}</a>
                        </td>
                        <td [attr.data-label]="i18n.t('print.transfer.status')">
                          <app-status [tone]="transferTone(t.status)">{{ i18n.t('transfer.status.' + t.status) }}</app-status>
                        </td>
                        <td [attr.data-label]="i18n.t('transfer.col.from')">{{ endName(t, 'from') }}</td>
                        <td [attr.data-label]="i18n.t('transfer.col.to')">
                          {{ endName(t, 'to') }}
                          @if (!t.countedHere) {
                            <div class="bo-muted-note">{{ i18n.t('itemStatus.transit.incoming') }}</div>
                          }
                        </td>
                        <td [attr.data-label]="i18n.t('transfer.col.dispatchedAt')">{{ i18n.date(t.dispatchedAtUtc) }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('transfer.col.inTransit')">{{ qty(t.qty) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </div>
          }

          <!-- Rented table: a page at a time from the server, filtered there (owner, 2026-11-17) -->
          @if (rentedCount() > 0) {
            <div class="bo-section-block">
              <h4 class="bo-section-title">{{ i18n.t('itemStatus.detail.rented.title') }}</h4>
              <div class="ui-toolbar rented-filters" role="search">
                <div class="ui-filter">
                  <label for="isd-f-state">{{ i18n.t('itemStatus.detail.rented.state') }}</label>
                  <select id="isd-f-state" [ngModel]="rentedState()" (ngModelChange)="rentedState.set($event)">
                    <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                    <option ngValue="overdue">{{ i18n.t('itemStatus.detail.rented.overdue') }}</option>
                    <option ngValue="onTime">{{ i18n.t('itemStatus.detail.rented.onTime') }}</option>
                  </select>
                </div>
                @if (rentedBranches().length > 1 || rentedBranch() !== 'all') {
                  <div class="ui-filter">
                    <label for="isd-f-branch">{{ i18n.t('itemStatus.detail.rented.issueBranch') }}</label>
                    <select id="isd-f-branch" [ngModel]="rentedBranch()" (ngModelChange)="rentedBranch.set($event)">
                      <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                      @for (b of rentedBranches(); track b.id) {
                        <option [ngValue]="b.id">{{ b.name }}</option>
                      }
                    </select>
                  </div>
                }
                @if (rentedFiltered()) {
                  <div class="ui-toolbar__end">
                    <button type="button" class="btn-ghost" (click)="clearRentedFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                  </div>
                }
              </div>
              @if (!rentedPager.total()) {
                <app-table-state kind="noResults" (clear)="clearRentedFilters()" />
              } @else {
                <div class="bo-scroll">
                  <table class="bo-data">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('itemStatus.detail.rented.customer') }}</th>
                        <th>{{ i18n.t('itemStatus.detail.rented.contract') }}</th>
                        <th>{{ i18n.t('itemStatus.detail.rented.issueBranch') }}</th>
                        <th>{{ i18n.t('itemStatus.detail.rented.site') }}</th>
                        <th>{{ i18n.t('itemStatus.detail.rented.issueNote') }}</th>
                        <th>{{ i18n.t('itemStatus.detail.rented.issuedOn') }}</th>
                        <th>{{ i18n.t('itemStatus.detail.rented.expectedReturn') }}</th>
                        <th>{{ i18n.t('itemStatus.detail.rented.state') }}</th>
                        <th class="num">{{ i18n.t('itemStatus.detail.rented.qty') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (r of rentedPager.rows(); track r.contractId + r.issuedOn) {
                        <tr>
                          <td [attr.data-label]="i18n.t('itemStatus.detail.rented.customer')">
                            <a [routerLink]="['/backoffice/customers/accounts']" [queryParams]="{ customer: r.customerId }">
                              @if (r.customerCode) {
                                <span class="mono">{{ r.customerCode }} — </span>
                              }
                              {{ r.customerName }}
                            </a>
                          </td>
                          <td class="mono" [attr.data-label]="i18n.t('itemStatus.detail.rented.contract')">
                            <a [routerLink]="['/backoffice/rentals/contracts', r.contractId]">
                              {{ r.contractNumber }}
                            </a>
                          </td>
                          <td [attr.data-label]="i18n.t('itemStatus.detail.rented.issueBranch')">
                            {{ issueBranchName(r) }}
                          </td>
                          <td [attr.data-label]="i18n.t('itemStatus.detail.rented.site')">{{ r.siteName || '—' }}</td>
                          <td class="mono" [attr.data-label]="i18n.t('itemStatus.detail.rented.issueNote')">{{ r.issueNoteNumber || '—' }}</td>
                          <td [attr.data-label]="i18n.t('itemStatus.detail.rented.issuedOn')">{{ i18n.date(r.issuedOn) }}</td>
                          <td [attr.data-label]="i18n.t('itemStatus.detail.rented.expectedReturn')">
                            {{ r.expectedReturnOn ? i18n.date(r.expectedReturnOn) : '—' }}
                          </td>
                          <td [attr.data-label]="i18n.t('itemStatus.detail.rented.state')">
                            @if (r.overdue) {
                              <app-status tone="critical">{{ i18n.t('itemStatus.detail.rented.overdue') }}</app-status>
                            } @else {
                              <app-status tone="success">{{ i18n.t('itemStatus.detail.rented.onTime') }}</app-status>
                            }
                          </td>
                          <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('itemStatus.detail.rented.qty')">
                            {{ qty(r.qty) }}
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
                <app-list-footer class="isd-list-foot" [pager]="rentedPager" [loading]="!rentedPager.loaded()" />
              }
            </div>
          }

          <!-- Movements table -->
          <div class="bo-section-block">
            <div class="bo-section-head">
              <h4 class="bo-section-title">{{ i18n.t('itemStatus.detail.movements.title') }}</h4>
              <div class="bo-actions-bar">
                <div class="bo-date-filters">
                  <label>
                    <span class="bo-date-label">{{ i18n.t('itemStatus.detail.movements.from') }}</span>
                    <app-date-input [ngModel]="fromDate()" (ngModelChange)="setFrom($event)" />
                  </label>
                  <label>
                    <span class="bo-date-label">{{ i18n.t('itemStatus.detail.movements.to') }}</span>
                    <app-date-input [ngModel]="toDate()" (ngModelChange)="setTo($event)" />
                  </label>
                </div>
                <button type="button" class="btn-ghost" (click)="exportExcel.emit({ from: fromDate(), to: toDate() })">
                  {{ i18n.t('itemStatus.btn.excel') }}
                </button>
                <button type="button" class="btn-ghost" (click)="print.emit({ from: fromDate(), to: toDate() })">
                  {{ i18n.t('itemStatus.btn.print') }}
                </button>
              </div>
            </div>

            @if (movementsHead().truncated) {
              <p class="notice info">{{ i18n.t('itemStatus.detail.movements.truncated') }}</p>
            }
            @if (movementsHead().unrecordedDifference) {
              <p class="notice info">
                {{ i18n.t('itemStatus.detail.movements.unrecordedDiff', { n: qty(movementsHead().unrecordedDifference) }) }}
              </p>
            }

            <div class="bo-scroll">
              <table class="bo-data">
                <thead>
                  <tr>
                    <th>{{ i18n.t('itemStatus.detail.movements.date') }}</th>
                    <th>{{ i18n.t('itemStatus.detail.movements.document') }}</th>
                    <th>{{ i18n.t('itemStatus.detail.movements.type') }}</th>
                    <th>{{ i18n.t('itemStatus.detail.movements.fromCol') }}</th>
                    <th>{{ i18n.t('itemStatus.detail.movements.toCol') }}</th>
                    <th>{{ i18n.t('itemStatus.detail.movements.customer') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.detail.movements.moved') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.detail.movements.in') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.detail.movements.out') }}</th>
                    <th class="num">{{ i18n.t('itemStatus.detail.movements.balance') }}</th>
                    <th>{{ i18n.t('itemStatus.detail.movements.user') }}</th>
                  </tr>
                </thead>
                <tbody>
                  <!-- Opening balance row: what stood before this page's first row (the period's opening on page one) -->
                  <tr class="bo-row-summary">
                    <td colspan="9">
                      <strong>{{ i18n.t('itemStatus.detail.movements.opening') }}</strong>
                    </td>
                    <td class="num mono bo-cell-strong">{{ qty(movementsOpening()) }}</td>
                    <td>—</td>
                  </tr>

                  @for (m of movementsPager.rows(); track m.atUtc + m.type + ($index)) {
                    <tr>
                      <td [attr.data-label]="i18n.t('itemStatus.detail.movements.date')">
                        {{ i18n.dateTime(m.atUtc) }}
                      </td>
                      <td class="mono" [attr.data-label]="i18n.t('itemStatus.detail.movements.document')">
                        {{ m.documentNumber || '—' }}
                      </td>
                      <td [attr.data-label]="i18n.t('itemStatus.detail.movements.type')">
                        {{ movementTypeWord(m) }}
                      </td>
                      <td [attr.data-label]="i18n.t('itemStatus.detail.movements.fromCol')">{{ fromCell(m) }}</td>
                      <td [attr.data-label]="i18n.t('itemStatus.detail.movements.toCol')">{{ toCell(m) }}</td>
                      <td [attr.data-label]="i18n.t('itemStatus.detail.movements.customer')">
                        {{ m.customerName || m.supplierName || '—' }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.detail.movements.moved')">
                        {{ m.moved !== null && m.moved !== undefined ? qty(m.moved) : '—' }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.detail.movements.in')">
                        {{ m.in !== null && m.in !== undefined ? qty(m.in) : '—' }}
                      </td>
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.detail.movements.out')">
                        {{ m.out !== null && m.out !== undefined ? qty(m.out) : '—' }}
                      </td>
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('itemStatus.detail.movements.balance')">
                        {{ qty(m.balance) }}
                      </td>
                      <td [attr.data-label]="i18n.t('itemStatus.detail.movements.user')">{{ m.userName || '—' }}</td>
                    </tr>
                  }

                  <!-- Closing balance row: the period's -->
                  <tr class="bo-row-summary">
                    <td colspan="9">
                      <strong>{{ i18n.t('itemStatus.detail.movements.closing') }}</strong>
                    </td>
                    <td class="num mono bo-cell-strong">{{ qty(movementsHead().closing) }}</td>
                    <td>—</td>
                  </tr>
                </tbody>
              </table>
            </div>
            <!-- A page at a time from the server: each row carries its own running balance, run over
                 every row of the period before the page was cut. -->
            <app-list-footer class="isd-list-foot" [pager]="movementsPager" [loading]="!movementsPager.loaded()" />
          </div>
        </div>
      </section>
    }
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .bo-detail-panel {
        margin-block-start: var(--sp-4);
        border: var(--line) solid var(--border-strong);
      }
      .bo-detail-head {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: var(--sp-3);
      }
      .bo-detail-titles {
        display: flex;
        align-items: baseline;
        gap: var(--sp-2);
        flex-wrap: wrap;
      }
      .bo-detail-sku {
        color: var(--muted);
        font-size: var(--fs-sm);
      }
      .bo-detail-unit {
        color: var(--muted);
        font-size: var(--fs-sm);
      }
      .bo-metrics-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(8rem, 1fr));
        gap: var(--sp-3);
        margin-block-end: var(--sp-4);
      }
      .bo-section-block {
        margin-block-start: var(--sp-5);
      }
      .bo-section-head {
        display: flex;
        justify-content: space-between;
        align-items: center;
        flex-wrap: wrap;
        gap: var(--sp-3);
        margin-block-end: var(--sp-2);
      }
      .bo-section-title {
        margin: 0;
        font-size: var(--fs-md);
        font-weight: 700;
      }
      .bo-actions-bar {
        display: flex;
        align-items: center;
        gap: var(--sp-2);
        flex-wrap: wrap;
      }
      .bo-date-filters {
        display: flex;
        align-items: center;
        gap: var(--sp-2);
      }
      .bo-date-label {
        font-size: var(--fs-meta);
        margin-inline-end: var(--sp-1);
        color: var(--muted);
      }
      .contract-chips {
        display: flex;
        gap: var(--sp-2);
        flex-wrap: wrap;
        margin-block-start: var(--sp-1);
      }
      .contract-chip {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-1);
        padding-inline: var(--sp-3);
        padding-block: var(--sp-1);
        background: var(--surface-2);
        border-radius: var(--radius-sm);
        border: 1px solid var(--border);
        font-size: var(--fs-meta);
      }
      .bo-kind-tag {
        font-size: var(--fs-micro);
        color: var(--muted);
        margin-inline-start: var(--sp-1);
      }
      .bo-inactive-tag {
        font-size: var(--fs-micro);
        color: var(--danger);
        margin-inline-start: var(--sp-1);
      }
      .bo-row-summary {
        background: var(--surface-2);
        font-weight: 600;
      }
      .rented-filters {
        margin-block-end: var(--sp-3);
      }
      .isd-list-foot {
        padding-block: var(--sp-3);
      }
    `,
  ],
})
export class ItemStatusDetailComponent implements OnInit {
  readonly i18n = inject(I18nService);
  private readonly pagesApi = inject(ItemStatusPagesApi);

  /** The breakdown without its two long lists, which page through the server below. */
  readonly detail = input.required<ItemStatusDetail>();
  /** The report's filters (branch, customer, contract…): the two lists are read under them too. */
  readonly query = input<ItemStatusQuery>({});
  readonly branches = input<AdminBranch[]>([]);

  readonly close = output<void>();
  readonly datesChange = output<{ from: string; to: string }>();
  readonly exportExcel = output<{ from: string; to: string }>();
  readonly print = output<{ from: string; to: string }>();

  /** The movements' period, `yyyy-MM-dd` or empty: the server reads it on the company's calendar. */
  readonly fromDate = signal('');
  readonly toDate = signal('');
  private currentProductId: string | null = null;

  constructor() {
    effect(() => {
      const d = this.detail();
      if (!d) return;
      const pid = d.item.productId;
      if (pid !== this.currentProductId) {
        this.currentProductId = pid;
        this.fromDate.set(d.movements?.from ?? '');
        this.toDate.set(d.movements?.to ?? '');
        // Another item: its own rentals, so the filters of the last one start over.
        this.clearRentedFilters();
      }
    });
  }

  // ---------- the rented list: a page at a time from the server, filtered there (owner, 2026-11-17) ----------

  readonly rentedState = signal<'all' | 'overdue' | 'onTime'>('all');
  readonly rentedBranch = signal<string>('all');

  readonly rentedPager = createServerPager(
    (page) =>
      this.pagesApi.rented(
        page.filters.productId,
        page.filters.query,
        { state: page.filters.state, issueBranchId: page.filters.issueBranchId },
        page.skip,
        page.take,
      ),
    {
      key: 'inventory.itemStatus.rented',
      filters: () => ({
        productId: this.detail().item.productId,
        query: this.query(),
        state: this.rentedState(),
        issueBranchId: this.rentedBranch(),
      }),
    },
  );

  /** How many rentals are out on the article, whatever the two filters; none, no section. */
  readonly rentedCount = computed(() => this.rentedPager.summary()?.count ?? 0);

  /** The branches the goods went out from, as the server found them; the filter shows only when they differ. */
  readonly rentedBranches = computed(() =>
    (this.rentedPager.summary()?.issueBranches ?? []).map((b) => ({
      id: b.id,
      name: this.issueBranchName({ issueBranchNameAr: b.nameAr, issueBranchNameFr: b.nameFr, issueBranchNameEn: b.nameEn }),
    })),
  );

  readonly rentedFiltered = computed(() => this.rentedState() !== 'all' || this.rentedBranch() !== 'all');

  /**
   * The movements a page at a time from the server. No filter but the period: each row carries the
   * running balance the server ran over every row of it, and the opening row shows what stood
   * before the page's first row.
   */
  readonly movementsPager = createServerPager(
    (page) =>
      this.pagesApi.movements(
        page.filters.productId,
        page.filters.query,
        page.filters.from,
        page.filters.to,
        page.skip,
        page.take,
      ),
    {
      key: 'inventory.itemStatus.movements',
      filters: () => ({
        productId: this.detail().item.productId,
        query: this.query(),
        from: this.fromDate(),
        to: this.toDate(),
      }),
    },
  );

  /** The period's figures and notices: the server's answer, or the breakdown's until it comes. */
  readonly movementsHead = computed(() => this.movementsPager.summary() ?? this.detail().movements);

  /** The balance before the page's first row; the period's opening on page one. */
  readonly movementsOpening = computed(
    () => this.movementsPager.summary()?.pageOpening ?? this.detail().movements.opening,
  );

  clearRentedFilters(): void {
    this.rentedState.set('all');
    this.rentedBranch.set('all');
  }

  readonly itemName = computed(() => {
    const item = this.detail()?.item;
    return item ? localizedName(item, this.i18n.lang()) : '';
  });

  readonly hasAvailableOnly = computed(() =>
    this.detail()?.locations.some((l) => l.availableOnly) ?? false,
  );

  ngOnInit(): void {
    const d = this.detail();
    this.currentProductId = d?.item.productId ?? null;
    this.fromDate.set(d?.movements?.from ?? '');
    this.toDate.set(d?.movements?.to ?? '');
  }

  qty(value: number | null | undefined): string {
    return formatQty(value, this.i18n.lang());
  }

  abs(value: number): number {
    return Math.abs(value);
  }

  name(item: Partial<MultilingualName> | null | undefined): string {
    return localizedName(item, this.i18n.lang());
  }

  issueBranchName(r: { issueBranchNameAr: string; issueBranchNameFr: string; issueBranchNameEn: string }): string {
    return localizedName(
      { nameAr: r.issueBranchNameAr, nameFr: r.issueBranchNameFr, nameEn: r.issueBranchNameEn },
      this.i18n.lang(),
    );
  }

  setFrom(value: string | null): void {
    this.fromDate.set(value ?? '');
    this.onDatesChange();
  }

  setTo(value: string | null): void {
    this.toDate.set(value ?? '');
    this.onDatesChange();
  }

  /** The new period, for the parent's papers and files; the movements below ask the server themselves. */
  onDatesChange(): void {
    this.datesChange.emit({ from: this.fromDate(), to: this.toDate() });
  }

  transferTone(status: ItemInTransitRow['status']) {
    return toneOf('transfer', status);
  }

  /** One end of a transfer in the interface language. */
  endName(t: ItemInTransitRow, end: 'from' | 'to'): string {
    return end === 'from'
      ? localizedName({ nameAr: t.fromNameAr, nameFr: t.fromNameFr, nameEn: t.fromNameEn }, this.i18n.lang())
      : localizedName({ nameAr: t.toNameAr, nameFr: t.toNameFr, nameEn: t.toNameEn }, this.i18n.lang());
  }

  movementTypeWord(m: ItemMovementRow): string {
    if (m.type === 'state') {
      const fromWord = this.i18n.t(`itemStatus.stateWord.${m.fromState || 'available'}`);
      const toWord = this.i18n.t(`itemStatus.stateWord.${m.toState || 'available'}`);
      return `${fromWord} → ${toWord}`;
    }
    return this.i18n.t(`itemStatus.mvt.${m.type}`);
  }

  private branchNameById(id: string | null | undefined): string {
    if (!id) return '';
    const b = this.branches().find((br) => br.id === id);
    return b ? this.name(b) : id;
  }

  fromCell(m: ItemMovementRow): string {
    if (m.type === 'state') {
      const stateWord = this.i18n.t(`itemStatus.stateWord.${m.fromState || 'available'}`);
      const loc = this.branchNameById(m.fromBranchId);
      return loc ? `${stateWord} · ${loc}` : stateWord;
    }
    if (m.fromBranchId) return this.branchNameById(m.fromBranchId);
    if (m.type === 'refund') return m.customerName || this.i18n.t('itemStatus.customer');
    if (m.type === 'receiving') return m.supplierName || this.i18n.t('itemStatus.detail.movements.supplier');
    if (m.type === 'rental-return' || m.type === 'rental-return-transfer') {
      return m.customerName || this.i18n.t('itemStatus.customer');
    }
    return '—';
  }

  toCell(m: ItemMovementRow): string {
    if (m.type === 'state') {
      const stateWord = this.i18n.t(`itemStatus.stateWord.${m.toState || 'available'}`);
      const loc = this.branchNameById(m.toBranchId) || this.branchNameById(m.fromBranchId);
      return loc ? `${stateWord} · ${loc}` : stateWord;
    }
    if (m.toBranchId) return this.branchNameById(m.toBranchId);
    if (m.type === 'sale') return m.customerName || this.i18n.t('itemStatus.detail.movements.counterCustomer');
    if (m.type === 'rental-issue') return m.customerName || this.i18n.t('itemStatus.customer');
    return '—';
  }
}
