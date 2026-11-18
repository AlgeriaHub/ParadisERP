import { Provider, Type, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AdminService } from '../core/admin.service';
import { AuthService } from '../core/auth.service';
import { CatalogStore } from '../core/catalog-store';
import { ConnectivityService } from '../core/connectivity.service';
import { I18nService } from '../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../core/i18n/translations-backoffice';
import { LedgerService } from '../core/ledger.service';
import { PlatformService } from '../core/platform.service';
import { PosService } from '../core/pos.service';
import { ConfirmRequest, ConfirmService } from '../ui/confirm.service';
import { differsFromBlank } from '../ui/confirm-requests';
import { ChangePasswordComponent } from './account/change-password.component';
import { BackofficeAccountingComponent } from './backoffice/accounting-tab.component';
import { BackofficeBranchesComponent } from './backoffice/branches-tab.component';
import { BackofficeCustomersComponent } from './backoffice/customers-tab.component';
import { BackofficeDevicesComponent } from './backoffice/devices-tab.component';
import { CashBoxesComponent } from './backoffice/finance/cash-boxes.component';
import { PaymentMethodsComponent } from './backoffice/finance/payment-methods.component';
import { BackofficeGiftCardsComponent } from './backoffice/gift-cards-tab.component';
import { HrApi } from './backoffice/payroll/hr-api.service';
import { PayrollAdvancesComponent } from './backoffice/payroll/advances.component';
import { HrDayGridComponent } from './backoffice/payroll/attendance-day-grid.component';
import { HrAttendanceSheetComponent } from './backoffice/payroll/attendance-sheet.component';
import { HrEmployeesComponent } from './backoffice/payroll/employees.component';
import { PayrollRunComponent } from './backoffice/payroll/payroll-run.component';
import { PayrollRunsComponent } from './backoffice/payroll/payroll-runs.component';
import { CustomerOpeningBalanceComponent } from './backoffice/customer-opening-balance.component';
import { PayrollRunsApi } from './backoffice/payroll/payroll-run-view';
import { PayrollApi } from './backoffice/payroll/payroll-api.service';
import { PayrollSettingsComponent } from './backoffice/payroll/payroll-settings.component';
import { StatutoryPaymentsComponent } from './backoffice/payroll/statutory-payments.component';
import { WageMoneyApi } from './backoffice/payroll/wage-money-api.service';
import { WagePaymentsComponent } from './backoffice/payroll/wage-payments.component';
import { PrintService } from './print/print.service';
import { BackofficeProductsComponent } from './backoffice/products-tab.component';
import { PaymentVoucherDetailComponent } from './backoffice/receivables/payment-voucher-detail.component';
import { PaymentVouchersApi } from './backoffice/receivables/payment-vouchers-api.service';
import { ReceivableInvoiceFormComponent } from './backoffice/receivables/receivable-invoice-form.component';
import { ReceivableCreditNoteFormComponent } from './backoffice/receivables/receivable-credit-note-form.component';
import { ReceivableReceiptsComponent } from './backoffice/receivables/receivable-receipts.component';
import { ReceivableReceiptFormComponent } from './backoffice/receivables/receivable-receipt-form.component';
import { PaymentVoucherFormComponent } from './backoffice/receivables/payment-voucher-form.component';
import { AwaitingCollectionComponent } from './backoffice/finance/awaiting-collection.component';
import { FinancePagesApi } from './backoffice/finance/finance-pages.api';
import { ReceivablesApi } from './backoffice/receivables/receivables-api.service';
import { ReceivablePagesApi } from './backoffice/receivables/receivable-pages.api';
import { SalesPagesApi } from './backoffice/sales-pages.api';
import { RentalContractFormComponent } from './backoffice/rentals/rental-contract-form.component';
import { RentalContractComponent } from './backoffice/rentals/rental-contract.component';
import { RentalExtendNewComponent } from './backoffice/rentals/rental-extend-new.component';
import { RentalIssueNoteComponent } from './backoffice/rentals/rental-issue-note.component';
import { RentalReturnNoteComponent } from './backoffice/rentals/rental-return-note.component';
import { RentalSettingsComponent } from './backoffice/rentals/rental-settings.component';
import { RentalsApi } from './backoffice/rentals/rentals-api.service';
import { PageChrome } from './backoffice/shell/page-chrome';
import { BackofficeStockComponent } from './backoffice/stock-tab.component';
import { BackofficeStoreComponent } from './backoffice/store-tab.component';
import { BackofficeUsersComponent } from './backoffice/users-tab.component';
import { StoreDocumentsApi } from './print/store-documents-api.service';
import { CompanyCreateComponent } from './platform/company-create.component';
import { CompanyHostingComponent } from './platform/company-hosting.component';
import { IntegrationsEditorComponent } from './platform/integrations-editor.component';
import { LanguagePacksComponent } from './platform/language-packs.component';
import { RegionalEditorComponent } from './platform/regional-editor.component';
import { RegisterComponent } from './register/register.component';
import { IntegrationsService } from '../core/integrations.service';
import { Sale } from '../core/models';
import { SyncService } from '../core/sync.service';
import { SendReceiptComponent } from '../shared/send-receipt.component';
import { SalesComponent } from './sales/sales.component';
import { SyncStatusComponent } from './sync/sync-status.component';

/**
 * Every delete in the program asks first (the owner, 2026-10-05), and so does every other action
 * that cannot be taken back: emptying the till's cart, reversing an entry, cancelling a voucher,
 * collecting a cheque, archiving a company. For each: a "no" leaves everything as it was, a "yes"
 * does exactly what the button always did.
 */

/** An API double: the methods a test names are spies; anything else answers an empty list. */
function api<T>(spies: Record<string, unknown> = {}): T {
  return new Proxy(spies, {
    get: (target, key) => (key in target ? target[key as string] : key === 'then' ? undefined : () => Promise.resolve([])),
  }) as T;
}

let answer: jasmine.Spy<(request: ConfirmRequest) => Promise<boolean>>;

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function make<T>(type: Type<T>, providers: Provider[] = [], inputs: Record<string, unknown> = {}): T & any {
  answer = jasmine.createSpy('ask').and.resolveTo(false);
  TestBed.configureTestingModule({
    providers: [provideRouter([]), PageChrome, { provide: ConfirmService, useValue: { ask: answer } }, ...providers],
  });
  TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  // Created, never rendered: the methods under test need the instance, not its template.
  const fixture = TestBed.createComponent(type);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  return fixture.componentInstance;
}

/** Runs the action twice — refused, then accepted — and checks the call happened only the second time. */
async function refusedThenAccepted(act: () => Promise<unknown>, call: jasmine.Spy): Promise<ConfirmRequest> {
  await act();
  expect(answer).toHaveBeenCalledTimes(1);
  expect(call).not.toHaveBeenCalled();
  answer.and.resolveTo(true);
  await act();
  expect(call).toHaveBeenCalledTimes(1);
  return answer.calls.mostRecent().args[0];
}

describe('confirm before acting — back-office deletes', () => {
  const named = { id: 'x1', nameFr: 'Annexe', nameAr: 'الفرع', nameEn: 'Annex' };

  it('customers', async () => {
    const deleteCustomer = jasmine.createSpy().and.resolveTo();
    const page = make(BackofficeCustomersComponent, [{ provide: AdminService, useValue: api({ deleteCustomer }) }]);
    const asked = await refusedThenAccepted(() => page.remove({ id: 'c1', code: 'C-1', fullName: 'SARL Injaz' }), deleteCustomer);
    expect(asked.title).toContain('SARL Injaz');
  });

  it('payment methods', async () => {
    const deletePaymentMethod = jasmine.createSpy().and.resolveTo();
    const page = make(PaymentMethodsComponent, [{ provide: AdminService, useValue: api({ deletePaymentMethod }) }]);
    await refusedThenAccepted(() => page.remove({ ...named, key: 'cheque', isBuiltIn: false }), deletePaymentMethod);
  });

  it('users: deactivating, and revoking sessions', async () => {
    const deactivateUser = jasmine.createSpy().and.resolveTo();
    const revokeSessions = jasmine.createSpy().and.resolveTo();
    const page = make(BackofficeUsersComponent, [{ provide: AdminService, useValue: api({ deactivateUser, revokeSessions }) }]);
    const user = { id: 'u2', username: 'karim', fullName: 'Karim B.' };
    const asked = await refusedThenAccepted(() => page.deactivate(user), deactivateUser);
    expect(asked.title).toContain('Karim B.');

    answer.calls.reset();
    answer.and.resolveTo(false);
    const revoke = await refusedThenAccepted(() => page.revokeSessions(user), revokeSessions);
    // Signing in again is all it costs, so it is not drawn as a danger.
    expect(revoke.danger).toBeFalsy();
  });

  it('products', async () => {
    // The products screen deletes through the API and marks the catalogue copy stale (server paging, 2026-11-17).
    const deleteProduct = jasmine.createSpy().and.resolveTo();
    const page = make(BackofficeProductsComponent, [
      { provide: AdminService, useValue: api({ deleteProduct }) },
      { provide: CatalogStore, useValue: api({ invalidate: () => undefined, active: signal([]), all: signal([]) }) },
    ]);
    await refusedThenAccepted(() => page.remove({ ...named, sku: 'B25' }), deleteProduct);
  });
});

describe('confirm before acting — documents and money', () => {
  it('a rental contract: removing a scanned copy, and closing it short', async () => {
    const removeAttachment = jasmine.createSpy().and.resolveTo();
    const closeShort = jasmine.createSpy().and.resolveTo({});
    const page = make(RentalContractComponent, [{ provide: RentalsApi, useValue: api({ removeAttachment, closeShort }) }], { contractId: 'k1' });
    await refusedThenAccepted(() => page.removeScan({ id: 'a1', fileName: 'cni.pdf', length: 2048 }), removeAttachment);
    expect(removeAttachment).toHaveBeenCalledWith('k1', 'a1');

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.closeReason.set('Accord commercial');
    const asked = await refusedThenAccepted(() => page.closeShort('k1'), closeShort);
    expect(asked.message).toContain('Accord commercial');
  });

  it('a rental contract form: an untouched line goes without asking, a filled one asks', async () => {
    const page = make(RentalContractFormComponent, [
      { provide: RentalsApi, useValue: api() },
      { provide: AdminService, useValue: api() },
      { provide: CatalogStore, useValue: api({ active: signal([]), all: signal([]) }) },
    ], { settings: { depositEnabled: false } });
    // A new contract opens with no line since 2026-10-06; an untouched one is added here.
    page.addLine();
    const blank = page.lines()[0];
    await page.removeLine(blank.key);
    expect(answer).not.toHaveBeenCalled();
    expect(page.lines().length).toBe(0);

    page.lines.set([{ ...blank, key: 7, productId: 'p1', qty: 3 }]);
    await page.removeLine(7);
    expect(answer).toHaveBeenCalledTimes(1);
    expect(page.lines().length).toBe(1);
    answer.and.resolveTo(true);
    await page.removeLine(7);
    expect(page.lines().length).toBe(0);
  });

  it('rental settings: going back to the company figure', async () => {
    const setProductMultiplier = jasmine.createSpy().and.resolveTo({ sku: 'ECH', rentalUnitPrice: 100 });
    const page = make(RentalSettingsComponent, [{ provide: RentalsApi, useValue: api({ setProductMultiplier }) }], { settings: {} });
    const row = { productId: 'p1', sku: 'ECH', nameFr: 'Échafaudage', nameAr: '', nameEn: '', overdueMultiplier: 2, rentalUnitPrice: 100 };
    await refusedThenAccepted(() => page.clearItem(row), setProductMultiplier);
  });

  it('accounting: reversing an entry', async () => {
    const reverseEntry = jasmine.createSpy().and.resolveTo({});
    const page = make(BackofficeAccountingComponent, [
      { provide: AdminService, useValue: api() },
      { provide: LedgerService, useValue: api({ reverseEntry }) },
    ]);
    page.reason = 'Saisie en double';
    await refusedThenAccepted(() => page.reverse({ id: 'e1', total: 1500, isReversal: false }), reverseEntry);
  });

  it('a payment voucher: cancelling', async () => {
    const cancel = jasmine.createSpy().and.resolveTo({ voucher: { number: 'BP-1', status: 'Cancelled' } });
    const page = make(PaymentVoucherDetailComponent, [{ provide: PaymentVouchersApi, useValue: api({ cancel }) }], { id: 'v1' });
    page.note.set('Doublon');
    const asked = await refusedThenAccepted(() => page.act('cancel', { id: 'v1', number: 'BP-1', amount: 9000 }), cancel);
    expect(asked.danger).toBeTrue();
  });

  it('receipts: collecting a cheque', async () => {
    const settleReceipt = jasmine.createSpy().and.resolveTo({ number: 'RC-1', status: 'Confirmed' });
    const page = make(ReceivableReceiptsComponent, [
      { provide: ReceivablesApi, useValue: api({ settleReceipt }) },
      { provide: ReceivablePagesApi, useValue: api() },
    ]);
    await refusedThenAccepted(() => page.collect({ id: 'r1', number: 'RC-1', amount: 5000 }), settleReceipt);
  });

  it('stock: declaring a branch open', async () => {
    const openBranchForOperation = jasmine.createSpy().and.resolveTo({});
    const page = make(BackofficeStockComponent, [
      { provide: AdminService, useValue: api({ openBranchForOperation }) },
      { provide: CatalogStore, useValue: api({ active: signal([]), all: signal([]) }) },
    ]);
    page.opening.branchId = 'b2';
    await refusedThenAccepted(() => page.openBranch(), openBranchForOperation);
  });
});

describe('confirm before acting — edits thrown away', () => {
  it('payroll settings: an empty new row goes, a filled one asks', async () => {
    const page = make(PayrollSettingsComponent, [{ provide: PayrollApi, useValue: api() }]);
    page.addAllowance();
    await page.removeAllowance(0);
    expect(answer).not.toHaveBeenCalled();
    expect(page.draft().allowances.length).toBe(0);

    page.addAllowance();
    page.draft().allowances[0].value = 1500;
    await page.removeAllowance(0);
    expect(answer).toHaveBeenCalledTimes(1);
    expect(page.draft().allowances.length).toBe(1);
    answer.and.resolveTo(true);
    await page.removeAllowance(0);
    expect(page.draft().allowances.length).toBe(0);
  });

  it('the attendance day grid: discarding unsaved days', async () => {
    const page = make(HrDayGridComponent, [{ provide: HrApi, useValue: api() }], { employeeId: 'e1', year: 2026, month: 10 });
    page.grid.set({ days: [] });
    page.dirty.set(new Set(['2026-10-01']));
    await page.discard();
    expect(page.dirty().size).toBe(1);
    answer.and.resolveTo(true);
    await page.discard();
    expect(page.dirty().size).toBe(0);
  });

  it('the attendance sheet: discarding unsaved cells', async () => {
    const page = make(HrAttendanceSheetComponent, [{ provide: HrApi, useValue: api() }, { provide: AdminService, useValue: api() }]);
    Object.defineProperty(page, 'unsaved', { value: () => 2 });
    const buffer = page.buffer;
    buffer.set(new Map([['e1', {}]]));
    await page.discard();
    expect(buffer().size).toBe(1);
    answer.and.resolveTo(true);
    await page.discard();
    expect(buffer().size).toBe(0);
  });
});

describe('confirm before acting — the till and the operator portal', () => {
  it('the till: emptying a cart with lines asks; the sale itself is untouched', async () => {
    const clear = jasmine.createSpy('clear');
    const items = signal([{ productId: 'p1' }]);
    const page = make(RegisterComponent, [{ provide: PosService, useValue: api({ clear, items }) }]);
    const asked = await refusedThenAccepted(() => page.clearCart(), clear);
    expect(asked.message).toContain('1');
  });

  it('the till: an empty cart clears without a question', async () => {
    const clear = jasmine.createSpy('clear');
    const page = make(RegisterComponent, [{ provide: PosService, useValue: api({ clear, items: signal([]) }) }]);
    await page.clearCart();
    expect(answer).not.toHaveBeenCalled();
    expect(clear).toHaveBeenCalled();
  });

  it('archiving a company; restoring one does not ask', async () => {
    const setArchived = jasmine.createSpy().and.resolveTo();
    const company = { id: 'acme', hosts: [], source: 'Portal', state: 'Active' };
    const page = make(CompanyHostingComponent, [{ provide: PlatformService, useValue: api({ setArchived }) }], { company });
    await refusedThenAccepted(() => page.setArchived(true), setArchived);
    answer.calls.reset();
    await page.setArchived(false);
    expect(answer).not.toHaveBeenCalled();
  });

  it('deleting a language pack', async () => {
    const deleteLanguagePack = jasmine.createSpy().and.resolveTo();
    const page = make(LanguagePacksComponent, [{ provide: PlatformService, useValue: api({ deleteLanguagePack }) }]);
    await refusedThenAccepted(() => page.remove({ code: 'kab', label: 'Taqbaylit' }), deleteLanguagePack);
  });

  it('a fiscal identifier row: empty goes, typed asks', async () => {
    const page = make(RegionalEditorComponent, [], { current: { fiscalIds: [] } });
    page.form.set({ fiscalIds: [{ key: '', code: '', labels: {} }, { key: 'nif', code: 'NIF', labels: {} }] });
    await page.removeFiscal(0);
    expect(answer).not.toHaveBeenCalled();
    expect(page.form().fiscalIds.length).toBe(1);
    await page.removeFiscal(0);
    expect(page.form().fiscalIds.length).toBe(1);
    answer.and.resolveTo(true);
    await page.removeFiscal(0);
    expect(page.form().fiscalIds.length).toBe(0);
  });
});

describe('confirm before acting — saves (catalogue and stock)', () => {
  it('products', async () => {
    const create = jasmine.createSpy().and.resolveTo({});
    const adjustStock = jasmine.createSpy().and.resolveTo({});
    const transferStock = jasmine.createSpy().and.resolveTo({ number: 'TRF-000001' });
    const page = make(BackofficeProductsComponent, [
      { provide: AdminService, useValue: api({ createProduct: create, adjustStock, transferStock }) },
      { provide: CatalogStore, useValue: api({ invalidate: () => undefined, active: signal([]), all: signal([]) }) },
    ]);
    page.form.sku = 'SKU-1';
    page.form.nameFr = 'Produit 1';
    const asked = await refusedThenAccepted(() => page.save(), create);
    expect(asked.title).toContain('Produit 1');

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.deltas['b1'] = 5;
    await refusedThenAccepted(() => page.applyRow('p1', { branchId: 'b1', branchName: 'B1', stockQty: 10, reorderLevel: 2 }), adjustStock);

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.transferFrom = 'b1';
    page.transferTo = 'b2';
    page.transferQty = 3;
    await refusedThenAccepted(() => page.transfer('p1'), transferStock);
  });

  it('gift cards', async () => {
    const issueGiftCard = jasmine.createSpy().and.resolveTo({ id: 'gc-1', code: 'GC-1', balance: 500 });
    const adjustGiftCard = jasmine.createSpy().and.resolveTo({ id: 'gc-1', code: 'GC-1', balance: 600 });
    const page = make(BackofficeGiftCardsComponent, [
      { provide: AdminService, useValue: api({ issueGiftCard, adjustGiftCard }) },
      { provide: SalesPagesApi, useValue: api() },
    ]);
    page.amount = 500;
    await refusedThenAccepted(() => page.issue(), issueGiftCard);

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.delta = 100;
    page.reason = 'Correction';
    const card = { id: 'gc-1', code: 'GC-1', balance: 500, initialAmount: 500, isActive: true, expiresAtUtc: null, customerId: null };
    await refusedThenAccepted(() => page.adjust(card), adjustGiftCard);
  });

  it('stock', async () => {
    const receiveStock = jasmine.createSpy().and.resolveTo({});
    const changeStockState = jasmine.createSpy().and.resolveTo({ available: 10, onRent: 0, maintenance: 2, scrap: 0 });
    const openingBalance = jasmine.createSpy().and.resolveTo({});
    const page = make(BackofficeStockComponent, [
      { provide: AdminService, useValue: api({ receiveStock, changeStockState, openingBalance }) },
      { provide: CatalogStore, useValue: api({ active: signal([]), all: signal([]) }) },
    ]);
    page.receive = { productId: 'p1', qty: 10, unitCost: null, assetUnitValue: null, reference: 'R1' };
    await refusedThenAccepted(() => page.receiveStock(), receiveStock);

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.move = { productId: 'p1', branchId: 'b1', qty: 2, from: 'Available', to: 'Maintenance', reason: 'Panne' };
    await refusedThenAccepted(() => page.changeState(), changeStockState);

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.opening = { branchId: 'b1', productId: 'p1', qty: 20 };
    await refusedThenAccepted(() => page.setOpening(), openingBalance);
  });
});

describe('confirm before acting — saves (settings and users)', () => {
  it('store', async () => {
    const updateStore = jasmine.createSpy().and.resolveTo({});
    const put = jasmine.createSpy().and.resolveTo({ changesPerYear: 5, allowances: [] });
    const page = make(BackofficeStoreComponent, [
      { provide: AdminService, useValue: api({ updateStore }) },
      { provide: StoreDocumentsApi, useValue: api({ put }) },
      { provide: PosService, useValue: api() },
      { provide: AuthService, useValue: { currentUser: () => ({ role: 'Admin' }) } },
    ]);
    page.form.set({
      nameFr: 'Boutique Principale',
      nameAr: '',
      nameEn: '',
      address: '',
      addressAr: '',
      addressEn: '',
      phone: '',
      fiscalIds: {},
      receiptFooter: '',
      receiptFooterAr: '',
      receiptFooterEn: '',
      requireCustomerEmail: false,
      requireCustomerBirthDate: false,
      requireCustomerPhone: false,
      requireCustomerAddress: false,
      requireCustomerTaxId: false,
      loyaltyEnabled: false,
      loyaltyPointsPerUnit: 0,
      loyaltyPointValue: 0,
      loyaltyMinRedeemPoints: 0,
    });
    const askedSave = await refusedThenAccepted(() => page.save(), updateStore);
    expect(askedSave.title).toContain('Boutique Principale');

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.docs.set({
      texts: {},
      wordings: [],
      email: '',
      website: '',
      bankDetails: '',
      changesPerYear: 5,
      logoUrl: null,
      titleLanguages: [],
      itemName: '',
      itemNameBeneath: '',
    });
    await refusedThenAccepted(() => page.saveEditor(), put);
  });

  it('branches', async () => {
    const createBranch = jasmine.createSpy().and.resolveTo({});
    const page = make(BackofficeBranchesComponent, [{ provide: AdminService, useValue: api({ createBranch }) }]);
    page.form.nameFr = 'Succursale Oran';
    const asked = await refusedThenAccepted(() => page.save(), createBranch);
    expect(asked.title).toContain('Succursale Oran');
  });

  it('devices', async () => {
    const createDevice = jasmine.createSpy().and.resolveTo({});
    const page = make(BackofficeDevicesComponent, [{ provide: AdminService, useValue: api({ createDevice }) }]);
    page.form.code = 'CAISSE-1';
    page.form.branchId = 'b1';
    const asked = await refusedThenAccepted(() => page.save(), createDevice);
    expect(asked.title).toContain('CAISSE-1');
  });

  it('users', async () => {
    const createUser = jasmine.createSpy().and.resolveTo({ fullName: 'Amine T.', username: 'amine' });
    const linkUserEmployee = jasmine.createSpy().and.resolveTo({ fullName: 'Amine T.' });
    const resetPassword = jasmine.createSpy().and.resolveTo({});
    const page = make(BackofficeUsersComponent, [
      { provide: AdminService, useValue: api({ createUser, linkUserEmployee, resetPassword }) },
      { provide: HrApi, useValue: api({ employees: () => Promise.resolve({ rows: [] }) }) },
    ]);
    page.employeeId = 'e1';
    page.password = '12345678';
    page.branchId = 'b1';
    page.employees.set([{ id: 'e1', fullName: 'Amine T.', number: '100', branchId: 'b1' } as any]);
    const askedSave = await refusedThenAccepted(() => page.save(), createUser);
    expect(askedSave.title).toContain('Amine T.');

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.editingId.set('u1');
    page.linkEmployeeId = 'e1';
    const askedLink = await refusedThenAccepted(() => page.link(), linkUserEmployee);
    expect(askedLink.confirmLabel).toBe(page.i18n.t('backoffice.users.link.action'));

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.credentialTarget.set({ id: 'u1', username: 'amine' } as any);
    page.credentialKind.set('password');
    page.credentialValue = 'secret123';
    const askedCred = await refusedThenAccepted(() => page.submitCredential(), resetPassword);
    expect(askedCred.confirmLabel).toBe(page.i18n.t('common.save'));
  });

  it('payment methods', async () => {
    const createPaymentMethod = jasmine.createSpy().and.resolveTo({});
    const page = make(PaymentMethodsComponent, [{ provide: AdminService, useValue: api({ createPaymentMethod }) }]);
    page.form.key = 'virement';
    page.form.nameFr = 'Virement bancaire';
    const asked = await refusedThenAccepted(() => page.save(), createPaymentMethod);
    expect(asked.title).toContain('Virement bancaire');
  });

  it('cash boxes', async () => {
    const remit = jasmine.createSpy().and.resolveTo({});
    const page = make(CashBoxesComponent, [{ provide: AdminService, useValue: api({ remit }) }]);
    page.allBoxes.set([
      { id: 'box-1', nameFr: 'Caisse 1', nameAr: '', nameEn: '', branchId: 'b1', balance: 5000, isMain: false } as any,
      { id: 'box-2', nameFr: 'Coffre', nameAr: '', nameEn: '', branchId: 'b1', balance: 0, isMain: true } as any,
    ]);
    page.remittance.fromCashBoxId = 'box-1';
    page.remittance.toCashBoxId = 'box-2';
    page.remittance.amount = 2000;
    const asked = await refusedThenAccepted(() => page.remit(), remit);
    expect(asked.confirmLabel).toBe(page.i18n.t('treasury.recordRemittance'));
  });
});

describe('confirm before acting — saves (treasury and receivables)', () => {
  it('PaymentVoucherFormComponent', async () => {
    const create = jasmine.createSpy().and.resolveTo({});
    const page = make(PaymentVoucherFormComponent, [
      { provide: PaymentVouchersApi, useValue: api({ create }) },
      { provide: ReceivablesApi, useValue: api() },
    ]);
    page.branchId.set('b1');
    page.methodKey.set('cash');
    page.amount.set(500);
    page.category.set('Maintenance');
    page.payeeName.set('Someone');
    await refusedThenAccepted(() => page.save(), create);
  });

  it('ReceivableCreditNoteFormComponent', async () => {
    const getInvoice = jasmine.createSpy().and.resolveTo({ id: 'i1', taxMode: 'TaxFree', lines: [{ id: 'l1', qty: 2, creditedQty: 0, lineNet: 100, taxRate: 0 }] });
    const createCreditNote = jasmine.createSpy().and.resolveTo({});
    const page = make(ReceivableCreditNoteFormComponent, [{ provide: ReceivablesApi, useValue: api({ getInvoice, createCreditNote }) }], { invoiceId: 'i1', customerId: 'c1' });
    await page.ngOnInit();
    page.reason.set('Wrong size');
    page.acknowledged.set(true);
    await refusedThenAccepted(() => page.save(), createCreditNote);
  });

  it('ReceivableReceiptFormComponent', async () => {
    const createReceipt = jasmine.createSpy().and.resolveTo({});
    const page = make(ReceivableReceiptFormComponent, [{ provide: ReceivablesApi, useValue: api({ createReceipt }) }]);
    page.customer.set('c1');
    page.branchId.set('b1');
    page.methodKey.set('cash');
    page.amount.set(500);
    await refusedThenAccepted(() => page.save(), createReceipt);
  });

  it('AwaitingCollectionComponent', async () => {
    const settlePayment = jasmine.createSpy().and.resolveTo({});
    const page = make(AwaitingCollectionComponent, [
      { provide: AdminService, useValue: api({ settlePayment }) },
      { provide: ReceivablesApi, useValue: api() },
      // The list is read a page at a time from the server (owner, 2026-11-17).
      { provide: FinancePagesApi, useValue: api() },
    ]);
    page.bouncing.set({ id: 'p1', source: 'sale', reference: 'SALE-1', amount: 500 } as any);
    page.reason.set('Bank refused');
    await refusedThenAccepted(() => page.confirmBounce(), settlePayment);
  });
});

describe('confirm before acting — saves (platform and account)', () => {
  it('change password', async () => {
    const changePassword = jasmine.createSpy().and.resolveTo({ ok: true });
    const page = make(ChangePasswordComponent, [
      {
        provide: AuthService,
        useValue: api({
          currentUser: signal({ id: 'u1', username: 'nadia', fullName: 'Nadia Benali' }),
          changePassword,
        }),
      },
      { provide: ConnectivityService, useValue: api({ online: signal(true), probe: () => Promise.resolve(true) }) },
    ]);
    page.current = 'current123';
    page.next = 'newpassword123';
    page.confirm = 'newpassword123';
    await refusedThenAccepted(() => page.submit(), changePassword);
  });

  it('create company', async () => {
    const createCompany = jasmine.createSpy().and.resolveTo({ id: 'acme', adminUsername: 'admin', bootstrapPassword: 'secret' });
    const page = make(CompanyCreateComponent, [{ provide: PlatformService, useValue: api({ createCompany }) }]);
    page.id = 'acme';
    page.hosts = 'acme.pos.dz';
    page.reason = 'New company';
    await refusedThenAccepted(() => page.submit(), createCompany);
  });

  it('company hosting', async () => {
    const updateHosts = jasmine.createSpy().and.resolveTo();
    const company = { id: 'acme', hosts: ['old.pos.dz'], source: 'Portal', state: 'Active' };
    const page = make(CompanyHostingComponent, [{ provide: PlatformService, useValue: api({ updateHosts }) }], { company });
    page.hosts = 'new.pos.dz';
    page.reason = 'Domain change';
    await refusedThenAccepted(() => page.saveHosts(), updateHosts);
  });

  it('integrations', async () => {
    const saveIntegrations = jasmine.createSpy().and.resolveTo();
    const initial = {
      revision: 1,
      hasWebhookSecret: false,
      settings: {
        card: { mode: 'None', requireCardReference: false },
        sms: { enabled: false, url: '', method: 'POST', tokenHeader: '', contentType: '', bodyTemplate: '' },
        email: { enabled: false, host: '', port: 25, user: '', from: '', useTls: false },
        webhook: { enabled: false, url: '' },
      },
    };
    const page = make(
      IntegrationsEditorComponent,
      [{ provide: PlatformService, useValue: api({ saveIntegrations, integrations: () => Promise.resolve(initial) }) }],
      { companyId: 'acme' },
    );
    page.loaded.set(initial);
    page.form.set(structuredClone(initial.settings));
    page.reason = 'Webhook update';
    await refusedThenAccepted(() => page.save(), saveIntegrations);
  });

  it('language packs', async () => {
    const saveLanguagePack = jasmine.createSpy().and.resolveTo();
    const page = make(LanguagePacksComponent, [{ provide: PlatformService, useValue: api({ saveLanguagePack }) }]);
    const pack = { code: 'kab', label: 'Taqbaylit', rtl: false, fallbackLanguage: 'en', strings: { 'app.title': 'Taqbaylit' } };
    page.pending.set(pack);
    page.reason = 'Upload pack';
    await refusedThenAccepted(() => page.save(pack), saveLanguagePack);
  });

  it('regional settings', async () => {
    const current = {
      countryCode: 'DZ',
      currencyCode: 'DZD',
      currencyDecimals: 2,
      languages: ['fr'],
      defaultLanguage: 'fr',
      uiFont: 'system',
      currencySymbols: {},
      currencySymbolFirst: false,
      timeZone: 'Africa/Algiers',
      cashRoundingIncrement: 0,
      cashRoundingMode: 'Nearest' as const,
      taxRates: [19],
      taxRateLabels: [],
      defaultTaxRate: 19,
      pricesIncludeTax: true,
      fiscalIds: [],
      customerTaxIdLabels: {},
      receiptSections: ['lines', 'totals'],
      receiptLegalText: {},
      paymentMethods: ['Cash'],
      cashDenominations: [],
    };
    const page = make(RegionalEditorComponent, [], { current });
    const saved = spyOn(page.saved, 'emit');
    page.form.set(current as any);
    page.rates = '19';
    page.reason = 'Tax change';
    await refusedThenAccepted(() => page.submit(), saved);
  });
});

describe('differsFromBlank', () => {
  it('ignores the fields named, and sees any other change', () => {
    const blank = { key: 1, qty: 1, name: '' };
    expect(differsFromBlank({ key: 9, qty: 1, name: '' }, blank, ['key'])).toBeFalse();
    expect(differsFromBlank({ key: 1, qty: 2, name: '' }, blank)).toBeTrue();
  });
});

describe('confirm before acting — saves (till)', () => {
  it('register: removing a line from the cart', async () => {
    const remove = jasmine.createSpy('remove');
    const page = make(RegisterComponent, [{ provide: PosService, useValue: api({ remove, items: signal([]) }) }]);
    const asked = await refusedThenAccepted(() => page.removeLine('p1'), remove);
    expect(asked.title).toBe(TestBed.inject(I18nService).t('confirm.line.title'));
  });

  it('sales: confirming a refund', async () => {
    const refund = jasmine.createSpy('refund').and.resolveTo({ reference: 'REF-1' });
    const page = make(SalesComponent, [
      {
        provide: PosService,
        useValue: api({
          refund,
          recentSales: () => Promise.resolve([]),
          refundedSaleIds: () => Promise.resolve(new Set()),
        }),
      },
    ]);
    page.openRefund({
      id: 's1',
      reference: 'S-001',
      status: 'Completed',
      paymentMethod: 'Cash',
      payments: [],
      lines: [{ id: 'l1', nameSnapshot: 'Item', qty: 2, lineTotalTtc: 500 }],
    } as unknown as Sale);
    const asked = await refusedThenAccepted(() => page.confirmRefund(), refund);
    expect(asked.title).toContain(TestBed.inject(I18nService).t('refund.confirm'));
  });

  it('send-receipt: sending a receipt', async () => {
    const sendReceipt = jasmine.createSpy('sendReceipt').and.resolveTo();
    const page = make(
      SendReceiptComponent,
      [{ provide: IntegrationsService, useValue: api({ sendReceipt, channels: signal(['sms']) }) }],
      { sale: { id: 's1' } as unknown as Sale },
    );
    page.to = '0555123456';
    const asked = await refusedThenAccepted(() => page.send(), sendReceipt);
    expect(asked.title).toContain(TestBed.inject(I18nService).t('receiptSend.send'));
  });

  it('sync: full resync', async () => {
    const fullResync = jasmine.createSpy('fullResync').and.resolveTo();
    const page = make(SyncStatusComponent, [
      { provide: SyncService, useValue: api({ fullResync, refreshStats: () => Promise.resolve() }) },
      { provide: PosService, useValue: api({ loadCatalog: () => Promise.resolve(), recentSales: () => Promise.resolve([]) }) },
    ]);
    const asked = await refusedThenAccepted(() => page.full(), fullResync);
    expect(asked.title).toContain(TestBed.inject(I18nService).t('sync.fullResync'));
  });
});

describe('confirm before acting — saves (customers and invoices)', () => {
  it('customers: saving the customer', async () => {
    const updateCustomer = jasmine.createSpy().and.resolveTo({});
    const page = make(BackofficeCustomersComponent, [
      { provide: AdminService, useValue: api({ updateCustomer }) }
    ]);
    page.edit({ id: 'c1', kind: 'Company', fullName: 'SARL Test', creditLimit: 0, balance: 0, loyaltyPoints: 0 } as any);
    page.form.fullName = 'SARL Test';
    const asked = await refusedThenAccepted(() => page.save(), updateCustomer);
    expect(asked.title).toContain('SARL Test');
  });

  it('customers: changing the representative', async () => {
    const changeRepresentative = jasmine.createSpy().and.resolveTo([]);
    const page = make(BackofficeCustomersComponent, [
      { provide: AdminService, useValue: api({ changeRepresentative }) }
    ]);
    page.edit({ id: 'c1', kind: 'Company', fullName: 'SARL Test', creditLimit: 0, balance: 0, loyaltyPoints: 0 } as any);
    page.openChange();
    page.repFullName.set('Nouveau Gérant');
    const asked = await refusedThenAccepted(() => page.confirmChange(), changeRepresentative);
    expect(asked.confirmLabel).toBe(page.i18n.t('cust.rep.confirm'));
  });

  it('customer opening balance: correcting', async () => {
    const correctOpeningBalance = jasmine.createSpy().and.resolveTo();
    const page = make(CustomerOpeningBalanceComponent, [
      { provide: AdminService, useValue: api({ correctOpeningBalance }) },
      { provide: SalesPagesApi, useValue: api() },
      { provide: AuthService, useValue: { currentUser: () => ({ role: 'Admin', capabilities: ['customers.opening_balance'] }) } },
    ], { party: { id: 'c1', openingBalance: 1000, openingBalanceOn: '2026-10-01T00:00:00Z', openingBalanceSetAtUtc: '2026-10-01T00:00:00Z', openingBalanceSetBy: 'u1' } as any });
    await Promise.resolve();
    const drive = async () => {
      page.startCorrection();
      page.patchAction({ amount: 2000, direction: 'owesUs', on: page.today });
      page.reason.set('Erreur de frappe');
      await page.saveCorrection({ id: 'c1' } as any);
    };
    const asked = await refusedThenAccepted(drive, correctOpeningBalance);
    expect(asked.confirmLabel).toBe(page.i18n.t('cust.opening.correction.save'));
  });

  it('receivable invoice form: saving', async () => {
    const createInvoice = jasmine.createSpy().and.resolveTo({});
    const page = make(ReceivableInvoiceFormComponent, [
      { provide: ReceivablesApi, useValue: api({ createInvoice }) },
      { provide: RentalsApi, useValue: api() },
      { provide: AdminService, useValue: api() },
    ], {
      customerId: 'cust-1',
      customers: [{ id: 'cust-1', fullName: 'Client Test', code: 'Cust-01', creditLimit: 5000, balance: 1000 }]
    });
    await page.ngOnInit();
    page.pickSale('sale-1');
    page.setSettlement('deferred');
    const asked = await refusedThenAccepted(() => page.save(), createInvoice);
    expect(asked.confirmLabel).toBe(page.i18n.t('inv.form.save'));
  });
});
describe('confirm before acting — saves (HR and payroll)', () => {
  it('advances', async () => {
    const grantAdvance = jasmine.createSpy().and.resolveTo({ advance: { number: 'ADV-1' } });
    const page = make(PayrollAdvancesComponent, [
      { provide: WageMoneyApi, useValue: api({ grantAdvance }) },
      { provide: AdminService, useValue: api() },
    ]);
    // An advance is granted at a branch (owner, 2026-11-09): chosen first.
    page.grantBranchId.set('b1');
    page.form.employeeId = 'e1';
    page.form.amount = 1000;
    page.form.payoutMethod = 'Bank';
    const asked = await refusedThenAccepted(() => page.grant(), grantAdvance);
    expect(asked.confirmLabel).toBe(page.i18n.t('hr.adv.grantAction'));
  });

  it('attendance day grid', async () => {
    const saveDays = jasmine.createSpy().and.resolveTo({ employeeId: 'e1', days: [] });
    const page = make(
      HrDayGridComponent,
      [{ provide: HrApi, useValue: api({ days: () => Promise.resolve({ employeeId: 'e1', days: [] }), saveDays }) }],
      { employeeId: 'e1', year: 2026, month: 10 },
    );
    page.grid.set({ employeeId: 'e1', days: [] } as any);
    page.setStatus('2026-10-01', 'Present');
    const asked = await refusedThenAccepted(() => page.save(), saveDays);
    expect(asked.confirmLabel).toBe(page.i18n.t('confirm.save.ok'));
  });

  it('attendance sheet', async () => {
    const saveSheet = jasmine.createSpy().and.resolveTo({ rows: [] });
    const page = make(HrAttendanceSheetComponent, [
      { provide: HrApi, useValue: api({ sheet: () => Promise.resolve({ rows: [], overtimeRules: [] }), saveSheet }) },
      { provide: AdminService, useValue: api({ listBranches: () => Promise.resolve([]) }) },
    ]);
    const row = {
      employeeId: 'e1',
      employeeNumber: '101',
      fullName: 'Farid B.',
      readOnly: false,
      daysPresent: 20,
      paidLeaveDays: 0,
      unpaidAbsenceDays: 0,
      overtime: [],
    };
    page.sheet.set({
      branchId: 'b1',
      periodYear: 2026,
      periodMonth: 10,
      overtimeRules: [],
      rows: [row],
      total: 1,
    } as any);
    page.loaded.set('e1', row as any);
    page.type(0, 0, '22');
    const asked = await refusedThenAccepted(() => page.save(), saveSheet);
    expect(asked.confirmLabel).toBe(page.i18n.t('confirm.save.ok'));
  });

  it('employees', async () => {
    const createEmployee = jasmine.createSpy().and.resolveTo({ id: 'e1', number: '101' });
    const markLeft = jasmine.createSpy().and.resolveTo({ id: 'e1', number: '101' });
    const page = make(HrEmployeesComponent, [
      { provide: HrApi, useValue: api({ createEmployee, markLeft }) },
      { provide: PayrollApi, useValue: api({ settings: () => Promise.resolve({ allowances: [] }) }) },
      { provide: AdminService, useValue: api() },
      { provide: AuthService, useValue: { currentUser: () => ({ role: 'Admin' }) } },
    ]);
    page.form.firstName = 'Farid';
    page.form.familyName = 'Belkacem';
    page.form.branchId = 'b1';
    page.form.hiredOn = '2026-01-01';
    page.form.baseRate = 50000;
    const askedSave = await refusedThenAccepted(() => page.save(), createEmployee);
    expect(askedSave.title).toContain('Farid Belkacem');

    const router = TestBed.inject(Router);
    const navigate = spyOn(router, 'navigate').and.resolveTo(true);
    answer.calls.reset();
    answer.and.resolveTo(false);
    const askedAccess = await refusedThenAccepted(() => page.giveAccess({ id: 'e1' } as any), navigate);
    expect(askedAccess.confirmLabel).toBe(page.i18n.t('hr.employees.giveAccess'));

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.editing.set({ id: 'e1' } as any);
    page.leftOn.set('2026-10-10');
    const askedLeave = await refusedThenAccepted(() => page.recordLeaving(), markLeft);
    expect(askedLeave.danger).toBeTrue();
    expect(askedLeave.confirmLabel).toBe(page.i18n.t('hr.employees.leaveAction'));
  });

  it('payroll run', async () => {
    const compute = jasmine.createSpy().and.resolveTo({ run: { id: 'r1', status: 'Draft' } });
    const upsertAdjustment = jasmine.createSpy().and.resolveTo({ adjustment: {} });
    const page = make(
      PayrollRunComponent,
      [
        { provide: PayrollRunsApi, useValue: api({ compute, upsertAdjustment, read: () => Promise.resolve({ run: { id: 'r1', status: 'Draft' } }) }) },
        { provide: PayrollApi, useValue: api({ settings: () => Promise.resolve({}) }) },
        { provide: PrintService, useValue: api() },
      ],
      { runId: 'r1' },
    );
    await refusedThenAccepted(() => page.run('compute'), compute);

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.draft.set({ key: 'bonus', amount: 500, reason: 'Good work', inSocialBase: true, inTaxBase: true });
    await refusedThenAccepted(() => page.saveAdjustment({ employeeId: 'e1' } as any), upsertAdjustment);
  });

  it('payroll runs', async () => {
    const create = jasmine.createSpy().and.resolveTo({ id: 'r1', number: 'RUN-1' });
    const page = make(PayrollRunsComponent, [
      { provide: PayrollRunsApi, useValue: api({ create, runs: () => Promise.resolve([]) }) },
      { provide: AdminService, useValue: api({ listBranches: () => Promise.resolve([]) }) },
    ]);
    page.newBranchId.set('b1');
    page.newPeriod.set('2026-10');
    const asked = await refusedThenAccepted(() => page.create(), create);
    expect(asked.confirmLabel).toBe(page.i18n.t('hr.runs.create'));
  });

  it('payroll settings', async () => {
    const validSettings = {
      workingDaysPerMonth: 26,
      hoursPerDay: 8,
      allowances: [],
      overtimeRules: [],
      incomeTaxBrackets: [],
      socialEmployeeRate: 9,
      socialEmployerRate: 26,
      minimumWage: 20000,
    };
    const saveCompany = jasmine.createSpy().and.resolveTo({ settings: validSettings, revision: 1 });
    const saveStatutory = jasmine.createSpy().and.resolveTo({ settings: validSettings, revision: 1 });
    const page = make(PayrollSettingsComponent, [
      {
        provide: PayrollApi,
        useValue: api({
          settings: () =>
            Promise.resolve({
              settings: validSettings,
              revision: 1,
              owners: {},
              canEditCompany: true,
              canEditStatutory: true,
            }),
          saveCompany,
          saveStatutory,
        }),
      },
    ]);
    page.view.set({
      settings: validSettings as any,
      revision: 1,
      owners: {},
      canEditCompany: true,
      canEditStatutory: true,
    } as any);
    page.editing.set(validSettings as any);
    const askedCompany = await refusedThenAccepted(() => page.saveCompany(), saveCompany);
    expect(askedCompany.confirmLabel).toBe(page.i18n.t('confirm.save.ok'));

    answer.calls.reset();
    answer.and.resolveTo(false);
    const askedStatutory = await refusedThenAccepted(() => page.saveStatutory(), saveStatutory);
    expect(askedStatutory.confirmLabel).toBe(page.i18n.t('confirm.save.ok'));
  });

  it('statutory payments', async () => {
    const recordStatutoryPayment = jasmine.createSpy().and.resolveTo({ payment: { accountCode: '431', amount: 5000 } });
    const page = make(StatutoryPaymentsComponent, [
      { provide: WageMoneyApi, useValue: api({ recordStatutoryPayment }) },
      { provide: AdminService, useValue: api() },
    ]);
    page.form.branchId = 'b1';
    page.form.accountCode = '431';
    page.form.amount = 5000;
    page.form.payoutMethod = 'Bank';
    page.form.periodYear = null;
    page.form.periodMonth = null;
    const asked = await refusedThenAccepted(() => page.record(), recordStatutoryPayment);
    expect(asked.confirmLabel).toBe(page.i18n.t('hr.stat.recordAction'));
  });

  it('wage payments', async () => {
    const pay = jasmine.createSpy().and.resolveTo({ payment: { payment: { number: 'PAY-1', amount: 5000 } } });
    const page = make(WagePaymentsComponent, [
      { provide: WageMoneyApi, useValue: api({ pay, runs: () => Promise.resolve([]) }) },
      { provide: AdminService, useValue: api() },
    ]);
    page.run.set({ id: 'r1', status: 'Approved', net: 10000, paidTotal: 5000 } as any);
    page.method.set('Bank');
    page.mode.set('all');
    const asked = await refusedThenAccepted(() => page.pay(), pay);
    expect(asked.confirmLabel).toBe(page.i18n.t('hr.wagepay.payAction'));
  });
});

describe('confirm before acting — saves (rentals)', () => {
  it('rental contract form', async () => {
    const createSite = jasmine.createSpy().and.resolveTo({ id: 's1', name: 'Chantier', isActive: true });
    const createContract = jasmine.createSpy().and.resolveTo({ id: 'k1' });
    const page = make(
      RentalContractFormComponent,
      [
        { provide: RentalsApi, useValue: api({ createSite, createContract }) },
        { provide: AdminService, useValue: api() },
        { provide: CatalogStore, useValue: api({ active: signal([]), all: signal([]) }) },
      ],
      {
        customers: [{ id: 'c1', kind: 'Person', fullName: 'Ali' }],
        settings: { depositEnabled: false },
      },
    );
    page.customerId.set('c1');
    page.newSiteName.set('Chantier');
    const askedSite = await refusedThenAccepted(() => page.createSite(), createSite);
    expect(askedSite.confirmLabel).toBe(page.i18n.t('confirm.save.ok'));

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.branchId.set('b1');
    page.lines.set([
      {
        key: 1,
        productId: 'p1',
        qty: 1,
        durationDays: 30,
        rentUnitPrice: 100,
        damageUnitPrice: 100,
        overdueUnitPrice: null,
        parentKey: null,
      },
    ]);
    const askedSave = await refusedThenAccepted(() => page.save(), createContract);
    expect(askedSave.confirmLabel).toBe(page.i18n.t('confirm.save.ok'));
  });

  it('rental extend new', async () => {
    const extendAsNew = jasmine.createSpy().and.resolveTo({ id: 'k2' });
    const page = make(RentalExtendNewComponent, [{ provide: RentalsApi, useValue: api({ extendAsNew }) }], {
      contractId: 'k1',
    });
    const asked = await refusedThenAccepted(() => page.save(), extendAsNew);
    expect(asked.confirmLabel).toBe(page.i18n.t('rent.extendNew.save'));
  });

  it('rental issue note', async () => {
    const issue = jasmine.createSpy().and.resolveTo({ id: 'in1' });
    const readContract = jasmine.createSpy().and.resolveTo({
      contract: {
        id: 'k1',
        isLocked: true,
        branchId: 'b1',
        signatoryName: 'Amine',
        lines: [{ id: 'l1', sku: 'S1', name: 'P1', qty: 5, issuedQty: 0, leftToIssueQty: 5, rentUnitPrice: 100 }],
      },
      extensions: [],
    });
    const page = make(
      RentalIssueNoteComponent,
      [
        { provide: RentalsApi, useValue: api({ issue, readContract }) },
        { provide: AdminService, useValue: api() },
      ],
      { contractId: 'k1' },
    );
    await page.ngOnInit();
    page.branchId.set('b1');
    page.customerTransport.set(true);
    page.setQty('l1', 1);
    const asked = await refusedThenAccepted(() => page.save(), issue);
    expect(asked.confirmLabel).toBe(page.i18n.t('rent.issue.raise'));
  });

  it('rental return note', async () => {
    const returnSpy = jasmine.createSpy('return').and.resolveTo({ id: 'rn1' });
    const previewReturn = jasmine.createSpy('previewReturn').and.callFake(async (_id: string, request: any) => ({
      rows: [{ returnLineIndex: 0, contractLineId: 'l1', issueNoteId: 'in1', issueNumber: 'ISS-1',
        issuedAtUtc: '2026-10-13T12:00:00Z', qty: request.lines[0].qtyOk, physicalQty: request.lines[0].qtyOk,
        sameDayUsageRequired: true, sameDayUsage: request.lines[0].sameDayUsage, chargeableDays: 1,
        dailyUnitRate: 10, rentalCharge: 20 }], charges: [], chargeTotal: 20, unusedRent: 0, customerBalance: 0, previouslyPaid: 0,
      customerCredit: 0, reliefDebtReduction: 0, reliefPaymentVoucher: 0, isClosed: false,
      late: null, canConfirm: true, problems: [],
    }));
    const readContract = jasmine.createSpy().and.resolveTo({
      contract: {
        id: 'k1',
        branchId: 'b1',
        lines: [
          {
            id: 'l1',
            sku: 'S1',
            name: 'P1',
            stillOutQty: 5,
            issuedQty: 5,
            settledQty: 0,
            durationDays: 30,
            rentUnitPrice: 10,
            damageUnitPrice: 100,
            overdueUnitPrice: 15,
            parentLineId: null,
          },
        ],
      },
      issueNotes: [
        {
          id: 'in1',
          issuedAtUtc: '2026-10-13T12:00:00Z',
          lines: [{ contractLineId: 'l1', qty: 5 }],
        },
      ],
      returnNotes: [],
      extensions: [],
    });
    answer = jasmine.createSpy('ask').and.resolveTo(false);
    TestBed.configureTestingModule({ providers: [provideRouter([]), PageChrome,
      { provide: ConfirmService, useValue: { ask: answer } },
      { provide: RentalsApi, useValue: api({ return: returnSpy, readContract, previewReturn }) },
      { provide: AdminService, useValue: api() }] });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    const fixture = TestBed.createComponent(RentalReturnNoteComponent);
    fixture.componentRef.setInput('contractId', 'k1');
    fixture.componentRef.setInput('settings', { overdueMultiplier: 1.5, depositEnabled: false, taxRate: 0, dayCutoffHour: null });
    const page = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    page.customerTransport.set(true);
    page.driverName.set('Driver');
    page.driverPhone.set('123');
    page.truckNumber.set('Truck');
    page.patch('l1', { qtyOk: 2 });
    page.patch('l1', { sameDayUsage: 'Used' });
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 250));
    fixture.detectChanges();
    expect(previewReturn).toHaveBeenCalled();
    expect(page.canSave()).toBeTrue();
    const asked = await refusedThenAccepted(() => page.save(), returnSpy);
    expect(asked.confirmLabel).toBe(page.i18n.t('rent.return.raise'));
  });

  it('rental settings', async () => {
    const updateSettings = jasmine.createSpy().and.resolveTo({
      overdueMultiplier: 1.5,
      depositEnabled: false,
      taxRate: 0,
      dayCutoffHour: null,
      minimumDays: 0,
    });
    const setProductMultiplier = jasmine.createSpy().and.resolveTo({
      productId: 'p1',
      sku: 'ECH',
      rentalUnitPrice: 100,
    });
    const page = make(
      RentalSettingsComponent,
      [
        {
          provide: RentalsApi,
          useValue: api({ updateSettings, setProductMultiplier, listProductMultipliers: () => Promise.resolve([]) }),
        },
      ],
      {
        settings: { overdueMultiplier: 1.5, depositEnabled: false, taxRate: 0, dayCutoffHour: null, minimumDays: 0 },
        products: [{ id: 'p1', sku: 'ECH', nameFr: 'Échafaudage' }],
      },
    );
    await page.ngOnInit();

    const askedSave = await refusedThenAccepted(() => page.save(), updateSettings);
    expect(askedSave.confirmLabel).toBe(page.i18n.t('confirm.save.ok'));

    answer.calls.reset();
    answer.and.resolveTo(false);
    page.pickItem('p1');
    page.itemRentalUnitPrice.set(100);
    const askedItem = await refusedThenAccepted(() => page.saveItem(), setProductMultiplier);
    expect(askedItem.confirmLabel).toBe(page.i18n.t('confirm.save.ok'));
  });
});

