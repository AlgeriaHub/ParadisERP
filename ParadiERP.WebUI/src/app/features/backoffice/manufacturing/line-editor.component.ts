import { Component, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { saveRequest } from '../../../ui/confirm-requests';
import { DialogComponent } from '../../../ui/dialog.component';
import { FormSectionComponent } from '../../../ui/form-section.component';
import { TouchedFields } from '../backoffice-forms';
import {
  LineLocation,
  LineUserCandidate,
  ManufacturingApiService,
  ProductionLineSummary,
  UpsertProductionLineRequest,
} from './manufacturing-api.service';
import { mfgError } from './manufacturing-shared';

/**
 * Creating or editing a production line (company scope): its code, names, description, the warehouse
 * it stands on and its responsible manager. A new line picks a warehouse no other line stands on; the
 * location of an existing line is fixed, because its stock is there.
 */
@Component({
  selector: 'app-mfg-line-editor',
  standalone: true,
  imports: [FormsModule, DialogComponent, FormSectionComponent],
  template: `
    <app-dialog [open]="isOpen()" size="md" [heading]="editing() ? i18n.t('common.edit') + ' — ' + editing()!.code : i18n.t('mfg.lines.new')" (dismiss)="close()">
      @if (isOpen()) {
        <div class="editor bo-form">
          @if (error()) {
            <p class="notice danger" role="alert">{{ error() }}</p>
          }
          <app-form-section [heading]="i18n.t('common.group.identity')" [columns]="3">
            <div class="bo-field" [class.is-invalid]="touched.missing('code', form.code)">
              <label for="le-code">{{ i18n.t('mfg.common.code') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="le-code" class="mono" dir="ltr" maxlength="16" [(ngModel)]="form.code" (blur)="touched.touch('code')" />
              <p class="bo-hint">{{ i18n.t('mfg.lines.codeHint') }}</p>
            </div>
            <div class="bo-field" [class.is-invalid]="touched.missing('nameAr', form.nameAr)">
              <label for="le-ar">{{ i18n.t('field.nameAr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="le-ar" dir="rtl" [(ngModel)]="form.nameAr" (blur)="touched.touch('nameAr')" />
            </div>
            <div class="bo-field" [class.is-invalid]="touched.missing('nameFr', form.nameFr)">
              <label for="le-fr">{{ i18n.t('field.nameFr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="le-fr" dir="ltr" [(ngModel)]="form.nameFr" (blur)="touched.touch('nameFr')" />
            </div>
            <div class="bo-field">
              <label for="le-en">{{ i18n.t('field.nameEn') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <input id="le-en" dir="ltr" [(ngModel)]="form.nameEn" />
            </div>
            <div class="bo-field bo-field--wide">
              <label for="le-desc">{{ i18n.t('mfg.common.description') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <textarea id="le-desc" rows="2" maxlength="1000" [(ngModel)]="form.description"></textarea>
            </div>
          </app-form-section>

          <app-form-section [heading]="i18n.t('mfg.lines.setup')" [columns]="2">
            <div class="bo-field" [class.is-invalid]="touched.missing('location', form.locationId)">
              <label for="le-loc">{{ i18n.t('mfg.lines.location') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              @if (editing()) {
                <p class="bo-fixed" id="le-loc">{{ locationLabel(form.locationId) }}</p>
                <p class="bo-hint">{{ i18n.t('mfg.lines.locationFixed') }}</p>
              } @else {
                <select id="le-loc" [(ngModel)]="form.locationId" (blur)="touched.touch('location')">
                  <option value="" disabled>{{ i18n.t('mfg.lines.chooseLocation') }}</option>
                  @for (l of freeLocations(); track l.id) {
                    <option [ngValue]="l.id">{{ l.code }} — {{ locationName(l) }}</option>
                  }
                </select>
                @if (!freeLocations().length) {
                  <p class="bo-hint">{{ i18n.t('mfg.lines.noFreeLocation') }}</p>
                } @else {
                  <p class="bo-hint">{{ i18n.t('mfg.lines.locationHint') }}</p>
                }
              }
            </div>
            <div class="bo-field">
              <label for="le-mgr">{{ i18n.t('mfg.lines.manager') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <select id="le-mgr" [(ngModel)]="form.managerUserId">
                <option [ngValue]="null">—</option>
                @for (u of users(); track u.id) {
                  <option [ngValue]="u.id">{{ u.fullName }} ({{ u.username }})</option>
                }
              </select>
            </div>
            <div class="bo-check">
              <input id="le-active" type="checkbox" [(ngModel)]="form.isActive" />
              <label for="le-active">{{ i18n.t('common.active') }}</label>
            </div>
          </app-form-section>
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
export class LineEditorComponent {
  private readonly api = inject(ManufacturingApiService);
  private readonly confirm = inject(ConfirmService);
  readonly i18n = inject(I18nService);

  /** The line saved, by id. */
  readonly saved = output<string>();

  readonly isOpen = signal(false);
  readonly editing = signal<ProductionLineSummary | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly locations = signal<LineLocation[]>([]);
  readonly users = signal<LineUserCandidate[]>([]);
  readonly freeLocations = computed(() => this.locations().filter((l) => l.isActive && !l.lineId));
  readonly touched = new TouchedFields();
  form: UpsertProductionLineRequest = this.emptyForm();

  private emptyForm(): UpsertProductionLineRequest {
    return { code: '', nameAr: '', nameFr: '', nameEn: null, description: null, locationId: '', managerUserId: null, isActive: true };
  }

  async open(line: ProductionLineSummary | null): Promise<void> {
    this.editing.set(line);
    this.touched.reset();
    this.error.set(null);
    this.form = line
      ? {
          code: line.code,
          nameAr: line.nameAr,
          nameFr: line.nameFr,
          nameEn: line.nameEn,
          description: line.description,
          locationId: line.locationId,
          managerUserId: line.managerUserId,
          isActive: line.isActive,
        }
      : this.emptyForm();
    this.isOpen.set(true);
    try {
      const [locations, users] = await Promise.all([this.api.lineLocations(), this.api.lineUserCandidates()]);
      this.locations.set(locations);
      this.users.set(users);
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    }
  }

  close(): void {
    this.isOpen.set(false);
    this.editing.set(null);
  }

  locationName(l: LineLocation): string {
    return this.i18n.lang() === 'ar' ? l.nameAr || l.nameFr : l.nameFr || l.nameAr;
  }

  locationLabel(id: string): string {
    const l = this.locations().find((x) => x.id === id);
    const line = this.editing();
    if (l) return `${l.code} — ${this.locationName(l)}`;
    return line ? `${line.locationCode} — ${this.i18n.lang() === 'ar' ? line.locationNameAr : line.locationNameFr}` : '—';
  }

  async save(): Promise<void> {
    this.touched.touchAll('code', 'nameAr', 'nameFr', 'location');
    if (!this.form.code.trim() || !this.form.nameAr.trim() || !this.form.nameFr.trim() || !this.form.locationId) {
      this.error.set(this.i18n.t('mfg.common.fillRequired'));
      return;
    }
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.form.code)))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const saved = await this.api.saveLine(this.editing()?.id ?? null, {
        ...this.form,
        code: this.form.code.trim(),
        nameEn: this.form.nameEn?.trim() || null,
        description: this.form.description?.trim() || null,
      });
      this.close();
      this.saved.emit(saved.line.id);
    } catch (e) {
      this.error.set(mfgError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }
}
