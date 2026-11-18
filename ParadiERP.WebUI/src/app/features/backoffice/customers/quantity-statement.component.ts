import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminBranch, AdminCustomer, AdminProduct, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { PrintService } from '../../print/print.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { MultilingualName, formatDay, localizedName } from '../backoffice-format';
import { startOfDay, startOfMonth, toInputDate } from '../receivables/receivable-view';
import { QuantityStatementHead, SalesPagesApi } from '../sales-pages.api';
import {
  CustomerSite,
  QuantityStatementItem,
  QuantityStatementQuery,
  QuantityStatementRate,
  QuantityStatementWarning,
  RentalContractRow,
  RentalsApi,
} from '../rentals/rentals-api.service';

/**
 * Customers › Customer quantity statement (كشف كميات العميل — owner, 2026-10-12): what went out
 * to one customer, what came back, and what they still hold as of the end of "to", with the
 * rental value of the window. No money owed or paid appears here — that is the account statement.
 *
 * <p>The server does the counting and the pricing (`GET /api/rentals/quantity-statement`); this
 * screen only shows it. Dates go out exactly as the inputs hold them, `yyyy-MM-dd`, and the server
 * reads them in the company's zone.</p>
 */
@Component({
  selector: 'app-quantity-statement',
  standalone: true,
  imports: [FormsModule, RouterLink, DateInputComponent, ListFooterComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <p class="bo-panel__note">{{ i18n.t('qstmt.lead') }}</p>
        </div>
        <div class="bo-panel__body bo-form">
          <div class="bo-fields bo-fields--roomy">
            <div class="bo-field">
              <label for="qs-customer">{{ i18n.t('qstmt.customer') }}</label>
              <select id="qs-customer" [ngModel]="customerId()" (ngModelChange)="setCustomer($event)">
                <option [ngValue]="null">{{ i18n.t('qstmt.chooseCustomer') }}</option>
                @for (c of customers(); track c.id) {
                  <option [ngValue]="c.id">{{ c.code }} — {{ c.fullName }}</option>
                }
              </select>
            </div>
            <div class="bo-field">
              <label for="qs-from">{{ i18n.t('qstmt.from') }}</label>
              <app-date-input id="qs-from" [ngModel]="from()" (ngModelChange)="from.set($event)" />
            </div>
            <div class="bo-field">
              <label for="qs-to">{{ i18n.t('qstmt.to') }}</label>
              <app-date-input id="qs-to" [ngModel]="to()" (ngModelChange)="to.set($event)" />
            </div>
            <div class="bo-field">
              <label for="qs-contract">{{ i18n.t('qstmt.contract') }}</label>
              <select id="qs-contract" [ngModel]="contractId()" (ngModelChange)="contractId.set($event)">
                <option [ngValue]="null">{{ i18n.t('qstmt.allContracts') }}</option>
                @for (c of contracts(); track c.id) {
                  <option [ngValue]="c.id">{{ c.number }}</option>
                }
              </select>
            </div>
            @if (lock.locked()) {
              <p class="bo-scope-note">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
            } @else {
              <div class="bo-field">
                <label for="qs-branch">{{ i18n.t('qstmt.branch') }}</label>
                <select id="qs-branch" [ngModel]="branchId()" (ngModelChange)="branchId.set($event)">
                  <option [ngValue]="null">{{ i18n.t('qstmt.allBranches') }}</option>
                  @for (b of branches(); track b.id) {
                    <option [ngValue]="b.id">{{ name(b) }}</option>
                  }
                </select>
              </div>
            }
            <div class="bo-field">
              <label for="qs-site">{{ i18n.t('qstmt.site') }}</label>
              <select id="qs-site" [ngModel]="siteId()" (ngModelChange)="siteId.set($event)">
                <option [ngValue]="null">{{ i18n.t('qstmt.allSites') }}</option>
                @for (s of sites(); track s.id) {
                  <option [ngValue]="s.id">{{ s.name }}</option>
                }
              </select>
            </div>
            <div class="bo-field">
              <label for="qs-status">{{ i18n.t('qstmt.status') }}</label>
              <select id="qs-status" [ngModel]="status()" (ngModelChange)="status.set($event)">
                @for (s of statuses; track s) {
                  <option [ngValue]="s">{{ i18n.t('qstmt.status.' + s) }}</option>
                }
              </select>
            </div>
            <div class="bo-field">
              <label for="qs-items">{{ i18n.t('qstmt.items') }}</label>
              <select id="qs-items" multiple size="4" [ngModel]="productIds()" (ngModelChange)="productIds.set($event ?? [])">
                @for (p of products(); track p.id) {
                  <option [ngValue]="p.id">{{ p.sku }} — {{ name(p) }}</option>
                }
              </select>
            </div>
            <div class="bo-field">
              <label for="qs-search">{{ i18n.t('qstmt.search') }}</label>
              <input id="qs-search" type="search" [ngModel]="search()" (ngModelChange)="search.set($event)" />
            </div>
          </div>
          <div class="bo-actions">
            <button type="button" class="btn-primary" [disabled]="!customerId() || busy()" (click)="show(false)">
              {{ i18n.t('qstmt.show') }}
            </button>
            <button type="button" class="btn-ghost" [disabled]="!customerId() || busy()" (click)="show(true)">
              {{ i18n.t('qstmt.current') }}
            </button>
            <button type="button" class="btn-ghost" [disabled]="!statement()" (click)="print()">{{ i18n.t('qstmt.print') }}</button>
            <button type="button" class="btn-ghost" [disabled]="!statement() || exporting()" (click)="excel()">{{ i18n.t('qstmt.excel') }}</button>
          </div>
        </div>
      </section>

      @if (busy()) {
        <p class="bo-loading" role="status">
          <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
          <span class="bo-loading__rows" aria-hidden="true"></span>
        </p>
      }

      @if (statement(); as s) {
        <p class="notice info" role="note">
          @if (s.firstIssueOn) {
            {{ i18n.t('qstmt.notRecorded', { date: day(s.firstIssueOn) }) }}
          } @else {
            {{ i18n.t('qstmt.noIssue') }}
          }
        </p>
        @for (w of s.warnings; track $index) {
          <p class="notice warn" role="status">{{ warning(w) }}</p>
        }

        <p class="qs-period">
          @if (s.current) {
            {{ i18n.t('qstmt.asOf', { date: day(s.to) }) }}
          } @else {
            {{ i18n.t('qstmt.period', { from: day(s.from), to: day(s.to) }) }}
          }
        </p>

        <div class="bo-metrics">
          <div class="bo-metric">
            <span class="bo-metric__label">{{ i18n.t('qstmt.sum.itemTypes') }}</span>
            <strong class="bo-metric__value mono">{{ s.summary.itemTypes }}</strong>
          </div>
          @if (!s.current) {
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('qstmt.sum.issued') }}</span>
              <strong class="bo-metric__value mono">{{ s.summary.issued }}</strong>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('qstmt.sum.returned') }}</span>
              <strong class="bo-metric__value mono">{{ s.summary.returned }}</strong>
            </div>
            <div class="bo-metric">
              <span class="bo-metric__label">{{ i18n.t('qstmt.sum.missingDamaged') }}</span>
              <strong class="bo-metric__value mono">{{ s.summary.missingDamaged }}</strong>
            </div>
          }
          <div class="bo-metric" [class.bo-metric--lead]="s.current">
            <span class="bo-metric__label">{{ i18n.t('qstmt.sum.withCustomer') }}</span>
            <strong class="bo-metric__value mono">{{ s.summary.withCustomer }}</strong>
          </div>
          @if (!s.current) {
            <div class="bo-metric bo-metric--lead">
              <span class="bo-metric__label">{{ i18n.t('qstmt.sum.value') }}</span>
              <strong class="bo-metric__value mono">{{ i18n.money(s.summary.value) }}</strong>
              @if (s.summary.provisionalValue > 0) {
                <span class="bo-metric__note"><span>{{ i18n.t('qstmt.provisional') }}</span></span>
              }
            </div>
          }
        </div>
        @if (!s.current && s.summary.provisionalValue > 0) {
          <p class="qs-note">{{ i18n.t('qstmt.provisionalNote') }}</p>
        }

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h3 class="bo-panel__title">{{ i18n.t('qstmt.itemsTitle') }}</h3>
          </div>
          @if (s.itemCount === 0) {
            <p class="bo-panel__body qs-empty">{{ i18n.t('qstmt.nothing') }}</p>
          } @else {
            <div class="bo-scroll">
              <table class="bo-data qs-items">
                <thead>
                  <tr>
                    <th scope="col"><span class="sr-only">{{ i18n.t('qstmt.movements') }}</span></th>
                    <th scope="col">{{ i18n.t('qstmt.col.sku') }}</th>
                    <th scope="col">{{ i18n.t('qstmt.col.item') }}</th>
                    @if (!s.current) {
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.opening') }}</th>
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.issued') }}</th>
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.returned') }}</th>
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.missingDamaged') }}</th>
                      @if (hasTransfers()) {
                        <th scope="col" class="num">{{ i18n.t('qstmt.col.transfers') }}</th>
                      }
                    }
                    <th scope="col" class="num">{{ i18n.t('qstmt.col.remaining') }}</th>
                    @if (!s.current) {
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.rate') }}</th>
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.value') }}</th>
                    }
                  </tr>
                </thead>
                <tbody>
                  @for (item of itemsPager.rows(); track item.productId) {
                    <tr>
                      <td>
                        <button
                          type="button"
                          class="btn-ghost bo-small"
                          [attr.aria-expanded]="isOpen(item.productId)"
                          (click)="toggle(item.productId)"
                        >
                          {{ isOpen(item.productId) ? i18n.t('qstmt.hideMovements') : i18n.t('qstmt.movements') }}
                        </button>
                      </td>
                      <td class="mono" [attr.data-label]="i18n.t('qstmt.col.sku')">{{ item.sku }}</td>
                      <td [attr.data-label]="i18n.t('qstmt.col.item')">
                        {{ itemName(item) }}
                        @if (item.negativeBalance) {
                          <span class="bo-state bo-state--warn">{{ i18n.t('qstmt.negative') }}</span>
                        }
                      </td>
                      @if (!s.current) {
                        <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.opening')">{{ item.opening }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.issued')">{{ item.issued }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.returned')">{{ item.returned }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.missingDamaged')">{{ item.missingDamaged }}</td>
                        @if (hasTransfers()) {
                          <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.transfers')">{{ item.transferNet }}</td>
                        }
                      }
                      <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('qstmt.col.remaining')">{{ item.remaining }}</td>
                      @if (!s.current) {
                        <td class="num" [attr.data-label]="i18n.t('qstmt.col.rate')">
                          @if (item.noRent) {
                            {{ i18n.t('qstmt.noRent') }}
                          } @else if (item.rates.length === 1) {
                            <span class="mono">{{ i18n.money(item.rates[0].unitPrice) }}</span>
                          } @else if (item.rates.length > 1) {
                            <button
                              type="button"
                              class="btn-ghost bo-small qs-rates-toggle"
                              [attr.aria-expanded]="ratesOpen(item.productId)"
                              (click)="toggleRates(item.productId)"
                            >
                              {{ i18n.t('qstmt.multipleRates') }}
                            </button>
                            @if (ratesOpen(item.productId)) {
                              <ul class="qs-rates">
                                @for (r of item.rates; track $index) {
                                  <li>{{ rateKind(r) }} <span class="mono">{{ i18n.money(r.unitPrice) }}</span></li>
                                }
                              </ul>
                            }
                          }
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.value')">
                          {{ i18n.money(item.value) }}
                          @if (item.provisionalValue > 0) {
                            <span class="qs-provisional">{{ i18n.t('qstmt.provisional') }}</span>
                          }
                        </td>
                      }
                    </tr>
                    @if (isOpen(item.productId)) {
                      <tr class="qs-detail">
                        <td [attr.colspan]="columns()">
                          <table class="bo-data qs-moves">
                            <thead>
                              <tr>
                                <th scope="col">{{ i18n.t('qstmt.col.date') }}</th>
                                <th scope="col">{{ i18n.t('qstmt.col.document') }}</th>
                                <th scope="col">{{ i18n.t('qstmt.col.contract') }}</th>
                                <th scope="col">{{ i18n.t('qstmt.col.movement') }}</th>
                                <th scope="col" class="num">{{ i18n.t('qstmt.col.qty') }}</th>
                                <th scope="col" class="num">{{ i18n.t('qstmt.col.rate') }}</th>
                                <th scope="col" class="num">{{ i18n.t('qstmt.col.balance') }}</th>
                                <th scope="col">{{ i18n.t('qstmt.col.branch') }}</th>
                                <th scope="col">{{ i18n.t('qstmt.col.site') }}</th>
                              </tr>
                            </thead>
                            <tbody>
                              @for (m of item.movements; track $index) {
                                <tr>
                                  <td [attr.data-label]="i18n.t('qstmt.col.date')">{{ day(m.on) }}</td>
                                  <td class="mono" [attr.data-label]="i18n.t('qstmt.col.document')">{{ m.documentNumber ?? '' }}</td>
                                  <td [attr.data-label]="i18n.t('qstmt.col.contract')">
                                    @if (m.contractId) {
                                      <a class="ui-link mono" [routerLink]="['/backoffice', 'rentals', 'contracts', m.contractId]">{{ m.contractNumber }}</a>
                                    }
                                  </td>
                                  <td [attr.data-label]="i18n.t('qstmt.col.movement')">{{ i18n.t('qstmt.move.' + m.kind) }}</td>
                                  <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.qty')">
                                    {{ m.kind === 'opening' ? '' : signed(m.qty) }}
                                  </td>
                                  <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.rate')">
                                    {{ m.unitPrice !== null ? i18n.money(m.unitPrice) : '' }}
                                  </td>
                                  <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('qstmt.col.balance')">{{ m.balance }}</td>
                                  <td [attr.data-label]="i18n.t('qstmt.col.branch')">{{ m.branchId ? branchName(m.branchId) : '' }}</td>
                                  <td [attr.data-label]="i18n.t('qstmt.col.site')">{{ m.siteName ?? '' }}</td>
                                </tr>
                              }
                            </tbody>
                          </table>
                        </td>
                      </tr>
                    }
                  }
                </tbody>
              </table>
            </div>
            <app-list-footer [pager]="itemsPager" [loading]="!itemsPager.loaded()" />
          }
        </section>

        @if (s.contractCount > 0) {
          <section class="bo-panel">
            <div class="bo-panel__head">
              <h3 class="bo-panel__title">{{ i18n.t('qstmt.contractsTitle') }}</h3>
            </div>
            <div class="bo-scroll">
              <table class="bo-data qs-contracts">
                <thead>
                  <tr>
                    <th scope="col">{{ i18n.t('qstmt.col.contract') }}</th>
                    <th scope="col">{{ i18n.t('qstmt.col.status') }}</th>
                    <th scope="col">{{ i18n.t('qstmt.col.branch') }}</th>
                    <th scope="col">{{ i18n.t('qstmt.col.site') }}</th>
                    <th scope="col">{{ i18n.t('qstmt.col.issueDate') }}</th>
                    <th scope="col">{{ i18n.t('qstmt.col.item') }}</th>
                    @if (!s.current) {
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.issued') }}</th>
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.returned') }}</th>
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.missingDamaged') }}</th>
                      @if (hasContractTransfers()) {
                        <th scope="col" class="num">{{ i18n.t('qstmt.col.transferIn') }}</th>
                        <th scope="col" class="num">{{ i18n.t('qstmt.col.transferOut') }}</th>
                      }
                    }
                    <th scope="col" class="num">{{ i18n.t('qstmt.col.remaining') }}</th>
                    @if (!s.current) {
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.rate') }}</th>
                      <th scope="col" class="num">{{ i18n.t('qstmt.col.value') }}</th>
                    }
                  </tr>
                </thead>
                <tbody>
                  @for (c of contractsPager.rows(); track c.contractId) {
                    @for (l of c.lines; track l.lineId; let first = $first) {
                      <tr>
                        <td [attr.data-label]="i18n.t('qstmt.col.contract')">
                          @if (first) {
                            <a class="ui-link mono" [routerLink]="['/backoffice', 'rentals', 'contracts', c.contractId]">{{ c.number }}</a>
                          }
                        </td>
                        <td [attr.data-label]="i18n.t('qstmt.col.status')">
                          @if (first) {
                            <span class="bo-state" [class.bo-state--ok]="c.status === 'closed'" [class.bo-state--info]="c.status === 'goodsBack'" [class.bo-state--lead]="c.status === 'open'">
                              {{ i18n.t('qstmt.status.' + c.status) }}
                            </span>
                          }
                        </td>
                        <td [attr.data-label]="i18n.t('qstmt.col.branch')">{{ first ? branchName(c.branchId) : '' }}</td>
                        <td [attr.data-label]="i18n.t('qstmt.col.site')">{{ first ? (c.siteName ?? '') : '' }}</td>
                        <td [attr.data-label]="i18n.t('qstmt.col.issueDate')">{{ l.firstIssueOn ? day(l.firstIssueOn) : '' }}</td>
                        <td [attr.data-label]="i18n.t('qstmt.col.item')">
                          <span class="mono">{{ l.sku }}</span> {{ lineName(l) }}
                        </td>
                        @if (!s.current) {
                          <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.issued')">{{ l.issued }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.returned')">{{ l.returned }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.missingDamaged')">{{ l.missingDamaged }}</td>
                          @if (hasContractTransfers()) {
                            <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.transferIn')">{{ l.transferredIn }}</td>
                            <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.transferOut')">{{ l.transferredOut }}</td>
                          }
                        }
                        <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('qstmt.col.remaining')">{{ l.remaining }}</td>
                        @if (!s.current) {
                          <td class="num" [attr.data-label]="i18n.t('qstmt.col.rate')">
                            @if (l.noRent) {
                              {{ i18n.t('qstmt.noRent') }}
                            } @else {
                              <span class="mono">{{ i18n.money(l.rentUnitPrice) }}</span>
                              @for (r of extraRates(l.rates); track $index) {
                                <span class="qs-extra-rate">{{ rateKind(r) }} <span class="mono">{{ i18n.money(r.unitPrice) }}</span></span>
                              }
                            }
                          </td>
                          <td class="num mono" [attr.data-label]="i18n.t('qstmt.col.value')">{{ i18n.money(l.value) }}</td>
                        }
                      </tr>
                    }
                    @if (!s.current) {
                      <tr class="qs-contract-total">
                        <th scope="row" [attr.colspan]="contractColumns() - 1">
                          {{ i18n.t('qstmt.contractTotal') }} <span class="mono">{{ c.number }}</span>
                          @if (c.provisionalValue > 0) {
                            <span class="qs-provisional">{{ i18n.t('qstmt.provisional') }}</span>
                          }
                        </th>
                        <td class="num mono bo-cell-strong">{{ i18n.money(c.value) }}</td>
                      </tr>
                    }
                  }
                </tbody>
              </table>
            </div>
            <app-list-footer [pager]="contractsPager" [loading]="!contractsPager.loaded()" />
          </section>
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      .qs-period,
      .qs-note,
      .qs-empty {
        margin: 0;
        color: var(--muted);
      }
      .qs-provisional,
      .qs-extra-rate {
        display: block;
        font-size: var(--fs-micro);
        color: var(--muted);
      }
      .qs-rates {
        margin: var(--sp-1) 0 0;
        padding: 0;
        list-style: none;
        text-align: end;
      }
      .qs-detail > td {
        background: var(--surface-2);
        padding-block: var(--sp-2);
      }
      .qs-moves {
        inline-size: 100%;
      }
      .qs-contract-total th {
        text-align: end;
        font-weight: 600;
      }
    `,
  ],
})
export class QuantityStatementComponent implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly rentals = inject(RentalsApi);
  private readonly pages = inject(SalesPagesApi);
  private readonly printer = inject(PrintService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);

  readonly statuses = ['all', 'open', 'goodsBack', 'closed'] as const;

  readonly customers = signal<AdminCustomer[]>([]);
  readonly branches = signal<AdminBranch[]>([]);
  readonly products = signal<AdminProduct[]>([]);
  readonly contracts = signal<RentalContractRow[]>([]);
  readonly sites = signal<CustomerSite[]>([]);

  readonly customerId = signal<string | null>(null);
  readonly from = signal<string>(toInputDate(startOfMonth(new Date())));
  readonly to = signal<string>(toInputDate(startOfDay(new Date())));
  readonly contractId = signal<string | null>(null);
  readonly branchId = signal<string | null>(null);
  readonly siteId = signal<string | null>(null);
  readonly status = signal<string>('all');
  readonly productIds = signal<string[]>([]);
  readonly search = signal<string>('');

  /** The query the statement on screen was read with — what the pages ask with, and what Print sends. */
  private readonly shownQuery = signal<QuantityStatementQuery | null>(null);
  readonly exporting = signal(false);
  readonly error = signal<string | null>(null);
  private readonly open = signal<ReadonlySet<string>>(new Set());
  private readonly openRates = signal<ReadonlySet<string>>(new Set());

  /**
   * Ten items, and ten contracts, to begin with, a page at a time from the server (owner,
   * 2026-11-17: "paginate in backend, not frontend"). The server builds the whole statement and
   * sends one page of one table; its head — the tiles, the warnings, the dates — comes with every
   * page and totals the whole statement. Paged only: its filters are the form's, sent on Show. A
   * contract pages whole, its lines and its total together. A new statement starts at page one.
   */
  readonly itemsPager = createServerPager(
    (page) => this.pages.quantityItemsPage(page.filters.query!, page.skip, page.take),
    {
      key: 'customers.quantityItems',
      filters: () => ({ query: this.shownQuery() }),
      enabled: () => !!this.shownQuery(),
      debounceMs: 0,
    },
  );
  readonly contractsPager = createServerPager(
    (page) => this.pages.quantityContractsPage(page.filters.query!, page.skip, page.take),
    {
      key: 'customers.quantityContracts',
      filters: () => ({ query: this.shownQuery() }),
      enabled: () => !!this.shownQuery(),
      debounceMs: 0,
    },
  );

  /** The statement on screen: its head, once the server has answered the query shown. */
  readonly statement = computed<QuantityStatementHead | null>(() =>
    this.shownQuery() && !this.itemsPager.error() ? this.itemsPager.summary() : null,
  );
  readonly busy = computed(() => this.itemsPager.loading() || this.contractsPager.loading());

  readonly hasTransfers = computed(() => this.statement()?.hasTransfers ?? false);
  readonly hasContractTransfers = computed(() => this.statement()?.hasContractTransfers ?? false);
  /** Columns of the item table, for the movements row that spans it. */
  readonly columns = computed(() => {
    const s = this.statement();
    if (!s) return 1;
    return s.current ? 4 : 10 + (this.hasTransfers() ? 1 : 0);
  });
  readonly contractColumns = computed(() => {
    const s = this.statement();
    if (!s) return 1;
    return s.current ? 7 : 12 + (this.hasContractTransfers() ? 2 : 0);
  });

  async ngOnInit(): Promise<void> {
    try {
      const [customers, branches, products] = await Promise.all([
        this.admin.listCustomers(),
        this.admin.listBranches(),
        this.admin.listProducts(),
      ]);
      this.customers.set(customers);
      this.branches.set(branches);
      this.products.set(products.filter((p) => p.isRentable));
      // A non-admin's statement is their branch's; the server answers that whatever is asked.
      this.branchId.set(this.lock.lockedBranchId());
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  async setCustomer(id: string | null): Promise<void> {
    this.customerId.set(id || null);
    this.contractId.set(null);
    this.siteId.set(null);
    this.shownQuery.set(null);
    this.contracts.set([]);
    this.sites.set([]);
    if (!id) return;
    try {
      const [contracts, sites] = await Promise.all([this.rentals.listContracts(undefined, id), this.rentals.listSites(id)]);
      this.contracts.set(contracts);
      this.sites.set(sites);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  query(current: boolean): QuantityStatementQuery | null {
    const customerId = this.customerId();
    if (!customerId) return null;
    return {
      customerId,
      current,
      from: current ? null : this.from(),
      to: current ? null : this.to(),
      productIds: this.productIds(),
      contractId: this.contractId(),
      branchId: this.branchId(),
      siteId: this.siteId(),
      status: this.status(),
      search: this.search(),
    };
  }

  /** Asks for the first page of both tables with the form's query; the server counts and prices. */
  async show(current: boolean): Promise<void> {
    const q = this.query(current);
    if (!q) return;
    this.error.set(null);
    this.shownQuery.set(q);
    this.open.set(new Set());
    this.openRates.set(new Set());
    this.itemsPager.reset();
    this.contractsPager.reset();
    await Promise.all([this.itemsPager.reload(), this.contractsPager.reload()]);
    const failed = this.itemsPager.error() ?? this.contractsPager.error();
    if (failed) this.error.set(adminErrorMessage(failed));
  }

  print(): void {
    const q = this.shownQuery();
    if (!q) return;
    this.printer.open('quantity-statement', q.customerId, {
      lang: this.i18n.lang(),
      from: q.from ?? undefined,
      to: q.to ?? undefined,
      current: q.current,
      contract: q.contractId ?? undefined,
      branch: q.branchId,
      site: q.siteId,
      status: q.status,
      search: q.search,
      products: q.productIds,
    });
  }

  /** The same query as the statement on screen, as an Excel file: the same figures and totals. */
  async excel(): Promise<void> {
    const q = this.shownQuery();
    const s = this.statement();
    if (!q || !s) return;
    this.exporting.set(true);
    this.error.set(null);
    try {
      const blob = await this.rentals.quantityStatementExport(q, this.i18n.lang());
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `quantity-statement-${s.customerCode}-${s.to}.xlsx`;
      link.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.exporting.set(false);
    }
  }

  isOpen(productId: string): boolean {
    return this.open().has(productId);
  }

  toggle(productId: string): void {
    this.open.set(flip(this.open(), productId));
  }

  ratesOpen(productId: string): boolean {
    return this.openRates().has(productId);
  }

  toggleRates(productId: string): void {
    this.openRates.set(flip(this.openRates(), productId));
  }

  name(record: Partial<MultilingualName>): string {
    return localizedName(record, this.i18n.lang());
  }

  branchName(id: string): string {
    const branch = this.branches().find((b) => b.id === id);
    return branch ? this.name(branch) : '—';
  }

  itemName(item: Pick<QuantityStatementItem, 'name' | 'nameAr'>): string {
    return this.i18n.lang() === 'ar' && item.nameAr ? item.nameAr : item.name;
  }

  lineName(line: { name: string; nameAr: string | null }): string {
    return this.itemName(line);
  }

  rateKind(rate: QuantityStatementRate): string {
    return this.i18n.t('qstmt.rate.' + rate.kind);
  }

  /** The rates beside the line's own rent: an extension's or the overdue one, when they applied. */
  extraRates(rates: QuantityStatementRate[]): QuantityStatementRate[] {
    return rates.filter((r) => r.kind !== 'rent');
  }

  signed(qty: number): string {
    return qty > 0 ? `+${qty}` : String(qty);
  }

  /** A plain `yyyy-MM-dd` day, shown as that day whatever the browser's zone. */
  day(value: string): string {
    return formatDay(`${value.slice(0, 10)}T00:00:00Z`, this.i18n.lang(), 'UTC');
  }

  warning(w: QuantityStatementWarning): string {
    return this.i18n.t('qstmt.warn.' + w.code, {
      date: w.on ? this.day(w.on) : '',
      contract: w.contractNumber ?? '',
      qty: w.qty ?? 0,
    });
  }
}

function flip(set: ReadonlySet<string>, key: string): ReadonlySet<string> {
  const next = new Set(set);
  if (next.has(key)) next.delete(key);
  else next.add(key);
  return next;
}
