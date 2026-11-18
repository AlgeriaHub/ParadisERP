import { NavUser, canOpen, findPage, landingPath, legacyTarget, mayEnterBackOffice, visibleNav } from './nav';
import { mayDo } from './landing';
import { RoleScope } from './models';

/**
 * AUTH-1 Step 6's proof for the screens: deciding from the keys the login reports changes nothing for the three
 * roles as they ship. Every legacy user — each system role with each combination of the five legacy grants, with
 * and without the manufacturing grants, under each subscription status — sees the same sidebar, lands on the same
 * page and comes into the back office alike, whether the screens read the role (a session from before) or the keys.
 *
 * The seeds are copied from the server: `Domain/Authorization/Roles.cs` (ManagerSeed, CashierSeed),
 * `AdminBaseline.cs`, `LegacyGrants.cs` (which legacy grants each role keeps) — the server's own equivalence proof
 * holds the same lists against the routes.
 */
describe('navigation decided from the keys', () => {
  const LEGACY = ['stock.view_other_branches', 'payroll.manage', 'hr.attendance', 'payroll.edit_statutory', 'treasury.cash_box'];
  const MANUFACTURING = [
    'manufacturing.view', 'manufacturing.lines.manage', 'manufacturing.bom.manage', 'manufacturing.bom.approve', 'manufacturing.settings',
    'manufacturing.orders.create', 'manufacturing.orders.start_short', 'manufacturing.orders.manage', 'manufacturing.materials.move',
    'manufacturing.log.submit', 'manufacturing.log.approve', 'manufacturing.labour.override', 'manufacturing.output.release',
    'manufacturing.completion.approve', 'manufacturing.reports.view', 'manufacturing.costs.view', 'manufacturing.costs.snapshot.create',
    'manufacturing.costs.rates.view',
  ];
  // Maintenance (Phase M0): the Admin's by role, anyone else's only as a grant (Domain/Authorization/AdminBaseline.cs).
  const MAINTENANCE = [
    'maintenance.view', 'maintenance.jobs.manage', 'maintenance.jobs.close', 'maintenance.labour.override', 'maintenance.pricing',
    'maintenance.costs.view', 'maintenance.settings',
  ];
  const CATALOGUE = [
    'sales.create', 'sales.view', 'sales.refund', 'reports.sales', 'sales.untaxed',
    'customers.view', 'customers.create', 'customers.edit', 'customers.delete', 'customers.accounts.view', 'customers.opening_balance',
    'customers.accounts.filter', 'customers.documents.view',
    'rentals.view', 'rentals.create', 'rentals.edit', 'rentals.issue', 'rentals.return', 'rentals.close', 'rentals.settings', 'rentals.cancelled',
    'products.view', 'products.manage', 'inventory.view', 'inventory.adjust', 'inventory.receive', 'inventory.transfer',
    'inventory.transfer.write_off', 'inventory.opening', 'stock.view_other_branches', 'gift_cards.view', 'gift_cards.manage',
    'treasury.cash_box', 'finance.receipts.view', 'finance.receipts.create', 'finance.receipts.settle', 'finance.receipts.settle_own',
    'finance.invoices.view', 'finance.invoices.create', 'finance.invoices.untaxed', 'finance.vouchers.view', 'finance.vouchers.create',
    'finance.vouchers.pay', 'finance.vouchers.cancel', 'finance.vouchers.approve', 'finance.vouchers.approve_own', 'finance.collections',
    'finance.cash_boxes.view', 'finance.remit', 'finance.settings', 'suppliers.opening_balance',
    'accounting.view', 'accounting.manage', 'reports.accounting', 'accounting.close',
    'payroll.settings.view', 'payroll.manage', 'payroll.edit_statutory', 'hr.attendance',
    ...MANUFACTURING,
    ...MAINTENANCE,
    'users.manage', 'roles.manage', 'documents.manage', 'branches.view', 'branches.manage', 'devices.manage', 'company.settings',
    'subscription.usage',
  ];
  const ADMIN_BASELINE = CATALOGUE.filter((k) => k !== 'hr.attendance');
  const MANAGER_SEED = [
    'sales.create', 'sales.view', 'sales.refund', 'reports.sales', 'sales.untaxed',
    'customers.view', 'customers.create', 'customers.edit', 'customers.delete', 'customers.accounts.view',
    'rentals.view', 'rentals.create', 'rentals.edit', 'rentals.issue', 'rentals.return', 'rentals.settings',
    'products.view', 'products.manage', 'inventory.view', 'inventory.adjust', 'inventory.receive', 'inventory.transfer',
    'inventory.opening', 'gift_cards.view', 'gift_cards.manage',
    'finance.receipts.view', 'finance.receipts.create', 'finance.receipts.settle', 'finance.invoices.view', 'finance.invoices.create',
    'finance.vouchers.view', 'finance.vouchers.create', 'finance.vouchers.pay', 'finance.vouchers.cancel', 'finance.vouchers.approve',
    'finance.collections', 'finance.cash_boxes.view', 'finance.remit', 'finance.settings',
    'accounting.view', 'accounting.manage', 'reports.accounting',
    'payroll.settings.view',
    'branches.view', 'branches.manage', 'devices.manage', 'company.settings', 'subscription.usage',
  ];
  const CASHIER_SEED = ['sales.create', 'sales.view', 'sales.refund', 'reports.sales'];

  type Role = 'Admin' | 'Manager' | 'Cashier';
  const SCOPE: Record<Role, RoleScope> = { Admin: 'Company', Manager: 'AssignedBranch', Cashier: 'TillBranch' };

  /** What the server resolves for that legacy user after the migration (LegacyGrants: which grants each role keeps). */
  function effective(role: Role, grants: string[], manufacturing: boolean): string[] {
    const kept =
      role === 'Admin' ? [] : role === 'Manager' ? grants.filter((g) => g !== 'treasury.cash_box') : grants.filter((g) => g === 'treasury.cash_box');
    const base = role === 'Admin' ? ADMIN_BASELINE : role === 'Manager' ? MANAGER_SEED : CASHIER_SEED;
    const all = new Set([...base, ...kept, ...(manufacturing ? MANUFACTURING : [])]);
    return CATALOGUE.filter((k) => all.has(k));
  }

  function* everyone(): Generator<{ name: string; legacy: NavUser; keyed: NavUser }> {
    for (const role of ['Admin', 'Manager', 'Cashier'] as Role[])
      for (let mask = 0; mask < 1 << LEGACY.length; mask++)
        for (const manufacturing of [false, true])
          for (const subscriptionStatus of [null, 'Active', 'Unrestricted']) {
            const grants = LEGACY.filter((_, i) => mask & (1 << i));
            const keys = effective(role, grants, manufacturing);
            // What the login sends today: the legacy five held, and the capability names (the manufacturing keys among them).
            const legacy: NavUser = {
              role,
              permissions: LEGACY.filter((k) => keys.includes(k)),
              capabilities: keys.filter((k) => MANUFACTURING.includes(k)),
              subscriptionStatus,
            };
            yield {
              name: `${role}[${grants.join(',')}]${manufacturing ? '+mfg' : ''} ${subscriptionStatus}`,
              legacy,
              keyed: { ...legacy, permissionKeys: keys, scope: SCOPE[role] },
            };
          }
  }

  const pages = (user: NavUser) => visibleNav(user).flatMap((m) => m.pages.map((p) => p.key));

  it('offers every legacy user the same pages, landing and way in, whether it reads the role or the keys', () => {
    let checked = 0;
    for (const { name, legacy, keyed } of everyone()) {
      expect(pages(keyed)).withContext(name).toEqual(pages(legacy));
      expect(landingPath(keyed)).withContext(name).toBe(landingPath(legacy));
      expect(mayEnterBackOffice(keyed)).withContext(name).toBe(mayEnterBackOffice(legacy));
      expect(legacyTarget('receivables', keyed)).withContext(name).toBe(legacyTarget('receivables', legacy));
      expect(mayDo(keyed, 'rentals.close')).withContext(name).toBe(mayDo(legacy, 'rentals.close'));
      expect(mayDo(keyed, 'documents.manage')).withContext(name).toBe(mayDo(legacy, 'documents.manage'));
      checked++;
    }
    expect(checked).toBe(3 * 32 * 2 * 3);
  });

  it('offers a role the company made exactly what its keys open, and a till role only the till’s pages', () => {
    const accountant: NavUser = {
      role: 'Manager', permissions: [], capabilities: [], scope: 'AssignedBranch',
      permissionKeys: ['accounting.view', 'reports.accounting'],
    };
    expect(pages(accountant)).toEqual([
      'pos.sell', 'reports.missingCost', 'reports.stockValuation', 'reports.fixedAssets', 'reports.trialBalance', 'reports.profitLoss', 'reports.balanceSheet', 'reports.accountStatement',
      'reports.journal',
    ]);
    // No dashboard key: they land on the first page they may open, never on one that would send them back.
    expect(landingPath(accountant)).toBe('/backoffice/reports/sold-without-cost');
    expect(canOpen(accountant, 'dashboard')).toBeFalse();

    // A till role holding the receipts but not the cash box comes in for the receipts, and nothing else.
    const counter: NavUser = {
      role: 'Cashier', permissions: [], capabilities: [], scope: 'TillBranch',
      permissionKeys: ['sales.create', 'finance.receipts.view', 'customers.view'],
    };
    expect(mayEnterBackOffice(counter)).toBeTrue();
    expect(pages(counter)).toEqual(['pos.sell', 'finance.receipts']);
    expect(landingPath(counter)).toBe('/backoffice/finance/receipts');

    // A key taken off the Manager role takes its page with it.
    const manager = MANAGER_SEED.filter((k) => k !== 'products.view');
    const trimmed: NavUser = { role: 'Manager', permissions: [], scope: 'AssignedBranch', permissionKeys: manager };
    expect(canOpen(trimmed, 'inventory.products')).toBeFalse();
    expect(canOpen(trimmed, 'inventory.availability')).toBeTrue();

    // Nothing to open in the back office: the till.
    const seller: NavUser = { role: 'Manager', permissions: [], scope: 'AssignedBranch', permissionKeys: ['sales.create'] };
    expect(landingPath(seller)).toBe('/register');
  });
});

describe('maintenance job pages (Phase M1)', () => {
  const keyed = (keys: string[], maintenanceOn: boolean | null): NavUser => ({
    role: 'Manager', permissions: [], capabilities: [], scope: 'AssignedBranch', permissionKeys: keys, maintenanceOn,
  });
  const maintenancePages = (user: NavUser) =>
    visibleNav(user).filter((m) => m.key === 'maintenance').flatMap((m) => m.pages.map((p) => p.key));

  it('shows Jobs and Waiting for repair only while the module is on, to holders of maintenance.view', () => {
    expect(maintenancePages(keyed(['maintenance.view'], true))).toEqual(['maintenance.jobs', 'maintenance.waiting', 'maintenance.history']);
    // Off, or not known yet: the server would answer 409 maintenance_off, so nothing is offered.
    expect(maintenancePages(keyed(['maintenance.view'], false))).toEqual([]);
    expect(maintenancePages(keyed(['maintenance.view'], null))).toEqual([]);
    // On, without the key: not offered. Settings stays as M0 made it, to its own keys whatever the module says.
    expect(maintenancePages(keyed(['accounting.manage'], true))).toEqual(['maintenance.settings']);
    expect(maintenancePages(keyed(['maintenance.view', 'maintenance.settings'], false))).toEqual(['maintenance.settings']);
    expect(canOpen(keyed(['maintenance.view'], true), 'maintenance.jobs')).toBeTrue();
    expect(canOpen(keyed(['maintenance.view'], false), 'maintenance.jobs')).toBeFalse();
    // A till-scoped user never: maintenance is back-office work.
    expect(maintenancePages({ ...keyed(['maintenance.view'], true), scope: 'TillBranch' })).toEqual([]);
    // A session from before keys: the Admin alone holds the maintenance keys by role.
    expect(maintenancePages({ role: 'Admin', maintenanceOn: true })).toContain('maintenance.jobs');
    expect(maintenancePages({ role: 'Manager', maintenanceOn: true })).not.toContain('maintenance.jobs');
  });

  it('shows Reports (Phase M4) only while the module is on, to holders of maintenance.costs.view', () => {
    expect(maintenancePages(keyed(['maintenance.view', 'maintenance.costs.view'], true)))
      .toEqual(['maintenance.jobs', 'maintenance.waiting', 'maintenance.history', 'maintenance.reports', 'maintenance.practiceLedger']);
    expect(maintenancePages(keyed(['maintenance.costs.view'], false))).toEqual([]);
    expect(maintenancePages(keyed(['maintenance.view'], true))).not.toContain('maintenance.reports');
    expect(canOpen(keyed(['maintenance.costs.view'], true), 'maintenance.reports')).toBeTrue();
  });

  it('shows Repair rates (Phase M5a, MS-23) only while the module is on, to holders of maintenance.pricing', () => {
    expect(maintenancePages(keyed(['maintenance.view', 'maintenance.pricing'], true)))
      .toEqual(['maintenance.jobs', 'maintenance.waiting', 'maintenance.history', 'maintenance.rates']);
    expect(maintenancePages(keyed(['maintenance.pricing'], false))).toEqual([]);
    expect(maintenancePages(keyed(['maintenance.view'], true))).not.toContain('maintenance.rates');
  });

  it('shows the Practice ledger (Phase M6) while the module is on, to holders of reports.accounting or maintenance.costs.view', () => {
    expect(maintenancePages(keyed(['reports.accounting'], true))).toEqual(['maintenance.practiceLedger']);
    expect(canOpen(keyed(['maintenance.costs.view'], true), 'maintenance.practiceLedger')).toBeTrue();
    expect(maintenancePages(keyed(['reports.accounting'], false))).toEqual([]);
    expect(maintenancePages(keyed(['maintenance.view'], true))).not.toContain('maintenance.practiceLedger');
    // Posting is accounting.manage's on the page; that key alone does not open it.
    expect(canOpen(keyed(['accounting.manage'], true), 'maintenance.practiceLedger')).toBeFalse();
    expect(findPage('maintenance.practiceLedger')?.path).toBe('maintenance/practice-ledger');
  });

  it('shows the Workshop (MS-04) while the module is on, to holders of maintenance.settings, before Settings', () => {
    expect(maintenancePages(keyed(['maintenance.settings'], true))).toEqual(['maintenance.workshop', 'maintenance.settings']);
    expect(maintenancePages(keyed(['maintenance.settings'], false))).toEqual(['maintenance.settings']);
    // Reading the list (maintenance.view, accounting.manage) does not offer the page that keeps it.
    expect(canOpen(keyed(['maintenance.view'], true), 'maintenance.workshop')).toBeFalse();
    expect(canOpen(keyed(['accounting.manage'], true), 'maintenance.workshop')).toBeFalse();
    // A session from before keys: the Admin alone, by role.
    expect(maintenancePages({ role: 'Admin', maintenanceOn: true })).toContain('maintenance.workshop');
    expect(maintenancePages({ role: 'Manager', maintenanceOn: true })).not.toContain('maintenance.workshop');
    expect(findPage('maintenance.workshop')?.path).toBe('maintenance/workshop');
  });

  it('shows the Repair history (Phase M7d, MS-38) while the module is on, to holders of maintenance.view', () => {
    expect(canOpen(keyed(['maintenance.view'], true), 'maintenance.history')).toBeTrue();
    expect(canOpen(keyed(['maintenance.view'], false), 'maintenance.history')).toBeFalse();
    expect(canOpen(keyed(['maintenance.costs.view'], true), 'maintenance.history')).toBeFalse();
    expect(findPage('maintenance.history')?.path).toBe('maintenance/history');
  });
});
