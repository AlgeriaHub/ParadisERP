import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthService } from '../../../core/auth.service';
import { AdminService } from '../../../core/admin.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { SalesPagesApi } from '../sales-pages.api';
import { BackofficeSuppliersComponent } from './suppliers-tab.component';
import {
  SupplierDto,
  SuppliersApiService,
  UpsertSupplierRequest,
} from './suppliers-api.service';

describe('BackofficeSuppliersComponent', () => {
  let fixture: ComponentFixture<BackofficeSuppliersComponent>;
  let comp: BackofficeSuppliersComponent;
  let suppliersApi: jasmine.SpyObj<SuppliersApiService>;
  let pages: jasmine.SpyObj<SalesPagesApi>;
  /** The server's answer to every page request: these suppliers, as one page. */
  const page = (rows: SupplierDto[]) =>
    pages.suppliersPage.and.callFake(async (q) => ({ items: rows, total: rows.length, skip: 0, take: q.take }));
  let createCalls: UpsertSupplierRequest[];
  let updateCalls: { id: string; req: UpsertSupplierRequest }[];
  const user = signal<{ capabilities: string[] } | null>({ capabilities: ['suppliers.opening_balance'] });

  const sampleSupplier = (over: Partial<SupplierDto> = {}): SupplierDto => ({
    id: 's1',
    code: 'SUP-001',
    name: 'Grossiste Alger Centre',
    phone: '0555123456',
    address: '12 Rue Didouche Mourad, Alger',
    fiscalIds: { nif: '123456789012345' },
    note: 'Fournisseur principal',
    balance: 15000,
    openingBalance: 15000,
    openingBalanceOn: '2026-10-01',
    openingBalanceSetAtUtc: '2026-10-01T10:00:00Z',
    openingBalanceSetBy: 'admin',
    ...over,
  });

  beforeEach(() => {
    createCalls = [];
    updateCalls = [];
    suppliersApi = jasmine.createSpyObj<SuppliersApiService>('SuppliersApiService', [
      'create',
      'update',
      'remove',
      'statement',
      'setOpeningBalance',
      'correctOpeningBalance',
      'listOpeningCorrections',
    ]);
    // The list comes a page at a time from the server (owner, 2026-11-17).
    pages = jasmine.createSpyObj<SalesPagesApi>('SalesPagesApi', ['suppliersPage', 'supplierStatementPage']);
    page([]);
    suppliersApi.create.and.callFake(async (req: UpsertSupplierRequest) => {
      createCalls.push(req);
      return sampleSupplier({ id: 's-new', code: 'SUP-NEW', name: req.name });
    });
    suppliersApi.update.and.callFake(async (id: string, req: UpsertSupplierRequest) => {
      updateCalls.push({ id, req });
      return sampleSupplier({ id, name: req.name });
    });
    suppliersApi.remove.and.resolveTo();

    TestBed.configureTestingModule({
      imports: [BackofficeSuppliersComponent],
      providers: [
        { provide: SuppliersApiService, useValue: suppliersApi },
        { provide: SalesPagesApi, useValue: pages },
        { provide: AdminService, useValue: {} },
        { provide: AuthService, useValue: { currentUser: user } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  const el = () => fixture.nativeElement as HTMLElement;
  const t = (key: string, params?: Record<string, string | number>) => TestBed.inject(I18nService).t(key, params);

  it('lists suppliers from a mocked service with the balance word', async () => {
    const list: SupplierDto[] = [
      sampleSupplier({ id: 's1', code: 'SUP-001', name: 'Fournisseur Positif', balance: 15000 }),
      sampleSupplier({ id: 's2', code: 'SUP-002', name: 'Fournisseur Négatif', balance: -3000 }),
      sampleSupplier({ id: 's3', code: 'SUP-003', name: 'Fournisseur Nul', balance: 0 }),
    ];
    page(list);

    fixture = TestBed.createComponent(BackofficeSuppliersComponent);
    comp = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const rows = el().querySelectorAll('tbody tr');
    expect(rows.length).toBe(3);

    // Supplier with positive balance: company owes supplier ("we owe")
    expect(rows[0].textContent).toContain('SUP-001');
    expect(rows[0].textContent).toContain('Fournisseur Positif');
    expect(rows[0].textContent).toContain(t('sup.weOwe'));

    // Supplier with negative balance: supplier owes company ("owes us")
    expect(rows[1].textContent).toContain('SUP-002');
    expect(rows[1].textContent).toContain('Fournisseur Négatif');
    expect(rows[1].textContent).toContain(t('sup.owesUs'));

    // Supplier with zero balance: no balance word shown
    expect(rows[2].textContent).toContain('SUP-003');
    expect(rows[2].textContent).toContain('Fournisseur Nul');
    expect(rows[2].textContent).not.toContain(t('sup.weOwe'));
    expect(rows[2].textContent).not.toContain(t('sup.owesUs'));
    // Ten to begin with, asked of the server with the filters.
    expect(pages.suppliersPage).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, q: '', balance: 'all', missing: 'all' }));
  });

  it('asks the server for a statement page with the days as typed, and shows its head', async () => {
    const supplier = sampleSupplier();
    page([supplier]);
    pages.supplierStatementPage.and.resolveTo({
      items: [
        {
          date: '2026-10-02T09:00:00Z', sourceType: 'receiving', sourceId: 'r1', documentNumber: 'REC-1',
          description: 'Réception', isReversal: false, debit: 0, credit: 4000, balance: 19000,
        },
      ],
      total: 1,
      skip: 0,
      take: 10,
      summary: {
        supplierId: 's1', supplierCode: 'SUP-001', supplierName: supplier.name, from: '2026-10-01', to: '2026-10-31',
        openingBalance: 15000, closingBalance: 19000, totalDebit: 0, totalCredit: 4000,
        recordedBalance: 19000, balanceDifference: 0, pageOpening: 15000,
      },
    });
    fixture = TestBed.createComponent(BackofficeSuppliersComponent);
    comp = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();

    await comp.openStatement(supplier);
    fixture.detectChanges();
    expect(pages.supplierStatementPage).toHaveBeenCalledWith(
      's1',
      jasmine.objectContaining({ skip: 0, take: 10, from: comp.stmtFrom, to: comp.stmtTo }),
    );
    expect(comp.stmtFrom).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(comp.statement()?.closingBalance).toBe(19000);
    expect(el().textContent).toContain('REC-1');
  });

  it('creating sends the opening balance', async () => {
    page([]);
    fixture = TestBed.createComponent(BackofficeSuppliersComponent);
    comp = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();

    comp.openCreate();
    fixture.detectChanges();

    comp.form.name = 'Nouveau Fournisseur Alpha';
    comp.openingDraft.set({
      amount: 8000,
      direction: 'weOwe',
      on: '2026-10-01',
    });
    fixture.detectChanges();

    await comp.save();

    expect(createCalls.length).toBe(1);
    expect(createCalls[0].name).toBe('Nouveau Fournisseur Alpha');
    expect(createCalls[0].openingBalance).toEqual({
      amount: 8000,
      direction: 'weOwe',
      on: '2026-10-01',
    });
  });

  it('editing never sends the opening balance', async () => {
    const existing = sampleSupplier({ id: 's1', name: 'Fournisseur Existant', balance: 5000 });
    page([existing]);
    fixture = TestBed.createComponent(BackofficeSuppliersComponent);
    comp = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();

    comp.edit(existing);
    fixture.detectChanges();

    comp.form.name = 'Fournisseur Modifié';
    // Even if openingDraft was somehow set, editing must omit openingBalance
    comp.openingDraft.set({
      amount: 9999,
      direction: 'weOwe',
      on: '2026-10-01',
    });
    fixture.detectChanges();

    await comp.save();

    expect(updateCalls.length).toBe(1);
    expect(updateCalls[0].id).toBe('s1');
    expect(updateCalls[0].req.name).toBe('Fournisseur Modifié');
    expect(updateCalls[0].req.openingBalance).toBeUndefined();
  });
});
