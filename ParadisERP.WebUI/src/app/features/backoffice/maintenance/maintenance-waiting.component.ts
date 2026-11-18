import { Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { localizedName } from '../../../core/multilingual';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { TableStateComponent } from '../../../ui/table-state.component';
import { formatQty } from '../backoffice-format';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { MaintenanceBucket } from './maintenance-api.service';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { maintenanceError } from './maintenance-words';

/**
 * Maintenance › Waiting for repair (Phase M1, docs/maintenance/plan.md §2.2 and §7): per branch and product, the units in
 * maintenance, what jobs hold of them and what is free; and every return-note line whose repairable units no job covers
 * yet — contract, note, units and the recharge typed at return. "Open a repair job" on each row opens New job prefilled.
 * The reader's own branch, every branch for the Admin. Each list is paged by the server (owner, 2026-11-17: "paginate in
 * backend, not frontend"; ten rows to begin with) and filtered there over all its rows: the branch for both, the day
 * returned and the recharge for the lines, free units for the products.
 */
@Component({
  selector: 'app-maintenance-waiting',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ...MENU, DateInputComponent, ListFooterComponent, TableStateComponent],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        @if (branches().length > 1) {
          <div class="ui-filter">
            <label for="mw-branch">{{ i18n.t('filters.branch') }}</label>
            <select id="mw-branch" [ngModel]="branch()" (ngModelChange)="branch.set($event)">
              <option [ngValue]="null">{{ i18n.t('filters.all') }}</option>
              @for (b of branches(); track b.id) {
                <option [ngValue]="b.id">{{ b.name }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-toolbar__end">
          @if (filtered()) {
            <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
          }
          <button type="button" class="btn-ghost" [disabled]="busy()" (click)="reload()">{{ i18n.t('subscription.refresh') }}</button>
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error() && loaded()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <section class="ui-surface" aria-labelledby="mw-lines" [attr.aria-busy]="!loaded()">
        <div class="ui-surface__head">
          <h2 id="mw-lines">{{ i18n.t('maint.waiting.lines') }}</h2>
        </div>
        @if (!loaded() && !error()) {
          <app-table-state kind="loading" />
        } @else if (!loaded()) {
          <app-table-state kind="error" [message]="error()" (retry)="reload()" />
        } @else if (!linePager.total() && !linesFiltered()) {
          <app-table-state kind="empty" [message]="i18n.t('maint.waiting.noLines')" />
        } @else {
          <div class="ui-surface__body">
            <div class="ui-toolbar" role="search">
              <div class="ui-filter">
                <label for="mw-from">{{ i18n.t('filters.from') }}</label>
                <app-date-input id="mw-from" [ngModel]="from()" (ngModelChange)="from.set($event || '')" />
              </div>
              <div class="ui-filter">
                <label for="mw-to">{{ i18n.t('filters.to') }}</label>
                <app-date-input id="mw-to" [ngModel]="to()" (ngModelChange)="to.set($event || '')" />
              </div>
              <div class="ui-filter">
                <label for="mw-recharge">{{ i18n.t('maint.job.recharge') }}</label>
                <select id="mw-recharge" [ngModel]="recharge()" (ngModelChange)="recharge.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="yes">{{ i18n.t('filters.yes') }}</option>
                  <option ngValue="no">{{ i18n.t('filters.no') }}</option>
                </select>
              </div>
            </div>
          </div>
          @if (!linePager.total()) {
            <app-table-state kind="noResults" (clear)="clearFilters()" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('maint.job.returnNote') }}</th>
                    <th>{{ i18n.t('maint.job.product') }}</th>
                    <th>{{ i18n.t('maint.job.branch') }}</th>
                    <th>{{ i18n.t('maint.waiting.returnedOn') }}</th>
                    <th class="num">{{ i18n.t('maint.waiting.damaged') }}</th>
                    <th class="num">{{ i18n.t('maint.waiting.covered') }}</th>
                    <th class="num">{{ i18n.t('maint.waiting.open') }}</th>
                    <th class="num">{{ i18n.t('maint.job.recharge') }}</th>
                    <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (l of linePager.rows(); track l.returnNoteLineId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('maint.job.returnNote')">
                        @if (canManage()) {
                          <button type="button" class="ui-link" (click)="openLine.emit(l.returnNoteLineId)"><span class="mono" dir="ltr">{{ l.returnNoteNumber }}</span></button>
                        } @else {
                          <span class="mono" dir="ltr">{{ l.returnNoteNumber }}</span>
                        }
                        <span class="ui-sub"><span dir="ltr">{{ l.contractNumber }}</span> · {{ l.customerName ?? '—' }}</span>
                      </td>
                      <td [attr.data-label]="i18n.t('maint.job.product')">{{ name(l) }} <span class="ui-sub mono" dir="ltr">{{ l.sku }}</span></td>
                      <td [attr.data-label]="i18n.t('maint.job.branch')">{{ l.branchName }}</td>
                      <td class="mono" [attr.data-label]="i18n.t('maint.waiting.returnedOn')">{{ i18n.date(l.returnedAtUtc) }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.waiting.damaged')"><span dir="ltr">{{ qty(l.damagedQty) }}</span></td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.waiting.covered')"><span dir="ltr">{{ qty(l.coveredQty) }}</span></td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.waiting.open')"><strong dir="ltr">{{ qty(l.openQty) }}</strong></td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.job.recharge')">{{ i18n.money(l.recharge) }}</td>
                      <td class="ui-cell-menu">
                        @if (canManage()) {
                          <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + l.returnNoteNumber">
                            <span menuTrigger aria-hidden="true">⋯</span>
                            <button type="button" appMenuItem (click)="openLine.emit(l.returnNoteLineId)">{{ i18n.t('maint.waiting.openJob') }}</button>
                          </app-menu>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
      </section>
      <app-list-footer [pager]="linePager" [loading]="!loaded()" />

      <section class="ui-surface" aria-labelledby="mw-buckets">
        <div class="ui-surface__head">
          <h2 id="mw-buckets">{{ i18n.t('maint.waiting.buckets') }}</h2>
        </div>
        @if (bucketPager.loaded() && !bucketPager.total() && !bucketsFiltered()) {
          <app-table-state kind="empty" [message]="i18n.t('maint.waiting.noBuckets')" />
        } @else if (bucketPager.loaded()) {
          <div class="ui-surface__body">
            <div class="ui-toolbar" role="search">
              <div class="ui-filter">
                <label for="mw-free">{{ i18n.t('maint.filter.free') }}</label>
                <select id="mw-free" [ngModel]="free()" (ngModelChange)="free.set($event)">
                  <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                  <option ngValue="some">{{ i18n.t('maint.filter.someFree') }}</option>
                  <option ngValue="none">{{ i18n.t('maint.filter.allHeld') }}</option>
                </select>
              </div>
            </div>
          </div>
          @if (!bucketPager.total()) {
            <app-table-state kind="noResults" (clear)="clearFilters()" />
          } @else {
            <div class="ui-scroll">
              <table class="ui-table ui-table--cards">
                <thead>
                  <tr>
                    <th>{{ i18n.t('maint.job.product') }}</th>
                    <th>{{ i18n.t('maint.job.branch') }}</th>
                    <th class="num">{{ i18n.t('maint.waiting.inMaintenance') }}</th>
                    <th class="num">{{ i18n.t('maint.waiting.held') }}</th>
                    <th class="num">{{ i18n.t('maint.waiting.free') }}</th>
                    <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (b of bucketPager.rows(); track b.branchId + b.productId) {
                    <tr>
                      <td [attr.data-label]="i18n.t('maint.job.product')">{{ name(b) }} <span class="ui-sub mono" dir="ltr">{{ b.sku }}</span></td>
                      <td [attr.data-label]="i18n.t('maint.job.branch')">{{ b.branchName }}</td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.waiting.inMaintenance')"><span dir="ltr">{{ qty(b.inMaintenance) }}</span></td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.waiting.held')"><span dir="ltr">{{ qty(b.heldByJobs) }}</span></td>
                      <td class="num mono" [attr.data-label]="i18n.t('maint.waiting.free')"><strong dir="ltr">{{ qty(b.free) }}</strong></td>
                      <td class="ui-cell-menu">
                        @if (canManage() && b.free > 0) {
                          <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + b.sku">
                            <span menuTrigger aria-hidden="true">⋯</span>
                            <button type="button" appMenuItem (click)="openBucket.emit(b)">{{ i18n.t('maint.waiting.openJob') }}</button>
                          </app-menu>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        }
      </section>
      <app-list-footer [pager]="bucketPager" [loading]="!bucketPager.loaded()" />
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MaintenanceWaitingComponent implements OnInit {
  private readonly pages = inject(MaintenancePagesApi);
  private readonly auth = inject(AuthService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  /** A return-note line to repair, or a product in maintenance at a branch: New job, prefilled. */
  readonly openLine = output<string>();
  readonly openBucket = output<MaintenanceBucket>();

  readonly busy = signal(false);
  readonly canManage = computed(() => mayDo(this.auth.currentUser(), 'maintenance.jobs.manage'));

  // The lists' filters (owner, 2026-11-17). Each is sent to the server, which filters every row of the reader's scope.
  /** Both lists: the branch, offered when they hold more than one (the Admin). */
  readonly branch = signal<string | null>(null);
  /** The lines: the day returned, on the company's clock (`yyyy-MM-dd`), and whether a recharge was typed at return. */
  readonly from = signal('');
  readonly to = signal('');
  readonly recharge = signal<'all' | 'yes' | 'no'>('all');
  /** The products in maintenance: some units still free to repair, or every unit held by jobs. */
  readonly free = signal<'all' | 'some' | 'none'>('all');

  readonly linesFiltered = computed(() => !!this.branch() || !!this.from() || !!this.to() || this.recharge() !== 'all');
  readonly bucketsFiltered = computed(() => !!this.branch() || this.free() !== 'all');
  readonly filtered = computed(() => this.linesFiltered() || this.bucketsFiltered());

  /** Ten rows to begin with, 20, 50 or 100 at the user's choice; each page is a request, a new filter starts at page one. */
  readonly linePager = createServerPager(
    (page) => this.pages.waitingLines({ skip: page.skip, take: page.take, ...page.filters }),
    {
      key: 'maintenance.waiting.lines',
      filters: () => ({ branch: this.branch(), from: this.from(), to: this.to(), recharge: this.recharge() }),
    },
  );
  readonly bucketPager = createServerPager(
    (page) => this.pages.waitingBuckets({ skip: page.skip, take: page.take, ...page.filters }),
    { key: 'maintenance.waiting.buckets', filters: () => ({ branch: this.branch(), free: this.free() }) },
  );

  /** The first answer of the lines arrived (the page's loading and error states follow it). */
  readonly loaded = computed(() => this.linePager.loaded());
  /** The branches either list holds, before any filter (the server's): several only for the Admin. */
  readonly branches = computed(() => this.linePager.summary()?.branches ?? this.bucketPager.summary()?.branches ?? []);
  /** A list could not be read, in words. */
  readonly error = computed(() => {
    const e = this.linePager.error() ?? this.bucketPager.error();
    return e ? maintenanceError(e, (k) => this.i18n.t(k)) : null;
  });

  clearFilters(): void {
    this.branch.set(null);
    this.from.set('');
    this.to.set('');
    this.recharge.set('all');
    this.free.set('all');
  }

  ngOnInit(): void {
    // Both lists load themselves through their pagers.
    this.chrome?.description.set(this.i18n.t('maint.waiting.lead'));
  }

  /** Refresh: both pages on screen, asked again. */
  async reload(): Promise<void> {
    this.busy.set(true);
    try {
      await Promise.all([this.linePager.reload(), this.bucketPager.reload()]);
    } finally {
      this.busy.set(false);
    }
  }

  name(x: { nameAr: string; nameFr: string; nameEn: string; sku: string }): string {
    return localizedName(x, this.i18n.lang()) || x.sku;
  }

  qty(value: number): string {
    return formatQty(value, this.i18n.lang());
  }
}

