import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminService } from '../../core/admin.service';
import { ApiError } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { SalesCosting, ValuationReport, ValuationRow, ValuationService, ValuationSettings } from '../../core/valuation.service';
import { FinancePagesApi } from './finance/finance-pages.api';
import { BackofficeStockValuationComponent } from './stock-valuation.component';

describe('BackofficeStockValuationComponent (P0.4b, C-01)', () => {
  let fixture: ComponentFixture<BackofficeStockValuationComponent>;
  let valuation: jasmine.SpyObj<ValuationService>;
  let pages: jasmine.SpyObj<FinancePagesApi>;

  const row = (over: Partial<ValuationRow> = {}): ValuationRow => ({
    productId: 'p-pipe', sku: 'PIPE-90', nameAr: 'أنبوب', nameFr: 'Tube 90', nameEn: 'Pipe',
    branchId: null, branchNameAr: null, branchNameFr: null, branchNameEn: null,
    openingQty: 20, openingValue: 12000, inQty: 10, inValue: 9000, outQty: 1, outValue: 700,
    movedInQty: 0, movedInValue: 0, movedOutQty: 0, movedOutValue: 0,
    closingQty: 29, closingValue: 20300, averageCost: 700, referenceCost: 900, referenceValue: 26100, difference: -5800,
    negativeStockGap: 0, postedCostOfSales: 900, shadowCostOfSales: 700,
    outputsAtReference: 0, outflowsWithoutAverage: 0, unclassified: 0,
    ...over,
  });

  const settings = (over: Partial<ValuationSettings> = {}): ValuationSettings => ({
    scope: 'Company', refundCost: 'SaleCost', unitCostDecimals: 3, startedAtUtc: '2026-11-08T08:00:00Z', updatedAtUtc: null, updatedBy: null,
    salesCost: 'CostPrice', salesCostChangedAtUtc: null, salesCostChangedBy: null,
    ...over,
  });

  const report = (rows: ValuationRow[] = [row()], s: ValuationSettings = settings()): ValuationReport => ({
    settings: s,
    generatedAtUtc: '2026-11-08T09:00:00Z',
    rows,
    totals: {
      openingValue: 12000, inValue: 9000, outValue: 700, closingValue: 20300, referenceValue: 26100, difference: -5800,
      negativeStockGap: 0, inTransitValue: 0, postedCostOfSales: 900, shadowCostOfSales: 700,
    },
  });

  const account = (slot: string, mapped: boolean) => ({
    slot, mapped, code: mapped ? '890' : null, nameAr: null, nameFr: mapped ? "Bilan d'ouverture" : null, nameEn: mapped ? 'Opening balance' : null,
  });

  const costing = (over: Partial<SalesCosting> = {}): SalesCosting => ({
    settings: settings(), shadowValue: 21000, bookValue: 9000, adjustment: 12000, stockAccounts: ['310', '318'],
    openingAdjustmentAccount: account('opening_adjustment', true), valuationDifferenceAccount: account('valuation_difference', false),
    pendingGaps: [], pendingGapTotal: 0, fallbackLines: 0, history: [],
    ...over,
  });

  async function render(r: ValuationReport, keys: string[], c: SalesCosting = costing()): Promise<HTMLElement> {
    valuation = jasmine.createSpyObj<ValuationService>('ValuationService', ['saveSettings', 'switchSalesCost', 'postGaps']);
    valuation.saveSettings.and.resolveTo(r.settings);
    // The server's pages (owner, 2026-11-17): the report's rows and the pending gaps, each with its figures beside it.
    pages = jasmine.createSpyObj<FinancePagesApi>('FinancePagesApi', ['valuationPage', 'salesCostGapsPage', 'valuationProductPage']);
    pages.valuationPage.and.callFake(async (q) => {
      const { rows, ...figures } = r;
      return { items: rows, total: rows.length, skip: 0, take: q.take, summary: { ...figures, all: rows.length } };
    });
    costingIs(c);
    const admin = jasmine.createSpyObj<AdminService>('AdminService', ['listBranches']);
    admin.listBranches.and.resolveTo([]);
    TestBed.configureTestingModule({
      imports: [BackofficeStockValuationComponent],
      providers: [
        provideRouter([]),
        { provide: ValuationService, useValue: valuation },
        { provide: FinancePagesApi, useValue: pages },
        { provide: AdminService, useValue: admin },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    TestBed.inject(AuthService).currentUser.set({
      id: 'u1', username: 'acc', displayName: 'Accountant', role: 'Manager', permissions: [], capabilities: [],
      permissionKeys: keys, scope: 'Company',
    } as never);
    fixture = TestBed.createComponent(BackofficeStockValuationComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  /** The costing status the server answers, its gaps as the page. */
  function costingIs(c: SalesCosting): void {
    pages.salesCostGapsPage.and.callFake(async (q) => ({
      items: c.pendingGaps, total: c.pendingGaps.length, skip: 0, take: q.take, summary: { ...c, pendingGaps: [] },
    }));
  }

  const t = (key: string, params?: Record<string, string | number>) => TestBed.inject(I18nService).t(key, params);
  const button = (page: HTMLElement, label: string) =>
    Array.from(page.querySelectorAll('button')).find((b) => b.textContent?.trim() === label) as HTMLButtonElement | undefined;

  it('says it is a simulation and shows the average beside the cost price', async () => {
    const page = await render(report(), ['reports.accounting']);
    expect(page.textContent).toContain(t('valuation.shadowNote'));
    expect(page.querySelectorAll('tbody tr').length).toBe(1);
    expect(page.textContent).toContain(t('valuation.atCostPrice'));
  });

  it('lets only a user who manages the accounts change the open choices', async () => {
    const page = await render(report(), ['reports.accounting']);
    expect((page.querySelector('#sv-scope') as HTMLSelectElement).disabled).toBeTrue();
    expect(page.textContent).toContain(t('valuation.settings.readOnly'));
  });

  it('saves the choices the admin made and reloads the figures', async () => {
    await render(report(), ['reports.accounting', 'accounting.manage']);
    const c = fixture.componentInstance;
    c.draftScope = 'Location';
    expect(c.dirty()).toBeTrue();
    await c.save();
    expect(valuation.saveSettings).toHaveBeenCalledWith({
      scope: 'Location', refundCost: 'SaleCost', unitCostDecimals: 3, directInvoiceStock: false,
    });
    expect(pages.valuationPage).toHaveBeenCalledTimes(2);
  });

  it('offers the direct-invoice stock choice, off by default, and saves it (P0.3)', async () => {
    const page = await render(report(), ['reports.accounting', 'accounting.manage']);
    const select = page.querySelector('#sv-direct-invoice') as HTMLSelectElement;
    expect(select.disabled).toBeFalse();
    expect(select.selectedOptions[0].textContent?.trim()).toBe(t('valuation.directInvoice.off'));
    expect(page.textContent).toContain(t('valuation.settings.directInvoiceHelp'));

    const c = fixture.componentInstance;
    c.draftDirectInvoiceStock = true;
    expect(c.dirty()).toBeTrue();
    await c.save();
    expect(valuation.saveSettings).toHaveBeenCalledWith({
      scope: 'Company', refundCost: 'SaleCost', unitCostDecimals: 3, directInvoiceStock: true,
    });
  });

  it('keeps the direct-invoice choice open while sales are costed at the average, and read only for a reader', async () => {
    const on = settings({ salesCost: 'WeightedAverage', directInvoiceStock: true });
    let page = await render(report([row()], on), ['reports.accounting', 'accounting.manage'], costing({ settings: on }));
    let select = page.querySelector('#sv-direct-invoice') as HTMLSelectElement;
    expect(select.disabled).toBeFalse();
    expect(select.selectedOptions[0].textContent?.trim()).toBe(t('valuation.directInvoice.on'));

    TestBed.resetTestingModule();
    page = await render(report(), ['reports.accounting']);
    select = page.querySelector('#sv-direct-invoice') as HTMLSelectElement;
    expect(select.disabled).toBeTrue();
  });

  it('shows the cost price on receipt at the last cost by default, read only for a reader', async () => {
    const page = await render(report(), ['reports.accounting']);
    const select = page.querySelector('#sv-receipt') as HTMLSelectElement;
    expect(select.disabled).toBeTrue();
    expect(fixture.componentInstance.draftReceipt).toBe('LastCost');
    expect(page.textContent).toContain(t('valuation.receipt.help.LastCost'));
    expect(page.textContent).toContain(t('valuation.receipt.zero'));
    expect(button(page, t('valuation.receipt.save'))).toBeUndefined();
  });

  it('saves the cost price on receipt with the saved choices, even while sales post the average', async () => {
    const on = settings({ salesCost: 'WeightedAverage', receiptCostRule: 'Unchanged' });
    const page = await render(report([row()], on), ['reports.accounting', 'accounting.manage'], costing({ settings: on }));
    expect((page.querySelector('#sv-receipt') as HTMLSelectElement).disabled).toBeFalse();
    const c = fixture.componentInstance;
    expect(c.draftReceipt).toBe('Unchanged');
    expect(c.receiptDirty()).toBeFalse();
    // A pending edit of the shadow's choices is not sent with it.
    c.draftScope = 'Location';
    c.draftReceipt = 'WeightedAverage';
    expect(c.receiptDirty()).toBeTrue();
    await c.saveReceipt();
    expect(valuation.saveSettings).toHaveBeenCalledWith({
      scope: 'Company', refundCost: 'SaleCost', unitCostDecimals: 3, receiptCostRule: 'WeightedAverage',
    });
    expect(pages.valuationPage).toHaveBeenCalledTimes(2);
  });

  it("shows the three valuation postings at today's behaviour by default, read only for a reader", async () => {
    const page = await render(report(), ['reports.accounting']);
    const c = fixture.componentInstance;
    expect([c.draftGapPosting, c.draftRevaluation, c.draftZeroCost]).toEqual(['Accountant', 'None', 'Zero']);
    for (const id of ['#sv-gap-posting', '#sv-revaluation', '#sv-zero-cost']) {
      expect((page.querySelector(id) as HTMLSelectElement).disabled).withContext(id).toBeTrue();
    }
    expect(page.textContent).toContain(t('valuation.postings.gapsHelp.Accountant'));
    expect(page.textContent).toContain(t('valuation.postings.revaluationHelp.None'));
    expect(page.textContent).toContain(t('valuation.postings.zeroHelp.Zero'));
    expect(button(page, t('valuation.postings.save'))).toBeUndefined();
  });

  it('saves the three valuation postings with the saved choices, even while sales post the average', async () => {
    const on = settings({ salesCost: 'WeightedAverage', gapPosting: 'Accountant', costPriceRevaluation: 'None', zeroCostReceipt: 'Zero' });
    const mapped = costing({ settings: on, valuationDifferenceAccount: account('valuation_difference', true) });
    const page = await render(report([row()], on), ['reports.accounting', 'accounting.manage'], mapped);
    expect((page.querySelector('#sv-gap-posting') as HTMLSelectElement).disabled).toBeFalse();
    const c = fixture.componentInstance;
    expect(c.postingsDirty()).toBeFalse();
    // A pending edit of the shadow's choices is not sent with them.
    c.draftScope = 'Location';
    c.draftGapPosting = 'Automatic';
    c.draftRevaluation = 'StockOnHand';
    c.draftZeroCost = 'CostPrice';
    fixture.detectChanges();
    expect(c.postingsDirty()).toBeTrue();
    expect(c.revaluationUnmapped()).toBeFalse();
    // Under the average there is nothing at the cost price to revalue: said in words.
    expect(page.textContent).toContain(t('valuation.postings.revaluationAverage'));
    expect(page.textContent).toContain(t('valuation.postings.gapsHelp.Automatic'));
    await c.savePostings();
    expect(valuation.saveSettings).toHaveBeenCalledWith({
      scope: 'Company', refundCost: 'SaleCost', unitCostDecimals: 3,
      gapPosting: 'Automatic', costPriceRevaluation: 'StockOnHand', zeroCostReceipt: 'CostPrice',
    });
    expect(pages.valuationPage).toHaveBeenCalledTimes(2);
  });

  it('will not save a revaluation while the valuation-difference account is unmapped, and says why', async () => {
    const page = await render(report(), ['reports.accounting', 'accounting.manage']);
    const c = fixture.componentInstance;
    c.draftRevaluation = 'StockOnHand';
    fixture.detectChanges();
    expect(c.revaluationUnmapped()).toBeTrue();
    expect(page.textContent).toContain(t('valuation.postings.differenceUnmapped'));
    expect(button(page, t('valuation.postings.save'))?.disabled).toBeTrue();
    // The other two need no account.
    c.draftRevaluation = 'None';
    c.draftGapPosting = 'Automatic';
    fixture.detectChanges();
    expect(button(page, t('valuation.postings.save'))?.disabled).toBeFalse();
  });

  it('says in words what a figure rests on', async () => {
    const page = await render(report([row({ outputsAtReference: 2, negativeStockGap: 150 })]), ['reports.accounting']);
    expect(page.textContent).toContain(t('valuation.flag.outputs', { n: 2 }));
  });

  it('asks the server for the page with the filters, and opens the movements of a product a page at a time (owner, 2026-11-17)', async () => {
    await render(report(), ['reports.accounting']);
    expect(pages.valuationPage).toHaveBeenCalledWith({ skip: 0, take: 10, branchId: null, qty: 'all', difference: 'all', notes: 'all' });
    const c = fixture.componentInstance;
    c.qtyFilter.set('negative');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    expect(pages.valuationPage).toHaveBeenCalledWith({ skip: 0, take: 10, branchId: null, qty: 'negative', difference: 'all', notes: 'all' });

    pages.valuationProductPage.and.callFake(async (_id, q) => ({
      items: [], total: 0, skip: 0, take: q.take,
      summary: { productId: 'p-pipe', settings: settings(), summary: null, places: [], lineCount: 0 },
    }));
    c.open(row());
    expect(c.detailBusy()).toBeTrue();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(pages.valuationProductPage).toHaveBeenCalledWith('p-pipe', { skip: 0, take: 10 });
    expect(c.detail()?.lineCount).toBe(0);
    expect(c.detailBusy()).toBeFalse();
  });

  it('shows the empty state when nothing is stocked', async () => {
    const page = await render(report([]), ['reports.accounting']);
    expect(page.textContent).toContain(t('valuation.empty'));
  });

  it('says sales are at the cost price by default and keeps the switch figures from a reader', async () => {
    const page = await render(report(), ['reports.accounting']);
    expect(page.querySelector('[data-testid="sales-cost-method"]')?.textContent).toContain(t('valuation.cost.method.CostPrice'));
    expect(page.textContent).toContain(t('valuation.cost.readOnly'));
    expect(pages.salesCostGapsPage).not.toHaveBeenCalled();
    expect(button(page, t('valuation.cost.switchOn'))).toBeUndefined();
  });

  it('refuses the switch in words until the opening-adjustment account is named', async () => {
    const page = await render(report(), ['reports.accounting', 'accounting.manage'],
      costing({ openingAdjustmentAccount: account('opening_adjustment', false) }));
    expect(page.textContent).toContain(t('valuation.cost.openingUnmapped'));
    expect(button(page, t('valuation.cost.switchOn'))!.disabled).toBeTrue();
  });

  it('shows the figure first and switches with the figure shown', async () => {
    const page = await render(report(), ['reports.accounting', 'accounting.manage']);
    expect(page.textContent).toContain(t('valuation.cost.adjustment'));
    button(page, t('valuation.cost.switchOn'))!.click();
    fixture.detectChanges();
    const c = fixture.componentInstance;
    expect(c.confirming()).toBe('WeightedAverage');
    valuation.switchSalesCost.and.resolveTo(costing({ settings: settings({ salesCost: 'WeightedAverage' }) }));
    await c.confirmSwitch();
    expect(valuation.switchSalesCost).toHaveBeenCalledWith('WeightedAverage', 12000);
    expect(c.confirming()).toBeNull();
    expect(pages.valuationPage).toHaveBeenCalledTimes(2);
  });

  it('keeps the dialog open on the new figure when the adjustment moved meanwhile', async () => {
    await render(report(), ['reports.accounting', 'accounting.manage']);
    const c = fixture.componentInstance;
    c.askSwitch('WeightedAverage');
    valuation.switchSalesCost.and.rejectWith(new ApiError(JSON.stringify({ status: 409, code: 'adjustment_changed' }), 409));
    costingIs(costing({ adjustment: 12500 }));
    await c.confirmSwitch();
    expect(c.confirming()).toBe('WeightedAverage');
    expect(c.costing()!.adjustment).toBe(12500);
    expect(c.costError()).toBe(t('valuation.cost.error.adjustment_changed'));
  });

  it('once on, locks the choices, lists the gaps and switches back without a figure', async () => {
    const on = settings({ salesCost: 'WeightedAverage', salesCostChangedAtUtc: '2026-11-09T08:00:00Z', salesCostChangedBy: 'acc' });
    const gap = {
      movementId: 'm1', atUtc: '2026-11-09T09:00:00Z', productId: 'p-pipe', sku: 'PIPE-90', nameAr: 'أنبوب', nameFr: 'Tube 90', nameEn: 'Pipe',
      branchId: 'b1', kind: 'Receipt', documentNumber: 'BR-1', amount: 1500,
    };
    const page = await render(report([row()], on), ['reports.accounting', 'accounting.manage'],
      costing({ settings: on, adjustment: 0, pendingGaps: [gap], pendingGapTotal: 1500 }));
    expect(page.textContent).toContain(t('valuation.shadowNoteOn'));
    expect((page.querySelector('#sv-scope') as HTMLSelectElement).disabled).toBeTrue();
    expect(page.textContent).toContain(t('valuation.cost.locked'));
    expect(page.textContent).toContain(t('valuation.cost.differenceUnmapped'));
    expect(button(page, t('valuation.cost.postGaps'))!.disabled).toBeTrue();

    const c = fixture.componentInstance;
    c.askSwitch('CostPrice');
    valuation.switchSalesCost.and.resolveTo(costing());
    await c.confirmSwitch();
    expect(valuation.switchSalesCost).toHaveBeenCalledWith('CostPrice', null);
  });

  it('once on, counts the stock movements that fell back to their reference cost, and says only production drifts', async () => {
    const on = settings({ salesCost: 'WeightedAverage', salesCostChangedAtUtc: '2026-11-09T08:00:00Z', salesCostChangedBy: 'acc' });
    let page = await render(report([row()], on), ['reports.accounting', 'accounting.manage'],
      costing({ settings: on, adjustment: 0, fallbackMovements: 2 }));
    expect(page.textContent).toContain(t('valuation.cost.fallbackMovements', { n: 2 }));
    expect(page.textContent).toContain(t('valuation.cost.driftNote'));

    // None (or a server that does not send the count): no notice.
    TestBed.resetTestingModule();
    page = await render(report([row()], on), ['reports.accounting', 'accounting.manage'], costing({ settings: on, adjustment: 0 }));
    const words = t('valuation.cost.fallbackMovements', { n: 0 }).replace(/^0\s*/, '');
    expect(page.textContent).not.toContain(words);
  });
});
