import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MaintenanceApi } from './maintenance-api.service';
import { MaintenanceCustomerApi, MaintenanceNewJobOptions } from './maintenance-customer-api.service';
import { MaintenanceJobNewComponent } from './maintenance-job-new.component';
import { WAITING, job, signIn, t } from './maintenance-test-data';

describe('MaintenanceJobNewComponent (maintenance, Phase M1)', () => {
  let fixture: ComponentFixture<MaintenanceJobNewComponent>;
  let api: jasmine.SpyObj<MaintenanceApi>;
  let customers: jasmine.SpyObj<MaintenanceCustomerApi>;

  async function render(inputs: { line?: string; product?: string; branch?: string } = {},
    options: MaintenanceNewJobOptions = { customerJobs: false, customers: [], products: [] }): Promise<HTMLElement> {
    api = jasmine.createSpyObj<MaintenanceApi>('MaintenanceApi', ['waiting', 'create', 'reworkCandidates']);
    api.waiting.and.resolveTo(WAITING);
    api.reworkCandidates.and.resolveTo([]);
    api.create.and.resolveTo(job({ id: 'new-job' }));
    customers = jasmine.createSpyObj<MaintenanceCustomerApi>('MaintenanceCustomerApi', ['newJobOptions']);
    customers.newJobOptions.and.resolveTo(options);
    TestBed.configureTestingModule({
      imports: [MaintenanceJobNewComponent],
      providers: [{ provide: MaintenanceApi, useValue: api }, { provide: MaintenanceCustomerApi, useValue: customers }],
    });
    signIn(['maintenance.view', 'maintenance.jobs.manage']);
    fixture = TestBed.createComponent(MaintenanceJobNewComponent);
    if (inputs.line) fixture.componentRef.setInput('line', inputs.line);
    if (inputs.product) fixture.componentRef.setInput('product', inputs.product);
    if (inputs.branch) fixture.componentRef.setInput('branch', inputs.branch);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('asks the kind first, labels every field above it and says which are required in words', async () => {
    const page = await render();
    const labels = Array.from(page.querySelectorAll('.bo-field > label')).map((l) => l.getAttribute('for'));
    expect(labels[0]).toBe('mn-kind');
    expect(page.querySelectorAll('.bo-req').length).toBeGreaterThan(0);
    expect(page.textContent).toContain(t('common.required'));
    // A customer job is offered only while MS-06 is on (Phase M5a).
    const kinds = Array.from((page.querySelector('#mn-kind') as HTMLSelectElement).options).map((o) => o.textContent?.trim());
    expect(kinds).toEqual([t('maint.kind.Internal'), t('maint.kind.Recharged')]);
  });

  it('opens an internal job on the free units of a product in maintenance, never more', async () => {
    await render({ product: 'p1', branch: 'b1' });
    const c = fixture.componentInstance;
    expect(c.kind()).toBe('Internal');
    // The coupler has nothing free: it is not offered.
    expect(c.buckets().map((b) => b.sku)).toEqual(['ECH-01']);
    expect(c.qtyValue).toBe(8);
    c.qtyValue = 9;
    expect(c.ready()).toBeFalse();
    c.qtyValue = 3;
    c.itemReference = ' SN-1 ';
    let opened = '';
    c.opened.subscribe((id: string) => (opened = id));
    await c.submit();
    expect(api.create).toHaveBeenCalledWith(jasmine.objectContaining({
      branchId: 'b1', kind: 'Internal', productId: 'p1', qty: 3, returnNoteLineId: null, itemReference: 'SN-1',
    }));
    expect(opened).toBe('new-job');
  });

  it('opens a recharged job from a return-note line, prefilled from the waiting list', async () => {
    const page = await render({ line: 'l1' });
    const c = fixture.componentInstance;
    expect(c.kind()).toBe('Recharged');
    expect(c.qtyValue).toBe(8);
    expect(page.textContent).toContain('RET-000001');
    await c.submit();
    expect(api.create).toHaveBeenCalledWith(jasmine.objectContaining({ kind: 'Recharged', returnNoteLineId: 'l1', productId: 'p1', qty: 8 }));
  });

  it('opens a customer job (Phase M5a) on a customer and the equipment in words, taking nothing from stock', async () => {
    const page = await render({}, {
      customerJobs: true,
      customers: [{ id: 'cu1', code: 'C001', fullName: 'Ben Ali BTP' }],
      products: [{ id: 'p9', sku: 'BET-350', nameAr: 'خلاطة', nameFr: 'Bétonnière', nameEn: 'Mixer' }],
    });
    const kinds = Array.from((page.querySelector('#mn-kind') as HTMLSelectElement).options).map((o) => o.textContent?.trim());
    expect(kinds).toContain(t('maint.kind.Customer'));
    const c = fixture.componentInstance;
    c.setKind('Customer');
    fixture.detectChanges();
    expect(page.querySelector('#mn-customer')).not.toBeNull();
    expect(page.querySelector('#mn-equipment')).not.toBeNull();
    expect(page.textContent).toContain(t('maint.kind.Customer.help'));
    expect(c.qtyValue).toBe(1);
    // Neither customer nor equipment yet: not ready.
    expect(c.ready()).toBeFalse();
    c.customerId.set('cu1');
    c.equipment = ' Bétonnière 350 L ';
    expect(c.ready()).toBeTrue();
    await c.submit();
    expect(api.create).toHaveBeenCalledWith(jasmine.objectContaining({
      kind: 'Customer', customerId: 'cu1', equipment: 'Bétonnière 350 L', productId: null, qty: 1, returnNoteLineId: null, reworkOfJobId: null,
    }));
  });

  it('offers a free rework (Phase M7c) of the customer’s completed jobs whose guarantee runs, and takes their equipment', async () => {
    const page = await render({}, { customerJobs: true, customers: [{ id: 'cu1', code: 'C001', fullName: 'Ben Ali BTP' }], products: [] });
    api.reworkCandidates.and.resolveTo([{
      id: 'orig', number: 'MJ-000004', status: 'Invoiced', equipment: 'Bétonnière 350 L', itemReference: null, productId: null,
      completedAtUtc: '2026-11-01T09:00:00Z', guaranteeUntil: '2027-02-01',
    }]);
    const c = fixture.componentInstance;
    c.setKind('Customer');
    fixture.detectChanges();
    // No customer yet: nothing to rework.
    expect(page.querySelector('#mn-rework')).toBeNull();
    await c.setCustomer('cu1');
    fixture.detectChanges();
    expect(api.reworkCandidates.calls.mostRecent().args[0]).toBe('cu1');
    const select = page.querySelector('#mn-rework') as HTMLSelectElement;
    expect(select).not.toBeNull();
    expect(select.options[0].textContent?.trim()).toBe(t('maint.rework.none'));
    expect(select.options[1].textContent).toContain('MJ-000004');
    expect(select.options[1].textContent).toContain('01/02/2027');
    c.setRework('orig');
    fixture.detectChanges();
    expect(c.equipment).toBe('Bétonnière 350 L');
    expect(page.textContent).toContain(t('maint.rework.freeHint'));
    await c.submit();
    expect(api.create).toHaveBeenCalledWith(jasmine.objectContaining({ kind: 'Customer', customerId: 'cu1', reworkOfJobId: 'orig' }));
    // Another customer: the choice is dropped.
    await c.setCustomer(null);
    expect(c.reworkOfId()).toBeNull();
  });
});
