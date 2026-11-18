import { OpeningImportPreview, OpeningImportRequest, OpeningImportRow } from '../../core/admin.service';

/**
 * The rules of the stock-sheet preview, apart from the screen so they can be pinned.
 *
 * <p>Every line of the sheet is shown, ticked or not, with what stands in its way in words.
 * A line is ticked by default only when nothing does: a negative count, an empty cell, a second
 * spelling of an article already on the sheet, or a name several catalogue articles answer to
 * would each load something nobody meant — so those start unticked and say why, and the person
 * decides. A fraction of a unit is unusual for counted pieces but not wrong, so it loads and is
 * pointed at.</p>
 */

export type ImportIssue = 'ambiguous' | 'duplicate' | 'noFigure' | 'negative' | 'fraction';

export type ImportFilter = 'all' | 'attention' | 'new' | 'existing';

/** A preview row with what the person has made of it. */
export interface ImportLine extends OpeningImportRow {
  include: boolean;
  qty: number | null;
}

/** What stands in a line's way, the most serious first; null when nothing does. */
export function issueOf(line: ImportLine): ImportIssue | null {
  if (line.match === 'ambiguous') return 'ambiguous';
  if (line.duplicateOfRow !== null) return 'duplicate';
  if (!isFigure(line.qty)) return 'noFigure';
  if (line.qty < 0) return 'negative';
  if (!Number.isInteger(line.qty)) return 'fraction';
  return null;
}

/**
 * Whether a ticked line cannot be sent as it is. A duplicate can — the person may have unticked
 * the first spelling — and the server refuses the pair if both go.
 */
export function blocks(line: ImportLine): boolean {
  const issue = issueOf(line);
  return issue === 'ambiguous' || issue === 'noFigure' || issue === 'negative';
}

/** The lines of a preview read from one quantity column, ticked where nothing stands in the way. */
export function linesFor(preview: OpeningImportPreview, column: number | null): ImportLine[] {
  const at = preview.columns.findIndex((c) => c.index === column);
  return preview.rows.map((row) => {
    const line: ImportLine = { ...row, qty: at >= 0 ? row.values[at] : null, include: false };
    const issue = issueOf(line);
    line.include = issue === null || issue === 'fraction';
    return line;
  });
}

export function needsAttention(line: ImportLine): boolean {
  return !line.include || issueOf(line) !== null;
}

export function shown(lines: ImportLine[], filter: ImportFilter): ImportLine[] {
  switch (filter) {
    case 'attention':
      return lines.filter(needsAttention);
    case 'new':
      return lines.filter((l) => l.match === 'new');
    case 'existing':
      return lines.filter((l) => l.match === 'existing');
    default:
      return lines;
  }
}

export interface ImportTally {
  toLoad: number;
  matched: number;
  created: number;
  leftOut: number;
  /** Ticked lines that cannot be sent as they are. */
  blocked: number;
}

export function tally(lines: ImportLine[]): ImportTally {
  const ticked = lines.filter((l) => l.include);
  return {
    toLoad: ticked.length,
    matched: ticked.filter((l) => l.match === 'existing').length,
    created: ticked.filter((l) => l.match === 'new').length,
    leftOut: lines.length - ticked.length,
    blocked: ticked.filter(blocks).length,
  };
}

/** The request for the ticked lines. An article matched on the preview is sent by id, so it cannot drift. */
export function importRequest(
  lines: ImportLine[],
  branchId: string,
  options: { note: string | null; newProductsRentable: boolean; skuPrefix: string },
): OpeningImportRequest {
  return {
    branchId,
    note: options.note,
    newProductsRentable: options.newProductsRentable,
    skuPrefix: options.skuPrefix.trim() || null,
    lines: lines
      .filter((l) => l.include && !blocks(l))
      .map((l) => ({ name: l.name, qty: Number(l.qty), productId: l.match === 'existing' ? l.productId : null })),
  };
}

function isFigure(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}
