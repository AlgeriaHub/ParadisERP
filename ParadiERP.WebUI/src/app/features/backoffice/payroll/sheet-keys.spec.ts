import { AttendanceSheetRow } from './hr-api.service';
import {
  SheetShape,
  cellId,
  changedColumns,
  daysInMonth,
  editFromRow,
  fillColumnDown,
  isDirty,
  monthDays,
  moveTo,
  parseFigure,
  isStep,
  readCell,
  readMove,
  restoreCell,
  sheetColumns,
  toRowRequest,
  typeIntoCell,
} from './sheet-keys';

/** The sheet the foreman sees: three fixed columns and two overtime bands from the settings. */
const columns = sheetColumns([{ key: 'night' }, { key: 'holiday' }]);

const row = (over: Partial<AttendanceSheetRow> = {}): AttendanceSheetRow => ({
  employeeId: 'e-1',
  employeeNumber: 'EMP-000001',
  employeeName: 'Karim Ben Ali',
  payBasis: 'Daily',
  mode: 'Declared',
  readOnly: false,
  daysPresent: 22,
  paidLeaveDays: 1,
  unpaidAbsenceDays: 0,
  overtime: [{ ruleKey: 'night', hours: 4 }],
  source: 'Declared',
  unpairedClockIns: 0,
  ...over,
});

/** Four rows, the third of them a Recorded one that may not be typed into. */
const shape = (rows = 4, cols = columns.length): SheetShape => ({
  rows,
  cols,
  readOnly: Array.from({ length: rows }, (_, i) => i === 2),
});

describe('sheetColumns', () => {
  it('puts the three fixed columns first and one per overtime rule after them', () => {
    expect(columns.map((c) => c.id)).toEqual([
      'daysPresent',
      'paidLeaveDays',
      'unpaidAbsenceDays',
      'ot:night',
      'ot:holiday',
    ]);
    expect(columns[3].ruleKey).toBe('night');
  });

  it('draws as many overtime columns as the settings define, never four', () => {
    expect(sheetColumns([]).length).toBe(3);
    expect(sheetColumns([{ key: 'a' }, { key: 'b' }, { key: 'c' }, { key: 'd' }, { key: 'e' }]).length).toBe(8);
  });
});

describe('readMove', () => {
  it('reads Enter and the down arrow as the same move', () => {
    expect(readMove({ key: 'Enter' })).toBe('down');
    expect(readMove({ key: 'ArrowDown' })).toBe('down');
    expect(readMove({ key: 'ArrowUp' })).toBe('up');
  });

  it('reads Tab forwards and Shift+Tab back', () => {
    expect(readMove({ key: 'Tab' })).toBe('next');
    expect(readMove({ key: 'Tab', shiftKey: true })).toBe('prev');
  });

  it('mirrors the arrows in Arabic: the key that moves on is the one pointing at the next column', () => {
    expect(readMove({ key: 'ArrowRight' }, { dir: 'ltr' })).toBe('next');
    expect(readMove({ key: 'ArrowRight' }, { dir: 'rtl' })).toBe('prev');
    expect(readMove({ key: 'ArrowLeft' }, { dir: 'rtl' })).toBe('next');
  });

  it('leaves an arrow inside a figure to the caret', () => {
    expect(readMove({ key: 'ArrowRight' }, { dir: 'ltr', caretAtEnd: false })).toBeNull();
    expect(readMove({ key: 'ArrowLeft' }, { dir: 'ltr', caretAtStart: false })).toBeNull();
    expect(readMove({ key: 'ArrowRight' }, { dir: 'ltr', caretAtEnd: true })).toBe('next');
  });

  it('claims Ctrl+D and Ctrl+S and nothing else that is held down', () => {
    expect(readMove({ key: 'd', ctrlKey: true })).toBe('fillDown');
    expect(readMove({ key: 'D', metaKey: true })).toBe('fillDown');
    // The sheet has to be savable without leaving the grid: it swallowed Tab at both edges, so
    // "Enregistrer la feuille" could only be reached with a mouse.
    expect(readMove({ key: 's', ctrlKey: true })).toBe('save');
    expect(readMove({ key: 'S', metaKey: true })).toBe('save');
    expect(readMove({ key: 'c', ctrlKey: true })).toBeNull();
    expect(readMove({ key: 'Enter', ctrlKey: true })).toBeNull();
  });

  it('does not treat the save chord as a step', () => {
    // isStep guards the focus move; a chord that slipped through it would move the caret instead
    // of saving.
    expect(isStep('save')).toBe(false);
    expect(isStep('next')).toBe(true);
  });

  it('reads Esc as a restore and leaves everything else to the browser', () => {
    expect(readMove({ key: 'Escape' })).toBe('restore');
    expect(readMove({ key: '5' })).toBeNull();
    expect(readMove({ key: 'F5', altKey: true })).toBeNull();
  });
});

describe('moveTo', () => {
  it('Enter moves down the same column and stops at the page boundary rather than wrapping', () => {
    expect(moveTo({ row: 0, col: 1 }, 'down', shape())).toEqual({ to: { row: 1, col: 1 }, edge: null });
    // The last row of the page: the caret stays where it is and the screen is told which edge it
    // met, because the next page has not been fetched and this function cannot invent its rows.
    expect(moveTo({ row: 3, col: 1 }, 'down', shape())).toEqual({ to: { row: 3, col: 1 }, edge: 'bottom' });
    expect(moveTo({ row: 0, col: 1 }, 'up', shape())).toEqual({ to: { row: 0, col: 1 }, edge: 'top' });
  });

  it('steps over a Recorded row instead of stopping on it', () => {
    expect(moveTo({ row: 1, col: 0 }, 'down', shape())).toEqual({ to: { row: 3, col: 0 }, edge: null });
    expect(moveTo({ row: 3, col: 0 }, 'up', shape())).toEqual({ to: { row: 1, col: 0 }, edge: null });
  });

  it('carries on to the next row past the last column, and says so at the very end', () => {
    expect(moveTo({ row: 0, col: 4 }, 'next', shape())).toEqual({ to: { row: 1, col: 0 }, edge: null });
    expect(moveTo({ row: 3, col: 4 }, 'next', shape())).toEqual({ to: { row: 3, col: 4 }, edge: 'end' });
    // An edge with the same cell back is the screen's signal to stop claiming the keystroke: with
    // no further page the Tab falls through to the browser and the caret reaches the pager and
    // the save button, instead of being held in the table for ever.
    expect(moveTo({ row: 3, col: 4 }, 'next', shape()).to).toEqual({ row: 3, col: 4 });
    expect(moveTo({ row: 1, col: 0 }, 'prev', shape())).toEqual({ to: { row: 0, col: 4 }, edge: null });
    expect(moveTo({ row: 0, col: 0 }, 'prev', shape())).toEqual({ to: { row: 0, col: 0 }, edge: 'start' });
  });

  it('holds a cell reference inside the page it was given', () => {
    expect(moveTo({ row: 99, col: 99 }, 'down', shape())).toEqual({ to: { row: 3, col: 4 }, edge: 'bottom' });
  });

  it('names a cell the same way every time it is asked', () => {
    expect(cellId({ row: 2, col: 3 })).toBe('att-cell-2-3');
  });
});

describe('parseFigure', () => {
  it('reads a plain figure and an empty cell', () => {
    expect(parseFigure('22').value).toBe(22);
    expect(parseFigure('  ').value).toBeNull();
    expect(parseFigure('').problem).toBeNull();
  });

  it('takes a comma for a decimal point, on either keyboard', () => {
    expect(parseFigure('1,5').value).toBe(1.5);
    expect(parseFigure('1٫5').value).toBe(1.5);
    expect(parseFigure('1.5').value).toBe(1.5);
  });

  it('reads Arabic-Indic digits as the digits they are', () => {
    expect(parseFigure('٢٢').value).toBe(22);
    expect(parseFigure('۲۲').value).toBe(22);
  });

  it('treats a figure still being typed as partial, never as a refusal', () => {
    expect(parseFigure('1,')).toEqual({ text: '1.', value: 1, partial: true, problem: null });
    expect(parseFigure('.')).toEqual({ text: '.', value: null, partial: true, problem: null });
  });

  it('names what is wrong with a figure that cannot stand', () => {
    expect(parseFigure('abc').problem).toBe('notANumber');
    expect(parseFigure('-3').problem).toBe('negative');
    expect(parseFigure('35', 31).problem).toBe('tooLarge');
    expect(parseFigure('35', 31).value).toBe(35);
  });
});

describe('the edit buffer', () => {
  it('starts from what the server said, and leaves a month nobody captured empty', () => {
    const edit = editFromRow(row());
    expect(edit.daysPresent).toBe(22);
    expect(edit.overtime['night']).toBe(4);

    const untouched = editFromRow(row({ source: 'None', daysPresent: 0, paidLeaveDays: 0, overtime: [] }));
    expect(untouched.daysPresent).toBeNull();
    expect(untouched.paidLeaveDays).toBeNull();
  });

  it('ignores typing into a read-only Recorded row', () => {
    const recorded = row({ mode: 'Recorded', readOnly: true, source: 'Recorded' });
    const typed = typeIntoCell(editFromRow(recorded), columns[0], '9', { readOnly: true });
    expect(typed.changed).toBeFalse();
    expect(readCell(typed.edit, columns[0])).toBe(22);
  });

  it('keeps the old figure when what was typed is not one', () => {
    const typed = typeIntoCell(editFromRow(row()), columns[0], 'x');
    expect(typed.changed).toBeFalse();
    expect(typed.problem).toBe('notANumber');
    expect(readCell(typed.edit, columns[0])).toBe(22);
  });

  it('accepts a figure over the month and says it is over, rather than dropping it', () => {
    const typed = typeIntoCell(editFromRow(row()), columns[0], '35', { max: 31 });
    expect(typed.changed).toBeTrue();
    expect(typed.problem).toBe('tooLarge');
    expect(readCell(typed.edit, columns[0])).toBe(35);
  });

  it('marks exactly the cells that no longer say what the server said', () => {
    const stored = row();
    const edit = typeIntoCell(editFromRow(stored), columns[1], '3').edit;
    expect(changedColumns(edit, stored, columns)).toEqual(['paidLeaveDays']);
    expect(isDirty(edit, stored, columns)).toBeTrue();
  });

  it('Esc restores the cell and clears its dirty mark, leaving the rest of the row typed', () => {
    const stored = row();
    const typed = typeIntoCell(typeIntoCell(editFromRow(stored), columns[0], '19').edit, columns[3], '7').edit;
    expect(changedColumns(typed, stored, columns)).toEqual(['daysPresent', 'ot:night']);

    const restored = restoreCell(typed, stored, columns[0]);
    expect(readCell(restored, columns[0])).toBe(22);
    expect(changedColumns(restored, stored, columns)).toEqual(['ot:night']);
  });

  it('sends an empty cell as a declared zero, and the overtime keys beside it', () => {
    const edit = typeIntoCell(editFromRow(row({ source: 'None', daysPresent: 0, overtime: [] })), columns[0], '20').edit;
    expect(toRowRequest('e-1', edit)).toEqual({
      employeeId: 'e-1',
      daysPresent: 20,
      paidLeaveDays: 0,
      unpaidAbsenceDays: 0,
      overtime: [],
    });
  });
});

describe('fillColumnDown', () => {
  it('Ctrl+D fills the column downwards from the current cell', () => {
    const rows = [{ readOnly: false }, { readOnly: false }, { readOnly: true }, { readOnly: false }];
    const filled = fillColumnDown(rows, [26, null, 18, null], 0);
    expect(filled).toEqual([26, 26, 18, 26]);
  });

  it('leaves the rows above it, and the Recorded rows, exactly as they were', () => {
    const rows = [{ readOnly: false }, { readOnly: false }, { readOnly: true }, { readOnly: false }];
    expect(fillColumnDown(rows, [26, 24, 18, 12], 1)).toEqual([26, 24, 18, 24]);
  });

  it('does nothing when it starts on a row that may not be typed into', () => {
    const rows = [{ readOnly: false }, { readOnly: true }, { readOnly: false }];
    expect(fillColumnDown(rows, [26, 18, 12], 1)).toEqual([26, 18, 12]);
  });
});

describe('the month', () => {
  it('counts the days the calendar has, February and all', () => {
    expect(daysInMonth(2026, 2)).toBe(28);
    expect(daysInMonth(2028, 2)).toBe(29);
    expect(daysInMonth(2026, 3)).toBe(31);
    expect(daysInMonth(2026, 4)).toBe(30);
  });

  it('builds the dates from the two integers, not from the device clock', () => {
    const days = monthDays(2026, 3);
    expect(days.length).toBe(31);
    expect(days[0]).toBe('2026-03-01');
    expect(days[30]).toBe('2026-03-31');
  });
});
