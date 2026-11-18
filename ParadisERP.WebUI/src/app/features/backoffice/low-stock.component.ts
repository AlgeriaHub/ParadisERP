import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminService, adminErrorMessage } from '../../core/admin.service';
import { BranchLock } from '../../core/branch-lock';
import { I18nService } from '../../core/i18n.service';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { TableStateComponent } from '../../ui/table-state.component';
import { MultilingualName, localizedName } from './backoffice-format';
import { FinancePagesApi } from './finance/finance-pages.api';

/**
 * Inventory › Low stock (REDESIGN_PLAN.md §3.2, [extracted]). The same list and the same branch
 * rule as the panel it replaces at the foot of the sales report — moved here because it is a stock
 * question, not a sales one. Only the period filter is gone: the list never read it. Read a page at
 * a time (`GET /api/catalog/products/low-stock/page`, owner 2026-11-17).
 */
@Component({
  selector: 'app-backoffice-low-stock',
  standalone: true,
  imports: [FormsModule, ListFooterComponent, TableStateComponent],
  template: `
    <div class="bo-stack">
      @if (problem()) {
        <p class="notice danger" role="alert">{{ problem() }}</p>
      }
      <section class="bo-panel">
        <div class="bo-panel__head">
          <p class="bo-panel__note">{{ i18n.t('reports.lowStockHint') }}</p>
        </div>
        <div class="bo-panel__body bo-form">
          <div class="bo-fields bo-fields--roomy">
            @if (lock.locked()) {
              <p class="bo-scope-note">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
            } @else {
              <div class="bo-field">
                <label for="ls-branch">{{ i18n.t('backoffice.tabs.branches') }}</label>
                <select id="ls-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
                  <option [ngValue]="null">{{ i18n.t('reports.allBranches') }}</option>
                  @for (b of branches(); track b.id) {
                    <option [ngValue]="b.id">{{ name(b) }}</option>
                  }
                </select>
              </div>
            }
            <!-- The list's own filter (owner, 2026-11-17): applied by the server over every row. -->
            @if (anyRows()) {
              <div class="bo-field">
                <label for="ls-f-stock">{{ i18n.t('backoffice.products.stock') }}</label>
                <select id="ls-f-stock" [ngModel]="stockFilter()" (ngModelChange)="stockFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="out">{{ i18n.t('lowStock.filter.out') }}</option>
                  <option ngValue="low">{{ i18n.t('lowStock.filter.low') }}</option>
                </select>
              </div>
            }
            @if (filtered()) {
              <div class="clear">
                <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
              </div>
            }
          </div>
        </div>
        @if (!pager.loaded()) {
          <p class="bo-loading" role="status">
            <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
            <span class="bo-loading__rows" aria-hidden="true"></span>
          </p>
        } @else if (!anyRows()) {
          <p class="bo-panel__body bo-empty-note">{{ i18n.t('common.nothingHere') }}</p>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th>{{ i18n.t('backoffice.products.sku') }}</th>
                  <th>{{ i18n.t('receipt.item') }}</th>
                  <th>{{ i18n.t('backoffice.tabs.branches') }}</th>
                  <th class="num">{{ i18n.t('backoffice.products.stock') }}</th>
                  <th class="num">{{ i18n.t('backoffice.products.reorderLevel') }}</th>
                </tr>
              </thead>
              <tbody>
                @for (row of pager.rows(); track row.productId + row.branchId) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('backoffice.products.sku')">{{ row.sku }}</td>
                    <td [attr.data-label]="i18n.t('receipt.item')">{{ name(row) }}</td>
                    <td [attr.data-label]="i18n.t('backoffice.tabs.branches')">{{ row.branchName }}</td>
                    <td
                      class="num mono bo-cell-strong"
                      [class.out]="row.stockQty <= 0"
                      [attr.data-label]="i18n.t('backoffice.products.stock')"
                    >
                      {{ row.stockQty }}
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('backoffice.products.reorderLevel')">{{ row.reorderLevel }}</td>
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
  styleUrls: ['./backoffice-ui.scss'],
  styles: [
    `
      .bo-empty-note {
        margin: 0;
        color: var(--muted);
      }
      /* Beside the fields, on their baseline. */
      .clear {
        align-self: end;
        justify-self: start;
      }
      /* Out of stock is said in red as well as by the figure itself. */
      .out {
        color: var(--danger);
      }
    `,
  ],
})
export class BackofficeLowStockComponent implements OnInit {
  private readonly admin = inject(AdminService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);

  private readonly pages = inject(FinancePagesApi);

  readonly branches = signal<AdminBranch[]>([]);
  readonly branchId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  /** Whether the branch is known: a locked user's is read before the first page is asked. */
  private readonly started = signal(false);

  /** The list's filter (owner, 2026-11-17): out of stock (zero or less), or low but still in stock. */
  readonly stockFilter = signal<'all' | 'out' | 'low'>('all');
  readonly filtered = computed(() => this.stockFilter() !== 'all');
  /**
   * The list a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"): the branch and
   * the filter run there over every row; a new branch or filter starts at page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.lowStockPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'inventory.lowStock',
      filters: () => ({ branchId: this.branchId(), stock: this.stockFilter() }),
      enabled: () => this.started(),
    },
  );
  /** Whether the branch has anything low at all, before the filter. */
  readonly anyRows = computed(() => (this.pager.summary()?.all ?? 0) > 0);
  /** What went wrong, in words: the branches, or the list. */
  readonly problem = computed(() => {
    const e = this.pager.error();
    return this.error() ?? (e ? adminErrorMessage(e) : null);
  });

  clearFilters(): void {
    this.stockFilter.set('all');
  }

  /** The endpoint serves the French name only, so a row falls through to it (as on the report). */
  name(record: Partial<MultilingualName>): string {
    return localizedName(record, this.i18n.lang());
  }

  async ngOnInit(): Promise<void> {
    try {
      this.branches.set(await this.admin.listBranches());
      // A non-admin's list is their branch's; the server answers that whatever is asked.
      this.branchId.set(this.lock.lockedBranchId());
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
    this.started.set(true);
  }

  /** A new branch is a new page one: the pager asks for it. */
  setBranch(value: string | null): void {
    this.branchId.set(value || null);
  }

  /** Asks again for the page on screen. */
  async run(): Promise<void> {
    await this.pager.reload();
  }
}
