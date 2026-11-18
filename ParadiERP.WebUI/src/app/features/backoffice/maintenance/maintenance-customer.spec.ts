import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ApiError } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { PrintService } from '../../print/print.service';
import { RentalReturnNoteComponent } from '../rentals/rental-return-note.component';
import { ContractDocuments, RentalSettings, RentalsApi } from '../rentals/rentals-api.service';
import { MaintenanceApi } from './maintenance-api.service';
import { MaintenanceCustomerApi, MaintenanceJobInvoice, MaintenanceJobPrice, MaintenanceRepairRates } from './maintenance-customer-api.service';
import { MaintenanceJobPriceComponent } from './maintenance-job-price.component';
import { MaintenanceJobsComponent } from './maintenance-jobs.component';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { MaintenanceRepairRatesComponent } from './maintenance-repair-rates.component';
import { job, pageOf, signIn, summary, t } from './maintenance-test-data';

/** A completed customer job, itemised: 4 bearings at 1,500 + 3 h × 1,200 + 4,000 outside + 10 % of it = 14,000 before tax. */
function price(over: Partial<MaintenanceJobPrice> = {}): MaintenanceJobPrice {
  return {
    jobId: 'j1', number: 'MJ-000001', kind: 'Customer', status: 'Completed', basis: 'Itemised', labourChargePerHour: 1200, markupPercent: 10,
    hours: 3, partsUsedQty: 4, outsideWork: 4000, parts: 6000, labour: 3600, markup: 400, costBase: null, proposed: 14000,
    costShown: true, costAvailable: true, cost: 7200, costProvisional: true, price: null, pricedBasis: null, proposedAtPricing: null,
    pricedBy: null, pricedAtUtc: null, taxMode: 'Untaxed', taxRateWhenTaxed: 19, belowCostRule: 'ShownAsLoss', belowCost: false, loss: null,
    belowCostConfirmedBy: null, belowCostConfirmedAtUtc: null, customerIncome: 'SameAsRecharges', incomeAccountCode: '707',
    invoiceId: null, invoiceNumber: null, ...over,
  };
}

const customerJob = () =>
  job({ kind: 'Customer', status: 'Completed', productId: null, productSku: '', equipment: 'Bétonnière 350 L', customerId: 'cu1',
    customerName: 'Ben Ali BTP', contractId: null, returnNoteId: null, returnNoteLineId: null, lineDamagedQty: null, lineRecharge: null, qty: 1 });

function invoiceResult(): MaintenanceJobInvoice {
  return {
    invoiceId: 'inv1', invoiceNumber: 'INV-000009', subTotalHt: 14000, taxTotal: 0, grandTotalTtc: 14000, taxMode: 'Untaxed',
    incomeAccountCode: '707', warning: null, job: { ...customerJob(), status: 'Invoiced', invoiceId: 'inv1', invoiceNumber: 'INV-000009' },
  };
}

describe('MaintenanceJobPriceComponent (maintenance, Phase M5a)', () => {
  let fixture: ComponentFixture<MaintenanceJobPriceComponent>;
  let api: jasmine.SpyObj<MaintenanceCustomerApi>;

  async function render(answer: MaintenanceJobPrice, keys = ['maintenance.view', 'maintenance.pricing', 'finance.invoices.create']): Promise<HTMLElement> {
    api = jasmine.createSpyObj<MaintenanceCustomerApi>('MaintenanceCustomerApi', ['price', 'setPrice', 'invoice']);
    api.price.and.resolveTo(answer);
    TestBed.configureTestingModule({
      imports: [MaintenanceJobPriceComponent],
      providers: [{ provide: MaintenanceCustomerApi, useValue: api }, { provide: PrintService, useValue: { open: jasmine.createSpy('open') } }],
    });
    signIn(keys);
    fixture = TestBed.createComponent(MaintenanceJobPriceComponent);
    fixture.componentRef.setInput('job', customerJob());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the itemised pieces before tax, proposes 14,000 and keeps VAT off until it is ticked', async () => {
    const page = await render(price());
    const money = (n: number) => TestBed.inject(I18nService).money(n);
    expect(api.price).toHaveBeenCalledWith('j1');
    for (const n of [6000, 3600, 4000, 400, 14000]) expect(page.textContent).toContain(money(n));
    expect(page.textContent).toContain(t('maint.price.vatNone'));
    expect(fixture.componentInstance.draft).toBe(14000);
    expect((page.querySelector('#mjp-vat') as HTMLInputElement).checked).toBeFalse();

    api.setPrice.and.resolveTo(price({ price: 14000, pricedBy: 'chef', pricedBasis: 'Itemised' }));
    await fixture.componentInstance.save();
    expect(api.setPrice).toHaveBeenCalledWith('j1', 14000, 'Untaxed', false);
  });

  it('asks for a confirmation under MS-25, then sends it — never refuses a price below cost', async () => {
    const page = await render(price({ belowCostRule: 'NeedsConfirmation' }));
    api.setPrice.and.returnValues(
      Promise.reject(new ApiError(JSON.stringify({ code: 'price_below_cost', detail: 'below cost' }), 409)),
      Promise.resolve(price({ price: 5000, belowCost: true, loss: 2200, belowCostConfirmedBy: 'chef' })),
    );
    const c = fixture.componentInstance;
    c.draft = 5000;
    await c.save();
    fixture.detectChanges();
    expect(c.askConfirm()).toBeTrue();
    expect(page.querySelector('#mjp-confirm')).not.toBeNull();
    c.confirmBelowCost = true;
    await c.save();
    fixture.detectChanges();
    expect(api.setPrice.calls.mostRecent().args).toEqual(['j1', 5000, 'Untaxed', true]);
    expect(page.textContent).toContain(t('maint.price.loss', { amount: TestBed.inject(I18nService).money(2200) }));
    expect(page.textContent).toContain(t('maint.price.lossConfirmed', { who: 'chef' }));
  });

  it('says a withheld cost in words and never shows the cost-plus proposal to a reader who may not see the cost', async () => {
    const page = await render(price({ basis: 'CostPlus', parts: null, labour: null, markup: null, costBase: null, proposed: null, costShown: false, cost: null }));
    expect(page.textContent).toContain(t('maint.price.costHidden'));
    expect(page.textContent).toContain(t('maint.cost.notShown'));
    expect(fixture.componentInstance.draft).toBeNull();
  });

  it('raises the one invoice of a completed job, untaxed unless the VAT box is ticked', async () => {
    const page = await render(price({ price: 14000 }));
    (Array.from(page.querySelectorAll('section button')).find((b) => b.textContent?.includes(t('maint.invoice.raise'))) as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const dialog = document.querySelector('#mi-vat') as HTMLInputElement;
    expect(dialog).not.toBeNull();
    expect(dialog.checked).toBeFalse();

    api.invoice.and.resolveTo(invoiceResult());
    api.price.and.resolveTo(price({ price: 14000, invoiceId: 'inv1', invoiceNumber: 'INV-000009', status: 'Invoiced' }));
    let invoiced: string | null = null;
    fixture.componentInstance.invoiced.subscribe((j) => (invoiced = j.status));
    (Array.from(document.querySelectorAll('app-maintenance-invoice-dialog button')).find((b) => b.textContent?.includes(t('maint.invoice.raise'))) as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    const args = api.invoice.calls.mostRecent().args;
    expect(args[0]).toBe('j1');
    expect(args[2]).toBe('Untaxed');
    expect(invoiced as string | null).toBe('Invoiced');
    expect(page.textContent).toContain(t('maint.invoice.raised', { n: 'INV-000009' }));
  });

  it('offers no price form without maintenance.pricing, and no invoice without finance.invoices.create', async () => {
    const page = await render(price({ price: 14000 }), ['maintenance.view']);
    expect(page.querySelector('#mjp-price')).toBeNull();
    expect(Array.from(page.querySelectorAll('section button')).some((b) => b.textContent?.includes(t('maint.invoice.raise')))).toBeFalse();
  });
});

describe('MaintenanceJobsComponent, customer jobs (maintenance, Phase M5a)', () => {
  it('shows a customer job by its equipment and offers Raise the invoice in its ⋯ menu once completed', async () => {
    const api = jasmine.createSpyObj<MaintenanceApi>('MaintenanceApi', ['cancel']);
    const pages = jasmine.createSpyObj<MaintenancePagesApi>('MaintenancePagesApi', ['jobs']);
    pages.jobs.and.resolveTo(pageOf([
      summary({ id: 'c1', number: 'MJ-000004', kind: 'Customer', status: 'Completed', productId: null, productSku: '', equipment: 'Bétonnière 350 L',
        jobCustomerName: 'Ben Ali BTP', contractId: null, returnNoteId: null, returnNoteNumber: null, contractNumber: null }),
    ], { branches: [] }));
    TestBed.configureTestingModule({
      imports: [MaintenanceJobsComponent],
      providers: [{ provide: MaintenanceApi, useValue: api }, { provide: MaintenancePagesApi, useValue: pages }],
    });
    signIn(['maintenance.view', 'finance.invoices.create']);
    const fixture = TestBed.createComponent(MaintenanceJobsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain('Bétonnière 350 L');
    expect(page.textContent).toContain('Ben Ali BTP');
    (page.querySelector('app-menu button') as HTMLButtonElement).click();
    fixture.detectChanges();
    const raise = Array.from(document.querySelectorAll('[appMenuItem]')).find((b) => b.textContent?.includes(t('maint.invoice.raise'))) as HTMLButtonElement;
    expect(raise).toBeTruthy();
    raise.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.invoicing()?.id).toBe('c1');
  });
});

describe('MaintenanceRepairRatesComponent (maintenance, Phase M5a, MS-23)', () => {
  it('lists the rates, says whether the return note suggests from them, and keeps one', async () => {
    const rates: MaintenanceRepairRates = {
      returnRecharge: 'SuggestedFromRate',
      rates: [{ productId: 'p1', sku: 'ECH-01', nameAr: 'سقالة', nameFr: 'Échafaudage', nameEn: 'Scaffold', ratePerUnit: 2000, updatedBy: 'tarif', updatedAtUtc: '2026-11-09T08:00:00Z' }],
      products: [{ id: 'p1', sku: 'ECH-01', nameAr: 'سقالة', nameFr: 'Échafaudage', nameEn: 'Scaffold' }],
    };
    const api = jasmine.createSpyObj<MaintenanceCustomerApi>('MaintenanceCustomerApi', ['setRepairRate', 'removeRepairRate']);
    api.setRepairRate.and.resolveTo(rates);
    // The server pages the rates (owner, 2026-11-17): the page, and MS-23 and the products in its summary.
    const pages = jasmine.createSpyObj<MaintenancePagesApi>('MaintenancePagesApi', ['repairRates']);
    pages.repairRates.and.resolveTo(pageOf(rates.rates, { returnRecharge: rates.returnRecharge, products: rates.products, rates: rates.rates.length }));
    TestBed.configureTestingModule({
      imports: [MaintenanceRepairRatesComponent],
      providers: [{ provide: MaintenanceCustomerApi, useValue: api }, { provide: MaintenancePagesApi, useValue: pages }],
    });
    signIn(['maintenance.view', 'maintenance.pricing']);
    const fixture = TestBed.createComponent(MaintenanceRepairRatesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;
    expect(page.textContent).toContain(t('maint.rates.on'));
    expect(page.textContent).toContain(TestBed.inject(I18nService).money(2000));
    expect(pages.repairRates).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, q: '' }));
    const c = fixture.componentInstance;
    // A product's rate is read from the server, whatever page it is on.
    await c.pick('p1');
    expect(pages.repairRates).toHaveBeenCalledWith({ skip: 0, take: 1, productId: 'p1' });
    expect(c.rate).toBe(2000);
    c.rate = 2500;
    await c.save();
    expect(api.setRepairRate).toHaveBeenCalledWith('p1', 2500);
    // The page on screen is asked for again after the change.
    expect(pages.repairRates).toHaveBeenCalledTimes(3);
  });
});

describe('the return note suggests a repair recharge from the rate (MS-23), and only suggests', () => {
  const docs = {
    contract: {
      id: 'c1', number: 'CTR-0001', customerId: 'cu1', customerName: 'Ben Ali BTP', branchId: 'b1',
      lines: [{
        id: 'l1', productId: 'p1', sku: 'ECH-01', name: 'Échafaudage', qty: 10, durationDays: 3, rentUnitPrice: 100, damageUnitPrice: 2000,
        overdueUnitPrice: 150, parentLineId: null, issuedQty: 10, leftToIssueQty: 0, settledQty: 0, stillOutQty: 10, taxRate: 0,
      }],
    },
    issueNotes: [{ id: 'i1', issuedAtUtc: '2026-09-26T14:00:00Z', periodStartsOn: '2026-09-27', lines: [{ contractLineId: 'l1', qty: 10 }] }],
    returnNotes: [], extensions: [], settlement: {},
  } as unknown as ContractDocuments;

  async function note(enabled: boolean): Promise<ComponentFixture<RentalReturnNoteComponent>> {
    const maintenance = jasmine.createSpyObj<MaintenanceCustomerApi>('MaintenanceCustomerApi', ['repairSuggestions']);
    maintenance.repairSuggestions.and.resolveTo({ enabled, rates: enabled ? [{ productId: 'p1', ratePerUnit: 2000 }] : [] });
    TestBed.configureTestingModule({
      providers: [
        { provide: RentalsApi, useValue: { readContract: async () => docs, return: jasmine.createSpy('return'), previewReturn: jasmine.createSpy('preview').and.resolveTo(null) } },
        { provide: AuthService, useValue: { currentUser: signal({ role: 'Admin', branchId: 'b1', branchLocked: false, permissions: [], capabilities: [] }) } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
        { provide: MaintenanceCustomerApi, useValue: maintenance },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    const fixture = TestBed.createComponent(RentalReturnNoteComponent);
    fixture.componentRef.setInput('contractId', 'c1');
    fixture.componentRef.setInput('settings', { overdueMultiplier: 1.5, depositEnabled: false, taxRate: 0, dayCutoffHour: 12 } as RentalSettings);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.componentInstance.patch('l1', { qtyOk: 8, qtyPartialDamage: 2 });
    fixture.detectChanges();
    return fixture;
  }

  it('offers 2 × 2,000 = 4,000 beside the typed recharge, which stays 0 until the clerk takes it', async () => {
    const fixture = await note(true);
    const c = fixture.componentInstance;
    expect(c.suggestedRepair('p1', 2)).toEqual({ qty: 2, rate: 2000, amount: 4000 });
    expect(c.draft('l1').maintenanceValue).toBe(0);
    const money = (n: number) => TestBed.inject(I18nService).money(n);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(t('maint.rates.suggestion', { n: 2, rate: money(2000), amount: money(4000) }));
    const use = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) => b.textContent?.includes(t('maint.rates.useSuggestion'))) as HTMLButtonElement;
    use.click();
    expect(c.draft('l1').maintenanceValue).toBe(4000);
  });

  it('suggests nothing while the module or MS-23 is off', async () => {
    const fixture = await note(false);
    expect(fixture.componentInstance.suggestedRepair('p1', 2)).toBeNull();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain(t('maint.rates.useSuggestion'));
  });
});
