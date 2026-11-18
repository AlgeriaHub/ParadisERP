import { signal } from '@angular/core';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AdminBranch, AdminService, AdminUser } from '../../core/admin.service';
import { ApiError } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { isolate } from '../../core/bidi';
import { I18nService } from '../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../ui/confirm.service';
import { AdminPagesApi } from './admin-pages.api';
import { Employee, HrApi } from './payroll/hr-api.service';
import { BackofficeUsersComponent } from './users-tab.component';

/**
 * The accounts as the server pages them (owner, 2026-11-17: "paginate in backend"): the page, and over every account
 * the roles they are on and the branches they work at.
 */
const usersPage = (rows: AdminUser[]) => ({
  items: rows,
  total: rows.length,
  skip: 0,
  take: 10,
  summary: {
    roles: [...new Set(rows.map((u) => u.roleId ?? u.role))].map((value) => ({ value, name: null })),
    branches: [...new Set(rows.map((u) => u.branchId).filter((b): b is string => !!b))],
  },
});

// `warehouse` is the kind (2026-10-05): a warehouse is the primary one in these fixtures.
const branch = (id: string, nameFr: string, warehouse: boolean): AdminBranch => ({
  id, nameAr: nameFr, nameFr, nameEn: nameFr, address: '', phone: '',
  isMain: true, isWarehouse: warehouse, allowNegativeStock: false, isActive: true, version: 1,
} as AdminBranch);

/** A slice of the catalogue (GET /api/permissions), in its order: sales before customers before payroll. */
const CATALOGUE = [
  { module: 'sales', keys: ['sales.create', 'sales.untaxed'] },
  { module: 'customers', keys: ['customers.create'] },
  { module: 'payroll', keys: ['payroll.manage', 'hr.attendance'] },
];

/** The reader: an Admin from a session before keys, who may give anything (core/landing mayGive). */
const ADMIN_READER = { id: 'me', role: 'Admin' };

const user = (username: string, tillCode: string | null, tillActive = true): AdminUser => ({
  id: username, username, fullName: username, role: tillCode ? 'Cashier' : 'Manager', isActive: true, version: 1,
  branchId: 'alger', tillCode, tillActive,
});

describe('BackofficeUsersComponent — a cashier’s own till', () => {
  let fixture: ComponentFixture<BackofficeUsersComponent>;
  let createError: unknown;

  beforeEach(async () => {
    createError = null;
    TestBed.configureTestingModule({
      imports: [BackofficeUsersComponent],
      providers: [
        {
          provide: AdminPagesApi,
          useValue: {
            usersPage: () => Promise.resolve(usersPage([user('caisse.a', 'C02'), user('caisse.b', 'C03', false), user('gerant', null)])),
          },
        },
        {
          provide: AdminService,
          useValue: {
            listBranches: () => Promise.resolve([branch('depot', 'Dépôt', true), branch('alger', 'Alger', false)]),
            createUser: () => (createError ? Promise.reject(createError) : Promise.resolve(user('x', null))),
          },
        },
        { provide: AuthService, useValue: { currentUser: signal(null), logout: () => Promise.resolve() } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
        { provide: HrApi, useValue: { employees: () => Promise.resolve({ total: 0, skip: 0, take: 1000, rows: [] }) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(BackofficeUsersComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  const i18n = () => TestBed.inject(I18nService);
  const cells = (label: string) =>
    [...fixture.nativeElement.querySelectorAll(`td[data-label="${label}"]`)].map((td: HTMLElement) => td.textContent?.trim());

  it('shows each cashier’s till beside them, switched off in words, and a dash for a user with none', () => {
    expect(cells(i18n().t('backoffice.users.till'))).toEqual(['C02', `C03 — ${i18n().t('common.inactive')}`, '—']);
  });

  it('lists the main warehouse for a cashier, but not as a choice, and says why', () => {
    fixture.componentInstance.openCreate();
    fixture.componentInstance.role = 'Cashier';
    fixture.detectChanges();
    const options = [...fixture.nativeElement.querySelectorAll('#us-branch option')] as HTMLOptionElement[];
    const depot = options.find((o) => o.textContent?.includes('Dépôt'))!;
    expect(depot.disabled).toBeTrue();
    expect(depot.textContent).toContain(i18n().t('backoffice.users.branch.warehouseNoCashier'));
    expect(options.find((o) => o.textContent?.includes('Alger'))!.disabled).toBeFalse();
    expect(fixture.nativeElement.textContent).toContain(i18n().t('backoffice.users.branch.hintTill'));
  });

  it('offers the warehouse to a manager', () => {
    fixture.componentInstance.openCreate();
    fixture.componentInstance.role = 'Manager';
    fixture.detectChanges();
    const options = [...fixture.nativeElement.querySelectorAll('#us-branch option')] as HTMLOptionElement[];
    expect(options.find((o) => o.textContent?.includes('Dépôt'))!.disabled).toBeFalse();
  });

  it('says the server’s refusal of a cashier on the warehouse in words', async () => {
    createError = new ApiError(JSON.stringify({ status: 400, code: 'cashier_on_warehouse' }), 400);
    const c = fixture.componentInstance;
    c.openCreate();
    c.employeeId = 'emp-c';
    c.password = 'secret1';
    c.branchId = 'depot';
    await c.save();
    expect(c.error()).toBe(i18n().t('backoffice.users.cashierOnWarehouse'));
  });
});

const employee = (id: string, fullName: string, branchId: string, accountUserId: string | null = null): Employee => ({
  id, number: 'EMP-00000' + id.slice(-1), fullName, branchId, branchName: branchId, payBasis: 'Monthly', baseRate: 30000,
  payoutMethod: 'Cash', hiredOn: '2026-01-01', isActive: true, openAdvances: 0,
  accountUserId, accountSignIn: accountUserId ? '58310427' : null,
} as Employee);

describe('BackofficeUsersComponent — an account from an employee, with a Login ID', () => {
  let fixture: ComponentFixture<BackofficeUsersComponent>;
  let sent: unknown[];
  let createError: unknown;
  let query: Record<string, string>;

  const setUp = async () => {
    sent = [];
    TestBed.configureTestingModule({
      imports: [BackofficeUsersComponent],
      providers: [
        {
          provide: AdminPagesApi,
          useValue: {
            usersPage: () => Promise.resolve(usersPage([
              { ...user('acc-1', null), username: '58310427', loginId: '58310427', fullName: 'Ahmed Mohamed Ali',
                employeeId: 'e1', employeeNumber: 'EMP-000001' },
              { ...user('acc-2', null), username: 'ancien.gerant', fullName: 'Ancien' },
            ])),
          },
        },
        {
          provide: AdminService,
          useValue: {
            listBranches: () => Promise.resolve([branch('depot', 'Dépôt', true), branch('alger', 'Alger', false)]),
            listPermissions: () => Promise.resolve(CATALOGUE),
            createUser: (req: unknown) => {
              sent.push(req);
              return createError
                ? Promise.reject(createError)
                : Promise.resolve({ ...user('acc-3', null), username: '71946205', loginId: '71946205', fullName: 'Nadia' });
            },
          },
        },
        { provide: AuthService, useValue: { currentUser: signal(ADMIN_READER), logout: () => Promise.resolve() } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
        {
          provide: HrApi,
          useValue: {
            employees: () => Promise.resolve({
              total: 3, skip: 0, take: 1000,
              rows: [employee('e1', 'Ahmed Mohamed Ali', 'alger', 'acc-1'), employee('e2', 'Nadia', 'alger'), employee('e3', 'Rachid', 'depot')],
            }),
          },
        },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(BackofficeUsersComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  };

  beforeEach(() => {
    createError = null;
    query = {};
  });

  const i18n = () => TestBed.inject(I18nService);
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  it('picks an employee instead of typing a username, offers only those without an account, and says the Login ID comes on save', async () => {
    await setUp();
    fixture.componentInstance.openCreate();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('#us-username')).toBeNull();
    const options = [...fixture.nativeElement.querySelectorAll('#us-employee option')].map((o: HTMLOptionElement) => o.textContent?.trim());
    expect(options.some((o) => o?.includes('Nadia'))).toBeTrue();
    expect(options.some((o) => o?.includes('Ahmed Mohamed Ali'))).toBeFalse();
    expect(text()).toContain(i18n().t('backoffice.users.loginId.willGenerate'));
  });

  it('sends the employee, never a username, starts on their branch, and shows the Login ID the server drew', async () => {
    await setUp();
    const c = fixture.componentInstance;
    c.openCreate();
    c.employeeId = 'e2';
    c.employeeChosen('e2');
    expect(c.branchId).toBe('alger');
    c.password = 'secret1';
    await c.save();
    fixture.detectChanges();
    const request = sent[0] as Record<string, unknown>;
    expect(request['employeeId']).toBe('e2');
    expect('username' in request).toBeFalse();
    expect('fullName' in request).toBeFalse();
    expect(c.created()).toEqual({ name: 'Nadia', loginId: '71946205' });
    expect(fixture.nativeElement.querySelector('[data-login-id]').textContent.trim()).toBe('71946205');
  });

  it('starts a new account with nothing of its own, sends what is ticked in any module, and shows the role’s as the role’s', async () => {
    await setUp();
    const c = fixture.componentInstance;
    c.openCreate();
    fixture.detectChanges();
    const box = (key: string) => fixture.nativeElement.querySelector(`[id="us-perm-${key}"]`) as HTMLInputElement;
    expect([box('sales.untaxed').checked, box('sales.untaxed').disabled]).toEqual([false, false]);
    // The explanation the screen always gave beneath "sell untaxed" stays beneath its key.
    expect(text()).toContain(i18n().t('backoffice.users.sellsUntaxed.hint'));

    c.employeeId = 'e2';
    c.employeeChosen('e2');
    c.password = 'secret1';
    box('customers.create').click();
    box('sales.untaxed').click();
    fixture.detectChanges();
    expect(text()).toContain(i18n().t('users.perm.own'));
    await c.save();
    // The whole set, in the catalogue's order, and none of the older fields beside it.
    expect((sent[0] as Record<string, unknown>)['additionalPermissions']).toEqual(['sales.untaxed', 'customers.create']);
    expect('permissions' in (sent[0] as Record<string, unknown>)).toBeFalse();
    expect('sellsUntaxed' in (sent[0] as Record<string, unknown>)).toBeFalse();

    // A manager holds "sell untaxed" by role: ticked, locked, said so, and never sent as their own.
    c.openCreate();
    // The Manager system role, by its fixed id (Domain SystemRoles.ManagerId).
    c.chooseRole('5a17e000-0000-4000-8000-0000000000a2');
    fixture.detectChanges();
    expect([box('sales.untaxed').checked, box('sales.untaxed').disabled]).toEqual([true, true]);
    expect(text()).toContain(i18n().t('backoffice.users.permissions.byRole'));
    c.employeeId = 'e2';
    c.employeeChosen('e2');
    c.password = 'secret1';
    await c.save();
    expect((sent[1] as Record<string, unknown>)['additionalPermissions']).toEqual([]);
  });

  it('says what the account holds in one sentence: from the role, given to the person, in all', async () => {
    await setUp();
    const c = fixture.componentInstance;
    c.openCreate();
    c.chooseRole('5a17e000-0000-4000-8000-0000000000a2');
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[id="us-perm-payroll.manage"]') as HTMLInputElement).click();
    fixture.detectChanges();
    // The Manager role gives "sell untaxed" (of these five), and payroll is given to the person.
    expect(c.summary()).toBe(i18n().t('users.perm.summary', {
      role: isolate(i18n().t('backoffice.users.role.manager')), fromRole: 1, own: 1, held: 2, total: 5,
    }));
    expect(text()).toContain(c.summary());
    expect(c.effectiveCount()).toBe(2);
    expect(c.ownKeys()).toEqual(['payroll.manage']);
    expect(text()).toContain(i18n().t('users.perm.ownList', { list: i18n().t('perm.payroll.manage') }));
  });

  it('a cashier is not started on a warehouse employee’s branch', async () => {
    await setUp();
    const c = fixture.componentInstance;
    c.openCreate();
    c.role = 'Cashier';
    c.employeeId = 'e3';
    c.employeeChosen('e3');
    expect(c.branchId).toBeNull();
  });

  it('says an employee who already has an account in words', async () => {
    await setUp();
    createError = new ApiError(JSON.stringify({ status: 409, code: 'employee_has_account' }), 409);
    const c = fixture.componentInstance;
    c.openCreate();
    c.employeeId = 'e2';
    c.employeeChosen('e2');
    c.password = 'secret1';
    await c.save();
    expect(c.error()).toBe(i18n().t('backoffice.users.problem.employee_has_account'));
  });

  it('lists the Login ID and the employee number, and an older account by its username, not linked', async () => {
    await setUp();
    const loginCells = [...fixture.nativeElement.querySelectorAll(`td[data-label="${i18n().t('backoffice.users.loginId')}"]`)]
      .map((td: HTMLElement) => td.textContent?.replace(/\s+/g, ' ').trim());
    expect(loginCells[0]).toBe('58310427');
    expect(loginCells[1]).toContain('ancien.gerant');
    expect(loginCells[1]).toContain(i18n().t('backoffice.users.noLoginId'));
    expect(text()).toContain('EMP-000001');
    expect(text()).toContain(i18n().t('backoffice.users.notLinked'));
  });

  it('opens on the employee chosen from Staff (“give access”)', async () => {
    query = { employee: 'e2' };
    await setUp();
    const c = fixture.componentInstance;
    expect(c.editorOpen()).toBeTrue();
    expect(c.employeeId).toBe('e2');
    expect(c.branchId).toBe('alger');
  });
});
describe('BackofficeUsersComponent — roles the company made (AUTH-1 Step 5)', () => {
  let fixture: ComponentFixture<BackofficeUsersComponent>;
  let sent: Record<string, unknown>[];
  let updateError: unknown;
  let listed: number;
  const role = (id: string, key: string | null, name: string, scope: 'Company' | 'AssignedBranch' | 'TillBranch', permissions: string[], isActive = true) => ({
    id, key, name, description: null, isActive, isSystem: !!key, scope, version: 1, permissions, members: 0,
    permissionsEditable: key !== 'admin', detailsEditable: !key, deletable: false,
  });
  const ROLES = [
    role('5a17e000-0000-4000-8000-0000000000a1', 'admin', 'Admin', 'Company', ['payroll.manage', 'sales.untaxed']),
    role('5a17e000-0000-4000-8000-0000000000a2', 'manager', 'Manager', 'AssignedBranch', ['sales.untaxed']),
    role('5a17e000-0000-4000-8000-0000000000a3', 'cashier', 'Cashier', 'TillBranch', []),
    role('foreman', null, 'Chef d’équipe', 'AssignedBranch', ['hr.attendance']),
    role('paused', null, 'En pause', 'AssignedBranch', [], false),
  ];

  beforeEach(async () => {
    sent = [];
    updateError = null;
    listed = 0;
    TestBed.configureTestingModule({
      imports: [BackofficeUsersComponent],
      providers: [
        {
          provide: AdminPagesApi,
          useValue: {
            usersPage: () => (listed++, Promise.resolve(usersPage([{
              ...user('chef', null), roleId: 'foreman', roleName: 'Chef d’équipe', roleScope: 'AssignedBranch',
              additionalPermissions: ['payroll.manage'], authorizationVersion: 7,
            } as AdminUser]))),
          },
        },
        {
          provide: AdminService,
          useValue: {
            listBranches: () => Promise.resolve([branch('alger', 'Alger', false)]),
            listRoles: () => Promise.resolve(ROLES),
            listPermissions: () => Promise.resolve(CATALOGUE),
            updateUser: (_id: string, r: Record<string, unknown>) => {
              sent.push(r);
              return updateError ? Promise.reject(updateError) : Promise.resolve(user('chef', null));
            },
          },
        },
        { provide: AuthService, useValue: { currentUser: signal(ADMIN_READER), logout: () => Promise.resolve() } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
        { provide: HrApi, useValue: { employees: () => Promise.resolve({ total: 0, skip: 0, take: 1000, rows: [] }) } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(BackofficeUsersComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('names the role on the row, offers every active role, and shows what the chosen role holds as the role’s', async () => {
    const c = fixture.componentInstance;
    const roleCell = fixture.nativeElement.querySelector(`td[data-label="${TestBed.inject(I18nService).t('backoffice.users.role')}"]`) as HTMLElement;
    expect(roleCell.textContent?.trim()).toBe('Chef d’équipe');

    c.edit(c.users()[0]);
    fixture.detectChanges();
    const options = [...fixture.nativeElement.querySelectorAll('#us-role option')].map((o) => (o as HTMLOptionElement).textContent?.trim());
    expect(options).toContain('Chef d’équipe');
    expect(options.some((o) => o?.startsWith('En pause'))).toBeFalse();
    // The foreman role gives hr.attendance: ticked and locked as the role's, and not sent as the account's own.
    const attendance = fixture.nativeElement.querySelector('[id="us-perm-hr.attendance"]') as HTMLInputElement;
    expect([attendance.checked, attendance.disabled]).toEqual([true, true]);
    await c.save();
    expect(sent[0]['roleId']).toBe('foreman');
    expect(sent[0]['role']).toBe('Manager');
    expect(sent[0]['additionalPermissions']).toEqual(['payroll.manage']);
  });

  it('shows the account’s own key as its own, sends the set without it once unticked, and the version it opened', async () => {
    const c = fixture.componentInstance;
    const i18n = TestBed.inject(I18nService);
    // The row says how many keys come on top of the role, and which.
    const cell = fixture.nativeElement.querySelector(`td[data-label="${i18n.t('backoffice.users.permissions')}"]`) as HTMLElement;
    expect(cell.textContent?.trim()).toBe(i18n.t('users.perm.rowOwn', { n: 1, list: i18n.t('perm.payroll.manage') }));

    c.edit(c.users()[0]);
    fixture.detectChanges();
    const payroll = fixture.nativeElement.querySelector('[id="us-perm-payroll.manage"]') as HTMLInputElement;
    expect([payroll.checked, payroll.disabled]).toEqual([true, false]);
    payroll.click();
    await c.save();
    expect(sent[0]['additionalPermissions']).toEqual([]);
    expect(sent[0]['authorizationVersion']).toBe(7);
    expect('permissions' in sent[0]).toBeFalse();
  });

  it('closes an account changed by someone else meanwhile, reads the list again and says so', async () => {
    updateError = new ApiError(JSON.stringify({ status: 409, code: 'user_changed' }), 409);
    const c = fixture.componentInstance;
    c.edit(c.users()[0]);
    const before = listed;
    await c.save();
    expect(c.editorOpen()).toBeFalse();
    expect(listed).toBe(before + 1);
    expect(c.error()).toBe(TestBed.inject(I18nService).t('users.perm.changed'));
  });

  it('offers a key the reader does not hold only to read, and says why', async () => {
    const auth = TestBed.inject(AuthService) as unknown as { currentUser: ReturnType<typeof signal> };
    auth.currentUser.set({ id: 'me', role: 'Manager', scope: 'AssignedBranch', permissionKeys: ['users.manage', 'sales.create'] });
    const c = fixture.componentInstance;
    c.edit(c.users()[0]);
    fixture.detectChanges();
    const box = (key: string) => fixture.nativeElement.querySelector(`[id="us-perm-${key}"]`) as HTMLInputElement;
    expect(box('sales.create').disabled).toBeFalse();
    expect(box('customers.create').disabled).toBeTrue();
    expect(fixture.nativeElement.textContent).toContain(TestBed.inject(I18nService).t('roles.notYours'));
  });
});
