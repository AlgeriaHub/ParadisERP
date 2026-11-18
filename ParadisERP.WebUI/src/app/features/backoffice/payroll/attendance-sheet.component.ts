import { Component, ElementRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AdminBranch, AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { discardRequest, saveRequest } from '../../../ui/confirm-requests';
import { localizedName } from '../backoffice-format';
import { PAGE_CHROME } from '../shell/page-chrome';
import { HrDayGridComponent, monthNames } from './attendance-day-grid.component';
import { AttendanceSheet, AttendanceSheetRow, HrApi, UpsertSheetRow, hrProblem } from './hr-api.service';
import { moneyErrorMessage } from './wage-money-api.service';
import { TextByLang } from './payroll-api.service';
import {
  CellRef,
  FigureProblem,
  SheetColumn,
  SheetEdit,
  SheetShape,
  cellId,
  changedColumns,
  daysInMonth,
  editFromRow,
  fillColumnDown,
  isStep,
  moveTo,
  readCell,
  readMove,
  restoreCell,
  sheetColumns,
  toRowRequest,
  typeIntoCell,
  writeCell,
} from './sheet-keys';

/**
 * The attendance sheet — the screen this module lives or dies on.
 *
 * <p>A foreman with sixty people types sixty rows of figures on the last day of the month. So the
 * period is chosen once at the top, every cell is reachable from the keyboard, a figure typed is
 * kept without reaching for the mouse, and what has already been entered is visible at a glance.
 * The keyboard itself is not written here: which key moves where, what a half-typed figure means
 * and what Ctrl+D copies are rules in <code>sheet-keys.ts</code>, tested without a DOM.</p>
 *
 * <p>Three things this screen will not do. It will not <b>type over a Recorded row</b> — those
 * figures were added up from the day grid and the clock, and it offers the grid instead. It will
 * not <b>hide a locked month</b>: a period whose run is locked says so, names the run and says
 * what to do, rather than letting a save come back refused. And it will not <b>lose a page of
 * typing</b> when the pager turns: the edit buffer is kept across pages and one save sends
 * everything that was typed, wherever it was typed.</p>
 */
@Component({
  selector: 'app-hr-attendance-sheet',
  standalone: true,
  imports: [FormsModule, HrDayGridComponent, ...PAGE_CHROME],
  template: `
    <ng-template appPageToolbar>
      <div class="ui-toolbar" role="search">
        @if (lock.locked()) {
          <p class="bo-scope-note" id="sh-scope">{{ i18n.t('branchLock.listNote', { branch: lock.ownBranchName(branches()) }) }}</p>
        } @else {
          <div class="ui-filter">
            <label for="sh-branch">{{ i18n.t('hr.employees.branch') }}</label>
            <select id="sh-branch" [ngModel]="branchId()" (ngModelChange)="setBranch($event)">
              @for (branch of branches(); track branch.id) {
                <option [value]="branch.id">{{ branchName(branch) }}</option>
              }
            </select>
          </div>
        }
        <div class="ui-filter">
          <label for="sh-month">{{ i18n.t('hr.sheet.month') }}</label>
          <select id="sh-month" [ngModel]="month()" (ngModelChange)="setPeriod(year(), +$event)">
            @for (name of months(); track $index) {
              <option [ngValue]="$index + 1">{{ name }}</option>
            }
          </select>
        </div>
        <div class="ui-filter sh-year">
          <label for="sh-year">{{ i18n.t('hr.sheet.year') }}</label>
          <!-- A list, not a number box: Chromium draws a number box's digits in the page's script
               (Arabic-Indic under Arabic), and the year is always one of a few. -->
          <select id="sh-year" class="mono" [ngModel]="year()" (ngModelChange)="setPeriod(+$event, month())">
            @for (y of years(); track y) {
              <option [ngValue]="y">{{ y }}</option>
            }
          </select>
        </div>
        <div class="ui-filter ui-filter--search">
          <label for="sh-q">{{ i18n.t('hr.employees.search') }}</label>
          <input id="sh-q" type="search" [ngModel]="query()" (ngModelChange)="setQuery($event)" />
        </div>
      </div>
    </ng-template>

    <div class="bo-stack">
      @if (error()) {
        <p class="notice danger" role="alert">{{ error() }}</p>
      }
      @if (notice()) {
        <p class="notice ok" role="status">{{ notice() }}</p>
      }

      <section class="bo-panel" [attr.aria-label]="i18n.t('hr.sheet.title')">
        <div class="bo-panel__body">
          @if (loading()) {
            <p class="notice">{{ i18n.t('common.loading') }}</p>
          } @else {
          @if (sheet(); as s) {
            @if (s.locked) {
              <!-- Said before a save is attempted, with the run named: "unlock the run" is an
                   instruction somebody can follow, "409" is not. -->
              <p class="notice warn" role="status">
                {{
                  i18n.t('hr.sheet.locked', {
                    run: s.runNumber ?? '—',
                    status: i18n.t('hr.run.status.' + (s.runStatus ?? 'Locked')),
                  })
                }}
              </p>
            }
            <p class="bo-hint">
              {{
                i18n.t('hr.sheet.hint', {
                  mode: i18n.t('hr.attendance.mode.' + s.companyDefaultMode),
                })
              }}
            </p>

            @if (s.rows.length === 0) {
              <p class="bo-empty">{{ i18n.t('hr.sheet.none') }}</p>
            } @else {
              <div class="bo-scroll bo-scroll--tall">
                <table class="bo-data sheet">
                  <thead>
                    <tr>
                      <th>{{ i18n.t('hr.employees.name') }}</th>
                      <th class="num">{{ i18n.t('hr.sheet.daysPresent') }}</th>
                      <th class="num">{{ i18n.t('hr.sheet.paidLeave') }}</th>
                      <th class="num">{{ i18n.t('hr.sheet.unpaidAbsence') }}</th>
                      @for (rule of s.overtimeRules; track rule.key) {
                        <th class="num">{{ label(rule.labels) || rule.key }} ({{ i18n.t('hr.unit.short.' + (rule.unit ?? 'Hours')) }})</th>
                      }
                      <th>{{ i18n.t('hr.sheet.source') }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (row of s.rows; track row.employeeId; let r = $index) {
                      <tr>
                        <td [attr.data-label]="i18n.t('hr.employees.name')">
                          <span class="bo-cell-strong">{{ row.employeeName }}</span>
                          <span class="bo-meta mono"><bdi dir="ltr">{{ row.employeeNumber }}</bdi></span>
                        </td>
                        @for (column of columns(); track column.id; let c = $index) {
                          <td class="num" [attr.data-label]="columnLabel(column)">
                            @if (row.readOnly) {
                              <span class="mono">{{ display(row, column) }}</span>
                            } @else {
                              <input
                                [id]="cellDomId(r, c)"
                                class="num mono cell"
                                type="text"
                                inputmode="decimal"
                                autocomplete="off"
                                [class.is-changed]="isChanged(row, column)"
                                [class.is-wrong]="problem(row, column) !== null"
                                [disabled]="s.locked"
                                [attr.aria-label]="row.employeeName + ' — ' + columnLabel(column)"
                                [attr.aria-invalid]="problem(row, column) !== null ? 'true' : null"
                                [value]="cellText(row, column)"
                                (input)="type(r, c, $any($event.target).value)"
                                (keydown)="key($event, r, c)"
                              />
                            }
                          </td>
                        }
                        <td [attr.data-label]="i18n.t('hr.sheet.source')">
                          <span
                            class="bo-state"
                            [class.bo-state--ok]="row.source !== 'None'"
                            [class.bo-state--warn]="row.source === 'None'"
                          >
                            {{ i18n.t('hr.attendance.source.' + row.source) }}
                          </span>
                          @if (row.readOnly) {
                            <button type="button" class="btn-ghost bo-small" (click)="openDays(row)">
                              {{ i18n.t('hr.sheet.openDays') }}
                            </button>
                          }
                          @if (row.unpairedClockIns > 0) {
                            <span class="bo-state bo-state--warn">
                              {{ i18n.t('hr.sheet.unpairedCount', { count: row.unpairedClockIns }) }}
                            </span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                  <tfoot>
                    <tr>
                      <th>{{ i18n.t('common.total') }}</th>
                      @for (column of columns(); track column.id) {
                        <td class="num mono">{{ total(column) }}</td>
                      }
                      <td></td>
                    </tr>
                  </tfoot>
                </table>
              </div>

              <div class="bo-actions">
                <nav class="pager" [attr.aria-label]="i18n.t('common.pagination')">
                  <button type="button" class="btn-ghost bo-small" [disabled]="skip() === 0" (click)="turn(-1)">
                    &lsaquo; {{ i18n.t('common.prev') }}
                  </button>
                  <span class="pager__range mono" aria-live="polite">{{ range() }}</span>
                  <button
                    type="button"
                    class="btn-ghost bo-small"
                    [disabled]="skip() + take >= s.total"
                    (click)="turn(1)"
                  >
                    {{ i18n.t('common.next') }} &rsaquo;
                  </button>
                </nav>
                <span class="bo-state" [class.bo-state--warn]="unsaved() > 0">
                  {{ unsaved() > 0 ? i18n.t('hr.sheet.unsaved', { count: unsaved() }) : i18n.t('hr.sheet.allSaved') }}
                </span>
                <button type="button" class="btn-ghost" [disabled]="busy() || unsaved() === 0" (click)="discard()">
                  {{ i18n.t('hr.sheet.discard') }}
                </button>
                <button
                  type="button"
                  class="btn-primary"
                  [disabled]="busy() || s.locked || unsaved() === 0"
                  (click)="save()"
                >
                  {{ i18n.t('hr.sheet.save') }}
                </button>
              </div>
              <p class="bo-hint">{{ i18n.t('hr.sheet.keys') }}</p>
            }
          }
          }
        </div>
      </section>
    </div>

    @if (dayGridFor(); as person) {
      <div class="bo-backdrop" (click)="closeDays()">
        <div class="bo-modal" role="dialog" aria-modal="true" (click)="$event.stopPropagation()">
          <app-hr-day-grid
            [employeeId]="person.employeeId"
            [year]="year()"
            [month]="month()"
            (saved)="refreshAfterDays()"
            (closed)="closeDays()"
          />
        </div>
      </div>
    }
  `,
  styleUrls: ['../backoffice-ui.scss', '../../../ui/ui-patterns.scss'],
  styles: [
    `
      .sh-year {
        min-inline-size: 6rem;
        max-inline-size: 7rem;
      }
      .pager {
        display: flex;
        align-items: center;
        gap: 0.5rem;
      }
      .pager__range {
        font-size: 0.82rem;
        color: var(--bo-ink-2);
        font-variant-numeric: tabular-nums;
      }
      .sheet .cell {
        inline-size: 5.5rem;
        text-align: end;
      }
      /* A cell that has been typed into and not yet sent. The counter beside the save button
         says the same thing in words — the tint is the second telling, never the only one. */
      .sheet .cell.is-changed {
        background: var(--warn-soft);
      }
      .sheet .cell.is-wrong {
        border-color: var(--danger);
      }
      /* The name column stays put while the figures scroll: sixty rows of numbers with no name
         against them is how a day's pay lands on the wrong person. */
      .sheet tbody td:first-child,
      .sheet thead th:first-child {
        position: sticky;
        inset-inline-start: 0;
        background: var(--surface);
      }
    `,
  ],
})
export class HrAttendanceSheetComponent implements OnInit {
  private readonly hr = inject(HrApi);
  private readonly admin = inject(AdminService);
  private readonly confirm = inject(ConfirmService);
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);
  readonly i18n = inject(I18nService);
  readonly lock = inject(BranchLock);

  readonly take = 100;

  readonly branches = signal<AdminBranch[]>([]);
  readonly branchId = signal('');
  readonly year = signal(new Date().getFullYear());
  readonly month = signal(new Date().getMonth() + 1);
  /** Next year back to five years ago, and whatever year is open if it lies outside that. */
  readonly years = computed(() => {
    const now = new Date().getFullYear();
    const list = Array.from({ length: 7 }, (_, i) => now + 1 - i);
    return list.includes(this.year()) ? list : [this.year(), ...list].sort((a, b) => b - a);
  });
  readonly query = signal('');
  readonly skip = signal(0);

  readonly sheet = signal<AttendanceSheet | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly dayGridFor = signal<AttendanceSheetRow | null>(null);

  /**
   * Every row the clerk has seen this period, page by page, and every figure typed into one. The
   * buffer outlives the pager on purpose: turning the page to check a name must not throw away
   * forty rows of typing, and one save sends all of it.
   */
  private readonly loaded = new Map<string, AttendanceSheetRow>();
  private readonly buffer = signal(new Map<string, SheetEdit>());
  private readonly texts = signal(new Map<string, string>());
  private readonly problems = signal(new Map<string, FigureProblem>());
  private pendingFocus: CellRef | null = null;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  readonly months = computed(() => monthNames(this.i18n.lang()));
  readonly columns = computed<SheetColumn[]>(() => sheetColumns(this.sheet()?.overtimeRules ?? []));

  readonly shape = computed<SheetShape>(() => {
    const rows = this.sheet()?.rows ?? [];
    return {
      rows: rows.length,
      cols: this.columns().length,
      readOnly: rows.map((row) => row.readOnly || (this.sheet()?.locked ?? false)),
    };
  });

  /** Cells typed into and not yet sent — counted across every page of this period. */
  readonly unsaved = computed(() => {
    const columns = this.columns();
    let count = 0;
    for (const [employeeId, edit] of this.buffer()) {
      const row = this.loaded.get(employeeId);
      if (row) count += changedColumns(edit, row, columns).length;
    }
    return count;
  });

  readonly range = computed(() => {
    const total = this.sheet()?.total ?? 0;
    return this.i18n.t('common.pageRange', {
      from: total === 0 ? 0 : this.skip() + 1,
      to: Math.min(this.skip() + this.take, total),
      total,
    });
  });

  ngOnInit(): void {
    void this.start();
  }

  private async start(): Promise<void> {
    try {
      const branches = await this.admin.listBranches();
      this.branches.set(branches);
      // A non-admin's sheet is their own branch's; the admin starts on his.
      if (this.lock.locked()) this.branchId.set(this.lock.lockedBranchId()!);
      else if (!this.branchId() && branches.length > 0) this.branchId.set(this.lock.defaultBranch(branches) ?? branches[0].id);
    } catch (e) {
      this.error.set(this.message(e));
    }
    await this.load();
  }

  private async load(): Promise<void> {
    if (!this.branchId()) {
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    try {
      const sheet = await this.hr.sheet({
        branchId: this.branchId(),
        year: this.year(),
        month: this.month(),
        skip: this.skip(),
        take: this.take,
        q: this.query().trim() || null,
      });
      this.sheet.set(sheet);
      for (const row of sheet.rows ?? []) this.loaded.set(row.employeeId, row);
      this.error.set(null);
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.loading.set(false);
      if (this.pendingFocus) {
        const cell = this.pendingFocus;
        this.pendingFocus = null;
        setTimeout(() => this.focus(cell));
      }
    }
  }

  // ---------- the period ----------

  setBranch(value: string): void {
    if (value === this.branchId()) return;
    this.branchId.set(value);
    this.forget();
    void this.load();
  }

  setPeriod(year: number, month: number): void {
    if (!Number.isFinite(year) || !Number.isFinite(month)) return;
    this.year.set(year);
    this.month.set(Math.min(Math.max(month, 1), 12));
    this.forget();
    void this.load();
  }

  setQuery(value: string): void {
    this.query.set(value ?? '');
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.skip.set(0);
      void this.load();
    }, 250);
  }

  turn(direction: 1 | -1): void {
    const total = this.sheet()?.total ?? 0;
    const next = Math.max(0, this.skip() + direction * this.take);
    if (direction === 1 && next >= total) return;
    this.skip.set(next);
    void this.load();
  }

  /** A different branch or month is a different sheet; typing from the old one may not follow it. */
  private forget(): void {
    this.skip.set(0);
    this.loaded.clear();
    this.buffer.set(new Map());
    this.texts.set(new Map());
    this.problems.set(new Map());
  }

  // ---------- one cell ----------

  cellDomId(row: number, col: number): string {
    return cellId({ row, col });
  }

  columnLabel(column: SheetColumn): string {
    if (column.kind === 'daysPresent') return this.i18n.t('hr.sheet.daysPresent');
    if (column.kind === 'paidLeaveDays') return this.i18n.t('hr.sheet.paidLeave');
    if (column.kind === 'unpaidAbsenceDays') return this.i18n.t('hr.sheet.unpaidAbsence');
    const rule = (this.sheet()?.overtimeRules ?? []).find((r) => r.key === column.ruleKey);
    const name = this.label(rule?.labels) || (column.ruleKey ?? '');
    return rule ? `${name} (${this.i18n.t('hr.unit.short.' + (rule.unit ?? 'Hours'))})` : name;
  }

  private editOf(row: AttendanceSheetRow): SheetEdit {
    return this.buffer().get(row.employeeId) ?? editFromRow(this.loaded.get(row.employeeId) ?? row);
  }

  /** What a Recorded row shows instead of a box: the figure that was added up for it. */
  display(row: AttendanceSheetRow, column: SheetColumn): string {
    const value = readCell(editFromRow(row), column);
    return value === null ? '—' : String(value);
  }

  cellText(row: AttendanceSheetRow, column: SheetColumn): string {
    const typed = this.texts().get(this.cellKey(row, column));
    if (typed !== undefined) return typed;
    const value = readCell(this.editOf(row), column);
    return value === null ? '' : String(value);
  }

  isChanged(row: AttendanceSheetRow, column: SheetColumn): boolean {
    const stored = this.loaded.get(row.employeeId) ?? row;
    return changedColumns(this.editOf(row), stored, [column]).length > 0;
  }

  problem(row: AttendanceSheetRow, column: SheetColumn): FigureProblem | null {
    return this.problems().get(this.cellKey(row, column)) ?? null;
  }

  type(rowIndex: number, colIndex: number, raw: string): void {
    const row = this.sheet()?.rows?.[rowIndex];
    const column = this.columns()[colIndex];
    if (!row || !column) return;

    this.texts.update((map) => new Map(map).set(this.cellKey(row, column), raw));
    const rule = column.kind === 'overtime' ? (this.sheet()?.overtimeRules ?? []).find((r) => r.key === column.ruleKey) : null;
    const max = column.kind === 'overtime' && rule?.unit !== 'Days' ? undefined : daysInMonth(this.year(), this.month());

    const typed = typeIntoCell(this.editOf(row), column, raw, {
      readOnly: row.readOnly || (this.sheet()?.locked ?? false),
      // A month has as many days as the calendar gives it; a figure above that is shown and
      // marked rather than silently trimmed, because trimming answers a question nobody asked.
      max,
    });

    this.problems.update((map) => {
      const next = new Map(map);
      const key = this.cellKey(row, column);
      if (typed.problem) next.set(key, typed.problem);
      else next.delete(key);
      return next;
    });
    if (typed.changed) this.put(row.employeeId, typed.edit);
  }

  key(event: KeyboardEvent, rowIndex: number, colIndex: number): void {
    const input = event.target as HTMLInputElement | null;
    const move = readMove(event, {
      dir: this.i18n.dir(),
      caretAtStart: input ? input.selectionStart === 0 && input.selectionEnd === 0 : true,
      caretAtEnd: input
        ? input.selectionStart === input.value.length && input.selectionEnd === input.value.length
        : true,
    });
    if (!move) return;

    const row = this.sheet()?.rows?.[rowIndex];
    const column = this.columns()[colIndex];
    if (!row || !column) return;

    if (move === 'restore') {
      event.preventDefault();
      this.restore(row, column);
      return;
    }

    if (move === 'fillDown') {
      event.preventDefault();
      this.fillDown(rowIndex, column);
      return;
    }

    if (move === 'save') {
      event.preventDefault();
      void this.save();
      return;
    }

    if (!isStep(move)) return;
    const result = moveTo({ row: rowIndex, col: colIndex }, move, this.shape());
    if (result.edge === 'bottom' || result.edge === 'end') {
      // The page ran out under the caret. Turning it and landing on the first row of the next
      // one is what the clerk was going to do anyway; the buffer follows them across.
      const total = this.sheet()?.total ?? 0;
      if (this.skip() + this.take < total) {
        event.preventDefault();
        this.pendingFocus = { row: 0, col: result.edge === 'end' ? 0 : colIndex };
        this.turn(1);
        return;
      }
    }
    if (result.edge === 'top' || result.edge === 'start') {
      if (this.skip() > 0) {
        event.preventDefault();
        this.pendingFocus = { row: this.take - 1, col: result.edge === 'start' ? this.columns().length - 1 : colIndex };
        this.turn(-1);
        return;
      }
    }
    // A move that ran out of sheet with no page to turn is left to the browser. Swallowing it
    // held the caret inside the table at both edges — twelve Tabs and twenty Shift+Tabs never got
    // out — so the save button this screen is written around could only be reached with a mouse.
    if (result.edge !== null) return;
    event.preventDefault();
    this.focus(result.to);
  }

  private restore(row: AttendanceSheetRow, column: SheetColumn): void {
    const stored = this.loaded.get(row.employeeId) ?? row;
    this.put(row.employeeId, restoreCell(this.editOf(row), stored, column));
    this.texts.update((map) => {
      const next = new Map(map);
      next.delete(this.cellKey(row, column));
      return next;
    });
    this.problems.update((map) => {
      const next = new Map(map);
      next.delete(this.cellKey(row, column));
      return next;
    });
  }

  private fillDown(rowIndex: number, column: SheetColumn): void {
    const rows = this.sheet()?.rows ?? [];
    const locked = this.sheet()?.locked ?? false;
    const shapeRows = rows.map((row) => ({ readOnly: row.readOnly || locked }));
    const values = rows.map((row) => readCell(this.editOf(row), column));
    const filled = fillColumnDown(shapeRows, values, rowIndex);

    const buffer = new Map(this.buffer());
    const texts = new Map(this.texts());
    rows.forEach((row, index) => {
      if (filled[index] === values[index]) return;
      buffer.set(row.employeeId, writeCell(this.editOf(row), column, filled[index]));
      texts.delete(this.cellKey(row, column));
    });
    this.buffer.set(buffer);
    this.texts.set(texts);
  }

  private put(employeeId: string, edit: SheetEdit): void {
    this.buffer.update((map) => new Map(map).set(employeeId, edit));
  }

  private cellKey(row: AttendanceSheetRow, column: SheetColumn): string {
    return `${row.employeeId}|${column.id}`;
  }

  private focus(cell: CellRef): void {
    const element = this.host.nativeElement.querySelector<HTMLInputElement>(`#${cellId(cell)}`);
    if (!element) return;
    element.focus();
    element.select();
  }

  // ---------- the column's total ----------

  total(column: SheetColumn): string {
    const rows = this.sheet()?.rows ?? [];
    let sum = 0;
    let any = false;
    for (const row of rows) {
      const value = readCell(this.editOf(row), column);
      if (value === null) continue;
      any = true;
      sum += value;
    }
    return any ? String(Math.round(sum * 100) / 100) : '—';
  }

  // ---------- saving ----------

  /** Throws away the unsaved cells, after asking: they were typed one at a time. */
  async discard(): Promise<void> {
    if (this.unsaved() > 0 && !(await this.confirm.ask(discardRequest(this.i18n)))) return;
    this.buffer.set(new Map());
    this.texts.set(new Map());
    this.problems.set(new Map());
  }

  async save(): Promise<void> {
    const sheet = this.sheet();
    if (!sheet || this.unsaved() === 0) return;
    if (!(await this.confirm.ask(saveRequest(this.i18n)))) return;

    const columns = this.columns();
    const rows: UpsertSheetRow[] = [];
    for (const [employeeId, edit] of this.buffer()) {
      const stored = this.loaded.get(employeeId);
      if (!stored || stored.readOnly) continue;
      if (changedColumns(edit, stored, columns).length === 0) continue;
      rows.push(toRowRequest(employeeId, edit));
    }
    if (rows.length === 0) return;

    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      const saved = await this.hr.saveSheet({
        branchId: sheet.branchId,
        periodYear: sheet.periodYear,
        periodMonth: sheet.periodMonth,
        rows,
      });
      // The answer carries exactly the rows that were sent, so it is merged into what is on
      // screen rather than swapped for it: the clerk is looking at a page, not at a response.
      this.merge(saved.rows ?? []);
      this.notice.set(this.i18n.t('hr.sheet.saved', { count: rows.length }));
    } catch (e) {
      this.error.set(this.message(e));
    } finally {
      this.busy.set(false);
    }
  }

  private merge(saved: AttendanceSheetRow[]): void {
    const byId = new Map(saved.map((row) => [row.employeeId, row]));
    for (const row of saved) this.loaded.set(row.employeeId, row);

    const sheet = this.sheet();
    if (sheet) {
      this.sheet.set({
        ...sheet,
        rows: sheet.rows.map((row) => byId.get(row.employeeId) ?? row),
      });
    }

    this.buffer.update((map) => {
      const next = new Map(map);
      for (const id of byId.keys()) next.delete(id);
      return next;
    });
    this.texts.update((map) => {
      const next = new Map(map);
      for (const key of [...next.keys()]) {
        if (byId.has(key.split('|')[0])) next.delete(key);
      }
      return next;
    });
    this.problems.set(new Map());
  }

  // ---------- the day grid ----------

  openDays(row: AttendanceSheetRow): void {
    this.dayGridFor.set(row);
  }

  closeDays(): void {
    this.dayGridFor.set(null);
  }

  /** A recorded day changes the totals this sheet is showing, so the page is read again. */
  refreshAfterDays(): void {
    void this.load();
  }

  // ---------- words ----------

  branchName(branch: AdminBranch): string {
    return localizedName(branch, this.i18n.lang());
  }

  label(labels: TextByLang | undefined): string {
    if (!labels) return '';
    return labels[this.i18n.lang()] ?? labels['fr'] ?? labels['en'] ?? '';
  }

  private message(error: unknown): string {
    const problem = hrProblem(error);
    if (!problem) return moneyErrorMessage(error, (key, params) => this.i18n.t(key, params));
    const key = `hr.problem.${problem.code}`;
    const text = this.i18n.t(key, {
      key: problem.key ?? '',
      run: problem.runNumber ?? '',
      status: problem.runStatus ? this.i18n.t(`hr.run.status.${problem.runStatus}`) : '',
    });
    return text === key ? problem.message || key : text;
  }
}
