import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MaintenanceBucket, MaintenanceWaiting } from './maintenance-api.service';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { MaintenanceWaitingComponent } from './maintenance-waiting.component';
import { WAITING, afterFilter, pageOf, signIn, t } from './maintenance-test-data';

describe('MaintenanceWaitingComponent (maintenance, Phase M1)', () => {
  let fixture: ComponentFixture<MaintenanceWaitingComponent>;
  let pages: jasmine.SpyObj<MaintenancePagesApi>;
  const branches = { branches: [{ id: 'b1', name: 'Alger-Centre' }] };

  async function render(keys: string[], waiting: MaintenanceWaiting = WAITING): Promise<HTMLElement> {
    pages = jasmine.createSpyObj<MaintenancePagesApi>('MaintenancePagesApi', ['waitingLines', 'waitingBuckets']);
    pages.waitingLines.and.resolveTo(pageOf(waiting.lines, branches));
    pages.waitingBuckets.and.resolveTo(pageOf(waiting.buckets, branches));
    TestBed.configureTestingModule({
      imports: [MaintenanceWaitingComponent],
      providers: [{ provide: MaintenancePagesApi, useValue: pages }],
    });
    signIn(keys);
    fixture = TestBed.createComponent(MaintenanceWaitingComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('lists the return lines not yet covered and the units in maintenance, each with its open job action', async () => {
    const page = await render(['maintenance.view', 'maintenance.jobs.manage']);
    expect(page.querySelectorAll('table.ui-table--cards').length).toBe(2);
    expect(page.textContent).toContain('RET-000001');
    expect(page.textContent).toContain('CTR-0001');
    expect(page.textContent).toContain('Ben Ali BTP');
    let line = '';
    let bucket: MaintenanceBucket | null = null;
    fixture.componentInstance.openLine.subscribe((id: string) => (line = id));
    fixture.componentInstance.openBucket.subscribe((b: MaintenanceBucket) => (bucket = b));
    (page.querySelector('button.ui-link') as HTMLButtonElement).click();
    expect(line).toBe('l1');
    const actions = Array.from(page.querySelectorAll('app-menu .app-menu-item')) as HTMLButtonElement[];
    // One per waiting line and one per product with free units: nothing to open on the coupler, all held.
    expect(actions.map((a) => a.textContent?.trim())).toEqual([t('maint.waiting.openJob'), t('maint.waiting.openJob')]);
    actions[1].click();
    expect(bucket!.productId).toBe('p1');
  });

  it('offers nothing to open to a reader, and says when nothing waits', async () => {
    let page = await render(['maintenance.view']);
    expect(page.querySelectorAll('app-menu').length).toBe(0);
    expect(page.querySelector('button.ui-link')).toBeNull();

    TestBed.resetTestingModule();
    page = await render(['maintenance.view'], { buckets: [], lines: [] });
    expect(page.textContent).toContain(t('maint.waiting.noLines'));
    expect(page.textContent).toContain(t('maint.waiting.noBuckets'));
  });

  it('sends each list its own filters, and the branch to both (owner, 2026-11-17)', async () => {
    await render(['maintenance.view']);
    expect(pages.waitingLines).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, recharge: 'all' }));
    expect(pages.waitingBuckets).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, free: 'all' }));
    const c = fixture.componentInstance;
    c.branch.set('b1');
    c.recharge.set('yes');
    c.from.set('2026-11-01');
    c.free.set('some');
    await afterFilter(fixture);
    expect(pages.waitingLines).toHaveBeenCalledWith(
      jasmine.objectContaining({ skip: 0, take: 10, branch: 'b1', recharge: 'yes', from: '2026-11-01' }),
    );
    expect(pages.waitingBuckets).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, branch: 'b1', free: 'some' }));
  });
});
