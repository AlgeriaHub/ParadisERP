import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminService, MissingCostReport } from '../../core/admin.service';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { FinancePagesApi } from './finance/finance-pages.api';
import { BackofficeMissingCostComponent } from './missing-cost.component';

describe('BackofficeMissingCostComponent (SAL-02)', () => {
  let fixture: ComponentFixture<BackofficeMissingCostComponent>;
  let admin: jasmine.SpyObj<AdminService>;
  let pages: jasmine.SpyObj<FinancePagesApi>;

  const report = (over: Partial<MissingCostReport> = {}): MissingCostReport => ({
    from: '2026-11-01',
    to: '2026-11-30',
    salesHt: 4000,
    uncostedHt: 3000,
    uncostedSales: 4,
    productsWithoutCost: 1,
    rows: [
      { productId: 'p-pipe', sku: 'PIPE-90', nameAr: 'أنبوب', nameFr: 'Tube 90', nameEn: 'Pipe', qty: 3, revenueHt: 2000, sales: 3,
        lastSoldAtUtc: '2026-11-01T09:00:00Z', costNow: 600, valueAtCostNow: 1800 },
      { productId: 'p-clamp', sku: 'CLAMP', nameAr: 'مشبك', nameFr: 'Collier', nameEn: 'Clamp', qty: 1, revenueHt: 1000, sales: 1,
        lastSoldAtUtc: '2026-11-01T10:00:00Z', costNow: 0, valueAtCostNow: null },
    ],
    ...over,
  });

  async function render(r: MissingCostReport): Promise<HTMLElement> {
    admin = jasmine.createSpyObj<AdminService>('AdminService', ['listBranches']);
    admin.listBranches.and.resolveTo([]);
    // The server's page (owner, 2026-11-17): the rows of this page, the period's figures beside them.
    pages = jasmine.createSpyObj<FinancePagesApi>('FinancePagesApi', ['missingCostPage']);
    const { rows, ...figures } = r;
    pages.missingCostPage.and.callFake(async (q) => ({
      items: rows, total: rows.length, skip: 0, take: q.take, summary: { ...figures, all: rows.length },
    }));
    TestBed.configureTestingModule({
      imports: [BackofficeMissingCostComponent],
      providers: [provideRouter([]), { provide: AdminService, useValue: admin }, { provide: FinancePagesApi, useValue: pages }],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(BackofficeMissingCostComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const t = (key: string, params?: Record<string, string | number>) => TestBed.inject(I18nService).t(key, params);

  it('asks for the current month by default and takes the period the server answered', async () => {
    await render(report());
    expect(pages.missingCostPage).toHaveBeenCalledWith({ skip: 0, take: 10, from: null, to: null, branchId: null, cost: 'all' });
    expect(fixture.componentInstance.from).toBe('2026-11-01');
    expect(fixture.componentInstance.to).toBe('2026-11-30');
  });

  it('says each product state in words and opens the product to correct its cost', async () => {
    const page = await render(report());
    const text = page.textContent!;
    expect(text).toContain(t('missingCost.state.set'));
    expect(text).toContain(t('missingCost.state.missing'));
    expect(text).toContain('75 %');
    // No estimate without a cost: a dash, never a zero.
    const rows = page.querySelectorAll('tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[1].querySelector('td:last-child')!.textContent!.trim()).toBe('—');
    const link = rows[0].querySelector('a.ui-link') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/backoffice/inventory/products?product=p-pipe');
    expect(text).toContain(t('missingCost.estimateNote'));
  });

  it('asks the server again for the period in the fields, with the cost filter (owner, 2026-11-17)', async () => {
    await render(report());
    const c = fixture.componentInstance;
    c.from = '2026-10-01';
    c.to = '2026-10-31';
    await c.reload();
    c.costFilter.set('missing');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    expect(pages.missingCostPage).toHaveBeenCalledWith(
      jasmine.objectContaining({ skip: 0, take: 10, from: '2026-10-01', to: '2026-10-31', cost: 'missing' }),
    );
  });

  it('shows the empty state when nothing sold without a cost', async () => {
    const page = await render(report({ rows: [], uncostedHt: 0, uncostedSales: 0 }));
    expect(page.textContent).toContain(t('missingCost.empty'));
    expect(page.querySelector('table')).toBeNull();
  });
});
