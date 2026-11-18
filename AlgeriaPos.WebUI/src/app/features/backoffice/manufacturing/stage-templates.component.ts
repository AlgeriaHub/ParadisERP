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
import {
  JobRoleDto,
  ManufacturingApiService,
  MaterialDto,
  PRODUCED_ROLES,
  StageDto,
  StageRequest,
  StageTemplateDto,
  UpsertStageTemplateRequest,
} from './manufacturing-api.service';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError } from './manufacturing-shared';
import { MfgStagesEditorComponent, emptyStage } from './stages-editor.component';

/** A stored stage as the editor edits it. */
export function stageRequest(s: StageDto): StageRequest {
  return {
    nameAr: s.nameAr,
    nameFr: s.nameFr,
    nameEn: s.nameEn,
    estimatedMinutes: s.estimatedMinutes,
    expectedWorkers: s.expectedWorkers,
    isQualityCheckpoint: s.isQualityCheckpoint,
    qualityNote: s.qualityNote,
    isOutput: s.isOutput,
    roles: s.roles.map((r) => ({ jobRoleId: r.jobRoleId, count: r.count })),
  };
}

/**
 * Manufacturing › Stage templates (Phase 1 §5): reusable sequences of stages with their duration, crew,
 * job roles and quality checkpoints, for any product or meant for one. A recipe copies a template's
 * stages and keeps its own copy, so editing a template here never rewrites an approved recipe.
 */
@Component({
  selector: 'app-mfg-stage-templates',
  standalone: true,
  imports: [
    FormsModule,
    ...PAGE_CHROME,
    ...MENU,
    DialogComponent,
    FormSectionComponent,
    ListFooterComponent,
    MfgStagesEditorComponent,
    StatusComponent,
    TableStateComponent,
  ],
  template: `
    @if (canEdit()) {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="openCreate()"><span aria-hidden="true">+</span> {{ i18n.t('mfg.templates.new') }}</button>
      </ng-template>
    }
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="st-f-product">{{ i18n.t('mfg.templates.product') }}</label>
          <select id="st-f-product" [ngModel]="productFilter()" (ngModelChange)="productFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="any">{{ i18n.t('mfg.templates.anyProduct') }}</option>
            <option ngValue="one">{{ i18n.t('mfg.filter.oneProduct') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="st-f-state">{{ i18n.t('common.state') }}</label>
          <select id="st-f-state" [ngModel]="stateFilter()" (ngModelChange)="stateFilter.set($event)">
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
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.templates.none')">
            @if (canEdit()) {
              <button type="button" class="btn-primary" (click)="openCreate()">{{ i18n.t('mfg.templates.new') }}</button>
            }
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.common.code') }}</th>
                  <th>{{ i18n.t('mfg.common.name') }}</th>
                  <th>{{ i18n.t('mfg.templates.product') }}</th>
                  <th>{{ i18n.t('mfg.templates.stages') }}</th>
                  <th class="num">{{ i18n.t('mfg.templates.duration') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  @if (canEdit()) {
                    <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (t of pager.rows(); track t.id) {
                  <tr [class.is-muted]="!t.isActive">
                    <td class="mono" [attr.data-label]="i18n.t('mfg.common.code')"><span dir="ltr">{{ t.code }}</span></td>
                    <td [attr.data-label]="i18n.t('mfg.common.name')">
                      @if (canEdit()) {
                        <button type="button" class="ui-link" (click)="edit(t)">{{ name(t) }}</button>
                      } @else {
                        {{ name(t) }}
                      }
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('mfg.templates.product')">
                      @if (t.productSku) {
                        <span dir="ltr">{{ t.productSku }}</span>
                      } @else {
                        <span class="ui-sub">{{ i18n.t('mfg.templates.anyProduct') }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.templates.stages')">{{ stageNames(t) }}</td>
                    <td class="num mono" [attr.data-label]="i18n.t('mfg.templates.duration')">{{ minutes(t) }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(t.isActive)">{{ i18n.t(t.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                    </td>
                    @if (canEdit()) {
                      <td class="ui-cell-menu">
                        <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + t.code">
                          <span menuTrigger aria-hidden="true">⋯</span>
                          <button type="button" appMenuItem (click)="edit(t)">{{ i18n.t('common.edit') }}</button>
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

    <app-dialog [open]="editorOpen()" size="lg" [heading]="editing() ? i18n.t('common.edit') + ' — ' + editing()!.code : i18n.t('mfg.templates.new')" (dismiss)="close()">
      @if (editorOpen()) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          <app-form-section [heading]="i18n.t('common.group.identity')" [columns]="3">
            <div class="bo-field" [class.is-invalid]="touched.missing('code', form.code)">
              <label for="st-code">{{ i18n.t('mfg.common.code') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="st-code" class="mono" dir="ltr" maxlength="24" [(ngModel)]="form.code" (blur)="touched.touch('code')" />
            </div>
            <div class="bo-field" [class.is-invalid]="touched.missing('nameAr', form.nameAr)">
              <label for="st-ar">{{ i18n.t('field.nameAr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="st-ar" dir="rtl" [(ngModel)]="form.nameAr" (blur)="touched.touch('nameAr')" />
            </div>
            <div class="bo-field" [class.is-invalid]="touched.missing('nameFr', form.nameFr)">
              <label for="st-fr">{{ i18n.t('field.nameFr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="st-fr" dir="ltr" [(ngModel)]="form.nameFr" (blur)="touched.touch('nameFr')" />
            </div>
            <div class="bo-field">
              <label for="st-product">{{ i18n.t('mfg.templates.product') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <select id="st-product" [(ngModel)]="form.productId">
                <option [ngValue]="null">{{ i18n.t('mfg.templates.anyProduct') }}</option>
                @for (p of products(); track p.productId) {
                  <option [ngValue]="p.productId">{{ p.sku }} — {{ productName(p) }}</option>
                }
              </select>
            </div>
            <div class="bo-field bo-field--wide">
              <label for="st-desc">{{ i18n.t('mfg.common.description') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <input id="st-desc" maxlength="1000" [(ngModel)]="form.description" />
            </div>
          </app-form-section>
          <app-form-section [heading]="i18n.t('mfg.templates.stages')" [columns]="1">
            <app-mfg-stages-editor [stages]="form.steps" [jobRoles]="activeRoles()" idPrefix="st" />
          </app-form-section>
          <div class="bo-check">
            <input id="st-active" type="checkbox" [(ngModel)]="form.isActive" />
            <label for="st-active">{{ i18n.t('common.active') }}</label>
          </div>
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
    `,
  ],
})
export class MfgStageTemplatesComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly confirm = inject(ConfirmService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly canEdit = input(false);
  readonly jobRoles = signal<JobRoleDto[]>([]);
  readonly activeRoles = computed(() => this.jobRoles().filter((r) => r.isActive));
  readonly products = signal<MaterialDto[]>([]);
  readonly busy = signal(false);
  /** The editor's job roles or products could not be read. */
  readonly pickerError = signal<string | null>(null);
  readonly editorOpen = signal(false);
  readonly editing = signal<StageTemplateDto | null>(null);
  readonly formError = signal<string | null>(null);
  readonly touched = new TouchedFields();
  form: UpsertStageTemplateRequest = this.emptyForm();

  // For any product or meant for one, and whether it is switched on — filtered on the server.
  readonly productFilter = signal<'all' | 'any' | 'one'>('all');
  readonly stateFilter = signal<'all' | 'active' | 'inactive'>('all');
  readonly filtered = computed(() => this.productFilter() !== 'all' || this.stateFilter() !== 'all');
  /** The templates a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"). */
  readonly pager = createServerPager(
    (page) => this.pages.stageTemplates({ skip: page.skip, take: page.take }, page.filters),
    { key: 'mfg.templates', filters: () => ({ product: this.productFilter(), state: this.stateFilter() }) },
  );

  /** The list (or the editor's choices) could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : this.pickerError();
  });

  clearFilters(): void {
    this.productFilter.set('all');
    this.stateFilter.set('all');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.templates.lead'));
    await this.loadChoices();
  }

  /** Asks again for the page on screen and the editor's choices — after a save, or to retry. */
  async reload(): Promise<void> {
    await Promise.all([this.pager.reload(), this.loadChoices()]);
  }

  /** What the editor offers: the job roles and the products a template may be meant for. */
  private async loadChoices(): Promise<void> {
    this.pickerError.set(null);
    try {
      const [roles, materials] = await Promise.all([this.api.jobRoles(), this.api.materials()]);
      this.jobRoles.set(roles);
      this.products.set(materials.filter((m) => PRODUCED_ROLES.includes(m.role)));
    } catch (e) {
      this.pickerError.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  name(t: StageTemplateDto): string {
    return localizedName(t, this.i18n.lang());
  }

  productName(p: MaterialDto): string {
    return localizedName(p, this.i18n.lang());
  }

  stageNames(t: StageTemplateDto): string {
    return t.steps.map((s) => localizedName(s, this.i18n.lang())).join(' → ') || '—';
  }

  minutes(t: StageTemplateDto): string {
    const total = t.steps.reduce((sum, s) => sum + s.estimatedMinutes, 0);
    return total ? this.i18n.t('mfg.common.minutes', { n: total }) : '—';
  }

  tone(active: boolean) {
    return toneOf('active', active ? 'Active' : 'Inactive');
  }

  private emptyForm(): UpsertStageTemplateRequest {
    return { code: '', nameAr: '', nameFr: '', nameEn: null, description: null, productId: null, isActive: true, steps: [emptyStage()] };
  }

  openCreate(): void {
    this.editing.set(null);
    this.form = this.emptyForm();
    this.touched.reset();
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  edit(t: StageTemplateDto): void {
    this.editing.set(t);
    this.form = {
      code: t.code,
      nameAr: t.nameAr,
      nameFr: t.nameFr,
      nameEn: t.nameEn,
      description: t.description,
      productId: t.productId,
      isActive: t.isActive,
      steps: t.steps.map(stageRequest),
    };
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
    const missing = !this.form.code.trim() || !this.form.nameAr.trim() || !this.form.nameFr.trim()
      || this.form.steps.some((s) => !s.nameAr.trim() || !s.nameFr.trim());
    if (missing) {
      this.formError.set(this.i18n.t('mfg.common.fillRequired'));
      return;
    }
    if (!this.form.steps.length) {
      this.formError.set(this.i18n.t('mfg.error.stages_required'));
      return;
    }
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.form.code)))) return;
    this.busy.set(true);
    this.formError.set(null);
    try {
      await this.api.saveStageTemplate(this.editing()?.id ?? null, {
        ...this.form,
        nameEn: this.form.nameEn?.trim() || null,
        description: this.form.description?.trim() || null,
        steps: this.form.steps.map((s) => ({
          ...s,
          estimatedMinutes: Number(s.estimatedMinutes) || 0,
          expectedWorkers: Number(s.expectedWorkers) || 0,
          roles: s.roles.map((r) => ({ jobRoleId: r.jobRoleId, count: Number(r.count) || 1 })),
        })),
      });
      this.close();
      await this.reload();
    } catch (e) {
      this.formError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }
}
