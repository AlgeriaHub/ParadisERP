import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminService, UpsertBranchRequest, adminErrorMessage, problemCode } from '../../core/admin.service';
import { SecureImageService, readAsDataUrl } from '../../core/secure-image.service';
import { LOGO_TYPES } from '../print/store-documents-api.service';

/** The server's cap on a branch's logo (StoreLogo.BranchMaxBytes): half the company's, since it travels to the tills. */
export const BRANCH_LOGO_MAX_BYTES = 256 * 1024;

/** The refusals BranchEndpoints sends with a stable code; each has a sentence under `backoffice.branches.error.`. */
export const BRANCH_CODES = [
  'main_branch_required',
  'main_warehouse_required',
  'warehouse_has_tills',
  'warehouse_has_cash',
  'warehouse_has_stock',
  // Location 901: every three-digit code, 100–999, is taken (owner, 2026-10-05).
  'branch_codes_exhausted',
  // Two locations with one name (owner, 2026-10-06).
  'branch_name_taken',
];

import { BranchLock } from '../../core/branch-lock';
import { I18nService } from '../../core/i18n.service';
import { RegionalService } from '../../core/regional.service';
import { localizedName } from './backoffice-format';
import { TouchedFields } from './backoffice-forms';
import { ConfirmService } from '../../ui/confirm.service';
import { saveRequest } from '../../ui/confirm-requests';
import { MENU } from '../../ui/menu.component';
import { StatusComponent } from '../../ui/status.component';
import { StatusTone, toneOf } from '../../ui/status-tones';
import { TableStateComponent } from '../../ui/table-state.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { PAGE_CHROME } from './shell/page-chrome';
import { FoldComponent } from '../../ui/fold.component';
import { AdminPagesApi } from './admin-pages.api';

@Component({
  selector: 'app-backoffice-branches',
  standalone: true,
  imports: [FormsModule, StatusComponent, FoldComponent, ...PAGE_CHROME, ...MENU, TableStateComponent, ListFooterComponent],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-primary" (click)="openCreate()">
        <span aria-hidden="true">+</span> {{ i18n.t('backoffice.branches.new') }}
      </button>
    </ng-template>

    <!-- The list's filters (owner, 2026-11-17) run on the server over every location: each change is a request. -->
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="br-f-kind">{{ i18n.t('backoffice.branches.kind') }}</label>
          <select id="br-f-kind" [ngModel]="kindFilter()" (ngModelChange)="kindFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="branch">{{ i18n.t('backoffice.branches.kind.branch') }}</option>
            <option ngValue="warehouse">{{ i18n.t('backoffice.branches.kind.warehouse') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="br-f-state">{{ i18n.t('common.state') }}</label>
          <select id="br-f-state" [ngModel]="stateFilter()" (ngModelChange)="stateFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="active">{{ i18n.t('common.active') }}</option>
            <option ngValue="inactive">{{ i18n.t('common.inactive') }}</option>
            <option ngValue="setup">{{ i18n.t('stock.inSetup') }}</option>
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
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }

      @if (editorOpen()) {
        <div class="bo-backdrop" (click)="cancelEdit()"></div>
      }
      <section
        class="bo-panel"
        [class.bo-edit-modal]="editorOpen()"
        [class.bo-editor-hidden]="!editorOpen()"
        [attr.role]="editorOpen() ? 'dialog' : null"
        [attr.aria-modal]="editorOpen() ? 'true' : null"
        [attr.aria-label]="editorOpen() ? (editingId() ? i18n.t('common.edit') : i18n.t('backoffice.branches.new')) : null"
        (keydown.escape)="editorOpen() && cancelEdit()"
      >
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ editingId() ? i18n.t('common.edit') : i18n.t('backoffice.branches.new') }}</h2>
          <p class="bo-panel__note">{{ i18n.t('backoffice.branches.hint') }}</p>
        </div>
        <div class="bo-panel__body bo-form">
          <!-- Two kinds (the owner, 2026-10-05): a branch sells, a warehouse holds stock. One main
               of each, always; ticking "main" moves the flag from the current one, in the same save. -->
          <app-fold class="bo-group" [heading]="i18n.t('backoffice.branches.kind')" [open]="true">
            <div class="kind-group">
              <!-- The fold's heading is a button, so the radio group keeps its own name. -->
              <span class="sr-only" id="br-kind-title">{{ i18n.t('backoffice.branches.kind') }}</span>
              <div class="kind-choice" role="radiogroup" aria-labelledby="br-kind-title" aria-describedby="br-kind-hint">
              <div class="bo-check">
                <input
                  id="br-kind-branch" type="radio" name="br-kind"
                  [checked]="!form.isWarehouse" [disabled]="kindLocked()"
                  (change)="setKind(false)"
                />
                <label for="br-kind-branch">{{ i18n.t('backoffice.branches.kind.branch') }}</label>
              </div>
              <div class="bo-check">
                <input
                  id="br-kind-warehouse" type="radio" name="br-kind"
                  [checked]="form.isWarehouse" [disabled]="kindLocked()"
                  (change)="setKind(true)"
                />
                <label for="br-kind-warehouse">{{ i18n.t('backoffice.branches.kind.warehouse') }}</label>
              </div>
              </div>
              <p class="bo-hint" id="br-kind-hint">
                {{ i18n.t(form.isWarehouse ? 'backoffice.branches.kind.warehouseHint' : 'backoffice.branches.kind.branchHint') }}
              </p>
              <div class="bo-check">
                <input
                  id="br-main" type="checkbox"
                  [(ngModel)]="form.isMain" [disabled]="isCurrentMain()"
                  aria-describedby="br-main-hint"
                />
                <label for="br-main">{{ i18n.t('backoffice.branches.setPrimary') }}</label>
              </div>
              @if (isCurrentMain()) {
                <p id="br-main-hint" class="bo-hint">
                  {{ i18n.t(form.isWarehouse ? 'backoffice.branches.mainLocked.warehouse' : 'backoffice.branches.mainLocked.branch') }}
                </p>
              } @else if (form.isMain && mainHolder(); as holder) {
                <p id="br-main-hint" class="notice warn" role="status">
                  {{ i18n.t(form.isWarehouse ? 'backoffice.branches.mainMoves.warehouse' : 'backoffice.branches.mainMoves.branch', { name: name(holder) }) }}
                </p>
              } @else {
                <p id="br-main-hint" class="bo-hint">
                  {{ i18n.t(form.isWarehouse ? 'backoffice.branches.mainHint.warehouse' : 'backoffice.branches.mainHint.branch') }}
                </p>
              }
            </div>
          </app-fold>

          <app-fold class="bo-group" [heading]="i18n.t('common.group.names')" [open]="true">
            <div class="bo-fields">
              <!-- The location's code (owner, 2026-10-05): three digits, 100–999, drawn by the server when
                   the location is created and fixed for life — it is inside every customer and
                   employee code drawn here. Text, not a control: nothing about it can be chosen. -->
              <div class="bo-field">
                <p class="bo-fixed-label">{{ i18n.t('backoffice.branches.code') }}</p>
                @if (loaded(); as was) {
                  <p class="bo-fixed mono" id="br-code-fixed"><span dir="ltr">{{ was.code || '—' }}</span></p>
                } @else {
                  <p class="bo-fixed" id="br-code-new">{{ i18n.t('backoffice.branches.code.onSave') }}</p>
                }
                <p class="bo-hint" id="br-code-hint">{{ i18n.t('backoffice.branches.code.hint') }}</p>
              </div>
              <div class="bo-field" [class.is-invalid]="touched.missing('nameFr', form.nameFr)">
                <label for="br-fr">
                  {{ i18n.t('field.nameFr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                </label>
                <input
                  id="br-fr" [(ngModel)]="form.nameFr"
                  [attr.aria-invalid]="touched.missing('nameFr', form.nameFr) ? 'true' : null"
                  [attr.aria-describedby]="touched.missing('nameFr', form.nameFr) ? 'br-fr-error' : null"
                  (blur)="touched.touch('nameFr')"
                />
                @if (touched.missing('nameFr', form.nameFr)) {
                  <span id="br-fr-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                }
              </div>
              <div class="bo-field">
                <label for="br-ar">{{ i18n.t('field.nameAr') }}</label>
                <input id="br-ar" [(ngModel)]="form.nameAr" dir="rtl" />
              </div>
              <div class="bo-field">
                <label for="br-en">{{ i18n.t('field.nameEn') }}</label>
                <input id="br-en" [(ngModel)]="form.nameEn" />
              </div>
            </div>
          </app-fold>

          <app-fold class="bo-group" [heading]="i18n.t('common.group.contact')">
            <div class="bo-fields bo-fields--roomy">
              <div class="bo-field">
                <label for="br-address-fr">{{ i18n.t('backoffice.branches.addressFr') }}</label>
                <input id="br-address-fr" [(ngModel)]="form.address" />
              </div>
              <div class="bo-field">
                <label for="br-address-ar">{{ i18n.t('backoffice.branches.addressAr') }}</label>
                <input id="br-address-ar" [(ngModel)]="form.addressAr" dir="rtl" />
              </div>
              <div class="bo-field">
                <label for="br-address-en">{{ i18n.t('backoffice.branches.addressEn') }}</label>
                <input id="br-address-en" [(ngModel)]="form.addressEn" />
              </div>
              <div class="bo-field">
                <label for="br-phone">{{ i18n.t('backoffice.store.phone') }}</label>
                <input id="br-phone" class="mono" [(ngModel)]="form.phone" />
              </div>
            </div>
          </app-fold>

          <!-- The branch's own register, NIF and the rest (owner, 2026-09-30): the same fields as the
               company's, named by the country profile. A blank one is not missing — it prints the
               company's, which the placeholder shows so the clerk sees what the paper will say. -->
          @if (profile().fiscalIds.length) {
            <app-fold class="bo-group" [heading]="i18n.t('branchIds.title')">
              <p class="bo-group__note" foldLead>{{ i18n.t('branchIds.hint') }}</p>
              <div class="bo-fields">
                @for (id of profile().fiscalIds; track id.key) {
                  <div class="bo-field">
                    <label [for]="'br-fiscal-' + id.key">{{ i18n.text(id.labels) || id.code }}</label>
                    <input
                      [id]="'br-fiscal-' + id.key"
                      class="mono"
                      dir="ltr"
                      maxlength="40"
                      [(ngModel)]="fiscalIds[id.key]"
                      [placeholder]="companyFiscalIds()[id.key] ?? ''"
                    />
                  </div>
                }
              </div>
            </app-fold>
          }

          <!-- The branch's own logo, shown with its name in the top bar (the owner, 2026-09-30).
               Without one the bar shows the main branch's. Printed documents keep the company's. -->
          <app-fold class="bo-group" [heading]="i18n.t('branchLogo.title')">
            <p class="bo-group__note" foldLead>{{ i18n.t('branchLogo.hint') }}</p>
            <div class="logo-row">
              <div class="logo-preview">
                @if (form.logoUrl) {
                  @if (logoPreview(); as src) {
                    <img [src]="src" [attr.alt]="form.nameAr || form.nameFr" />
                  }
                } @else {
                  <span class="bo-soft">{{ i18n.t('branchLogo.none') }}</span>
                }
              </div>
              <!-- The Admin's alone (the owner, 2026-09-30); the server ignores anyone else's. -->
              @if (lock.locked()) {
                <p class="bo-hint" id="br-logo-admin-only">{{ i18n.t('branchLogo.adminOnly') }}</p>
              } @else {
              <div class="bo-field">
                <div class="bo-actions bo-actions--start">
                  <button type="button" class="btn-ghost" id="br-logo-choose" (click)="logoFile.click()">
                    {{ i18n.t(form.logoUrl ? 'logo.replace' : 'logo.choose') }}
                  </button>
                  <input #logoFile id="br-logo-file" type="file" hidden [accept]="logoTypes" (change)="pickLogo($event)" />
                  @if (form.logoUrl) {
                    <button type="button" class="btn-ghost" (click)="removeLogo()">{{ i18n.t('logo.remove') }}</button>
                  }
                </div>
                <span class="bo-hint">{{ i18n.t('logo.types', { kb: logoMaxKb }) }}</span>
                @if (logoProblem()) {
                  <span class="bo-error" role="alert">{{ logoProblem() }}</span>
                }
              </div>
              }
            </div>
          </app-fold>

          <app-fold class="bo-group" [heading]="i18n.t('common.group.options')">
            <div class="bo-fields bo-fields--roomy">
              <div class="bo-check">
                <input id="br-neg" type="checkbox" [(ngModel)]="form.allowNegativeStock" />
                <label for="br-neg">{{ i18n.t('backoffice.branches.allowNegativeStock') }}</label>
              </div>
              <div class="bo-check">
                <input id="br-active" type="checkbox" [(ngModel)]="form.isActive" />
                <label for="br-active">{{ i18n.t('common.active') }}</label>
              </div>
              <!-- A branch is never deleted (owner, 2026-10-06): this box is how one stops being used. -->
              <p class="bo-hint bo-field--wide">{{ i18n.t('backoffice.branches.activeHint') }}</p>
            </div>
          </app-fold>
        </div>
        <div class="bo-panel__foot">
          <button type="button" class="btn-ghost" (click)="cancelEdit()">{{ i18n.t('common.cancel') }}</button>
          <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">
            {{ i18n.t('common.save') }}
          </button>
        </div>
      </section>

      <section class="ui-surface" [attr.aria-busy]="!pager.loaded()" [attr.aria-label]="i18n.t('backoffice.tabs.branches')">
        @if (!pager.loaded()) {
          <app-table-state kind="loading" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('backoffice.branches.none')">
            <button type="button" class="btn-primary" (click)="openCreate()">{{ i18n.t('backoffice.branches.new') }}</button>
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('backoffice.branches.code') }}</th>
                  <th>{{ i18n.t('common.name') }}</th>
                  <th>{{ i18n.t('backoffice.branches.kind') }}</th>
                  <th>{{ i18n.t('backoffice.store.address') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (b of pager.rows(); track b.id) {
                  <tr [class.is-muted]="!b.isActive">
                    <td class="mono" [attr.data-label]="i18n.t('backoffice.branches.code')"><span dir="ltr">{{ b.code || '—' }}</span></td>
                    <td [attr.data-label]="i18n.t('common.name')">
                      <button type="button" class="ui-link" (click)="edit(b)">{{ name(b) }}</button>
                    </td>
                    <td [attr.data-label]="i18n.t('backoffice.branches.kind')">
                      <span class="bo-states">
                        <span>{{ i18n.t(b.isWarehouse ? 'backoffice.branches.kind.warehouse' : 'backoffice.branches.kind.branch') }}</span>
                        @if (b.isMain) {
                          <!-- Read as two words, not one: the badge sits beside the kind visually only. -->
                          <span class="sr-only"> — </span>
                          <app-status [tone]="primaryTone">{{ i18n.t('backoffice.branches.primary') }}</app-status>
                        }
                      </span>
                    </td>
                    <td [attr.data-label]="i18n.t('backoffice.store.address')">{{ branchAddress(b) || '—' }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <span class="bo-states">
                        <app-status [tone]="activeTone(b.isActive)">{{ i18n.t(b.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                        @if (b.isOpenForOperation === false) {
                          <app-status tone="warning">{{ i18n.t('stock.inSetup') }}</app-status>
                        }
                        @if (hasOwnFiscalIds(b)) {
                          <app-status tone="neutral">{{ i18n.t('branchIds.own') }}</app-status>
                        }
                      </span>
                    </td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + name(b)">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="edit(b)">{{ i18n.t('common.edit') }}</button>
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
  `,
  styleUrls: ['./backoffice-ui.scss', '../../ui/ui-patterns.scss'],
  styles: [
    `
      /* The kind and the primary flag read as one decision: the two kinds side by side, what the
         chosen kind means, then "Set as primary" with what it will do. */
      .kind-group {
        display: flex;
        flex-direction: column;
        align-items: flex-start;
        gap: var(--sp-2);
      }
      .kind-choice {
        display: flex;
        flex-wrap: wrap;
        gap: var(--sp-2) var(--sp-6);
      }
      .kind-group .bo-hint,
      .kind-group .notice {
        margin: 0;
        max-inline-size: var(--read-max);
      }
      .logo-row {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-start;
        gap: 1rem;
      }
      .logo-preview {
        display: flex;
        align-items: center;
        justify-content: center;
        min-inline-size: 8rem;
        block-size: 4rem;
        padding: 0.5rem;
        background: var(--surface);
        border: 1px solid var(--border);
        border-radius: var(--radius);
      }
      .logo-preview img {
        block-size: 100%;
        max-inline-size: 12rem;
        object-fit: contain;
      }
    `,
  ],
})
export class BackofficeBranchesComponent implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly pages = inject(AdminPagesApi);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  private readonly confirm = inject(ConfirmService);

  // The list's filters (owner, 2026-11-17), applied on the server over every location the reader sees.
  readonly kindFilter = signal<'all' | 'branch' | 'warehouse'>('all');
  readonly stateFilter = signal<'all' | 'active' | 'inactive' | 'setup'>('all');
  readonly filtered = computed(() => this.kindFilter() !== 'all' || this.stateFilter() !== 'all');
  /**
   * The locations a page at a time from the server (owner, 2026-11-17: "paginate in backend"). A non-admin sees
   * their own branch, as before (BranchLock), now decided there; the main branch and the main warehouse come in the
   * summary, whatever the page, so the editor can say whose flag a save takes.
   */
  readonly pager = createServerPager(
    (page) => this.pages.branchesPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'admin.branches',
      filters: () => ({ kind: this.kindFilter(), state: this.stateFilter() }),
    },
  );
  /** The page on screen. */
  readonly branches = computed(() => this.pager.rows());

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  clearFilters(): void {
    this.kindFilter.set('all');
    this.stateFilter.set('all');
  }

  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly editingId = signal<string | null>(null);
  readonly editorOpen = signal(false);
  /** Which identifiers exist and what they are called: the country profile's, never a list here. */
  readonly profile = inject(RegionalService).settings;
  /** The company's own values, shown as each blank field's placeholder — what that field prints. */
  readonly companyFiscalIds = signal<Record<string, string>>({});
  /** The branch's own values being typed, beside `form` so a reset is one assignment. */
  fiscalIds: Record<string, string> = {};

  hasOwnFiscalIds(branch: AdminBranch): boolean {
    return Object.values(branch.fiscalIds ?? {}).some((v) => v?.trim());
  }

  /** The list carries one name column, so it carries the reader's name. */
  name(record: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(record, this.i18n.lang());
  }

  branchAddress(branch: AdminBranch): string {
    const lang = this.i18n.lang();
    if (lang === 'ar') return branch.addressAr || branch.addressFr || branch.address;
    if (lang === 'en') return branch.addressEn || branch.addressFr || branch.address;
    return branch.addressFr || branch.address;
  }

  /** Which required fields have earned the right to complain. */
  readonly touched = new TouchedFields();

  // The logo: the store tab's four types, at the branch cap — it replicates to every till.
  readonly logoTypes = LOGO_TYPES.join(',');
  readonly logoMaxKb = BRANCH_LOGO_MAX_BYTES / 1024;
  readonly logoProblem = signal<string | null>(null);
  /** What the preview draws: a picked file as it is, a saved logo fetched with the token. */
  readonly logoPreview = signal<string | null>(null);
  private readonly images = inject(SecureImageService);

  /** Reads the picked file into the form; nothing is sent until Save. */
  async pickLogo(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    if (!LOGO_TYPES.includes(file.type)) {
      this.logoProblem.set(this.i18n.t('logo.badType'));
      return;
    }
    if (file.size > BRANCH_LOGO_MAX_BYTES) {
      this.logoProblem.set(this.i18n.t('logo.tooBig', { size: Math.ceil(file.size / 1024), kb: this.logoMaxKb }));
      return;
    }
    try {
      this.setLogo(await readAsDataUrl(file));
      this.logoProblem.set(null);
    } catch {
      this.logoProblem.set(this.i18n.t('logo.unreadable'));
    }
  }

  removeLogo(): void {
    this.setLogo(null);
    this.logoProblem.set(null);
  }

  /**
   * The form's logo: the saved link (sent back as it is, which keeps it), a picked file as a
   * `data:` URL (which replaces it), or null (which removes it).
   */
  private setLogo(logoUrl: string | null): void {
    this.form = { ...this.form, logoUrl };
    this.logoPreview.set(null);
    void this.images.src(logoUrl).then((src) => {
      if (this.form.logoUrl === logoUrl) this.logoPreview.set(src);
    });
  }

  form: UpsertBranchRequest = this.emptyForm();

  async ngOnInit(): Promise<void> {
    // The list loads itself through its pager.
    try {
      this.companyFiscalIds.set({ ...((await this.admin.getStore()).fiscalIds ?? {}) });
    } catch {
      /* the placeholders simply stay empty; the fields work the same */
    }
  }

  private emptyForm(): UpsertBranchRequest {
    return {
      nameAr: '', nameFr: '', nameEn: '', address: '', addressAr: '', addressEn: '', phone: '',
      isMain: false, isWarehouse: false, allowNegativeStock: false, isActive: true, logoUrl: null,
    };
  }

  /** The record being edited as it was loaded, so the form knows what it held before — and its code. */
  readonly loaded = signal<AdminBranch | null>(null);

  /** The main one of a kind, as the server reported it beside the list (whatever the page). */
  private mainOf(warehouse: boolean): AdminBranch | null {
    return this.pager.summary()?.mains.find((b) => b.isMain && !!b.isWarehouse === warehouse) ?? null;
  }

  /** The current main one of the chosen kind, when it is another record: it loses the flag on save. */
  mainHolder(): AdminBranch | null {
    const holder = this.mainOf(this.form.isWarehouse);
    return holder && holder.id !== this.editingId() ? holder : null;
  }

  /**
   * The record is the main one of its kind: "main" cannot be unticked and the kind cannot change —
   * the company would be left without one. Another record takes the flag first.
   */
  isCurrentMain(): boolean {
    const was = this.loaded();
    return !!was && was.isMain;
  }

  /** The kind is fixed for the main one of its kind (see {@link isCurrentMain}). */
  kindLocked(): boolean {
    return this.isCurrentMain();
  }

  setKind(warehouse: boolean): void {
    // "Main" means the main one of the chosen kind, so it starts unticked on a change of kind.
    this.form = { ...this.form, isWarehouse: warehouse, isMain: false };
  }

  /** The list's "Primary" badge: in words, toned from the shared mapping. */
  readonly primaryTone = toneOf('location', 'primary');

  activeTone(isActive: boolean): StatusTone {
    return toneOf('active', isActive ? 'Active' : 'Inactive');
  }

  /** The server's refusals in the reader's words (BranchEndpoints' codes). */
  private branchError(error: unknown): string {
    const code = problemCode(error);
    return code && BRANCH_CODES.includes(code) ? this.i18n.t('backoffice.branches.error.' + code) : adminErrorMessage(error);
  }

  /** Asks again for the page on screen, after a save. A non-admin's list is their own branch (the server's BranchLock). */
  async reload(): Promise<void> {
    this.error.set(null);
    await this.pager.reload();
  }

  openCreate(): void {
    this.cancelEdit();
    this.editorOpen.set(true);
  }

  edit(b: AdminBranch): void {
    this.editingId.set(b.id);
    this.loaded.set(b);
    this.form = {
      nameAr: b.nameAr,
      nameFr: b.nameFr,
      nameEn: b.nameEn,
      address: b.addressFr ?? b.address,
      addressAr: b.addressAr ?? '',
      addressEn: b.addressEn ?? '',
      phone: b.phone,
      isMain: b.isMain,
      isWarehouse: !!b.isWarehouse,
      allowNegativeStock: b.allowNegativeStock,
      isActive: b.isActive,
      logoUrl: null,
    };
    this.setLogo(b.logoUrl ?? null);
    this.fiscalIds = { ...(b.fiscalIds ?? {}) };
    this.editorOpen.set(true);
  }

  cancelEdit(): void {
    this.editorOpen.set(false);
    this.editingId.set(null);
    this.loaded.set(null);
    this.touched.reset();
    this.form = this.emptyForm();
    this.logoPreview.set(null);
    this.fiscalIds = {};
    this.logoProblem.set(null);
  }

  async save(): Promise<void> {
    this.touched.touchAll('nameFr');
    if (!this.form.nameFr.trim()) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.name(this.form))))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const id = this.editingId();
      // Every identifier the profile defines is sent, blank ones as blank, so clearing a field
      // clears it; the server drops the blanks and keeps any value under a key since removed.
      const fiscalIds = Object.fromEntries(
        this.profile().fiscalIds.map((d) => [d.key, this.fiscalIds[d.key]?.trim() ?? '']),
      );
      const request: UpsertBranchRequest = { ...this.form, fiscalIds };
      if (id) await this.admin.updateBranch(id, request);
      else await this.admin.createBranch(request);
      this.cancelEdit();
      await this.reload();
    } catch (e) {
      this.error.set(this.branchError(e));
    } finally {
      this.busy.set(false);
    }
  }
}
