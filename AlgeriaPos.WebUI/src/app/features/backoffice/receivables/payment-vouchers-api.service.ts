import { Injectable, inject } from '@angular/core';
import { ApiService } from '../../../core/api.service';

/**
 * The payment voucher's wire types and its client — written against
 * `AlgeriaPos.API/Endpoints/PaymentVoucherEndpoints.cs` and
 * `AlgeriaPos.Application/Contracts/PaymentVoucherContracts.cs`.
 *
 * <p>Unlike the receipt, the kind and the status travel as <b>numbers</b>: the API has no string
 * enum converter and these two DTOs carry the enums themselves rather than their names. The maps
 * in `payment-voucher-view.ts` turn them into words.</p>
 */

/**
 * `PaymentVoucherKind`: 1 a refund to a customer, 2 an expense the branch paid, 3 a payroll payout
 * (an advance, wages, a statutory payment) raised paid by its payroll document (answer 24).
 */
export type PaymentVoucherKind = 1 | 2 | 3 | 4;
export const REFUND: PaymentVoucherKind = 1;
export const EXPENSE: PaymentVoucherKind = 2;
export const PAYROLL: PaymentVoucherKind = 3;
export const SUPPLIER_PAYMENT: PaymentVoucherKind = 4;

/** `PaymentVoucherStatus`: pending → approved → paid, or cancelled from either of the first two. */
export type PaymentVoucherStatus = 1 | 2 | 3 | 4;

/** The categories the server posts, each to its own expense account (answer 24). */
export type ExpenseCategory = 'fuel' | 'transport' | 'repair' | 'purchase' | 'dailyLabour' | 'workersBonus' | 'other';
export const EXPENSE_CATEGORIES: readonly ExpenseCategory[] = ['fuel', 'transport', 'repair', 'purchase', 'dailyLabour', 'workersBonus', 'other'];

/** Workers' bonuses (مكافآت عمال, owner 2026-11-08): one voucher, several workers, each on their payslip. */
export const WORKERS_BONUS: ExpenseCategory = 'workersBonus';

/** One worker's share of a workers' bonus, as the voucher shows it; `runNumber` is the payroll run that put it on the payslip. */
export interface PaymentVoucherBonusLine {
  employeeId: string;
  employeeNumber: string;
  employeeName: string;
  amount: number;
  payrollYear: number;
  payrollMonth: number;
  runId: string | null;
  runNumber: string | null;
  settled: boolean;
}

/** A worker a bonus may name: who they are, never what they earn. */
export interface BonusWorker {
  id: string;
  number: string;
  fullName: string;
}

/** A category, the account it posts to, and whether the company requires a contract on it (answer 26). */
export interface ExpenseCategoryInfo {
  key: ExpenseCategory;
  accountCode: string;
  contractRequired: boolean;
}

export interface PaymentVoucher {
  id: string;
  /** 'PV-000001'. */
  number: string;
  branchId: string;
  branchName: string;
  kind: PaymentVoucherKind;
  customerId: string | null;
  customerName: string | null;
  supplierId?: string | null;
  /** Who received the money, as the paper names them. A refund defaults to the customer. */
  payeeName: string | null;
  expenseCategory: ExpenseCategory | string | null;
  expenseAccountCode: string | null;
  /** What actually leaves. On a refund, the part not taken off the debt. */
  amount: number;
  methodKey: string;
  /** The tender's French name; the screens use their own method list for the reader's language. */
  methodName: string;
  instrumentNumber: string | null;
  instrumentBank: string | null;
  /** The "in exchange for" line. */
  purpose: string | null;
  contractId: string | null;
  returnNoteId: string | null;
  unusedDays: number | null;
  /** How much of the refund was set against what the customer owed when the voucher was raised. */
  deductedFromDebt: number;
  status: PaymentVoucherStatus;
  requestedBy: string | null;
  createdAtUtc: string;
  approvedByUserId: string | null;
  approvedByName: string | null;
  approvedAtUtc: string | null;
  paidAtUtc: string | null;
  paidByName: string | null;
  cancelledAtUtc: string | null;
  statusNote: string | null;
  notes: string | null;
  contractNumber: string | null;
  returnNoteNumber: string | null;
  /** What the return note gave back, and the part of it the return itself took off the debt. */
  returnNoteUnusedRent: number | null;
  returnNoteDebtReduction: number | null;
  /** On a workers' bonus: who received what. */
  bonusLines?: PaymentVoucherBonusLine[] | null;
}

/** Every write answers this: the voucher, and anything to be told about it. */
export interface PaymentVoucherResponse {
  voucher: PaymentVoucher;
  /** The English sentence; the screen prefers `pv.warn.{warningCode}` with `warningArgs`. */
  warning: string | null;
  warningCode: string | null;
  warningArgs: Record<string, string> | null;
}

export interface CreatePaymentVoucherRequest {
  branchId: string;
  kind: PaymentVoucherKind;
  /** 0 on a refund over a return note means "what the note still owes". */
  amount: number;
  methodKey: string;
  customerId?: string | null;
  supplierId?: string | null;
  payeeName?: string | null;
  expenseCategory?: ExpenseCategory | null;
  purpose?: string | null;
  returnNoteId?: string | null;
  instrumentNumber?: string | null;
  instrumentBank?: string | null;
  notes?: string | null;
  /** The rental contract an expense is for; required for the categories the company says (answer 26). */
  contractId?: string | null;
  /** A workers' bonus: each worker and their amount; the total is the voucher's amount. */
  bonusLines?: { employeeId: string; amount: number }[] | null;
  /** Its payroll month, 'yyyy-MM'; blank is the company's current month, on the server's clock. */
  payrollMonth?: string | null;
}

/** A return note whose early-return refund is (or was) owed as a voucher. */
export interface RefundableReturnNote {
  returnNoteId: string;
  number: string;
  contractId: string;
  contractNumber: string;
  customerId: string;
  customerName: string;
  branchId: string;
  returnedAtUtc: string;
  unusedRent: number;
  reliefDebtReduction: number;
  reliefPaymentVoucher: number;
  /** Already on live vouchers — paid, approved or pending, with what they took off the debt. */
  alreadyRaised: number;
  stillOwed: number;
  /** What the customer's account holds in their favour, net of refunds raised and not yet paid. */
  customerCredit: number;
  /** What a voucher raised now pays out: the owed figure, capped by that credit (answer 27). */
  payable: number;
  unusedDays: number | null;
}

/**
 * A contract whose customer may still be refunded for the period they did not use (owner,
 * 2026-10-06), whatever its return notes did with the money — given back off a debt since paid,
 * say. `payable` is the lesser of what is left and the customer's credit.
 */
export interface RefundableContract {
  contractId: string;
  number: string;
  customerId: string;
  customerName: string;
  /** What its return notes gave back for the unused period. */
  unusedRent: number;
  /** Already on refund vouchers not cancelled, by the contract or one of its notes. */
  alreadyRaised: number;
  stillOwed: number;
  customerCredit: number;
  payable: number;
}

export interface PaymentVoucherListQuery {
  branchId?: string | null;
  customerId?: string | null;
  returnNoteId?: string | null;
  status?: PaymentVoucherStatus | null;
  kind?: PaymentVoucherKind | null;
  take?: number | null;
}

function query(params: URLSearchParams): string {
  const text = params.toString();
  return text ? `?${text}` : '';
}

@Injectable({ providedIn: 'root' })
export class PaymentVouchersApi {
  private readonly api = inject(ApiService);

  list(q: PaymentVoucherListQuery = {}): Promise<PaymentVoucher[]> {
    const params = new URLSearchParams();
    if (q.branchId) params.set('branchId', q.branchId);
    if (q.customerId) params.set('customerId', q.customerId);
    if (q.returnNoteId) params.set('returnNoteId', q.returnNoteId);
    if (q.status) params.set('status', String(q.status));
    if (q.kind) params.set('kind', String(q.kind));
    if (q.take) params.set('take', String(q.take));
    return this.api.getJson<PaymentVoucher[]>(`/api/payment-vouchers${query(params)}`);
  }

  get(id: string): Promise<PaymentVoucher> {
    return this.api.getJson<PaymentVoucher>(`/api/payment-vouchers/${id}`);
  }

  refundable(customerId?: string | null, returnNoteId?: string | null): Promise<RefundableReturnNote[]> {
    const params = new URLSearchParams();
    if (customerId) params.set('customerId', customerId);
    if (returnNoteId) params.set('returnNoteId', returnNoteId);
    return this.api.getJson<RefundableReturnNote[]>(`/api/payment-vouchers/refundable${query(params)}`);
  }

  refundableContracts(customerId?: string | null, contractId?: string | null): Promise<RefundableContract[]> {
    const params = new URLSearchParams();
    if (customerId) params.set('customerId', customerId);
    if (contractId) params.set('contractId', contractId);
    return this.api.getJson<RefundableContract[]>(`/api/payment-vouchers/refundable-contracts${query(params)}`);
  }

  categories(): Promise<ExpenseCategoryInfo[]> {
    return this.api.getJson<ExpenseCategoryInfo[]>('/api/payment-vouchers/categories');
  }

  /** Manager only: the categories whose voucher must name a contract. */
  setContractCategories(keys: readonly string[]): Promise<ExpenseCategoryInfo[]> {
    return this.api.putJson<ExpenseCategoryInfo[]>('/api/payment-vouchers/categories', { contractRequired: keys });
  }

  /** The active workers of a branch, for a workers' bonus. */
  bonusWorkers(branchId: string): Promise<BonusWorker[]> {
    return this.api.getJson<BonusWorker[]>(`/api/payment-vouchers/bonus-workers?branchId=${encodeURIComponent(branchId)}`);
  }

  create(req: CreatePaymentVoucherRequest): Promise<PaymentVoucherResponse> {
    return this.api.postJson<PaymentVoucherResponse>('/api/payment-vouchers', req);
  }

  approve(id: string, note?: string | null): Promise<PaymentVoucherResponse> {
    return this.api.postJson<PaymentVoucherResponse>(`/api/payment-vouchers/${id}/approve`, { note: note ?? null });
  }

  pay(id: string, note?: string | null): Promise<PaymentVoucherResponse> {
    return this.api.postJson<PaymentVoucherResponse>(`/api/payment-vouchers/${id}/pay`, { note: note ?? null });
  }

  cancel(id: string, note: string): Promise<PaymentVoucherResponse> {
    return this.api.postJson<PaymentVoucherResponse>(`/api/payment-vouchers/${id}/cancel`, { note });
  }
}
