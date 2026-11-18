// Paradis redesign — Batch 1 screenshot harness (shell, tokens, sign-in).
//
//   SHOT_SET=before UI_FONT=ibm-plex-sans-arabic WEB_DIST=<bundle from main> node tests/e2e/capture-paradis-batch1-screenshots.cjs
//   SHOT_SET=after node tests/e2e/capture-paradis-batch1-screenshots.cjs
//
// Serves an already-built bundle (WEB_DIST, default ParadiERP.WebUI/dist/paradierp-web-ui/browser;
// a relative path is read against the repo root) on 127.0.0.1:5216, answers every /api call from the
// mocks below, captures sign-in shots in a fresh signed-out context, then signs in through #/login in
// the main context and writes PNGs to artifacts/ui-redesign-checkpoint/paradis-batch1/<SHOT_SET>.
// It never builds anything.
//
// Options:
//   OUT_DIR=<folder>       writes there instead (a relative path is read against the repo root);
//   SHOTS_ONLY=01,40,50    captures only the shots whose file name starts with one of these;
//   UI_FONT=<font>         value of regional.uiFont in the mocks (default 'thmanyah-sans');
//   APPEARANCE='{"mode":"dark","theme":"atlas"}'   default theme/mode when a shot has no appearance;
//   CHROME_PATH=<path>     optional path to an installed Chrome/Chromium binary.

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
const out = process.env.OUT_DIR
  ? path.resolve(root, process.env.OUT_DIR)
  : path.join(root, 'artifacts/ui-redesign-checkpoint/paradis-batch1', SHOT_SET);
const ONLY = (process.env.SHOTS_ONLY || '').split(',').map((x) => x.trim()).filter(Boolean);
const chosen = (shot) => ONLY.length === 0 || ONLY.some((prefix) => shot.file.startsWith(prefix));
const UI_FONT = (process.env.UI_FONT || 'thmanyah-sans').trim() || 'thmanyah-sans';
const APPEARANCE = (process.env.APPEARANCE || '').trim() || null;
if (APPEARANCE) JSON.parse(APPEARANCE);
const webDist = path.resolve(root, process.env.WEB_DIST || 'ParadiERP.WebUI/dist/paradierp-web-ui/browser');

const PORT = 5216;
const BASE = `http://127.0.0.1:${PORT}`;
const DESKTOP = { width: 1440, height: 960 };
const TABLET = { width: 900, height: 960 };
const PHONE = { width: 390, height: 844 };
const READY_TIMEOUT = 15_000;

// =====================================================================================
// the company (copied verbatim from capture-batch3-screenshots.cjs, with UI_FONT)
// =====================================================================================

const permissions = [
  'admin.access',
  'dashboard.view',
  'catalog.view',
  'catalog.edit',
  'rentals.read',
  'rentals.contracts',
  'rentals.return',
  'rentals.issue',
  'sales.view',
  'customers.view',
  'customers.edit',
  'customers.accounts',
  'customers.opening_balance',
  'customer_statements.filter',
  'stock.view',
  'stock.receiving',
  'stock.transfers',
  'finance.view',
  'vouchers.view',
  'vouchers.create',
  'vouchers.approve',
  'receivables.view',
  'users.manage',
  'roles.manage',
  // The named grants the Batch 3 pages are gated on (core/nav.ts, Domain/Permissions.cs).
  'payroll.manage',
  'hr.attendance',
  'payroll.edit_statutory',
  'treasury.cash_box',
  'stock.view_other_branches',
];

// The Batch 2 admin, now with its branch: an Admin is never branch-locked.
const mockUser = {
  id: 'u-admin',
  username: 'admin',
  fullName: 'Chef de Parc / Directeur',
  role: 'Admin',
  branchId: 'b-main',
  branchLocked: false,
  permissions,
  capabilities: ['customer_statements.filter'],
};

// The Algeria preset, as the server sends it with the sign-in and every pull (core/models.ts).
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
  paymentMethods: ['Cash', 'Card', 'Credit'],
  receiptSections: ['header', 'fiscalIds', 'meta', 'lines', 'totals', 'taxBreakdown', 'payment', 'legal', 'footer'],
  receiptLegalText: {},
  uiFont: UI_FONT,
};

// The owner owns the installation: no plan, no expiry (core/nav.ts hostedSubscription).
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

// AdminBranch (core/admin.service.ts), also the sync Branch row. Same ids and names as Batch 2.
const mockBranches = [
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
    isOpenForOperation: true,
    logoUrl: null,
    fiscalIds: {},
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
    isOpenForOperation: true,
    logoUrl: null,
    fiscalIds: { ai: '31012345678' },
    version: 4,
  },
];
const branchNameFr = (id) => mockBranches.find((b) => b.id === id)?.nameFr ?? '—';

// AdminStore.
const mockStore = {
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

// StoreDocumentsDto (features/print/store-documents-api.service.ts).
const mockStoreDocuments = {
  documentTexts: {
    invoice: {
      fr: 'Paiement à 30 jours fin de mois. Tout retard entraîne des pénalités conformément à la réglementation en vigueur.',
      ar: 'الدفع خلال 30 يوماً من نهاية الشهر. يترتب على كل تأخير غرامات وفق التنظيم المعمول به.',
    },
    rentalContract: {
      fr: 'Le matériel est remis en bon état et doit être restitué propre. Toute pièce manquante ou endommagée est facturée à sa valeur de remplacement.',
      ar: 'يُسلَّم العتاد في حالة جيدة ويجب إرجاعه نظيفاً. تُفوتر كل قطعة مفقودة أو تالفة بقيمة استبدالها.',
    },
    'rentalContract.lt4k9m2q': {
      fr: 'Contrat de longue durée pour chantier : facturation mensuelle, inventaire contradictoire chaque fin de mois.',
      ar: 'عقد طويل المدة للورشة: فوترة شهرية وجرد حضوري في نهاية كل شهر.',
    },
    payslip: { fr: 'Bulletin à conserver sans limitation de durée.', ar: 'كشف الراتب يُحفظ دون تحديد للمدة.' },
  },
  email: 'contact@atlas-location.dz',
  website: 'www.atlas-location.dz',
  bankDetails: 'BNA agence Oued Smar — RIB 001 00612 0300012345 67',
  changesPerYear: 3,
  allowances: [
    { kind: 'invoice', year: 2026, limit: 3, used: 1, remaining: 2, resetsOnUtc: '2026-12-31T23:00:00Z' },
    { kind: 'rentalContract', year: 2026, limit: 3, used: 2, remaining: 1, resetsOnUtc: '2026-12-31T23:00:00Z' },
    { kind: 'rentalContract.lt4k9m2q', year: 2026, limit: 3, used: 1, remaining: 2, resetsOnUtc: '2026-12-31T23:00:00Z' },
  ],
  logoUrl: null,
  titleLanguages: ['fr', 'ar'],
  itemNameLanguages: ['fr', 'ar'],
  contractWordings: [{ key: 'rentalContract.lt4k9m2q', names: { fr: 'Contrat chantier (longue durée)', ar: 'عقد ورشة (مدة طويلة)' } }],
};

const mockDocumentHistory = [
  {
    id: 'dh-1',
    kind: 'rentalContract',
    changeYear: 2026,
    changedAtUtc: '2026-03-04T10:20:00Z',
    previousTexts: { fr: 'Le matériel est remis en bon état et doit être restitué propre.' },
    changedBy: 'admin',
  },
];

// ---------- employees (Employee) ----------

function employee(e) {
  return {
    payBasis: 'Monthly',
    isActive: true,
    leftOn: null,
    nationalId: null,
    socialSecurityNumber: null,
    fingerprintNumber: null,
    birthDate: null,
    phone: null,
    email: null,
    address: null,
    jobTitle: null,
    bankAccount: null,
    attendanceModeOverride: null,
    attendanceMode: 'Declared',
    notes: null,
    allowances: [],
    openAdvances: 0,
    accountUserId: null,
    accountSignIn: null,
    ...e,
    fullName: `${e.firstName} ${e.familyName}`,
    branchName: branchNameFr(e.branchId),
  };
}

const mockEmployees = [
  employee({
    id: 'e-1', number: 'Emp-162203001', firstName: 'كريم', familyName: 'بن علي', branchId: 'b-main', baseRate: 85000,
    payoutMethod: 'Bank', hiredOn: '2022-03-01', jobTitle: 'رئيس الحظيرة', fingerprintNumber: '0101',
    nationalId: '109850160012345678', socialSecurityNumber: '85 1604 1234 56', birthDate: '1985-04-12',
    phone: '0661 23 45 67', email: 'k.benali@atlas-location.dz', address: 'حي 1200 مسكن، باب الزوار، الجزائر',
    bankAccount: '00400 1234 0012345678 21',
  }),
  employee({
    id: 'e-2', number: 'Emp-162306002', firstName: 'ياسين', familyName: 'حداد', branchId: 'b-main', baseRate: 52000,
    payoutMethod: 'Cash', hiredOn: '2023-06-15', jobTitle: 'سائق وزن ثقيل', fingerprintNumber: '0102',
    socialSecurityNumber: '90 1607 5566 12', birthDate: '1990-07-22', phone: '0770 45 12 98', openAdvances: 15000,
  }),
  employee({
    id: 'e-3', number: 'Emp-162401003', firstName: 'Samir', familyName: 'Boudiaf', branchId: 'b-main', baseRate: 45000,
    payoutMethod: 'Cash', hiredOn: '2024-01-08', jobTitle: 'Magasinier', fingerprintNumber: '0103',
    socialSecurityNumber: '94 1602 7788 03', birthDate: '1994-02-03', phone: '0555 66 77 01',
    address: 'Cité Bachdjarah, Alger', attendanceModeOverride: 'Recorded', attendanceMode: 'Recorded',
    allowances: [{ allowanceKey: 'transport', value: 4500, isEnabled: true }],
    notes: 'Habilité au chariot élévateur.',
  }),
  employee({
    id: 'e-4', number: 'Emp-162109004', firstName: 'Nadia', familyName: 'Mansouri', branchId: 'b-main', baseRate: 68000,
    payoutMethod: 'Bank', hiredOn: '2021-09-01', jobTitle: 'Comptable', fingerprintNumber: '0104',
    socialSecurityNumber: '88 1611 3344 77', birthDate: '1988-11-17', phone: '0662 98 11 40',
    email: 'n.mansouri@atlas-location.dz', bankAccount: '00799999 0001234567 89',
    accountUserId: 'u-acc', accountSignIn: '16260412',
  }),
  employee({
    id: 'e-5', number: 'Emp-312302001', firstName: 'رشيد', familyName: 'بلقاسم', branchId: 'b-oran', baseRate: 58000,
    payoutMethod: 'Cash', hiredOn: '2023-02-20', jobTitle: 'مركب سقالات', fingerprintNumber: '0201',
    socialSecurityNumber: '87 3105 2211 09', birthDate: '1987-05-09', phone: '0698 33 21 54',
    accountUserId: 'u-mgr', accountSignIn: '31260203',
  }),
  employee({
    id: 'e-6', number: 'Emp-312505002', firstName: 'Amina', familyName: 'Zerrouki', branchId: 'b-oran', baseRate: 50000,
    payoutMethod: 'Bank', hiredOn: '2025-05-04', jobTitle: 'Agent commercial', fingerprintNumber: '0202',
    socialSecurityNumber: '97 3108 9900 14', birthDate: '1997-08-14', phone: '0540 12 34 56',
    email: 'a.zerrouki@atlas-location.dz', bankAccount: '00300 0456 0987654321 33',
    accountUserId: 'u-cash', accountSignIn: '31260518',
  }),
  employee({
    id: 'e-7', number: 'Emp-162211005', firstName: 'Mourad', familyName: 'Khelifi', branchId: 'b-main', baseRate: 48000,
    payoutMethod: 'Cash', hiredOn: '2022-11-01', jobTitle: 'Chauffeur', isActive: false, leftOn: '2026-08-31',
  }),
];
const emp = (id) => mockEmployees.find((e) => e.id === id);

// ---------- sales summary & sales (Batch 3 verbatim) ----------

const mockSalesSummary = {
  fromUtc: '2026-09-30T23:00:00.000Z',
  toUtc: '2026-10-15T23:00:00.000Z',
  saleCount: 142,
  refundCount: 2,
  grossTtc: 1465240,
  netHt: 1236000,
  taxTotal: 229240,
  discountTotal: 18500,
  refundTotal: 12000,
  roundingTotal: 0,
  giftCardTotal: 0,
  loyaltyTotal: 0,
  taxBreakdown: [
    { rate: 19, baseHt: 1180000, taxAmount: 224200 },
    { rate: 9, baseHt: 56000, taxAmount: 5040 },
  ],
  topProducts: [
    { productId: 'p1', name: 'Panneau coffrage 0.76 x 3.00 m', qty: 340, revenue: 412300 },
    { productId: 'p2', name: 'Étai métallique télescopique 3.50 m', qty: 610, revenue: 298900 },
    { productId: 'p3', name: 'Cadre échafaudage acier 2.00 m', qty: 185, revenue: 241750 },
    { productId: 'p4', name: 'Plancher acier galvanisé 3.00 m', qty: 160, revenue: 196350 },
    { productId: 'p5', name: 'Bétonnière 350 L thermique', qty: 12, revenue: 118900 },
  ],
  branchBreakdown: [
    { branchId: 'b-main', branchName: 'Succursale Alger Centre', saleCount: 96, grossTtc: 1012340 },
    { branchId: 'b-oran', branchName: 'Succursale Oran', saleCount: 46, grossTtc: 452900 },
  ],
};

const mockSales = [
  ['2026-10-15T09:42:00Z', 'b-main', 'ALG-01', 'Samir Boudiaf', 64800, 'Cash', 'Completed'],
  ['2026-10-15T08:16:00Z', 'b-oran', 'ORN-01', 'Amina Zerrouki', 23500, 'Card', 'Completed'],
  ['2026-10-14T14:05:00Z', 'b-main', 'ALG-01', 'Samir Boudiaf', 112000, 'Credit', 'Completed'],
  ['2026-10-13T10:30:00Z', 'b-main', 'ALG-02', 'Nadia Mansouri', 8600, 'Cash', 'Completed'],
  ['2026-10-12T15:48:00Z', 'b-oran', 'ORN-01', 'Amina Zerrouki', 41200, 'Cash', 'Completed'],
  ['2026-10-11T09:05:00Z', 'b-main', 'ALG-01', 'Samir Boudiaf', 10084, 'Cash', 'Refunded'],
  ['2026-10-08T11:22:00Z', 'b-main', 'ALG-01', 'Samir Boudiaf', 186000, 'Credit', 'Completed'],
  ['2026-10-07T13:10:00Z', 'b-oran', 'ORN-01', 'Amina Zerrouki', 57300, 'Card', 'Completed'],
  ['2026-10-06T08:47:00Z', 'b-main', 'ALG-02', 'Nadia Mansouri', 15400, 'Cash', 'Completed'],
  ['2026-10-05T16:02:00Z', 'b-main', 'ALG-01', 'Samir Boudiaf', 73900, 'Cash', 'Completed'],
  ['2026-10-04T09:30:00Z', 'b-oran', 'ORN-01', 'Amina Zerrouki', 29800, 'Cash', 'Completed'],
  ['2026-10-01T10:12:00Z', 'b-main', 'ALG-01', 'Samir Boudiaf', 95600, 'Credit', 'Completed'],
].map(([soldAtUtc, branchId, deviceCode, cashierName, ht, paymentMethod, status], i) => {
  const taxTotal = Math.round(ht * 0.19);
  const grandTotalTtc = ht + taxTotal;
  const paidAmount = paymentMethod === 'Cash' ? Math.ceil(grandTotalTtc / 500) * 500 : grandTotalTtc;
  return {
    id: `sale-${i + 1}`,
    reference: `TCK-${deviceCode.replace('-', '')}-${String(412 - i).padStart(6, '0')}`,
    deviceCode,
    branchId,
    soldAtUtc,
    receivedAtUtc: soldAtUtc,
    cashierName,
    customerId: paymentMethod === 'Credit' ? 'cust-1' : null,
    subTotalHt: ht,
    taxTotal,
    discountTotal: 0,
    grandTotalTtc,
    paidAmount,
    changeAmount: paidAmount - grandTotalTtc,
    paymentMethod,
    status,
    stockWarning: false,
    lineCount: 1 + (i % 4),
  };
});

// ---------- the books (LedgerAccount, TrialBalance, ProfitAndLoss) ----------

const ledgerAccounts = [
  ['101', 'Capital social', 'رأس المال الاجتماعي', 'Share capital', 'Equity'],
  ['215', 'Matériel et outillage de location', 'معدات وأدوات التأجير', 'Rental equipment', 'Asset'],
  ['30', 'Stocks de marchandises', 'مخزونات البضائع', 'Goods inventory', 'Asset'],
  ['401', 'Fournisseurs', 'الموردون', 'Suppliers', 'Liability'],
  ['411', 'Clients', 'الزبائن', 'Customers', 'Asset'],
  ['421', 'Personnel, rémunérations dues', 'المستخدمون، أجور مستحقة', 'Wages payable', 'Liability'],
  ['425', 'Personnel, avances et acomptes', 'المستخدمون، تسبيقات', 'Staff advances', 'Asset'],
  ['431', 'Organismes sociaux', 'الهيئات الاجتماعية', 'Social security bodies', 'Liability'],
  ['442', 'État, impôts et taxes (IRG)', 'الدولة، الضرائب والرسوم', 'State, taxes (IRG)', 'Liability'],
  ['4457', 'TVA collectée', 'الرسم على القيمة المضافة المحصل', 'VAT collected', 'Liability'],
  ['512', 'Banque', 'البنك', 'Bank', 'Asset'],
  ['53', 'Caisse', 'الصندوق', 'Cash', 'Asset'],
  ['600', 'Achats de marchandises consommés', 'مشتريات البضائع المستهلكة', 'Goods consumed', 'Expense'],
  ['624', 'Transports de biens', 'نقل البضائع', 'Freight', 'Expense'],
  ['631', 'Rémunérations du personnel', 'أجور المستخدمين', 'Staff wages', 'Expense'],
  ['635', 'Cotisations aux organismes sociaux', 'الاشتراكات في الهيئات الاجتماعية', 'Social contributions', 'Expense'],
  ['681', 'Dotations aux amortissements', 'مخصصات الاهتلاك', 'Depreciation', 'Expense'],
  ['700', 'Ventes de marchandises', 'مبيعات البضائع', 'Sales of goods', 'Income'],
  ['706', 'Prestations de location', 'خدمات التأجير', 'Rental services', 'Income'],
].map(([code, nameFr, nameAr, nameEn, type], i) => ({ id: `acc-${code}`, code, nameAr, nameFr, nameEn, type, isSystem: true, isActive: true, sortOrder: i + 1 }));

const accountByCode = (code) => ledgerAccounts.find((a) => a.code === code);

const plIncome = [['706', 1280000], ['700', 236000]];
const plExpenses = [['600', 85000], ['624', 25000], ['631', 412000], ['635', 107120], ['681', 52000]];

function trialBalance(query) {
  const moves = {
    '101': [0, 2500000],
    '215': [1850000, 0],
    '30': [420000, 85000],
    '401': [85000, 465000],
    '411': [1756240, 1026240],
    '421': [287450, 287450],
    '425': [20000, 5000],
    '431': [62300, 149420],
    '442': [41800, 73640],
    '4457': [0, 229240],
    '512': [980000, 412000],
    '53': [0, 610000],
  };
  for (const [code, amount] of plIncome) moves[code] = [0, amount];
  for (const [code, amount] of plExpenses) moves[code] = [amount, 0];
  const sumSide = (side) => Object.values(moves).reduce((a, m) => a + m[side], 0);
  moves['53'][0] = sumSide(1) - sumSide(0);
  const rows = ledgerAccounts
    .filter((a) => moves[a.code])
    .map((a) => {
      const [debit, credit] = moves[a.code];
      const naturalDebit = a.type === 'Asset' || a.type === 'Expense';
      return { accountId: a.id, code: a.code, nameAr: a.nameAr, nameFr: a.nameFr, nameEn: a.nameEn, type: a.type, debit, credit, balance: naturalDebit ? debit - credit : credit - debit };
    });
  const totalDebit = rows.reduce((a, r) => a + r.debit, 0);
  const totalCredit = rows.reduce((a, r) => a + r.credit, 0);
  const branchId = query.get('branchId') || null;
  return {
    from: query.get('from') || '2026-09-30T23:00:00.000Z',
    to: query.get('to') || '2026-10-15T23:00:00.000Z',
    branchId,
    consolidated: !branchId,
    rows,
    totalDebit,
    totalCredit,
    balanced: totalDebit === totalCredit,
  };
}

function profitAndLoss(query) {
  const line = ([code, amount]) => {
    const a = accountByCode(code);
    return { code, nameAr: a.nameAr, nameFr: a.nameFr, nameEn: a.nameEn, amount };
  };
  const income = plIncome.map(line);
  const expenses = plExpenses.map(line);
  const totalIncome = income.reduce((a, l) => a + l.amount, 0);
  const totalExpenses = expenses.reduce((a, l) => a + l.amount, 0);
  const branchId = query.get('branchId') || null;
  return {
    from: query.get('from') || '2026-09-30T23:00:00.000Z',
    to: query.get('to') || '2026-10-15T23:00:00.000Z',
    branchId,
    consolidated: !branchId,
    income,
    expenses,
    totalIncome,
    totalExpenses,
    netProfit: totalIncome - totalExpenses,
    byBranch: [
      { branchId: 'b-main', branchName: 'Succursale Alger Centre', income: 1062000, expenses: 452620, netProfit: 609380 },
      { branchId: 'b-oran', branchName: 'Succursale Oran', income: 454000, expenses: 228500, netProfit: 225500 },
    ],
    eliminatedInterBranch: 0,
  };
}

// ---------- roles and the permission catalogue (GET /api/roles, GET /api/permissions) ----------

// As a fresh company's server answers them (2026-11-05): the catalogue by module, and the three system roles
// as seeded — the Admin with every key but hr.attendance, the Manager and the Cashier with theirs.
const mockCatalogue = [{"module":"sales","keys":["sales.create","sales.view","sales.refund","reports.sales","sales.untaxed"]},{"module":"customers","keys":["customers.view","customers.create","customers.edit","customers.delete","customers.accounts.view","customers.opening_balance","customers.accounts.filter","customers.documents.view"]},{"module":"rentals","keys":["rentals.view","rentals.create","rentals.edit","rentals.issue","rentals.return","rentals.close","rentals.settings","rentals.cancelled"]},{"module":"inventory","keys":["products.view","products.manage","inventory.view","inventory.adjust","inventory.receive","inventory.transfer","inventory.transfer.write_off","inventory.opening","stock.view_other_branches","gift_cards.view","gift_cards.manage"]},{"module":"finance","keys":["treasury.cash_box","finance.receipts.view","finance.receipts.create","finance.receipts.settle","finance.receipts.settle_own","finance.invoices.view","finance.invoices.create","finance.invoices.untaxed","finance.vouchers.view","finance.vouchers.create","finance.vouchers.pay","finance.vouchers.cancel","finance.vouchers.approve","finance.vouchers.approve_own","finance.collections","finance.cash_boxes.view","finance.remit","finance.settings","suppliers.opening_balance"]},{"module":"accounting","keys":["accounting.view","accounting.manage","reports.accounting","accounting.close"]},{"module":"payroll","keys":["payroll.settings.view","payroll.manage","payroll.edit_statutory","hr.attendance"]},{"module":"manufacturing","keys":["manufacturing.view","manufacturing.lines.manage","manufacturing.bom.manage","manufacturing.bom.approve","manufacturing.settings","manufacturing.orders.create","manufacturing.orders.start_short","manufacturing.orders.manage","manufacturing.materials.move","manufacturing.log.submit","manufacturing.log.approve","manufacturing.labour.override","manufacturing.output.release","manufacturing.completion.approve","manufacturing.reports.view","manufacturing.costs.view","manufacturing.costs.snapshot.create","manufacturing.costs.rates.view"]},{"module":"administration","keys":["users.manage","roles.manage","documents.manage","branches.view","branches.manage","devices.manage","company.settings","subscription.usage"]}];
const allKeys = mockCatalogue.flatMap((m) => m.keys);
const systemRole = (id, key, name, scope, permissions, members) => ({
  id, key, name, description: null, isActive: true, isSystem: true, scope, version: 1, permissions, members,
  permissionsEditable: key !== 'admin', detailsEditable: false, deletable: false,
});
const mockRoles = [
  systemRole('5a17e000-0000-4000-8000-0000000000a1', 'admin', 'Admin', 'Company', allKeys.filter((k) => k !== 'hr.attendance'), 1),
  systemRole('5a17e000-0000-4000-8000-0000000000a2', 'manager', 'Manager', 'AssignedBranch', ["sales.create","sales.view","sales.refund","reports.sales","sales.untaxed","customers.view","customers.create","customers.edit","customers.delete","customers.accounts.view","rentals.view","rentals.create","rentals.edit","rentals.issue","rentals.return","rentals.settings","products.view","products.manage","inventory.view","inventory.adjust","inventory.receive","inventory.transfer","inventory.opening","gift_cards.view","gift_cards.manage","finance.receipts.view","finance.receipts.create","finance.receipts.settle","finance.invoices.view","finance.invoices.create","finance.vouchers.view","finance.vouchers.create","finance.vouchers.pay","finance.vouchers.cancel","finance.vouchers.approve","finance.collections","finance.cash_boxes.view","finance.remit","finance.settings","accounting.view","accounting.manage","reports.accounting","payroll.settings.view","branches.view","branches.manage","devices.manage","company.settings","subscription.usage"], 2),
  systemRole('5a17e000-0000-4000-8000-0000000000a3', 'cashier', 'Cashier', 'TillBranch', ["sales.create","sales.view","sales.refund","reports.sales"], 1),
];

// ---------- users and tills (AdminUser, AdminDevice) ----------

const mockUsers = [
  {
    id: 'u-admin', username: 'admin', fullName: 'Chef de Parc / Directeur', role: 'Admin', isActive: true, version: 3,
    permissions: [], effectivePermissions: ['stock.view_other_branches', 'payroll.manage', 'payroll.edit_statutory', 'treasury.cash_box'],
    branchId: 'b-main', tillCode: null, tillActive: false, loginId: null, employeeId: null, employeeNumber: null,
    roleId: '5a17e000-0000-4000-8000-0000000000a1', roleName: 'Admin', roleScope: 'Company', additionalPermissions: [], authorizationVersion: 0,
  },
  {
    id: 'u-acc', username: 'n.mansouri', fullName: 'Nadia Mansouri', role: 'Manager', isActive: true, version: 5,
    permissions: ['payroll.manage', 'payroll.edit_statutory', 'treasury.cash_box'],
    effectivePermissions: ['payroll.manage', 'payroll.edit_statutory', 'treasury.cash_box'],
    branchId: 'b-main', tillCode: null, tillActive: false, loginId: '16260412', employeeId: 'e-4', employeeNumber: 'Emp-162109004',
    roleId: '5a17e000-0000-4000-8000-0000000000a2', roleName: 'Manager', roleScope: 'AssignedBranch',
    additionalPermissions: ["treasury.cash_box", "payroll.manage", "payroll.edit_statutory", "manufacturing.view"], authorizationVersion: 4,
  },
  {
    id: 'u-mgr', username: 'r.belkacem', fullName: 'رشيد بلقاسم', role: 'Manager', isActive: true, version: 2,
    permissions: ['hr.attendance', 'stock.view_other_branches'], effectivePermissions: ['hr.attendance', 'stock.view_other_branches'],
    branchId: 'b-oran', tillCode: null, tillActive: false, loginId: '31260203', employeeId: 'e-5', employeeNumber: 'Emp-312302001',
    roleId: '5a17e000-0000-4000-8000-0000000000a2', roleName: 'Manager', roleScope: 'AssignedBranch',
    additionalPermissions: ["stock.view_other_branches", "hr.attendance"], authorizationVersion: 2,
  },
  {
    id: 'u-cash', username: 'a.zerrouki', fullName: 'Amina Zerrouki', role: 'Cashier', isActive: true, version: 4,
    permissions: [], effectivePermissions: [],
    branchId: 'b-oran', tillCode: 'ORN-01', tillActive: true, loginId: '31260518', employeeId: 'e-6', employeeNumber: 'Emp-312505002',
    roleId: '5a17e000-0000-4000-8000-0000000000a3', roleName: 'Cashier', roleScope: 'TillBranch',
    additionalPermissions: ["sales.untaxed", "customers.create"], authorizationVersion: 1, sellsUntaxed: true,
  },
];

const mockDevices = [
  { id: 'd-1', code: 'ALG-01', name: 'Comptoir location — Alger', branchId: 'b-main', branchName: branchNameFr('b-main'), isActive: true, lastSyncAtUtc: '2026-10-15T09:43:12Z', lastCursor: 18422, onWarehouse: false },
  { id: 'd-2', code: 'ALG-02', name: 'Caisse consommables — Alger', branchId: 'b-main', branchName: branchNameFr('b-main'), isActive: true, lastSyncAtUtc: '2026-10-13T16:05:40Z', lastCursor: 18107, onWarehouse: false },
  { id: 'd-3', code: 'ORN-01', name: 'Comptoir — Oran', branchId: 'b-oran', branchName: branchNameFr('b-oran'), isActive: true, lastSyncAtUtc: '2026-10-15T08:17:55Z', lastCursor: 18419, onWarehouse: false },
];

// =====================================================================================
// additional Batch 1 page mocks (dashboard, products, cash boxes — using 'b-main', 'b-oran')
// =====================================================================================

const mockProducts = [
  { id: 'p1', sku: 'ART-0042', barcode: '6131234567890', nameFr: 'Panneau coffrage 0.76 x 3.00 m', nameAr: 'لوح قولبة 0.76 × 3.00 م', nameEn: 'Formwork panel 0.76 x 3.00 m', categoryId: null, categoryName: 'Coffrage', price: 12500, costPrice: 9000, taxRate: 19, unit: 'pcs', trackStock: true, rentalRate: 120, stockQuantity: 45, minStockQuantity: 10, isRentable: true, isActive: true, version: 1 },
  { id: 'p2', sku: 'ART-0043', barcode: '6131234567891', nameFr: 'Étai métallique télescopique 3.50 m', nameAr: 'دعامة معدنية تلسكوبية 3.50 م', nameEn: 'Telescopic steel prop 3.50 m', categoryId: null, categoryName: 'Étaiement', price: 4800, costPrice: 3200, taxRate: 19, unit: 'pcs', trackStock: true, rentalRate: 80, stockQuantity: 120, minStockQuantity: 20, isRentable: true, isActive: true, version: 1 },
  { id: 'p3', sku: 'ART-0044', barcode: '6131234567892', nameFr: 'Cadre échafaudage acier 2.00 m', nameAr: 'إطار سقالة فولاذي 2.00 م', nameEn: 'Steel scaffold frame 2.00 m', categoryId: null, categoryName: 'Échafaudage', price: 8200, costPrice: 6000, taxRate: 19, unit: 'pcs', trackStock: true, rentalRate: 150, stockQuantity: 8, minStockQuantity: 15, isRentable: true, isActive: true, version: 1 },
  { id: 'p4', sku: 'ART-0045', barcode: '6131234567893', nameFr: 'Plancher acier galvanisé 3.00 m', nameAr: 'أرضية فولاذية مجلفنة 3.00 م', nameEn: 'Galvanized steel deck 3.00 m', categoryId: null, categoryName: 'Échafaudage', price: 9500, costPrice: 7200, taxRate: 19, unit: 'pcs', trackStock: true, rentalRate: 110, stockQuantity: 34, minStockQuantity: 10, isRentable: true, isActive: true, version: 1 },
  { id: 'p5', sku: 'BTP-0101', barcode: '6130201001017', nameFr: 'Ciment CPJ 42.5 — sac 50 kg', nameAr: 'إسمنت CPJ 42.5 — كيس 50 كغ', nameEn: 'CPJ 42.5 cement — 50 kg bag', categoryId: null, categoryName: 'Liants', price: 980, costPrice: 760, taxRate: 19, unit: 'sac', trackStock: true, rentalRate: 0, stockQuantity: 640, minStockQuantity: 100, isRentable: false, isActive: true, version: 1 },
  { id: 'p6', sku: 'BTP-0102', barcode: '6130201001024', nameFr: 'Rond à béton HA12 — barre 12 m', nameAr: 'حديد التسليح HA12 — قضيب 12 م', nameEn: 'HA12 rebar — 12 m bar', categoryId: null, categoryName: 'Aciers', price: 1650, costPrice: 1320, taxRate: 19, unit: 'barre', trackStock: true, rentalRate: 0, stockQuantity: 860, minStockQuantity: 150, isRentable: false, isActive: true, version: 1 },
  { id: 'p7', sku: 'LOC-0203', barcode: '6130201002038', nameFr: 'Bétonnière thermique 350 L', nameAr: 'خلاطة خرسانة حرارية 350 لتر', nameEn: 'Petrol concrete mixer 350 L', categoryId: null, categoryName: 'Matériel BTP', price: 185000, costPrice: 142000, taxRate: 19, unit: 'pcs', trackStock: true, rentalRate: 4500, stockQuantity: 3, minStockQuantity: 2, isRentable: true, isActive: true, version: 1 },
  { id: 'p8', sku: 'LOC-0204', barcode: '6130201002045', nameFr: 'Marteau-piqueur électrique 1500 W', nameAr: 'مطرقة هدم كهربائية 1500 واط', nameEn: 'Electric breaker 1500 W', categoryId: null, categoryName: 'Outillage', price: 68000, costPrice: 51000, taxRate: 19, unit: 'pcs', trackStock: true, rentalRate: 2200, stockQuantity: 1, minStockQuantity: 3, isRentable: true, isActive: true, version: 1 },
];

const mockSuppliers = [
  { id: 'sup-1', code: 'FRN-001', name: 'Société Métallurgique Algérienne', phone: '021 55 44 33', email: 'commercial@sma.dz' },
  { id: 'sup-2', code: 'FRN-002', name: 'Équipements & Bâtiment Chéraga', phone: '023 11 22 33', email: 'ebc@equip-dz.com' },
];

const mockCashBoxes = [
  { id: 'cb1', name: 'Caisse Principale (Coffre)', nameFr: 'Caisse Principale (Coffre)', nameAr: 'الصندوق الرئيسي (الخزنة)', nameEn: 'Main Cash Box (Safe)', branchId: 'b-main', balance: 1485000, currency: 'DZD', isMain: true },
  { id: 'cb2', name: 'Caisse Comptoir Vente', nameFr: 'Caisse Comptoir Vente', nameAr: 'صندوق كاونتر البيع', nameEn: 'Counter Till Box', branchId: 'b-main', balance: 92400, currency: 'DZD', isMain: false },
  { id: 'cb3', name: 'Caisse Annexe Oran', nameFr: 'Caisse Annexe Oran', nameAr: 'الصندوق الفرعي وهران', nameEn: 'Oran Branch Box', branchId: 'b-oran', balance: 340000, currency: 'DZD', isMain: false },
];

const mockPaymentMethods = [
  { id: 'm-cash', key: 'Cash', code: 'Cash', nameFr: 'Espèces', nameAr: 'نقداً', nameEn: 'Cash', behavior: 'Cash', isActive: true },
  { id: 'm-cheque', key: 'Cheque', code: 'Cheque', nameFr: 'Chèque', nameAr: 'شيك', nameEn: 'Cheque', behavior: 'BankCheque', isActive: true },
  { id: 'm-transfer', key: 'BankTransfer', code: 'BankTransfer', nameFr: 'Virement bancaire', nameAr: 'تحويل بنكي', nameEn: 'Transfer', behavior: 'BankTransfer', isActive: true },
];

const mockPaymentVouchers = [
  {
    id: 'pv-1',
    number: 'DEC-2026-0015',
    kind: 4, // supplierPayment
    expenseCategory: 'Achats de consommables et pièces',
    payeeName: 'Société Métallurgique Algérienne',
    branchId: 'b-main',
    branchName: 'Succursale Alger Centre',
    methodKey: 'Cash',
    methodName: 'Espèces',
    amount: 85000,
    status: 1, // Pending
    createdAtUtc: '2026-10-12T09:30:00Z',
  },
  {
    id: 'pv-2',
    number: 'DEC-2026-0016',
    kind: 1, // refund
    customerName: 'Karim Belkacem',
    returnNoteNumber: 'RET-2026-0089',
    branchId: 'b-main',
    branchName: 'Succursale Alger Centre',
    methodKey: 'Cash',
    methodName: 'Espèces',
    amount: 12000,
    status: 1, // Pending
    createdAtUtc: '2026-10-14T11:15:00Z',
  },
  {
    id: 'pv-3',
    number: 'DEC-2026-0017',
    kind: 2, // expense
    expenseCategory: 'Transport et logistique',
    payeeName: 'Transporteur Express Centre',
    branchId: 'b-main',
    branchName: 'Succursale Alger Centre',
    methodKey: 'BankTransfer',
    methodName: 'Virement bancaire',
    amount: 25000,
    status: 2, // Approved
    createdAtUtc: '2026-10-15T08:00:00Z',
  },
];

const mockPendingPayments = [
  {
    id: 'pp-1',
    saleId: 'sale-1',
    saleReference: 'TCK-ALG01-000412',
    branchId: 'b-main',
    customerId: 'cust-1',
    customerName: 'SARL Bâtiment Moderne Algérie',
    methodKey: 'Cheque',
    amount: 142000,
    status: 'Pending',
    instrumentNumber: '4471093',
    instrumentBank: 'BNA — Oued Smar',
    instrumentDueDate: '2026-10-25',
    createdAtUtc: '2026-10-12T10:15:00Z',
    clearedAtUtc: null,
    cashBoxId: 'cb1',
    note: null,
  },
  {
    id: 'pp-2',
    saleId: 'sale-2',
    saleReference: 'TCK-ORN01-000411',
    branchId: 'b-oran',
    customerId: 'cust-3',
    customerName: 'EURL Travaux Hydrauliques de l’Est',
    methodKey: 'BankTransfer',
    amount: 64000,
    status: 'Pending',
    instrumentNumber: 'VIR-CPA-3109',
    instrumentBank: 'CPA — Oran',
    instrumentDueDate: '2026-10-22',
    createdAtUtc: '2026-10-14T14:30:00Z',
    clearedAtUtc: null,
    cashBoxId: 'cb3',
    note: null,
  },
];

const mockLowStock = [
  {
    productId: 'p3',
    sku: 'ART-0044',
    nameAr: 'إطار سقالة فولاذي 2.00 م',
    nameFr: 'Cadre échafaudage acier 2.00 m',
    nameEn: 'Steel scaffold frame 2.00 m',
    branchId: 'b-main',
    branchName: 'Succursale Alger Centre',
    stockQty: 8,
    reorderLevel: 15,
  },
  {
    productId: 'p8',
    sku: 'LOC-0204',
    nameAr: 'مطرقة هدم كهربائية 1500 واط',
    nameFr: 'Marteau-piqueur électrique 1500 W',
    nameEn: 'Electric breaker 1500 W',
    branchId: 'b-main',
    branchName: 'Succursale Alger Centre',
    stockQty: 1,
    reorderLevel: 3,
  },
];

const mockRentalContracts = [
  {
    id: 'c-1001',
    number: 'LOC-2026-0042',
    customerId: 'cust-1',
    customerName: 'SARL Bâtiment Moderne Algérie',
    branchId: 'b-main',
    status: 'Open',
    isLocked: true,
    lockedAtUtc: '2026-10-01T08:15:00Z',
    signedAtUtc: '2026-10-01T08:30:00Z',
    closedAtUtc: null,
    issuedQty: 50,
    settledQty: 6,
    stillOutQty: 44,
    chargeTotal: 142000,
    overdueTotal: 0,
    depositAmount: 50000,
  },
  {
    id: 'c-1002',
    number: 'LOC-2026-0045',
    customerId: 'cust-3',
    customerName: 'EURL Travaux Hydrauliques de l’Est',
    branchId: 'b-oran',
    status: 'Open',
    isLocked: true,
    lockedAtUtc: '2026-10-06T09:00:00Z',
    signedAtUtc: '2026-10-06T09:20:00Z',
    closedAtUtc: null,
    issuedQty: 20,
    settledQty: 0,
    stillOutQty: 20,
    chargeTotal: 96000,
    overdueTotal: 18000,
    depositAmount: 30000,
  },
  {
    id: 'c-1003',
    number: 'LOC-2026-0048',
    customerId: 'cust-2',
    customerName: 'Karim Belkacem',
    branchId: 'b-main',
    status: 'Open',
    isLocked: false,
    lockedAtUtc: null,
    signedAtUtc: null,
    closedAtUtc: null,
    issuedQty: 0,
    settledQty: 0,
    stillOutQty: 0,
    chargeTotal: 28000,
    overdueTotal: 0,
    depositAmount: null,
  },
];

const mockDebtAgeing = {
  asOf: '2026-10-15T00:00:00Z',
  total: 730000,
  current: 560000,
  days1To30: 142000,
  days31To60: 28000,
  days61To90: 0,
  over90: 0,
  creditBalance: 0,
  ledgerBalance: 730000,
  uninvoiced: 0,
  customers: [],
};

const mockProfile = {
  id: 'u-admin',
  fullName: 'Chef de Parc / Directeur',
  username: 'admin',
  loginId: null,
  role: 'Admin',
  roleName: 'Admin',
  branchNameAr: 'فرع الجزائر الوسطى',
  branchNameFr: 'Succursale Alger Centre',
  branchNameEn: 'Algiers Centre branch',
  employee: null,
  photoVersion: null,
  twoFactor: {
    enabled: false,
    enabledAtUtc: null,
    recoveryCodesLeft: 0,
  },
};

// =====================================================================================
// the API
// =====================================================================================

/** The answer to one request, or null when nothing here knows the route (the default then answers). */
function mockApi(method, url, body) {
  const p = url.pathname;
  const q = url.searchParams;
  const json = (respBody, status = 200) => ({ status, body: respBody });

  // ---- session and sync (Batch 3 verbatim + two-step login for username 'twostep') ----
  if (p === '/api/auth/login') {
    if (body && body.username === 'twostep' && !body.twoFactorCode) {
      return json({ code: 'two_factor_required', message: 'Two-factor authentication code required.' }, 401);
    }
    return json({
      token: 'mock-jwt-token',
      expiresAtUtc: '2099-01-01T00:00:00Z',
      user: mockUser,
      // An office session (no till): the back office opens straight away.
      deviceCode: '',
      deviceBranchId: null,
      regional,
      subscription,
    });
  }
  if (p === '/api/auth/ping') return json({ ok: true });
  if (p === '/api/auth/sso/config') return json({ enabled: false, isPlatform: false });
  if (p === '/api/auth/profile') return json(mockProfile);
  if (p === '/api/auth/logout') return json({ ok: true });
  if (p === '/api/sync/pull') {
    return json({
      cursor: 1,
      hasMore: false,
      serverTimeUtc: new Date().toISOString(),
      regional,
      subscription,
      store: mockStore,
      branches: mockBranches,
      categories: [],
      products: [],
      customers: [],
      users: [],
      giftCards: [],
      paymentMethods: [],
    });
  }
  if (p === '/api/sync/push') return json({ cursor: 1, serverTimeUtc: new Date().toISOString(), accepted: 0, duplicates: 0, rejected: 0, results: [] });

  // ---- company, branches, tills, users (Batch 3 verbatim) ----
  if (p === '/api/catalog/branches') return json(mockBranches);
  if (p === '/api/catalog/store') return json(mockStore);
  if (p === '/api/catalog/store/documents') return json(mockStoreDocuments);
  if (p === '/api/catalog/store/documents/history') return json(mockDocumentHistory);
  if (p === '/api/devices') return json(mockDevices);
  if (p === '/api/users') return json(mockUsers);
  if (p === '/api/permissions') return json(mockCatalogue);
  if (p === '/api/roles') return json(mockRoles);

  // ---- personnel (Batch 3 verbatim) ----
  if (p === '/api/hr/employees') {
    const active = q.get('active');
    const branchId = q.get('branchId');
    const text = (q.get('q') || '').trim().toLowerCase();
    const skip = Number(q.get('skip')) || 0;
    const take = Number(q.get('take')) || 50;
    const rows = mockEmployees.filter((e) => {
      const here = e.isActive && !e.leftOn;
      if (active === 'true' && !here) return false;
      if (active === 'false' && here) return false;
      if (branchId && e.branchId !== branchId) return false;
      return !text || e.fullName.toLowerCase().includes(text) || e.number.toLowerCase().includes(text);
    });
    return json({ total: rows.length, skip, take, rows: rows.slice(skip, skip + take) });
  }
  const oneEmployee = /^\/api\/hr\/employees\/([^/]+)$/.exec(p);
  if (oneEmployee) {
    const found = emp(oneEmployee[1]);
    return found ? json(found) : json({ code: 'notFound', message: 'No such employee.' }, 404);
  }

  // ---- sales & the books (Batch 3 verbatim) ----
  if (p === '/api/reports/summary' || p === '/api/sales/summary') return json(mockSalesSummary);
  if (p === '/api/sales') return json(mockSales);
  if (p === '/api/ledger/accounts') return json(ledgerAccounts);
  if (p === '/api/ledger/reports/trial-balance') return json(trialBalance(q));
  if (p === '/api/ledger/reports/profit-and-loss') return json(profitAndLoss(q));

  // ---- catalog products, suppliers, low stock (Batch 1 additions) ----
  if (p === '/api/catalog/products') return json(mockProducts);
  if (p === '/api/catalog/categories') return json([]);
  if (p === '/api/catalog/suppliers') return json(mockSuppliers);
  if (p === '/api/catalog/products/low-stock' || p === '/api/stock/low') {
    const branchId = q.get('branchId');
    return json(branchId ? mockLowStock.filter((r) => r.branchId === branchId) : mockLowStock);
  }

  // ---- treasury, finance & dashboard tiles (Batch 1 additions) ----
  if (p === '/api/treasury/cash-boxes') {
    const branchId = q.get('branchId');
    return json(branchId ? mockCashBoxes.filter((b) => b.branchId === branchId) : mockCashBoxes);
  }
  if (p === '/api/catalog/payment-methods' || p === '/api/treasury/payment-methods') {
    return json(mockPaymentMethods);
  }
  if (p === '/api/treasury/pending-payments') {
    const status = q.get('status');
    const branchId = q.get('branchId');
    let rows = mockPendingPayments;
    if (status) rows = rows.filter((r) => r.status === status);
    if (branchId) rows = rows.filter((r) => r.branchId === branchId);
    return json(rows);
  }
  if (p === '/api/payment-vouchers') {
    const statusParam = q.get('status');
    const branchId = q.get('branchId');
    let rows = mockPaymentVouchers;
    if (statusParam !== null && statusParam !== '') {
      const s = parseInt(statusParam, 10);
      rows = rows.filter((v) => v.status === s);
    }
    if (branchId) rows = rows.filter((v) => v.branchId === branchId);
    return json(rows);
  }
  if (p === '/api/receivables/ageing') return json(mockDebtAgeing);
  if (p === '/api/rentals/contracts') {
    const status = q.get('status');
    const branchId = q.get('branchId');
    let rows = mockRentalContracts;
    if (status) rows = rows.filter((r) => r.status === status);
    if (branchId) rows = rows.filter((r) => r.branchId === branchId);
    return json(rows);
  }
  if (p === '/api/maintenance/settings') {
    return json({ module: 'On', intakeCashBoxId: 'cb1', workshopLocationId: null, updatedAtUtc: '2026-10-01T08:00:00Z' });
  }

  return null;
}

// =====================================================================================
// the run
// =====================================================================================

const READY = {
  login: ['تسجيل الدخول', 'كلمة المرور', 'Connexion', 'Mot de passe'],
  dashboard: ['فرع وهران', 'Succursale Oran'],
  'inventory/products': ['ART-0042', 'Panneau coffrage 0.76 x 3.00 m', 'لوح قولبة 0.76 × 3.00 م'],
  'finance/cash-boxes': ['Caisse Principale (Coffre)', 'الصندوق الرئيسي (الخزنة)'],
  'admin/users': ['Nadia Mansouri', 'رشيد بلقاسم'],
};

const notes = [];
function note(message) {
  console.log(message);
  notes.push(message);
}

async function openTwoStep(activePage) {
  if (await activePage.locator('#server').isVisible().catch(() => false)) {
    await activePage.fill('#server', BASE);
  }
  await activePage.fill('#username', 'twostep');
  await activePage.fill('#password', 'AnyPassword123');
  await activePage.click('button[type=submit]');
  await activePage.locator('#two-factor-code').waitFor({ state: 'visible', timeout: 8000 });
  await activePage.waitForTimeout(300);
}

async function collapseSidebar(activePage) {
  await activePage.locator('.shell-sidebar .menu__toggle').first().click({ timeout: 5000 });
  await activePage.waitForTimeout(300);
}

async function collapseAndOpenRentalsRail(activePage) {
  await activePage.locator('.shell-sidebar .menu__toggle').first().click({ timeout: 5000 });
  await activePage.waitForTimeout(300);
  const rail = activePage.locator('.shell-sidebar .mod.tone-rentals .mod__row').first();
  if ((await rail.count()) > 0) {
    await rail.click({ timeout: 5000 });
    await activePage.waitForTimeout(300);
  } else {
    note('  (no rentals module in this menu)');
  }
}

async function openMobileDrawer(activePage) {
  await activePage.locator('.bar__menu').first().click({ timeout: 5000 });
  await activePage.waitForTimeout(300);
}

async function searchProInNav(activePage) {
  const input = activePage.locator('app-bo-nav-search input').first();
  if ((await input.count()) > 0) {
    await input.click({ timeout: 5000 });
    await input.fill('pro');
    await activePage.waitForTimeout(300);
  } else {
    note('  (no page search in this bundle)');
  }
}

/** Every screen, in order. `ready`: any of these texts (or input values) means the page shows its data. */
const SHOTS = [
  // Sign-in context:
  { file: '01-login-ar-1440.png', context: 'signin', route: 'login', lang: 'ar', vp: DESKTOP, ready: ['تسجيل الدخول', 'كلمة المرور'] },
  { file: '02-login-fr-1440.png', context: 'signin', route: 'login', lang: 'fr', vp: DESKTOP, ready: ['Connexion', 'Mot de passe'] },
  { file: '03-login-ar-390.png', context: 'signin', route: 'login', lang: 'ar', vp: PHONE, ready: ['تسجيل الدخول', 'كلمة المرور'] },
  { file: '04-login-two-step-ar-1440.png', context: 'signin', route: 'login', lang: 'ar', vp: DESKTOP, ready: ['تسجيل الدخول', 'كلمة المرور'], act: openTwoStep },
  { file: '05-login-two-step-ar-390.png', context: 'signin', route: 'login', lang: 'ar', vp: PHONE, ready: ['تسجيل الدخول', 'كلمة المرور'], act: openTwoStep },

  // Main context (signed in as the admin):
  { file: '10-dashboard-ar-1440.png', context: 'main', route: 'dashboard', lang: 'ar', vp: DESKTOP, ready: READY.dashboard },
  { file: '11-dashboard-fr-1440.png', context: 'main', route: 'dashboard', lang: 'fr', vp: DESKTOP, ready: READY.dashboard },
  { file: '12-dashboard-ar-390.png', context: 'main', route: 'dashboard', lang: 'ar', vp: PHONE, ready: READY.dashboard },

  { file: '20-products-ar-1440.png', context: 'main', route: 'inventory/products', lang: 'ar', vp: DESKTOP, ready: READY['inventory/products'] },
  { file: '21-products-fr-1440.png', context: 'main', route: 'inventory/products', lang: 'fr', vp: DESKTOP, ready: READY['inventory/products'] },
  { file: '22-products-ar-390.png', context: 'main', route: 'inventory/products', lang: 'ar', vp: PHONE, ready: READY['inventory/products'] },

  { file: '30-cash-boxes-ar-1440.png', context: 'main', route: 'finance/cash-boxes', lang: 'ar', vp: DESKTOP, ready: READY['finance/cash-boxes'] },
  { file: '31-cash-boxes-fr-1440.png', context: 'main', route: 'finance/cash-boxes', lang: 'fr', vp: DESKTOP, ready: READY['finance/cash-boxes'] },
  { file: '32-cash-boxes-ar-390.png', context: 'main', route: 'finance/cash-boxes', lang: 'ar', vp: PHONE, ready: READY['finance/cash-boxes'] },

  { file: '40-users-ar-1440.png', context: 'main', route: 'admin/users', lang: 'ar', vp: DESKTOP, ready: READY['admin/users'] },
  { file: '41-users-fr-1440.png', context: 'main', route: 'admin/users', lang: 'fr', vp: DESKTOP, ready: READY['admin/users'] },
  { file: '42-users-ar-390.png', context: 'main', route: 'admin/users', lang: 'ar', vp: PHONE, ready: READY['admin/users'] },

  { file: '50-menu-collapsed-ar-1440.png', context: 'main', route: 'dashboard', lang: 'ar', vp: DESKTOP, fullPage: false, ready: READY.dashboard, act: collapseSidebar },
  { file: '51-menu-collapsed-floating-ar-1440.png', context: 'main', route: 'dashboard', lang: 'ar', vp: DESKTOP, fullPage: false, ready: READY.dashboard, act: collapseAndOpenRentalsRail },
  { file: '52-menu-tablet-ar-900.png', context: 'main', route: 'dashboard', lang: 'ar', vp: TABLET, fullPage: false, ready: READY.dashboard },
  { file: '53-drawer-open-ar-390.png', context: 'main', route: 'dashboard', lang: 'ar', vp: PHONE, fullPage: false, ready: READY.dashboard, act: openMobileDrawer },
  { file: '54-drawer-open-fr-390.png', context: 'main', route: 'dashboard', lang: 'fr', vp: PHONE, fullPage: false, ready: READY.dashboard, act: openMobileDrawer },
  { file: '55-page-search-fr-1440.png', context: 'main', route: 'dashboard', lang: 'fr', vp: DESKTOP, fullPage: false, ready: READY.dashboard, act: searchProInNav },

  { file: '60-dashboard-dark-ar-1440.png', context: 'main', route: 'dashboard', lang: 'ar', vp: DESKTOP, appearance: '{"mode":"dark","theme":"atelier"}', ready: READY.dashboard },
  { file: '61-products-dark-fr-1440.png', context: 'main', route: 'inventory/products', lang: 'fr', vp: DESKTOP, appearance: '{"mode":"dark","theme":"atelier"}', ready: READY['inventory/products'] },
  { file: '62-login-dark-ar-1440.png', context: 'signin', route: 'login', lang: 'ar', vp: DESKTOP, appearance: '{"mode":"dark","theme":"atelier"}', ready: ['تسجيل الدخول', 'كلمة المرور'] },

  { file: '70-dashboard-oasis-ar-1440.png', context: 'main', route: 'dashboard', lang: 'ar', vp: DESKTOP, appearance: '{"mode":"light","theme":"oasis"}', ready: READY.dashboard },
  { file: '71-dashboard-sahara-dark-ar-1440.png', context: 'main', route: 'dashboard', lang: 'ar', vp: DESKTOP, appearance: '{"mode":"dark","theme":"sahara"}', ready: READY.dashboard },
  { file: '72-users-graphite-fr-1440.png', context: 'main', route: 'admin/users', lang: 'fr', vp: DESKTOP, appearance: '{"mode":"light","theme":"graphite"}', ready: READY['admin/users'] },
  { file: '73-products-atlas-ar-1440.png', context: 'main', route: 'inventory/products', lang: 'ar', vp: DESKTOP, appearance: '{"mode":"light","theme":"atlas"}', ready: READY['inventory/products'] },
];

/** Waits until any of the texts is on screen, as text or as a field's value. */
async function waitForAny(activePage, texts, timeout = READY_TIMEOUT) {
  await activePage.waitForFunction(
    (list) => {
      const body = document.body ? document.body.innerText : '';
      if (list.some((t) => body.includes(t))) return true;
      return Array.from(document.querySelectorAll('input, textarea')).some((el) => list.some((t) => (el.value || '').includes(t)));
    },
    texts,
    { timeout, polling: 250 },
  );
}

async function open(activePage, shot) {
  const vp = shot.vp || DESKTOP;
  await activePage.setViewportSize(vp);
  const url = shot.route === 'login' ? `${BASE}/#/login` : `${BASE}/#/backoffice/${shot.route}`;
  const wantedAppearance = shot.appearance !== undefined ? shot.appearance : APPEARANCE;
  await activePage.goto(url);
  await activePage.evaluate(
    ([l, look]) => {
      localStorage.setItem('pos.lang', l);
      if (look) localStorage.setItem('pos.appearance.v3', look);
      else localStorage.removeItem('pos.appearance.v3');
      localStorage.removeItem('algeriapos.bo.sidebarCollapsed');
    },
    [shot.lang, wantedAppearance],
  );
  await activePage.reload();
  await activePage.waitForLoadState('domcontentloaded');
}

async function settle(activePage) {
  await activePage.evaluate(() => (document.fonts ? document.fonts.ready.then(() => true) : true)).catch(() => {});
  await activePage.evaluate(() => window.scrollTo(0, 0));
  await activePage.waitForTimeout(700);
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
  console.log(`Paradis Batch 1 screenshots — set "${SHOT_SET}" (UI_FONT=${UI_FONT})`);
  console.log(`  bundle: ${path.relative(root, webDist) || webDist}`);
  console.log(`  output: ${path.relative(root, out)}`);

  const server = await serve();
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || undefined });
  const fellThrough = new Map();
  const pageErrors = new Map();
  const results = [];
  let current = 'sign-in';

  async function wireContextPage(ctxPage) {
    ctxPage.on('pageerror', (error) => {
      const key = `${current}: ${String(error.message).split('\n')[0]}`;
      pageErrors.set(key, (pageErrors.get(key) ?? 0) + 1);
    });

    await ctxPage.route('**/api/**', async (route) => {
      const request = route.request();
      const url = new URL(request.url());
      let body = null;
      if (request.method() === 'POST' || request.method() === 'PUT' || request.method() === 'PATCH') {
        try {
          body = request.postDataJSON();
        } catch {
          body = null;
        }
      }
      let answer = null;
      try {
        answer = mockApi(request.method(), url, body);
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
  }

  try {
    // Sign-in context: fresh signed-out browser context for #/login shots.
    const signinContext = await browser.newContext({ viewport: DESKTOP, serviceWorkers: 'block', timezoneId: 'Africa/Algiers' });
    const signinPage = await signinContext.newPage();
    await wireContextPage(signinPage);

    // Main context: signed in lazily after initial sign-in shots run.
    const mainContext = await browser.newContext({ viewport: DESKTOP, serviceWorkers: 'block', timezoneId: 'Africa/Algiers' });
    const mainPage = await mainContext.newPage();
    await wireContextPage(mainPage);
    let mainSignedIn = false;

    async function ensureMainSignedIn() {
      if (mainSignedIn) return;
      const prev = current;
      current = 'sign-in';
      await mainPage.goto(`${BASE}/#/login`);
      await mainPage.evaluate(() => localStorage.setItem('pos.lang', 'ar'));
      await mainPage.reload();
      await mainPage.waitForSelector('#username', { timeout: READY_TIMEOUT });
      if (await mainPage.locator('#server').isVisible().catch(() => false)) await mainPage.fill('#server', BASE);
      await mainPage.fill('#username', 'admin');
      await mainPage.fill('#password', 'AnyPassword123');
      await mainPage.click('button[type=submit]');
      await mainPage.waitForURL((u) => !u.hash.startsWith('#/login'), { timeout: 30_000 });
      await settle(mainPage);
      console.log(`Signed in (landed on ${new URL(mainPage.url()).hash || '/'})`);
      mainSignedIn = true;
      current = prev;
    }

    for (const shot of SHOTS.filter(chosen)) {
      current = shot.file;
      const target = path.join(out, shot.file);
      const problems = [];
      let overflowCulprits = [];
      const isSignin = shot.context === 'signin';
      if (!isSignin) {
        await ensureMainSignedIn();
      }
      const activePage = isSignin ? signinPage : mainPage;

      try {
        await open(activePage, shot);
        const readyTexts = shot.ready || READY[shot.route];
        if (readyTexts) {
          await waitForAny(activePage, readyTexts).catch(() =>
            problems.push(`no data on screen (waited for: ${readyTexts.join(' | ')})`),
          );
        }
        if (shot.act && problems.length === 0) {
          await shot.act(activePage).catch((e) => problems.push(`action failed: ${e.message.split('\n')[0]}`));
        }
        const dir = await activePage.evaluate(() => document.documentElement.getAttribute('dir') || document.dir || 'ltr');
        if ((shot.lang === 'ar') !== (dir === 'rtl')) {
          problems.push(`page is ${dir}, expected ${shot.lang === 'ar' ? 'rtl' : 'ltr'}`);
        }
        await settle(activePage);

        const scrollInfo = await activePage.evaluate(() => {
          const scrollWidth = document.documentElement.scrollWidth;
          const innerWidth = window.innerWidth;
          const culprits = [];
          if (scrollWidth > innerWidth + 1) {
            const all = document.querySelectorAll('body *');
            for (const el of all) {
              const r = el.getBoundingClientRect();
              if (r.width === 0 && r.height === 0) continue;
              if (r.left < -1 || r.right > innerWidth + 1) {
                const cls =
                  typeof el.className === 'string' && el.className.trim()
                    ? '.' + el.className.trim().split(/\s+/).join('.')
                    : '';
                culprits.push(`${el.tagName.toLowerCase()}${cls} (left=${Math.round(r.left)}px, right=${Math.round(r.right)}px)`);
                if (culprits.length >= 3) break;
              }
            }
          }
          return { scrollWidth, innerWidth, culprits };
        });
        if (scrollInfo.scrollWidth > scrollInfo.innerWidth + 1) {
          problems.push(`scrolls sideways (scrollWidth ${scrollInfo.scrollWidth} > ${scrollInfo.innerWidth})`);
          overflowCulprits = scrollInfo.culprits;
        }

        await activePage.screenshot({ path: target, fullPage: shot.fullPage !== false });

        if (shot.appearance) {
          await activePage
            .evaluate((defaultLook) => {
              if (defaultLook) localStorage.setItem('pos.appearance.v3', defaultLook);
              else localStorage.removeItem('pos.appearance.v3');
            }, APPEARANCE)
            .catch(() => {});
        }

        if (problems.length) {
          results.push({ file: shot.file, status: 'warn', problems, overflowCulprits });
          console.log(`  wrote ${shot.file}  (WARN: ${problems.join('; ')})`);
        } else {
          results.push({ file: shot.file, status: 'ok', problems: [], overflowCulprits: [] });
          console.log(`  wrote ${shot.file}`);
        }
      } catch (error) {
        problems.push(error.message.split('\n')[0]);
        results.push({ file: shot.file, status: 'failed', problems, overflowCulprits });
        console.log(`  FAILED ${shot.file}: ${problems.join('; ')}`);
      }
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
  for (const r of [...warned, ...failed]) {
    console.log(`  ${r.status.toUpperCase()} ${r.file}: ${r.problems.join('; ')}`);
    if (r.overflowCulprits && r.overflowCulprits.length) {
      console.log(`    overflowing elements: ${r.overflowCulprits.join(' | ')}`);
    }
  }

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
