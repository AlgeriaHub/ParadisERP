import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { ConfirmService } from '../../../ui/confirm.service';
import { saveRequest } from '../../../ui/confirm-requests';
import { DialogComponent } from '../../../ui/dialog.component';
import { FormSectionComponent } from '../../../ui/form-section.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import {
  ClassifyMaterialRequest,
  MANUFACTURING_ROLES,
  ManufacturingApiService,
  ManufacturingRole,
  MaterialDto,
  SpecFieldDto,
  UnitDto,
} from './manufacturing-api.service';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError, qty } from './manufacturing-shared';
import { MfgSpecValuesComponent, SpecRow, describeSpecs, specRequest, specRows } from './spec-values.component';

interface UnitRow {
  unitId: string;
  factorToBase: number;
  forPurchase: boolean;
  forProduction: boolean;
  forSale: boolean;
}

/**
 * Manufacturing › Materials (Phase 1 §2–3): what each product is to manufacturing, the unit its stock
 * is counted in, and the other units it comes in with their fixed factor (a carton of these nails is
 * 25 kg). Every product starts as ordinary merchandise and behaves exactly as before; a raw material,
 * a consumable or a semi-finished good leaves the till's grid, search and scanner on its next pull —
 * unless it is marked for sale as well (surplus sold to another workshop). Its measured specification
 * (length, thickness, grade…) is optional and carries only the fields that mean something for it.
 */
@Component({
  selector: 'app-mfg-materials',
  standalone: true,
  imports: [
    FormsModule, ...PAGE_CHROME, ...MENU, DialogComponent, FormSectionComponent, ListFooterComponent, MfgSpecValuesComponent, TableStateComponent,
  ],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter ui-filter--search">
          <label for="mt-q">{{ i18n.t('products.search') }}</label>
          <input id="mt-q" type="search" [placeholder]="i18n.t('products.searchHint')" [ngModel]="query()" (ngModelChange)="query.set($event)" />
        </div>
        <div class="ui-filter">
          <label for="mt-role">{{ i18n.t('mfg.materials.role') }}</label>
          <select id="mt-role" [ngModel]="roleFilter()" (ngModelChange)="roleFilter.set($event)">
            <option ngValue="">{{ i18n.t('mfg.materials.allRoles') }}</option>
            <option ngValue="manufacturing">{{ i18n.t('mfg.materials.anyManufacturing') }}</option>
            @for (r of roles; track r) {
              <option [ngValue]="r">{{ i18n.t('mfg.role.' + r) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="mt-till">{{ i18n.t('mfg.materials.till') }}</label>
          <select id="mt-till" [ngModel]="tillFilter()" (ngModelChange)="tillFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="sold">{{ i18n.t('mfg.materials.soldAtTill') }}</option>
            <option ngValue="also">{{ i18n.t('mfg.materials.alsoSold') }}</option>
            <option ngValue="not">{{ i18n.t('mfg.materials.notAtTill') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="mt-base">{{ i18n.t('mfg.materials.baseUnit') }}</label>
          <select id="mt-base" [ngModel]="baseFilter()" (ngModelChange)="baseFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="set">{{ i18n.t('mfg.filter.baseUnitSet') }}</option>
            <option ngValue="missing">{{ i18n.t('mfg.filter.baseUnitMissing') }}</option>
          </select>
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
      @if (error() && pager.loaded()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      <section class="ui-surface" [attr.aria-busy]="pager.loading()">
        @if (error() && !pager.loaded()) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!pager.loaded()) {
          <app-table-state kind="loading" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.materials.none')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('backoffice.products.sku') }}</th>
                  <th>{{ i18n.t('receipt.item') }}</th>
                  <th>{{ i18n.t('mfg.materials.role') }}</th>
                  <th>{{ i18n.t('mfg.materials.baseUnit') }}</th>
                  <th>{{ i18n.t('mfg.materials.otherUnits') }}</th>
                  <th>{{ i18n.t('mfg.materials.till') }}</th>
                  @if (canEdit()) {
                    <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (m of pager.rows(); track m.productId) {
                  <tr>
                    <td class="mono" [attr.data-label]="i18n.t('backoffice.products.sku')"><span dir="ltr">{{ m.sku }}</span></td>
                    <td [attr.data-label]="i18n.t('receipt.item')">
                      @if (canEdit()) {
                        <button type="button" class="ui-link" (click)="edit(m)">{{ name(m) }}</button>
                      } @else {
                        {{ name(m) }}
                      }
                      @if (m.specs.length) {
                        <span class="ui-sub">{{ specs(m) }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.materials.role')">{{ i18n.t('mfg.role.' + m.role) }}</td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.materials.baseUnit')">
                      @if (m.baseUnitCode) {
                        <span dir="ltr">{{ m.baseUnitCode }}</span>
                      } @else {
                        <span class="ui-sub">{{ i18n.t('mfg.materials.noBaseUnit', { unit: m.unit }) }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.materials.otherUnits')">
                      @for (u of m.units; track u.unitId) {
                        <span class="ui-sub mono" dir="ltr">1 {{ u.unitCode }} = {{ qty(u.factorToBase) }} {{ m.baseUnitCode }}</span>
                      } @empty {
                        —
                      }
                    </td>
                    <!-- Said in words beside the role: whether the till offers it. -->
                    <td [attr.data-label]="i18n.t('mfg.materials.till')">
                      {{ i18n.t(m.alsoForSale ? 'mfg.materials.alsoSold' : m.soldAtTill ? 'mfg.materials.soldAtTill' : 'mfg.materials.notAtTill') }}
                    </td>
                    @if (canEdit()) {
                      <td class="ui-cell-menu">
                        <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + m.sku">
                          <span menuTrigger aria-hidden="true">⋯</span>
                          <button type="button" appMenuItem (click)="edit(m)">{{ i18n.t('mfg.materials.classify') }}</button>
                        </app-menu>
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
      <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />
    </div>

    <app-dialog [open]="!!editing()" size="lg" [heading]="editing() ? i18n.t('mfg.materials.classify') + ' — ' + editing()!.sku : ''" (dismiss)="close()">
      @if (editing(); as m) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          <app-form-section [heading]="i18n.t('mfg.materials.classification')" [columns]="2">
            <div class="bo-field">
              <label for="mt-f-role">{{ i18n.t('mfg.materials.role') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <select id="mt-f-role" [(ngModel)]="role">
                @for (r of roles; track r) {
                  <option [ngValue]="r">{{ i18n.t('mfg.role.' + r) }}</option>
                }
              </select>
              <p class="bo-hint">{{ i18n.t('mfg.roleHint.' + role) }}</p>
            </div>
            <div class="bo-field">
              <label for="mt-f-base">{{ i18n.t('mfg.materials.baseUnit') }}</label>
              <select id="mt-f-base" [(ngModel)]="baseUnitId">
                <option [ngValue]="null">{{ i18n.t('mfg.materials.noBaseUnitOption', { unit: m.unit }) }}</option>
                @for (u of choosableUnits(baseUnitId); track u.id) {
                  <option [ngValue]="u.id">{{ u.code }} — {{ unitName(u) }}</option>
                }
              </select>
              <p class="bo-hint">{{ i18n.t('mfg.materials.baseUnitHint') }}</p>
            </div>
            @if (!soldAtTill(role)) {
              <div class="bo-field bo-field--wide">
                <div class="bo-check">
                  <input id="mt-f-sale" type="checkbox" [(ngModel)]="alsoForSale" />
                  <label for="mt-f-sale">{{ i18n.t('mfg.materials.alsoForSale') }}</label>
                </div>
                <p class="bo-hint">{{ i18n.t('mfg.materials.alsoForSaleHint') }}</p>
              </div>
            }
          </app-form-section>

          <app-form-section [heading]="i18n.t('mfg.materials.otherUnits')" [note]="i18n.t('mfg.materials.otherUnitsHint')" [columns]="1">
            @if (!baseUnitId) {
              <p class="bo-hint">{{ i18n.t('mfg.materials.baseFirst') }}</p>
            } @else {
              @for (row of unitRows; track $index; let i = $index) {
                <div class="unit-row">
                  <div class="bo-field">
                    <label [for]="'mt-u-' + i">{{ i18n.t('mfg.common.unit') }}</label>
                    <select [id]="'mt-u-' + i" [(ngModel)]="row.unitId">
                      @for (u of choosableUnits(row.unitId); track u.id) {
                        @if (u.id !== baseUnitId) {
                          <option [ngValue]="u.id">{{ u.code }} — {{ unitName(u) }}</option>
                        }
                      }
                    </select>
                  </div>
                  <div class="bo-field">
                    <label [for]="'mt-f-' + i">{{ i18n.t('mfg.materials.factor', { base: baseCode() }) }}</label>
                    <input [id]="'mt-f-' + i" type="number" min="0" step="any" class="num mono" [(ngModel)]="row.factorToBase" />
                  </div>
                  <div class="flags">
                    <div class="bo-check">
                      <input [id]="'mt-p-' + i" type="checkbox" [(ngModel)]="row.forPurchase" />
                      <label [for]="'mt-p-' + i">{{ i18n.t('mfg.materials.forPurchase') }}</label>
                    </div>
                    <div class="bo-check">
                      <input [id]="'mt-m-' + i" type="checkbox" [(ngModel)]="row.forProduction" />
                      <label [for]="'mt-m-' + i">{{ i18n.t('mfg.materials.forProduction') }}</label>
                    </div>
                    <div class="bo-check">
                      <input [id]="'mt-s-' + i" type="checkbox" [(ngModel)]="row.forSale" />
                      <label [for]="'mt-s-' + i">{{ i18n.t('mfg.materials.forSale') }}</label>
                    </div>
                  </div>
                  <button type="button" class="btn-ghost" (click)="unitRows.splice(i, 1)">{{ i18n.t('mfg.common.remove') }}</button>
                </div>
              }
              <div>
                <button type="button" class="btn-ghost" (click)="addUnit()">+ {{ i18n.t('mfg.materials.addUnit') }}</button>
              </div>
            }
          </app-form-section>
          <app-form-section [heading]="i18n.t('mfg.specs.heading')" [note]="i18n.t('mfg.specs.productHint')" [columns]="1">
            <app-mfg-spec-values idPrefix="mt-spec" [rows]="specRows" [fields]="specFields()" [units]="units()" />
          </app-form-section>
          @if (!soldAtTill(role) && !alsoForSale) {
            <p class="notice warn">{{ i18n.t('mfg.materials.tillWarning') }}</p>
          }
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="close()">{{ i18n.t('common.cancel') }}</button>
        <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">{{ i18n.t('common.save') }}</button>
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
      .unit-row {
        display: grid;
        gap: var(--sp-2) var(--sp-3);
        grid-template-columns: minmax(9rem, 1fr) minmax(9rem, 1fr) auto auto;
        align-items: end;
        padding-block-end: var(--sp-2);
        border-block-end: var(--line) solid var(--border);
      }
      .flags {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2) var(--sp-3);
      }
      @media (max-width: 640px) {
        .unit-row {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class MfgMaterialsComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly confirm = inject(ConfirmService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly canEdit = input(false);
  readonly roles = MANUFACTURING_ROLES;
  readonly qty = qty;

  readonly units = signal<UnitDto[]>([]);
  readonly specFields = signal<SpecFieldDto[]>([]);
  readonly busy = signal(false);
  /** The units or the specification fields could not be read. */
  readonly choicesError = signal<string | null>(null);
  readonly query = signal('');
  /** '' every product; 'manufacturing' any role but ordinary merchandise; or one role. */
  readonly roleFilter = signal<'' | 'manufacturing' | ManufacturingRole>('');
  /** What the till does with it, as the Till column says it. */
  readonly tillFilter = signal<'all' | 'sold' | 'also' | 'not'>('all');
  /** Whether its stock is counted in a base unit yet. */
  readonly baseFilter = signal<'all' | 'set' | 'missing'>('all');

  readonly filtered = computed(
    () => !!this.query().trim() || !!this.roleFilter() || this.tillFilter() !== 'all' || this.baseFilter() !== 'all',
  );

  /**
   * A page of the table from the server (owner, 2026-11-17: "paginate in backend, not frontend"): ten rows to
   * begin with, 20, 50 or 100 at the user's choice; the search (the till's fold), the role, the Till word and
   * the base unit run there over every active product, and a change goes back to page one.
   */
  readonly pager = createServerPager(
    (page) => this.pages.materials({ skip: page.skip, take: page.take }, page.filters),
    {
      key: 'mfg.materials',
      filters: () => ({ q: this.query().trim(), role: this.roleFilter(), till: this.tillFilter(), baseUnit: this.baseFilter() }),
    },
  );

  /** The list (or the units and fields the editor needs) could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : this.choicesError();
  });

  clearFilters(): void {
    this.query.set('');
    this.roleFilter.set('');
    this.tillFilter.set('all');
    this.baseFilter.set('all');
  }

  readonly editing = signal<MaterialDto | null>(null);
  readonly formError = signal<string | null>(null);
  role: ManufacturingRole = 'None';
  baseUnitId: string | null = null;
  unitRows: UnitRow[] = [];
  alsoForSale = false;
  specRows: SpecRow[] = [];

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.materials.lead'));
    await this.loadChoices();
  }

  /** Asks again for the page on screen and the editor's choices. */
  async reload(): Promise<void> {
    await Promise.all([this.pager.reload(), this.loadChoices()]);
  }

  /** The units and specification fields: what the rows' specifications and the editor read. */
  private async loadChoices(): Promise<void> {
    this.choicesError.set(null);
    try {
      const [units, fields] = await Promise.all([this.api.units(), this.api.specFields()]);
      this.units.set(units);
      this.specFields.set(fields);
    } catch (e) {
      this.choicesError.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  name(m: MaterialDto): string {
    return localizedName(m, this.i18n.lang());
  }

  specs(m: MaterialDto): string {
    return describeSpecs(m.specs, this.specFields(), this.i18n.lang());
  }

  unitName(u: UnitDto): string {
    return localizedName(u, this.i18n.lang());
  }

  /** Active units, and the one already chosen even if it has since been switched off. */
  choosableUnits(current: string | null): UnitDto[] {
    return this.units().filter((u) => u.isActive || u.id === current);
  }

  baseCode(): string {
    return this.units().find((u) => u.id === this.baseUnitId)?.code ?? '';
  }

  soldAtTill(role: ManufacturingRole): boolean {
    return role !== 'RawMaterial' && role !== 'Consumable' && role !== 'SemiFinished';
  }

  edit(m: MaterialDto): void {
    this.editing.set(m);
    this.formError.set(null);
    this.role = m.role;
    this.baseUnitId = m.baseUnitId;
    this.unitRows = m.units.map((u) => ({ ...u }));
    this.alsoForSale = m.alsoForSale;
    this.specRows = specRows(m.specs);
  }

  close(): void {
    this.editing.set(null);
  }

  addUnit(): void {
    const free = this.units().find((u) => u.isActive && u.id !== this.baseUnitId && !this.unitRows.some((r) => r.unitId === u.id));
    this.unitRows.push({ unitId: free?.id ?? '', factorToBase: 1, forPurchase: false, forProduction: true, forSale: false });
  }

  async save(): Promise<void> {
    const m = this.editing();
    if (!m) return;
    if (this.unitRows.some((r) => !r.unitId || !(Number(r.factorToBase) > 0))) {
      this.formError.set(this.i18n.t('mfg.error.invalid_factor'));
      return;
    }
    if (!(await this.confirm.ask(saveRequest(this.i18n, m.sku)))) return;
    this.busy.set(true);
    this.formError.set(null);
    try {
      const req: ClassifyMaterialRequest = {
        role: this.role,
        baseUnitId: this.baseUnitId,
        units: this.baseUnitId ? this.unitRows.map((r) => ({ ...r, factorToBase: Number(r.factorToBase) })) : [],
        // The server drops the mark for a role the counter sells anyway.
        alsoForSale: !this.soldAtTill(this.role) && this.alsoForSale,
        specs: specRequest(this.specRows, this.specFields()),
      };
      await this.api.classify(m.productId, req);
      this.close();
      // The row as the server now holds it — or gone from the page, when it no longer fits the filters.
      await this.pager.reload();
    } catch (e) {
      this.formError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }
}
