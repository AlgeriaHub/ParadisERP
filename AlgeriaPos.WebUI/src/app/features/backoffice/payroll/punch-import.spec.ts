import { ApiError } from '../../../core/api.service';
import { PunchRow } from './hr-api.service';
import { assign, columnLabel, emptyMapping, fileProblemKey, filterRows, mappingProblem, wallClock } from './punch-import';

/** FR-3B: the punch-file preview's rules, without a DOM. */
describe('punch import — mapping the columns', () => {
  it('asks for the number, then for the date and time or one combined column', () => {
    let m = emptyMapping();
    expect(mappingProblem(m)).toBe('mapNumber');
    m = assign(m, 'number', 1);
    expect(mappingProblem(m)).toBe('mapWhen');
    m = assign(m, 'date', 3);
    expect(mappingProblem(m)).toBe('mapWhen');
    m = assign(m, 'time', 4);
    expect(mappingProblem(m)).toBeNull();
  });

  it('gives a column one role: choosing it again takes it off the first', () => {
    const m = assign(assign(emptyMapping(), 'number', 1), 'name', 1);
    expect(m.name).toBe(1);
    expect(m.number).toBeNull();
  });

  it('says the date one way: combined clears date and time, and either of those clears combined', () => {
    let m = assign(assign(assign(emptyMapping(), 'number', 1), 'date', 3), 'time', 4);
    m = assign(m, 'dateTime', 5);
    expect([m.date, m.time, m.dateTime]).toEqual([null, null, 5]);
    expect(mappingProblem(m)).toBeNull();
    m = assign(m, 'time', 4);
    expect([m.date, m.time, m.dateTime]).toEqual([null, 4, null]);
  });

  it('clearing a role touches nothing else', () => {
    const m = assign(assign(assign(emptyMapping(), 'number', 1), 'dateTime', 5), 'dateTime', null);
    expect(m).toEqual({ ...emptyMapping(), number: 1 });
  });

  it('supports check-out column (timeOut)', () => {
    expect(emptyMapping().timeOut).toBeNull();
    let m = assign(assign(emptyMapping(), 'number', 1), 'time', 4);
    m = assign(m, 'timeOut', 4);
    expect(m.timeOut).toBe(4);
    expect(m.time).toBeNull();

    m = assign(m, 'dateTime', 3);
    expect(m.dateTime).toBe(3);
    expect(m.timeOut).toBe(4);
  });

  it('names a column by its letter, its header and what is under it', () => {
    expect(columnLabel({ index: 2, letter: 'B', header: 'Nom', samples: ['Ali', 'Omar'] })).toBe('B — Nom (Ali, Omar)');
    expect(columnLabel({ index: 3, letter: 'C', header: null, samples: [] })).toBe('C');
  });
});

describe('punch import — reading the answer', () => {
  const rows = [
    { row: 2, status: 'matched' },
    { row: 3, status: 'unknown' },
    { row: 4, status: 'invalid' },
    { row: 5, status: 'matched' },
  ] as PunchRow[];

  it('filters the rows by what became of them', () => {
    expect(filterRows(rows, 'all').length).toBe(4);
    expect(filterRows(rows, 'matched').map((r) => r.row)).toEqual([2, 5]);
    expect(filterRows(rows, 'otherBranch')).toEqual([]);
  });

  it('shows a punch as the wall-clock time the file gave, never moved to the device zone', () => {
    expect(wallClock('2026-09-01T07:58:00')).toBe('01/09/2026 07:58:00');
    expect(wallClock('2026-09-01')).toBe('01/09/2026');
    expect(wallClock(null)).toBe('—');
  });

  it('says the reader\'s refusals in words, and leaves other errors to the generic message', () => {
    expect(fileProblemKey(new ApiError('"This file is not an Excel workbook (.xlsx). Open it…"', 400))).toBe('punch.err.notWorkbook');
    expect(fileProblemKey(new ApiError('The workbook is larger than 10 MB.', 400))).toBe('punch.err.tooLarge');
    expect(fileProblemKey(new ApiError('The sheet has more than 60,000 rows. Split it into smaller sheets.', 400))).toBe('punch.err.tooManyRows');
    expect(fileProblemKey(new ApiError('Forbidden', 403))).toBeNull();
    expect(fileProblemKey(new Error('x'))).toBeNull();
  });
});
