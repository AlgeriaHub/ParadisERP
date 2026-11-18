import { ComponentFixture, TestBed } from '@angular/core/testing';
import { I18nService } from '../../../core/i18n.service';
import { MaintenanceCostApi, MaintenanceJobCost } from './maintenance-cost-api.service';
import { MaintenanceJobCostComponent } from './maintenance-job-cost.component';
import { job, signIn, t } from './maintenance-test-data';

/** MNT-04's job as the server answers it to a reader with pay-rate access: 3,200 + 6 h × 450 + 4,000 = 9,900. */
function cost(over: Partial<MaintenanceJobCost> = {}): MaintenanceJobCost {
  return {
    jobId: 'j1', number: 'MJ-000001', kind: 'Recharged', jobStatus: 'Open', status: 'provisional', policyApproved: false,
    labourMoneyShown: true, costIncludes: 'PartsLabourOutsideWork', labourRate: 'PayrollDerived', burdenAndVat: 'FollowManufacturing',
    overheadPerLabourHour: 0, workshopRatePerHour: null, employerBurdenPercent: 0, expenseVatPercent: 0,
    parts: 3200, labour: 2700, overhead: 0, outsideWork: 4000, total: 9900, labourHours: 6, recharge: 16000, rechargeLessCost: 6100,
    statuses: { parts: 'reference_cost', labour: 'approved', overhead: 'excluded', outsideWork: 'provisional' },
    partLines: [],
    labourLines: [
      { employeeId: 'e1', employeeNumber: 'E-001', employeeName: 'Karim Benali', year: 2026, month: 10, hours: 6, overtimeHours: 0,
        rateKind: 'Actual', reason: null, rate: 450, cost: 2700, withoutEmployerSocial: false },
    ],
    outsideWorkLines: [{ id: 'x1', description: 'Peinture', recorded: 4000, counted: 4000, settled: false, voucherNumber: null }],
    notes: [{ code: 'expense_unsettled', value: 4000 }, { code: 'costing_policy_not_approved', value: null }],
    ...over,
  };
}

describe('MaintenanceJobCostComponent (maintenance, Phase M4)', () => {
  let fixture: ComponentFixture<MaintenanceJobCostComponent>;
  let api: jasmine.SpyObj<MaintenanceCostApi>;

  async function render(answer: MaintenanceJobCost): Promise<HTMLElement> {
    api = jasmine.createSpyObj<MaintenanceCostApi>('MaintenanceCostApi', ['jobCost', 'recharges', 'products']);
    api.jobCost.and.resolveTo(answer);
    TestBed.configureTestingModule({
      imports: [MaintenanceJobCostComponent],
      providers: [{ provide: MaintenanceCostApi, useValue: api }],
    });
    signIn(['maintenance.view', 'maintenance.costs.view']);
    fixture = TestBed.createComponent(MaintenanceJobCostComponent);
    fixture.componentRef.setInput('job', job());
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('says the cost is provisional in words, shows every element, the labour lines and the notes', async () => {
    const page = await render(cost());
    const money = (n: number) => TestBed.inject(I18nService).money(n);
    expect(api.jobCost).toHaveBeenCalledWith('j1');
    expect(page.querySelector('app-status')?.textContent).toContain(t('maint.cost.status.provisional'));
    const lead = page.querySelector('.ui-metric--lead')!;
    expect(lead.textContent).toContain(t('maint.cost.total.provisional'));
    expect(lead.textContent).toContain(money(9900));
    expect(page.textContent).toContain(money(2700));
    expect(page.textContent).toContain(t('maint.cost.rechargeLessCost', { amount: money(6100) }));
    // Overhead is out of the cost under MS-10's default: its element says so, and no overhead tile is drawn.
    expect(page.textContent).toContain(t('maint.cost.elementStatus.excluded'));
    expect(page.textContent).not.toContain(t('maint.cost.overheadRate', { rate: money(0) }));
    expect(page.textContent).toContain('Karim Benali');
    expect(page.textContent).toContain(t('maint.cost.rateKind.Actual'));
    expect(page.textContent).toContain(t('mfg.cost.note.expense_unsettled', { value: money(4000) }));
    expect(page.textContent).toContain(t('maint.cost.note.costing_policy_not_approved'));
    expect(page.textContent).toContain(t('maint.cost.basis.unconfirmed'));
  });

  it('without pay-rate access says the labour money and the total are not shown, never as a zero', async () => {
    const page = await render(cost({
      labourMoneyShown: false, labour: null, total: null, rechargeLessCost: null,
      labourLines: [{ ...cost().labourLines[0], rate: null, cost: null }],
    }));
    expect(page.textContent).toContain(t('maint.cost.withheld'));
    expect(page.querySelector('.ui-metric--lead')?.textContent).toContain(t('maint.cost.notShown'));
    // The hours stay, the rate and amount columns go.
    expect(page.textContent).toContain(t('maint.cost.hours', { n: fixture.componentInstance.qty(6) }));
    expect(page.textContent).not.toContain(t('maint.cost.rate'));
    expect(page.textContent).not.toContain(t('maint.cost.rechargeLessCost', { amount: '' }).trim());
  });

  it('reads a final cost at the workshop rate in words', async () => {
    const page = await render(cost({
      status: 'approved', policyApproved: true, labourRate: 'WorkshopRate', workshopRatePerHour: 500, labour: 3000, total: 10200,
      statuses: { parts: 'none', labour: 'approved', overhead: 'excluded', outsideWork: 'recorded' }, notes: [],
      labourLines: [{ ...cost().labourLines[0], rateKind: 'Workshop', rate: 500, cost: 3000 }],
    }));
    const money = (n: number) => TestBed.inject(I18nService).money(n);
    expect(page.querySelector('app-status')?.textContent).toContain(t('maint.cost.status.approved'));
    expect(page.textContent).toContain(t('maint.cost.basis.workshop', { rate: money(500) }));
    expect(page.textContent).toContain(t('maint.cost.basis.confirmed'));
    expect(page.textContent).toContain(t('maint.cost.rateKind.Workshop'));
  });

  it('says in words that a repair was capitalised into the equipment register at completion (Phase M7d, MS-30)', async () => {
    const page = await render(cost({ capitalisedAtUtc: '2026-11-09T15:00:00Z', capitalised: 9900 }));
    const i18n = TestBed.inject(I18nService);
    expect(page.textContent).toContain(t('maint.cost.capitalised', { amount: i18n.money(9900), date: i18n.date('2026-11-09T15:00:00Z') }));
    // Withheld with the total: the fact stays, the amount does not.
    TestBed.resetTestingModule();
    const hidden = await render(cost({ capitalisedAtUtc: '2026-11-09T15:00:00Z', capitalised: null, total: null, labour: null, labourMoneyShown: false }));
    expect(hidden.textContent).toContain(t('maint.cost.capitalisedWithheld', { date: TestBed.inject(I18nService).date('2026-11-09T15:00:00Z') }));
  });
});
