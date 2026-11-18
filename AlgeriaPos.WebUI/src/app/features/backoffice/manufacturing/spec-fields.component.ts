import { Component, OnInit, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { ConfirmService } from '../../../ui/confirm.service';
import { saveRequest } from '../../../ui/confirm-requests';
import { DialogComponent } from '../../../ui/dialog.component';
import { FormSectionComponent } from '../../../ui/form-section.component';
import { MENU } from '../../../ui/menu.component';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { TouchedFields } from '../backoffice-forms';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { ManufacturingApiService, SpecFieldDto, SpecKind, UnitDimension, UpsertSpecFieldRequest } from './manufacturing-api.service';
import { mfgError } from './manufacturing-shared';

/** What a measured field may measure: every dimension a unit has. */
const DIMENSIONS: UnitDimension[] = ['Length', 'Mass', 'Area', 'Volume', 'Count', 'Other'];

/**
 * Manufacturing › Specification fields (hardening review, 2026-10-26): the fields a product's or a recipe
 * line's specification may carry as figures — length, width, thickness, diameter, grade, and whatever the
 * company adds. Not a fixed set of columns: each product carries only the fields that mean something for
 * it, beside its free-text specification. A measured field names what it measures, so its value is always
 * written in a unit that converts; once a value uses it, that is fixed.
 */
@Component({
  selector: 'app-mfg-spec-fields',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ...MENU, DialogComponent, FormSectionComponent, StatusComponent, TableStateComponent],
  template: `
    @if (canEdit()) {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="openCreate()"><span aria-hidden="true">+</span> {{ i18n.t('mfg.specs.new') }}</button>
      </ng-template>
    }

    <div class="bo-stack">
      @if (error() && fields().length) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      <section class="ui-surface" [attr.aria-busy]="busy() && !fields().length">
        @if (busy() && !fields().length) {
          <app-table-state kind="loading" />
        } @else if (error() && !fields().length) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!fields().length) {
          <app-table-state kind="empty" [message]="i18n.t('mfg.specs.none')">
            @if (canEdit()) {
              <button type="button" class="btn-primary" (click)="openCreate()">{{ i18n.t('mfg.specs.new') }}</button>
            }
          </app-table-state>
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('mfg.common.code') }}</th>
                  <th>{{ i18n.t('mfg.common.name') }}</th>
                  <th>{{ i18n.t('mfg.specs.kind') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  @if (canEdit()) {
                    <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (f of fields(); track f.id) {
                  <tr [class.is-muted]="!f.isActive">
                    <td class="mono" [attr.data-label]="i18n.t('mfg.common.code')"><span dir="ltr">{{ f.code }}</span></td>
                    <td [attr.data-label]="i18n.t('mfg.common.name')">
                      @if (canEdit()) {
                        <button type="button" class="ui-link" (click)="edit(f)">{{ name(f) }}</button>
                      } @else {
                        {{ name(f) }}
                      }
                      @if (f.isSystem) {
                        <span class="ui-sub">{{ i18n.t('mfg.specs.system') }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.specs.kind')">{{ kindLabel(f) }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(f.isActive)">{{ i18n.t(f.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                    </td>
                    @if (canEdit()) {
                      <td class="ui-cell-menu">
                        <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + f.code">
                          <span menuTrigger aria-hidden="true">⋯</span>
                          <button type="button" appMenuItem (click)="edit(f)">{{ i18n.t('common.edit') }}</button>
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
    </div>

    <app-dialog [open]="editorOpen()" size="md" [heading]="editing() ? i18n.t('common.edit') + ' — ' + editing()!.code : i18n.t('mfg.specs.new')" (dismiss)="close()">
      @if (editorOpen()) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          <app-form-section [heading]="i18n.t('common.group.identity')" [columns]="3">
            <div class="bo-field" [class.is-invalid]="touched.missing('code', form.code)">
              <label for="sf-code">{{ i18n.t('mfg.common.code') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="sf-code" class="mono" dir="ltr" maxlength="16" [(ngModel)]="form.code" [disabled]="fixed()" (blur)="touched.touch('code')" />
            </div>
            <div class="bo-field" [class.is-invalid]="touched.missing('nameAr', form.nameAr)">
              <label for="sf-ar">{{ i18n.t('field.nameAr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="sf-ar" dir="rtl" maxlength="80" [(ngModel)]="form.nameAr" (blur)="touched.touch('nameAr')" />
            </div>
            <div class="bo-field" [class.is-invalid]="touched.missing('nameFr', form.nameFr)">
              <label for="sf-fr">{{ i18n.t('field.nameFr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="sf-fr" dir="ltr" maxlength="80" [(ngModel)]="form.nameFr" (blur)="touched.touch('nameFr')" />
            </div>
            <div class="bo-field">
              <label for="sf-en">{{ i18n.t('field.nameEn') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <input id="sf-en" dir="ltr" maxlength="80" [(ngModel)]="form.nameEn" />
            </div>
          </app-form-section>
          <app-form-section [heading]="i18n.t('mfg.specs.holds')" [columns]="3">
            <div class="bo-field">
              <label for="sf-kind">{{ i18n.t('mfg.specs.kind') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <select id="sf-kind" [(ngModel)]="form.kind" [disabled]="locked()">
                <option ngValue="Measure">{{ i18n.t('mfg.specs.kind.Measure') }}</option>
                <option ngValue="Text">{{ i18n.t('mfg.specs.kind.Text') }}</option>
              </select>
            </div>
            @if (form.kind === 'Measure') {
              <div class="bo-field">
                <label for="sf-dim">{{ i18n.t('mfg.units.dimension') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
                <select id="sf-dim" [(ngModel)]="form.dimension" [disabled]="locked()">
                  @for (d of dimensions; track d) {
                    <option [ngValue]="d">{{ i18n.t('mfg.dimension.' + d) }}</option>
                  }
                </select>
              </div>
            }
            <div class="bo-field">
              <label for="sf-order">{{ i18n.t('mfg.specs.order') }}</label>
              <input id="sf-order" type="number" min="0" max="999" step="1" class="num mono" [(ngModel)]="form.sortOrder" />
            </div>
          </app-form-section>
          @if (locked()) {
            <p class="bo-hint">{{ i18n.t(editing()!.isSystem ? 'mfg.specs.systemFixed' : 'mfg.specs.inUseFixed') }}</p>
          }
          <div class="bo-check">
            <input id="sf-active" type="checkbox" [(ngModel)]="form.isActive" />
            <label for="sf-active">{{ i18n.t('common.active') }}</label>
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
export class MfgSpecFieldsComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly confirm = inject(ConfirmService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly canEdit = input(false);
  readonly dimensions = DIMENSIONS;
  readonly fields = signal<SpecFieldDto[]>([]);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly editorOpen = signal(false);
  readonly editing = signal<SpecFieldDto | null>(null);
  readonly formError = signal<string | null>(null);
  readonly touched = new TouchedFields();
  form: UpsertSpecFieldRequest = this.emptyForm();

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.specs.lead'));
    await this.reload();
  }

  async reload(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.fields.set(await this.api.specFields());
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }

  name(f: SpecFieldDto): string {
    return localizedName(f, this.i18n.lang());
  }

  kindLabel(f: SpecFieldDto): string {
    return f.kind === 'Text' ? this.i18n.t('mfg.specs.kind.Text') : this.i18n.t('mfg.dimension.' + f.dimension);
  }

  tone(active: boolean) {
    return toneOf('active', active ? 'Active' : 'Inactive');
  }

  /** A system field keeps its code. */
  fixed(): boolean {
    return !!this.editing()?.isSystem;
  }

  /** What it measures is fixed for a system field and for one a value already uses. */
  locked(): boolean {
    const f = this.editing();
    return !!f && (f.isSystem || f.inUse);
  }

  private emptyForm(): UpsertSpecFieldRequest {
    return { code: '', nameAr: '', nameFr: '', nameEn: '', kind: 'Measure' as SpecKind, dimension: 'Length', isActive: true, sortOrder: 0 };
  }

  openCreate(): void {
    this.editing.set(null);
    this.form = { ...this.emptyForm(), sortOrder: Math.max(0, ...this.fields().map((f) => f.sortOrder)) + 1 };
    this.touched.reset();
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  edit(f: SpecFieldDto): void {
    this.editing.set(f);
    this.form = {
      code: f.code, nameAr: f.nameAr, nameFr: f.nameFr, nameEn: f.nameEn, kind: f.kind,
      dimension: f.kind === 'Measure' ? f.dimension : 'Length', isActive: f.isActive, sortOrder: f.sortOrder,
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
    if (!this.form.code.trim() || !this.form.nameAr.trim() || !this.form.nameFr.trim()) {
      this.formError.set(this.i18n.t('mfg.common.fillRequired'));
      return;
    }
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.form.code)))) return;
    this.busy.set(true);
    this.formError.set(null);
    try {
      await this.api.saveSpecField(this.editing()?.id ?? null, {
        ...this.form,
        nameEn: this.form.nameEn?.trim() || null,
        dimension: this.form.kind === 'Measure' ? this.form.dimension : null,
        sortOrder: Math.trunc(Number(this.form.sortOrder) || 0),
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
