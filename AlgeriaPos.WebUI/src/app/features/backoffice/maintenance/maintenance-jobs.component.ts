import { Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { localizedName } from '../../../core/multilingual';
import { DateInputComponent } from '../../../ui/date-input.component';
import { DialogComponent } from '../../../ui/dialog.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatQty } from '../backoffice-format';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { JOB_KINDS, JOB_STATUSES, MaintenanceApi, MaintenanceJobStatus, MaintenanceJobSummary } from './maintenance-api.service';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { MaintenanceJobInvoice } from './maintenance-customer-api.service';
import { InvoiceableJob, MaintenanceInvoiceDialogComponent } from './maintenance-invoice-dialog.component';
import { maintenanceError } from './maintenance-words';

/**
 * Maintenance › Jobs (Phase M1, docs/maintenance/plan.md §7): every job of the reader's branch — every branch for the
 * Admin — newest first, with what it repairs, how many units, where it stands in words and what became of them. The job
 * number opens it; Complete opens it at its completion; Cancel asks why. Filters: words, status, kind, the day it was
 * opened (on the company's clock), and the branch when the list holds several. Paged by the server (owner, 2026-11-17:
 * "paginate in backend, not frontend"): ten rows to begin with, and every page, size or filter change asks
 * `GET /api/maintenance/jobs/page`, which filters every job of the reader's scope.
 */
@Component({
  selector: 'app-maintenance-jobs',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ...MENU, DateInputComponent, DialogComponent, ListFooterComponent, MaintenanceInvoiceDialogComponent, StatusComponent, TableStateComponent],
  template: `
    @if (canManage()) {
      <ng-template appPageActions>
        <button type="button" class="btn-primary" (click)="create.emit()"><span aria-hidden="true">+</span> {{ i18n.t('maint.jobs.new') }}</button>
      </ng-template>
    }
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        <div class="ui-filter ui-filter--search">
          <label for="mj-q">{{ i18n.t('products.search') }}</label>
          <input id="mj-q" type="search" [ngModel]="query()" (ngModelChange)="query.set($event)" />
        </div>
        <div class="ui-filter">
          <label for="mj-status">{{ i18n.t('common.state') }}</label>
          <select id="mj-status" [ngModel]="status()" (ngModelChange)="status.set($event)">
            <option [ngValue]="null">{{ i18n.t('maint.jobs.allStatuses') }}</option>
            @for (s of statuses; track s) {
              <option [ngValue]="s">{{ i18n.t('maint.status.' + s) }}</option>
            }
          </select>
        </div>
        <div class="ui-filter">
          <label for="mj-kind">{{ i18n.t('maint.job.kind') }}</label>
          <select id="mj-kind" [ngModel]="kind()" (ngModelChange)="kind.set($event)">
            <option [ngValue]="null">{{ i18n.t('maint.jobs.allKinds') }}</option>
            @for (k of kinds; track k) {
              <option [ngValue]="k">{{ i18n.t('maint.kind.' + k) }}</option>
            }
          </select>
        </div>
        @if (branches().length > 1) {
          <div class="ui-filter">
            <label for="mj-branch">{{ i18n.t('filters.branch') }}</label>
            <select id="mj-branch" [ngModel]="branch()" (ngModelChange)="branch.set($event)">
              <option [ngValue]="null">{{ i18n.t('filters.all') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ b.name }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <label for="mj-from">{{ i18n.t('maint.jobs.from') }}</label>
          <app-date-input id="mj-from" [ngModel]="from()" (ngModelChange)="from.set($event || '')" />
        </div>
        <div class="ui-filter">
          <label for="mj-to">{{ i18n.t('maint.jobs.to') }}</label>
          <app-date-input id="mj-to" [ngModel]="to()" (ngModelChange)="to.set($event || '')" />
        </div>
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clear()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="busy() || pager.loading()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (notice()) {
        <p class="notice ok" role="status">{{ notice() }}</p>
      }
      @if (listError() && pager.loaded()) {
        <p class="notice danger" role="alert">{{ listError() }}</p>
      }
      <section class="ui-surface" [attr.aria-busy]="!pager.loaded()">
        @if (!pager.loaded() && !listError()) {
          <app-table-state kind="loading" />
        } @else if (!pager.loaded()) {
          <app-table-state kind="error" [message]="listError()" (retry)="reload()" />
        } @else if (!pager.total() && !filtered()) {
          <app-table-state kind="empty" [message]="i18n.t('maint.jobs.none')">
            @if (canManage()) {
              <button type="button" class="btn-primary" (click)="create.emit()">{{ i18n.t('maint.jobs.new') }}</button>
            }
          </app-table-state>
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clear()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <thead>
                <tr>
                  <th>{{ i18n.t('maint.job.number') }}</th>
                  <th>{{ i18n.t('maint.job.product') }}</th>
                  <th>{{ i18n.t('maint.job.kind') }}</th>
                  <th>{{ i18n.t('common.state') }}</th>
                  <th class="num">{{ i18n.t('maint.job.qty') }}</th>
                  <th>{{ i18n.t('maint.job.branch') }}</th>
                  <th>{{ i18n.t('maint.job.openedOn') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (j of pager.rows(); track j.id) {
                  <tr [class.is-muted]="j.status === 'Cancelled'">
                    <td class="mono" [attr.data-label]="i18n.t('maint.job.number')">
                      <button type="button" class="ui-link" (click)="open.emit(j.id)"><span dir="ltr">{{ j.number }}</span></button>
                    </td>
                    <td [attr.data-label]="i18n.t('maint.job.product')">
                      @if (j.kind === 'Customer') {
                        {{ j.equipment ?? '—' }} <span class="ui-sub">{{ j.jobCustomerName ?? '' }}</span>
                        @if (j.reworkOfJobId) {
                          <!-- Phase M7c: a free rework under guarantee, said in words. -->
                          <span class="ui-sub">{{ i18n.t('maint.rework.badge') }}</span>
                        }
                      } @else {
                        {{ productName(j) }} <span class="ui-sub mono" dir="ltr">{{ j.productSku }}</span>
                      }
                      @if (j.itemReference) {
                        <span class="ui-sub">{{ j.itemReference }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('maint.job.kind')">
                      {{ i18n.t('maint.kind.' + j.kind) }}
                      @if (j.returnNoteNumber) {
                        <span class="ui-sub mono" dir="ltr">{{ j.returnNoteNumber }}</span>
                      }
                    </td>
                    <td [attr.data-label]="i18n.t('common.state')">
                      <app-status [tone]="tone(j.status)">{{ i18n.t('maint.status.' + j.status) }}</app-status>
                      @if (j.status === 'Completed') {
                        <span class="ui-sub">{{ i18n.t('maint.jobs.outcome', { repaired: qty(j.repairedQty), scrapped: qty(j.scrappedQty) }) }}</span>
                      }
                    </td>
                    <td class="num mono" [attr.data-label]="i18n.t('maint.job.qty')"><span dir="ltr">{{ qty(j.qty) }}</span></td>
                    <td [attr.data-label]="i18n.t('maint.job.branch')">{{ j.branchName }}</td>
                    <td class="mono" [attr.data-label]="i18n.t('maint.job.openedOn')">{{ i18n.date(j.createdAtUtc) }}</td>
                    <td class="ui-cell-menu">
                      <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + j.number">
                        <span menuTrigger aria-hidden="true">⋯</span>
                        <button type="button" appMenuItem (click)="open.emit(j.id)">{{ i18n.t('maint.jobs.openJob') }}</button>
                        @if (canClose() && j.status === 'Open') {
                          <button type="button" appMenuItem (click)="open.emit(j.id)">{{ i18n.t('maint.job.complete') }}</button>
                        }
                        @if (canInvoice() && j.kind === 'Customer' && j.status === 'Completed' && !j.invoiceId && !j.reworkOfJobId) {
                          <button type="button" appMenuItem (click)="invoicing.set({ id: j.id, number: j.number })">{{ i18n.t('maint.invoice.raise') }}</button>
                        }
                        @if (canClose() && (j.status === 'Draft' || j.status === 'Open')) {
                          <hr appMenuSeparator />
                          <button type="button" appMenuItem class="is-danger" (click)="askCancel(j)">{{ i18n.t('maint.job.cancel') }}</button>
                        }
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

    <!-- Phase M5a: a completed customer job's one invoice, from its ⋯ menu. -->
    <app-maintenance-invoice-dialog [job]="invoicing()" (raised)="onInvoiced($event)" (dismiss)="invoicing.set(null)" />

    <app-dialog [open]="!!cancelling()" size="sm" [heading]="i18n.t('maint.job.cancelHeading', { n: cancelling()?.number ?? '' })" (dismiss)="cancelling.set(null)">
      @if (cancelling()) {
        <div class="bo-form">
          @if (cancelError()) {
            <p class="notice danger" role="alert">{{ cancelError() }}</p>
          }
          <p class="bo-hint">{{ i18n.t('maint.job.cancelNote') }}</p>
          <div class="bo-field">
            <label for="mj-cancel-reason">{{ i18n.t('maint.job.cancelReason') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
            <input id="mj-cancel-reason" maxlength="400" [(ngModel)]="cancelReason" />
          </div>
        </div>
      }
      <div dialogFoot class="ui-actionbar">
        <button type="button" class="btn-ghost" (click)="cancelling.set(null)">{{ i18n.t('common.close') }}</button>
        <button type="button" class="btn-danger" [disabled]="busy() || !cancelReason.trim()" (click)="cancel()">{{ i18n.t('maint.job.cancel') }}</button>
      </div>
    </app-dialog>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MaintenanceJobsComponent implements OnInit {
  private readonly api = inject(MaintenanceApi);
  private readonly pages = inject(MaintenancePagesApi);
  private readonly auth = inject(AuthService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly open = output<string>();
  readonly create = output<void>();

  readonly statuses = JOB_STATUSES;
  readonly kinds = JOB_KINDS;
  readonly busy = signal(false);
  readonly query = signal('');
  readonly status = signal<MaintenanceJobStatus | null>(null);
  readonly kind = signal<string | null>(null);
  readonly from = signal('');
  readonly to = signal('');
  /** The job's branch (owner, 2026-11-17): offered when the list holds more than one, i.e. to the Admin. */
  readonly branch = signal<string | null>(null);
  readonly cancelling = signal<MaintenanceJobSummary | null>(null);
  readonly cancelError = signal<string | null>(null);
  cancelReason = '';

  readonly canManage = computed(() => mayDo(this.auth.currentUser(), 'maintenance.jobs.manage'));
  readonly canClose = computed(() => mayDo(this.auth.currentUser(), 'maintenance.jobs.close'));
  /** Raising a customer job's invoice: finance.invoices.create (the server also asks maintenance.view, which this page needs). */
  readonly canInvoice = computed(() => mayDo(this.auth.currentUser(), 'finance.invoices.create'));
  readonly invoicing = signal<InvoiceableJob | null>(null);
  readonly notice = signal<string | null>(null);

  readonly filtered = computed(
    () => !!this.query().trim() || !!this.status() || !!this.kind() || !!this.branch() || !!this.from() || !!this.to(),
  );
  /**
   * The jobs a page at a time from the server (owner, 2026-11-17): the words, status, kind, branch and days opened run there
   * over every job of the reader's scope; a change goes back to page one. Ten rows to begin with, 20, 50 or 100.
   */
  readonly pager = createServerPager(
    (page) => this.pages.jobs({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'maintenance.jobs',
      filters: () => ({
        q: this.query().trim(),
        status: this.status(),
        kind: this.kind(),
        branch: this.branch(),
        from: this.from(),
        to: this.to(),
      }),
    },
  );
  /** The branches the reader's jobs belong to, by name (the server's, over every job): one for a branch reader, every branch for the Admin. */
  readonly branches = computed(() => this.pager.summary()?.branches ?? []);
  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? maintenanceError(e, (k) => this.i18n.t(k)) : null;
  });

  ngOnInit(): void {
    // The list loads itself through its pager.
    this.chrome?.description.set(this.i18n.t('maint.jobs.lead'));
  }

  /** Asks again for the page on screen: Refresh, and after a cancel or an invoice. */
  async reload(): Promise<void> {
    await this.pager.reload();
  }

  clear(): void {
    this.query.set('');
    this.status.set(null);
    this.kind.set(null);
    this.from.set('');
    this.to.set('');
    this.branch.set(null);
  }

  productName(j: MaintenanceJobSummary): string {
    return localizedName({ nameAr: j.productNameAr, nameFr: j.productNameFr, nameEn: j.productNameEn }, this.i18n.lang()) || j.productSku;
  }

  qty(value: number): string {
    return formatQty(value, this.i18n.lang());
  }

  tone(status: MaintenanceJobStatus) {
    return toneOf('maintenanceJob', status);
  }

  async onInvoiced(result: MaintenanceJobInvoice): Promise<void> {
    this.invoicing.set(null);
    this.notice.set(this.i18n.t('maint.invoice.raised', { n: result.invoiceNumber }) + (result.warning ? ` ${result.warning}` : ''));
    await this.reload();
  }

  askCancel(j: MaintenanceJobSummary): void {
    this.cancelReason = '';
    this.cancelError.set(null);
    this.cancelling.set(j);
  }

  async cancel(): Promise<void> {
    const job = this.cancelling();
    if (!job) return;
    this.busy.set(true);
    this.cancelError.set(null);
    try {
      await this.api.cancel(job.id, this.cancelReason.trim());
      this.cancelling.set(null);
      await this.reload();
    } catch (e) {
      this.cancelError.set(maintenanceError(e, (k) => this.i18n.t(k)));
    } finally {
      this.busy.set(false);
    }
  }
}

