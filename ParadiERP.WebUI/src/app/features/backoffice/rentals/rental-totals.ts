import type { RentalContractRow } from './rentals-api.service';

/**
 * The yard at a glance, summed from the rows `GET /api/rentals/contracts` already carries (no new
 * figure): what is out and with how many customers, what has been charged, the overdue charges
 * billed on returns, and the contracts not yet issued. Closed contracts are left out.
 *
 * Shared by the contract list's figures strip and the dashboard's rentals block, so the two can
 * never add the same rows up differently.
 */
export interface RentalTotals {
  stillOut: number;
  charged: number;
  overdue: number;
  drafts: number;
  openWithGoods: number;
}

export function rentalTotals(rows: readonly RentalContractRow[]): RentalTotals {
  let stillOut = 0;
  let charged = 0;
  let overdue = 0;
  let drafts = 0;
  let openWithGoods = 0;
  for (const row of rows) {
    // Only live contracts: a closed one is finished, a cancelled one never started (owner, 2026-10-06).
    if (row.status !== 'Open') continue;
    if (!row.isLocked) drafts++;
    stillOut += row.stillOutQty;
    charged += row.chargeTotal;
    overdue += row.overdueTotal;
    if (row.stillOutQty > 0) openWithGoods++;
  }
  return { stillOut, charged, overdue, drafts, openWithGoods };
}
