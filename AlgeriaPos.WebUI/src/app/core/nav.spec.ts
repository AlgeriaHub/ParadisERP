import { BACKOFFICE_PAGES, LEGACY_SECTIONS, NAV, NavUser, RETIRED_PAGES, canOpen, findPage, landingPath, legacyTarget, visibleNav } from './nav';

/**
 * The sidebar, the route guards and the landing page all read `core/nav.ts`, and every rule in it
 * mirrors a server gate (REDESIGN_PLAN.md §3.3). These pin who sees what, so a rule that drifts
 * from the API shows up here rather than as a screen that answers 403.
 */
describe('back-office navigation', () => {
  const admin: NavUser = {
    role: 'Admin',
    // An Admin holds these by role (Domain/Permissions.cs AdminHolds), and the login sends them.
    permissions: ['stock.view_other_branches', 'payroll.manage', 'payroll.edit_statutory', 'treasury.cash_box'],
    // And the capabilities its role resolves to, the manufacturing module's among them (Phases 1 and 2, costing).
    capabilities: [
      'manufacturing.view', 'manufacturing.lines.manage', 'manufacturing.bom.manage', 'manufacturing.bom.approve', 'manufacturing.settings',
      'manufacturing.orders.create', 'manufacturing.log.submit', 'manufacturing.log.approve', 'manufacturing.reports.view',
      'manufacturing.costs.view', 'manufacturing.costs.rates.view',
    ],
    // With the maintenance module on (MS-01), whose job pages show only then (Phase M1).
    maintenanceOn: true,
  };
  const manager: NavUser = { role: 'Manager', permissions: [] };
  const payrollManager: NavUser = { role: 'Manager', permissions: ['payroll.manage'] };
  const foreman: NavUser = { role: 'Manager', permissions: ['hr.attendance'] };
  const cashier: NavUser = { role: 'Cashier', permissions: [] };
  const cashBoxCashier: NavUser = { role: 'Cashier', permissions: ['treasury.cash_box'] };

  const pageKeys = (user: NavUser) => visibleNav(user).flatMap((m) => m.pages.map((p) => p.key));
  const moduleKeys = (user: NavUser) => visibleNav(user).map((m) => m.key);

  it('gives every page a unique key and every in-shell page a unique address', () => {
    const keys = NAV.flatMap((m) => m.pages.map((p) => p.key));
    expect(new Set(keys).size).toBe(keys.length);
    const paths = BACKOFFICE_PAGES.map((p) => p.path);
    expect(new Set(paths).size).toBe(paths.length);
    expect(BACKOFFICE_PAGES.every((p) => !!p.section)).toBeTrue();
  });

  it('shows the Admin every module and every page', () => {
    expect(pageKeys(admin).length).toBe(NAV.flatMap((m) => m.pages).length);
  });

  it('opens manufacturing from the capabilities the server answered, never from the role', () => {
    // A plain Manager has no manufacturing key by role: no module.
    expect(moduleKeys(manager)).not.toContain('manufacturing');
    // Granted the read, a branch manager sees the lines and recipes the server will let them reach,
    // and the settings lists, which the read also opens — not the access page, which is users.manage.
    const reader: NavUser = { role: 'Manager', permissions: [], capabilities: ['manufacturing.view'] };
    expect(visibleNav(reader).find((m) => m.key === 'manufacturing')!.pages.map((p) => p.key)).toEqual([
      'manufacturing.lines',
      'manufacturing.orders',
      'manufacturing.records',
      'manufacturing.boms',
      'manufacturing.materials',
      'manufacturing.stages',
      'manufacturing.jobRoles',
      'manufacturing.units',
      'manufacturing.specs',
    ]);
    // The settings grant alone opens the settings lists, not the lines or recipes.
    // The reports need their own key: a reader of the configuration does not see them.
    expect(canOpen(reader, 'manufacturing.posExceptions')).toBeFalse();
    const reports: NavUser = { role: 'Manager', permissions: [], capabilities: ['manufacturing.reports.view'] };
    expect(canOpen(reports, 'manufacturing.posExceptions')).toBeTrue();
    expect(canOpen(reports, 'manufacturing.pending')).toBeTrue();
    expect(canOpen(reports, 'manufacturing.materialFlow')).toBeTrue();
    // What orders cost needs its own key (costing, Phase A): the reports key does not open it.
    expect(canOpen(reports, 'manufacturing.workInProgress')).toBeFalse();
    expect(canOpen(reports, 'manufacturing.labourCost')).toBeFalse();
    const costs: NavUser = { role: 'Manager', permissions: [], capabilities: ['manufacturing.costs.view'] };
    expect(canOpen(costs, 'manufacturing.workInProgress')).toBeTrue();
    expect(canOpen(costs, 'manufacturing.labourCost')).toBeTrue();
    expect(canOpen(costs, 'manufacturing.pending')).toBeFalse();
    const settings: NavUser = { role: 'Manager', permissions: [], capabilities: ['manufacturing.settings'] };
    expect(canOpen(settings, 'manufacturing.units')).toBeTrue();
    expect(canOpen(settings, 'manufacturing.lines')).toBeFalse();
    expect(findPage('manufacturing.access')).toBeUndefined();
    expect(RETIRED_PAGES['manufacturing/access']).toBe('admin/users');
  });

  it('shows a plain Manager no payroll page: those endpoints need payroll.manage and answer 403 without it', () => {
    expect(moduleKeys(manager)).not.toContain('employees');
    expect(canOpen(manager, 'employees.employees')).toBeFalse();
    expect(canOpen(manager, 'employees.attendance')).toBeFalse();
  });

  it('opens the whole payroll module to a Manager granted payroll.manage', () => {
    const keys = pageKeys(payrollManager).filter((k) => k.startsWith('employees.'));
    expect(keys.length).toBe(10);
  });

  it('gives a foreman (hr.attendance only) attendance, the fingerprint import and the clock, and no wage page', () => {
    expect(pageKeys(foreman).filter((k) => k.startsWith('employees.'))).toEqual([
      'employees.attendance',
      'employees.attendanceImport',
      'employees.clock',
      'employees.attendanceReview',
    ]);
  });

  it('puts the dashboard first, as one page, for a Manager or an Admin only', () => {
    expect(NAV[0].key).toBe('dashboard');
    expect(NAV[0].pages.length).toBe(1);
    expect(canOpen(admin, 'dashboard')).toBeTrue();
    expect(canOpen(manager, 'dashboard')).toBeTrue();
    expect(canOpen(cashier, 'dashboard')).toBeFalse();
    expect(canOpen(cashBoxCashier, 'dashboard')).toBeFalse();
  });

  it('keeps Users for the Admin alone', () => {
    expect(canOpen(admin, 'admin.users')).toBeTrue();
    expect(canOpen(manager, 'admin.users')).toBeFalse();
  });

  it('leaves a Manager the setup pages the server still allows them', () => {
    for (const key of ['admin.company', 'admin.branches', 'admin.tills', 'admin.subscription']) {
      expect(canOpen(manager, key)).withContext(key).toBeTrue();
    }
  });

  it('shows inventory.transfers to Manager and Admin', () => {
    expect(canOpen(admin, 'inventory.transfers')).toBeTrue();
    expect(canOpen(manager, 'inventory.transfers')).toBeTrue();
    expect(canOpen(cashier, 'inventory.transfers')).toBeFalse();
  });

  it('shows no subscription page to a company that owns its installation (Unrestricted)', () => {
    const owned = { ...admin, subscriptionStatus: 'Unrestricted' };
    expect(canOpen(owned, 'admin.subscription')).toBeFalse();
    expect(canOpen({ ...manager, subscriptionStatus: 'Unrestricted' }, 'admin.subscription')).toBeFalse();
    expect(pageKeys(owned)).not.toContain('admin.subscription');
    // Every other administration page stays.
    expect(pageKeys(owned).filter((k) => k.startsWith('admin.'))).toEqual(['admin.company', 'admin.branches', 'admin.tills', 'admin.users', 'admin.roles']);
  });

  it('keeps the subscription page for a hosted company, and while the status is not known yet', () => {
    for (const status of ['Trial', 'Active', 'Grace', 'Expired', 'Suspended', 'Cancelled', null, undefined]) {
      expect(canOpen({ ...admin, subscriptionStatus: status }, 'admin.subscription')).withContext(String(status)).toBeTrue();
    }
    expect(canOpen({ ...cashier, subscriptionStatus: 'Active' }, 'admin.subscription')).toBeFalse();
  });

  it('shows a cashier the till and nothing in the back office', () => {
    expect(pageKeys(cashier)).toEqual(['pos.sell']);
  });

  it('shows a cashier who holds the cash box the till, the receipts and the vouchers only', () => {
    expect(pageKeys(cashBoxCashier)).toEqual(['pos.sell', 'finance.receipts', 'finance.vouchers']);
  });

  it('lands each kind of user where they landed before, at the new addresses', () => {
    expect(landingPath(admin)).toBe('/backoffice/dashboard');
    expect(landingPath(manager)).toBe('/backoffice/dashboard');
    expect(landingPath(cashBoxCashier)).toBe('/backoffice/finance/vouchers');
    expect(landingPath(cashier)).toBe('/register');
    expect(landingPath(null)).toBe('/login');
  });

  it('sends every old section address to a page that exists', () => {
    const paths = new Set(BACKOFFICE_PAGES.map((p) => p.path));
    for (const [section, target] of Object.entries(LEGACY_SECTIONS)) {
      expect(paths.has(target)).withContext(section).toBeTrue();
    }
    expect(Object.keys(LEGACY_SECTIONS).length).toBe(17);
  });

  it('sends the retired categories page, and its old section address, to the product list', () => {
    const paths = new Set(BACKOFFICE_PAGES.map((p) => p.path));
    expect(paths.has('inventory/categories')).toBeFalse();
    expect(RETIRED_PAGES['inventory/categories']).toBe('inventory/products');
    expect(LEGACY_SECTIONS['categories']).toBe('inventory/products');
    for (const [path, target] of Object.entries(RETIRED_PAGES)) {
      expect(paths.has(target)).withContext(path).toBeTrue();
    }
  });

  it('makes every Finance list read the working branch, and only those whose endpoint takes "all branches"', () => {
    const finance = NAV.find((m) => m.key === 'finance')!.pages;
    const working = finance.filter((p) => p.working).map((p) => p.key);
    expect(working).toEqual(['finance.receipts', 'finance.vouchers', 'finance.awaitingCollection', 'finance.cashBoxes']);
    for (const page of finance.filter((p) => p.working)) expect(page.scope).withContext(page.key).toBe('branchOrAll');
    // Settings are the company's: no branch to choose, and said so.
    expect(finance.find((p) => p.key === 'finance.settings')!.scope).toBe('company');
    // No page outside Finance claims the working branch yet: its header would name a scope its filter does not show.
    expect(NAV.filter((m) => m.key !== 'finance').flatMap((m) => m.pages).some((p) => p.working)).toBeFalse();
  });

  it('gives every Finance document an address: a receipt, a voucher, a cash box', () => {
    const deep = BACKOFFICE_PAGES.filter((p) => p.deep).map((p) => p.key);
    expect(deep).toContain('finance.receipts');
    expect(deep).toContain('finance.vouchers');
    expect(deep).toContain('finance.cashBoxes');
  });

  it('keeps the cash-box cashier out of the managers’ Finance pages, which their endpoints refuse', () => {
    for (const key of ['finance.awaitingCollection', 'finance.cashBoxes', 'finance.settings', 'customers.accounts', 'finance.suppliers']) {
      expect(canOpen(cashBoxCashier, key)).withContext(key).toBeFalse();
      expect(canOpen(manager, key)).withContext(key).toBeTrue();
    }
    expect(canOpen(cashier, 'finance.receipts')).toBeFalse();
    expect(canOpen(cashier, 'finance.vouchers')).toBeFalse();
  });

  it('sends a cash-box cashier who follows an old customer-accounts link to the receipts', () => {
    expect(legacyTarget('receivables', cashBoxCashier)).toBe('finance/receipts');
    expect(legacyTarget('receivables', manager)).toBe('customers/accounts');
    expect(legacyTarget('nonsense', manager)).toBeNull();
  });
});
