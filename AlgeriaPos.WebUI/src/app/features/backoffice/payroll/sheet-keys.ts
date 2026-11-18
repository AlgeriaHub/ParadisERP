import { AttendanceOvertime, AttendanceSheetRow, UpsertSheetRow } from './hr-api.service';

/**
 * The attendance sheet's keyboard, its figures and its edit buffer — as functions over values,
 * with no DOM and no Angular anywhere in them.
 *
 * <p>The sheet is the screen this module lives or dies on: a foreman with sixty people types
 * sixty rows of figures, and every one of them has to land without reaching for the mouse. Which
 * key moves where, what a half-typed figure means, what Ctrl+D copies and what Esc puts back are
 * therefore rules, not incidents of how the template happened to be written — so they are here,
 * where a test can state them in one line and a second screen can obey the same ones.</p>
 *
 * <p>Two things are deliberately not the browser's defaults:</p>
 * <ul>
 *   <li><b>The arrows mirror.</b> In Arabic the first column is on the right, so <kbd>←</kbd> is
 *   the key that moves on. The physical key is the same one; what it means follows the reading
 *   direction, exactly as the layout does.</li>
 *   <li><b>An arrow inside a figure is still an arrow.</b> Moving a cell only happens when the
 *   caret is already at that end of the text, so correcting the "5" in "1.5" does not jump the
 *   focus to the next column.</li>
 * </ul>
 *
 * <p>What the sheet deliberately does <i>not</i> claim is a step that would not move: a
 * <kbd>Tab</kbd> at the last cell of the last page is left to the browser, so the caret walks on
 * to the pager and the save button instead of being held inside the table. `moveTo` says so by
 * naming the edge it met and handing the same cell back.</p>
 */

// ---------- the columns ----------

export type SheetColumnKind = 'daysPresent' | 'paidLeaveDays' | 'unpaidAbsenceDays' | 'overtime';

export interface SheetColumn {
  /** Stable within a sheet: the three fixed kinds, then `ot:<rule key>`. */
  id: string;
  kind: SheetColumnKind;
  /** Only on an overtime column, and it is the settings' key, never a position. */
  ruleKey?: string;
}

/**
 * The sheet's columns: days present, paid leave, unpaid absence, then one per overtime rule the
 * settings define. How many there are is configuration — a company that defines a fifth band
 * gets a fifth column with no change here.
 */
export function sheetColumns(rules: readonly { key: string }[]): SheetColumn[] {
  const columns: SheetColumn[] = [
    { id: 'daysPresent', kind: 'daysPresent' },
    { id: 'paidLeaveDays', kind: 'paidLeaveDays' },
    { id: 'unpaidAbsenceDays', kind: 'unpaidAbsenceDays' },
  ];
  for (const rule of rules) {
    if (!rule?.key) continue;
    columns.push({ id: `ot:${rule.key}`, kind: 'overtime', ruleKey: rule.key });
  }
  return columns;
}

// ---------- the keys ----------

export type SheetMove = 'down' | 'up' | 'next' | 'prev' | 'fillDown' | 'restore' | 'save';
export type SheetStep = 'down' | 'up' | 'next' | 'prev';

/** Everything of a `KeyboardEvent` these rules read. A plain object in a test is the whole point. */
export interface KeyPress {
  key: string;
  ctrlKey?: boolean;
  metaKey?: boolean;
  shiftKey?: boolean;
  altKey?: boolean;
}

/**
 * Where the caret is and which way the sheet reads. Left unsaid, the caret counts as being at
 * both ends — which is what an empty cell is, and what a caller with no caret to report means.
 */
export interface KeyContext {
  dir?: 'ltr' | 'rtl';
  caretAtStart?: boolean;
  caretAtEnd?: boolean;
}

export function isStep(move: SheetMove | null): move is SheetStep {
  return move === 'down' || move === 'up' || move === 'next' || move === 'prev';
}

/** What this keystroke means in the sheet, or null to leave it to the browser. */
export function readMove(press: KeyPress, context: KeyContext = {}): SheetMove | null {
  if (press.altKey) return null;

  const held = press.ctrlKey === true || press.metaKey === true;
  if (held) {
    // The two chords the sheet claims. Everything else with Ctrl held — copy, paste, the
    // browser's own find — is none of its business.
    if (press.key === 'd' || press.key === 'D') return 'fillDown';
    // Save without leaving the grid. The sheet is typed by a foreman with sixty rows in front of
    // him, and the alternative was tabbing out of a table that swallowed Tab at both edges.
    if (press.key === 's' || press.key === 'S') return 'save';
    return null;
  }

  const rtl = context.dir === 'rtl';
  const atStart = context.caretAtStart ?? true;
  const atEnd = context.caretAtEnd ?? true;

  switch (press.key) {
    case 'Enter':
    case 'ArrowDown':
      return 'down';
    case 'ArrowUp':
      return 'up';
    case 'Tab':
      return press.shiftKey ? 'prev' : 'next';
    case 'Escape':
      return 'restore';
    case 'ArrowRight':
      // The near edge of the text in this direction: at it, the key moves on; inside the
      // figure it is still the caret's key.
      return rtl ? (atStart ? 'prev' : null) : atEnd ? 'next' : null;
    case 'ArrowLeft':
      return rtl ? (atEnd ? 'next' : null) : atStart ? 'prev' : null;
    default:
      return null;
  }
}

// ---------- moving ----------

export interface CellRef {
  row: number;
  col: number;
}

/** The page being typed into: its size, and which of its rows may not be typed into at all. */
export interface SheetShape {
  rows: number;
  cols: number;
  /** One flag per row. A Recorded row is read-only: its figures were added up, not typed. */
  readOnly: readonly boolean[];
}

/** Which way the move ran out of sheet. The screen turns the page on `bottom` or `end`. */
export type SheetEdge = 'top' | 'bottom' | 'start' | 'end';

export interface MoveResult {
  to: CellRef;
  /** Null when the move landed somewhere; the edge it met when it did not. */
  edge: SheetEdge | null;
}

/**
 * Where a step lands.
 *
 * <p>Read-only rows are stepped over rather than stopped on. A step that runs out of page stays
 * where it was and names the edge it met — the caller turns the page and puts the caret on its
 * first row, which is the one thing this function cannot know, since the next page has not been
 * fetched yet.</p>
 */
export function moveTo(from: CellRef, move: SheetStep, shape: SheetShape): MoveResult {
  const at = clamp(from, shape);
  if (shape.rows <= 0 || shape.cols <= 0) return { to: at, edge: null };

  if (move === 'down' || move === 'up') {
    const row = nextEditableRow(at.row, move === 'down' ? 1 : -1, shape);
    return row === null
      ? { to: at, edge: move === 'down' ? 'bottom' : 'top' }
      : { to: { row, col: at.col }, edge: null };
  }

  if (move === 'next') {
    if (at.col < shape.cols - 1) return { to: { row: at.row, col: at.col + 1 }, edge: null };
    // Past the last column the reading order continues on the next row, the way a sheet of
    // figures is read aloud. The alternative — stopping dead at the right-hand edge — makes the
    // clerk reach for the mouse once per row, which is the thing this screen exists to avoid.
    const row = nextEditableRow(at.row, 1, shape);
    return row === null ? { to: at, edge: 'end' } : { to: { row, col: 0 }, edge: null };
  }

  if (at.col > 0) return { to: { row: at.row, col: at.col - 1 }, edge: null };
  const row = nextEditableRow(at.row, -1, shape);
  return row === null ? { to: at, edge: 'start' } : { to: { row, col: shape.cols - 1 }, edge: null };
}

/** The first row that may be typed into, or null when the page has none. */
export function firstEditableRow(shape: SheetShape): number | null {
  for (let row = 0; row < shape.rows; row++) if (!shape.readOnly[row]) return row;
  return null;
}

/** The last one, for a page turned backwards. */
export function lastEditableRow(shape: SheetShape): number | null {
  for (let row = shape.rows - 1; row >= 0; row--) if (!shape.readOnly[row]) return row;
  return null;
}

/** The DOM id of a cell. One spelling, so the template and the focus call cannot drift apart. */
export function cellId(cell: CellRef): string {
  return `att-cell-${cell.row}-${cell.col}`;
}

function clamp(cell: CellRef, shape: SheetShape): CellRef {
  return {
    row: Math.min(Math.max(cell.row, 0), Math.max(shape.rows - 1, 0)),
    col: Math.min(Math.max(cell.col, 0), Math.max(shape.cols - 1, 0)),
  };
}

function nextEditableRow(from: number, step: 1 | -1, shape: SheetShape): number | null {
  for (let row = from + step; row >= 0 && row < shape.rows; row += step) {
    if (!shape.readOnly[row]) return row;
  }
  return null;
}

// ---------- the figures ----------

export type FigureProblem = 'notANumber' | 'negative' | 'tooLarge';

export interface Figure {
  /** What was typed, with the digits and the separator folded to the shape a number parses from. */
  text: string;
  /** The figure so far; null when there is not one yet. */
  value: number | null;
  /** True for "1." or "1," — a figure being typed, not a refusal. */
  partial: boolean;
  problem: FigureProblem | null;
}

const ARABIC_DIGITS = '٠١٢٣٤٥٦٧٨٩';
const PERSIAN_DIGITS = '۰۱۲۳۴۵۶۷۸۹';

/**
 * One typed cell.
 *
 * <p>Arabic-Indic digits and the Arabic decimal separator are read as the figures they are: the
 * foreman typing this sheet is as likely to be on an Arabic keyboard as a French one, and a
 * screen that answered "not a number" to ٥ would be telling him his own digits are wrong.</p>
 *
 * <p>A half-typed figure is never refused. "1," is one, still being written; the cell keeps what
 * it has and says nothing, because a red mark that appears between the "1" and the "5" teaches
 * the typist to distrust a message he is about to make untrue.</p>
 */
export function parseFigure(text: string, max?: number): Figure {
  const folded = foldFigure(text);
  if (!folded) return { text: folded, value: null, partial: false, problem: null };

  if (folded === '.' || folded === '-') return { text: folded, value: null, partial: true, problem: null };
  if (folded.startsWith('-')) return { text: folded, value: null, partial: false, problem: 'negative' };
  if (!/^\d*\.?\d*$/.test(folded)) return { text: folded, value: null, partial: false, problem: 'notANumber' };

  const partial = folded.endsWith('.');
  const value = Number(partial ? folded.slice(0, -1) : folded);
  if (!Number.isFinite(value)) return { text: folded, value: null, partial, problem: 'notANumber' };
  if (max !== undefined && value > max) return { text: folded, value, partial, problem: 'tooLarge' };
  return { text: folded, value, partial, problem: null };
}

/** Digits to Latin, both decimal commas to a point, spaces out. Nothing else is touched. */
export function foldFigure(text: string): string {
  let folded = '';
  for (const char of (text ?? '').trim()) {
    if (char === ' ' || char === ' ' || char === ' ') continue;
    if (char === ',' || char === '٫') {
      folded += '.';
      continue;
    }
    const arabic = ARABIC_DIGITS.indexOf(char);
    if (arabic >= 0) {
      folded += String(arabic);
      continue;
    }
    const persian = PERSIAN_DIGITS.indexOf(char);
    if (persian >= 0) {
      folded += String(persian);
      continue;
    }
    folded += char;
  }
  return folded;
}

// ---------- the edit buffer ----------

/**
 * One row's figures as they are being typed. Null is "nothing in the cell", which is not zero
 * until the row is saved — the sheet's own distinction between a month nobody has entered and a
 * month entered as zero days.
 */
export interface SheetEdit {
  daysPresent: number | null;
  paidLeaveDays: number | null;
  unpaidAbsenceDays: number | null;
  /** Overtime rule key to hours. A key absent from here is a cell nobody has typed in. */
  overtime: Record<string, number | null>;
}

/** What the server last said about this person's month, as an edit to start from. */
export function editFromRow(row: AttendanceSheetRow): SheetEdit {
  const overtime: Record<string, number | null> = {};
  for (const entry of row.overtime ?? []) overtime[entry.ruleKey] = entry.hours;
  return {
    // A row nobody has captured comes back as zeros with source "None"; showing those zeros as
    // typed figures would claim the month was entered. Empty cells say what is true.
    daysPresent: row.source === 'None' ? null : row.daysPresent,
    paidLeaveDays: row.source === 'None' ? null : row.paidLeaveDays,
    unpaidAbsenceDays: row.source === 'None' ? null : row.unpaidAbsenceDays,
    overtime,
  };
}

export function readCell(edit: SheetEdit, column: SheetColumn): number | null {
  if (column.kind === 'overtime') return edit.overtime[column.ruleKey ?? ''] ?? null;
  return edit[column.kind];
}

export function writeCell(edit: SheetEdit, column: SheetColumn, value: number | null): SheetEdit {
  if (column.kind === 'overtime') {
    return { ...edit, overtime: { ...edit.overtime, [column.ruleKey ?? '']: value } };
  }
  return { ...edit, [column.kind]: value };
}

export interface TypedCell {
  edit: SheetEdit;
  /** False when nothing moved — a read-only row, or a keystroke that changed no figure. */
  changed: boolean;
  problem: FigureProblem | null;
}

/**
 * A figure typed into a cell.
 *
 * <p>A Recorded row is ignored outright: those figures were added up from the day grid and the
 * clock, and a sheet that let them be typed over would hold two answers to one question with
 * nothing to say which the run should read.</p>
 */
export function typeIntoCell(
  edit: SheetEdit,
  column: SheetColumn,
  text: string,
  options: { readOnly?: boolean; max?: number } = {},
): TypedCell {
  if (options.readOnly) return { edit, changed: false, problem: null };

  const figure = parseFigure(text, options.max);
  if (figure.problem === 'notANumber' || figure.problem === 'negative') {
    return { edit, changed: false, problem: figure.problem };
  }
  return { edit: writeCell(edit, column, figure.value), changed: true, problem: figure.problem };
}

/** Esc: this one cell back to what the server last said, leaving the rest of the row alone. */
export function restoreCell(edit: SheetEdit, row: AttendanceSheetRow, column: SheetColumn): SheetEdit {
  return writeCell(edit, column, readCell(editFromRow(row), column));
}

/** Which columns of this row no longer say what the server said. Empty means the row is clean. */
export function changedColumns(
  edit: SheetEdit,
  row: AttendanceSheetRow,
  columns: readonly SheetColumn[],
): string[] {
  const stored = editFromRow(row);
  return columns.filter((column) => !sameFigure(readCell(edit, column), readCell(stored, column))).map((c) => c.id);
}

export function isDirty(edit: SheetEdit, row: AttendanceSheetRow, columns: readonly SheetColumn[]): boolean {
  return changedColumns(edit, row, columns).length > 0;
}

/**
 * Ctrl+D down a column.
 *
 * <p>Read-only rows keep their own figures, and a fill started on one does nothing at all: the
 * cell it would copy is not a typed figure in the first place. The row it starts on is left
 * exactly as it is — the fill is downwards, which is what the key says.</p>
 */
export function fillColumnDown(
  rows: readonly { readOnly: boolean }[],
  values: readonly (number | null)[],
  fromRow: number,
): (number | null)[] {
  const copy = values.slice();
  if (fromRow < 0 || fromRow >= rows.length || rows[fromRow]?.readOnly) return copy;
  const value = values[fromRow] ?? null;
  for (let row = fromRow + 1; row < rows.length; row++) {
    if (rows[row]?.readOnly) continue;
    copy[row] = value;
  }
  return copy;
}

/**
 * One row for the PUT. An empty cell is sent as zero — by saving the row the clerk is declaring
 * the month, and a declared month of no days is a figure the run has to be able to read — while
 * a row nobody touched is never in this list at all.
 */
export function toRowRequest(employeeId: string, edit: SheetEdit): UpsertSheetRow {
  const overtime: AttendanceOvertime[] = [];
  for (const [ruleKey, hours] of Object.entries(edit.overtime)) {
    if (!ruleKey) continue;
    overtime.push({ ruleKey, hours: hours ?? 0 });
  }
  return {
    employeeId,
    daysPresent: edit.daysPresent ?? 0,
    paidLeaveDays: edit.paidLeaveDays ?? 0,
    unpaidAbsenceDays: edit.unpaidAbsenceDays ?? 0,
    overtime,
  };
}

// ---------- the month ----------

/** How many days the month has, in the calendar — the sheet's own cap on a day figure. */
export function daysInMonth(year: number, month: number): number {
  if (!Number.isFinite(year) || !Number.isFinite(month) || month < 1 || month > 12) return 31;
  return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

/**
 * The month's dates as `YYYY-MM-DD`, built from the two integers rather than from a `Date` the
 * device's own clock would shift: a period is a calendar month in the company's time zone, and a
 * grid drawn from local midnight loses or repeats a day twice a year.
 */
export function monthDays(year: number, month: number): string[] {
  const days: string[] = [];
  for (let day = 1; day <= daysInMonth(year, month); day++) {
    days.push(`${year}-${pad(month)}-${pad(day)}`);
  }
  return days;
}

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

function sameFigure(a: number | null, b: number | null): boolean {
  if (a === null || b === null) return a === b;
  return Math.abs(a - b) < 1e-9;
}
