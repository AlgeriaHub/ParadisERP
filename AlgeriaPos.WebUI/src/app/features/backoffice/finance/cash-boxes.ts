import type { AdminCashBox } from '../../../core/admin.service';

/**
 * Which cash boxes a scope shows, and which figures it may state (Phase 4, Finance › Cash boxes).
 * Pure, so the rule that keeps a company total from being read as one branch's is pinned by
 * `cash-boxes.spec.ts`.
 */

/**
 * The boxes to show and in what order: the central safe first, then by name as the server sent
 * them. Narrowed to one branch, only that branch's boxes are kept: the server also names the main
 * boxes of other branches as places to remit to, with a balance of 0 that is not theirs to read,
 * and a 0 shown as a balance would be read as an empty safe.
 */
export function cashBoxesFor(boxes: readonly AdminCashBox[], branchId: string | null): AdminCashBox[] {
  const kept = branchId ? boxes.filter((b) => b.branchId === branchId) : [...boxes];
  return kept.sort((a, b) => Number(b.isMain) - Number(a.isMain));
}

/** The safes another branch holds: named as where takings go, never with a balance. */
export function safesElsewhere(boxes: readonly AdminCashBox[], branchId: string | null): AdminCashBox[] {
  return branchId ? boxes.filter((b) => b.isMain && b.branchId !== branchId) : [];
}

/**
 * The figures above the table.
 *
 * - **Every branch** (the Admin): the company's cash, the central safe, and what the branches still
 *   hold — the figure a collection round is planned against.
 * - **One branch**: that branch's cash only, and the safe only when the safe is that branch's. No
 *   company total, so nothing on the page reads as one branch's when it is the company's.
 */
export type CashFigures =
  | { scope: 'company'; total: number; central: number; atBranches: number; boxes: number }
  | { scope: 'branch'; total: number; central: number | null; boxes: number };

export function cashFigures(shown: readonly AdminCashBox[], branchId: string | null): CashFigures {
  const total = shown.reduce((sum, b) => sum + b.balance, 0);
  const mains = shown.filter((b) => b.isMain);
  const central = mains.reduce((sum, b) => sum + b.balance, 0);
  if (branchId === null) return { scope: 'company', total, central, atBranches: total - central, boxes: shown.length };
  return { scope: 'branch', total, central: mains.length ? central : null, boxes: shown.length };
}

/**
 * Where a remittance starts: the working branch's own till box when it names one, else the first
 * box this reader holds that is not a safe, else any. It goes to the central safe.
 */
export function remittanceDefaults(
  sources: readonly AdminCashBox[],
  destinations: readonly AdminCashBox[],
  branchId: string | null,
): { from: string; to: string } {
  const branchBoxes = branchId ? sources.filter((b) => b.branchId === branchId) : [];
  const from =
    branchBoxes.find((b) => !b.isMain) ?? sources.find((b) => !b.isMain) ?? branchBoxes[0] ?? sources[0] ?? null;
  const main = destinations.find((b) => b.isMain) ?? destinations[0] ?? null;
  return { from: from?.id ?? '', to: main?.id ?? '' };
}
