import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { PrintService } from '../../print/print.service';
import { QuantityStatement, QuantityStatementQuery, RentalsApi } from '../rentals/rentals-api.service';
import { QuantityStatementHead, SalesPagesApi } from '../sales-pages.api';
import { QuantityStatementComponent } from './quantity-statement.component';

/**
 * Customers › Customer quantity statement (owner, 2026-10-12): the screen shows what the server
 * counted. Two figures never share a word — the daily unit rate and the rental value of the
 * period — in Arabic (right to left) and French (left to right) alike.
 */
describe('QuantityStatementComponent', () => {
  let fixture: ComponentFixture<QuantityStatementComponent>;
  let asked: QuantityStatementQuery[];
  let printed: unknown[];
  let exported: [QuantityStatementQuery, string][];

  const statement = (over: Partial<QuantityStatement> = {}): QuantityStatement => ({
    customerId: 'c1',
    customerCode: 'C-001',
    customerName: 'Entreprise Test',
    from: '2026-03-01',
    to: '2026-03-31',
    current: false,
    firstIssueOn: '2026-01-05',
    summary: { itemTypes: 1, issued: 4, returned: 0, missingDamaged: 0, withCustomer: 4, value: 682, provisionalValue: 682 },
    items: [
      {
        productId: 'p1',
        sku: 'ECH-1',
        name: 'Échafaudage',
        nameAr: 'سقالة',
        barcode: null,
        opening: 0,
        issued: 4,
        returned: 0,
        missingDamaged: 0,
        transferNet: 0,
        remaining: 4,
        rates: [
          { kind: 'rent', unitPrice: 5 },
          { kind: 'rent', unitPrice: 6 },
        ],
        noRent: false,
        value: 682,
        provisionalValue: 682,
        negativeBalance: false,
        movements: [],
      },
    ],
    contracts: [],
    warnings: [],
    ...over,
  });

  let answer: QuantityStatement;

  /** The statement's head, as the server sends it beside each page of either table. */
  const head = (st: QuantityStatement): QuantityStatementHead => {
    const { items, contracts, ...rest } = st;
    return {
      ...rest,
      hasTransfers: items.some((i) => i.transferNet !== 0),
      hasContractTransfers: contracts.some((c) => c.lines.some((l) => l.transferredIn !== 0 || l.transferredOut !== 0)),
      itemCount: items.length,
      contractCount: contracts.length,
    };
  };

  beforeEach(async () => {
    asked = [];
    printed = [];
    exported = [];
    answer = statement();
    TestBed.configureTestingModule({
      imports: [QuantityStatementComponent],
      providers: [
        provideRouter([]),
        {
          provide: AdminService,
          useValue: {
            listCustomers: () => Promise.resolve([{ id: 'c1', code: 'C-001', fullName: 'Entreprise Test' }]),
            listBranches: () => Promise.resolve([]),
            listProducts: () => Promise.resolve([]),
          },
        },
        {
          provide: RentalsApi,
          useValue: {
            listContracts: () => Promise.resolve([]),
            listSites: () => Promise.resolve([]),
            quantityStatementExport: (q: QuantityStatementQuery, lang: string) => (exported.push([q, lang]), Promise.resolve(new Blob(['x']))),
          },
        },
        {
          // The two tables come a page at a time from the server (owner, 2026-11-17), the head with each.
          provide: SalesPagesApi,
          useValue: {
            quantityItemsPage: (q: QuantityStatementQuery, skip: number, take: number) => (
              asked.push(q),
              Promise.resolve({ items: answer.items.slice(skip, skip + take), total: answer.items.length, skip, take, summary: head(answer) })
            ),
            quantityContractsPage: (_q: QuantityStatementQuery, skip: number, take: number) =>
              Promise.resolve({ items: answer.contracts.slice(skip, skip + take), total: answer.contracts.length, skip, take, summary: head(answer) }),
          },
        },
        { provide: BranchLock, useValue: { locked: () => false, lockedBranchId: () => null, ownBranchName: () => '' } },
        { provide: PrintService, useValue: { open: (...args: unknown[]) => printed.push(args) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
  });

  const i18n = () => TestBed.inject(I18nService);
  const el = () => fixture.nativeElement as HTMLElement;
  const headers = () => [...el().querySelectorAll('table.qs-items > thead th')].map((th) => th.textContent?.trim());

  async function open(lang: 'ar' | 'fr', current = false): Promise<void> {
    fixture = TestBed.createComponent(QuantityStatementComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    // After the first pass: the service's own effect settles the company default then.
    i18n().lang.set(lang);
    await fixture.componentInstance.setCustomer('c1');
    await fixture.componentInstance.show(current);
    fixture.detectChanges();
  }

  it('in Arabic, reads right to left and names the daily rate and the rental value apart', async () => {
    await open('ar');
    expect(i18n().dir()).toBe('rtl');
    expect(headers()).toContain('سعر الوحدة / اليوم');
    expect(headers()).toContain('قيمة الإيجار للفترة');
    // Plain dates, as the inputs hold them — the server reads them in the company's zone.
    expect(asked[0].from).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(asked[0].to).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(el().textContent).toContain('سقالة');
    // Ten rows to begin with, the tiles over the whole statement.
    expect(fixture.componentInstance.itemsPager.size()).toBe(10);
    expect(fixture.componentInstance.statement()?.summary.value).toBe(682);
  });

  it('in French, reads left to right with the same two columns', async () => {
    await open('fr');
    expect(i18n().dir()).toBe('ltr');
    expect(headers()).toContain('Prix unitaire / jour');
    expect(headers()).toContain('Valeur de location de la période');
  });

  it('shows "Multiple rates" for an item with two rates, and lists both when asked', async () => {
    await open('fr');
    const toggle = el().querySelector('.qs-rates-toggle') as HTMLButtonElement;
    expect(toggle.textContent?.trim()).toBe(i18n().t('qstmt.multipleRates'));
    toggle.click();
    fixture.detectChanges();
    const rates = [...el().querySelectorAll('.qs-rates li')];
    expect(rates.length).toBe(2);
  });

  it('asks for the current quantities with no dates, and hides the value column', async () => {
    answer = statement({ current: true });
    await open('fr', true);
    expect(asked[0].current).toBeTrue();
    expect(asked[0].from).toBeNull();
    expect(asked[0].to).toBeNull();
    expect(headers()).not.toContain('Valeur de location de la période');
    expect(headers()).toContain(i18n().t('qstmt.col.remaining'));
  });

  it('says an unfinished price correction in words', async () => {
    answer = statement({
      warnings: [
        {
          code: 'pricingCorrectionPending',
          productId: null,
          sku: null,
          name: null,
          nameAr: null,
          contractId: 'k1',
          contractNumber: 'RC-000001',
          on: null,
          qty: null,
        },
      ],
    });
    await open('fr');
    const warn = el().querySelector('.notice.warn');
    expect(warn?.textContent).toContain('RC-000001');
    expect(warn?.textContent).toContain(i18n().t('qstmt.warn.pricingCorrectionPending', { contract: 'RC-000001' }));
  });

  it('prints the query the statement on screen was read with', async () => {
    await open('fr');
    fixture.componentInstance.print();
    expect(printed.length).toBe(1);
    expect((printed[0] as unknown[])[0]).toBe('quantity-statement');
    expect((printed[0] as unknown[])[1]).toBe('c1');
  });

  it("exports the same query as the statement on screen, in the reader's language", async () => {
    await open('ar');
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    spyOn(URL, 'revokeObjectURL');
    spyOn(HTMLAnchorElement.prototype, 'click');
    await fixture.componentInstance.excel();
    expect(exported.length).toBe(1);
    expect(exported[0][0]).toEqual(asked[0]);
    expect(exported[0][1]).toBe('ar');
  });
});
