import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import {
  AdminBranch,
  AdminProduct,
  AdminService,
  BranchAvailability,
  ProductStockRow,
  UpsertProductRequest,
  adminErrorMessage,
} from '../../core/admin.service';
import { ApiService } from '../../core/api.service';
import { CatalogStore } from '../../core/catalog-store';
import { BranchLock } from '../../core/branch-lock';
import { I18nService } from '../../core/i18n.service';
import { fold } from '../../core/product-search';
import { RegionalService } from '../../core/regional.service';
import { ConfirmService } from '../../ui/confirm.service';
import { actionRequest, deleteRequest, saveRequest } from '../../ui/confirm-requests';
import { DialogComponent } from '../../ui/dialog.component';
import { ErrorSummaryComponent, FieldError } from '../../ui/error-summary.component';
import { FormSectionComponent } from '../../ui/form-section.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { MENU } from '../../ui/menu.component';
import { createServerPager } from '../../ui/server-pager';
import { pageQueryString } from '../../core/paging';
import { StatusComponent } from '../../ui/status.component';
import { toneOf } from '../../ui/status-tones';
import { TableStateComponent } from '../../ui/table-state.component';
import { localizedName } from './backoffice-format';
import { TouchedFields } from './backoffice-forms';
import { PAGE_CHROME, PageChrome } from './shell/page-chrome';

type StatusFilter = 'all' | 'active' | 'inactive';
type SortKey = 'sku' | 'name' | 'price';
type NameField = 'nameFr' | 'nameAr' | 'nameEn';

/**
 * Inventory › Products — the reference list screen of the redesign (REDESIGN_PLAN.md §10, Phase 3).
 *
 * What it shows is the catalogue the session already holds (`CatalogStore`), so search, filters,
 * sorting and paging are all over rows in memory and never a request. What it does is unchanged:
 * the same create, update, delete, per-branch stock adjustment and transfer calls, the same
 * required fields, the same rules for a rentable item.
 *
 * - **Rows are data.** The name opens the editor; the stock cell opens the per-branch stock; the
 *   rest is in the ⋯ menu, with Delete last, in red only there, and confirmed.
 * - **No type:** every item is rented and sold, from one stock (owner, 2026-10-06), so the list
 *   has no type column or filter and the card no rental box.
 * - **No stock figure in the list:** the product list carries none — stock is per branch and is
 *   read one product at a time — so the cell opens that view instead of inventing a number.
 */
@Component({
  selector: 'app-backoffice-products',
  standalone: true,
  imports: [
    FormsModule,
    ...PAGE_CHROME,
    ...MENU,
    DialogComponent,
    ErrorSummaryComponent,
    FormSectionComponent,
    ListFooterComponent,
    StatusComponent,
    TableStateComponent,
  ],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-primary" (click)="openCreate()">
        <span aria-hidden="true">+</span> {{ i18n.t('backoffice.products.new') }}
      </button>
    </ng-template>

    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter ui-filter--search">
          <label for="pr-q">{{ i18n.t('products.search') }}</label>
          <input id="pr-q" type="search" [placeholder]="i18n.t('products.searchHint')" [ngModel]="query()" (ngModelChange)="setQuery($event)" />
        </div>
        <div class="ui-filter">
          <label for="pr-f-state">{{ i18n.t('common.state') }}</label>
          <select id="pr-f-state" [ngModel]="statusFilter()" (ngModelChange)="setFilter(statusFilter, $event)">
            <option ngValue="all">{{ i18n.t('products.filter.all') }}</option>
            <option ngValue="active">{{ i18n.t('common.active') }}</option>
            <option ngValue="inactive">{{ i18n.t('common.inactive') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="pr-f-stock">{{ i18n.t('backoffice.products.stock') }}</label>
          <select id="pr-f-stock" [ngModel]="stockFilter()" (ngModelChange)="setFilter(stockFilter, $event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="tracked">{{ i18n.t('products.filter.tracked') }}</option>
            <option ngValue="untracked">{{ i18n.t('products.filter.untracked') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="pr-f-barcode">{{ i18n.t('backoffice.products.barcode') }}</label>
          <select id="pr-f-barcode" [ngModel]="barcodeFilter()" (ngModelChange)="setFilter(barcodeFilter, $event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
            <option ngValue="no">{{ i18n.t('filters.no') }}</option>
          </select>
        </div>
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="busy()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() && pager.total()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <section class="ui-surface" [attr.aria-busy]="!pager.loaded()">
        @if (!pager.loaded() && !listError()) {
          <app-table-state kind="loading" />
        } @else if (listError() && !pager.total()) {
          <app-table-state kind="error" [message]="listError()" (retry)="reload()" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('backoffice.products.none')">
            <button type="button" class="btn-primary" (click)="openCreate()">{{ i18n.t('backoffice.products.new') }}</button>
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th [attr.aria-sort]="ariaSort('sku')">
                    <button type="button" class="ui-sort" (click)="sortBy('sku')">
                      {{ i18n.t('backoffice.products.sku') }}<span class="ui-sort__glyph" aria-hidden="true">{{ sortGlyph('sku') }}</span>
                    </button>
                  </th>
                  <th [attr.aria-sort]="ariaSort('name')">
                    <button type="button" class="ui-sort" (click)="sortBy('name')">
                      {{ i18n.t('receipt.item') }}<span class="ui-sort__glyph" aria-hidden="true">{{ sortGlyph('name') }}</span>
                    </button>
                  </th>
                  <th class="num" [attr.aria-sort]="ariaSort('price')">
                    <button type="button" class="ui-sort" (click)="sortBy('price')">
                      {{ i18n.t('backoffice.products.price') }}<span class="ui-sort__glyph" aria-hidden="true">{{ sortGlyph('price') }}</span>
                    </button>
                  </th>
                  <th>{{ i18n.t('backoffice.products.stock') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (p of pager.rows(); track p.id) {
                  <tr [class.is-muted]="!p.isActive">
                    <td class="mono" [attr.data-label]="i18n.t('backoffice.products.sku')">{{ p.sku }}</td>
                    <td [attr.data-label]="i18n.t('receipt.item')">
                      <button type="button" class="ui-link" (click)="edit(p)">{{ name(p) }}</button>
                      @if (p.barcode) {
                        <span class="ui-sub mono">{{ p.barcode }}</span>
                      }
                    </td>
                    <td class="num mono price" [attr.data-label]="i18n.t('backoffice.products.price')">
                      {{ i18n.money(p.price) }}
                    </td>
                    <td [attr.data-label]="i18n.t('backoffice.products.stock')">
                      @if (p.trackStock) {
                        <button type="button" class="ui-link stock-link" (click)="openStock(p)">{{ i18n.t('products.stock.perBranch') }}</button>
                      } @else {
                        <span class="ui-sub">{{ i18n.t('products.stock.untracked') }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(p)">{{ i18n.t(p.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                    </td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + name(p)">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="edit(p)">{{ i18n.t('common.edit') }}</button>
                        @if (p.trackStock) {
                          <button type="button" appMenuItem (click)="openStock(p)">{{ i18n.t('products.menu.stock') }}</button>
                        }
                        <button type="button" appMenuItem (click)="openRentalSettings()">{{ i18n.t('products.menu.rentalSettings') }}</button>
                        <hr appMenuSeparator />
                        <button type="button" appMenuItem class="is-danger" (click)="remove(p)">{{ i18n.t('common.delete') }}</button>
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

    <!-- The editor: one dialog, the same fields as before, grouped. It never closes on its own;
         a dismissal with unsaved input asks first. -->
    <app-dialog
      [open]="editorOpen()"
      size="md"
      [heading]="editingId() ? i18n.t('common.edit') : i18n.t('backoffice.products.new')"
      (dismiss)="dismissEditor()"
    >
      @if (editorOpen()) {
        <div class="editor bo-form">
          @if (formErrors().length) {
            <app-error-summary [errors]="formErrors()" />
          }
          @if (error()) {
            <p class="notice danger" role="alert">{{ error() }}</p>
          }

          <app-form-section [heading]="i18n.t('common.group.identity')" [columns]="3">
            <div class="bo-field" [class.is-invalid]="touched.missing('sku', form.sku)">
              <label for="pr-sku">
                {{ i18n.t('backoffice.products.sku') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
              </label>
              <input
                id="pr-sku" class="mono" autofocus [(ngModel)]="form.sku"
                [attr.aria-invalid]="touched.missing('sku', form.sku) ? 'true' : null"
                [attr.aria-describedby]="touched.missing('sku', form.sku) ? 'pr-sku-error' : null"
                (blur)="touched.touch('sku')"
              />
              @if (touched.missing('sku', form.sku)) {
                <span id="pr-sku-error" class="bo-error">{{ i18n.t('common.fieldRequired') }}</span>
              }
            </div>
            <div class="bo-field">
              <label for="pr-barcode">
                {{ i18n.t('backoffice.products.barcode') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
              </label>
              <input id="pr-barcode" class="mono" [(ngModel)]="form.barcode" />
            </div>
            <div class="bo-field">
              <label for="pr-unit">{{ i18n.t('backoffice.products.unit') }}</label>
              <input id="pr-unit" [(ngModel)]="form.unit" />
            </div>
          </app-form-section>

          <!-- The reader's own language first; French stays the required one, as before. -->
          <app-form-section [heading]="i18n.t('common.group.names')" [columns]="3">
            @for (f of nameOrder(); track f) {
              @switch (f) {
                @case ('nameFr') {
                  <div class="bo-field" [class.is-invalid]="touched.missing('nameFr', form.nameFr)">
                    <label for="pr-fr">
                      {{ i18n.t('field.nameFr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <input
                      id="pr-fr" dir="ltr" [(ngModel)]="form.nameFr"
                      [attr.aria-invalid]="touched.missing('nameFr', form.nameFr) ? 'true' : null"
                      [attr.aria-describedby]="touched.missing('nameFr', form.nameFr) ? 'pr-fr-error' : null"
                      (blur)="touched.touch('nameFr')"
                    />
                    @if (touched.missing('nameFr', form.nameFr)) {
                      <span id="pr-fr-error" class="bo-error">{{ i18n.t('common.fieldRequired') }}</span>
                    }
                  </div>
                }
                @case ('nameAr') {
                  <div class="bo-field">
                    <label for="pr-ar">{{ i18n.t('field.nameAr') }}</label>
                    <input id="pr-ar" [(ngModel)]="form.nameAr" dir="rtl" />
                  </div>
                }
                @case ('nameEn') {
                  <div class="bo-field">
                    <label for="pr-en">{{ i18n.t('field.nameEn') }}</label>
                    <input id="pr-en" dir="ltr" [(ngModel)]="form.nameEn" />
                  </div>
                }
              }
            }
          </app-form-section>

          <app-form-section [heading]="i18n.t('common.group.pricing')" [columns]="3">
            <div class="bo-field">
              <label for="pr-price">{{ i18n.t('backoffice.products.price') }}</label>
              <input id="pr-price" type="number" min="0" [step]="regional.moneyStep()" class="num mono" [(ngModel)]="form.price" />
            </div>
            <div class="bo-field">
              <label for="pr-cost">{{ i18n.t('backoffice.products.costPrice') }}</label>
              <input id="pr-cost" type="number" min="0" [step]="regional.moneyStep()" class="num mono" [(ngModel)]="form.costPrice" />
            </div>
            <div class="bo-field">
              <label for="pr-tax">{{ i18n.t('backoffice.products.taxRate') }}</label>
              <select id="pr-tax" [(ngModel)]="form.taxRate">
                @for (rate of taxChoices(); track rate) {
                  <option [ngValue]="rate">
                    {{ rate }}%{{ i18n.rateLabel(rate) ? ' · ' + i18n.rateLabel(rate) : ''
                    }}{{ isRetired(rate) ? ' — ' + i18n.t('backoffice.products.rateRetired') : '' }}
                  </option>
                }
              </select>
            </div>
          </app-form-section>

          <app-form-section [heading]="i18n.t('products.section.catalogue')" [columns]="2">
            <div class="checks">
              <div class="bo-check">
                <input id="pr-track" type="checkbox" [(ngModel)]="form.trackStock" />
                <label for="pr-track">{{ i18n.t('backoffice.products.trackStock') }}</label>
              </div>
              <div class="bo-check">
                <input id="pr-active" type="checkbox" [(ngModel)]="form.isActive" />
                <label for="pr-active">{{ i18n.t('common.active') }}</label>
              </div>
            </div>
          </app-form-section>

          <!-- No rental box (owner, 2026-10-06): every item is rented and sold, from one stock. -->
          <app-form-section [heading]="i18n.t('backoffice.products.assetValue')" [columns]="2">
            <div class="bo-field">
              <label for="pr-asset">{{ i18n.t('backoffice.products.assetValue') }}</label>
              <input id="pr-asset" type="number" min="0" [step]="regional.moneyStep()" class="num mono" [(ngModel)]="form.assetUnitValue" />
              <p class="bo-hint">{{ i18n.t('backoffice.products.assetValueHint') }}</p>
            </div>
          </app-form-section>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="dismissEditor()">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">{{ i18n.t('common.save') }}</button>
      </div>
    </app-dialog>

    <!-- Per-branch stock: the same adjustments and transfer as before, in the shared dialog. -->
    <app-dialog
      [open]="!!stockTarget()"
      size="lg"
      [heading]="stockTarget() ? i18n.t('backoffice.products.stock') + ' — ' + name(stockTarget()!) : ''"
      (dismiss)="closeStock()"
    >
      @if (stockTarget(); as target) {
        <div class="bo-stack">
          @if (stockError()) {
            <p class="notice danger" role="alert">{{ stockError() }}</p>
          }
          @if (stockDone()) {
            <p class="notice ok" role="status">{{ stockDone() }}</p>
          }

          <div class="ui-surface ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('backoffice.tabs.branches') }}</th>
                  <th class="num">{{ i18n.t('backoffice.products.stock') }}</th>
                  <th class="num">{{ i18n.t('backoffice.products.reorderLevel') }}</th>
                  <th class="num">{{ i18n.t('backoffice.products.stockDelta') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (row of stockRows(); track row.branchId) {
                  <tr>
                    <td [attr.data-label]="i18n.t('backoffice.tabs.branches')">{{ row.branchName }}</td>
                    <td class="num mono figure" [attr.data-label]="i18n.t('backoffice.products.stock')">{{ row.stockQty }}</td>
                    <td class="num" [attr.data-label]="i18n.t('backoffice.products.reorderLevel')">
                      <input
                        type="number" min="0" step="1" class="num mono narrow"
                        [attr.aria-label]="i18n.t('backoffice.products.reorderLevel') + ' — ' + row.branchName"
                        [ngModel]="row.reorderLevel"
                        (ngModelChange)="row.reorderLevel = $event"
                      />
                    </td>
                    <td class="num" [attr.data-label]="i18n.t('backoffice.products.stockDelta')">
                      <input
                        type="number" step="1" class="num mono narrow"
                        [attr.aria-label]="i18n.t('backoffice.products.stockDelta') + ' — ' + row.branchName"
                        [(ngModel)]="deltas[row.branchId]"
                      />
                    </td>
                    <td class="ui-cell-menu">
                      <button type="button" class="btn-ghost" (click)="applyRow(target.id, row)">{{ i18n.t('backoffice.products.apply') }}</button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          @if (lock.locked() && lock.mayLookUpOtherBranches()) {
            <app-form-section [heading]="i18n.t('branchLock.otherAvailability')" [note]="i18n.t('branchLock.otherAvailabilityHint')" [columns]="1">
              @if (otherAvailability().length) {
                <div class="ui-surface ui-scroll">
                  <table class="ui-table">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('branchLock.branch') }}</th>
                        <th class="num">{{ i18n.t('branchLock.available') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (row of otherAvailability(); track row.branchId) {
                        <tr>
                          <td>{{ name(row) }}</td>
                          <td class="num mono figure">{{ row.available }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              } @else {
                <p class="bo-hint">{{ i18n.t('branchLock.otherAvailabilityNone') }}</p>
              }
            </app-form-section>
          }

          <div class="bo-form">
            <app-form-section [heading]="i18n.t('backoffice.products.transfer')" [columns]="3">
              @if (lock.locked()) {
                <!-- A non-admin moves goods out of their own branch; where they go stays a
                     choice, because it is a property of the move and it must be elsewhere. -->
                <div class="bo-field">
                  <p class="bo-fixed-label">{{ i18n.t('backoffice.products.fromBranch') }}</p>
                  <p class="bo-fixed" id="pr-from-fixed">{{ lock.ownBranchName(branches()) }}</p>
                </div>
              } @else {
                <div class="bo-field">
                  <label for="pr-from">{{ i18n.t('backoffice.products.fromBranch') }}</label>
                  <select id="pr-from" [(ngModel)]="transferFrom">
                    @for (row of stockRows(); track row.branchId) {
                      <option [ngValue]="row.branchId">{{ row.branchName }} ({{ row.stockQty }})</option>
                    }
                  </select>
                </div>
              }
              <div class="bo-field">
                <label for="pr-to">{{ i18n.t('backoffice.products.toBranch') }}</label>
                <select id="pr-to" [(ngModel)]="transferTo">
                  @for (b of transferTargets(); track b.id) {
                    <option [ngValue]="b.id">{{ name(b) }}</option>
                  }
                </select>
              </div>
              <div class="bo-field">
                <label for="pr-qty">{{ i18n.t('register.qty') }}</label>
                <input id="pr-qty" type="number" min="0" step="1" class="num mono" [(ngModel)]="transferQty" />
              </div>
            </app-form-section>
          </div>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="closeStock()">{{ i18n.t('common.close') }}</button>
        <button
          type="button"
          class="btn-primary"
          [disabled]="!transferFrom || !transferTo || transferFrom === transferTo || transferQty <= 0"
          (click)="transfer(stockTarget()!.id)"
        >
          {{ i18n.t('backoffice.products.transfer') }}
        </button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['./backoffice-ui.scss', '../../ui/ui-patterns.scss'],
  styles: [
    `
      .price,
      .figure {
        font-weight: 700;
      }
      .stock-link {
        font-weight: 500;
      }
      .editor {
        display: grid;
        gap: var(--sp-3);
      }
      .checks {
        display: grid;
        gap: var(--sp-2);
        align-content: end;
      }
      .narrow {
        inline-size: 6.5rem;
        min-block-size: var(--control-h-sm);
      }
    `,
  ],
})
export class BackofficeProductsComponent implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute, { optional: true });
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  readonly regional = inject(RegionalService);

  /** Shared with every other screen; edits below go through it so none of them lags. */
  private readonly catalog = inject(CatalogStore);
  readonly products = this.catalog.products;
  readonly branches = signal<AdminBranch[]>([]);
  /** Where a transfer may go: any branch for the Admin; any branch but their own for anyone else. */
  readonly transferTargets = computed(() => {
    const own = this.lock.lockedBranchId();
    return own === null ? this.branches() : this.branches().filter((b) => b.id !== own);
  });
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly editingId = signal<string | null>(null);
  readonly editorOpen = signal(false);

  /** A catalogue or branch record named in the reader's language, not always in French. */
  name(record: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(record, this.i18n.lang());
  }

  /** Which required fields have earned the right to complain. */
  readonly touched = new TouchedFields();

  // ---------- filters, sort, paging: all over the catalogue in memory ----------

  // No category filter (owner, 2026-10-12): categories are no longer used. A product keeps
  // whatever category it had stored; the screens neither show nor edit it.
  readonly query = signal('');
  readonly statusFilter = signal<StatusFilter>('all');
  // More filters (owner, 2026-11-17), over fields each product already carries; none is a request.
  readonly stockFilter = signal<'all' | 'tracked' | 'untracked'>('all');
  readonly barcodeFilter = signal<'all' | 'yes' | 'no'>('all');
  readonly sort = signal<{ key: SortKey; dir: 1 | -1 } | null>(null);

  readonly filtered = computed(
    () =>
      !!this.query().trim() ||
      this.statusFilter() !== 'all' ||
      this.stockFilter() !== 'all' ||
      this.barcodeFilter() !== 'all',
  );

  /**
   * The products list a page at a time from the server (owner, 2026-11-17: "paginate in backend,
   * not frontend"): the search (the till's fold), the filters and the sort run there, over the
   * whole catalogue; only the page comes back. A filter or a sort goes back to page one.
   */
  readonly pager = createServerPager<AdminProduct, Record<string, string | number | null>>(
    (page) =>
      this.api.getJson(`/api/catalog/products/page${pageQueryString({ skip: page.skip, take: page.take, ...page.filters })}`),
    {
      key: 'products',
      filters: () => ({
        q: this.query().trim(),
        status: this.statusFilter(),
        stock: this.stockFilter(),
        barcode: this.barcodeFilter(),
        sort: this.sort()?.key ?? null,
        dir: this.sort() ? (this.sort()!.dir === 1 ? 'asc' : 'desc') : null,
        lang: this.i18n.lang(),
      }),
    },
  );

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  /** A new filter starts at its first page; page three of the old list means nothing now. */
  setQuery(value: string): void {
    this.query.set(value);
  }

  setFilter<T>(filter: { set(value: T): void }, value: T): void {
    filter.set(value);
  }

  clearFilters(): void {
    this.query.set('');
    this.statusFilter.set('all');
    this.stockFilter.set('all');
    this.barcodeFilter.set('all');
  }

  sortBy(key: SortKey): void {
    const now = this.sort();
    this.sort.set(now?.key === key ? (now.dir === 1 ? { key, dir: -1 } : null) : { key, dir: 1 });
  }

  ariaSort(key: SortKey): 'ascending' | 'descending' | 'none' {
    const now = this.sort();
    return now?.key === key ? (now.dir === 1 ? 'ascending' : 'descending') : 'none';
  }

  sortGlyph(key: SortKey): string {
    const now = this.sort();
    return now?.key === key ? (now.dir === 1 ? '▲' : '▼') : '';
  }

  tone(p: AdminProduct) {
    return toneOf('active', p.isActive ? 'Active' : 'Inactive');
  }

  // ---------- stock dialog state ----------

  readonly stockTarget = signal<AdminProduct | null>(null);
  readonly stockRows = signal<ProductStockRow[]>([]);
  /**
   * The other branches' available quantity, for a non-admin holding the permission — read-only,
   * beside the rows of their own branch they adjust. Empty for everyone else.
   */
  readonly otherAvailability = signal<BranchAvailability[]>([]);
  readonly stockError = signal<string | null>(null);
  readonly stockDone = signal<string | null>(null);
  deltas: Record<string, number> = {};
  transferFrom = '';
  transferTo = '';
  transferQty = 0;

  form: UpsertProductRequest = this.emptyForm();
  /** The form as it was opened, so a dismissal knows whether anything would be lost. */
  private opened = '';
  readonly formErrors = signal<FieldError[]>([]);

  /** The reader's own language first in the names group; the order changes nothing else. */
  readonly nameOrder = computed<NameField[]>(() => {
    const lang = this.i18n.lang();
    const first: NameField = lang === 'ar' ? 'nameAr' : lang === 'en' ? 'nameEn' : 'nameFr';
    return [first, ...(['nameFr', 'nameAr', 'nameEn'] as NameField[]).filter((f) => f !== first)];
  });

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('products.lead'));
    await this.reload();
    // `?product=<id>` opens that product's editor — how a report sends the reader to correct one
    // (Sales without a cost, SAL-02). An id not in the catalogue opens nothing.
    const wanted = this.route?.snapshot.queryParamMap.get('product');
    const product = wanted ? await this.productById(wanted) : null;
    if (product) this.edit(product);
  }

  private emptyForm(): UpsertProductRequest {
    return {
      sku: '',
      barcode: null,
      nameAr: '',
      nameFr: '',
      nameEn: '',
      categoryId: null,
      price: 0,
      costPrice: 0,
      taxRate: this.regional.settings().defaultTaxRate,
      unit: 'pcs',
      trackStock: true,
      isActive: true,
      isRentable: true,
      assetUnitValue: 0,
    };
  }

  /** One product as the server holds it, whatever page it is on. */
  private async productById(id: string): Promise<AdminProduct | null> {
    try {
      const page = await this.api.getJson<{ items: AdminProduct[] }>(`/api/catalog/products/page${pageQueryString({ id, take: 1 })}`);
      return page.items[0] ?? null;
    } catch {
      return null;
    }
  }

  async reload(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      // The page on screen, fresh from the server (this screen edits the catalogue: a stale row
      // here is a stale row saved back); never the whole catalogue.
      const [branches] = await Promise.all([this.admin.listBranches(), this.pager.reload()]);
      this.branches.set(branches);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  openCreate(): void {
    this.resetEditor();
    this.opened = JSON.stringify(this.form);
    this.editorOpen.set(true);
  }

  edit(p: AdminProduct): void {
    this.resetEditor();
    this.editingId.set(p.id);
    this.form = {
      sku: p.sku,
      barcode: p.barcode,
      nameAr: p.nameAr,
      nameFr: p.nameFr,
      nameEn: p.nameEn,
      categoryId: p.categoryId,
      price: p.price,
      costPrice: p.costPrice,
      taxRate: p.taxRate,
      unit: p.unit,
      trackStock: p.trackStock,
      isActive: p.isActive,
      isRentable: true,
      assetUnitValue: p.assetUnitValue,
    };
    this.opened = JSON.stringify(this.form);
    this.editorOpen.set(true);
  }

  /** The profile's rates, plus a rate the operator has since removed if this product still uses it. */
  taxChoices(): number[] {
    const rates = this.regional.settings().taxRates;
    return rates.includes(this.form.taxRate) ? rates : [...rates, this.form.taxRate];
  }

  isRetired(rate: number): boolean {
    return !this.regional.settings().taxRates.includes(rate);
  }

  /** Escape, the backdrop, the × or Cancel: asks first when something typed would be lost. */
  async dismissEditor(): Promise<void> {
    if (JSON.stringify(this.form) !== this.opened) {
      const discard = await this.confirm.ask({
        title: this.i18n.t('ui.discard.title'),
        message: this.i18n.t('ui.discard.message'),
        confirmLabel: this.i18n.t('ui.discard.ok'),
        cancelLabel: this.i18n.t('ui.discard.keep'),
        danger: true,
      });
      if (!discard) return;
    }
    this.cancelEdit();
  }

  cancelEdit(): void {
    this.editorOpen.set(false);
    this.resetEditor();
  }

  private resetEditor(): void {
    this.editingId.set(null);
    this.touched.reset();
    this.formErrors.set([]);
    this.error.set(null);
    this.form = this.emptyForm();
  }

  async save(): Promise<void> {
    this.touched.touchAll('sku', 'nameFr');
    // The same two required fields as always; said in a summary now, not only beside each field.
    const errors: FieldError[] = [];
    const required = this.i18n.t('common.fieldRequired');
    if (!this.form.sku.trim()) errors.push({ fieldId: 'pr-sku', message: `${this.i18n.t('backoffice.products.sku')} — ${required}` });
    if (!this.form.nameFr.trim()) errors.push({ fieldId: 'pr-fr', message: `${this.i18n.t('field.nameFr')} — ${required}` });
    this.formErrors.set(errors);
    if (errors.length) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.name(this.form))))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const id = this.editingId();
      const payload: UpsertProductRequest = {
        ...this.form,
        barcode: this.form.barcode?.trim() || null,
        // An article that is not hired has no place in the asset register, so the value does
        // not linger on one that was un-marked.
        // Every item is rented and sold (owner, 2026-10-06).
        isRentable: true,
      };
      // Straight to the server; the session's catalogue copy is marked stale, so every picker
      // elsewhere reads it again before it is next used, and this page is asked again.
      if (id) await this.admin.updateProduct(id, payload);
      else await this.admin.createProduct(payload);
      this.catalog.invalidate();
      await this.pager.reload();
      this.cancelEdit();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async remove(p: AdminProduct): Promise<void> {
    const yes = await this.confirm.ask(deleteRequest(this.i18n, this.i18n.t('products.delete.title', { name: this.name(p) })));
    if (!yes) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.admin.deleteProduct(p.id);
      this.catalog.invalidate();
      await this.pager.reload();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  openRentalSettings(): void {
    void this.router.navigate(['/backoffice', 'rentals', 'settings']);
  }

  // ---------- stock ----------

  async openStock(p: AdminProduct): Promise<void> {
    this.stockTarget.set(p);
    this.stockError.set(null);
    this.deltas = {};
    this.transferQty = 0;
    await this.reloadStock(p.id);
    // Unchanged behaviour: a transfer starts from the warehouse that carried isMain — the primary warehouse.
    const main = this.stockRows().find((r) => {
      const b = this.branches().find((x) => x.id === r.branchId);
      return !!b?.isMain && !!b.isWarehouse;
    });
    this.transferFrom = this.lock.lockedBranchId() ?? main?.branchId ?? this.stockRows()[0]?.branchId ?? '';
    this.transferTo =
      this.stockRows().find((r) => r.branchId !== this.transferFrom)?.branchId ??
      this.transferTargets()[0]?.id ??
      '';
  }

  private async reloadStock(productId: string): Promise<void> {
    try {
      this.stockRows.set(await this.admin.getProductStock(productId));
      // Asked for only when the answer can hold another branch; the server decides it anyway.
      const own = this.lock.lockedBranchId();
      this.otherAvailability.set(
        own !== null && this.lock.mayLookUpOtherBranches()
          ? (await this.admin.availability(productId)).filter((r) => r.branchId !== own)
          : [],
      );
    } catch (e) {
      this.stockError.set(adminErrorMessage(e));
    }
  }

  closeStock(): void {
    this.stockTarget.set(null);
    this.stockRows.set([]);
    this.otherAvailability.set([]);
    this.stockError.set(null);
    this.stockDone.set(null);
  }

  async applyRow(productId: string, row: ProductStockRow): Promise<void> {
    const delta = Number(this.deltas[row.branchId]) || 0;
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('backoffice.products.apply'))))) return;
    this.stockError.set(null);
    try {
      await this.admin.adjustStock(productId, row.branchId, delta, 'adjustment', row.reorderLevel);
      this.deltas[row.branchId] = 0;
      await this.reloadStock(productId);
      await this.reload();
    } catch (e) {
      this.stockError.set(adminErrorMessage(e));
    }
  }

  async transfer(productId: string): Promise<void> {
    if (!(await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('backoffice.products.transfer'))))) return;
    this.stockError.set(null);
    this.stockDone.set(null);
    try {
      const created = await this.admin.transferStock({
        fromBranchId: this.transferFrom,
        toBranchId: this.transferTo,
        note: null,
        lines: [{ productId, qty: this.transferQty }],
      });
      this.transferQty = 0;
      this.stockDone.set(this.i18n.t('transfer.dispatched', { number: created?.number ?? '' }));
      await this.reloadStock(productId);
      await this.reload();
    } catch (e) {
      this.stockError.set(adminErrorMessage(e));
    }
  }
}
