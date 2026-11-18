import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../core/i18n.service';
import { LIST_FILTERS_TRANSLATIONS } from '../../core/i18n/translations-list-filters';
import { LIST_FILTERS_INVENTORY_TRANSLATIONS } from '../../core/i18n/translations-list-filters-inventory';
import { Product, soldAtTill } from '../../core/models';
import { productName } from '../../core/money';
import { PosService } from '../../core/pos.service';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createPager } from '../../ui/pager';

/**
 * Read-only catalog view. Editing happens in the back office and syncs down, which is the
 * rule that removes catalog merge conflicts between terminals.
 */
@Component({
  selector: 'app-products',
  standalone: true,
  imports: [FormsModule, ListFooterComponent],
  template: `
    <div class="wrap">
      <header class="head">
        <div class="head-text">
          <h1>{{ i18n.t('products.title') }}</h1>
          <p class="note">{{ i18n.t('products.readOnly') }}</p>
        </div>
        <div class="search-row">
          <input
            type="search"
            class="search"
            [placeholder]="i18n.t('products.search')"
            [attr.aria-label]="i18n.t('products.search')"
            [value]="term()"
            (input)="term.set($any($event.target).value)"
            autocomplete="off"
            spellcheck="false"
          />
          @if (term()) {
            <button
              type="button"
              class="search-clear"
              [attr.aria-label]="i18n.t('register.clearSearch')"
              (click)="term.set('')"
            >
              ×
            </button>
          }
        </div>
      </header>

      <!-- More filters (owner, 2026-11-17), over the catalogue already on this device; none is a request. -->
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="tp-f-stock">{{ i18n.t('products.stock') }}</label>
          <select id="tp-f-stock" [ngModel]="stockFilter()" (ngModelChange)="stockFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="low">{{ i18n.t('products.lowStock') }}</option>
            <option ngValue="ok">{{ i18n.t('products.filter.stockOk') }}</option>
            <option ngValue="untracked">{{ i18n.t('products.filter.untracked') }}</option>
          </select>
        </div>
        @if (filtered()) {
          <div class="ui-toolbar__end">
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          </div>
        }
      </div>

      <div class="card table-wrap table-cards">
        <table>
          <thead>
            <tr>
              <th>{{ i18n.t('products.sku') }}</th>
              <th>{{ i18n.t('receipt.item') }}</th>
              <th class="num">{{ i18n.taxName() }}</th>
              <th class="num">{{ i18n.t('products.price') }}</th>
              <th class="num">{{ i18n.t('products.stock') }}</th>
            </tr>
          </thead>
          <tbody>
            @for (p of pager.rows(); track p.id) {
              <tr [class.low]="isLow(p)">
                <td class="mono" [attr.data-label]="i18n.t('products.sku')">{{ p.sku }}</td>
                <td class="name-cell" [attr.data-label]="i18n.t('receipt.item')">
                  <span>{{ name(p) }}</span>
                  @if (isLow(p)) {
                    <span class="badge badge-warn">{{ i18n.t('products.lowStock') }}</span>
                  }
                </td>
                <td class="num mono" [attr.data-label]="i18n.taxName()">{{ p.taxRate }}%</td>
                <td class="num mono price" [attr.data-label]="i18n.t('products.price')">{{ i18n.money(p.price) }}</td>
                <td class="num mono" [attr.data-label]="i18n.t('products.stock')">{{ p.trackStock ? p.stockQty : '—' }}</td>
              </tr>
            } @empty {
              <tr class="empty-row">
                <td colspan="5" class="center">
                  <!-- Three different nothings, told apart. The catalog is read from this
                       device, so without the first one the screen reports "no products" in
                       the instant before there are some. -->
                  @if (!ready()) {
                    <strong>{{ i18n.t('common.loading') }}</strong>
                  } @else if (filtered() && !onlySearch()) {
                    <strong>{{ i18n.t('ui.state.noResults') }}</strong>
                    <button type="button" class="btn-ghost clear-key" (click)="clearFilters()">
                      {{ i18n.t('ui.state.clearFilters') }}
                    </button>
                  } @else if (term()) {
                    <strong>{{ i18n.t('register.noResults') }}</strong>
                    <button type="button" class="btn-ghost clear-key" (click)="term.set('')">
                      {{ i18n.t('register.clearSearch') }}
                    </button>
                  } @else {
                    <strong>{{ i18n.t('register.catalogEmpty') }}</strong>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      <app-list-footer [pager]="pager" [loading]="!ready()" />
    </div>
  `,
  styleUrls: ['../../ui/ui-patterns.scss'],
  styles: [
    `
      .wrap {
        display: grid;
        gap: var(--sp-5);
      }

      .head {
        display: flex;
        align-items: end;
        justify-content: space-between;
        gap: var(--sp-5);
        flex-wrap: wrap;

        h1 {
          font-size: var(--fs-lg);
          margin: 0;
          letter-spacing: -0.01em;
        }
      }

      .head-text {
        min-width: 0;
      }

      /* Secondary text stays inside a panel elsewhere; here it sits on the page ground, so
         it takes --muted at its corrected 6.8:1 rather than a lighter grey. */
      .note {
        margin: var(--sp-1) 0 0;
        font-size: var(--fs-sm);
        color: var(--muted);
        max-width: 48ch;
        line-height: 1.45;
      }

      /* The same field, and the same clear key, as the till's catalog search. */
      .search-row {
        position: relative;
        width: min(22rem, 100%);
      }

      .search {
        min-height: var(--control-h);
        padding-inline: var(--sp-4);
        padding-inline-end: 3rem;
        background: var(--surface-2);
      }

      .search:focus {
        background: var(--surface);
      }

      .search::-webkit-search-cancel-button {
        -webkit-appearance: none;
        appearance: none;
      }

      .search-clear {
        position: absolute;
        inset-inline-end: var(--sp-2);
        top: 50%;
        transform: translateY(-50%);
        display: grid;
        place-items: center;
        width: var(--control-h-sm);
        height: var(--control-h-sm);
        border: none;
        border-radius: var(--radius-pill);
        background: transparent;
        color: var(--muted);
        font-size: var(--fs-lg);
        line-height: 1;
        padding: 0;

        &:hover {
          background: var(--border);
          color: var(--text);
        }
      }

      .table-wrap {
        overflow-x: auto;
        background: var(--surface);
        border: var(--line) solid var(--border-strong);
        border-radius: var(--radius-card);
        box-shadow: var(--shadow);
      }

      /* The header row stays put while a long catalog scrolls under it. */
      thead th {
        position: sticky;
        top: 0;
        z-index: 1;
      }

      .name-cell {
        font-weight: 500;
      }

      .badge {
        margin-inline-start: var(--sp-2);
      }

      .price {
        font-weight: 600;
        color: var(--primary-dark);
      }

      .center {
        text-align: center;
        padding: var(--sp-7) 0;
        color: var(--muted);
      }

      .center strong {
        display: block;
        font-size: var(--fs-md);
        line-height: 1.4;
        color: var(--text);
      }

      .clear-key {
        min-height: var(--control-h-sm);
        margin-block-start: var(--sp-4);
      }

      @media (max-width: 720px) {
        .empty-row {
          border: none;
        }

        .empty-row td {
          justify-content: center;
        }

        .name-cell {
          font-weight: 600;
        }
      }
    `,
  ],
})
export class ProductsComponent implements OnInit {
  readonly pos = inject(PosService);
  readonly i18n = inject(I18nService);
  /**
   * The filter words. The till does not load the back office's dictionary, so the shared list
   * words and this group's own are registered here (a second registration changes nothing).
   */
  private readonly words =
    this.i18n.extend(LIST_FILTERS_TRANSLATIONS) + this.i18n.extend(LIST_FILTERS_INVENTORY_TRANSLATIONS);

  readonly term = signal('');
  /** False until the local catalog has been read, so an empty table is never called empty early. */
  readonly ready = signal(false);

  // More filters (owner, 2026-11-17), over the catalogue already on this device; none is a request.
  readonly stockFilter = signal<'all' | 'low' | 'ok' | 'untracked'>('all');

  /** The till's price list: what the counter sells, not the workshop's materials (manufacturing, Phase 1). */
  private readonly sold = computed(() => this.pos.products().filter(soldAtTill));


  /** True when any filter, the search included, narrows the list. */
  readonly filtered = computed(() => !!this.term().trim() || this.stockFilter() !== 'all');
  /** Only the search narrows it: the empty row keeps its own words for that. */
  readonly onlySearch = computed(() => this.stockFilter() === 'all');

  readonly shown = computed(() => {
    const q = this.term().trim().toLowerCase();
    const stock = this.stockFilter();
    return this.sold().filter(
      (p) =>
        (!q ||
          p.sku.toLowerCase().includes(q) ||
          (p.barcode ?? '').includes(q) ||
          p.nameFr.toLowerCase().includes(q) ||
          p.nameEn.toLowerCase().includes(q) ||
          p.nameAr.includes(q)) &&
        (stock === 'all' ||
          (stock === 'untracked' ? !p.trackStock : p.trackStock && this.isLow(p) === (stock === 'low')))
    );
  });

  /** Ten rows to begin with, 20, 50 or 100 at the user's choice (owner, 2026-11-17); a new filter starts at page one. */
  readonly pager = createPager(() => this.shown(), {
    key: 'till.products',
    resetOn: () => [this.term(), this.stockFilter()],
  });

  clearFilters(): void {
    this.term.set('');
    this.stockFilter.set('all');
  }

  async ngOnInit(): Promise<void> {
    try {
      await this.pos.loadCatalog();
    } finally {
      this.ready.set(true);
    }
  }

  name(product: Product): string {
    return productName(product, this.i18n.lang());
  }

  isLow(product: Product): boolean {
    return product.trackStock && product.stockQty <= product.reorderLevel;
  }
}
