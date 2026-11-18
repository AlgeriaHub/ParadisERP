import { EXPENSE } from './payment-vouchers-api.service';
import { VoucherDraft, voucherProblem } from './payment-voucher-view';
import { elementLabel, totalWithheld } from '../payroll/payroll-run-view';

/** Workers' bonuses on a payment voucher (owner, 2026-11-08): the form's checks and the payslip's words. */
describe('workers’ bonus voucher', () => {
  const draft = (lines: { employeeId: string | null; amount: number | null }[]): VoucherDraft => ({
    kind: EXPENSE, branchId: 'b1', methodKey: 'cash', amount: null, requiresInstrument: false,
    instrumentNumber: '', instrumentBank: '', customerId: null, customerBalance: null, returnNote: null,
    category: 'workersBonus', payeeName: '', bonusLines: lines,
  });

  it('needs a worker on every line, an amount above zero, and each worker once — and no payee or amount of its own', () => {
    expect(voucherProblem(draft([]))).toBe('workersRequired');
    expect(voucherProblem(draft([{ employeeId: null, amount: 100 }]))).toBe('workersRequired');
    expect(voucherProblem(draft([{ employeeId: 'e1', amount: 0 }]))).toBe('workerAmountRequired');
    expect(voucherProblem(draft([{ employeeId: 'e1', amount: 100 }, { employeeId: 'e1', amount: 50 }]))).toBe('workerTwice');
    expect(voucherProblem(draft([{ employeeId: 'e1', amount: 100 }, { employeeId: 'e2', amount: 50 }]))).toBeNull();
  });

  it('names the bonus on the payslip in words and counts what the voucher already paid as withheld', () => {
    const t = (key: string) => `[${key}]`;
    expect(elementLabel({ key: 'voucherBonus', kind: 'Adjustment' }, { elements: new Map() } as never, 'fr', t)).toBe('[hr.element.voucherBonus]');
    expect(totalWithheld({ socialEmployee: 0, incomeTax: 0, advanceRecovered: 1_000, otherDeductions: 0, paidByVoucher: 5_000 })).toBe(6_000);
  });
});
