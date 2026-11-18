import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../core/i18n.service';
import { LIST_FILTERS_TRANSLATIONS } from '../../core/i18n/translations-list-filters';
import { LIST_FILTERS_SALES_TRANSLATIONS } from '../../core/i18n/translations-list-filters-sales';
import { Customer, PaymentMethod, Sale } from '../../core/models';
import { roundMoney } from '../../core/money';
import { RegionalService } from '../../core/regional.service';
import { PosService } from '../../core/pos.service';
import { ReceiptComponent } from '../../shared/receipt.component';
import { SendReceiptComponent } from '../../shared/send-receipt.component';
import { ConfirmService } from '../../ui/confirm.service';
import { actionRequest } from '../../ui/confirm-requests';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createPager } from '../../ui/pager';
import { TableStateComponent } from '../../ui/table-state.component';

interface RefundLineDraft {
  lineId: string;
  name: string;
  maxQty: number;
  qty: number;
}

@Component({
  selector: 'app-sales',
  standalone: true,
  imports: [ReceiptComponent, FormsModule, SendReceiptComponent, ListFooterComponent, TableStateComponent],
  template: `
    <div class="wrap">
      <header class="head">
        <h1>{{ i18n.t('sales.title') }}</h1>
        <div class="filters" role="group" [attr.aria-label]="i18n.t('sales.title')">
          <button
            type="button"
            [class.active]="filter() === 'today'"
            [attr.aria-pressed]="filter() === 'today'"
            (click)="setFilter('today')"
          >
            {{ i18n.t('sales.today') }}
          </button>
          <button
            type="button"
            [class.active]="filter() === 'all'"
            [attr.aria-pressed]="filter() === 'all'"
            (click)="setFilter('all')"
          >
            {{ i18n.t('sales.all') }}
          </button>
          <button
            type="button"
            [class.active]="filter() === 'pending'"
            [attr.aria-pressed]="filter() === 'pending'"
            (click)="setFilter('pending')"
          >
            {{ i18n.t('sales.pendingOnly') }}
          </button>
        </div>
      </header>

      <!-- More filters (owner, 2026-11-17), over the sales already on this device; each shows only
           when the sales on it differ in that respect. The tiles below count what the filters show. -->
      @if (kindChoices().length > 1 || methodChoices().length > 1 || cashierChoices().length > 1 || filtered()) {
        <div class="ui-toolbar" role="search">
          @if (kindChoices().length > 1 || kindFilter() !== 'all') {
            <div class="ui-filter">
              <label for="sl-f-kind">{{ i18n.t('filters.kind') }}</label>
              <select id="sl-f-kind" [ngModel]="kindFilter()" (ngModelChange)="kindFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                <option ngValue="sale">{{ i18n.t('sales.filter.sale') }}</option>
                <option ngValue="refund">{{ i18n.t('sales.filter.refund') }}</option>
              </select>
            </div>
          }
          @if (methodChoices().length > 1 || methodFilter() !== 'all') {
            <div class="ui-filter">
              <label for="sl-f-method">{{ i18n.t('pay.method') }}</label>
              <select id="sl-f-method" [ngModel]="methodFilter()" (ngModelChange)="methodFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                @for (method of methodChoices(); track method) {
                  <option [ngValue]="method">{{ i18n.t('pay.' + method.toLowerCase()) }}</option>
                }
              </select>
            </div>
          }
          @if (cashierChoices().length > 1 || cashierFilter() !== 'all') {
            <div class="ui-filter">
              <label for="sl-f-cashier">{{ i18n.t('receipt.cashier') }}</label>
              <select id="sl-f-cashier" [ngModel]="cashierFilter()" (ngModelChange)="cashierFilter.set($event)">
                <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                @for (name of cashierChoices(); track name) {
                  <option [ngValue]="name">{{ name }}</option>
                }
              </select>
            </div>
          }
          @if (filtered()) {
            <div class="ui-toolbar__end">
              <button type="button" class="btn-ghost small" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
            </div>
          }
        </div>
      }

      <div class="summary">
        <div class="stat card">
          <span class="stat-label">{{ i18n.t('sales.title') }}</span>
          <strong class="stat-value mono">{{ rows().length }}</strong>
        </div>
        <div class="stat card stat-money">
          <span class="stat-label">{{ i18n.t('register.total') }}</span>
          <strong class="stat-value mono">{{ i18n.money(total()) }}</strong>
        </div>
        <div class="stat card">
          <span class="stat-label">{{ i18n.t('register.tax') }}</span>
          <strong class="stat-value mono">{{ i18n.money(taxTotal()) }}</strong>
        </div>
      </div>

      <div class="card table-wrap table-cards">
        <table>
          <thead>
            <tr>
              <th>{{ i18n.t('receipt.no') }}</th>
              <th>{{ i18n.t('receipt.date') }}</th>
              <th>{{ i18n.t('receipt.cashier') }}</th>
              <th class="num">{{ i18n.t('register.total') }}</th>
              <th>{{ i18n.t('nav.sync') }}</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (s of pager.rows(); track s.id) {
              <tr>
                <td class="mono ref" [attr.data-label]="i18n.t('receipt.no')">{{ s.reference }}</td>
                <td [attr.data-label]="i18n.t('receipt.date')">{{ i18n.dateTime(s.soldAtUtc) }}</td>
                <td [attr.data-label]="i18n.t('receipt.cashier')">{{ s.cashierName }}</td>
                <td class="num mono amount" [attr.data-label]="i18n.t('register.total')">{{ i18n.money(s.grandTotalTtc) }}</td>
                <td [attr.data-label]="i18n.t('nav.sync')">
                  <!-- The sync state is a word first. The tint only makes it findable. -->
                  <span class="badge" [class]="badgeClass(s)">{{ stateLabel(s) }}</span>
                </td>
                <td class="num row-actions">
                  <button type="button" class="btn-ghost small" (click)="open(s)">
                    {{ i18n.t('sales.reprint') }}
                  </button>
                  @if (canRefund(s)) {
                    <button type="button" class="btn-ghost small" (click)="openRefund(s)">
                      {{ i18n.t('sales.refund') }}
                    </button>
                  } @else if (isRefunded(s)) {
                    <span class="muted small-label">{{ i18n.t('sales.refunded') }}</span>
                  }
                </td>
              </tr>
            } @empty {
              <tr class="empty-row">
                <td colspan="6" class="center">
                  <!-- An empty list under "not sent yet" is not an absence, it is the answer
                       the screen was opened for, so it says so rather than reporting nothing. -->
                  @if (segment().length && filtered()) {
                    <app-table-state kind="noResults" (clear)="clearFilters()" />
                  } @else {
                    <strong>{{ i18n.t(filter() === 'pending' ? 'status.allSynced' : 'sales.none') }}</strong>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      <app-list-footer [pager]="pager" />
    </div>

    @if (selected(); as sale) {
      <div class="till-dialog" (click)="selected.set(null)">
        <div
          class="modal card"
          role="dialog"
          aria-modal="true"
          aria-labelledby="reprint-title"
          (click)="$event.stopPropagation()"
        >
          <header class="modal-head">
            <h2 id="reprint-title">{{ i18n.t('sales.reprint') }}</h2>
            <span class="modal-ref mono">{{ sale.reference }}</span>
          </header>
          <div class="modal-body">
            <div class="receipt-holder">
              <app-receipt [sale]="sale" [store]="pos.store()" [customer]="customerOf(sale)" />
            </div>
            <app-send-receipt [sale]="sale" />
          </div>
          <div class="modal-foot">
            <div class="actions">
              <button type="button" class="btn-ghost" (click)="selected.set(null)">
                {{ i18n.t('common.close') }}
              </button>
              <button type="button" class="btn-primary" (click)="print()">{{ i18n.t('pay.print') }}</button>
            </div>
          </div>
        </div>
      </div>
    }

    @if (refundTarget(); as target) {
      <div class="till-dialog" (click)="closeRefund()">
        <div
          class="modal card"
          role="dialog"
          aria-modal="true"
          aria-labelledby="refund-title"
          (click)="$event.stopPropagation()"
        >
          @if (!refundResult()) {
            <header class="modal-head">
              <h2 id="refund-title">{{ i18n.t('refund.title') }}</h2>
              <span class="modal-ref mono">{{ target.reference }}</span>
            </header>

            <div class="modal-body">
              <p class="muted lead">{{ i18n.t('refund.selectItems') }}</p>

              <div class="refund-lines">
                @for (line of refundLines(); track line.lineId) {
                  <div class="refund-line">
                    <span class="rl-name">{{ line.name }}</span>
                    <input
                      type="number"
                      min="0"
                      [max]="line.maxQty"
                      step="1"
                      class="mono num"
                      [attr.aria-label]="i18n.t('register.qty') + ' — ' + line.name"
                      [ngModel]="line.qty"
                      (ngModelChange)="setLineQty(line, $event)"
                    />
                    <span class="muted mono rl-max">/ {{ line.maxQty }}</span>
                  </div>
                }
              </div>

              <div class="field">
                <label for="refund-method">{{ i18n.t('refund.method') }}</label>
                <select id="refund-method" [ngModel]="refundMethod()" (ngModelChange)="refundMethod.set($event)">
                  @for (method of refundMethods(); track method) {
                    <option [value]="method">{{ i18n.t('pay.' + method.toLowerCase()) }}</option>
                  }
                </select>
              </div>
              @if (refundMethod() === 'GiftCard') {
                <div class="field">
                  <label for="refund-card">{{ i18n.t('pay.giftCardCode') }}</label>
                  <input id="refund-card" class="mono" [ngModel]="refundCard()" (ngModelChange)="refundCard.set($event)" />
                </div>
              }

              <div class="change">
                <span>{{ i18n.t('refund.total') }}</span>
                <strong class="mono num">{{ i18n.money(refundPreviewTotal()) }}</strong>
              </div>

              @if (refundError()) {
                <p class="error" role="alert">{{ refundError() }}</p>
              }
            </div>

            <div class="modal-foot">
              <div class="actions">
                <button type="button" class="btn-ghost" (click)="closeRefund()">{{ i18n.t('pay.cancel') }}</button>
                <button type="button" class="btn-primary" [disabled]="refundBusy()" (click)="confirmRefund()">
                  {{ refundBusy() ? i18n.t('common.loading') : i18n.t('refund.confirm') }}
                </button>
              </div>
            </div>
          } @else {
            <div class="modal-body">
              <div class="done">
                <h2>{{ i18n.t('refund.success') }}</h2>
                <p class="done-ref mono">{{ refundResult()!.reference }}</p>
              </div>
              <div class="receipt-holder">
                <app-receipt [sale]="refundResult()!" [store]="pos.store()" [customer]="customerOf(target)" />
              </div>
            </div>
            <div class="modal-foot">
              <div class="actions">
                <button type="button" class="btn-ghost" (click)="printRefund()">{{ i18n.t('pay.print') }}</button>
                <button type="button" class="btn-primary" (click)="closeRefund()">{{ i18n.t('common.close') }}</button>
              </div>
            </div>
          }
        </div>
      </div>
    }
  `,
  styles: [
    `
      .wrap {
        display: grid;
        gap: var(--sp-5);
      }

      .head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-5);
        flex-wrap: wrap;

        h1 {
          font-size: var(--fs-lg);
          margin: 0;
          letter-spacing: -0.01em;
        }
      }

      /* One segmented control, not three loose buttons: the three filters are one choice. */
      .filters {
        display: flex;
        gap: 2px;
        background: var(--surface);
        border: 1px solid var(--border-strong);
        border-radius: var(--radius);
        padding: 2px;

        button {
          border: none;
          background: transparent;
          border-radius: calc(var(--radius) - 3px);
          font-size: var(--fs-sm);
          min-height: var(--control-h-sm);
          padding: 0 var(--sp-4);
          color: var(--muted);

          &:hover:not(.active) {
            background: var(--surface-2);
          }

          &.active {
            background: var(--primary-soft);
            color: var(--primary-dark);
            font-weight: 600;
          }
        }
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

      .summary {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
        gap: var(--sp-4);
      }

      .stat {
        display: grid;
        gap: var(--sp-1);
        padding: var(--sp-4) var(--sp-5);
        background: var(--surface);
        border: var(--line) solid var(--border-strong);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow);
      }

      .stat-label {
        font-size: var(--fs-meta);
        font-weight: 600;
        color: var(--text-2);
      }

      .stat-value {
        font-family: var(--font-display);
        font-size: 34px;
        font-weight: 600;
        letter-spacing: -0.03em;
        line-height: var(--lh-tight);
        font-variant-numeric: tabular-nums;
        color: var(--text);
      }

      /* The day's takings is the key tile: var(--accent) fill, var(--on-accent) text. */
      .stat-money {
        background: var(--accent);
        border-color: var(--border-strong);

        .stat-label {
          color: var(--on-accent);
        }

        .stat-value {
          color: var(--on-accent);
        }
      }

      .table-wrap {
        overflow-x: auto;
        background: var(--surface);
        border: var(--line) solid var(--border-strong);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow);
      }

      thead th {
        position: sticky;
        top: 0;
        z-index: 1;
      }

      .ref {
        font-weight: 600;
      }

      .amount {
        font-weight: 600;
      }

      .small {
        min-height: var(--control-h-sm);
        padding: 0 var(--sp-3);
        font-size: var(--fs-sm);
      }

      .center {
        text-align: center;
        padding: var(--sp-7) 0;
        color: var(--muted);
      }

      .lead {
        margin: 0;
        font-size: var(--fs-sm);
        line-height: 1.45;
      }

      .row-actions {
        display: flex;
        gap: var(--sp-2);
        justify-content: end;
        align-items: center;
        flex-wrap: wrap;
      }

      .small-label {
        font-size: var(--fs-meta);
      }

      .error {
        margin: 0;
        padding: var(--sp-3) var(--sp-4);
        border-radius: var(--radius);
        background: var(--danger-soft);
        color: var(--danger);
        border-inline-start: 3px solid var(--danger);
        font-size: var(--fs-sm);
        line-height: 1.45;
      }

      .refund-lines {
        display: grid;
        gap: var(--sp-2);
      }

      .refund-line {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 5.5rem auto;
        align-items: center;
        gap: var(--sp-3);
        background: var(--surface-2);
        border: 1px solid var(--border);
        border-radius: var(--radius);
        padding: var(--sp-2) var(--sp-3);
      }

      .rl-name {
        font-weight: 500;
        line-height: 1.3;
      }

      .rl-max {
        font-size: var(--fs-sm);
      }

      @media (max-width: 720px) {
        .empty-row {
          border: none;
        }

        .empty-row td {
          justify-content: center;
        }

        .row-actions {
          justify-content: flex-start;
          padding-top: var(--sp-2) !important;
        }

        .row-actions::before {
          content: none;
        }
      }

    `,
  ],
})
export class SalesComponent implements OnInit {
  readonly pos = inject(PosService);
  readonly i18n = inject(I18nService);
  private readonly confirm = inject(ConfirmService);
  private readonly regional = inject(RegionalService);
  private readonly money = (value: number) => roundMoney(value, this.regional.decimals());

  readonly filter = signal<'today' | 'all' | 'pending'>('today');
  readonly sales = signal<Sale[]>([]);
  readonly selected = signal<Sale | null>(null);

  readonly refundedIds = signal<Set<string>>(new Set());
  readonly refundTarget = signal<Sale | null>(null);
  readonly refundLines = signal<RefundLineDraft[]>([]);
  readonly refundMethod = signal<PaymentMethod>('Cash');
  readonly refundCard = signal('');
  readonly refundBusy = signal(false);
  readonly refundError = signal<string | null>(null);
  readonly refundResult = signal<Sale | null>(null);

  readonly refundPreviewTotal = computed(() => {
    const target = this.refundTarget();
    if (!target) return 0;
    return this.money(
      this.refundLines().reduce((sum, draft) => {
        if (draft.qty <= 0) return sum;
        const orig = target.lines.find((l) => l.id === draft.lineId);
        if (!orig || orig.qty <= 0) return sum;
        return sum + (orig.lineTotalTtc * Math.min(draft.qty, draft.maxQty)) / orig.qty;
      }, 0),
    );
  });

  /** The choice the screen always offered: today, every sale on the device, or those not yet sent. */
  readonly segment = computed(() => {
    const all = this.sales();
    if (this.filter() === 'pending') return all.filter((s) => s.outboxState !== 'synced');
    if (this.filter() === 'today') {
      const startOfDay = new Date();
      startOfDay.setHours(0, 0, 0, 0);
      return all.filter((s) => new Date(s.soldAtUtc) >= startOfDay);
    }
    return all;
  });

  // More filters (owner, 2026-11-17). Each narrows the sales already read from this device; none
  // reads the database again, and the outbox and sync never see them.
  readonly kindFilter = signal<'all' | 'sale' | 'refund'>('all');
  readonly methodFilter = signal<'all' | PaymentMethod>('all');
  readonly cashierFilter = signal<'all' | string>('all');

  /** What the sales on the device actually hold, so a filter offers only what can match. */
  readonly kindChoices = computed(() => [...new Set(this.sales().map((s) => (s.refundOfSaleId ? 'refund' : 'sale')))]);
  readonly methodChoices = computed(() => [...new Set(this.sales().map((s) => s.paymentMethod))]);
  readonly cashierChoices = computed(() =>
    [...new Set(this.sales().map((s) => s.cashierName).filter((name) => !!name))].sort((a, b) => a.localeCompare(b)),
  );

  readonly filtered = computed(
    () => this.kindFilter() !== 'all' || this.methodFilter() !== 'all' || this.cashierFilter() !== 'all',
  );

  /** The rows on screen; the tiles count them, as they always counted what the screen showed. */
  readonly rows = computed(() => {
    const kind = this.kindFilter();
    const method = this.methodFilter();
    const cashier = this.cashierFilter();
    return this.segment().filter(
      (s) =>
        (kind === 'all' || (kind === 'refund' ? !!s.refundOfSaleId : !s.refundOfSaleId)) &&
        (method === 'all' || s.paymentMethod === method) &&
        (cashier === 'all' || s.cashierName === cashier),
    );
  });

  /** Ten sales to begin with; another segment or filter starts at page one. */
  readonly pager = createPager(() => this.rows(), {
    key: 'sales.till',
    resetOn: () => [this.filter(), this.kindFilter(), this.methodFilter(), this.cashierFilter()],
  });

  clearFilters(): void {
    this.kindFilter.set('all');
    this.methodFilter.set('all');
    this.cashierFilter.set('all');
  }

  readonly total = computed(() => this.money(this.rows().reduce((sum, s) => sum + s.grandTotalTtc, 0)));
  readonly taxTotal = computed(() => this.money(this.rows().reduce((sum, s) => sum + s.taxTotal, 0)));

  /** Enabled methods, plus the original sale's own method so money can go back the way it came. */
  readonly refundMethods = computed(() => {
    const original = this.refundTarget()?.paymentMethod;
    const enabled = this.regional.settings().paymentMethods;
    const used = this.refundTarget()?.payments.map((p) => p.method) ?? [];
    return (['Cash', 'Card', 'Credit', 'GiftCard', 'Loyalty'] as PaymentMethod[]).filter((m) => enabled.includes(m) || m === original || used.includes(m));
  });

  constructor() {
    // The filter words live with the back office's; a till that never opened it still needs them.
    this.i18n.extend(LIST_FILTERS_TRANSLATIONS);
    this.i18n.extend(LIST_FILTERS_SALES_TRANSLATIONS);
  }

  async ngOnInit(): Promise<void> {
    await this.pos.loadCatalog();
    await this.reload();
  }

  setFilter(value: 'today' | 'all' | 'pending'): void {
    this.filter.set(value);
  }

  async reload(): Promise<void> {
    this.sales.set(await this.pos.recentSales(500));
    this.refundedIds.set(await this.pos.refundedSaleIds());
  }

  canRefund(sale: Sale): boolean {
    return sale.status === 'Completed' && !this.refundedIds().has(sale.id);
  }

  isRefunded(sale: Sale): boolean {
    return sale.status === 'Completed' && this.refundedIds().has(sale.id);
  }

  // ---------- refund ----------

  openRefund(sale: Sale): void {
    this.refundTarget.set(sale);
    this.refundLines.set(
      sale.lines.map((l) => ({ lineId: l.id, name: l.nameSnapshot, maxQty: l.qty, qty: l.qty })),
    );
    this.refundMethod.set(sale.paymentMethod === 'Mixed' ? 'Cash' : sale.paymentMethod);
    this.refundCard.set(sale.payments.find((p) => p.method === 'GiftCard')?.reference ?? '');
    this.refundError.set(null);
    this.refundResult.set(null);
  }

  setLineQty(line: RefundLineDraft, value: number): void {
    const qty = Math.max(0, Math.min(Number(value) || 0, line.maxQty));
    this.refundLines.set(this.refundLines().map((l) => (l.lineId === line.lineId ? { ...l, qty } : l)));
  }

  closeRefund(): void {
    this.refundTarget.set(null);
    this.refundLines.set([]);
    this.refundResult.set(null);
    this.refundError.set(null);
  }

  async confirmRefund(): Promise<void> {
    const target = this.refundTarget();
    if (!target) return;

    const selections = this.refundLines()
      .map((l) => ({ lineId: l.lineId, qty: Number(l.qty) || 0 }))
      .filter((l) => l.qty > 0);

    if (!selections.length) {
      this.refundError.set(this.i18n.t('refund.none'));
      return;
    }

    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('refund.confirm'))))) return;

    this.refundBusy.set(true);
    this.refundError.set(null);
    try {
      const refund = await this.pos.refund(target, selections, this.refundMethod(), this.refundCard());
      this.refundResult.set(refund);
      await this.reload();
    } catch (error) {
      this.refundError.set((error as Error)?.message ?? 'Error');
    } finally {
      this.refundBusy.set(false);
    }
  }

  printRefund(): void {
    window.print();
  }

  open(sale: Sale): void {
    this.selected.set(sale);
  }

  print(): void {
    window.print();
  }

  customerOf(sale: Sale): Customer | null {
    return this.pos.customers().find((c) => c.id === sale.customerId) ?? null;
  }

  stateLabel(sale: Sale): string {
    if (sale.outboxState === 'synced') return this.i18n.t('status.allSynced');
    if (sale.outboxState === 'rejected') return this.i18n.t('sync.rejected');
    return this.i18n.t('status.pending', { n: 1 });
  }

  badgeClass(sale: Sale): string {
    if (sale.outboxState === 'synced') return 'badge-ok';
    if (sale.outboxState === 'rejected') return 'badge-danger';
    return 'badge-warn';
  }
}
