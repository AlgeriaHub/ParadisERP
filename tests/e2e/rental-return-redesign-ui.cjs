// Comprehensive Playwright browser verification for the redesigned Rental Return Condition Screen.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');

const root = path.resolve(__dirname, '../..');
const out = path.join(root, 'artifacts/rental-return-redesign');
fs.mkdirSync(out, { recursive: true });

let assertions = 0;
function check(description, condition) {
  assert.equal(Boolean(condition), true, `FAILED assertion: ${description}`);
  assertions++;
  console.log(`  ✓ ${description}`);
}

const mockProducts = [
  { id: 'p1', sku: 'ART-0042', nameFr: 'Panneau coffrage 0.76 x 3.00 m', nameAr: 'لوح قولبة 0.76 × 3.00 م', nameEn: 'Formwork panel 0.76 x 3.00 m' },
  { id: 'p2', sku: 'ART-0043', nameFr: 'Étai métallique 3.50 m', nameAr: 'دعامة معدنية 3.50 م', nameEn: 'Steel prop 3.50 m' },
  { id: 'p3', sku: 'ART-0044', nameFr: 'Cadre échafaudage 2.00 m', nameAr: 'إطار سقالة 2.00 م', nameEn: 'Scaffold frame 2.00 m' },
  { id: 'p4', sku: 'ART-0045', nameFr: 'Plancher acier 3.00 m', nameAr: 'أرضية فولاذية 3.00 م', nameEn: 'Steel plank 3.00 m' },
  { id: 'p5', sku: 'ART-0046', nameFr: 'Lisse de sécurité 3.00 m', nameAr: 'درابزين حماية 3.00 م', nameEn: 'Safety guardrail 3.00 m' },
];

const mockLines = [
  { id: 'l1', contractId: 'ctr-1', productId: 'p1', sku: 'ART-0042', nameFr: 'Panneau coffrage 0.76 x 3.00 m', nameAr: 'لوح قولبة 0.76 × 3.00 م', nameEn: 'Formwork panel 0.76 x 3.00 m', qty: 9, issuedQty: 9, stillOutQty: 9, returnedQty: 0, rentUnitPrice: 120, damageUnitPrice: 3500, firstIssuedOn: '2026-10-14' },
  { id: 'l2', contractId: 'ctr-1', productId: 'p2', sku: 'ART-0043', nameFr: 'Étai métallique 3.50 m', nameAr: 'دعامة معدنية 3.50 م', nameEn: 'Steel prop 3.50 m', qty: 4, issuedQty: 4, stillOutQty: 4, returnedQty: 0, rentUnitPrice: 80, damageUnitPrice: 4500, firstIssuedOn: '2026-10-14' },
  { id: 'l3', contractId: 'ctr-1', productId: 'p3', sku: 'ART-0044', nameFr: 'Cadre échafaudage 2.00 m', nameAr: 'إطار سقالة 2.00 م', nameEn: 'Scaffold frame 2.00 m', qty: 10, issuedQty: 10, stillOutQty: 10, returnedQty: 0, rentUnitPrice: 150, damageUnitPrice: 6000, firstIssuedOn: '2026-10-14' },
  { id: 'l4', contractId: 'ctr-1', productId: 'p4', sku: 'ART-0045', nameFr: 'Plancher acier 3.00 m', nameAr: 'أرضية فولاذية 3.00 م', nameEn: 'Steel plank 3.00 m', qty: 8, issuedQty: 8, stillOutQty: 8, returnedQty: 0, rentUnitPrice: 110, damageUnitPrice: 5500, firstIssuedOn: '2026-10-14' },
  { id: 'l5', contractId: 'ctr-1', productId: 'p5', sku: 'ART-0046', nameFr: 'Lisse de sécurité 3.00 m', nameAr: 'درابزين حماية 3.00 م', nameEn: 'Safety guardrail 3.00 m', qty: 6, issuedQty: 6, stillOutQty: 6, returnedQty: 0, rentUnitPrice: 50, damageUnitPrice: 2000, firstIssuedOn: '2026-10-14' },
];

const mockContract = {
  id: 'ctr-1',
  number: 'CTR-0042',
  customerId: 'cust-1',
  customerName: 'Entreprise Bâtiment Moderne',
  branchId: 'b-main',
  siteName: 'Chantier Bab Ezzouar',
  siteAddress: 'Zone Industrielle, Alger',
  isLocked: true,
  status: 'Active',
  signatoryName: 'Karim Hadj',
  signatoryIdNumber: '109 876 543',
  lines: mockLines,
};

const mockBranches = [
  { id: 'b-main', nameFr: 'Succursale Alger', nameAr: 'فرع الجزائر', nameEn: 'Algiers Branch', isMain: true, isActive: true },
];

const mockCustomer = {
  id: 'cust-1',
  code: 'CLI-0089',
  fullName: 'Entreprise Bâtiment Moderne',
  kind: 'Company',
  creditLimit: 500000,
  balance: 0,
};

const permissions = [
  'rentals.read',
  'rentals.issue',
  'rentals.return',
  'rentals.contracts',
  'rentals.settings',
  'catalog.view',
];

const mockUser = {
  id: 'u-admin',
  username: 'admin',
  fullName: 'Chef de Parc',
  role: 'Admin',
  isActive: true,
  permissions,
  branchLocked: false,
};

async function main() {
  console.log('\n======================================================');
  console.log('STARTING RENTAL RETURN REDESIGN E2E BROWSER TEST SUITE');
  console.log('======================================================\n');

  const webDist = path.join(root, 'ParadisERP.WebUI/dist/ParadisERP-web-ui/browser');
  if (!fs.existsSync(webDist)) {
    throw new Error('Production Angular build not found. Run npm run build first.');
  }

  // Local static file server on 5195
  const server = http.createServer((req, res) => {
    const requested = decodeURIComponent(new URL(req.url, 'http://127.0.0.1:5195').pathname);
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

  await new Promise((resolve) => server.listen(5195, '127.0.0.1', resolve));

  const browser = await chromium.launch({ headless: true, channel: 'chrome' });

  try {
    // ---------------------------------------------------------------
    // SCENARIO A: FRENCH DESKTOP (1440px)
    // - 5 compact item cards
    // - "All returned in good condition" convenience action
    // - Collapsing / reopening card preserves values
    // - Line 2: Good + Repairable + Damaged + Missing in ONE return
    // - Progressive disclosure of repair cost & loss date
    // - Line 3: Partial return (4 out of 10)
    // - Line 1: NotUsed (same day), Line 2: Used (same day)
    // - Paid contract refund: customer credit note relief calculation
    // - Top document summary: separate physical units vs money
    // - Validation failure retains all draft values & expands error card
    // ---------------------------------------------------------------
    console.log('Scenario A: French Desktop (1440px)');
    {
      const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, serviceWorkers: 'block' });
      await context.addInitScript(() => localStorage.setItem('pos.lang', 'fr'));
      const page = await context.newPage();
      const pageErrors = [];
      page.on('pageerror', (e) => pageErrors.push(e.message));

      let rejectReturnSave = false;

      await page.route('**/api/**', async (route) => {
        const url = new URL(route.request().url());
        const p = url.pathname;
        const method = route.request().method();

        if (p === '/api/auth/login') {
          return route.fulfill({
            json: {
              token: 'mock-jwt-token',
              expiresAtUtc: '2099-01-01T00:00:00Z',
              user: mockUser,
              deviceCode: 'DEV-01',
              deviceBranchId: 'b-main',
            },
          });
        }
        if (p === '/api/catalog/branches') return route.fulfill({ json: mockBranches });
        if (p === '/api/catalog/products') return route.fulfill({ json: mockProducts });
        if (p === '/api/catalog/customers') return route.fulfill({ json: [mockCustomer] });
        if (p === '/api/rentals/settings') {
          return route.fulfill({
            json: { minimumDays: 1, dayCutoffHour: 18, taxRate: 19, depositEnabled: false, overdueMultiplier: 2 },
          });
        }
        if (p.startsWith('/api/rentals/contracts/') && p.endsWith('/return-notes/preview')) {
          const body = route.request().postDataJSON();
          const l1 = body.lines.find((x) => x.contractLineId === 'l1');
          const l2 = body.lines.find((x) => x.contractLineId === 'l2');
          let chargeTotal = 0;
          const charges = [];

          if (l2) {
            if ((l2.maintenanceValue || 0) > 0) {
              charges.push({ kind: 'Maintenance', description: 'Coût total de réparation', amount: l2.maintenanceValue, lineId: 'l2' });
              chargeTotal += l2.maintenanceValue;
            }
            if ((l2.qtyTotalDamage || 0) > 0) {
              const dmg = (l2.qtyTotalDamage || 0) * 4500;
              charges.push({ kind: 'Damage', description: 'Perte / casse irréversible', amount: dmg, lineId: 'l2' });
              chargeTotal += dmg;
            }
            if ((l2.qtyMissing || 0) > 0) {
              const miss = (l2.qtyMissing || 0) * 4500;
              charges.push({ kind: 'Missing', description: 'Non retourné / perdu', amount: miss, lineId: 'l2' });
              chargeTotal += miss;
            }
            if (l2.sameDayUsage === 'Used') {
              charges.push({ kind: 'Rent', description: 'Location 1 jour (utilisé le jour même)', amount: 80, lineId: 'l2' });
              chargeTotal += 80;
            }
          }

          const reliefCredit = l1?.sameDayUsage === 'NotUsed' ? 3600 : 0;
          return route.fulfill({
            json: {
              canConfirm: true,
              chargeTotal,
              charges,
              unusedRent: reliefCredit,
              customerBalance: 0,
              previouslyPaid: 2000,
              customerCredit: reliefCredit,
              reliefDebtReduction: 0,
              reliefPaymentVoucher: reliefCredit,
              isClosed: false,
              late: null,
              problems: [],
              rows: body.lines.map((l, idx) => ({
                returnLineIndex: idx,
                contractLineId: l.contractLineId,
                issueNoteId: 'iss-1',
                issueNumber: 'ISS-0001',
                issuedAtUtc: '2026-10-14T08:00:00Z',
                qty: l.qtyOk + (l.qtyPartialDamage || 0) + (l.qtyTotalDamage || 0) + (l.qtyMissing || 0),
                physicalQty: l.qtyOk + (l.qtyPartialDamage || 0),
                sameDayUsageRequired: l.contractLineId === 'l1' || l.contractLineId === 'l2',
                sameDayUsage: l.sameDayUsage || null,
                chargeableDays: l.sameDayUsage === 'Used' ? 1 : (l.sameDayUsage === 'NotUsed' ? 0 : null),
                dailyUnitRate: 80,
                rentalCharge: l.sameDayUsage === 'Used' ? 80 : 0,
              })),
            },
          });
        }
        if (p.endsWith('/issue-notes') && method === 'GET') {
          return route.fulfill({
            json: [{ id: 'iss-1', number: 'ISS-0001', branchId: 'b-main', contractId: 'ctr-1', issuedAtUtc: '2026-10-14T08:00:00Z', periodRent: 2000, lines: [] }],
          });
        }
        if (p.endsWith('/return-notes') && method === 'GET') {
          return route.fulfill({ json: [] });
        }
        if (p.endsWith('/extensions') && method === 'GET') {
          return route.fulfill({ json: [] });
        }
        if (p.endsWith('/settlement') && method === 'GET') {
          return route.fulfill({
            json: {
              contractId: 'ctr-1',
              currency: 'DZD',
              totalAgreed: 2000,
              totalPaid: 2000,
              balanceDue: 0,
              lines: [],
            },
          });
        }
        if (p.startsWith('/api/rentals/contracts/ctr-1') && method === 'GET') {
          return route.fulfill({
            json: mockContract,
          });
        }
        if (p.startsWith('/api/rentals/contracts/') && p.endsWith('/return-notes') && method === 'POST') {
          if (rejectReturnSave) {
            return route.fulfill({
              status: 400,
              json: { message: 'Refus de validation serveur synthétique : vérification des données requise.' },
            });
          }
          return route.fulfill({
            status: 201,
            json: {
              id: 'ret-0001',
              number: 'RET-0001',
              returnedAtUtc: '2026-10-14T14:00:00Z',
              chargeTotal: 9580,
              reliefPaymentVoucher: 3600,
            },
          });
        }
        if (p === '/api/catalog/store/documents') return route.fulfill({ json: { contractWordings: [] } });
        return route.fulfill({ json: [] });
      });

      // Navigate and login
      await page.goto('http://127.0.0.1:5195/#/login');
      await page.fill('#server', 'http://127.0.0.1:5195');
      await page.fill('#username', 'admin');
      await page.fill('#password', 'AnyPassword123');
      await page.click('button[type=submit]');
      await page.waitForURL(/office|register/);

      // Open Return note screen
      await page.goto('http://127.0.0.1:5195/#/backoffice/rentals/contracts/ctr-1/return');
      await page.waitForSelector('app-rental-return-note');

      // Fill return metadata
      await page.fill('#rr-date', '2026-10-14');
      await page.fill('#rr-time', '14:00');
      await page.check('#rr-by-customer');

      // 1. Verify 5 compact cards rendered
      const cards = page.locator('.return-item-card');
      check('Rendered all 5 outstanding rental item cards', (await cards.count()) === 5);

      // 2. Test "All in good condition" convenience action on Line 1 (ART-0042)
      const line1Card = page.locator('#rc-card-l1');
      await line1Card.locator('.btn-convenience').click();
      // Wait for server preview response
      await page.waitForResponse((r) => r.url().includes('/return-notes/preview'));
      await page.waitForTimeout(200);

      // Check live reconciliation state on Line 1
      const l1ReconBadge = line1Card.locator('.reconcile-pill');
      const l1ReconText = await l1ReconBadge.textContent();
      check('Line 1 shows completed allocation in pill', l1ReconText.includes('9'));

      // Requirement 5: Unresolved error (same-day usage required) forces card open, never hidden!
      check('Line 1 card remains expanded / auto-expands due to unresolved same-day usage', await line1Card.locator('.return-card-body').isVisible());
      check('Line 1 displays same-day usage required notice', await line1Card.locator('.usage-choice .bo-error').isVisible());

      // Select same-day NotUsed for Line 1
      await line1Card.locator('input[value="NotUsed"]').click();
      await page.waitForTimeout(350);

      // Once error is resolved, Line 1 automatically returns to its collapsed summary state (Requirement 5 & 7)
      check('Line 1 collapsed into summary after error resolved', await line1Card.locator('.return-card-collapsed-summary').isVisible());

      // 3. Test expanding and re-collapsing preserves values
      await line1Card.locator('.btn-toggle-card').click(); // Re-open to edit
      await page.waitForTimeout(100);
      const l1OkVal = await page.inputValue('#rr-okl1');
      check('Line 1 re-expanded and qtyOk still 9', l1OkVal === '9');
      check('Line 1 preserved NotUsed choice', await line1Card.locator('input[value="NotUsed"]').isChecked());

      await line1Card.locator('.btn-toggle-card').click(); // Collapse back
      await page.waitForTimeout(100);
      check('Line 1 re-collapsed into summary', await line1Card.locator('.return-card-collapsed-summary').isVisible());

      // 4. Test Line 2 (ART-0043): Good + Repairable + Fully Damaged + Missing in ONE return
      const line2Card = page.locator('#rc-card-l2');
      await line2Card.locator('#rr-okl2').fill('1');
      await line2Card.locator('#rr-pdl2').fill('1');
      // Progressive disclosure: repair cost must now appear
      check('Repair cost field progressively revealed when repairable > 0', await line2Card.locator('#rr-mvl2').isVisible());
      await line2Card.locator('#rr-mvl2').fill('500');

      await line2Card.locator('#rr-tdl2').fill('1');

      await line2Card.locator('#rr-msl2').fill('1');
      // Progressive disclosure: loss report date must now appear
      check('Loss report date field progressively revealed when missing > 0', await line2Card.locator('#rr-rpl2').isVisible());
      check('Loss report date is disabled when mixed with returned items (pureLoss rule)', await line2Card.locator('#rr-rpl2').isDisabled());

      // Check distinct visual missing tag
      check('Missing column has distinct visual badge and dashed boundary', await line2Card.locator('.col-missing .missing-tag').isVisible());

      // Reconciliation on Line 2: 1+1+1+1 = 4 of 4
      const l2ReconText = await line2Card.locator('.reconcile-pill').textContent();
      check('Line 2 reconciled 4 of 4 allocated', l2ReconText.includes('4'));

      // Select same-day Used for Line 2
      await line2Card.locator('input[value="Used"]').click();
      await page.waitForTimeout(350);

      // 5. Test Line 3 (ART-0044): Partial return (4 out of 10)
      const line3Card = page.locator('#rc-card-l3');
      await line3Card.locator('#rr-okl3').fill('4');
      const l3ReconText = await line3Card.locator('.reconcile-pill').textContent();
      check('Line 3 partial return reconciliation displays remaining count', l3ReconText.includes('4') && l3ReconText.includes('6'));

      // Test Line 5 pure loss (ART-0046, 6 missing, pure loss rule enables date input)
      const line5Card = page.locator('#rc-card-l5');
      await line5Card.locator('#rr-msl5').fill('6');
      check('Loss report date field visible on Line 5', await line5Card.locator('#rr-rpl5').isVisible());
      check('Loss report date is enabled when pure loss (pureLoss rule)', await line5Card.locator('#rr-rpl5').isEnabled());
      await line5Card.locator('#rr-rpl5').fill('2026-10-14');

      // 6. Test Document Summary Strip (Physical vs Financial)
      const docSummary = page.locator('.return-doc-summary');
      check('Top document summary strip is visible', await docSummary.isVisible());

      // Allow server preview debounce to settle
      await page.waitForTimeout(400);

      // Verify financial metrics separated from physical quantities
      const finText = await page.locator('.return-financial-strip').textContent();
      check('Document summary displays total repair cost separately', finText.includes('500'));

      const creditRefund = await page.locator('.bo-metric, .usage-preview, .bo-panel').allTextContents();
      check('Customer Credit Note relief is clearly displayed', creditRefund.some((t) => t.replace(/\s+/g, ' ').includes('3 600')));

      // 7. Test Validation Failure Preserves All Values
      rejectReturnSave = true;
      const confirmButton = page.locator('.ui-actionbar .btn-primary');
      await confirmButton.click();
      const modalConfirm = page.locator('app-confirm-dialog button.btn-primary');
      if (await modalConfirm.count()) await modalConfirm.click();

      // Error alert must appear
      await page.waitForSelector('app-rental-return-note .notice.danger');
      check('Validation refusal alert rendered without clearing form', await page.locator('app-rental-return-note .notice.danger').isVisible());

      // Confirm all values are intact!
      await line1Card.locator('.btn-toggle-card').click(); // Re-open to inspect values
      await page.waitForTimeout(100);
      check('Line 1 preserved qtyOk after server refusal', (await page.inputValue('#rr-okl1')) === '9');
      check('Line 1 preserved NotUsed choice', await line1Card.locator('input[value="NotUsed"]').isChecked());
      check('Line 2 preserved qtyOk after server refusal', (await page.inputValue('#rr-okl2')) === '1');
      check('Line 2 preserved qtyPartialDamage after server refusal', (await page.inputValue('#rr-pdl2')) === '1');
      check('Line 2 preserved repair cost after server refusal', (await page.inputValue('#rr-mvl2')) === '500');
      check('Line 2 preserved qtyTotalDamage after server refusal', (await page.inputValue('#rr-tdl2')) === '1');
      check('Line 2 preserved qtyMissing after server refusal', (await page.inputValue('#rr-msl2')) === '1');
      check('Line 5 preserved loss report date after server refusal', (await page.inputValue('#rr-rpl5')) === '2026-10-14');
      check('Line 2 preserved Used choice', await line2Card.locator('input[value="Used"]').isChecked());
      check('Line 3 preserved partial return qtyOk', (await page.inputValue('#rr-okl3')) === '4');

      // 8. Capture French Desktop Screenshot
      const frDesktopShot = path.join(out, 'french-desktop-1440.png');
      await page.screenshot({ path: frDesktopShot, fullPage: true });
      check(`Saved French Desktop screenshot: ${frDesktopShot}`, fs.existsSync(frDesktopShot));

      check('Zero unexpected page errors', pageErrors.length === 0);
      await context.close();
    }

    // ---------------------------------------------------------------
    // SCENARIO B: ARABIC RTL DESKTOP (1440px) & MOBILE PHONE (390px)
    // - Arabic dictionary rendering
    // - RTL direction & logical CSS alignment
    // - Phone 390px responsive layout (zero horizontal scroll)
    // ---------------------------------------------------------------
    console.log('\nScenario B: Arabic RTL Desktop (1440px) & Mobile (390px)');
    {
      const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, serviceWorkers: 'block' });
      await context.addInitScript(() => localStorage.setItem('pos.lang', 'ar'));
      const page = await context.newPage();
      const pageErrors = [];
      page.on('pageerror', (e) => pageErrors.push(e.message));

      await page.route('**/api/**', async (route) => {
        const url = new URL(route.request().url());
        const p = url.pathname;
        const method = route.request().method();

        if (p === '/api/auth/login') {
          return route.fulfill({
            json: { token: 'mock-jwt-token', expiresAtUtc: '2099-01-01T00:00:00Z', user: mockUser, deviceCode: 'DEV-01', deviceBranchId: 'b-main' },
          });
        }
        if (p === '/api/catalog/branches') return route.fulfill({ json: mockBranches });
        if (p === '/api/catalog/products') return route.fulfill({ json: mockProducts });
        if (p === '/api/catalog/customers') return route.fulfill({ json: [mockCustomer] });
        if (p === '/api/rentals/settings') return route.fulfill({ json: { minimumDays: 1, dayCutoffHour: 18, taxRate: 19 } });
        if (p.startsWith('/api/rentals/contracts/') && p.endsWith('/return-notes/preview')) {
          return route.fulfill({
            json: { canConfirm: true, chargeTotal: 0, charges: [], reliefDebtReduction: 0, reliefPaymentVoucher: 0, isClosed: false, rows: [] },
          });
        }
        if (p.endsWith('/issue-notes') && method === 'GET') {
          return route.fulfill({
            json: [{ id: 'iss-1', number: 'ISS-0001', branchId: 'b-main', contractId: 'ctr-1', issuedAtUtc: '2026-10-14T08:00:00Z', periodRent: 2000, lines: [] }],
          });
        }
        if (p.endsWith('/return-notes') && method === 'GET') {
          return route.fulfill({ json: [] });
        }
        if (p.endsWith('/extensions') && method === 'GET') {
          return route.fulfill({ json: [] });
        }
        if (p.endsWith('/settlement') && method === 'GET') {
          return route.fulfill({
            json: {
              contractId: 'ctr-1',
              currency: 'DZD',
              totalAgreed: 2000,
              totalPaid: 2000,
              balanceDue: 0,
              lines: [],
            },
          });
        }
        if (p.startsWith('/api/rentals/contracts/ctr-1') && method === 'GET') {
          return route.fulfill({
            json: mockContract,
          });
        }
        return route.fulfill({ json: [] });
      });

      await page.goto('http://127.0.0.1:5195/#/login');
      await page.fill('#server', 'http://127.0.0.1:5195');
      await page.fill('#username', 'admin');
      await page.fill('#password', 'AnyPassword123');
      await page.click('button[type=submit]');
      await page.waitForURL(/office|register/);

      await page.goto('http://127.0.0.1:5195/#/backoffice/rentals/contracts/ctr-1/return');
      await page.waitForSelector('app-rental-return-note');

      // Check RTL direction on HTML element
      const dir = await page.getAttribute('html', 'dir');
      check('Page has dir="rtl" in Arabic', dir === 'rtl');

      // Verify Arabic labels on cards
      const line1Card = page.locator('#rc-card-l1');
      const soundBtnText = await line1Card.locator('.btn-convenience').textContent();
      check('Arabic "All in good condition" button text (الكل سليم)', soundBtnText.includes('سليم'));

      const soundLabel = await line1Card.locator('label[for="rr-okl1"]').textContent();
      check('Arabic "Returned good" label (سليمة)', soundLabel.includes('سليمة'));

      const repairLabel = await line1Card.locator('label[for="rr-pdl1"]').textContent();
      check('Arabic "Repairable" label (قابلة للإصلاح)', repairLabel.includes('قابلة للإصلاح'));

      const damageLabel = await line1Card.locator('label[for="rr-tdl1"]').textContent();
      check('Arabic "Fully damaged" label (تالفة كلياً)', damageLabel.includes('تالفة'));

      const missingLabel = await line1Card.locator('label[for="rr-msl1"]').textContent();
      check('Arabic "Missing" label (لم تعد أبداً)', missingLabel.includes('لم تعد'));

      // Capture Arabic Desktop Screenshot
      const arDesktopShot = path.join(out, 'arabic-desktop-1440.png');
      await page.screenshot({ path: arDesktopShot, fullPage: true });
      check(`Saved Arabic Desktop screenshot: ${arDesktopShot}`, fs.existsSync(arDesktopShot));

      // Test Mobile Phone (390px)
      await page.setViewportSize({ width: 390, height: 844 });
      await page.waitForTimeout(200);

      const hasHorizontalScroll = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
      check('Arabic mobile view (390px) has ZERO horizontal scroll', !hasHorizontalScroll);

      // Capture Arabic Mobile Screenshot
      const arMobileShot = path.join(out, 'arabic-phone-390.png');
      await page.screenshot({ path: arMobileShot, fullPage: true });
      check(`Saved Arabic Phone screenshot: ${arMobileShot}`, fs.existsSync(arMobileShot));

      check('Zero unexpected page errors in Arabic', pageErrors.length === 0);
      await context.close();
    }

    console.log(`\n======================================================`);
    console.log(`ALL BROWSER E2E TESTS PASSED (${assertions} assertions)`);
    console.log(`======================================================\n`);
  } finally {
    await browser.close();
    await new Promise((resolve) => server.close(resolve));
  }
}

main().catch((err) => {
  console.error('\nE2E EXECUTION ERROR:', err);
  process.exit(1);
});

