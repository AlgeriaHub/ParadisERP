import type { LowStockRow, PendingPayment } from '../../../core/admin.service';
import type { DebtAgeing } from '../receivables/receivables-api.service';
import type { PaymentVoucher } from '../receivables/payment-vouchers-api.service';

/**
 * The dashboard's arithmetic (REDESIGN_PLAN.md Appendix A), pure so `dashboard-figures.spec.ts`
 * pins it. Every figure is a count or a sum of rows an existing endpoint already returns; nothing
 * here asks the server for anything new or decides anything the server decides.
 */

/** A count and the money it stands for. */
export interface CountAmount {
  count: number;
  amount: number;
}

/** Today in the reader's own day, as the sales overview's *Today* asks for it. */
export function todayWindow(now: Date): { fromUtc: string; toUtc: string } {
  const from = new Date(now);
  from.setHours(0, 0, 0, 0);
  const to = new Date(now);
  to.setHours(23, 59, 59, 999);
  return { fromUtc: from.toISOString(), toUtc: to.toISOString() };
}

/** `PaymentVoucherStatus` 1: raised, not yet approved. */
export const VOUCHER_PENDING = 1 as const;

/** Vouchers waiting for a manager's approval. The list is asked for Pending only; the filter is a guard. */
export function vouchersToApprove(rows: readonly PaymentVoucher[]): CountAmount {
  return sum(rows.filter((v) => v.status === VOUCHER_PENDING).map((v) => v.amount));
}

/** Cheques and transfers taken at a till and not yet confirmed or bounced. */
export function awaitingCollection(rows: readonly PendingPayment[]): CountAmount {
  return sum(rows.filter((p) => p.status === 'Pending').map((p) => p.amount));
}

/**
 * Items below a reorder level that is **set**. The low-stock list also returns items whose level
 * is 0 (REDESIGN_PLAN.md §16.3) — a rental item a branch holds none of — and counting those would
 * bury the few that need ordering under every item the branch never stocks.
 */
export function lowStockCount(rows: readonly LowStockRow[]): number {
  return rows.filter((r) => r.reorderLevel > 0).length;
}

/** The same boxes, in the same order, as Finance › Cash boxes shows them (finance/cash-boxes.ts). */
export { cashBoxesFor } from '../finance/cash-boxes';

/** What customers owe in all, and the part of it that is past its due date. */
export function customersOwe(ageing: DebtAgeing): { outstanding: number; pastDue: number } {
  return {
    outstanding: ageing.total,
    pastDue: ageing.days1To30 + ageing.days31To60 + ageing.days61To90 + ageing.over90,
  };
}

/**
 * The ground a card is drawn on (owner, PinSay #300, 2026-10-05: "the cards all the same height
 * and coloured"). Coloured **by state**, never by subject: each tone is a state hue's own tint
 * (`ok-soft`, `warn-soft`…), the same pairs `ui/status-tones.ts` gives a chip, and every one sits
 * beside words the card already says — "none to approve", "needs the connection", the figure itself.
 * `money` is the sales card: `primary-soft` under the `primary-dark` figure, the one pairing the
 * system allows that tint. Null is the plain surface, while the card is still loading.
 */
export type CardTone = 'money' | 'success' | 'info' | 'warning' | 'critical' | 'neutral';

/** The card's tone: offline is the warning it always is, a failed load is critical, a loaded one says what it holds. */
export function cardTone(status: 'loading' | 'ready' | 'error' | 'offline', ready: () => CardTone): CardTone | null {
  if (status === 'offline') return 'warning';
  if (status === 'error') return 'critical';
  return status === 'ready' ? ready() : null;
}

/** Something waiting on somebody is a warning; nothing waiting is the all-clear. */
export function waitingTone(count: number): CardTone {
  return count > 0 ? 'warning' : 'success';
}

/** Past due is a warning; owed but not yet due is notable, not wrong; nothing owed is clear. */
export function oweTone(owe: { outstanding: number; pastDue: number }): CardTone {
  if (owe.pastDue > 0) return 'warning';
  return owe.outstanding > 0 ? 'info' : 'success';
}

/** The till's outbox: a rejected sale is critical, one still to send a warning, an empty outbox clear. */
export function tillTone(stats: { pending: number; rejected: number }): CardTone {
  if (stats.rejected > 0) return 'critical';
  return stats.pending > 0 ? 'warning' : 'success';
}

/** A bar's length against the largest row, not the total: the comparison is row to row. */
export function shareOf(value: number, top: number): number {
  if (!(top > 0) || !(value > 0)) return 0;
  return Math.round((value / top) * 100);
}

function sum(amounts: number[]): CountAmount {
  return { count: amounts.length, amount: amounts.reduce((a, b) => a + b, 0) };
}
