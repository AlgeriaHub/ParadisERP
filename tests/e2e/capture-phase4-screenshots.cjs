// Captures Phase 4 checkpoint screenshots for the 4 reference screens:
// 1. Dashboard (Arabic 1440px RTL, French 1440px LTR)
// 2. Products (Arabic 1440px RTL, French 1440px LTR, Arabic 390px Mobile)
// 3. Rental Contract detail (Arabic 1440px RTL, French 1440px LTR, Arabic 390px Mobile)
// 4. Rental Return note (Arabic 1440px RTL, French 1440px LTR, Arabic 390px Mobile)

const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');

const root = path.resolve(__dirname, '../..');
const out = path.join(root, 'artifacts/ui-redesign-checkpoint/phase4');
fs.mkdirSync(out, { recursive: true });

const webDist = path.join(root, 'ParadiERP.WebUI/dist/paradierp-web-ui/browser');

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
  'stock.view',
  'finance.view',
  'vouchers.view',
  'receivables.view',
  'users.manage',
  'roles.manage',
];

const mockUser = {
  id: 'u-admin',
  username: 'admin',
  fullName: 'Chef de Parc / Directeur',
  role: 'Admin',
  isActive: true,
  permissions,
  branchLocked: false,
};

const mockBranches = [
  { id: 'b-main', code: 'ALG', nameFr: 'Succursale Alger Centre', nameAr: 'فرع الجزائر الوسطى', isMain: true, isActive: true },
  { id: 'b-oran', code: 'ORN', nameFr: 'Succursale Oran', nameAr: 'فرع وهران', isMain: false, isActive: true },
];

const mockProducts = [
  { id: 'p1', sku: 'ART-0042', barcode: '6131234567890', nameFr: 'Panneau coffrage 0.76 x 3.00 m', nameAr: 'لوح قولبة 0.76 × 3.00 م', categoryName: 'Coffrage', price: 12500, costPrice: 9000, rentalRate: 120, stockQuantity: 45, minStockQuantity: 10, isActive: true },
  { id: 'p2', sku: 'ART-0043', barcode: '6131234567891', nameFr: 'Étai métallique télescopique 3.50 m', nameAr: 'دعامة معدنية تلسكوبية 3.50 م', categoryName: 'Étaiement', price: 4800, costPrice: 3200, rentalRate: 80, stockQuantity: 120, minStockQuantity: 20, isActive: true },
  { id: 'p3', sku: 'ART-0044', barcode: '6131234567892', nameFr: 'Cadre échafaudage acier 2.00 m', nameAr: 'إطار سقالة فولاذي 2.00 م', categoryName: 'Échafaudage', price: 8200, costPrice: 6000, rentalRate: 150, stockQuantity: 8, minStockQuantity: 15, isActive: true },
  { id: 'p4', sku: 'ART-0045', barcode: '6131234567893', nameFr: 'Plancher acier galvanisé 3.00 m', nameAr: 'أرضية فولاذية مجلفنة 3.00 م', categoryName: 'Échafaudage', price: 9500, costPrice: 7200, rentalRate: 110, stockQuantity: 34, minStockQuantity: 10, isActive: true },
  { id: 'p5', sku: 'ART-0046', barcode: '6131234567894', nameFr: 'Lisse de sécurité tubulaire 3.00 m', nameAr: 'درابزين حماية أنبوبي 3.00 م', categoryName: 'Sécurité', price: 3200, costPrice: 2100, rentalRate: 50, stockQuantity: 62, minStockQuantity: 15, isActive: true },
  { id: 'p6', sku: 'ART-0047', barcode: '6131234567895', nameFr: 'Bétonnière thermique 350 L', nameAr: 'خلاطة خرسانة حرارية 350 لتر', categoryName: 'Matériel lourd', price: 185000, costPrice: 145000, rentalRate: 2500, stockQuantity: 3, minStockQuantity: 5, isActive: false },
];

const mockCategories = [
  { id: 'c1', name: 'Coffrage', nameAr: 'قولبة' },
  { id: 'c2', name: 'Étaiement', nameAr: 'دعم' },
  { id: 'c3', name: 'Échafaudage', nameAr: 'سقالات' },
  { id: 'c4', name: 'Sécurité', nameAr: 'سلامة' },
  { id: 'c5', name: 'Matériel lourd', nameAr: 'عتاد ثقيل' },
];

const mockCustomers = [
  {
    id: 'cust-1',
    code: 'CLI-0012',
    name: 'SARL Bâtiment Moderne Algérie',
    phone: '0550 12 34 56',
    address: '14 Rue Didouche Mourad, Alger',
    tradeRegister: '16/00-1234567B22',
    taxId: '002216012345678',
    balance: 142000,
    creditLimit: 500000,
    isActive: true,
  },
];

const mockSalesSummary = {
  fromUtc: '2026-10-15T00:00:00Z',
  toUtc: '2026-10-15T23:59:59Z',
  saleCount: 18,
  refundCount: 0,
  grossTtc: 425000,
  netHt: 357142.86,
  taxTotal: 67857.14,
  discountTotal: 0,
  refundTotal: 0,
  roundingTotal: 0,
  giftCardTotal: 0,
  loyaltyTotal: 0,
  taxBreakdown: [],
  topProducts: [],
  branchBreakdown: [
    { branchId: 'b-main', branchName: 'Succursale Alger Centre', grossTtc: 285000, saleCount: 12 },
    { branchId: 'b-oran', branchName: 'Succursale Oran', grossTtc: 140000, saleCount: 6 },
  ],
};

const mockCashBoxes = [
  { id: 'cb1', name: 'Caisse Principale (Coffre)', branchId: 'b-main', balance: 1485000, currency: 'DZD' },
  { id: 'cb2', name: 'Caisse Comptoir Vente', branchId: 'b-main', balance: 92400, currency: 'DZD' },
];

const mockContract = {
  id: 'c-1001',
  number: 'LOC-2026-0042',
  customerId: 'cust-1',
  customerName: 'SARL Bâtiment Moderne Algérie',
  customerPhone: '0550 12 34 56',
  branchId: 'b-main',
  branchName: 'Succursale Alger Centre',
  status: 'Open',
  isLocked: false,
  startDate: '2026-10-01T08:00:00Z',
  expectedEndDate: '2026-10-30T17:00:00Z',
  depositAmount: 50000,
  totalRental: 142000,
  paid: 50000,
  owed: 92000,
  stillOutQty: 44,
  chargeTotal: 142000,
  overdueTotal: 0,
  notes: 'Chantier Bab Ezzouar - Tour Résidentielle',
  lines: [
    {
      id: 'l-1',
      productId: 'p1',
      sku: 'ART-0042',
      nameFr: 'Panneau coffrage 0.76 x 3.00 m',
      nameAr: 'لوح قولبة 0.76 × 3.00 م',
      quantity: 10,
      issuedQty: 10,
      returnedQty: 1,
      stillOutQty: 9,
      leftToIssueQty: 0,
      dailyRate: 120,
      replacementValue: 12500,
    },
    {
      id: 'l-2',
      productId: 'p2',
      sku: 'ART-0043',
      nameFr: 'Étai métallique télescopique 3.50 m',
      nameAr: 'دعامة معدنية تلسكوبية 3.50 م',
      quantity: 25,
      issuedQty: 25,
      returnedQty: 5,
      stillOutQty: 20,
      leftToIssueQty: 0,
      dailyRate: 80,
      replacementValue: 4800,
    },
    {
      id: 'l-3',
      productId: 'p4',
      sku: 'ART-0045',
      nameFr: 'Plancher acier galvanisé 3.00 m',
      nameAr: 'أرضية فولاذية مجلفنة 3.00 م',
      quantity: 15,
      issuedQty: 15,
      returnedQty: 0,
      stillOutQty: 15,
      leftToIssueQty: 0,
      dailyRate: 110,
      replacementValue: 9500,
    },
  ],
  attachments: [],
};

const mockSettlement = {
  contractId: 'c-1001',
  totalRental: 142000,
  depositHeld: 50000,
  depositToReturn: 50000,
  damage: 0,
  missing: 0,
  maintenance: 0,
  stillOutQty: 44,
  lines: [
    { lineId: 'l-1', settledQty: 1, stillOutQty: 9 },
    { lineId: 'l-2', settledQty: 5, stillOutQty: 20 },
    { lineId: 'l-3', settledQty: 0, stillOutQty: 15 },
  ],
};

const mockIssueNotes = [
  {
    id: 'iss-1',
    number: 'BON-SORTIE-001',
    issueDate: '2026-10-01T09:00:00Z',
    lines: [
      { productId: 'p1', quantity: 10 },
      { productId: 'p2', quantity: 25 },
      { productId: 'p4', quantity: 15 },
    ],
  },
];

const mockReturnNotes = [
  {
    id: 'ret-1',
    number: 'BON-RETOUR-001',
    returnDate: '2026-10-08T14:30:00Z',
    lines: [
      { productId: 'p1', quantityGood: 1, quantityRepairable: 0, quantityDamaged: 0, quantityLost: 0 },
      { productId: 'p2', quantityGood: 5, quantityRepairable: 0, quantityDamaged: 0, quantityLost: 0 },
    ],
  },
];

async function main() {
  console.log(`\nCapturing Phase 4 Reference Screens...`);

  const server = http.createServer((req, res) => {
    const requested = decodeURIComponent(new URL(req.url, 'http://127.0.0.1:5210').pathname);
    const filePath = path.resolve(webDist, '.' + (requested === '/' ? '/index.html' : requested));
    if (!filePath.startsWith(webDist + path.sep) || !fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
      const fallback = path.join(webDist, 'index.html');
      res.writeHead(200, { 'Content-Type': 'text/html' });
      fs.createReadStream(fallback).pipe(res);
      return;
    }
    const mimeMap = {
      '.html': 'text/html',
      '.js': 'text/javascript',
      '.css': 'text/css',
      '.json': 'application/json',
      '.woff2': 'font/woff2',
      '.svg': 'image/svg+xml',
      '.png': 'image/png',
    };
    res.setHeader('Content-Type', mimeMap[path.extname(filePath)] || 'application/octet-stream');
    fs.createReadStream(filePath).pipe(res);
  });

  await new Promise((resolve) => server.listen(5210, '127.0.0.1', resolve));

  const browser = await chromium.launch({ headless: true, channel: 'chrome' });

  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 960 }, serviceWorkers: 'block' });
    const page = await context.newPage();

    await page.route('**/api/**', async (route) => {
      const url = new URL(route.request().url());
      const p = url.pathname;

      if (p === '/api/auth/login') {
        return route.fulfill({
          json: {
            token: 'mock-jwt-token',
            expiresAtUtc: '2099-01-01T00:00:00Z',
            user: mockUser,
            deviceCode: 'OFFICE-01',
            deviceBranchId: 'b-main',
          },
        });
      }
      if (p === '/api/catalog/branches') return route.fulfill({ json: mockBranches });
      if (p === '/api/catalog/products') return route.fulfill({ json: mockProducts });
      if (p === '/api/catalog/categories') return route.fulfill({ json: mockCategories });
      if (p === '/api/catalog/customers') return route.fulfill({ json: mockCustomers });
      if (p === '/api/catalog/suppliers') return route.fulfill({ json: [] });
      if (p.startsWith('/api/reports/summary')) return route.fulfill({ json: mockSalesSummary });
      if (p.startsWith('/api/catalog/products/low-stock')) {
        return route.fulfill({ json: [
          { productId: 'p3', nameFr: 'Cadre échafaudage acier 2.00 m', nameAr: 'إطار سقالة فولاذي 2.00 م', sku: 'ART-0044', currentStock: 8, minStock: 15 },
          { productId: 'p6', nameFr: 'Bétonnière thermique 350 L', nameAr: 'خلاطة خرسانة حرارية 350 لتر', sku: 'ART-0047', currentStock: 3, minStock: 5 },
        ] });
      }
      if (p.startsWith('/api/treasury/cash-boxes')) return route.fulfill({ json: mockCashBoxes });
      if (p.startsWith('/api/finance/payment-vouchers')) return route.fulfill({ json: [] });
      if (p.startsWith('/api/treasury/pending-payments')) return route.fulfill({ json: [] });
      if (p.startsWith('/api/receivables/ageing')) {
        return route.fulfill({
          json: {
            asOf: '2026-10-15T00:00:00Z',
            total: 3450000,
            current: 2560000,
            days1To30: 890000,
            days31To60: 0,
            days61To90: 0,
            over90: 0,
            creditBalance: 0,
            ledgerBalance: 3450000,
            uninvoiced: 0,
            customers: [],
          },
        });
      }
      if (p.startsWith('/api/rentals/contracts')) {
        if (p === '/api/rentals/contracts/c-1001') return route.fulfill({ json: mockContract });
        if (p === '/api/rentals/contracts/c-1001/settlement') return route.fulfill({ json: mockSettlement });
        if (p === '/api/rentals/contracts/c-1001/issues') return route.fulfill({ json: mockIssueNotes });
        if (p === '/api/rentals/contracts/c-1001/returns') return route.fulfill({ json: mockReturnNotes });
        if (p === '/api/rentals/contracts/c-1001/extensions') return route.fulfill({ json: [] });
        if (p === '/api/rentals/contracts/c-1001/returns/preview') {
          return route.fulfill({
            json: {
              depositHeld: 50000,
              depositReturned: 0,
              remainingDeposit: 50000,
              lineCosts: [],
            },
          });
        }
        return route.fulfill({ json: [mockContract] });
      }
      if (p === '/api/rentals/settings') return route.fulfill({ json: { overdueMultiplier: 1.5, depositEnabled: true, taxRate: 19, dayCutoffHour: 18, minimumDays: 1 } });
      if (p === '/api/rentals/settings/products') return route.fulfill({ json: [] });
      if (p === '/api/catalog/store/documents') return route.fulfill({ json: { contractWordings: [] } });

      return route.fulfill({ json: [] });
    });

    // Helper to switch language reliably
    async function setLanguage(lang) {
      await page.evaluate((l) => localStorage.setItem('pos.lang', l), lang);
      await page.reload();
      await page.waitForTimeout(600);
    }

    // 1. Sign in
    await page.goto('http://127.0.0.1:5210/#/login');
    await page.evaluate(() => localStorage.setItem('pos.lang', 'ar'));
    await page.reload();
    await page.fill('#server', 'http://127.0.0.1:5210');
    await page.fill('#username', 'admin');
    await page.fill('#password', 'AnyPassword123');
    await page.click('button[type=submit]');
    await page.waitForURL(/office|register|dashboard/);

    // =============================================================
    // SCREEN 1: DASHBOARD
    // =============================================================
    console.log('Capturing Screen 1: Dashboard...');
    // Arabic 1440px
    await page.setViewportSize({ width: 1440, height: 960 });
    await page.goto('http://127.0.0.1:5210/#/backoffice/dashboard');
    await setLanguage('ar');
    await page.waitForSelector('.bo-panel, .bo-metrics, app-dashboard, .bo-stack');
    await page.waitForTimeout(800);
    const s1Ar = path.join(out, '01-dashboard-ar-1440.png');
    await page.screenshot({ path: s1Ar, fullPage: false });

    // French 1440px
    await setLanguage('fr');
    await page.waitForSelector('.bo-panel, .bo-metrics, app-dashboard, .bo-stack');
    await page.waitForTimeout(800);
    const s1Fr = path.join(out, '01-dashboard-fr-1440.png');
    await page.screenshot({ path: s1Fr, fullPage: false });

    // =============================================================
    // SCREEN 2: PRODUCTS
    // =============================================================
    console.log('Capturing Screen 2: Products...');
    // Arabic 1440px
    await page.goto('http://127.0.0.1:5210/#/backoffice/inventory/products');
    await setLanguage('ar');
    await page.waitForSelector('.ui-table, app-backoffice-products, table');
    await page.waitForTimeout(600);
    const s2Ar = path.join(out, '02-products-ar-1440.png');
    await page.screenshot({ path: s2Ar, fullPage: false });

    // French 1440px
    await setLanguage('fr');
    await page.waitForSelector('.ui-table, app-backoffice-products, table');
    await page.waitForTimeout(600);
    const s2Fr = path.join(out, '02-products-fr-1440.png');
    await page.screenshot({ path: s2Fr, fullPage: false });

    // Mobile 390px Arabic
    await page.setViewportSize({ width: 390, height: 844 });
    await setLanguage('ar');
    await page.waitForSelector('.ui-table, app-backoffice-products, table');
    await page.waitForTimeout(600);
    const s2Mob = path.join(out, '02-products-ar-390.png');
    await page.screenshot({ path: s2Mob, fullPage: false });

    // Reset desktop viewport
    await page.setViewportSize({ width: 1440, height: 960 });

    // =============================================================
    // SCREEN 3: RENTAL CONTRACT DETAIL
    // =============================================================
    console.log('Capturing Screen 3: Rental Contract Detail...');
    // Arabic 1440px
    await page.goto('http://127.0.0.1:5210/#/backoffice/rentals/contracts/c-1001');
    await setLanguage('ar');
    await page.waitForSelector('.status-line, .facts-grid, app-rental-contract');
    await page.waitForTimeout(700);
    const s3Ar = path.join(out, '03-rental-contract-ar-1440.png');
    await page.screenshot({ path: s3Ar, fullPage: false });

    // French 1440px
    await setLanguage('fr');
    await page.waitForSelector('.status-line, .facts-grid, app-rental-contract');
    await page.waitForTimeout(700);
    const s3Fr = path.join(out, '03-rental-contract-fr-1440.png');
    await page.screenshot({ path: s3Fr, fullPage: false });

    // Mobile 390px Arabic
    await page.setViewportSize({ width: 390, height: 844 });
    await setLanguage('ar');
    await page.waitForSelector('.status-line, .facts-grid, app-rental-contract');
    await page.waitForTimeout(700);
    const s3Mob = path.join(out, '03-rental-contract-ar-390.png');
    await page.screenshot({ path: s3Mob, fullPage: false });

    // Reset desktop viewport
    await page.setViewportSize({ width: 1440, height: 960 });

    // =============================================================
    // SCREEN 4: RENTAL RETURN NOTE
    // =============================================================
    console.log('Capturing Screen 4: Rental Return Note...');
    // Arabic 1440px (scrolled to return condition cards)
    await page.goto('http://127.0.0.1:5210/#/backoffice/rentals/contracts/c-1001/return');
    await setLanguage('ar');
    await page.waitForSelector('.return-doc-summary, .return-items-stack, app-rental-return-note');
    await page.locator('.return-doc-summary').scrollIntoViewIfNeeded();
    await page.waitForTimeout(700);
    const s4Ar = path.join(out, '04-rental-return-ar-1440.png');
    await page.screenshot({ path: s4Ar, fullPage: false });

    // French 1440px (scrolled to return condition cards)
    await setLanguage('fr');
    await page.waitForSelector('.return-doc-summary, .return-items-stack, app-rental-return-note');
    await page.locator('.return-doc-summary').scrollIntoViewIfNeeded();
    await page.waitForTimeout(700);
    const s4Fr = path.join(out, '04-rental-return-fr-1440.png');
    await page.screenshot({ path: s4Fr, fullPage: false });

    // Mobile 390px Arabic
    await page.setViewportSize({ width: 390, height: 844 });
    await setLanguage('ar');
    await page.waitForSelector('.return-doc-summary, .return-items-stack, app-rental-return-note');
    await page.locator('.return-doc-summary').scrollIntoViewIfNeeded();
    await page.waitForTimeout(700);
    const s4Mob = path.join(out, '04-rental-return-ar-390.png');
    await page.screenshot({ path: s4Mob, fullPage: false });

    await context.close();
    console.log(`\nAll Phase 4 reference screenshots captured in ${out}!\n`);
  } finally {
    await browser.close();
    await new Promise((resolve) => server.close(resolve));
  }
}

main().catch((err) => {
  console.error('Error capturing screenshots:', err);
  process.exit(1);
});
