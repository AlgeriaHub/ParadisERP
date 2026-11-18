import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminBranch } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ITEM_STATUS_TRANSLATIONS } from '../../../core/i18n/translations-item-status';
import { TRANSFER_TRANSLATIONS } from '../../../core/i18n/translations-transfers';
import { ItemRentedRow, ItemStatusDetail } from './item-status-api.service';
import { ItemStatusDetailComponent } from './item-status-detail.component';
import { ItemStatusPagesApi } from './item-status-pages.api';

describe('ItemStatusDetailComponent', () => {
  let fixture: ComponentFixture<ItemStatusDetailComponent>;
  let component: ItemStatusDetailComponent;

  const sampleDetail: ItemStatusDetail = {
    asOfUtc: '2026-10-12T15:00:00Z',
    scope: { kind: 'company', othersAvailableShown: false },
    item: {
      productId: 'p-1',
      sku: 'ECHAF-01',
      nameAr: 'سقالة',
      nameFr: 'Échafaudage',
      nameEn: 'Scaffolding',
      barcode: '123456789012',
      unit: 'pcs',
      isRentable: true,
      isActive: true,
      total: 50,
      warehouses: 30,
      branches: 20,
      available: 20,
      rented: 20,
      maintenance: 5,
      scrap: 2,
      inTransit: 3,
      stillOut: 18,
      unexplainedOnRent: 2,
      leftToIssue: 4,
      otherAvailable: null,
    },
    locations: [
      {
        branchId: 'b-alger',
        code: 'ALG',
        nameAr: 'الجزائر',
        nameFr: 'Alger',
        nameEn: 'Algiers',
        isWarehouse: true,
        isMain: true,
        isActive: true,
        availableOnly: false,
        available: 15,
        onRent: 15,
        maintenance: 3,
        scrap: 2,
        inTransit: 3,
        total: 38,
        stillOut: 15,
        unexplainedOnRent: 0,
      },
    ],
    rented: [],
    leftToIssue: [
      {
        contractId: 'cnt-1',
        contractNumber: 'CTR-000001',
        customerId: 'c-1',
        customerName: 'Client Alpha',
        qty: 4,
      },
    ],
    inTransit: [
      {
        transferId: 't-1',
        number: 'TRF-000001',
        status: 'Dispatched',
        fromBranchId: 'b-alger',
        fromNameAr: 'الجزائر',
        fromNameFr: 'Alger',
        fromNameEn: 'Algiers',
        toBranchId: 'b-oran',
        toNameAr: 'وهران',
        toNameFr: 'Oran',
        toNameEn: 'Oran',
        dispatchedAtUtc: '2026-10-12T10:00:00Z',
        qty: 3,
        countedHere: true,
      },
    ],
    movements: {
      from: '2026-10-01',
      to: '2026-10-12',
      opening: 0,
      closing: 50,
      unrecordedDifference: null,
      truncated: false,
      rows: [
        {
          atUtc: '2026-10-12T08:00:00Z',
          type: 'receiving',
          documentNumber: 'REC-001',
          documentId: 'd-1',
          userName: 'Admin',
          supplierName: 'Fournisseur A',
          moved: 50,
          in: 50,
          out: 0,
          balance: 50,
        },
      ],
    },
  };

  const sampleBranches: AdminBranch[] = [
    { id: 'b-alger', nameAr: 'الجزائر', nameFr: 'Alger', nameEn: 'Algiers' } as AdminBranch,
    { id: 'b-oran', nameAr: 'وهران', nameFr: 'Oran', nameEn: 'Oran' } as AdminBranch,
  ];

  /** Two rentals out of Alger, one late, as the server pages them. */
  const rentedRows: ItemRentedRow[] = [
    {
      contractId: 'cnt-2', contractNumber: 'CTR-000002', contractStatus: 'Open', customerId: 'c-2', customerCode: 'C002',
      customerName: 'Client Beta', issueBranchId: 'b-alger', issueBranchNameAr: 'الجزائر', issueBranchNameFr: 'Alger',
      issueBranchNameEn: 'Algiers', siteName: null, issueNoteId: 'n-1', issueNoteNumber: 'BS-000001',
      issuedOn: '2026-10-01', expectedReturnOn: '2026-10-05', overdue: true, qty: 6,
    },
    {
      contractId: 'cnt-3', contractNumber: 'CTR-000003', contractStatus: 'Open', customerId: 'c-3', customerCode: null,
      customerName: 'Client Gamma', issueBranchId: 'b-alger', issueBranchNameAr: 'الجزائر', issueBranchNameFr: 'Alger',
      issueBranchNameEn: 'Algiers', siteName: null, issueNoteId: 'n-2', issueNoteNumber: 'BS-000002',
      issuedOn: '2026-10-10', expectedReturnOn: '2026-11-30', overdue: false, qty: 12,
    },
  ];

  let pages: jasmine.SpyObj<ItemStatusPagesApi>;

  beforeEach(async () => {
    // The two long lists page through the server (owner, 2026-11-17): rentals out and movements.
    pages = jasmine.createSpyObj<ItemStatusPagesApi>('ItemStatusPagesApi', ['rented', 'movements']);
    pages.rented.and.callFake(async (_id, _q, filters, skip, take) => {
      const rows = rentedRows.filter((r) => filters.state === 'all' || r.overdue === (filters.state === 'overdue'));
      return {
        items: rows, total: rows.length, skip, take,
        summary: { count: rentedRows.length, issueBranches: [{ id: 'b-alger', nameAr: 'الجزائر', nameFr: 'Alger', nameEn: 'Algiers' }] },
      };
    });
    pages.movements.and.callFake(async (_id, _q, from, to, skip, take) => ({
      items: sampleDetail.movements.rows, total: 1, skip, take,
      summary: { from: from || null, to: to || null, opening: 0, closing: 50, unrecordedDifference: null, truncated: false, pageOpening: 0 },
    }));

    await TestBed.configureTestingModule({
      imports: [ItemStatusDetailComponent],
      providers: [provideRouter([]), { provide: ItemStatusPagesApi, useValue: pages }],
    }).compileComponents();

    const i18n = TestBed.inject(I18nService);
    i18n.extend(BACKOFFICE_TRANSLATIONS);
    i18n.extend(ITEM_STATUS_TRANSLATIONS);
    i18n.extend(TRANSFER_TRANSLATIONS);

    fixture = TestBed.createComponent(ItemStatusDetailComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('detail', sampleDetail);
    fixture.componentRef.setInput('branches', sampleBranches);
    fixture.detectChanges();
  });

  it('renders item header with SKU, unit, and barcode', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('ECHAF-01');
    expect(el.textContent).toContain('pcs');
    expect(el.textContent).toContain('123456789012');
  });

  it('renders all metric values (total, available, rented, maintenance, scrap, inTransit)', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('50'); // total
    expect(el.textContent).toContain('20'); // available & rented
    expect(el.textContent).toContain('5'); // maintenance
    expect(el.textContent).toContain('2'); // scrap
    expect(el.textContent).toContain('3'); // inTransit
  });

  it('renders reconciliation warning when unexplainedOnRent is not zero', () => {
    const el = fixture.nativeElement as HTMLElement;
    const warning = el.querySelector('.notice.warn');
    expect(warning).not.toBeNull();
    expect(warning?.textContent).toContain('2');
  });

  it('renders left-to-issue informational section with link and contract number', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('CTR-000001');
    expect(el.textContent).toContain('Client Alpha');
  });

  it('renders in-transit table when goods are in transit', () => {
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('TRF-000001');
    expect(el.textContent).toContain('Alger');
    expect(el.textContent).toContain('Oran');
  });

  it('emits close output when close button is clicked', () => {
    spyOn(component.close, 'emit');
    const closeBtn = fixture.nativeElement.querySelector('.btn-ghost') as HTMLButtonElement;
    closeBtn.click();
    expect(component.close.emit).toHaveBeenCalled();
  });

  describe('the two long lists, a page at a time from the server (owner, 2026-11-17)', () => {
    async function settle(): Promise<void> {
      await fixture.whenStable();
      fixture.detectChanges();
    }

    it('asks for the first ten movements of the period, under the report filters', async () => {
      await new Promise((resolve) => setTimeout(resolve, 350));
      await settle();
      expect(pages.movements).toHaveBeenCalledWith('p-1', jasmine.any(Object), '2026-10-01', '2026-10-12', 0, 10);
      fixture.componentRef.setInput('query', { branchId: 'b-alger' });
      fixture.detectChanges();
      await new Promise((resolve) => setTimeout(resolve, 350));
      await settle();
      expect(pages.movements).toHaveBeenCalledWith('p-1', jasmine.objectContaining({ branchId: 'b-alger' }), '2026-10-01', '2026-10-12', 0, 10);
      const el = fixture.nativeElement as HTMLElement;
      expect(el.textContent).toContain('REC-001');
    });

    it('shows the balance before the page on the opening row, and the period closing', async () => {
      pages.movements.and.callFake(async (_id, _q, from, to, skip, take) => ({
        items: sampleDetail.movements.rows, total: 11, skip: 10, take,
        summary: { from: from || null, to: to || null, opening: 0, closing: 50, unrecordedDifference: null, truncated: false, pageOpening: 37 },
      }));
      component.movementsPager.go(1);
      fixture.detectChanges();
      await settle();
      expect(component.movementsOpening()).toBe(37);
      expect(component.movementsHead().closing).toBe(50);
    });

    it('shows the rentals out once the server counts some, and sends their filters', async () => {
      await settle();
      const el = fixture.nativeElement as HTMLElement;
      expect(el.textContent).toContain('CTR-000002');
      expect(el.textContent).toContain('CTR-000003');
      expect(component.rentedCount()).toBe(2);

      component.rentedState.set('overdue');
      fixture.detectChanges();
      await new Promise((resolve) => setTimeout(resolve, 350));
      await settle();
      expect(pages.rented).toHaveBeenCalledWith('p-1', jasmine.any(Object), { state: 'overdue', issueBranchId: 'all' }, 0, 10);
      expect(el.textContent).toContain('CTR-000002');
      expect(el.textContent).not.toContain('CTR-000003');
    });

    it('emits the new period when a date changes, and asks for its movements', async () => {
      await settle();
      spyOn(component.datesChange, 'emit');
      component.setFrom('2026-10-05');
      expect(component.datesChange.emit).toHaveBeenCalledWith({ from: '2026-10-05', to: '2026-10-12' });
      fixture.detectChanges();
      await new Promise((resolve) => setTimeout(resolve, 350));
      await settle();
      expect(pages.movements).toHaveBeenCalledWith('p-1', jasmine.any(Object), '2026-10-05', '2026-10-12', 0, 10);
    });
  });
});
