const { requiredPassword } = require('./test-credentials.cjs');
const testPassword = requiredPassword();
// Photographs the built app against the mock API, so the design can be looked at rather than
// described. Not a test — nothing is asserted; it fails loudly only if a screen will not open.
//
//   node scripts/mock-api/server.js &
//   node tests/e2e/design-shots.cjs
//
// Output lands in artifacts/design (gitignored).
const { chromium } = require('playwright');
const path = require('path');

const BASE = process.env.BASE || 'http://localhost:5099';
const OUT = process.env.OUT || path.join(process.cwd(), 'artifacts', 'design');

(async () => {
  const browser = await chromium.launch({ args: ['--no-sandbox', '--disable-dev-shm-usage'] });
  const taken = [];

  async function shoot(name, width, height, steps) {
    // A fresh context each time: the till keeps a session, and a screenshot of the wrong
    // screen is worse than a missing one.
    const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 2 });
    const page = await context.newPage();
    page.on('pageerror', (e) => console.log('   PAGE ERROR:', e.message));
    try {
      await page.goto(BASE, { waitUntil: 'networkidle' });
      await steps(page);
      const file = path.join(OUT, name + '.png');
      await page.screenshot({ path: file });
      taken.push(file);
      console.log('shot  ' + name);
    } catch (e) {
      console.log('SKIP  ' + name + ' — ' + e.message.split('\n')[0]);
    }
    await context.close();
  }

  const setLanguage = async (page, lang) => {
    if (!lang) return;
    await page.selectOption('select.lang', lang).catch(() => {});
    await page.waitForTimeout(400);
  };

  async function signIn(page, lang) {
    await page.waitForSelector('.login-wrap', { timeout: 20000 });
    await setLanguage(page, lang);
    await page.fill('#server', BASE);
    await page.fill('#username', 'admin');
    await page.fill('#password', testPassword);
    // A till sign-in names its till: an empty code is an office sign-in since 2026-10-05.
    if (await page.locator('#device').count() === 0) await page.click('.shared-toggle');
    await page.fill('#device', 'C01');
    await page.click('button[type=submit]');
    await page.waitForSelector('.register', { timeout: 20000 });
    await page.waitForTimeout(800);
  }

  async function fillCart(page, lines = 3) {
    const tiles = page.locator('.tile');
    const count = Math.min(await tiles.count(), lines);
    for (let i = 0; i < count; i++) {
      await tiles.nth(i).click();
      await page.waitForTimeout(150);
    }
  }

  await shoot('01-login-fr', 1440, 900, async (page) => {
    await page.waitForSelector('.login-wrap', { timeout: 20000 });
  });

  await shoot('02-login-ar', 1440, 900, async (page) => {
    await page.waitForSelector('.login-wrap', { timeout: 20000 });
    await setLanguage(page, 'ar');
  });

  await shoot('03-till-fr', 1440, 900, async (page) => {
    await signIn(page);
    await fillCart(page);
  });

  await shoot('04-till-ar', 1440, 900, async (page) => {
    await signIn(page, 'ar');
    await fillCart(page);
  });

  await shoot('05-payment-fr', 1440, 900, async (page) => {
    await signIn(page);
    await fillCart(page);
    await page.click('button.pay');
    await page.waitForTimeout(700);
  });

  await shoot('06-payment-ar', 1440, 900, async (page) => {
    await signIn(page, 'ar');
    await fillCart(page);
    await page.click('button.pay');
    await page.waitForTimeout(700);
  });

  await shoot('07-till-phone-fr', 390, 844, async (page) => {
    await signIn(page);
    await fillCart(page);
  });

  await shoot('08-till-phone-ar', 390, 844, async (page) => {
    await signIn(page, 'ar');
    await fillCart(page);
  });

  await shoot('09-products-fr', 1440, 900, async (page) => {
    await signIn(page);
    await page.click('a[href*="products"]');
    await page.waitForTimeout(900);
  });

  await shoot('10-sync-ar', 1440, 900, async (page) => {
    await signIn(page, 'ar');
    await page.click('a[href*="sync"]');
    await page.waitForTimeout(900);
  });

  await browser.close();
  console.log('\n' + taken.length + ' screenshot(s) in ' + OUT);
})();

