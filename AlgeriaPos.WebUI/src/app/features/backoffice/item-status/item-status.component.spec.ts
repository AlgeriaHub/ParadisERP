import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminBranch, AdminCustomer, AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { PrintService } from '../../print/print.service';
import { RentalsApi } from '../rentals/rentals-api.service';
import {
  ItemStatusApiService,
  ItemStatusDetail,
  ItemStatusReport,
  ItemStatusRow,
} from './item-status-api.service';
import { ItemStatusComponent } from './item-status.component';
import { ItemStatusPagesApi } from './item-status-pages.api';

describe('ItemStatusComponent', () => {
  let fixture: ComponentFixture<ItemStatusComponent>;
  let component: ItemStatusComponent;
  let adminSpy: jasmine.SpyObj<AdminService>;
  let rentalsSpy: jasmine.SpyObj<RentalsApi>;
  let apiSpy: jasmine.SpyObj<ItemStatusApiService>;
  let pagesSpy: jasmine.SpyObj<ItemStatusPagesApi>;
  let printSpy: jasmine.SpyObj<PrintService>;

  const lockedSignal = signal(false);
  const lockedBranchIdSignal = signal<string | null>(null);

  const mockBranches: AdminBranch[] = [
    {
      id: 'b-main',
      nameAr: 'الفرع الرئيسي',
      nameFr: 'Succursale Principale',
      nameEn: 'Main Branch',
      address: '',
      phone: '',
      isMain: true,
      isWarehouse: false,
      allowNegativeStock: false,
      isActive: true,
      version: 1,
    },
    {
      id: 'w-central',
      nameAr: 'المستودع المركزي',
      nameFr: 'Entrepôt Central',
      nameEn: 'Central Warehouse',
      address: '',
      phone: '',
      isMain: false,
      isWarehouse: true,
      allowNegativeStock: false,
      isActive: true,
      version: 1,
    },
  ];

  const mockCustomers = [
    {
      id: 'c-1',
      code: 'C001',
      fullName: 'Client Test',
      phone: '0555000000',
      address: 'Alger',
      taxId: null,
      balance: 0,
      creditLimit: 0,
      isActive: true,
      version: 1,
    },
  ];

  const createReport = (
    rows: ItemStatusRow[],
    othersAvailableShown = false,
  ): ItemStatusReport => ({
    asOfUtc: '2026-10-12T12:00:00Z',
    scope: {
      kind: 'company',
      locationId: null,
      othersAvailableShown,
    },
    rows,
    totals: {
      total: 100,
      warehouses: 40,
      branches: 60,
      available: 70,
      rented: 20,
      maintenance: 5,
      scrap: 5,
      stillOut: 20,
      unexplainedOnRent: 0,
      leftToIssue: 0,
      otherAvailable: othersAvailableShown ? 15 : null,
      inTransit: 4,
    },
  });

  const row1: ItemStatusRow = {
    productId: 'p-1',
    sku: 'SKU-001',
    barcode: '123456',
    nameAr: 'مثقاب',
    nameFr: 'Perceuse',
    nameEn: 'Drill',
    unit: 'U',
    isRentable: true,
    isActive: true,
    total: 50,
    warehouses: 20,
    branches: 30,
    available: 35,
    rented: 10,
    maintenance: 3,
    scrap: 2,
    stillOut: 10,
    unexplainedOnRent: 0,
    leftToIssue: 0,
    otherAvailable: null,
    inTransit: 0,
  };

  const rowWithWarning: ItemStatusRow = {
    productId: 'p-2',
    sku: 'SKU-002',
    barcode: null,
    nameAr: 'رافعة',
    nameFr: 'Grue',
    nameEn: 'Crane',
    unit: 'U',
    isRentable: true,
    isActive: true,
    total: 50,
    warehouses: 20,
    branches: 30,
    available: 35,
    rented: 10,
    maintenance: 2,
    scrap: 3,
    stillOut: 7,
    unexplainedOnRent: 3,
    leftToIssue: 2,
    otherAvailable: null,
    inTransit: 0,
  };

  beforeEach(async () => {
    lockedSignal.set(false);
    lockedBranchIdSignal.set(null);

    adminSpy = jasmine.createSpyObj<AdminService>('AdminService', ['listBranches', 'listCustomers']);
    rentalsSpy = jasmine.createSpyObj<RentalsApi>('RentalsApi', ['listContracts']);
    apiSpy = jasmine.createSpyObj<ItemStatusApiService>('ItemStatusApiService', [
      'summary',
      'detail',
      'exportSummary',
      'exportDetail',
    ]);
    printSpy = jasmine.createSpyObj<PrintService>('PrintService', ['open']);
    // The report and the breakdown are read a page at a time from the server (owner, 2026-11-17).
    pagesSpy = jasmine.createSpyObj<ItemStatusPagesApi>('ItemStatusPagesApi', ['page', 'overview', 'rented', 'movements']);

    adminSpy.listBranches.and.resolveTo(mockBranches);
    adminSpy.listCustomers.and.resolveTo(mockCustomers as unknown as AdminCustomer[]);
    rentalsSpy.listContracts.and.resolveTo([]);
    serve(createReport([row1, rowWithWarning]));

    TestBed.configureTestingModule({
      imports: [ItemStatusComponent],
      providers: [
        provideRouter([]),
        { provide: AdminService, useValue: adminSpy },
        { provide: RentalsApi, useValue: rentalsSpy },
        { provide: ItemStatusApiService, useValue: apiSpy },
        { provide: ItemStatusPagesApi, useValue: pagesSpy },
        { provide: PrintService, useValue: printSpy },
        {
          provide: BranchLock,
          useValue: {
            locked: lockedSignal,
            lockedBranchId: lockedBranchIdSignal,
            ownBranchName: () => 'Succursale Principale',
          },
        },
      ],
    });

    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);

    fixture = TestBed.createComponent(ItemStatusComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  const el = () => fixture.nativeElement as HTMLElement;

  /** The server's answer to every page request: the report's rows, one page, its figures over all of them. */
  function serve(report: ItemStatusReport): void {
    pagesSpy.page.and.callFake(async (_q, skip, take) => ({
      items: report.rows.slice(skip, skip + take),
      total: report.rows.length,
      skip,
      take,
      summary: { asOfUtc: report.asOfUtc, scope: report.scope, totals: report.totals, productIds: report.rows.map((r) => r.productId) },
    }));
  }

  it('renders rows and the server totals in tfoot', () => {
    const tableRows = el().querySelectorAll('tbody tr');
    expect(tableRows.length).toBe(2);

    const firstRowText = tableRows[0].textContent || '';
    expect(firstRowText).toContain('SKU-001');
    expect(firstRowText).toContain('Perceuse');

    const tfoot = el().querySelector('tfoot');
    expect(tfoot).toBeTruthy();
    const tfootText = tfoot?.textContent || '';
    expect(tfootText).toContain('100'); // total
    expect(tfootText).toContain('40'); // warehouses
    expect(tfootText).toContain('60'); // branches
    expect(tfootText).toContain('70'); // available
  });

  it('shows the "On rent without a contract" word when unexplainedOnRent ≠ 0', () => {
    const tableRows = el().querySelectorAll('tbody tr');
    const secondRow = tableRows[1];
    expect(secondRow.textContent).toContain('Grue');

    // Row has unexplainedOnRent = 3
    const warning = secondRow.querySelector('app-status');
    expect(warning).toBeTruthy();
    expect(warning?.textContent).toContain('3');
    expect(warning?.textContent).toContain('Loué sans contrat');

    // Row 1 has unexplainedOnRent = 0, so no app-status warning
    const firstRowWarning = tableRows[0].querySelector('app-status');
    expect(firstRowWarning).toBeNull();
  });

  it('hides the Location select and shows the scope note for a locked user', async () => {
    expect(el().querySelector('#is-branch')).toBeTruthy();

    lockedSignal.set(true);
    lockedBranchIdSignal.set('b-main');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(el().querySelector('#is-branch')).toBeNull();
    const scopeNote = el().querySelector('.bo-scope-note');
    expect(scopeNote).toBeTruthy();
    expect(scopeNote?.textContent).toContain('Succursale Principale');
  });

  it('shows what is in transit as its own column, in the totals too (owner, 2026-10-12)', () => {
    const label = TestBed.inject(I18nService).t('itemStatus.col.inTransit');
    const headers = Array.from(el().querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    const at = headers.indexOf(label);
    expect(at).toBeGreaterThan(-1);
    const totals = Array.from(el().querySelectorAll('tfoot td')).map((td) => td.textContent?.trim());
    expect(totals).toContain('4');
  });

  it('shows the "Other locations" column only when othersAvailableShown is true', async () => {
    // Default mock report has othersAvailableShown = false
    let headers = Array.from(el().querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    const otherLabel = TestBed.inject(I18nService).t('itemStatus.col.otherAvailable');
    expect(headers).not.toContain(otherLabel);

    // Update report with othersAvailableShown = true
    serve(createReport([row1], true));
    await component.run();
    fixture.detectChanges();

    headers = Array.from(el().querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    expect(headers).toContain(otherLabel);
  });

  it('preserves detail dates when summary reloads', async () => {
    const mockDetail: ItemStatusDetail = {
      asOfUtc: '2026-10-12T12:00:00Z',
      scope: { kind: 'company', locationId: null, othersAvailableShown: false },
      item: row1,
      locations: [],
      rented: [],
      leftToIssue: [],
      inTransit: [],
      movements: {
        from: '2026-10-01',
        to: '2026-10-10',
        opening: 10,
        closing: 50,
        unrecordedDifference: null,
        truncated: false,
        rows: [],
      },
    };
    pagesSpy.overview.and.resolveTo(mockDetail);
    component.openProductId.set('p-1');
    await component.onDetailDatesChange({ from: '2026-10-01', to: '2026-10-10' });

    expect(component.detailFrom()).toBe('2026-10-01');
    expect(component.detailTo()).toBe('2026-10-10');

    // Summary reloads via run()
    await component.run();
    expect(pagesSpy.overview).toHaveBeenCalledWith('p-1', jasmine.any(Object), '2026-10-01', '2026-10-10');
  });

  it('exports detail excel named with item sku', async () => {
    const mockDetail: ItemStatusDetail = {
      asOfUtc: '2026-10-12T12:00:00Z',
      scope: { kind: 'company', locationId: null, othersAvailableShown: false },
      item: row1,
      locations: [],
      rented: [],
      leftToIssue: [],
      inTransit: [],
      movements: {
        from: '2026-10-01',
        to: '2026-10-10',
        opening: 10,
        closing: 50,
        unrecordedDifference: null,
        truncated: false,
        rows: [],
      },
    };
    apiSpy.exportDetail.and.resolveTo(new Blob(['test']));
    component.openProductId.set('p-1');
    component.detail.set(mockDetail);

    await component.exportDetailExcel({ from: '2026-10-01', to: '2026-10-10' });
    expect(apiSpy.exportDetail).toHaveBeenCalledWith('p-1', jasmine.any(Object), '2026-10-01', '2026-10-10', jasmine.any(String));
  });

  it('asks the server for ten rows first, and again from page one when a filter changes', async () => {
    expect(pagesSpy.page).toHaveBeenCalledWith(jasmine.objectContaining({ branchId: null }), 0, 10);
    component.setStatus('rented');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    await fixture.whenStable();
    expect(pagesSpy.page).toHaveBeenCalledWith(jasmine.objectContaining({ status: 'rented' }), 0, 10);
  });

  it('selects every row of the report with "select all", not only the page on screen', async () => {
    const many = Array.from({ length: 12 }, (_, i) => ({ ...row1, productId: `p-${i + 1}`, sku: `SKU-${i + 1}` }));
    serve(createReport(many));
    await component.run();
    fixture.detectChanges();
    expect(el().querySelectorAll('tbody tr').length).toBe(10);

    component.toggleSelectAll();
    expect(component.selectedIds().size).toBe(12);
    expect(component.isAllSelected()).toBeTrue();
  });
});
