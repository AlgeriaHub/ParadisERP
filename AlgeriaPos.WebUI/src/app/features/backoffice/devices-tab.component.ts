import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminDevice, AdminService, UpsertDeviceRequest, adminErrorMessage } from '../../core/admin.service';
import { ApiError } from '../../core/api.service';
import { BranchLock } from '../../core/branch-lock';
import { I18nService } from '../../core/i18n.service';
import { MENU } from '../../ui/menu.component';
import { StatusComponent } from '../../ui/status.component';
import { localizedName } from './backoffice-format';
import { TouchedFields } from './backoffice-forms';
import { ConfirmService } from '../../ui/confirm.service';
import { saveRequest } from '../../ui/confirm-requests';
import { StatusTone, toneOf } from '../../ui/status-tones';
import { TableStateComponent } from '../../ui/table-state.component';
import { ListFooterComponent } from '../../ui/list-footer.component';
import { createServerPager } from '../../ui/server-pager';
import { PAGE_CHROME } from './shell/page-chrome';
import { AdminPagesApi } from './admin-pages.api';

/** The server's refusal to put a till on a warehouse (DevicesEndpoints.WarehouseTillCode). */
const WAREHOUSE_TILL = 'warehouse_till';

/** The stable `code` of a ProblemDetails refusal; the transport hands the body back as the message. */
function problemCode(error: unknown): string | null {
  if (!(error instanceof ApiError)) return null;
  try {
    const parsed = JSON.parse(error.message) as { code?: unknown };
    return typeof parsed?.code === 'string' ? parsed.code : null;
  } catch {
    return null;
  }
}

@Component({
  selector: 'app-backoffice-devices',
  standalone: true,
  imports: [FormsModule, StatusComponent, ...PAGE_CHROME, ...MENU, TableStateComponent, ListFooterComponent],
  template: `
    <ng-template appPageActions>
      <button type="button" class="btn-primary" (click)="openCreate()">
        <span aria-hidden="true">+</span> {{ i18n.t('backoffice.devices.new') }}
      </button>
    </ng-template>

    <!-- The list's filters (owner, 2026-11-17) run on the server over every till: each change is a request. -->
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        @if (!lock.locked()) {
          <div class="ui-filter">
            <label for="dv-f-branch">{{ i18n.t('backoffice.devices.branch') }}</label>
            <select id="dv-f-branch" [ngModel]="branchFilter()" (ngModelChange)="branchFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ name(b) }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <label for="dv-f-state">{{ i18n.t('common.state') }}</label>
          <select id="dv-f-state" [ngModel]="stateFilter()" (ngModelChange)="stateFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="active">{{ i18n.t('common.active') }}</option>
            <option ngValue="inactive">{{ i18n.t('common.inactive') }}</option>
          </select>
        </div>
        <div class="ui-filter">
          <label for="dv-f-sync">{{ i18n.t('sync.lastSync') }}</label>
          <select id="dv-f-sync" [ngModel]="syncFilter()" (ngModelChange)="syncFilter.set($event)">
            <option ngValue="all">{{ i18n.t('filters.all') }}</option>
            <option ngValue="synced">{{ i18n.t('devices.filter.synced') }}</option>
            <option ngValue="never">{{ i18n.t('status.never') }}</option>
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
      @if ((error() || listError()) && !editorOpen()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }

      <section class="ui-surface" [attr.aria-busy]="!pager.loaded()" [attr.aria-label]="i18n.t('backoffice.tabs.devices')">
        @if (!pager.loaded()) {
          <app-table-state kind="loading" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('backoffice.devices.none')">
            <button type="button" class="btn-primary" (click)="openCreate()">{{ i18n.t('backoffice.devices.new') }}</button>
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('backoffice.devices.code') }}</th>
                  <th>{{ i18n.t('backoffice.devices.name') }}</th>
                  <th>{{ i18n.t('backoffice.devices.branch') }}</th>
                  <th>{{ i18n.t('sync.lastSync') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (d of pager.rows(); track d.id) {
                  <tr [class.is-muted]="!d.isActive">
                    <td class="mono" [attr.data-label]="i18n.t('backoffice.devices.code')">{{ d.code }}</td>
                    <td [attr.data-label]="i18n.t('backoffice.devices.name')">
                      <button type="button" class="ui-link" (click)="edit(d)">{{ d.name }}</button>
                    </td>
                    <td [attr.data-label]="i18n.t('backoffice.devices.branch')">
                      {{ branchName(d) }}
                      @if (d.onWarehouse) {
                        <span class="warehouse-note">
                          <app-status [tone]="warehouseTone">{{ i18n.t('backoffice.devices.onWarehouse') }}</app-status>
                          <span class="ui-sub">{{ i18n.t('backoffice.devices.onWarehouseHint') }}</span>
                        </span>
                      }
                    </td>
                    <td class="mono" [attr.data-label]="i18n.t('sync.lastSync')">
                      {{ d.lastSyncAtUtc ? i18n.dateTime(d.lastSyncAtUtc) : i18n.t('status.never') }}
                    </td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="activeTone(d.isActive)">{{ i18n.t(d.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                    </td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + d.name">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="edit(d)">{{ i18n.t('common.edit') }}</button>
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

      @if (editorOpen()) {
        <div class="bo-backdrop" (click)="cancelEdit()" (keydown.escape)="cancelEdit()">
          <section
            #editDialog
            class="bo-modal device-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="device-editor-title"
            (click)="$event.stopPropagation()"
          >
            <div class="bo-panel__head">
              <h2 id="device-editor-title" class="bo-panel__title">
                {{ editingId() ? i18n.t('common.edit') : i18n.t('backoffice.devices.new') }}
              </h2>
              <p class="bo-panel__note">
                {{ editingId() ? form.code : i18n.t('backoffice.devices.hint') }}
              </p>
            </div>
            <div class="bo-panel__body bo-form">
              @if (error()) {
                <p class="notice danger" role="alert">{{ error() }}</p>
              }
              <div class="bo-fields device-fields">
                <div class="bo-field" [class.is-invalid]="touched.missing('code', form.code)">
                  <label for="dv-edit-code">
                    {{ i18n.t('backoffice.devices.code') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                  </label>
                  <input
                    id="dv-edit-code"
                    [(ngModel)]="form.code"
                    class="mono"
                    [disabled]="!!editingId()"
                    [attr.aria-invalid]="touched.missing('code', form.code) ? 'true' : null"
                    [attr.aria-describedby]="touched.missing('code', form.code) ? 'dv-edit-code-error' : null"
                    (blur)="touched.touch('code')"
                  />
                  @if (touched.missing('code', form.code)) {
                    <span id="dv-edit-code-error" class="bo-error" role="alert">{{ i18n.t('common.fieldRequired') }}</span>
                  }
                </div>
                <div class="bo-field">
                  <label for="dv-edit-name">{{ i18n.t('backoffice.devices.name') }}</label>
                  <input id="dv-edit-name" [(ngModel)]="form.name" />
                </div>
                @if (lock.locked()) {
                  <div class="bo-field">
                    <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
                    <p class="bo-fixed" id="dv-fixed">{{ lock.ownBranchName(branches()) }}</p>
                    <span class="bo-hint">{{ i18n.t('branchLock.recordHint') }}</span>
                  </div>
                } @else {
                  <div class="bo-field">
                    <label for="dv-edit-branch">
                      {{ i18n.t('backoffice.devices.branch') }}<span class="bo-req">{{ i18n.t('common.required') }}</span>
                    </label>
                    <!-- A warehouse does not sell (the owner, 2026-10-05): it is listed so the
                         reader sees why it cannot be chosen, and stays choosable only for a till
                         already there, which can still be renamed or switched off. -->
                    <select id="dv-edit-branch" [(ngModel)]="form.branchId">
                      @for (b of branches(); track b.id) {
                        <option [ngValue]="b.id" [disabled]="b.isWarehouse && b.id !== keptWarehouseId()">
                          {{ name(b) }}@if (b.isWarehouse) { {{ i18n.t('backoffice.devices.warehouseOption') }} }
                        </option>
                      }
                    </select>
                    @if (!sellingBranches().length) {
                      <span class="bo-hint">{{ i18n.t('backoffice.devices.noSellingBranch') }}</span>
                    }
                  </div>
                }
                <div class="bo-check device-active">
                  <input id="dv-edit-active" type="checkbox" [(ngModel)]="form.isActive" />
                  <label for="dv-edit-active">{{ i18n.t('common.active') }}</label>
                </div>
              </div>
            </div>
            <div class="bo-panel__foot">
              <button type="button" class="btn-ghost" (click)="cancelEdit()">{{ i18n.t('common.cancel') }}</button>
              <button type="button" class="btn-primary" [disabled]="busy()" (click)="save()">
                {{ i18n.t('common.save') }}
              </button>
            </div>
          </section>
        </div>
      }
    </div>
  `,
  styleUrls: ['./backoffice-ui.scss', '../../ui/ui-patterns.scss'],
  styles: [
    `
      /* Code, name and branch describe one terminal and should read as one
         balanced row. The state is a separate choice below that identity. */
      .device-fields {
        grid-template-columns: repeat(3, minmax(0, 1fr));
      }
      .device-modal {
        width: min(52rem, 100%);
      }
      /* A till on the warehouse: the state in words under the branch, then what to do. */
      .warehouse-note {
        display: grid;
        justify-items: start;
        gap: var(--sp-1);
        margin-block-start: var(--sp-1);
      }
      .device-active {
        grid-column: 1 / -1;
        min-block-size: var(--control-h);
        padding-inline: 0.75rem;
        border: 1px solid var(--border);
        border-radius: var(--radius);
        background: var(--surface-2);
      }
      @media (max-width: 900px) {
        .device-fields {
          grid-template-columns: 1fr;
        }
      }
    `,
  ],
})
export class BackofficeDevicesComponent implements OnInit {
  @ViewChild('editDialog') private editDialog?: ElementRef<HTMLElement>;

  private readonly admin = inject(AdminService);
  private readonly pages = inject(AdminPagesApi);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);
  private readonly confirm = inject(ConfirmService);

  readonly branches = signal<AdminBranch[]>([]);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly editingId = signal<string | null>(null);
  readonly editorOpen = signal(false);
  /** The warehouse, while editing a till that is already on it; null otherwise. */
  readonly keptWarehouseId = signal<string | null>(null);
  /** Branches a till may be put on: every one but the warehouses, which do not sell. */
  readonly sellingBranches = computed(() => this.branches().filter((b) => !b.isWarehouse));
  readonly warehouseTone = toneOf('till', 'onWarehouse');

  // The list's filters (owner, 2026-11-17), applied on the server; a branch-held reader's branch is theirs there.
  readonly branchFilter = signal<string>('all');
  readonly stateFilter = signal<'all' | 'active' | 'inactive'>('all');
  readonly syncFilter = signal<'all' | 'synced' | 'never'>('all');
  readonly filtered = computed(() => this.branchFilter() !== 'all' || this.stateFilter() !== 'all' || this.syncFilter() !== 'all');
  /** The tills a page at a time from the server (owner, 2026-11-17: "paginate in backend"), by code. */
  readonly pager = createServerPager(
    (page) => this.pages.devicesPage({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'admin.devices',
      filters: () => ({ branchId: this.branchFilter(), state: this.stateFilter(), sync: this.syncFilter() }),
    },
  );
  /** The page on screen. */
  readonly devices = computed(() => this.pager.rows());

  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? adminErrorMessage(e) : null;
  });

  clearFilters(): void {
    this.branchFilter.set('all');
    this.stateFilter.set('all');
    this.syncFilter.set('all');
  }

  activeTone(isActive: boolean): StatusTone {
    return toneOf('active', isActive ? 'Active' : 'Inactive');
  }

  /** A branch named in the reader's language, not always in French. */
  name(record: { nameAr: string; nameFr: string; nameEn: string }): string {
    return localizedName(record, this.i18n.lang());
  }

  /** The till's branch in the reader's language; the server's row carries the French name only. */
  branchName(d: AdminDevice): string {
    const branch = this.branches().find((b) => b.id === d.branchId);
    return branch ? this.name(branch) : d.branchName;
  }

  /** Which required fields have earned the right to complain. */
  readonly touched = new TouchedFields();

  form: UpsertDeviceRequest = this.emptyForm();

  async ngOnInit(): Promise<void> {
    // The list loads itself; the branches name its rows and fill the form.
    await this.loadBranches();
  }

  private emptyForm(): UpsertDeviceRequest {
    // A locked manager's own branch, else the first branch that sells — never the warehouse.
    const preferred = this.lock.defaultBranch(this.branches());
    const branchId = this.lock.locked() || this.sellingBranches().some((b) => b.id === preferred)
      ? preferred
      : this.sellingBranches()[0]?.id;
    return { code: '', name: '', branchId: branchId ?? '', isActive: true };
  }

  /** After a save: the branches and the page on screen, read again. */
  async reload(): Promise<void> {
    await Promise.all([this.loadBranches(), this.pager.reload()]);
  }

  private async loadBranches(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      this.branches.set(await this.admin.listBranches());
      if (!this.editorOpen()) this.form = this.emptyForm();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  openCreate(): void {
    this.editingId.set(null);
    this.keptWarehouseId.set(null);
    this.touched.reset();
    this.error.set(null);
    this.form = this.emptyForm();
    this.editorOpen.set(true);
    this.focusEditor('#dv-edit-code');
  }

  edit(d: AdminDevice): void {
    this.touched.reset();
    this.error.set(null);
    this.editingId.set(d.id);
    this.keptWarehouseId.set(d.onWarehouse ? d.branchId : null);
    this.form = { code: d.code, name: d.name, branchId: d.branchId, isActive: d.isActive };
    this.editorOpen.set(true);
    this.focusEditor('#dv-edit-name');
  }

  cancelEdit(): void {
    this.editorOpen.set(false);
    this.editingId.set(null);
    this.keptWarehouseId.set(null);
    this.touched.reset();
    this.form = this.emptyForm();
  }

  async save(): Promise<void> {
    this.touched.touchAll('code');
    if (!this.form.code.trim() || !this.form.branchId) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n, this.form.code)))) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const id = this.editingId();
      const payload: UpsertDeviceRequest = { ...this.form, code: this.form.code.trim().toUpperCase() };
      if (id) await this.admin.updateDevice(id, payload);
      else await this.admin.createDevice(payload);
      this.cancelEdit();
      await this.reload();
    } catch (e) {
      this.error.set(this.deviceError(e));
    } finally {
      this.busy.set(false);
    }
  }

  private focusEditor(selector: string): void {
    /* Focus enters the modal after Angular has rendered it. */
    setTimeout(() => this.editDialog?.nativeElement.querySelector<HTMLInputElement>(selector)?.focus());
  }

  private deviceError(error: unknown): string {
    if (problemCode(error) === WAREHOUSE_TILL) return this.i18n.t('backoffice.devices.warehouseRefused');
    const message = adminErrorMessage(error);
    const duplicate = message.match(/Terminal code ['"]?(.+?)['"]? already exists\.?$/i);
    return duplicate
      ? this.i18n.t('backoffice.devices.codeExists', { code: duplicate[1] })
      : message;
  }
}
