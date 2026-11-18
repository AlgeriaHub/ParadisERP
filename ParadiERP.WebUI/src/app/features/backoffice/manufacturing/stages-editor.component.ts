import { Component, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { JobRoleDto, StageRequest } from './manufacturing-api.service';

/** An empty stage, as a new row starts. */
export function emptyStage(): StageRequest {
  return {
    nameAr: '',
    nameFr: '',
    nameEn: null,
    estimatedMinutes: 0,
    expectedWorkers: 1,
    isQualityCheckpoint: false,
    qualityNote: null,
    isOutput: false,
    roles: [],
  };
}

/**
 * The stages of a template or a recipe, edited in place: their order, names, duration, crew, the job
 * roles they need and the quality checkpoints. On a recipe one stage is marked as the output stage —
 * where finished units come off (audit amendment 5); left unmarked, the server takes the last.
 *
 * Works on the array it is given, as the materials screen does with its unit rows: the form that owns
 * the array sends it whole on save.
 */
@Component({
  selector: 'app-mfg-stages-editor',
  standalone: true,
  imports: [FormsModule],
  template: `
    <ol class="stages">
      @for (s of stages(); track $index; let i = $index; let first = $first; let last = $last) {
        <li class="stage">
          <div class="stage__head">
            <strong class="stage__no">{{ i18n.t('mfg.stages.number', { n: i + 1 }) }}</strong>
            <div class="stage__moves">
              <button type="button" class="btn-ghost bo-small" [disabled]="first" [attr.aria-label]="i18n.t('mfg.stages.up', { n: i + 1 })" (click)="move(i, -1)">↑</button>
              <button type="button" class="btn-ghost bo-small" [disabled]="last" [attr.aria-label]="i18n.t('mfg.stages.down', { n: i + 1 })" (click)="move(i, 1)">↓</button>
              <button type="button" class="btn-ghost bo-small" (click)="remove(i)">{{ i18n.t('mfg.common.remove') }}</button>
            </div>
          </div>
          <div class="stage__fields">
            <div class="bo-field">
              <label [for]="idPrefix() + '-ar-' + i">{{ i18n.t('field.nameAr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input [id]="idPrefix() + '-ar-' + i" dir="rtl" [(ngModel)]="s.nameAr" />
            </div>
            <div class="bo-field">
              <label [for]="idPrefix() + '-fr-' + i">{{ i18n.t('field.nameFr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input [id]="idPrefix() + '-fr-' + i" dir="ltr" [(ngModel)]="s.nameFr" />
            </div>
            <div class="bo-field">
              <label [for]="idPrefix() + '-min-' + i">{{ i18n.t('mfg.stages.minutes') }}</label>
              <input [id]="idPrefix() + '-min-' + i" type="number" min="0" step="1" class="num mono" [(ngModel)]="s.estimatedMinutes" />
            </div>
            <div class="bo-field">
              <label [for]="idPrefix() + '-wk-' + i">{{ i18n.t('mfg.stages.workers') }}</label>
              <input [id]="idPrefix() + '-wk-' + i" type="number" min="0" step="1" class="num mono" [(ngModel)]="s.expectedWorkers" />
            </div>
          </div>

          <fieldset class="stage__roles">
            <legend>{{ i18n.t('mfg.stages.roles') }}</legend>
            @for (r of s.roles; track $index; let j = $index) {
              <div class="role-row">
                <select [attr.aria-label]="i18n.t('mfg.stages.role')" [(ngModel)]="r.jobRoleId">
                  @for (jr of jobRoles(); track jr.id) {
                    <option [ngValue]="jr.id">{{ jr.code }} — {{ roleName(jr) }}</option>
                  }
                </select>
                <input type="number" min="1" step="1" class="num mono narrow" [attr.aria-label]="i18n.t('mfg.stages.roleCount')" [(ngModel)]="r.count" />
                <button type="button" class="btn-ghost bo-small" (click)="s.roles.splice(j, 1)">{{ i18n.t('mfg.common.remove') }}</button>
              </div>
            } @empty {
              <p class="bo-hint">{{ i18n.t('mfg.stages.noRoles') }}</p>
            }
            @if (jobRoles().length) {
              <button type="button" class="btn-ghost bo-small" (click)="addRole(i)">+ {{ i18n.t('mfg.stages.addRole') }}</button>
            }
          </fieldset>

          <div class="stage__checks">
            <div class="bo-check">
              <input [id]="idPrefix() + '-qc-' + i" type="checkbox" [(ngModel)]="s.isQualityCheckpoint" />
              <label [for]="idPrefix() + '-qc-' + i">{{ i18n.t('mfg.stages.checkpoint') }}</label>
            </div>
            @if (forBom()) {
              <div class="bo-check">
                <input [id]="idPrefix() + '-out-' + i" type="radio" [name]="idPrefix() + '-output'" [checked]="s.isOutput" (change)="markOutput(i)" />
                <label [for]="idPrefix() + '-out-' + i">{{ i18n.t('mfg.stages.output') }}</label>
              </div>
            }
          </div>
          @if (s.isQualityCheckpoint) {
            <div class="bo-field">
              <label [for]="idPrefix() + '-qn-' + i">{{ i18n.t('mfg.stages.checkpointNote') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <input [id]="idPrefix() + '-qn-' + i" maxlength="400" [(ngModel)]="s.qualityNote" />
            </div>
          }
        </li>
      }
    </ol>
    <button type="button" class="btn-ghost" (click)="add()">+ {{ i18n.t('mfg.stages.add') }}</button>
    @if (forBom() && stages().length && !hasOutput()) {
      <p class="bo-hint">{{ i18n.t('mfg.stages.lastIsOutput') }}</p>
    }
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      :host {
        display: grid;
        gap: var(--sp-3);
        justify-items: start;
      }
      .stages {
        display: grid;
        gap: var(--sp-3);
        margin: 0;
        padding: 0;
        list-style: none;
        inline-size: 100%;
      }
      .stage {
        display: grid;
        gap: var(--sp-2);
        padding: var(--sp-3);
        border: var(--line) solid var(--border);
        border-radius: var(--radius-card);
        background: var(--surface);
      }
      .stage__head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: var(--sp-2);
      }
      .stage__moves {
        display: flex;
        gap: var(--sp-1);
      }
      .stage__fields {
        display: grid;
        gap: var(--sp-2) var(--sp-3);
        grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
      }
      .stage__roles {
        display: grid;
        gap: var(--sp-2);
        margin: 0;
        padding: var(--sp-2) var(--sp-3);
        border: var(--line) solid var(--border);
        border-radius: var(--radius-sm);
        justify-items: start;
      }
      .stage__roles legend {
        padding-inline: var(--sp-1);
        color: var(--muted);
        font-size: var(--fs-sm);
      }
      .role-row {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2);
        align-items: center;
      }
      .narrow {
        inline-size: 5.5rem;
      }
      .stage__checks {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2) var(--sp-4);
      }
    `,
  ],
})
export class MfgStagesEditorComponent {
  readonly i18n = inject(I18nService);

  /** The stages, edited in place. */
  readonly stages = input.required<StageRequest[]>();
  readonly jobRoles = input<JobRoleDto[]>([]);
  /** A recipe's stages: one of them is the output stage. */
  readonly forBom = input(false);
  /** Keeps the field ids apart when two editors share a page. */
  readonly idPrefix = input('stg');

  roleName(r: JobRoleDto): string {
    return localizedName(r, this.i18n.lang());
  }

  hasOutput(): boolean {
    return this.stages().some((s) => s.isOutput);
  }

  add(): void {
    this.stages().push(emptyStage());
  }

  remove(index: number): void {
    this.stages().splice(index, 1);
  }

  move(index: number, by: -1 | 1): void {
    const list = this.stages();
    const [stage] = list.splice(index, 1);
    list.splice(index + by, 0, stage);
  }

  addRole(index: number): void {
    const stage = this.stages()[index];
    const free = this.jobRoles().find((r) => !stage.roles.some((x) => x.jobRoleId === r.id));
    if (free) stage.roles.push({ jobRoleId: free.id, count: 1 });
  }

  markOutput(index: number): void {
    this.stages().forEach((s, i) => (s.isOutput = i === index));
  }
}
