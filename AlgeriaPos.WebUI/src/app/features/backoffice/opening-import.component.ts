import { Component, ElementRef, computed, effect, inject, input, output, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AdminBranch,
  AdminService,
  OpeningImportPreview,
  OpeningImportResult,
  adminErrorMessage,
} from '../../core/admin.service';
import { BranchLock } from '../../core/branch-lock';
import { CatalogStore } from '../../core/catalog-store';
import { I18nService } from '../../core/i18n.service';
import { ConfirmService } from '../../ui/confirm.service';
import { FoldComponent } from '../../ui/fold.component';
import { localizedName } from './backoffice-format';
import {
  ImportFilter,
  ImportLine,
  blocks,
  importRequest,
  issueOf,
  linesFor,
  shown,
  tally,
} from './opening-import';

const DEFAULT_PREFIX = 'ART-';

/**
 * A branch's opening stock loaded from the count it already keeps in Excel.
 *
 * <p>Two steps, and nothing is written until the second. The file is read on the server and
 * every line comes back with what it would become — an article already in the catalogue (found
 * by name, however the typist spaced it), a new one, or a name several answer to — and the
 * person ticks what loads. The quantity column is chosen here, not guessed: a branch's sheet
 * carries what is in the shop beside what is out on hire, and only the first is an opening
 * balance.</p>
 *
 * <p>Sits under the one-line opening form and follows the same rules: only a branch still in
 * setup, and for anyone but the Admin only their own branch, which the server enforces as
 * well.</p>
 */
@Component({
  selector: 'app-opening-import',
  standalone: true,
  imports: [FormsModule, FoldComponent],
  template: `
    <section class="bo-panel" aria-labelledby="oi-title">
      <div class="bo-panel__head">
        <h2 class="bo-panel__title" id="oi-title">{{ i18n.t('stockImport.title') }}</h2>
        <p class="bo-panel__note">{{ i18n.t('stockImport.hint') }}</p>
      </div>

      @if (error()) {
        <div class="bo-panel__body">
          <p class="notice danger" role="alert">{{ error() }}</p>
        </div>
      }
      @if (done(); as d) {
        <div class="bo-panel__body">
          <p class="notice ok" role="status">
            {{ i18n.t('stockImport.done', { count: d.lines, branch: d.branch, created: d.created, matched: d.matched }) }}
            @if (d.first) {
              <br />{{ i18n.t('stockImport.doneCodes', { first: d.first, last: d.last }) }}
            }
          </p>
        </div>
      }

      <div class="bo-panel__body bo-form">
        <div class="bo-fields bo-fields--roomy">
          @if (lock.locked()) {
            <div class="bo-field">
              <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
              <p class="bo-fixed">{{ lock.ownBranchName(branches()) }}</p>
              <span class="bo-hint">{{ i18n.t('stock.inSetup') }}</span>
            </div>
          } @else {
            <div class="bo-field">
              <label for="oi-branch">{{ i18n.t('backoffice.devices.branch') }}</label>
              <select id="oi-branch" [(ngModel)]="branchId" [disabled]="busy()">
                @for (b of branches(); track b.id) {
                  <option [ngValue]="b.id">{{ name(b) }}</option>
                }
              </select>
              <p class="bo-hint">{{ i18n.t('stock.branchState') }} — {{ i18n.t('stock.inSetup') }}</p>
            </div>
          }
          <div class="bo-field bo-field--wide">
            <label for="oi-file-choose">{{ i18n.t('stockImport.file') }}</label>
            <!-- The browser's own file control speaks the browser's language, not the app's. -->
            <div class="bo-file">
              <button type="button" id="oi-file-choose" class="btn-ghost" [disabled]="busy()" (click)="fileInput.click()">
                {{ i18n.t('common.chooseFile') }}
              </button>
              <span class="bo-file__name">{{ fileInput.files?.[0]?.name || i18n.t('common.noFile') }}</span>
              <input
                #fileInput
                id="oi-file"
                type="file"
                hidden
                accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                [disabled]="busy()"
                (change)="pick($event)"
              />
            </div>
            <p class="bo-hint">{{ i18n.t('stockImport.fileHint') }}</p>
          </div>
          @if (preview(); as p) {
            @if (p.sheetNames.length > 1) {
              <div class="bo-field">
                <label for="oi-sheet">{{ i18n.t('stockImport.sheet') }}</label>
                <select id="oi-sheet" [ngModel]="p.sheetName" (ngModelChange)="chooseSheet($event)" [disabled]="busy()">
                  @for (s of p.sheetNames; track s) {
                    <option [ngValue]="s">{{ s }}</option>
                  }
                </select>
              </div>
            }
          }
        </div>

        @if (reading()) {
          <p class="bo-loading" role="status">
            <span class="bo-loading__word">{{ i18n.t('stockImport.reading') }}</span>
            <span class="bo-loading__rows" aria-hidden="true"></span>
          </p>
        }

        @if (preview(); as p) {
          <!-- Open: the quantity column is required, and nothing loads until it is chosen. -->
          <app-fold class="bo-group" [heading]="i18n.t('stockImport.source', { file: p.fileName, sheet: p.sheetName })" [open]="true">
            <p class="bo-group__note" foldLead>
              {{ i18n.t('stockImport.layout', { column: p.nameColumn, header: p.nameHeader, row: p.headerRow }) }}
            </p>
            <div class="bo-fields bo-fields--roomy">
              <div class="bo-field" [class.is-invalid]="column() === null">
                <label for="oi-qty">
                  {{ i18n.t('stockImport.quantity') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <select
                  id="oi-qty"
                  [ngModel]="column()"
                  (ngModelChange)="chooseColumn($event)"
                  [disabled]="busy()"
                  [attr.aria-invalid]="column() === null ? 'true' : null"
                  aria-describedby="oi-qty-hint"
                >
                  <option [ngValue]="null">{{ i18n.t('stockImport.quantityChoose') }}</option>
                  @for (c of p.columns; track c.index) {
                    <option [ngValue]="c.index">
                      {{ i18n.t('stockImport.column', { letter: c.letter }) }}{{ c.header ? ' — ' + c.header : '' }}
                    </option>
                  }
                </select>
                <p class="bo-hint" id="oi-qty-hint">
                  {{ i18n.t(column() === null ? 'stockImport.quantityNone' : 'stockImport.quantityHint') }}
                </p>
              </div>
              <div class="bo-field">
                <label for="oi-prefix">{{ i18n.t('stockImport.skuPrefix') }}</label>
                <input id="oi-prefix" class="mono" maxlength="20" [(ngModel)]="skuPrefix" [disabled]="busy()" aria-describedby="oi-prefix-hint" />
                <p class="bo-hint" id="oi-prefix-hint">
                  {{ i18n.t('stockImport.skuPrefixHint', { example: (skuPrefix.trim() || defaultPrefix) + '0001' }) }}
                </p>
              </div>
            </div>
          </app-fold>

          @if (lines().length) {
            <div class="bo-metrics">
              <div class="bo-metric bo-metric--lead">
                <span class="bo-metric__label">{{ i18n.t('stockImport.toLoad') }}</span>
                <strong class="bo-metric__value mono">{{ counts().toLoad }}</strong>
              </div>
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('stockImport.matched') }}</span>
                <strong class="bo-metric__value mono">{{ counts().matched }}</strong>
              </div>
              <div class="bo-metric">
                <span class="bo-metric__label">{{ i18n.t('stockImport.created') }}</span>
                <strong class="bo-metric__value mono">{{ counts().created }}</strong>
              </div>
              <div class="bo-metric" [class.bo-metric--alert]="counts().leftOut > 0">
                <span class="bo-metric__label">{{ i18n.t('stockImport.leftOut') }}</span>
                <strong class="bo-metric__value mono">{{ counts().leftOut }}</strong>
              </div>
            </div>

            <nav class="bo-chips" [attr.aria-label]="i18n.t('stockImport.title')">
              @for (f of filters; track f) {
                <button
                  type="button"
                  [class.is-on]="filter() === f"
                  [attr.aria-current]="filter() === f ? 'true' : null"
                  (click)="filter.set(f)"
                >
                  {{ i18n.t('stockImport.filter.' + f) }}
                </button>
              }
            </nav>
          }
        }
      </div>

      @if (preview() && lines().length) {
        <div class="bo-scroll bo-scroll--tall">
          <table class="bo-data">
            <thead>
              <tr>
                <th>{{ i18n.t('stockImport.col.load') }}</th>
                <th class="num">{{ i18n.t('stockImport.col.row') }}</th>
                <th>{{ i18n.t('stockImport.col.sheetName') }}</th>
                <th>{{ i18n.t('stockImport.col.catalogue') }}</th>
                <th class="num">{{ i18n.t('stockImport.col.qty') }}</th>
                <th>{{ i18n.t('stockImport.col.note') }}</th>
              </tr>
            </thead>
            <tbody>
              @for (line of visible(); track line.row) {
                <tr>
                  <td [attr.data-label]="i18n.t('stockImport.col.load')">
                    <input
                      type="checkbox"
                      [(ngModel)]="line.include"
                      (ngModelChange)="touched()"
                      [disabled]="busy() || line.match === 'ambiguous'"
                      [attr.aria-label]="i18n.t('stockImport.col.load') + ' — ' + line.name"
                    />
                  </td>
                  <td class="num mono" [attr.data-label]="i18n.t('stockImport.col.row')">{{ line.row }}</td>
                  <td [attr.data-label]="i18n.t('stockImport.col.sheetName')"><bdi>{{ line.name }}</bdi></td>
                  <td [attr.data-label]="i18n.t('stockImport.col.catalogue')">
                    @switch (line.match) {
                      @case ('existing') {
                        <span class="bo-state bo-state--ok">{{ i18n.t('stockImport.match.existing') }}</span>
                        <span class="bo-soft mono"> {{ line.sku }}</span>
                        <span class="bo-soft"> <bdi>{{ productName(line) }}</bdi></span>
                      }
                      @case ('new') {
                        <span class="bo-state bo-state--lead">{{ i18n.t('stockImport.match.new') }}</span>
                      }
                      @default {
                        <span class="bo-state bo-state--warn">{{ i18n.t('stockImport.match.ambiguous') }}</span>
                      }
                    }
                  </td>
                  <td class="num" [attr.data-label]="i18n.t('stockImport.col.qty')">
                    <input
                      type="number"
                      class="num mono bo-input-narrow"
                      [(ngModel)]="line.qty"
                      (ngModelChange)="touched()"
                      [disabled]="busy()"
                      [attr.aria-label]="i18n.t('stockImport.col.qty') + ' — ' + line.name"
                      [attr.aria-invalid]="line.include && blocks(line) ? 'true' : null"
                    />
                  </td>
                  <td [attr.data-label]="i18n.t('stockImport.col.note')">
                    @if (issue(line); as i) {
                      <span [class.bo-error]="line.include && blocks(line)" [class.bo-soft]="!(line.include && blocks(line))">
                        {{ i18n.t('stockImport.issue.' + i, { row: line.duplicateOfRow ?? '' }) }}
                      </span>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      @if (preview()) {
        <div class="bo-panel__foot">
          @if (counts().blocked) {
            <span class="bo-error" role="alert">{{ i18n.t('stockImport.blocked', { count: counts().blocked }) }}</span>
          } @else if (!counts().toLoad && lines().length) {
            <span class="bo-hint">{{ i18n.t('stockImport.nothingToLoad') }}</span>
          }
          <button type="button" class="btn-ghost" [disabled]="busy()" (click)="reset()">
            {{ i18n.t('stockImport.another') }}
          </button>
          <button
            type="button"
            class="btn-primary"
            [disabled]="busy() || !branchId || column() === null || !counts().toLoad || counts().blocked > 0"
            (click)="load()"
          >
            {{ i18n.t('stockImport.load', { count: counts().toLoad }) }}
          </button>
        </div>
      }
    </section>
  `,
  styleUrls: ['./backoffice-ui.scss'],
})
export class OpeningImportComponent {
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  private readonly admin = inject(AdminService);
  private readonly confirm = inject(ConfirmService);
  private readonly catalog = inject(CatalogStore);

  /** The branches still in setup, already narrowed to the caller's own for anyone but the Admin. */
  readonly branches = input.required<AdminBranch[]>();
  /** After a sheet has been loaded, so the screen around this can refresh what it shows. */
  readonly loaded = output<OpeningImportResult>();

  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  readonly filters: ImportFilter[] = ['all', 'attention', 'new', 'existing'];
  readonly defaultPrefix = DEFAULT_PREFIX;

  readonly preview = signal<OpeningImportPreview | null>(null);
  readonly lines = signal<ImportLine[]>([]);
  readonly column = signal<number | null>(null);
  readonly filter = signal<ImportFilter>('all');
  readonly busy = signal(false);
  readonly reading = signal(false);
  readonly error = signal<string | null>(null);
  readonly done = signal<{ lines: number; branch: string; created: number; matched: number; first: string; last: string } | null>(null);
  /** Bumped on every tick or edit: the lines are mutated in place by the form, not replaced. */
  private readonly edits = signal(0);

  branchId = '';
  skuPrefix = DEFAULT_PREFIX;
  private file: File | null = null;

  readonly blocks = blocks;
  readonly issue = issueOf;

  constructor() {
    // The branch list arrives after the screen's first load, and shrinks when one is opened.
    effect(() => {
      const list = this.branches();
      if (!list.some((b) => b.id === this.branchId)) this.branchId = list[0]?.id ?? '';
    });
  }

  readonly counts = computed(() => {
    this.edits();
    return tally(this.lines());
  });

  readonly visible = computed(() => {
    this.edits();
    return shown(this.lines(), this.filter());
  });

  touched(): void {
    this.edits.update((n) => n + 1);
  }

  name(record: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(record, this.i18n.lang());
  }

  productName(line: ImportLine): string {
    return localizedName(
      { nameAr: line.productNameAr ?? '', nameFr: line.productNameFr ?? '', nameEn: line.productNameEn ?? '' },
      this.i18n.lang(),
    );
  }

  async pick(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0] ?? null;
    if (!file) return;
    this.file = file;
    this.done.set(null);
    await this.read(null);
  }

  async chooseSheet(sheet: string): Promise<void> {
    await this.read(sheet);
  }

  chooseColumn(column: number | null): void {
    const preview = this.preview();
    if (!preview) return;
    this.column.set(column);
    this.lines.set(linesFor(preview, column));
    this.touched();
  }

  reset(): void {
    this.file = null;
    this.preview.set(null);
    this.lines.set([]);
    this.column.set(null);
    this.filter.set('all');
    this.error.set(null);
    const input = this.fileInput()?.nativeElement;
    if (input) input.value = '';
  }

  async load(): Promise<void> {
    const preview = this.preview();
    const t = this.counts();
    if (!preview || !this.branchId || t.blocked || !t.toLoad) return;
    const branch = this.name(this.branches().find((b) => b.id === this.branchId) ?? { nameAr: '', nameFr: '', nameEn: '' });
    // Each article's quantity at the branch is replaced, not added to: worth a real question.
    const go = await this.confirm.ask({
      title: this.i18n.t('stockImport.confirmTitle', { branch }),
      message: this.i18n.t('stockImport.confirm', { count: t.toLoad, branch, created: t.created }),
      confirmLabel: this.i18n.t('stockImport.load', { count: t.toLoad }),
    });
    if (!go) return;

    this.busy.set(true);
    this.error.set(null);
    try {
      const result = await this.admin.importOpening(
        importRequest(this.lines(), this.branchId, {
          note: `${preview.fileName} — ${preview.sheetName}`,
          // Every item is rented and sold (owner, 2026-10-06); the server sets it either way.
          newProductsRentable: true,
          skuPrefix: this.skuPrefix,
        }),
      );
      const codes = result.createdProducts.map((p) => p.sku);
      this.done.set({
        lines: result.lines,
        branch,
        created: result.created,
        matched: result.matched,
        first: codes[0] ?? '',
        last: codes[codes.length - 1] ?? '',
      });
      this.reset();
      await this.catalog.refresh();
      this.loaded.emit(result);
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  private async read(sheet: string | null): Promise<void> {
    if (!this.file) return;
    this.busy.set(true);
    this.reading.set(true);
    this.error.set(null);
    try {
      const preview = await this.admin.previewOpeningImport(this.file, sheet);
      this.preview.set(preview);
      this.column.set(preview.quantityColumn);
      this.lines.set(linesFor(preview, preview.quantityColumn));
      this.filter.set('all');
      this.touched();
    } catch (e) {
      this.preview.set(null);
      this.lines.set([]);
      this.error.set(adminErrorMessage(e));
    } finally {
      this.reading.set(false);
      this.busy.set(false);
    }
  }
}
