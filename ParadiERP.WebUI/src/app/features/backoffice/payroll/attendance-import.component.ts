import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminBranch, AdminService, adminErrorMessage } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { BranchLock } from '../../../core/branch-lock';
import { BranchScopeService, startBranch } from '../../../core/branch-scope.service';
import { I18nService } from '../../../core/i18n.service';
import { canOpen } from '../../../core/nav';
import { DateInputComponent } from '../../../ui/date-input.component';
import { FormSectionComponent } from '../../../ui/form-section.component';
import { ListFooterComponent } from '../../../ui/list-footer.component';
import { createPager } from '../../../ui/pager';
import { StatusComponent } from '../../../ui/status.component';
import { toneOf } from '../../../ui/status-tones';
import { TableStateComponent } from '../../../ui/table-state.component';
import { localizedName } from '../backoffice-format';
import { HrApi, PunchDateOrder, PunchMapping, PunchPreview, PunchRow, hrProblem } from './hr-api.service';
import {
  PUNCH_ROLES,
  PunchRole,
  ROW_FILTERS,
  RowFilter,
  assign,
  columnLabel,
  emptyMapping,
  fileProblemKey,
  filterRows,
  mappingProblem,
  wallClock,
} from './punch-import';

/**
 * Employees › Import attendance (FR-3B): the fingerprint terminal's monthly export, read and shown
 * — and nothing written.
 *
 * <p>Format-neutral, because the terminal's real export has not been seen yet. The reader picks
 * the file, the sheet and the header row, and says which column is the fingerprint number, the
 * date and the time (or one column with both), and — only if the file has them — the name and an
 * in/out marker. The server reads every row through that and answers what each became: a punch of
 * an employee of this branch, a number nobody carries, an employee of another branch, or a row it
 * could not read and why.</p>
 *
 * <p>A person is found by the number on their card and never by name. What a punch means — the
 * arrival, the departure, a repeated punch, a single punch on a day, a night shift, lateness,
 * absence, overtime — and what to do where a day was already clocked here, all wait for one real
 * export; until then there is no button that writes.</p>
 */
@Component({
  selector: 'app-hr-attendance-import',
  standalone: true,
  imports: [FormsModule, RouterLink, DateInputComponent, FormSectionComponent, ListFooterComponent, StatusComponent, TableStateComponent],
  template: `
    <div class="bo-stack">
      <p class="notice" role="note">{{ i18n.t('punch.previewOnly') }}</p>

      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }

      <app-form-section [heading]="i18n.t('punch.fileTitle')" [note]="i18n.t('punch.fileNote')" [columns]="3">
            @if (lock.locked()) {
              <div class="bo-field">
                <p class="bo-fixed-label">{{ i18n.t('branchLock.branch') }}</p>
                <p class="bo-fixed" id="pi-branch-fixed">{{ lock.ownBranchName(branches()) }}</p>
              </div>
            } @else {
              <div class="bo-field">
                <label for="pi-branch">{{ i18n.t('branchLock.branch') }}</label>
                <select id="pi-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)" [disabled]="busy()">
                  @for (b of branches(); track b.id) {
                    <option [ngValue]="b.id">{{ name(b) }}</option>
                  }
                </select>
              </div>
            }
            <div class="bo-field">
              <label for="pi-file-choose">{{ i18n.t('punch.file') }}</label>
              <!-- The browser's own file control speaks the browser's language, not the app's. -->
              <div class="bo-file">
                <button type="button" id="pi-file-choose" class="btn-ghost" [disabled]="busy()" aria-describedby="pi-file-hint" (click)="piFile.click()">
                  {{ i18n.t('common.chooseFile') }}
                </button>
                <span class="bo-file__name">{{ piFile.files?.[0]?.name || i18n.t('common.noFile') }}</span>
                <input
                  #piFile
                  id="pi-file"
                  type="file"
                  hidden
                  accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                  [disabled]="busy()"
                  (change)="pick($event)"
                />
              </div>
              <p class="bo-hint" id="pi-file-hint">{{ i18n.t('punch.fileHint') }}</p>
            </div>
            @if (preview(); as p) {
              @if (p.sheetNames.length > 1) {
                <div class="bo-field">
                  <label for="pi-sheet">{{ i18n.t('punch.sheet') }}</label>
                  <select id="pi-sheet" [ngModel]="p.sheetName" (ngModelChange)="setSheet($event)" [disabled]="busy()">
                    @for (s of p.sheetNames; track s) {
                      <option [ngValue]="s">{{ s }}</option>
                    }
                  </select>
                </div>
              }
            }
      </app-form-section>

      @if (busy() && !preview()) {
        <app-table-state kind="loading" />
      }

      @if (preview(); as p) {
        <section class="ui-surface" aria-labelledby="pi-layout-title">
          <div class="ui-surface__head">
            <h2 class="surface-title" id="pi-layout-title">{{ i18n.t('punch.layoutTitle') }}</h2>
            <p>{{ i18n.t('punch.source', { file: p.fileName, sheet: p.sheetName }) }}</p>
          </div>
          <div class="ui-surface__body bo-stack">
            <div class="bo-field header-field">
              <label for="pi-header">{{ i18n.t('punch.headerRow') }}</label>
              <select id="pi-header" [ngModel]="p.headerRow ?? 0" (ngModelChange)="setHeader(+$event)" [disabled]="busy()" aria-describedby="pi-header-hint">
                <option [ngValue]="0">{{ i18n.t('punch.noHeader') }}</option>
                @for (r of p.top; track r.row) {
                  <option [ngValue]="r.row">{{ topRowLabel(r.row, r.cells) }}</option>
                }
              </select>
              <p class="bo-hint" id="pi-header-hint">
                {{ p.suggestedHeaderRow ? i18n.t('punch.headerSuggested', { row: p.suggestedHeaderRow }) : i18n.t('punch.headerNone') }}
              </p>
            </div>

            <div class="ui-scroll top-rows">
              <table class="ui-table">
                <caption class="sr-only">{{ i18n.t('punch.topRows') }}</caption>
                <thead>
                  <tr>
                    <th class="num">{{ i18n.t('punch.row') }}</th>
                    @for (c of topColumns(); track c) {
                      <th>{{ letter(c) }}</th>
                    }
                  </tr>
                </thead>
                <tbody>
                  @for (r of p.top; track r.row) {
                    <tr [class.is-header]="r.row === p.headerRow" [attr.aria-current]="r.row === p.headerRow ? 'true' : null">
                      <td class="num mono">{{ r.row }}</td>
                      @for (c of topColumns(); track c) {
                        <td>{{ r.cells[c] ?? '' }}</td>
                      }
                    </tr>
                  }
                </tbody>
              </table>
            </div>

            @if (autoMapped) {
              <p class="bo-hint">{{ i18n.t('punch.mappingSuggested') }}</p>
            }
            <app-form-section [heading]="i18n.t('punch.columnsTitle')" [note]="i18n.t('punch.columnsNote')" [columns]="3">
              @for (role of roles; track role) {
                <div class="bo-field" [class.is-invalid]="missingRole(role)">
                  <label [for]="'pi-col-' + role">
                    {{ i18n.t('punch.role.' + role) }}
                    @if (role === 'number') {
                      <span class="bo-req">{{ i18n.t('common.required') }}</span>
                    }
                  </label>
                  <select
                    [id]="'pi-col-' + role"
                    [ngModel]="mapping()[role]"
                    (ngModelChange)="setRole(role, $event)"
                    [disabled]="busy()"
                    [attr.aria-describedby]="'pi-col-' + role + '-hint'"
                  >
                    <option [ngValue]="null">{{ i18n.t(role === 'number' ? 'punch.chooseColumn' : 'punch.notInFile') }}</option>
                    @for (c of p.columns; track c.index) {
                      <option [ngValue]="c.index">{{ label(c) }}</option>
                    }
                  </select>
                  <p class="bo-hint" [id]="'pi-col-' + role + '-hint'">{{ i18n.t('punch.roleHint.' + role) }}</p>
                </div>
              }
              @if (p.summary && p.summary.textDates > 0) {
                <div class="bo-field">
                  <label for="pi-order">{{ i18n.t('punch.dateOrder') }}</label>
                  <select id="pi-order" [ngModel]="order()" (ngModelChange)="setOrder($event)" [disabled]="busy()" aria-describedby="pi-order-hint">
                    <option value="dmy">{{ i18n.t('punch.order.dmy') }}</option>
                    <option value="mdy">{{ i18n.t('punch.order.mdy') }}</option>
                    <option value="ymd">{{ i18n.t('punch.order.ymd') }}</option>
                  </select>
                  <p class="bo-hint" id="pi-order-hint">{{ i18n.t('punch.dateOrderHint', { n: p.summary.textDates }) }}</p>
                </div>
              }
            </app-form-section>

            @if (p.mappingProblem) {
              <p class="notice warn" role="status">{{ i18n.t('punch.problem.' + p.mappingProblem) }}</p>
            }
          </div>
        </section>

        @if (p.summary; as s) {
          <div class="ui-metrics" aria-live="polite">
            <div class="ui-metric ui-metric--lead">
              <span class="ui-metric__label">{{ i18n.t('punch.metric.rows') }}</span>
              <strong class="ui-metric__value mono">{{ s.rows }}</strong>
              <span class="ui-metric__note">{{ s.first ? i18n.t('punch.period', { from: day(s.first), to: day(s.last) }) : '—' }}</span>
            </div>
            <div class="ui-metric">
              <span class="ui-metric__label">{{ i18n.t('punch.status.matched') }}</span>
              <strong class="ui-metric__value mono">{{ s.matched }}</strong>
              <span class="ui-metric__note">{{ i18n.t('punch.metric.people', { n: p.employees.length }) }}</span>
            </div>
            <div class="ui-metric" [class.ui-metric--warn]="s.unknown > 0">
              <span class="ui-metric__label">{{ i18n.t('punch.status.unknown') }}</span>
              <strong class="ui-metric__value mono">{{ s.unknown }}</strong>
              <span class="ui-metric__note">{{ i18n.t('punch.metric.numbers', { n: p.unknownNumbers.length }) }}</span>
            </div>
            <div class="ui-metric" [class.ui-metric--warn]="s.invalid > 0">
              <span class="ui-metric__label">{{ i18n.t('punch.status.invalid') }}</span>
              <strong class="ui-metric__value mono">{{ s.invalid }}</strong>
              @if (s.otherBranch) {
                <span class="ui-metric__note">{{ i18n.t('punch.metric.otherBranch', { n: s.otherBranch }) }}</span>
              }
              @if ((s.flagged ?? 0) > 0) {
                <span class="ui-metric__note">{{ i18n.t('punch.summary.flagged', { n: s.flagged ?? 0 }) }}</span>
              }
            </div>
          </div>

          @if (p.unknownNumbers.length) {
            <section class="ui-surface" aria-labelledby="pi-unknown-title">
              <div class="ui-surface__head">
                <h2 class="surface-title" id="pi-unknown-title">{{ i18n.t('punch.unknownTitle') }}</h2>
                <p>
                  {{ i18n.t('punch.unknownNote') }}
                  @if (mayOpenEmployees()) {
                    <a class="ui-link" routerLink="/backoffice/employees/employees">{{ i18n.t('punch.openEmployees') }}</a>
                  }
                </p>
              </div>
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('hr.employees.fingerprint') }}</th>
                      <th class="num">{{ i18n.t('punch.metric.rows') }}</th>
                      <th>{{ i18n.t('punch.nameInFile') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (u of unknownPager.rows(); track u.number) {
                      <tr>
                        <td class="mono" [attr.data-label]="i18n.t('hr.employees.fingerprint')">{{ u.number }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('punch.metric.rows')">{{ u.rows }}</td>
                        <td [attr.data-label]="i18n.t('punch.nameInFile')">{{ u.names.join(' · ') || '—' }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </section>
            <app-list-footer [pager]="unknownPager" />
          }

          @if (p.otherBranchNumbers.length) {
            <p class="notice" role="note">
              {{ i18n.t('punch.otherBranchNote', { numbers: otherNumbers() }) }}
            </p>
          }

          @if (p.employees.length) {
            <section class="ui-surface" aria-labelledby="pi-people-title">
              <div class="ui-surface__head">
                <h2 class="surface-title" id="pi-people-title">{{ i18n.t('punch.peopleTitle') }}</h2>
                <p>{{ i18n.t('punch.peopleNote') }}</p>
              </div>
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('hr.employees.name') }}</th>
                      <th>{{ i18n.t('hr.employees.fingerprint') }}</th>
                      <th class="num">{{ i18n.t('punch.punches') }}</th>
                      <th class="num">{{ i18n.t('punch.dates') }}</th>
                      <th>{{ i18n.t('punch.firstPunch') }}</th>
                      <th>{{ i18n.t('punch.lastPunch') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (e of peoplePager.rows(); track e.employeeId) {
                      <tr>
                        <td [attr.data-label]="i18n.t('hr.employees.name')">
                          {{ e.employeeName }}<span class="ui-sub mono"><bdi dir="ltr">{{ e.employeeNumber }}</bdi></span>
                        </td>
                        <td class="mono" [attr.data-label]="i18n.t('hr.employees.fingerprint')">{{ e.fingerprintNumber }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('punch.punches')">{{ e.punches }}</td>
                        <td class="num mono" [attr.data-label]="i18n.t('punch.dates')">{{ e.dates }}</td>
                        <td class="mono" [attr.data-label]="i18n.t('punch.firstPunch')">{{ clock(e.first) }}</td>
                        <td class="mono" [attr.data-label]="i18n.t('punch.lastPunch')">{{ clock(e.last) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </section>
            <app-list-footer [pager]="peoplePager" />
          }

          <section class="ui-surface" aria-labelledby="pi-rows-title">
            <div class="ui-surface__head">
              <h2 class="surface-title" id="pi-rows-title">{{ i18n.t('punch.rowsTitle') }}</h2>
              <div class="bo-chips" role="group" [attr.aria-label]="i18n.t('common.state')">
                @for (f of filters; track f) {
                  <button type="button" [class.is-on]="filter() === f" [attr.aria-pressed]="filter() === f" (click)="setFilter(f)">
                    {{ i18n.t(f === 'all' ? 'fin.source.all' : 'punch.status.' + f) }}
                    <span class="mono count">{{ countOf(f) }}</span>
                  </button>
                }
              </div>
            </div>
            @if (p.rowsCut) {
              <p class="ui-surface__body bo-hint">{{ i18n.t('punch.rowsCut') }}</p>
            }
            @if (p.rows.length) {
              <!-- These narrow the rows already read (owner, 2026-11-17); none is a request. -->
              <div class="ui-surface__body">
                <div class="ui-toolbar" role="search">
                  @if (p.employees.length) {
                    <div class="ui-filter">
                      <label for="pi-f-employee">{{ i18n.t('punch.employee') }}</label>
                      <select id="pi-f-employee" [ngModel]="employeeFilter()" (ngModelChange)="employeeFilter.set($event)">
                        <option ngValue="all">{{ i18n.t('filters.all') }}</option>
                        @for (e of p.employees; track e.employeeId) {
                          <option [ngValue]="e.employeeId">{{ e.employeeName }} ({{ e.employeeNumber }})</option>
                        }
                      </select>
                    </div>
                  }
                  <div class="ui-filter">
                    <label for="pi-f-from">{{ i18n.t('filters.from') }}</label>
                    <app-date-input id="pi-f-from" [ngModel]="fromDay()" (ngModelChange)="fromDay.set($event || '')" />
                  </div>
                  <div class="ui-filter">
                    <label for="pi-f-to">{{ i18n.t('filters.to') }}</label>
                    <app-date-input id="pi-f-to" [ngModel]="toDay()" (ngModelChange)="toDay.set($event || '')" />
                  </div>
                  @if (filtered()) {
                    <div class="ui-toolbar__end">
                      <button type="button" class="btn-ghost" (click)="clearFilters()">{{ i18n.t('ui.state.clearFilters') }}</button>
                    </div>
                  }
                </div>
              </div>
            }
            @if (!visible().length) {
              <app-table-state kind="empty" [message]="i18n.t('punch.noRows')" />
            } @else if (!shown().length) {
              <app-table-state kind="noResults" (clear)="clearFilters()" />
            } @else {
              <div class="ui-scroll">
                <table class="ui-table ui-table--cards">
                  <thead>
                    <tr>
                      <th class="num">{{ i18n.t('punch.row') }}</th>
                      <th>{{ i18n.t('common.state') }}</th>
                      <th>{{ i18n.t('hr.employees.fingerprint') }}</th>
                      <th>{{ i18n.t('punch.employee') }}</th>
                      <th>{{ i18n.t('punch.when') }}</th>
                      @if (mapping().timeOut) {
                        <th>{{ i18n.t('punch.col.kind') }}</th>
                      }
                      @if (mapping().marker) {
                        <th>{{ i18n.t('punch.role.marker') }}</th>
                      }
                    </tr>
                  </thead>
                  <tbody>
                    @for (r of pager.rows(); track r.row) {
                      <tr>
                        <td class="num mono" [attr.data-label]="i18n.t('punch.row')">{{ r.row }}</td>
                        <td [attr.data-label]="i18n.t('common.state')">
                          <app-status [tone]="tone(r)">{{ i18n.t('punch.status.' + r.status) }}</app-status>
                          @if (r.problem) {
                            <span class="ui-sub">{{ i18n.t('punch.rowProblem.' + r.problem) }}</span>
                          }
                        </td>
                        <td class="mono" [attr.data-label]="i18n.t('hr.employees.fingerprint')">{{ r.number ?? '—' }}</td>
                        <td [attr.data-label]="i18n.t('punch.employee')">
                          @if (r.employeeName) {
                            {{ r.employeeName }}<span class="ui-sub mono"><bdi dir="ltr">{{ r.employeeNumber }}</bdi></span>
                          } @else {
                            —
                          }
                          @if (r.name) {
                            <span class="ui-sub">{{ i18n.t('punch.inFile', { name: r.name }) }}</span>
                          }
                        </td>
                        <td class="mono" [attr.data-label]="i18n.t('punch.when')">
                          @if (r.at) {
                            {{ clock(r.at) }}
                          } @else {
                            <span class="ui-sub">{{ r.source || '—' }}</span>
                          }
                        </td>
                        @if (mapping().timeOut) {
                          <td [attr.data-label]="i18n.t('punch.col.kind')">
                            {{ i18n.t(r.exit ? 'punch.kind.out' : 'punch.kind.in') }}
                            @for (f of r.flags ?? []; track f) {
                              <span class="ui-sub">{{ i18n.t('punch.flag.' + f) }}</span>
                            }
                          </td>
                        }
                        @if (mapping().marker) {
                          <td [attr.data-label]="i18n.t('punch.role.marker')">{{ r.marker ?? '—' }}</td>
                        }
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
          <app-list-footer [pager]="pager" />
        }
      }
    </div>
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .surface-title {
        margin: 0;
        font-size: var(--fs-card);
        font-weight: 600;
      }
      .header-field {
        max-inline-size: var(--form-max);
      }
      .top-rows tr.is-header td {
        background: var(--surface-tint);
        font-weight: 600;
      }
      .count {
        margin-inline-start: var(--sp-1);
        color: var(--muted);
      }
    `,
  ],
})
export class HrAttendanceImportComponent implements OnInit {
  private readonly hr = inject(HrApi);
  private readonly admin = inject(AdminService);
  private readonly scope = inject(BranchScopeService);
  private readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);

  readonly roles = PUNCH_ROLES;
  readonly filters = ROW_FILTERS;

  readonly branches = signal<AdminBranch[]>([]);
  readonly branchId = signal<string | null>(null);
  readonly file = signal<File | null>(null);
  readonly sheet = signal<string | null>(null);
  /** null: the server's suggestion; 0: no header row. */
  readonly header = signal<number | null>(null);
  readonly mapping = signal<PunchMapping>(emptyMapping());
  readonly order = signal<PunchDateOrder>('dmy');
  readonly preview = signal<PunchPreview | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly filter = signal<RowFilter>('all');

  /** Answers can cross: only the latest request's is shown. */
  private sequence = 0;

  readonly visible = computed(() => filterRows(this.preview()?.rows ?? [], this.filter()));

  // The rows' own filters (owner, 2026-11-17), beside the state chips. Each narrows the rows already
  // read; none is a request.
  readonly employeeFilter = signal<string>('all');
  readonly fromDay = signal('');
  readonly toDay = signal('');
  readonly filtered = computed(() => this.employeeFilter() !== 'all' || !!this.fromDay() || !!this.toDay());

  /**
   * The rows after the chips and the filters. A row's `at` is already the wall clock of the
   * company's zone (`YYYY-MM-DDTHH:mm:ss`), so its day is its first ten characters; a row with no
   * time read has no day and falls outside any range.
   */
  readonly shown = computed(() => {
    const employee = this.employeeFilter();
    const from = this.fromDay();
    const to = this.toDay();
    return this.visible().filter((r) => {
      if (employee !== 'all' && r.employeeId !== employee) return false;
      if (from || to) {
        const day = r.at?.slice(0, 10) ?? '';
        if (!day) return false;
        if (from && day < from) return false;
        if (to && day > to) return false;
      }
      return true;
    });
  });

  /** Ten rows to begin with, 20, 50 or 100 at the user's choice (owner, 2026-11-17); it replaced fifty fixed. */
  readonly pager = createPager(() => this.shown(), {
    key: 'payroll.attendanceImport.rows',
    resetOn: () => [this.filter(), this.employeeFilter(), this.fromDay(), this.toDay()],
  });
  readonly unknownPager = createPager(() => this.preview()?.unknownNumbers ?? [], { key: 'payroll.attendanceImport.unknown' });
  readonly peoplePager = createPager(() => this.preview()?.employees ?? [], { key: 'payroll.attendanceImport.people' });

  clearFilters(): void {
    this.employeeFilter.set('all');
    this.fromDay.set('');
    this.toDay.set('');
  }

  /** The top rows' columns, A to the last one used, so the grid reads like the sheet. */
  readonly topColumns = computed(() => {
    const top = this.preview()?.top ?? [];
    const max = Math.max(0, ...top.flatMap((r) => Object.keys(r.cells).map(Number)));
    return Array.from({ length: Math.min(max, 26) }, (_, i) => i + 1);
  });

  readonly otherNumbers = computed(() => (this.preview()?.otherBranchNumbers ?? []).map((o) => o.number).join(', '));

  /** The employee list is behind payroll.manage; a foreman holding hr.attendance alone cannot open it. */
  readonly mayOpenEmployees = computed(() => canOpen(this.auth.currentUser(), 'employees.employees'));

  autoMapped = false;

  ngOnInit(): void {
    void this.start();
  }

  private async start(): Promise<void> {
    // A locked reader's branch is known before the list arrives; a file chosen meanwhile is read.
    this.branchId.set(this.lock.lockedBranchId());
    try {
      const branches = await this.admin.listBranches();
      this.branches.set(branches.filter((b) => b.isActive));
      this.branchId.set(startBranch(this.scope, this.lock, this.branches()));
      if (this.file() && !this.preview() && !this.busy()) void this.read();
    } catch (e) {
      this.error.set(adminErrorMessage(e));
    }
  }

  // ---------- choices ----------

  pick(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    this.file.set(file);
    this.autoMapped = false;
    // A new file is a new layout: nothing chosen for the last one carries over.
    this.sheet.set(null);
    this.header.set(null);
    this.mapping.set(emptyMapping());
    this.preview.set(null);
    this.filter.set('all');
    void this.read();
  }

  setBranch(id: string): void {
    this.branchId.set(id);
    void this.read();
  }

  setSheet(name: string): void {
    this.sheet.set(name);
    this.autoMapped = false;
    this.header.set(null);
    this.mapping.set(emptyMapping());
    void this.read();
  }

  setHeader(row: number): void {
    this.header.set(row);
    void this.read();
  }

  setRole(role: PunchRole, column: number | null): void {
    this.mapping.set(assign(this.mapping(), role, column));
    void this.read();
  }

  setOrder(order: PunchDateOrder): void {
    this.order.set(order);
    void this.read();
  }

  setFilter(filter: RowFilter): void {
    this.filter.set(filter);
    this.pager.reset();
  }

  // ---------- reading ----------

  private async read(): Promise<void> {
    const file = this.file();
    const branchId = this.branchId();
    if (!file || !branchId) return;
    const mine = ++this.sequence;
    this.busy.set(true);
    this.error.set(null);
    try {
      const answer = await this.hr.previewPunches({
        branchId,
        file,
        sheet: this.sheet(),
        header: this.header(),
        mapping: this.mapping(),
        order: this.order(),
      });
      if (mine !== this.sequence) return;
      this.preview.set(answer);
      this.pager.reset();
      this.unknownPager.reset();
      this.peoplePager.reset();

      const m = this.mapping();
      const allNull = Object.values(m).every((v) => v === null);
      if (!this.autoMapped && allNull && answer.suggestedMapping?.number != null) {
        this.autoMapped = true;
        this.mapping.set({ ...emptyMapping(), ...answer.suggestedMapping });
        void this.read();
        return;
      }
    } catch (e) {
      if (mine !== this.sequence) return;
      this.error.set(this.message(e));
    } finally {
      if (mine === this.sequence) this.busy.set(false);
    }
  }

  // ---------- words ----------

  name(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  label = columnLabel;
  clock = wallClock;

  letter(index: number): string {
    return this.preview()?.columns.find((c) => c.index === index)?.letter ?? String.fromCharCode(64 + index);
  }

  topRowLabel(row: number, cells: Record<string, string>): string {
    const text = Object.values(cells).filter((v) => v?.trim()).slice(0, 4).join(' · ');
    return this.i18n.t('punch.rowLabel', { row, text: text.length > 60 ? text.slice(0, 60) + '…' : text });
  }

  day(at: string | null): string {
    return at ? wallClock(at.slice(0, 10)) : '—';
  }

  missingRole(role: PunchRole): boolean {
    const problem = this.preview() ? mappingProblem(this.mapping()) : null;
    return (problem === 'mapNumber' && role === 'number') || (problem === 'mapWhen' && (role === 'date' || role === 'time' || role === 'dateTime'));
  }

  countOf(filter: RowFilter): number {
    const s = this.preview()?.summary;
    if (!s) return 0;
    return filter === 'all' ? s.rows : s[filter];
  }

  tone(row: PunchRow) {
    return toneOf('punch', row.status);
  }

  private message(error: unknown): string {
    const key = fileProblemKey(error);
    if (key) return this.i18n.t(key);
    const problem = hrProblem(error);
    if (problem) {
      const k = `hr.problem.${problem.code}`;
      const text = this.i18n.t(k);
      return text === k ? problem.message || k : text;
    }
    return adminErrorMessage(error);
  }
}
