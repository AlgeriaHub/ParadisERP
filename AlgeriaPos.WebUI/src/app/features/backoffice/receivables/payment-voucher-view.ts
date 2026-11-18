import type { PosUser } from '../../../core/models';
import { roundTo } from './receivable-view';
import {
  EXPENSE,
  PaymentVoucher,
  PaymentVoucherKind,
  PaymentVoucherResponse,
  PaymentVoucherStatus,
  REFUND,
  SUPPLIER_PAYMENT,
  RefundableContract, RefundableReturnNote,
} from './payment-vouchers-api.service';

/**
 * Pure helpers for the payment voucher screens and the printed voucher: the state words and
 * their tones, what each state allows, the form's refusals asked before the server is, and the
 * warning sentence in the reader's language. No Angular in here, so all of it is tested.
 */

// --- state words -------------------------------------------------------------------------------

/** The server's number to the word the dictionary is keyed on (`pv.state.Pending` …). */
export const STATUS_NAME: Record<PaymentVoucherStatus, 'Pending' | 'Approved' | 'Paid' | 'Cancelled'> = {
  1: 'Pending',
  2: 'Approved',
  3: 'Paid',
  4: 'Cancelled',
};

/** Tone beside the word, never instead of it: waiting is warn, signed is lead, gone is ok. */
export const STATUS_TONE: Record<PaymentVoucherStatus, string> = {
  1: 'bo-state--warn',
  2: 'bo-state--lead',
  3: 'bo-state--ok',
  4: '',
};

export const KIND_NAME: Record<PaymentVoucherKind, 'refund' | 'expense' | 'payroll' | 'supplierPayment'> = {
  1: 'refund',
  2: 'expense',
  3: 'payroll',
  4: 'supplierPayment',
};

export function statusKey(status: PaymentVoucherStatus | number): string {
  return `pv.state.${STATUS_NAME[status as PaymentVoucherStatus] ?? status}`;
}

export function kindKey(kind: PaymentVoucherKind | number): string {
  return `pv.kind.${KIND_NAME[kind as PaymentVoucherKind] ?? kind}`;
}

// --- what a state allows -----------------------------------------------------------------------

export interface VoucherActions {
  approve: boolean;
  /** Pending, but raised by this approver: another manager or an administrator signs it. */
  approveIsOwn: boolean;
  pay: boolean;
  cancel: boolean;
}

/** The server capabilities that decide who approves (Domain/Capabilities.cs), as the login spells them. */
export const APPROVE_VOUCHERS = 'payment_vouchers.approve';
export const APPROVE_OWN_VOUCHERS = 'payment_vouchers.approve_own';

/** Who is looking at the voucher: their login name and what the server said they may do. */
export interface VoucherReader {
  username: string | null | undefined;
  capabilities: readonly string[] | null | undefined;
}

export function voucherReader(user: Pick<PosUser, 'username' | 'capabilities'> | null | undefined): VoucherReader {
  return { username: user?.username, capabilities: user?.capabilities ?? [] };
}

/**
 * The server's own rules, said before the click: approve a pending voucher (not one's own,
 * unless the server says this reader may), pay an approved one, cancel either. A paid or
 * cancelled voucher offers nothing.
 *
 * Who approves is read from the login (`payment_vouchers.approve`, `…approve_own`), never from a
 * role name: a cashier who holds the cash box raises, pays and cancels, and is not offered
 * Approve because the server would refuse it. Paying and cancelling need nothing more than the
 * page itself — whoever may open it holds the cash box. A session signed in before the login
 * reported these reads as "may not approve" until the next online sign-in.
 */
export function voucherActions(v: Pick<PaymentVoucher, 'status' | 'requestedBy'>, me: VoucherReader): VoucherActions {
  const own = !!v.requestedBy && !!me.username && v.requestedBy.toLowerCase() === me.username.toLowerCase();
  const pending = v.status === 1;
  const mayApprove = me.capabilities?.includes(APPROVE_VOUCHERS) ?? false;
  const mayApproveOwn = me.capabilities?.includes(APPROVE_OWN_VOUCHERS) ?? false;
  return {
    approve: pending && mayApprove && (!own || mayApproveOwn),
    // Said only to someone who could otherwise approve: to a cashier it would be noise.
    approveIsOwn: pending && own && mayApprove && !mayApproveOwn,
    pay: v.status === 2,
    cancel: pending || v.status === 2,
  };
}

// --- the form's refusals -----------------------------------------------------------------------

export type VoucherProblem =
  | 'branchRequired'
  | 'methodRequired'
  | 'customerRequired'
  | 'categoryRequired'
  | 'payeeRequired'
  | 'workersRequired'
  | 'workerAmountRequired'
  | 'workerTwice'
  | 'amountRequired'
  | 'instrumentRequired'
  | 'nothingOwed'
  | 'overOwed'
  | 'nothingPayable'
  | 'overCredit'
  | 'contractNothingLeft'
  | 'overContract'
  | 'supplierRequired';

export interface VoucherDraft {
  kind: PaymentVoucherKind;
  branchId: string | null;
  methodKey: string | null;
  amount: number | null;
  requiresInstrument: boolean;
  instrumentNumber: string;
  instrumentBank: string;
  customerId: string | null;
  /** The customer's recorded balance: negative is money in their favour. */
  customerBalance: number | null;
  returnNote: RefundableReturnNote | null;
  /** With no return note: the contract whose unused period is refunded (owner, 2026-10-06). */
  refundContract?: RefundableContract | null;
  category: string | null;
  payeeName: string;
  supplierId?: string | null;
  /** A workers' bonus: each row's worker and amount (null while not filled). */
  bonusLines?: readonly { employeeId: string | null; amount: number | null }[];
}

/**
 * The server's refusals, in its order, at the currency's precision. The customer's credit for a
 * refund with no note is read off the recorded balance only — the server also nets refunds
 * already raised and not paid, and says so if that is what stops it.
 */
export function voucherProblem(d: VoucherDraft, decimals = 2): VoucherProblem | null {
  const r = (v: number) => roundTo(v, decimals);
  if (!d.branchId) return 'branchRequired';
  if (!d.methodKey) return 'methodRequired';
  if (d.requiresInstrument && (!d.instrumentNumber.trim() || !d.instrumentBank.trim())) return 'instrumentRequired';
  if (d.kind === SUPPLIER_PAYMENT) {
    if (!d.supplierId) return 'supplierRequired';
    if (!d.amount || d.amount <= 0) return 'amountRequired';
    return null;
  }
  if (d.kind === EXPENSE) {
    if (!d.category) return 'categoryRequired';
    if (d.category === 'workersBonus') {
      // Its workers are the payee and their amounts the total (owner, 2026-11-08).
      const lines = d.bonusLines ?? [];
      if (lines.length === 0 || lines.some((l) => !l.employeeId)) return 'workersRequired';
      if (lines.some((l) => !l.amount || l.amount <= 0)) return 'workerAmountRequired';
      if (new Set(lines.map((l) => l.employeeId)).size !== lines.length) return 'workerTwice';
      return null;
    }
    if (!d.payeeName.trim()) return 'payeeRequired';
    if (!d.amount || d.amount <= 0) return 'amountRequired';
    return null;
  }
  if (!d.customerId && !d.returnNote) return 'customerRequired';
  if (d.returnNote) {
    if (r(d.returnNote.stillOwed) <= 0) return 'nothingOwed';
    if (r(d.returnNote.payable) <= 0) return 'nothingPayable';
    if (d.amount !== null && r(d.amount) > r(d.returnNote.stillOwed)) return 'overOwed';
    if (d.amount !== null && d.amount < 0) return 'amountRequired';
    return null;
  }
  if (d.refundContract) {
    // Blank is "what can be paid now"; the server works it out.
    if (r(d.refundContract.stillOwed) <= 0) return 'contractNothingLeft';
    if (r(d.refundContract.payable) <= 0) return 'nothingPayable';
    if (d.amount !== null && d.amount < 0) return 'amountRequired';
    if (d.amount !== null && r(d.amount) > r(d.refundContract.stillOwed)) return 'overContract';
    if (d.amount !== null && r(d.amount) > r(d.refundContract.payable)) return 'overCredit';
    return null;
  }
  if (!d.amount || d.amount <= 0) return 'amountRequired';
  const credit = Math.max(0, -(d.customerBalance ?? 0));
  if (r(d.amount) > r(credit)) return credit > 0 ? 'overCredit' : 'nothingPayable';
  return null;
}

/**
 * How a refund over a note will split, before it is raised (answer 27): what leaves the box,
 * and what is taken off the debt the customer has run up since the return. `amount` null means
 * "what the note still owes", which is what the server takes for a blank figure.
 */
export function refundSplit(amount: number | null, note: RefundableReturnNote, decimals = 2): { paid: number; deducted: number } {
  const asked = roundTo(amount ?? note.stillOwed, decimals);
  const payable = roundTo(Math.max(0, note.payable), decimals);
  return asked <= payable ? { paid: asked, deducted: 0 } : { paid: payable, deducted: roundTo(asked - payable, decimals) };
}

/** The default line for a refund over a whole contract — the server writes the same when left blank. */
export function refundContractPurpose(contract: Pick<RefundableContract, 'number'>): string {
  return `Remboursement — contrat ${contract.number}, période non utilisée`;
}

/** The default "in exchange for" line for a refund over a note — the server writes the same when left blank. */
export function refundPurpose(note: Pick<RefundableReturnNote, 'contractNumber' | 'number' | 'unusedDays'>): string {
  const text = `Remboursement — contrat ${note.contractNumber}, retour ${note.number}`;
  return note.unusedDays && note.unusedDays > 0 ? `${text}, ${note.unusedDays} jours non utilisés` : text;
}

// --- warnings in the reader's language ---------------------------------------------------------

/** The figures in a warning's arguments, by name. Everything else is passed through as written. */
const MONEY_ARGS = new Set(['amount', 'balance', 'deducted', 'paid']);

/**
 * The warning beside a recorded voucher, in the reader's language when the dictionary knows the
 * code, else the server's English sentence. Money arguments are formatted by the caller's
 * regional formatter — never with a currency written here.
 */
export function warningText(
  response: Pick<PaymentVoucherResponse, 'warning' | 'warningCode' | 'warningArgs'>,
  t: (key: string, params?: Record<string, string | number>) => string,
  money: (value: number) => string,
): string | null {
  if (!response.warning && !response.warningCode) return null;
  if (response.warningCode) {
    const key = `pv.warn.${response.warningCode}`;
    const params: Record<string, string> = {};
    for (const [name, value] of Object.entries(response.warningArgs ?? {})) {
      params[name] = MONEY_ARGS.has(name) && Number.isFinite(Number(value)) ? money(Number(value)) : value;
    }
    const text = t(key, params);
    if (text !== key) return text;
  }
  return response.warning;
}

export { EXPENSE, REFUND, SUPPLIER_PAYMENT };
