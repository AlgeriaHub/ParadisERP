import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConnectivityService } from '../../core/connectivity.service';
import { getMeta } from '../../core/db';
import { I18nService } from '../../core/i18n.service';
import { LIST_FILTERS_TRANSLATIONS } from '../../core/i18n/translations-list-filters';
import { DeviceMeta, Sale } from '../../core/models';
import { PosService } from '../../core/pos.service';
import { SyncService } from '../../core/sync.service';
import { ConfirmService } from '../../ui/confirm.service';
import { actionRequest } from '../../ui/confirm-requests';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createPager } from '../../ui/pager';
import { TableStateComponent } from '../../ui/table-state.component';

@Component({
  selector: 'app-sync-status',
  standalone: true,
  imports: [FormsModule, ListFooterComponent, TableStateComponent],
  template: `
    <div class="wrap">
      <header class="head">
        <h1>{{ i18n.t('sync.title') }}</h1>
        <div class="head-actions">
          <button type="button" class="btn-primary" [disabled]="sync.syncing()" (click)="run()">
            {{ sync.syncing() ? i18n.t('status.syncing') : i18n.t('sync.now') }}
          </button>
          <button type="button" class="btn-ghost" [disabled]="sync.syncing()" (click)="full()">
            {{ i18n.t('sync.fullResync') }}
          </button>
        </div>
      </header>

      @if (!connectivity.online()) {
        <p class="notice warn">{{ i18n.t('sync.offlineNotice') }}</p>
      }

      <div class="tiles">
        <div class="card tile">
          <span class="muted">{{ i18n.t('sync.pending') }}</span>
          <strong [class.warn-text]="sync.stats().pending > 0">{{ sync.stats().pending }}</strong>
        </div>
        <div class="card tile">
          <span class="muted">{{ i18n.t('sync.rejected') }}</span>
          <strong [class.danger-text]="sync.stats().rejected > 0">{{ sync.stats().rejected }}</strong>
        </div>
        <!-- A count of sales already on the server, named like its two neighbours. It was
             labelled "Everything synced", which read as a false all-clear beside a sale still
             waiting. -->
        <div class="card tile">
          <span class="muted">{{ i18n.t('sync.synced') }}</span>
          <strong>{{ sync.stats().synced }}</strong>
        </div>
        <div class="card tile">
          <span class="muted">{{ i18n.t('sync.catalog') }}</span>
          <strong>{{ sync.stats().products }}</strong>
          <small class="muted">{{ i18n.t('sync.products') }}</small>
        </div>
      </div>

      <div class="card details">
        <div class="row">
          <span class="muted">{{ i18n.t('sync.device') }}</span>
          <b class="mono">{{ meta()?.deviceCode }}</b>
        </div>
        <div class="row">
          <span class="muted">{{ i18n.t('sync.server') }}</span>
          @if (meta()?.apiBaseUrl; as url) {
            <b class="mono">{{ url }}</b>
          } @else {
            <b>{{ i18n.t('sync.sameOrigin') }}</b>
          }
        </div>
        <div class="row">
          <span class="muted">{{ i18n.t('sync.cursor') }}</span>
          <b class="mono">{{ sync.stats().cursor }}</b>
        </div>
        <div class="row">
          <span class="muted">{{ i18n.t('status.lastSync', { v: '' }) }}</span>
          <b class="mono">
            {{ sync.stats().lastSyncAtUtc ? i18n.dateTime(sync.stats().lastSyncAtUtc!) : i18n.t('status.never') }}
          </b>
        </div>
        <div class="row">
          <span class="muted">{{ i18n.t('sync.customers') }}</span>
          <b class="mono">{{ sync.stats().customers }}</b>
        </div>
      </div>

      @if (sync.stats().lastError) {
        <div class="notice danger">
          <strong>{{ i18n.t('sync.errorTitle') }}</strong>
          <div>{{ sync.stats().lastError }}</div>
        </div>
      }

      <p class="muted hint">{{ i18n.t('sync.fullResyncHint') }}</p>

      @if (problems().length) {
        <!-- One filter (owner, 2026-11-17), shown when the list holds both: a rejected sale waits
             for a person, one that failed to send will be sent again by itself. -->
        @if (problemStates().length > 1 || stateFilter() !== 'all') {
          <div class="ui-toolbar" role="search">
            <div class="ui-filter">
              <label for="sy-f-state">{{ i18n.t('filters.status') }}</label>
              <select id="sy-f-state" [ngModel]="stateFilter()" (ngModelChange)="stateFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="rejected">{{ i18n.t('sync.rejected') }}</option>
                <option ngValue="retrying">{{ i18n.t('sync.pending') }}</option>
              </select>
            </div>
            @if (stateFilter() !== 'all') {
              <div class="ui-toolbar__end">
                <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
              </div>
            }
          </div>
        }
        <div class="card table-wrap">
          <table>
            <thead>
              <tr>
                <th>{{ i18n.t('receipt.no') }}</th>
                <th>{{ i18n.t('receipt.date') }}</th>
                <th class="num">{{ i18n.t('register.total') }}</th>
                <th>{{ i18n.t('sync.errorTitle') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (s of pager.rows(); track s.id) {
                <tr>
                  <td class="mono">{{ s.reference }}</td>
                  <td>{{ i18n.dateTime(s.soldAtUtc) }}</td>
                  <td class="num mono">{{ i18n.money(s.grandTotalTtc) }}</td>
                  <td class="err">{{ rejectionText(s) }}</td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="4">
                    <app-table-state kind="noResults" (clear)="clearFilters()" />
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <app-list-footer [pager]="pager" />
      }
    </div>
  `,
  styles: [
    `
      .wrap {
        display: grid;
        gap: var(--sp-4);
        width: 100%;
        min-width: 0;
      }

      .head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-4);
        flex-wrap: wrap;

        h1 {
          margin: 0;
          font-family: var(--font-display);
          font-size: var(--fs-lg);
          font-weight: 700;
        }
      }

      .head-actions {
        display: flex;
        gap: var(--sp-2);
      }

      .tiles {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
        gap: var(--sp-4);
      }

      .tile {
        padding: var(--sp-4) var(--sp-5);
        display: grid;
        gap: var(--sp-1);
        border: var(--line) solid var(--border-strong);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow);
        background: var(--surface);

        span {
          font-size: var(--fs-meta);
          font-weight: 600;
          color: var(--text-2);
        }

        strong {
          font-family: var(--font-display);
          font-size: 34px;
          font-weight: 600;
          letter-spacing: -0.03em;
          line-height: var(--lh-tight);
          font-variant-numeric: tabular-nums;
          color: var(--text);
        }

        small {
          font-size: var(--fs-meta);
          color: var(--muted);
        }
      }

      .warn-text {
        color: var(--warn);
      }

      .danger-text {
        color: var(--danger);
      }

      .details {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        column-gap: var(--sp-7);
        padding: var(--sp-4) var(--sp-6);
        border: var(--line) solid var(--border-strong);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow);
        background: var(--surface);
      }

      .row {
        display: flex;
        justify-content: space-between;
        gap: var(--sp-4);
        padding: var(--sp-2) 0;
        border-block-end: 1px solid var(--border);
        font-size: var(--fs-sm);

        &:last-child {
          border-block-end: none;
        }
      }

      .hint {
        margin: 0;
        font-size: var(--fs-sm);
        color: var(--muted);
      }

      .table-wrap {
        overflow-x: auto;
        border: var(--line) solid var(--border-strong);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow);
        background: var(--surface);
      }

      /* The back office's filter row (ui/ui-patterns.scss), drawn here: the till does not load that sheet. */
      .ui-toolbar {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        gap: var(--sp-3);
      }

      .ui-filter {
        display: grid;
        gap: var(--sp-1);
        min-inline-size: 9.5rem;

        label {
          margin: 0;
          font-size: var(--fs-meta);
          font-weight: 600;
          color: var(--text-2);
        }

        select {
          min-block-size: var(--control-h-sm);
          font-size: var(--fs-sm);
        }
      }

      .ui-toolbar__end {
        display: flex;
        gap: var(--sp-2);
        margin-inline-start: auto;
      }

      .err {
        font-size: var(--fs-meta);
        color: var(--danger);
        font-weight: 600;
      }

      @media (max-width: 640px) {
        .details {
          grid-template-columns: minmax(0, 1fr);
        }
      }
    `,
  ],
})
export class SyncStatusComponent implements OnInit {
  readonly sync = inject(SyncService);
  readonly i18n = inject(I18nService);
  readonly connectivity = inject(ConnectivityService);
  private readonly pos = inject(PosService);
  private readonly confirm = inject(ConfirmService);

  readonly meta = signal<DeviceMeta | null>(null);
  readonly problems = signal<Sale[]>([]);

  /** Rejected: the server refused it and a person must act. Retrying: it failed to send and goes again. */
  readonly stateFilter = signal<'all' | 'rejected' | 'retrying'>('all');
  readonly problemStates = computed(() => [...new Set(this.problems().map((s) => this.problemState(s)))]);
  readonly shownProblems = computed(() => {
    const state = this.stateFilter();
    return state === 'all' ? this.problems() : this.problems().filter((s) => this.problemState(s) === state);
  });
  /** Ten to begin with; another filter starts at page one. */
  readonly pager = createPager(() => this.shownProblems(), {
    key: 'sync.problems',
    resetOn: () => [this.stateFilter()],
  });

  constructor() {
    // The filter words live with the back office's; a till that never opened it still needs them.
    this.i18n.extend(LIST_FILTERS_TRANSLATIONS);
  }

  clearFilters(): void {
    this.stateFilter.set('all');
  }

  private problemState(s: Sale): 'rejected' | 'retrying' {
    return s.outboxState === 'rejected' ? 'rejected' : 'retrying';
  }

  async ngOnInit(): Promise<void> {
    await this.refresh();
  }

  async run(): Promise<void> {
    await this.sync.sync('sync-page');
    await this.refresh();
  }

  async full(): Promise<void> {
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('sync.fullResync'))))) return;
    await this.sync.fullResync();
    await this.pos.loadCatalog();
    await this.refresh();
  }

  /**
   * The server's rejection in the reader's language when it sent a code, the server's own
   * sentence otherwise — a till may meet an older server during a rolling upgrade, and a
   * code this bundle does not know yet must still say something.
   */
  rejectionText(s: Sale): string {
    const code = s.lastErrorCode;
    if (code) {
      const key = `sync.rejected.${code}`;
      // A server_error sentence carries the reference an operator greps the log for; it is
      // the one thing in the message worth keeping across the translation.
      const ref = /Reference (\S+)\./.exec(s.lastError ?? '')?.[1] ?? '';
      const text = this.i18n.t(key, { ref });
      if (text !== key) return text;
    }
    return s.lastError ?? '';
  }

  private async refresh(): Promise<void> {
    this.meta.set(await getMeta());
    await this.sync.refreshStats();
    const all = await this.pos.recentSales(500);
    this.problems.set(all.filter((s) => s.outboxState === 'rejected' || (s.lastError && s.outboxState !== 'synced')));
  }
}
