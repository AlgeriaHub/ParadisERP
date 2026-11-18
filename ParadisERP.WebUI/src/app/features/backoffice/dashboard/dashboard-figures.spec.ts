import type { AdminCashBox, LowStockRow, PendingPayment } from '../../../core/admin.service';
import type { DebtAgeing } from '../receivables/receivables-api.service';
import type { PaymentVoucher } from '../receivables/payment-vouchers-api.service';
import { rentalTotals } from '../rentals/rental-totals';
import type { RentalContractRow } from '../rentals/rentals-api.service';
import {
  awaitingCollection,
  cardTone,
  cashBoxesFor,
  customersOwe,
  lowStockCount,
  oweTone,
  shareOf,
  tillTone,
  todayWindow,
  vouchersToApprove,
  waitingTone,
} from './dashboard-figures';

/** The dashboard's figures are counts and sums of rows other pages already show (Appendix A). */
describe('dashboard figures', () => {
  it('asks for today from the reader\'s midnight to the last millisecond of the day', () => {
    const { fromUtc, toUtc } = todayWindow(new Date(2026, 9, 3, 14, 30));
    const from = new Date(fromUtc);
    const to = new Date(toUtc);
    expect([from.getFullYear(), from.getMonth(), from.getDate(), from.getHours(), from.getMinutes()]).toEqual([2026, 9, 3, 0, 0]);
    expect([to.getDate(), to.getHours(), to.getMinutes(), to.getSeconds(), to.getMilliseconds()]).toEqual([3, 23, 59, 59, 999]);
  });

  it('counts only Pending vouchers and sums what they would pay', () => {
    const v = (status: number, amount: number) => ({ status, amount }) as PaymentVoucher;
    expect(vouchersToApprove([v(1, 1000), v(1, 250.5), v(2, 9999), v(4, 5)])).toEqual({ count: 2, amount: 1250.5 });
    expect(vouchersToApprove([])).toEqual({ count: 0, amount: 0 });
  });

  it('counts only payments still awaiting collection', () => {
    const p = (status: PendingPayment['status'], amount: number) => ({ status, amount }) as PendingPayment;
    expect(awaitingCollection([p('Pending', 300), p('Confirmed', 50), p('Pending', 700)])).toEqual({ count: 2, amount: 1000 });
  });

  it('counts low stock only where a reorder level is set, so items a branch never stocks are not counted', () => {
    const row = (reorderLevel: number) => ({ reorderLevel, stockQty: 0 }) as LowStockRow;
    expect(lowStockCount([row(0), row(0), row(5), row(10)])).toBe(2);
  });

  it('puts the central safe first and, on one branch, shows only that branch\'s boxes', () => {
    const box = (id: string, branchId: string, isMain: boolean, balance: number) =>
      ({ id, branchId, isMain, balance, nameFr: id, nameAr: id, nameEn: id }) as AdminCashBox;
    const boxes = [box('oran', 'b-oran', false, 40), box('central', 'b-alger', true, 0), box('alger', 'b-alger', false, 10)];
    expect(cashBoxesFor(boxes, null).map((b) => b.id)).toEqual(['central', 'oran', 'alger']);
    // Oran's manager: the central safe is another branch's, named only with a 0 that is not a balance.
    expect(cashBoxesFor(boxes, 'b-oran').map((b) => b.id)).toEqual(['oran']);
    expect(cashBoxesFor(boxes, 'b-alger').map((b) => b.id)).toEqual(['central', 'alger']);
  });

  it('reads what customers owe and the part past its due date from the ageing buckets', () => {
    const ageing = { total: 1000, current: 400, days1To30: 300, days31To60: 200, days61To90: 0, over90: 100 } as DebtAgeing;
    expect(customersOwe(ageing)).toEqual({ outstanding: 1000, pastDue: 600 });
  });

  it('draws a share bar against the largest row', () => {
    expect(shareOf(50, 200)).toBe(25);
    expect(shareOf(0, 200)).toBe(0);
    expect(shareOf(10, 0)).toBe(0);
  });

  it('sums the rentals exactly as the contract list does, closed contracts left out', () => {
    const row = (status: string, isLocked: boolean, stillOutQty: number, chargeTotal: number, overdueTotal = 0) =>
      ({ status, isLocked, stillOutQty, chargeTotal, overdueTotal }) as unknown as RentalContractRow;
    expect(rentalTotals([row('Open', true, 12, 5000, 300), row('Open', false, 0, 0), row('Closed', true, 3, 900)])).toEqual({
      stillOut: 12,
      charged: 5000,
      overdue: 300,
      drafts: 1,
      openWithGoods: 1,
    });
  });

  // PinSay #300: the cards are coloured by state, and the state is always the one the words say.
  it('colours a card by its state: offline a warning, a failed load critical, loading plain', () => {
    const ready = () => 'success' as const;
    expect(cardTone('offline', ready)).toBe('warning');
    expect(cardTone('error', ready)).toBe('critical');
    expect(cardTone('loading', ready)).toBeNull();
    expect(cardTone('ready', ready)).toBe('success');
  });

  it('warns while something waits and gives the all-clear when nothing does', () => {
    expect(waitingTone(3)).toBe('warning');
    expect(waitingTone(0)).toBe('success');
    expect(oweTone({ outstanding: 900, pastDue: 100 })).toBe('warning');
    expect(oweTone({ outstanding: 900, pastDue: 0 })).toBe('info');
    expect(oweTone({ outstanding: 0, pastDue: 0 })).toBe('success');
    expect(tillTone({ pending: 3, rejected: 1 })).toBe('critical');
    expect(tillTone({ pending: 3, rejected: 0 })).toBe('warning');
    expect(tillTone({ pending: 0, rejected: 0 })).toBe('success');
  });
});
