import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { RegionalService } from '../../../core/regional.service';
import { localDay } from '../../../core/rentals';
import { DateInputComponent } from '../../../ui/date-input.component';
import { DialogComponent } from '../../../ui/dialog.component';
import { FormSectionComponent } from '../../../ui/form-section.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatDay } from '../backoffice-format';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { LineProductDto, ManufacturingApiService, MaterialDto, ProductionLineSummary, UnitDto } from './manufacturing-api.service';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { MFG, mfgCan, mfgError, qty } from './manufacturing-shared';
import {
  CreateOrderRequest,
  ORDER_STATUSES,
  OrderPreview,
  ProductionApiService,
  ProductionOrderStatus,
  ProductionOrderSummary,
  requestId,
} from './production-api.service';

/**
 * Manufacturing › Production orders (Phase 2): every order on the lines the reader reaches — open, held,
 * waiting for completion, completed, cancelled — with what was planned, what has been accepted into stock
 * and whether it is late. A new order is made from the line's approved recipe; before it is made the screen
 * shows what each material needs against what the line can promise, and a shortage is refused unless the
 * reader may start short and says why.
 */
@Component({
  selector: 'app-mfg-orders',
  standalone: true,
  imports: [
    FormsModule, ...PAGE_CHROME, ...MENU, DateInputComponent, DialogComponent, FormSectionComponent, ListFooterComponent, StatusComponent,
    TableStateComponent,
  ],
  template: `
    @if (canCreate() && enabled()) {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="openCreate()"><span aria-hidden="true">+</span> {{ i18n.t('mfg.orders.new') }}</button>
      </ng-template>
    }
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter ui-filter--search">
          <label for="mo-q">{{ i18n.t('products.search') }}</label>
          <input id="mo-q" type="search" [ngModel]="query()" (ngModelChange)="query.set($event)" />
        </div>
        <div class="ui-filter">
          <label for="mo-line">{{ i18n.t('mfg.boms.line') }}</label>
          <select id="mo-line" [ngModel]="lineFilter()" (ngModelChange)="lineFilterChange.emit($event)">
            <option [ngValue]="null">{{ i18n.t('mfg.orders.allLines') }}</option>
            @for (l of lines(); track l.id) {
              <option [ngValue]="l.id">{{ l.code }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="mo-status">{{ i18n.t('common.state') }}</label>
          <select id="mo-status" [ngModel]="status()" (ngModelChange)="status.set($event)">
            <option [ngValue]="null">{{ i18n.t('mfg.boms.allStates') }}</option>
            @for (s of statuses; track s) {
              <option [ngValue]="s">{{ i18n.t('mfg.orderStatus.' + s) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="mo-late">{{ i18n.t('mfg.filter.late') }}</label>
          <select id="mo-late" [ngModel]="lateFilter()" (ngModelChange)="lateFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
            <option ngValue="no">{{ i18n.t('filters.no') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="mo-short">{{ i18n.t('mfg.orders.startedShort') }}</label>
          <select id="mo-short" [ngModel]="shortFilter()" (ngModelChange)="shortFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
            <option ngValue="no">{{ i18n.t('filters.no') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="mo-from">{{ i18n.t('filters.from') }}</label>
          <app-date-input id="mo-from" [ngModel]="endFrom()" (ngModelChange)="endFrom.set($event || '')" />
        </div>
        <div class="ui-filter">
          <label for="mo-to">{{ i18n.t('filters.to') }}</label>
          <app-date-input id="mo-to" [ngModel]="endTo()" (ngModelChange)="endTo.set($event || '')" />
        </div>
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="pager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (!enabled()) {
        <p class="notice warn" role="status">{{ i18n.t('mfg.operations.off') }}</p>
      }
      @if (error() && pager.loaded()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      <section class="ui-surface" [attr.aria-busy]="pager.loading()">
        @if (error() && !pager.loaded()) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!pager.loaded()) {
          <app-table-state kind="loading" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.orders.none')">
            @if (canCreate() && enabled()) {
              <button type="button" class="btn-primary" (click)="openCreate()">{{ i18n.t('mfg.orders.new') }}</button>
            }
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.orders.number') }}</th>
                  <th>{{ i18n.t('mfg.boms.product') }}</th>
                  <th>{{ i18n.t('mfg.boms.line') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.planned') }}</th>
                  <th class="num">{{ i18n.t('mfg.orders.accepted') }}</th>
                  <th>{{ i18n.t('mfg.orders.plannedEnd') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (o of pager.rows(); track o.id) {
                  <tr [class.is-muted]="o.status === 'Cancelled'">
                    <td class="mono" [attr.data-label]="i18n.t('mfg.orders.number')">
                      <button type="button" class="ui-link" (click)="open.emit(o.id)"><span dir="ltr">{{ o.number }}</span></button>
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.boms.product')">
                      {{ productName(o) }} <span class="ui-sub mono" dir="ltr">{{ o.productSku }}</span>
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.boms.line')">{{ o.lineCode }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(o.status)">{{ i18n.t('mfg.orderStatus.' + o.status) }}</app-status>
                      @if (o.late) {
                        <span class="ui-sub">{{ i18n.t('mfg.orders.lateBy', { n: o.overdueDays }) }}</span>
                      }
                      @if (o.startedShort) {
                        <span class="ui-sub">{{ i18n.t('mfg.orders.startedShort') }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.planned')"><span dir="ltr">{{ qty(o.plannedBaseQty) }} {{ o.baseUnitCode }}</span></td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.accepted')"><span dir="ltr">{{ qty(o.acceptedBaseQty) }} {{ o.baseUnitCode }}</span></td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.orders.plannedEnd')">{{ day(o.plannedEnd) }}</td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + o.number">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="open.emit(o.id)">{{ i18n.t('mfg.orders.open') }}</button>
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

    <app-dialog [open]="creating()" size="lg" [heading]="i18n.t('mfg.orders.new')" (dismiss)="creating.set(false)">
      @if (creating()) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          <app-form-section [heading]="i18n.t('mfg.orders.what')" [columns]="2">
            <div class="bo-field">
              <label for="mo-f-line">{{ i18n.t('mfg.boms.line') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <select id="mo-f-line" [(ngModel)]="form.lineId" (ngModelChange)="lineChanged()">
                @for (l of activeLines(); track l.id) {
                  <option [ngValue]="l.id">{{ l.code }} — {{ lineName(l) }}</option>
                }
              </select>
            </div>
            <div class="bo-field">
              <label for="mo-f-product">{{ i18n.t('mfg.boms.product') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <select id="mo-f-product" [(ngModel)]="form.productId" (ngModelChange)="productChanged()">
                @for (p of lineProducts(); track p.productId) {
                  <option [ngValue]="p.productId">{{ p.sku }} — {{ localName(p) }}</option>
                }
              </select>
              @if (!lineProducts().length) {
                <p class="bo-hint">{{ i18n.t('mfg.orders.noLineProducts') }}</p>
              }
            </div>
            <div class="bo-field">
              <label for="mo-f-qty">{{ i18n.t('mfg.orders.planned') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <div class="pair">
                <input id="mo-f-qty" type="number" min="0" step="any" class="num mono" [(ngModel)]="form.plannedQty" (ngModelChange)="preview.set(null)" />
                <select [attr.aria-label]="i18n.t('mfg.common.unit')" [(ngModel)]="form.plannedUnitId" (ngModelChange)="preview.set(null)">
                  @for (u of productUnits(); track u.id) {
                    <option [ngValue]="u.id">{{ u.code }}</option>
                  }
                </select>
              </div>
            </div>
            <div class="bo-field">
              <label for="mo-f-start">{{ i18n.t('mfg.orders.plannedStart') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <app-date-input id="mo-f-start" [(ngModel)]="form.plannedStart" />
            </div>
            <div class="bo-field">
              <label for="mo-f-end">{{ i18n.t('mfg.orders.plannedEnd') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <app-date-input id="mo-f-end" [(ngModel)]="form.plannedEnd" />
            </div>
            <div class="bo-field">
              <label for="mo-f-notes">{{ i18n.t('mfg.common.note') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <input id="mo-f-notes" maxlength="1000" [(ngModel)]="form.notes" />
            </div>
          </app-form-section>

          <app-form-section [heading]="i18n.t('mfg.orders.availability')" [note]="i18n.t('mfg.orders.availabilityHint')" [columns]="1">
            <div>
              <button type="button" class="btn-ghost" [disabled]="busy() || !ready()" (click)="check()">{{ i18n.t('mfg.orders.check') }}</button>
            </div>
            @if (preview(); as p) {
              <p class="bo-hint">{{ i18n.t('mfg.orders.fromRecipe', { v: p.bomVersion, batches: qty(p.batches) }) }}</p>
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('mfg.bom.component', { n: '' }) }}</th>
                      <th class="num">{{ i18n.t('mfg.orders.required') }}</th>
                      <th class="num">{{ i18n.t('mfg.orders.atLine') }}</th>
                      <th class="num">{{ i18n.t('mfg.orders.reservedOthers') }}</th>
                      <th class="num">{{ i18n.t('mfg.orders.free') }}</th>
                      <th class="num">{{ i18n.t('mfg.orders.short') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (m of p.materials; track m.productId) {
                      <tr>
                        <td [attr.data-label]="i18n.t('mfg.bom.component', { n: '' })">{{ localName(m) }} <span class="ui-sub mono" dir="ltr">{{ m.sku }}</span></td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.required')"><span dir="ltr">{{ qty(m.requiredBaseQty) }} {{ m.baseUnitCode }}</span></td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.atLine')"><span dir="ltr">{{ qty(m.stockBaseQty) }}</span></td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.reservedOthers')"><span dir="ltr">{{ qty(m.reservedByOthersBaseQty) }}</span></td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.free')"><span dir="ltr">{{ qty(m.freeBaseQty) }}</span></td>
                        <td class="num mono" [attr.data-label]="i18n.t('mfg.orders.short')">
                          @if (m.shortBaseQty > 0) {
                            <strong dir="ltr">{{ qty(m.shortBaseQty) }}</strong>
                          } @else {
                            —
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              @if (p.short) {
                <p class="notice warn">{{ i18n.t('mfg.orders.shortNotice') }}</p>
                @if (canStartShort()) {
                  <div class="bo-check">
                    <input id="mo-f-short" type="checkbox" [(ngModel)]="form.startShort" />
                    <label for="mo-f-short">{{ i18n.t('mfg.orders.startShort') }}</label>
                  </div>
                  @if (form.startShort) {
                    <div class="bo-field">
                      <label for="mo-f-reason">{{ i18n.t('mfg.orders.shortReason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                      <input id="mo-f-reason" maxlength="400" [(ngModel)]="form.shortageReason" />
                    </div>
                  }
                }
              } @else {
                <p class="notice ok">{{ i18n.t('mfg.orders.allFree') }}</p>
              }
            }
          </app-form-section>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="creating.set(false)">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy() || !ready()" (click)="create()">{{ i18n.t('mfg.orders.create') }}</button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .editor {
        display: grid;
        gap: var(--sp-3);
      }
      .pair {
        display: flex;
        gap: var(--sp-2);
      }
      .pair input {
        flex: 1 1 auto;
        min-inline-size: 0;
      }
      .pair select {
        flex: 0 0 6rem;
      }
    `,
  ],
})
export class MfgOrdersComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly production = inject(ProductionApiService);
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly auth = inject(AuthService);
  private readonly regional = inject(RegionalService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** The line in the address (`?line=`). */
  readonly lineFilter = input<string | null>(null);
  readonly open = output<string>();
  readonly lineFilterChange = output<string | null>();

  readonly statuses = ORDER_STATUSES;
  readonly qty = qty;
  readonly lines = signal<ProductionLineSummary[]>([]);
  readonly units = signal<UnitDto[]>([]);
  readonly materials = signal<MaterialDto[]>([]);
  readonly lineProducts = signal<LineProductDto[]>([]);
  readonly enabled = signal(true);
  readonly busy = signal(false);
  readonly query = signal('');
  readonly status = signal<ProductionOrderStatus | null>(null);
  // Late, started short, and the planned end between two company days (filtered on the server).
  readonly lateFilter = signal<'all' | 'yes' | 'no'>('all');
  readonly shortFilter = signal<'all' | 'yes' | 'no'>('all');
  readonly endFrom = signal('');
  readonly endTo = signal('');
  readonly creating = signal(false);
  readonly formError = signal<string | null>(null);
  readonly preview = signal<OrderPreview | null>(null);
  form: CreateOrderRequest = this.emptyForm();

  readonly canCreate = computed(() => mfgCan(this.auth.currentUser(), MFG.ordersCreate));
  readonly canStartShort = computed(() => mfgCan(this.auth.currentUser(), MFG.ordersStartShort));
  readonly activeLines = computed(() => this.lines().filter((l) => l.isActive));
  readonly filtered = computed(
    () =>
      !!this.query().trim() ||
      !!this.status() ||
      this.lateFilter() !== 'all' ||
      this.shortFilter() !== 'all' ||
      !!this.endFrom() ||
      !!this.endTo(),
  );
  /**
   * The orders a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"): the
   * line in the address, the state, late, started short, the planned end (company days, ISO) and the search run
   * there over every order the reader reaches; a change goes back to page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.orders({ skip: page.skip, take: page.take }, page.filters),
    {
      key: 'mfg.orders',
      filters: () => ({
        lineId: this.lineFilter(),
        status: this.status(),
        q: this.query().trim(),
        late: this.lateFilter(),
        startedShort: this.shortFilter(),
        endFrom: this.endFrom(),
        endTo: this.endTo(),
      }),
    },
  );

  /** The list could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.query.set('');
    this.status.set(null);
    this.lateFilter.set('all');
    this.shortFilter.set('all');
    this.endFrom.set('');
    this.endTo.set('');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.orders.lead'));
    this.production.operationsStatus().then((s) => this.enabled.set(s.stockOperationsEnabled), () => undefined);
    this.api.lines(true).then((l) => this.lines.set(l), () => undefined);
  }

  /** Asks again for the page on screen. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  productName(o: ProductionOrderSummary): string {
    return localizedName({ nameAr: o.productNameAr, nameFr: o.productNameFr, nameEn: o.productNameEn }, this.i18n.lang()) || o.productSku;
  }

  localName(x: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(x, this.i18n.lang());
  }

  lineName(l: ProductionLineSummary): string {
    return localizedName(l, this.i18n.lang());
  }

  day(value: string): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  tone(status: ProductionOrderStatus) {
    return toneOf('productionOrder', status);
  }

  private today(): string {
    return localDay(new Date().toISOString(), this.regional.settings().timeZone);
  }

  private emptyForm(): CreateOrderRequest {
    return {
      lineId: '', productId: '', plannedQty: 0, plannedUnitId: '', plannedStart: '', plannedEnd: '', notes: null,
      clientRequestId: requestId(), startShort: false, shortageReason: null,
    };
  }

  /** The product's base unit and its own units: what a planned quantity can be written in. */
  productUnits(): UnitDto[] {
    const product = this.materials().find((m) => m.productId === this.form.productId);
    if (!product?.baseUnitId) return [];
    const ids = new Set([product.baseUnitId, ...product.units.map((u) => u.unitId)]);
    const base = this.units().find((u) => u.id === product.baseUnitId);
    return this.units().filter((u) => ids.has(u.id) || (base && u.dimension === base.dimension && u.dimension !== 'Other' && u.isActive));
  }

  ready(): boolean {
    const f = this.form;
    return !!f.lineId && !!f.productId && !!f.plannedUnitId && Number(f.plannedQty) > 0 && !!f.plannedStart && !!f.plannedEnd;
  }

  async openCreate(): Promise<void> {
    this.form = this.emptyForm();
    this.form.lineId = this.lineFilter() ?? this.activeLines()[0]?.id ?? '';
    this.form.plannedStart = this.today();
    this.form.plannedEnd = this.today();
    this.preview.set(null);
    this.formError.set(null);
    this.creating.set(true);
    try {
      const [units, materials] = await Promise.all([this.api.units(), this.api.materials(null, false)]);
      this.units.set(units);
      this.materials.set(materials);
      await this.lineChanged();
    } catch (e) {
      this.formError.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  async lineChanged(): Promise<void> {
    this.preview.set(null);
    this.lineProducts.set([]);
    if (!this.form.lineId) return;
    const line = await this.api.line(this.form.lineId);
    this.lineProducts.set(line.products.filter((p) => p.role === 'Finished' || p.role === 'SemiFinished'));
    if (!this.lineProducts().some((p) => p.productId === this.form.productId)) this.form.productId = this.lineProducts()[0]?.productId ?? '';
    this.productChanged();
  }

  productChanged(): void {
    this.preview.set(null);
    const product = this.materials().find((m) => m.productId === this.form.productId);
    this.form.plannedUnitId = product?.baseUnitId ?? '';
  }

  private request(): CreateOrderRequest {
    return {
      ...this.form,
      plannedQty: Number(this.form.plannedQty),
      notes: this.form.notes?.trim() || null,
      shortageReason: this.form.startShort ? this.form.shortageReason?.trim() || null : null,
    };
  }

  async check(): Promise<void> {
    this.busy.set(true);
    this.formError.set(null);
    try {
      this.preview.set(await this.production.preview(this.request()));
    } catch (e) {
      this.formError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  async create(): Promise<void> {
    this.busy.set(true);
    this.formError.set(null);
    try {
      const created = await this.production.create(this.request());
      this.creating.set(false);
      this.open.emit(created.order.id);
    } catch (e) {
      this.formError.set(mfgError(e, (k) => this.i18n.t(k)));
      // A refusal may be a shortage: show what is short.
      if (!this.preview()) await this.check().catch(() => undefined);
    } finally {
      this.busy.set(false);
    }
  }
}
