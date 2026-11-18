import {
  VoucherDraft,
  refundContractPurpose,
  refundPurpose,
  refundSplit,
  statusKey,
  voucherActions,
  voucherProblem,
  voucherReader,
  warningText,
} from './payment-voucher-view';
import { EXPENSE, REFUND, RefundableContract, RefundableReturnNote } from './payment-vouchers-api.service';

function note(over: Partial<RefundableReturnNote> = {}): RefundableReturnNote {
  return {
    returnNoteId: 'n1',
    number: 'RET-000004',
    contractId: 'c1',
    contractNumber: 'CTR-000012',
    customerId: 'cu1',
    customerName: 'Ben Ali BTP',
    branchId: 'b1',
    returnedAtUtc: '2026-03-11T09:00:00Z',
    unusedRent: 200_000,
    reliefDebtReduction: 0,
    reliefPaymentVoucher: 200_000,
    alreadyRaised: 0,
    stillOwed: 200_000,
    customerCredit: 200_000,
    payable: 200_000,
    unusedDays: 20,
    ...over,
  };
}

function draft(over: Partial<VoucherDraft> = {}): VoucherDraft {
  return {
    kind: EXPENSE,
    branchId: 'b1',
    methodKey: 'cash',
    amount: 12_000,
    requiresInstrument: false,
    instrumentNumber: '',
    instrumentBank: '',
    customerId: null,
    customerBalance: null,
    returnNote: null,
    category: 'fuel',
    payeeName: 'Station Naftal',
    ...over,
  };
}

describe('payment voucher view', () => {
  it('names each state by the word the dictionary is keyed on', () => {
    expect(statusKey(1)).toBe('pv.state.Pending');
    expect(statusKey(2)).toBe('pv.state.Approved');
    expect(statusKey(3)).toBe('pv.state.Paid');
    expect(statusKey(4)).toBe('pv.state.Cancelled');
  });

  // What the login reports (Domain/Capabilities.cs): a manager approves, the admin also their own.
  const MANAGER = ['customers.create', 'payment_vouchers.approve'];
  const ADMIN = ['customers.create', 'payment_vouchers.approve', 'payment_vouchers.approve_own'];

  it('lets a manager approve someone else’s voucher but not their own, and the admin both', () => {
    const own = { status: 1 as const, requestedBy: 'Gerant' };
    expect(voucherActions(own, { username: 'gerant', capabilities: MANAGER })).toEqual({
      approve: false,
      approveIsOwn: true,
      pay: false,
      cancel: true,
    });
    expect(voucherActions(own, { username: 'second', capabilities: MANAGER }).approve).toBeTrue();
    expect(voucherActions(own, { username: 'gerant', capabilities: ADMIN }).approve).toBeTrue();
  });

  it('never offers Approve to a cash-box cashier, who still pays and cancels', () => {
    const cashier = { username: 'caisse', capabilities: [] };
    expect(voucherActions({ status: 1, requestedBy: 'gerant' }, cashier)).toEqual({ approve: false, approveIsOwn: false, pay: false, cancel: true });
    expect(voucherActions({ status: 1, requestedBy: 'caisse' }, cashier)).toEqual({ approve: false, approveIsOwn: false, pay: false, cancel: true });
    expect(voucherActions({ status: 2, requestedBy: 'gerant' }, cashier)).toEqual({ approve: false, approveIsOwn: false, pay: true, cancel: true });
  });

  it('reads a session from before the login reported capabilities as "may not approve", never as an error', () => {
    expect(voucherActions({ status: 1, requestedBy: 'x' }, { username: 'y', capabilities: undefined }).approve).toBeFalse();
    expect(voucherReader(null)).toEqual({ username: undefined, capabilities: [] });
    expect(voucherReader({ username: 'g', capabilities: MANAGER }).capabilities).toEqual(MANAGER);
  });

  it('decides from what the server reported, not from a role name', () => {
    // No role is read: a login reporting no approval is not offered it, whatever its role.
    expect(voucherActions({ status: 1, requestedBy: 'x' }, { username: 'y', capabilities: ['customers.create'] }).approve).toBeFalse();
    expect(voucherActions({ status: 1, requestedBy: 'x' }, { username: 'y', capabilities: MANAGER }).approve).toBeTrue();
  });

  it('pays only an approved voucher and offers nothing once it is paid or cancelled', () => {
    const me = { username: 'x', capabilities: ADMIN };
    expect(voucherActions({ status: 2, requestedBy: 'y' }, me)).toEqual({ approve: false, approveIsOwn: false, pay: true, cancel: true });
    expect(voucherActions({ status: 3, requestedBy: 'y' }, me)).toEqual({ approve: false, approveIsOwn: false, pay: false, cancel: false });
    expect(voucherActions({ status: 4, requestedBy: 'y' }, me)).toEqual({ approve: false, approveIsOwn: false, pay: false, cancel: false });
  });

  it('asks for an expense’s category, payee and amount, and a cheque’s number and bank', () => {
    expect(voucherProblem(draft())).toBeNull();
    expect(voucherProblem(draft({ category: null }))).toBe('categoryRequired');
    expect(voucherProblem(draft({ payeeName: '  ' }))).toBe('payeeRequired');
    expect(voucherProblem(draft({ amount: 0 }))).toBe('amountRequired');
    expect(voucherProblem(draft({ requiresInstrument: true, instrumentNumber: '0012345' }))).toBe('instrumentRequired');
    expect(voucherProblem(draft({ requiresInstrument: true, instrumentNumber: '0012345', instrumentBank: 'BNA' }))).toBeNull();
  });

  it('refuses a refund over a note above what the note still owes, and accepts a blank figure', () => {
    const refund = draft({ kind: REFUND, category: null, payeeName: '', returnNote: note(), amount: null });
    expect(voucherProblem(refund)).toBeNull();
    expect(voucherProblem({ ...refund, amount: 200_000.004 })).toBeNull();
    expect(voucherProblem({ ...refund, amount: 200_001 })).toBe('overOwed');
    expect(voucherProblem({ ...refund, returnNote: note({ stillOwed: 0, payable: 0 }) })).toBe('nothingOwed');
    expect(voucherProblem({ ...refund, returnNote: note({ customerCredit: 0, payable: 0 }) })).toBe('nothingPayable');
  });

  it('keeps a refund with no note inside what the account holds in the customer’s favour', () => {
    const refund = draft({ kind: REFUND, category: null, payeeName: '', customerId: 'cu1', amount: 30_000 });
    expect(voucherProblem({ ...refund, customerBalance: -30_000 })).toBeNull();
    expect(voucherProblem({ ...refund, customerBalance: -10_000 })).toBe('overCredit');
    expect(voucherProblem({ ...refund, customerBalance: 5_000 })).toBe('nothingPayable');
    expect(voucherProblem({ ...refund, customerId: null })).toBe('customerRequired');
  });

  it('splits a refund into what is paid out and what comes off debt run up since the return', () => {
    expect(refundSplit(null, note())).toEqual({ paid: 200_000, deducted: 0 });
    expect(refundSplit(null, note({ payable: 150_000, customerCredit: 150_000 }))).toEqual({ paid: 150_000, deducted: 50_000 });
    expect(refundSplit(100_000, note({ payable: 150_000 }))).toEqual({ paid: 100_000, deducted: 0 });
  });

  it('writes the refund’s purpose the way the server does', () => {
    expect(refundPurpose(note())).toBe('Remboursement — contrat CTR-000012, retour RET-000004, 20 jours non utilisés');
    expect(refundPurpose(note({ unusedDays: null }))).toBe('Remboursement — contrat CTR-000012, retour RET-000004');
  });

  it('says a warning in the reader’s language with its figures formatted, else in the server’s words', () => {
    const t = (key: string, p?: Record<string, string | number>) =>
      key === 'pv.warn.boxShort' ? `caisse ${p?.['box']} : ${p?.['amount']} > ${p?.['balance']}` : key;
    const money = (v: number) => `${v.toFixed(2)} u`;

    expect(
      warningText({ warning: 'Paying 90000…', warningCode: 'boxShort', warningArgs: { amount: '90000', balance: '0', box: 'Caisse Alger' } }, t, money),
    ).toBe('caisse Caisse Alger : 90000.00 u > 0.00 u');
    expect(warningText({ warning: 'Something new', warningCode: 'unknown', warningArgs: {} }, t, money)).toBe('Something new');
    expect(warningText({ warning: null, warningCode: null, warningArgs: null }, t, money)).toBeNull();
  });
});

/** A refund for the unused period of a whole contract (owner, 2026-10-06). */
describe('a refund over a contract', () => {
  const contract = (over: Partial<RefundableContract> = {}): RefundableContract => ({
    contractId: 'c1', number: 'CTR-000012', customerId: 'cu1', customerName: 'Ben Ali BTP',
    unusedRent: 200_000, alreadyRaised: 50_000, stillOwed: 150_000, customerCredit: 120_000, payable: 120_000,
    ...over,
  });
  const refund = (over: Partial<VoucherDraft>): VoucherDraft => ({
    kind: REFUND, branchId: 'b1', methodKey: 'cash', amount: null, requiresInstrument: false,
    instrumentNumber: '', instrumentBank: '', customerId: 'cu1', customerBalance: -120_000,
    returnNote: null, category: null, payeeName: '', ...over,
  });

  it('takes a blank amount as what can be paid now, and refuses what is not left or not payable', () => {
    expect(voucherProblem(refund({ refundContract: contract() }))).toBeNull();
    expect(voucherProblem(refund({ refundContract: contract(), amount: 120_000 }))).toBeNull();
    expect(voucherProblem(refund({ refundContract: contract(), amount: 150_001 }))).toBe('overContract');
    expect(voucherProblem(refund({ refundContract: contract(), amount: 130_000 }))).toBe('overCredit');
    expect(voucherProblem(refund({ refundContract: contract({ stillOwed: 0, payable: 0 }) }))).toBe('contractNothingLeft');
    expect(voucherProblem(refund({ refundContract: contract({ customerCredit: 0, payable: 0 }) }))).toBe('nothingPayable');
    // With no contract and no note the amount is still required.
    expect(voucherProblem(refund({}))).toBe('amountRequired');
  });

  it('names the contract and the unused period on its line, as the server does', () => {
    expect(refundContractPurpose(contract())).toBe('Remboursement — contrat CTR-000012, période non utilisée');
  });
});
