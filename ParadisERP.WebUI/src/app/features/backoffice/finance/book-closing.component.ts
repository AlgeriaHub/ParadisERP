import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { BookClosingRow, LedgerService } from '../../../core/ledger.service';
import { RegionalService } from '../../../core/regional.service';
import { localDay } from '../../../core/rentals';
import { ConfirmService } from '../../../ui/confirm.service';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { MonthInputComponent } from '../payroll/month-input.component';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { FinancePagesApi } from './finance-pages.api';

/**
 * Finance › Closing the books (CLO-01; owner, 2026-10-31 and 2026-11-01). The Admin closes the books through the end
 * of a month that has ended, forward only; nothing can then be dated on or before that day. The way back is a reopen —
 * to an earlier month end, or every month — always with a reason. Every close and reopen is listed with who and when.
 *
 * <p>Months are chosen by name (`app-month-input`, `YYYY-MM`), never a native month box, and the month that has ended
 * is counted on the company's clock. The server checks every rule again; this page only offers what it would accept.</p>
 *
 * <p>The trail is read a page at a time (`GET /api/ledger/closing/page`, owner 2026-11-17); its filters run on the
 * server, and the state above it (closed through, the last change) comes with every page.</p>
 */
@Component({
  selector: 'app-book-closing',
  standalone: true,
  imports: [
    FormsModule,
    ...PAGE_CHROME,
    DateInputComponent,
    ListFooterComponent,
    MonthInputComponent,
    StatusComponent,
    TableStateComponent,
  ],
  template: `
    <div class="bo-stack">
      @if (problem()) {
        <p class="notice danger" role="alert">{{ problem() }}</p>
      }
      @if (notice()) {
        <p class="notice ok" role="status">{{ notice() }}</p>
      }

      <div class="ui-metrics">
        <div class="ui-metric ui-metric--lead">
          <span class="ui-metric__label">{{ i18n.t('closing.state') }}</span>
          <strong class="ui-metric__value mono">{{ closedThrough() ? day(closedThrough()!) : i18n.t('closing.none') }}</strong>
        </div>
        @if (last(); as l) {
          <div class="ui-metric">
            <span class="ui-metric__label">{{ i18n.t('closing.lastChange') }}</span>
            <strong class="ui-metric__value ui-metric__value--text">{{ i18n.t('closing.action.' + action(l)) }}</strong>
            <span class="ui-metric__note">{{ i18n.t('closing.lastChangeBy', { when: i18n.dateTime(l.atUtc), who: l.userName || '—' }) }}</span>
          </div>
        }
      </div>

      <p class="notice info">{{ i18n.t('closing.tillNote') }}</p>

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('closing.close.title') }}</h2>
        </div>
        <div class="bo-panel__body">
          <div class="bo-fields bo-fields--roomy">
            <div class="bo-field">
              <label for="cl-month">
                {{ i18n.t('closing.close.month') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
              </label>
              <app-month-input id="cl-month" [(ngModel)]="closeMonth" />
            </div>
            <div class="bo-field">
              <label for="cl-note">
                {{ i18n.t('closing.close.note') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
              </label>
              <input id="cl-note" maxlength="400" [(ngModel)]="note" />
            </div>
          </div>
        </div>
        <div class="bo-panel__foot">
          <button type="button" class="btn-primary" [disabled]="busy() || !closeMonth" (click)="close()">
            {{ i18n.t('closing.close.button') }}
          </button>
        </div>
      </section>

      @if (closedThrough()) {
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('closing.reopen.title') }}</h2>
          </div>
          <div class="bo-panel__body">
            <div class="bo-fields bo-fields--roomy">
              <div class="bo-field">
                <div class="bo-check">
                  <input id="cl-all" type="checkbox" [(ngModel)]="reopenAll" />
                  <label for="cl-all">{{ i18n.t('closing.reopen.all') }}</label>
                </div>
              </div>
              @if (!reopenAll) {
                <div class="bo-field">
                  <label for="cl-reopen-month">{{ i18n.t('closing.reopen.month') }}</label>
                  <app-month-input id="cl-reopen-month" [(ngModel)]="reopenMonth" />
                </div>
              }
              <div class="bo-field bo-field--wide">
                <label for="cl-reason">
                  {{ i18n.t('closing.reopen.reason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input id="cl-reason" maxlength="400" [(ngModel)]="reason" />
              </div>
            </div>
          </div>
          <div class="bo-panel__foot">
            <button
              type="button"
              class="btn-secondary"
              [disabled]="busy() || !reason.trim() || (!reopenAll && !reopenMonth)"
              (click)="reopen()"
            >
              {{ i18n.t('closing.reopen.button') }}
            </button>
          </div>
        </section>
      }

      <section class="ui-surface" [attr.aria-busy]="busy() || pager.loading()">
        <h2 class="ui-surface__title">{{ i18n.t('closing.history') }}</h2>
        @if (!pager.loaded()) {
          <app-table-state kind="loading" />
        } @else if (!anyRows()) {
          <app-table-state kind="empty" [message]="i18n.t('closing.empty')" />
        } @else {
          <!-- The trail's own filters (owner, 2026-11-17): they narrow the rows below, never the closing above. -->
          <div class="ui-surface__body">
            <div class="ui-toolbar" role="search">
              <div class="ui-filter">
                <label for="cl-f-action">{{ i18n.t('closing.col.action') }}</label>
                <select id="cl-f-action" [ngModel]="actionFilter()" (ngModelChange)="actionFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="closed">{{ i18n.t('closing.action.closed') }}</option>
                  <option ngValue="reopened">{{ i18n.t('closing.action.reopened') }}</option>
                </select>
              </div>
              <div class="ui-filter">
                <label for="cl-f-from">{{ i18n.t('filters.from') }}</label>
                <app-date-input id="cl-f-from" [ngModel]="fromFilter()" (ngModelChange)="fromFilter.set($event)" />
              </div>
              <div class="ui-filter">
                <label for="cl-f-to">{{ i18n.t('filters.to') }}</label>
                <app-date-input id="cl-f-to" [ngModel]="toFilter()" (ngModelChange)="toFilter.set($event)" />
              </div>
              @if (filtered()) {
                <div class="ui-toolbar__end">
                  <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                </div>
              }
            </div>
          </div>
          @if (!pager.total()) {
            <app-table-state kind="noResults" (clear)="clearFilters()" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('closing.col.when') }}</th>
                    <th>{{ i18n.t('closing.col.who') }}</th>
                    <th>{{ i18n.t('closing.col.action') }}</th>
                    <th>{{ i18n.t('closing.col.through') }}</th>
                    <th>{{ i18n.t('closing.col.reason') }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of pager.rows(); track row.id) {
                    <tr>
                      <td class="mono" [attr.data-label]="i18n.t('closing.col.when')">{{ i18n.dateTime(row.atUtc) }}</td>
                      <td [attr.data-label]="i18n.t('closing.col.who')">{{ row.userName || '—' }}</td>
                      <td [attr.data-label]="i18n.t('closing.col.action')">
                        <app-status [tone]="tone(row)">{{ i18n.t('closing.action.' + action(row)) }}</app-status>
                      </td>
                      <td class="mono" [attr.data-label]="i18n.t('closing.col.through')">
                        {{ row.closedThrough ? day(row.closedThrough) : i18n.t('closing.none') }}
                      </td>
                      <td [attr.data-label]="i18n.t('closing.col.reason')">{{ row.reason || '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
      </section>
      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .ui-surface__title {
        margin: 0;
        padding: var(--sp-3) var(--sp-4);
        font-size: var(--fs-section);
      }
      .ui-metric__value--text {
        font-size: var(--fs-lg);
      }
      /* The closed day is one figure: it never breaks, and on a phone its tile takes the row. */
      .ui-metric--lead .ui-metric__value {
        white-space: nowrap;
      }
      @media (max-width: 640px) {
        .ui-metric--lead {
          grid-column: 1 / -1;
        }
      }
    `,
  ],
})
export class BookClosingComponent implements OnInit {
  private readonly ledger = inject(LedgerService);
  private readonly confirm = inject(ConfirmService);
  private readonly regional = inject(RegionalService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  private readonly pages = inject(FinancePagesApi);

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  // The trail's filters (owner, 2026-11-17): applied by the server; they narrow the rows, never the closing above.
  readonly actionFilter = signal<'all' | 'closed' | 'reopened'>('all');
  /** `yyyy-MM-dd`, the company's days; empty is no bound. */
  readonly fromFilter = signal('');
  readonly toFilter = signal('');

  readonly filtered = computed(() => this.actionFilter() !== 'all' || !!this.fromFilter() || !!this.toFilter());

  /** The trail a page at a time from the server; ten rows to begin with, a new filter starts at page one. */
  readonly pager = createServerPager(
    (page) => this.pages.bookClosingPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'finance.bookClosing',
      filters: () => ({ action: this.actionFilter(), from: this.fromFilter(), to: this.toFilter() }),
    },
  );

  /** The books' state, as the server answered with the page: whatever the filters. */
  readonly closedThrough = computed(() => this.pager.summary()?.closedThrough ?? null);
  readonly last = computed(() => this.pager.summary()?.last ?? null);
  /** Whether there has ever been a close or a reopen, before the filters. */
  readonly anyRows = computed(() => (this.pager.summary()?.all ?? 0) > 0);
  /** What went wrong, in words: an act, or reading the trail. */
  readonly problem = computed(() => {
    const e = this.pager.error();
    return this.error() ?? (e ? adminErrorMessage(e) : null);
  });

  constructor() {
    // The month the form offers follows what is closed: on the first answer, and after every close and reopen.
    effect(() => {
      if (!this.pager.loaded()) return;
      this.closedThrough();
      untracked(() => (this.closeMonth = this.nextToClose()));
    });
  }

  clearFilters(): void {
    this.actionFilter.set('all');
    this.fromFilter.set('');
    this.toFilter.set('');
  }

  /** `YYYY-MM`: the month to close through. */
  closeMonth = '';
  note = '';
  reopenAll = false;
  /** `YYYY-MM`: the month the books go back to being closed through. */
  reopenMonth = '';
  reason = '';

  ngOnInit(): void {
    // The trail and the state above it load through the pager, from its first page.
    this.chrome?.description.set(this.i18n.t('closing.lead'));
  }

  /** Asks again for the page on screen and the books' state with it. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  /**
   * The month the form offers: the one after what is closed, as long as it has ended on the company's clock; else the
   * month that ended last. Never the browser's month (CLAUDE.md, dates).
   */
  nextToClose(): string {
    const today = localDay(new Date().toISOString(), this.regional.settings().timeZone);
    const lastEnded = previousMonth(today.slice(0, 7));
    const closed = this.closedThrough();
    if (!closed) return lastEnded;
    const after = nextMonth(closed.slice(0, 7));
    return after <= lastEnded ? after : lastEnded;
  }

  async close(): Promise<void> {
    const through = monthEnd(this.closeMonth);
    const ok = await this.confirm.ask({
      title: this.i18n.t('closing.close.title'),
      message: this.i18n.t('closing.close.confirm', { date: this.day(through) }),
      confirmLabel: this.i18n.t('closing.close.button'),
    });
    if (!ok) return;
    await this.run(async () => {
      await this.ledger.closeBooks(through, this.note.trim() || null);
      this.note = '';
      this.notice.set(this.i18n.t('closing.close.done', { date: this.day(through) }));
    });
  }

  async reopen(): Promise<void> {
    const through = this.reopenAll ? null : monthEnd(this.reopenMonth);
    const what = through
      ? this.i18n.t('closing.reopen.toDate', { date: this.day(through) })
      : this.i18n.t('closing.reopen.toNothing');
    const ok = await this.confirm.ask({
      title: this.i18n.t('closing.reopen.title'),
      message: this.i18n.t('closing.reopen.confirm', { what }),
      confirmLabel: this.i18n.t('closing.reopen.button'),
      danger: true,
    });
    if (!ok) return;
    await this.run(async () => {
      await this.ledger.reopenBooks(through, this.reason.trim());
      this.reason = '';
      this.reopenAll = false;
      this.reopenMonth = '';
      this.notice.set(this.i18n.t('closing.reopen.done'));
    });
  }

  action(row: BookClosingRow): 'closed' | 'reopened' {
    return row.isReopen ? 'reopened' : 'closed';
  }

  tone(row: BookClosingRow) {
    return toneOf('bookClosing', this.action(row));
  }

  /** A `yyyy-MM-dd` day as the app writes days: dd/MM/yyyy, Latin digits, whatever the language. */
  day(iso: string): string {
    const [y, m, d] = iso.split('-');
    return `${d}/${m}/${y}`;
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      await action();
      await this.pager.reload();
      this.closeMonth = this.nextToClose();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}

/** The last day of a `YYYY-MM` month, as `yyyy-MM-dd`. */
export function monthEnd(month: string): string {
  const [y, m] = month.split('-').map(Number);
  const last = new Date(Date.UTC(y, m, 0)).getUTCDate();
  return `${month}-${String(last).padStart(2, '0')}`;
}

function previousMonth(month: string): string {
  const [y, m] = month.split('-').map(Number);
  return m === 1 ? `${y - 1}-12` : `${y}-${String(m - 1).padStart(2, '0')}`;
}

function nextMonth(month: string): string {
  const [y, m] = month.split('-').map(Number);
  return m === 12 ? `${y + 1}-01` : `${y}-${String(m + 1).padStart(2, '0')}`;
}
