import { Component, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminCustomer, adminErrorMessage } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME } from '../shell/page-chrome';
import { I18nService } from '../../../core/i18n.service';
import { AuthService } from '../../../core/auth.service';
import { RegionalService } from '../../../core/regional.service';
import { formatDay, localizedName } from '../backoffice-format';
import { RentalPagesApi } from './rental-pages.api';
import { RentalContractRow } from './rentals-api.service';
import { RentalTotals } from './rental-totals';
import { ContractState, contractState, seesCancelled } from './rental-view';

// "Cancelled" is the Admin's alone (owner, 2026-10-06); the server hides those rows from everyone else.
const FILTERS = ['open', 'all', 'closed', 'cancelled'] as const;
type Filter = (typeof FILTERS)[number];

/** The progress words of a contract, in the order it goes through them (rental-view.ts). */
const STAGES: readonly ContractState[] = ['draft', 'out', 'partly', 'unpaid', 'closed', 'cancelled'];

/**
 * The question this screen is opened to answer: **what is out, and with whom.**
 *
 * <p>There is no due-date column, and that is not an omission. A contract has no end date: each
 * line carries its own duration and each issue batch counts from the day it went out, so one
 * contract can have ten frames due tomorrow and forty due in three weeks. A single date here
 * would be a number the business does not have, and every clerk would work to it.</p>
 *
 * <p>The quantity out and the money charged arrive on the row itself: the list route sums the
 * stored documents in three set queries whatever the number of contracts. The figures are **what
 * was charged** — the contract screen recomputes from the documents, and the two agree by
 * construction — so the header tiles are a live picture of the whole yard, not of the page.</p>
 *
 * <p>Paged on the server (owner, 2026-11-17: "paginate in backend, not frontend"): `GET
 * /api/rentals/contracts/page` runs every filter over every contract and sends one page, with the
 * tiles summed over the state chip and the customer, as before.</p>
 */
@Component({
  selector: 'app-rental-contracts',
  standalone: true,
  imports: [FormsModule, RouterLink, ...PAGE_CHROME, DateInputComponent, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-primary" (click)="createContract.emit()">
        <span aria-hidden="true">+</span> {{ i18n.t('rent.newContract') }}
      </button>
    </ng-template>

    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <span class="filter-label" id="rc-state-label">{{ i18n.t('common.state') }}</span>
          <div class="bo-chips" role="group" aria-labelledby="rc-state-label">
            @for (choice of filters(); track choice) {
              <button type="button" [class.is-on]="filter() === choice" [attr.aria-pressed]="filter() === choice" (click)="setFilter(choice)">
                {{ i18n.t('rent.filter.' + choice) }}
              </button>
            }
          </div>
        </div>
        <div class="ui-filter">
          <label for="rc-customer">{{ i18n.t('common.customer') }}</label>
          <select id="rc-customer" [ngModel]="customerId()" (ngModelChange)="pickCustomer($event)">
            <option [ngValue]="null">{{ i18n.t('rent.filter.allCustomers') }}</option>
            @for (c of customers(); track c.id) {
              <option [ngValue]="c.id">{{ c.fullName }}</option>
            }
          </select>
        </div>
        <div class="ui-filter ui-filter--search">
          <label for="rc-find">{{ i18n.t('rent.list.find') }}</label>
          <input id="rc-find" type="search" [placeholder]="i18n.t('rent.list.findHint')" [ngModel]="query()" (ngModelChange)="setQuery($event)" />
        </div>
        <!-- More filters (owner, 2026-11-17), applied by the server like the two above. -->
        @if (stagesShown().length > 1 || stageFilter() !== 'all') {
          <div class="ui-filter">
            <label for="rc-f-stage">{{ i18n.t('rent.filter.stage') }}</label>
            <select id="rc-f-stage" [ngModel]="stageFilter()" (ngModelChange)="stageFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              @for (s of stagesShown(); track s) {
                <option [ngValue]="s">{{ i18n.t('rent.state.' + s) }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <label for="rc-f-late">{{ i18n.t('rent.list.returnedLate') }}</label>
          <select id="rc-f-late" [ngModel]="lateFilter()" (ngModelChange)="lateFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
            <option ngValue="no">{{ i18n.t('filters.no') }}</option>
          </select>
        </div>
        @if (showBranch()) {
          <div class="ui-filter">
            <label for="rc-f-branch">{{ i18n.t('filters.branch') }}</label>
            <select id="rc-f-branch" [ngModel]="branchFilter()" (ngModelChange)="branchFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ branchName(b.id) }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <label for="rc-f-from">{{ i18n.t('filters.from') }}</label>
          <app-date-input id="rc-f-from" [ngModel]="signedFrom()" (ngModelChange)="signedFrom.set($event ?? '')" />
        </div>
        <div class="ui-filter">
          <label for="rc-f-to">{{ i18n.t('filters.to') }}</label>
          <app-date-input id="rc-f-to" [ngModel]="signedTo()" (ngModelChange)="signedTo.set($event ?? '')" />
        </div>
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="pager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() && pager.loaded()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <!-- The yard at a glance, summed by the server over the contracts of the state chip and the
           customer (no new figure): what is out and with how many customers, what has been charged,
           overdue charges billed on returns, and contracts not yet issued. Open contracts only. -->
      <div class="ui-metrics">
        <div class="ui-metric ui-metric--lead">
          <span class="ui-metric__label">{{ i18n.t('rent.list.outNow') }}</span>
          <strong class="ui-metric__value mono">{{ totals().stillOut }}</strong>
          <span class="ui-metric__note">{{ i18n.t('rent.list.withCustomers', { n: totals().openWithGoods }) }}</span>
        </div>
        <div class="ui-metric">
          <span class="ui-metric__label">{{ i18n.t('rent.chargedSoFar') }}</span>
          <strong class="ui-metric__value mono">{{ i18n.money(totals().charged) }}</strong>
          <span class="ui-metric__note">{{ i18n.t('rent.list.openOnly') }}</span>
        </div>
        <div class="ui-metric" [class.ui-metric--warn]="totals().overdue > 0">
          <span class="ui-metric__label">{{ i18n.t('rent.kind.Overdue') }}</span>
          <strong class="ui-metric__value mono">{{ i18n.money(totals().overdue) }}</strong>
          <span class="ui-metric__note">{{ i18n.t('rent.overdueTileNote') }}</span>
        </div>
        <div class="ui-metric">
          <span class="ui-metric__label">{{ i18n.t('rent.list.notIssued') }}</span>
          <strong class="ui-metric__value mono">{{ totals().drafts }}</strong>
          <span class="ui-metric__note">{{ i18n.t('rent.list.stillEditable') }}</span>
        </div>
      </div>

      <section class="ui-surface" [attr.aria-busy]="!pager.loaded()">
        <div class="ui-surface__head">
          <p>{{ i18n.t('rent.list.noEndDate') }}</p>
        </div>
        @if (!pager.loaded() && !error()) {
          <app-table-state kind="loading" />
        } @else if (error() && !pager.loaded()) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!summary()?.count) {
          <app-table-state kind="empty" [message]="i18n.t('rent.list.none') + ' ' + i18n.t('rent.list.noneHint')">
            <button type="button" class="btn-primary" (click)="createContract.emit()">{{ i18n.t('rent.newContract') }}</button>
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('rent.contract') }}</th>
                  <th>{{ i18n.t('common.customer') }}</th>
                  @if (showBranch()) {
                    <th>{{ i18n.t('rent.branch') }}</th>
                  }
                  <th>{{ i18n.t('rent.signedOn') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="num">{{ i18n.t('rent.stillOut') }}</th>
                  <th class="num">{{ i18n.t('rent.chargedSoFar') }}</th>
                  <!-- Only while the switch is on: no column, no zero (§5.6). -->
                  @if (depositVisible()) {
                    <th class="num">{{ i18n.t('rent.deposit') }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (row of pager.rows(); track row.id) {
                  <tr>
                    <!-- The number opens the contract, at its own address. -->
                    <td [attr.data-label]="i18n.t('rent.contract')">
                      <a class="ui-link mono" [routerLink]="['/backoffice', 'rentals', 'contracts', row.id]">{{ row.number }}</a>
                    </td>
                    <td [attr.data-label]="i18n.t('common.customer')">{{ row.customerName || '—' }}</td>
                    @if (showBranch()) {
                      <td [attr.data-label]="i18n.t('rent.branch')">{{ branchName(row.branchId) }}</td>
                    }
                    <td class="mono" [attr.data-label]="i18n.t('rent.signedOn')">{{ day(row.signedAtUtc) }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <span class="states">
                        <app-status [tone]="tone(row)">{{ i18n.t('rent.state.' + state(row)) }}</app-status>
                        @if (row.overdueTotal > 0) {
                          <app-status tone="critical">{{ i18n.t('rent.list.returnedLate') }}</app-status>
                        }
                      </span>
                    </td>
                    <td class="num mono figure" [attr.data-label]="i18n.t('rent.stillOut')">{{ row.stillOutQty }}</td>
                    <td class="num mono figure" [attr.data-label]="i18n.t('rent.chargedSoFar')">{{ i18n.money(row.chargeTotal) }}</td>
                    @if (depositVisible()) {
                      <td class="num mono" [attr.data-label]="i18n.t('rent.deposit')">
                        {{ row.depositAmount ? i18n.money(row.depositAmount) : '—' }}
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>

      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .figure {
        font-weight: 700;
      }
      .states {
        display: inline-flex;
        flex-wrap: wrap;
        gap: var(--sp-1);
      }
      .filter-label {
        font-size: var(--fs-meta);
        font-weight: 600;
        color: var(--muted);
      }
    `,
  ],
})
export class RentalContractsComponent {
  private readonly pagesApi = inject(RentalPagesApi);
  readonly i18n = inject(I18nService);
  private readonly regional = inject(RegionalService);

  readonly customers = input<AdminCustomer[]>([]);
  readonly branches = input<AdminBranch[]>([]);
  private readonly lock = inject(BranchLock);
  /** The Admin sees every branch's contracts, so each row says whose; a locked user sees one branch. */
  readonly showBranch = computed(() => !this.lock.locked());
  readonly openContract = output<string>();
  readonly createContract = output<void>();

  private readonly auth = inject(AuthService);
  readonly filters = computed(() => FILTERS.filter((f) => f !== 'cancelled' || seesCancelled(this.auth.currentUser())));
  readonly filter = signal<Filter>('open');
  readonly customerId = signal<string | null>(null);
  readonly query = signal('');

  // More filters (owner, 2026-11-17), sent to the server with the state chips and the customer.
  readonly stageFilter = signal<'all' | ContractState>('all');
  readonly lateFilter = signal<'all' | 'yes' | 'no'>('all');
  readonly branchFilter = signal<string>('all');
  /** The signing day, `yyyy-MM-dd`, read in the company's zone. */
  readonly signedFrom = signal('');
  readonly signedTo = signal('');

  /**
   * The contracts a page at a time from the server (owner, 2026-11-17: "paginate in backend, not
   * frontend"): ten to begin with, 20, 50 or 100 at the user's choice. Every filter runs there, over
   * every contract, and a change goes back to page one.
   */
  readonly pager = createServerPager(
    (page) => this.pagesApi.contractsPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'rentals.contracts',
      filters: () => {
        const filter = this.filter();
        return {
          status: filter === 'all' ? undefined : filter === 'open' ? 'Open' : filter === 'closed' ? 'Closed' : 'Cancelled',
          customerId: this.customerId(),
          q: this.query().trim(),
          stage: this.stageFilter(),
          late: this.lateFilter(),
          branch: this.showBranch() ? this.branchFilter() : 'all',
          from: this.signedFrom(),
          to: this.signedTo(),
        };
      },
    },
  );

  /** The server's figures over the contracts of the state chip and the customer; null until it answers. */
  readonly summary = computed(() => this.pager.summary());

  /** The list could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  /**
   * The progress words those contracts actually carry, in the order a contract goes through them —
   * plus the one chosen, so a reload that no longer holds it still shows what is selected.
   */
  readonly stagesShown = computed(() => {
    const present = new Set<ContractState>(this.summary()?.stages ?? []);
    const chosen = this.stageFilter();
    if (chosen !== 'all') present.add(chosen);
    return STAGES.filter((s) => present.has(s));
  });

  readonly filtered = computed(
    () =>
      !!this.query().trim() ||
      this.stageFilter() !== 'all' ||
      this.lateFilter() !== 'all' ||
      (this.showBranch() && this.branchFilter() !== 'all') ||
      !!this.signedFrom() ||
      !!this.signedTo(),
  );

  setQuery(value: string): void {
    this.query.set(value);
  }

  /** The search and the further filters back to "all"; the state chip and the customer stay. */
  clearFilters(): void {
    this.query.set('');
    this.stageFilter.set('all');
    this.lateFilter.set('all');
    this.branchFilter.set('all');
    this.signedFrom.set('');
    this.signedTo.set('');
  }

  branchName(id: string): string {
    const branch = this.branches().find((b) => b.id === id);
    return branch ? localizedName(branch, this.i18n.lang()) : '—';
  }

  /**
   * The server sends the deposit on every row or on none: the switch decides, not the contract. It
   * says whether any contract of the state chip and the customer carries one, as the rows did.
   */
  readonly depositVisible = computed(() => this.summary()?.depositVisible ?? false);

  /** The same sums the dashboard's rentals block shows (rental-totals.ts), made by the server. */
  readonly totals = computed<RentalTotals>(
    () => this.summary()?.totals ?? { stillOut: 0, charged: 0, overdue: 0, drafts: 0, openWithGoods: 0 },
  );

  setFilter(filter: Filter): void {
    this.filter.set(filter);
  }

  pickCustomer(id: string | null): void {
    this.customerId.set(id);
  }

  state(row: RentalContractRow) {
    return contractState(row, row.stillOutQty, row.settledQty);
  }

  tone(row: RentalContractRow) {
    return toneOf('contract', this.state(row));
  }

  day(value: string | null): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  /** Asks again for the page on screen (the refresh button, the error state's retry). */
  async reload(): Promise<void> {
    await this.pager.reload();
  }
}
