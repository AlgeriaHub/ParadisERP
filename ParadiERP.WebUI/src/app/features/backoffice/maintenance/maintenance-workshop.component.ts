import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { problemCode } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { mayDo } from '../../../core/landing';
import { ConfirmService } from '../../../ui/confirm.service';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { MENU } from '../../../ui/menu.component';
import { createServerPager } from '../../../ui/server-pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { PAGE_CHROME, PageChrome } from '../shell/page-chrome';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { maintenanceError } from './maintenance-words';
import {
  MaintenanceWorkshop,
  MaintenanceWorkshopApi,
  MaintenanceWorkshopMember,
  WorkshopMemberState,
  emptyWorkshopBranches,
  workshopMemberState,
} from './maintenance-workshop-api.service';

/** The refusals of the workshop list said in the reader's language; any other goes to `maintenanceError`. */
const REFUSALS: Readonly<Record<string, string>> = {
  already_on_workshop_list: 'maint.workshop.error.already',
  employee_not_of_branch: 'maint.workshop.error.notOfBranch',
  employee_not_employed: 'maint.workshop.error.left',
  not_found: 'maint.workshop.error.notFound',
};

/**
 * Maintenance › Workshop (MS-04, docs/maintenance/plan.md "MS-04 as built"): which employees of a branch are workshop
 * staff. While Maintenance › Settings says "only the workshop list", a job's hours are booked only for the people on its
 * branch's list; under the default ("any employee of the job's branch") the list is kept and not read. The reader's own
 * branch; every branch for the Admin. Kept by `maintenance.settings`: a person is added to their branch's list and taken
 * off it after a confirmation. Someone who moved keeps the old row, which no longer counts: the page says so in words.
 * The list is paged by the server (owner, 2026-11-17: "paginate in backend, not frontend"; ten rows to begin with) and
 * filtered there by its state and, for the Admin, its branch.
 */
@Component({
  selector: 'app-maintenance-workshop',
  standalone: true,
  imports: [FormsModule, ...PAGE_CHROME, ...MENU, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    @if (memberCount()) {
      <ng-template appPageToolbar>
        <div class="ui-toolbar" role="search">
          @if (memberBranches().length > 1) {
            <div class="ui-filter">
              <label for="mws-f-branch">{{ i18n.t('filters.branch') }}</label>
              <select id="mws-f-branch" [ngModel]="branchFilter()" (ngModelChange)="branchFilter.set($event)">
                <option [ngValue]="null">{{ i18n.t('filters.all') }}</option>
                @for (b of memberBranches(); track b.id) {
                  <option [ngValue]="b.id">{{ b.name }}</option>
                }
              </select>
            </div>
          }
          <div class="ui-filter">
            <label for="mws-f-state">{{ i18n.t('maint.workshop.state') }}</label>
            <select id="mws-f-state" [ngModel]="stateFilter()" (ngModelChange)="stateFilter.set($event)">
              <option ngValue="all">{{ i18n.t('filters.all') }}</option>
              <option ngValue="bookable">{{ i18n.t('maint.workshop.state.bookable') }}</option>
              <option ngValue="moved">{{ i18n.t('maint.filter.moved') }}</option>
              <option ngValue="left">{{ i18n.t('maint.workshop.state.left') }}</option>
            </select>
          </div>
          @if (filtered()) {
            <div class="ui-toolbar__end">
              <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
            </div>
          }
        </div>
      </ng-template>
    }

    <div class="bo-stack">
      @if (data(); as d) {
        <p class="notice" [class.info]="d.bookableEmployees === 'WorkshopListOnly'" role="status">
          {{ i18n.t(d.bookableEmployees === 'WorkshopListOnly' ? 'maint.workshop.inForce' : 'maint.workshop.notInForce') }}
        </p>
        @if (d.bookableEmployees === 'WorkshopListOnly') {
          @for (b of empty(); track b.branchId) {
            <p class="notice warn" role="status">{{ i18n.t('maint.workshop.emptyBranch', { branch: b.branchName }) }}</p>
          }
        }
      }
      @if (error() || (listError() && pager.loaded())) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }
      @if (done()) {
        <p class="notice ok" role="status">{{ done() }}</p>
      }

      @if (canEdit() && data()) {
        <section class="bo-panel" aria-labelledby="mws-add">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title" id="mws-add">{{ i18n.t('maint.workshop.add') }}</h2>
          </div>
          <div class="bo-panel__body bo-form">
            <div class="bo-field">
              <label for="mws-employee">{{ i18n.t('maint.workshop.employee') }}<span class="bo-req">{{ i18n.t('common.required') }}</span></label>
              <select id="mws-employee" [(ngModel)]="employeeId" [disabled]="busy() || !available().length">
                <option [ngValue]="null">{{ i18n.t('maint.workshop.pickEmployee') }}</option>
                @for (e of available(); track e.employeeId + e.branchId) {
                  <option [ngValue]="e.employeeId">{{ e.fullName }} · {{ e.number }}{{ manyBranches() ? ' — ' + e.branchName : '' }}</option>
                }
              </select>
              <p class="bo-hint">{{ i18n.t(available().length ? 'maint.workshop.employeeHint' : 'maint.workshop.noneToAdd') }}</p>
            </div>
            <div class="ui-actionbar">
              <button type="button" class="btn-primary" [disabled]="busy() || !employeeId" (click)="add()">{{ i18n.t('maint.workshop.addButton') }}</button>
            </div>
          </div>
        </section>
      }

      <section class="ui-surface">
        @if (!pager.loaded() && !listError()) {
          <app-table-state kind="loading" />
        } @else if (!pager.loaded()) {
          <app-table-state kind="error" [message]="listError()" (retry)="load()" />
        } @else if (!memberCount()) {
          <app-table-state kind="empty" [message]="i18n.t('maint.workshop.none')" />
        } @else if (!pager.total()) {
          <app-table-state kind="noResults" (clear)="clearFilters()" />
        } @else {
          <div class="ui-scroll">
            <table class="ui-table ui-table--cards">
              <caption class="sr-only">{{ i18n.t('maint.page.workshop') }}</caption>
              <thead>
                <tr>
                  <th>{{ i18n.t('maint.workshop.employee') }}</th>
                  <th>{{ i18n.t('maint.job.branch') }}</th>
                  <th>{{ i18n.t('maint.workshop.state') }}</th>
                  <th>{{ i18n.t('maint.workshop.added') }}</th>
                  <th class="ui-cell-menu"><span class="sr-only">{{ i18n.t('common.actions') }}</span></th>
                </tr>
              </thead>
              <tbody>
                @for (m of pager.rows(); track m.id) {
                  <tr>
                    <td [attr.data-label]="i18n.t('maint.workshop.employee')">{{ m.fullName }} <span class="ui-sub mono" dir="ltr">{{ m.number }}</span></td>
                    <td [attr.data-label]="i18n.t('maint.job.branch')">{{ m.branchName }}</td>
                    <td [attr.data-label]="i18n.t('maint.workshop.state')">
                      <app-status [tone]="tone(m)">{{ stateText(m) }}</app-status>
                    </td>
                    <td [attr.data-label]="i18n.t('maint.workshop.added')">{{ m.addedBy ?? '—' }} <span class="ui-sub mono">{{ i18n.dateTime(m.addedAtUtc) }}</span></td>
                    <td class="ui-cell-menu">
                      @if (canEdit()) {
                        <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more') + ' — ' + m.fullName">
                          <span menuTrigger aria-hidden="true">⋯</span>
                          <button type="button" appMenuItem class="is-danger" (click)="remove(m)">{{ i18n.t('maint.workshop.remove') }}</button>
                        </app-menu>
                      }
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
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class MaintenanceWorkshopComponent implements OnInit {
  private readonly api = inject(MaintenanceWorkshopApi);
  private readonly pages = inject(MaintenancePagesApi);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmService);
  private readonly chrome = inject(PageChrome, { optional: true });
  readonly i18n = inject(I18nService);

  readonly busy = signal(false);
  /** An add or a removal refused, in words. */
  readonly error = signal<string | null>(null);
  readonly done = signal<string | null>(null);
  employeeId: string | null = null;

  readonly canEdit = computed(() => mayDo(this.auth.currentUser(), 'maintenance.settings'));

  // The list's filters (owner, 2026-11-17). Each is sent to the server, which filters every row of the reader's lists.
  readonly branchFilter = signal<string | null>(null);
  readonly stateFilter = signal<'all' | WorkshopMemberState>('all');
  readonly filtered = computed(() => !!this.branchFilter() || this.stateFilter() !== 'all');

  /** Ten rows to begin with, 20, 50 or 100 at the user's choice; each page is a request, a new filter starts at page one. */
  readonly pager = createServerPager(
    (page) => this.pages.workshop({ skip: page.skip, take: page.take, ...page.filters }),
    { key: 'maintenance.workshop', filters: () => ({ branch: this.branchFilter(), state: this.stateFilter() }) },
  );

  /** Everything the list route answers but the rows: MS-04, who may be added, each branch's head count. */
  readonly data = computed(() => this.pager.summary());
  /** How many rows the lists hold before any filter. */
  readonly memberCount = computed(() => this.data()?.members ?? 0);
  readonly available = computed(() => this.data()?.available ?? []);
  readonly empty = computed(() => {
    const d = this.data();
    return emptyWorkshopBranches(d ? { bookableEmployees: d.bookableEmployees, members: [], available: d.available, branches: d.branches } : null);
  });
  /** The Admin reads every branch: each person offered says which. */
  readonly manyBranches = computed(() => (this.data()?.branches.length ?? 0) > 1);
  /** The branches whose lists hold someone: one for a branch reader, several for the Admin. */
  readonly memberBranches = computed(() => this.data()?.memberBranches ?? []);
  /** The list could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? this.refusal(e) : null;
  });

  clearFilters(): void {
    this.branchFilter.set(null);
    this.stateFilter.set('all');
  }

  ngOnInit(): void {
    // The list loads itself through its pager.
    this.chrome?.description.set(this.i18n.t('maint.workshop.lead'));
  }

  /** Asks again for the page on screen. */
  async load(): Promise<void> {
    await this.pager.reload();
  }

  tone(m: MaintenanceWorkshopMember) {
    return toneOf('workshopMember', workshopMemberState(m));
  }

  stateText(m: MaintenanceWorkshopMember): string {
    const state = workshopMemberState(m);
    return state === 'moved'
      ? this.i18n.t('maint.workshop.state.moved', { branch: m.employeeBranchName })
      : this.i18n.t('maint.workshop.state.' + state);
  }

  async add(): Promise<void> {
    const person = this.available().find((e) => e.employeeId === this.employeeId);
    if (!person) return;
    const ok = await this.act(() => this.api.add(person.employeeId, person.branchId));
    if (ok) {
      this.employeeId = null;
      this.done.set(this.i18n.t('maint.workshop.added.notice', { name: person.fullName }));
    }
  }

  async remove(m: MaintenanceWorkshopMember): Promise<void> {
    const yes = await this.confirm.ask({
      title: this.i18n.t('maint.workshop.remove.title', { name: m.fullName }),
      message: this.i18n.t('maint.workshop.remove.message', { branch: m.branchName }),
      confirmLabel: this.i18n.t('maint.workshop.remove'),
      cancelLabel: this.i18n.t('confirm.keep'),
      danger: true,
    });
    if (!yes) return;
    if (await this.act(() => this.api.remove(m.id))) this.done.set(this.i18n.t('maint.workshop.removed.notice', { name: m.fullName }));
  }

  /** Adds or removes, then asks again for the page on screen and its summary (who may still be added). */
  private async act(call: () => Promise<MaintenanceWorkshop>): Promise<boolean> {
    this.busy.set(true);
    this.error.set(null);
    this.done.set(null);
    try {
      await call();
      await this.pager.reload();
      return true;
    } catch (e) {
      this.error.set(this.refusal(e));
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  private refusal(e: unknown): string {
    const code = problemCode(e);
    return code && REFUSALS[code] ? this.i18n.t(REFUSALS[code]) : maintenanceError(e, (k) => this.i18n.t(k));
  }
}
