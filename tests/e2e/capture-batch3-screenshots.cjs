// Captures Batch 3 screenshots for the Phase 4 redesign review (employees, payroll, reports, admin).
//
//   SHOT_SET=before WEB_DIST=AlgeriaPos.WebUI/dist/before/browser node tests/e2e/capture-batch3-screenshots.cjs
//   SHOT_SET=after node tests/e2e/capture-batch3-screenshots.cjs
//
// Serves an already-built bundle (WEB_DIST, default AlgeriaPos.WebUI/dist/algeria-pos-web-ui/browser;
// a relative path is read against the repo root) on 127.0.0.1:5212, answers every /api call from the
// mocks below, signs in through #/login and writes full-page PNGs to
// artifacts/ui-redesign-checkpoint/batch3/<SHOT_SET>. It never builds anything.
//
// The mocks describe one company: an Algerian construction-equipment rental business with an Algiers
// and an Oran branch (the Batch 2 branches and admin), seven employees, October 2026 attendance, three
// payroll runs, the month's sales, a small trial balance and P&L, four users and three tills. Every
// shape follows the TypeScript interface the screen reads (hr-api.service.ts, payroll-api.service.ts,
// payroll-run-view.ts, core/admin.service.ts, core/ledger.service.ts, store-documents-api.service.ts).
//
// A page that does not render its data is logged and captured anyway; nothing stops the run. At the
// end the script lists every /api path that fell through to the default mock.
//
// Two options for a later review of one screen (the users screen's permissions, 2026-11-05):
//   OUT_DIR=<folder>       writes there instead (a relative path is read against the repo root);
//   SHOTS_ONLY=08,10       captures only the shots whose file name starts with one of these;
//   APPEARANCE='{"mode":"dark","theme":"atlas"}'   the theme and mode saved before each page loads
//                          (core/theme.service.ts, pos.appearance.v3).

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
  : path.join(root, 'artifacts/ui-redesign-checkpoint/batch3', SHOT_SET);
const ONLY = (process.env.SHOTS_ONLY || '').split(',').map((x) => x.trim()).filter(Boolean);
const chosen = (shot) => ONLY.length === 0 || ONLY.some((prefix) => shot.file.startsWith(prefix));
const APPEARANCE = (process.env.APPEARANCE || '').trim() || null;
if (APPEARANCE) JSON.parse(APPEARANCE);
const webDist = path.resolve(root, process.env.WEB_DIST || 'AlgeriaPos.WebUI/dist/algeria-pos-web-ui/browser');

const PORT = 5212;
const BASE = `http://127.0.0.1:${PORT}`;
const DESKTOP = { width: 1440, height: 960 };
const PHONE = { width: 390, height: 844 };
const READY_TIMEOUT = 15_000;

// =====================================================================================
// the company
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
  uiFont: 'ibm-plex-sans-arabic',
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

// ---------- payroll settings (PayrollSettingsView) ----------

const overtimeRules = [
  { key: 'hs-50', labels: { fr: 'Heures sup. jour (+50 %)', ar: 'ساعات إضافية نهارية (+50%)', en: 'Daytime overtime (+50%)' }, appliesTo: 'Weekday', unit: 'Hours', multiplier: 1.5, subjectToCnas: true, subjectToIrg: true, isActive: true },
  { key: 'hs-75', labels: { fr: 'Heures de nuit (+75 %)', ar: 'ساعات ليلية (+75%)', en: 'Night hours (+75%)' }, appliesTo: 'Night', unit: 'Hours', multiplier: 1.75, subjectToCnas: true, subjectToIrg: true, isActive: true },
  { key: 'hs-100', labels: { fr: 'Repos hebdo / férié (+100 %)', ar: 'راحة أسبوعية / عطلة رسمية (+100%)', en: 'Rest day / holiday (+100%)' }, appliesTo: 'WeeklyRest', unit: 'Hours', multiplier: 2, subjectToCnas: true, subjectToIrg: true, isActive: true },
];

const payrollSettings = {
  countryCode: 'DZ',
  socialEmployeeRate: 9,
  socialEmployerRate: 26,
  socialNames: { fr: 'CNAS', ar: 'الضمان الاجتماعي', en: 'Social security (CNAS)' },
  socialBaseFloor: null,
  socialBaseCeiling: null,
  employerLevies: [],
  incomeTaxMode: 'Scale',
  incomeTaxBrackets: [
    { upTo: 20000, rate: 0 },
    { upTo: 40000, rate: 23 },
    { upTo: 80000, rate: 27 },
    { upTo: 160000, rate: 30 },
    { upTo: 320000, rate: 33 },
    { upTo: null, rate: 35 },
  ],
  incomeTaxNames: { fr: 'IRG', ar: 'الضريبة على الدخل', en: 'Income tax (IRG)' },
  employeeContributionReducesTaxBase: true,
  minimumWage: 20000,
  allowances: [
    { key: 'panier', labels: { fr: 'Prime de panier', ar: 'منحة القفة', en: 'Meal allowance' }, calculation: 'FixedAmount', value: 4400, subjectToCnas: false, subjectToIrg: true, isActive: true, sortOrder: 1 },
    { key: 'transport', labels: { fr: 'Indemnité de transport', ar: 'منحة النقل', en: 'Transport allowance' }, calculation: 'FixedAmount', value: 3000, subjectToCnas: false, subjectToIrg: true, isActive: true, sortOrder: 2 },
    { key: 'rendement', labels: { fr: 'Prime de rendement', ar: 'منحة المردودية', en: 'Performance bonus' }, calculation: 'PercentOfBase', value: 10, subjectToCnas: true, subjectToIrg: true, isActive: false, sortOrder: 3 },
  ],
  overtimeRules,
  deductions: [],
  defaultAttendanceMode: 'Declared',
  workingDaysPerMonth: 22,
  hoursPerDay: 8,
  schedule: {
    start: '08:00',
    end: '16:30',
    breakMinutes: 60,
    lateGraceMinutes: 10,
    restDays: [5, 6],
    periods: [{ name: "Horaire d'été", from: '2026-06-15', to: '2026-09-15', start: '06:30', end: '13:30', breakMinutes: 30 }],
  },
  absenceOffGross: false,
  netPayOnly: false,
  confirmed: true,
  confirmedAtUtc: '2026-09-28T10:15:00Z',
  confirmedBy: 'Cabinet comptable Benyahia',
  needsReview: false,
};

const payrollSettingsView = {
  settings: payrollSettings,
  revision: 12,
  owners: {
    socialEmployeeRate: 'Operator',
    socialEmployerRate: 'Operator',
    socialNames: 'Operator',
    socialBaseFloor: 'Operator',
    socialBaseCeiling: 'Operator',
    employerLevies: 'Operator',
    incomeTaxMode: 'Operator',
    incomeTaxBrackets: 'Operator',
    incomeTaxNames: 'Operator',
    employeeContributionReducesTaxBase: 'Operator',
    minimumWage: 'Operator',
    confirmed: 'Operator',
    confirmedAtUtc: 'Operator',
    confirmedBy: 'Operator',
    needsReview: 'Operator',
    countryCode: 'Operator',
    allowances: 'Company',
    overtimeRules: 'Company',
    deductions: 'Company',
    defaultAttendanceMode: 'Company',
    workingDaysPerMonth: 'Company',
    hoursPerDay: 'Company',
    schedule: 'Company',
    absenceOffGross: 'Company',
    netPayOnly: 'Company',
  },
  canEditCompany: true,
  canEditStatutory: true,
  updatedBy: 'admin',
  updatedAtUtc: '2026-10-01T09:12:00Z',
};

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

// ---------- attendance, October 2026 (AttendanceSheet) ----------
// Eleven working days have passed (Sunday to Thursday weeks); Friday and Saturday are rest days.

const sheetRows = {
  'b-main': [
    { employeeId: 'e-1', mode: 'Declared', readOnly: false, daysPresent: 11, paidLeaveDays: 0, unpaidAbsenceDays: 0, overtime: [{ ruleKey: 'hs-50', hours: 6 }], source: 'Declared', unpairedClockIns: 0 },
    { employeeId: 'e-2', mode: 'Declared', readOnly: false, daysPresent: 9, paidLeaveDays: 1, unpaidAbsenceDays: 1, overtime: [{ ruleKey: 'hs-50', hours: 4 }, { ruleKey: 'hs-100', hours: 8 }], source: 'Declared', unpairedClockIns: 0 },
    { employeeId: 'e-3', mode: 'Recorded', readOnly: true, daysPresent: 10, paidLeaveDays: 0, unpaidAbsenceDays: 1, overtime: [{ ruleKey: 'hs-75', hours: 3 }], source: 'Recorded', unpairedClockIns: 2 },
    { employeeId: 'e-4', mode: 'Declared', readOnly: false, daysPresent: 11, paidLeaveDays: 0, unpaidAbsenceDays: 0, overtime: [], source: 'Declared', unpairedClockIns: 0 },
  ],
  'b-oran': [
    { employeeId: 'e-5', mode: 'Declared', readOnly: false, daysPresent: 10, paidLeaveDays: 0, unpaidAbsenceDays: 1, overtime: [{ ruleKey: 'hs-50', hours: 5 }], source: 'Declared', unpairedClockIns: 0 },
    { employeeId: 'e-6', mode: 'Declared', readOnly: false, daysPresent: 0, paidLeaveDays: 0, unpaidAbsenceDays: 0, overtime: [], source: 'None', unpairedClockIns: 0 },
  ],
};

function attendanceSheet(query) {
  const branchId = query.get('branchId') || 'b-main';
  const q = (query.get('q') || '').trim().toLowerCase();
  const rows = (sheetRows[branchId] ?? [])
    .map((r) => {
      const e = emp(r.employeeId);
      return {
        employeeNumber: e.number,
        employeeName: e.fullName,
        payBasis: e.payBasis,
        updatedBy: r.source === 'None' ? null : r.source === 'Recorded' ? null : 'admin',
        updatedAtUtc: r.source === 'Declared' ? '2026-10-15T07:40:00Z' : null,
        ...r,
      };
    })
    .filter((r) => !q || r.employeeName.toLowerCase().includes(q) || r.employeeNumber.toLowerCase().includes(q));
  return {
    branchId,
    periodYear: Number(query.get('year')) || 2026,
    periodMonth: Number(query.get('month')) || 10,
    locked: false,
    runId: null,
    runNumber: null,
    runStatus: null,
    companyDefaultMode: 'Declared',
    overtimeRules: overtimeRules.map(({ key, labels, multiplier, unit }) => ({ key, labels, multiplier, unit })),
    total: rows.length,
    skip: 0,
    take: Number(query.get('take')) || 100,
    rows,
  };
}

// ---------- the fingerprint export, previewed (PunchPreview) ----------

const punchPeople = ['e-1', 'e-2', 'e-3', 'e-4'].map(emp);
const punchRows = [];
{
  let row = 2;
  const days = ['2026-10-01', '2026-10-04', '2026-10-05'];
  const times = { 'e-1': ['07:52', '16:35'], 'e-2': ['07:58', '16:41'], 'e-3': ['08:14', '16:30'], 'e-4': ['08:03', '16:32'] };
  for (const day of days) {
    for (const e of punchPeople) {
      times[e.id].forEach((time, i) => {
        const [y, m, d] = day.split('-');
        punchRows.push({
          row: row++,
          status: 'matched',
          problem: null,
          number: e.fingerprintNumber,
          name: `${e.familyName} ${e.firstName}`.toUpperCase(),
          at: `${day}T${time}:00`,
          marker: i === 0 ? 'Entrée' : 'Sortie',
          source: `${d}/${m}/${y} ${time}`,
          employeeId: e.id,
          employeeNumber: e.number,
          employeeName: e.fullName,
          flags: e.id === 'e-3' && i === 0 ? ['earlyEntry'] : [],
        });
      });
    }
  }
  punchRows.push(
    { row: row++, status: 'unknown', problem: null, number: '0117', name: 'KHELIFI MOURAD', at: '2026-10-01T07:45:00', marker: 'Entrée', source: '01/10/2026 07:45', flags: [] },
    { row: row++, status: 'unknown', problem: null, number: '0117', name: 'KHELIFI MOURAD', at: '2026-10-01T16:02:00', marker: 'Sortie', source: '01/10/2026 16:02', flags: [] },
    { row: row++, status: 'unknown', problem: null, number: '0123', name: null, at: '2026-10-04T08:20:00', marker: 'Entrée', source: '04/10/2026 08:20', flags: [] },
    { row: row++, status: 'otherBranch', problem: null, number: '0201', name: 'BELKACEM RACHID', at: '2026-10-05T07:55:00', marker: 'Entrée', source: '05/10/2026 07:55', employeeId: 'e-5', employeeNumber: 'Emp-312302001', employeeName: 'رشيد بلقاسم', flags: [] },
    { row: row++, status: 'invalid', problem: 'dateUnreadable', number: '0102', name: 'HADDAD YACINE', at: null, marker: 'Entrée', source: '31/09/2026 07:58', flags: [] },
    { row: row++, status: 'invalid', problem: 'numberMissing', number: null, name: 'BOUDIAF SAMIR', at: null, marker: 'Sortie', source: '05/10/2026 16:30', flags: [] },
  );
}

const punchPreview = {
  fileName: 'pointage_octobre_2026.xlsx',
  sheetName: 'Feuil1',
  sheetNames: ['Feuil1', 'Paramètres'],
  headerRow: 1,
  suggestedHeaderRow: 1,
  dateOrder: 'dmy',
  columns: [
    { index: 1, letter: 'A', header: 'N° badge', samples: ['0101', '0102', '0103'] },
    { index: 2, letter: 'B', header: 'Nom', samples: ['BEN ALI KARIM', 'HADDAD YACINE', 'BOUDIAF SAMIR'] },
    { index: 3, letter: 'C', header: 'Date', samples: ['01/10/2026', '01/10/2026', '01/10/2026'] },
    { index: 4, letter: 'D', header: 'Heure', samples: ['07:52', '07:58', '08:14'] },
    { index: 5, letter: 'E', header: 'Etat', samples: ['Entrée', 'Sortie'] },
  ],
  top: [
    { row: 1, cells: { 1: 'N° badge', 2: 'Nom', 3: 'Date', 4: 'Heure', 5: 'Etat' } },
    { row: 2, cells: { 1: '0101', 2: 'BEN ALI KARIM', 3: '01/10/2026', 4: '07:52', 5: 'Entrée' } },
    { row: 3, cells: { 1: '0101', 2: 'BEN ALI KARIM', 3: '01/10/2026', 4: '16:35', 5: 'Sortie' } },
    { row: 4, cells: { 1: '0102', 2: 'HADDAD YACINE', 3: '01/10/2026', 4: '07:58', 5: 'Entrée' } },
    { row: 5, cells: { 1: '0102', 2: 'HADDAD YACINE', 3: '01/10/2026', 4: '16:41', 5: 'Sortie' } },
    { row: 6, cells: { 1: '0103', 2: 'BOUDIAF SAMIR', 3: '01/10/2026', 4: '08:14', 5: 'Entrée' } },
  ],
  mappingProblem: null,
  summary: {
    rows: 214,
    matched: 196,
    unknown: 12,
    otherBranch: 4,
    invalid: 2,
    textDates: 214,
    first: '2026-10-01T07:45:00',
    last: '2026-10-15T16:48:00',
    flagged: 3,
  },
  rows: punchRows,
  rowsCut: true,
  employees: punchPeople.map((e, i) => ({
    employeeId: e.id,
    employeeNumber: e.number,
    employeeName: e.fullName,
    fingerprintNumber: e.fingerprintNumber,
    punches: [50, 48, 47, 51][i],
    dates: [11, 10, 10, 11][i],
    first: `2026-10-01T0${['7:52', '7:58', '8:14', '8:03'][i]}:00`,
    last: `2026-10-15T16:${['35', '41', '30', '32'][i]}:00`,
  })),
  unknownNumbers: [
    { number: '0117', rows: 8, names: ['KHELIFI MOURAD'] },
    { number: '0123', rows: 4, names: [] },
  ],
  otherBranchNumbers: [{ number: '0201', rows: 4 }],
  suggestedMapping: { number: 1, name: 2, date: 3, time: 4, timeOut: null, dateTime: null, marker: 5 },
};

// ---------- payroll runs (PayrollRun, PayrollRunDetail, PayrollRunLinesPage) ----------

const WORKING_DAYS = payrollSettings.workingDaysPerMonth;
const HOURS_PER_DAY = payrollSettings.hoursPerDay;

function incomeTax(base) {
  let tax = 0;
  let floor = 0;
  for (const { upTo, rate } of payrollSettings.incomeTaxBrackets) {
    const ceiling = upTo ?? Infinity;
    if (base > floor) tax += (Math.min(base, ceiling) - floor) * (rate / 100);
    floor = ceiling;
  }
  return Math.round(tax);
}

const round2 = (n) => Math.round(n * 100) / 100;

function payLine(runId, employeeId, opts) {
  const e = emp(employeeId);
  const { present = WORKING_DAYS, paidLeave = 0, unpaid = 0, overtime = [], advance = 0, paid = false, warnings = [] } = opts;
  const dayRate = round2(e.baseRate / WORKING_DAYS);
  const hourly = e.baseRate / (WORKING_DAYS * HOURS_PER_DAY);
  const transport = e.allowances.find((a) => a.allowanceKey === 'transport')?.value ?? 3000;
  const elements = [{ key: 'base', kind: 'Base', amount: e.baseRate, qty: WORKING_DAYS, rate: dayRate, inSocialBase: true, inTaxBase: true, sortOrder: 0 }];
  if (unpaid > 0) elements.push({ key: 'absence', kind: 'Absence', amount: -Math.round(dayRate * unpaid), qty: unpaid, rate: dayRate, inSocialBase: true, inTaxBase: true, sortOrder: 1 });
  elements.push({ key: 'panier', kind: 'Allowance', amount: 200 * (present + paidLeave), qty: present + paidLeave, rate: 200, inSocialBase: false, inTaxBase: true, sortOrder: 10 });
  elements.push({ key: 'transport', kind: 'Allowance', amount: transport, qty: null, rate: null, inSocialBase: false, inTaxBase: true, sortOrder: 11 });
  overtime.forEach((o, i) => {
    const rule = overtimeRules.find((r) => r.key === o.key);
    const rate = round2(hourly * rule.multiplier);
    elements.push({ key: o.key, kind: 'Overtime', amount: Math.round(rate * o.hours), qty: o.hours, rate, inSocialBase: true, inTaxBase: true, sortOrder: 20 + i });
  });
  const gross = elements.reduce((a, el) => a + el.amount, 0);
  const socialBase = elements.filter((el) => el.inSocialBase).reduce((a, el) => a + el.amount, 0);
  const socialEmployee = Math.round(socialBase * 0.09);
  const socialEmployer = Math.round(socialBase * 0.26);
  const taxBase = gross - socialEmployee;
  const tax = incomeTax(taxBase);
  const net = gross - socialEmployee - tax - advance;
  return {
    id: `${runId}-${employeeId}`,
    employeeId,
    employeeNumber: e.number,
    employeeName: e.fullName,
    socialNumber: e.socialSecurityNumber,
    branchId: e.branchId,
    payBasis: e.payBasis,
    baseRate: e.baseRate,
    daysPresent: present,
    paidLeaveDays: paidLeave,
    unpaidAbsenceDays: unpaid,
    attendanceSource: e.attendanceMode === 'Recorded' ? 'Recorded' : 'Declared',
    gross,
    socialBase,
    socialEmployee,
    socialEmployer,
    taxBase,
    incomeTax: tax,
    advanceRecovered: advance,
    otherDeductions: 0,
    net,
    paidAmount: paid ? net : 0,
    elements,
    warnings,
    wagePaymentId: paid ? `wp-${runId}-${employeeId}` : null,
  };
}

function runFrom(base, lines) {
  const sum = (key) => lines.reduce((a, l) => a + l[key], 0);
  return {
    headCount: lines.length,
    currencyDecimals: 2,
    isProvisional: false,
    gross: sum('gross'),
    socialBase: sum('socialBase'),
    socialEmployee: sum('socialEmployee'),
    socialEmployer: sum('socialEmployer'),
    taxBase: sum('taxBase'),
    incomeTax: sum('incomeTax'),
    employerLevies: 0,
    advancesRecovered: sum('advanceRecovered'),
    otherDeductions: 0,
    net: sum('net'),
    paidTotal: sum('paidAmount'),
    lines: null,
    adjustments: null,
    warnings: null,
    ...base,
  };
}

function posting(run) {
  const lines = [
    { accountCode: '631', debit: run.gross, credit: 0 },
    { accountCode: '635', debit: run.socialEmployer, credit: 0 },
    { accountCode: '431', debit: 0, credit: run.socialEmployee + run.socialEmployer },
    { accountCode: '442', debit: 0, credit: run.incomeTax },
  ];
  if (run.advancesRecovered) lines.push({ accountCode: '425', debit: 0, credit: run.advancesRecovered });
  lines.push({ accountCode: '421', debit: 0, credit: run.net });
  return lines;
}

const runLines = {
  'run-2609-alg': [
    payLine('run-2609-alg', 'e-1', { overtime: [{ key: 'hs-50', hours: 6 }], paid: true }),
    payLine('run-2609-alg', 'e-2', { present: 21, unpaid: 1, overtime: [{ key: 'hs-50', hours: 4 }, { key: 'hs-100', hours: 8 }], advance: 5000, paid: true, warnings: ['advance_not_fully_recovered'] }),
    payLine('run-2609-alg', 'e-3', { present: 20, paidLeave: 1, unpaid: 1, overtime: [{ key: 'hs-75', hours: 3 }], paid: true }),
    payLine('run-2609-alg', 'e-4', { paid: true }),
  ],
  'run-2609-orn': [
    payLine('run-2609-orn', 'e-5', { overtime: [{ key: 'hs-50', hours: 5 }] }),
    payLine('run-2609-orn', 'e-6', { present: 21, paidLeave: 1 }),
  ],
  'run-2610-alg': [],
};

const mockRuns = [
  runFrom(
    { id: 'run-2610-alg', number: 'PAIE-16-2026-10', branchId: 'b-main', branchName: branchNameFr('b-main'), periodYear: 2026, periodMonth: 10, status: 'Draft', createdBy: 'admin', createdAtUtc: '2026-10-14T08:30:00Z' },
    runLines['run-2610-alg'],
  ),
  runFrom(
    {
      id: 'run-2609-alg', number: 'PAIE-16-2026-09', branchId: 'b-main', branchName: branchNameFr('b-main'), periodYear: 2026, periodMonth: 9, status: 'Paid',
      computedAtUtc: '2026-09-29T14:05:00Z', lockedBy: 'admin', approvedAtUtc: '2026-09-30T09:20:00Z', approvedBy: 'admin', paidAtUtc: '2026-10-02T10:00:00Z',
      ledgerEntryId: 'le-payroll-2609-alg', createdBy: 'admin', createdAtUtc: '2026-09-28T08:00:00Z',
    },
    runLines['run-2609-alg'],
  ),
  runFrom(
    {
      id: 'run-2609-orn', number: 'PAIE-31-2026-09', branchId: 'b-oran', branchName: branchNameFr('b-oran'), periodYear: 2026, periodMonth: 9, status: 'Approved',
      computedAtUtc: '2026-09-29T15:40:00Z', lockedBy: 'رشيد بلقاسم', approvedAtUtc: '2026-09-30T11:10:00Z', approvedBy: 'admin',
      ledgerEntryId: 'le-payroll-2609-orn', createdBy: 'رشيد بلقاسم', createdAtUtc: '2026-09-28T09:00:00Z',
    },
    runLines['run-2609-orn'],
  ),
];

function runDetail(id) {
  const run = mockRuns.find((r) => r.id === id);
  if (!run) return null;
  const lines = runLines[id];
  const counts = new Map();
  for (const line of lines) for (const code of line.warnings ?? []) counts.set(code, (counts.get(code) ?? 0) + 1);
  return {
    run,
    warnings: [...counts].map(([code, n]) => ({ code, lines: n })),
    posting: run.status === 'Draft' ? [] : posting(run),
    refusal: null,
    recomputeNeeded: false,
    settingsSnapshot: run.status === 'Draft' ? null : payrollSettings,
  };
}

function runList(query) {
  const branchId = query.get('branchId');
  const status = query.get('status');
  const year = Number(query.get('year')) || null;
  // The list sends zero for paidTotal; only the detail read carries it.
  return mockRuns
    .filter((r) => (!branchId || r.branchId === branchId) && (!status || r.status === status) && (!year || r.periodYear === year))
    .map((r) => ({ ...r, paidTotal: 0 }));
}

// ---------- sales, October 2026 (SalesSummary, AdminSale) ----------

const mockSalesSummary = {
  fromUtc: '2026-09-30T23:00:00Z',
  toUtc: '2026-10-15T22:59:59Z',
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
  // [debit, credit] moved in the period; the cash account takes the balancing figure.
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
// the API
// =====================================================================================

/** The answer to one request, or null when nothing here knows the route (the default then answers). */
function mockApi(method, url) {
  const p = url.pathname;
  const q = url.searchParams;
  const json = (body, status = 200) => ({ status, body });

  // ---- session and sync ----
  if (p === '/api/auth/login') {
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

  // ---- company, branches, tills, users ----
  if (p === '/api/catalog/branches') return json(mockBranches);
  if (p === '/api/catalog/store') return json(mockStore);
  if (p === '/api/catalog/store/documents') return json(mockStoreDocuments);
  if (p === '/api/catalog/store/documents/history') return json(mockDocumentHistory);
  if (p === '/api/devices') return json(mockDevices);
  if (p === '/api/users') return json(mockUsers);
  if (p === '/api/permissions') return json(mockCatalogue);
  if (p === '/api/roles') return json(mockRoles);

  // ---- personnel ----
  if (p === '/api/hr/settings') return json(payrollSettingsView);
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
  if (p === '/api/hr/attendance/sheet') return json(attendanceSheet(q));
  if (p === '/api/hr/attendance/import/preview') return json(punchPreview);

  // ---- payroll runs ----
  if (p === '/api/hr/payroll/runs' && method === 'GET') return json(runList(q));
  const runLinesRoute = /^\/api\/hr\/payroll\/runs\/([^/]+)\/lines$/.exec(p);
  if (runLinesRoute) {
    const lines = runLines[runLinesRoute[1]] ?? [];
    const skip = Number(q.get('skip')) || 0;
    const take = Number(q.get('take')) || 100;
    const text = (q.get('q') || '').trim().toLowerCase();
    const matching = lines.filter((l) => !text || l.employeeName.toLowerCase().includes(text) || l.employeeNumber.toLowerCase().includes(text));
    return json({ total: matching.length, skip, take, lines: matching.slice(skip, skip + take) });
  }
  const runRoute = /^\/api\/hr\/payroll\/runs\/([^/]+)$/.exec(p);
  if (runRoute && method === 'GET') {
    const detail = runDetail(runRoute[1]);
    return detail ? json(detail) : json({ code: 'notFound' }, 404);
  }

  // ---- sales ----
  if (p === '/api/reports/summary') return json(mockSalesSummary);
  if (p === '/api/sales') return json(mockSales);

  // ---- the books ----
  if (p === '/api/ledger/accounts') return json(ledgerAccounts);
  if (p === '/api/ledger/reports/trial-balance') return json(trialBalance(q));
  if (p === '/api/ledger/reports/profit-and-loss') return json(profitAndLoss(q));

  return null;
}

// =====================================================================================
// the run
// =====================================================================================

const ar = (arText, frText) => ({ ar: arText, fr: frText });
const TEXT = {
  edit: ar('تعديل', 'Modifier'),
  view: ar('عرض', 'Voir'),
  newEmployee: ar('جديد', 'Nouveau'),
  lineDetail: ar('التفصيل', 'Détail'),
  permissions: ar('الصلاحيات', 'Autorisations'),
  payrollModule: ar('الموظفون والأجور', 'Personnel et paie'),
  thisMonth: ar('هذا الشهر', 'Ce mois-ci'),
};

/** Every screen, in order. `ready`: any of these texts (or input values) means the page shows its data. */
const SHOTS = [
  // 1. employees
  { file: '01-employees-ar-1440.png', route: 'employees/employees', lang: 'ar', vp: DESKTOP, ready: ['Samir Boudiaf'] },
  { file: '01-employees-fr-1440.png', route: 'employees/employees', lang: 'fr', vp: DESKTOP, ready: ['Samir Boudiaf'] },
  { file: '01-employees-ar-390.png', route: 'employees/employees', lang: 'ar', vp: PHONE, ready: ['Samir Boudiaf'] },
  { file: '01b-employee-form-ar-1440.png', route: 'employees/employees', lang: 'ar', vp: DESKTOP, ready: ['Samir Boudiaf'], act: openEmployeeForm, fullPage: false },
  // 2. attendance
  { file: '02-attendance-ar-1440.png', route: 'employees/attendance', lang: 'ar', vp: DESKTOP, ready: ['Samir Boudiaf'] },
  { file: '02-attendance-fr-1440.png', route: 'employees/attendance', lang: 'fr', vp: DESKTOP, ready: ['Samir Boudiaf'] },
  // 3. fingerprint import, with a file picked so the preview is on screen
  { file: '03-attendance-import-ar-1440.png', route: 'employees/attendance-import', lang: 'ar', vp: DESKTOP, act: pickPunchFile, after: ['0117', 'KHELIFI MOURAD'] },
  { file: '03-attendance-import-fr-1440.png', route: 'employees/attendance-import', lang: 'fr', vp: DESKTOP, act: pickPunchFile, after: ['0117', 'KHELIFI MOURAD'] },
  // 4. payroll runs
  { file: '04-payroll-runs-ar-1440.png', route: 'employees/runs', lang: 'ar', vp: DESKTOP, ready: ['PAIE-16-2026-09'] },
  { file: '04-payroll-runs-fr-1440.png', route: 'employees/runs', lang: 'fr', vp: DESKTOP, ready: ['PAIE-16-2026-09'] },
  { file: '04-payroll-runs-ar-390.png', route: 'employees/runs', lang: 'ar', vp: PHONE, ready: ['PAIE-16-2026-09'] },
  { file: '04b-payroll-run-detail-ar-1440.png', route: 'employees/runs', lang: 'ar', vp: DESKTOP, ready: ['PAIE-16-2026-09'], act: openPaidRun, after: ['Samir Boudiaf'] },
  // 5. payroll settings
  { file: '05-payroll-settings-ar-1440.png', route: 'employees/settings', lang: 'ar', vp: DESKTOP, ready: ['Cabinet comptable Benyahia'] },
  // 6. sales overview, on "this month"
  { file: '06-sales-overview-ar-1440.png', route: 'sales/overview', lang: 'ar', vp: DESKTOP, ready: ['TCK-ALG01-'], act: thisMonth, after: ['TCK-ALG01-'] },
  { file: '06-sales-overview-fr-1440.png', route: 'sales/overview', lang: 'fr', vp: DESKTOP, ready: ['TCK-ALG01-'], act: thisMonth, after: ['TCK-ALG01-'] },
  { file: '06-sales-overview-ar-390.png', route: 'sales/overview', lang: 'ar', vp: PHONE, ready: ['TCK-ALG01-'], act: thisMonth, after: ['TCK-ALG01-'] },
  // 7. accounting
  { file: '07-trial-balance-ar-1440.png', route: 'reports/trial-balance', lang: 'ar', vp: DESKTOP, ready: ['4457'] },
  { file: '07-trial-balance-fr-1440.png', route: 'reports/trial-balance', lang: 'fr', vp: DESKTOP, ready: ['4457'] },
  { file: '07-profit-loss-ar-1440.png', route: 'reports/profit-loss', lang: 'ar', vp: DESKTOP, ready: ['خدمات التأجير', 'Prestations de location'] },
  // 8. users and their role and permissions
  { file: '08-users-ar-1440.png', route: 'admin/users', lang: 'ar', vp: DESKTOP, ready: ['31260518'] },
  { file: '08-users-fr-1440.png', route: 'admin/users', lang: 'fr', vp: DESKTOP, ready: ['31260518'] },
  { file: '08-users-ar-390.png', route: 'admin/users', lang: 'ar', vp: PHONE, ready: ['31260518'] },
  { file: '08b-user-role-permissions-ar-1440.png', route: 'admin/users', lang: 'ar', vp: DESKTOP, ready: ['31260518'], act: openUserPermissions, fullPage: false },
  { file: '08b-user-role-permissions-fr-1440.png', route: 'admin/users', lang: 'fr', vp: DESKTOP, ready: ['31260518'], act: openUserPermissions, fullPage: false },
  { file: '08b-user-role-permissions-ar-390.png', route: 'admin/users', lang: 'ar', vp: PHONE, ready: ['31260518'], act: openUserPermissions, fullPage: false },
  // 10. one module of a person's permissions opened (2026-11-05): payroll, where the person holds keys of their own
  { file: '10-user-permission-module-ar-1440.png', route: 'admin/users', lang: 'ar', vp: DESKTOP, ready: ['31260518'], act: openUserPermissionModule, fullPage: false },
  { file: '10-user-permission-module-fr-1440.png', route: 'admin/users', lang: 'fr', vp: DESKTOP, ready: ['31260518'], act: openUserPermissionModule, fullPage: false },
  { file: '10-user-permission-module-ar-390.png', route: 'admin/users', lang: 'ar', vp: PHONE, ready: ['31260518'], act: openUserPermissionModule, fullPage: false },
  // 9. administration
  { file: '09-branches-ar-1440.png', route: 'admin/branches', lang: 'ar', vp: DESKTOP, ready: ['فرع وهران', 'Succursale Oran'] },
  { file: '09-company-ar-1440.png', route: 'admin/company', lang: 'ar', vp: DESKTOP, ready: ['Atlas Location', 'شركة الأطلس'] },
  { file: '09-company-fr-1440.png', route: 'admin/company', lang: 'fr', vp: DESKTOP, ready: ['Atlas Location', 'شركة الأطلس'] },
  { file: '09-tills-ar-1440.png', route: 'admin/tills', lang: 'ar', vp: DESKTOP, ready: ['ORN-01'] },
];

let page;

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

/**
 * Clicks the button whose text is exactly one of `labels`, in the smallest block that also holds
 * `rowText` — a table row, or a card in a phone layout. Independent of the markup on purpose: the
 * same script shoots the bundle before the redesign and after it. A button inside a closed row menu
 * (`role="menu"`, the redesign's "⋯") is reached the way a person reaches it: the menu's trigger is
 * pressed first, then the item.
 */
async function clickNear(rowText, labels) {
  const found = await page.evaluate(
    ({ rowText, labels }) => {
      const visible = (el) => !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length);
      const buttons = Array.from(document.querySelectorAll('button, [role="button"], [role="menuitem"], a')).filter((b) =>
        labels.includes((b.textContent || '').replace(/\s+/g, ' ').trim()),
      );
      let best = null;
      let bestScore = Infinity;
      for (const button of buttons) {
        // A visible button wins over one folded inside a closed menu.
        const inMenu = !visible(button) && button.closest('[role="menu"]');
        if (!visible(button) && !inMenu) continue;
        for (let el = button.parentElement; el && el !== document.body; el = el.parentElement) {
          const text = el.innerText || '';
          if (!text.includes(rowText)) continue;
          const score = text.length + (inMenu ? 1e6 : 0);
          if (score < bestScore) {
            best = button;
            bestScore = score;
          }
          break;
        }
      }
      if (!best) return null;
      document.querySelectorAll('[data-shot-target]').forEach((el) => el.removeAttribute('data-shot-target'));
      document.querySelectorAll('[data-shot-trigger]').forEach((el) => el.removeAttribute('data-shot-trigger'));
      best.setAttribute('data-shot-target', '');
      if (visible(best)) return 'direct';
      const menu = best.closest('[role="menu"]');
      const host = menu.parentElement;
      const trigger =
        (menu.id && document.querySelector(`[aria-controls="${menu.id}"]`)) ||
        (host && host.querySelector('[aria-haspopup="menu"], [aria-haspopup="true"]'));
      if (!trigger) return 'hidden';
      trigger.setAttribute('data-shot-trigger', '');
      return 'menu';
    },
    { rowText, labels },
  );
  if (!found) throw new Error(`no "${labels.join('" / "')}" button beside "${rowText}"`);
  if (found === 'menu') {
    await page.locator('[data-shot-trigger]').click({ timeout: 5000 });
    await page.locator('[data-shot-target]').waitFor({ state: 'visible', timeout: 5000 });
  }
  if (found === 'hidden') {
    // A folded item with no trigger to be found: its own click handler still opens the form.
    await page.evaluate(() => document.querySelector('[data-shot-target]').click());
    return;
  }
  await page.locator('[data-shot-target]').click({ timeout: 5000 });
}

async function waitForDialog(timeout = 8000) {
  await page.locator('[role="dialog"]:visible, dialog[open]').first().waitFor({ state: 'visible', timeout });
}

async function openEmployeeForm(lang) {
  try {
    await clickNear('Samir Boudiaf', [TEXT.edit[lang]]);
  } catch {
    // No edit button beside the row: the new-employee form will do.
    await page.getByRole('button', { name: TEXT.newEmployee[lang], exact: true }).first().click({ timeout: 5000 });
  }
  await waitForDialog();
}

async function pickPunchFile() {
  const input = page.locator('#pi-file, input[type="file"]').first();
  await input.waitFor({ state: 'attached', timeout: READY_TIMEOUT });
  await input.setInputFiles({
    name: 'pointage_octobre_2026.xlsx',
    mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    // The server is mocked: the preview answer does not depend on these bytes.
    buffer: Buffer.from('PK\u0003\u0004 mock workbook'),
  });
}

async function openPaidRun(lang) {
  await clickNear('PAIE-16-2026-09', [TEXT.view[lang]]).catch(async () => {
    // After the redesign there is no View button: the run's period is the link into it.
    await page.locator('tr', { hasText: 'PAIE-16-2026-09' }).first().locator('.ui-link').first().click({ timeout: 5000 });
  });
  await waitForAny(['Samir Boudiaf']);
  // One payslip opened, so its breakdown is in the picture too. Optional.
  await clickNear('Samir Boudiaf', [TEXT.lineDetail[lang]]).catch((e) => note(`  (run detail: payslip not expanded — ${e.message})`));
}

async function thisMonth(lang) {
  await page.getByRole('button', { name: TEXT.thisMonth[lang], exact: true }).first().click({ timeout: 5000 });
}

async function openUserPermissions(lang) {
  await clickNear('Nadia Mansouri', [TEXT.edit[lang]]);
  await waitForDialog();
  // The permissions group starts folded; open it inside the dialog.
  const opened = await page.evaluate((label) => {
    const scope = document.querySelector('[role="dialog"][aria-modal="true"]') || document.querySelector('dialog[open]') || document;
    const button = Array.from(scope.querySelectorAll('button[aria-expanded="false"]')).find((b) => (b.textContent || '').includes(label));
    if (button) {
      button.click();
      return true;
    }
    const summary = Array.from(scope.querySelectorAll('details:not([open]) > summary')).find((s) => (s.textContent || '').includes(label));
    if (summary) {
      summary.click();
      return true;
    }
    return false;
  }, TEXT.permissions[lang]);
  if (!opened) note('  (users: the permissions group was not found folded — shot as it opened)');
  await page.waitForTimeout(300);
}

/** The permissions group, then its payroll module, opened and scrolled into the dialog's view. */
async function openUserPermissionModule(lang) {
  await openUserPermissions(lang);
  const opened = await page.evaluate((label) => {
    const scope = document.querySelector('[role="dialog"][aria-modal="true"]') || document;
    const button = Array.from(scope.querySelectorAll('button[aria-expanded="false"]')).find((b) => (b.textContent || '').includes(label));
    if (!button) return false;
    button.click();
    button.scrollIntoView({ block: 'start' });
    return true;
  }, TEXT.payrollModule[lang]);
  if (!opened) note('  (users: the payroll module was not found folded — an older screen has none)');
  await page.waitForTimeout(300);
}

const notes = [];
function note(message) {
  console.log(message);
  notes.push(message);
}

async function open(route, lang, vp) {
  await page.setViewportSize(vp);
  await page.goto(`${BASE}/#/backoffice/${route}`);
  await page.evaluate(([l, look]) => {
    localStorage.setItem('pos.lang', l);
    if (look) localStorage.setItem('pos.appearance.v3', look);
  }, [lang, APPEARANCE]);
  await page.reload();
  await page.waitForLoadState('domcontentloaded');
}

async function settle() {
  await page.evaluate(() => (document.fonts ? document.fonts.ready.then(() => true) : true)).catch(() => {});
  // Back to the top: a click that scrolled the page would leave the sticky bars mid-way down a
  // full-page capture.
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
  console.log(`Batch 3 screenshots — set "${SHOT_SET}"`);
  console.log(`  bundle: ${path.relative(root, webDist) || webDist}`);
  console.log(`  output: ${path.relative(root, out)}`);

  const server = await serve();
  // CHROME_PATH: an installed Chrome, where Playwright's own browser was never downloaded.
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || undefined });
  const fellThrough = new Map();
  const pageErrors = new Map();
  const results = [];
  let current = 'sign-in';

  try {
    // No locale on purpose: the app formats dates and numbers itself.
    const context = await browser.newContext({ viewport: DESKTOP, serviceWorkers: 'block', timezoneId: 'Africa/Algiers' });
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

    // ---- sign in, as Batch 2 does ----
    await page.goto(`${BASE}/#/login`);
    await page.evaluate(() => localStorage.setItem('pos.lang', 'ar'));
    await page.reload();
    await page.waitForSelector('#username', { timeout: READY_TIMEOUT });
    if (await page.locator('#server').isVisible().catch(() => false)) await page.fill('#server', BASE);
    await page.fill('#username', 'admin');
    await page.fill('#password', 'AnyPassword123');
    await page.click('button[type=submit]');
    await page.waitForURL((u) => !u.hash.startsWith('#/login'), { timeout: 30_000 });
    console.log(`Signed in (landed on ${new URL(page.url()).hash || '/'})`);

    for (const shot of SHOTS.filter(chosen)) {
      current = shot.file;
      const target = path.join(out, shot.file);
      const problems = [];
      try {
        await open(shot.route, shot.lang, shot.vp);
        if (shot.ready) await waitForAny(shot.ready).catch(() => problems.push(`no data on screen (waited for: ${shot.ready.join(' | ')})`));
        if (shot.act && problems.length === 0) {
          await shot.act(shot.lang).catch((e) => problems.push(`action failed: ${e.message.split('\n')[0]}`));
          if (shot.after && problems.length === 0) {
            await waitForAny(shot.after).catch(() => problems.push(`no data after the action (waited for: ${shot.after.join(' | ')})`));
          }
        }
        const dir = await page.evaluate(() => document.documentElement.getAttribute('dir') || document.dir || 'ltr');
        if ((shot.lang === 'ar') !== (dir === 'rtl')) problems.push(`page is ${dir}, expected ${shot.lang === 'ar' ? 'rtl' : 'ltr'}`);
        await settle();
        await page.screenshot({ path: target, fullPage: shot.fullPage !== false });
        if (problems.length) {
          results.push({ file: shot.file, status: 'warn', problems });
          console.log(`  wrote ${shot.file}  (WARN: ${problems.join('; ')})`);
        } else {
          results.push({ file: shot.file, status: 'ok' });
          console.log(`  wrote ${shot.file}`);
        }
      } catch (error) {
        problems.push(error.message.split('\n')[0]);
        results.push({ file: shot.file, status: 'failed', problems });
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
