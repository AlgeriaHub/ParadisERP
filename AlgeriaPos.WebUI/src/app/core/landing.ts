import { PosUser, RoleScope } from './models';

/**
 * Where a user lands — the one piece of `core/nav.ts` the till needs (its header's way into the
 * back office), kept apart so the navigation table itself travels in the back-office chunk and not
 * in a till's download.
 */

/**
 * The facts the navigation rules read: a role and the named permissions the login answered, and
 * the company's subscription status as the server last reported it (sign-in or pull). Unknown —
 * an installation that has not pulled since the upgrade — reads as today: nothing hidden.
 */
export type NavUser = Pick<PosUser, 'role'> & {
  permissions?: readonly string[];
  /** What the server's own decision lets the user do (LoginResponse.user.capabilities). */
  capabilities?: readonly string[];
  /**
   * Every key the server's decision gives (AUTH-1 Step 6). Present, the rules read it and the role's scope,
   * so a role the company created is offered exactly what it holds; absent — a session from before — they
   * read the role as they always did.
   */
  permissionKeys?: readonly string[];
  scope?: RoleScope | null;
  subscriptionStatus?: string | null;
  /**
   * MS-01, the maintenance module, as the server last said it (`core/maintenance-module.service.ts`): the module's job
   * pages are offered only while it is on. Unknown or absent reads as off.
   */
  maintenanceOn?: boolean | null;
};

/** `Domain/Permissions.CashBox`, spelled as the claim is. */
export const CASH_BOX = 'treasury.cash_box';

const managerOrAdmin = (user: NavUser) => user.role === 'Manager' || user.role === 'Admin';

/** True when the login reported the user's keys, so the screens decide from them (AUTH-1 Step 6). */
export const keyed = (user: NavUser): boolean => Array.isArray(user.permissionKeys);

/** The user holds this key, as the server's decision said at sign-in. */
export const holds = (user: NavUser, key: string): boolean => (user.permissionKeys ?? []).includes(key);

/** A narrower key, by the wider one that opens all it opens (Domain PermissionCatalog.CoveredBy). */
const COVERED_BY: Readonly<Record<string, string>> = { 'hr.attendance': 'payroll.manage' };

/**
 * Whether the reader may give or take away this key — on a role or to one person: they hold it, or a key that
 * covers it. Never more than their own authority (AUTH-1 §17.3); the server checks it all again. A session from
 * before the keys reads as the Admin's, the only one who could give anything then.
 */
export const mayGive = (user: NavUser | null | undefined, key: string): boolean => {
  if (!user) return false;
  if (!keyed(user)) return user.role === 'Admin';
  return holds(user, key) || (!!COVERED_BY[key] && holds(user, COVERED_BY[key]));
};

/**
 * A till-scoped user — today's Cashier — works at the till: of the back office they use only the receipts and
 * the vouchers (owner, 2026-10-02). A scope the login did not say is read as the narrowest, as the server reads it.
 */
export const tillScoped = (user: NavUser): boolean => (user.scope ?? 'TillBranch') === 'TillBranch';

/** The keys of the back-office pages a till-scoped user may open: the receipts and the vouchers. */
export const TILL_BACKOFFICE_KEYS: readonly string[] = [CASH_BOX, 'finance.receipts.view', 'finance.vouchers.view'];

/**
 * The keys the dashboard's blocks read (REDESIGN_PLAN.md Appendix A): any one opens it, and a block whose key
 * is missing says so on its own.
 */
export const DASHBOARD_KEYS: readonly string[] = [
  'reports.sales', 'rentals.view', 'customers.accounts.view', 'finance.cash_boxes.view', 'finance.collections',
  'finance.vouchers.view', 'inventory.view',
];

/**
 * One action a page offers to whoever holds its key — read from the keys the login reported, or, on a session
 * from before them, as the Admin's (the key's only holder by role until roles could be edited).
 */
export function mayDo(user: NavUser | null | undefined, key: string): boolean {
  if (!user) return false;
  return keyed(user) ? holds(user, key) : user.role === 'Admin';
}

/** May come into the back office at all: anyone off the till, or a till user for the receipts and vouchers. */
export function mayEnterBackOffice(user: NavUser | null | undefined): boolean {
  if (!user) return false;
  if (!keyed(user)) return managerOrAdmin(user) || (user.permissions ?? []).includes(CASH_BOX);
  return !tillScoped(user) || TILL_BACKOFFICE_KEYS.some((k) => holds(user, k));
}

/**
 * Where a user lands after signing in, or on a page they may not open — today's rules with the
 * new addresses (§3.3): a manager or admin on the dashboard (Phase 4, in place of the sales
 * overview), a cashier who holds the cash box on the vouchers, anyone else on the till.
 */
export function landingPath(user: NavUser | null | undefined): string {
  if (!user) return '/login';
  if (keyed(user)) {
    if (!tillScoped(user) && DASHBOARD_KEYS.some((k) => holds(user, k))) return '/backoffice/dashboard';
    if (holds(user, CASH_BOX)) return '/backoffice/finance/vouchers';
    // Some other page of the back office: the first it may open, which the back office's own table finds
    // (core/nav.ts) — kept out of here so the table is not in a till's download.
    return mayEnterBackOffice(user) ? '/backoffice' : '/register';
  }
  if (managerOrAdmin(user)) return '/backoffice/dashboard';
  if ((user.permissions ?? []).includes(CASH_BOX)) return '/backoffice/finance/vouchers';
  return '/register';
}

