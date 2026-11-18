import { ComponentFixture, TestBed } from '@angular/core/testing';
import { I18nService } from '../../../core/i18n.service';
import { provideRouter } from '@angular/router';
import { MaintenanceApi, MaintenanceJob } from './maintenance-api.service';
import { MaintenanceCustomerApi } from './maintenance-customer-api.service';
import { MaintenanceJobComponent } from './maintenance-job.component';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { job, signIn, t, work, workWithParts } from './maintenance-test-data';

describe('MaintenanceJobComponent (maintenance, Phase M1)', () => {
  let fixture: ComponentFixture<MaintenanceJobComponent>;
  let api: jasmine.SpyObj<MaintenanceApi>;

  async function render(j: MaintenanceJob, keys: string[], me = 'u-reader'): Promise<HTMLElement> {
    api = jasmine.createSpyObj<MaintenanceApi>('MaintenanceApi', ['job', 'open', 'complete', 'cancel', 'update', 'work', 'workOptions']);
    api.job.and.resolveTo(j);
    api.work.and.resolveTo(work());
    api.workOptions.and.resolveTo({ products: [], vouchers: [] });
    api.complete.and.callFake(async (_id, repaired, scrapped) => ({ ...j, status: 'Completed', repairedQty: repaired, scrappedQty: scrapped }));
    api.open.and.callFake(async () => ({ ...j, status: 'Open', openedBy: 'adjoint', openedAtUtc: '2026-11-09T09:00:00Z' }));
    TestBed.configureTestingModule({
      imports: [MaintenanceJobComponent],
      providers: [
        provideRouter([]),
        { provide: MaintenanceApi, useValue: api },
        // The hours card (Phase M3) has its own spec; here its server page is an empty job's.
        {
          provide: MaintenancePagesApi,
          useValue: {
            jobHours: async () => ({ items: [], total: 0, skip: 0, take: 10, summary: { jobId: j.id, jobStatus: j.status, totalHours: 0, today: '2026-11-09' } }),
          },
        },
        // A customer job's Price card (Phase M5a) has its own spec; here it stays loading.
        { provide: MaintenanceCustomerApi, useValue: { price: () => new Promise(() => undefined) } },
      ],
    });
    signIn(keys, me);
    fixture = TestBed.createComponent(MaintenanceJobComponent);
    fixture.componentRef.setInput('id', j.id);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const buttons = () => Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).map((b) => b.textContent?.trim());

  it('says the job, its return note, its recharge and its units in words, and its history', async () => {
    const page = await render(job(), ['maintenance.view']);
    expect(page.querySelector('app-status')?.textContent?.trim()).toBe(t('maint.status.Open'));
    expect(page.textContent).toContain('CTR-0001');
    expect(page.textContent).toContain('RET-000001');
    expect(page.textContent).toContain('Ben Ali BTP');
    expect(page.textContent).toContain(t('maint.job.inMaintenanceNote'));
    expect(page.textContent).toContain(t('maint.event.Opened'));
    // A reader has nothing to act on.
    expect(page.querySelector('.ui-actionbar--sticky')).toBeNull();
    expect(buttons()).not.toContain(t('maint.job.complete'));
  });

  it('completes with what became of every unit, proposing the rest as beyond repair', async () => {
    const page = await render(job(), ['maintenance.view', 'maintenance.jobs.close']);
    const c = fixture.componentInstance;
    expect(page.querySelector('.ui-actionbar--sticky')).not.toBeNull();
    expect(c.repaired).toBe(8);
    expect(c.balanced()).toBeTrue();
    c.repaired = 6;
    c.scrapped = c.remainder(6);
    expect(c.scrapped).toBe(2);
    c.scrapped = 1;
    expect(c.balanced()).toBeFalse();
    c.scrapped = 2;
    await c.complete();
    expect(api.complete).toHaveBeenCalledWith('j1', 6, 2, null);
    fixture.detectChanges();
    expect(c.job()?.status).toBe('Completed');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(t('maint.job.completedNotice'));
    expect(buttons()).not.toContain(t('maint.job.complete'));
  });

  it('keeps the opener from completing under four eyes (MS-05), in words', async () => {
    const page = await render(job({ jobCloser: 'NotTheOpener', openedByUserId: 'u-chef' }), ['maintenance.jobs.close'], 'u-chef');
    expect(fixture.componentInstance.ownJob()).toBeTrue();
    expect(page.textContent).toContain(t('maint.job.fourEyes'));
    const complete = Array.from(page.querySelectorAll('button')).find((b) => b.textContent?.trim() === t('maint.job.complete')) as HTMLButtonElement;
    expect(complete.disabled).toBeTrue();
  });

  it('offers a draft to be taken before it can be completed', async () => {
    await render(job({ status: 'Draft', openedByUserId: null, openedBy: null, openedAtUtc: null }), ['maintenance.jobs.manage', 'maintenance.jobs.close']);
    expect(buttons()).toContain(t('maint.job.take'));
    expect(buttons()).not.toContain(t('maint.job.complete'));
    await fixture.componentInstance.take();
    fixture.detectChanges();
    expect(api.open).toHaveBeenCalledWith('j1');
    expect(buttons()).toContain(t('maint.job.complete'));
  });

  it('shows a cancelled job with its reason and nothing left to do', async () => {
    const page = await render(
      job({ status: 'Cancelled', cancelledBy: 'chef', cancelledAtUtc: '2026-11-09T10:00:00Z', cancelReason: 'Sous-traité' }),
      ['maintenance.jobs.manage', 'maintenance.jobs.close'],
    );
    expect(page.textContent).toContain('Sous-traité');
    expect(page.querySelector('.ui-actionbar--sticky')).toBeNull();
    expect((page.querySelector('#mj-desc') as HTMLTextAreaElement).disabled).toBeTrue();
  });

  it('with parts still at the job, cancels only once they are said to stay as used, and completion says they were used (M2)', async () => {
    const page = await render(job(), ['maintenance.view', 'maintenance.jobs.close']);
    api.work.and.resolveTo(workWithParts());
    const c = fixture.componentInstance;
    c.partsAtJob.set(17);
    fixture.detectChanges();
    expect(page.textContent).toContain(t('maint.parts.usedAtCompletion', { n: '17' }));

    api.cancel.and.callFake(async () => job({ status: 'Cancelled' }));
    c.askCancel();
    c.cancelReason = 'Abandonné';
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const dialog = document.body;
    expect(dialog.textContent).toContain(t('maint.parts.atJobOnCancel', { n: '17' }));
    const cancel = Array.from(dialog.querySelectorAll('button.btn-danger')).pop() as HTMLButtonElement;
    expect(cancel.disabled).toBeTrue();
    c.partsUsed = true;
    await c.cancel();
    expect(api.cancel).toHaveBeenCalledWith('j1', 'Abandonné', true);
  });

  it('says a rework under guarantee (Phase M7c) is free, links the job it redoes and offers no price', async () => {
    const page = await render(job({
      kind: 'Customer', customerId: 'cu1', customerName: 'Ben Ali BTP', equipment: 'Bétonnière 350 L', productId: null,
      contractId: null, contractNumber: null, returnNoteId: null, returnNoteNumber: null, returnNoteLineId: null,
      lineDamagedQty: null, lineRecharge: null, reworkOfJobId: 'orig', reworkOfNumber: 'MJ-000004',
    }), ['maintenance.view', 'maintenance.pricing']);
    expect(page.textContent).toContain(t('maint.rework.of'));
    expect(page.textContent).toContain('MJ-000004');
    expect(page.textContent).toContain(TestBed.inject(I18nService).t('maint.rework.freeNotice', { number: 'MJ-000004' }));
    expect(page.querySelector('app-maintenance-job-price')).toBeNull();
  });

  it('says a customer job’s guarantee, the provision set aside at its invoice and its reworks (Phase M7c)', async () => {
    const page = await render(job({
      kind: 'Customer', status: 'Invoiced', customerId: 'cu1', customerName: 'Ben Ali BTP', equipment: 'Bétonnière 350 L', productId: null,
      contractId: null, contractNumber: null, returnNoteId: null, returnNoteNumber: null, returnNoteLineId: null,
      lineDamagedQty: null, lineRecharge: null, guaranteeUntil: '2027-02-09', provisionAmount: 2000,
      reworks: [{ id: 'rw', number: 'MJ-000007', status: 'Open', createdAtUtc: '2026-12-01T09:00:00Z' }],
    }), ['maintenance.view']);
    expect(page.textContent).toContain(t('maint.rework.guaranteeUntil'));
    expect(page.textContent).toContain('09/02/2027');
    expect(page.textContent).toContain(t('maint.rework.provision'));
    expect(page.textContent).toContain('MJ-000007');
    expect(page.querySelector('app-maintenance-job-price')).not.toBeNull();
  });
});
