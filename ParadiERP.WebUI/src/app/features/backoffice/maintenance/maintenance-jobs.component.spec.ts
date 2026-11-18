import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MaintenanceApi, MaintenanceJobSummary } from './maintenance-api.service';
import { MaintenanceJobsComponent } from './maintenance-jobs.component';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { afterFilter, job, pageOf, signIn, summary, t } from './maintenance-test-data';

describe('MaintenanceJobsComponent (maintenance, Phase M1)', () => {
  let fixture: ComponentFixture<MaintenanceJobsComponent>;
  let api: jasmine.SpyObj<MaintenanceApi>;
  let pages: jasmine.SpyObj<MaintenancePagesApi>;

  async function render(jobs: MaintenanceJobSummary[], keys: string[]): Promise<HTMLElement> {
    api = jasmine.createSpyObj<MaintenanceApi>('MaintenanceApi', ['cancel']);
    api.cancel.and.resolveTo(job({ status: 'Cancelled' }));
    pages = jasmine.createSpyObj<MaintenancePagesApi>('MaintenancePagesApi', ['jobs']);
    pages.jobs.and.resolveTo(pageOf(jobs, { branches: [{ id: 'b1', name: 'Alger-Centre' }] }));
    TestBed.configureTestingModule({
      imports: [MaintenanceJobsComponent],
      providers: [{ provide: MaintenanceApi, useValue: api }, { provide: MaintenancePagesApi, useValue: pages }],
    });
    signIn(keys);
    fixture = TestBed.createComponent(MaintenanceJobsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('lists each job as a card row: its number opens it, its status said in words, what became of its units', async () => {
    const page = await render(
      [summary(), summary({ id: 'j2', number: 'MJ-000002', kind: 'Internal', status: 'Completed', repairedQty: 6, scrappedQty: 2, returnNoteNumber: null })],
      ['maintenance.view'],
    );
    expect(page.querySelector('table.ui-table.ui-table--cards')).not.toBeNull();
    const links = Array.from(page.querySelectorAll('button.ui-link')).map((b) => b.textContent?.trim());
    expect(links).toEqual(['MJ-000001', 'MJ-000002']);
    const pills = Array.from(page.querySelectorAll('app-status')).map((s) => s.textContent?.trim());
    expect(pills).toEqual([t('maint.status.Open'), t('maint.status.Completed')]);
    expect(page.textContent).toContain(t('maint.kind.Recharged'));
    expect(page.textContent).toContain('RET-000001');
    expect(page.textContent).toContain(t('maint.jobs.outcome', { repaired: '6', scrapped: '2' }));
    // Every cell says what it is when the row becomes a card.
    expect(page.querySelectorAll('td[data-label]').length).toBeGreaterThan(0);
    let opened = '';
    fixture.componentInstance.open.subscribe((id: string) => (opened = id));
    (page.querySelector('button.ui-link') as HTMLButtonElement).click();
    expect(opened).toBe('j1');
  });

  it('offers the first job when there is none, to whoever may open one, and nothing to a reader', async () => {
    let page = await render([], ['maintenance.view', 'maintenance.jobs.manage']);
    expect(page.querySelector('app-table-state')).not.toBeNull();
    expect(page.textContent).toContain(t('maint.jobs.none'));
    expect(Array.from(page.querySelectorAll('button')).some((b) => b.textContent?.trim() === t('maint.jobs.new'))).toBeTrue();

    TestBed.resetTestingModule();
    page = await render([], ['maintenance.view']);
    expect(Array.from(page.querySelectorAll('button')).some((b) => b.textContent?.trim() === t('maint.jobs.new'))).toBeFalse();
  });

  it('asks the server for a page with the status, kind and words, and says when nothing matches (owner, 2026-11-17)', async () => {
    await render([summary(), summary({ id: 'j2', number: 'MJ-000002', kind: 'Internal', status: 'Draft' })], ['maintenance.view']);
    expect(pages.jobs).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, q: '', status: null, kind: null }));
    const c = fixture.componentInstance;
    c.status.set('Draft');
    c.kind.set('Recharged');
    c.query.set('nothing-like-it');
    pages.jobs.and.resolveTo(pageOf([], { branches: [] }));
    await afterFilter(fixture);
    expect(pages.jobs).toHaveBeenCalledWith(
      jasmine.objectContaining({ skip: 0, take: 10, q: 'nothing-like-it', status: 'Draft', kind: 'Recharged' }),
    );
    const state = (fixture.nativeElement as HTMLElement).querySelector('app-table-state');
    expect(state).not.toBeNull();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain(t('maint.jobs.none'));
    c.clear();
    expect(c.filtered()).toBeFalse();
  });

  it('keeps Cancel last in the ⋯ menu, after a separator and marked as dangerous, and asks why before cancelling', async () => {
    const page = await render([summary()], ['maintenance.view', 'maintenance.jobs.close']);
    const c = fixture.componentInstance;
    const items = Array.from(page.querySelectorAll('app-menu .app-menu-item, app-menu .app-menu-separator')) as HTMLElement[];
    const order = items.map((el) => (el.getAttribute('role') === 'separator' ? '—' : el.textContent?.trim()));
    expect(order).toEqual([t('maint.jobs.openJob'), t('maint.job.complete'), '—', t('maint.job.cancel')]);
    expect(items[items.length - 1].classList).toContain('is-danger');
    c.askCancel(summary());
    fixture.detectChanges();
    expect(c.cancelling()?.id).toBe('j1');
    c.cancelReason = 'Ouvert par erreur';
    await c.cancel();
    expect(api.cancel).toHaveBeenCalledWith('j1', 'Ouvert par erreur');
    // The page on screen is asked for again.
    expect(pages.jobs).toHaveBeenCalledTimes(2);
    expect(c.cancelling()).toBeNull();
  });
});
