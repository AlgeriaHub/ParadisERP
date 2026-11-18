import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminCustomer, AdminPaymentMethod, AdminSale, AdminService } from '../../../core/admin.service';
import { AuthService } from '../../../core/auth.service';
import { PosUser } from '../../../core/models';
import { ConfirmService } from '../../../ui/confirm.service';
import { RentalsApi } from '../rentals/rentals-api.service';
import { ReceivableInvoiceFormComponent } from './receivable-invoice-form.component';
import { Invoice, ReceivablesApi } from './receivables-api.service';

describe('ReceivableInvoiceFormComponent', () => {
  let fixture: ComponentFixture<ReceivableInvoiceFormComponent>;
  let component: ReceivableInvoiceFormComponent;
  let mockApi: jasmine.SpyObj<ReceivablesApi>;
  let mockAdmin: jasmine.SpyObj<AdminService>;
  let mockRentals: jasmine.SpyObj<RentalsApi>;
  let currentUserSignal: ReturnType<typeof signal<PosUser | null>>;

  const methods: AdminPaymentMethod[] = [
    {
      id: 'm1',
      key: 'cash',
      behavior: 'Cash',
      isActive: true,
      sortOrder: 1,
      nameFr: 'Espèces',
      nameAr: 'نقداً',
      nameEn: 'Cash',
      requiresConfirmation: false,
      affectsCashDrawer: true,
      requiresReference: false,
      requiresInstrumentDetails: false,
      isBuiltIn: true,
    },
    {
      id: 'm2',
      key: 'cheque',
      behavior: 'Generic',
      isActive: true,
      sortOrder: 2,
      nameFr: 'Chèque',
      nameAr: 'شيك',
      nameEn: 'Cheque',
      requiresConfirmation: true,
      affectsCashDrawer: false,
      requiresReference: false,
      requiresInstrumentDetails: true,
      isBuiltIn: false,
    },
  ];

  const testSale: AdminSale = {
    id: 'sale-1',
    customerId: 'cust-1',
    paymentMethod: 'Credit',
    status: 'Completed',
    grandTotalTtc: 1000,
    paidAmount: 200,
    soldAtUtc: '2026-10-10T10:00:00Z',
    reference: 'S-001',
  } as unknown as AdminSale;

  beforeEach(() => {
    currentUserSignal = signal<PosUser | null>(null);

    mockApi = jasmine.createSpyObj<ReceivablesApi>('ReceivablesApi', ['listInvoices', 'createInvoice']);
    mockApi.listInvoices.and.resolveTo([]);
    mockApi.createInvoice.and.resolveTo({
      invoice: { id: 'inv-1', number: 'INV-000001' } as unknown as Invoice,
      warning: null,
    });

    mockAdmin = jasmine.createSpyObj<AdminService>('AdminService', ['listPaymentMethods', 'listSales']);
    mockAdmin.listPaymentMethods.and.resolveTo(methods);
    mockAdmin.listSales.and.resolveTo([testSale]);

    mockRentals = jasmine.createSpyObj<RentalsApi>('RentalsApi', ['listContracts', 'listIssueNotes', 'listReturnNotes']);
    mockRentals.listContracts.and.resolveTo([]);
    mockRentals.listIssueNotes.and.resolveTo([]);
    mockRentals.listReturnNotes.and.resolveTo([]);

    TestBed.configureTestingModule({
      imports: [ReceivableInvoiceFormComponent],
      providers: [
        { provide: ReceivablesApi, useValue: mockApi },
        { provide: AdminService, useValue: mockAdmin },
        { provide: RentalsApi, useValue: mockRentals },
        { provide: AuthService, useValue: { currentUser: currentUserSignal } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });

    fixture = TestBed.createComponent(ReceivableInvoiceFormComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('customerId', 'cust-1');
    fixture.componentRef.setInput('customers', [
      { id: 'cust-1', fullName: 'Client Test', code: 'Cust-01', creditLimit: 5000, balance: 1000 } as unknown as AdminCustomer,
    ]);
    fixture.detectChanges();
  });

  it('offers sale and rental modes only, with no direct mode and no add-line button', () => {
    expect(component.MODES).toEqual(['sale', 'rental']);
    expect(component.mode()).toBe('sale');

    const compiled = fixture.nativeElement as HTMLElement;
    // No direct mode button/chip
    const modeButtons = compiled.querySelectorAll('.bo-chips button');
    expect(modeButtons.length).toBe(2);
    // No add-line button or item picker from direct mode
    expect(compiled.querySelector('#if-pick')).toBeNull();
    expect(compiled.querySelector('.bo-actions button')).toBeNull();
  });

  it('hides the untaxed select without the capability and shows it with it', async () => {
    // Without capability
    expect(component.canUntax()).toBeFalse();
    expect(fixture.nativeElement.querySelector('#if-tax')).toBeNull();

    // With capability
    currentUserSignal.set({
      id: 'u1',
      username: 'manager',
      capabilities: ['finance.invoices.untaxed'],
    } as unknown as PosUser);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(component.canUntax()).toBeTrue();
    expect(fixture.nativeElement.querySelector('#if-tax')).not.toBeNull();
  });

  it('refuses paid in full without a method and sends settlement full with the method', async () => {
    await fixture.whenStable();
    component.pickSale('sale-1');
    component.setSettlement('full');
    fixture.detectChanges();

    // Attempting to save without a settlement method fails
    await component.save();
    expect(component.methodInvalid()).toBeTrue();
    expect(mockApi.createInvoice).not.toHaveBeenCalled();

    // Selecting a method satisfies validation and sends settlement: 'full'
    component.setSettlementMethod('cash');
    fixture.detectChanges();
    expect(component.methodInvalid()).toBeFalse();

    await component.save();
    expect(mockApi.createInvoice).toHaveBeenCalledWith(
      jasmine.objectContaining({
        saleId: 'sale-1',
        settlement: 'full',
        settlementMethodKey: 'cash',
        paymentMethodKey: 'cash',
      }),
    );
  });

  it('sends settlement deferred and optional paymentMethodKey', async () => {
    await fixture.whenStable();
    component.pickSale('sale-1');
    component.setSettlement('deferred');
    component.setPaymentMethod('cheque');
    fixture.detectChanges();

    await component.save();
    expect(mockApi.createInvoice).toHaveBeenCalledWith(
      jasmine.objectContaining({
        saleId: 'sale-1',
        settlement: 'deferred',
        settlementMethodKey: null,
        paymentMethodKey: 'cheque',
      }),
    );
  });

  it('shows a one-line notice for a sale already fully paid at the till', async () => {
    const paidSale: AdminSale = {
      id: 'sale-paid',
      customerId: 'cust-1',
      paymentMethod: 'Mixed',
      status: 'Completed',
      grandTotalTtc: 1000,
      paidAmount: 1000,
      soldAtUtc: '2026-10-10T10:00:00Z',
      reference: 'S-PAID',
    } as unknown as AdminSale;

    component.sales.set([paidSale]);
    component.pickSale('sale-paid');
    fixture.detectChanges();

    expect(component.saleAlreadyPaid()).toBeTrue();
    const notice = fixture.nativeElement.querySelector('.notice.info');
    expect(notice).not.toBeNull();
  });
});
