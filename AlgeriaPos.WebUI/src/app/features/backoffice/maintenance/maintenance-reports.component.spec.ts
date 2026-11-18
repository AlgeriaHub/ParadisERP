import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { I18nService } from '../../../core/i18n.service';
import { MaintenanceProductCostReport, MaintenanceRechargeReport } from './maintenance-cost-api.service';
import { MaintenanceProfitReport, MaintenanceProfitRow } from './maintenance-customer-api.service';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { MaintenanceReportsComponent } from './maintenance-reports.component';
import { afterFilter, pageOf, signIn, t } from './maintenance-test-data';

/** REN-11: eight frames charged 16,000 at return, repaired by two jobs (5 and 3 units) for 7,000 and 4,000. */
const RECHARGES: MaintenanceRechargeReport = {
  from: null, to: null, rechargeSharing: 'ByUnits', labourMoneyShown: true,
  rows: [
    { jobId: 'a', number: 'MJ-000001', status: 'Completed', branchId: 'b1', branchName: 'Alger-Centre', productId: 'p1', sku: 'ECH-01',
      nameAr: 'سقالة', nameFr: 'Échafaudage', nameEn: 'Scaffold', qty: 5, contractNumber: 'CTR-0001', returnNoteNumber: 'RET-000001',
      customerName: 'Ben Ali BTP', lineRecharge: 16000, recharge: 10000, parts: 0, labour: 0, overhead: 0, outsideWork: 7000, total: 7000,
      difference: 3000, labourHours: 0, costStatus: 'provisional', withheld: false, createdAtUtc: '2026-11-09T08:00:00Z' },
    { jobId: 'b', number: 'MJ-000002', status: 'Open', branchId: 'b1', branchName: 'Alger-Centre', productId: 'p1', sku: 'ECH-01',
      nameAr: 'سقالة', nameFr: 'Échafaudage', nameEn: 'Scaffold', qty: 3, contractNumber: 'CTR-0001', returnNoteNumber: 'RET-000001',
      customerName: 'Ben Ali BTP', lineRecharge: 16000, recharge: 6000, parts: 0, labour: null, overhead: 0, outsideWork: 4000, total: null,
      difference: null, labourHours: 2, costStatus: 'provisional', withheld: true, createdAtUtc: '2026-11-09T09:00:00Z' },
  ],
  recharge: 16000, parts: 0, overhead: 0, outsideWork: 11000, labourHours: 2, labour: null, total: null, difference: null, totalsWithheld: true,
};

const PRODUCTS: MaintenanceProductCostReport = {
  from: null, to: null, labourMoneyShown: false, jobs: 2, parts: 0, overhead: 0, outsideWork: 11000, labourHours: 2, recharge: 16000,
  labour: null, total: null, totalsWithheld: true,
  rows: [
    { productId: 'p1', sku: 'ECH-01', nameAr: 'سقالة', nameFr: 'Échafaudage', nameEn: 'Scaffold', jobs: 2, units: 8, repairedQty: 5,
      scrappedQty: 0, parts: 0, overhead: 0, outsideWork: 11000, labourHours: 2, labour: null, total: null, recharge: 16000, workers: 1,
      withheld: true, provisional: true },
  ],
};

/** Company profit (Phase M5): REN-11's recharged job (16,000 − 11,000), a customer job invoiced 20,000 against 7,200, an internal job. */
function profitRow(over: Partial<MaintenanceProfitRow>): MaintenanceProfitRow {
  return {
    jobId: 'r', number: 'MJ-000001', kind: 'Recharged', status: 'Completed', branchId: 'b1', branchName: 'Alger-Centre', productId: 'p1',
    productSku: 'ECH-01', productNameAr: 'سقالة', productNameFr: 'Échafaudage', productNameEn: 'Scaffold', equipment: null,
    customerName: 'Ben Ali BTP', month: '2026-11', qty: 8, revenueSource: 'Recharge', revenue: 16000, invoiceNumber: null, parts: 0,
    outsideWork: 11000, overhead: 0, labourHours: 0, labour: 0, cost: 11000, profit: 5000, costStatus: 'provisional', withheld: false,
    openedAtUtc: '2026-11-09T08:00:00Z', ...over,
  };
}

const PROFIT: MaintenanceProfitReport = {
  from: null, to: null, moneyShown: true,
  rows: [
    profitRow({}),
    profitRow({ jobId: 'c', number: 'MJ-000002', kind: 'Customer', productId: null, productSku: '', equipment: 'Bétonnière 350 L',
      revenueSource: 'Invoice', revenue: 20000, invoiceNumber: 'INV-000009', cost: 7200, profit: 12800 }),
    profitRow({ jobId: 'i', number: 'MJ-000003', kind: 'Internal', customerName: null, revenueSource: 'None', revenue: 0, cost: 1000, profit: -1000 }),
  ],
  byMonth: [{ key: '2026-11', label: '2026-11', nameAr: null, nameFr: null, nameEn: null, jobs: 3, revenue: 36000, cost: 19200, profit: 16800, withheld: false }],
  byBranch: [{ key: 'b1', label: 'Alger-Centre', nameAr: null, nameFr: null, nameEn: null, jobs: 3, revenue: 36000, cost: 19200, profit: 16800, withheld: false }],
  byKind: [
    { key: 'Customer', label: 'Customer', nameAr: null, nameFr: null, nameEn: null, jobs: 1, revenue: 20000, cost: 7200, profit: 12800, withheld: false },
    { key: 'Internal', label: 'Internal', nameAr: null, nameFr: null, nameEn: null, jobs: 1, revenue: 0, cost: 1000, profit: -1000, withheld: false },
    { key: 'Recharged', label: 'Recharged', nameAr: null, nameFr: null, nameEn: null, jobs: 1, revenue: 16000, cost: 11000, profit: 5000, withheld: false },
  ],
  byProduct: [
    { key: '', label: '', nameAr: null, nameFr: null, nameEn: null, jobs: 1, revenue: 20000, cost: 7200, profit: 12800, withheld: false },
    { key: 'p1', label: 'ECH-01', nameAr: 'سقالة', nameFr: 'Échafaudage', nameEn: 'Scaffold', jobs: 2, revenue: 16000, cost: null, profit: null, withheld: true },
  ],
  revenue: 36000, cost: null, profit: null, totalsWithheld: true,
};

describe('MaintenanceReportsComponent (maintenance, Phase M4)', () => {
  let fixture: ComponentFixture<MaintenanceReportsComponent>;
  let pages: jasmine.SpyObj<MaintenancePagesApi>;

  /** A report as the server pages it: its rows as one page, the rest (totals, words, breakdowns) as the summary. */
  function paged<T extends { rows: unknown[] }>(report: T) {
    const { rows, ...summary } = report;
    return pageOf(rows as never[], summary as never);
  }

  async function render(profit: MaintenanceProfitReport = PROFIT, products: MaintenanceProductCostReport = PRODUCTS): Promise<HTMLElement> {
    pages = jasmine.createSpyObj<MaintenancePagesApi>('MaintenancePagesApi', ['recharges', 'products', 'profit']);
    pages.recharges.and.resolveTo(paged(RECHARGES));
    pages.products.and.resolveTo(paged(products));
    pages.profit.and.resolveTo(paged(profit));
    TestBed.configureTestingModule({
      imports: [MaintenanceReportsComponent],
      providers: [provideRouter([]), { provide: MaintenancePagesApi, useValue: pages }],
    });
    signIn(['maintenance.view', 'maintenance.costs.view']);
    fixture = TestBed.createComponent(MaintenanceReportsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('sets each recharged job’s share against its cost, and says a withheld figure in words', async () => {
    const page = await render();
    const money = (n: number) => TestBed.inject(I18nService).money(n);
    expect(pages.recharges).toHaveBeenCalledWith({ skip: 0, take: 10, from: '', to: '' });
    expect(pages.products).toHaveBeenCalledWith({ skip: 0, take: 10, from: '', to: '' });
    const tables = page.querySelectorAll('table');
    const rows = tables[0].querySelectorAll('tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('MJ-000001');
    expect(rows[0].textContent).toContain(money(10000));
    expect(rows[0].textContent).toContain(t('maint.reports.ofLine', { amount: money(16000) }));
    expect(rows[0].textContent).toContain(money(3000));
    expect(rows[1].textContent).toContain(t('maint.cost.notShown'));
    expect(tables[0].querySelector('tfoot')?.textContent).toContain(money(16000));
    expect(tables[0].querySelector('tfoot')?.textContent).toContain(t('maint.cost.notShown'));
    expect(page.textContent).toContain(t('maint.reports.withheld'));
    expect(page.textContent).toContain(t('maint.reports.sharing.ByUnits'));
  });

  it('adds each equipment type’s jobs up, and asks again for a period', async () => {
    const page = await render();
    const product = page.querySelectorAll('table')[1].querySelector('tbody tr')!;
    expect(product.textContent).toContain('Échafaudage');
    expect(product.textContent).toContain(t('maint.reports.someProvisional'));
    expect(product.textContent).toContain(t('maint.cost.notShown'));

    const c = fixture.componentInstance;
    c.from.set('2026-10-01');
    c.to.set('2026-10-31');
    await afterFilter(fixture);
    expect(pages.recharges).toHaveBeenCalledWith({ skip: 0, take: 10, from: '2026-10-01', to: '2026-10-31' });
    expect(pages.products).toHaveBeenCalledWith({ skip: 0, take: 10, from: '2026-10-01', to: '2026-10-31' });
    expect(pages.profit).toHaveBeenCalledWith({ skip: 0, take: 10, from: '2026-10-01', to: '2026-10-31' });
  });

  it('says what of an equipment type’s cost was capitalised into its register and what stays an expense (Phase M7d, MS-30)', async () => {
    const money = (n: number) => TestBed.inject(I18nService).money(n);
    const row = { ...PRODUCTS.rows[0], labour: 2700, total: 3800, withheld: false, capitalised: 3500, expensed: 300 };
    const page = await render(PROFIT, { ...PRODUCTS, rows: [row], labour: 2700, total: 3800, totalsWithheld: false, capitalised: 3500, expensed: 300 });
    const table = page.querySelectorAll('table')[1];
    const split = t('maint.reports.capitalisedSplit', { capitalised: money(3500), expensed: money(300) });
    expect(table.querySelector('tbody tr')!.textContent).toContain(split);
    expect(table.querySelector('tfoot')!.textContent).toContain(split);
    // Each type opens its repair history (MS-38).
    expect(table.querySelector('tbody a')!.getAttribute('href')).toContain('/backoffice/maintenance/history?product=p1');
  });

  it('sets company profit (Phase M5) = external revenue − cost per job and by month, branch, kind and product', async () => {
    const page = await render();
    const money = (n: number) => TestBed.inject(I18nService).money(n);
    expect(pages.profit).toHaveBeenCalledWith({ skip: 0, take: 10, from: '', to: '' });
    const section = page.querySelector('[aria-labelledby="mr-profit"]')!;
    const rows = section.querySelector('table')!.querySelectorAll('tbody tr');
    expect(rows.length).toBe(3);
    // REN-11: 16,000 − 11,000 = 5,000.
    expect(rows[0].textContent).toContain(money(16000));
    expect(rows[0].textContent).toContain(money(5000));
    expect(rows[0].textContent).toContain(t('maint.profit.source.Recharge'));
    expect(rows[1].textContent).toContain('Bétonnière 350 L');
    expect(rows[1].textContent).toContain('INV-000009');
    expect(rows[1].textContent).toContain(money(12800));
    expect(rows[2].textContent).toContain(t('maint.profit.source.None'));
    // The totals and a product line withheld (labour privacy) are said in words, the revenue still shown.
    const foot = section.querySelector('tfoot')!.textContent!;
    expect(foot).toContain(money(36000));
    expect(foot).toContain(t('maint.cost.notShown'));
    expect(section.textContent).toContain(t('maint.profit.by.kind'));
    expect(section.textContent).toContain(t('maint.kind.Customer'));
    expect(section.textContent).toContain(t('maint.profit.noProduct'));
  });

  it('says how reworks under guarantee are reported (MS-37, Phase M7c), on the original’s line or their own', async () => {
    const money = (n: number) => TestBed.inject(I18nService).money(n);
    // Added to the original job (default): the original's line counts its rework and its cost.
    let page = await render({
      ...PROFIT, rework: 'AddedToOriginalJob',
      rows: [profitRow({ jobId: 'c', number: 'MJ-000002', kind: 'Customer', revenueSource: 'Invoice', revenue: 20000, cost: 9600, profit: 10400, reworks: 1, reworkCost: 2400 })],
    });
    let section = page.querySelector('[aria-labelledby="mr-profit"]')!;
    expect(section.textContent).toContain(t('maint.profit.rework.AddedToOriginalJob'));
    expect(section.textContent).toContain(TestBed.inject(I18nService).t('maint.profit.withReworks', { n: 1, cost: money(2400) }));
    TestBed.resetTestingModule();
    // Its own line: no revenue, naming the job it redoes.
    page = await render({
      ...PROFIT, rework: 'OwnJob',
      rows: [profitRow({ jobId: 'r', number: 'MJ-000005', kind: 'Customer', revenueSource: 'Rework', revenue: 0, cost: 2400, profit: -2400, reworkOfNumber: 'MJ-000002' })],
    });
    section = page.querySelector('[aria-labelledby="mr-profit"]')!;
    expect(section.textContent).toContain(t('maint.profit.rework.OwnJob'));
    expect(section.textContent).toContain(t('maint.profit.source.Rework'));
    expect(section.textContent).toContain(TestBed.inject(I18nService).t('maint.profit.reworkOf', { number: 'MJ-000002' }));
  });

  it('opens a job from its number', async () => {
    const page = await render();
    let opened: string | null = null;
    fixture.componentInstance.openJob.subscribe((id) => (opened = id));
    (page.querySelector('tbody .ui-link') as HTMLButtonElement).click();
    expect(opened as string | null).toBe('a');
  });
});
