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
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { TouchedFields } from '../backoffice-forms';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { ManufacturingApiService, UNIT_DIMENSIONS, UnitDimension, UnitDto, UpsertUnitRequest } from './manufacturing-api.service';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError, qty } from './manufacturing-shared';

/**
 * Manufacturing › Units (Phase 1 §3): the units quantities are counted in, and what one is in its
 * dimension's reference unit. The six system units are fixed — only their names change — so 1 kg is
 * 1,000 g for good. Two units convert only within one dimension; packaging converts through a factor
 * written on each product (Materials), never here.
 */
@Component({
  selector: 'app-mfg-units',
  standalone: true,
  imports: [
    FormsModule, ...PAGE_CHROME, ...MENU, DialogComponent, FormSectionComponent, ListFooterComponent, StatusComponent, TableStateComponent,
  ],
  template: `
    @if (canEdit()) {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="openCreate()"><span aria-hidden="true">+</span> {{ i18n.t('mfg.units.new') }}</button>
      </ng-template>
    }
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="un-f-dim">{{ i18n.t('mfg.units.dimension') }}</label>
          <select id="un-f-dim" [ngModel]="dimensionFilter()" (ngModelChange)="dimensionFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            @for (d of dimensions; track d) {
              <option [ngValue]="d">{{ i18n.t('mfg.dimension.' + d) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="un-f-state">{{ i18n.t('common.state') }}</label>
          <select id="un-f-state" [ngModel]="stateFilter()" (ngModelChange)="stateFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="active">{{ i18n.t('common.active') }}</option>
            <option ngValue="inactive">{{ i18n.t('common.inactive') }}</option>
          </select>
        </div>
        @if (filtered()) {
          <div class="ui-toolbar__end">
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          </div>
        }
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
        } @else if (filtered() && !pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.units.code') }}</th>
                  <th>{{ i18n.t('mfg.common.name') }}</th>
                  <th>{{ i18n.t('mfg.units.dimension') }}</th>
                  <th class="num">{{ i18n.t('mfg.units.factor') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  @if (canEdit()) {
                    <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (u of pager.rows(); track u.id) {
                  <tr [class.is-muted]="!u.isActive">
                    <td class="mono" [attr.data-label]="i18n.t('mfg.units.code')"><span dir="ltr">{{ u.code }}</span></td>
                    <td [attr.data-label]="i18n.t('mfg.common.name')">
                      @if (canEdit()) {
                        <button type="button" class="ui-link" (click)="edit(u)">{{ name(u) }}</button>
                      } @else {
                        {{ name(u) }}
                      }
                      @if (u.isSystem) {
                        <span class="ui-sub">{{ i18n.t('mfg.units.system') }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.units.dimension')">{{ i18n.t('mfg.dimension.' + u.dimension) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.units.factor')">
                      @if (u.dimension === 'Other') {
                        <span class="ui-sub">{{ i18n.t('mfg.units.noStandard') }}</span>
                      } @else {
                        <span dir="ltr">1 {{ u.code }} = {{ qty(u.factorToReference) }} {{ reference(u.dimension) }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(u.isActive)">{{ i18n.t(u.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                    </td>
                    @if (canEdit()) {
                      <td class="ui-cell-menu">
                        <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + u.code">
                          <span menuTrigger aria-hidden="true">⋯</span>
                          <button type="button" appMenuItem (click)="edit(u)">{{ i18n.t('common.edit') }}</button>
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

      <!-- The conversion as the server makes it: the same route every recipe uses. -->
      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('mfg.units.convertTitle') }}</h2>
        </div>
        <div class="bo-panel__body">
          <div class="convert">
            <div class="bo-field">
              <label for="cv-qty">{{ i18n.t('mfg.common.quantity') }}</label>
              <input id="cv-qty" type="number" min="0" step="any" class="num mono" [(ngModel)]="convertQty" />
            </div>
            <div class="bo-field">
              <label for="cv-from">{{ i18n.t('mfg.units.from') }}</label>
              <select id="cv-from" [(ngModel)]="convertFrom">
                @for (u of activeUnits(); track u.id) {
                  <option [ngValue]="u.id">{{ u.code }} — {{ name(u) }}</option>
                }
              </select>
            </div>
            <div class="bo-field">
              <label for="cv-to">{{ i18n.t('mfg.units.to') }}</label>
              <select id="cv-to" [(ngModel)]="convertTo">
                @for (u of activeUnits(); track u.id) {
                  <option [ngValue]="u.id">{{ u.code }} — {{ name(u) }}</option>
                }
              </select>
            </div>
            <div class="bo-actions bo-actions--start">
              <button type="button" class="btn-ghost" [disabled]="!convertFrom || !convertTo" (click)="convert()">{{ i18n.t('mfg.units.convert') }}</button>
            </div>
          </div>
          @if (convertResult(); as r) {
            <p class="notice ok" role="status"><span dir="ltr" class="mono">{{ r }}</span></p>
          }
          @if (convertError()) {
            <p class="notice warn" role="alert">{{ convertError() }}</p>
          }
        </div>
      </section>
    </div>

    <app-dialog [open]="editorOpen()" size="md" [heading]="editing() ? i18n.t('common.edit') + ' — ' + editing()!.code : i18n.t('mfg.units.new')" (dismiss)="close()">
      @if (editorOpen()) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          @if (editing()?.isSystem) {
            <p class="notice">{{ i18n.t('mfg.units.systemNote') }}</p>
          }
          <app-form-section [heading]="i18n.t('mfg.units.definition')" [columns]="3">
            <div class="bo-field" [class.is-invalid]="touched.missing('code', form.code)">
              <label for="un-code">{{ i18n.t('mfg.units.code') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="un-code" class="mono" dir="ltr" maxlength="16" [disabled]="!!editing()?.isSystem" [(ngModel)]="form.code" (blur)="touched.touch('code')" />
              @if (touched.missing('code', form.code)) {
                <span class="bo-error">{{ i18n.t('common.fieldRequired') }}</span>
              }
            </div>
            <div class="bo-field">
              <label for="un-dim">{{ i18n.t('mfg.units.dimension') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <select id="un-dim" [disabled]="!!editing()?.isSystem" [(ngModel)]="form.dimension">
                @for (d of dimensions; track d) {
                  <option [ngValue]="d">{{ i18n.t('mfg.dimension.' + d) }}</option>
                }
              </select>
            </div>
            @if (form.dimension !== 'Other') {
              <div class="bo-field">
                <label for="un-factor">{{ i18n.t('mfg.units.factorIn', { unit: reference(form.dimension) }) }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <input id="un-factor" type="number" min="0" step="any" class="num mono" [disabled]="!!editing()?.isSystem" [(ngModel)]="form.factorToReference" />
                <p class="bo-hint">{{ i18n.t('mfg.units.factorHint') }}</p>
              </div>
            }
          </app-form-section>
          <app-form-section [heading]="i18n.t('common.group.names')" [columns]="3">
            <div class="bo-field" [class.is-invalid]="touched.missing('nameAr', form.nameAr)">
              <label for="un-ar">{{ i18n.t('field.nameAr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="un-ar" dir="rtl" [(ngModel)]="form.nameAr" (blur)="touched.touch('nameAr')" />
            </div>
            <div class="bo-field" [class.is-invalid]="touched.missing('nameFr', form.nameFr)">
              <label for="un-fr">{{ i18n.t('field.nameFr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="un-fr" dir="ltr" [(ngModel)]="form.nameFr" (blur)="touched.touch('nameFr')" />
            </div>
            <div class="bo-field">
              <label for="un-en">{{ i18n.t('field.nameEn') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <input id="un-en" dir="ltr" [(ngModel)]="form.nameEn" />
            </div>
          </app-form-section>
          @if (!editing()?.isSystem) {
            <div class="bo-check">
              <input id="un-active" type="checkbox" [(ngModel)]="form.isActive" />
              <label for="un-active">{{ i18n.t('common.active') }}</label>
            </div>
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
      .convert {
        display: grid;
        gap: var(--sp-3);
        grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
        align-items: end;
      }
    `,
  ],
})
export class MfgUnitsComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly confirm = inject(ConfirmService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** Whether the reader may add and edit units (`manufacturing.settings`); otherwise the list only. */
  readonly canEdit = input(false);

  /** Every unit, for the converter below the list (a picker, not the list). */
  readonly units = signal<UnitDto[]>([]);
  readonly activeUnits = computed(() => this.units().filter((u) => u.isActive));
  readonly busy = signal(false);
  /** The converter's units could not be read. */
  readonly unitsError = signal<string | null>(null);
  readonly editorOpen = signal(false);
  readonly editing = signal<UnitDto | null>(null);
  readonly formError = signal<string | null>(null);
  readonly touched = new TouchedFields();
  readonly dimensions = UNIT_DIMENSIONS;
  readonly qty = qty;
  form: UpsertUnitRequest = this.emptyForm();

  convertQty = 1;
  convertFrom = '';
  convertTo = '';
  readonly convertResult = signal<string | null>(null);
  readonly convertError = signal<string | null>(null);

  // What a unit measures, and whether it is switched on — filtered on the server.
  readonly dimensionFilter = signal<'all' | UnitDimension>('all');
  readonly stateFilter = signal<'all' | 'active' | 'inactive'>('all');
  readonly filtered = computed(() => this.dimensionFilter() !== 'all' || this.stateFilter() !== 'all');
  /** The units a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"). */
  readonly pager = createServerPager(
    (page) => this.pages.units({ skip: page.skip, take: page.take }, page.filters),
    { key: 'mfg.units', filters: () => ({ dimension: this.dimensionFilter(), state: this.stateFilter() }) },
  );

  /** The list (or the converter's units) could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : this.unitsError();
  });

  clearFilters(): void {
    this.dimensionFilter.set('all');
    this.stateFilter.set('all');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.units.lead'));
    await this.loadUnits();
  }

  /** Asks again for the page on screen and the converter's units — after a save. */
  async reload(): Promise<void> {
    await Promise.all([this.pager.reload(), this.loadUnits()]);
  }

  /** Every unit for the converter, which offers the active ones. */
  private async loadUnits(): Promise<void> {
    this.unitsError.set(null);
    try {
      const units = await this.api.units();
      this.units.set(units);
      this.convertFrom ||= units.find((u) => u.code === 'kg')?.id ?? units[0]?.id ?? '';
      this.convertTo ||= units.find((u) => u.code === 'g')?.id ?? units[0]?.id ?? '';
    } catch (e) {
      this.unitsError.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  name(u: UnitDto): string {
    return localizedName(u, this.i18n.lang());
  }

  /** The dimension's reference unit, by its code, as the factor is stated against it. */
  reference(dimension: UnitDimension): string {
    return this.i18n.t('mfg.reference.' + dimension);
  }

  tone(active: boolean) {
    return toneOf('active', active ? 'Active' : 'Inactive');
  }

  private emptyForm(): UpsertUnitRequest {
    return { code: '', nameAr: '', nameFr: '', nameEn: '', dimension: 'Count', factorToReference: 1, isActive: true };
  }

  openCreate(): void {
    this.editing.set(null);
    this.form = this.emptyForm();
    this.touched.reset();
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  edit(u: UnitDto): void {
    this.editing.set(u);
    this.form = { code: u.code, nameAr: u.nameAr, nameFr: u.nameFr, nameEn: u.nameEn, dimension: u.dimension, factorToReference: u.factorToReference, isActive: u.isActive };
    this.touched.reset();
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  close(): void {
    this.editorOpen.set(false);
    this.editing.set(null);
  }

  async save(): Promise<void> {
    this.touched.touchAll('code', 'nameAr', 'nameFr');
    if (!this.form.code.trim() || !this.form.nameAr.trim() || !this.form.nameFr.trim()) {
      this.formError.set(this.i18n.t('mfg.common.fillRequired'));
      return;
    }
    if (this.form.dimension !== 'Other' && !(Number(this.form.factorToReference) > 0)) {
      this.formError.set(this.i18n.t('mfg.error.invalid_factor'));
      return;
    }
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.form.code)))) return;
    this.busy.set(true);
    this.formError.set(null);
    try {
      await this.api.saveUnit(this.editing()?.id ?? null, {
        ...this.form,
        code: this.form.code.trim(),
        nameEn: this.form.nameEn?.trim() || null,
        factorToReference: Number(this.form.factorToReference) || 1,
      });
      this.close();
      await this.reload();
    } catch (e) {
      this.formError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  async convert(): Promise<void> {
    this.convertResult.set(null);
    this.convertError.set(null);
    const from = this.units().find((u) => u.id === this.convertFrom);
    const to = this.units().find((u) => u.id === this.convertTo);
    try {
      const r = await this.api.convert(Number(this.convertQty) || 0, this.convertFrom, this.convertTo);
      this.convertResult.set(`${qty(r.quantity)} ${from?.code ?? ''} = ${qty(r.result)} ${to?.code ?? ''}`);
    } catch (e) {
      this.convertError.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }
}
