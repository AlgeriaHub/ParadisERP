import { NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminBranch, AdminCustomer, AdminPaymentMethod, adminErrorMessage } from '../../../core/admin.service';
import { BranchScopeService } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { ConfirmService } from '../../../ui/confirm.service';
import { collectRequest } from '../../../ui/confirm-requests';
import { MENU } from '../../../ui/menu.component';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatDay, localizedName } from '../backoffice-format';
import { PAGE_CHROME } from '../shell/page-chrome';
import { DocRef, DueWord, dueWord, withZ } from './receivable-view';
import { ReceivablePagesApi } from './receivable-pages.api';
import { KEEP_ADVANCE_PERMISSION, KeepAdvanceDialogComponent, keepable, keepingAllowed } from './keep-advance-dialog.component';
import { ReceiptStatus, ReceiptVoucher, ReceivableSettings, ReceivablesApi } from './receivables-api.service';
import { AuthService } from '../../../core/auth.service';
import { mayDo } from '../../../core/landing';

const FILTERS = ['pending', 'confirmed', 'bounced', 'all'] as const;
type Filter = (typeof FILTERS)[number];

/** The chip word to the server's status; `all` sends no filter. */
const FILTER_STATUS: Record<Filter, ReceiptStatus | null> = {
  pending: 'Pending',
  confirmed: 'Confirmed',
  bounced: 'Bounced',
  all: null,
};

let nextId = 0;

/**
 * Which cheques and transfers sit uncleared, which are past their date, what has actually been
 * collected, and what bounced.
 *
 * <p>A pending voucher settles nothing: the invoice it is written against stays owed until the
 * money is confirmed, so the list leads with the paper still in hand and how much of it is
 * already past its date. The server's order — due date, then the day it was received, oldest
 * first — is kept as it comes, because that is already "chase these first".</p>
 *
 * <p>Collecting is one choice with a confirmation, since it needs nothing more than the fact.
 * Bouncing opens the voucher instead: a returned cheque deserves the bank's reason written
 * down, and the detail screen is where that is typed. Both stay here for whoever holds the cash
 * box, who has no other place to settle a receipt; Finance › Awaiting collection gathers the same
 * paper with the till's for the managers.</p>
 *
 * <p>Two homes. As Finance › Receipts (no `customerId`) it is a page: its new-receipt action and
 * filters sit in the page header, its branch filter is the working branch (D2), and a number opens
 * the receipt at its own address. Inside a customer's account it is that customer's history, its
 * tools drawn in place and every state shown.</p>
 */
@Component({
  selector: 'app-receivable-receipts',
  standalone: true,
  imports: [
    NgTemplateOutlet, FormsModule, RouterLink, ...PAGE_CHROME, ...MENU, ListFooterComponent, StatusComponent, TableStateComponent,
    KeepAdvanceDialogComponent,
  ],
  template: `
    <ng-template #newAction>
      <button type="button" class="btn-primary" (click)="newReceipt.emit(customerId() ?? selectedCustomer())">
        <span aria-hidden="true">+</span> {{ i18n.t('inv.acct.newReceipt') }}
      </button>
    </ng-template>

    <ng-template #filtersBar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <span class="filter-label" [id]="ids.state">{{ i18n.t('common.state') }}</span>
          <div class="bo-chips" role="group" [attr.aria-labelledby]="ids.state">
            @for (choice of filters; track choice) {
              <button type="button" [class.is-on]="filter() === choice" [attr.aria-pressed]="filter() === choice" (click)="setFilter(choice)">
                {{ i18n.t('inv.rcpt.filter.' + choice) }}
              </button>
            }
          </div>
        </div>
        @if (!customerId() && scope.canChoose()) {
          <div class="ui-filter">
            <label for="rr-branch">{{ i18n.t('branchLock.branch') }}</label>
            <select id="rr-branch" [ngModel]="branchId()" (ngModelChange)="pickBranch($event)">
              <option [ngValue]="null">{{ i18n.t('pv.list.allBranches') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ name(b) }}</option>
              }
            </select>
          </div>
        }
        @if (!customerId()) {
          <div class="ui-filter">
            <label for="rr-customer">{{ i18n.t('common.customer') }}</label>
            <select id="rr-customer" [ngModel]="selectedCustomer()" (ngModelChange)="pickCustomer($event)">
              <option [ngValue]="null">{{ i18n.t('rent.filter.allCustomers') }}</option>
              @for (c of customers(); track c.id) {
                <option [ngValue]="c.id">{{ c.fullName }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter ui-filter--search">
          <label [for]="ids.find">{{ i18n.t('inv.find') }}</label>
          <input [id]="ids.find" type="search" [placeholder]="i18n.t('inv.find')" [ngModel]="query()" (ngModelChange)="setQuery($event)" />
        </div>
        <!-- More filters (owner, 2026-11-17), applied by the server over every receipt of the state chosen. -->
        @if (methodKeys().length > 1 || methodFilter() !== 'all') {
          <div class="ui-filter">
            <label [for]="ids.method">{{ i18n.t('inv.rcpt.method') }}</label>
            <select [id]="ids.method" [ngModel]="methodFilter()" (ngModelChange)="methodFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              @for (key of methodKeys(); track key) {
                <option [ngValue]="key">{{ methodName(key) }}</option>
              }
            </select>
          </div>
        }
        @if (hasPending() || dueFilter() !== 'all') {
          <div class="ui-filter">
            <label [for]="ids.due">{{ i18n.t('treasury.dueDate') }}</label>
            <select [id]="ids.due" [ngModel]="dueFilter()" (ngModelChange)="dueFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              @for (word of dueWords; track word) {
                <option [ngValue]="word">{{ i18n.t('treasury.due.' + word) }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <label [for]="ids.unallocated">{{ i18n.t('inv.rcpt.unallocated') }}</label>
          <select [id]="ids.unallocated" [ngModel]="unallocatedFilter()" (ngModelChange)="unallocatedFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
            <option ngValue="no">{{ i18n.t('filters.no') }}</option>
          </select>
        </div>
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="busy() || pager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
          @if (customerId()) {
            <ng-container *ngTemplateOutlet="newAction" />
          }
        </div>
      </div>
    </ng-template>

    @if (!customerId()) {
      <ng-template appPageActions><ng-container *ngTemplateOutlet="newAction" /></ng-template>
      <ng-template appPageToolbar><ng-container *ngTemplateOutlet="filtersBar" /></ng-template>
    }

    <div class="bo-stack">
      @if (shownError() && pager.total()) {
        <p class="notice danger" role="alert">{{ shownError() }}</p>
      }
      @if (done()) {
        <p class="notice ok" role="status">{{ done() }}</p>
      }

      <div class="ui-metrics">
        <div class="ui-metric ui-metric--lead">
          <span class="ui-metric__label">{{ i18n.t('inv.rcpt.pendingTotal') }}</span>
          <strong class="ui-metric__value mono">{{ i18n.money(pendingTotal()) }}</strong>
          <span class="ui-metric__note">{{ i18n.t('inv.rcpt.loadedNote') }}</span>
        </div>
        <div class="ui-metric" [class.ui-metric--warn]="dueTotal() > 0">
          <span class="ui-metric__label">{{ i18n.t('inv.rcpt.dueTotal') }}</span>
          <strong class="ui-metric__value mono">{{ i18n.money(dueTotal()) }}</strong>
          <span class="ui-metric__note">{{ i18n.t('inv.rcpt.dueTotalNote') }} {{ i18n.t('inv.rcpt.loadedNote') }}</span>
        </div>
        @if (unallocatedTotal() > 0) {
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('inv.rcpt.unallocated') }}</span>
            <strong class="ui-metric__value mono">{{ i18n.money(unallocatedTotal()) }}</strong>
            <span class="ui-metric__note">{{ i18n.t('inv.rcpt.unallocatedHint') }} {{ i18n.t('inv.rcpt.loadedNote') }}</span>
          </div>
        }
      </div>

      @if (customerId()) {
        <ng-container *ngTemplateOutlet="filtersBar" />
      }

      <section class="ui-surface" [attr.aria-busy]="pager.loading() && !pager.total()">
        <div class="ui-surface__head">
          <p>{{ i18n.t('inv.rcpt.pendingNote') }}</p>
        </div>
        @if (!pager.loaded() && !listError()) {
          <app-table-state kind="loading" />
        } @else if (shownError() && !pager.total()) {
          <app-table-state kind="error" [message]="shownError()" (retry)="reload()" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state
            kind="empty"
            [message]="i18n.t(filter() === 'pending' ? 'inv.rcpt.nonePending' : 'inv.rcpt.none') + ' ' + i18n.t('inv.rcpt.noneHint')"
          />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('inv.number') }}</th>
                  @if (!customerId()) {
                    <th>{{ i18n.t('common.customer') }}</th>
                  }
                  @if (showBranch()) {
                    <th>{{ i18n.t('branchLock.branch') }}</th>
                  }
                  <th>{{ i18n.t('inv.rcpt.receivedOn') }}</th>
                  <th>{{ i18n.t('inv.rcpt.method') }}</th>
                  <th>{{ i18n.t('treasury.instrument') }}</th>
                  <th>{{ i18n.t('treasury.dueDate') }}</th>
                  <th class="num">{{ i18n.t('pay.amount') }}</th>
                  <th class="num">{{ i18n.t('inv.rcpt.allocated') }}</th>
                  <th class="num">{{ i18n.t('inv.rcpt.unallocated') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (v of pager.rows(); track v.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('inv.number')">
                      @if (linkBase()) {
                        <a class="ui-link mono" [routerLink]="link(v.id)">{{ v.number }}</a>
                      } @else {
                        <button type="button" class="ui-link mono" (click)="openReceipt.emit({ id: v.id, customerId: v.customerId })">{{ v.number }}</button>
                      }
                    </td>
                    @if (!customerId()) {
                      <td [attr.data-label]="i18n.t('common.customer')">{{ v.customerName || '—' }}</td>
                    }
                    @if (showBranch()) {
                      <td [attr.data-label]="i18n.t('branchLock.branch')">{{ branchName(v.branchId) }}</td>
                    }
                    <td class="mono" [attr.data-label]="i18n.t('inv.rcpt.receivedOn')">{{ day(v.receivedAtUtc) }}</td>
                    <td [attr.data-label]="i18n.t('inv.rcpt.method')">
                      <span>{{ methodName(v.methodKey) }}</span>
                      <span class="ui-sub">{{ behaviorWord(v.behavior) }}</span>
                    </td>
                    <td [attr.data-label]="i18n.t('treasury.instrument')">
                      @if (v.instrumentNumber || v.instrumentBank) {
                        @if (v.instrumentNumber) {
                          <span class="mono">{{ v.instrumentNumber }}</span>
                        }
                        @if (v.instrumentBank) {
                          <span class="ui-sub">{{ v.instrumentBank }}</span>
                        }
                      } @else {
                        <span>—</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('treasury.dueDate')">
                      <span class="mono">{{ day(v.instrumentDueDate) }}</span>
                      @if (v.status === 'Pending') {
                        <app-status [tone]="dueTone(v)">{{ i18n.t('treasury.due.' + due(v)) }}</app-status>
                      }
                    </td>
                    <td class="num mono figure" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(v.amount) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('inv.rcpt.allocated')">{{ i18n.money(v.allocated) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('inv.rcpt.unallocated')">
                      @if (v.unallocated > 0) {
                        {{ i18n.money(v.unallocated) }}
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="stateTone(v.status)">{{ stateWord(v.status) }}</app-status>
                      @if (v.statusNote) {
                        <span class="ui-sub">{{ v.statusNote }}</span>
                      }
                    </td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + v.number">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="openReceipt.emit({ id: v.id, customerId: v.customerId })">{{ i18n.t('common.view') }}</button>
                        @if (!customerId() && showAccount()) {
                          <button type="button" appMenuItem (click)="openAccount.emit(v.customerId)">{{ i18n.t('inv.openAccount') }}</button>
                        }
                        @if (canKeep(v)) {
                          <!-- SAL-13: only while the accountant's setting allows it, for whoever approves refunds. -->
                          <button type="button" appMenuItem [disabled]="busy()" (click)="keeping.set(v)">{{ i18n.t('adv.keep.menu') }}</button>
                        }
                        @if (v.status === 'Pending') {
                          <button type="button" appMenuItem [disabled]="busy()" (click)="collect(v)">{{ i18n.t('treasury.markCollected') }}</button>
                          <hr appMenuSeparator />
                          <button type="button" appMenuItem class="is-danger" [disabled]="busy()" (click)="bounce.emit({ id: v.id, customerId: v.customerId })">
                            {{ i18n.t('treasury.markBounced') }}
                          </button>
                        }
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

    <app-keep-advance-dialog [receipt]="keeping()" [incomeAccount]="keepAccount()" (kept)="onKept($event)" (dismiss)="keeping.set(null)" />
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
        color: var(--text-2);
      }
      td app-status {
        margin-inline-start: var(--sp-1);
      }
    `,
  ],
})
export class ReceivableReceiptsComponent implements OnInit {
  private readonly api = inject(ReceivablesApi);
  private readonly pages = inject(ReceivablePagesApi);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly scope = inject(BranchScopeService);
  private readonly regional = inject(RegionalService);

  /** Set inside an account: the list is that customer's alone and the customer column goes. */
  readonly customerId = input<string | null>(null);
  readonly customers = input<AdminCustomer[]>([]);
  readonly methods = input<AdminPaymentMethod[]>([]);
  readonly branches = input<AdminBranch[]>([]);
  /** Whether this reader may open a customer's account (the nav table's rule). */
  readonly showAccount = input(true);
  /** Where a receipt lives as an address (`finance/receipts`): its number links there. */
  readonly linkBase = input<string[] | null>(null);

  readonly openReceipt = output<DocRef>();
  readonly openAccount = output<string>();
  readonly newReceipt = output<string | null>();
  readonly bounce = output<DocRef>();

  readonly filters = FILTERS;
  readonly filter = signal<Filter>('pending');
  readonly selectedCustomer = signal<string | null>(null);
  readonly query = signal('');
  /** What an action (collecting) is doing; the list's own loading is the pager's. */
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);

  /** Two of these can sit on one screen (the page, an account): their field ids must not clash. */
  readonly ids = (() => {
    const n = nextId++;
    return {
      state: `rr-state-${n}`,
      find: `rr-find-${n}`,
      method: `rr-f-method-${n}`,
      due: `rr-f-due-${n}`,
      unallocated: `rr-f-unallocated-${n}`,
    };
  })();

  /**
   * The branch asked for. As a page: the working branch for the Admin (null: every branch), their
   * own for a locked user. Inside an account: none — the customer's whole history, as before.
   */
  readonly branchId = computed(() => (this.customerId() ? null : this.scope.resolve('branchOrAll').branchId));
  /** Rows of several branches say whose. */
  readonly showBranch = computed(() => !this.customerId() && this.branchId() === null);

  // More filters (owner, 2026-11-17), applied by the server over every receipt, like the finder.
  readonly methodFilter = signal<string>('all');
  /** The due date's word, as the state column says it; it applies to paper still pending. */
  readonly dueFilter = signal<'all' | DueWord>('all');
  /** Money left on the customer's account, written against no invoice (`unallocated`). */
  readonly unallocatedFilter = signal<'all' | 'yes' | 'no'>('all');
  readonly dueWords: readonly DueWord[] = ['overdue', 'today', 'soon', 'later', 'none'];

  readonly filtered = computed(
    () =>
      !!this.query().trim() ||
      this.methodFilter() !== 'all' ||
      this.dueFilter() !== 'all' ||
      this.unallocatedFilter() !== 'all',
  );

  /** Set once the first state chip is chosen (the whole history inside an account), so the first request carries it. */
  private readonly ready = signal(false);

  /**
   * The receipts a page at a time from the server (owner, 2026-11-17: "paginate in backend, not
   * frontend"): ten rows to begin with, 20, 50 or 100 at the reader's choice. The state, the
   * customer, the branch, the finder and the filters run there over every receipt — no longer the
   * first thousand — in the server's order, oldest due first. Another filter, customer or branch
   * starts at page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.receiptsPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'receivables.receipts',
      filters: () => ({
        customerId: this.customerId() ?? this.selectedCustomer(),
        status: FILTER_STATUS[this.filter()],
        branchId: this.branchId(),
        q: this.query().trim(),
        method: this.methodFilter(),
        due: this.dueFilter(),
        unallocated: this.unallocatedFilter(),
      }),
      enabled: () => this.ready(),
    },
  );

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });
  /** An action's failure, else the list's. */
  readonly shownError = computed(() => this.error() ?? this.listError());

  /** The methods the receipts of this state were taken by, so a choice can always match. */
  readonly methodKeys = computed(() => this.pager.summary()?.methodKeys ?? []);
  readonly hasPending = computed(() => this.pager.summary()?.hasPending ?? false);

  clearFilters(): void {
    this.query.set('');
    this.methodFilter.set('all');
    this.dueFilter.set('all');
    this.unallocatedFilter.set('all');
  }

  /** Paper still in hand: it is money nobody can spend yet, which is why it leads. Over every receipt of the state asked for. */
  readonly pendingTotal = computed(() => this.pager.summary()?.pendingTotal ?? 0);

  readonly dueTotal = computed(() => this.pager.summary()?.dueTotal ?? 0);

  /** Confirmed money written against no invoice. It can never be applied later, so it is worth a tile. */
  readonly unallocatedTotal = computed(() => this.pager.summary()?.unallocatedTotal ?? 0);

  // ---------- SAL-13: keeping part of an advance on a cancelled order ----------

  private readonly auth = inject(AuthService);
  /** The receivables settings, read only by whoever may keep: they say whether keeping is allowed and to which account. */
  readonly keepSettings = signal<ReceivableSettings | null>(null);
  /** The receipt the keep dialog is open on. */
  readonly keeping = signal<ReceiptVoucher | null>(null);
  readonly mayKeep = computed(() => mayDo(this.auth.currentUser(), KEEP_ADVANCE_PERMISSION) && keepingAllowed(this.keepSettings()));

  /** Offered on money that has arrived and still holds something, while the setting allows it and the reader may. */
  canKeep(v: ReceiptVoucher): boolean {
    return this.mayKeep() && keepable(v) > 0;
  }

  /** "7585 — Autres produits", as the reader reads it. */
  keepAccount(): string | null {
    const s = this.keepSettings();
    const code = s?.keptIncomeAccount;
    if (!code) return null;
    const account = s?.incomeAccounts?.find((a) => a.code === code);
    return account ? `${code} — ${this.name(account)}` : code;
  }

  private async loadKeepSettings(): Promise<void> {
    if (!mayDo(this.auth.currentUser(), KEEP_ADVANCE_PERMISSION)) return;
    try {
      this.keepSettings.set(await this.api.settings());
    } catch {
      // Without the settings the action is simply not offered.
      this.keepSettings.set(null);
    }
  }

  async onKept(updated: ReceiptVoucher): Promise<void> {
    const amount = (updated.kept ?? 0) - (this.keeping()?.kept ?? 0);
    this.keeping.set(null);
    this.done.set(this.i18n.t('adv.keep.done', { amount: this.i18n.money(amount), n: updated.number }));
    await this.reload();
  }

  constructor() {
    void this.loadKeepSettings();
  }

  ngOnInit(): void {
    // Inside an account the reader wants the customer's whole history; the cross-customer list is
    // a worklist and opens on what still has to be chased. Every change of branch — chosen here or
    // in the header — is a filter of the pager, which asks again.
    if (this.customerId()) this.filter.set('all');
    this.ready.set(true);
  }

  // Each change below is a filter of the pager, which asks the server again from page one.

  setFilter(filter: Filter): void {
    this.filter.set(filter);
  }

  setQuery(value: string): void {
    this.query.set(value);
  }

  pickCustomer(id: string | null): void {
    this.selectedCustomer.set(id);
  }

  pickBranch(id: string | null): void {
    this.scope.set(id ? { kind: 'branch', id } : { kind: 'all' });
  }

  link(id: string): string[] {
    return [...(this.linkBase() ?? []), id];
  }

  due(v: ReceiptVoucher): DueWord {
    return dueWord(v.instrumentDueDate, this.regional.settings().timeZone);
  }

  dueTone(v: ReceiptVoucher) {
    return toneOf('due', this.due(v));
  }

  stateTone(status: ReceiptStatus) {
    return toneOf('payment', status);
  }

  /** A status the dictionary has no word for prints raw rather than as a missing key. */
  stateWord(status: string): string {
    const key = 'inv.rcpt.state.' + status;
    const word = this.i18n.t(key);
    return word === key ? status : word;
  }

  behaviorWord(behavior: string): string {
    const key = 'inv.behavior.' + behavior;
    const word = this.i18n.t(key);
    return word === key ? behavior : word;
  }

  methodName(key: string): string {
    const method = this.methods().find((m) => m.key === key);
    return method ? localizedName(method, this.i18n.lang()) : key;
  }

  branchName(id: string): string {
    const branch = this.branches().find((b) => b.id === id);
    return branch ? this.name(branch) : '—';
  }

  name(value: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(value, this.i18n.lang());
  }

  day(value: string | null): string {
    return formatDay(value ? withZ(value) : null, this.i18n.lang(), this.regional.settings().timeZone);
  }

  /** Asks again for the page on screen, after a receipt was collected or kept. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  /**
   * Confirming needs nothing but the fact, so it is done here; the confirmation says what it
   * settles because there is no un-settle. A bounce goes through the detail screen, where the
   * bank's reason can be written down.
   */
  async collect(v: ReceiptVoucher): Promise<void> {
    const message = this.i18n.t('inv.rcpt.confirmCollect', { n: v.number, v: this.i18n.money(v.amount) });
    if (!(await this.confirm.ask(collectRequest(this.i18n, v.number, message)))) return;
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    try {
      const settled = await this.api.settleReceipt(v.id, { status: 'Confirmed' });
      this.done.set(
        this.i18n.t('inv.rcpt.settle.done', { n: settled.number, state: this.stateWord(settled.status) }),
      );
    } catch (e) {
      // Already settled elsewhere is the usual refusal; the reload below shows the truth.
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
      await this.reload();
    }
  }
}
