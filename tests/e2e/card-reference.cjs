const { requiredPassword } = require('./test-credentials.cjs');
const testPassword = requiredPassword();
// The operator can require the bank terminal's approval number for card payments. This walks
// the whole till journey in Arabic — login, cart, card payment — and checks three things the
// UI and the sync payload have to agree on:
//   1. the field only appears once the operator asks for it,
//   2. a blank one blocks the sale instead of silently selling,
//   3. the number the cashier typed actually reaches the server on the payment line.
// (3) is the regression: the till used to demand the number and then drop it, so a card sale
// arrived with no reference and could not be reconciled against the terminal's own report.
const { spawn } = require('node:child_process');
const path = require('node:path');
const { chromium } = require('playwright');

const PORT = Number(process.env.MOCK_PORT || 5098);
const BASE = `http://localhost:${PORT}`;
const INTEGRATIONS = {
  cardMode: 'ExternalTerminal',
  requireCardReference: true,
  smsReceipts: false,
  emailReceipts: false,
};
const APPROVAL = 'APPROVAL-99001';
const AR = { label: 'مرجع طرفية الدفع', error: 'أدخل مرجع طرفية الدفع لإتمام الدفع بالبطاقة.' };

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
      /* server not up yet */
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

    browser = await chromium.launch({ args: ['--no-sandbox', '--disable-dev-shm-usage'] });
    const context = await browser.newContext();
    // The cashier's till is set to Arabic, so every message below is the Arabic one.
    await context.addInitScript(() => localStorage.setItem('pos.lang', 'ar'));
    const page = await context.newPage();
    const pageErrors = [];
    page.on('pageerror', (e) => pageErrors.push(e.message));

    // ---- login ---------------------------------------------------------------
    await page.goto(BASE, { waitUntil: 'networkidle' });
    await page.fill('#server', BASE);
    await page.fill('#username', 'admin');
    await page.fill('#password', testPassword);
    // A till sign-in names its till: an empty code is an office sign-in since 2026-10-05.
    if (await page.locator('#device').count() === 0) await page.click('.shared-toggle');
    await page.fill('#device', 'C01');
    await page.click('button[type=submit]');
    await page.waitForSelector('.tile', { timeout: 30000 });
    check('the till signs in and pulls the catalog', (await page.locator('.tile').count()) > 0);

    // ---- a card payment asks for the approval number --------------------------
    // The + is the only thing on a tile that adds (since 2026-09-23); a click on the card adds nothing.
    await page.locator('.tile .tile-add').first().click();
    await page.click('button.pay');
    await page.locator('button', { hasText: /^\s*بطاقة\s*$/ }).first().click();
    await page.waitForSelector('#card-ref', { timeout: 10000 });
    const label = await page.locator('label[for=card-ref]').textContent();
    check('the approval-number field appears for card payments', true, `label "${(label || '').trim()}"`);
    check('its label is the Arabic one', (label || '').trim() === AR.label, `got "${(label || '').trim()}"`);

    // ---- blank is refused, and nothing is sold --------------------------------
    await page.click('.actions .btn-primary');
    await page.waitForTimeout(500);
    const error = ((await page.locator('.error').first().textContent()) || '').trim();
    check('a blank approval number blocks the sale', error === AR.error, `got "${error}"`);
    check('the pay dialog stays open', (await page.locator('#card-ref').count()) === 1);
    check('nothing reached the server yet', (await state()).sales.length === 0);

    // ---- with the number, the sale completes and carries it ------------------
    await page.fill('#card-ref', APPROVAL);
    await page.click('.actions .btn-primary');
    await page.waitForSelector('.done', { timeout: 20000 });
    check('the sale completes once the number is entered', true);

    const synced = await waitFor(async () => {
      const s = await state();
      return s.sales.length === 1 ? s : null;
    }, 20000);
    check('the sale reaches the server', !!synced, synced ? synced.sales[0].reference : 'never arrived');

    const references = synced ? synced.sales[0].references : [];
    check(
      'the approval number is stored on the card payment line',
      references.includes(APPROVAL),
      JSON.stringify(references),
    );

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
