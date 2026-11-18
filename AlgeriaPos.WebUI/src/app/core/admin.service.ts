import { Injectable } from '@angular/core';
import { ApiService } from './api.service';
import { PageResult, pageQueryString } from './paging';
import type { RoleScope } from './models';
import type { StockTransfer } from '../features/backoffice/stock/stock-transfers-api.service';

// The error sentences live in their own small file, so start-up can use them without this client.
export { adminErrorMessage, problemCode, setErrorTranslator } from './error-message';

/**
 * Back-office CRUD against the server catalog. Deliberately never touches IndexedDB: these
 * screens edit the source of truth directly, and the change reaches terminals on their next
 * pull. Every call here requires connectivity, which is why the back office is gated behind
 * the same online-only assumption as the rest of catalog management.
 */

export interface AdminStore {
  id: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  address: string;
  addressAr?: string;
  addressFr?: string;
  addressEn?: string;
  phone: string;
  fiscalIds: Record<string, string>;
  receiptFooter: string;
  receiptFooterAr?: string;
  receiptFooterFr?: string;
  receiptFooterEn?: string;
  requireCustomerEmail: boolean;
  requireCustomerBirthDate: boolean;
  requireCustomerPhone: boolean;
  requireCustomerAddress: boolean;
  requireCustomerTaxId: boolean;
  loyaltyEnabled: boolean;
  loyaltyPointsPerUnit: number;
  loyaltyPointValue: number;
  loyaltyMinRedeemPoints: number;
  /** The company's interface face; null follows the country profile. */
  uiFont: string | null;
  version: number;
}

export interface AdminBranch {
  id: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  address: string;
  addressAr?: string;
  addressFr?: string;
  addressEn?: string;
  phone: string;
  /** The main one of its kind: the main branch, or the main warehouse when `isWarehouse`. */
  isMain: boolean;
  /** The kind: a warehouse holds stock and never sells (no till, no cashier, no cash box). */
  isWarehouse?: boolean;
  allowNegativeStock: boolean;
  isActive: boolean;
  /** A branch still in setup accepts opening balances; a trading one does not. */
  isOpenForOperation?: boolean;
  /** The signed-in link to the branch's logo, or null: the top bar then shows the main branch's. */
  logoUrl?: string | null;
  /** The branch's own fiscal identifiers, keyed like the store's; a key absent prints the company's value. */
  fiscalIds?: Record<string, string> | null;
  /**
   * The location's three-digit code, 100–999 (owner, 2026-10-05): drawn by the server when the
   * location is created and fixed for life, never typed — so it is not on `UpsertBranchRequest`.
   * It sits inside the customer and employee codes drawn there (Cust-112610001, Emp-112610001).
   * Absent from a server that predates it.
   */
  code?: string | null;
  version: number;
}

export interface AdminDevice {
  id: string;
  code: string;
  name: string;
  branchId: string;
  branchName: string;
  isActive: boolean;
  lastSyncAtUtc: string | null;
  lastCursor: number;
  /** On a warehouse, which does not sell: the server refuses new tills there and the list warns. */
  onWarehouse?: boolean;
}

export interface AdminCategory {
  id: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  color: string;
  sortOrder: number;
  version: number;
}

/** No stockQty/reorderLevel here — those are per branch, fetched via getProductStock(). */
export interface AdminProduct {
  id: string;
  sku: string;
  barcode: string | null;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  categoryId: string | null;
  price: number;
  costPrice: number;
  taxRate: number;
  unit: string;
  trackStock: boolean;
  isActive: boolean;
  /** Hired out rather than sold. What a rental contract may put on a line. */
  isRentable: boolean;
  /** What one unit is carried at in the asset register — not its cost, not its hire price. */
  assetUnitValue: number;
  version: number;
}

export interface ProductStockRow {
  productId: string;
  branchId: string;
  branchName: string;
  stockQty: number;
  reorderLevel: number;
}

/**
 * A person signs for themselves; a company acts through an authorised representative. Rows that
 * predate the distinction arrive as `Unspecified` — the migration never guessed, so the screen
 * says "kind not set" and asks. Strings on the wire, never integers: the tills push other enums
 * as numbers and a global converter would break their upload.
 */
export type CustomerKind = 'Person' | 'Company' | 'Unspecified';

/** Which paper an identity number is from — the three the company's contract accepts. */
export type IdDocumentType = 'nationalCard' | 'drivingLicence' | 'passport';

/** The rest of an identity paper beside its number: which paper, who issued it, and when (a day). */
export interface IdDocumentDetails {
  idDocumentType: IdDocumentType | null;
  idDocumentIssuer: string | null;
  /** `YYYY-MM-DD`. */
  idDocumentIssuedOn: string | null;
}

/**
 * One entry in a company's representative history. Append-only on the server: a change ends
 * the current row (`toUtc` set, once) and adds a new one; nothing is edited or deleted, so a
 * contract signed by someone who has since left still names them. `toUtc === null` is the
 * current one. Back-office only — the tills never see this table.
 */
export interface Representative extends Partial<IdDocumentDetails> {
  id: string;
  customerId: string;
  fullName: string;
  idDocumentNumber: string | null;
  phone: string | null;
  note: string | null;
  fromUtc: string;
  toUtc: string | null;
  createdBy: string | null;
  createdAtUtc: string;
}

/** A copy of a customer's identity document kept on file (owner, 2026-10-06). */
export interface CustomerAttachment {
  id: string;
  kind: string;
  fileName: string;
  contentType: string;
  length: number;
  uploadedAtUtc: string;
  uploadedBy: string | null;
}

/** The customers list's server filters; a filter at "all" is not sent. */
export interface CustomersPageQuery {
  skip: number;
  take: number;
  q?: string;
  kind?: string;
  balance?: string;
  points?: string;
  missing?: string;
  id?: string;
}

export interface AdminCustomer extends Partial<IdDocumentDetails> {
  id: string;
  code: string;
  /** For a company, the legal name (raison sociale); for a person, the first name then the family name. */
  fullName: string;
  /** A person's first name; null for a company, and for a person filed before the two parts existed. */
  firstName?: string | null;
  /** A person's family name (اللقب); for one filed before the parts existed, the whole name. Null for a company. */
  familyName?: string | null;
  phone: string | null;
  address: string | null;
  taxId: string | null;
  email: string | null;
  birthDate: string | null;
  creditLimit: number;
  balance: number;
  loyaltyPoints: number;
  version: number;
  kind: CustomerKind;
  /** A person's own identity document — not a fiscal identifier. Always null for a company. */
  idDocumentNumber: string | null;
  /** The current representative of a company; null for a person, an unspecified row, or a company with none yet. */
  representative: Representative | null;
  /**
   * A company's other fiscal identifiers (RC, AI, NIS in the Algeria preset), keyed by the country
   * profile's identifier keys; empty for a person. Absent from a server older than 2026-10-10.
   */
  fiscalIds?: Record<string, string>;
  /**
   * The balance the customer started on the system with, signed: positive, the customer owes the
   * company (owner, 2026-10-10). Fixed once `openingBalanceSetAtUtc` is set; a correction with a
   * reason is the only change after that. Absent from an older server: read as not set.
   */
  openingBalance?: number;
  openingBalanceOn?: string | null;
  openingBalanceSetAtUtc?: string | null;
  openingBalanceSetBy?: string | null;
}

/** Who owes whom on an opening balance or a correction (OpeningBalanceRules on the server). */
export type OpeningDirection = 'owesUs' | 'weOwe';

/** An opening balance as sent: a positive amount, a direction, and the day (null: today). */
export interface OpeningBalanceRequest {
  amount: number;
  direction: OpeningDirection;
  on: string | null;
}

export interface OpeningCorrectionRequest extends OpeningBalanceRequest {
  reason: string;
}

/** A correction as the server keeps it; `amount` is signed like the opening balance. */
export interface OpeningCorrection {
  id: string;
  number: string;
  customerId: string;
  amount: number;
  effectiveOn: string;
  reason: string;
  createdBy: string | null;
  createdAtUtc: string;
}

export type AdminRole = 'Cashier' | 'Manager' | 'Admin';

/**
 * The named permissions the server knows, spelled exactly as `AlgeriaPos.Domain.Permissions`
 * spells them — they become JWT claim values and are compared case-sensitively, and the server
 * drops a name it does not recognise rather than storing it.
 */
export const ADMIN_PERMISSIONS = [
  'stock.view_other_branches',
  'payroll.manage',
  'hr.attendance',
  'payroll.edit_statutory',
  'treasury.cash_box',
] as const;

export type AdminPermission = (typeof ADMIN_PERMISSIONS)[number];

/**
 * What an administrator holds without anyone granting it (`Permissions.AdminHolds`). The users
 * screen shows these ticked and locked for an admin, so it is clear they are the role's and not
 * something this screen could take away.
 */
export const ADMIN_ROLE_PERMISSIONS: readonly AdminPermission[] = [
  'stock.view_other_branches',
  'payroll.manage',
  'payroll.edit_statutory',
  'treasury.cash_box',
];

export interface AdminUser {
  id: string;
  username: string;
  fullName: string;
  role: AdminRole;
  isActive: boolean;
  version: number;
  /** Granted on top of the role — what the users screen ticks and may revoke. */
  permissions?: string[] | null;
  /**
   * What they actually hold: the grants above plus whatever the role carries. Two lists rather
   * than one, because a screen has to be able to show an admin's grant as inherited instead of
   * offering to revoke something it cannot.
   */
  effectivePermissions?: string[] | null;
  /** The one branch a cashier may open a session at; null is any terminal. Ignored for other roles. */
  branchId?: string | null;
  /** The cashier's own till, created with them (2026-10-05): its code, or null for a user with none. */
  tillCode?: string | null;
  /** Whether that till is switched on. */
  tillActive?: boolean;
  /**
   * The Login ID the server drew when the account was created (8 digits, 2026-10-05) — what the
   * account signs in with. Null on an account from before Login IDs, which signs in with `username`.
   */
  loginId?: string | null;
  /** The employee the account belongs to; null on the bootstrap admin and on accounts not linked yet. */
  employeeId?: string | null;
  /** That employee's HR number (Emp-112610001; EMP-000001 before 2026-10-05) — a separate identifier, never the Login ID. */
  employeeNumber?: string | null;
  /** Whether they may leave a till sale untaxed (`sales.untaxed`, owner 2026-11-02). */
  sellsUntaxed?: boolean;
  /** True when that comes from their role (Admin, Manager): shown ticked and locked, never their own tick. */
  sellsUntaxedByRole?: boolean;
  /** The role that decides their access (AUTH-1): `role` above is its scope read as Admin, Manager or Cashier. */
  roleId?: string | null;
  roleName?: string | null;
  roleScope?: RoleScope | null;
  /**
   * Every key given to this account on top of its role, whatever the module (AUTH-1 §17.10), in the catalogue's
   * order. Never a key the role gives: the server stores none twice.
   */
  additionalPermissions?: string[] | null;
  /** The version of the account's role and grants; sent back on a save, so one made on an older view is refused. */
  authorizationVersion?: number;
  /** Two-step login is on for this account (its own choice); the users screen may only turn it off. */
  twoFactorOn?: boolean;
}

/** A module of the permission catalogue and its keys, in the catalogue's order (GET /api/permissions). */
export interface PermissionModule {
  module: string;
  keys: string[];
}

/**
 * A role as the roles screen lists it (GET /api/roles). What may change is the server's to say: the Admin role
 * nothing, the Manager and Cashier roles their permissions, a role the company made everything; it may be deleted
 * once no account, removed ones included, names it.
 */
export interface AdminRoleRow {
  id: string;
  /** `admin`, `manager` or `cashier` for the system roles; null for a role the company made. */
  key: string | null;
  name: string;
  description: string | null;
  isActive: boolean;
  isSystem: boolean;
  scope: RoleScope;
  version: number;
  permissions: string[];
  /** Accounts on it that were not removed, active or not: whose access a save changes. */
  members: number;
  permissionsEditable: boolean;
  detailsEditable: boolean;
  deletable: boolean;
}

export interface RoleMember {
  id: string;
  fullName: string;
  signIn: string;
  branchId: string | null;
  isActive: boolean;
}

/** Creates or edits a role; on an edit an absent field stays as it is. `version` is the one the screen opened. */
export interface UpsertRoleRequest {
  name?: string;
  description?: string | null;
  scope?: RoleScope;
  isActive?: boolean;
  permissions?: string[];
  version?: number;
}

export interface UpsertCategoryRequest {
  nameAr: string;
  nameFr: string;
  nameEn: string;
  color: string;
  sortOrder: number;
}

export interface UpsertProductRequest {
  sku: string;
  barcode: string | null;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  categoryId: string | null;
  price: number;
  costPrice: number;
  taxRate: number;
  unit: string;
  trackStock: boolean;
  isActive: boolean;
  // Absent from this request until now, and the server reads an absent flag as false: every
  // save from the products screen therefore un-marked a rentable article and zeroed its asset
  // value. Present and always sent, so a save carries the article forward as it found it.
  isRentable: boolean;
  assetUnitValue: number;
}

/**
 * No code (owner, 2026-10-05): the server draws a customer's code when the create is saved —
 * the kind's prefix and a dash, the branch's three-digit code, the year and month, a sequence
 * (Cust-112610001) — and never changes it, so the request does not carry one; the drawn code
 * comes back on the returned `AdminCustomer`.
 */
export interface UpsertCustomerRequest extends Partial<IdDocumentDetails> {
  /**
   * Create only: the branch the customer is created at, whose code goes into theirs. The server
   * decides — it honours the Admin's working branch and gives anyone locked to a branch their own,
   * whatever is sent. Never sent on an update: the code does not change.
   */
  branchId?: string | null;
  /** Composed for an older server; a current one composes a person's itself from the two parts. */
  fullName: string;
  /** A person's name in two parts (owner, 2026-10-06), both required; null for a company, which has one name. */
  firstName?: string | null;
  familyName?: string | null;
  phone: string | null;
  address: string | null;
  taxId: string | null;
  /**
   * A company's other fiscal identifiers, by profile key. Omit to leave the stored ones as they
   * are; the server clears a person's whatever is sent.
   */
  fiscalIds?: Record<string, string> | null;
  /** Create only: the balance the customer starts with. Never sent on an update. */
  openingBalance?: OpeningBalanceRequest | null;
  email: string | null;
  birthDate: string | null;
  creditLimit: number;
  kind: CustomerKind;
  idDocumentNumber: string | null;
  /**
   * The FIRST representative of a company, saved in the same request as the company itself so
   * the one decision the feature is about is taken when the kind is set. Never sent while a
   * current one exists — the server answers 409 — because that would be a history rewrite;
   * a departure goes through `changeRepresentative`.
   */
  representative:
    | ({ fullName: string; idDocumentNumber: string | null; phone: string | null; note: string | null } & Partial<IdDocumentDetails>)
    | null;
}

export interface ChangeRepresentativeRequest extends Partial<IdDocumentDetails> {
  fullName: string;
  idDocumentNumber: string | null;
  phone: string | null;
  note: string | null;
  /** The takeover instant; null means now. The previous representative is ended at that same instant. */
  fromUtc: string | null;
}

export interface UpsertStoreRequest {
  nameAr: string;
  nameFr: string;
  nameEn: string;
  address: string;
  addressAr: string;
  addressEn: string;
  phone: string;
  fiscalIds: Record<string, string>;
  receiptFooter: string;
  receiptFooterAr: string;
  receiptFooterEn: string;
  requireCustomerEmail: boolean;
  requireCustomerBirthDate: boolean;
  requireCustomerPhone: boolean;
  requireCustomerAddress: boolean;
  requireCustomerTaxId: boolean;
  loyaltyEnabled: boolean;
  loyaltyPointsPerUnit: number;
  loyaltyPointValue: number;
  loyaltyMinRedeemPoints: number;
  /** Omit to leave the face unchanged; null returns to the country profile's default. */
  uiFont?: string | null;
}

export interface AdminGiftCard {
  id: string;
  code: string;
  initialAmount: number;
  balance: number;
  isActive: boolean;
  expiresAtUtc: string | null;
  customerId: string | null;
  version: number;
}

export interface GiftCardMovement {
  id: string;
  delta: number;
  balanceAfter: number;
  reason: string;
  note: string | null;
  saleId: string | null;
  createdAtUtc: string;
}

export interface UpsertBranchRequest {
  nameAr: string;
  nameFr: string;
  nameEn: string;
  address: string;
  addressAr: string;
  addressEn: string;
  phone: string;
  isMain: boolean;
  isWarehouse: boolean;
  allowNegativeStock: boolean;
  isActive: boolean;
  /** Omitted leaves the stored ones as they are; an empty object clears them. */
  fiscalIds?: Record<string, string>;
  /**
   * The branch's logo, sent whole on every save: the link the server gave keeps it, a new `data:`
   * URL replaces it, null or absent clears it.
   */
  logoUrl: string | null;
}

export interface UpsertDeviceRequest {
  code: string;
  name: string;
  branchId: string;
  isActive: boolean;
}

export interface StockTransferRequest {
  fromBranchId: string;
  toBranchId: string;
  note: string | null;
  lines: { productId: string; qty: number }[];
}

/**
 * A new account, always for an existing employee (2026-10-05). There is no username or name in it:
 * the account takes the employee's name and the server draws its Login ID at save time.
 */
export interface CreateUserRequest {
  employeeId: string;
  role: AdminRole;
  /** The role by id (AUTH-1 Step 5): when sent, `role` is only its scope's legacy name and the server ignores it. */
  roleId?: string | null;
  password: string;
  pin: string | null;
  /** Named grants on top of the role. An unknown name is dropped by the server, never stored. */
  permissions?: string[];
  /** The branch the account works at; absent means the employee's own branch. */
  branchId?: string | null;
  /** "Sell untaxed at the till" on the account itself; a new cashier starts without it. */
  sellsUntaxed?: boolean;
  /**
   * Every key given to the account on top of its role, from the whole catalogue (AUTH-1 §17.10). When sent, the
   * server ignores `permissions` and `sellsUntaxed`.
   */
  additionalPermissions?: string[];
}

export interface UpdateUserRequest {
  fullName: string;
  role: AdminRole;
  /** The role by id (AUTH-1 Step 5): when sent, `role` is ignored by the server. */
  roleId?: string | null;
  isActive: boolean;
  /**
   * Named grants, always sent by the users screen. Omitting the key leaves them as they are on
   * the server, so a client that predates a permission cannot revoke one by not knowing it.
   */
  permissions?: string[];
  /**
   * Always sent by the users tab, for every role: the branch the user works at. Omitting the key
   * leaves it unchanged on the server; null is refused — a branch may be changed, never removed.
   */
  branchId: string | null;
  /** "Sell untaxed at the till" on the account itself. Omitted leaves it as it is on the server. */
  sellsUntaxed?: boolean;
  /**
   * The account's whole set of keys on top of its role (AUTH-1 §17.10): what is not in it is taken away. When
   * sent, the server ignores `permissions` and `sellsUntaxed`.
   */
  additionalPermissions?: string[];
  /** The account's `authorizationVersion` as the screen opened it: a save on an older view is refused (`user_changed`). */
  authorizationVersion?: number;
}

// ---------- reports ----------

export interface AdminSale {
  id: string;
  reference: string;
  deviceCode: string;
  branchId: string;
  soldAtUtc: string;
  receivedAtUtc: string;
  cashierName: string;
  customerId: string | null;
  subTotalHt: number;
  taxTotal: number;
  discountTotal: number;
  grandTotalTtc: number;
  paidAmount: number;
  changeAmount: number;
  paymentMethod: string;
  status: string;
  stockWarning: boolean;
  lineCount: number;
}

export interface AdminSaleLine {
  id: string;
  productId: string;
  sku: string;
  nameSnapshot: string;
  qty: number;
  unitPrice: number;
  taxRate: number;
  lineDiscount: number;
  lineTotalTtc: number;
  lineTaxAmount: number;
}

export interface AdminSalePayment {
  id: string;
  method: string;
  amount: number;
  reference: string | null;
}

export interface AdminSaleDetail extends AdminSale {
  localSeq: number;
  cashierUserId: string;
  refundOfSaleId: string | null;
  note: string | null;
  lines: AdminSaleLine[];
  payments: AdminSalePayment[];
}

export interface TaxBucket {
  rate: number;
  baseHt: number;
  taxAmount: number;
}

export interface TopProduct {
  productId: string;
  name: string;
  qty: number;
  revenue: number;
}

export interface BranchSummary {
  branchId: string;
  branchName: string;
  saleCount: number;
  grossTtc: number;
}

export interface LowStockRow {
  productId: string;
  sku: string;
  nameFr: string;
  branchId: string;
  branchName: string;
  stockQty: number;
  reorderLevel: number;
}

/**
 * `GET /api/reports/missing-cost` (SAL-02): what sold while its cost was 0, by product, for days on the
 * company's clock. Refunds subtract. `salesHt` is every sale of the period before tax, to read the share against.
 */
/** What a hand change to a gift card's balance is (SAL-17): money in, money out, or a mistake put right. */
export type GiftCardAdjustKind = 'topUp' | 'cashOut' | 'correction';

export interface MissingCostReport {
  from: string;
  to: string;
  salesHt: number;
  uncostedHt: number;
  uncostedSales: number;
  productsWithoutCost: number;
  rows: MissingCostRow[];
}

/** One product sold with no cost. `costNow` is 0 while still missing; once set, `valueAtCostNow` estimates it (never posted). */
export interface MissingCostRow {
  productId: string;
  sku: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  qty: number;
  revenueHt: number;
  sales: number;
  lastSoldAtUtc: string;
  costNow: number;
  valueAtCostNow: number | null;
}

export interface SalesSummary {
  fromUtc: string;
  toUtc: string;
  saleCount: number;
  refundCount: number;
  grossTtc: number;
  netHt: number;
  taxTotal: number;
  discountTotal: number;
  refundTotal: number;
  /** Net cash rounding; absent from servers older than the rounding setting. */
  roundingTotal?: number;
  giftCardTotal?: number;
  loyaltyTotal?: number;
  taxBreakdown: TaxBucket[];
  topProducts: TopProduct[];
  branchBreakdown: BranchSummary[];
}

// ---------- treasury ----------

export interface AdminPaymentMethod {
  id: string;
  key: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  behavior: 'Cash' | 'Card' | 'Credit' | 'GiftCard' | 'Loyalty' | 'Generic';
  requiresConfirmation: boolean;
  affectsCashDrawer: boolean;
  requiresReference: boolean;
  requiresInstrumentDetails: boolean;
  isBuiltIn: boolean;
  isActive: boolean;
  sortOrder: number;
}

export interface UpsertPaymentMethodRequest {
  key: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  requiresConfirmation: boolean;
  requiresReference: boolean;
  requiresInstrumentDetails: boolean;
  isActive: boolean;
  sortOrder: number;
}

export interface PendingPayment {
  id: string;
  saleId: string;
  saleReference: string;
  soldAtUtc: string;
  customerId: string | null;
  customerName: string | null;
  methodKey: string;
  amount: number;
  status: 'Pending' | 'Confirmed' | 'Bounced';
  instrumentNumber: string | null;
  instrumentBank: string | null;
  instrumentDueDate: string | null;
  confirmedAtUtc: string | null;
  statusNote: string | null;
}

export interface AdminCashBox {
  id: string;
  branchId: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  isMain: boolean;
  balance: number;
}

export interface AdminCashMovement {
  id: string;
  cashBoxId: string;
  direction: 'In' | 'Out';
  amount: number;
  balanceAfter: number;
  kind: string;
  relatedCashBoxId: string | null;
  userName: string | null;
  reference: string | null;
  note: string | null;
  occurredAtUtc: string;
}

// ---------- stock states and assets ----------

export type StockState = 'Available' | 'OnRent' | 'Maintenance' | 'Scrap' | 'WrittenOff';

export interface ReceivingLine {
  productId: string;
  qty: number;
  /** Optional: a repeat delivery of something already priced need not restate these. */
  unitCost: number | null;
  assetUnitValue: number | null;
}

export interface SupplierOption {
  id: string;
  code: string;
  name: string;
  /** What the company owes the supplier. */
  balance: number;
}

export interface BranchAvailability {
  branchId: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  isMain: boolean;
  isWarehouse?: boolean;
  available: number;
  /** Null on another branch's row for a non-admin: availability is all they may see of it. */
  onRent: number | null;
  maintenance: number | null;
  scrap: number | null;
  owned: number | null;
}

export interface StockStateResult {
  available: number;
  onRent: number;
  maintenance: number;
  scrap: number;
  owned: number;
}

// ---------- opening balance from a stock sheet ----------

export interface OpeningImportColumn {
  /** 1 = column A. */
  index: number;
  letter: string;
  header: string;
}

export type OpeningImportMatch = 'existing' | 'new' | 'ambiguous';

export interface OpeningImportRow {
  /** The row's number in the sheet, as Excel shows it. */
  row: number;
  name: string;
  /** One per `columns` entry; null where the cell is not a number. */
  values: (number | null)[];
  match: OpeningImportMatch;
  productId: string | null;
  sku: string | null;
  productNameAr: string | null;
  productNameFr: string | null;
  productNameEn: string | null;
  duplicateOfRow: number | null;
}

export interface OpeningImportPreview {
  fileName: string;
  sheetName: string;
  sheetNames: string[];
  headerRow: number;
  nameColumn: string;
  nameHeader: string;
  columns: OpeningImportColumn[];
  /** The column whose header names the available count; null when none does. */
  quantityColumn: number | null;
  rows: OpeningImportRow[];
}

export interface OpeningImportRequest {
  branchId: string;
  lines: { name: string; qty: number; productId: string | null }[];
  note: string | null;
  newProductsRentable: boolean;
  skuPrefix: string | null;
}

export interface OpeningImportResult {
  branchId: string;
  documentId: string;
  lines: number;
  matched: number;
  created: number;
  value: number;
  createdProducts: { id: string; sku: string; nameAr: string }[];
}

@Injectable({ providedIn: 'root' })
export class AdminService {
  constructor(private api: ApiService) {}

  // ---------- store ----------
  getStore(): Promise<AdminStore> {
    return this.api.getJson<AdminStore>('/api/catalog/store');
  }
  updateStore(req: UpsertStoreRequest): Promise<AdminStore> {
    return this.api.putJson<AdminStore>('/api/catalog/store', req);
  }

  // ---------- gift cards ----------
  listGiftCards(q = ''): Promise<AdminGiftCard[]> {
    return this.api.getJson<AdminGiftCard[]>(`/api/catalog/gift-cards?q=${encodeURIComponent(q)}`);
  }
  /** SAL-17: a card is sold for cash at `branchId` (a non-admin's own branch, whatever is sent). */
  issueGiftCard(req: { code: string | null; amount: number; expiresAtUtc: string | null; customerId: string | null; branchId: string | null }): Promise<AdminGiftCard> {
    return this.api.postJson<AdminGiftCard>('/api/catalog/gift-cards', req);
  }
  updateGiftCard(id: string, req: { isActive: boolean; expiresAtUtc: string | null; customerId: string | null }): Promise<AdminGiftCard> {
    return this.api.putJson<AdminGiftCard>(`/api/catalog/gift-cards/${id}`, req);
  }
  /** SAL-17: a paid top-up (cash in), a cash refund (cash out) at `branchId`, or a correction (no money). */
  adjustGiftCard(id: string, req: { delta: number; reason: string; kind: GiftCardAdjustKind; branchId: string | null }): Promise<AdminGiftCard> {
    return this.api.postJson<AdminGiftCard>(`/api/catalog/gift-cards/${id}/adjust`, req);
  }
  giftCardMovements(id: string): Promise<GiftCardMovement[]> {
    return this.api.getJson<GiftCardMovement[]>(`/api/catalog/gift-cards/${id}/movements`);
  }

  // ---------- branches ----------
  listBranches(): Promise<AdminBranch[]> {
    return this.api.getJson<AdminBranch[]>('/api/catalog/branches');
  }
  createBranch(req: UpsertBranchRequest): Promise<AdminBranch> {
    return this.api.postJson<AdminBranch>('/api/catalog/branches', req);
  }
  updateBranch(id: string, req: UpsertBranchRequest): Promise<AdminBranch> {
    return this.api.putJson<AdminBranch>(`/api/catalog/branches/${id}`, req);
  }

  // ---------- devices (tills) ----------
  listDevices(): Promise<AdminDevice[]> {
    return this.api.getJson<AdminDevice[]>('/api/devices');
  }
  createDevice(req: UpsertDeviceRequest): Promise<AdminDevice> {
    return this.api.postJson<AdminDevice>('/api/devices', req);
  }
  updateDevice(id: string, req: UpsertDeviceRequest): Promise<AdminDevice> {
    return this.api.putJson<AdminDevice>(`/api/devices/${id}`, req);
  }

  // ---------- categories ----------
  listCategories(): Promise<AdminCategory[]> {
    return this.api.getJson<AdminCategory[]>('/api/catalog/categories');
  }
  createCategory(req: UpsertCategoryRequest): Promise<AdminCategory> {
    return this.api.postJson<AdminCategory>('/api/catalog/categories', req);
  }
  updateCategory(id: string, req: UpsertCategoryRequest): Promise<AdminCategory> {
    return this.api.putJson<AdminCategory>(`/api/catalog/categories/${id}`, req);
  }
  deleteCategory(id: string): Promise<void> {
    return this.api.deleteJson<void>(`/api/catalog/categories/${id}`);
  }

  // ---------- products ----------
  /** One page when `page` is given; without it, what the server hands a caller that does not page. */
  listProducts(q?: string, page?: { skip: number; take: number }): Promise<AdminProduct[]> {
    const params = new URLSearchParams();
    if (q) params.set('q', q);
    if (page) {
      params.set('skip', String(page.skip));
      params.set('take', String(page.take));
    }
    const query = params.toString();
    return this.api.getJson<AdminProduct[]>(`/api/catalog/products${query ? '?' + query : ''}`);
  }
  createProduct(req: UpsertProductRequest): Promise<AdminProduct> {
    return this.api.postJson<AdminProduct>('/api/catalog/products', req);
  }
  updateProduct(id: string, req: UpsertProductRequest): Promise<AdminProduct> {
    return this.api.putJson<AdminProduct>(`/api/catalog/products/${id}`, req);
  }
  deleteProduct(id: string): Promise<void> {
    return this.api.deleteJson<void>(`/api/catalog/products/${id}`);
  }
  getProductStock(id: string): Promise<ProductStockRow[]> {
    return this.api.getJson<ProductStockRow[]>(`/api/catalog/products/${id}/stock`);
  }
  /** Every article's quantity at one branch; a non-admin always reads their own branch. */
  stockLevels(branchId: string): Promise<{ productId: string; stockQty: number }[]> {
    return this.api.getJson<{ productId: string; stockQty: number }[]>(
      `/api/catalog/stock/levels?branchId=${encodeURIComponent(branchId)}`,
    );
  }
  adjustStock(id: string, branchId: string, qtyDelta: number, reason: string, reorderLevel?: number): Promise<void> {
    return this.api.postJson<void>(`/api/catalog/products/${id}/stock`, {
      branchId,
      qtyDelta,
      reason,
      reorderLevel: reorderLevel ?? null,
    });
  }
  transferStock(req: StockTransferRequest): Promise<StockTransfer> {
    // Dispatches a transfer (owner, 2026-10-12): the destination receives it on the transfers page.
    return this.api.postJson<StockTransfer>('/api/catalog/stock/transfers', req);
  }

  // ---------- customers ----------
  listCustomers(): Promise<AdminCustomer[]> {
    return this.api.getJson<AdminCustomer[]>('/api/catalog/customers');
  }
  /** One page of the customers list, searched and filtered on the server (owner, 2026-11-17). */
  customersPage(query: CustomersPageQuery): Promise<PageResult<AdminCustomer>> {
    return this.api.getJson<PageResult<AdminCustomer>>(`/api/catalog/customers/page${pageQueryString({ ...query })}`);
  }
  createCustomer(req: UpsertCustomerRequest): Promise<AdminCustomer> {
    return this.api.postJson<AdminCustomer>('/api/catalog/customers', req);
  }
  updateCustomer(id: string, req: UpsertCustomerRequest): Promise<AdminCustomer> {
    return this.api.putJson<AdminCustomer>(`/api/catalog/customers/${id}`, req);
  }
  deleteCustomer(id: string): Promise<void> {
    return this.api.deleteJson<void>(`/api/catalog/customers/${id}`);
  }
  /** Sets the opening balance left empty at registration — once (customers.opening_balance). */
  setOpeningBalance(customerId: string, req: OpeningBalanceRequest): Promise<AdminCustomer> {
    return this.api.postJson<AdminCustomer>(`/api/catalog/customers/${customerId}/opening-balance`, req);
  }
  /** Corrects a set opening balance, with its reason; the opening balance itself never changes. */
  correctOpeningBalance(customerId: string, req: OpeningCorrectionRequest): Promise<OpeningCorrection> {
    return this.api.postJson<OpeningCorrection>(`/api/catalog/customers/${customerId}/opening-corrections`, req);
  }
  /** The corrections of a customer's opening balance, oldest first. */
  listOpeningCorrections(customerId: string): Promise<OpeningCorrection[]> {
    return this.api.getJson<OpeningCorrection[]>(`/api/catalog/customers/${customerId}/opening-corrections`);
  }
  /** The whole history, newest first; the open row (`toUtc === null`) is the current one. */
  listRepresentatives(customerId: string): Promise<Representative[]> {
    return this.api.getJson<Representative[]>(`/api/catalog/customers/${customerId}/representatives`);
  }
  /** Ends the current representative and records the new one in one write; answers with the whole history. */
  changeRepresentative(customerId: string, req: ChangeRepresentativeRequest): Promise<Representative[]> {
    return this.api.postJson<Representative[]>(`/api/catalog/customers/${customerId}/representatives`, req);
  }
  /** The copies of the customer's identity document kept on file, oldest first; never the bytes. */
  listCustomerAttachments(customerId: string): Promise<CustomerAttachment[]> {
    return this.api.getJson<CustomerAttachment[]>(`/api/catalog/customers/${customerId}/attachments`);
  }
  /** One scan or photo up; the server reads its type from the file's own bytes. */
  uploadCustomerAttachment(customerId: string, file: File): Promise<CustomerAttachment> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.api.postForm<CustomerAttachment>(`/api/catalog/customers/${customerId}/attachments`, form);
  }
  /** The file itself, for viewing: it needs the session's token, which a plain link would not carry. */
  customerAttachmentBlob(customerId: string, attachmentId: string): Promise<Blob> {
    return this.api.getBlob(`/api/catalog/customers/${customerId}/attachments/${attachmentId}`);
  }
  removeCustomerAttachment(customerId: string, attachmentId: string): Promise<void> {
    return this.api.deleteJson<void>(`/api/catalog/customers/${customerId}/attachments/${attachmentId}`);
  }

  // ---------- users ----------
  listUsers(): Promise<AdminUser[]> {
    return this.api.getJson<AdminUser[]>('/api/users');
  }
  createUser(req: CreateUserRequest): Promise<AdminUser> {
    return this.api.postJson<AdminUser>('/api/users', req);
  }
  updateUser(id: string, req: UpdateUserRequest): Promise<AdminUser> {
    return this.api.putJson<AdminUser>(`/api/users/${id}`, req);
  }
  /** Links an account from before the rule to its employee; how it signs in does not change. */
  linkUserEmployee(id: string, employeeId: string): Promise<AdminUser> {
    return this.api.postJson<AdminUser>(`/api/users/${id}/employee`, { employeeId });
  }
  resetPassword(id: string, value: string): Promise<void> {
    return this.api.postJson<void>(`/api/users/${id}/password`, { value });
  }
  revokeSessions(id: string): Promise<void> {
    return this.api.postJson<void>(`/api/users/${id}/revoke-sessions`, {});
  }
  /** Turns off a user's two-step login (owner, 2026-11-11): never on, and the key is never seen. */
  resetTwoFactor(id: string): Promise<void> {
    return this.api.postJson<void>(`/api/users/${id}/two-factor/reset`, {});
  }
  resetPin(id: string, value: string): Promise<void> {
    return this.api.postJson<void>(`/api/users/${id}/pin`, { value });
  }
  deactivateUser(id: string): Promise<void> {
    return this.api.deleteJson<void>(`/api/users/${id}`);
  }

  // ---------- roles and permissions (AUTH-1 Step 5) ----------
  listPermissions(): Promise<PermissionModule[]> {
    return this.api.getJson<PermissionModule[]>('/api/permissions');
  }
  listRoles(): Promise<AdminRoleRow[]> {
    return this.api.getJson<AdminRoleRow[]>('/api/roles');
  }
  roleMembers(id: string): Promise<RoleMember[]> {
    return this.api.getJson<RoleMember[]>(`/api/roles/${id}/members`);
  }
  createRole(req: UpsertRoleRequest): Promise<AdminRoleRow> {
    return this.api.postJson<AdminRoleRow>('/api/roles', req);
  }
  updateRole(id: string, req: UpsertRoleRequest): Promise<AdminRoleRow> {
    return this.api.putJson<AdminRoleRow>(`/api/roles/${id}`, req);
  }
  deleteRole(id: string): Promise<void> {
    return this.api.deleteJson<void>(`/api/roles/${id}`);
  }

  // ---------- reports ----------
  getSalesSummary(fromUtc: string, toUtc: string, branchId?: string | null): Promise<SalesSummary> {
    const params = new URLSearchParams({ from: fromUtc, to: toUtc });
    if (branchId) params.set('branchId', branchId);
    return this.api.getJson<SalesSummary>(`/api/reports/summary?${params.toString()}`);
  }
  listSales(options: { fromUtc?: string; toUtc?: string; branchId?: string | null; take?: number }): Promise<AdminSale[]> {
    const params = new URLSearchParams();
    if (options.fromUtc) params.set('from', options.fromUtc);
    if (options.toUtc) params.set('to', options.toUtc);
    if (options.branchId) params.set('branchId', options.branchId);
    params.set('take', String(options.take ?? 100));
    return this.api.getJson<AdminSale[]>(`/api/sales?${params.toString()}`);
  }
  getSale(id: string): Promise<AdminSaleDetail> {
    return this.api.getJson<AdminSaleDetail>(`/api/sales/${id}`);
  }
  /** SAL-02. Days as `yyyy-MM-dd` on the company's clock; none asks for the current month. */
  getMissingCost(filter: { from?: string | null; to?: string | null; branchId?: string | null }): Promise<MissingCostReport> {
    const params = new URLSearchParams();
    if (filter.from) params.set('from', filter.from);
    if (filter.to) params.set('to', filter.to);
    if (filter.branchId) params.set('branchId', filter.branchId);
    const query = params.toString();
    return this.api.getJson<MissingCostReport>(`/api/reports/missing-cost${query ? `?${query}` : ''}`);
  }

  getLowStock(branchId?: string | null): Promise<LowStockRow[]> {
    const query = branchId ? `?branchId=${encodeURIComponent(branchId)}` : '';
    return this.api.getJson<LowStockRow[]>(`/api/catalog/products/low-stock${query}`);
  }

  // ---------- treasury ----------
  listPaymentMethods(): Promise<AdminPaymentMethod[]> {
    return this.api.getJson<AdminPaymentMethod[]>('/api/treasury/payment-methods');
  }
  createPaymentMethod(req: UpsertPaymentMethodRequest): Promise<AdminPaymentMethod> {
    return this.api.postJson<AdminPaymentMethod>('/api/treasury/payment-methods', req);
  }
  updatePaymentMethod(id: string, req: UpsertPaymentMethodRequest): Promise<AdminPaymentMethod> {
    return this.api.putJson<AdminPaymentMethod>(`/api/treasury/payment-methods/${id}`, req);
  }
  deletePaymentMethod(id: string): Promise<void> {
    return this.api.deleteJson<void>(`/api/treasury/payment-methods/${id}`);
  }

  /** `branchId` narrows an Admin's list to one branch; without it the call is what it always was. */
  listPendingPayments(status: 'Pending' | 'Confirmed' | 'Bounced' = 'Pending', branchId?: string | null): Promise<PendingPayment[]> {
    const branch = branchId ? `&branchId=${encodeURIComponent(branchId)}` : '';
    return this.api.getJson<PendingPayment[]>(`/api/treasury/pending-payments?status=${status}${branch}`);
  }
  settlePayment(id: string, status: 'Confirmed' | 'Bounced', note: string | null): Promise<unknown> {
    return this.api.postJson(`/api/treasury/pending-payments/${id}/settle`, { status, note });
  }

  /** `branchId` narrows an Admin's list to one branch (and the main boxes); without it, as before. */
  listCashBoxes(branchId?: string | null): Promise<AdminCashBox[]> {
    const query = branchId ? `?branchId=${encodeURIComponent(branchId)}` : '';
    return this.api.getJson<AdminCashBox[]>(`/api/treasury/cash-boxes${query}`);
  }
  listCashMovements(cashBoxId: string): Promise<AdminCashMovement[]> {
    return this.api.getJson<AdminCashMovement[]>(`/api/treasury/cash-boxes/${cashBoxId}/movements`);
  }
  remit(req: { fromCashBoxId: string; toCashBoxId: string; amount: number; reference: string | null; note: string | null }): Promise<{ warning: string | null }> {
    return this.api.postJson<{ warning: string | null }>('/api/treasury/remittances', req);
  }


  // ---------- suppliers ----------
  listSuppliers(): Promise<SupplierOption[]> {
    return this.api.getJson<SupplierOption[]>('/api/suppliers');
  }

  // ---------- stock ----------
  receiveStock(
    lines: ReceivingLine[],
    reference: string | null,
    supplierId: string | null = null,
    branchId: string | null = null,
  ): Promise<{ received: number; branchId: string }> {
    return this.api.postJson('/api/catalog/stock/receiving', { lines, reference, supplierId, branchId, note: null });
  }
  openingBalance(branchId: string, lines: { productId: string; qty: number }[]): Promise<unknown> {
    return this.api.postJson('/api/catalog/stock/opening', { branchId, lines, note: null });
  }
  /** Reads a branch's stock sheet and says what each line would become; writes nothing. */
  previewOpeningImport(file: File, sheet: string | null): Promise<OpeningImportPreview> {
    const form = new FormData();
    form.append('file', file, file.name);
    if (sheet) form.append('sheet', sheet);
    return this.api.postForm<OpeningImportPreview>('/api/catalog/stock/opening/import/preview', form);
  }
  /** Creates what the catalogue lacks and sets the branch's opening quantities, in one go. */
  importOpening(req: OpeningImportRequest): Promise<OpeningImportResult> {
    return this.api.postJson<OpeningImportResult>('/api/catalog/stock/opening/import', req);
  }
  openBranchForOperation(branchId: string): Promise<unknown> {
    return this.api.postJson(`/api/catalog/stock/branches/${branchId}/open`, {});
  }
  changeStockState(req: {
    productId: string;
    branchId: string;
    qty: number;
    from: StockState;
    to: StockState;
    reason: string | null;
  }): Promise<StockStateResult> {
    return this.api.postJson<StockStateResult>('/api/catalog/stock/state', req);
  }
  /** Behind a named permission; a cashier or an ungranted manager gets a 403. */
  availability(productId: string): Promise<BranchAvailability[]> {
    return this.api.getJson<BranchAvailability[]>(`/api/catalog/stock/availability/${productId}`);
  }

}
