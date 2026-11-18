import { NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminBranch, AdminCashBox, AdminService, SalesSummary, adminErrorMessage } from '../../../core/admin.service';
import { BranchFace } from '../../../core/branch-face';
import { BranchLock } from '../../../core/branch-lock';
import { ScopeLabel } from '../../../core/branch-scope.service';
import { ConnectivityService } from '../../../core/connectivity.service';
import { I18nService } from '../../../core/i18n.service';
import { SyncService } from '../../../core/sync.service';
import { localizedName } from '../backoffice-format';
import { PaymentVouchersApi } from '../receivables/payment-vouchers-api.service';
import { ReceivablesApi } from '../receivables/receivables-api.service';
import { RentalTotals, rentalTotals } from '../rentals/rental-totals';
import { RentalsApi } from '../rentals/rentals-api.service';
import { PageActionsDirective, PageToolbarDirective } from '../shell/page-chrome';
import {
  CardTone,
  CountAmount,
  VOUCHER_PENDING,
  awaitingCollection,
  cardTone,
  cashBoxesFor,
  customersOwe,
  lowStockCount,
  oweTone,
  shareOf,
  tillTone,
  todayWindow,
  vouchersToApprove,
  waitingTone,
} from './dashboard-figures';
import { DashboardScope, dashboardScope } from './dashboard-scope';

/** One block's life: each loads on its own, so one failing endpoint never blanks the page. */
export type BlockState<T> =
  | { status: 'loading' }
  | { status: 'ready'; data: T }
  | { status: 'error'; message: string }
  | { status: 'offline' };

export class Block<T> {
  readonly state = signal<BlockState<T>>({ status: 'loading' });
  readonly data = computed(() => {
    const s = this.state();
    return s.status === 'ready' ? s.data : null;
  });
  private ticket = 0;

  /** A later load wins: a reply to an earlier branch never overwrites the one asked for since. */
  async load(online: boolean, fetch: () => Promise<T>): Promise<void> {
    const mine = ++this.ticket;
    if (!online) {
      this.state.set({ status: 'offline' });
      return;
    }
    this.state.set({ status: 'loading' });
    try {
      const data = await fetch();
      if (mine === this.ticket) this.state.set({ status: 'ready', data });
    } catch (e) {
      if (mine === this.ticket) this.state.set({ status: 'error', message: adminErrorMessage(e) });
    }
  }
}

/** The cards that take a tone (PinSay #300). */
type CardName = 'sales' | 'rentals' | 'owe' | 'vouchers' | 'awaiting' | 'cash' | 'low' | 'till';

/** The server caps these lists; at the cap there may be more than the block counted. */
const CONTRACT_CAP = 500;
const PENDING_CAP = 500;
const VOUCHER_TAKE = 1000;

interface RentalsBlock {
  totals: RentalTotals;
  capped: boolean;
}

/**
 * The dashboard (REDESIGN_PLAN.md Appendix A): where a manager or an admin lands. Built only on
 * endpoints that exist today, each block a count or a sum of what its own worklist shows, and each
 * heading the link to that worklist. No charts, no trend, no figure the server does not have.
 *
 * - **Whose figures.** A locked user's are their branch's, as the server answers whatever is asked.
 *   The Admin's follow the dashboard's own branch choice (`DashboardScope`), not the working branch:
 *   every branch by default when the company has several, kept once chosen, and never carried into
 *   another page. Every block names what it shows; customers owe is company-wide and says so.
 * - **Each block on its own.** Loading, error and offline are per block. Offline, every server block
 *   says it needs the connection, and *This till* — the device's own outbox — still answers.
 */
@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [NgTemplateOutlet, FormsModule, RouterLink, PageActionsDirective, PageToolbarDirective],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-ghost" [disabled]="!connectivity.online()" (click)="reload()">
        {{ i18n.t('dash.refresh') }}
      </button>
      <a routerLink="/register" class="btn-primary">
        {{ i18n.t('nav.openTill') }}
      </a>
    </ng-template>

    @if (showPicker()) {
      <ng-template appPageToolbar>
        <div class="ui-toolbar">
          <div class="ui-filter">
            <label for="db-branch">{{ i18n.t('branchLock.branch') }}</label>
            <select id="db-branch" [ngModel]="branchChoice()" (ngModelChange)="chooseBranch($event)">
              <option [ngValue]="ALL">{{ i18n.t('scope.all') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ name(b) }}</option>
              }
            </select>
          </div>
        </div>
      </ng-template>
    }

    <div class="dash-page">
      <!-- Row 1: Key Figures (4 metric cards) -->
      <div class="dash-row dash-row-1">
        <!-- Today's sales (KEY tile: var(--accent) fill, var(--on-accent) text) -->
        <section class="card metric metric--key" [attr.data-tone]="tones().sales" aria-labelledby="db-sales">
          <header class="metric-top">
            <h2 id="db-sales"><a [routerLink]="'/backoffice/sales/overview'">{{ i18n.t('dash.sales.title') }}</a></h2>
            <span class="metric-ic" aria-hidden="true">
              <svg class="ic" viewBox="0 0 24 24"><path d="M6 2 3 6v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V6l-3-4Z"/><path d="M3 6h18"/><path d="M16 10a4 4 0 0 1-8 0"/></svg>
            </span>
          </header>
          <ng-container *ngTemplateOutlet="status; context: { $implicit: sales.state() }" />
          @if (sales.data(); as s) {
            <p class="figure mono">{{ i18n.money(s.grossTtc) }}</p>
            <div class="metric-note">
              <span>{{ i18n.t('reports.saleCount') }}: <strong class="mono">{{ s.saleCount }}</strong></span>
              <span> · {{ i18n.t('reports.refundTotal') }}: <strong class="mono">{{ i18n.money(s.refundTotal) }}</strong> ({{ s.refundCount }})</span>
            </div>
          }
        </section>

        <!-- Customers owe -->
        <section class="card metric" [attr.data-tone]="tones().owe" aria-labelledby="db-owe">
          <header class="metric-top">
            <h2 id="db-owe"><a [routerLink]="'/backoffice/customers/accounts'">{{ i18n.t('dash.owe.title') }}</a></h2>
            <span class="metric-ic" aria-hidden="true">
              <svg class="ic" viewBox="0 0 24 24"><path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/></svg>
            </span>
          </header>
          <ng-container *ngTemplateOutlet="status; context: { $implicit: owe.state() }" />
          @if (owe.data(); as o) {
            <p class="figure mono" [class.is-danger]="o.pastDue > 0">{{ i18n.money(o.outstanding) }}</p>
            <p class="metric-note" [class.is-warn]="o.pastDue > 0">
              {{ i18n.t('dash.owe.pastDue') }}: <span class="mono">{{ i18n.money(o.pastDue) }}</span>
            </p>
          }
        </section>

        <!-- Rentals -->
        <section class="card metric" [attr.data-tone]="tones().rentals" aria-labelledby="db-rentals">
          <header class="metric-top">
            <h2 id="db-rentals"><a [routerLink]="'/backoffice/rentals/contracts'">{{ i18n.t('nav.rent') }}</a></h2>
            <span class="metric-ic" aria-hidden="true">
              <svg class="ic" viewBox="0 0 24 24"><rect width="18" height="18" x="3" y="3" rx="2"/><path d="M7 7h10"/><path d="M7 12h10"/><path d="M7 17h10"/></svg>
            </span>
          </header>
          <ng-container *ngTemplateOutlet="status; context: { $implicit: rentals.state() }" />
          @if (rentals.data(); as r) {
            <p class="figure mono">{{ r.totals.stillOut }}</p>
            <p class="metric-note">{{ i18n.t('rent.list.outNow') }} — {{ i18n.t('rent.list.withCustomers', { n: r.totals.openWithGoods }) }}</p>
            <div class="metric-sub">
              <span>{{ i18n.t('rent.list.notIssued') }}: <span class="mono">{{ r.totals.drafts }}</span></span>
              <span> · {{ i18n.t('rent.chargedSoFar') }}: <span class="mono">{{ i18n.money(r.totals.charged) }}</span></span>
            </div>
            @if (r.capped) {
              <p class="note">{{ i18n.t('dash.capped', { n: CONTRACT_CAP }) }}</p>
            }
          }
        </section>

        <!-- This till -->
        <section class="card metric" [attr.data-tone]="tones().till" aria-labelledby="db-till">
          <header class="metric-top">
            <h2 id="db-till"><a routerLink="/sync">{{ i18n.t('dash.till.title') }}</a></h2>
            <span class="metric-ic" aria-hidden="true">
              <svg class="ic" viewBox="0 0 24 24"><rect width="20" height="14" x="2" y="3" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/><path d="M7 8h10"/></svg>
            </span>
          </header>
          <div class="till-stats">
            <div class="till-stat" [class.is-warn]="sync.stats().pending > 0">
              <span class="muted">{{ i18n.t('sync.pending') }}</span>
              <span class="figure mono">{{ sync.stats().pending }}</span>
            </div>
            <div class="till-stat" [class.is-danger]="sync.stats().rejected > 0">
              <span class="muted">{{ i18n.t('sync.rejected') }}</span>
              <span class="figure mono">{{ sync.stats().rejected }}</span>
            </div>
          </div>
          <p class="metric-note">{{ i18n.t('dash.till.scope') }}</p>
        </section>
      </div>

      <!-- Row 2: Sales by branch (flex 2) & Cash boxes (flex 1) -->
      <div class="dash-row dash-row-2">
        @if (showByBranch()) {
          <section class="card block--branches" aria-labelledby="db-branches">
            <header class="card-head">
              <h2 id="db-branches" class="card-title">
                <a [routerLink]="'/backoffice/sales/overview'">{{ i18n.t('dash.byBranch.title') }}</a>
              </h2>
            </header>
            <div class="table-wrap">
              <table class="ui-table branch-table">
                <thead>
                  <tr>
                    <th scope="col">{{ i18n.t('branchLock.branch') }}</th>
                    <th scope="col" class="num">{{ i18n.t('reports.saleCount') }}</th>
                    <th scope="col" class="num">{{ i18n.t('reports.grossTtc') }}</th>
                    <th scope="col" class="bar-col"><span class="visually-hidden">{{ i18n.t('reports.share') }}</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (b of sales.data()!.branchBreakdown; track b.branchId) {
                    <tr>
                      <td dir="auto" class="branch-name">{{ b.branchName }}</td>
                      <td class="num mono">{{ b.saleCount }}</td>
                      <td class="num mono">{{ i18n.money(b.grossTtc) }}</td>
                      <td class="bar-col">
                        <div class="share-cell">
                          <span class="share-pct mono">{{ share(b.grossTtc) }}%</span>
                          <span class="share-bar" aria-hidden="true">
                            <span class="share-bar__fill" [style.inline-size.%]="share(b.grossTtc)"></span>
                          </span>
                        </div>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </section>
        }

        <!-- Cash boxes -->
        <section class="card block--cash" [attr.data-tone]="tones().cash" aria-labelledby="db-cash">
          <header class="card-head">
            <h2 id="db-cash" class="card-title">
              <a [routerLink]="'/backoffice/finance/cash-boxes'">{{ i18n.t('treasury.cash') }}</a>
            </h2>
            <span class="scope-tag">{{ scopeWord(branchScope()) }}</span>
          </header>
          <ng-container *ngTemplateOutlet="status; context: { $implicit: cash.state() }" />
          @if (cash.data(); as boxes) {
            @if (boxes.length === 0) {
              <div class="dash-empty"><p class="note">{{ i18n.t('dash.cash.none') }}</p></div>
            } @else {
              <ul class="rows-list">
                @for (b of boxes; track b.id) {
                  <li class="row-item">
                    <div class="row-main">
                      <span class="row-title" dir="auto">{{ boxName(b) }}</span>
                    </div>
                    @if (b.isMain) {
                      <span class="ui-tag">{{ i18n.t('treasury.centralSafe') }}</span>
                    }
                    <span class="row-val mono">{{ i18n.money(b.balance) }}</span>
                  </li>
                }
              </ul>
            }
          }
        </section>
      </div>

      <!-- Row 3: Worklists (3 equal cards) -->
      <div class="dash-row dash-row-3">
        <!-- Vouchers to approve -->
        <section class="card block--worklist" [attr.data-tone]="tones().vouchers" aria-labelledby="db-vouchers">
          <header class="card-head">
            <div class="card-title-group">
              <h2 id="db-vouchers" class="card-title">
                <a [routerLink]="'/backoffice/finance/vouchers'">{{ i18n.t('dash.vouchers.title') }}</a>
              </h2>
              @if (vouchers.data(); as v) {
                <span class="count-tag">{{ v.figures.count }}</span>
              }
            </div>
            <a [routerLink]="'/backoffice/finance/vouchers'" class="open-link">
              {{ i18n.t('dash.open') }}
              <svg class="ic ic-sm ic-dir" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 18 6-6-6-6"/></svg>
            </a>
          </header>
          <ng-container *ngTemplateOutlet="status; context: { $implicit: vouchers.state() }" />
          @if (vouchers.data(); as v) {
            <div class="worklist-body">
              <ng-container *ngTemplateOutlet="countAmount; context: { $implicit: v.figures, noneKey: 'dash.vouchers.none' }" />
              @if (v.capped) {
                <p class="note">{{ i18n.t('dash.capped', { n: VOUCHER_TAKE }) }}</p>
              }
            </div>
          }
        </section>

        <!-- Awaiting collection -->
        <section class="card block--worklist" [attr.data-tone]="tones().awaiting" aria-labelledby="db-awaiting">
          <header class="card-head">
            <div class="card-title-group">
              <h2 id="db-awaiting" class="card-title">
                <a [routerLink]="'/backoffice/finance/awaiting-collection'">{{ i18n.t('treasury.pending') }}</a>
              </h2>
              @if (awaiting.data(); as a) {
                <span class="count-tag">{{ a.figures.count }}</span>
              }
            </div>
            <a [routerLink]="'/backoffice/finance/awaiting-collection'" class="open-link">
              {{ i18n.t('dash.open') }}
              <svg class="ic ic-sm ic-dir" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 18 6-6-6-6"/></svg>
            </a>
          </header>
          <ng-container *ngTemplateOutlet="status; context: { $implicit: awaiting.state() }" />
          @if (awaiting.data(); as a) {
            <div class="worklist-body">
              <ng-container *ngTemplateOutlet="countAmount; context: { $implicit: a.figures, noneKey: 'dash.awaiting.none' }" />
              @if (a.capped) {
                <p class="note">{{ i18n.t('dash.capped', { n: PENDING_CAP }) }}</p>
              }
            </div>
          }
        </section>

        <!-- Low stock -->
        <section class="card block--worklist" [attr.data-tone]="tones().low" aria-labelledby="db-low">
          <header class="card-head">
            <div class="card-title-group">
              <h2 id="db-low" class="card-title">
                <a [routerLink]="'/backoffice/inventory/low-stock'">{{ i18n.t('reports.lowStock') }}</a>
              </h2>
              @if (low.data(); as n) {
                <span class="count-tag">{{ n }}</span>
              }
            </div>
            <a [routerLink]="'/backoffice/inventory/low-stock'" class="open-link">
              {{ i18n.t('dash.open') }}
              <svg class="ic ic-sm ic-dir" viewBox="0 0 24 24" aria-hidden="true"><path d="m9 18 6-6-6-6"/></svg>
            </a>
          </header>
          <ng-container *ngTemplateOutlet="status; context: { $implicit: low.state() }" />
          <div class="worklist-body">
            @if (low.data(); as n) {
              <p class="figure mono" [class.is-warn]="n > 0">{{ n }}</p>
              <p class="note">{{ i18n.t('dash.low.note') }}</p>
            } @else if (low.state().status === 'ready') {
              <p class="figure mono">0</p>
              <p class="note">{{ i18n.t('dash.low.note') }}</p>
            }
          </div>
        </section>
      </div>
    </div>

    <ng-template #status let-state>
      @switch (state.status) {
        @case ('loading') {
          <div class="dash-skeleton" aria-busy="true" [attr.aria-label]="i18n.t('common.loading')">
            <span class="skeleton-line skeleton-figure"></span>
            <span class="skeleton-line skeleton-note"></span>
          </div>
        }
        @case ('offline') { <p class="note p-body is-warn">{{ i18n.t('dash.offline') }}</p> }
        @case ('error') { <p class="note p-body is-danger" role="alert">{{ state.message }}</p> }
      }
    </ng-template>

    <ng-template #countAmount let-f let-noneKey="noneKey">
      @if (f.count === 0) {
        <p class="figure mono">0</p>
        <p class="note">{{ i18n.t(noneKey) }}</p>
      } @else {
        <p class="figure mono is-warn">{{ f.count }}</p>
        <p class="note">{{ i18n.t('dash.forAmount', { amount: i18n.money(f.amount) }) }}</p>
      }
    </ng-template>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      /* Atelier dashboard (docs/design/atelier): the key figures in a row, sales by branch beside
         the cash boxes, the three worklists under them. Each card says its state in words; the
         tone (dashboard-figures.ts cardTone) only colours the figure, never the ground. */
      .dash-page {
        display: grid;
        /* minmax(0, …): a wide table scrolls in its own wrapper instead of widening the page on a phone. */
        grid-template-columns: minmax(0, 1fr);
        gap: var(--sp-4);
      }
      .dash-row-1,
      .dash-row-3 {
        display: grid;
        gap: var(--sp-4);
        align-items: stretch;
      }
      .dash-row-1 {
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 15rem), 1fr));
      }
      .dash-row-3 {
        grid-template-columns: repeat(auto-fit, minmax(min(100%, 18rem), 1fr));
      }
      .dash-row-2 {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-4);
        align-items: stretch;
      }
      .block--branches {
        flex: 2 1 32rem;
        min-inline-size: 0;
      }
      .block--cash {
        flex: 1 1 20rem;
        min-inline-size: 0;
      }

      .ic {
        inline-size: 18px;
        block-size: 18px;
        flex: none;
        fill: none;
        stroke: currentColor;
        stroke-width: 2;
        stroke-linecap: round;
        stroke-linejoin: round;
      }
      .ic-sm {
        inline-size: 16px;
        block-size: 16px;
      }
      :host-context([dir='rtl']) .ic-dir {
        transform: scaleX(-1);
      }

      /* ---- key figures ---- */
      .metric {
        display: grid;
        gap: var(--sp-2-5, 10px);
        align-content: start;
        padding: 16px 18px 18px;
        container-type: inline-size;
      }
      .metric-top {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-3);
      }
      .metric-top h2,
      .card-title {
        margin: 0;
        font-family: var(--font-display);
        font-weight: 600;
        letter-spacing: 0;
        line-height: 1.2;
      }
      .metric-top h2 {
        font-size: 16px;
        color: var(--text-2);
      }
      .card-title {
        font-size: 20px;
      }
      .metric-top h2 a,
      .card-title a {
        color: inherit;
        text-decoration: none;
      }
      .metric-top h2 a:hover,
      .card-title a:hover {
        text-decoration: underline;
        text-underline-offset: 3px;
      }
      .metric-ic {
        display: inline-grid;
        place-items: center;
        inline-size: 32px;
        block-size: 32px;
        flex: none;
        border: 1px solid var(--border);
        border-radius: var(--radius-sm);
        background: var(--surface-2);
        color: var(--text-2);
      }
      .figure {
        margin: 0;
        font-family: var(--font-display);
        font-size: clamp(20px, 10cqi, 30px);
        font-weight: 700;
        letter-spacing: 0;
        line-height: 1.15;
        font-variant-numeric: tabular-nums;
      }
      .metric-note,
      .metric-sub,
      .note {
        margin: 0;
        font-size: 13px;
        color: var(--muted);
      }
      .metric-note strong,
      .metric-sub strong {
        color: var(--text-2);
        font-weight: 600;
      }
      .metric[data-tone='success'] .figure {
        color: var(--ok);
      }
      .metric[data-tone='warning'] .figure,
      .is-warn {
        color: var(--warn);
      }
      .metric[data-tone='critical'] .figure,
      .is-danger {
        color: var(--danger);
      }

      /* The one key tile: today's sales on the signal orange. */
      .metric--key {
        background: var(--accent);
        color: var(--on-accent);
      }
      .metric--key .metric-top h2,
      .metric--key .metric-note,
      .metric--key .metric-note strong,
      .metric--key .note,
      .metric--key[data-tone] .figure {
        color: var(--on-accent);
      }
      /* On the orange, the icon square is drawn with translucent contrast */
      .metric--key .metric-ic {
        background: color-mix(in srgb, var(--on-accent) 15%, transparent);
        border-color: color-mix(in srgb, var(--on-accent) 30%, transparent);
        color: var(--on-accent);
      }

      .till-stats {
        display: grid;
        gap: var(--sp-2);
      }
      .till-stat {
        display: flex;
        align-items: baseline;
        justify-content: space-between;
        gap: var(--sp-3);
        padding-block-start: var(--sp-2);
        border-block-start: 1px solid var(--border);
      }
      .till-stat .figure {
        font-size: 22px;
      }

      /* ---- cards with a head ---- */
      .card-head {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-3);
        padding: 14px 18px;
        border-block-end: var(--line) solid var(--border-strong);
      }
      .card-title-group {
        display: flex;
        align-items: center;
        gap: var(--sp-3);
        min-inline-size: 0;
      }
      .count-tag {
        display: inline-grid;
        place-items: center;
        min-inline-size: 24px;
        block-size: 22px;
        padding-inline: 7px;
        border-radius: var(--radius-pill);
        border: 1px solid var(--border);
        background: var(--surface-2);
        color: var(--text-2);
        font-family: var(--font-mono);
        font-size: 12px;
        font-weight: 600;
      }
      .open-link {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-1);
        font-size: 13px;
        font-weight: 600;
        color: var(--text-2);
        text-decoration: none;
        white-space: nowrap;
      }
      .open-link:hover {
        color: var(--accent);
        text-decoration: underline;
        text-underline-offset: 3px;
      }
      .scope-tag {
        font-size: 12.5px;
        color: var(--muted);
      }
      .p-body {
        padding: var(--sp-4) 20px;
      }
      .metric .p-body {
        padding: 0;
      }
      .worklist-body {
        display: grid;
        gap: var(--sp-2);
        padding: 14px 18px 18px;
      }

      /* Sales by branch: the share as a sleek accent bar. */
      .table-wrap {
        overflow-x: auto;
      }
      .branch-name {
        font-weight: 600;
      }
      .bar-col {
        inline-size: 34%;
      }
      .share-cell {
        display: flex;
        align-items: center;
        gap: var(--sp-3);
      }
      .share-pct {
        min-inline-size: 3.5em;
        font-size: 13px;
        text-align: end;
      }
      .share-bar {
        flex: 1;
        block-size: 8px;
        border: 0;
        border-radius: var(--radius-pill);
        background: var(--surface-2);
        overflow: hidden;
      }
      .share-bar__fill {
        display: block;
        block-size: 100%;
        border-radius: var(--radius-pill);
        background: var(--accent);
        transition: inline-size 300ms ease;
      }

      /* Cash boxes as rows. */
      .rows-list {
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .row-item {
        display: flex;
        align-items: center;
        gap: var(--sp-3);
        padding: 12px 18px;
        border-block-start: 1px solid var(--border);
        transition: background-color var(--t-fast) ease;
      }
      .row-item:hover {
        background-color: var(--surface-hover);
      }
      .row-item:first-child {
        border-block-start: 0;
      }
      .row-main {
        flex: 1;
        min-inline-size: 0;
      }
      .row-title {
        font-weight: 600;
      }
      .row-val {
        font-weight: 600;
        white-space: nowrap;
      }

      /* Loading skeleton & empty states */
      @keyframes dash-pulse {
        0%, 100% { opacity: 0.65; }
        50% { opacity: 0.25; }
      }
      .dash-skeleton {
        display: grid;
        gap: 8px;
        padding-block: 4px;
      }
      .skeleton-line {
        display: block;
        border-radius: var(--radius-sm);
        background: var(--surface-2);
        animation: dash-pulse 1.5s ease-in-out infinite;
      }
      .metric--key .skeleton-line {
        background: color-mix(in srgb, var(--on-accent) 28%, transparent);
      }
      .skeleton-figure {
        block-size: 28px;
        inline-size: 50%;
      }
      .skeleton-note {
        block-size: 12px;
        inline-size: 80%;
      }
      .dash-empty {
        display: flex;
        align-items: center;
        justify-content: center;
        padding: var(--sp-6) var(--sp-4);
        text-align: center;
      }

      .muted {
        color: var(--muted);
        font-weight: 400;
      }
      .visually-hidden {
        position: absolute;
        inline-size: 1px;
        block-size: 1px;
        overflow: hidden;
        clip-path: inset(50%);
        white-space: nowrap;
      }
    `,
  ],
})
export class DashboardComponent implements OnInit {
  readonly i18n = inject(I18nService);
  readonly connectivity = inject(ConnectivityService);
  readonly scope = inject(DashboardScope);
  readonly sync = inject(SyncService);
  private readonly lock = inject(BranchLock);
  private readonly face = inject(BranchFace);
  private readonly admin = inject(AdminService);
  private readonly rentalsApi = inject(RentalsApi);
  private readonly receivables = inject(ReceivablesApi);
  private readonly vouchersApi = inject(PaymentVouchersApi);

  readonly ALL = '__all__';
  readonly CONTRACT_CAP = CONTRACT_CAP;
  readonly PENDING_CAP = PENDING_CAP;
  readonly VOUCHER_TAKE = VOUCHER_TAKE;

  /** The company's branches for the Admin; null until known (loading, or the list failed). */
  private readonly branchList = signal<AdminBranch[] | null>(null);
  private readonly listing = signal<'idle' | 'loading' | 'done' | 'failed'>('idle');
  /** Set once the first answer for the list is in, success or not; a retry never unsets it. */
  private readonly firstListing = signal(false);
  readonly branches = computed(() => this.branchList() ?? []);

  readonly sales = new Block<SalesSummary>();
  readonly rentals = new Block<RentalsBlock>();
  readonly owe = new Block<{ outstanding: number; pastDue: number }>();
  readonly vouchers = new Block<{ figures: CountAmount; capped: boolean }>();
  readonly awaiting = new Block<{ figures: CountAmount; capped: boolean }>();
  readonly cash = new Block<AdminCashBox[]>();
  readonly low = new Block<number>();

  /**
   * Each card's ground, from the state its own words already say (PinSay #300). Rentals out are
   * live contracts, the info tone a chip gives an Open contract; cash balances carry no state.
   */
  readonly tones = computed<Record<CardName, CardTone | null>>(() => ({
    sales: cardTone(this.sales.state().status, () => 'money'),
    rentals: cardTone(this.rentals.state().status, () => ((this.rentals.data()?.totals.stillOut ?? 0) > 0 ? 'info' : 'neutral')),
    owe: cardTone(this.owe.state().status, () => oweTone(this.owe.data()!)),
    vouchers: cardTone(this.vouchers.state().status, () => waitingTone(this.vouchers.data()!.figures.count)),
    awaiting: cardTone(this.awaiting.state().status, () => waitingTone(this.awaiting.data()!.figures.count)),
    cash: cardTone(this.cash.state().status, () => 'neutral'),
    low: cardTone(this.low.state().status, () => waitingTone(this.low.data() ?? 0)),
    // The device's own outbox answers offline too, so it is never the offline warning.
    till: tillTone(this.sync.stats()),
  }));

  /** The branch the branch-aware blocks ask for (null: every branch), and how to say it. */
  readonly resolved = computed(() =>
    dashboardScope({ lockedId: this.scope.lockedId(), stored: this.scope.stored(), branches: this.branchList() }),
  );
  /** The Admin's first look waits for the branch list, so it opens on the right scope, not on two. */
  private readonly ready = computed(() => !this.scope.canChoose() || this.firstListing());
  /** A one-branch company has nothing to choose between. */
  readonly showPicker = computed(() => this.scope.canChoose() && this.branches().length > 1);
  readonly branchScope = computed<ScopeLabel>(() => this.resolved().label);
  readonly branchChoice = computed(() => this.resolved().branchId ?? this.ALL);

  readonly showByBranch = computed(
    () => this.resolved().label === 'all' && !this.lock.locked() && (this.sales.data()?.branchBreakdown.length ?? 0) > 1,
  );
  private readonly branchTop = computed(() =>
    Math.max(0, ...(this.sales.data()?.branchBreakdown ?? []).map((b) => b.grossTtc)),
  );

  constructor() {
    // A change of branch, or the connection coming back, asks every server block again.
    effect(() => {
      this.resolved();
      this.connectivity.online();
      if (!this.ready()) return;
      untracked(() => this.reload());
    });
  }

  ngOnInit(): void {
    void this.sync.refreshStats();
    void this.loadBranches();
  }

  private async loadBranches(): Promise<void> {
    if (!this.scope.canChoose()) return;
    this.listing.set('loading');
    try {
      this.branchList.set(await this.admin.listBranches());
      this.listing.set('done');
    } catch {
      // Unknown: a remembered choice still holds, else every branch; asked again on the next reload.
      this.listing.set('failed');
    }
    this.firstListing.set(true);
  }

  reload(): void {
    const online = this.connectivity.online();
    // The list decides the default scope; if it comes in and moves the scope, the effect asks again.
    if (online && this.listing() === 'failed') void this.loadBranches();
    const branchId = this.resolved().branchId;
    void this.sales.load(online, () => {
      const { fromUtc, toUtc } = todayWindow(new Date());
      return this.admin.getSalesSummary(fromUtc, toUtc, branchId);
    });
    void this.rentals.load(online, async () => {
      const rows = await this.rentalsApi.listContracts('Open', undefined, branchId);
      return { totals: rentalTotals(rows), capped: rows.length >= CONTRACT_CAP };
    });
    void this.owe.load(online, async () => customersOwe(await this.receivables.ageing()));
    void this.vouchers.load(online, async () => {
      const rows = await this.vouchersApi.list({ status: VOUCHER_PENDING, branchId, take: VOUCHER_TAKE });
      return { figures: vouchersToApprove(rows), capped: rows.length >= VOUCHER_TAKE };
    });
    void this.awaiting.load(online, async () => {
      const rows = await this.admin.listPendingPayments('Pending', branchId);
      return { figures: awaitingCollection(rows), capped: rows.length >= PENDING_CAP };
    });
    void this.cash.load(online, async () => cashBoxesFor(await this.admin.listCashBoxes(branchId), branchId));
    void this.low.load(online, async () => lowStockCount(await this.admin.getLowStock(branchId)));
    void this.sync.refreshStats();
  }

  chooseBranch(value: string): void {
    // The dashboard's own choice: the working branch, and so every other page, is untouched.
    this.scope.set(value === this.ALL ? { kind: 'all' } : { kind: 'branch', id: value });
  }

  scopeWord(label: ScopeLabel): string {
    if (label === 'company') return this.i18n.t('scope.company');
    if (label === 'all') return this.i18n.t('scope.all');
    const id = this.resolved().branchId;
    const branch = this.branches().find((b) => b.id === id);
    // A locked user's branch, or the Admin's own without the list: the branch the header names.
    const name = branch ? this.name(branch) : id === this.lock.ownBranchId() ? this.face.name() : '';
    return this.i18n.t('scope.branch', { branch: name || '—' });
  }

  name(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  boxName(box: AdminCashBox): string {
    return localizedName(box, this.i18n.lang());
  }

  share(value: number): number {
    return shareOf(value, this.branchTop());
  }
}
