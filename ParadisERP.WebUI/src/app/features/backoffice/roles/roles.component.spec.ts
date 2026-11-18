import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminRoleRow, AdminService, PermissionModule, UpsertRoleRequest } from '../../../core/admin.service';
import { ApiError } from '../../../core/api.service';
import { AuthService } from '../../../core/auth.service';
import { I18nService } from '../../../core/i18n.service';
import { BACKOFFICE_TRANSLATIONS } from '../../../core/i18n/translations-backoffice';
import { ConfirmService } from '../../../ui/confirm.service';
import { AdminPagesApi, RolesPageQuery } from '../admin-pages.api';
import { BackofficeRolesComponent } from './roles.component';

/**
 * Administration › Roles & permissions (AUTH-1 Steps 5–6): the screen offers only what the server would accept —
 * the Admin role read-only, the Manager and Cashier roles in their permissions only, a role with accounts on it
 * with its scope and status fixed, and never a key the reader does not hold — and says a refusal in words.
 */
describe('roles and permissions', () => {
  const CATALOGUE: PermissionModule[] = [
    { module: 'sales', keys: ['sales.create', 'sales.view'] },
    { module: 'payroll', keys: ['payroll.manage', 'hr.attendance'] },
    { module: 'administration', keys: ['users.manage', 'roles.manage'] },
  ];
  const row = (over: Partial<AdminRoleRow>): AdminRoleRow => ({
    id: 'r', key: null, name: 'Rôle', description: null, isActive: true, isSystem: false, scope: 'AssignedBranch', version: 3,
    permissions: [], members: 0, permissionsEditable: true, detailsEditable: true, deletable: true, ...over,
  });
  const ROLES: AdminRoleRow[] = [
    row({ id: 'admin', key: 'admin', name: 'Admin', isSystem: true, scope: 'Company', permissions: ['sales.create', 'sales.view', 'payroll.manage', 'users.manage', 'roles.manage'], members: 1, permissionsEditable: false, detailsEditable: false, deletable: false }),
    row({ id: 'manager', key: 'manager', name: 'Manager', isSystem: true, permissions: ['sales.create'], members: 4, detailsEditable: false, deletable: false }),
    row({ id: 'keeper', name: 'Magasinier', description: 'Le dépôt', members: 2, deletable: false, permissions: ['sales.view'] }),
    row({ id: 'spare', name: 'Saisonnier', members: 0 }),
  ];

  let fixture: ComponentFixture<BackofficeRolesComponent>;
  let updates: [string, UpsertRoleRequest][];
  let refuseWith: string | null;

  async function open(path: string[], keys = ['sales.create', 'sales.view', 'payroll.manage', 'users.manage', 'roles.manage']) {
    updates = [];
    refuseWith = null;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        // The list is paged by the server, and a role the address names is read by its id (owner, 2026-11-17).
        {
          provide: AdminPagesApi,
          useValue: {
            rolesPage: async (q: RolesPageQuery) => {
              const rows = ROLES.filter((r) => !q.id || r.id === q.id).map((r) => ({ ...r }));
              return { items: rows, total: rows.length, skip: 0, take: q.take };
            },
          },
        },
        {
          provide: AdminService,
          useValue: {
            listPermissions: async () => CATALOGUE,
            roleMembers: async () => [],
            createRole: async (r: UpsertRoleRequest) => row({ id: 'new-id', name: r.name }),
            updateRole: async (id: string, r: UpsertRoleRequest) => {
              if (refuseWith) throw new ApiError(JSON.stringify({ code: refuseWith }), 409);
              updates.push([id, r]);
              return { ...ROLES.find((x) => x.id === id)!, permissions: r.permissions ?? [], version: 4 };
            },
            deleteRole: async () => undefined,
          },
        },
        {
          provide: AuthService,
          useValue: { currentUser: signal({ role: 'Admin', scope: 'Company', permissionKeys: keys, permissions: [], capabilities: [] }) },
        },
        { provide: ConfirmService, useValue: { ask: async () => true } },
      ],
    });
    TestBed.inject(I18nService).extend(BACKOFFICE_TRANSLATIONS);
    fixture = TestBed.createComponent(BackofficeRolesComponent);
    fixture.componentRef.setInput('path', path);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  const el = (selector: string) => fixture.nativeElement.querySelector(selector) as HTMLInputElement | null;
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const t = (key: string, params?: Record<string, string | number>) => TestBed.inject(I18nService).t(key, params);

  it('lists the roles with their scope, accounts and permissions in words, and offers deleting only an unused one', async () => {
    await open([]);
    const rows = Array.from(fixture.nativeElement.querySelectorAll('tbody tr')) as HTMLElement[];
    expect(rows.length).toBe(4);
    expect(rows[0].textContent).toContain(t('backoffice.users.role.admin'));
    expect(rows[0].textContent).toContain(t('roles.system'));
    expect(rows[0].textContent).toContain(t('roles.scope.Company'));
    expect(rows[2].textContent).toContain('Magasinier');
    expect(rows[2].textContent).toContain('Le dépôt');
    expect(rows[2].textContent).toContain(t('roles.permissionsOf', { n: 1, total: 6 }));
    const component = fixture.componentInstance;
    expect(component.roles().filter((r) => r.deletable).map((r) => r.id)).toEqual(['spare']);
  });

  it('opens the Admin role read-only, with no Save', async () => {
    await open(['admin']);
    expect(text()).toContain(t('roles.adminLocked'));
    expect(el('#rl-name')!.disabled).toBeTrue();
    expect(el('#rl-sales\\.create')!.disabled).toBeTrue();
    expect(el('#rl-sales\\.create')!.checked).toBeTrue();
    expect(fixture.nativeElement.querySelector('.rl-foot .btn-primary')).toBeNull();
  });

  it('lets the Manager role change only its permissions, and saves them with the version it opened', async () => {
    const c = await open(['manager']);
    expect(text()).toContain(t('roles.systemFixed'));
    expect([el('#rl-name')!.disabled, el('#rl-scope')!.disabled, el('#rl-active')!.disabled]).toEqual([true, true, true]);
    expect(text()).toContain(t('roles.impact', { n: 4 }));
    el('#rl-sales\\.view')!.click();
    fixture.detectChanges();
    await c.save();
    expect(updates).toEqual([['manager', { permissions: ['sales.create', 'sales.view'], version: 3 }]]);
    fixture.detectChanges();
    expect(text()).toContain(t('roles.saved'));
  });

  it('keeps a role with accounts on it at its scope and switched on, and offers a free one everything', async () => {
    await open(['keeper']);
    expect(el('#rl-name')!.disabled).toBeFalse();
    expect(el('#rl-scope')!.disabled).toBeTrue();
    expect(el('#rl-active')!.disabled).toBeTrue();
    expect(text()).toContain(t('roles.scope.lockedMembers'));
    TestBed.resetTestingModule();
    await open(['spare']);
    expect([el('#rl-scope')!.disabled, el('#rl-active')!.disabled]).toEqual([false, false]);
  });

  it('shows, never offers, a key the reader does not hold — but hr.attendance to whoever runs payroll', async () => {
    await open(['spare'], ['sales.create', 'payroll.manage', 'roles.manage']);
    expect(el('#rl-sales\\.view')!.disabled).toBeTrue();
    expect(el('#rl-users\\.manage')!.disabled).toBeTrue();
    expect(el('#rl-hr\\.attendance')!.disabled).toBeFalse();
    expect(text()).toContain(t('roles.notYours'));
  });

  it('says in words that someone else changed the role meanwhile', async () => {
    const c = await open(['keeper']);
    refuseWith = 'role_changed';
    await c.save();
    fixture.detectChanges();
    expect(text()).toContain(t('roles.error.role_changed'));
  });

  it('asks for a name before creating a role', async () => {
    const c = await open(['new']);
    expect(el('#rl-name')!.disabled).toBeFalse();
    await c.save();
    fixture.detectChanges();
    expect(text()).toContain(t('common.fieldRequired'));
  });
});
