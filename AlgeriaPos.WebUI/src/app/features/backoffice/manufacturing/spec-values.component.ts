import { Component, inject, input } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { I18nService } from '../../../core/i18n.service';
import { localizedName } from '../../../core/multilingual';
import { SpecFieldDto, SpecValueDto, SpecValueRequest, UnitDto } from './manufacturing-api.service';
import { qty } from './manufacturing-shared';

/** A value as the editor holds it: the figure as typed, until it is sent. */
export interface SpecRow {
  attributeId: string;
  value: number | null;
  unitId: string | null;
  text: string | null;
}

/** Stored values, ready to edit. */
export function specRows(specs: readonly SpecValueDto[]): SpecRow[] {
  return specs.map((s) => ({ attributeId: s.attributeId, value: s.value, unitId: s.unitId, text: s.text }));
}

/**
 * The rows as the server takes them: a measured field sends its figure and unit, a text field its word,
 * and a row left without its field is dropped. The server checks the rest (units that convert, figures
 * above zero) and answers with a code.
 */
export function specRequest(rows: readonly SpecRow[], fields: readonly SpecFieldDto[]): SpecValueRequest[] {
  return rows
    .filter((r) => !!r.attributeId)
    .map((r) => {
      const text = fields.find((f) => f.id === r.attributeId)?.kind === 'Text';
      return {
        attributeId: r.attributeId,
        value: text || r.value === null || (r.value as unknown) === '' ? null : Number(r.value),
        unitId: text ? null : r.unitId || null,
        text: text ? r.text?.trim() || null : null,
      };
    });
}

/** One line for a list: "Longueur 6 m · Épaisseur 3.2 mm · Nuance S235", in the reader's language. */
export function describeSpecs(specs: readonly SpecValueDto[], fields: readonly SpecFieldDto[], lang: string): string {
  return specs
    .map((s) => {
      const field = fields.find((f) => f.id === s.attributeId);
      const name = field ? localizedName(field, lang) : s.attributeCode;
      return s.kind === 'Text' ? `${name} ${s.text ?? ''}` : `${name} ${qty(s.value ?? 0)} ${s.unitCode ?? ''}`;
    })
    .join(' · ');
}

/**
 * The structured specification of a product or a recipe line (hardening review, 2026-10-26): any of the
 * company's fields, each a figure in a unit of the field's dimension or a word. Optional — a product with
 * no physical dimensions carries none — and beside the free-text specification, never instead of it. The
 * rows are the caller's array, edited in place like the materials' unit rows.
 */
@Component({
  selector: 'app-mfg-spec-values',
  standalone: true,
  imports: [FormsModule],
  template: `
    @for (row of rows(); track $index; let i = $index) {
      <div class="spec-row">
        <div class="bo-field">
          <label [for]="idPrefix() + '-f-' + i">{{ i18n.t('mfg.specs.field') }}</label>
          <select [id]="idPrefix() + '-f-' + i" [disabled]="disabled()" [(ngModel)]="row.attributeId" (ngModelChange)="fieldChanged(row)">
            @for (f of choosable(row); track f.id) {
              <option [ngValue]="f.id">{{ fieldName(f) }}</option>
            }
          </select>
        </div>
        @if (fieldOf(row)?.kind === 'Text') {
          <div class="bo-field spec-row__value">
            <label [for]="idPrefix() + '-t-' + i">{{ i18n.t('mfg.specs.word') }}</label>
            <input [id]="idPrefix() + '-t-' + i" maxlength="200" [disabled]="disabled()" [(ngModel)]="row.text" />
          </div>
        } @else {
          <div class="bo-field spec-row__value">
            <label [for]="idPrefix() + '-v-' + i">{{ i18n.t('mfg.specs.value') }}</label>
            <div class="pair">
              <input [id]="idPrefix() + '-v-' + i" type="number" min="0" step="any" class="num mono" [disabled]="disabled()" [(ngModel)]="row.value" />
              <select [attr.aria-label]="i18n.t('mfg.common.unit')" [disabled]="disabled()" [(ngModel)]="row.unitId">
                @for (u of unitsFor(row); track u.id) {
                  <option [ngValue]="u.id">{{ u.code }}</option>
                }
              </select>
            </div>
          </div>
        }
        @if (!disabled()) {
          <button type="button" class="btn-ghost bo-small" (click)="rows().splice(i, 1)">{{ i18n.t('mfg.common.remove') }}</button>
        }
      </div>
    } @empty {
      @if (disabled()) {
        <p class="bo-hint">—</p>
      }
    }
    @if (!disabled()) {
      <div>
        <button type="button" class="btn-ghost bo-small" [disabled]="!nextField()" (click)="add()">+ {{ i18n.t('mfg.specs.add') }}</button>
      </div>
    }
  `,
  styleUrls: ['../backoffice-ui.scss'],
  styles: [
    `
      :host {
        display: grid;
        gap: var(--sp-2);
      }
      .spec-row {
        display: grid;
        gap: var(--sp-2) var(--sp-3);
        grid-template-columns: minmax(9rem, 1fr) minmax(12rem, 1.4fr) auto;
        align-items: end;
      }
      .pair {
        display: grid;
        grid-template-columns: minmax(5rem, 1fr) minmax(4.5rem, auto);
        gap: var(--sp-2);
      }
      @media (max-width: 640px) {
        .spec-row {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class MfgSpecValuesComponent {
  readonly i18n = inject(I18nService);

  readonly rows = input.required<SpecRow[]>();
  readonly fields = input.required<SpecFieldDto[]>();
  readonly units = input.required<UnitDto[]>();
  readonly disabled = input(false);
  readonly idPrefix = input('spec');

  fieldName(f: SpecFieldDto): string {
    return localizedName(f, this.i18n.lang());
  }

  fieldOf(row: SpecRow): SpecFieldDto | undefined {
    return this.fields().find((f) => f.id === row.attributeId);
  }

  /** Active fields not already used by another row, and this row's own even if switched off since. */
  choosable(row: SpecRow): SpecFieldDto[] {
    return this.fields().filter(
      (f) => f.id === row.attributeId || (f.isActive && !this.rows().some((r) => r !== row && r.attributeId === f.id)),
    );
  }

  /** Units of the field's dimension only — a thickness is never offered in kilograms. */
  unitsFor(row: SpecRow): UnitDto[] {
    const field = this.fieldOf(row);
    return this.units().filter((u) => field && u.dimension === field.dimension && (u.isActive || u.id === row.unitId));
  }

  nextField(): SpecFieldDto | undefined {
    return this.fields().find((f) => f.isActive && !this.rows().some((r) => r.attributeId === f.id));
  }

  add(): void {
    const field = this.nextField();
    if (!field) return;
    const row: SpecRow = { attributeId: field.id, value: null, unitId: null, text: null };
    this.fieldChanged(row);
    this.rows().push(row);
  }

  /** A new field keeps the row's unit when it still fits, and otherwise starts from the dimension's first. */
  fieldChanged(row: SpecRow): void {
    const units = this.unitsFor(row);
    if (!units.some((u) => u.id === row.unitId)) row.unitId = units[0]?.id ?? null;
  }
}
