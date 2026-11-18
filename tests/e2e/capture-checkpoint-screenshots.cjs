// Captures checkpoint screenshots for visual comparison before and after UI redesign.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');

const root = path.resolve(__dirname, '../..');
const stage = process.argv[2] || 'before'; // 'before' or 'after'
const out = path.join(root, 'artifacts/ui-redesign-checkpoint', stage);
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

const mockSalesSummary = {
  todaySalesCount: 18,
  todayGrossSales: 425000,
  todayNetSales: 418000,
  todayTaxAmount: 67000,
  todayCashCollected: 310000,
  byBranch: [
    { branchId: 'b-main', branchName: 'Succursale Alger Centre', amount: 285000, salesCount: 12 },
    { branchId: 'b-oran', branchName: 'Succursale Oran', amount: 140000, salesCount: 6 },
  ],
};

const mockCashBoxes = [
  { id: 'cb1', name: 'Caisse Principale (Coffre)', branchId: 'b-main', balance: 1485000, currency: 'DZD' },
  { id: 'cb2', name: 'Caisse Comptoir Vente', branchId: 'b-main', balance: 92400, currency: 'DZD' },
];

async function main() {
  console.log(`\nCapturing UI Checkpoint Screenshots for stage: "${stage}"...`);

  const server = http.createServer((req, res) => {
    const requested = decodeURIComponent(new URL(req.url, 'http://127.0.0.1:5198').pathname);
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

  await new Promise((resolve) => server.listen(5198, '127.0.0.1', resolve));

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
      if (p === '/api/catalog/customers') return route.fulfill({ json: [] });
      if (p === '/api/catalog/suppliers') return route.fulfill({ json: [] });
      if (p === '/api/sales/summary') return route.fulfill({ json: mockSalesSummary });
      if (p === '/api/stock/low') return route.fulfill({ json: [mockProducts[2], mockProducts[5]] });
      if (p === '/api/treasury/cash-boxes') return route.fulfill({ json: mockCashBoxes });
      if (p === '/api/finance/payment-vouchers') return route.fulfill({ json: [] });
      if (p === '/api/finance/receivables') return route.fulfill({ json: { totalOwed: 3450000, overdueAmount: 890000, customerCount: 14 } });
      if (p === '/api/rentals/totals' || p === '/api/rentals/contracts') {
        return route.fulfill({
          json: { activeContractsCount: 28, totalUnitsOut: 342, totalValueOut: 18500000, overdueCount: 3 },
        });
      }
      if (p === '/api/rentals/settings') return route.fulfill({ json: { overdueMultiplier: 1.5, depositEnabled: false, taxRate: 19, dayCutoffHour: 18, minimumDays: 1 } });
      if (p === '/api/rentals/settings/products') return route.fulfill({ json: [] });
      if (p === '/api/catalog/store/documents') return route.fulfill({ json: { contractWordings: [] } });

      return route.fulfill({ json: [] });
    });

    // 1. Sign in to Back Office
    await page.goto('http://127.0.0.1:5198/#/login');
    await page.evaluate(() => localStorage.setItem('pos.lang', 'ar'));
    await page.reload();
    await page.fill('#server', 'http://127.0.0.1:5198');
    await page.fill('#username', 'admin');
    await page.fill('#password', 'AnyPassword123');
    await page.click('button[type=submit]');
    await page.waitForURL(/office|register|dashboard/);

    // -------------------------------------------------------------
    // SHOT 1: DASHBOARD — ARABIC DESKTOP (1440px)
    // -------------------------------------------------------------
    await page.goto('http://127.0.0.1:5198/#/backoffice/dashboard');
    await page.waitForSelector('.bo-panel, .bo-metrics, app-dashboard, .bo-stack');
    await page.waitForTimeout(600);
    const shot1 = path.join(out, '01-dashboard-ar-1440.png');
    await page.screenshot({ path: shot1, fullPage: false });
    console.log(`✓ Shot 1 saved: ${shot1}`);

    // -------------------------------------------------------------
    // SHOT 2: DASHBOARD — FRENCH DESKTOP (1440px)
    // -------------------------------------------------------------
    await page.evaluate(() => localStorage.setItem('pos.lang', 'fr'));
    await page.reload();
    await page.waitForSelector('.bo-panel, .bo-metrics, app-dashboard, .bo-stack');
    await page.waitForTimeout(600);
    const shot2 = path.join(out, '02-dashboard-fr-1440.png');
    await page.screenshot({ path: shot2, fullPage: false });
    console.log(`✓ Shot 2 saved: ${shot2}`);

    // Revert back to Arabic as default
    await page.evaluate(() => localStorage.setItem('pos.lang', 'ar'));
    await page.reload();
    await page.waitForTimeout(500);

    // -------------------------------------------------------------
    // SHOT 3: PRODUCTS — ARABIC DESKTOP (1440px)
    // -------------------------------------------------------------
    await page.goto('http://127.0.0.1:5198/#/backoffice/inventory/products');
    await page.waitForSelector('.ui-table, app-backoffice-products, table');
    await page.waitForTimeout(600);
    const shot3 = path.join(out, '03-products-ar-1440.png');
    await page.screenshot({ path: shot3, fullPage: false });
    console.log(`✓ Shot 3 saved: ${shot3}`);

    // -------------------------------------------------------------
    // SHOT 4: STANDARD FORM — ARABIC DESKTOP (1440px)
    // -------------------------------------------------------------
    await page.goto('http://127.0.0.1:5198/#/backoffice/rentals/settings');
    await page.waitForSelector('.bo-panel, app-rental-settings, .bo-form');
    await page.waitForTimeout(600);
    const shot4 = path.join(out, '04-standard-form-ar-1440.png');
    await page.screenshot({ path: shot4, fullPage: false });
    console.log(`✓ Shot 4 saved: ${shot4}`);

    // -------------------------------------------------------------
    // SHOT 5: DIALOG / MODAL (1440px)
    // -------------------------------------------------------------
    await page.goto('http://127.0.0.1:5198/#/backoffice/inventory/products');
    await page.waitForSelector('.ui-table, app-backoffice-products, table');
    await page.waitForTimeout(400);
    const createBtn = page.locator('button.btn-primary');
    if (await createBtn.count()) {
      await createBtn.first().click();
      await page.waitForSelector('app-dialog[open], dialog[open], .ui-dialog');
      await page.waitForTimeout(500);
    }
    const shot5 = path.join(out, '05-dialog-modal-1440.png');
    await page.screenshot({ path: shot5, fullPage: false });
    console.log(`✓ Shot 5 saved: ${shot5}`);

    // Close dialog
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);

    // -------------------------------------------------------------
    // SHOT 6: BACK OFFICE — ARABIC 390px WITH DRAWER OPEN
    // -------------------------------------------------------------
    await page.setViewportSize({ width: 390, height: 844 });
    await page.evaluate(() => localStorage.setItem('pos.lang', 'ar'));
    await page.goto('http://127.0.0.1:5198/#/backoffice/dashboard');
    await page.waitForTimeout(500);
    const burgerAr = page.locator('.bar__menu, button.bar__menu');
    if (await burgerAr.count()) {
      await burgerAr.first().click();
      await page.waitForTimeout(400);
    }
    const shot6 = path.join(out, '06-backoffice-ar-390-drawer.png');
    await page.screenshot({ path: shot6, fullPage: false });
    console.log(`✓ Shot 6 saved: ${shot6}`);

    // Close drawer
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);

    // -------------------------------------------------------------
    // SHOT 7: BACK OFFICE — FRENCH 390px WITH DRAWER OPEN
    // -------------------------------------------------------------
    await page.evaluate(() => localStorage.setItem('pos.lang', 'fr'));
    await page.reload();
    await page.waitForTimeout(500);
    const burgerFr = page.locator('.bar__menu, button.bar__menu');
    if (await burgerFr.count()) {
      await burgerFr.first().click();
      await page.waitForTimeout(400);
    }
    const shot7 = path.join(out, '07-backoffice-fr-390-drawer.png');
    await page.screenshot({ path: shot7, fullPage: false });
    console.log(`✓ Shot 7 saved: ${shot7}`);

    // Close drawer
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);

    // -------------------------------------------------------------
    // SHOT 8: SIDEBAR EXPANDED (1440px)
    // -------------------------------------------------------------
    await page.setViewportSize({ width: 1440, height: 960 });
    await page.evaluate(() => localStorage.setItem('pos.lang', 'ar'));
    await page.reload();
    await page.waitForTimeout(500);
    const shot8 = path.join(out, '08-sidebar-expanded-1440.png');
    await page.screenshot({ path: shot8, fullPage: false });
    console.log(`✓ Shot 8 saved: ${shot8}`);

    // -------------------------------------------------------------
    // SHOT 9: SIDEBAR COLLAPSED (1440px)
    // -------------------------------------------------------------
    const toggleBtn = page.locator('button.bar__toggle');
    if (await toggleBtn.count()) {
      await toggleBtn.first().click();
      await page.waitForTimeout(400);
    }
    const shot9 = path.join(out, '09-sidebar-collapsed-1440.png');
    await page.screenshot({ path: shot9, fullPage: false });
    console.log(`✓ Shot 9 saved: ${shot9}`);

    await context.close();
    console.log(`\nAll ${stage} screenshots captured successfully in ${out}!\n`);
  } finally {
    await browser.close();
    await new Promise((resolve) => server.close(resolve));
  }
}

main().catch((err) => {
  console.error('Error capturing screenshots:', err);
  process.exit(1);
});
