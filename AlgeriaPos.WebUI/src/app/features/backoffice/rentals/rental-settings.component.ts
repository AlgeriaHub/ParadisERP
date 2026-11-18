import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminProduct, adminErrorMessage } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { RegionalService } from '../../../core/regional.service';
import { overdueUnitPrice } from '../../../core/rentals';
import { localizedName } from '../backoffice-format';
import { ProductPickerComponent } from '../../../shared/product-picker.component';
import { ConfirmService } from '../../../ui/confirm.service';
import { saveRequest } from '../../../ui/confirm-requests';
import { FoldComponent } from '../../../ui/fold.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { RentalPagesApi } from './rental-pages.api';
import { ProductOverdueMultiplier, RentalSettings, RentalsApi } from './rentals-api.service';

/**
 * The three company decisions the rental arithmetic reads, and the per-item exception to one.
 *
 * <p>The overdue price is a **multiplier**, not a second price list: it stays correct on its own
 * when hire prices change, instead of needing a second table kept in step. The figure it produces
 * is still frozen onto every line at signing, so raising the multiplier tomorrow never re-prices
 * a contract the customer has already signed — and it is a **floor**: a manager may sign a higher
 * overdue price on a contract, never a lower one.</p>
 *
 * <p>The per-item exceptions are listed from the server, a page at a time (`GET
 * /settings/products/page`, owner 2026-11-17), so the screen shows what is actually set rather than
 * only what it has just saved, and picking an item prefills its current figure (`GET
 * /settings/products/{id}`, whatever page it is on).</p>
 *
 * <p>The deposit switch records a figure on the contract and shows it; it collects and refunds
 * nothing, and posts nothing, until a money document exists. That is said beside the switch rather
 * than discovered afterwards.</p>
 *
 * <p>The day cutoff hour is the owner's rule for when a day is spent (answers 5 and 6): goods out
 * after it start the next day, goods back after it have their return day counted, and the branch
 * manager may decide otherwise on the note. Blank keeps the old rule, and a worked example in
 * words sits under the field so nobody has to guess what 12 means. Each item may also carry a
 * fixed replacement value (answer 13) that a contract line's damage price starts from.</p>
 */
@Component({
  selector: 'app-rental-settings',
  standalone: true,
  imports: [FormsModule, ProductPickerComponent, FoldComponent, ListFooterComponent],
  template: `
    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }
      @if (saved()) {
        <p class="notice ok" role="status">{{ i18n.t('common.saved') }}</p>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('rent.settings.company') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('rent.settings.companyHint') }}</p>
        </div>

        <!-- One row per decision: what it is and why on one side, the control and what the figure
             typed will do on the other. Each row folds: the first starts open, the others folded. -->
        <div class="bo-panel__body bo-form">
          <!-- The overdue multiplier is not set here (owner, 2026-10-06: "useless"). The stored value
               still sets each line's overdue floor and is sent back unchanged on save. -->
          <app-fold class="bo-group bo-group--row" [heading]="i18n.t('rent.settings.tax')" [open]="true">
            <p class="bo-group__note" foldLead>{{ i18n.t('rent.settings.taxHint') }}</p>
            <div class="bo-fields bo-fields--roomy">
              <div class="bo-field" [class.is-invalid]="taxInvalid()">
                <label for="rs-tax">{{ i18n.t('rent.settings.taxRate') }}</label>
                <input
                  id="rs-tax"
                  type="number"
                  min="0"
                  max="100"
                  step="0.1"
                  class="num mono"
                  [ngModel]="taxRate()"
                  (ngModelChange)="taxRate.set(+$event)"
                />
                <p class="bo-hint">{{ i18n.t('rent.settings.taxZero') }}</p>
                @if (taxInvalid()) {
                  <span class="bo-error" role="alert">{{ i18n.t('rent.settings.taxRange') }}</span>
                }
              </div>
            </div>
          </app-fold>

          <app-fold class="bo-group bo-group--row" [heading]="i18n.t('rent.day.settingsTitle')">
            <p class="bo-group__note" foldLead>{{ i18n.t('rent.day.settingsHint') }}</p>
            <div class="bo-fields bo-fields--roomy">
              <div class="bo-field" [class.is-invalid]="cutoffInvalid()">
                <label for="rs-cutoff">
                  {{ i18n.t('rent.day.cutoff') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
                </label>
                <input
                  id="rs-cutoff"
                  type="number"
                  min="0"
                  max="23"
                  step="1"
                  class="num mono"
                  [ngModel]="cutoff()"
                  (ngModelChange)="cutoff.set($event === null || $event === '' ? null : +$event)"
                />
                <p class="bo-hint">{{ i18n.t('rent.day.cutoffBlank') }}</p>
                @if (cutoffInvalid()) {
                  <span class="bo-error" role="alert">{{ i18n.t('rent.day.cutoffRange') }}</span>
                }
              </div>
              <div class="bo-field" [class.is-invalid]="minDaysInvalid()">
                <label for="rs-min-days">{{ i18n.t('rent.minDays.label') }}</label>
                <input
                  id="rs-min-days"
                  type="number"
                  min="0"
                  max="366"
                  step="1"
                  class="num mono"
                  [ngModel]="minDays()"
                  (ngModelChange)="minDays.set($event === null || $event === '' ? 0 : +$event)"
                />
                <p class="bo-hint">{{ i18n.t('rent.minDays.hint') }}</p>
                @if (minDaysInvalid()) {
                  <span class="bo-error" role="alert">{{ i18n.t('rent.minDays.range') }}</span>
                }
              </div>
            </div>
            <p class="bo-callout" aria-live="polite">
              <span class="bo-state" [class.bo-state--lead]="cutoff() !== null">
                {{ i18n.t(cutoff() === null ? 'rent.day.off' : 'rent.day.on') }}
              </span>
              <span>{{ cutoffExample() }}</span>
            </p>
          </app-fold>

          <app-fold class="bo-group bo-group--row" [heading]="i18n.t('rent.settings.deposit')">
            <p class="bo-group__note" foldLead>{{ i18n.t('rent.settings.depositHint') }}</p>
            <div class="bo-check">
              <input
                id="rs-deposit"
                type="checkbox"
                [ngModel]="depositEnabled()"
                (ngModelChange)="depositEnabled.set($event)"
              />
              <label for="rs-deposit">{{ i18n.t('rent.settings.depositSwitch') }}</label>
            </div>
            @if (depositEnabled()) {
              <p class="notice warn">{{ i18n.t('rent.settings.depositOn') }}</p>
            }
          </app-fold>
        </div>

        <div class="bo-panel__foot">
          <button type="button" class="btn-primary" [disabled]="busy() || !canSave()" (click)="save()">
            {{ i18n.t('common.save') }}
          </button>
        </div>
      </section>

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('rent.settings.perItem') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('rent.settings.perItemHint') }}</p>
        </div>
        <div class="bo-panel__body bo-form">
          <div class="bo-fields bo-fields--roomy">
            <div class="bo-field bo-field--wide">
              <label for="rs-product">{{ i18n.t('rent.item') }}</label>
              <app-product-picker
                inputId="rs-product"
                [products]="products()"
                [productId]="productId()"
                (picked)="pickItem($event.id)"
              />
            </div>
            <div class="bo-field" [class.is-invalid]="itemPriceInvalid()">
              <label for="rs-item-price">
                {{ i18n.t('rent.settings.predefinedPrice') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
              </label>
              <input
                id="rs-item-price"
                type="number"
                min="0"
                class="num mono"
                [step]="regional.moneyStep()"
                [ngModel]="itemRentalUnitPrice()"
                (ngModelChange)="itemRentalUnitPrice.set(+$event || 0)"
              />
              <p class="bo-hint">{{ i18n.t('rent.settings.predefinedPriceHint') }}</p>
              @if (itemPriceInvalid()) {
                <span class="bo-error" role="alert">{{ i18n.t('rent.settings.predefinedPriceInvalid') }}</span>
              }
            </div>
            <div class="bo-field" [class.is-invalid]="itemDamageInvalid()">
              <label for="rs-item-damage">
                {{ i18n.t('rent.damage.itemValue') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span>
              </label>
              <input
                id="rs-item-damage"
                type="number"
                min="0"
                class="num mono"
                [step]="regional.moneyStep()"
                [ngModel]="itemDamageValue()"
                (ngModelChange)="itemDamageValue.set($event === null || $event === '' ? null : +$event)"
              />
              <p class="bo-hint">{{ i18n.t('rent.damage.itemValueHint') }}</p>
              @if (itemDamageInvalid()) {
                <span class="bo-error" role="alert">{{ i18n.t('rent.damage.itemValueInvalid') }}</span>
              }
            </div>
          </div>
          @if (itemSaved()) {
            <p class="notice ok" role="status">{{ itemSaved() }}</p>
          }
        </div>
        <div class="bo-panel__foot">
          <button
            type="button"
            class="btn-primary"
            [disabled]="busy() || !productId() || itemPriceInvalid() || itemDamageInvalid()"
            (click)="saveItem()"
          >
            {{ i18n.t('common.save') }}
          </button>
        </div>
      </section>

      <!-- What is actually set, read from the server — not only what this screen just saved. -->
      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('rent.settings.perItemList') }}</h2>
        </div>
        <!-- Filters applied by the server, over every item (owner, 2026-11-17). -->
        @if (overridesPager.total() || overridesFiltered()) {
          <div class="bo-panel__body">
            <div class="ui-toolbar" role="search">
              <div class="ui-filter ui-filter--search">
                <label for="rs-f-q">{{ i18n.t('products.search') }}</label>
                <input id="rs-f-q" type="search" [placeholder]="i18n.t('products.searchHint')" [ngModel]="overrideQuery()" (ngModelChange)="overrideQuery.set($event)" />
              </div>
              <div class="ui-filter">
                <label for="rs-f-price">{{ i18n.t('rent.settings.predefinedPrice') }}</label>
                <select id="rs-f-price" [ngModel]="priceFilter()" (ngModelChange)="priceFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="set">{{ i18n.t('rent.filter.set') }}</option>
                  <option ngValue="unset">{{ i18n.t('rent.filter.notSet') }}</option>
                </select>
              </div>
              <div class="ui-filter">
                <label for="rs-f-damage">{{ i18n.t('rent.damage.itemValue') }}</label>
                <select id="rs-f-damage" [ngModel]="damageFilter()" (ngModelChange)="damageFilter.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="set">{{ i18n.t('rent.filter.set') }}</option>
                  <option ngValue="unset">{{ i18n.t('rent.filter.notSet') }}</option>
                </select>
              </div>
              @if (overridesFiltered()) {
                <div class="ui-toolbar__end">
                  <button type="button" class="btn-ghost" (click)="clearOverrideFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                </div>
              }
            </div>
          </div>
        }
        <div class="bo-scroll">
          <table class="bo-data">
            <thead>
              <tr>
                <th>{{ i18n.t('rent.item') }}</th>
                <th class="num">{{ i18n.t('rent.settings.predefinedPrice') }}</th>
                <th class="num">{{ i18n.t('rent.damage.itemValue') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (row of overridesPager.rows(); track row.productId) {
                <tr>
                  <td [attr.data-label]="i18n.t('rent.item')">
                    <span class="mono bo-soft">{{ row.sku }}</span>
                    <span>{{ rowName(row) }}</span>
                  </td>
                  <td class="num mono bo-cell-strong" [attr.data-label]="i18n.t('rent.settings.predefinedPrice')">
                    {{ i18n.money(row.rentalUnitPrice) }}
                  </td>
                  <td class="num mono" [attr.data-label]="i18n.t('rent.damage.itemValue')">
                    {{ row.rentalDamageUnitPrice === null || row.rentalDamageUnitPrice === undefined ? '—' : i18n.money(row.rentalDamageUnitPrice) }}
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="3" class="bo-empty">
                    @if (!overridesPager.loaded()) {
                      <strong>{{ i18n.t('common.loading') }}</strong>
                    } @else if (overridesFiltered()) {
                      <strong>{{ i18n.t('ui.state.noResults') }}</strong>
                      <button type="button" class="btn-ghost" (click)="clearOverrideFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                    } @else {
                      <strong>{{ i18n.t('rent.settings.perItemNone') }}</strong>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <app-list-footer class="rs-list-foot" [pager]="overridesPager" [loading]="!overridesPager.loaded()" />
      </section>
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .rs-list-foot {
        padding: var(--sp-4) var(--sp-5);
        border-block-start: var(--line) solid var(--border);
      }
    `,
  ],
})
export class RentalSettingsComponent implements OnInit {
  private readonly api = inject(RentalsApi);
  private readonly pagesApi = inject(RentalPagesApi);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);
  readonly regional = inject(RegionalService);

  readonly products = input<AdminProduct[]>([]);
  readonly settings = input.required<RentalSettings>();
  readonly changed = output<RentalSettings>();

  readonly multiplier = signal(1.5);
  readonly depositEnabled = signal(false);
  readonly taxRate = signal(0);
  /** The day cutoff hour; null keeps the old rule. */
  readonly cutoff = signal<number | null>(null);
  readonly minDays = signal<number>(0);
  readonly example = signal(1000);
  readonly productId = signal<string | null>(null);
  readonly itemMultiplier = signal<number | null>(null);
  readonly itemRentalUnitPrice = signal(0);
  /** The item's fixed replacement value; null means none, and saving null clears it. */
  readonly itemDamageValue = signal<number | null>(null);
  readonly itemSaved = signal<string | null>(null);
  /** The picked item's figures as the server holds them now; what the form was prefilled from. */
  readonly pickedRow = signal<ProductOverdueMultiplier | null>(null);

  // The list's filters (owner, 2026-11-17), applied by the server over every item.
  readonly overrideQuery = signal('');
  /** The hire price: zero means the administrator has not set one yet (rentals-api.service.ts). */
  readonly priceFilter = signal<'all' | 'set' | 'unset'>('all');
  readonly damageFilter = signal<'all' | 'set' | 'unset'>('all');

  readonly overridesFiltered = computed(
    () => !!this.overrideQuery().trim() || this.priceFilter() !== 'all' || this.damageFilter() !== 'all',
  );

  /**
   * Every item on its own figure, a page at a time from the server (owner, 2026-11-17: "paginate in
   * backend, not frontend"): ten rows to begin with; the search (the till's fold, so سقاله finds سقالة
   * and echafaudage finds échafaudage) and the two filters run there, and a change goes back to page
   * one. Asked again after each save.
   */
  readonly overridesPager = createServerPager(
    (page) => this.pagesApi.itemFiguresPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'rentals.settings.perItem',
      filters: () => ({ q: this.overrideQuery().trim(), price: this.priceFilter(), damage: this.damageFilter() }),
    },
  );

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.overridesPager.error();
    return e ? adminErrorMessage(e) : null;
  });

  clearOverrideFilters(): void {
    this.overrideQuery.set('');
    this.priceFilter.set('all');
    this.damageFilter.set('all');
  }

  readonly busy = signal(false);
  readonly saved = signal(false);
  readonly error = signal<string | null>(null);

  readonly worked = computed(() =>
    overdueUnitPrice(this.example(), this.multiplier(), this.regional.decimals()),
  );

  readonly taxInvalid = computed(() => !(this.taxRate() >= 0 && this.taxRate() <= 100));
  readonly itemPriceInvalid = computed(() => this.itemRentalUnitPrice() < 0);
  readonly itemDamageInvalid = computed(() => (this.itemDamageValue() ?? 0) < 0);
  readonly cutoffInvalid = computed(() => {
    const hour = this.cutoff();
    return hour !== null && !(Number.isInteger(hour) && hour >= 0 && hour <= 23);
  });
  readonly minDaysInvalid = computed(() => {
    const d = this.minDays();
    return !(Number.isInteger(d) && d >= 0 && d <= 366);
  });
  readonly canSave = computed(
    () => !this.taxInvalid() && !this.cutoffInvalid() && !this.minDaysInvalid(),
  );

  /**
   * The owner's own example, worked through with the hour typed: out at 15:00 on the 26th, back on
   * the 29th in the morning or the afternoon. Blank says what the old rule does instead.
   */
  readonly cutoffExample = computed(() => {
    const hour = this.cutoff();
    if (hour === null || this.cutoffInvalid()) return this.i18n.t('rent.day.exampleOff');
    return this.i18n.t('rent.day.example', { h: `${String(hour).padStart(2, '0')}:00` });
  });

  /**
   * The settings come from the section shell, which fetches them once for every screen under it.
   * They are copied into fields here rather than bound through, so that a half-typed multiplier
   * is not overwritten by a reload happening somewhere else on the section.
   */
  ngOnInit(): void {
    const settings = this.settings();
    this.multiplier.set(settings.overdueMultiplier);
    this.depositEnabled.set(settings.depositEnabled);
    this.taxRate.set(settings.taxRate);
    this.cutoff.set(settings.dayCutoffHour ?? null);
    this.minDays.set(settings.minimumDays ?? 0);
    // The per-item list loads itself through its pager.
  }

  productName(product: AdminProduct): string {
    return localizedName(product, this.i18n.lang());
  }

  /** The row carries its three names itself, so a deleted or unloaded catalogue still reads. */
  rowName(row: ProductOverdueMultiplier): string {
    return localizedName(row, this.i18n.lang());
  }

  /**
   * Picking an item prefills what it currently carries, so "save" with nothing typed clears it
   * knowingly. Read by id, since the list on screen is one page: an item on no figure of its own
   * answers with none (no multiplier, no price, no replacement value), as before.
   */
  async pickItem(id: string): Promise<void> {
    this.productId.set(id);
    this.itemSaved.set(null);
    let existing: ProductOverdueMultiplier | null = null;
    try {
      existing = await this.api.getProductMultiplier(id);
    } catch {
      existing = null;
    }
    // Another item was picked while this one was being read: that one's figures win.
    if (this.productId() !== id) return;
    this.pickedRow.set(existing);
    this.itemMultiplier.set(existing?.overdueMultiplier ?? null);
    this.itemRentalUnitPrice.set(existing?.rentalUnitPrice ?? 0);
    this.itemDamageValue.set(existing?.rentalDamageUnitPrice ?? null);
  }

  async save(): Promise<void> {
    if (!this.canSave()) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n)))) return;
    this.busy.set(true);
    this.error.set(null);
    this.saved.set(false);
    try {
      const settings = await this.api.updateSettings({
        overdueMultiplier: this.multiplier(),
        depositEnabled: this.depositEnabled(),
        taxRate: this.taxRate(),
        dayCutoffHour: this.cutoff(),
        minimumDays: this.minDays(),
      });
      this.multiplier.set(settings.overdueMultiplier);
      this.depositEnabled.set(settings.depositEnabled);
      this.taxRate.set(settings.taxRate);
      this.cutoff.set(settings.dayCutoffHour ?? null);
      this.minDays.set(settings.minimumDays ?? 0);
      this.changed.emit(settings);
      this.saved.set(true);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async saveItem(): Promise<void> {
    const id = this.productId();
    if (!id) return;
    const product = this.products().find((p) => p.id === id);
    const picked = this.pickedRow();
    const existing = picked?.productId === id ? picked : null;
    const name =
      (product && (localizedName(product, this.i18n.lang()) || product.sku)) ||
      (existing && (localizedName(existing, this.i18n.lang()) || existing.sku)) ||
      null;
    if (!(await this.confirm.ask(saveRequest(this.i18n, name)))) return;
    return this.writeItem(id, this.itemMultiplier(), this.itemRentalUnitPrice(), this.itemDamageValue());
  }

  /** Back to the company figure, straight from the list — the same PUT with null; the item's other figures stay. */
  /** Removes the article's own multiplier, after saying which figure goes. */
  async clearItem(row: ProductOverdueMultiplier): Promise<void> {
    const yes = await this.confirm.ask({
      title: this.i18n.t('rent.settings.perItemClearTitle', { name: localizedName(row, this.i18n.lang()) || row.sku }),
      message: this.i18n.t('rent.settings.perItemClearMessage', { m: row.overdueMultiplier ?? '' }),
      confirmLabel: this.i18n.t('rent.settings.perItemClearAction'),
      cancelLabel: this.i18n.t('confirm.keep'),
      danger: true,
    });
    if (!yes) return;
    if (this.productId() === row.productId) this.itemMultiplier.set(null);
    return this.writeItem(row.productId, null, row.rentalUnitPrice, row.rentalDamageUnitPrice ?? null);
  }

  private async writeItem(
    id: string,
    multiplier: number | null,
    rentalUnitPrice: number,
    damageValue: number | null,
  ): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.itemSaved.set(null);
    try {
      const result = await this.api.setProductMultiplier(id, multiplier, rentalUnitPrice, damageValue);
      this.itemSaved.set(this.i18n.t('rent.settings.perItemSaved', {
        sku: result.sku,
        price: this.i18n.money(result.rentalUnitPrice),
      }));
      await this.overridesPager.reload();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
