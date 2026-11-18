import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminBranch, AdminPaymentMethod, adminErrorMessage } from '../../../core/admin.service';
import { BranchScopeService } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PrintService } from '../../print/print.service';
import { formatDay, localizedName } from '../backoffice-format';
import { PAGE_CHROME } from '../shell/page-chrome';
import { withZ } from './receivable-view';
import { ReceivablePagesApi } from './receivable-pages.api';
import { STATUS_NAME, kindKey, statusKey } from './payment-voucher-view';
import { PaymentVoucher, PaymentVoucherKind, PaymentVoucherStatus } from './payment-vouchers-api.service';

const FILTERS = ['pending', 'approved', 'paid', 'cancelled', 'all'] as const;
type Filter = (typeof FILTERS)[number];

const FILTER_STATUS: Record<Filter, PaymentVoucherStatus | null> = {
  pending: 1,
  approved: 2,
  paid: 3,
  cancelled: 4,
  all: null,
};

/**
 * Money going out, as a worklist: what waits for a manager's signature, what is signed and
 * waits to be paid, and what has left.
 *
 * <p>Opens on the pending vouchers, because that is the queue somebody has to act on. Every row
 * says its state in words beside the colour; its number opens it, at its own address, where the
 * acts its state allows are offered to whoever the server allows them to.</p>
 *
 * <p>The branch filter is the Admin's working branch (D2): it starts there, and choosing here moves
 * it. A locked user has no filter — the server answers their branch whatever is asked, and the
 * page header names it.</p>
 */
@Component({
  selector: 'app-payment-voucher-list',
  standalone: true,
  imports: [
    FormsModule,
    RouterLink,
    DateInputComponent,
    ...PAGE_CHROME,
    ...MENU,
    ListFooterComponent,
    StatusComponent,
    TableStateComponent,
  ],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-primary" (click)="newVoucher.emit()">
        <span aria-hidden="true">+</span> {{ i18n.t('pv.new') }}
      </button>
    </ng-template>

    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <span class="filter-label" id="pv-state-label">{{ i18n.t('common.state') }}</span>
          <div class="bo-chips" role="group" aria-labelledby="pv-state-label">
            @for (choice of FILTERS; track choice) {
              <button type="button" [class.is-on]="filter() === choice" [attr.aria-pressed]="filter() === choice" (click)="setFilter(choice)">
                {{ i18n.t('pv.filter.' + choice) }}
              </button>
            }
          </div>
        </div>
        @if (scope.canChoose()) {
          <div class="ui-filter">
            <label for="pv-branch-filter">{{ i18n.t('branchLock.branch') }}</label>
            <select id="pv-branch-filter" [ngModel]="branchId()" (ngModelChange)="pickBranch($event)">
              <option [ngValue]="null">{{ i18n.t('pv.list.allBranches') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ name(b) }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter ui-filter--search">
          <label for="pv-find">{{ i18n.t('inv.find') }}</label>
          <input id="pv-find" type="search" [placeholder]="i18n.t('inv.find')" [ngModel]="query()" (ngModelChange)="setQuery($event)" />
        </div>
        <!-- More filters (owner, 2026-11-17), applied by the server over every voucher of the state chosen. -->
        @if (kinds().length > 1 || kindFilter() !== 'all') {
          <div class="ui-filter">
            <label for="pv-f-kind">{{ i18n.t('pv.form.kind') }}</label>
            <select id="pv-f-kind" [ngModel]="kindFilter()" (ngModelChange)="kindFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              @for (k of kinds(); track k) {
                <option [ngValue]="k">{{ i18n.t(kindKey(k)) }}</option>
              }
            </select>
          </div>
        }
        @if (methodChoices().length > 1 || methodFilter() !== 'all') {
          <div class="ui-filter">
            <label for="pv-f-method">{{ i18n.t('pay.method') }}</label>
            <select id="pv-f-method" [ngModel]="methodFilter()" (ngModelChange)="methodFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              @for (m of methodChoices(); track m.key) {
                <option [ngValue]="m.key">{{ m.name }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <label for="pv-f-from">{{ i18n.t('filters.from') }}</label>
          <app-date-input id="pv-f-from" [ngModel]="fromFilter()" (ngModelChange)="fromFilter.set($event || '')" />
        </div>
        <div class="ui-filter">
          <label for="pv-f-to">{{ i18n.t('filters.to') }}</label>
          <app-date-input id="pv-f-to" [ngModel]="toFilter()" (ngModelChange)="toFilter.set($event || '')" />
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
      @if (error() && pager.total()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <div class="ui-metrics">
        <div class="ui-metric" [class.ui-metric--lead]="filter() === 'pending'">
          <span class="ui-metric__label">{{ i18n.t('pv.metric.pending') }}</span>
          <strong class="ui-metric__value mono">{{ i18n.money(totals().pending) }}</strong>
          <span class="ui-metric__note">{{ i18n.t('pv.metric.pendingNote') }}</span>
        </div>
        <div class="ui-metric" [class.ui-metric--lead]="filter() === 'approved'">
          <span class="ui-metric__label">{{ i18n.t('pv.metric.approved') }}</span>
          <strong class="ui-metric__value mono">{{ i18n.money(totals().approved) }}</strong>
          <span class="ui-metric__note">{{ i18n.t('pv.metric.approvedNote') }}</span>
        </div>
      </div>

      <section class="ui-surface" [attr.aria-busy]="pager.loading() && !pager.total()">
        <div class="ui-surface__head">
          <p>{{ i18n.t('pv.list.note') }}</p>
        </div>
        @if (!pager.loaded() && !error()) {
          <app-table-state kind="loading" />
        } @else if (error() && !pager.total()) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('pv.list.none') + ' ' + i18n.t('pv.list.noneHint')">
            <button type="button" class="btn-primary" (click)="newVoucher.emit()">{{ i18n.t('pv.new') }}</button>
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('inv.number') }}</th>
                  <th>{{ i18n.t('pv.view.createdOn') }}</th>
                  <th>{{ i18n.t('pv.form.kind') }}</th>
                  <th>{{ i18n.t('pv.view.payee') }}</th>
                  @if (showBranch()) {
                    <th>{{ i18n.t('branchLock.branch') }}</th>
                  }
                  <th>{{ i18n.t('pay.method') }}</th>
                  <th class="num">{{ i18n.t('pay.amount') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (v of pager.rows(); track v.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('inv.number')">
                      <a class="ui-link mono" [routerLink]="['/backoffice', 'finance', 'vouchers', v.id]">{{ v.number }}</a>
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('pv.view.createdOn')">{{ day(v.createdAtUtc) }}</td>
                    <td [attr.data-label]="i18n.t('pv.form.kind')">
                      <span>{{ i18n.t(kindKey(v.kind)) }}</span>
                      @if (v.expenseCategory) {
                        <span class="ui-sub">{{ categoryWord(v.expenseCategory) }}</span>
                      }
                      @if (v.returnNoteNumber) {
                        <span class="ui-sub mono">{{ v.returnNoteNumber }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('pv.view.payee')">{{ v.payeeName || v.customerName || '—' }}</td>
                    @if (showBranch()) {
                      <td [attr.data-label]="i18n.t('branchLock.branch')">{{ branchName(v) }}</td>
                    }
                    <td [attr.data-label]="i18n.t('pay.method')">
                      <span>{{ methodName(v) }}</span>
                      @if (v.instrumentNumber) {
                        <span class="ui-sub mono">{{ v.instrumentNumber }}</span>
                      }
                    </td>
                    <td class="num mono figure" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(v.amount) }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(v)">{{ i18n.t(statusKey(v.status)) }}</app-status>
                      @if (v.status === 2 && v.approvedByName) {
                        <span class="ui-sub">{{ v.approvedByName }}</span>
                      }
                    </td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + v.number">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <a appMenuItem [routerLink]="['/backoffice', 'finance', 'vouchers', v.id]">{{ i18n.t('common.view') }}</a>
                        <button type="button" appMenuItem (click)="printer.open('payment-voucher', v.id)">{{ i18n.t('common.print') }}</button>
                      </app-menu>
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
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .figure {
        font-weight: 700;
      }
      .filter-label {
        font-size: var(--fs-meta);
        font-weight: 600;
        color: var(--muted);
      }
    `,
  ],
})
export class PaymentVoucherListComponent {
  private readonly pages = inject(ReceivablePagesApi);
  readonly printer = inject(PrintService);
  readonly i18n = inject(I18nService);
  readonly scope = inject(BranchScopeService);
  private readonly regional = inject(RegionalService);

  readonly branches = input<AdminBranch[]>([]);
  readonly methods = input<AdminPaymentMethod[]>([]);
  readonly newVoucher = output<void>();

  readonly FILTERS = FILTERS;
  readonly kindKey = kindKey;
  readonly statusKey = statusKey;

  readonly filter = signal<Filter>('pending');
  readonly query = signal('');

  /** The branch asked for: the working branch for the Admin (null: every branch), else their own. */
  readonly branchId = computed(() => this.scope.resolve('branchOrAll').branchId);
  /** Rows of several branches say whose; one branch's rows need not. */
  readonly showBranch = computed(() => this.branchId() === null);

  // More filters (owner, 2026-11-17), applied by the server over every voucher, like the finder.
  readonly kindFilter = signal<'all' | PaymentVoucherKind>('all');
  readonly methodFilter = signal<string>('all');
  /** A day range on the day the voucher was made, read in the company's zone (`yyyy-MM-dd`, '' for none). */
  readonly fromFilter = signal('');
  readonly toFilter = signal('');

  readonly filtered = computed(
    () =>
      !!this.query().trim() ||
      this.kindFilter() !== 'all' ||
      this.methodFilter() !== 'all' ||
      !!this.fromFilter() ||
      !!this.toFilter(),
  );

  /**
   * The vouchers a page at a time from the server (owner, 2026-11-17: "paginate in backend, not
   * frontend"): ten rows to begin with, 20, 50 or 100 at the reader's choice. The state, the branch,
   * the finder and the filters run there over every voucher — no longer the first thousand —
   * newest first. Another filter or branch starts at page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.paymentVouchersPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'finance.paymentVouchers',
      filters: () => ({
        branchId: this.branchId(),
        status: FILTER_STATUS[this.filter()],
        q: this.query().trim(),
        kind: this.kindFilter(),
        method: this.methodFilter(),
        from: this.fromFilter(),
        to: this.toFilter(),
      }),
    },
  );

  /** The list could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  /** The kinds and methods the vouchers of this state hold, so a choice can always match. */
  readonly kinds = computed(() => this.pager.summary()?.kinds ?? []);
  readonly methodChoices = computed(() =>
    (this.pager.summary()?.methods ?? []).map((m) => ({ key: m.key, name: this.methodLabel(m.key, m.name) })),
  );

  clearFilters(): void {
    this.query.set('');
    this.kindFilter.set('all');
    this.methodFilter.set('all');
    this.fromFilter.set('');
    this.toFilter.set('');
  }

  /** Both queues of the branch, whatever the chip and the filters: the two figures a manager opens this screen for. */
  readonly totals = computed(() => {
    const summary = this.pager.summary();
    return { pending: summary?.pendingTotal ?? 0, approved: summary?.approvedTotal ?? 0 };
  });

  // Each change below is a filter of the pager, which asks the server again from page one; so is
  // the branch, chosen here or in the header.

  setFilter(filter: Filter): void {
    this.filter.set(filter);
  }

  setQuery(value: string): void {
    this.query.set(value);
  }

  pickBranch(id: string | null): void {
    this.scope.set(id ? { kind: 'branch', id } : { kind: 'all' });
  }

  tone(v: PaymentVoucher) {
    return toneOf('voucher', STATUS_NAME[v.status]);
  }

  categoryWord(category: string): string {
    const key = 'pv.category.' + category;
    const word = this.i18n.t(key);
    return word === key ? category : word;
  }

  methodName(v: PaymentVoucher): string {
    return this.methodLabel(v.methodKey, v.methodName);
  }

  /** A method in the reader's words, or the name the server sent beside it. */
  private methodLabel(key: string, fallback: string): string {
    const method = this.methods().find((m) => m.key === key);
    return method ? localizedName(method, this.i18n.lang()) : fallback;
  }

  branchName(v: PaymentVoucher): string {
    const branch = this.branches().find((b) => b.id === v.branchId);
    return branch ? this.name(branch) : v.branchName;
  }

  name(value: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(value, this.i18n.lang());
  }

  day(value: string | null): string {
    return formatDay(value ? withZ(value) : null, this.i18n.lang(), this.regional.settings().timeZone);
  }

  /** Asks again for the page on screen. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }
}
