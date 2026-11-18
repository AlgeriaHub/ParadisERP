import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminBranch, AdminStore } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { DEFAULT_REGIONAL } from '../../core/regional.service';
import { StockTransfer } from '../backoffice/stock/stock-transfers-api.service';
import { buildPrintContext, PrintContext } from './print-context';
import { PrintStockTransferComponent } from './print-stock-transfer.component';

describe('PrintStockTransferComponent', () => {
  let fixture: ComponentFixture<PrintStockTransferComponent>;
  let component: PrintStockTransferComponent;
  let printContext: PrintContext;

  const sampleTransfer: StockTransfer = {
    id: 't-1',
    number: 'TRF-000001',
    status: 'PartlyReceived',
    fromBranchId: 'b-alger',
    fromBranchName: 'Alger Depôt',
    toBranchId: 'b-oran',
    toBranchName: 'Oran Magasin',
    note: 'Note de transfert',
    dispatchedAtUtc: '2026-10-12T10:00:00Z',
    dispatchedByName: 'Admin User',
    closedAtUtc: null,
    qtyDispatched: 10,
    inTransit: 4,
    qtyShortPending: 1,
    canDecideShortfall: false,
    lines: [
      {
        id: 'l-1',
        productId: 'p-1',
        sku: 'SKU-001',
        nameAr: 'منتج 1',
        nameFr: 'Produit 1',
        nameEn: 'Product 1',
        qtyDispatched: 6,
        qtyReceived: 5,
        qtyLost: 0,
        qtyReturned: 0,
        inTransit: 1,
        unitCost: 100,
        qtyShortPending: 1,
        qtyShortReported: 1,
        qtyShortConfirmed: 0,
        qtyShortRejected: 0,
      },
      {
        id: 'l-2',
        productId: 'p-2',
        sku: 'SKU-002',
        nameAr: 'منتج 2',
        nameFr: 'Produit 2',
        nameEn: 'Product 2',
        qtyDispatched: 4,
        qtyReceived: 1,
        qtyLost: 0,
        qtyReturned: 0,
        inTransit: 3,
        unitCost: 200,
        qtyShortPending: 0,
        qtyShortReported: 0,
        qtyShortConfirmed: 0,
        qtyShortRejected: 0,
      },
    ],
    events: [
      {
        id: 'e-1',
        kind: 'Dispatch',
        atUtc: '2026-10-12T10:00:00Z',
        userName: 'Admin User',
        reason: null,
      },
      {
        id: 'e-2',
        kind: 'Receive',
        atUtc: '2026-10-12T14:00:00Z',
        userName: 'Oran Manager',
        reason: 'First batch arrived',
      },
      {
        id: 'e-3',
        kind: 'ShortfallReported',
        atUtc: '2026-10-12T14:00:00Z',
        userName: 'Oran Manager',
        reason: '1 carton missing',
      },
    ],
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PrintStockTransferComponent],
    }).compileComponents();

    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);

    printContext = buildPrintContext(i18n, DEFAULT_REGIONAL, 'fr', {
      store: {
        id: 'store-1',
        nameFr: 'SARL TEST',
        nameAr: '',
        nameEn: '',
        fiscalIds: {},
        receiptFooter: '',
      } as unknown as AdminStore,
      documents: null,
      branches: [
        { id: 'b-alger', nameAr: 'الجزائر', nameFr: 'Alger', nameEn: 'Algiers' } as AdminBranch,
        { id: 'b-oran', nameAr: 'وهران', nameFr: 'Oran', nameEn: 'Oran' } as AdminBranch,
      ],
      customers: [],
      methods: [],
      branchId: null,
    });

    fixture = TestBed.createComponent(PrintStockTransferComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('ctx', printContext);
    fixture.componentRef.setInput('transfer', sampleTransfer);
    fixture.detectChanges();
  });

  it('renders transfer header, title and number', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('TRF-000001');
    expect(el.textContent).toContain('Admin User');
    expect(el.textContent).toContain('Note de transfert');
  });

  it('resolves branch name from context and falls back to server name when unknown', () => {
    expect(component.branchName('b-alger', 'Fallback Alger')).toBe('Alger');
    expect(component.branchName('b-unknown', 'Fallback Unk')).toBe('Fallback Unk');
  });

  it('filters shortfallLines to only lines with qtyShortReported > 0', () => {
    expect(component.shortfallLines().length).toBe(1);
    expect(component.shortfallLines()[0].sku).toBe('SKU-001');
  });

  it('renders the shortfall table and shortfall tag when qtyShortPending > 0', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain(printContext.t('transfer.shortfall.tag'));
    expect(el.textContent).toContain(printContext.t('print.transfer.shortfalls'));
    expect(el.textContent).toContain('SKU-001');
  });

  it('omits shortfall table when no lines have reported shortfall', () => {
    const withoutShortfall: StockTransfer = {
      ...sampleTransfer,
      qtyShortPending: 0,
      events: [],
      lines: sampleTransfer.lines.map((l) => ({ ...l, qtyShortReported: 0, qtyShortPending: 0 })),
    };
    fixture.componentRef.setInput('transfer', withoutShortfall);
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(component.shortfallLines().length).toBe(0);
    expect(el.textContent).not.toContain(printContext.t('print.transfer.shortfalls'));
    expect(el.textContent).not.toContain(printContext.t('transfer.shortfall.tag'));
  });

  it('renders all transfer history events with user names and reasons', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain(printContext.t('transfer.event.Dispatch'));
    expect(el.textContent).toContain(printContext.t('transfer.event.Receive'));
    expect(el.textContent).toContain(printContext.t('transfer.event.ShortfallReported'));
    expect(el.textContent).toContain('1 carton missing');
    expect(el.textContent).toContain('First batch arrived');
  });

  it('renders signatures section for dispatched and received by', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain(printContext.t('print.transfer.sign.dispatchedBy'));
    expect(el.textContent).toContain(printContext.t('print.transfer.sign.receivedBy'));
  });
});
