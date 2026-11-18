// Render the real UI with synthetic fixtures only; never contacts the production API.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');
const root = path.resolve('ParadiERP.WebUI/dist/paradierp-web-ui/browser');
const output = path.resolve('artifacts/design/screen-redesign');
fs.mkdirSync(output, { recursive: true });
const products = [{ id: 'product1', sku: 'LOC-001', barcode: '6130000000001', nameFr: 'Générateur de chantier', nameAr: 'مولد كهربائي', nameEn: 'Site generator', price: 500, taxRate: 0, unit: 'pcs', isActive: true, isRentable: true, trackStock: true, stockQty: 20, reorderLevel: 2, version: 1, isDeleted: false }];
const branches = [{ id: 'branch1', nameFr: 'Agence centrale', nameAr: 'الفرع الرئيسي', nameEn: 'Main branch', isActive: true }];
const customers = [{ id: 'customer1', code: 'DEMO-01', fullName: 'Client démonstration', kind: 'Person', balance: 0, creditLimit: 0 }];
const permissions = [...new Set(['payroll.manage', 'hr.attendance', ...[...fs.readFileSync('ParadiERP.Domain/Authorization/PermissionCatalog.cs', 'utf8').matchAll(/public const string \w+ = "([^"]+)"/g)].map(m => m[1])])];
const user = { id: 'ui-admin', username: 'admin', fullName: 'Administrateur', role: 'Admin', permissions, capabilities: ['customers.create'], isActive: true, branchLocked: false };
const server = http.createServer((req, res) => {
  const requested = new URL(req.url, 'http://localhost').pathname;
  const file = path.resolve(root, '.' + (requested === '/' ? '/index.html' : requested));
  if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404); return res.end(); }
  res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' })[path.extname(file)] || 'application/octet-stream');
  fs.createReadStream(file).pipe(res);
});
/** Waits for an attribute to settle: a fold redraws on the next render, not inside the key press. */
async function expectAttr(locator, name, value) {
  for (let i = 0; i < 40; i++) {
    if ((await locator.getAttribute(name)) === value) return;
    await new Promise(r => setTimeout(r, 50));
  }
  assert.equal(await locator.getAttribute(name), value);
}
// The word the back office prints beside a required field, per language (a .bo-req also says "optional").
const requiredWord = Object.fromEntries([...fs.readFileSync('ParadiERP.WebUI/src/app/core/i18n/translations-backoffice.ts', 'utf8')
  .matchAll(/'common.required': '([^']+)'/g)].map((m, i) => [['fr', 'ar', 'en'][i], m[1]]));
async function main() {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const browser = await chromium.launch({ headless: true, channel: 'chrome' });
  let checks = 0;
  const pages = ['register', 'products', 'sales', 'sync', 'backoffice/inventory/products', 'backoffice/customers/list', 'backoffice/rentals/contracts/new', 'backoffice/rentals/settings', 'backoffice/finance/settings'];
  // Additional routes exercise empty/error states; populated financial workflows have dedicated suites.
  if (process.argv.includes('--all-routes')) {
    const nav = fs.readFileSync('ParadiERP.WebUI/src/app/core/nav.ts', 'utf8');
    for (const match of nav.matchAll(/path: '([^']+)'/g)) {
      if (match[1].startsWith('/') || match[1].includes('subscription')) continue;
      const route = `backoffice/${match[1]}`;
      if (!pages.includes(route)) pages.push(route);
    }
  }
  try {
    for (const lang of ['fr', 'ar']) {
      const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, serviceWorkers: 'block' });
      await context.addInitScript(l => localStorage.setItem('pos.lang', l), lang);
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.route('**/api/**', async route => {
        const p = new URL(route.request().url()).pathname;
        let data;
        if (p === '/api/auth/login') data = { token: 'synthetic-ui-only', expiresAtUtc: '2099-01-01T00:00:00Z', user, deviceCode: 'C01', deviceBranchId: 'branch1', deviceId: 'device1' };
        if (p === '/api/sync/pull') data = { cursor: 1, hasMore: false, products, categories: [], customers, users: [user], branches, store: { id: 'store1', nameFr: 'Atelier Location', nameAr: 'مؤسسة التأجير', fiscalIds: {} } };
        if (p === '/api/catalog/products') data = products;
        if (p === '/api/catalog/customers') data = customers;
        if (p === '/api/catalog/branches') data = branches;
        if (['/api/catalog/categories', '/api/treasury/payment-methods', '/api/treasury/cash-boxes', '/api/catalog/users'].includes(p)) data = [];
        if (p === '/api/rentals/settings') data = { overdueMultiplier: 2, depositEnabled: false, taxRate: 0 };
        if (p === '/api/rentals/settings/products') data = products.map(x => ({ productId: x.id, rentalUnitPrice: 500, rentalDamageUnitPrice: 10000, overdueMultiplier: null }));
        if (p === '/api/catalog/store/documents') data = { contractWordings: [] };
        if (data === undefined) return route.fulfill({ status: 503, json: { message: 'Synthetic preview: data unavailable.' } });
        await route.fulfill({ json: data });
      });
      await page.goto(`${origin}/#/login`);
      await page.locator('#username').waitFor();
      await page.evaluate(() => document.fonts.ready);
      for (const width of [1440, 390]) {
        await page.setViewportSize({ width, height: 1000 });
        await page.screenshot({ path: path.join(output, `${lang}-${width}-login.png`), fullPage: true });
      }
      await page.fill('#server', origin);
      await page.fill('#username', 'admin');
      await page.fill('#password', 'synthetic-only');
      await page.click('button[type=submit]');
      await page.waitForURL(/office|register/);
      for (const route of pages) {
        console.log(`Checking ${lang} ${route}`);
        await page.goto(`${origin}/?review=${checks}#/${route}`);
        await page.waitForFunction(() => !document.querySelector('app-root')?.textContent?.includes('Loading...'));
        await page.locator(route === 'register' ? '.register' : 'h1').first().waitFor();
        if (route.startsWith('backoffice/')) await page.locator('.work').waitFor();
        await page.waitForLoadState('networkidle');
        // Owner, 2026-10-08: a long form opens where the work starts — a visible section holding a
        // required field is never folded on arrival.
        const closedRequired = await page.evaluate(word => [...document.querySelectorAll('app-fold')]
          .filter(fold => fold.getClientRects().length && (fold.querySelector('[required]')
            || [...fold.querySelectorAll('.bo-req')].some(mark => mark.textContent.trim() === word)))
          .filter(fold => fold.querySelector('h3 button')?.getAttribute('aria-expanded') !== 'true')
          .map(fold => fold.querySelector('h3 button')?.textContent?.trim()), requiredWord[lang]);
        assert.deepEqual(closedRequired, [], `${route}: sections with required fields start open`);
        checks++;
        assert.equal(new URL(page.url()).hash, `#/${route}`, 'The requested screen must render, not a guard redirect');
        await page.evaluate(() => document.fonts.ready);
        for (const width of [1440, 768, 390]) {
          await page.setViewportSize({ width, height: 1000 });
          assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${lang} ${width} ${route}: page overflow`);
          checks++;
          await page.screenshot({ path: path.join(output, `${lang}-${width}-${route.replaceAll('/', '-')}.png`), fullPage: true });
        }
        if (route.endsWith('contracts/new')) {
          await page.locator('app-rental-contract-form').waitFor();
          const first = page.locator('app-rental-contract-form app-fold').first().locator('h3 button');
          assert.equal(await first.getAttribute('aria-expanded'), 'true', 'The first section starts open');
          // A draft survives folding its section away and back, by keyboard.
          await page.fill('#rf-notes', 'Draft preserved');
          const notes = page.locator('app-rental-contract-form app-fold', { has: page.locator('#rf-notes') }).locator('h3 button');
          await notes.focus();
          await page.keyboard.press('Enter');
          await expectAttr(notes, 'aria-expanded', 'false');
          await page.keyboard.press('Enter');
          await expectAttr(notes, 'aria-expanded', 'true');
          assert.equal(await page.inputValue('#rf-notes'), 'Draft preserved');
          // The items are the work: never folded.
          assert.equal(await page.locator('app-rental-contract-form app-fold #rf-scan').count(), 0, 'Items are not inside a fold');
          checks += 5;
        }
      }
      assert.deepEqual(errors, [], `${lang}: browser errors`);
      checks++;
      await context.close();
    }
    console.log(`PASS ${checks} layout and disclosure checks; screenshots: ${output}`);
  } finally { await browser.close(); server.close(); }
}
main().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
