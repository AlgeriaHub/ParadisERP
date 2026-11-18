import { OpeningImportPreview, OpeningImportRow } from '../../core/admin.service';
import { ImportLine, blocks, importRequest, issueOf, linesFor, shown, tally } from './opening-import';

/** Rows shaped like the Ouled Moussa count: the serial, the shop (K), out on hire (L), the total. */
function row(r: number, name: string, values: (number | null)[], extra: Partial<OpeningImportRow> = {}): OpeningImportRow {
  return {
    row: r,
    name,
    values,
    match: 'new',
    productId: null,
    sku: null,
    productNameAr: null,
    productNameFr: null,
    productNameEn: null,
    duplicateOfRow: null,
    ...extra,
  };
}

function preview(rows: OpeningImportRow[]): OpeningImportPreview {
  return {
    fileName: 'mosa.xlsx',
    sheetName: '29-09',
    sheetNames: ['29-09'],
    headerRow: 5,
    nameColumn: 'J',
    nameHeader: 'الصنف',
    columns: [
      { index: 9, letter: 'I', header: 'م' },
      { index: 11, letter: 'K', header: 'محل' },
      { index: 12, letter: 'L', header: 'مؤجر' },
      { index: 13, letter: 'M', header: 'اجمالي الفرع' },
    ],
    quantityColumn: 11,
    rows,
  };
}

const SHEET = preview([
  row(6, 'ارجل جالفا', [1, 972, 1897, 2869]),
  row(7, 'تيب 6.00 م', [2, 18, 0, 18], { match: 'existing', productId: 'p-tube', sku: 'TUBE-6' }),
  row(8, 'كولي ثابت', [3, -192, 11, -181]),
  row(9, 'مقص 1,90 م', [4, 1452.5, 2559.5, 4012]),
  row(10, 'بانو 0,24*1,00 م', [5, 0, 0, 0]),
  row(11, 'بانو 0.24* 1.00 م', [6, 2, 0, 2], { duplicateOfRow: 10 }),
  row(12, 'كرو', [7, null, 625, 625]),
  row(13, 'سلم', [8, 1, 0, 1], { match: 'ambiguous' }),
]);

describe('opening import', () => {
  it('reads the chosen column and ticks only what nothing stands in the way of', () => {
    const lines = linesFor(SHEET, 11);

    expect(lines.map((l) => l.qty)).toEqual([972, 18, -192, 1452.5, 0, 2, null, 1]);
    expect(lines.map((l) => issueOf(l))).toEqual([null, null, 'negative', 'fraction', null, 'duplicate', 'noFigure', 'ambiguous']);
    // A fraction loads and is pointed at; a zero loads (it creates the article with nothing in stock).
    expect(lines.map((l) => l.include)).toEqual([true, true, false, true, true, false, false, false]);
  });

  it('switching to the hire column reads its figures afresh', () => {
    const lines = linesFor(SHEET, 12);

    expect(lines[0].qty).toBe(1897);
    expect(lines[2].qty).toBe(11);
    expect(lines[2].include).toBeTrue();
  });

  it('with no column chosen every line lacks a figure and nothing is ticked', () => {
    const lines = linesFor(SHEET, null);

    expect(lines.every((l) => l.qty === null && !l.include)).toBeTrue();
    expect(tally(lines).toLoad).toBe(0);
  });

  it('counts what loads, what is new and what is left out', () => {
    expect(tally(linesFor(SHEET, 11))).toEqual({ toLoad: 4, matched: 1, created: 3, leftOut: 4, blocked: 0 });
  });

  it('a ticked line with a negative or missing figure blocks the load until it is corrected', () => {
    const lines = linesFor(SHEET, 11);
    const negative = lines[2];

    negative.include = true;
    expect(blocks(negative)).toBeTrue();
    expect(tally(lines).blocked).toBe(1);

    negative.qty = 0;
    expect(blocks(negative)).toBeFalse();
    expect(tally(lines).blocked).toBe(0);
  });

  it('a duplicate the person ticks is sent; the server refuses the pair if both go', () => {
    const lines = linesFor(SHEET, 11);
    lines[5].include = true;
    expect(blocks(lines[5])).toBeFalse();
  });

  it('sends the ticked lines, a matched article by its id', () => {
    const request = importRequest(linesFor(SHEET, 11), 'b-om', { note: 'mosa.xlsx — 29-09', newProductsRentable: true, skuPrefix: ' OM- ' });

    expect(request.branchId).toBe('b-om');
    expect(request.skuPrefix).toBe('OM-');
    expect(request.lines).toEqual([
      { name: 'ارجل جالفا', qty: 972, productId: null },
      { name: 'تيب 6.00 م', qty: 18, productId: 'p-tube' },
      { name: 'مقص 1,90 م', qty: 1452.5, productId: null },
      { name: 'بانو 0,24*1,00 م', qty: 0, productId: null },
    ]);
  });

  it('a blank prefix is left to the server default', () => {
    expect(importRequest([], 'b', { note: null, newProductsRentable: false, skuPrefix: '  ' }).skuPrefix).toBeNull();
  });

  it('filters to what needs a look, to new articles, or to matched ones', () => {
    const lines = linesFor(SHEET, 11);
    const rows = (filtered: ImportLine[]) => filtered.map((l) => l.row);

    expect(rows(shown(lines, 'all')).length).toBe(8);
    expect(rows(shown(lines, 'attention'))).toEqual([8, 9, 11, 12, 13]);
    expect(rows(shown(lines, 'existing'))).toEqual([7]);
    expect(rows(shown(lines, 'new'))).toEqual([6, 8, 9, 10, 11, 12]);
  });
});
