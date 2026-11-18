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
import { JobRoleDto, ManufacturingApiService, UpsertJobRoleRequest } from './manufacturing-api.service';
import { ManufacturingPagesApi } from './manufacturing-pages.api';
import { mfgError } from './manufacturing-shared';

/**
 * Manufacturing › Job roles (Phase 1 §6): the jobs of the shop floor that stage templates name —
 * welder, cutter, painter. The HR file has only a free-text job title and no grade table, so
 * manufacturing keeps this short list of its own. It holds no pay: wages stay in payroll.
 */
@Component({
  selector: 'app-mfg-job-roles',
  standalone: true,
  imports: [
    FormsModule, ...PAGE_CHROME, ...MENU, DialogComponent, FormSectionComponent, ListFooterComponent, StatusComponent, TableStateComponent,
  ],
  template: `
    @if (canEdit()) {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="openCreate()"><span aria-hidden="true">+</span> {{ i18n.t('mfg.jobRoles.new') }}</button>
      </ng-template>
    }
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter">
          <label for="jr-f-state">{{ i18n.t('common.state') }}</label>
          <select id="jr-f-state" [ngModel]="stateFilter()" (ngModelChange)="stateFilter.set($event)">
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
          <app-table-state kind="empty" [message]="i18n.t('mfg.jobRoles.none')">
            @if (canEdit()) {
              <button type="button" class="btn-primary" (click)="openCreate()">{{ i18n.t('mfg.jobRoles.new') }}</button>
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
                  <th>{{ i18n.t('mfg.common.description') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  @if (canEdit()) {
                    <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (r of pager.rows(); track r.id) {
                  <tr [class.is-muted]="!r.isActive">
                    <td class="mono" [attr.data-label]="i18n.t('mfg.common.code')"><span dir="ltr">{{ r.code }}</span></td>
                    <td [attr.data-label]="i18n.t('mfg.common.name')">
                      @if (canEdit()) {
                        <button type="button" class="ui-link" (click)="edit(r)">{{ name(r) }}</button>
                      } @else {
                        {{ name(r) }}
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('mfg.common.description')">{{ r.description || '—' }}</td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(r.isActive)">{{ i18n.t(r.isActive ? 'common.active' : 'common.inactive') }}</app-status>
                    </td>
                    @if (canEdit()) {
                      <td class="ui-cell-menu">
                        <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + r.code">
                          <span menuTrigger aria-hidden="true">⋯</span>
                          <button type="button" appMenuItem (click)="edit(r)">{{ i18n.t('common.edit') }}</button>
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

    <app-dialog [open]="editorOpen()" size="md" [heading]="editing() ? i18n.t('common.edit') + ' — ' + editing()!.code : i18n.t('mfg.jobRoles.new')" (dismiss)="close()">
      @if (editorOpen()) {
        <div class="editor bo-form">
          @if (formError()) {
            <p class="notice danger" role="alert">{{ formError() }}</p>
          }
          <app-form-section [heading]="i18n.t('common.group.identity')" [columns]="3">
            <div class="bo-field" [class.is-invalid]="touched.missing('code', form.code)">
              <label for="jr-code">{{ i18n.t('mfg.common.code') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="jr-code" class="mono" dir="ltr" maxlength="24" [(ngModel)]="form.code" (blur)="touched.touch('code')" />
            </div>
            <div class="bo-field" [class.is-invalid]="touched.missing('nameAr', form.nameAr)">
              <label for="jr-ar">{{ i18n.t('field.nameAr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="jr-ar" dir="rtl" [(ngModel)]="form.nameAr" (blur)="touched.touch('nameAr')" />
            </div>
            <div class="bo-field" [class.is-invalid]="touched.missing('nameFr', form.nameFr)">
              <label for="jr-fr">{{ i18n.t('field.nameFr') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <input id="jr-fr" dir="ltr" [(ngModel)]="form.nameFr" (blur)="touched.touch('nameFr')" />
            </div>
            <div class="bo-field">
              <label for="jr-en">{{ i18n.t('field.nameEn') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <input id="jr-en" dir="ltr" [(ngModel)]="form.nameEn" />
            </div>
            <div class="bo-field bo-field--wide">
              <label for="jr-desc">{{ i18n.t('mfg.common.description') }}<span class="bo-req">{{ i18n.t('common.optional') }}</span></label>
              <input id="jr-desc" maxlength="400" [(ngModel)]="form.description" />
            </div>
          </app-form-section>
          <div class="bo-check">
            <input id="jr-active" type="checkbox" [(ngModel)]="form.isActive" />
            <label for="jr-active">{{ i18n.t('common.active') }}</label>
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
export class MfgJobRolesComponent implements OnInit {
  private readonly api = inject(ManufacturingApiService);
  private readonly pages = inject(ManufacturingPagesApi);
  private readonly confirm = inject(ConfirmService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly canEdit = input(false);
  readonly busy = signal(false);
  readonly editorOpen = signal(false);
  readonly editing = signal<JobRoleDto | null>(null);
  readonly formError = signal<string | null>(null);
  readonly touched = new TouchedFields();
  form: UpsertJobRoleRequest = this.emptyForm();

  /** Active or switched off, filtered on the server. */
  readonly stateFilter = signal<'all' | 'active' | 'inactive'>('all');
  readonly filtered = computed(() => this.stateFilter() !== 'all');
  /** The job roles a page at a time from the server (owner, 2026-11-17: "paginate in backend, not frontend"). */
  readonly pager = createServerPager(
    (page) => this.pages.jobRoles({ skip: page.skip, take: page.take }, page.filters),
    { key: 'mfg.jobRoles', filters: () => ({ state: this.stateFilter() }) },
  );

  /** The list could not be read, in words. */
  readonly error = computed(() => {
    const e = this.pager.error();
    return e ? mfgError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.stateFilter.set('all');
  }

  async ngOnInit(): Promise<void> {
    this.chrome?.description.set(this.i18n.t('mfg.jobRoles.lead'));
  }

  /** Asks again for the page on screen — after a save. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  name(r: JobRoleDto): string {
    return localizedName(r, this.i18n.lang());
  }

  tone(active: boolean) {
    return toneOf('active', active ? 'Active' : 'Inactive');
  }

  private emptyForm(): UpsertJobRoleRequest {
    return { code: '', nameAr: '', nameFr: '', nameEn: '', description: '', isActive: true };
  }

  openCreate(): void {
    this.editing.set(null);
    this.form = this.emptyForm();
    this.touched.reset();
    this.formError.set(null);
    this.editorOpen.set(true);
  }

  edit(r: JobRoleDto): void {
    this.editing.set(r);
    this.form = { code: r.code, nameAr: r.nameAr, nameFr: r.nameFr, nameEn: r.nameEn, description: r.description ?? '', isActive: r.isActive };
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
      await this.api.saveJobRole(this.editing()?.id ?? null, {
        ...this.form,
        nameEn: this.form.nameEn?.trim() || null,
        description: this.form.description?.trim() || null,
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
