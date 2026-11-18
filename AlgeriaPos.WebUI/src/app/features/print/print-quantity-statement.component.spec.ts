import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminBranch, AdminCustomer, AdminStore } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { DEFAULT_REGIONAL } from '../../core/regional.service';
import { QuantityStatement } from '../backoffice/rentals/rentals-api.service';
import { buildPrintContext, PrintContext } from './print-context';
import { PrintQuantityStatementComponent } from './print-quantity-statement.component';

describe('PrintQuantityStatementComponent', () => {
  let fixture: ComponentFixture<PrintQuantityStatementComponent>;
  let component: PrintQuantityStatementComponent;
  let printContext: PrintContext;

  const sampleStatement: QuantityStatement = {
    customerId: 'c-1',
    customerCode: 'CLI-0001',
    customerName: 'Entreprise Bâtiment',
    from: '2026-10-01',
    to: '2026-10-10',
    current: false,
    firstIssueOn: '2026-10-01',
    summary: {
      itemTypes: 1,
      issued: 20,
      returned: 5,
      missingDamaged: 1,
      withCustomer: 14,
      value: 14000,
      provisionalValue: 2000,
    },
    warnings: [
      {
        code: 'negativeBalance',
        contractNumber: 'CTR-001',
        qty: 2,
        productId: 'p-1',
        sku: 'ECHAF-01',
        name: 'Échafaudage',
        nameAr: 'سقالة',
        contractId: 'cnt-1',
        on: '2026-10-05',
      },
    ],
    items: [
      {
        productId: 'p-1',
        sku: 'ECHAF-01',
        name: 'Échafaudage',
        nameAr: 'سقالة',
        barcode: '123456',
        opening: 0,
        issued: 20,
        returned: 5,
        missingDamaged: 1,
        transferNet: 0,
        remaining: 14,
        noRent: false,
        rates: [{ kind: 'rent', unitPrice: 100 }],
        value: 14000,
        provisionalValue: 2000,
        negativeBalance: false,
        movements: [
          {
            on: '2026-10-01',
            atUtc: '2026-10-01T10:00:00Z',
            kind: 'issue',
            noteId: 'n-1',
            documentNumber: 'BL-001',
            contractId: 'cnt-1',
            contractNumber: 'CTR-001',
            branchId: 'b-alger',
            siteName: 'Chantier 1',
            qty: 20,
            returned: 0,
            missingDamaged: 0,
            balance: 20,
            unitPrice: 100,
          },
          {
            on: '2026-10-05',
            atUtc: '2026-10-05T14:00:00Z',
            kind: 'return',
            noteId: 'n-2',
            documentNumber: 'BR-001',
            contractId: 'cnt-1',
            contractNumber: 'CTR-001',
            branchId: 'b-alger',
            siteName: 'Chantier 1',
            qty: -5,
            returned: 5,
            missingDamaged: 0,
            balance: 15,
            unitPrice: 100,
          },
        ],
      },
    ],
    contracts: [
      {
        contractId: 'cnt-1',
        number: 'CTR-001',
        status: 'open',
        branchId: 'b-alger',
        siteId: 'site-1',
        siteName: 'Chantier 1',
        firstIssueOn: '2026-10-01',
        pricingCorrectionPending: false,
        value: 14000,
        provisionalValue: 2000,
        lines: [
          {
            lineId: 'l-1',
            productId: 'p-1',
            sku: 'ECHAF-01',
            name: 'Échafaudage',
            nameAr: 'سقالة',
            firstIssueOn: '2026-10-01',
            opening: 0,
            issued: 20,
            returned: 5,
            missingDamaged: 1,
            remaining: 14,
            rentUnitPrice: 100,
            noRent: false,
            rates: [{ kind: 'rent', unitPrice: 100 }],
            value: 14000,
            provisionalValue: 2000,
            transferredIn: 0,
            transferredOut: 0,
          },
        ],
      },
    ],
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PrintQuantityStatementComponent],
    }).compileComponents();

    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);

    printContext = buildPrintContext(i18n, DEFAULT_REGIONAL, 'fr', {
      store: {
        id: 'store-1',
        nameFr: 'SARL LOCATION',
        nameAr: '',
        nameEn: '',
        fiscalIds: {},
        receiptFooter: '',
      } as unknown as AdminStore,
      documents: null,
      branches: [
        { id: 'b-alger', nameAr: 'الجزائر', nameFr: 'Alger', nameEn: 'Algiers' } as AdminBranch,
      ],
      customers: [
        { id: 'c-1', code: 'CLI-0001', fullName: 'Entreprise Bâtiment' } as AdminCustomer,
      ],
      methods: [],
      branchId: null,
    });

    fixture = TestBed.createComponent(PrintQuantityStatementComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('ctx', printContext);
    fixture.componentRef.setInput('statement', sampleStatement);
    fixture.detectChanges();
  });

  it('renders customer information, title and customer code', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('CLI-0001');
    expect(el.textContent).toContain('Entreprise Bâtiment');
  });

  it('renders summary figures including issued, returned and withCustomer', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('20');
    expect(el.textContent).toContain('5');
    expect(el.textContent).toContain('14');
  });

  it('formats dates in ISO UTC format correctly', () => {
    expect(component.day('2026-10-01')).toBe('01/10/2026');
    expect(component.day('2026-10-10T15:30:00Z')).toBe('10/10/2026');
  });

  it('computes localized item and branch names', () => {
    expect(component.name({ name: 'Échafaudage', nameAr: 'سقالة' })).toBe('Échafaudage');
    expect(component.branchName('b-alger')).toBe('Alger');
    expect(component.branchName('b-missing')).toBe('—');
  });

  it('formats rates for rent rate, no-rent and multiple rates', () => {
    expect(component.rates(true, [])).toBe(printContext.t('qstmt.noRent'));
    expect(component.rates(false, [{ kind: 'rent', unitPrice: 150 }])).toContain('150.00');
    expect(
      component.rates(false, [
        { kind: 'rent', unitPrice: 150 },
        { kind: 'overdue', unitPrice: 120 },
      ])
    ).toContain('150.00');
  });

  it('renders items and contracts tables', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('ECHAF-01');
    expect(el.textContent).toContain('CTR-001');
    expect(el.textContent).toContain('Chantier 1');
  });

  it('renders movements table with signed quantities and balances', () => {
    expect(component.signed(20)).toBe('+20');
    expect(component.signed(-5)).toBe('-5');
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('BL-001');
    expect(el.textContent).toContain('BR-001');
  });
});
