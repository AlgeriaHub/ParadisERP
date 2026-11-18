import type { PendingPayment } from '../../../core/admin.service';
import type { DueWord } from '../receivables/receivable-view';
import type { ReceiptVoucher } from '../receivables/receivables-api.service';

/**
 * Finance › Awaiting collection, as one list (Phase 4): every cheque and transfer still in hand,
 * whichever door the money came in by.
 *
 * - **A till sale** paid by cheque or transfer (`GET /api/treasury/pending-payments`): settled
 *   from here, as it always was (`POST …/pending-payments/{id}/settle`).
 * - **A receipt** taken against a customer's account (`GET /api/receivables/receipts?status=Pending`):
 *   settled by the receipt's own acts — collected with a confirmation, bounced on the receipt with
 *   the bank's reason (`POST /api/receivables/receipts/{id}/settle`).
 *
 * Nothing is merged on the server and no rule moves: the two lists are read as they are and only
 * drawn together, each row keeping the source it is settled through.
 */
export type AwaitingSource = 'sale' | 'receipt';

export interface AwaitingRow {
  source: AwaitingSource;
  /** The pending payment's id, or the receipt's. */
  id: string;
  /** The till sale's reference, or the receipt's number. */
  reference: string;
  customerId: string | null;
  customerName: string | null;
  methodKey: string;
  amount: number;
  instrumentNumber: string | null;
  instrumentBank: string | null;
  instrumentDueDate: string | null;
  /** When the paper was taken: the sale, or the receipt. */
  takenAtUtc: string;
  /** A receipt's branch; a till payment does not carry one (its sale does, on the server). */
  branchId: string | null;
}

/** The till's pending payments and the pending receipts as one list: earliest due first, undated last. */
export function awaitingRows(sales: readonly PendingPayment[], receipts: readonly ReceiptVoucher[]): AwaitingRow[] {
  const rows: AwaitingRow[] = [
    ...sales
      .filter((p) => p.status === 'Pending')
      .map<AwaitingRow>((p) => ({
        source: 'sale',
        id: p.id,
        reference: p.saleReference,
        customerId: p.customerId,
        customerName: p.customerName,
        methodKey: p.methodKey,
        amount: p.amount,
        instrumentNumber: p.instrumentNumber,
        instrumentBank: p.instrumentBank,
        instrumentDueDate: p.instrumentDueDate,
        takenAtUtc: p.soldAtUtc,
        branchId: null,
      })),
    ...receipts
      .filter((v) => v.status === 'Pending')
      .map<AwaitingRow>((v) => ({
        source: 'receipt',
        id: v.id,
        reference: v.number,
        customerId: v.customerId,
        customerName: v.customerName,
        methodKey: v.methodKey,
        amount: v.amount,
        instrumentNumber: v.instrumentNumber,
        instrumentBank: v.instrumentBank,
        instrumentDueDate: v.instrumentDueDate,
        takenAtUtc: v.receivedAtUtc,
        branchId: v.branchId,
      })),
  ];
  const time = (value: string | null) => (value ? Date.parse(value) : Number.POSITIVE_INFINITY);
  return rows.sort(
    (a, b) => time(a.instrumentDueDate) - time(b.instrumentDueDate) || time(a.takenAtUtc) - time(b.takenAtUtc),
  );
}

export interface AwaitingFigures {
  total: number;
  count: number;
  overdueTotal: number;
  overdueCount: number;
}

/** The three figures the page leads with: how much is on paper, how much should have arrived. */
export function awaitingFigures(rows: readonly AwaitingRow[], due: (row: AwaitingRow) => DueWord): AwaitingFigures {
  return rows.reduce<AwaitingFigures>(
    (sum, row) => {
      const overdue = due(row) === 'overdue';
      return {
        total: sum.total + row.amount,
        count: sum.count + 1,
        overdueTotal: sum.overdueTotal + (overdue ? row.amount : 0),
        overdueCount: sum.overdueCount + (overdue ? 1 : 0),
      };
    },
    { total: 0, count: 0, overdueTotal: 0, overdueCount: 0 },
  );
}

/** The till's list stops at 500 rows and the receipts' at the take asked for: at either, there may be more. */
export const SALES_CAP = 500;
