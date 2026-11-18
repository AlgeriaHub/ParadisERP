import type { AdminCashBox, PendingPayment } from '../../../core/admin.service';
import { voucherUrl, voucherViewFor } from '../receivables/payment-vouchers-tab.component';
import { receiptUrl, receiptViewFor } from '../receivables/receivables-tab.component';
import type { ReceiptVoucher } from '../receivables/receivables-api.service';
import { awaitingFigures, awaitingRows } from './awaiting';
import { cashViewFor } from './cash-boxes.component';
import { cashBoxesFor, cashFigures, remittanceDefaults, safesElsewhere } from './cash-boxes';

/** Finance (REDESIGN_PLAN.md Phase 4, step 2): the rules the pages are drawn from. */
describe('finance', () => {
  describe('awaiting collection', () => {
    const sale = (id: string, due: string | null, amount: number, status: PendingPayment['status'] = 'Pending') =>
      ({
        id,
        saleId: 's-' + id,
        saleReference: 'V-' + id,
        soldAtUtc: '2026-09-01T10:00:00Z',
        customerId: null,
        customerName: null,
        methodKey: 'cheque',
        amount,
        status,
        instrumentNumber: '00' + id,
        instrumentBank: 'BNA',
        instrumentDueDate: due,
        confirmedAtUtc: null,
        statusNote: null,
      }) as PendingPayment;
    const receipt = (id: string, due: string | null, amount: number, status: ReceiptVoucher['status'] = 'Pending') =>
      ({
        id,
        number: 'RCV-' + id,
        customerId: 'c1',
        customerName: 'Client',
        branchId: 'b-oran',
        receivedAtUtc: '2026-09-02T10:00:00Z',
        methodKey: 'virement',
        status,
        amount,
        instrumentNumber: null,
        instrumentBank: null,
        instrumentDueDate: due,
      }) as ReceiptVoucher;

    it('gathers the till\'s and the receipts\' uncleared paper, earliest due first and undated last', () => {
      const rows = awaitingRows(
        [sale('1', '2026-10-10', 100), sale('2', null, 50), sale('3', '2026-09-01', 70, 'Confirmed')],
        [receipt('9', '2026-10-01', 300), receipt('8', '2026-10-05', 10, 'Bounced')],
      );
      expect(rows.map((r) => `${r.source}:${r.reference}`)).toEqual(['receipt:RCV-9', 'sale:V-1', 'sale:V-2']);
      // A till payment carries no branch here; a receipt keeps its own.
      expect(rows.map((r) => r.branchId)).toEqual(['b-oran', null, null]);
    });

    it('sums what is on paper and what should already have arrived', () => {
      const rows = awaitingRows([sale('1', '2026-09-01', 100), sale('2', '2026-12-01', 50)], [receipt('9', '2026-09-15', 300)]);
      const figures = awaitingFigures(rows, (r) => (r.instrumentDueDate! < '2026-10-03' ? 'overdue' : 'later'));
      expect(figures).toEqual({ total: 450, count: 3, overdueTotal: 400, overdueCount: 2 });
    });
  });

  describe('cash boxes', () => {
    const box = (id: string, branchId: string, isMain: boolean, balance: number) =>
      ({ id, branchId, isMain, balance, nameAr: id, nameFr: id, nameEn: id }) as AdminCashBox;
    // As the server answers Oran: Oran's boxes, and the central safe (Alger's) named with a 0.
    const oranAnswer = [box('central', 'b-alger', true, 0), box('oran', 'b-oran', false, 4000)];
    const everything = [box('central', 'b-alger', true, 90000), box('alger', 'b-alger', false, 1000), box('oran', 'b-oran', false, 4000)];

    it('on every branch, states the company\'s cash, the central safe and what the branches hold', () => {
      expect(cashFigures(cashBoxesFor(everything, null), null)).toEqual({
        scope: 'company',
        total: 95000,
        central: 90000,
        atBranches: 5000,
        boxes: 3,
      });
    });

    it('on one branch, states that branch\'s cash only, never another branch\'s safe as a figure', () => {
      const shown = cashBoxesFor(oranAnswer, 'b-oran');
      expect(shown.map((b) => b.id)).toEqual(['oran']);
      expect(cashFigures(shown, 'b-oran')).toEqual({ scope: 'branch', total: 4000, central: null, boxes: 1 });
      // The safe is named as where takings go, without its 0.
      expect(safesElsewhere(oranAnswer, 'b-oran').map((b) => b.id)).toEqual(['central']);
      expect(safesElsewhere(everything, null)).toEqual([]);
    });

    it('on the branch that holds the safe, states the safe too — it is that branch\'s', () => {
      const shown = cashBoxesFor(everything, 'b-alger');
      expect(cashFigures(shown, 'b-alger')).toEqual({ scope: 'branch', total: 91000, central: 90000, boxes: 2 });
    });

    it('starts a remittance from the working branch\'s till box to the central safe', () => {
      expect(remittanceDefaults(everything, everything, 'b-oran')).toEqual({ from: 'oran', to: 'central' });
      expect(remittanceDefaults(everything, everything, null)).toEqual({ from: 'alger', to: 'central' });
      expect(remittanceDefaults([], [], null)).toEqual({ from: '', to: '' });
    });
  });

  describe('every Finance document is an address', () => {
    it('reads and writes a voucher\'s address', () => {
      expect(voucherViewFor([])).toEqual({ kind: 'list' });
      expect(voucherViewFor(['new'])).toEqual({ kind: 'new' });
      expect(voucherViewFor(['v1'])).toEqual({ kind: 'voucher', id: 'v1' });
      expect(voucherViewFor(['v1', 'nonsense'])).toEqual({ kind: 'list' });
      for (const view of [{ kind: 'list' as const }, { kind: 'new' as const }, { kind: 'voucher' as const, id: 'v1' }]) {
        expect(voucherViewFor(voucherUrl(view).slice(3))).toEqual(view);
      }
    });

    it('reads and writes a receipt\'s address, the bounce form and the new form\'s customer included', () => {
      expect(receiptViewFor([])).toEqual({ kind: 'receipts' });
      expect(receiptViewFor(['r1'], { act: 'bounce' })).toEqual({ kind: 'receipt', id: 'r1', customerId: '', outcome: 'Bounced' });
      expect(receiptViewFor(['r1'])).toEqual({ kind: 'receipt', id: 'r1', customerId: '', outcome: null });
      expect(receiptViewFor(['new'], { customer: 'c1', invoice: 'i1' })).toEqual({ kind: 'newReceipt', customerId: 'c1', invoiceId: 'i1' });
      expect(receiptUrl({ kind: 'receipt', id: 'r1', customerId: 'c1', outcome: 'Bounced' })).toEqual({
        path: ['/backoffice', 'finance', 'receipts', 'r1'],
        query: { act: 'bounce' },
      });
      expect(receiptUrl({ kind: 'newReceipt', customerId: 'c1', invoiceId: null })).toEqual({
        path: ['/backoffice', 'finance', 'receipts', 'new'],
        query: { customer: 'c1' },
      });
      // An invoice or an account has no address on this page yet: it opens in place.
      expect(receiptUrl({ kind: 'invoice', id: 'i1', customerId: 'c1' })).toBeNull();
    });

    it('reads a cash box\'s address', () => {
      expect(cashViewFor([])).toEqual({ kind: 'boxes' });
      expect(cashViewFor(['remit'])).toEqual({ kind: 'remit' });
      expect(cashViewFor(['box-1'])).toEqual({ kind: 'box', id: 'box-1' });
    });
  });
});
