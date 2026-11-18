import { ComponentFixture, TestBed } from '@angular/core/testing';
import { I18nService } from '../../../core/i18n.service';
import { MaintenanceApi } from './maintenance-api.service';
import { MaintenanceHistoryJob, MaintenanceRepairHistory } from './maintenance-cost-api.service';
import { MaintenanceHistoryComponent } from './maintenance-history.component';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { pageOf, signIn, summary, t } from './maintenance-test-data';

function historyJob(over: Partial<MaintenanceHistoryJob>): MaintenanceHistoryJob {
  return {
    id: 'j1', number: 'MJ-000001', kind: 'Internal', status: 'Completed', branchId: 'b1', branchName: 'Alger-Centre', itemReference: 'BT-07',
    qty: 1, repairedQty: 1, scrappedQty: 0, openedAtUtc: '2026-10-02T08:00:00Z', completedAtUtc: '2026-10-05T08:00:00Z', labourHours: 6,
    parts: 3200, partLines: [{ productId: 'rod', sku: 'ROD-25', nameAr: 'قضيب', nameFr: 'Baguette', nameEn: 'Rod', qty: 80, value: 3200 }],
    outsideWork: 4000, cost: 9900, costWithheld: false, capitalised: null, costStatus: 'provisional', ...over,
  };
}

/** A mixer (900,000 a piece, tracked above 500,000): two jobs on BT-07, one of them capitalised (MNT-01's engine rebuild). */
const PIECE: MaintenanceRepairHistory = {
  productId: 'p9', sku: 'BET-350', nameAr: 'خلاطة', nameFr: 'Bétonnière', nameEn: 'Mixer', mode: 'PerPiece', trackedPerPiece: true,
  unitValue: 900000, perPieceAboveValue: 500000, reference: 'BT-07', pieces: [{ reference: 'BT-07', jobs: 2, lastAtUtc: '2026-11-03T08:00:00Z' }],
  jobs: [historyJob({}), historyJob({ id: 'j2', number: 'MJ-000004', openedAtUtc: '2026-11-03T08:00:00Z', cost: 180000, capitalised: 180000 })],
  labourHours: 12, parts: 6400, outsideWork: 8000, cost: 189900, capitalised: 180000, costsShown: true, totalsWithheld: false,
};

describe('MaintenanceHistoryComponent (maintenance, Phase M7d, MS-38)', () => {
  let fixture: ComponentFixture<MaintenanceHistoryComponent>;
  let pages: jasmine.SpyObj<MaintenancePagesApi>;

  /** The server's pages of this history: its jobs and pieces as one page each, its head and references as the summary. */
  async function render(answer: MaintenanceRepairHistory, product: string | null = 'p9', reference: string | null = 'BT-07'): Promise<HTMLElement> {
    pages = jasmine.createSpyObj<MaintenancePagesApi>('MaintenancePagesApi', ['historyJobs', 'historyPieces']);
    pages.historyJobs.and.resolveTo(pageOf(answer.jobs, { history: { ...answer, jobs: [], pieces: [] }, references: answer.pieces.map((p) => p.reference) }));
    pages.historyPieces.and.resolveTo(pageOf(answer.pieces));
    const api = jasmine.createSpyObj<MaintenanceApi>('MaintenanceApi', ['jobs']);
    api.jobs.and.resolveTo([
      summary({ productId: 'p9', productSku: 'BET-350', productNameFr: 'Bétonnière', kind: 'Internal' }),
      summary({ id: 'c', productId: 'p8', productSku: 'CUST-1', kind: 'Customer' }),
    ]);
    TestBed.configureTestingModule({
      imports: [MaintenanceHistoryComponent],
      providers: [{ provide: MaintenancePagesApi, useValue: pages }, { provide: MaintenanceApi, useValue: api }],
    });
    signIn(['maintenance.view', 'maintenance.costs.view']);
    fixture = TestBed.createComponent(MaintenanceHistoryComponent);
    fixture.componentRef.setInput('product', product);
    fixture.componentRef.setInput('reference', reference);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('lists every job on the piece with its dates, parts, hours and cost, and says it is tracked per piece in words', async () => {
    const page = await render(PIECE);
    const i18n = TestBed.inject(I18nService);
    expect(pages.historyJobs).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, productId: 'p9', reference: 'BT-07' }));
    // One piece asked: its pieces list is not read.
    expect(pages.historyPieces).not.toHaveBeenCalled();
    expect(page.textContent).toContain(t('maint.history.tracked', { value: i18n.money(900000), above: i18n.money(500000) }));
    const rows = page.querySelectorAll('tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('MJ-000001');
    expect(rows[0].textContent).toContain('Baguette');
    expect(rows[0].textContent).toContain(i18n.money(9900));
    expect(rows[1].textContent).toContain(t('maint.history.capitalised', { amount: i18n.money(180000) }));
    expect(page.textContent).toContain(t('maint.history.ofPiece', { reference: 'BT-07' }));
    // The jobs tile is the server's count over every job of the history.
    expect(page.querySelector('.ui-metric--lead .ui-metric__value')?.textContent?.trim()).toBe('2');
    // Only the company's equipment is offered: a customer job's product is not.
    // (The picker sits in the page toolbar, drawn by the shell.)
    expect(fixture.componentInstance.products().map((p) => p.sku)).toEqual(['BET-350']);
  });

  it('withholds the cost in words, offers the pieces of a type, and moves the address to a piece', async () => {
    const type: MaintenanceRepairHistory = {
      ...PIECE, mode: 'PerTypeWithReference', trackedPerPiece: false, reference: null, cost: null, capitalised: null, totalsWithheld: true,
      jobs: [historyJob({ cost: null, costWithheld: true }), historyJob({ id: 'j3', number: 'MJ-000005', itemReference: 'BT-08' })],
      pieces: [{ reference: 'BT-07', jobs: 1, lastAtUtc: '2026-10-02T08:00:00Z' }, { reference: 'BT-08', jobs: 1, lastAtUtc: '2026-10-02T08:00:00Z' }],
    };
    const page = await render(type, 'p9', null);
    expect(pages.historyPieces).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, productId: 'p9', reference: null }));
    expect(page.textContent).toContain(t('maint.history.perType'));
    expect(page.textContent).toContain(t('maint.cost.notShown'));
    const picked: { product: string | null; reference: string | null }[] = [];
    fixture.componentInstance.pick.subscribe((p) => picked.push(p));
    const piece = Array.from(page.querySelectorAll('button.ui-link')).find((b) => b.textContent?.includes('BT-08')) as HTMLButtonElement;
    piece.click();
    expect(picked).toEqual([{ product: 'p9', reference: 'BT-08' }]);
  });

  it('asks for a type first and reads nothing until one is chosen', async () => {
    const page = await render(PIECE, null, null);
    expect(pages.historyJobs).not.toHaveBeenCalled();
    expect(pages.historyPieces).not.toHaveBeenCalled();
    expect(page.textContent).toContain(t('maint.history.pickFirst'));
  });
});
