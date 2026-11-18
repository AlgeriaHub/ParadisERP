import type { BranchSupport } from './branch-scope.service';
import { CASH_BOX, DASHBOARD_KEYS, NavUser, holds, keyed, landingPath as tillLanding, tillScoped } from './landing';

export { CASH_BOX, mayEnterBackOffice } from './landing';
export type { NavUser } from './landing';

/**
 * The one table the back-office navigation is drawn from (REDESIGN_PLAN.md §3–4).
 *
 * The sidebar, the route guards and the landing page all read it, so the three can never
 * disagree about who may open what. Every rule below mirrors the gate of the API endpoint group
 * the page calls (`ParadisERP.API/Endpoints/*`): nothing is offered that the server would refuse,
 * and nothing is hidden that it allows. This is the screen half only. The server still decides,
 * and a page reached by typing its address is refused there exactly as before.
 *
 * Pure on purpose — no injection — so the whole table is pinned by `nav.spec.ts`.
 */

/**
 * The existing section components a back-office page mounts (`BackofficePageComponent`). Two are
 * not sections of the old rail: the low-stock list and the finance settings page, which gather
 * panels that used to sit inside another screen.
 */
export type BackofficeSection =
  | 'dashboard'
  | 'reports'
  | 'giftCards'
  | 'customers'
  | 'customerQuantities'
  | 'receivables'
  | 'rentals'
  | 'products'
  | 'stock'
  | 'lowStock'
  | 'itemStatus'
  | 'missingCost'
  | 'stockValuation'
  | 'fixedAssets'
  | 'closing'
  | 'transfers'
  | 'treasury'
  | 'paymentVouchers'
  | 'suppliers'
  | 'financeSettings'
  | 'personnel'
  | 'accounting'
  | 'store'
  | 'branches'
  | 'devices'
  | 'users'
  | 'roles'
  | 'subscription'
  | 'manufacturing'
  | 'maintenance';

/** One drawing per module (D1: modules only). The paths live in `ui/icon.component.ts`. */
export type ModuleIcon =
  | 'dashboard'
  | 'pos'
  | 'rentals'
  | 'sales'
  | 'customers'
  | 'inventory'
  | 'finance'
  | 'employees'
  | 'manufacturing'
  | 'maintenance'
  | 'reports'
  | 'admin';

export interface NavPage {
  /** Stable id: `<module>.<page>`. */
  key: string;
  /** Dictionary key of the page's name. */
  label: string;
  /** Under `/backoffice/`, or an absolute address for a page outside the shell (the till). */
  path: string;
  /** True when `path` leaves the back-office shell. */
  external?: boolean;
  /** The component the page mounts, and the chip or view it opens inside it. */
  section?: BackofficeSection;
  view?: string;
  /**
   * What the page's endpoint can show (REDESIGN_PLAN.md §8.2), which decides the scope label in
   * its header. `none`: the page says it itself (one product's per-branch table; receiving, which
   * is always the central warehouse).
   */
  scope?: BranchSupport | 'none';
  /**
   * The page's entities and documents are addresses under it (`rentals/contracts/<id>/issue`), all
   * served by the same route so the section keeps what it loaded while the reader moves through
   * them (REDESIGN_PLAN.md §4).
   */
  deep?: boolean;
  /**
   * The page's branch filter is the Admin's working branch (D2, §8.1): it starts there, a change
   * on the page moves it, and the page header says the scope. Only on pages migrated to read it,
   * so a header never names a scope a page's own filter does not show.
   */
  working?: boolean;
  access: (user: NavUser) => boolean;
}

export interface NavModule {
  key: string;
  label: string;
  icon: ModuleIcon;
  pages: NavPage[];
}

// ---------- the rules, one per server gate ----------

type Rule = (user: NavUser) => boolean;

const has = (user: NavUser, permission: string) => (user.permissions ?? []).includes(permission);

/**
 * A page's rule (AUTH-1 Step 6): open to whoever holds one of the keys its endpoint accepts
 * (`API/Authorization/RoutePermissions.cs`), as the login reported them, so a role the company created is
 * offered exactly what it holds, and a key taken off the Manager role takes its page with it. A till-scoped
 * user is offered only the pages marked `till`: the receipts, the vouchers, and manufacturing, which never
 * asked for a role. Before a sign-in that reports keys, the page keeps the role rule it had, `legacy`.
 *
 * For the three system roles as they ship, both sides offer the same pages (`nav.spec.ts` holds that).
 */
const gate =
  (legacy: Rule, keys: readonly string[], options: { till?: boolean } = {}): Rule =>
  (user) =>
    keyed(user) ? (options.till === true || !tillScoped(user)) && keys.some((k) => holds(user, k)) : legacy(user);

/** `RequireAuthorization()` — any signed-in user. */
const signedIn = (_user: NavUser) => true;
/** `RequireRole(Manager, Admin)` — the gate of almost every back-office endpoint group. */
const managerOrAdmin = (user: NavUser) => user.role === 'Manager' || user.role === 'Admin';
/** `RequireRole(Admin)` — `UsersEndpoints`. */
const adminOnly = (user: NavUser) => user.role === 'Admin';
/** `ForCashBox()` — a manager or an admin, or a cashier granted the cash box (`core/cash-box.ts`). */
const handlesCash = (user: NavUser) => managerOrAdmin(user) || has(user, CASH_BOX);
/** `HrEmployeeEndpoints`, `HrPayrollEndpoints`, `HrMoneyEndpoints`, `HrEndpoints:53`. */
const payroll = (user: NavUser) => managerOrAdmin(user) && has(user, MANAGE_PAYROLL);
/**
 * `SubscriptionEndpoints` — Manager or Admin, and only for a company the operator hosts on a
 * subscription. An `Unrestricted` company owns its installation (the owner, 2026-10-05: "no
 * subscription — the owner owns the website"), so there is no plan, expiry or limit to show.
 */
const hostedSubscription = (user: NavUser) => managerOrAdmin(user) && user.subscriptionStatus !== 'Unrestricted';
/** `HrAttendanceEndpoints:37-39` — either grant opens the attendance door. */
const attendance = (user: NavUser) =>
  managerOrAdmin(user) && (has(user, MANAGE_PAYROLL) || has(user, ENTER_ATTENDANCE));

/**
 * `ManufacturingEndpoints` (Phase 1): the reads need `manufacturing.view` — the settings lists also
 * `manufacturing.settings` — read from the capabilities the login answered, never from the role, so a
 * grant on the manufacturing access page opens exactly what the server opens. Which line a reader
 * reaches is the server's; these only decide which pages are offered.
 */
const capable = (user: NavUser, key: string) => (user.capabilities ?? []).includes(key);
const mfgView = (user: NavUser) => capable(user, 'manufacturing.view');
const mfgSettingsView = (user: NavUser) => mfgView(user) || capable(user, 'manufacturing.settings');
/** Phase 2's reports: materials sold at POS, movements awaiting posting, the material flow check. */
const mfgReports = (user: NavUser) => capable(user, 'manufacturing.reports.view');
/** Costing, Phase A: what orders cost — the work in progress and the labour absorbed. */
const mfgCosts = (user: NavUser) => capable(user, 'manufacturing.costs.view');

// Each page's keys, beside the role rule it had. The keys are the ones its own endpoint accepts.
const anyOfOr = (legacy: Rule, ...keys: string[]) => gate(legacy, keys);
const dashboard = gate(managerOrAdmin, DASHBOARD_KEYS);
const cashPage = (key: string) => gate(handlesCash, [key, CASH_BOX], { till: true });
const payrollPage = gate(payroll, ['payroll.manage']);
const attendancePage = gate(attendance, ['payroll.manage', 'hr.attendance']);
const subscriptionPage: Rule = (user) =>
  keyed(user) ? user.subscriptionStatus !== 'Unrestricted' && holds(user, 'subscription.usage') && !tillScoped(user) : hostedSubscription(user);
// Manufacturing never asked for a role: the keys alone, for anyone the back office lets in.
const mfgGate = (legacy: Rule, ...keys: string[]) => gate(legacy, keys, { till: true });
/**
 * The cost history (costing B2) is labour by order, so it is only for whoever sees pay rates as well (owner,
 * 2026-11-03; the server's CostPrivacy): costs, the rates key and payroll.manage, all three.
 */
const COST_HISTORY_KEYS: readonly string[] = ['manufacturing.costs.view', 'manufacturing.costs.rates.view', 'payroll.manage'];
const mfgCostHistory: Rule = (user) =>
  keyed(user)
    ? COST_HISTORY_KEYS.every((k) => holds(user, k))
    : capable(user, 'manufacturing.costs.view') && capable(user, 'manufacturing.costs.rates.view') && has(user, 'payroll.manage');

/**
 * Maintenance › Settings (Phase M0, docs/maintenance/plan.md §7): the operational half is `maintenance.settings`'s, the
 * accounting half `accounting.manage`'s, so either key opens the page (`MaintenanceEndpoints`). A session from before keys
 * keeps the role rule: the Manager holds accounting.manage by role. The module's other pages come with M1, shown only
 * while MS-01 is on.
 */
const maintenanceSettingsPage = gate(managerOrAdmin, ['maintenance.settings', 'accounting.manage']);
/**
 * Maintenance › Jobs and Waiting for repair (Phase M1): `maintenance.view` on the server, and only while the module is on
 * (MS-01) — off, every route of it answers 409 `maintenance_off`, so the pages are not offered. The keys are the Admin's by
 * role and anyone else's by grant, so a session from before keys offers them to the Admin alone.
 */
const maintenanceJobsPage: Rule = (user) => user.maintenanceOn === true && gate(adminOnly, ['maintenance.view'])(user);
/**
 * Maintenance › Reports (Phase M4): recharge against cost and cost per equipment type, `maintenance.costs.view` on the
 * server, while the module is on. What of the labour money it shows is the server's (pay-rate access, CostPrivacy).
 */
const maintenanceReportsPage: Rule = (user) => user.maintenanceOn === true && gate(adminOnly, ['maintenance.costs.view'])(user);
/**
 * Maintenance › Repair rates (Phase M5a, MS-23): read with maintenance.view or maintenance.pricing, kept with
 * maintenance.pricing, while the module is on. Offered to the pricing holders, who keep them.
 */
const maintenanceRatesPage: Rule = (user) => user.maintenanceOn === true && gate(adminOnly, ['maintenance.pricing'])(user);
/**
 * Maintenance › Practice ledger (Phase M6): `reports.accounting` or `maintenance.costs.view` on the server, while the module is
 * on. Labour at payroll rates in its entries also needs pay-rate access there (CostPrivacy); posting is `accounting.manage`'s.
 * A session from before keys keeps the role rule: the Manager holds reports.accounting by role.
 */
const maintenancePracticeLedgerPage: Rule = (user) =>
  user.maintenanceOn === true && gate(managerOrAdmin, ['reports.accounting', 'maintenance.costs.view'])(user);
/**
 * Maintenance › Workshop (MS-04): the workshop list, read with the settings' keys and kept with `maintenance.settings` on the
 * server, while the module is on. Offered to its keepers — the Admin by role, anyone else by grant.
 */
const maintenanceWorkshopPage: Rule = (user) => user.maintenanceOn === true && gate(adminOnly, ['maintenance.settings'])(user);

/** The claim strings, spelled as `Domain/Permissions.cs` spells them. */
export const MANAGE_PAYROLL = 'payroll.manage';
export const ENTER_ATTENDANCE = 'hr.attendance';

// ---------- the tree (REDESIGN_PLAN.md §3.2) ----------

export const NAV: readonly NavModule[] = [
  {
    key: 'dashboard',
    label: 'nav.page.dashboard',
    icon: 'dashboard',
    // One page, so the sidebar draws it as one link. Every block reads an M/A endpoint.
    pages: [{ key: 'dashboard', scope: 'branchOrAll', label: 'nav.page.dashboard', path: 'dashboard', section: 'dashboard', access: dashboard }],
  },
  {
    key: 'pos',
    label: 'nav.module.pos',
    icon: 'pos',
    // The till keeps its own full-screen layout and its own four pages (§12). From the back
    // office it is one way out, not a group to expand.
    pages: [{ key: 'pos.sell', label: 'nav.module.pos', path: '/register', external: true, access: signedIn }],
  },
  {
    key: 'rentals',
    label: 'nav.rent',
    icon: 'rentals',
    pages: [
      { key: 'rentals.contracts', scope: 'branchOrAll', deep: true, label: 'rent.section.contracts', path: 'rentals/contracts', section: 'rentals', view: 'contracts', access: anyOfOr(managerOrAdmin, 'rentals.view') },
      { key: 'rentals.settings', scope: 'company', label: 'rent.section.settings', path: 'rentals/settings', section: 'rentals', view: 'settings', access: anyOfOr(managerOrAdmin, 'rentals.settings') },
    ],
  },
  {
    key: 'sales',
    label: 'nav.module.sales',
    icon: 'sales',
    pages: [
      { key: 'sales.overview', scope: 'branchOrAll', label: 'nav.page.salesOverview', path: 'sales/overview', section: 'reports', access: anyOfOr(managerOrAdmin, 'reports.sales') },
      { key: 'sales.invoices', scope: 'branchOrAll', label: 'nav.page.invoices', path: 'sales/invoices', section: 'receivables', view: 'invoices', access: anyOfOr(managerOrAdmin, 'finance.invoices.view') },
      { key: 'sales.giftCards', scope: 'company', label: 'backoffice.tabs.giftCards', path: 'sales/gift-cards', section: 'giftCards', access: anyOfOr(managerOrAdmin, 'gift_cards.view') },
    ],
  },
  {
    key: 'customers',
    label: 'backoffice.tabs.customers',
    icon: 'customers',
    pages: [
      { key: 'customers.list', scope: 'company', label: 'backoffice.tabs.customers', path: 'customers/list', section: 'customers', access: anyOfOr(managerOrAdmin, 'customers.view') },
      { key: 'customers.accounts', scope: 'company', label: 'backoffice.tabs.receivables', path: 'customers/accounts', section: 'receivables', view: 'home', access: anyOfOr(managerOrAdmin, 'customers.accounts.view') },
      { key: 'customers.quantities', scope: 'branchOrAll', label: 'qstmt.title', path: 'customers/quantities', section: 'customerQuantities', access: anyOfOr(managerOrAdmin, 'rentals.view') },
    ],
  },
  {
    key: 'inventory',
    label: 'nav.module.inventory',
    icon: 'inventory',
    pages: [
      { key: 'inventory.products', scope: 'company', label: 'backoffice.tabs.products', path: 'inventory/products', section: 'products', access: anyOfOr(managerOrAdmin, 'products.view') },
      { key: 'inventory.availability', scope: 'none', label: 'stock.availability', path: 'inventory/availability', section: 'stock', view: 'availability', access: anyOfOr(managerOrAdmin, 'inventory.view') },
      { key: 'inventory.receiving', scope: 'none', label: 'stock.receiving', path: 'inventory/receiving', section: 'stock', view: 'receiving', access: anyOfOr(managerOrAdmin, 'inventory.receive') },
      { key: 'inventory.transfers', scope: 'branchOrAll', label: 'transfer.page', path: 'inventory/transfers', section: 'transfers', access: anyOfOr(managerOrAdmin, 'inventory.view', 'inventory.transfer') },
      { key: 'inventory.states', scope: 'oneBranch', label: 'stock.states', path: 'inventory/states', section: 'stock', view: 'states', access: anyOfOr(managerOrAdmin, 'inventory.view') },
      { key: 'inventory.lowStock', scope: 'branchOrAll', label: 'reports.lowStock', path: 'inventory/low-stock', section: 'lowStock', access: anyOfOr(managerOrAdmin, 'inventory.view') },
      { key: 'inventory.opening', scope: 'oneBranch', label: 'stock.opening', path: 'inventory/opening', section: 'stock', view: 'opening', access: anyOfOr(managerOrAdmin, 'inventory.opening') },
    ],
  },
  {
    key: 'finance',
    label: 'nav.module.finance',
    icon: 'finance',
    pages: [
      // Every Finance list's endpoint takes an optional branch and reads null as "every branch"
      // (handlers re-read for Phase 4: ReceivableEndpoints /receipts, PaymentVoucherEndpoints /,
      // TreasuryEndpoints /pending-payments and /cash-boxes), so all four offer All branches.
      { key: 'finance.receipts', scope: 'branchOrAll', working: true, deep: true, label: 'nav.page.receipts', path: 'finance/receipts', section: 'receivables', view: 'receipts', access: cashPage('finance.receipts.view') },
      { key: 'finance.vouchers', scope: 'branchOrAll', working: true, deep: true, label: 'backoffice.tabs.paymentVouchers', path: 'finance/vouchers', section: 'paymentVouchers', access: cashPage('finance.vouchers.view') },
      { key: 'finance.suppliers', scope: 'company', label: 'nav.page.suppliers', path: 'finance/suppliers', section: 'suppliers', access: anyOfOr(managerOrAdmin, 'inventory.receive', 'finance.vouchers.view') },
      { key: 'finance.awaitingCollection', scope: 'branchOrAll', working: true, label: 'treasury.pending', path: 'finance/awaiting-collection', section: 'treasury', view: 'pending', access: anyOfOr(managerOrAdmin, 'finance.collections') },
      { key: 'finance.cashBoxes', scope: 'branchOrAll', working: true, deep: true, label: 'treasury.cash', path: 'finance/cash-boxes', section: 'treasury', view: 'cash', access: anyOfOr(managerOrAdmin, 'finance.cash_boxes.view') },
      { key: 'finance.settings', scope: 'company', label: 'nav.page.financeSettings', path: 'finance/settings', section: 'financeSettings', access: anyOfOr(managerOrAdmin, 'finance.settings') },
      // CLO-01 (owner, 2026-11-01): the Admin closes and reopens months; accounting.close on the server.
      { key: 'finance.closing', scope: 'company', label: 'closing.navLabel', path: 'finance/closing', section: 'closing', access: anyOfOr(adminOnly, 'accounting.close') },
    ],
  },
  {
    key: 'employees',
    label: 'backoffice.tabs.personnel',
    icon: 'employees',
    pages: [
      { key: 'employees.employees', scope: 'branchOrAll', label: 'hr.section.employees', path: 'employees/employees', section: 'personnel', view: 'employees', access: payrollPage },
      { key: 'employees.attendance', scope: 'oneBranch', label: 'hr.section.attendance', path: 'employees/attendance', section: 'personnel', view: 'attendance', access: attendancePage },
      // FR-3B: the terminal's export, previewed. HrAttendanceEndpoints' group: the same two grants.
      { key: 'employees.attendanceImport', scope: 'oneBranch', label: 'hr.section.attendanceImport', path: 'employees/attendance-import', section: 'personnel', view: 'attendanceImport', access: attendancePage },
      { key: 'employees.clock', scope: 'branchOrAll', label: 'hr.section.clock', path: 'employees/clock', section: 'personnel', view: 'clock', access: attendancePage },
      { key: 'employees.attendanceReview', scope: 'oneBranch', label: 'hr.section.attendanceReview', path: 'employees/attendance-review', section: 'personnel', view: 'attendanceReview', access: attendancePage },
      { key: 'employees.runs', scope: 'branchOrAll', label: 'hr.section.runs', path: 'employees/runs', section: 'personnel', view: 'runs', access: payrollPage },
      { key: 'employees.advances', scope: 'branchOrAll', label: 'hr.section.advances', path: 'employees/advances', section: 'personnel', view: 'advances', access: payrollPage },
      { key: 'employees.wages', scope: 'oneBranch', label: 'hr.section.payments', path: 'employees/wages', section: 'personnel', view: 'payments', access: payrollPage },
      { key: 'employees.statutory', scope: 'branchOrAll', label: 'hr.section.statutory', path: 'employees/statutory', section: 'personnel', view: 'statutory', access: payrollPage },
      { key: 'employees.settings', scope: 'company', label: 'backoffice.tabs.payrollSettings', path: 'employees/settings', section: 'personnel', view: 'settings', access: payrollPage },
    ],
  },
  {
    // Manufacturing, Phase 1: configuration only — lines, recipes and the settings they stand on.
    key: 'manufacturing',
    label: 'nav.module.manufacturing',
    icon: 'manufacturing',
    pages: [
      { key: 'manufacturing.lines', scope: 'none', deep: true, label: 'mfg.page.lines', path: 'manufacturing/lines', section: 'manufacturing', view: 'lines', access: mfgGate(mfgView, 'manufacturing.view') },
      { key: 'manufacturing.orders', scope: 'none', deep: true, label: 'mfg.page.orders', path: 'manufacturing/orders', section: 'manufacturing', view: 'orders', access: mfgGate(mfgView, 'manufacturing.view') },
      { key: 'manufacturing.records', scope: 'none', deep: true, label: 'mfg.page.records', path: 'manufacturing/records', section: 'manufacturing', view: 'records', access: mfgGate(mfgView, 'manufacturing.view') },
      { key: 'manufacturing.boms', scope: 'none', deep: true, label: 'mfg.page.boms', path: 'manufacturing/boms', section: 'manufacturing', view: 'boms', access: mfgGate(mfgView, 'manufacturing.view') },
      { key: 'manufacturing.materials', scope: 'company', label: 'mfg.page.materials', path: 'manufacturing/materials', section: 'manufacturing', view: 'materials', access: mfgGate(mfgSettingsView, 'manufacturing.view', 'manufacturing.settings') },
      { key: 'manufacturing.stages', scope: 'company', label: 'mfg.page.stages', path: 'manufacturing/stage-templates', section: 'manufacturing', view: 'stages', access: mfgGate(mfgSettingsView, 'manufacturing.view', 'manufacturing.settings') },
      { key: 'manufacturing.jobRoles', scope: 'company', label: 'mfg.page.jobRoles', path: 'manufacturing/job-roles', section: 'manufacturing', view: 'jobRoles', access: mfgGate(mfgSettingsView, 'manufacturing.view', 'manufacturing.settings') },
      { key: 'manufacturing.units', scope: 'company', label: 'mfg.page.units', path: 'manufacturing/units', section: 'manufacturing', view: 'units', access: mfgGate(mfgSettingsView, 'manufacturing.view', 'manufacturing.settings') },
      { key: 'manufacturing.specs', scope: 'company', label: 'mfg.page.specs', path: 'manufacturing/spec-fields', section: 'manufacturing', view: 'specs', access: mfgGate(mfgSettingsView, 'manufacturing.view', 'manufacturing.settings') },
      { key: 'manufacturing.posExceptions', scope: 'branchOrAll', label: 'mfg.page.posExceptions', path: 'manufacturing/pos-exceptions', section: 'manufacturing', view: 'posExceptions', access: mfgGate(mfgReports, 'manufacturing.reports.view') },
      { key: 'manufacturing.pending', scope: 'none', label: 'mfg.page.pending', path: 'manufacturing/awaiting-posting', section: 'manufacturing', view: 'pending', access: mfgGate(mfgReports, 'manufacturing.reports.view') },
      { key: 'manufacturing.materialFlow', scope: 'none', label: 'mfg.page.materialFlow', path: 'manufacturing/material-flow', section: 'manufacturing', view: 'materialFlow', access: mfgGate(mfgReports, 'manufacturing.reports.view') },
      { key: 'manufacturing.workInProgress', scope: 'none', label: 'mfg.page.workInProgress', path: 'manufacturing/work-in-progress', section: 'manufacturing', view: 'workInProgress', access: mfgGate(mfgCosts, 'manufacturing.costs.view') },
      // Costing B2: B1's frozen sheets, their late costs and their reconciliation, read only.
      { key: 'manufacturing.costing', scope: 'none', label: 'mfg.page.costing', path: 'manufacturing/costing', section: 'manufacturing', view: 'costing', access: mfgCostHistory },
      { key: 'manufacturing.labourCost', scope: 'none', label: 'mfg.page.labourCost', path: 'manufacturing/labour-cost', section: 'manufacturing', view: 'labourCost', access: mfgGate(mfgCosts, 'manufacturing.costs.view') },
      // Phase C1 (owner, 2026-11-08): the accountant's open costing choices as settings, and the account map. Offered with the
      // costs key, like the other costing pages (manufacturing is never opened by role); changing them asks accounting.manage.
      { key: 'manufacturing.costingSettings', scope: 'none', label: 'mfg.page.costingSettings', path: 'manufacturing/costing-settings', section: 'manufacturing', view: 'costingSettings', access: mfgGate(mfgCosts, 'manufacturing.costs.view') },
      // Phase C2 (owner, 2026-11-09): one month of the entries the chosen model would make, posted nowhere. Each carries one
      // order's labour, so it needs pay-rate access, like the cost history.
      { key: 'manufacturing.practiceLedger', scope: 'none', label: 'mfg.page.practiceLedger', path: 'manufacturing/practice-ledger', section: 'manufacturing', view: 'practiceLedger', access: mfgCostHistory },
    ],
  },
  {
    // Maintenance (docs/maintenance/plan.md): Phase M0's settings, and Phase M1's jobs and waiting list while MS-01 is on.
    key: 'maintenance',
    label: 'nav.module.maintenance',
    icon: 'maintenance',
    pages: [
      // Phase M1: jobs over units in maintenance — a job's address is `maintenance/jobs/<id>`, a new one `maintenance/jobs/new`.
      { key: 'maintenance.jobs', scope: 'branchOrAll', deep: true, label: 'maint.page.jobs', path: 'maintenance/jobs', section: 'maintenance', view: 'jobs', access: maintenanceJobsPage },
      { key: 'maintenance.waiting', scope: 'branchOrAll', label: 'maint.page.waiting', path: 'maintenance/waiting', section: 'maintenance', view: 'waiting', access: maintenanceJobsPage },
      // Phase M7d (MS-38): the repair history of an equipment type or a piece, read with maintenance.view (costs only to costs.view).
      { key: 'maintenance.history', scope: 'branchOrAll', label: 'maint.page.history', path: 'maintenance/history', section: 'maintenance', view: 'history', access: maintenanceJobsPage },
      // Phase M4: the cost reports, a working panel.
      { key: 'maintenance.reports', scope: 'branchOrAll', label: 'maint.page.reports', path: 'maintenance/reports', section: 'maintenance', view: 'reports', access: maintenanceReportsPage },
      // Phase M5a: MS-23's repair rates, the return-note screen's suggestion.
      { key: 'maintenance.rates', scope: 'company', label: 'maint.page.rates', path: 'maintenance/repair-rates', section: 'maintenance', view: 'rates', access: maintenanceRatesPage },
      // Phase M6: the maintenance section of the practice ledger, beside production's as a tab; a month posted from it.
      { key: 'maintenance.practiceLedger', scope: 'branchOrAll', label: 'maintLedger.page', path: 'maintenance/practice-ledger', section: 'maintenance', view: 'practiceLedger', access: maintenancePracticeLedgerPage },
      // MS-04: who of a branch is workshop staff, booked on its jobs under "only the workshop list".
      { key: 'maintenance.workshop', scope: 'branchOrAll', label: 'maint.page.workshop', path: 'maintenance/workshop', section: 'maintenance', view: 'workshop', access: maintenanceWorkshopPage },
      { key: 'maintenance.settings', scope: 'company', label: 'maint.page.settings', path: 'maintenance/settings', section: 'maintenance', view: 'settings', access: maintenanceSettingsPage },
    ],
  },
  {
    key: 'reports',
    label: 'backoffice.tabs.reports',
    icon: 'reports',
    pages: [
      { key: 'reports.itemStatus', scope: 'branchOrAll', label: 'itemStatus.navLabel', path: 'reports/item-status', section: 'itemStatus', access: anyOfOr(managerOrAdmin, 'inventory.view') },
      // SAL-02 (owner, 2026-10-31): what sold with no cost, so the margin is known to be incomplete. `reports.accounting` on the server.
      { key: 'reports.missingCost', scope: 'branchOrAll', label: 'missingCost.navLabel', path: 'reports/sold-without-cost', section: 'missingCost', access: anyOfOr(managerOrAdmin, 'reports.accounting') },
      // P0.4b: stock at a moving weighted average beside today's cost price, company-wide and read only. `reports.accounting` on the server.
      { key: 'reports.stockValuation', scope: 'company', label: 'valuation.navLabel', path: 'reports/stock-valuation', section: 'stockValuation', access: anyOfOr(managerOrAdmin, 'reports.accounting') },
      // REN-12 (Phase M7a): hire equipment as fixed assets — register, month's depreciation, settings. `reports.accounting` on
      // the server; the settings, the switch-over and posting ask accounting.manage.
      { key: 'reports.fixedAssets', scope: 'branchOrAll', label: 'fa.navLabel', path: 'reports/fixed-assets', section: 'fixedAssets', access: anyOfOr(managerOrAdmin, 'reports.accounting') },
      { key: 'reports.trialBalance', scope: 'branchOrGroup', label: 'acc.section.trial', path: 'reports/trial-balance', section: 'accounting', view: 'trial', access: anyOfOr(managerOrAdmin, 'reports.accounting') },
      { key: 'reports.profitLoss', scope: 'branchOrGroup', label: 'acc.section.pl', path: 'reports/profit-loss', section: 'accounting', view: 'pl', access: anyOfOr(managerOrAdmin, 'reports.accounting') },
      { key: 'reports.balanceSheet', scope: 'branchOrGroup', label: 'acc.section.balance', path: 'reports/balance-sheet', section: 'accounting', view: 'balance', access: anyOfOr(managerOrAdmin, 'reports.accounting') },
      { key: 'reports.accountStatement', scope: 'branchOrGroup', label: 'acc.section.statement', path: 'reports/account-statement', section: 'accounting', view: 'statement', access: anyOfOr(managerOrAdmin, 'reports.accounting') },
      { key: 'reports.journal', scope: 'branchOrGroup', label: 'acc.section.vouchers', path: 'reports/journal', section: 'accounting', view: 'vouchers', access: anyOfOr(managerOrAdmin, 'accounting.view') },
    ],
  },
  {
    key: 'admin',
    label: 'nav.module.admin',
    icon: 'admin',
    pages: [
      { key: 'admin.company', scope: 'company', label: 'backoffice.tabs.store', path: 'admin/company', section: 'store', access: anyOfOr(managerOrAdmin, 'company.settings') },
      { key: 'admin.branches', scope: 'company', label: 'backoffice.tabs.branches', path: 'admin/branches', section: 'branches', access: anyOfOr(managerOrAdmin, 'branches.view') },
      { key: 'admin.tills', scope: 'branchOrAll', label: 'backoffice.tabs.devices', path: 'admin/tills', section: 'devices', access: anyOfOr(managerOrAdmin, 'devices.manage') },
      { key: 'admin.users', scope: 'company', label: 'backoffice.tabs.users', path: 'admin/users', section: 'users', access: anyOfOr(adminOnly, 'users.manage') },
      // AUTH-1 Steps 5–6: the roles the company works with and what each holds. `roles.manage` on the server.
      { key: 'admin.roles', scope: 'company', deep: true, label: 'nav.page.roles', path: 'admin/roles', section: 'roles', access: anyOfOr(adminOnly, 'roles.manage') },
      { key: 'admin.subscription', scope: 'company', label: 'subscription.title', path: 'admin/subscription', section: 'subscription', access: subscriptionPage },
    ],
  },
];

/** Every page inside the back-office shell — the routes are generated from this. */
export const BACKOFFICE_PAGES: readonly NavPage[] = NAV.flatMap((m) => m.pages).filter((p) => !p.external);

/** The modules this user may see, each holding only the pages they may open; empty modules dropped. */
export function visibleNav(user: NavUser | null | undefined): NavModule[] {
  if (!user) return [];
  return NAV.map((m) => ({ ...m, pages: m.pages.filter((p) => p.access(user)) })).filter((m) => m.pages.length > 0);
}

export function findPage(key: string): NavPage | undefined {
  return NAV.flatMap((m) => m.pages).find((p) => p.key === key);
}

export function moduleOf(pageKey: string): NavModule | undefined {
  return NAV.find((m) => m.pages.some((p) => p.key === pageKey));
}

export function canOpen(user: NavUser | null | undefined, pageKey: string): boolean {
  const page = findPage(pageKey);
  return !!user && !!page && page.access(user);
}

/**
 * The old one-segment addresses (`#/backoffice/<section>`), so a bookmark, a printed link or a
 * link inside the app still lands on the page that replaced its section. A cashier who holds the
 * cash box only ever had the receipts of the customer accounts, so for them that one goes to the
 * receipts page.
 */
export const LEGACY_SECTIONS: Readonly<Record<string, string>> = {
  reports: 'sales/overview',
  customers: 'customers/list',
  receivables: 'customers/accounts',
  giftCards: 'sales/gift-cards',
  rentals: 'rentals/contracts',
  products: 'inventory/products',
  categories: 'inventory/products',
  stock: 'inventory/receiving',
  personnel: 'employees/employees',
  treasury: 'finance/awaiting-collection',
  paymentVouchers: 'finance/vouchers',
  accounting: 'reports/trial-balance',
  store: 'admin/company',
  branches: 'admin/branches',
  devices: 'admin/tills',
  users: 'admin/users',
  subscription: 'admin/subscription',
};

/**
 * Pages that were taken out, sent to the page that took their place. Product categories went in
 * 2026-10-12 (the owner: no longer used since rental and sale items stopped being kept apart), so
 * their page opens the product list.
 */
export const RETIRED_PAGES: Readonly<Record<string, string>> = {
  'inventory/categories': 'inventory/products',
  // Retired 2026-11-05 (owner): the users screen gives every permission, the manufacturing ones included.
  'manufacturing/access': 'admin/users',
};

export function legacyTarget(section: string, user: NavUser | null | undefined): string | null {
  const cashBoxAtTill = !!user && (keyed(user) ? tillScoped(user) && holds(user, CASH_BOX) : !managerOrAdmin(user) && has(user, CASH_BOX));
  if (section === 'receivables' && cashBoxAtTill) return 'finance/receipts';
  return LEGACY_SECTIONS[section] ?? null;
}

/**
 * Where a user lands after signing in, or on a page they may not open (core/landing.ts); and, for a user the
 * dashboard is not for, on a role the company made, the first page of this table they may open.
 */
export function landingPath(user: NavUser | null | undefined): string {
  const path = tillLanding(user);
  if (path !== '/backoffice' || !user) return path;
  // Not the subscription page: whether it shows depends on a status this call is not given.
  const first = BACKOFFICE_PAGES.find((p) => p.key !== 'admin.subscription' && p.access(user));
  return first ? `/backoffice/${first.path}` : '/register';
}
