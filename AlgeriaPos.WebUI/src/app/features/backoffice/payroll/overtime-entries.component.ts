import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MENU } from '../../../ui/menu.component';
import { AdminBranch, AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { deleteRequest, saveRequest } from '../../../ui/confirm-requests';
import { DateInputComponent } from '../../../ui/date-input.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createServerPager } from '../../../ui/server-pager';
import { localizedName } from '../backoffice-format';
import { Employee, HrApi, OvertimeEntry, OvertimeEntryChange, hrProblem } from './hr-api.service';
import { MonthInputComponent } from './month-input.component';
import { PayrollPagesApi } from './payroll-pages.api';
import { moneyErrorMessage } from './wage-money-api.service';

@Component({
  selector: 'app-hr-overtime',
  standalone: true,
  imports: [CommonModule, FormsModule, DateInputComponent, MonthInputComponent, ListFooterComponent, ...MENU],
  template: `
    <div class="bo-stack">
      @if (error() || listError()) {
        <p class="notice danger" role="alert">{{ error() || listError() }}</p>
      }

      <section class="bo-panel">
        <div class="bo-panel__head">
          <h2 class="bo-panel__title">{{ i18n.t('hr.overtime.title') }}</h2>
          <div class="bo-actions bo-actions--start">
            @if (lock.locked()) {
              <p class="bo-scope-note" id="ot-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
            } @else {
              <div class="bo-field">
                <label for="ot-branch">{{ i18n.t('hr.employees.branch') }}</label>
                <select id="ot-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
                  @for (branch of branches(); track branch.id) {
                    <option [value]="branch.id">{{ branchName(branch) }}</option>
                  }
                </select>
              </div>
            }
            <div class="bo-field">
              <label for="ot-month">{{ i18n.t('hr.overtime.month') }}</label>
              <app-month-input
                id="ot-month"
                [ngModel]="monthString()"
                (ngModelChange)="onMonthSelected($event)"
                [disabled]="loading()"
              />
            </div>
            <div class="bo-field">
              <label for="ot-filter-emp">{{ i18n.t('hr.overtime.employee') }}</label>
              <select
                id="ot-filter-emp"
                [ngModel]="employeeFilter()"
                (ngModelChange)="employeeFilter.set($event)"
                [disabled]="loading() || !branchId()"
              >
                <option [ngValue]="null">{{ i18n.t('hr.overtime.allEmployees') }}</option>
                @for (emp of employees(); track emp.id) {
                  <option [ngValue]="emp.id">{{ emp.fullName }} ({{ emp.number }})</option>
                }
              </select>
            </div>
          </div>
        </div>
      </section>

      @if (loading()) {
        <p class="notice">{{ i18n.t('common.loading') }}</p>
      } @else if (list(); as data) {
        @if (data.locked) {
          <p class="notice warn" role="status">
            {{
              i18n.t('hr.overtime.locked', {
                run: data.runNumber ?? '',
                status: i18n.t('hr.run.status.' + (data.runStatus ?? 'Locked')),
              })
            }}
          </p>
        }

        <section class="bo-panel">
          <div class="bo-panel__head">
            <h2 class="bo-panel__title">{{ i18n.t(editMode() ? 'hr.overtime.edit' : 'hr.overtime.record') }}</h2>
          </div>
          <div class="bo-panel__body bo-form">
            <div class="bo-fields">
              <div class="bo-field">
                <label for="ot-emp">{{ i18n.t('hr.overtime.employee') }}</label>
                <select
                  id="ot-emp"
                  name="employeeId"
                  [(ngModel)]="formEmployeeId"
                  [disabled]="data.locked || editMode() !== null"
                  required
                >
                  @for (emp of employees(); track emp.id) {
                    <option [ngValue]="emp.id">{{ emp.fullName }} ({{ emp.number }})</option>
                  }
                </select>
              </div>
              <div class="bo-field">
                <label for="ot-date">{{ i18n.t('hr.overtime.date') }}</label>
                <app-date-input
                  id="ot-date"
                  name="workDate"
                  [(ngModel)]="formWorkDate"
                  [min]="minDate()"
                  [max]="maxDate()"
                  [disabled]="data.locked"
                  required
                />
              </div>
              <div class="bo-field">
                <label for="ot-kind">{{ i18n.t('hr.overtime.kind') }}</label>
                <select
                  id="ot-kind"
                  name="ruleKey"
                  [ngModel]="formRuleKey()"
                  (ngModelChange)="formRuleKey.set($event)"
                  [disabled]="data.locked"
                  required
                >
                  @for (kind of data.kinds; track kind.key) {
                    <option [ngValue]="kind.key">{{ kind.labels[i18n.lang()] }} ×{{ kind.multiplier }}</option>
                  }
                </select>
              </div>
              <div class="bo-field">
                <label for="ot-hours">{{ i18n.t(isDaysKind() ? 'hr.overtime.days' : 'hr.overtime.hours') }}</label>
                <input
                  id="ot-hours"
                  type="number"
                  class="num mono"
                  name="hours"
                  [(ngModel)]="formHours"
                  step="0.25"
                  min="0"
                  [disabled]="data.locked"
                  required
                />
              </div>
              <div class="bo-field bo-field--wide">
                <label for="ot-note">{{ i18n.t('hr.overtime.note') }}</label>
                <input
                  id="ot-note"
                  type="text"
                  name="note"
                  [(ngModel)]="formNote"
                  [disabled]="data.locked"
                />
              </div>
            </div>
          </div>
          <div class="bo-panel__foot">
            @if (editMode() !== null) {
              <button type="button" class="btn-ghost" (click)="cancelEdit()">
                {{ i18n.t('hr.overtime.cancel') }}
              </button>
            }
            <button
              type="button"
              class="btn-primary"
              [disabled]="data.locked || saving()"
              (click)="save()"
            >
              {{ i18n.t('hr.overtime.save') }}
            </button>
          </div>
        </section>

        <section class="bo-panel">
          <div class="bo-panel__body">
            @if (!pager.total()) {
              <p class="bo-empty">{{ i18n.t('hr.overtime.none') }}</p>
            } @else {
              <div class="bo-scroll">
                <table class="bo-data">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('hr.overtime.date') }}</th>
                      <th>{{ i18n.t('hr.overtime.employee') }}</th>
                      <th>{{ i18n.t('hr.overtime.kind') }}</th>
                      <th class="num">{{ i18n.t('hr.overtime.hours') }} / {{ i18n.t('hr.overtime.days') }}</th>
                      <th>{{ i18n.t('hr.overtime.note') }}</th>
                      <th>{{ i18n.t('hr.overtime.history') }}</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (entry of pager.rows(); track entry.id) {
                      <tr>
                        <td [attr.data-label]="i18n.t('hr.overtime.date')">
                          <bdi dir="ltr" class="mono">{{ entry.workDate }}</bdi>
                        </td>
                        <td [attr.data-label]="i18n.t('hr.overtime.employee')">
                          <span class="bo-cell-strong">{{ entry.employeeName }}</span>
                          <span class="bo-meta mono"><bdi dir="ltr">{{ entry.employeeNumber }}</bdi></span>
                        </td>
                        <td [attr.data-label]="i18n.t('hr.overtime.kind')">
                          {{ kindLabel(entry.ruleKey) }}
                        </td>
                        <td class="num mono" [attr.data-label]="i18n.t('hr.overtime.hours')">
                          {{ entry.hours }} {{ unitForKind(entry.ruleKey) }}
                        </td>
                        <td [attr.data-label]="i18n.t('hr.overtime.note')">
                          {{ entry.note }}
                        </td>
                        <td [attr.data-label]="i18n.t('hr.overtime.history')">
                          @if (entry.createdBy) {
                            <div class="bo-meta">{{ i18n.t('hr.overtime.recordedBy', { name: entry.createdBy }) }}</div>
                          }
                          @if (entry.updatedBy && entry.updatedBy !== entry.createdBy) {
                            <div class="bo-meta">{{ i18n.t('hr.overtime.changedBy', { name: entry.updatedBy }) }}</div>
                          }
                          <button type="button" class="btn-ghost" (click)="toggleHistory(entry.id)">
                            {{ i18n.t('hr.overtime.history') }}
                          </button>
                          @if (historyOpen() === entry.id) {
                            @if (loadingHistory()) {
                              <p class="notice">{{ i18n.t('common.loading') }}</p>
                            } @else {
                              @for (ch of historyData(); track ch.id) {
                                <div class="bo-meta">
                                  {{ i18n.t('hr.overtime.action.' + ch.action) }} — {{ ch.changedBy || '?' }} ({{ i18n.dateTime(ch.changedAtUtc) }})
                                  @if (ch.action === 'Edited') {
                                    : <bdi dir="ltr" class="mono">{{ ch.previousWorkDate }}</bdi> → <bdi dir="ltr" class="mono">{{ ch.workDate }}</bdi>, {{ kindLabel(ch.previousRuleKey || '') }} → {{ kindLabel(ch.ruleKey || '') }}, <span class="mono">{{ ch.previousHours }}</span> → <span class="mono">{{ ch.hours }}</span>
                                  }
                                </div>
                              }
                              @if (historyData().length === 0) {
                                <p class="bo-empty">{{ i18n.t('hr.overtime.none') }}</p>
                              }
                            }
                          }
                        </td>
                        <td class="bo-actions" [attr.data-label]="i18n.t('common.actions')">
                          @if (entry.canEdit) {
                            <app-menu triggerClass="btn-ghost ui-more" [label]="i18n.t('ui.menu.more')">
                              <span menuTrigger aria-hidden="true">⋯</span>
                              <button type="button" appMenuItem (click)="editEntry(entry)">{{ i18n.t('hr.overtime.edit') }}</button>
                              <hr appMenuSeparator />
                              <button type="button" appMenuItem class="is-danger" (click)="deleteEntry(entry)">{{ i18n.t('hr.overtime.delete') }}</button>
                            </app-menu>
                          } @else {
                            <span class="bo-meta">{{ i18n.t('hr.overtime.inRun', { run: entry.lockedByRun || '' }) }}</span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              <!-- Paged by the server; the totals below are its sums over every entry the filter keeps (owner, 2026-11-17). -->
              <app-list-footer [pager]="pager" [loading]="!pager.loaded()" />

              @if (totals().length > 0) {
                <div class="bo-panel__foot bo-actions">
                  <span class="bo-meta">{{ i18n.t('hr.overtime.totals') }}:</span>
                  @for (tot of totals(); track tot.key) {
                    <span class="bo-meta">
                      {{ tot.label }}: <strong class="num mono">{{ tot.hours }}</strong> {{ tot.unit }}
                    </span>
                  }
                </div>
              }
            }
          </div>
        </section>
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
})
export class OvertimeEntriesComponent implements OnInit {
  readonly i18n = inject(I18nService);
  private readonly api = inject(HrApi);
  private readonly pages = inject(PayrollPagesApi);
  private readonly dialog = inject(ConfirmService);
  private readonly admin = inject(AdminService);
  readonly lock = inject(BranchLock);

  readonly branches = signal<AdminBranch[]>([]);
  readonly branchId = signal<string | null>(null);
  readonly year = signal<number>(new Date().getFullYear());
  readonly month = signal<number>(new Date().getMonth() + 1);
  readonly employeeFilter = signal<string | null>(null);

  /**
   * The month's entries a page at a time from the server (owner, 2026-11-17: "paginate in backend"), for everyone or
   * one employee; a change of branch, month or employee goes back to page one. The month's head (locked by which run,
   * the kinds of work) and the hours per kind over every entry kept come in the summary.
   */
  readonly pager = createServerPager(
    (page) =>
      this.pages.overtimePage({
        skip: page.skip,
        take: page.take,
        branchId: page.filters.branchId ?? '',
        year: page.filters.year,
        month: page.filters.month,
        employeeId: page.filters.employeeId,
      }),
    {
      key: 'payroll.overtime',
      filters: () => ({ branchId: this.branchId(), year: this.year(), month: this.month(), employeeId: this.employeeFilter() }),
      enabled: () => !!this.branchId(),
    },
  );

  /** The month as the server read it, or nothing before the first answer (or without a branch). */
  readonly list = computed(() => (this.branchId() ? this.pager.summary() : null));
  readonly employees = signal<Employee[]>([]);
  /** The first answer for the branch is on its way. */
  readonly loading = computed(() => !!this.branchId() && !this.pager.loaded() && !this.pager.error());
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  /** The month could not be read, in words. */
  readonly listError = computed(() => {
    const e = this.pager.error();
    return e ? this.message(e) : null;
  });

  readonly editMode = signal<string | null>(null);
  formEmployeeId: string | null = null;
  formWorkDate = '';
  readonly formRuleKey = signal('');
  formHours: number | null = null;
  formNote = '';

  readonly historyOpen = signal<string | null>(null);
  readonly historyData = signal<OvertimeEntryChange[]>([]);
  readonly loadingHistory = signal(false);

  readonly monthString = computed(() => {
    const y = this.year();
    const m = this.month().toString().padStart(2, '0');
    return `${y}-${m}`;
  });

  readonly minDate = computed(() => `${this.monthString()}-01`);
  readonly maxDate = computed(() => {
    const y = this.year();
    const m = this.month();
    const lastDay = String(new Date(y, m, 0).getDate()).padStart(2, '0');
    return `${this.monthString()}-${lastDay}`;
  });

  readonly isDaysKind = computed(() => {
    const data = this.list();
    const key = this.formRuleKey();
    if (!data || !key) return false;
    const kind = data.kinds.find((k) => k.key === key);
    return kind?.unit === 'Days';
  });

  /** Hours per kind of work, summed by the server over every entry the filter keeps; a kind with none is left out. */
  readonly totals = computed(() => {
    const data = this.list();
    if (!data) return [];
    const sums = new Map(data.totals.map((t) => [t.key, t.hours]));
    return data.kinds
      .map((k) => {
        const sum = sums.get(k.key) ?? 0;
        return {
          key: k.key,
          label: k.labels[this.i18n.lang()],
          hours: sum,
          unit: this.i18n.t(k.unit === 'Days' ? 'hr.unit.short.Days' : 'hr.unit.short.Hours'),
        };
      })
      .filter((t) => t.hours > 0);
  });

  ngOnInit(): void {
    void this.start();
  }

  private async start(): Promise<void> {
    try {
      const branches = await this.admin.listBranches();
      this.branches.set(branches);
      if (this.lock.locked()) {
        this.branchId.set(this.lock.lockedBranchId()!);
      } else if (!this.branchId() && branches.length > 0) {
        this.branchId.set(this.lock.defaultBranch(branches) ?? branches[0].id);
      }
    } catch (e) {
      this.error.set(this.message(e));
    }
    // The entries load themselves once a branch is chosen (the pager's filter).
    await this.loadEmployees();
  }

  branchName(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  setBranch(branchId: string): void {
    this.branchId.set(branchId || null);
    this.cancelEdit();
    void this.loadEmployees();
  }

  onMonthSelected(val: string): void {
    if (!val) return;
    const [y, m] = val.split('-');
    this.year.set(parseInt(y, 10));
    this.month.set(parseInt(m, 10));
    this.cancelEdit();
  }

  private async loadEmployees(): Promise<void> {
    const bid = this.branchId();
    if (!bid) {
      this.employees.set([]);
      return;
    }
    try {
      const res = await this.api.employees({ branchId: bid, active: true, take: 500 });
      this.employees.set(res.rows);
    } catch (e) {
      this.employees.set([]);
      this.error.set(this.message(e));
    }
  }

  /** Asks again for the page on screen after a save or a delete, and empties the form. */
  private async loadData(): Promise<void> {
    if (!this.branchId()) {
      this.cancelEdit();
      return;
    }
    await this.pager.reload();
    this.cancelEdit();
  }

  kindLabel(key: string): string {
    const k = this.list()?.kinds.find((x) => x.key === key);
    if (!k) return key;
    return `${k.labels[this.i18n.lang()]} ×${k.multiplier}`;
  }

  unitForKind(key: string): string {
    const k = this.list()?.kinds.find((x) => x.key === key);
    if (!k) return '';
    return this.i18n.t(k.unit === 'Days' ? 'hr.unit.short.Days' : 'hr.unit.short.Hours');
  }

  editEntry(entry: OvertimeEntry): void {
    this.editMode.set(entry.id);
    this.formEmployeeId = entry.employeeId;
    this.formWorkDate = entry.workDate;
    this.formRuleKey.set(entry.ruleKey);
    this.formHours = entry.hours;
    this.formNote = entry.note ?? '';
    this.error.set(null);
  }

  cancelEdit(): void {
    this.editMode.set(null);
    this.formEmployeeId = null;
    this.formWorkDate = '';
    this.formRuleKey.set('');
    this.formHours = null;
    this.formNote = '';
    this.error.set(null);
  }

  async save(): Promise<void> {
    const employeeId = this.formEmployeeId;
    const workDate = this.formWorkDate;
    const ruleKey = this.formRuleKey();
    const hours = this.formHours;
    if (!employeeId || !workDate || !ruleKey || hours === null) return;
    if (!(await this.dialog.ask(saveRequest(this.i18n)))) return;
    this.saving.set(true);
    this.error.set(null);
    try {
      const id = this.editMode();
      if (id) {
        await this.api.updateOvertimeEntry(id, {
          workDate,
          ruleKey,
          hours,
          note: this.formNote || null,
        });
      } else {
        await this.api.createOvertimeEntry({
          employeeId,
          workDate,
          ruleKey,
          hours,
          note: this.formNote || null,
        });
      }
      await this.loadData();
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.saving.set(false);
    }
  }

  async deleteEntry(entry: OvertimeEntry): Promise<void> {
    const title = this.i18n.t('hr.overtime.deleteTitle');
    const msg = this.i18n.t('hr.overtime.deleteMessage');
    const req = deleteRequest(this.i18n, title, msg);
    if (await this.dialog.ask(req)) {
      try {
        await this.api.deleteOvertimeEntry(entry.id);
        await this.loadData();
      } catch (e) {
        this.error.set(this.message(e));
      }
    }
  }

  async toggleHistory(id: string): Promise<void> {
    if (this.historyOpen() === id) {
      this.historyOpen.set(null);
      return;
    }
    this.historyOpen.set(id);
    this.loadingHistory.set(true);
    try {
      const h = await this.api.overtimeHistory(id);
      this.historyData.set(h);
    } catch (e) {
      this.historyData.set([]);
      this.error.set(this.message(e));
    } finally {
      this.loadingHistory.set(false);
    }
  }

  private message(error: unknown): string {
    const problem = hrProblem(error);
    if (!problem) return moneyErrorMessage(error, (key, params) => this.i18n.t(key, params));
    const key = `hr.problem.${problem.code}`;
    const text = this.i18n.t(key, { key: problem.key ?? '', run: problem.runNumber ?? '' });
    return text === key ? problem.message || key : text;
  }
}
