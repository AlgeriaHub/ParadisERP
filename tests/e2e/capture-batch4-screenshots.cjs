// Captures Batch 4 screenshots for the Phase 4 redesign review: the till (point of sale, #/register).
//
//   SHOT_SET=before WEB_DIST=ParadiERP.WebUI/dist/before/browser node tests/e2e/capture-batch4-screenshots.cjs
//   SHOT_SET=after node tests/e2e/capture-batch4-screenshots.cjs
//
// Serves an already-built bundle (WEB_DIST, default ParadiERP.WebUI/dist/paradierp-web-ui/browser;
// a relative path is read against the repo root) on 127.0.0.1:5214, answers every /api call from the
// mocks below, signs in through #/login as a cashier whose till is C01 (a till session, not an office
// one) and writes PNGs to artifacts/ui-redesign-checkpoint/batch4/<SHOT_SET>. It never builds anything.
//
// The mocks describe the Batch 2/3 company (an Algerian construction-equipment rental and supply
// business) seen from its Algiers counter till C01: twelve articles (consumables sold by the bag, the
// bar or the m³, and equipment that is also rented), three customers, four tenders (cash, card, and the
// operator-defined cheque and bank transfer) and the Algeria preset (DZD, TVA 19 % / 9 %, Africa/Algiers,
// quick-tender notes 100 to 5000). Every shape follows core/models.ts (LoginResponse, PullResponse,
// Product, Customer, PaymentMethodDef, Store, Branch).
//
// The till keeps its catalogue and cart in IndexedDB, so each run (one language at one size) gets its
// own browser context and starts from an empty database. A run walks one sale: the empty till, the
// cart, the payment dialog, then the same dialog split between cash and a cheque. The till is shot full
// page except on the phone (see RUNS); the dialogs as the viewport shows them. A step that cannot
// be completed is logged and captured anyway; nothing stops the run. At the end the script lists every
// /api path that fell through to the default mock.

const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');

const root = path.resolve(__dirname, '../..');
const SHOT_SET = (process.env.SHOT_SET || 'after').trim() || 'after';
if (!/^[\w.-]+$/.test(SHOT_SET)) {
  console.error(`SHOT_SET must be a plain folder name, got "${SHOT_SET}".`);
  process.exit(2);
}
const out = path.join(root, 'artifacts/ui-redesign-checkpoint/batch4', SHOT_SET);
const webDist = path.resolve(root, process.env.WEB_DIST || 'ParadiERP.WebUI/dist/paradierp-web-ui/browser');

const PORT = 5214;
const BASE = `http://127.0.0.1:${PORT}`;
const DESKTOP = { width: 1440, height: 960 };
const TABLET = { width: 1024, height: 768 };
const PHONE = { width: 390, height: 844 };
const READY_TIMEOUT = 15_000;

// =====================================================================================
// the company, from till C01
// =====================================================================================

// The Algeria preset, as the server sends it with the sign-in and every pull (core/models.ts).
// The enabled tenders name the two operator-defined ones by key, as RegionalSettings keeps them.
const regional = {
  countryCode: 'DZ',
  currencyCode: 'DZD',
  currencyDecimals: 2,
  currencySymbols: { ar: 'دج', fr: 'DA', en: 'DA' },
  currencyUnitNames: { ar: { major: 'دينار', minor: 'سنتيم' }, fr: { major: 'dinar', minor: 'centime' }, en: { major: 'dinar', minor: 'centime' } },
  currencySymbolFirst: false,
  timeZone: 'Africa/Algiers',
  languages: ['fr', 'ar', 'en'],
  defaultLanguage: 'fr',
  taxNames: { ar: 'ض.م', fr: 'TVA', en: 'VAT' },
  taxRates: [19, 9, 0],
  taxRateLabels: [
    { rate: 19, labels: { ar: 'النسبة العادية', fr: 'Taux normal', en: 'Standard rate' } },
    { rate: 9, labels: { ar: 'النسبة المخفضة', fr: 'Taux réduit', en: 'Reduced rate' } },
    { rate: 0, labels: { ar: 'معفى', fr: 'Exonéré', en: 'Exempt' } },
  ],
  defaultTaxRate: 19,
  showZeroTax: false,
  pricesIncludeTax: false,
  fiscalIds: [
    { key: 'nif', code: 'NIF', labels: { ar: 'الرقم الجبائي', fr: "Numéro d'identification fiscale (NIF)", en: 'Tax ID (NIF)' }, required: false, showOnReceipt: true },
    { key: 'nis', code: 'NIS', labels: { ar: 'الرقم الإحصائي', fr: "Numéro d'identification statistique (NIS)", en: 'Statistical ID (NIS)' }, required: false, showOnReceipt: true },
    { key: 'rc', code: 'RC', labels: { ar: 'السجل التجاري', fr: 'Registre du commerce (RC)', en: 'Trade register (RC)' }, required: false, showOnReceipt: true },
    { key: 'ai', code: 'AI', labels: { ar: 'رقم المادة الجبائية', fr: "Article d'imposition (AI)", en: 'Tax article (AI)' }, required: false, showOnReceipt: true },
  ],
  customerTaxIdLabels: { ar: 'الرقم الجبائي', fr: 'NIF', en: 'Tax ID (NIF)' },
  cashDenominations: [100, 200, 500, 1000, 2000, 5000],
  cashRoundingIncrement: 0,
  cashRoundingMode: 'Nearest',
  paymentMethods: ['Cash', 'Card', 'cheque', 'transfer'],
  receiptSections: ['header', 'fiscalIds', 'meta', 'lines', 'totals', 'taxBreakdown', 'payment', 'legal', 'footer'],
  receiptLegalText: {},
  uiFont: 'ibm-plex-sans-arabic',
};

// The owner owns the installation: no plan, no expiry.
const subscription = {
  plan: 'Owner',
  status: 'Unrestricted',
  canOperate: true,
  accessUntilUtc: null,
  maxUsers: null,
  maxBranches: null,
  maxDevices: null,
  checkedAtUtc: '2026-10-15T07:00:00Z',
};

// A card terminal beside the till, no approval number asked for, no receipts sent.
const integrations = { cardMode: 'ExternalTerminal', requireCardReference: false, smsReceipts: false, emailReceipts: false };

// The cashier of till C01. The server puts a cashier on their own till, so the sign-in sends no code
// and the answer carries C01. `customers.create` lets the till offer "new customer" (core/customer-access.ts).
const cashier = {
  id: 'u-c01',
  username: 's.boudiaf',
  fullName: 'Samir Boudiaf',
  role: 'Cashier',
  branchId: 'b-main',
  branchLocked: true,
  permissions: [],
  capabilities: ['customers.create'],
};
const TILL_CODE = 'C01';

// Branch (core/models.ts), the sync row. Same ids and names as Batches 2 and 3.
const branches = [
  {
    id: 'b-main',
    code: '16',
    nameAr: 'فرع الجزائر الوسطى',
    nameFr: 'Succursale Alger Centre',
    nameEn: 'Algiers Centre branch',
    address: 'Zone industrielle Oued Smar, lot 42, Alger',
    addressAr: 'المنطقة الصناعية واد السمار، القطعة 42، الجزائر',
    addressFr: 'Zone industrielle Oued Smar, lot 42, Alger',
    addressEn: 'Oued Smar industrial zone, lot 42, Algiers',
    phone: '023 92 45 10',
    isMain: true,
    isWarehouse: false,
    allowNegativeStock: false,
    isActive: true,
    logoUrl: null,
    version: 6,
  },
  {
    id: 'b-oran',
    code: '31',
    nameAr: 'فرع وهران',
    nameFr: 'Succursale Oran',
    nameEn: 'Oran branch',
    address: 'Route de Es Senia, cité El Menzah, Oran',
    addressAr: 'طريق السانية، حي المنزه، وهران',
    addressFr: 'Route de Es Senia, cité El Menzah, Oran',
    addressEn: 'Es Senia road, El Menzah, Oran',
    phone: '041 58 22 07',
    isMain: false,
    isWarehouse: false,
    allowNegativeStock: false,
    isActive: true,
    logoUrl: null,
    version: 4,
  },
];

// Store (core/models.ts).
const store = {
  id: 'store-1',
  nameAr: 'شركة الأطلس لتأجير معدات البناء',
  nameFr: 'SARL Atlas Location Matériel BTP',
  nameEn: 'Atlas Construction Equipment Rental',
  address: 'Zone industrielle Oued Smar, lot 42, Alger',
  addressAr: 'المنطقة الصناعية واد السمار، القطعة 42، الجزائر',
  addressFr: 'Zone industrielle Oued Smar, lot 42, Alger',
  addressEn: 'Oued Smar industrial zone, lot 42, Algiers',
  phone: '023 92 45 10',
  fiscalIds: { nif: '001216098765432', nis: '001216010045678', rc: '16/00-0987654B12', ai: '16012345678' },
  receiptFooter: 'Merci de votre confiance — matériel vérifié avant chaque sortie.',
  receiptFooterAr: 'شكراً لثقتكم — يُفحص العتاد قبل كل خروج.',
  receiptFooterFr: 'Merci de votre confiance — matériel vérifié avant chaque sortie.',
  receiptFooterEn: 'Thank you for your trust — equipment checked before every hire.',
  requireCustomerEmail: false,
  requireCustomerBirthDate: false,
  requireCustomerPhone: true,
  requireCustomerAddress: true,
  requireCustomerTaxId: false,
  loyaltyEnabled: false,
  loyaltyPointsPerUnit: 0,
  loyaltyPointValue: 0,
  loyaltyMinRedeemPoints: 0,
  uiFont: null,
  version: 7,
};

// Product (core/models.ts). Prices are before tax (pricesIncludeTax: false); stock is the Algiers
// branch's. The grid sorts by SKU, so the consumables come first and the rental equipment after.
function product(n, sku, barcode, nameFr, nameAr, nameEn, price, taxRate, unit, stockQty, reorderLevel, isRentable) {
  return {
    id: `p-${String(n).padStart(2, '0')}`,
    sku,
    barcode,
    nameFr,
    nameAr,
    nameEn,
    categoryId: null,
    price,
    taxRate,
    unit,
    trackStock: true,
    stockQty,
    reorderLevel,
    isActive: true,
    isRentable,
    version: 10 + n,
    isDeleted: false,
  };
}

const products = [
  product(1, 'BTP-0101', '6130201001017', 'Ciment CPJ 42.5 — sac 50 kg', 'إسمنت CPJ 42.5 — كيس 50 كغ', 'CPJ 42.5 cement — 50 kg bag', 980, 19, 'sac', 640, 100, false),
  product(2, 'BTP-0102', '6130201001024', 'Rond à béton HA12 — barre 12 m', 'حديد التسليح HA12 — قضيب 12 م', 'HA12 rebar — 12 m bar', 1650, 19, 'barre', 860, 150, false),
  product(3, 'BTP-0103', '6130201001031', 'Étai métallique télescopique 3.50 m', 'دعامة معدنية تلسكوبية 3.50 م', 'Telescopic steel prop 3.50 m', 4800, 19, 'pcs', 120, 20, true),
  product(4, 'BTP-0104', '6130201001048', 'Casque de chantier EN 397', 'خوذة ورشة EN 397', 'Site helmet EN 397', 1200, 19, 'pcs', 85, 20, false),
  product(5, 'BTP-0105', '6130201001055', 'Disque à tronçonner métal Ø230', 'قرص قطع المعادن Ø230', 'Metal cutting disc Ø230', 450, 19, 'pcs', 240, 50, false),
  product(6, 'BTP-0106', '6130201001062', 'Sable de carrière 0/4 — m³', 'رمل المحجرة 0/4 — م³', 'Quarry sand 0/4 — m³', 2400, 9, 'm³', 36.5, 10, false),
  product(7, 'BTP-0107', '6130201001079', 'Gants de manutention — paire', 'قفازات العمل — زوج', 'Work gloves — pair', 350, 19, 'paire', 300, 50, false),
  product(8, 'BTP-0108', '6130201001086', 'Fil de ligature recuit — rouleau 25 kg', 'سلك الربط المليّن — لفة 25 كغ', 'Annealed tie wire — 25 kg coil', 4200, 19, 'rouleau', 42, 10, false),
  product(9, 'LOC-0201', '6130201002014', 'Cadre échafaudage acier 2.00 m', 'إطار سقالة فولاذي 2.00 م', 'Steel scaffold frame 2.00 m', 8200, 19, 'pcs', 8, 15, true),
  product(10, 'LOC-0202', '6130201002021', 'Panneau coffrage 0.76 × 3.00 m', 'لوح قولبة 0.76 × 3.00 م', 'Formwork panel 0.76 × 3.00 m', 12500, 19, 'pcs', 45, 10, true),
  product(11, 'LOC-0203', '6130201002038', 'Bétonnière thermique 350 L', 'خلاطة خرسانة حرارية 350 لتر', 'Petrol concrete mixer 350 L', 185000, 19, 'pcs', 3, 1, true),
  product(12, 'LOC-0204', '6130201002045', 'Marteau-piqueur électrique 1500 W', 'مطرقة هدم كهربائية 1500 واط', 'Electric breaker 1500 W', 68000, 19, 'pcs', 0, 1, true),
];
const P = Object.fromEntries(products.map((p) => [p.sku, p]));

// Customer (core/models.ts).
const customers = [
  {
    id: 'cust-1', code: 'CL-00412', fullName: 'ETB Bensalem Travaux', phone: '0661 45 78 12',
    address: 'Cité 500 logements, Bab Ezzouar, Alger', taxId: '000216098765431', email: 'contact@bensalem-travaux.dz',
    birthDate: null, creditLimit: 500000, balance: 126400, loyaltyPoints: 0, version: 3, isDeleted: false,
  },
  {
    id: 'cust-2', code: 'CL-00418', fullName: 'مؤسسة الإخوة بوزيد للبناء', phone: '0770 88 41 26',
    address: 'حي 200 مسكن، الرويبة، الجزائر', taxId: '000316045612378', email: null,
    birthDate: null, creditLimit: 300000, balance: 0, loyaltyPoints: 0, version: 2, isDeleted: false,
  },
  {
    id: 'cust-3', code: 'CL-00427', fullName: 'Karim Ould Ali', phone: '0555 23 67 90',
    address: 'Rue des Frères Bouadou, Bir Mourad Raïs, Alger', taxId: null, email: null,
    birthDate: '1983-06-21', creditLimit: 0, balance: 0, loyaltyPoints: 0, version: 1, isDeleted: false,
  },
];

// PaymentMethodDef (core/models.ts): the two built-ins the preset enables, and the operator's cheque
// (traceable instrument: number, bank, due date) and bank transfer.
const paymentMethods = [
  {
    id: 'pm-cash', key: 'cash', nameAr: 'نقدًا', nameFr: 'Espèces', nameEn: 'Cash', behavior: 'Cash',
    requiresConfirmation: false, affectsCashDrawer: true, requiresReference: false, requiresInstrumentDetails: false,
    isBuiltIn: true, isActive: true, sortOrder: 1, version: 1, isDeleted: false,
  },
  {
    id: 'pm-card', key: 'card', nameAr: 'بطاقة', nameFr: 'Carte', nameEn: 'Card', behavior: 'Card',
    requiresConfirmation: false, affectsCashDrawer: false, requiresReference: false, requiresInstrumentDetails: false,
    isBuiltIn: true, isActive: true, sortOrder: 2, version: 1, isDeleted: false,
  },
  {
    id: 'pm-cheque', key: 'cheque', nameAr: 'شيك', nameFr: 'Chèque', nameEn: 'Cheque', behavior: 'Generic',
    requiresConfirmation: true, affectsCashDrawer: false, requiresReference: false, requiresInstrumentDetails: true,
    isBuiltIn: false, isActive: true, sortOrder: 3, version: 2, isDeleted: false,
  },
  {
    id: 'pm-transfer', key: 'transfer', nameAr: 'تحويل بنكي', nameFr: 'Virement bancaire', nameEn: 'Bank transfer', behavior: 'Generic',
    requiresConfirmation: true, affectsCashDrawer: false, requiresReference: false, requiresInstrumentDetails: false,
    isBuiltIn: false, isActive: true, sortOrder: 4, version: 2, isDeleted: false,
  },
];

// The user row a pull carries for this till (PosUser). No PIN hash: the shots never sign in offline.
const tillUsers = [{ id: cashier.id, username: cashier.username, fullName: cashier.fullName, role: 'Cashier', pinHash: '', isActive: true, version: 2, isDeleted: false }];

// =====================================================================================
// the API
// =====================================================================================

/** The answer to one request, or null when nothing here knows the route (the default then answers). */
function mockApi(method, url) {
  const p = url.pathname;
  const json = (body, status = 200) => ({ status, body });

  if (p === '/api/auth/login') {
    return json({
      token: 'mock-jwt-token',
      expiresAtUtc: '2099-01-01T00:00:00Z',
      user: cashier,
      // A till session: the cashier's own till, on the Algiers branch.
      deviceCode: TILL_CODE,
      deviceBranchId: 'b-main',
      lastLocalSeq: 411,
      regional,
      subscription,
      integrations,
    });
  }
  if (p === '/api/auth/ping') return json({ ok: true });
  if (p === '/api/auth/sso/config') return json({ enabled: false, isPlatform: false });
  if (p === '/api/auth/logout') return json({ ok: true });
  if (p === '/api/sync/pull') {
    return json({
      cursor: 18422,
      hasMore: false,
      serverTimeUtc: new Date().toISOString(),
      regional,
      subscription,
      integrations,
      store,
      branches,
      categories: [],
      products,
      customers,
      users: tillUsers,
      giftCards: [],
      paymentMethods,
    });
  }
  if (p === '/api/sync/push' && method === 'POST') {
    return json({ cursor: 18422, serverTimeUtc: new Date().toISOString(), accepted: 0, duplicates: 0, rejected: 0, results: [] });
  }
  if (p === '/api/catalog/branches') return json(branches);
  if (p === '/api/catalog/store') return json(store);
  if (p === '/api/catalog/customers') return json(customers);
  if (p === '/api/catalog/products') return json(products);
  return null;
}

// =====================================================================================
// the run
// =====================================================================================

const ar = (arText, frText) => ({ ar: arText, fr: frText });
// The till's words (core/i18n/translations.ts), for reaching controls by their accessible names.
const TEXT = {
  add: ar('إضافة', 'Ajouter'),
  qty: ar('الكمية', 'Qté'),
  customer: ar('الزبون', 'Client'),
  addDiscount: ar('إضافة تخفيض', 'Ajouter une remise'),
  doneDiscount: ar('تم', 'Terminé'),
  discount: ar('تخفيض', 'Remise'),
  tax: ar('ض.م', 'TVA'),
  pay: ar('الدفع', 'Encaisser'),
  showCart: ar('عرض السلة', 'Voir le panier'),
  search: ar('ابحث أو امسح الباركود', 'Rechercher ou scanner un code-barres'),
  splitOn: ar('الدفع بعدة وسائل', 'Payer avec plusieurs moyens'),
  method: ar('طريقة الدفع', 'Mode de paiement'),
  amount: ar('المبلغ', 'Montant'),
  chequeNumber: ar('رقم الشيك', 'N° du chèque'),
  bank: ar('البنك', 'Banque'),
  dueDate: ar('تاريخ الاستحقاق', 'Échéance'),
};

// The sale every run rings up: four articles, the cement three times over (two presses of the
// stepper), for ETB Bensalem Travaux, taxed, with 5 % off. Two TVA rates, so both buckets show.
const CART = [P['BTP-0101'], P['BTP-0103'], P['BTP-0104'], P['BTP-0106']];
const STEPPED = P['BTP-0101'];
const CART_CUSTOMER = customers[0];
const DISCOUNT_PERCENT = '5';
// The cheque half of the split. Seven digits, as an Algerian cheque is numbered.
const CHEQUE = {
  number: '4471093',
  bank: ar('البنك الوطني الجزائري — وكالة باب الزوار', 'BNA — agence Bab Ezzouar'),
  dueDate: '2026-11-15',
};

const nameOf = (p, lang) => (lang === 'ar' ? p.nameAr : p.nameFr);

const STEPS = {
  empty: { id: '01-till-empty', act: null, fullPage: true },
  cart: { id: '02-till-cart', act: buildCart, fullPage: true },
  pay: { id: '03-till-pay', act: openPayment, fullPage: false },
  split: { id: '04-till-pay-split', act: splitCashCheque, fullPage: false },
};

/**
 * One run = one language at one size, in its own browser context, walking one sale in order.
 * `fullPage: false` on the phone: there the cart is a sheet fixed to the bottom of the screen, and a
 * capture taller than the screen leaves it stranded mid-image over tiles the cashier never sees.
 */
const RUNS = [
  { lang: 'ar', vp: DESKTOP, tag: '1440', steps: [STEPS.empty, STEPS.cart, STEPS.pay, STEPS.split] },
  { lang: 'fr', vp: DESKTOP, tag: '1440', steps: [STEPS.empty, STEPS.cart, STEPS.pay, STEPS.split] },
  { lang: 'ar', vp: PHONE, tag: '390', fullPage: false, steps: [STEPS.empty, STEPS.cart, STEPS.pay, STEPS.split] },
  { lang: 'ar', vp: TABLET, tag: '1024', steps: [STEPS.empty, STEPS.cart] },
];

let page;

const notes = [];
function note(message) {
  console.log(message);
  notes.push(message);
}

const escapeRe = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Waits until any of the texts is on screen, as text or as a field's value. */
async function waitForAny(texts, timeout = READY_TIMEOUT) {
  await page.waitForFunction(
    (list) => {
      const body = document.body ? document.body.innerText : '';
      if (list.some((t) => body.includes(t))) return true;
      return Array.from(document.querySelectorAll('input, textarea')).some((el) => list.some((t) => (el.value || '').includes(t)));
    },
    texts,
    { timeout, polling: 250 },
  );
}

/** The first candidate locator that matches something, or null. */
async function firstOf(...candidates) {
  for (const candidate of candidates) {
    if ((await candidate.count().catch(() => 0)) > 0) return candidate.first();
  }
  return null;
}

/**
 * A press the way a finger makes it. When something lies over the control (the phone's cart sheet
 * over the last row of tiles), the control's own click handler is called instead, and that is said.
 */
async function press(locator, what) {
  try {
    await locator.click({ timeout: 4000 });
  } catch {
    note(`  (${what}: covered or not clickable — pressed through its handler)`);
    await locator.evaluate((el) => el.click());
  }
}

// ---------- the steps; each returns the soft problems it met and throws on a hard one ----------

/**
 * The tile's add key for one article. By its accessible name ("Ajouter — <name>") first; failing
 * that, the last button of the tile that names it, so a reworked tile is still reached.
 */
async function addTile(p, lang) {
  const name = nameOf(p, lang);
  const key = await firstOf(
    page.getByRole('button', { name: `${TEXT.add[lang]} — ${name}`, exact: true }),
    page.locator('.tile', { hasText: name }).getByRole('button'),
  );
  if (!key) throw new Error(`no add key for "${name}"`);
  await press(key, `add ${p.sku}`);
}

/** The cart line's quantity field, by its label ("Qté — <name>"), else the number field of the line. */
async function qtyField(p, lang) {
  const name = nameOf(p, lang);
  return firstOf(
    page.getByRole('spinbutton', { name: `${TEXT.qty[lang]} — ${name}`, exact: true }),
    page.locator('.line', { hasText: name }).locator('input[type="number"]'),
  );
}

/** Below the till's two-column breakpoint the cart is a docked sheet: open it. */
async function openCartSheet(lang) {
  const handle = await firstOf(page.locator('.sheet-handle'), page.getByRole('button', { name: new RegExp(escapeRe(TEXT.showCart[lang])) }));
  if (!handle || !(await handle.isVisible().catch(() => false))) return false;
  if ((await handle.getAttribute('aria-expanded')) === 'true') return true;
  await handle.click({ timeout: 4000 });
  await page.waitForTimeout(400);
  return true;
}

async function buildCart(lang) {
  const problems = [];
  let added = 0;
  for (const p of CART) {
    try {
      await addTile(p, lang);
      added++;
    } catch (e) {
      problems.push(`could not add ${p.sku}: ${e.message.split('\n')[0]}`);
    }
  }
  if (!added) throw new Error('no article reached the cart');

  if (await openCartSheet(lang)) note('  (cart sheet opened)');

  // The quantity: the stepper's + pressed twice on the cement, 1 → 3.
  try {
    const field = await qtyField(STEPPED, lang);
    if (!field) throw new Error('no quantity field on the cement line');
    const stepper = field.locator('xpath=..');
    const plus = await firstOf(
      stepper.getByRole('button', { name: `${TEXT.qty[lang]} +`, exact: true }),
      stepper.getByRole('button', { name: '+', exact: true }),
    );
    if (!plus) throw new Error('no + beside the quantity');
    await press(plus, 'stepper +');
    await press(plus, 'stepper +');
    await page.waitForFunction((el) => el.value === '3', await field.elementHandle(), { timeout: 4000 });
  } catch (e) {
    problems.push(`quantity 3 not set with the stepper: ${e.message.split('\n')[0]}`);
  }

  // The customer.
  try {
    const select = await firstOf(page.locator('#cust'), page.getByLabel(TEXT.customer[lang], { exact: true }));
    if (!select) throw new Error('no customer picker');
    await select.selectOption(CART_CUSTOMER.id, { timeout: 4000 });
  } catch (e) {
    problems.push(`customer not chosen: ${e.message.split('\n')[0]}`);
  }

  // Taxed, so the TVA lines show (the till starts untaxed).
  try {
    const box = await firstOf(page.locator('#taxmode'), page.getByRole('checkbox', { name: TEXT.tax[lang], exact: true }));
    if (!box) throw new Error('no tax switch');
    await box.check({ timeout: 4000 });
  } catch (e) {
    problems.push(`tax not switched on: ${e.message.split('\n')[0]}`);
  }

  // 5 % off, through the discount editor, then closed back to its summary.
  try {
    const open = await firstOf(page.getByRole('button', { name: TEXT.addDiscount[lang], exact: true }), page.locator('.disc-add-btn'));
    if (!open) throw new Error('no add-discount button');
    await press(open, 'add discount');
    const type = await firstOf(page.locator('#disc-type'), page.getByRole('combobox', { name: TEXT.discount[lang], exact: true }));
    if (!type) throw new Error('no discount type');
    await type.selectOption('percent', { timeout: 4000 });
    const value = await firstOf(page.locator('#disc-value'), page.getByRole('spinbutton', { name: TEXT.discount[lang], exact: true }));
    if (!value) throw new Error('no discount value');
    await value.fill(DISCOUNT_PERCENT);
    await value.press('Tab');
    const done = await firstOf(page.getByRole('button', { name: TEXT.doneDiscount[lang], exact: true }), page.locator('.disc-done-btn'));
    if (done) await press(done, 'discount done');
  } catch (e) {
    problems.push(`discount not applied: ${e.message.split('\n')[0]}`);
  }

  // Back to the search box, where the next scan lands — the till's resting state.
  const search = await firstOf(page.getByRole('searchbox', { name: TEXT.search[lang] }), page.locator('input[type="search"]'));
  if (search) await search.focus().catch(() => {});

  if (added < CART.length) problems.push(`${added} of ${CART.length} articles in the cart`);
  return problems;
}

async function openPayment(lang) {
  const pay = await firstOf(page.getByRole('button', { name: TEXT.pay[lang], exact: true }), page.locator('button.pay'));
  if (!pay) throw new Error('no pay button');
  await press(pay, 'pay');
  await page.locator('[role="dialog"]:visible, dialog[open]').first().waitFor({ state: 'visible', timeout: 8000 });
  return [];
}

async function splitCashCheque(lang) {
  const problems = [];
  const dialog = page.locator('[role="dialog"]:visible, dialog[open]').first();
  const toggle = await firstOf(dialog.getByRole('button', { name: TEXT.splitOn[lang], exact: true }), dialog.locator('.split-toggle'));
  if (!toggle) throw new Error('no split toggle in the payment dialog');
  await press(toggle, 'split');

  const methods = dialog.getByRole('combobox', { name: TEXT.method[lang], exact: true });
  await methods.nth(1).waitFor({ state: 'visible', timeout: 5000 });
  const amounts = dialog.getByRole('spinbutton', { name: TEXT.amount[lang], exact: true });

  // The first line opens holding the whole total: read it there, as a plain number.
  const total = Number(await amounts.nth(0).inputValue());
  if (!(total > 0)) throw new Error(`no total on the first payment line (read "${total}")`);
  const cheque = Math.max(1000, Math.floor((total * 0.6) / 1000) * 1000);
  const cash = Math.round((total - cheque) * 100) / 100;

  await methods.nth(0).selectOption('cash', { timeout: 4000 }).catch(() => {});
  await methods.nth(1).selectOption('cheque', { timeout: 4000 });
  await amounts.nth(1).fill(String(cheque));
  await amounts.nth(0).fill(String(cash));

  try {
    const number = dialog.getByLabel(TEXT.chequeNumber[lang], { exact: true });
    await number.waitFor({ state: 'visible', timeout: 4000 });
    await number.fill(CHEQUE.number);
    await dialog.getByLabel(TEXT.bank[lang], { exact: true }).fill(CHEQUE.bank[lang]);
    const due = dialog.getByLabel(TEXT.dueDate[lang], { exact: true });
    await due.fill(CHEQUE.dueDate);
    // fill() leaves a date segment selected; a cashier's tap elsewhere would not.
    await due.blur().catch(() => {});
    // On a phone the dialog scrolls: bring the cheque's fields into the picture.
    await due.scrollIntoViewIfNeeded().catch(() => {});
  } catch (e) {
    problems.push(`cheque details not filled: ${e.message.split('\n')[0]}`);
  }
  note(`  (split: ${cash} cash + ${cheque} cheque = ${total})`);
  return problems;
}

// ---------- plumbing ----------

async function signIn(lang) {
  await page.goto(`${BASE}/#/login`);
  await page.evaluate((l) => localStorage.setItem('pos.lang', l), lang);
  await page.reload();
  await page.waitForSelector('#username', { timeout: READY_TIMEOUT });
  if (await page.locator('#server').isVisible().catch(() => false)) await page.fill('#server', BASE);
  await page.fill('#username', cashier.username);
  await page.fill('#password', 'AnyPassword123');
  await page.click('button[type=submit]');
  await page.waitForURL((u) => u.hash.startsWith('#/register'), { timeout: 30_000 });
  // The tiles are drawn once the pull has reached IndexedDB and the till has read it back.
  await waitForAny(products.slice(0, 3).map((p) => nameOf(p, lang)));
}

async function settle() {
  await page.evaluate(() => (document.fonts ? document.fonts.ready.then(() => true) : true)).catch(() => {});
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.waitForTimeout(700);
}

function serve() {
  const mime = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript',
    '.mjs': 'text/javascript',
    '.css': 'text/css',
    '.json': 'application/json',
    '.webmanifest': 'application/manifest+json',
    '.woff2': 'font/woff2',
    '.woff': 'font/woff',
    '.ttf': 'font/ttf',
    '.svg': 'image/svg+xml',
    '.png': 'image/png',
    '.ico': 'image/x-icon',
  };
  const server = http.createServer((req, res) => {
    const requested = decodeURIComponent(new URL(req.url, BASE).pathname);
    const filePath = path.resolve(webDist, '.' + (requested === '/' ? '/index.html' : requested));
    const inside = filePath.startsWith(webDist + path.sep);
    if (!inside || !fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
      res.writeHead(200, { 'Content-Type': mime['.html'] });
      fs.createReadStream(path.join(webDist, 'index.html')).pipe(res);
      return;
    }
    res.setHeader('Content-Type', mime[path.extname(filePath)] || 'application/octet-stream');
    fs.createReadStream(filePath).pipe(res);
  });
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(PORT, '127.0.0.1', () => resolve(server));
  });
}

async function main() {
  if (!fs.existsSync(path.join(webDist, 'index.html'))) {
    console.error(`No built bundle at ${webDist} (index.html missing). Build it, or point WEB_DIST at one.`);
    process.exit(2);
  }
  fs.mkdirSync(out, { recursive: true });
  console.log(`Batch 4 screenshots (the till) — set "${SHOT_SET}"`);
  console.log(`  bundle: ${path.relative(root, webDist) || webDist}`);
  console.log(`  output: ${path.relative(root, out)}`);

  const server = await serve();
  const browser = await chromium.launch({ headless: true });
  const fellThrough = new Map();
  const pageErrors = new Map();
  const results = [];
  let current = 'start';

  try {
    for (const run of RUNS) {
      const label = `${run.lang}-${run.tag}`;
      current = `${label} sign-in`;
      console.log(`Run ${label} (${run.vp.width}x${run.vp.height})`);
      // A fresh context per run: an empty IndexedDB, so no catalogue, cart or sale carries over.
      // No locale on purpose: the app formats dates and numbers itself.
      const context = await browser.newContext({ viewport: run.vp, serviceWorkers: 'block', timezoneId: 'Africa/Algiers' });
      page = await context.newPage();
      page.on('pageerror', (error) => {
        const key = `${current}: ${String(error.message).split('\n')[0]}`;
        pageErrors.set(key, (pageErrors.get(key) ?? 0) + 1);
      });
      await page.route('**/api/**', async (route) => {
        const request = route.request();
        const url = new URL(request.url());
        let answer = null;
        try {
          answer = mockApi(request.method(), url);
        } catch (error) {
          note(`  mock error on ${request.method()} ${url.pathname}: ${error.message}`);
        }
        if (!answer) {
          const key = `${request.method()} ${url.pathname}`;
          if (!fellThrough.has(key)) fellThrough.set(key, current);
          return route.fulfill({ json: [] });
        }
        return route.fulfill({ status: answer.status, json: answer.body });
      });

      let signedIn = true;
      try {
        await signIn(run.lang);
      } catch (error) {
        signedIn = false;
        note(`  sign-in or catalogue failed: ${error.message.split('\n')[0]}`);
      }

      for (const step of run.steps) {
        const file = `${step.id}-${label}.png`;
        current = file;
        const target = path.join(out, file);
        const problems = [];
        try {
          if (!signedIn) problems.push('the till did not open with its products');
          if (step.act) {
            const soft = await step.act(run.lang).catch((e) => [`action failed: ${e.message.split('\n')[0]}`]);
            problems.push(...soft);
          }
          const dir = await page.evaluate(() => document.documentElement.getAttribute('dir') || document.dir || 'ltr');
          if ((run.lang === 'ar') !== (dir === 'rtl')) problems.push(`page is ${dir}, expected ${run.lang === 'ar' ? 'rtl' : 'ltr'}`);
          await settle();
          await page.screenshot({ path: target, fullPage: step.fullPage && run.fullPage !== false });
          results.push({ file, status: problems.length ? 'warn' : 'ok', problems });
          console.log(problems.length ? `  wrote ${file}  (WARN: ${problems.join('; ')})` : `  wrote ${file}`);
        } catch (error) {
          problems.push(error.message.split('\n')[0]);
          results.push({ file, status: 'failed', problems });
          console.log(`  FAILED ${file}: ${problems.join('; ')}`);
        }
      }
      await context.close();
    }
  } finally {
    await browser.close();
    server.close();
  }

  // ---- summary ----
  const ok = results.filter((r) => r.status === 'ok').length;
  const warned = results.filter((r) => r.status === 'warn');
  const failed = results.filter((r) => r.status === 'failed');
  console.log(`\nDone: ${ok} clean, ${warned.length} with warnings, ${failed.length} failed — ${path.relative(root, out)}`);
  for (const r of [...warned, ...failed]) console.log(`  ${r.status.toUpperCase()} ${r.file}: ${r.problems.join('; ')}`);

  if (fellThrough.size) {
    console.log('\n/api paths answered by the default mock ([]):');
    for (const [key, first] of fellThrough) console.log(`  ${key}   (first on ${first})`);
  } else {
    console.log('\nEvery /api call was answered by a specific mock.');
  }

  if (pageErrors.size) {
    console.log('\nUncaught page errors:');
    for (const [key, count] of pageErrors) console.log(`  ${key}${count > 1 ? `  ×${count}` : ''}`);
  }

  if (failed.length) process.exitCode = 1;
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
