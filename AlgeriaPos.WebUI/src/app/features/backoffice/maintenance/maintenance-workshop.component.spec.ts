import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ApiError } from '../../../core/api.service';
import { ConfirmService } from '../../../ui/confirm.service';
import { MaintenancePagesApi } from './maintenance-pages.api';
import { pageOf, signIn, t } from './maintenance-test-data';
import { MaintenanceWorkshop, MaintenanceWorkshopApi, MaintenanceWorkshopMember } from './maintenance-workshop-api.service';
import { MaintenanceWorkshopComponent } from './maintenance-workshop.component';

describe('MaintenanceWorkshopComponent (MS-04, the workshop list)', () => {
  let fixture: ComponentFixture<MaintenanceWorkshopComponent>;
  let api: jasmine.SpyObj<MaintenanceWorkshopApi>;
  let pages: jasmine.SpyObj<MaintenancePagesApi>;
  let confirm: jasmine.SpyObj<ConfirmService>;

  const member = (over: Partial<MaintenanceWorkshopMember>): MaintenanceWorkshopMember => ({
    id: 'm1', employeeId: 'e1', number: 'Emp-101', fullName: 'Karim Benali', branchId: 'b1', branchName: 'Alger-Centre',
    employeeBranchId: 'b1', employeeBranchName: 'Alger-Centre', employed: true, effective: true, addedBy: 'admin',
    addedAtUtc: '2026-11-10T08:00:00Z', ...over,
  });
  const LIST: MaintenanceWorkshop = {
    bookableEmployees: 'WorkshopListOnly',
    members: [
      member({}),
      member({ id: 'm2', employeeId: 'e2', fullName: 'Nadia Haddad', employeeBranchId: 'b2', employeeBranchName: 'Oran', effective: false }),
      member({ id: 'm3', employeeId: 'e3', fullName: 'Samir Parti', employed: false, effective: false }),
    ],
    available: [{ employeeId: 'e4', number: 'Emp-102', fullName: 'Yacine Saidi', branchId: 'b1', branchName: 'Alger-Centre' }],
    branches: [
      { branchId: 'b1', branchName: 'Alger-Centre', employees: 3, onList: 1 },
      { branchId: 'b2', branchName: 'Oran', employees: 2, onList: 0 },
    ],
  };

  /** The server's page of the list: every member as one page, the rest of the list's answer as its summary. */
  function serve(list: MaintenanceWorkshop): void {
    pages.workshop.and.resolveTo(pageOf(list.members, {
      bookableEmployees: list.bookableEmployees, available: list.available, branches: list.branches,
      memberBranches: [...new Map(list.members.map((m) => [m.branchId, m.branchName])).entries()].map(([id, name]) => ({ id, name })),
      members: list.members.length,
    }));
  }

  async function render(keys: string[], list: MaintenanceWorkshop = LIST, answer = true): Promise<HTMLElement> {
    api = jasmine.createSpyObj<MaintenanceWorkshopApi>('MaintenanceWorkshopApi', ['add', 'remove']);
    pages = jasmine.createSpyObj<MaintenancePagesApi>('MaintenancePagesApi', ['workshop']);
    serve(list);
    api.add.and.callFake(async (employeeId) => ({
      ...list, available: [],
      members: [...list.members, member({ id: 'm4', employeeId, fullName: 'Yacine Saidi' })],
    }));
    api.remove.and.callFake(async (id) => ({ ...list, members: list.members.filter((m) => m.id !== id) }));
    confirm = jasmine.createSpyObj<ConfirmService>('ConfirmService', ['ask']);
    confirm.ask.and.resolveTo(answer);
    TestBed.configureTestingModule({
      imports: [MaintenanceWorkshopComponent],
      providers: [
        { provide: MaintenanceWorkshopApi, useValue: api },
        { provide: MaintenancePagesApi, useValue: pages },
        { provide: ConfirmService, useValue: confirm },
      ],
    });
    signIn(keys);
    fixture = TestBed.createComponent(MaintenanceWorkshopComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('lists each person with their state in words and warns of a branch whose list books nobody', async () => {
    const page = await render(['maintenance.view', 'maintenance.settings']);
    expect(page.textContent).toContain(t('maint.workshop.inForce'));
    expect(page.textContent).toContain(t('maint.workshop.emptyBranch', { branch: 'Oran' }));
    expect(page.textContent).not.toContain(t('maint.workshop.emptyBranch', { branch: 'Alger-Centre' }));
    const states = Array.from(page.querySelectorAll('app-status')).map((s) => s.textContent?.trim());
    expect(states).toEqual([
      t('maint.workshop.state.bookable'),
      t('maint.workshop.state.moved', { branch: 'Oran' }),
      t('maint.workshop.state.left'),
    ]);
    // The colour follows the word, from the one tone table.
    expect(page.querySelector('app-status')!.className).toContain('tone-success');
    expect(page.querySelectorAll('table.ui-table--cards td[data-label]').length).toBeGreaterThan(0);
  });

  it('says the list is kept but not read under the default choice', async () => {
    const page = await render(['maintenance.view'], { ...LIST, bookableEmployees: 'AnyEmployeeOfBranch' });
    expect(page.textContent).toContain(t('maint.workshop.notInForce'));
    expect(page.textContent).not.toContain(t('maint.workshop.emptyBranch', { branch: 'Oran' }));
  });

  it('adds a person to their own branch’s list, and removes one only once the removal is confirmed', async () => {
    const page = await render(['maintenance.view', 'maintenance.settings']);
    const c = fixture.componentInstance;
    c.employeeId = 'e4';
    await c.add();
    fixture.detectChanges();
    expect(api.add).toHaveBeenCalledWith('e4', 'b1');
    expect(page.textContent).toContain(t('maint.workshop.added.notice', { name: 'Yacine Saidi' }));
    // The page on screen is asked for again after the change.
    expect(pages.workshop).toHaveBeenCalledTimes(2);

    const removal = page.querySelectorAll('app-menu .app-menu-item');
    expect(Array.from(removal).every((b) => b.classList.contains('is-danger'))).toBeTrue();
    await c.remove(LIST.members[0]);
    expect(confirm.ask).toHaveBeenCalledWith(jasmine.objectContaining({ danger: true }));
    expect(api.remove).toHaveBeenCalledWith('m1');

    // Kept: nothing removed.
    TestBed.resetTestingModule();
    await render(['maintenance.view', 'maintenance.settings'], LIST, false);
    await fixture.componentInstance.remove(LIST.members[0]);
    expect(api.remove).not.toHaveBeenCalled();
  });

  it('offers neither adding nor removing to a reader without maintenance.settings, and says a refusal in words', async () => {
    let page = await render(['maintenance.view']);
    expect(page.querySelector('#mws-employee')).toBeNull();
    expect(page.querySelectorAll('app-menu').length).toBe(0);

    TestBed.resetTestingModule();
    page = await render(['maintenance.view', 'maintenance.settings']);
    api.add.and.rejectWith(new ApiError(JSON.stringify({ detail: 'x', code: 'already_on_workshop_list' }), 409));
    fixture.componentInstance.employeeId = 'e4';
    await fixture.componentInstance.add();
    fixture.detectChanges();
    expect(page.querySelector('.notice.danger')?.textContent).toContain(t('maint.workshop.error.already'));
  });

  it('says when nobody is on the list yet', async () => {
    const page = await render(['maintenance.view'], { ...LIST, members: [] });
    expect(page.textContent).toContain(t('maint.workshop.none'));
  });

  it('asks the server for the page with the state and branch filters (owner, 2026-11-17)', async () => {
    await render(['maintenance.view']);
    expect(pages.workshop).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, branch: null, state: 'all' }));
    fixture.componentInstance.stateFilter.set('moved');
    fixture.componentInstance.branchFilter.set('b1');
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 350));
    expect(pages.workshop).toHaveBeenCalledWith(jasmine.objectContaining({ skip: 0, take: 10, branch: 'b1', state: 'moved' }));
  });
});
