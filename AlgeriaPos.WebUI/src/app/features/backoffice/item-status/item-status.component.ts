import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { AdminBranch, AdminCustomer, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { PrintService } from '../../print/print.service';
import { RentalContractRow, RentalsApi } from '../rentals/rentals-api.service';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { MultilingualName, formatQty, localizedName } from '../backoffice-format';
import {
  ItemStatusApiService,
  ItemStatusDetail,
  ItemStatusFilter,
  ItemStatusQuery,
  ItemStatusRow,
  ItemTypeFilter,
  itemStatusQueryString,
  saveBlob,
} from './item-status-api.service';
import { ItemStatusPagesApi } from './item-status-pages.api';
import { ItemStatusDetailComponent } from './item-status-detail.component';

@Component({
  selector: 'app-item-status',
  standalone: true,
  imports: [FormsModule, StatusComponent, ItemStatusDetailComponent, ListFooterComponent],
  template: `
    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }

      <section class="bo-panel">
        <!-- Head -->
        <div class="bo-panel__head bo-main-head">
          <div>
            <h2 class="bo-panel__title">{{ i18n.t('itemStatus.title') }}</h2>
            <p class="bo-panel__note">{{ i18n.t('itemStatus.lead') }}</p>
            <p class="bo-panel__note bo-panel__no-transit">{{ i18n.t('itemStatus.transitNote') }}</p>
          </div>
          <div class="bo-head-actions">
            <button type="button" class="btn-ghost" (click)="exportSummaryExcel()">
              {{ i18n.t('itemStatus.btn.excel') }}
            </button>
            <button type="button" class="btn-ghost" (click)="printSummary()">
              {{ i18n.t('itemStatus.btn.print') }}
            </button>
          </div>
        </div>

        <!-- Filters -->
        <div class="bo-panel__body bo-form">
          <div class="bo-fields bo-fields--roomy bo-filters-grid">
            <!-- Search -->
            <div class="bo-field">
              <label for="is-search">{{ i18n.t('itemStatus.filter.search') }}</label>
              <input
                id="is-search"
                type="search"
                [placeholder]="i18n.t('itemStatus.searchPlaceholder')"
                [ngModel]="searchQuery()"
                (ngModelChange)="onSearchInput($event)"
              />
            </div>

            <!-- Location -->
            @if (lock.locked()) {
              <div class="bo-field bo-field--scope">
                <span class="bo-field-label">{{ i18n.t('itemStatus.locations') }}</span>
                <p class="bo-scope-note">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
              </div>
            } @else {
              <div class="bo-field">
                <label for="is-branch">{{ i18n.t('itemStatus.locations') }}</label>
                <select id="is-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
                  <option [ngValue]="null">{{ i18n.t('itemStatus.allLocations') }}</option>
                  @if (warehouses().length > 0) {
                    <optgroup [label]="i18n.t('itemStatus.warehouses')">
                      @for (w of warehouses(); track w.id) {
                        <option [ngValue]="w.id">
                          {{ name(w) }}{{ w.isActive ? '' : ' ' + i18n.t('itemStatus.inactive') }}
                        </option>
                      }
                    </optgroup>
                  }
                  @if (regularBranches().length > 0) {
                    <optgroup [label]="i18n.t('itemStatus.branches')">
                      @for (b of regularBranches(); track b.id) {
                        <option [ngValue]="b.id">
                          {{ name(b) }}{{ b.isActive ? '' : ' ' + i18n.t('itemStatus.inactive') }}
                        </option>
                      }
                    </optgroup>
                  }
                </select>
              </div>
            }

            <!-- Item Type -->
            <div class="bo-field">
              <label for="is-type">{{ i18n.t('itemStatus.filter.type') }}</label>
              <select id="is-type" [ngModel]="itemType()" (ngModelChange)="setItemType($event)">
                <option [ngValue]="null">{{ i18n.t('itemStatus.itemType.all') }}</option>
                <option value="rental">{{ i18n.t('itemStatus.itemType.rental') }}</option>
                <option value="sale">{{ i18n.t('itemStatus.itemType.sale') }}</option>
              </select>
            </div>

            <!-- Status -->
            <div class="bo-field">
              <label for="is-status">{{ i18n.t('itemStatus.filter.status') }}</label>
              <select id="is-status" [ngModel]="status()" (ngModelChange)="setStatus($event)">
                <option [ngValue]="null">{{ i18n.t('itemStatus.status.all') }}</option>
                <option value="available">{{ i18n.t('itemStatus.status.available') }}</option>
                <option value="rented">{{ i18n.t('itemStatus.status.rented') }}</option>
                <option value="maintenance">{{ i18n.t('itemStatus.status.maintenance') }}</option>
                <option value="scrap">{{ i18n.t('itemStatus.status.scrap') }}</option>
                <option value="unexplained">{{ i18n.t('itemStatus.status.unexplained') }}</option>
                <option value="transit">{{ i18n.t('itemStatus.status.transit') }}</option>
              </select>
            </div>

            <!-- Customer -->
            <div class="bo-field">
              <label for="is-customer">{{ i18n.t('itemStatus.customer') }}</label>
              <select id="is-customer" [ngModel]="customerId()" (ngModelChange)="setCustomer($event)">
                <option [ngValue]="null">{{ i18n.t('itemStatus.allCustomers') }}</option>
                @for (c of customers(); track c.id) {
                  <option [ngValue]="c.id">
                    @if (c.code) { {{ c.code }} — } {{ c.fullName }}
                  </option>
                }
              </select>
            </div>

            <!-- Contract (visible/active once customer is chosen) -->
            @if (customerId()) {
              <div class="bo-field">
                <label for="is-contract">{{ i18n.t('itemStatus.contract') }}</label>
                <select id="is-contract" [ngModel]="contractId()" (ngModelChange)="setContract($event)">
                  <option [ngValue]="null">{{ i18n.t('itemStatus.allContracts') }}</option>
                  @for (cnt of contracts(); track cnt.id) {
                    <option [ngValue]="cnt.id">{{ cnt.number }}</option>
                  }
                </select>
              </div>
            }

            <!-- Include Inactive Checkbox -->
            <div class="bo-field bo-field--check">
              <label class="bo-check-label">
                <input
                  type="checkbox"
                  [ngModel]="includeInactive()"
                  (ngModelChange)="setIncludeInactive($event)"
                />
                <span>{{ i18n.t('itemStatus.includeInactive') }}</span>
              </label>
            </div>
          </div>

          <!-- Selection Action Buttons -->
          @if (selectedIds().size > 0 || productIdsFilter() !== null) {
            <div class="bo-selection-bar">
              @if (selectedIds().size > 0 && productIdsFilter() === null) {
                <button type="button" class="btn-ghost" (click)="filterToSelected()">
                  {{ i18n.t('itemStatus.selectionOnly', { n: selectedIds().size }) }}
                </button>
              }
              @if (productIdsFilter() !== null) {
                <button type="button" class="btn-ghost" (click)="clearProductFilter()">
                  {{ i18n.t('itemStatus.showAll') }}
                </button>
              }
            </div>
          }
        </div>

        <!-- Scope sentence in words above the table -->
        @if (report(); as rep) {
          <div class="bo-scope-statement-bar">
            <p class="bo-scope-note bo-scope-statement">
              {{ scopeSentence(rep.scope) }}
            </p>
          </div>
        }

        <!-- Table or Loading / Empty states -->
        @if (!pager.loaded()) {
          <p class="bo-loading" role="status">
            <span class="bo-loading__word">{{ i18n.t('common.loading') }}</span>
            <span class="bo-loading__rows" aria-hidden="true"></span>
          </p>
        } @else if (!pager.total()) {
          <p class="bo-panel__body bo-empty-note">{{ i18n.t('common.nothingHere') }}</p>
        } @else {
          <div class="bo-scroll">
            <table class="bo-data">
              <thead>
                <tr>
                  <th class="bo-cell-check">
                    <input
                      type="checkbox"
                      [attr.aria-label]="i18n.t('itemStatus.col.select')"
                      [checked]="isAllSelected()"
                      (change)="toggleSelectAll()"
                    />
                  </th>
                  <th>{{ i18n.t('itemStatus.col.sku') }}</th>
                  <th>{{ i18n.t('itemStatus.col.item') }}</th>
                  <th class="num">{{ i18n.t('itemStatus.col.total') }}</th>
                  <th class="num">{{ i18n.t('itemStatus.col.warehouses') }}</th>
                  <th class="num">{{ i18n.t('itemStatus.col.branches') }}</th>
                  <th class="num">{{ i18n.t('itemStatus.col.rented') }}</th>
                  <th class="num">{{ i18n.t('itemStatus.col.unavailable') }}</th>
                  <th class="num">{{ i18n.t('itemStatus.col.inTransit') }}</th>
                  <th class="num">{{ i18n.t('itemStatus.col.available') }}</th>
                  @if (report()?.scope?.othersAvailableShown) {
                    <th class="num">{{ i18n.t('itemStatus.col.otherAvailable') }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (row of pager.rows(); track row.productId) {
                  <tr [class.is-selected]="openProductId() === row.productId">
                    <td class="bo-cell-check">
                      <input
                        type="checkbox"
                        [attr.aria-label]="i18n.t('itemStatus.col.select')"
                        [checked]="selectedIds().has(row.productId)"
                        (change)="toggleSelection(row.productId)"
                      />
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('itemStatus.col.sku')">
                      {{ row.sku }}
                    </td>
                    <td [attr.data-label]="i18n.t('itemStatus.col.item')">
                      <button
                        type="button"
                        class="bo-item-link-btn"
                        (click)="toggleOpenItem(row.productId)"
                      >
                        {{ itemName(row) }}
                      </button>
                      @if (row.unexplainedOnRent !== 0) {
                        <div class="bo-sub-tag">
                          <app-status tone="warning">
                            {{ i18n.t('itemStatus.unexplainedWarning', { n: qty(row.unexplainedOnRent) }) }}
                          </app-status>
                        </div>
                      }
                      @if (row.leftToIssue > 0) {
                        <div class="bo-muted-note">
                          {{ i18n.t('itemStatus.leftToIssueInfo', { n: qty(row.leftToIssue) }) }}
                        </div>
                      }
                    </td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('itemStatus.col.total')">
                      {{ qty(row.total) }}
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.warehouses')">
                      {{ qty(row.warehouses) }}
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.branches')">
                      {{ qty(row.branches) }}
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.rented')">
                      {{ qty(row.rented) }}
                    </td>
                    <td
                      class="num mono"
                      [attr.data-label]="i18n.t('itemStatus.col.unavailable')"
                      [title]="unavailableSplitTitle(row)"
                    >
                      {{ qty(row.maintenance + row.scrap) }}
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.inTransit')">
                      {{ qty(row.inTransit) }}
                    </td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('itemStatus.col.available')">
                      {{ qty(row.available) }}
                    </td>
                    @if (report()?.scope?.othersAvailableShown) {
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.otherAvailable')">
                        {{ qty(row.otherAvailable) }}
                      </td>
                    }
                  </tr>
                }
              </tbody>
              @if (report()?.totals; as tot) {
                <tfoot>
                  <tr class="bo-totals-row">
                    <td class="bo-cell-check"></td>
                    <td colspan="2">
                      <strong>{{ i18n.t('itemStatus.col.total') }}</strong>
                    </td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('itemStatus.col.total')">{{ qty(tot.total) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.warehouses')">{{ qty(tot.warehouses) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.branches')">{{ qty(tot.branches) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.rented')">{{ qty(tot.rented) }}</td>
                    <td
                      class="num mono"
                      [attr.data-label]="i18n.t('itemStatus.col.unavailable')"
                      [title]="unavailableSplitTitle(tot)"
                    >
                      {{ qty(tot.maintenance + tot.scrap) }}
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.inTransit')">{{ qty(tot.inTransit) }}</td>
                    <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('itemStatus.col.available')">{{ qty(tot.available) }}</td>
                    @if (report()?.scope?.othersAvailableShown) {
                      <td class="num mono" [attr.data-label]="i18n.t('itemStatus.col.otherAvailable')">{{ qty(tot.otherAvailable) }}</td>
                    }
                  </tr>
                </tfoot>
              }
            </table>
          </div>
          <!-- A page at a time from the server; the totals row is the server's, over every row. -->
          <app-list-footer class="is-list-foot" [pager]="pager" [loading]="!pager.loaded()" />
        }

        <!-- Breakdown section under the table when an item is selected -->
        @if (openProductId() && detail()) {
          <app-item-status-detail
            [detail]="detail()!"
            [query]="query()"
            [branches]="branches()"
            (close)="toggleOpenItem(openProductId()!)"
            (datesChange)="onDetailDatesChange($event)"
            (exportExcel)="exportDetailExcel($event)"
            (print)="printDetail($event)"
          />
        }
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      .bo-main-head {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: var(--sp-3);
      }
      .bo-head-actions {
        display: flex;
        gap: var(--sp-2);
        flex-wrap: wrap;
      }
      .bo-panel__no-transit {
        margin-block-start: var(--sp-1);
        color: var(--text-2);
      }
      .bo-filters-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(12.5rem, 1fr));
        gap: var(--sp-3);
        align-items: flex-end;
      }
      .bo-field--scope {
        display: flex;
        flex-direction: column;
        justify-content: flex-end;
      }
      .bo-field-label {
        font-size: var(--fs-sm);
        font-weight: 600;
        margin-block-end: var(--sp-1);
      }
      .bo-field--check {
        display: flex;
        align-items: center;
        min-block-size: 2.5rem;
      }
      .bo-check-label {
        display: inline-flex;
        align-items: center;
        gap: var(--sp-2);
        cursor: pointer;
        font-size: var(--fs-sm);
        font-weight: 500;
      }
      .bo-selection-bar {
        display: flex;
        gap: var(--sp-2);
        margin-block-start: var(--sp-2);
      }
      .bo-scope-statement-bar {
        padding-inline: var(--sp-4);
        padding-block: var(--sp-2);
        border-block-end: 1px solid var(--border);
      }
      .bo-scope-statement {
        margin: 0;
        font-weight: 600;
      }
      .bo-cell-check {
        inline-size: 2.5rem;
        text-align: center;
      }
      .bo-item-link-btn {
        background: none;
        border: none;
        padding: 0;
        font: inherit;
        font-weight: 600;
        color: var(--text);
        text-align: start;
        cursor: pointer;
        text-decoration: underline;
        text-decoration-color: var(--border);
        text-underline-offset: 3px;
      }
      .bo-item-link-btn:hover {
        color: var(--primary);
        text-decoration-color: var(--primary);
      }
      .bo-sub-tag {
        margin-block-start: var(--sp-1);
      }
      .bo-muted-note {
        font-size: var(--fs-meta);
        color: var(--muted);
        margin-block-start: var(--sp-1);
      }
      .bo-totals-row {
        background: var(--surface-2);
        border-block-start: 1px solid var(--border);
      }
      .bo-empty-note {
        margin: 0;
        color: var(--muted);
      }
      .is-list-foot {
        padding: var(--sp-4) var(--sp-5);
        border-block-start: var(--line) solid var(--border);
      }
    `,
  ],
})
export class ItemStatusComponent implements OnInit, OnDestroy {
  private readonly admin = inject(AdminService);
  private readonly rentalsApi = inject(RentalsApi);
  private readonly api = inject(ItemStatusApiService);
  private readonly pagesApi = inject(ItemStatusPagesApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly printService = inject(PrintService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);

  readonly branches = signal<AdminBranch[]>([]);
  readonly customers = signal<AdminCustomer[]>([]);
  readonly contracts = signal<RentalContractRow[]>([]);

  readonly searchQuery = signal('');
  readonly branchId = signal<string | null>(this.lock.lockedBranchId());
  readonly itemType = signal<ItemTypeFilter | null>(null);
  readonly status = signal<ItemStatusFilter | null>(null);
  readonly customerId = signal<string | null>(null);
  readonly contractId = signal<string | null>(null);
  readonly includeInactive = signal(false);

  readonly selectedIds = signal<Set<string>>(new Set());
  readonly productIdsFilter = signal<string[] | null>(null);

  /** The report's filters as the server reads them, for the list, its breakdown and the papers. */
  readonly query = computed(() => this.currentQuery());

  /**
   * The report a page at a time from the server (owner, 2026-11-17: "paginate in backend, not
   * frontend"): ten rows to begin with; every filter runs there and a change goes back to page one.
   * The totals row, the scope and every row's product come with each page (`summary`), over the
   * whole report — so the selection and "select all" still cover every row of it, as before.
   */
  readonly pager = createServerPager(
    (page) => this.pagesApi.page(page.filters, page.skip, page.take),
    { key: 'inventory.itemStatus', filters: () => this.query() },
  );

  /** The server's figures over every row of the report; null until the first page answers. */
  readonly report = computed(() => this.pager.summary());
  /** Every row's product, across all pages. */
  readonly allProductIds = computed(() => this.report()?.productIds ?? []);

  /** The report could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });
  readonly openProductId = signal<string | null>(null);
  readonly detail = signal<ItemStatusDetail | null>(null);
  readonly detailFrom = signal<string | undefined>(undefined);
  readonly detailTo = signal<string | undefined>(undefined);

  readonly error = signal<string | null>(null);

  readonly warehouses = computed(() => this.branches().filter((b) => b.isWarehouse));
  readonly regularBranches = computed(() => this.branches().filter((b) => !b.isWarehouse));

  private searchDebounceTimer: any;
  private routeSub?: Subscription;

  name(item: Partial<MultilingualName> | null | undefined): string {
    return localizedName(item, this.i18n.lang());
  }

  itemName(row: ItemStatusRow): string {
    return localizedName(row, this.i18n.lang());
  }

  qty(value: number | null | undefined): string {
    return formatQty(value, this.i18n.lang());
  }

  unavailableSplitTitle(row: { maintenance: number; scrap: number }): string {
    return this.i18n.t('itemStatus.unavailableSplit', {
      m: this.qty(row.maintenance),
      s: this.qty(row.scrap),
    });
  }

  scopeSentence(scope: { kind: string; locationId?: string | null; othersAvailableShown: boolean }): string {
    if (scope.kind === 'company') {
      return this.i18n.t('itemStatus.scope.company');
    }
    if (scope.kind === 'location') {
      const loc = this.branches().find((b) => b.id === scope.locationId);
      return loc ? this.name(loc) : '—';
    }
    if (scope.kind === 'own') {
      const base = this.i18n.t('itemStatus.scope.own');
      return scope.othersAvailableShown ? base + this.i18n.t('itemStatus.scope.andOthers') : base;
    }
    return '';
  }

  async ngOnInit(): Promise<void> {
    try {
      const [branches, customers] = await Promise.all([
        this.admin.listBranches().catch(() => [] as AdminBranch[]),
        this.admin.listCustomers().catch(() => [] as AdminCustomer[]),
      ]);
      this.branches.set(branches);
      this.customers.set(customers);
      // The value the field starts with: the list has asked already and does not ask again.
      this.branchId.set(this.lock.lockedBranchId());
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }

    this.routeSub = this.route.queryParamMap.subscribe((params) => {
      const itemParam = params.get('item');
      if (itemParam !== this.openProductId()) {
        this.openProductId.set(itemParam);
        this.detailFrom.set(undefined);
        this.detailTo.set(undefined);
        if (itemParam) {
          void this.loadDetail(itemParam);
        } else {
          this.detail.set(null);
        }
      }
    });

    // The report loads itself through its pager.
  }

  ngOnDestroy(): void {
    clearTimeout(this.searchDebounceTimer);
    this.routeSub?.unsubscribe();
  }

  // Each filter is one of the pager's, which asks the server again by itself (the search after a
  // pause); what is left to do here is the open breakdown, read again under the new filters.

  onSearchInput(val: string): void {
    this.searchQuery.set(val);
    clearTimeout(this.searchDebounceTimer);
    this.searchDebounceTimer = setTimeout(() => {
      void this.refreshDetail();
    }, 300);
  }

  setBranch(val: string | null): void {
    this.branchId.set(val || null);
    void this.refreshDetail();
  }

  setItemType(val: ItemTypeFilter | null): void {
    this.itemType.set(val || null);
    void this.refreshDetail();
  }

  setStatus(val: ItemStatusFilter | null): void {
    this.status.set(val || null);
    void this.refreshDetail();
  }

  async setCustomer(val: string | null): Promise<void> {
    this.customerId.set(val || null);
    this.contractId.set(null);
    if (val) {
      try {
        const contracts = await this.rentalsApi.listContracts(undefined, val);
        this.contracts.set(contracts);
      } catch {
        this.contracts.set([]);
      }
    } else {
      this.contracts.set([]);
    }
    void this.refreshDetail();
  }

  setContract(val: string | null): void {
    this.contractId.set(val || null);
    void this.refreshDetail();
  }

  setIncludeInactive(val: boolean): void {
    this.includeInactive.set(val);
    void this.refreshDetail();
  }

  toggleSelection(id: string): void {
    const set = new Set(this.selectedIds());
    if (set.has(id)) set.delete(id);
    else set.add(id);
    this.selectedIds.set(set);
  }

  /** Every row of the report, on every page — not only the page on screen. */
  isAllSelected(): boolean {
    const ids = this.allProductIds();
    if (ids.length === 0) return false;
    const set = this.selectedIds();
    return ids.every((id) => set.has(id));
  }

  toggleSelectAll(): void {
    const all = this.isAllSelected();
    if (all) {
      this.selectedIds.set(new Set());
    } else {
      this.selectedIds.set(new Set(this.allProductIds()));
    }
  }

  filterToSelected(): void {
    const ids = Array.from(this.selectedIds());
    if (ids.length === 0) return;
    this.productIdsFilter.set(ids);
    void this.refreshDetail();
  }

  clearProductFilter(): void {
    this.productIdsFilter.set(null);
    void this.refreshDetail();
  }

  toggleOpenItem(productId: string | null): void {
    const next = this.openProductId() === productId ? null : productId;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { item: next },
      queryParamsHandling: 'merge',
    });
  }

  currentQuery(): ItemStatusQuery {
    return {
      branchId: this.branchId(),
      q: this.searchQuery() || undefined,
      productIds: this.productIdsFilter() ? this.productIdsFilter()!.join(',') : undefined,
      itemType: this.itemType(),
      status: this.status(),
      customerId: this.customerId(),
      contractId: this.contractId(),
      includeInactive: this.includeInactive() ? true : undefined,
    };
  }

  /** Asks again for the page on screen and the open breakdown. */
  async run(): Promise<void> {
    this.error.set(null);
    await Promise.all([this.pager.reload(), this.refreshDetail()]);
  }

  /** The open breakdown, read again under the filters of the moment and its own dates. */
  private async refreshDetail(): Promise<void> {
    const open = this.openProductId();
    if (open) await this.loadDetail(open, this.detailFrom(), this.detailTo());
  }

  /**
   * One article's breakdown, without its two long lists: the rentals out and the movements page
   * through the server inside the breakdown itself (owner, 2026-11-17).
   */
  async loadDetail(productId: string, from?: string, to?: string): Promise<void> {
    try {
      const detail = await this.pagesApi.overview(productId, this.currentQuery(), from, to);
      this.detail.set(detail);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  async onDetailDatesChange(dates: { from: string; to: string }): Promise<void> {
    this.detailFrom.set(dates.from);
    this.detailTo.set(dates.to);
    if (this.openProductId()) {
      await this.loadDetail(this.openProductId()!, dates.from, dates.to);
    }
  }

  async exportSummaryExcel(): Promise<void> {
    try {
      const blob = await this.api.exportSummary(this.currentQuery(), this.i18n.lang());
      const dateStr = new Date().toISOString().slice(0, 10);
      saveBlob(blob, `item-status-${dateStr}.xlsx`);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  async exportDetailExcel(dates: { from: string; to: string }): Promise<void> {
    if (!this.openProductId()) return;
    try {
      const blob = await this.api.exportDetail(
        this.openProductId()!,
        this.currentQuery(),
        dates.from,
        dates.to,
        this.i18n.lang(),
      );
      const dateStr = new Date().toISOString().slice(0, 10);
      const sku = this.detail()?.item.sku || this.openProductId()!;
      saveBlob(blob, `item-status-${sku}-${dateStr}.xlsx`);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  printSummary(): void {
    const qs = itemStatusQueryString(this.currentQuery());
    this.printService.open('report', 'item-status', { filters: qs, lang: this.i18n.lang() });
  }

  printDetail(dates: { from: string; to: string }): void {
    if (!this.openProductId()) return;
    const qs = itemStatusQueryString(this.currentQuery(), { from: dates.from, to: dates.to });
    this.printService.open('report', 'item-status', {
      filters: qs,
      item: this.openProductId()!,
      lang: this.i18n.lang(),
    });
  }
}
