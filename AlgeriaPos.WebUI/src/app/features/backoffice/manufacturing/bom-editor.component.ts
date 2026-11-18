import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { RegionalService } from '../../../core/regional.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { actionRequest, deleteRequest, saveRequest } from '../../../ui/confirm-requests';
import { FormSectionComponent } from '../../../ui/form-section.component';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatDay } from '../backoffice-format';
import { PageChrome } from '../shell/page-chrome';
import {
  BomDto,
  BomLineRequest,
  BomRequirements,
  COMPONENT_ROLES,
  JobRoleDto,
  ManufacturingApiService,
  MaterialDto,
  PRODUCED_ROLES,
  ProductionLineSummary,
  SpecFieldDto,
  StageRequest,
  StageTemplateDto,
  UnitDto,
} from './manufacturing-api.service';
import { mfgError, qty } from './manufacturing-shared';
import { MfgSpecValuesComponent, specRequest, specRows } from './spec-values.component';
import { stageRequest } from './stage-templates.component';
import { MfgStagesEditorComponent } from './stages-editor.component';

interface Draft {
  productId: string;
  productionLineId: string | null;
  batchQuantity: number;
  batchUnitId: string;
  stageTemplateId: string | null;
  notes: string;
  lines: BomLineRequest[];
  stages: StageRequest[];
}

/**
 * One recipe version (`manufacturing/boms/<id>`, or `…/new`): written while it is a draft, read only once
 * it is active or retired. A change to an approved recipe is a new version, copied from it. Below the
 * recipe, what any planned quantity of the product needs — the net quantity, the expected waste and the
 * total in each component's stock unit — calculated by the server, the same way for every screen.
 */
@Component({
  selector: 'app-mfg-bom-editor',
  standalone: true,
  imports: [FormsModule, FormSectionComponent, MfgSpecValuesComponent, MfgStagesEditorComponent, StatusComponent, TableStateComponent],
  template: `
    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (notice()) {
        <p class="notice ok" role="status">{{ notice() }}</p>
      }

      @if (loading()) {
        <section class="ui-surface"><app-table-state kind="loading" /></section>
      } @else {
        @if (bom(); as b) {
          <!-- ---------- what this version is ---------- -->
          <section class="bo-panel">
            <div class="bo-panel__head">
              <div class="title-row">
                <h2 class="bo-panel__title">{{ i18n.t('mfg.bom.version', { n: b.bom.version }) }}</h2>
                <app-status [tone]="tone()">{{ i18n.t('mfg.bomStatus.' + b.bom.status) }}</app-status>
              </div>
              <div class="head-actions">
                @if (b.bom.status === 'Draft' && b.canApprove) {
                  <button type="button" class="btn-primary" [disabled]="busy() || dirty()" (click)="activate()">{{ i18n.t('mfg.bom.activate') }}</button>
                }
                @if (b.bom.status !== 'Draft' && mayWrite()) {
                  <button type="button" class="btn-ghost" [disabled]="busy()" (click)="newVersion()">{{ i18n.t('mfg.bom.newVersion') }}</button>
                }
                @if (b.bom.status === 'Active' && b.canApprove) {
                  <button type="button" class="btn-ghost" [disabled]="busy()" (click)="retire()">{{ i18n.t('mfg.bom.retire') }}</button>
                }
                @if (b.bom.status === 'Draft' && b.canEdit) {
                  <button type="button" class="btn-danger" [disabled]="busy()" (click)="remove()">{{ i18n.t('mfg.bom.deleteDraft') }}</button>
                }
              </div>
            </div>
            <div class="bo-panel__body">
              <p class="bo-hint">{{ i18n.t('mfg.bomExplain.' + b.bom.status) }}</p>
              @if (b.bom.status === 'Draft' && b.canApprove && dirty()) {
                <p class="notice warn">{{ i18n.t('mfg.bom.saveBeforeActivate') }}</p>
              }
              <dl class="facts">
                <div><dt>{{ i18n.t('mfg.bom.createdBy') }}</dt><dd>{{ b.createdBy || '—' }} · {{ day(b.bom.createdAtUtc) }}</dd></div>
                @if (b.bom.activatedAtUtc) {
                  <div><dt>{{ i18n.t('mfg.bom.activatedBy') }}</dt><dd>{{ b.activatedBy || '—' }} · {{ day(b.bom.activatedAtUtc) }}</dd></div>
                }
                @if (b.bom.retiredAtUtc) {
                  <div><dt>{{ i18n.t('mfg.bom.retiredBy') }}</dt><dd>{{ b.retiredBy || '—' }} · {{ day(b.bom.retiredAtUtc) }}</dd></div>
                }
              </dl>
            </div>
          </section>
        }

        <!-- ---------- the recipe ---------- -->
        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t('mfg.bom.recipe') }}</h2>
          </div>
          <div class="bo-panel__body bo-form bo-form--full">
            <app-form-section [heading]="i18n.t('mfg.bom.output')" [columns]="3">
              <div class="bo-field">
                <label for="bm-product">{{ i18n.t('mfg.boms.product') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                @if (isNew()) {
                  <select id="bm-product" [(ngModel)]="draft.productId" (ngModelChange)="productChanged()">
                    <option value="" disabled>{{ i18n.t('mfg.bom.chooseProduct') }}</option>
                    @for (p of outputs(); track p.productId) {
                      <option [ngValue]="p.productId">{{ p.sku }} — {{ name(p) }}</option>
                    }
                  </select>
                  @if (!outputs().length) {
                    <p class="bo-hint">{{ i18n.t('mfg.bom.noOutputs') }}</p>
                  }
                } @else {
                  <p class="bo-fixed" id="bm-product"><span class="mono" dir="ltr">{{ productOf(draft.productId)?.sku }}</span> {{ productOf(draft.productId) ? name(productOf(draft.productId)!) : '' }}</p>
                }
              </div>
              <div class="bo-field">
                <label for="bm-line">{{ i18n.t('mfg.boms.line') }}</label>
                @if (isNew()) {
                  <select id="bm-line" [(ngModel)]="draft.productionLineId">
                    <option [ngValue]="null">{{ i18n.t('mfg.boms.everyLine') }}</option>
                    @for (l of lines(); track l.id) {
                      <option [ngValue]="l.id">{{ l.code }} — {{ name(l) }}</option>
                    }
                  </select>
                  <p class="bo-hint">{{ i18n.t('mfg.bom.lineHint') }}</p>
                } @else {
                  <p class="bo-fixed" id="bm-line">{{ lineCode(draft.productionLineId) }}</p>
                }
              </div>
              <div class="bo-field">
                <label for="bm-batch">{{ i18n.t('mfg.bom.batch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <div class="pair">
                  <input id="bm-batch" type="number" min="0" step="any" class="num mono" [disabled]="!editable()" [(ngModel)]="draft.batchQuantity" />
                  <select [attr.aria-label]="i18n.t('mfg.common.unit')" [disabled]="!editable()" [(ngModel)]="draft.batchUnitId">
                    @for (u of unitsFor(draft.productId, draft.batchUnitId); track u.id) {
                      <option [ngValue]="u.id">{{ u.code }}</option>
                    }
                  </select>
                </div>
                <p class="bo-hint">{{ i18n.t('mfg.bom.batchHint') }}</p>
              </div>
            </app-form-section>

            <app-form-section [heading]="i18n.t('mfg.bom.components')" [note]="i18n.t('mfg.bom.componentsHint')" [columns]="1">
              @if (!draft.lines.length) {
                <p class="bo-hint">{{ i18n.t('mfg.bom.noComponents') }}</p>
              }
              @for (l of draft.lines; track $index; let i = $index) {
                <div class="component">
                  <div class="bo-field component__product">
                    <label [for]="'bc-p-' + i">{{ i18n.t('mfg.bom.component', { n: i + 1 }) }}</label>
                    <select [id]="'bc-p-' + i" [disabled]="!editable()" [(ngModel)]="l.componentProductId" (ngModelChange)="componentChanged(l)">
                      @for (p of components(); track p.productId) {
                        <option [ngValue]="p.productId">{{ p.sku }} — {{ name(p) }} · {{ i18n.t('mfg.role.' + p.role) }}</option>
                      }
                    </select>
                  </div>
                  <div class="bo-field">
                    <label [for]="'bc-q-' + i">{{ i18n.t('mfg.common.quantity') }}</label>
                    <div class="pair">
                      <input [id]="'bc-q-' + i" type="number" min="0" step="any" class="num mono" [disabled]="!editable()" [(ngModel)]="l.quantity" />
                      <select [attr.aria-label]="i18n.t('mfg.common.unit')" [disabled]="!editable()" [(ngModel)]="l.unitId">
                        @for (u of unitsFor(l.componentProductId, l.unitId); track u.id) {
                          <option [ngValue]="u.id">{{ u.code }}</option>
                        }
                      </select>
                    </div>
                  </div>
                  <div class="bo-field">
                    <label [for]="'bc-w-' + i">{{ i18n.t('mfg.bom.waste') }}</label>
                    <input [id]="'bc-w-' + i" type="number" min="0" max="99.99" step="any" class="num mono" [disabled]="!editable()" [(ngModel)]="l.wastePercent" />
                  </div>
                  <div class="bo-field component__wide">
                    <label [for]="'bc-s-' + i">{{ i18n.t('mfg.bom.specification') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
                    <input [id]="'bc-s-' + i" maxlength="400" [disabled]="!editable()" [(ngModel)]="l.specification" [placeholder]="editable() ? i18n.t('mfg.bom.specificationHint') : ''" />
                  </div>
                  <!-- The same specification as figures a later calculation can read; optional, frozen with the recipe. -->
                  @if (editable() || l.specs.length) {
                    <div class="component__wide component__specs">
                      <span class="component__specs-title">{{ i18n.t('mfg.specs.lineHeading') }}</span>
                      <app-mfg-spec-values [idPrefix]="'bc-x-' + i" [rows]="l.specs" [fields]="specFields()" [units]="units()" [disabled]="!editable()" />
                    </div>
                  }
                  @if (editable()) {
                    <div class="component__remove">
                      <button type="button" class="btn-ghost bo-small" (click)="draft.lines.splice(i, 1)">{{ i18n.t('mfg.common.remove') }}</button>
                    </div>
                  }
                </div>
              }
              @if (editable()) {
                <div>
                  <button type="button" class="btn-ghost" [disabled]="!components().length" (click)="addComponent()">+ {{ i18n.t('mfg.bom.addComponent') }}</button>
                  @if (!components().length) {
                    <p class="bo-hint">{{ i18n.t('mfg.bom.noComponentProducts') }}</p>
                  }
                </div>
              }
            </app-form-section>

            <app-form-section [heading]="i18n.t('mfg.bom.stages')" [note]="i18n.t('mfg.bom.stagesHint')" [columns]="1">
              @if (editable()) {
                <div class="bo-field template-pick">
                  <label for="bm-tpl">{{ i18n.t('mfg.bom.copyTemplate') }}</label>
                  <select id="bm-tpl" [ngModel]="draft.stageTemplateId" (ngModelChange)="copyTemplate($event)">
                    <option [ngValue]="null">—</option>
                    @for (t of templatesFor(draft.productId); track t.id) {
                      <option [ngValue]="t.id">{{ t.code }} — {{ name(t) }}</option>
                    }
                  </select>
                </div>
                <app-mfg-stages-editor [stages]="draft.stages" [jobRoles]="activeRoles()" [forBom]="true" idPrefix="bs" />
              } @else if (!draft.stages.length) {
                <p class="bo-hint">{{ i18n.t('mfg.bom.noStages') }}</p>
              } @else {
                <ol class="stage-list">
                  @for (s of draft.stages; track $index) {
                    <li>
                      <strong>{{ name(s) }}</strong>
                      @if (s.estimatedMinutes) {
                        <span class="ui-sub">{{ i18n.t('mfg.common.minutes', { n: s.estimatedMinutes }) }}</span>
                      }
                      @if (s.expectedWorkers) {
                        <span class="ui-sub">{{ i18n.t('mfg.stages.workersN', { n: s.expectedWorkers }) }}</span>
                      }
                      @for (r of s.roles; track r.jobRoleId) {
                        <span class="ui-sub mono">{{ roleCode(r.jobRoleId) }} ×{{ r.count }}</span>
                      }
                      @if (s.isQualityCheckpoint) {
                        <span class="ui-sub">{{ i18n.t('mfg.stages.checkpoint') }}{{ s.qualityNote ? ' — ' + s.qualityNote : '' }}</span>
                      }
                      @if (s.isOutput) {
                        <span class="ui-sub"><strong>{{ i18n.t('mfg.stages.output') }}</strong></span>
                      }
                    </li>
                  }
                </ol>
              }
            </app-form-section>

            <app-form-section [heading]="i18n.t('mfg.common.note')" [columns]="1">
              <div class="bo-field">
                <label for="bm-notes" class="sr-only">{{ i18n.t('mfg.common.note') }}</label>
                <textarea id="bm-notes" rows="2" maxlength="1000" [disabled]="!editable()" [(ngModel)]="draft.notes"></textarea>
              </div>
            </app-form-section>
          </div>
          @if (editable()) {
            <div class="bo-panel__foot sticky-foot">
              <button type="button" class="btn-ghost" (click)="cancelled.emit()">{{ i18n.t('common.cancel') }}</button>
              <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">{{ i18n.t(isNew() ? 'mfg.bom.saveDraft' : 'common.save') }}</button>
            </div>
          }
        </section>

        <!-- ---------- what a planned quantity needs ---------- -->
        @if (bom(); as b) {
          <section class="bo-panel">
            <div class="bo-panel__head">
              <h2 class="bo-panel__title">{{ i18n.t('mfg.req.title') }}</h2>
            </div>
            <div class="bo-panel__body">
              <div class="req-form">
                <div class="bo-field">
                  <label for="rq-qty">{{ i18n.t('mfg.req.planned') }}</label>
                  <input id="rq-qty" type="number" min="0" step="any" class="num mono" [(ngModel)]="planned" />
                </div>
                <div class="bo-field">
                  <label for="rq-unit">{{ i18n.t('mfg.common.unit') }}</label>
                  <select id="rq-unit" [(ngModel)]="plannedUnit">
                    @for (u of unitsFor(b.bom.productId, b.bom.batchUnitId); track u.id) {
                      <option [ngValue]="u.id">{{ u.code }}</option>
                    }
                  </select>
                </div>
                <div class="bo-actions bo-actions--start">
                  <button type="button" class="btn-primary" [disabled]="!(planned > 0) || reqBusy()" (click)="calculate()">{{ i18n.t('mfg.req.calculate') }}</button>
                </div>
              </div>
              @if (reqError()) {
                <p class="notice warn" role="alert">{{ reqError() }}</p>
              }
              @if (requirements(); as r) {
                <!-- The figures isolated left to right, so "2 ctn = 0.5" reads the same inside Arabic text. -->
                <p class="bo-hint">
                  <bdi class="mono" dir="ltr">{{ qty(r.plannedQuantity) }} {{ r.plannedUnitCode }}</bdi> =
                  {{ i18n.t('mfg.req.batchCount', { n: qty(r.batches) }) }}
                </p>
                <div class="ui-scroll">
                  <table class="ui-table ui-table--cards">
                    <thead>
                      <tr>
                        <th>{{ i18n.t('mfg.req.component') }}</th>
                        <th class="num">{{ i18n.t('mfg.req.net') }}</th>
                        <th class="num">{{ i18n.t('mfg.req.waste') }}</th>
                        <th class="num">{{ i18n.t('mfg.req.gross') }}</th>
                        <th class="num">{{ i18n.t('mfg.req.inStockUnit') }}</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (l of r.lines; track l.bomLineId) {
                        <tr>
                          <td [attr.data-label]="i18n.t('mfg.req.component')"><span class="mono" dir="ltr">{{ l.componentSku }}</span> {{ componentName(l) }}</td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.req.net')"><span dir="ltr">{{ qty(l.netQuantity) }} {{ l.unitCode }}</span></td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.req.waste')"><span dir="ltr">{{ qty(l.wasteQuantity) }} {{ l.unitCode }}</span></td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.req.gross')"><span dir="ltr">{{ qty(l.grossQuantity) }} {{ l.unitCode }}</span></td>
                          <td class="num mono" [attr.data-label]="i18n.t('mfg.req.inStockUnit')"><strong dir="ltr">{{ qty(l.baseQuantity) }} {{ l.baseUnitCode }}</strong></td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
              }
            </div>
          </section>
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .title-row,
      .head-actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--sp-2);
      }
      .facts {
        display: grid;
        gap: var(--sp-2) var(--sp-4);
        grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr));
        margin: var(--sp-2) 0 0;
      }
      .facts dt {
        color: var(--muted);
        font-size: var(--fs-sm);
      }
      .facts dd {
        margin: 0;
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
      .component {
        display: grid;
        gap: var(--sp-2) var(--sp-3);
        grid-template-columns: minmax(14rem, 2fr) minmax(10rem, 1fr) minmax(6rem, 0.6fr);
        padding-block-end: var(--sp-3);
        border-block-end: var(--line) solid var(--border);
      }
      .component__wide {
        grid-column: 1 / 3;
      }
      .component__specs {
        grid-column: 1 / -1;
        display: grid;
        gap: var(--sp-1);
      }
      .component__specs-title {
        color: var(--muted);
        font-size: var(--fs-sm);
      }
      .component__remove {
        display: flex;
        align-items: end;
      }
      .template-pick {
        max-inline-size: 24rem;
      }
      .stage-list {
        display: grid;
        gap: var(--sp-2);
        margin: 0;
        padding-inline-start: var(--sp-5);
      }
      .stage-list li {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-1) var(--sp-3);
      }
      .req-form {
        display: grid;
        gap: var(--sp-3);
        grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
        align-items: end;
        margin-block-end: var(--sp-3);
      }
      .sticky-foot {
        position: sticky;
        inset-block-end: 0;
        background: var(--surface);
        z-index: 1;
      }
      @media (max-width: 640px) {
        .component {
          grid-template-columns: 1fr;
        }
        .component__wide {
          grid-column: auto;
        }
      }
    `,
  ],
})
export class MfgBomEditorComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly confirm = inject(ConfirmService);
  private readonly regional = inject(RegionalService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** The version's id; null for a new recipe. */
  readonly id = input<string | null>(null);
  /** A new recipe's line, from the address (`…/new?line=`). */
  readonly startLine = input<string | null>(null);
  /** Whether the reader writes recipes at all (`manufacturing.bom.manage`). */
  readonly mayWrite = input(false);
  /** A version the reader should now be on: after saving a new draft, or after starting a new version. */
  readonly opened = output<string>();
  readonly cancelled = output<void>();
  readonly deleted = output<void>();

  readonly qty = qty;
  readonly bom = signal<BomDto | null>(null);
  readonly materials = signal<MaterialDto[]>([]);
  readonly units = signal<UnitDto[]>([]);
  readonly lines = signal<ProductionLineSummary[]>([]);
  readonly templates = signal<StageTemplateDto[]>([]);
  readonly jobRoles = signal<JobRoleDto[]>([]);
  readonly specFields = signal<SpecFieldDto[]>([]);
  readonly activeRoles = computed(() => this.jobRoles().filter((r) => r.isActive));
  readonly outputs = computed(() => this.materials().filter((m) => PRODUCED_ROLES.includes(m.role) && m.isActive));
  readonly components = computed(() => this.materials().filter((m) => COMPONENT_ROLES.includes(m.role) && m.isActive));
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  readonly isNew = computed(() => !this.id());
  /** A new recipe, or a draft the server lets this reader write. */
  readonly editable = computed(() => this.isNew() || (this.bom()?.bom.status === 'Draft' && !!this.bom()?.canEdit));

  draft: Draft = this.emptyDraft();
  /** The draft as it was loaded or last saved, so activating never approves something unsaved. */
  private savedDraft = '';
  readonly dirty = () => this.editable() && !this.isNew() && JSON.stringify(this.draft) !== this.savedDraft;

  planned = 1;
  plannedUnit = '';
  readonly requirements = signal<BomRequirements | null>(null);
  readonly reqBusy = signal(false);
  readonly reqError = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    try {
      const [materials, units, lines, templates, roles, fields] = await Promise.all([
        this.api.materials(null, true),
        this.api.units(),
        this.api.lines(),
        this.api.stageTemplates(),
        this.api.jobRoles(),
        this.api.specFields(),
      ]);
      this.specFields.set(fields);
      this.materials.set(materials);
      this.units.set(units);
      this.lines.set(lines);
      this.templates.set(templates);
      this.jobRoles.set(roles);
      const id = this.id();
      if (id) await this.load(id);
      else {
        this.draft = this.emptyDraft();
        this.draft.productionLineId = this.startLine();
        this.chrome?.heading.set(this.i18n.t('mfg.boms.new'));
        this.chrome?.description.set(null);
      }
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.loading.set(false);
    }
  }

  private async load(id: string): Promise<void> {
    const bom = await this.api.bom(id);
    this.bom.set(bom);
    this.draft = {
      productId: bom.bom.productId,
      productionLineId: bom.bom.productionLineId,
      batchQuantity: bom.bom.batchQuantity,
      batchUnitId: bom.bom.batchUnitId,
      stageTemplateId: bom.stageTemplateId,
      notes: bom.notes ?? '',
      lines: bom.lines.map((l) => ({
        componentProductId: l.componentProductId,
        quantity: l.quantity,
        unitId: l.unitId,
        wastePercent: l.wastePercent,
        specification: l.specification,
        note: l.note,
        specs: specRows(l.specs),
      })),
      stages: bom.stages.map(stageRequest),
    };
    this.savedDraft = JSON.stringify(this.draft);
    this.plannedUnit = bom.bom.batchUnitId;
    this.planned = bom.bom.batchQuantity;
    const product = this.productOf(bom.bom.productId);
    this.chrome?.heading.set(`${bom.bom.productSku} — v${bom.bom.version}${product ? ' · ' + this.name(product) : ''}`);
    this.chrome?.description.set(null);
  }

  private emptyDraft(): Draft {
    return { productId: '', productionLineId: null, batchQuantity: 1, batchUnitId: '', stageTemplateId: null, notes: '', lines: [], stages: [] };
  }

  // ---------- lookups ----------

  /** A record named in the reader's language; a stage typed here may not have its English name yet. */
  name(value: { nameAr: string; nameFr: string; nameEn: string | null }): string {
    return localizedName({ nameAr: value.nameAr, nameFr: value.nameFr, nameEn: value.nameEn ?? '' }, this.i18n.lang());
  }

  componentName(l: { componentNameAr: string; componentNameFr: string; componentNameEn: string }): string {
    return localizedName({ nameAr: l.componentNameAr, nameFr: l.componentNameFr, nameEn: l.componentNameEn }, this.i18n.lang());
  }

  productOf(id: string): MaterialDto | undefined {
    return this.materials().find((m) => m.productId === id);
  }

  lineCode(id: string | null): string {
    return id ? (this.lines().find((l) => l.id === id)?.code ?? this.bom()?.bom.productionLineCode ?? '—') : this.i18n.t('mfg.boms.everyLine');
  }

  roleCode(id: string): string {
    return this.jobRoles().find((r) => r.id === id)?.code ?? '?';
  }

  /**
   * The units a quantity of this product may be written in, as the server converts them: its base unit,
   * the units of the same dimension, and the units written on it with a factor. The one already chosen
   * stays offered, so a stored line never shows a blank unit.
   */
  unitsFor(productId: string, current: string | null): UnitDto[] {
    const product = this.productOf(productId);
    const base = this.units().find((u) => u.id === product?.baseUnitId);
    const own = new Set((product?.units ?? []).map((u) => u.unitId));
    return this.units().filter(
      (u) =>
        u.id === current ||
        (u.isActive && !!base && (u.id === base.id || own.has(u.id) || (u.dimension === base.dimension && u.dimension !== 'Other'))),
    );
  }

  templatesFor(productId: string): StageTemplateDto[] {
    return this.templates().filter((t) => t.isActive && (!t.productId || t.productId === productId));
  }

  tone() {
    return toneOf('bom', this.bom()?.bom.status);
  }

  day(value: string): string {
    return formatDay(value, this.i18n.lang(), this.regional.settings().timeZone);
  }

  // ---------- editing ----------

  productChanged(): void {
    const product = this.productOf(this.draft.productId);
    this.draft.batchUnitId = product?.baseUnitId ?? '';
  }

  componentChanged(line: BomLineRequest): void {
    line.unitId = this.productOf(line.componentProductId)?.baseUnitId ?? '';
  }

  addComponent(): void {
    const used = new Set(this.draft.lines.map((l) => l.componentProductId));
    const next = this.components().find((p) => !used.has(p.productId) && p.productId !== this.draft.productId) ?? this.components()[0];
    this.draft.lines.push({
      componentProductId: next.productId,
      quantity: 1,
      unitId: next.baseUnitId ?? '',
      wastePercent: 0,
      specification: null,
      note: null,
      specs: [],
    });
  }

  /** Copies the template's stages onto the draft: the recipe's own copy from then on. */
  copyTemplate(templateId: string | null): void {
    this.draft.stageTemplateId = templateId;
    const template = this.templates().find((t) => t.id === templateId);
    if (template) this.draft.stages = template.steps.map(stageRequest);
  }

  private payload() {
    return {
      productId: this.draft.productId,
      productionLineId: this.draft.productionLineId,
      batchQuantity: Number(this.draft.batchQuantity),
      batchUnitId: this.draft.batchUnitId,
      stageTemplateId: this.draft.stageTemplateId,
      notes: this.draft.notes.trim() || null,
      lines: this.draft.lines.map((l) => ({
        ...l,
        quantity: Number(l.quantity),
        wastePercent: Number(l.wastePercent) || 0,
        specification: l.specification?.trim() || null,
        note: l.note?.trim() || null,
        specs: specRequest(l.specs, this.specFields()),
      })),
      // Always sent, possibly empty: these are the recipe's own stages, never re-copied by the server.
      stages: this.draft.stages.map((s) => ({
        ...s,
        estimatedMinutes: Number(s.estimatedMinutes) || 0,
        expectedWorkers: Number(s.expectedWorkers) || 0,
        roles: s.roles.map((r) => ({ jobRoleId: r.jobRoleId, count: Number(r.count) || 1 })),
      })),
    };
  }

  async save(): Promise<void> {
    if (!this.draft.productId || !this.draft.batchUnitId || !(Number(this.draft.batchQuantity) > 0)) {
      this.error.set(this.i18n.t('mfg.common.fillRequired'));
      return;
    }
    if (this.draft.stages.some((s) => !s.nameAr.trim() || !s.nameFr.trim())) {
      this.error.set(this.i18n.t('mfg.common.fillRequired'));
      return;
    }
    const sku = this.productOf(this.draft.productId)?.sku ?? '';
    if (!(await this.confirm.ask(saveRequest(this.i18n, sku)))) return;
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      const id = this.id();
      if (id) {
        const saved = await this.api.updateBom(id, this.payload());
        await this.load(saved.bom.id);
        this.notice.set(this.i18n.t('mfg.bom.saved'));
      } else {
        const created = await this.api.createBom(this.payload());
        this.opened.emit(created.bom.id);
      }
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  async activate(): Promise<void> {
    const b = this.bom();
    if (!b) return;
    const ok = await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('mfg.bom.activate'), this.i18n.t('mfg.bom.activateWhat', { sku: b.bom.productSku, n: b.bom.version })));
    if (!ok) return;
    await this.act(() => this.api.activateBom(b.bom.id), 'mfg.bom.activated');
  }

  async retire(): Promise<void> {
    const b = this.bom();
    if (!b) return;
    const ok = await this.confirm.ask(actionRequest(this.i18n, this.i18n.t('mfg.bom.retire'), this.i18n.t('mfg.bom.retireWhat', { sku: b.bom.productSku, n: b.bom.version })));
    if (!ok) return;
    await this.act(() => this.api.retireBom(b.bom.id), 'mfg.bom.retired');
  }

  async newVersion(): Promise<void> {
    const b = this.bom();
    if (!b) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const copy = await this.api.newBomVersion(b.bom.id);
      this.opened.emit(copy.bom.id);
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  async remove(): Promise<void> {
    const b = this.bom();
    if (!b) return;
    if (!(await this.confirm.ask(deleteRequest(this.i18n, this.i18n.t('mfg.bom.deleteWhat', { sku: b.bom.productSku, n: b.bom.version }))))) return;
    this.busy.set(true);
    try {
      await this.api.deleteBom(b.bom.id);
      this.deleted.emit();
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  private async act(call: () => Promise<BomDto>, done: string): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      const b = await call();
      await this.load(b.bom.id);
      this.notice.set(this.i18n.t(done));
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  async calculate(): Promise<void> {
    const b = this.bom();
    if (!b) return;
    this.reqBusy.set(true);
    this.reqError.set(null);
    try {
      this.requirements.set(await this.api.requirements(b.bom.id, Number(this.planned), this.plannedUnit || null));
    } catch (e) {
      this.requirements.set(null);
      this.reqError.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.reqBusy.set(false);
    }
  }
}
