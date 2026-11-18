const { requiredPassword } = require('./test-credentials.cjs');
const testPassword = requiredPassword();
// The till's finished-sale screen follows the sale's sync (found 2026-09-17, fixed 2026-11-02).
// Checkout hands the screen its sale pending; the push that follows marked it synced in IndexedDB
// only, so the screen kept saying "saved offline" and the send-receipt form — shown once a sale is
// synced — never appeared. This drives one online cash sale with SMS receipts switched on and checks
// that, once the sale reaches the server, the badge goes and the form comes, without a reload.
//
//   ADMIN_PASSWORD=... node tests/e2e/send-receipt-after-sync.cjs   (after `npm run build` in AlgeriaPos.WebUI)
const { spawn } = require('node:child_process');
const path = require('node:path');
const { chromium } = require('playwright');

const PORT = Number(process.env.MOCK_PORT || 5097);
const BASE = `http://localhost:${PORT}`;
const INTEGRATIONS = { cardMode: 'None', requireCardReference: false, smsReceipts: true, emailReceipts: false };

let failures = 0;
const check = (name, ok, extra = '') => {
  console.log(`${ok ? '✅' : '❌'} ${name}${extra ? ' — ' + extra : ''}`);
  if (!ok) failures++;
};

const state = async () => (await fetch(`${BASE}/__state`)).json();

async function waitFor(predicate, timeoutMs = 15000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const value = await predicate();
      if (value) return value;
    } catch {
      /* not there yet */
    }
    await new Promise((r) => setTimeout(r, 250));
  }
  return null;
}

(async () => {
  const mock = spawn(process.execPath, [path.resolve(__dirname, '../../scripts/mock-api/server.js')], {
    env: { ...process.env, MOCK_PORT: String(PORT), MOCK_INTEGRATIONS: JSON.stringify(INTEGRATIONS) },
    stdio: 'ignore',
  });

  let browser;
  try {
    const up = await waitFor(async () => (await fetch(`${BASE}/__state`)).ok);
    if (!up) throw new Error(`mock api never came up on ${BASE}`);

    browser = await chromium.launch({ headless: true, channel: 'chrome' });
    const page = await (await browser.newContext()).newPage();
    const pageErrors = [];
    page.on('pageerror', (e) => pageErrors.push(e.message));

    await page.goto(BASE, { waitUntil: 'networkidle' });
    await page.fill('#server', BASE);
    await page.fill('#username', 'admin');
    await page.fill('#password', testPassword);
    if (await page.locator('#device').count() === 0) await page.click('.shared-toggle');
    await page.fill('#device', 'C01');
    await page.click('button[type=submit]');
    await page.waitForSelector('.tile', { timeout: 30000 });

    await page.locator('.tile .tile-add').first().click();
    await page.click('button.pay');
    await page.waitForSelector('.modal');
    await page.fill('#received', '1000');
    await page.click('.actions .btn-primary');
    await page.waitForSelector('.done', { timeout: 10000 });
    check('the sale completes', true);

    const reached = await waitFor(async () => ((await state()).sales.length === 1 ? true : null), 20000);
    check('the sale reaches the server', !!reached);

    // The push that carried it marks it synced; the screen reads it again without a reload.
    const badgeGone = await waitFor(async () => ((await page.locator('.done .badge').count()) === 0 ? true : null), 10000);
    check('"saved offline" goes once the sale is synced', !!badgeGone);
    const form = await waitFor(async () => ((await page.locator('app-send-receipt input[name=to]').count()) === 1 ? true : null), 10000);
    check('the send-receipt form appears once the sale is synced', !!form);
    check('it no longer says the receipt waits for the sync',
      !(await page.locator('app-send-receipt .state-warn').count()));

    check('no page errors', pageErrors.length === 0, pageErrors.join(' | '));
  } catch (error) {
    check('run completed', false, error.message);
  } finally {
    if (browser) await browser.close();
    mock.kill();
  }

  console.log(failures ? `\n${failures} CHECK(S) FAILED` : '\nALL CHECKS PASSED');
  process.exit(failures ? 1 : 0);
})();
