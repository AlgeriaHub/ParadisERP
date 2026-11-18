import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MaintenanceApi, MaintenanceJob, MaintenanceJobWork, MaintenanceWorkOptions } from './maintenance-api.service';
import { MaintenanceJobPartsComponent } from './maintenance-job-parts.component';
import { job, signIn, t, workWithParts } from './maintenance-test-data';

const OPTIONS: MaintenanceWorkOptions = {
  products: [{ id: 'rod', sku: 'ROD-25', barcode: null, nameAr: 'قضيب', nameFr: 'Baguette', nameEn: 'Rod', available: 28 }],
  vouchers: [
    { id: 'v2', number: 'PV-000802', status: 'Paid', expenseCategory: 'repair', payeeName: 'Atelier Kaci', purpose: null,
      amount: 5000, linked: 2000, free: 3000, createdAtUtc: '2026-11-08T09:00:00Z' },
  ],
};

describe('MaintenanceJobPartsComponent (maintenance, Phase M2)', () => {
  let fixture: ComponentFixture<MaintenanceJobPartsComponent>;
  let api: jasmine.SpyObj<MaintenanceApi>;

  async function render(j: MaintenanceJob, keys: string[], w: MaintenanceJobWork = workWithParts()): Promise<HTMLElement> {
    api = jasmine.createSpyObj<MaintenanceApi>('MaintenanceApi', ['work', 'workOptions', 'moveParts', 'addExpense']);
    api.work.and.resolveTo(w);
    api.workOptions.and.resolveTo(OPTIONS);
    api.moveParts.and.resolveTo(w);
    api.addExpense.and.resolveTo(w);
    TestBed.configureTestingModule({ imports: [MaintenanceJobPartsComponent], providers: [{ provide: MaintenanceApi, useValue: api }] });
    signIn(keys);
    fixture = TestBed.createComponent(MaintenanceJobPartsComponent);
    fixture.componentRef.setInput('job', j);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('says each part where it stands in words, folds the done ones and says nothing posts at issue', async () => {
    const page = await render(job(), ['maintenance.view']);
    const c = fixture.componentInstance;
    expect(page.textContent).toContain(t('maint.parts.postingNote'));
    const [rod, paint] = c.work()!.parts;
    expect(c.lineState(rod)).toBe(t('maint.parts.state.atJob'));
    expect(c.lineState(paint)).toBe(t('maint.parts.state.returned'));
    // A product still at the job shows its documents; one returned in full folds to its summary until opened.
    expect(c.folded(rod)).toBeFalse();
    expect(c.folded(paint)).toBeTrue();
    expect(page.textContent).toContain('MJR-000001');
    expect(page.textContent).not.toContain('MJR-000002');
    c.toggle(paint);
    fixture.detectChanges();
    expect(page.textContent).toContain('MJR-000002');
    // Outside work with its voucher's number and status in words; a reader has no form and is offered nothing.
    expect(page.textContent).toContain('PV-000801');
    expect(page.textContent).toContain(t('maint.voucherStatus.Paid'));
    expect(page.querySelector('#mjp-product')).toBeNull();
    expect(page.querySelector('#mjp-exp-desc')).toBeNull();
    expect(api.workOptions).not.toHaveBeenCalled();
  });

  it('issues no more than the branch has free, and a retried issue keeps its request id', async () => {
    await render(job(), ['maintenance.view', 'maintenance.jobs.manage']);
    const c = fixture.componentInstance;
    expect(api.workOptions).toHaveBeenCalledWith('j1');
    c.issue = { productId: 'rod', qty: 29 };
    expect(c.issuable()).toBeFalse();
    c.issue = { productId: 'rod', qty: 3 };
    expect(c.issuable()).toBeTrue();

    api.moveParts.and.rejectWith(new Error('network'));
    await c.give();
    const first = api.moveParts.calls.mostRecent().args;
    expect(first.slice(0, 3)).toEqual(['j1', 'Issue', [{ productId: 'rod', qty: 3 }]]);
    api.moveParts.and.resolveTo(workWithParts());
    await c.give();
    expect(api.moveParts.calls.mostRecent().args[3]).toBe(first[3]);
    expect(c.issue.productId).toBeNull();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain(t('maint.parts.issued'));
  });

  it('returns what is at the job, never more', async () => {
    await render(job(), ['maintenance.jobs.manage']);
    const c = fixture.componentInstance;
    const rod = c.work()!.parts[0];
    c.returning['rod'] = 18;
    expect(c.returnable(rod)).toBeFalse();
    c.returning['rod'] = 17;
    await c.giveBack(rod);
    expect(api.moveParts).toHaveBeenCalledWith('j1', 'Return', [{ productId: 'rod', qty: 17 }], jasmine.any(String));
  });

  it('records outside work against a voucher only up to what is left of it', async () => {
    const page = await render(job(), ['maintenance.jobs.manage']);
    const c = fixture.componentInstance;
    const voucher = page.querySelector('#mjp-exp-voucher') as HTMLSelectElement;
    expect(voucher.textContent).toContain('PV-000802');
    c.expense = { description: 'Soudeur', amount: 3500, voucherId: 'v2' };
    expect(c.voucherShort()).toBeTrue();
    expect(c.expenseReady()).toBeFalse();
    c.expense = { description: 'Soudeur', amount: 3000, voucherId: 'v2' };
    expect(c.expenseReady()).toBeTrue();
    await c.record();
    expect(api.addExpense).toHaveBeenCalledWith('j1', 'Soudeur', 3000, 'v2', jasmine.any(String));
    expect(c.expense.description).toBe('');
  });

  it('on a completed job, offers no parts movement, says the parts were used, and still takes outside work', async () => {
    const page = await render(job({ status: 'Completed' }), ['maintenance.jobs.manage']);
    const c = fixture.componentInstance;
    expect(c.lineState(c.work()!.parts[0])).toBe(t('maint.parts.state.used'));
    expect(c.folded(c.work()!.parts[0])).toBeTrue();
    expect(page.querySelector('#mjp-product')).toBeNull();
    expect(page.querySelector('#mjp-exp-desc')).not.toBeNull();
  });

  it('tells the job page what is still at the job each time it reads the parts', async () => {
    const seen: number[] = [];
    await render(job(), ['maintenance.view']);
    fixture.componentInstance.changed.subscribe((w) => seen.push(w.partsAtJobQty));
    fixture.componentRef.setInput('job', job({ diagnosis: 'Soudé' }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(seen).toEqual([17]);
  });
});
