import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminBranch, AdminPaymentMethod, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { BranchScopeService } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { DialogComponent } from '../../../ui/dialog.component';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest, collectRequest } from '../../../ui/confirm-requests';
import { MENU } from '../../../ui/menu.component';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatDay, localizedName } from '../backoffice-format';
import { DueWord, dueWord } from '../receivables/receivable-view';
import { ReceivablesApi } from '../receivables/receivables-api.service';
import { PAGE_CHROME } from '../shell/page-chrome';
import { AwaitingRow, AwaitingSource } from './awaiting';
import { FinancePagesApi } from './finance-pages.api';

const SOURCES = ['all', 'sale', 'receipt'] as const;
type SourceFilter = (typeof SOURCES)[number];
/** The due words the state column shows, in its order. */
const DUE_WORDS: readonly DueWord[] = ['overdue', 'today', 'soon', 'later', 'none'];

/**
 * Finance › Awaiting collection: every cheque and transfer still in hand, in one place (Phase 4) —
 * the till's and the receipts' (`awaiting.ts`). The question it answers is never "list the
 * cheques": it is how much is owed on paper and how much of it should already have arrived, so it
 * leads with those figures, every row says how far it is from its date in words, and the earliest
 * due sorts first.
 *
 * Each row is settled exactly as before, through its own source:
 * - **a till sale's** payment is collected or bounced from its menu, in one step, as the treasury
 *   list always did;
 * - **a receipt** is collected with the confirmation its own list asks, and bounced on the receipt,
 *   where the bank's reason is written down.
 *
 * The receipts still appear in Finance › Receipts too: that is where whoever holds the cash box —
 * who may not open this page — settles them.
 *
 * The branch filter is the working branch (D2). A till payment carries no branch of its own here,
 * so the Branch column names the receipts' only and says "—" for the till's; the server filters both.
 *
 * Read a page at a time (`GET /api/treasury/awaiting/page`, owner 2026-11-17): the server draws the two lists together,
 * runs every filter over all their rows and sends the figures over the filtered rows with the page.
 */
@Component({
  selector: 'app-awaiting-collection',
  standalone: true,
  imports: [
    FormsModule,
    RouterLink,
    ...PAGE_CHROME,
    ...MENU,
    DateInputComponent,
    DialogComponent,
    ListFooterComponent,
    StatusComponent,
    TableStateComponent,
  ],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <span class="filter-label" id="ac-source-label">{{ i18n.t('fin.source') }}</span>
          <div class="bo-chips" role="group" aria-labelledby="ac-source-label">
            @for (choice of SOURCES; track choice) {
              <button type="button" [class.is-on]="source() === choice" [attr.aria-pressed]="source() === choice" (click)="setSource(choice)">
                {{ i18n.t('fin.source.' + choice) }}
              </button>
            }
          </div>
        </div>
        @if (scope.canChoose()) {
          <div class="ui-filter">
            <label for="ac-branch">{{ i18n.t('branchLock.branch') }}</label>
            <select id="ac-branch" [ngModel]="branchId()" (ngModelChange)="pickBranch($event)">
              <option [ngValue]="null">{{ i18n.t('pv.list.allBranches') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ name(b) }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <label for="ac-f-due">{{ i18n.t('common.state') }}</label>
          <select id="ac-f-due" [ngModel]="dueFilter()" (ngModelChange)="dueFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            @for (word of DUE_WORDS; track word) {
              <option [ngValue]="word">{{ i18n.t('treasury.due.' + word) }}</option>
            }
          </select>
        </div>
        @if (methodKeys().length > 1 || methodFilter() !== 'all') {
          <div class="ui-filter">
            <label for="ac-f-method">{{ i18n.t('pay.method') }}</label>
            <select id="ac-f-method" [ngModel]="methodFilter()" (ngModelChange)="methodFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              @for (key of methodKeys(); track key) {
                <option [ngValue]="key">{{ methodName(key) }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <label for="ac-f-from">{{ i18n.t('treasury.filter.dueFrom') }}</label>
          <app-date-input id="ac-f-from" [ngModel]="dueFrom()" (ngModelChange)="dueFrom.set($event)" />
        </div>
        <div class="ui-filter">
          <label for="ac-f-to">{{ i18n.t('treasury.filter.dueTo') }}</label>
          <app-date-input id="ac-f-to" [ngModel]="dueTo()" (ngModelChange)="dueTo.set($event)" />
        </div>
        <div class="ui-filter ui-filter--search">
          <label for="ac-find">{{ i18n.t('inv.find') }}</label>
          <input id="ac-find" type="search" [placeholder]="i18n.t('fin.awaiting.findHint')" [ngModel]="query()" (ngModelChange)="setQuery($event)" />
        </div>
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="busy() || pager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (problem() && anyRows()) {
        <p class="notice danger" role="alert">{{ problem() }}</p>
      }
      @if (done()) {
        <p class="notice ok" role="status">{{ done() }}</p>
      }

      <div class="ui-metrics">
        <div class="ui-metric ui-metric--lead">
          <span class="ui-metric__label">{{ i18n.t('treasury.pendingTotal') }}</span>
          <strong class="ui-metric__value mono">{{ i18n.money(figures().total) }}</strong>
          <span class="ui-metric__note">{{ i18n.t('treasury.pendingCount') }} <span class="mono">{{ figures().count }}</span></span>
        </div>
        <div class="ui-metric" [class.ui-metric--warn]="figures().overdueTotal > 0">
          <span class="ui-metric__label">{{ i18n.t('treasury.overdueTotal') }}</span>
          <strong class="ui-metric__value mono">{{ i18n.money(figures().overdueTotal) }}</strong>
          <span class="ui-metric__note">{{ i18n.t('treasury.due.overdue') }} <span class="mono">{{ figures().overdueCount }}</span></span>
        </div>
      </div>

      <section class="ui-surface" [attr.aria-busy]="!pager.loaded()">
        <div class="ui-surface__head">
          <p>{{ i18n.t('fin.awaiting.note') }}</p>
        </div>
        @if (!pager.loaded() && !problem()) {
          <app-table-state kind="loading" />
        } @else if (problem() && !anyRows()) {
          <app-table-state kind="error" [message]="problem()" (retry)="reload()" />
        } @else if (!anyRows()) {
          <app-table-state kind="empty" [message]="i18n.t('treasury.nonePending') + ' ' + i18n.t('treasury.pendingHint')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('treasury.dueDate') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th>{{ i18n.t('treasury.instrument') }}</th>
                  <th>{{ i18n.t('common.customer') }}</th>
                  <th>{{ i18n.t('fin.source') }}</th>
                  <th>{{ i18n.t('fin.reference') }}</th>
                  @if (showBranch()) {
                    <th>{{ i18n.t('branchLock.branch') }}</th>
                  }
                  <th class="num">{{ i18n.t('pay.amount') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (row of pager.rows(); track row.source + row.id) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('treasury.dueDate')">{{ day(row.instrumentDueDate) }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="dueTone(row)">{{ i18n.t('treasury.due.' + due(row)) }}</app-status>
                    </td>
                    <td [attr.data-label]="i18n.t('treasury.instrument')">
                      <span class="mono">{{ row.instrumentNumber || methodName(row.methodKey) }}</span>
                      @if (row.instrumentBank) {
                        <span class="ui-sub">{{ row.instrumentBank }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('common.customer')">{{ row.customerName || '—' }}</td>
                    <td [attr.data-label]="i18n.t('fin.source')">{{ i18n.t('fin.source.' + row.source) }}</td>
                    <td [attr.data-label]="i18n.t('fin.reference')">
                      @if (row.source === 'receipt') {
                        <a class="ui-link mono" [routerLink]="['/backoffice', 'finance', 'receipts', row.id]">{{ row.reference }}</a>
                      } @else {
                        <span class="mono">{{ row.reference }}</span>
                      }
                    </td>
                    @if (showBranch()) {
                      <td [attr.data-label]="i18n.t('branchLock.branch')">{{ branchName(row.branchId) }}</td>
                    }
                    <td class="num mono figure" [attr.data-label]="i18n.t('pay.amount')">{{ i18n.money(row.amount) }}</td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + row.reference">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        @if (row.source === 'receipt') {
                          <a appMenuItem [routerLink]="['/backoffice', 'finance', 'receipts', row.id]">{{ i18n.t('common.view') }}</a>
                        }
                        <button type="button" appMenuItem [disabled]="busy()" (click)="collect(row)">{{ i18n.t('treasury.markCollected') }}</button>
                        <hr appMenuSeparator />
                        <button type="button" appMenuItem class="is-danger" [disabled]="busy()" (click)="bounce(row)">{{ i18n.t('treasury.markBounced') }}</button>
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

    <!-- FR-2B: a till cheque or transfer is bounced here, saying why, after an explicit confirmation.
         A receipt is bounced on the receipt, which asks the same. -->
    <app-dialog [open]="!!bouncing()" [heading]="bouncing() ? i18n.t('fin.bounce.title', { n: bouncing()!.reference }) : ''" (dismiss)="closeBounce()">
      @if (bouncing(); as row) {
        <div class="bounce">
          <p class="bounce__consequence">{{ i18n.t('fin.bounce.consequence', { v: i18n.money(row.amount) }) }}</p>
          <div class="bo-field" [class.is-invalid]="reasonMissing()">
            <label for="ac-bounce-reason">
              {{ i18n.t('fin.bounce.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
            </label>
            <textarea
              id="ac-bounce-reason"
              rows="3"
              maxlength="400"
              [ngModel]="reason()"
              (ngModelChange)="reason.set($event)"
              [attr.aria-invalid]="reasonMissing() ? 'true' : null"
              [attr.aria-describedby]="reasonMissing() ? 'ac-bounce-error' : 'ac-bounce-hint'"
            ></textarea>
            @if (reasonMissing()) {
              <span id="ac-bounce-error" class="bo-error" role="alert">{{ i18n.t('fin.bounce.reasonRequired') }}</span>
            } @else {
              <span id="ac-bounce-hint" class="bo-hint">{{ i18n.t('fin.bounce.reasonHint') }}</span>
            }
          </div>
        </div>
      }
      <div dialogFoot class="bounce__foot">
        <button type="button" class="btn-ghost" (click)="closeBounce()">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-danger-solid" [disabled]="busy()" (click)="confirmBounce()">{{ i18n.t('fin.bounce.confirm') }}</button>
      </div>
    </app-dialog>
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
      .bounce {
        display: grid;
        gap: var(--sp-4);
      }
      .bounce__consequence {
        margin: 0;
      }
      .bounce__foot {
        display: flex;
        justify-content: flex-end;
        gap: var(--sp-2);
      }
    `,
  ],
})
export class AwaitingCollectionComponent implements OnInit {
  readonly i18n = inject(I18nService);
  readonly scope = inject(BranchScopeService);
  private readonly admin = inject(AdminService);
  private readonly receivables = inject(ReceivablesApi);
  private readonly confirm = inject(ConfirmService);
  private readonly regional = inject(RegionalService);
  private readonly router = inject(Router);

  readonly SOURCES = SOURCES;
  readonly DUE_WORDS = DUE_WORDS;

  /** The till payment being bounced, while its dialog is open; the reason typed for it. */
  readonly bouncing = signal<AwaitingRow | null>(null);
  readonly reason = signal('');
  readonly reasonTried = signal(false);
  readonly reasonMissing = computed(() => this.reasonTried() && !this.reason().trim());

  private readonly pages = inject(FinancePagesApi);

  readonly branches = signal<AdminBranch[]>([]);
  readonly methods = signal<AdminPaymentMethod[]>([]);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  readonly source = signal<SourceFilter>('all');
  readonly query = signal('');
  // More filters (owner, 2026-11-17): applied by the server over every row.
  readonly dueFilter = signal<'all' | DueWord>('all');
  /** A payment method's key; `all` is any. */
  readonly methodFilter = signal<string>('all');
  /** `yyyy-MM-dd`, the company's days; empty is no bound. */
  readonly dueFrom = signal('');
  readonly dueTo = signal('');

  /** The working branch for the Admin (null: every branch); their own for a locked user. */
  readonly branchId = computed(() => this.scope.resolve('branchOrAll').branchId);
  readonly showBranch = computed(() => this.branchId() === null);

  readonly filtered = computed(
    () =>
      this.source() !== 'all' ||
      !!this.query().trim() ||
      this.dueFilter() !== 'all' ||
      this.methodFilter() !== 'all' ||
      !!this.dueFrom() ||
      !!this.dueTo(),
  );

  /**
   * The paper in hand a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"): the
   * till's and the receipts' drawn together there, earliest due first, every filter run over all of them. Ten rows to
   * begin with, 20, 50 or 100 at the user's choice; a new filter or branch starts at page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.awaitingPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'finance.awaiting',
      filters: () => ({
        source: this.source(),
        q: this.query().trim(),
        due: this.dueFilter(),
        method: this.methodFilter(),
        dueFrom: this.dueFrom(),
        dueTo: this.dueTo(),
        branchId: this.branchId(),
      }),
    },
  );

  /** Whether any paper is in hand at all, before the filters. */
  readonly anyRows = computed(() => (this.pager.summary()?.all ?? 0) > 0);
  /** The methods the rows were taken by, so the filter offers only what is there. */
  readonly methodKeys = computed(() => this.pager.summary()?.methodKeys ?? []);
  /** On what is shown: a source chip, a search or a filter narrows the figures with the rows (the server's sums). */
  readonly figures = computed(() => {
    const s = this.pager.summary();
    return { total: s?.total ?? 0, count: s?.count ?? 0, overdueTotal: s?.overdueTotal ?? 0, overdueCount: s?.overdueCount ?? 0 };
  });
  /** What went wrong, in words: an act, or reading the list. */
  readonly problem = computed(() => {
    const e = this.pager.error();
    return this.error() ?? (e ? adminErrorMessage(e) : null);
  });

  async ngOnInit(): Promise<void> {
    try {
      const [branches, methods] = await Promise.all([this.admin.listBranches(), this.admin.listPaymentMethods()]);
      this.branches.set(branches.filter((b) => b.isActive));
      this.methods.set(methods);
    } catch {
      // Names only: the rows still say their method key and the filter still offers every branch.
    }
  }

  /** A new source, search or branch starts at page one: the pager sees the filter change. */
  setSource(source: SourceFilter): void {
    this.source.set(source);
  }

  setQuery(value: string): void {
    this.query.set(value);
  }

  clearFilters(): void {
    this.source.set('all');
    this.setQuery('');
    this.dueFilter.set('all');
    this.methodFilter.set('all');
    this.dueFrom.set('');
    this.dueTo.set('');
  }

  pickBranch(id: string | null): void {
    this.scope.set(id ? { kind: 'branch', id } : { kind: 'all' });
  }

  due(row: AwaitingRow) {
    return dueWord(row.instrumentDueDate, this.regional.settings().timeZone);
  }

  dueTone(row: AwaitingRow) {
    return toneOf('due', this.due(row));
  }

  day(value: string | null): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  name(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  branchName(id: string | null): string {
    const branch = id ? this.branches().find((b) => b.id === id) : undefined;
    return branch ? this.name(branch) : '—';
  }

  methodName(key: string): string {
    const method = this.methods().find((m) => m.key === key);
    return method ? localizedName(method, this.i18n.lang()) : key;
  }

  /** Asks again for the page on screen, its figures with it. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  /**
   * Collected, after a confirmation (FR-2B, for consistency with the receipts, which always asked
   * it): a receipt with its own sentence, which says what it settles; a till payment with the
   * same question. Nothing else about collecting changed.
   */
  async collect(row: AwaitingRow): Promise<void> {
    const v = this.i18n.money(row.amount);
    const ask = (key: string) =>
      this.confirm.ask(collectRequest(this.i18n, row.reference, this.i18n.t(key, { n: row.reference, v })));
    if (row.source === 'receipt') {
      if (!(await ask('inv.rcpt.confirmCollect'))) return;
      await this.settle(row, () => this.receivables.settleReceipt(row.id, { status: 'Confirmed' }));
    } else {
      if (!(await ask('fin.collect.confirm'))) return;
      await this.settle(row, () => this.admin.settlePayment(row.id, 'Confirmed', null));
    }
  }

  /**
   * Bounced, saying why (FR-2B): a till payment in a dialog that asks the bank's reason and an
   * explicit confirmation, the reason going into the note the route always stored; a receipt on
   * the receipt, whose form asks the same.
   */
  bounce(row: AwaitingRow): void {
    if (row.source === 'receipt') {
      void this.router.navigate(['/backoffice', 'finance', 'receipts', row.id], { queryParams: { act: 'bounce' } });
      return;
    }
    this.reason.set('');
    this.reasonTried.set(false);
    this.bouncing.set(row);
  }

  closeBounce(): void {
    this.bouncing.set(null);
  }

  async confirmBounce(): Promise<void> {
    const row = this.bouncing();
    const reason = this.reason().trim();
    this.reasonTried.set(true);
    if (!row || !reason) return;
    
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('fin.bounce.confirm'))))) return;

    this.bouncing.set(null);
    await this.settle(row, () => this.admin.settlePayment(row.id, 'Bounced', reason));
  }

  private async settle(row: AwaitingRow, act: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    try {
      await act();
      this.done.set(this.i18n.t('fin.awaiting.settled', { n: row.reference }));
    } catch (e) {
      // Settled elsewhere already is the usual refusal; the reload below shows the truth.
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
      await this.reload();
    }
  }
}

export type { AwaitingSource };
