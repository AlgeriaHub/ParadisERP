import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { AdminService } from '../../../core/admin.service';
import { BranchLock } from '../../../core/branch-lock';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { PayrollAdvancesComponent } from './advances.component';
import { PayrollPagesApi } from './payroll-pages.api';
import { WageMoneyApi } from './wage-money-api.service';

/** Granting an advance (owner, 2026-11-09): the branch first, then its employees and only its cash boxes. */
describe('PayrollAdvancesComponent — the branch first', () => {
  const branches = [
    { id: 'b1', isActive: true, nameFr: 'Alger', nameAr: 'الجزائر', nameEn: 'Algiers' },
    { id: 'b2', isActive: true, nameFr: 'Oran', nameAr: 'وهران', nameEn: 'Oran' },
  ];
  const boxes = [
    { id: 'x1', branchId: 'b1', isMain: false, balance: 1000, nameFr: 'Caisse Alger', nameAr: 'صندوق الجزائر', nameEn: 'Algiers box' },
    { id: 'x2', branchId: 'b2', isMain: false, balance: 1000, nameFr: 'Caisse Oran', nameAr: 'صندوق وهران', nameEn: 'Oran box' },
  ];

  async function render(lockedBranchId: string | null) {
    const money = jasmine.createSpyObj<WageMoneyApi>('WageMoneyApi', ['employees', 'grantAdvance']);
    money.employees.and.callFake(async (f) => ({
      rows: [{ id: 'e-' + f.branchId, number: 'Emp-1', fullName: 'Worker', branchId: f.branchId!, branchName: '', payoutMethod: 'Cash', openAdvances: 0 }],
    }) as never);
    // The register and "who owes" are paged by the server (owner, 2026-11-17).
    const pages = jasmine.createSpyObj<PayrollPagesApi>('PayrollPagesApi', ['advancesPage', 'owingPage']);
    pages.advancesPage.and.callFake(async (q) => ({ items: [], total: 0, skip: 0, take: q.take }));
    pages.owingPage.and.callFake(async (q) => ({ items: [], total: 0, skip: 0, take: q.take, summary: { outstandingTotal: 0, peopleOwing: 0 } }));
    const admin = jasmine.createSpyObj<AdminService>('AdminService', ['listBranches', 'listCashBoxes']);
    admin.listBranches.and.resolveTo(branches as never);
    admin.listCashBoxes.and.resolveTo(boxes as never);
    const lock = {
      locked: signal(lockedBranchId !== null),
      lockedBranchId: signal(lockedBranchId),
      scopeRows: <T extends { branchId: string }>(rows: T[]) => (lockedBranchId ? rows.filter((r) => r.branchId === lockedBranchId) : rows),
      ownBranchName: () => 'Alger',
    };
    TestBed.configureTestingModule({
      imports: [PayrollAdvancesComponent],
      providers: [
        { provide: WageMoneyApi, useValue: money },
        { provide: PayrollPagesApi, useValue: pages },
        { provide: AdminService, useValue: admin },
        { provide: BranchLock, useValue: lock },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    const fixture = TestBed.createComponent(PayrollAdvancesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, c: fixture.componentInstance, money, pages, el: fixture.nativeElement as HTMLElement };
  }

  it('lets the admin choose the branch, then offers its employees and only its boxes', async () => {
    const { c, money, el } = await render(null);
    expect(el.querySelector('#adv-grant-branch')).not.toBeNull();
    expect(c.employees()).toEqual([]);
    expect(money.employees).not.toHaveBeenCalled();

    await c.setGrantBranch('b2');
    expect(money.employees).toHaveBeenCalledWith(jasmine.objectContaining({ branchId: 'b2' }));
    expect(c.branchBoxes().map((b) => b.id)).toEqual(['x2']);
    c.pickEmployee('e-b2');
    expect(c.form.cashBoxId).toBe('x2');
  });

  it('holds a branch manager to their own branch: no branch choice, their employees and boxes', async () => {
    const { c, money, el } = await render('b1');
    expect(el.querySelector('#adv-grant-branch')).toBeNull();
    expect(money.employees).toHaveBeenCalledWith(jasmine.objectContaining({ branchId: 'b1' }));
    expect(c.branchBoxes().map((b) => b.id)).toEqual(['x1']);
  });

  it('asks the server for ten advances and the people owing, at the branch, and shows its totals', async () => {
    const { c, pages } = await render('b1');
    expect(pages.advancesPage).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, branchId: 'b1', method: 'all' }));
    expect(pages.owingPage).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, branchId: 'b1' }));
    expect(c.outstandingTotal()).toBe(0);
    expect(c.peopleOwing()).toBe(0);
  });
});
