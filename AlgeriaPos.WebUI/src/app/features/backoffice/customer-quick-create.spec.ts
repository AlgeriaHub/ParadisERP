import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ADMIN_PERMISSIONS, ADMIN_ROLE_PERMISSIONS, AdminCustomer, AdminService } from '../../core/admin.service';
import { AuthService, withBranch } from '../../core/auth.service';
import { BranchScopeService } from '../../core/branch-scope.service';
import { CatalogStore } from '../../core/catalog-store';
import { ConnectivityService } from '../../core/connectivity.service';
import { CUSTOMERS_CREATE, canCreateCustomer, selectCreatedCustomer } from '../../core/customer-access';
import { PosUser } from '../../core/models';
import { ConfirmService } from '../../ui/confirm.service';
import { BackofficeCustomersComponent } from './customers-tab.component';
import { QuickCustomerComponent } from './quick-customer.component';
import { RentalContractFormComponent } from './rentals/rental-contract-form.component';
import { RentalSettings, RentalsApi } from './rentals/rentals-api.service';

/**
 * FR-1 (the owner, 2026-10-03): a new customer is created from the rental form or the till,
 * offered only to whom the server lets create one and only online, picked straight away on
 * success, and leaving the screen underneath exactly as it was on success or on cancel.
 */
const newCustomer = (id = 'new-1'): AdminCustomer =>
  ({
    id,
    code: 'C-NEW',
    fullName: 'Nouveau Client',
    phone: null,
    address: null,
    taxId: null,
    email: null,
    birthDate: null,
    creditLimit: 0,
    balance: 0,
    loyaltyPoints: 0,
    version: 1,
    kind: 'Person',
    idDocumentNumber: null,
    representative: null,
  }) as AdminCustomer;

/** A signed-in user as the login describes them: role, grants, and the server's capabilities. */
const userWith = (role: PosUser['role'], capabilities?: string[], permissions: string[] = []): PosUser =>
  ({ id: 'u-' + role, username: role.toLowerCase(), fullName: role, role, pinHash: '', isActive: true, version: 1, permissions, capabilities }) as PosUser;

/**
 * What a new customer's dialog shows before the kind is chosen, wherever it is hosted (owner,
 * 2026-10-05): the small dialog, the two choices, Cancel — no field and no Save.
 */
function expectKindQuestionOnly(root: HTMLElement): void {
  const dialog = root.querySelector('section.bo-edit-modal') as HTMLElement;
  expect(dialog).withContext('the editor’s own dialog').not.toBeNull();
  expect(dialog.classList).toContain('is-choosing');
  expect(dialog.querySelector('#cu-pick-person')).not.toBeNull();
  expect(dialog.querySelector('#cu-pick-company')).not.toBeNull();
  expect(dialog.querySelectorAll('input, select, textarea').length).toBe(0);
  expect(dialog.querySelector('.bo-panel__foot .btn-primary')).toBeNull();
  // One dialog, never one inside another.
  expect(root.querySelectorAll('section.bo-edit-modal, [role="dialog"]').length).toBe(1);
}

describe('who may create a customer from a screen', () => {
  it('offers it to an Admin and a Manager whose login reports customers.create, while online', () => {
    expect(canCreateCustomer(userWith('Admin', [CUSTOMERS_CREATE]), true)).toBeTrue();
    expect(canCreateCustomer(userWith('Manager', [CUSTOMERS_CREATE]), true)).toBeTrue();
  });

  it('does not offer it to a Cashier, whose login carries no such capability — not even one holding the cash box', () => {
    expect(canCreateCustomer(userWith('Cashier', []), true)).toBeFalse();
    expect(canCreateCustomer(userWith('Cashier', [], ['treasury.cash_box']), true)).toBeFalse();
  });

  it('does not offer it to an authorised user offline: there is no offline customer creation', () => {
    expect(canCreateCustomer(userWith('Manager', [CUSTOMERS_CREATE]), false)).toBeFalse();
    expect(canCreateCustomer(userWith('Admin', [CUSTOMERS_CREATE]), false)).toBeFalse();
    expect(canCreateCustomer(null, true)).toBeFalse();
  });

  it('reads the capability, never a role name: the role alone grants nothing and does not take it away', () => {
    expect(canCreateCustomer(userWith('Admin', []), true)).toBeFalse();
    expect(canCreateCustomer(userWith('Manager', []), true)).toBeFalse();
    // The server is the one to decide who holds it; the screen reflects whatever it reported.
    expect(canCreateCustomer(userWith('Cashier', [CUSTOMERS_CREATE]), true)).toBeTrue();
    // A grant spelled like the capability is not the capability.
    expect(canCreateCustomer(userWith('Manager', [], [CUSTOMERS_CREATE]), true)).toBeFalse();
  });

  it('is not on the users screen: nobody can tick it, so a Cashier cannot be given it', () => {
    expect(ADMIN_PERMISSIONS as readonly string[]).not.toContain(CUSTOMERS_CREATE);
    expect(ADMIN_ROLE_PERMISSIONS as readonly string[]).not.toContain(CUSTOMERS_CREATE);
  });

  it('reads an old session without capabilities as not allowed, without an error', () => {
    const stored = userWith('Admin', undefined);
    // Signed in before the login carried capabilities: no sessionBranch at all, or one without the field.
    expect(() => canCreateCustomer(withBranch(stored, { sessionBranch: null }), true)).not.toThrow();
    expect(canCreateCustomer(withBranch(stored, { sessionBranch: null }), true)).toBeFalse();
    const older = withBranch(stored, { sessionBranch: { userId: stored.id, branchId: null, locked: false, permissions: [] } });
    expect(older.capabilities).toEqual([]);
    expect(canCreateCustomer(older, true)).toBeFalse();
    // The next online sign-in stores them, and the session laid over the pulled row carries them.
    const current = withBranch(stored, {
      sessionBranch: { userId: stored.id, branchId: null, locked: false, permissions: [], capabilities: [CUSTOMERS_CREATE] },
    });
    expect(canCreateCustomer(current, true)).toBeTrue();
  });
});

describe('the till picks a customer it just created', () => {
  it('pulls first, then selects the customer the pull brought', async () => {
    const order: string[] = [];
    const known = new Set<string>();
    const picked = await selectCreatedCustomer(
      'new-1',
      async () => {
        order.push('refresh');
        known.add('new-1');
      },
      (id) => known.has(id),
      (id) => order.push('select ' + id),
    );
    expect(picked).toBeTrue();
    expect(order).toEqual(['refresh', 'select new-1']);
  });

  it('selects nothing when the pull has not brought it, so the sale never names a customer the till cannot show', async () => {
    const select = jasmine.createSpy('select');
    const picked = await selectCreatedCustomer('new-1', async () => undefined, () => false, select);
    expect(picked).toBeFalse();
    expect(select).not.toHaveBeenCalled();
  });
});

describe('the customer editor opened to create one customer', () => {
  let admin: jasmine.SpyObj<AdminService>;

  beforeEach(() => {
    admin = jasmine.createSpyObj<AdminService>('AdminService', ['createCustomer', 'customersPage', 'getStore']);
    admin.createCustomer.and.resolveTo(newCustomer());
    admin.customersPage.and.resolveTo({ items: [], total: 0, skip: 0, take: 10 });
    admin.getStore.and.resolveTo({
      requireCustomerPhone: false,
      requireCustomerEmail: false,
      requireCustomerBirthDate: false,
      requireCustomerAddress: false,
      requireCustomerTaxId: false,
    } as never);
    TestBed.configureTestingModule({ providers: [{ provide: AdminService, useValue: admin }, { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } }] });
  });

  async function open() {
    const fixture = TestBed.createComponent(BackofficeCustomersComponent);
    fixture.componentRef.setInput('createOnly', true);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  it('opens straight on a new customer and never loads the customer list', async () => {
    const fixture = await open();
    expect(fixture.componentInstance.editorOpen()).toBeTrue();
    expect(fixture.componentInstance.editingId()).toBeNull();
    expect(admin.customersPage).not.toHaveBeenCalled();
  });

  it('on a successful save reports the customer the server returned, through the same API call', async () => {
    const fixture = await open();
    const editor = fixture.componentInstance;
    const created: AdminCustomer[] = [];
    editor.created.subscribe((c) => created.push(c));
    editor.chooseKind('Person');
    Object.assign(editor.form, { firstName: 'Nouveau', familyName: 'Client' });
    await editor.save();
    expect(admin.createCustomer).toHaveBeenCalledTimes(1);
    expect(created.map((c) => c.id)).toEqual(['new-1']);
    expect(admin.customersPage).not.toHaveBeenCalled();
  });

  it('asks for no code and sends none: the server draws it on save (owner, 2026-10-05)', async () => {
    const fixture = await open();
    const editor = fixture.componentInstance;
    expect((fixture.nativeElement as HTMLElement).querySelector('input[id^="cu-code"]')).toBeNull();
    editor.chooseKind('Person');
    Object.assign(editor.form, { firstName: 'Nouveau', familyName: 'Client' });
    await editor.save();
    expect('code' in admin.createCustomer.calls.mostRecent().args[0]).toBeFalse();
  });

  it('keeps its own validation: an incomplete customer is not sent', async () => {
    const fixture = await open();
    const editor = fixture.componentInstance;
    await editor.save(); // no name, no kind
    expect(admin.createCustomer).not.toHaveBeenCalled();
  });

  it('creates at the branch the host gives, and falls back to the working-branch rule without one', async () => {
    spyOn(TestBed.inject(BranchScopeService), 'resolve').and.returnValue({ branchId: 'working', label: 'branch' });
    const fixture = await open();
    const editor = fixture.componentInstance;
    const create = async () => {
      editor.openCreate();
      editor.chooseKind('Person');
      Object.assign(editor.form, { firstName: 'Nouveau', familyName: 'Client' });
      await editor.save();
      return admin.createCustomer.calls.mostRecent().args[0].branchId;
    };
    fixture.componentRef.setInput('atBranch', 'b-contract');
    expect(await create()).toBe('b-contract');
    fixture.componentRef.setInput('atBranch', null);
    expect(await create()).toBe('working');
  });

  it('opens on the kind question alone, as the till hosts it, and grows into the fields once answered', async () => {
    const fixture = TestBed.createComponent(QuickCustomerComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expectKindQuestionOnly(root);

    (root.querySelector('#cu-pick-person') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    const dialog = root.querySelector('section.bo-edit-modal') as HTMLElement;
    expect(dialog.classList).not.toContain('is-choosing');
    expect(dialog.querySelector('#cu-first-name')).not.toBeNull();
    expect(dialog.querySelector('#cu-family-name')).not.toBeNull();
    expect(dialog.querySelector('#cu-id-doc')).not.toBeNull();
    expect(dialog.querySelector('.bo-panel__foot .btn-primary')).not.toBeNull();
  });

  it('on cancel reports closed and creates nothing', async () => {
    const fixture = await open();
    const editor = fixture.componentInstance;
    let closed = 0;
    editor.closed.subscribe(() => closed++);
    editor.cancelEdit();
    expect(closed).toBe(1);
    expect(admin.createCustomer).not.toHaveBeenCalled();
  });
});

describe('the rental contract form and a customer created from it', () => {
  const settings: RentalSettings = { overdueMultiplier: 1.5, depositEnabled: false, taxRate: 0, cutoffHour: null } as RentalSettings;
  const existing = { ...newCustomer('old-1'), code: 'C-OLD', fullName: 'Client existant' } as AdminCustomer;
  const manager = { id: 'u1', role: 'Manager', branchId: 'b1', branchLocked: true, permissions: [], capabilities: [CUSTOMERS_CREATE] };
  const user = signal<unknown>(manager);
  const online = signal(true);

  let admin: jasmine.SpyObj<AdminService>;

  beforeEach(() => {
    const api = jasmine.createSpyObj<RentalsApi>('RentalsApi', ['listSites']);
    api.listSites.and.resolveTo([]);
    admin = jasmine.createSpyObj<AdminService>('AdminService', ['listRepresentatives', 'createCustomer', 'getStore']);
    admin.listRepresentatives.and.resolveTo([]);
    admin.createCustomer.and.resolveTo(newCustomer());
    admin.getStore.and.rejectWith(new Error('not needed'));
    TestBed.configureTestingModule({
      providers: [
        { provide: RentalsApi, useValue: api },
        { provide: AdminService, useValue: admin },
        { provide: AuthService, useValue: { currentUser: user } },
        { provide: ConnectivityService, useValue: { online } },
        { provide: CatalogStore, useValue: { active: signal([]), products: signal([]), ensure: () => Promise.resolve([]), refresh: () => Promise.resolve([]) } },
        { provide: ConfirmService, useValue: { ask: () => Promise.resolve(true) } },
      ],
    });
  });

  function form() {
    const fixture = TestBed.createComponent(RentalContractFormComponent);
    fixture.componentRef.setInput('settings', settings);
    fixture.componentRef.setInput('customers', [existing]);
    fixture.componentRef.setInput('branches', [{ id: 'b1', nameFr: 'Dépôt', nameAr: 'المستودع', nameEn: 'Depot', isMain: true, isActive: true }]);
    return fixture;
  }

  it('offers the button when the login reports customers.create and the till is online, and not otherwise', () => {
    const f = form().componentInstance;
    user.set(manager);
    online.set(true);
    expect(f.mayCreateCustomer()).toBeTrue();
    online.set(false);
    expect(f.mayCreateCustomer()).toBeFalse();
    online.set(true);
    user.set({ id: 'u2', role: 'Cashier', branchId: 'b1', branchLocked: true, permissions: ['treasury.cash_box'], capabilities: [] });
    expect(f.mayCreateCustomer()).toBeFalse();
    // An old session: a Manager whose stored login predates capabilities.
    user.set({ id: 'u1', role: 'Manager', branchId: 'b1', branchLocked: true, permissions: [] });
    expect(f.mayCreateCustomer()).toBeFalse();
    user.set(manager);
  });

  it('selects the new customer at once and leaves everything else typed on the form as it was', async () => {
    const f = form().componentInstance;
    f.notes.set('livraison lundi');
    f.signedOn.set('2026-10-01');
    const before = f.lines();
    const emitted: AdminCustomer[] = [];
    f.customerCreatedOut.subscribe((c) => emitted.push(c));
    f.quickCustomer.set(true);

    await f.customerCreated(newCustomer());

    expect(f.quickCustomer()).toBeFalse();
    expect(f.customerId()).toBe('new-1');
    expect(f.allCustomers().map((c) => c.id)).toEqual(['old-1', 'new-1']);
    expect(emitted.map((c) => c.id)).toEqual(['new-1']);
    expect(f.notes()).toBe('livraison lundi');
    expect(f.signedOn()).toBe('2026-10-01');
    expect(f.lines()).toBe(before);
  });

  it('closing the editor without saving changes nothing on the form', () => {
    const f = form().componentInstance;
    f.notes.set('livraison lundi');
    void f.pickCustomer('old-1');
    f.quickCustomer.set(true);
    f.quickCustomer.set(false); // what (closed) does
    expect(f.customerId()).toBe('old-1');
    expect(f.notes()).toBe('livraison lundi');
    expect(f.allCustomers().map((c) => c.id)).toEqual(['old-1']);
  });

  /** The editor the form opened, found through the quick-create overlay it hosts. */
  async function openQuickCreate(fixture: ReturnType<typeof form>) {
    fixture.componentInstance.quickCustomer.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const quick = fixture.debugElement.query(By.directive(QuickCustomerComponent));
    const editor = quick.query(By.directive(BackofficeCustomersComponent)).componentInstance as BackofficeCustomersComponent;
    return { quick: quick.componentInstance as QuickCustomerComponent, editor };
  }

  it('opens the same small kind question over the contract, in the editor’s own dialog', async () => {
    const fixture = form();
    fixture.detectChanges();
    await fixture.whenStable();
    const { editor } = await openQuickCreate(fixture);
    const host = fixture.debugElement.query(By.directive(QuickCustomerComponent)).nativeElement as HTMLElement;
    expectKindQuestionOnly(host);

    editor.pickKind('Company');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.querySelector('section.bo-edit-modal')?.classList).not.toContain('is-choosing');
    expect(host.querySelector('#cu-tax')).not.toBeNull();
    expect(host.querySelector('#cu-rep-name')).not.toBeNull();
  });

  it('creates the customer at the branch the contract is raised at, not the working branch', async () => {
    spyOn(TestBed.inject(BranchScopeService), 'resolve').and.returnValue({ branchId: 'working', label: 'branch' });
    const fixture = form();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.componentInstance.branchId.set('b1');
    const { quick, editor } = await openQuickCreate(fixture);
    expect(quick.branchId()).toBe('b1');

    editor.chooseKind('Person');
    Object.assign(editor.form, { firstName: 'Nouveau', familyName: 'Client' });
    await editor.save();
    expect(admin.createCustomer.calls.mostRecent().args[0].branchId).toBe('b1');
  });

  it('falls back to the working-branch rule while the contract has no branch yet', async () => {
    spyOn(TestBed.inject(BranchScopeService), 'resolve').and.returnValue({ branchId: 'working', label: 'branch' });
    const fixture = form();
    // A new contract starts on the default branch; the reader has cleared the choice.
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.componentInstance.branchId.set(null);
    const { quick, editor } = await openQuickCreate(fixture);
    expect(quick.branchId()).toBeNull();

    editor.chooseKind('Person');
    Object.assign(editor.form, { firstName: 'Nouveau', familyName: 'Client' });
    await editor.save();
    expect(admin.createCustomer.calls.mostRecent().args[0].branchId).toBe('working');
  });
});
