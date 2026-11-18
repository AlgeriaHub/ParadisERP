import type { PhoneNumberRules } from './phone';

/** Languages whose translations ship with the app. */
export type BuiltinLang = 'ar' | 'fr' | 'en';
/** Any enabled UI language: a built-in one or an operator-uploaded pack (e.g. 'tzm'). */
export type Lang = string;
export type PaymentMethod = 'Cash' | 'Card' | 'Credit' | 'Mixed' | 'GiftCard' | 'Loyalty';
export type SaleStatus = 'Completed' | 'Refunded';
export type OutboxState = 'pending' | 'sending' | 'synced' | 'rejected';

/** Rows replicated from the server. `version` is the delta-sync cursor value. */
export interface SyncRow {
  id: string;
  version: number;
  isDeleted?: boolean;
}

export interface Store extends SyncRow {
  nameAr: string;
  nameFr: string;
  nameEn: string;
  address: string;
  addressAr?: string;
  addressFr?: string;
  addressEn?: string;
  phone: string;
  /** Values keyed by the country profile's fiscal identifier keys. */
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
  loyaltyEnabled?: boolean;
  loyaltyPointsPerUnit?: number;
  loyaltyPointValue?: number;
  loyaltyMinRedeemPoints?: number;
  /** The company's interface face (a `UI_FONTS` key); null or absent follows the country profile. */
  uiFont?: string | null;
}

/** Prepaid card replicated from the server; the balance is server-owned. */
export interface GiftCard extends SyncRow {
  /** Only a hint for the receipt ("…7KQ2"): a till never holds the code itself (security review, 2026-10-10). */
  code: string;
  /** SHA-256 of the normalised code, hex — how a till finds the card a customer hands over. */
  codeHash?: string;
  initialAmount: number;
  balance: number;
  isActive: boolean;
  expiresAtUtc: string | null;
  customerId: string | null;
}

export type LocalizedText = Record<Lang, string | undefined>;

export interface TaxRateLabel {
  rate: number;
  labels: LocalizedText;
}

export type CashRoundingMode = 'Nearest' | 'Up' | 'Down';
export type ReceiptSection =
  | 'header' | 'fiscalIds' | 'meta' | 'lines' | 'totals' | 'taxBreakdown' | 'payment' | 'legal' | 'footer';

/** Operator-uploaded translations for a language the app does not ship with. */
export interface LanguagePack {
  code: string;
  label: string;
  rtl: boolean;
  fallbackLanguage: BuiltinLang;
  strings: Record<string, string>;
  /** Local bookkeeping: the server revision this copy came from. */
  revision?: number;
}

export interface FiscalIdDefinition {
  key: string;
  /** Short code printed on receipts, e.g. "NIF". */
  code: string;
  labels: LocalizedText;
  required: boolean;
  showOnReceipt: boolean;
}

/** Operator-owned country profile, delivered with login and every pull. */
export interface RegionalSettings {
  countryCode: string;
  currencyCode: string;
  currencyDecimals: number;
  currencySymbols: LocalizedText;
  /** Unit names for the amount in words, per language; a language with none prints figures only. */
  currencyUnitNames?: Record<Lang, { major: string; minor: string } | undefined>;
  currencySymbolFirst: boolean;
  /** How figures are written (core/number-format.ts): 1,234.56 (the default), 1.234,56, 1 234,56 or each language's own. */
  numberFormat?: string;
  timeZone: string;
  languages: Lang[];
  defaultLanguage: Lang;
  taxNames: LocalizedText;
  taxRates: number[];
  taxRateLabels: TaxRateLabel[];
  defaultTaxRate: number;
  pricesIncludeTax: boolean;
  /**
   * Whether a document prints its tax line when the tax is zero. Separate from whether tax
   * applies: a business may want an untaxed invoice to say "tax 0.00" rather than say nothing,
   * so the customer can see it was considered rather than forgotten.
   */
  showZeroTax?: boolean;
  fiscalIds: FiscalIdDefinition[];
  customerTaxIdLabels: LocalizedText;
  cashDenominations: number[];
  /** 0 = no rounding; otherwise cash amounts due are rounded to a multiple of this. */
  cashRoundingIncrement: number;
  cashRoundingMode: CashRoundingMode;
  paymentMethods: PaymentMethod[];
  receiptSections: ReceiptSection[];
  receiptLegalText: LocalizedText;
  /** Interface face key; see UI_FONTS. Screen only — the receipt never follows it. */
  uiFont: string;
  /** How a customer's phone is read and stored (core/phone.ts). Absent from a server older than 2026-11-17. */
  phone?: PhoneNumberRules;
}

/**
 * A physical location, of one of two kinds (owner, 2026-10-05): a selling branch, or a warehouse
 * (`isWarehouse`) that holds stock and never sells. `isMain` marks the main one of its kind —
 * exactly one main branch (the head office, the central safe) and one main warehouse.
 */
export interface Branch extends SyncRow {
  nameAr: string;
  nameFr: string;
  nameEn: string;
  address: string;
  addressAr?: string;
  addressFr?: string;
  addressEn?: string;
  phone: string;
  isMain: boolean;
  isWarehouse?: boolean;
  allowNegativeStock: boolean;
  isActive: boolean;
  /**
   * The branch's own fiscal identifiers, keyed like the store's; a key absent prints the main
   * branch's, then the company's (core/document-issuer.ts). Absent from an older server's pull.
   */
  fiscalIds?: Record<string, string> | null;
  /**
   * The signed-in link to the branch's logo (`/api/catalog/branches/{id}/logo?v=…`); null falls back
   * (core/branch-face.ts). The file itself is kept by SecureImageService, so a till draws it offline.
   */
  logoUrl?: string | null;
}

export interface Category extends SyncRow {
  nameAr: string;
  nameFr: string;
  nameEn: string;
  color: string;
  sortOrder: number;
}

export interface Product extends SyncRow {
  sku: string;
  barcode: string | null;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  categoryId: string | null;
  /** Shelf price. Tax-inclusive when the country profile's pricesIncludeTax is true. */
  price: number;
  /** Tax percentage, one of the country profile's rates. */
  taxRate: number;
  unit: string;
  trackStock: boolean;
  /**
   * Server figure at last pull for THIS terminal's own branch only (resolved server-side
   * from the device code), adjusted locally by offline sales in between — an estimate, never
   * another branch's stock.
   */
  stockQty: number;
  reorderLevel: number;
  isActive: boolean;
  /** Carried for older tills; every item is rented and sold since 2026-10-06, so nothing reads it. */
  isRentable?: boolean;
  /**
   * The workshop's, not the counter's (manufacturing, Phase 1): a raw material, a consumable or a
   * semi-finished good. The till leaves it out of its grid, its search and its scanner (`soldAtTill`).
   * The server decides it from the product's role; a row cached before the flag existed has none,
   * which reads as shown until its next pull.
   */
  hiddenAtTill?: boolean;
}

/** Whether the till offers this product — the one filter its grid, search and scanner share. */
export function soldAtTill(product: Pick<Product, 'hiddenAtTill'>): boolean {
  return !product.hiddenAtTill;
}

export interface Customer extends SyncRow {
  code: string;
  fullName: string;
  phone: string | null;
  address: string | null;
  taxId: string | null;
  email: string | null;
  /** ISO date string (yyyy-MM-dd), date part only. */
  birthDate: string | null;
  creditLimit: number;
  balance: number;
  /** Server figure at last pull, adjusted locally by offline sales in between. */
  loyaltyPoints?: number;
}

/** On which data a role's members act (Domain/RoleScope): every branch, their own branch, or the till's. */
export type RoleScope = 'Company' | 'AssignedBranch' | 'TillBranch';

export interface PosUser extends SyncRow {
  username: string;
  fullName: string;
  role: 'Cashier' | 'Manager' | 'Admin';
  /** BCrypt hash of the POS unlock PIN — lets a cashier sign in with no network. */
  pinHash: string;
  isActive: boolean;
  /**
   * The branch this user works at, as the login answered it (assigned, or the main branch for a
   * legacy row). Absent on a row that only came from a pull. See core/branch-lock.ts.
   */
  branchId?: string | null;
  /** True for everyone but the Admin: no branch choice on any screen. */
  branchLocked?: boolean;
  /** The named permissions the login answered (see LoginResponse.user.permissions). */
  permissions?: string[];
  /** What the server's own decision lets this user do (see LoginResponse.user.capabilities). */
  capabilities?: string[];
  /**
   * Every permission key the server's decision gives them, as their last online sign-in here answered it
   * (AUTH-1 Step 6): the screens decide what to offer from this. Absent on a session from before — then the
   * screens read the role as they always did (core/landing.ts).
   */
  permissionKeys?: string[];
  /** The role that decides their access, its name and its scope (LoginResponse.user). Absent like the keys. */
  roleId?: string | null;
  roleName?: string | null;
  scope?: RoleScope | null;
  /**
   * Whether this user may leave a till sale untaxed (`sales.untaxed`), as their last online sign-in on
   * this browser said (core/untaxed-sale.ts). Absent when no sign-in since it existed has said: read as
   * allowed, as every seller was before (owner, 2026-11-02).
   */
  sellsUntaxed?: boolean;
}

export interface SaleLine {
  id: string;
  productId: string;
  sku: string;
  nameSnapshot: string;
  qty: number;
  unitPrice: number;
  taxRate: number;
  /** Share of the order discount carried by this line. */
  lineDiscount: number;
  /** Net of lineDiscount. Sum of these equals grandTotalTtc. */
  lineTotalTtc: number;
  lineTaxAmount: number;
}

export interface SalePaymentPart {
  id: string;
  method: PaymentMethod;
  amount: number;
  reference: string | null;
  /**
   * The tender definition this was taken under. Operator-defined tenders (a postal transfer,
   * a cheque) travel by key because no built-in name describes them; `method` then carries
   * 'Mixed' so older readers still see something sensible.
   */
  methodKey?: string;
  /** Cheque or transfer number, the issuing bank, and when it falls due. */
  instrumentNumber?: string | null;
  instrumentBank?: string | null;
  instrumentDueDate?: string | null;
}

/**
 * A tender the company can take, as configured by the operator. Replicated to the till so
 * the payment screen is right while offline.
 */
export interface PaymentMethodDef {
  id: string;
  key: string;
  nameAr: string;
  nameFr: string;
  nameEn: string;
  behavior: 'Cash' | 'Card' | 'Credit' | 'GiftCard' | 'Loyalty' | 'Generic';
  /** False only for cash and card: everything else is money in transit until confirmed. */
  requiresConfirmation: boolean;
  affectsCashDrawer: boolean;
  requiresReference: boolean;
  requiresInstrumentDetails: boolean;
  isBuiltIn: boolean;
  isActive: boolean;
  sortOrder: number;
  version: number;
  isDeleted: boolean;
}

/** Whether tax applies to a document. Chosen per invoice, because the customer may not need it. */
export type TaxMode = 'Taxed' | 'Untaxed';

/** A completed sale. Immutable and append-only once written. */
export interface Sale {
  id: string;
  reference: string;
  localSeq: number;
  soldAtUtc: string;
  customerId: string | null;
  cashierUserId: string;
  cashierName: string;
  subTotalHt: number;
  taxTotal: number;
  discountTotal: number;
  grandTotalTtc: number;
  /** Cash rounding: amount due = grandTotalTtc + this. Missing on sales made before rounding existed. */
  roundingAdjustment?: number;
  paidAmount: number;
  changeAmount: number;
  paymentMethod: PaymentMethod;
  /** Absent on sales made before the choice existed, which were all taxed. */
  taxMode?: TaxMode;
  status: SaleStatus;
  refundOfSaleId: string | null;
  note: string | null;
  lines: SaleLine[];
  payments: SalePaymentPart[];

  // local-only bookkeeping
  outboxState: OutboxState;
  attempts: number;
  lastError: string | null;
  /** The server's rejection code, when it sent one — see PushResultItem.code. */
  lastErrorCode?: string | null;
  syncedAtUtc: string | null;
}

/** Operator-configured integrations, as much as a till needs to know. */
export interface IntegrationsPublic {
  cardMode: 'None' | 'ExternalTerminal';
  requireCardReference: boolean;
  smsReceipts: boolean;
  emailReceipts: boolean;
}

export interface PullResponse {
  integrations?: IntegrationsPublic;
  subscription?: SubscriptionAccess;
  regional?: RegionalSettings;
  languagePacks?: Record<string, number>;
  cursor: number;
  /** True when the server truncated the batch — keep pulling from the returned cursor. */
  hasMore: boolean;
  serverTimeUtc: string;
  store: Store | null;
  branches: Branch[];
  categories: Category[];
  products: Product[];
  customers: Customer[];
  users: PosUser[];
  giftCards?: GiftCard[];
  paymentMethods?: PaymentMethodDef[];
}

export interface PushResultItem {
  id: string;
  status: 'accepted' | 'duplicate' | 'rejected';
  message: string | null;
  /**
   * Why a sale was rejected, as a stable code the till translates
   * (duplicate_receipt_number | concurrent_update | server_error). Absent from an older
   * server during a rolling upgrade, in which case `message` is shown as it came.
   */
  code?: string | null;
}

export interface PushResponse {
  cursor: number;
  serverTimeUtc: string;
  accepted: number;
  duplicates: number;
  rejected: number;
  results: PushResultItem[];
}

export interface LoginResponse {
  integrations?: IntegrationsPublic;
  subscription?: SubscriptionAccess;
  regional?: RegionalSettings;
  languagePacks?: Record<string, number>;
  token: string;
  expiresAtUtc: string;
  /** The branch of the terminal the session was opened on. */
  deviceBranchId?: string | null;
  /** The highest receipt sequence the server holds for that till; the counter continues after it. */
  lastLocalSeq?: number;
  /** Two-step login: the "trust this computer" token, when the sign-in asked to be remembered (30 days). */
  twoFactorTrust?: string | null;
  user: {
    id: string;
    username: string;
    fullName: string;
    role: string;
    branchId?: string | null;
    branchLocked?: boolean;
    /** What the user holds, grants and the Admin's own; decides what a screen offers, never what the server allows. */
    permissions?: string[];
    /**
     * Derived by the server, never granted (Domain/Capabilities.cs): a screen offers an action by
     * reading this, not by naming a role. Absent from an older server's answer — read as none.
     */
    capabilities?: string[];
    /** Every key the server's decision gives (AUTH-1 Step 6). Absent from an older server's answer. */
    permissionKeys?: string[];
    roleId?: string | null;
    roleName?: string | null;
    scope?: RoleScope | null;
  };
  deviceCode: string;
}

/** Single-row local settings blob. */
export interface DeviceMeta {
  integrations?: IntegrationsPublic;
  subscription?: SubscriptionAccess;
  regional?: RegionalSettings;
  key: 'device';
  /**
   * The terminal the current session was opened on. Empty for an office session — signed in with
   * no till code, to work in the back office from a computer that is not a till (2026-10-05).
   */
  deviceCode: string;
  /**
   * Wrong PINs per cashier on this till, and until when their offline unlock is closed (security
   * review, 2026-10-10): a 4-digit PIN is otherwise a few minutes of guessing at the counter.
   */
  pinLock?: Record<string, { failures: number; untilUtc: string | null }>;
  /**
   * Two-step login (owner, 2026-11-11): the "trust this computer" token each account earned here, by
   * the sign-in name in lower case. It only stands in for the code; the password is always asked.
   */
  twoFactorTrust?: Record<string, string>;
  /** The till code this browser last signed in with successfully; what the login form offers. */
  lastTillCode?: string;
  cursor: number;
  lastSyncAtUtc: string | null;
  lastSyncError: string | null;
  localSeq: number;
  lang: Lang;
  token: string | null;
  tokenExpiresAtUtc: string | null;
  currentUserId: string | null;
  apiBaseUrl: string;
  /**
   * The branch the last online sign-in answered for that user. Kept here rather than only on the
   * user row because a pull rewrites a cashier's row without it.
   */
  sessionBranch?: {
    userId: string;
    branchId: string | null;
    locked: boolean;
    permissions?: string[];
    /** Absent on a session stored before capabilities existed: read as none, not as an error. */
    capabilities?: string[];
    /** Absent on a session stored before AUTH-1 Step 6: the screens then read the role. */
    permissionKeys?: string[];
    roleId?: string | null;
    roleName?: string | null;
    scope?: RoleScope | null;
  } | null;
  /** The branch of this terminal, as the last online sign-in answered it; the till's top bar shows it. */
  deviceBranchId?: string | null;
  /**
   * Per user, whether their last online sign-in here let them sell untaxed (`sales.untaxed`). Kept here,
   * not on the user row, because a pull rewrites a cashier's row without their permissions, and a PIN
   * unlock offline still has to know (owner, 2026-11-02).
   */
  untaxedByUser?: Record<string, boolean>;
}

export interface SubscriptionAccess {
  plan: string;
  status: 'Unrestricted' | 'Trial' | 'Active' | 'Grace' | 'Expired' | 'Suspended' | 'Cancelled';
  canOperate: boolean;
  accessUntilUtc: string | null;
  maxUsers: number | null;
  maxBranches: number | null;
  maxDevices: number | null;
  checkedAtUtc: string;
}
